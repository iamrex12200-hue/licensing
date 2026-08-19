#!/usr/bin/env python3
"""Device Sentinel telemetry agent (stdlib-only, optional psutil acceleration).

Runs as a scheduled task (or --loop for a persistent daemon) on monitored
machines. On first run it activates the license key from key.txt and stores
token.txt + hwid.txt in the config dir; afterwards it registers the device
and ingests metrics per the tier cadence (server enforces the cadence and
answers 429 with Retry-After, which this agent honors).

Config (env or --flags):
  LIC_ENDPOINT    server base URL            (default http://127.0.0.1:8000)
  LIC_KEY_PATH    license key file           (default <config>/key.txt)
  LIC_TOKEN_PATH  token file (auto-written)  (default <config>/token.txt)
  LIC_HWID_PATH   hwid file (auto-written)   (default <config>/hwid.txt)
  LIC_INTERVAL    seconds between loop cycles(default 60)
  LIC_CONFIG_DIR  config dir override (Linux: ~/.config/licensemanager,
                  Windows: %APPDATA%\\LicenseManager)

Windows scheduled task (daily-ish cadence):
  schtasks /create /tn "SentryAgent" /tr "python C:\\sentry_agent.py" /sc minute /mo 5
"""

import argparse
import ctypes
import hashlib
import json
import os
import platform
import random
import subprocess
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

try:
    import psutil  # optional accelerator
except ImportError:  # pragma: no cover
    psutil = None


def default_config_dir():
    if sys.platform == "win32":
        base = os.environ.get("APPDATA") or str(Path.home())
        return Path(base) / "LicenseManager"
    return Path(os.environ.get("XDG_CONFIG_HOME",
                               str(Path.home() / ".config"))) / "licensemanager"


def parse_args():
    cfg_dir = Path(os.environ.get("LIC_CONFIG_DIR", default_config_dir()))
    p = argparse.ArgumentParser(description="Device Sentinel telemetry agent")
    p.add_argument("--endpoint",
                   default=os.environ.get("LIC_ENDPOINT", "http://127.0.0.1:8000"))
    p.add_argument("--config-dir", default=str(cfg_dir))
    p.add_argument("--loop", action="store_true",
                   help="run continuously instead of one cycle")
    p.add_argument("--interval", type=int,
                   default=int(os.environ.get("LIC_INTERVAL", "60")))
    p.add_argument("--max-retries", type=int,
                   default=int(os.environ.get("LIC_MAX_RETRIES", "5")),
                   help="retries for transient failures (network/5xx/429)")
    p.add_argument("--backoff-base", type=float,
                   default=float(os.environ.get("LIC_BACKOFF_BASE", "2.0")),
                   help="exponential backoff base in seconds")
    p.add_argument("--backoff-cap", type=float,
                   default=float(os.environ.get("LIC_BACKOFF_CAP", "60.0")),
                   help="max backoff delay in seconds")
    p.add_argument("--no-jitter", action="store_true",
                   help="use plain exponential backoff without jitter")
    return p.parse_args()


def http_json(method, url, headers=None, payload=None, timeout=8):
    headers = dict(headers or {})
    data = None
    if payload is not None:
        data = json.dumps(payload).encode()
        headers.setdefault("Content-Type", "application/json")
    req = urllib.request.Request(url, data=data, headers=headers,
                                 method=method)
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        body = resp.read().decode()
        return resp.status, dict(resp.headers), json.loads(body) if body else {}


def _backoff_delay(attempt, base, cap, jitter):
    delay = min(cap, base * (2 ** attempt))
    if jitter:
        delay = random.uniform(0, delay)  # full jitter (AWS style)
    return delay


def request_retry(method, url, headers=None, payload=None,
                  max_retries=5, backoff_base=2.0, backoff_cap=60.0,
                  jitter=True):
    """HTTP request with resilience: exponential backoff + jitter for
    network drops / server restarts (connection errors) and 5xx responses;
    429 rate_limited responses honor Retry-After; 429 cadence_violation and
    other 4xx responses are NOT retried (deterministic client errors)."""
    for attempt in range(max_retries + 1):
        retry_delay = None
        try:
            status, hdrs, body = http_json(method, url, headers, payload)
        except urllib.error.HTTPError as exc:
            status, hdrs = exc.code, dict(exc.headers)
            body = {}
            try:
                body = json.loads(exc.read().decode())
            except Exception:
                pass
        except (urllib.error.URLError, ConnectionError, TimeoutError,
                OSError) as exc:
            if attempt == max_retries:
                raise
            retry_delay = _backoff_delay(attempt, backoff_base,
                                         backoff_cap, jitter)
            print(f"  retry {attempt + 1}/{max_retries} after "
                  f"{type(exc).__name__}: {exc} (sleep {retry_delay:.1f}s)")
            time.sleep(retry_delay)
            continue

        if status == 429 and body.get("error") == "cadence_violation":
            return status, hdrs, body  # tier cadence - never retry in-cycle
        if status == 429:
            if attempt == max_retries:
                return status, hdrs, body
            try:
                retry_after = int(hdrs.get("Retry-After") or 0)
            except (TypeError, ValueError):
                retry_after = 0
            retry_delay = max(
                retry_after,
                _backoff_delay(attempt, backoff_base, backoff_cap, jitter))
            print(f"  retry {attempt + 1}/{max_retries} on 429 "
                  f"(sleep {retry_delay:.1f}s)")
            time.sleep(retry_delay)
            continue
        if status < 500:
            return status, hdrs, body
        if attempt == max_retries:
            return status, hdrs, body
        retry_delay = _backoff_delay(attempt, backoff_base, backoff_cap,
                                     jitter)
        print(f"  retry {attempt + 1}/{max_retries} on HTTP {status} "
              f"(sleep {retry_delay:.1f}s)")
        time.sleep(retry_delay)
    return status, hdrs, body


