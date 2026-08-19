"""Licensing & authentication backend (Flask + storage abstraction).

Endpoints:
  POST /api/v1/admin/keys     generate license keys        (X-Admin-Key required)
  POST /api/v1/activate       bind a key to a hardware ID  -> signed token
  POST /api/v1/validate       validate a token + hardware  -> valid/expired/mismatch
  POST /api/v1/deactivate     free a device slot
  GET  /api/v1/sentry/ping    Device Sentinel agent probe (Blueprint: sentry/)
  GET  /api/v1/admin/metrics  operator telemetry snapshot (X-Admin-Key)

Storage: SQLite by default (LIC_DB_DIALECT=postgres + LIC_DATABASE_URL for
PostgreSQL). All queries go through storage.sql() so they stay portable.

Secrets come from environment variables; never ship defaults to production.
"""

import argparse
import base64
import functools
import hashlib
import hmac
import json
import os
import secrets
import smtplib
import sys
import threading
import time
import urllib.error
import urllib.request
from datetime import datetime, timedelta, timezone
from email.mime.text import MIMEText

from flask import (Flask, g, jsonify, redirect, render_template, request,
                   session, url_for)

import storage

DB_PATH = os.environ.get("LIC_DB_PATH", "licenses.db")
ADMIN_KEY = os.environ.get("LIC_ADMIN_KEY", "change-me-admin-key")
HMAC_SECRET = os.environ.get("LIC_HMAC_SECRET", "change-me-hmac-secret")
TOKEN_TTL_SECONDS = 24 * 3600
KEY_ALPHABET = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"
MAX_FIELD_LENGTH = 512

RATE_LIMITS = {
    "activate": (int(os.environ.get("LIC_RATE_ACTIVATE", "5")), 60),
    "validate": (int(os.environ.get("LIC_RATE_VALIDATE", "30")), 60),
    "admin":    (int(os.environ.get("LIC_RATE_ADMIN", "10")), 60),
    "protected": (int(os.environ.get("LIC_RATE_PROTECTED", "60")), 60),
    "upgrade":  (int(os.environ.get("LIC_RATE_UPGRADE", "10")), 60),
    "sentry":   (int(os.environ.get("LIC_RATE_SENTRY", "30")), 60),
}
RATE_DISABLED = os.environ.get("LIC_RATE_LIMIT", "1") == "0"
TRUST_XFF = os.environ.get("LIC_TRUST_XFF", "0") == "1"

# Device Sentinel alert engine (Milestone 4).
ALERT_EVAL_INTERVAL = int(os.environ.get("LIC_ALERT_INTERVAL", "60"))
ALERTS_DISABLED = os.environ.get("LIC_ALERTS_DISABLED", "0") == "1"
ALERT_METRICS = ("cpu_usage", "ram_usage", "disk_usage", "uptime_seconds")

# Telemetry retention (tier limits mirror the dashboard history windows).
RETENTION_BASIC_HOURS = int(os.environ.get("LIC_RETENTION_BASIC_HOURS", "24"))
RETENTION_PRO_HOURS = int(os.environ.get("LIC_RETENTION_PRO_HOURS", "720"))
PRUNE_INTERVAL = int(os.environ.get("LIC_PRUNE_INTERVAL", "3600"))

PRODUCT_FEATURES = {
    "pro": ["feature_a", "feature_b", "launch"],
    "basic": ["feature_a"],
}

# Device slots per license key (basic: 1, pro: multi-device). Enforced in
# activate/upgrade; injected into the sentry Blueprint as the single source
# of truth for tier device caps.
PRODUCT_MAX_DEVICES = {"basic": 1, "pro": 10}

LICENSES_COLUMNS = [
    ("key", "TEXT", "PRIMARY KEY"),
    ("product", "TEXT", "NOT NULL"),
    ("days", "INTEGER", "NOT NULL"),
    ("created_at", "INTEGER", "NOT NULL"),
    ("expires_at", "INTEGER", "NOT NULL"),
    ("status", "TEXT", "NOT NULL DEFAULT 'active'"),
    ("revoked_at", "INTEGER", ""),
]
ACTIVATIONS_COLUMNS = [
    ("license_key", "TEXT", "NOT NULL"),
    ("hwid_hash", "TEXT", "NOT NULL"),
    ("activated_at", "INTEGER", "NOT NULL"),
    ("last_seen_at", "INTEGER", "NOT NULL"),
    ("PRIMARY KEY (license_key, hwid_hash)", "", ""),
]
RATE_LIMITS_COLUMNS = [
    ("bucket", "TEXT", "NOT NULL"),
    ("ts", "INTEGER", "NOT NULL"),
]


def features_for(product):
    return PRODUCT_FEATURES.get(product, [])

app = Flask(__name__)
app.secret_key = os.environ.get("LIC_SESSION_SECRET", HMAC_SECRET)
LOGIN_USER = "rexy"
LOGIN_PASSWORD = os.environ.get("LIC_LOGIN_PASSWORD", "rexy9033")


