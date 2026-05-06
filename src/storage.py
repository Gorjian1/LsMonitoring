"""Local SQLite cache of readings, so the tool keeps history beyond the
gateway's current.csv window."""
from __future__ import annotations

import sqlite3
from contextlib import contextmanager
from datetime import datetime
from pathlib import Path
from typing import Iterable, Iterator, Optional

from .parser import Reading


SCHEMA = """
CREATE TABLE IF NOT EXISTS readings (
    node_id    INTEGER NOT NULL,
    ts         TEXT    NOT NULL,
    temperature REAL,
    a_axis     REAL,
    b_axis     REAL,
    a_var      REAL,
    b_var      REAL,
    invalid    INTEGER NOT NULL DEFAULT 0,
    reasons    TEXT,
    PRIMARY KEY (node_id, ts)
);
CREATE INDEX IF NOT EXISTS idx_readings_node_ts ON readings(node_id, ts);
"""


class Storage:
    def __init__(self, db_path: Path):
        self.db_path = db_path
        self.db_path.parent.mkdir(parents=True, exist_ok=True)
        with self._conn() as c:
            c.executescript(SCHEMA)

    @contextmanager
    def _conn(self) -> Iterator[sqlite3.Connection]:
        c = sqlite3.connect(self.db_path)
        try:
            yield c
            c.commit()
        finally:
            c.close()

    def upsert_readings(self, node_id: int, readings: Iterable[Reading]) -> int:
        rows = [
            (
                node_id,
                r.timestamp.isoformat(timespec="seconds"),
                r.temperature,
                r.a_axis,
                r.b_axis,
                r.a_variation,
                r.b_variation,
                1 if r.invalid else 0,
                "; ".join(r.invalid_reasons) if r.invalid_reasons else None,
            )
            for r in readings
        ]
        if not rows:
            return 0
        with self._conn() as c:
            c.executemany(
                """
                INSERT OR IGNORE INTO readings
                  (node_id, ts, temperature, a_axis, b_axis, a_var, b_var, invalid, reasons)
                VALUES (?,?,?,?,?,?,?,?,?)
                """,
                rows,
            )
            return c.total_changes

    def load_readings(self, node_id: int, limit: Optional[int] = None) -> list[Reading]:
        q = "SELECT ts, temperature, a_axis, b_axis, a_var, b_var, invalid, reasons FROM readings WHERE node_id=? ORDER BY ts ASC"
        params: tuple = (node_id,)
        if limit:
            q = (
                "SELECT ts, temperature, a_axis, b_axis, a_var, b_var, invalid, reasons FROM ("
                "SELECT * FROM readings WHERE node_id=? ORDER BY ts DESC LIMIT ?"
                ") ORDER BY ts ASC"
            )
            params = (node_id, limit)
        out: list[Reading] = []
        with self._conn() as c:
            for ts, t, a, b, av, bv, inv, reasons in c.execute(q, params):
                r = Reading(
                    timestamp=datetime.fromisoformat(ts),
                    temperature=t,
                    a_axis=a,
                    b_axis=b,
                    a_variation=av,
                    b_variation=bv,
                    invalid=bool(inv),
                    invalid_reasons=(reasons or "").split("; ") if reasons else [],
                )
                out.append(r)
        return out

    def known_nodes(self) -> list[int]:
        with self._conn() as c:
            return [row[0] for row in c.execute("SELECT DISTINCT node_id FROM readings ORDER BY node_id")]
