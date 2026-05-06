"""Background polling worker — runs on a QThread, never blocks the UI.

Signals:
  - readings_ready(node_id, ParsedCsv)
  - connection_state(ok: bool, message: str)
  - error(node_id, message)
"""
from __future__ import annotations

import logging
from typing import Optional

from PySide6.QtCore import QObject, QTimer, Signal, Slot

from ..datasource import DataSource

log = logging.getLogger(__name__)


class PollerWorker(QObject):
    readings_ready = Signal(int, object)        # node_id, ParsedCsv
    connection_state = Signal(bool, str)
    error = Signal(int, str)

    def __init__(self, source: DataSource, interval_s: int = 10):
        super().__init__()
        self._source = source
        self._interval_ms = max(1000, int(interval_s * 1000))
        self._nodes: list[int] = []
        self._timer: Optional[QTimer] = None

    # --- runs in the worker thread once moved ---
    @Slot()
    def start(self) -> None:
        try:
            self._source.connect()
            self.connection_state.emit(True, "")
        except Exception as e:
            log.exception("connect failed")
            self.connection_state.emit(False, str(e))
        self._timer = QTimer()
        self._timer.setInterval(self._interval_ms)
        self._timer.timeout.connect(self._tick)
        self._timer.start()
        self._tick()  # immediate first poll

    @Slot()
    def stop(self) -> None:
        if self._timer:
            self._timer.stop()
            self._timer = None
        try:
            self._source.disconnect()
        except Exception:
            pass

    @Slot(list)
    def set_nodes(self, nodes: list[int]) -> None:
        self._nodes = list(nodes)

    @Slot(int)
    def set_interval(self, seconds: int) -> None:
        self._interval_ms = max(1000, int(seconds * 1000))
        if self._timer:
            self._timer.setInterval(self._interval_ms)

    @Slot()
    def poll_now(self) -> None:
        self._tick()

    def _tick(self) -> None:
        if not self._nodes:
            return
        any_ok = False
        last_err = ""
        for nid in list(self._nodes):
            try:
                parsed = self._source.fetch_csv(nid)
                self.readings_ready.emit(nid, parsed)
                any_ok = True
            except Exception as e:
                log.warning("fetch node %s failed: %s", nid, e)
                last_err = str(e)
                self.error.emit(nid, str(e))
        self.connection_state.emit(any_ok, last_err if not any_ok else "")