def db():
    return storage.connect(DB_PATH)


def init_db():
    with db() as conn:
        conn.execute(storage.render_create_table("licenses", LICENSES_COLUMNS))
        conn.execute(storage.render_create_table(
            "activations", ACTIVATIONS_COLUMNS))
        conn.execute(storage.render_create_table(
            "rate_limits", RATE_LIMITS_COLUMNS))
        storage.add_column_if_missing(conn, "licenses", "revoked_at",
                                      "INTEGER")
        conn.execute(storage.sql(
            "CREATE INDEX IF NOT EXISTS idx_rate_ts ON rate_limits(ts)"))


def now():
    return int(time.time())


def now_utc():
    return datetime.now(timezone.utc)


def clamp(value, name):
    if not isinstance(value, str) or not value or len(value) > MAX_FIELD_LENGTH:
        return None
    return value.strip()


def gen_key():
    parts = []
    for _ in range(4):
        parts.append("".join(secrets.choice(KEY_ALPHABET) for _ in range(5)))
    key = "-".join(parts)
    return key + "-" + checksum_char(key)


def checksum_char(key):
    return KEY_ALPHABET[sum(key.encode()) % len(KEY_ALPHABET)]


def key_ok(key):
    if not key or len(key) > 26:
        return False
    body, sep, chk = key.rpartition("-")
    return bool(sep) and chk == checksum_char(body)


def b64u(data):
    return base64.urlsafe_b64encode(data).rstrip(b"=").decode()


def unb64u(text):
    pad = "=" * (-len(text) % 4)
    return base64.urlsafe_b64decode(text + pad)


def issue_token(key, hwid_hash, product, features=None, ttl=TOKEN_TTL_SECONDS):
    header = b64u(json.dumps({"alg": "HS256"}).encode())
    payload = b64u(json.dumps({
        "k": key, "h": hwid_hash, "p": product,
        "f": features or [], "i": now(), "e": now() + ttl,
    }).encode())
    sig = hmac.new(HMAC_SECRET.encode(), f"{header}.{payload}".encode(),
                   hashlib.sha256).digest()
    return f"{header}.{payload}.{b64u(sig)}"


def verify_token(token):
    try:
        header_b64, payload_b64, sig_b64 = token.split(".")
        expected = hmac.new(HMAC_SECRET.encode(),
                            f"{header_b64}.{payload_b64}".encode(),
                            hashlib.sha256).digest()
        if not hmac.compare_digest(expected, unb64u(sig_b64)):
            return None
        payload = json.loads(unb64u(payload_b64))
        if payload["e"] < now():
            return {"error": "expired"}
        return payload
    except Exception:
        return None


def admin_ok():
    supplied = request.headers.get("X-Admin-Key", "")
    return hmac.compare_digest(supplied, ADMIN_KEY)


def body_json():
    data = request.get_json(silent=True)
    return data if isinstance(data, dict) else {}


def client_ip():
    if TRUST_XFF:
        fwd = request.headers.get("X-Forwarded-For")
        if fwd:
            return fwd.split(",")[0].strip()
    return request.remote_addr or "unknown"


def rate_limit(bucket):
    if RATE_DISABLED:
        return None
    limit, window = RATE_LIMITS[bucket]
    key = f"{bucket}:{client_ip()}"
    cutoff = now() - window
    with db() as conn:
        conn.execute(storage.sql(
            "DELETE FROM rate_limits WHERE bucket=? AND ts<?"), (key, cutoff))
        count = conn.execute(storage.sql(
            "SELECT COUNT(*) FROM rate_limits WHERE bucket=?"), (key,)
        ).fetchone()
        count = storage.scalar(count)
        if count >= limit:
            return window
        conn.execute(storage.sql(
            "INSERT INTO rate_limits (bucket, ts) VALUES (?,?)"), (key, now()))
    return None


def limited(bucket):
    def decorator(fn):
        @functools.wraps(fn)
        def wrapper(*args, **kwargs):
            retry_after = rate_limit(bucket)
            if retry_after is not None:
                resp = jsonify({"success": False, "error": "rate_limited",
                                "retry_after": retry_after})
                resp.headers["Retry-After"] = str(retry_after)
                return resp, 429
            return fn(*args, **kwargs)
        return wrapper
    return decorator


def normalize_hwid(value):
    candidate = value.strip().lower()
    if len(candidate) == 64 and all(ch in "0123456789abcdef" for ch in candidate):
        return candidate
    return hashlib.sha256(value.encode()).hexdigest()


