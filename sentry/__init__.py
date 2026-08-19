"""Device Sentinel - Milestone 4: registration, telemetry ingest,
dashboard read path, and alert rules (Pro tier).

Mounts under /api/v1/sentry and reuses the core licensing security model
(injected: rate limiter + token/HWID middleware + shared storage path).

Tier rules enforced here (server-side, not client-side):
  basic: 1 active device slot, min 15 min between metric ingests
  pro:   10 device slots,   min 1 min between metric ingests

Dependency-injection note: this package deliberately has NO imports from
license_server. `license_server.py` runs as `__main__`; an import here would
re-execute it as a second module (double Flask app, lost registrations).
license_server passes its middleware in via build_sentry_blueprint().
The `storage` module is a leaf dependency shared by both.
"""

from flask import Blueprint, g, jsonify, request

import storage

TIER_RULES = {
    "basic": {"min_interval_seconds": 900},
    "pro":   {"min_interval_seconds": 60},
}
FALLBACK_TIER = "basic"
MAX_FIELD_LENGTH = 512

ALERT_METRICS = ("cpu_usage", "ram_usage", "disk_usage", "uptime_seconds")
ALERT_OPERATORS = ("gt", "lt")
ALERT_FEATURE = "feature_b"

SENTRY_DEVICES_COLUMNS = [
    ("id", "INTEGER PRIMARY KEY AUTOINCREMENT", ""),
    ("license_key", "TEXT", "NOT NULL"),
    ("hwid_hash", "TEXT", "NOT NULL"),
    ("hostname", "TEXT", ""),
    ("user_id", "TEXT", ""),
    ("registered_at", "DATETIME", "DEFAULT CURRENT_TIMESTAMP"),
    ("last_seen", "DATETIME", "DEFAULT CURRENT_TIMESTAMP"),
    ("last_ingest_at", "DATETIME", ""),
    ("UNIQUE (license_key, hwid_hash)", "", ""),
]
SENTRY_METRICS_COLUMNS = [
    ("id", "INTEGER PRIMARY KEY AUTOINCREMENT", ""),
    ("hwid_hash", "TEXT", "NOT NULL"),
    ("cpu_usage", "REAL", ""),
    ("ram_usage", "REAL", ""),
    ("disk_usage", "REAL", ""),
    ("uptime_seconds", "INTEGER", ""),
    ("recorded_at", "DATETIME", "DEFAULT CURRENT_TIMESTAMP"),
]
ALERT_RULES_COLUMNS = [
    ("id", "INTEGER PRIMARY KEY AUTOINCREMENT", ""),
    ("license_key", "TEXT", "NOT NULL"),
    ("metric", "TEXT", "NOT NULL"),
    ("operator", "TEXT", "NOT NULL DEFAULT 'gt'"),
    ("threshold", "REAL", "NOT NULL"),
    ("enabled", "INTEGER", "NOT NULL DEFAULT 1"),
    ("cooldown_minutes", "INTEGER", "NOT NULL DEFAULT 10"),
    ("webhook_url", "TEXT", ""),
    ("email_to", "TEXT", ""),
    ("created_at", "DATETIME", "DEFAULT CURRENT_TIMESTAMP"),
    ("updated_at", "DATETIME", "DEFAULT CURRENT_TIMESTAMP"),
]
ALERT_EVENTS_COLUMNS = [
    ("id", "INTEGER PRIMARY KEY AUTOINCREMENT", ""),
    ("rule_id", "INTEGER", "NOT NULL"),
    ("license_key", "TEXT", "NOT NULL"),
    ("hwid_hash", "TEXT", "NOT NULL"),
    ("hostname", "TEXT", ""),
    ("metric", "TEXT", "NOT NULL"),
    ("operator", "TEXT", "NOT NULL"),
    ("threshold", "REAL", "NOT NULL"),
    ("observed", "REAL", "NOT NULL"),
    ("dispatched_to", "TEXT", ""),
    ("dispatch_status", "TEXT", "NOT NULL DEFAULT 'logged'"),
    ("created_at", "DATETIME", "DEFAULT CURRENT_TIMESTAMP"),
]


