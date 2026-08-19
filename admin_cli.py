"""Admin CLI for the licensing backend. Stdlib only.

Usage:
  admin_cli.py --url http://127.0.0.1:8000 --admin-key SECRET generate --product pro --days 30 --count 5
  admin_cli.py --url http://127.0.0.1:8000 --admin-key SECRET status
  admin_cli.py --url http://127.0.0.1:8000 --admin-key SECRET status --key XXXX-XXXX-XXXX-XXXX-X
  admin_cli.py --url http://127.0.0.1:8000 --admin-key SECRET revoke --key XXXX-XXXX-XXXX-XXXX-X
  admin_cli.py --url http://127.0.0.1:8000 --admin-key SECRET revoke --key XXXX-XXXX-XXXX-XXXX-X --hwid <hash-or-raw>
  admin_cli.py --url http://127.0.0.1:8000 --admin-key SECRET unrevoke --key XXXX-XXXX-XXXX-XXXX-X

Admin key also via LIC_ADMIN_KEY environment variable.
"""

import argparse
import json
import os
import sys
import urllib.error
import urllib.request
from datetime import datetime, timezone

DEFAULT_URL = "http://127.0.0.1:8000"


def api(base, admin_key, method, path, payload=None):
    req = urllib.request.Request(
        base.rstrip("/") + path,
        data=json.dumps(payload).encode() if payload is not None else None,
        method=method,
        headers={"X-Admin-Key": admin_key, "Content-Type": "application/json"},
    )
    try:
        with urllib.request.urlopen(req, timeout=10) as resp:
            return resp.status, json.loads(resp.read().decode())
    except urllib.error.HTTPError as exc:
        try:
            return exc.code, json.loads(exc.read().decode())
        except Exception:
            return exc.code, {"error": f"http_{exc.code}"}
    except urllib.error.URLError as exc:
        sys.exit(f"network error: {exc.reason}")


def fmt_ts(ts):
    if not ts:
        return "-"
    return datetime.fromtimestamp(ts, tz=timezone.utc).strftime("%Y-%m-%d %H:%M UTC")


def fmt_days_left(ts):
    if not ts:
        return "-"
    left = (ts - int(__import__("time").time())) / 86400
    return f"{left:+.0f}d" if left >= 0 else f"{left:+.0f}d (EXPIRED)"


def cmd_generate(args):
    status, body = api(args.url, args.admin_key, "POST", "/api/v1/admin/keys",
                       {"product": args.product, "days": args.days,
                        "count": args.count})
    if status != 201:
        sys.exit(f"[{status}] {body.get('error', body)}")
    print(f"generated {len(body['keys'])} key(s), expires {fmt_ts(body['expires_at'])}:")
    for key in body["keys"]:
        print("  " + key)


def cmd_status(args):
    path = "/api/v1/admin/status"
    if args.key:
        path += "?key=" + args.key
    status, body = api(args.url, args.admin_key, "GET", path)
    if status != 200:
        sys.exit(f"[{status}] {body.get('error', body)}")
    licenses = body["licenses"]
    if not licenses:
        print("no licenses found")
        return
    for lic in licenses:
        print(f"{lic['key']}  {lic['product']:<10} {lic['status']:<8} "
              f"created {fmt_ts(lic['created_at'])}  "
              f"expires {fmt_ts(lic['expires_at'])}  "
              f"({fmt_days_left(lic['expires_at'])})")
        for act in lic["activations"]:
            print(f"    bound {act['hwid_hash'][:16]}... "
                  f"since {fmt_ts(act['activated_at'])}  "
                  f"last_seen {fmt_ts(act['last_seen_at'])}")


def cmd_revoke(args):
    action = "revoke_binding" if args.hwid else "revoke_key"
    payload = {"action": action, "key": args.key}
    if args.hwid:
        payload["hwid"] = args.hwid
    status, body = api(args.url, args.admin_key, "POST",
                       "/api/v1/admin/revoke", payload)
    if status != 200:
        sys.exit(f"[{status}] {body.get('error', body)}")
    print(f"ok: {body['status']} ({body.get('key', '')})"
          + (f" hwid={body.get('hwid_hash', '')[:16]}..." if args.hwid else ""))


def cmd_unrevoke(args):
    status, body = api(args.url, args.admin_key, "POST",
                       "/api/v1/admin/revoke",
                       {"action": "unrevoke", "key": args.key})
    if status != 200:
        sys.exit(f"[{status}] {body.get('error', body)}")
    print(f"ok: {body['status']} ({body.get('key', '')})")


def main():
    parser = argparse.ArgumentParser(description="License backend admin CLI")
    parser.add_argument("--url", default=DEFAULT_URL)
    parser.add_argument("--admin-key",
                        default=os.environ.get("LIC_ADMIN_KEY", ""))
    sub = parser.add_subparsers(dest="command", required=True)

    g = sub.add_parser("generate")
    g.add_argument("--product", required=True)
    g.add_argument("--days", type=int, required=True)
    g.add_argument("--count", type=int, default=1)
    g.set_defaults(func=cmd_generate)

    s = sub.add_parser("status")
    s.add_argument("--key")
    s.set_defaults(func=cmd_status)

    r = sub.add_parser("revoke")
    r.add_argument("--key", required=True)
    r.add_argument("--hwid")
    r.set_defaults(func=cmd_revoke)

    u = sub.add_parser("unrevoke")
    u.add_argument("--key", required=True)
    u.set_defaults(func=cmd_unrevoke)

    args = parser.parse_args()
    if not args.admin_key:
        sys.exit("admin key required: --admin-key or LIC_ADMIN_KEY env var")
    args.func(args)


if __name__ == "__main__":
    main()