def require_feature(feature, require_device=True):
    def decorator(fn):
        @functools.wraps(fn)
        def wrapper(*args, **kwargs):
            auth = request.headers.get("Authorization", "")
            if not auth.startswith("Bearer "):
                return jsonify({"success": False, "error": "missing_token"}), 401
            payload = verify_token(auth[7:])
            if payload is None:
                return jsonify({"success": False, "error": "bad_token"}), 401
            if isinstance(payload, dict) and payload.get("error") == "expired":
                return jsonify({"success": False, "status": "expired",
                                "error": "token_expired"}), 401
            key, hwid_hash = payload["k"], payload["h"]
            with db() as conn:
                row = conn.execute(storage.sql(
                    "SELECT status, expires_at FROM licenses WHERE key=?"),
                    (key,)).fetchone()
                if row is None or row["status"] != "active":
                    return jsonify({"success": False, "status": "revoked",
                                    "error": "license_revoked"}), 401
                if row["expires_at"] < now():
                    return jsonify({"success": False, "status": "expired",
                                    "error": "license_expired"}), 401
                activation = conn.execute(storage.sql(
                    "SELECT 1 FROM activations"
                    " WHERE license_key=? AND hwid_hash=?"),
                    (key, hwid_hash)).fetchone()
                if activation is None:
                    return jsonify({"success": False,
                                    "status": "not_activated",
                                    "error": "not_activated"}), 401
            if require_device:
                device = request.headers.get("X-Device-Hwid", "")
                if not device or not hmac.compare_digest(
                        device.strip().lower(), hwid_hash):
                    return jsonify({"success": False, "status": "device_mismatch",
                                    "error": "device_mismatch"}), 403
            if feature not in payload.get("f", []):
                return jsonify({"success": False, "error": "feature_required",
                                "feature": feature}), 403
            g.license = payload
            return fn(*args, **kwargs)
        return wrapper
    return decorator


@app.get("/api/v1/data/summary")
@limited("protected")
@require_feature("feature_a")
def data_summary():
    return jsonify({"success": True, "product": g.license["p"],
                    "data": {"reports": 42, "queries": 17}})


@app.get("/api/v1/data/advanced")
@limited("protected")
@require_feature("feature_b")
def data_advanced():
    return jsonify({"success": True, "product": g.license["p"],
                    "data": {"advanced": True, "queries": 128}})


# Device Sentinel (Milestones 1-2). Imported from the top block; middleware
# is injected (not imported by the package) to avoid a circular import.
from sentry import build_sentry_blueprint  # noqa: E402

sentry_bp, init_sentry_db = build_sentry_blueprint(
    limited, require_feature, DB_PATH, PRODUCT_MAX_DEVICES)
app.register_blueprint(sentry_bp)


@app.get("/sentry/login")
def sentry_login_page():
    if session.get("dash_user") == LOGIN_USER:
        return redirect(url_for("sentry_dashboard_ui"))
    return render_template("login.html", error=None)


@app.post("/sentry/login")
def sentry_login():
    if (request.form.get("id") == LOGIN_USER
            and request.form.get("pass") == LOGIN_PASSWORD):
        session["dash_user"] = LOGIN_USER
        nxt = request.args.get("next") or url_for("sentry_dashboard_ui")
        return redirect(nxt)
    return render_template("login.html",
                           error="Invalid credentials"), 401


@app.get("/sentry/logout")
def sentry_logout():
    session.pop("dash_user", None)
    return redirect(url_for("sentry_login_page"))


@app.get("/sentry/dashboard")
def sentry_dashboard_ui():
    if session.get("dash_user") != LOGIN_USER:
        return redirect(url_for("sentry_login_page", next="/sentry/dashboard"))
    return render_template("dashboard.html")


def _dispatch_webhook(webhook_url, payload):
    try:
        req = urllib.request.Request(
            webhook_url, data=json.dumps(payload).encode(),
            headers={"Content-Type": "application/json"}, method="POST")
        with urllib.request.urlopen(req, timeout=8) as resp:
            return "delivered" if 200 <= resp.status < 300 else "failed"
    except Exception:
        return "failed"


def _dispatch_email(email_to, payload):
    host = os.environ.get("LIC_SMTP_HOST")
    if not host or not email_to:
        return None
    try:
        msg = MIMEText(json.dumps(payload, indent=2))
        msg["Subject"] = (f"[Sentry Alert] {payload['metric']} breach on "
                          f"{payload.get('hostname', 'unknown')}")
        msg["From"] = os.environ.get("LIC_SMTP_FROM", "sentry@localhost")
        msg["To"] = email_to
        with smtplib.SMTP(host,
                          int(os.environ.get("LIC_SMTP_PORT", "587")),
                          timeout=8) as server:
            user = os.environ.get("LIC_SMTP_USER")
            if user:
                server.starttls()
                server.login(user, os.environ.get("LIC_SMTP_PASS", ""))
            server.send_message(msg)
        return "delivered"
    except Exception:
        return "failed"


def _alert_payload(rule, row, observed):
    return {
        "event": "metric_breach",
        "rule_id": rule["id"],
        "license_key": rule["license_key"],
        "hwid_hash": row["hwid_hash"],
        "hostname": row["hostname"],
        "metric": rule["metric"],
        "operator": rule["operator"],
        "threshold": rule["threshold"],
        "observed": observed,
        "recorded_at": row["recorded_at"],
    }


