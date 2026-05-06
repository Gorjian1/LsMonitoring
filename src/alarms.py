"""Threshold evaluation and alarm-state tracking."""
from __future__ import annotations

import csv
from dataclasses import dataclass
from datetime import datetime, timedelta
from enum import Enum
from pathlib import Path
from typing import Optional

from .config import AlarmConfig, Thresholds
from .parser import Reading


class Status(str, Enum):
    OK = "OK"
    WARNING = "WARNING"
    CRITICAL = "CRITICAL"
    INVALID = "INVALID"


@dataclass
class Evaluation:
    status: Status
    reasons: list[str]
    a_value: Optional[float]
    b_value: Optional[float]


def evaluate(reading: Reading, thresholds: Thresholds, alarm: AlarmConfig) -> Evaluation:
    ev = evaluate_axis_thresholds(reading, thresholds)
    if reading.invalid:
        ev.reasons = reading.invalid_reasons + ev.reasons
        # If raw A/B values also exceed thresholds, keep the threshold status.
        # Otherwise preserve the sensor-invalid marker for logs/table display.
        if ev.status == Status.OK:
            ev.status = Status.INVALID
        return ev
    return ev


def evaluate_axis_thresholds(reading: Reading, thresholds: Thresholds) -> Evaluation:
    reasons: list[str] = []
    if thresholds.mode == "variation":
        a_val = reading.a_variation
        b_val = reading.b_variation
    else:
        a_val = reading.a_axis
        b_val = reading.b_axis

    warn_a = thresholds.warning_a
    crit_a = thresholds.critical_a
    warn_b = thresholds.warning_a if thresholds.same_for_ab else thresholds.warning_b
    crit_b = thresholds.critical_a if thresholds.same_for_ab else thresholds.critical_b

    status = Status.OK
    if a_val is not None:
        if abs(a_val) >= crit_a:
            status = Status.CRITICAL
            reasons.append(f"|A|={abs(a_val):.3f} ≥ critical {crit_a}")
        elif abs(a_val) >= warn_a:
            status = max(status, Status.WARNING, key=_status_rank)
            reasons.append(f"|A|={abs(a_val):.3f} ≥ warning {warn_a}")
    if b_val is not None:
        if abs(b_val) >= crit_b:
            status = Status.CRITICAL
            reasons.append(f"|B|={abs(b_val):.3f} ≥ critical {crit_b}")
        elif abs(b_val) >= warn_b:
            status = max(status, Status.WARNING, key=_status_rank)
            reasons.append(f"|B|={abs(b_val):.3f} ≥ warning {warn_b}")
    return Evaluation(status, reasons, a_val, b_val)


def _status_rank(s: Status) -> int:
    return {Status.OK: 0, Status.INVALID: 0, Status.WARNING: 1, Status.CRITICAL: 2}[s]


class InvalidStreakTracker:
    """Promotes long-running INVALID streaks to CRITICAL when alarm.invalid_behavior == 'alarm'."""

    def __init__(self) -> None:
        self.streak_start: dict[int, datetime] = {}

    def update(self, node_id: int, reading: Reading, alarm: AlarmConfig) -> Optional[Status]:
        if reading.invalid:
            self.streak_start.setdefault(node_id, reading.timestamp)
            if alarm.invalid_behavior == "alarm":
                age = reading.timestamp - self.streak_start[node_id]
                if age >= timedelta(minutes=alarm.invalid_alarm_minutes):
                    return Status.CRITICAL
        else:
            self.streak_start.pop(node_id, None)
        return None


class AlarmLog:
    """Append-only CSV log of alarm transitions."""

    def __init__(self, path: Path):
        self.path = path
        if not path.exists():
            path.parent.mkdir(parents=True, exist_ok=True)
            with path.open("w", encoding="utf-8", newline="") as f:
                csv.writer(f).writerow(
                    ["logged_at", "node_id", "ts", "status", "a", "b", "reasons"]
                )

    def write(self, node_id: int, reading: Reading, ev: Evaluation) -> None:
        with self.path.open("a", encoding="utf-8", newline="") as f:
            csv.writer(f).writerow(
                [
                    datetime.now().isoformat(timespec="seconds"),
                    node_id,
                    reading.timestamp.isoformat(timespec="seconds"),
                    ev.status.value,
                    ev.a_value if ev.a_value is not None else "",
                    ev.b_value if ev.b_value is not None else "",
                    "; ".join(ev.reasons),
                ]
            )