def get_hardware_id():
    """Stable per-machine SHA256 hex id (64-hex, accepted by normalize_hwid)."""
    parts = [platform.node(), platform.machine(), platform.processor()]
    if sys.platform == "win32":
        try:
            out = subprocess.run(
                ["reg", "query",
                 r"HKLM\SOFTWARE\Microsoft\Cryptography",
                 "/v", "MachineGuid"],
                capture_output=True, text=True, timeout=5).stdout
            for line in out.splitlines():
                if "MachineGuid" in line:
                    parts.append(line.rsplit("REG_SZ", 1)[-1].strip())
        except Exception:
            pass
    return hashlib.sha256("|".join(parts).encode()).hexdigest()


def ensure_credentials(endpoint, cfg_dir, max_retries=5, backoff_base=2.0,
                       backoff_cap=60.0, jitter=True):
    token_path = Path(os.environ.get("LIC_TOKEN_PATH", cfg_dir / "token.txt"))
    hwid_path = Path(os.environ.get("LIC_HWID_PATH", cfg_dir / "hwid.txt"))
    if token_path.exists() and hwid_path.exists():
        return token_path.read_text(encoding="utf-8").strip(), \
            hwid_path.read_text(encoding="utf-8").strip()

    key_path = Path(os.environ.get("LIC_KEY_PATH", cfg_dir / "key.txt"))
    if not key_path.exists():
        print("Agent error: no saved token and no key.txt - run client "
              "activation first or place a key file in the config dir.")
        return None, None
    key = key_path.read_text(encoding="utf-8").strip()
    hwid = get_hardware_id()
    try:
        status, _, body = request_retry(
            "POST", f"{endpoint}/api/v1/activate",
            payload={"key": key, "hwid": hwid},
            max_retries=max_retries, backoff_base=backoff_base,
            backoff_cap=backoff_cap, jitter=jitter)
    except Exception as exc:
        print(f"Activation failed after {max_retries} retries: {exc}")
        return None, None
    if status != 200 or not body.get("token"):
        print(f"Activation rejected (HTTP {status}): {body}")
        return None, None
    cfg_dir.mkdir(parents=True, exist_ok=True)
    tmp_tok = cfg_dir / "token.txt.tmp"
    tmp_hwid = cfg_dir / "hwid.txt.tmp"
    tmp_tok.write_text(body["token"], encoding="utf-8")
    tmp_hwid.write_text(hwid, encoding="utf-8")
    os.replace(tmp_tok, token_path)
    os.replace(tmp_hwid, hwid_path)
    print("Activated and saved token.txt/hwid.txt.")
    return body["token"], hwid


def _cpu_percent():
    if psutil is not None:
        return psutil.cpu_percent(interval=0.5)
    if sys.platform.startswith("linux"):
        def _sample():
            totals = {}
            with open("/proc/stat", encoding="utf-8") as fh:
                for line in fh:
                    if line.startswith("cpu "):
                        fields = list(map(int, line.split()[1:]))
                        idle = fields[3] + (fields[4] if len(fields) > 4 else 0)
                        totals = {"idle": idle, "total": sum(fields)}
                        break
            return totals
        a = _sample()
        time.sleep(0.25)
        b = _sample()
        if a and b:
            delta_idle = b["idle"] - a["idle"]
            delta_total = b["total"] - a["total"]
            if delta_total > 0:
                return round(100.0 * (1 - delta_idle / delta_total), 1)
    return 0.0