def evaluate_alert_rules():
    """One evaluation pass over enabled rules vs metrics recorded in the
    last evaluation window. Returns the number of events fired."""
    fired = 0
    with db() as conn:
        rules = [dict(r) for r in conn.execute(storage.sql(
            "SELECT * FROM alert_rules WHERE enabled=1"))]
        for rule in rules:
            cooldown_expr, cooldown_params = storage.dt_minus(
                rule["cooldown_minutes"], "minutes")
            recent = {r["hwid_hash"] for r in conn.execute(storage.sql(
                "SELECT hwid_hash FROM alert_events WHERE rule_id=?"
                f" AND created_at > {cooldown_expr}"),
                [rule["id"]] + cooldown_params)}
            window_expr, window_params = storage.dt_minus(
                ALERT_EVAL_INTERVAL + 5, "seconds")
            rows = conn.execute(storage.sql(
                "SELECT m.hwid_hash, m.cpu_usage, m.ram_usage, m.disk_usage,"
                " m.uptime_seconds, m.recorded_at, d.hostname"
                " FROM sentry_metrics m"
                " JOIN sentry_devices d ON d.hwid_hash = m.hwid_hash"
                " WHERE d.license_key=?"
                f" AND m.recorded_at >= {window_expr}"),
                [rule["license_key"]] + window_params).fetchall()
            for row in rows:
                observed = row[rule["metric"]]
                if observed is None:
                    continue
                breach = (observed > rule["threshold"] if
                          rule["operator"] == "gt" else
                          observed < rule["threshold"])
                if not breach:
                    continue
                if row["hwid_hash"] in recent:
                    continue
                recent.add(row["hwid_hash"])
                payload = _alert_payload(rule, row, observed)
                dispatched_to = "log"
                dispatch_status = "logged"
                if rule.get("webhook_url"):
                    dispatched_to = rule["webhook_url"]
                    dispatch_status = _dispatch_webhook(
                        rule["webhook_url"], payload)
                if rule.get("email_to"):
                    status = _dispatch_email(rule["email_to"], payload)
                    if status == "delivered" and dispatch_status != "delivered":
                        dispatched_to = f"email:{rule['email_to']}"
                        dispatch_status = "delivered"
                    elif status == "failed" and dispatch_status == "logged":
                        dispatched_to = f"email:{rule['email_to']}"
                        dispatch_status = "failed"
                conn.execute(storage.sql(
                    "INSERT INTO alert_events"
                    " (rule_id, license_key, hwid_hash, hostname, metric,"
                    "  operator, threshold, observed, dispatched_to,"
                    "  dispatch_status)"
                    " VALUES (?,?,?,?,?,?,?,?,?,?)"),
                    (rule["id"], rule["license_key"], row["hwid_hash"],
                     row["hostname"], rule["metric"], rule["operator"],
                     rule["threshold"], observed, dispatched_to,
                     dispatch_status))
                fired += 1
    return fired


_last_alert_tick = None
_alert_events_total = 0
_last_prune_at = None
_last_pruned_rows = 0


def prune_telemetry():
    """Deletes raw telemetry older than the tier retention limit
    (basic: 24h, pro: 30 days) plus orphaned rows. Returns rows removed."""
    deleted = 0
    with db() as conn:
        basic_expr, basic_params = storage.dt_minus(
            RETENTION_BASIC_HOURS, "hours")
        pro_expr, pro_params = storage.dt_minus(RETENTION_PRO_HOURS, "hours")
        cur = conn.execute(storage.sql(
            "DELETE FROM sentry_metrics WHERE id IN ("
            " SELECT m.id FROM sentry_metrics m"
            " JOIN sentry_devices d ON d.hwid_hash = m.hwid_hash"
            " JOIN licenses l ON l.key = d.license_key"
            f" WHERE (l.product = 'pro' AND m.recorded_at < {pro_expr})"
            f"    OR (l.product <> 'pro' AND m.recorded_at < {basic_expr}))"),
            pro_params + basic_params)
        deleted += cur.rowcount
        cur = conn.execute(storage.sql(
            "DELETE FROM sentry_metrics WHERE id IN ("
            " SELECT m.id FROM sentry_metrics m"
            " LEFT JOIN sentry_devices d ON d.hwid_hash = m.hwid_hash"
            " WHERE d.hwid_hash IS NULL)"))
        deleted += cur.rowcount
    return deleted


def _alert_pass():
    global _last_alert_tick, _alert_events_total
    _last_alert_tick = now()
    try:
        fired = evaluate_alert_rules()
        _alert_events_total += fired
        if fired:
            print(f"[{now_utc().isoformat()}] alert engine: "
                  f"{fired} event(s) fired", flush=True)
    except Exception as exc:  # never let the worker die
        print(f"[{now_utc().isoformat()}] alert engine error: {exc}",
              flush=True)


def _prune_pass():
    global _last_prune_at, _last_pruned_rows
    try:
        removed = prune_telemetry()
        _last_prune_at = now_utc().isoformat()
        _last_pruned_rows += removed
        if removed:
            print(f"[{now_utc().isoformat()}] retention: "
                  f"pruned {removed} row(s)", flush=True)
    except Exception as exc:
        print(f"[{now_utc().isoformat()}] retention error: {exc}",
              flush=True)