def build_sentry_blueprint(limited, require_feature, db_path, max_devices):
    """Returns (blueprint, init_sentry_db). `limited`/`require_feature` and
    `max_devices` (per-product device caps) are injected by license_server to
    avoid a circular import and keep tier caps defined in exactly one place."""

    sentry_bp = Blueprint("sentry", __name__, url_prefix="/api/v1/sentry")

    def init_sentry_db():
        """Initializes dedicated tables for Device Sentinel (idempotent)."""
        with storage.connect(db_path) as conn:
            conn.execute(storage.render_create_table(
                "sentry_devices", SENTRY_DEVICES_COLUMNS))
            conn.execute(storage.render_create_table(
                "sentry_metrics", SENTRY_METRICS_COLUMNS))
            conn.execute(storage.render_create_table(
                "alert_rules", ALERT_RULES_COLUMNS))
            conn.execute(storage.render_create_table(
                "alert_events", ALERT_EVENTS_COLUMNS))
            conn.execute(storage.sql(
                "CREATE INDEX IF NOT EXISTS idx_sentry_devices_key"
                " ON sentry_devices(license_key)"))
            conn.execute(storage.sql(
                "CREATE INDEX IF NOT EXISTS idx_sentry_metrics_hwid_ts"
                " ON sentry_metrics(hwid_hash, recorded_at)"))
            conn.execute(storage.sql(
                "CREATE INDEX IF NOT EXISTS idx_alert_rules_key"
                " ON alert_rules(license_key)"))
            conn.execute(storage.sql(
                "CREATE INDEX IF NOT EXISTS idx_alert_events_key"
                " ON alert_events(license_key)"))
            conn.execute(storage.sql(
                "CREATE INDEX IF NOT EXISTS idx_alert_events_cooldown"
                " ON alert_events(rule_id, hwid_hash, created_at)"))
            storage.add_column_if_missing(
                conn, "sentry_devices", "last_ingest_at", "DATETIME")
            storage.add_column_if_missing(
                conn, "sentry_devices", "user_id", "TEXT")

    def _tier_rules(product):
        return TIER_RULES.get(product, TIER_RULES[FALLBACK_TIER])

    def _clean_str(value, default):
        if not isinstance(value, str):
            return default
        value = value.strip()
        return value[:MAX_FIELD_LENGTH] if value else default

    def _clean_pct(value):
        if isinstance(value, bool) or not isinstance(value, (int, float)):
            return 0.0
        value = float(value)
        if value != value or value in (float("inf"), float("-inf")):
            return 0.0
        return max(0.0, min(100.0, value))

    def _clean_uptime(value):
        if isinstance(value, bool) or not isinstance(value, (int, float)):
            return 0
        return max(0, int(value))

    @sentry_bp.get("/ping")
    @limited("validate")
    @require_feature("feature_a")
    def sentry_ping():
        """Milestone 1 auth/health probe for Sentinel agents."""
        lic = g.license
        return jsonify({
            "status": "active",
            "message": "Device Sentinel telemetry link established.",
            "product": lic["p"],
            "features": lic["f"],
            "hwid_bound": lic["h"],
        })

    @sentry_bp.post("/devices")
    @limited("sentry")
    @require_feature("feature_a")
    def register_device():
        """Registers (or updates) the calling device for the active license."""
        lic = g.license
        key, hwid, product = lic["k"], lic["h"], lic["p"]
        data = request.get_json(silent=True) or {}
        hostname = _clean_str(data.get("hostname"), "unknown-host")
        user_id = _clean_str(data.get("user_id"), "")

        with storage.connect(db_path) as conn:
            registered = [r["hwid_hash"] for r in conn.execute(
                storage.sql(
                    "SELECT hwid_hash FROM sentry_devices WHERE license_key=?"),
                (key,))]
            cap = max_devices.get(product, 1)
            if hwid not in registered and len(registered) >= cap:
                return jsonify({
                    "success": False,
                    "error": "device_limit_exceeded",
                    "detail": f"{product} tier allows {cap} active device(s)",
                    "devices_bound": len(registered),
                }), 403
            conn.execute(storage.sql(
                "INSERT INTO sentry_devices"
                " (license_key, hwid_hash, hostname, user_id, last_seen)"
                " VALUES (?,?,?,?,CURRENT_TIMESTAMP)"
                " ON CONFLICT(license_key, hwid_hash)"
                " DO UPDATE SET last_seen=CURRENT_TIMESTAMP,"
                "               hostname=excluded.hostname,"
                "               user_id=excluded.user_id"),
                (key, hwid, hostname, user_id))
            bound = storage.scalar(conn.execute(storage.sql(
                "SELECT COUNT(*) FROM sentry_devices WHERE license_key=?"),
                (key,)).fetchone())
        return jsonify({
            "success": True,
            "status": "registered",
            "hwid_hash": hwid,
            "hostname": hostname,
            "product": product,
            "devices_bound": bound,
        })

    @sentry_bp.post("/metrics")
    @limited("validate")
    @require_feature("feature_a")
    def ingest_metrics():
        """Ingests live system metrics from the authenticated agent.

        Enforces the tier cadence (basic: 15 min, pro: 1 min per device).
        """
        lic = g.license
        key, hwid, product = lic["k"], lic["h"], lic["p"]
        data = request.get_json(silent=True) or {}
        cpu = _clean_pct(data.get("cpu_usage"))
        ram = _clean_pct(data.get("ram_usage"))
        disk = _clean_pct(data.get("disk_usage"))
        uptime = _clean_uptime(data.get("uptime_seconds"))
        rules = _tier_rules(product)

        with storage.connect(db_path) as conn:
            row = conn.execute(storage.sql(
                "SELECT last_ingest_at FROM sentry_devices"
                " WHERE license_key=? AND hwid_hash=?"),
                (key, hwid)).fetchone()
            if row is None:
                return jsonify({
                    "success": False,
                    "error": "device_not_registered",
                    "detail": "register the device via POST /api/v1/sentry/devices first",
                }), 409
            if row["last_ingest_at"] is not None:
                since = storage.scalar(conn.execute(storage.sql(
                    f"SELECT CAST({storage.epoch_now()} AS INTEGER)"
                    f" - CAST({storage.epoch_seconds('last_ingest_at')} AS INTEGER)"
                    " FROM sentry_devices WHERE license_key=? AND hwid_hash=?"),
                    (key, hwid)).fetchone())
                if since < rules["min_interval_seconds"]:
                    wait = rules["min_interval_seconds"] - since
                    resp = jsonify({
                        "success": False,
                        "error": "cadence_violation",
                        "retry_after": wait,
                        "min_interval_seconds": rules["min_interval_seconds"],
                    })
                    resp.headers["Retry-After"] = str(wait)
                    return resp, 429
            conn.execute(storage.sql(
                "INSERT INTO sentry_metrics"
                " (hwid_hash, cpu_usage, ram_usage, disk_usage, uptime_seconds)"
                " VALUES (?,?,?,?,?)"),
                (hwid, cpu, ram, disk, uptime))
            conn.execute(storage.sql(
                "UPDATE sentry_devices SET last_seen=CURRENT_TIMESTAMP,"
                " last_ingest_at=CURRENT_TIMESTAMP"
                " WHERE license_key=? AND hwid_hash=?"),
                (key, hwid))
        return jsonify({
            "success": True,
            "status": "recorded",
            "hwid": hwid,
            "product": product,
            "interval_seconds": rules["min_interval_seconds"],
        })

    @sentry_bp.get("/dashboard")
    @limited("validate")
    @require_feature("feature_a")
    def get_dashboard():
        """Device list, per-device summary, and telemetry history.

        Tier-gated retention: feature_b (pro) unlocks 30 days of history,
        basic is capped at 24 hours.
        """
        lic = g.license
        key = lic["k"]
        features = lic["f"]
        tier = "pro" if "feature_b" in features else "basic"
        history_hours = 720 if tier == "pro" else 24
        window_expr, window_params = storage.dt_minus(history_hours, "hours")

        with storage.connect(db_path) as conn:
            devices = [dict(r) for r in conn.execute(
                storage.sql(
                    "SELECT hwid_hash, hostname, user_id, registered_at, last_seen"
                    " FROM sentry_devices WHERE license_key=?"
                    " ORDER BY registered_at"),
                (key,))]
            hwids = [d["hwid_hash"] for d in devices]
            metrics = []
            if hwids:
                placeholders = ",".join("?" * len(hwids))
                metrics = [dict(r) for r in conn.execute(
                    storage.sql(
                        "SELECT hwid_hash, cpu_usage, ram_usage, disk_usage,"
                        " uptime_seconds, recorded_at"
                        " FROM sentry_metrics WHERE hwid_hash IN ("
                        + placeholders + ")"
                        f" AND recorded_at >= {window_expr}"
                        " ORDER BY recorded_at ASC"),
                    hwids + window_params)]

        by_hwid = {}
        for m in metrics:
            by_hwid.setdefault(m["hwid_hash"], []).append(m)

        summary = []
        for d in devices:
            rows = by_hwid.get(d["hwid_hash"], [])
            if not rows:
                summary.append({
                    "hwid_hash": d["hwid_hash"],
                    "hostname": d.get("user_id") or d["hostname"],
                    "user_id": d.get("user_id") or "",
                    "samples": 0,
                    "avg_cpu_usage": None,
                    "avg_ram_usage": None,
                    "avg_disk_usage": None,
                    "latest": None,
                })
                continue
            n = len(rows)
            summary.append({
                "hwid_hash": d["hwid_hash"],
                "hostname": d.get("user_id") or d["hostname"],
                "user_id": d.get("user_id") or "",
                "samples": n,
                "avg_cpu_usage": round(sum(r["cpu_usage"] for r in rows) / n, 1),
                "avg_ram_usage": round(sum(r["ram_usage"] for r in rows) / n, 1),
                "avg_disk_usage": round(sum(r["disk_usage"] for r in rows) / n, 1),
                "latest": {
                    "cpu_usage": rows[-1]["cpu_usage"],
                    "ram_usage": rows[-1]["ram_usage"],
                    "disk_usage": rows[-1]["disk_usage"],
                    "uptime_seconds": rows[-1]["uptime_seconds"],
                    "recorded_at": rows[-1]["recorded_at"],
                },
            })

        return jsonify({
            "success": True,
            "tier": tier,
            "history_window_hours": history_hours,
            "devices": devices,
            "summary": summary,
            "metrics": metrics,
        })

    def _clean_rule_input(data, require_metric=True):
        """Validates alert rule fields; returns (fields_dict, error_message)."""
        metric = _clean_str(data.get("metric"), "")
        if require_metric and metric not in ALERT_METRICS:
            return None, f"metric must be one of {list(ALERT_METRICS)}"
        if metric and metric not in ALERT_METRICS:
            return None, f"metric must be one of {list(ALERT_METRICS)}"
        operator = _clean_str(data.get("operator"), "gt")
        if operator not in ALERT_OPERATORS:
            return None, f"operator must be one of {list(ALERT_OPERATORS)}"
        threshold = data.get("threshold")
        if isinstance(threshold, bool) or not isinstance(threshold, (int, float)):
            return None, "threshold must be numeric"
        threshold = float(threshold)
        if threshold != threshold or threshold in (float("inf"), float("-inf")):
            return None, "threshold must be a finite number"
        webhook = _clean_str(data.get("webhook_url"), "") or None
        if webhook and not webhook.startswith(("http://", "https://")):
            return None, "webhook_url must be http(s)"
        email = _clean_str(data.get("email_to"), "") or None
        cooldown = data.get("cooldown_minutes", 10)
        try:
            cooldown = int(cooldown)
        except (TypeError, ValueError):
            return None, "cooldown_minutes must be an integer"
        cooldown = max(0, min(1440, cooldown))
        return {
            "metric": metric,
            "operator": operator,
            "threshold": threshold,
            "enabled": 1 if data.get("enabled", True) else 0,
            "cooldown_minutes": cooldown,
            "webhook_url": webhook,
            "email_to": email,
        }, None

    def _rule_row(conn, rule_id, key):
        return conn.execute(
            storage.sql(
                "SELECT * FROM alert_rules WHERE id=? AND license_key=?"),
            (rule_id, key)).fetchone()

    @sentry_bp.get("/alerts")
    @limited("validate")
    @require_feature(ALERT_FEATURE)
    def list_alerts():
        """Lists the caller's alert rules (Pro tier, owner-scoped)."""
        key = g.license["k"]
        with storage.connect(db_path) as conn:
            rows = conn.execute(storage.sql(
                "SELECT * FROM alert_rules WHERE license_key=?"
                " ORDER BY created_at DESC, id DESC"),
                (key,)).fetchall()
        return jsonify({"success": True, "rules": [dict(r) for r in rows]})

    @sentry_bp.post("/alerts")
    @limited("validate")
    @require_feature(ALERT_FEATURE)
    def create_alert():
        """Creates an alert rule for the caller's license key."""
        key = g.license["k"]
        fields, err = _clean_rule_input(request.get_json(silent=True) or {})
        if err:
            return jsonify({"success": False, "error": "invalid_rule",
                            "detail": err}), 400
        with storage.connect(db_path) as conn:
            cur = conn.execute(storage.sql(
                "INSERT INTO alert_rules"
                " (license_key, metric, operator, threshold, enabled,"
                "  cooldown_minutes, webhook_url, email_to)"
                " VALUES (?,?,?,?,?,?,?,?)"),
                (key, fields["metric"], fields["operator"], fields["threshold"],
                 fields["enabled"], fields["cooldown_minutes"],
                 fields["webhook_url"], fields["email_to"]))
            rule_id = storage.last_id(cur)
            row = conn.execute(
                storage.sql("SELECT * FROM alert_rules WHERE id=?"),
                (rule_id,)).fetchone()
        return jsonify({"success": True, "rule": dict(row)}), 201

    @sentry_bp.put("/alerts/<int:rule_id>")
    @limited("validate")
    @require_feature(ALERT_FEATURE)
    def update_alert(rule_id):
        """Partially updates one of the caller's alert rules."""
        key = g.license["k"]
        data = request.get_json(silent=True) or {}
        with storage.connect(db_path) as conn:
            row = _rule_row(conn, rule_id, key)
            if row is None:
                return jsonify({"success": False,
                                "error": "rule_not_found"}), 404
            merged = dict(row)
            for field in ("metric", "operator", "threshold", "enabled",
                          "cooldown_minutes", "webhook_url", "email_to"):
                if field in data:
                    merged[field] = data[field]
            fields, err = _clean_rule_input(
                merged, require_metric=False)
            if err:
                return jsonify({"success": False, "error": "invalid_rule",
                                "detail": err}), 400
            conn.execute(storage.sql(
                "UPDATE alert_rules SET metric=?, operator=?, threshold=?,"
                " enabled=?, cooldown_minutes=?, webhook_url=?, email_to=?,"
                " updated_at=CURRENT_TIMESTAMP WHERE id=? AND license_key=?"),
                (fields["metric"], fields["operator"], fields["threshold"],
                 fields["enabled"], fields["cooldown_minutes"],
                 fields["webhook_url"], fields["email_to"], rule_id, key))
            row = _rule_row(conn, rule_id, key)
        return jsonify({"success": True, "rule": dict(row)})

    @sentry_bp.delete("/alerts/<int:rule_id>")
    @limited("validate")
    @require_feature(ALERT_FEATURE)
    def delete_alert(rule_id):
        """Deletes one of the caller's alert rules."""
        key = g.license["k"]
        with storage.connect(db_path) as conn:
            cur = conn.execute(storage.sql(
                "DELETE FROM alert_rules WHERE id=? AND license_key=?"),
                (rule_id, key))
        if cur.rowcount == 0:
            return jsonify({"success": False, "error": "rule_not_found"}), 404
        return jsonify({"success": True, "deleted": rule_id})

    @sentry_bp.get("/alerts/events")
    @limited("validate")
    @require_feature(ALERT_FEATURE)
    def list_alert_events():
        """Recent alert events for the caller's license key."""
        key = g.license["k"]
        try:
            limit = max(1, min(500, int(request.args.get("limit", 50))))
        except (TypeError, ValueError):
            limit = 50
        with storage.connect(db_path) as conn:
            rows = conn.execute(storage.sql(
                "SELECT * FROM alert_events WHERE license_key=?"
                " ORDER BY id DESC LIMIT ?"),
                (key, limit)).fetchall()
        return jsonify({"success": True,
                        "events": [dict(r) for r in rows]})

    return sentry_bp, init_sentry_db
