"""Storage abstraction: SQLite (default) or PostgreSQL.

Single source of truth for dialect differences so the rest of the codebase
stays in portable SQL. Selection is environment-driven:

  LIC_DB_DIALECT     "sqlite" (default) | "postgres"
  LIC_DATABASE_URL   postgres://user:pass@host:5432/db   (postgres only)

Everything else just calls storage.connect(path) and storage.sql(stmt);
the same query text runs on both engines.
"""

import os
import sqlite3

DIALECT = os.environ.get("LIC_DB_DIALECT", "sqlite").lower()
DATABASE_URL = os.environ.get("LIC_DATABASE_URL", "")

TYPE_MAP_PG = {
    "INTEGER PRIMARY KEY AUTOINCREMENT": "BIGSERIAL PRIMARY KEY",
    "INTEGER": "BIGINT",
    "REAL": "DOUBLE PRECISION",
    "DATETIME": "TIMESTAMPTZ",
    "TEXT": "TEXT",
}


def dialect():
    return DIALECT


def sql(stmt):
    """Rewrites shared SQL for the active dialect. SQLite uses '?' params;
    psycopg uses '%s'."""
    if DIALECT == "postgres":
        return stmt.replace("?", "%s")
    return stmt


def connect(path_or_url):
    """Opens a connection. `path_or_url` is the sqlite file path for the
    sqlite dialect (ignored for postgres, which uses LIC_DATABASE_URL)."""
    if DIALECT == "postgres":
        import psycopg
        from psycopg.rows import dict_row
        return psycopg.connect(DATABASE_URL or path_or_url,
                               row_factory=dict_row)
    conn = sqlite3.connect(path_or_url, timeout=15)
    conn.row_factory = sqlite3.Row
    conn.execute("PRAGMA journal_mode=WAL")
    conn.execute("PRAGMA busy_timeout=15000")
    conn.execute("PRAGMA foreign_keys=ON")
    return conn


def col_type(kind):
    if DIALECT == "postgres":
        return TYPE_MAP_PG.get(kind, kind)
    return kind


def render_create_table(table, columns):
    """columns: list of (name, kind, extra). Renders CREATE TABLE IF NOT
    EXISTS with dialect-appropriate types."""
    defs = ", ".join(
        f"{name} {col_type(kind)}" + (f" {extra}" if extra else "")
        for name, kind, extra in columns)
    return f"CREATE TABLE IF NOT EXISTS {table} ({defs})"


def scalar(row):
    """First column value from a row (works for sqlite Row and psycopg dict_row)."""
    return row[0] if not isinstance(row, dict) else next(iter(row.values()))


def last_id(cur):
    """Id of the most recently inserted row (dialect-aware)."""
    if DIALECT == "postgres":
        return cur.fetchone()["id"]
    return cur.lastrowid


def dt_minus(amount, unit):
    """Returns (sql_fragment, extra_params) expressing
    `now() - <amount> <unit>` for comparison against a DATETIME column."""
    if DIALECT == "postgres":
        return f"now() - make_interval({unit} => {int(amount)})", []
    return "datetime('now', ?)", [f"-{int(amount)} {unit}"]


def epoch_seconds(expr):
    """Seconds since epoch of a DATETIME/TIMESTAMPTZ expression."""
    if DIALECT == "postgres":
        return f"EXTRACT(EPOCH FROM {expr})"
    return f"strftime('%s', {expr})"


def epoch_now():
    """Seconds since epoch of the current time."""
    if DIALECT == "postgres":
        return "EXTRACT(EPOCH FROM now())"
    return "strftime('%s','now')"


def add_column_if_missing(conn, table, column, kind):
    """Idempotent ALTER TABLE ADD COLUMN for both dialects."""
    if DIALECT == "postgres":
        conn.execute(f"ALTER TABLE {table} ADD COLUMN IF NOT EXISTS "
                     f"{column} {col_type(kind)}")
        return
    cols = [r[1] for r in conn.execute(f"PRAGMA table_info({table})")]
    if column not in cols:
        conn.execute(f"ALTER TABLE {table} ADD COLUMN {column} "
                     f"{col_type(kind)}")