_scheduler = None
_scheduler_provider = "disabled"


def start_alert_engine():
    """Background worker (APScheduler when available, stdlib thread
    fallback). Never blocks startup and never crashes the server.
    Runs the alert evaluation job and the telemetry retention job."""
    global _scheduler, _scheduler_provider
    if ALERTS_DISABLED:
        print(f"[{now_utc().isoformat()}] alert engine disabled "
              "(LIC_ALERTS_DISABLED=1)", flush=True)
        return None
    try:
        from apscheduler.schedulers.background import BackgroundScheduler
        _scheduler = BackgroundScheduler(daemon=True)
        _scheduler.add_job(_alert_pass, "interval",
                           seconds=ALERT_EVAL_INTERVAL,
                           id="alert_engine", max_instances=1,
                           coalesce=True)
        _scheduler.add_job(_prune_pass, "interval",
                           seconds=PRUNE_INTERVAL,
                           id="retention_prune", max_instances=1,
                           coalesce=True)
        _scheduler.start()
        _scheduler_provider = "apscheduler"
        print(f"[{now_utc().isoformat()}] alert engine started "
              f"(APScheduler: alerts/{ALERT_EVAL_INTERVAL}s, "
              f"retention/{PRUNE_INTERVAL}s)", flush=True)
    except ImportError:
        def _loop():
            while True:
                _alert_pass()
                _prune_pass()
                time.sleep(min(ALERT_EVAL_INTERVAL, PRUNE_INTERVAL))
        threading.Thread(target=_loop, daemon=True,
                         name="alert-engine-fallback").start()
        _scheduler_provider = "thread-fallback"
        print(f"[{now_utc().isoformat()}] alert engine started "
              f"(stdlib thread fallback)", flush=True)
    return _scheduler


def scheduler_status():
    """Snapshot for /api/v1/admin/metrics."""
    running = bool(_scheduler and _scheduler.running)
    next_run = None
    if running:
        try:
            job = _scheduler.get_job("alert_engine")
            if job and job.next_run_time:
                next_run = job.next_run_time.isoformat()
        except Exception:
            next_run = None
    return {
        "provider": _scheduler_provider,
        "running": running or _scheduler_provider == "thread-fallback",
        "last_tick_age_seconds": (
            None if _last_alert_tick is None else now() - _last_alert_tick),
        "alerts_fired_total": _alert_events_total,
        "next_run": next_run,
        "interval_seconds": ALERT_EVAL_INTERVAL,
    }


@app.errorhandler(404)
def not_found(_):
    return jsonify({"success": False, "error": "not_found"}), 404


@app.post("/api/v1/admin/keys")
@limited("admin")
def admin_generate():
    if not admin_ok():
        return jsonify({"success": False, "error": "unauthorized"}), 401
    data = body_json()
    product = clamp(data.get("product"), "product")
    days = data.get("days")
    count = data.get("count", 1)
    if not product:
        return jsonify({"success": False, "error": "product required"}), 400
    try:
        days = int(days)
        count = int(count)
    except (TypeError, ValueError):
        return jsonify({"success": False, "error": "days/count must be int"}), 400
    if days < 1 or days > 3650:
        return jsonify({"success": False, "error": "days out of range"}), 400
    count = min(max(count, 1), 100)
    created = now()
    expires = created + days * 86400
    keys = []
    with db() as conn:
        for _ in range(count):
            key = gen_key()
            while conn.execute(storage.sql(
                "SELECT 1 FROM licenses WHERE key=?"), (key,)).fetchone():
                key = gen_key()
            conn.execute(storage.sql(
                "INSERT INTO licenses (key, product, days, created_at, expires_at)"
                " VALUES (?,?,?,?,?)"),
                (key, product, days, created, expires))
            keys.append(key)
    return jsonify({"success": True, "keys": keys, "expires_at": expires}), 201


