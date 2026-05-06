"""Application config persisted to config.json next to the .exe.

Password is base64-encoded (NOT encryption — just to keep it out of plaintext).
"""
from __future__ import annotations

import base64
import json
import sys
from dataclasses import dataclass, field, asdict
from pathlib import Path
from typing import Optional


def app_dir() -> Path:
    """Folder where config.json and logs live.

    When frozen by PyInstaller --onefile, sys.executable points at the temp
    extracted exe; we want the directory of the exe shown to the user.
    """
    if getattr(sys, "frozen", False):
        return Path(sys.executable).resolve().parent
    return Path(__file__).resolve().parents[1]


CONFIG_PATH = app_dir() / "config.json"


@dataclass
class Thresholds:
    warning_a: float = 5.0
    critical_a: float = 10.0
    warning_b: float = 5.0
    critical_b: float = 10.0
    same_for_ab: bool = True
    mode: str = "absolute"  # "absolute" or "variation"


@dataclass
class AlarmConfig:
    sound: bool = True
    popup: bool = True
    log_to_csv: bool = True
    invalid_behavior: str = "mark"  # "hide" | "mark" | "alarm"
    invalid_alarm_minutes: int = 5


@dataclass
class Connection:
    gateway_ip: str = "169.254.0.1"
    username: str = "admin"
    password_b64: str = ""
    polling_interval_s: int = 5
    request_timeout_s: int = 8

    @property
    def password(self) -> str:
        if not self.password_b64:
            return ""
        try:
            return base64.b64decode(self.password_b64.encode("ascii")).decode("utf-8")
        except Exception:
            return ""

    @password.setter
    def password(self, value: str) -> None:
        self.password_b64 = base64.b64encode(value.encode("utf-8")).decode("ascii") if value else ""


@dataclass
class AppConfig:
    connection: Connection = field(default_factory=Connection)
    thresholds: Thresholds = field(default_factory=Thresholds)
    alarm: AlarmConfig = field(default_factory=AlarmConfig)
    nodes: list[int] = field(default_factory=list)  # manually-added node ids
    plot_buffer_points: int = 1000
    language: str = "ru"

    @classmethod
    def load(cls, path: Path = CONFIG_PATH) -> "AppConfig":
        if not path.exists():
            return cls()
        try:
            data = json.loads(path.read_text(encoding="utf-8"))
        except Exception:
            return cls()
        return cls.from_dict(data)

    @classmethod
    def from_dict(cls, data: dict) -> "AppConfig":
        cfg = cls()
        if "connection" in data:
            for k, v in data["connection"].items():
                if hasattr(cfg.connection, k):
                    setattr(cfg.connection, k, v)
        if "thresholds" in data:
            for k, v in data["thresholds"].items():
                if hasattr(cfg.thresholds, k):
                    setattr(cfg.thresholds, k, v)
        if "alarm" in data:
            for k, v in data["alarm"].items():
                if hasattr(cfg.alarm, k):
                    setattr(cfg.alarm, k, v)
        cfg.nodes = list(data.get("nodes", []))
        cfg.plot_buffer_points = int(data.get("plot_buffer_points", cfg.plot_buffer_points))
        cfg.language = data.get("language", cfg.language)
        return cfg

    def save(self, path: Path = CONFIG_PATH) -> None:
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(asdict(self), indent=2, ensure_ascii=False), encoding="utf-8")
