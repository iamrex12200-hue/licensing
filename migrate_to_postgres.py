#!/usr/bin/env python3
"""SQLite -> PostgreSQL migration tool for the licensing/Sentinel backend.

Runbook (server must be STOPPED during migration):
  1. python migrate_to_postgres.py --sqlite licenses.db --dry-run
       - prints the generated PostgreSQL DDL and per-table row counts
  2. export LIC_DATABASE_URL=postgres://user:pass@host:5432/licensing
     python migrate_to_postgres.py --sqlite licenses.db --apply
       - creates the PG schema, copies every table (transactional), verifies
  3. start the server with LIC_DB_DIALECT=postgres + LIC_DATABASE_URL set

Schema introspection maps SQLite types to PostgreSQL; indexes are recreated.
The tool refuses to touch a target table that already contains rows.
"""

import argparse
import json
import os
import re
import sqlite3
import sys

TYPE_MAP = {
    "INTEGER PRIMARY KEY AUTOINCREMENT": "BIGSERIAL PRIMARY KEY",
    "INTEGER": "BIGINT",
    "REAL": "DOUBLE PRECISION",
    "TEXT": "TEXT",
    "DATETIME": "TIMESTAMPTZ",
    "BLOB": "BYTEA",
    "NUMERIC": "NUMERIC",
}
SKIP_TABLES = {"sqlite_sequence"}


def inspect_sqlite(path):
    conn = sqlite3.connect(path)
    conn.row_factory = sqlite3.Row
    tables = {}
    for row in conn.execute(
            "SELECT name, sql FROM sqlite_master WHERE type='table'"):
        name = row["name"]
        if name in SKIP_TABLES:
            continue
        cols = [dict(c) for c in conn.execute(
            f"PRAGMA table_info({name})")]
        tables[name] = {"columns": cols,
                        "sql": row["sql"] or "",
                        "count": conn.execute(
                            f"SELECT COUNT(*) FROM {name}").fetchone()[0]}
    indexes = [dict(r) for r in conn.execute(
        "SELECT name, sql FROM sqlite_master"
        " WHERE type='index' AND sql IS NOT NULL"
        " AND name NOT LIKE 'sqlite_autoindex%'")]
    return conn, tables, indexes


def pg_column_def(col, single_pk):
    declared = col["type"].upper()
    mapped = TYPE_MAP.get(declared, declared)
    if col["pk"] and declared == "INTEGER" and single_pk:
        mapped = "BIGSERIAL PRIMARY KEY"
    parts = [f'"{col["name"]}"', mapped]
    if col["pk"] and declared != "INTEGER" and single_pk:
        parts.append("PRIMARY KEY")
    if col["notnull"]:
        parts.append("NOT NULL")
    if col["dflt_value"]:
        parts.append("DEFAULT " + col["dflt_value"])
    return " ".join(parts)


def pg_table_constraints(table_sql):
    """Carries over composite PRIMARY KEY / UNIQUE table constraints from the
    original SQLite DDL (matching on parens so column-level PKs are skipped)."""
    return re.findall(r"(?:PRIMARY KEY|UNIQUE)\s*\([^)]*\)", table_sql)


def pg_index_sql(index):
    sql = index["sql"]
    return sql.replace("CREATE INDEX", "CREATE INDEX").replace(";", "")


def render_pg_ddl(tables, indexes):
    stmts = []
    for name, info in tables.items():
        pk_cols = [c for c in info["columns"] if c["pk"]]
        single_pk = len(pk_cols) == 1
        defs = [pg_column_def(c, single_pk) for c in info["columns"]]
        for con in pg_table_constraints(info["sql"]):
            if single_pk and con.startswith("PRIMARY KEY"):
                continue
            defs.append(con)
        stmts.append(f'CREATE TABLE "{name}" (\n  {",\n  ".join(defs)}\n);')
    for idx in indexes:
        stmts.append(pg_index_sql(idx))
    return stmts


def copy_table(pg_conn, tables, name):
    info = tables[name]
    cols = info["columns"]
    col_names = [c["name"] for c in cols]
    placeholders = ", ".join(["%s"] * len(col_names))
    quoted = ", ".join(f'"{c}"' for c in col_names)
    stmt = f'INSERT INTO "{name}" ({quoted}) VALUES ({placeholders})'
    sqlite_conn = sqlite3.connect(args.sqlite)
    sqlite_conn.row_factory = sqlite3.Row
    rows = sqlite_conn.execute(f"SELECT * FROM {name}")
    batch = []
    inserted = 0
    for row in rows:
        batch.append(tuple(row[c] for c in col_names))
        if len(batch) >= 1000:
            pg_conn.executemany(stmt, batch)
            batch = []
            inserted += 1000
    if batch:
        pg_conn.executemany(stmt, batch)
        inserted += len(batch)
    sqlite_conn.close()
    return inserted


def main():
    global args
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--sqlite", required=True, help="path to the SQLite DB")
    p.add_argument("--dry-run", action="store_true",
                   help="print PG DDL + counts without touching anything")
    p.add_argument("--apply", action="store_true",
                   help="create PG schema and copy all rows")
    args = p.parse_args()
    if args.dry_run == args.apply:
        sys.exit("exactly one of --dry-run or --apply is required")

    conn, tables, indexes = inspect_sqlite(args.sqlite)
    ddl = render_pg_ddl(tables, indexes)
    print(f"tables: {', '.join(sorted(tables))}")
    for name, info in sorted(tables.items()):
        print(f"  {name}: {info['count']} rows")
    print("\n-- generated PostgreSQL DDL --")
    for stmt in ddl:
        print(stmt + "\n")

    if args.apply:
        url = os.environ.get("LIC_DATABASE_URL")
        if not url:
            sys.exit("set LIC_DATABASE_URL (postgres://...) to apply")
        import psycopg
        pg = psycopg.connect(url)
        pg.autocommit = False
        try:
            for name, info in sorted(tables.items()):
                existing = pg.execute(
                    f'SELECT COUNT(*) FROM "{name}"').fetchone()[0]
                if existing:
                    sys.exit(f"refusing: table {name} already has "
                             f"{existing} rows - truncate it first")
            for stmt in ddl:
                pg.execute(stmt)
            for name in sorted(tables):
                copied = copy_table(pg, tables, name)
                got = pg.execute(
                    f'SELECT COUNT(*) FROM "{name}"').fetchone()[0]
                print(f"  {name}: copied {copied}, verified {got}")
                if copied != got or copied != tables[name]["count"]:
                    raise SystemExit(f"row count mismatch on {name}")
            pg.commit()
            print("\nmigration complete - start the server with "
                  "LIC_DB_DIALECT=postgres and LIC_DATABASE_URL")
        except Exception:
            pg.rollback()
            raise
        finally:
            pg.close()
    conn.close()


if __name__ == "__main__":
    main()