@app.post("/api/v1/activate")
@limited("activate")
def activate():
    data = body_json()
    key = clamp(data.get("key"), "key")
    hwid = clamp(data.get("hwid"), "hwid")
    if not key_ok(key) or not hwid:
        return jsonify({"success": False, "error": "invalid_request"}), 400
    hwid_hash = normalize_hwid(hwid)
    with db() as conn:
        row = conn.execute(storage.sql(
            "SELECT * FROM licenses WHERE key=?"), (key,)).fetchone()
        if row is None:
            return jsonify({"success": False, "error": "invalid_key"}), 404
        if row["status"] != "active":
            return jsonify({"success": False, "error": "revoked"}), 403
        if row["expires_at"] < now():
            return jsonify({"success": False, "error": "expired"}), 403
        existing = conn.execute(storage.sql(
            "SELECT hwid_hash FROM activations WHERE license_key=?"),
            (key,)).fetchall()
        bound = [r["hwid_hash"] for r in existing]
        max_devices = PRODUCT_MAX_DEVICES.get(row["product"], 1)
        if hwid_hash not in bound and len(bound) >= max_devices:
            return jsonify({"success": False, "error": "device_limit_exceeded",
                            "detail": f"{row['product']} tier allows "
                                      f"{max_devices} device(s)"}), 403
        conn.execute(storage.sql(
            "INSERT INTO activations"
            " (license_key, hwid_hash, activated_at, last_seen_at)"
            " VALUES (?,?,?,?)"
            " ON CONFLICT(license_key, hwid_hash) DO UPDATE SET"
            " activated_at=excluded.activated_at,"
            " last_seen_at=excluded.last_seen_at"),
            (key, hwid_hash, now(), now()))
    token = issue_token(key, hwid_hash, row["product"], features_for(row["product"]))
    return jsonify({
        "success": True, "status": "activated",
        "token": token,
        "product": row["product"],
        "features": features_for(row["product"]),
        "expires_at": row["expires_at"],
        "expires_at_iso": datetime.fromtimestamp(
            row["expires_at"], tz=timezone.utc
        ).isoformat(),
    })


@app.post("/api/v1/validate")
@limited("validate")
def validate():
    data = body_json()
    token = clamp(data.get("token"), "token")
    hwid = clamp(data.get("hwid"), "hwid")
    if not token or not hwid:
        return jsonify({"success": False, "error": "invalid_request"}), 400
    payload = verify_token(token)
    if payload is None:
        return jsonify({"success": False, "error": "bad_token"}), 401
    if isinstance(payload, dict) and payload.get("error") == "expired":
        return jsonify({"success": False, "status": "expired",
                        "error": "token_expired"}), 401
    hwid_hash = normalize_hwid(hwid)
    if not hmac.compare_digest(payload["h"], hwid_hash):
        return jsonify({"success": False, "status": "device_mismatch",
                        "error": "hwid_mismatch"}), 403
    with db() as conn:
        row = conn.execute(storage.sql(
            "SELECT * FROM licenses WHERE key=?"), (payload["k"],)).fetchone()
        if row is None:
            return jsonify({"success": False, "error": "invalid_key"}), 404
        if row["status"] != "active":
            return jsonify({"success": False, "status": "revoked"}), 403
        if row["expires_at"] < now():
            return jsonify({"success": False, "status": "expired"}), 403
        activation = conn.execute(storage.sql(
            "SELECT 1 FROM activations WHERE license_key=? AND hwid_hash=?"),
            (payload["k"], hwid_hash)).fetchone()
        if activation is None:
            return jsonify({"success": False, "status": "not_activated",
                            "error": "not_activated"}), 401
        conn.execute(storage.sql(
            "UPDATE activations SET last_seen_at=?"
            " WHERE license_key=? AND hwid_hash=?"),
            (now(), payload["k"], hwid_hash))
    return jsonify({
        "success": True, "status": "valid",
        "product": row["product"],
        "features": features_for(row["product"]),
        "expires_at": row["expires_at"],
        "expires_at_iso": datetime.fromtimestamp(
            row["expires_at"], tz=timezone.utc
        ).isoformat(),
    })


@app.post("/api/v1/deactivate")
def deactivate():
    data = body_json()
    key = clamp(data.get("key"), "key")
    hwid = clamp(data.get("hwid"), "hwid")
    if not key or not hwid:
        return jsonify({"success": False, "error": "invalid_request"}), 400
    hwid_hash = normalize_hwid(hwid)
    with db() as conn:
        cur = conn.execute(storage.sql(
            "DELETE FROM activations WHERE license_key=? AND hwid_hash=?"),
            (key, hwid_hash))
    if cur.rowcount == 0:
        return jsonify({"success": False, "error": "not_found"}), 404
    return jsonify({"success": True, "status": "deactivated"})


@app.get("/")
def index():
    return jsonify({"success": True, "status": "ok", "service": "licensing",
                    "health": "/healthz", "dashboard": "/sentry/dashboard"}), 200


@app.get("/healthz")
def healthz():
    return jsonify({"success": True, "status": "ok"}), 200


@app.get("/api/v1/admin/status")
@limited("admin")
def admin_status():
    if not admin_ok():
        return jsonify({"success": False, "error": "unauthorized"}), 401
    key = request.args.get("key")
    with db() as conn:
        if key:
            rows = conn.execute(storage.sql(
                "SELECT * FROM licenses WHERE key=?"), (key,)).fetchall()
        else:
            rows = conn.execute(storage.sql(
                "SELECT * FROM licenses ORDER BY created_at DESC")).fetchall()
        licenses = []
        for row in rows:
            activations = conn.execute(storage.sql(
                "SELECT hwid_hash, activated_at, last_seen_at"
                " FROM activations WHERE license_key=?"),
                (row["key"],)).fetchall()
            licenses.append({**dict(row),
                             "activations": [dict(a) for a in activations]})
    return jsonify({"success": True, "licenses": licenses})


