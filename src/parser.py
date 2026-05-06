"""LoadSensing CMT-Edge readings CSV parser.

Format:
  - Lines 1..N: metadata "Key",value
  - Then a header line containing Date-and-time + per-channel columns
  - Then data rows (may have FEWER columns than header — Variation cols
    can be missing when no reference point is set on the node)

Column names are per-node/per-channel: e.g. Aaxis-6989-Ch1, so we match
by pattern, not by exact name.
"""
from __future__ import annotations

import csv
import io
import re
from dataclasses import dataclass, field
from datetime import datetime
from pathlib import Path
from typing import Iterable, Optional

SENSOR_RANGE_DEG = 15.0  # ±15° for LS-G6-INC15; |val| > this => INVALID

_TS_FORMATS = ("%Y-%m-%d %H:%M:%S", "%Y-%m-%dT%H:%M:%S")


def _parse_ts(s: str) -> Optional[datetime]:
    s = s.strip().strip('"')
    for fmt in _TS_FORMATS:
        try:
            return datetime.strptime(s, fmt)
        except ValueError:
            pass
    return None


def _to_float(s: str) -> Optional[float]:
    s = s.strip().strip('"')
    if not s:
        return None
    try:
        return float(s)
    except ValueError:
        return None


@dataclass
class Reading:
    timestamp: datetime
    temperature: Optional[float] = None
    a_axis: Optional[float] = None
    b_axis: Optional[float] = None
    a_variation: Optional[float] = None
    b_variation: Optional[float] = None
    invalid: bool = False  # any axis sentinel out of ±SENSOR_RANGE_DEG
    invalid_reasons: list[str] = field(default_factory=list)

    def mark_invalid_if_out_of_range(self) -> None:
        for name, v in (("A", self.a_axis), ("B", self.b_axis)):
            if v is not None and abs(v) > SENSOR_RANGE_DEG:
                self.invalid = True
                self.invalid_reasons.append(f"{name}={v:.4f} out of ±{SENSOR_RANGE_DEG}°")


@dataclass
class ParsedCsv:
    metadata: dict[str, str]
    node_id: Optional[int]
    model: Optional[str]
    channel: Optional[str]
    readings: list[Reading]

    @property
    def latest(self) -> Optional[Reading]:
        return self.readings[-1] if self.readings else None

    @property
    def latest_valid(self) -> Optional[Reading]:
        for r in reversed(self.readings):
            if not r.invalid:
                return r
        return None


_COL_PATTERNS = {
    "temp": re.compile(r"^Temp-(\d+)-(Ch\d+)$", re.IGNORECASE),
    "a_axis": re.compile(r"^Aaxis-(\d+)-(Ch\d+)$", re.IGNORECASE),
    "b_axis": re.compile(r"^Baxis-(\d+)-(Ch\d+)$", re.IGNORECASE),
    "a_var": re.compile(r"^AaxisVariation-(\d+)-(Ch\d+)$", re.IGNORECASE),
    "b_var": re.compile(r"^BaxisVariation-(\d+)-(Ch\d+)$", re.IGNORECASE),
}


def _classify_header(cols: list[str]) -> tuple[dict[str, int], Optional[str]]:
    """Returns (mapping field->index, channel)."""
    mapping: dict[str, int] = {}
    channel: Optional[str] = None
    for i, raw in enumerate(cols):
        c = raw.strip().strip('"')
        if not c:
            continue
        if c.lower() == "date-and-time":
            mapping["ts"] = i
            continue
        for field_name, pat in _COL_PATTERNS.items():
            m = pat.match(c)
            if m:
                mapping[field_name] = i
                channel = m.group(2)
                break
    return mapping, channel


def parse_csv(source: str | Path | bytes) -> ParsedCsv:
    if isinstance(source, (str, Path)) and Path(str(source)).exists() and len(str(source)) < 4096:
        text = Path(source).read_text(encoding="utf-8-sig", errors="replace")
    elif isinstance(source, bytes):
        text = source.decode("utf-8-sig", errors="replace")
    else:
        text = str(source)

    metadata: dict[str, str] = {}
    header_idx: Optional[int] = None
    rows: list[list[str]] = []

    reader = csv.reader(io.StringIO(text))
    all_rows = list(reader)

    for i, row in enumerate(all_rows):
        if not row:
            continue
        # data header is the first row whose first cell equals Date-and-time
        first = row[0].strip().strip('"').lower()
        if first == "date-and-time":
            header_idx = i
            break
        if len(row) >= 2:
            key = row[0].strip().strip('"')
            val = row[1].strip().strip('"')
            metadata[key] = val

    if header_idx is None:
        raise ValueError("CSV header (Date-and-time) not found")

    header_cols = all_rows[header_idx]
    mapping, channel = _classify_header(header_cols)
    if "ts" not in mapping:
        raise ValueError("Date-and-time column not found in header")

    for row in all_rows[header_idx + 1:]:
        if not row or all(not (c or "").strip() for c in row):
            continue
        rows.append(row)

    node_id_raw = metadata.get("Node ID") or metadata.get("NodeID")
    node_id = int(node_id_raw) if node_id_raw and node_id_raw.isdigit() else None

    readings: list[Reading] = []
    for row in rows:
        ts = _parse_ts(row[mapping["ts"]]) if mapping["ts"] < len(row) else None
        if ts is None:
            continue

        def cell(field_name: str) -> Optional[float]:
            idx = mapping.get(field_name)
            if idx is None or idx >= len(row):
                return None
            return _to_float(row[idx])

        r = Reading(
            timestamp=ts,
            temperature=cell("temp"),
            a_axis=cell("a_axis"),
            b_axis=cell("b_axis"),
            a_variation=cell("a_var"),
            b_variation=cell("b_var"),
        )
        r.mark_invalid_if_out_of_range()
        readings.append(r)

    return ParsedCsv(
        metadata=metadata,
        node_id=node_id,
        model=metadata.get("Model"),
        channel=channel,
        readings=readings,
    )


def estimate_sampling_interval(readings: Iterable[Reading]) -> Optional[float]:
    """Median delta-seconds between consecutive timestamps (None if <2 readings)."""
    rs = list(readings)
    if len(rs) < 2:
        return None
    deltas = [
        (b.timestamp - a.timestamp).total_seconds()
        for a, b in zip(rs, rs[1:])
        if (b.timestamp - a.timestamp).total_seconds() > 0
    ]
    if not deltas:
        return None
    deltas.sort()
    return deltas[len(deltas) // 2]
