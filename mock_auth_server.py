"""Mock authentication / whitelist server for local testing.

Simulates an api.php auth endpoint so a client pointed at 127.0.0.1
(via the hosts file) can be tested without contacting the real server.

Only use against clients/systems you own or are authorized to test.
"""

import argparse
import json
import random
import secrets
import ssl
import string
import sys
from datetime import datetime

from flask import Flask, request, jsonify

# ================= CONFIG: edit here =================

# True -> approve ANY request regardless of key/HWID.
AUTO_APPROVE = False

# Whitelist: key -> list of allowed HWIDs (lowercase).
#   "*" as the list value -> any HWID is allowed for that key.
#   Add new keys/HWIDs by editing this dict.
WHITELIST = {
    "rohan": ["hwid-1234-5678", "hwid-abcd-efgh"],
    "tester": "*",
}

# Response payload templates (token is filled per-request).
SUCCESS_RESPONSE = {
    "success": True,
    "status": "authorized",
    "authorized": 1,
    "token": "",
}
DENIED_RESPONSE = {
    "success": False,
    "status": "denied",
    "authorized": 0,
    "token": "",
}

# "json" -> JSON payload above, "text" -> plaintext line below.
RESPONSE_MODE = "json"
TEXT_OK = "authorized"
TEXT_DENIED = "denied"

# If the client forces HTTPS, run with --tls (self-signed cert).
# ======================================================

app = Flask(__name__)

HWID_PARAM_NAMES = ("hwid", "hw_id", "hardwareid", "hardware_id",
                    "machineid", "machine_id", "deviceid", "device_id")


def now():
    return datetime.now().strftime("%H:%M:%S")


def log(message):
    print(f"[{now()}] {message}", flush=True)


def get_hwid(params):
    for name in HWID_PARAM_NAMES:
        for pname, pvalue in params.items():
            if pname.lower() == name:
                return pvalue.strip()
    return ""


def is_authorized(params):
    if AUTO_APPROVE:
        return True
    key = params.get("key", "").strip().lower()
    hwid = get_hwid(params).lower()
    allowed = WHITELIST.get(key)
    if allowed is None:
        return False
    if allowed == "*":
        return True
    return hwid in {h.strip().lower() for h in allowed}


@app.route("/api.php", methods=["GET", "POST"])
@app.route("/api.php/", methods=["GET", "POST"])
@app.route("/", methods=["GET", "POST"])
def api():
    params = dict(request.args)
    if request.method == "POST":
        params.update({k: v for k, v in request.form.items()})
    try:
        if request.is_json:
            params.update(request.get_json(silent=True) or {})
    except Exception:
        pass

    ip = request.remote_addr or "?"
    forwarded = request.headers.get("X-Forwarded-For")
    log("-" * 60)
    log(f"{request.method} {request.full_path} from {ip}"
        + (f" (XFF: {forwarded})" if forwarded else ""))
    log(f"User-Agent: {request.headers.get('User-Agent', '?')}")
    log(f"Authorization header: {request.headers.get('Authorization', '(none)')}")
    for pname, pvalue in params.items():
        log(f"  param {pname} = {pvalue}")
    if request.data:
        log(f"raw body: {request.data.decode('utf-8', errors='replace')}")

    authorized = is_authorized(params)

    if RESPONSE_MODE == "text":
        return (TEXT_OK + "\n") if authorized else (TEXT_DENIED + "\n"), 200 if authorized else 403

    payload = dict(SUCCESS_RESPONSE if authorized else DENIED_RESPONSE)
    payload["token"] = "".join(secrets.choice(string.ascii_lowercase + string.digits)
                               for _ in range(32))
    log(f"-> {'AUTHORIZED' if authorized else 'DENIED'} "
        + json.dumps(payload, separators=(",", ":")))
    return jsonify(payload), 200 if authorized else 403


@app.route("/<path:path>", methods=["GET", "POST"])
def catch_all(path):
    log(f"404 {request.method} /{path} (unhandled path)")
    return jsonify({"success": False, "status": "not_found"}), 404


def main():
    parser = argparse.ArgumentParser(description="Mock auth/whitelist server")
    parser.add_argument("--host", default="0.0.0.0")
    parser.add_argument("--port", type=int, default=80,
                        help="port 80 needs admin; use e.g. 8080 otherwise")
    parser.add_argument("--tls", action="store_true",
                        help="serve HTTPS with self-signed cert")
    parser.add_argument("--cert", default="cert.pem")
    parser.add_argument("--key", default="key.pem")
    args = parser.parse_args()

    ssl_ctx = None
    if args.tls:
        try:
            ssl_ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
            ssl_ctx.load_cert_chain(args.cert, args.key)
        except Exception as exc:
            sys.exit(f"TLS cert load failed: {exc}\n"
                     "Generate a self-signed cert first:\n"
                     "  openssl req -x509 -newkey rsa:2048 -keyout key.pem "
                     "-out cert.pem -days 365 -nodes -subj /CN=terminalx999.live")

    scheme = "https" if args.tls else "http"
    log(f"Mock auth server starting on {scheme}://{args.host}:{args.port}")
    log(f"AUTO_APPROVE={AUTO_APPROVE} keys={list(WHITELIST)} "
        f"mode={RESPONSE_MODE}")
    try:
        app.run(host=args.host, port=args.port, ssl_context=ssl_ctx,
                debug=False, use_reloader=False)
    except PermissionError:
        sys.exit("Permission denied binding the port. Run as Administrator, "
                 "or use --port 8080.")


if __name__ == "__main__":
    main()