@app.get("/api/v1/admin/metrics")
@limited("admin")
def admin_metrics():
    """Operator telemetry snapshot: token/agent counts, scheduler health,
    retention state. Secured by X-Admin-Key."""
    if not admin_ok():
        return jsonify({"success": False, "error": "unauthorized"}), 401
    day_ago = now() - 86400
    with db() as conn:
        count = lambda q, *p: storage.scalar(
            conn.execute(storage.sql(q), p).fetchone())
        licenses_total = count("SELECT COUNT(*) FROM licenses")
        licenses_active = count(
            "SELECT COUNT(*) FROM licenses WHERE status='active'"
            " AND expires_at > ?", now())
        activations_total = count("SELECT COUNT(*) FROM activations")
        activations_active_24h = count(
            "SELECT COUNT(*) FROM activations WHERE last_seen_at >= ?",
            day_ago)
        sentry_devices_total = count("SELECT COUNT(*) FROM sentry_devices")
        devices_expr, devices_params = storage.dt_minus(24, "hours")
        sentry_devices_active_24h = count(
            f"SELECT COUNT(*) FROM sentry_devices"
            f" WHERE last_seen >= {devices_expr}",
            *devices_params)
        metrics_expr, metrics_params = storage.dt_minus(24, "hours")
        metrics_ingested_24h = count(
            f"SELECT COUNT(*) FROM sentry_metrics"
            f" WHERE recorded_at >= {metrics_expr}",
            *metrics_params)
        alert_events_total = count("SELECT COUNT(*) FROM alert_events")
    db_bytes = None
    if storage.dialect() == "sqlite":
        try:
            db_bytes = os.path.getsize(DB_PATH)
        except OSError:
            db_bytes = None
    return jsonify({
        "success": True,
        "generated_at": now_utc().isoformat(),
        "dialect": storage.dialect(),
        "licenses": {"total": licenses_total, "active": licenses_active},
        "tokens": {"activations_total": activations_total,
                   "activations_active_24h": activations_active_24h},
        "sentry": {"devices_total": sentry_devices_total,
                   "devices_active_24h": sentry_devices_active_24h,
                   "metrics_ingested_24h": metrics_ingested_24h,
                   "alert_events_total": alert_events_total,
                   "db_bytes": db_bytes},
        "scheduler": scheduler_status(),
        "retention": {"basic_hours": RETENTION_BASIC_HOURS,
                      "pro_hours": RETENTION_PRO_HOURS,
                      "interval_seconds": PRUNE_INTERVAL,
                      "last_prune_at": _last_prune_at,
                      "rows_pruned_total": _last_pruned_rows},
    })


@app.post("/api/v1/upgrade")
@limited("upgrade")
def upgrade():
    data = body_json()
    new_key = clamp(data.get("key"), "key")
    hwid = clamp(data.get("hwid"), "hwid")
    mode = data.get("mode", "supersede")
    if not key_ok(new_key):
        return jsonify({"success": False, "error": "invalid_key"}), 400
    if not hwid:
        return jsonify({"success": False, "error": "hwid required"}), 400
    if mode not in ("supersede", "revoke"):
        return jsonify({"success": False, "error": "invalid mode"}), 400
    hwid_hash = normalize_hwid(hwid)

    with db() as conn:
        old_key = None
        token = data.get("current_token")
        if token:
            payload = verify_token(token)
            if (isinstance(payload, dict) and payload.get("error") != "expired"
                    and payload["h"] == hwid_hash):
                old_key = payload["k"]
        if old_key is None:
            candidate = clamp(data.get("old_key"), "old_key")
            if candidate and key_ok(candidate):
                old_key = candidate
        if not old_key:
            return jsonify({"success": False, "error": "no_proof"}), 401
        if old_key == new_key:
            return jsonify({"success": False, "error": "same_key"}), 400

        old = conn.execute(storage.sql(
            "SELECT status, expires_at, product FROM licenses WHERE key=?"),
            (old_key,)).fetchone()
        if old is None or old["status"] != "active" or old["expires_at"] < now():
            return jsonify({"success": False, "status": "current_license_invalid",
                            "error": "current_license_invalid"}), 401
        if conn.execute(storage.sql(
            "SELECT 1 FROM activations WHERE license_key=? AND hwid_hash=?"),
            (old_key, hwid_hash)).fetchone() is None:
            return jsonify({"success": False, "status": "not_activated",
                            "error": "not_activated"}), 401

        nw = conn.execute(storage.sql(
            "SELECT status, expires_at, product FROM licenses WHERE key=?"),
            (new_key,)).fetchone()
        if nw is None:
            return jsonify({"success": False, "error": "invalid_key"}), 404
        if nw["status"] != "active":
            return jsonify({"success": False, "status": "revoked",
                            "error": "license_revoked"}), 403
        if nw["expires_at"] < now():
            return jsonify({"success": False, "status": "expired",
                            "error": "license_expired"}), 403
        bound = [r["hwid_hash"] for r in conn.execute(storage.sql(
            "SELECT hwid_hash FROM activations WHERE license_key=?"),
            (new_key,)).fetchall()]
        max_devices = PRODUCT_MAX_DEVICES.get(nw["product"], 1)
        if hwid_hash not in bound and len(bound) >= max_devices:
            return jsonify({"success": False, "status": "device_limit_exceeded",
                            "error": "device_limit_exceeded",
                            "detail": f"{nw['product']} tier allows "
                                      f"{max_devices} device(s)"}), 403

        conn.execute(storage.sql(
            "DELETE FROM activations WHERE license_key=? AND hwid_hash=?"),
            (old_key, hwid_hash))
        if mode == "revoke":
            conn.execute(storage.sql(
                "UPDATE licenses SET status='revoked', revoked_at=? WHERE key=?"),
                (now(), old_key))
        conn.execute(storage.sql(
            "INSERT INTO activations"
            " (license_key, hwid_hash, activated_at, last_seen_at)"
            " VALUES (?,?,?,?)"
            " ON CONFLICT(license_key, hwid_hash) DO UPDATE SET"
            " activated_at=excluded.activated_at,"
            " last_seen_at=excluded.last_seen_at"),
            (new_key, hwid_hash, now(), now()))
        conn.execute(storage.sql(
            "UPDATE sentry_devices SET license_key=?"
            " WHERE license_key=? AND hwid_hash=?"),
            (new_key, old_key, hwid_hash))

    features = features_for(nw["product"])
    token = issue_token(new_key, hwid_hash, nw["product"], features)
    return jsonify({
        "success": True, "status": "upgraded",
        "token": token,
        "product": nw["product"],
        "features": features,
        "expires_at": nw["expires_at"],
        "expires_at_iso": datetime.fromtimestamp(
            nw["expires_at"], tz=timezone.utc).isoformat(),
        "previous": {"key": old_key, "product": old["product"]},
        "new": {"key": new_key, "product": nw["product"]},
    })