def _mem_percent():
    if psutil is not None:
        return psutil.virtual_memory().percent
    if sys.platform.startswith("linux"):
        with open("/proc/meminfo", encoding="utf-8") as fh:
            vals = {}
            for line in fh:
                if line.startswith(("MemTotal:", "MemAvailable:")):
                    name, rest = line.split(":", 1)
                    vals[name] = int(rest.split()[0]) * 1024
        if "MemTotal" in vals and vals["MemTotal"] > 0:
            used = vals["MemTotal"] - vals.get("MemAvailable", vals["MemTotal"])
            return round(100.0 * used / vals["MemTotal"], 1)
        return 0.0
    if sys.platform == "win32":
        class MEMORYSTATUSEX(ctypes.Structure):
            _fields_ = [("dwLength", ctypes.c_ulong),
                        ("dwMemoryLoad", ctypes.c_ulong),
                        ("ullTotalPhys", ctypes.c_ulonglong),
                        ("ullAvailPhys", ctypes.c_ulonglong),
                        ("ullTotalPageFile", ctypes.c_ulonglong),
                        ("ullAvailPageFile", ctypes.c_ulonglong),
                        ("ullTotalVirtual", ctypes.c_ulonglong),
                        ("ullAvailVirtual", ctypes.c_ulonglong),
                        ("ullAvailExtendedVirtual", ctypes.c_ulonglong)]
        ms = MEMORYSTATUSEX()
        ms.dwLength = ctypes.sizeof(ms)
        if ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(ms)):
            return float(ms.dwMemoryLoad)
    return 0.0


def _disk_percent():
    if psutil is not None:
        return psutil.disk_usage("/").percent
    if sys.platform.startswith("linux"):
        st = os.statvfs("/")
        total = st.f_frsize * st.f_blocks
        free = st.f_frsize * st.f_bavail
        if total > 0:
            return round(100.0 * (1 - free / total), 1)
        return 0.0
    if sys.platform == "win32":
        root = os.environ.get("SystemDrive", "C:") + "\\"
        free = ctypes.c_ulonglong(0)
        total = ctypes.c_ulonglong(0)
        if ctypes.windll.kernel32.GetDiskFreeSpaceExW(
                root, ctypes.byref(free), ctypes.byref(total), None) and \
                total.value > 0:
            return round(100.0 * (1 - free.value / total.value), 1)
    return 0.0


def _uptime_seconds():
    if psutil is not None:
        try:
            return int(time.time() - psutil.boot_time())
        except Exception:
            return 0
    if sys.platform.startswith("linux"):
        try:
            with open("/proc/uptime", encoding="utf-8") as fh:
                return int(float(fh.read().split()[0]))
        except Exception:
            return 0
    if sys.platform == "win32":
        try:
            return int(ctypes.windll.kernel32.GetTickCount64() // 1000)
        except Exception:
            return 0
    return 0


def collect_stats():
    return {
        "cpu_usage": _cpu_percent(),
        "ram_usage": _mem_percent(),
        "disk_usage": _disk_percent(),
        "uptime_seconds": _uptime_seconds(),
    }


def read_user_id(cfg_dir):
    """Optional game user id from user_id.txt (or env LIC_USER_ID_PATH)."""
    user_id_path = Path(os.environ.get("LIC_USER_ID_PATH", cfg_dir / "user_id.txt"))
    if user_id_path.exists():
        value = user_id_path.read_text(encoding="utf-8").strip()
        return value[:512] if value else ""
    return ""


def run_cycle(endpoint, cfg_dir, max_retries=5, backoff_base=2.0,
              backoff_cap=60.0, jitter=True):
    """One register + ingest pass. Returns (exit_code, retry_after_seconds)."""
    token, hwid = ensure_credentials(endpoint, cfg_dir, max_retries,
                                     backoff_base, backoff_cap, jitter)
    if token is None:
        return 1, 0
    headers = {"Authorization": f"Bearer {token}",
               "X-Device-Hwid": hwid,
               "Content-Type": "application/json"}
    stats = collect_stats()
    user_id = read_user_id(cfg_dir)

    status, _, body = request_retry(
        "POST", f"{endpoint}/api/v1/sentry/devices", headers,
        payload={"hostname": platform.node(), "user_id": user_id},
        max_retries=max_retries, backoff_base=backoff_base,
        backoff_cap=backoff_cap, jitter=jitter)
    print(f"Device registration: HTTP {status} {body}")

    status, resp_headers, body = request_retry(
        "POST", f"{endpoint}/api/v1/sentry/metrics", headers,
        payload=stats,
        max_retries=max_retries, backoff_base=backoff_base,
        backoff_cap=backoff_cap, jitter=jitter)
    print(f"Metrics ingest: HTTP {status} {body}")
    wait = 0
    if status == 429:
        wait = int(resp_headers.get("Retry-After", "0") or 0)
        if wait > 0:
            print(f"Cadence limited - Retry-After {wait}s.")
    return (0 if status == 200 else 1), wait


def main():
    args = parse_args()
    cfg_dir = Path(args.config_dir)
    print(f"Sentry agent (psutil={'yes' if psutil else 'no (stdlib fallback)'})"
          f" -> {args.endpoint}")
    if not args.loop:
        code, _ = run_cycle(args.endpoint, cfg_dir, args.max_retries,
                            args.backoff_base, args.backoff_cap,
                            not args.no_jitter)
        return code
    interval = max(5, args.interval)
    while True:
        try:
            _, wait = run_cycle(args.endpoint, cfg_dir, args.max_retries,
                                args.backoff_base, args.backoff_cap,
                                not args.no_jitter)
        except Exception as exc:
            print(f"Cycle error: {exc}")
            wait = 0
        time.sleep(max(interval, wait))


if __name__ == "__main__":
    sys.exit(main())