@app.post("/api/v1/admin/revoke")
@limited("admin")
def admin_revoke():
    if not admin_ok():
        return jsonify({"success": False, "error": "unauthorized"}), 401
    data = body_json()
    action = data.get("action")
    key = clamp(data.get("key"), "key")
    if action in ("revoke_key", "unrevoke"):
        if not key:
            return jsonify({"success": False, "error": "key required"}), 400
        with db() as conn:
            row = conn.execute(storage.sql(
                "SELECT 1 FROM licenses WHERE key=?"), (key,)).fetchone()
            if row is None:
                return jsonify({"success": False, "error": "invalid_key"}), 404
            status = "revoked" if action == "revoke_key" else "active"
            revoked_at = now() if action == "revoke_key" else None
            conn.execute(storage.sql(
                "UPDATE licenses SET status=?, revoked_at=? WHERE key=?"),
                (status, revoked_at, key))
        return jsonify({"success": True, "status": status, "key": key})
    if action == "revoke_binding":
        hwid = clamp(data.get("hwid"), "hwid")
        if not key or not hwid:
            return jsonify({"success": False,
                            "error": "key and hwid required"}), 400
        hwid_hash = normalize_hwid(hwid)
        with db() as conn:
            cur = conn.execute(storage.sql(
                "DELETE FROM activations WHERE license_key=? AND hwid_hash=?"),
                (key, hwid_hash))
        if cur.rowcount == 0:
            return jsonify({"success": False, "error": "binding_not_found"}), 404
        return jsonify({"success": True, "status": "binding_revoked",
                        "key": key, "hwid_hash": hwid_hash})
    return jsonify({"success": False, "error": "invalid action"}), 400


def main():
    parser = argparse.ArgumentParser(description="Licensing backend")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8000)
    parser.add_argument("--tls", action="store_true")
    parser.add_argument("--cert", default="cert.pem")
    parser.add_argument("--key", default="key.pem")
    parser.add_argument("--prune-once", action="store_true",
                        help="run the telemetry retention pass and exit")
    args = parser.parse_args()
    init_db()
    init_sentry_db()
    if args.prune_once:
        removed = prune_telemetry()
        print(f"[{now_utc().isoformat()}] retention pass: "
              f"pruned {removed} row(s)", flush=True)
        return
    start_alert_engine()
    print(f"[{now_utc().isoformat()}] license server on "
          f"{'https' if args.tls else 'http'}://{args.host}:{args.port} "
          f"(dialect={storage.dialect()}, db={DB_PATH})", flush=True)
    if ADMIN_KEY.startswith("change-me") or HMAC_SECRET.startswith("change-me"):
        print("WARNING: default secrets in use - set LIC_ADMIN_KEY and "
              "LIC_HMAC_SECRET", flush=True)
    try:
        app.run(host=args.host, port=args.port, debug=False,
                use_reloader=False,
                ssl_context=(args.cert, args.key) if args.tls else None)
    except PermissionError:
        sys.exit("Cannot bind port (run as admin or use another port).")


if __name__ == "__main__":
    main()
