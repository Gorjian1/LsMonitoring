"""Main monitoring window."""
from __future__ import annotations

import logging
import os
import platform
import subprocess
from datetime import datetime, timedelta
from pathlib import Path
from typing import Optional

import pyqtgraph as pg
from PySide6.QtCore import Qt, QThread, QTimer, Signal
from PySide6.QtGui import QAction, QColor, QFont
from PySide6.QtWidgets import (
    QApplication, QFileDialog, QHBoxLayout, QInputDialog, QLabel, QListWidget,
    QListWidgetItem, QMainWindow, QMessageBox, QPushButton, QSplitter, QStatusBar,
    QTableWidget, QTableWidgetItem, QToolBar, QVBoxLayout, QWidget,
)

from ..alarms import AlarmLog, InvalidStreakTracker, Status, evaluate, evaluate_axis_thresholds
from ..config import AppConfig, app_dir
from ..datasource import CsvScraperSource
from ..i18n import t
from ..parser import ParsedCsv, Reading, estimate_sampling_interval
from ..storage import Storage
from .exporter import export_to_xlsx
from .settings_dialog import SettingsDialog
from .worker import PollerWorker

log = logging.getLogger(__name__)

STATUS_COLORS = {
    Status.OK: "#1f8a3a",
    Status.WARNING: "#d68a00",
    Status.CRITICAL: "#c1272d",
    Status.INVALID: "#888888",
}

PLOT_BG_NORMAL = "#ffffff"
PLOT_BG_WARN = "#fff4d6"
PLOT_BG_CRITICAL = "#ffd6d6"
ALARMS_ENABLED = False


def _ts_to_x(t: datetime) -> float:
    return t.timestamp()


class NodeView:
    """Per-node in-memory state: history (Readings list) + plot curves."""

    def __init__(self, node_id: int, plot: pg.PlotWidget, parent_window: "MainWindow"):
        self.node_id = node_id
        self.window = parent_window
        self.plot = plot
        self.readings: list[Reading] = []
        self.last_status: Status = Status.OK
        self.last_alarm_status: Status = Status.OK

        # 3 curves (T, A, B). connect='finite' so NaNs break the line on real time gaps.
        self.curve_t = plot.plot(pen=pg.mkPen("#888888", width=1), name=t("temperature"), connect="finite")
        self.curve_a = plot.plot(pen=pg.mkPen("#1f6feb", width=2), name=t("a_axis"), connect="finite")
        self.curve_b = plot.plot(pen=pg.mkPen("#6f42c1", width=2), name=t("b_axis"), connect="finite")
        self.invalid_regions: list[pg.LinearRegionItem] = []
        self.threshold_items: list[object] = []

    def clear_invalid_regions(self) -> None:
        for r in self.invalid_regions:
            self.plot.removeItem(r)
        self.invalid_regions.clear()

    def clear_threshold_items(self) -> None:
        for item in self.threshold_items:
            self.plot.removeItem(item)
        self.threshold_items.clear()

    def replace_history(self, readings: list[Reading]) -> None:
        self.readings = list(readings)

    def merge_readings(self, new_readings: list[Reading]) -> list[Reading]:
        """Append readings whose timestamp is newer than what we have. Returns
        the list of newly-added readings."""
        if not new_readings:
            return []
        existing_ts = {r.timestamp for r in self.readings}
        added = [r for r in new_readings if r.timestamp not in existing_ts]
        if added:
            self.readings.extend(added)
            self.readings.sort(key=lambda r: r.timestamp)
        return added

    def trim_to(self, max_points: int) -> None:
        if len(self.readings) > max_points:
            self.readings = self.readings[-max_points:]

    def redraw(self, expected_interval_s: Optional[float]) -> None:
        if not self.readings:
            self.curve_t.clear()
            self.curve_a.clear()
            self.curve_b.clear()
            self.clear_invalid_regions()
            return

        xs: list[float] = []
        ts: list[float] = []
        a_vals: list[float] = []
        b_vals: list[float] = []
        gap_thresh = (expected_interval_s or 60.0) * 2.5

        prev_ts: Optional[datetime] = None
        for r in self.readings:
            # insert NaN to break the line if there's a real time gap
            if prev_ts is not None and (r.timestamp - prev_ts).total_seconds() > gap_thresh:
                xs.append(_ts_to_x(prev_ts) + 1.0)
                ts.append(float("nan"))
                a_vals.append(float("nan"))
                b_vals.append(float("nan"))
            xs.append(_ts_to_x(r.timestamp))
            ts.append(r.temperature if r.temperature is not None else float("nan"))
            a_vals.append(r.a_axis if r.a_axis is not None else float("nan"))
            b_vals.append(r.b_axis if r.b_axis is not None else float("nan"))
            prev_ts = r.timestamp

        self.curve_t.setData(xs, ts)
        self.curve_a.setData(xs, a_vals)
        self.curve_b.setData(xs, b_vals)

        # Invalid points are currently plotted as-is; keep old grey bands disabled.
        self.clear_invalid_regions()
        self.clear_threshold_items()
        if ALARMS_ENABLED:
            for x0, x1, status in self._threshold_intervals(expected_interval_s):
                if status == Status.CRITICAL:
                    brush = pg.mkBrush(193, 39, 45, 45)
                    pen = pg.mkPen("#c1272d", width=2)
                else:
                    brush = pg.mkBrush(214, 138, 0, 45)
                    pen = pg.mkPen("#d68a00", width=2)
                region = pg.LinearRegionItem(
                    values=(x0, x1),
                    brush=brush,
                    movable=False,
                )
                region.setZValue(-10)
                self.plot.addItem(region)
                start_line = pg.InfiniteLine(pos=x0, angle=90, pen=pen, movable=False)
                start_line.setZValue(5)
                self.plot.addItem(start_line)
                self.threshold_items.extend([region, start_line])

    def _invalid_intervals(self) -> list[tuple[float, float]]:
        out: list[tuple[float, float]] = []
        start: Optional[datetime] = None
        prev: Optional[datetime] = None
        for r in self.readings:
            if r.invalid:
                if start is None:
                    start = r.timestamp
                prev = r.timestamp
            else:
                if start is not None and prev is not None:
                    out.append((_ts_to_x(start), _ts_to_x(prev) + 1.0))
                start = None
                prev = None
        if start is not None and prev is not None:
            out.append((_ts_to_x(start), _ts_to_x(prev) + 1.0))
        return out

    def _threshold_intervals(self, expected_interval_s: Optional[float]) -> list[tuple[float, float, Status]]:
        out: list[tuple[float, float, Status]] = []
        start: Optional[datetime] = None
        prev: Optional[datetime] = None
        active: Optional[Status] = None
        pad = max(1.0, expected_interval_s or 30.0)

        for r in self.readings:
            ev = evaluate_axis_thresholds(r, self.window.cfg.thresholds)
            status = ev.status if ev.status in (Status.WARNING, Status.CRITICAL) else None
            if status is None:
                if start is not None and prev is not None and active is not None:
                    out.append((_ts_to_x(start), _ts_to_x(prev) + pad, active))
                start = None
                prev = None
                active = None
                continue

            if active is None:
                start = r.timestamp
                active = status
            elif status != active:
                if start is not None and prev is not None:
                    out.append((_ts_to_x(start), _ts_to_x(prev) + pad, active))
                start = r.timestamp
                active = status
            prev = r.timestamp

        if start is not None and prev is not None and active is not None:
            out.append((_ts_to_x(start), _ts_to_x(prev) + pad, active))
        return out


class MainWindow(QMainWindow):
    request_set_nodes = Signal(list)
    request_poll_now = Signal()
    request_set_interval = Signal(int)

    def __init__(self, cfg: AppConfig):
        super().__init__()
        self.cfg = cfg
        self.setWindowTitle(t("app_title"))
        self.resize(1280, 800)

        # Storage / alarm log
        data_dir = app_dir() / "data"
        self.storage = Storage(data_dir / "history.sqlite")
        self.alarm_log = AlarmLog(data_dir / "alarms.csv")
        self.invalid_tracker = InvalidStreakTracker()

        # Per-node views
        self.nodes: dict[int, NodeView] = {}
        self.current_node: Optional[int] = None
        self.last_sample_ts: dict[int, datetime] = {}
        self.expected_interval_s: dict[int, Optional[float]] = {}

        self._build_ui()
        self._build_toolbar()
        self._build_statusbar()

        # First-run: prompt for connection if no password set
        if not self.cfg.connection.password_b64:
            QTimer.singleShot(50, self._first_run_prompt)

        self._init_nodes_from_config_and_storage()
        self._start_worker()

    # ---------- UI construction ----------

    def _build_ui(self) -> None:
        central = QWidget()
        self.setCentralWidget(central)
        outer = QHBoxLayout(central)
        outer.setContentsMargins(4, 4, 4, 4)

        splitter = QSplitter(Qt.Horizontal)
        outer.addWidget(splitter)

        # Left: node list
        left = QWidget()
        left_layout = QVBoxLayout(left)
        left_layout.setContentsMargins(0, 0, 0, 0)
        left_layout.addWidget(QLabel(t("nodes")))
        self.node_list = QListWidget()
        self.node_list.itemSelectionChanged.connect(self._on_node_selected)
        left_layout.addWidget(self.node_list)

        btn_row = QHBoxLayout()
        self.btn_add = QPushButton(t("add_node"))
        self.btn_add.clicked.connect(self._add_node_dialog)
        self.btn_remove = QPushButton(t("remove_node"))
        self.btn_remove.clicked.connect(self._remove_node)
        self.btn_discover = QPushButton(t("discover"))
        self.btn_discover.clicked.connect(self._discover_nodes)
        btn_row.addWidget(self.btn_add)
        btn_row.addWidget(self.btn_remove)
        btn_row.addWidget(self.btn_discover)
        left_layout.addLayout(btn_row)
        splitter.addWidget(left)

        # Right: plot + table
        right = QSplitter(Qt.Vertical)

        pg.setConfigOption("background", PLOT_BG_NORMAL)
        pg.setConfigOption("foreground", "k")
        self.plot = pg.PlotWidget()
        self.plot.addLegend(offset=(10, 10))
        self.plot.showGrid(x=True, y=True, alpha=0.3)
        # X axis as time
        axis = pg.DateAxisItem(orientation="bottom")
        self.plot.setAxisItems({"bottom": axis})
        right.addWidget(self.plot)

        self.table = QTableWidget()
        self.table.setColumnCount(7)
        self.table.setHorizontalHeaderLabels(
            ["Time", "T,°C", "A,°", "B,°", "A-var", "B-var", "Status"]
        )
        self.table.horizontalHeader().setStretchLastSection(True)
        self.table.verticalHeader().setVisible(False)
        right.addWidget(self.table)
        right.setStretchFactor(0, 3)
        right.setStretchFactor(1, 1)

        splitter.addWidget(right)
        splitter.setStretchFactor(0, 0)
        splitter.setStretchFactor(1, 1)
        splitter.setSizes([240, 1040])

    def _build_toolbar(self) -> None:
        tb = QToolBar()
        tb.setMovable(False)
        self.addToolBar(tb)

        act_settings = QAction(t("settings"), self)
        act_settings.triggered.connect(self._open_settings)
        tb.addAction(act_settings)

        act_export = QAction(t("export_excel"), self)
        act_export.triggered.connect(self._export_excel)
        tb.addAction(act_export)

        act_open_folder = QAction(t("open_data_folder"), self)
        act_open_folder.triggered.connect(self._open_data_folder)
        tb.addAction(act_open_folder)

    def _build_statusbar(self) -> None:
        sb = QStatusBar()
        self.setStatusBar(sb)
        self.lbl_dot = QLabel("●")
        self.lbl_dot.setStyleSheet("color: #888;")
        f = QFont()
        f.setPointSize(14)
        self.lbl_dot.setFont(f)
        self.lbl_state = QLabel(t("disconnected"))
        self.lbl_last = QLabel("—")
        sb.addWidget(self.lbl_dot)
        sb.addWidget(self.lbl_state)
        sb.addPermanentWidget(QLabel(t("last_sample")))
        sb.addPermanentWidget(self.lbl_last)

    # ---------- Worker / polling ----------

    def _start_worker(self) -> None:
        self.source = CsvScraperSource(
            gateway_ip=self.cfg.connection.gateway_ip,
            username=self.cfg.connection.username,
            password=self.cfg.connection.password,
            timeout=self.cfg.connection.request_timeout_s,
        )
        self.thread = QThread()
        self.worker = PollerWorker(self.source, self.cfg.connection.polling_interval_s)
        self.worker.moveToThread(self.thread)
        self.worker.readings_ready.connect(self._on_readings_ready)
        self.worker.connection_state.connect(self._on_connection_state)
        self.worker.error.connect(self._on_fetch_error)
        self.request_set_nodes.connect(self.worker.set_nodes)
        self.request_poll_now.connect(self.worker.poll_now)
        self.request_set_interval.connect(self.worker.set_interval)
        self.thread.started.connect(self.worker.start)
        self.thread.start()
        self.request_set_nodes.emit(list(self.nodes.keys()))

        self.heartbeat = QTimer(self)
        self.heartbeat.setInterval(15_000)
        self.heartbeat.timeout.connect(self._refresh_link_indicators)
        self.heartbeat.start()

    def _restart_worker(self) -> None:
        try:
            self.worker.stop()
        except Exception:
            pass
        self.thread.quit()
        self.thread.wait(2000)
        self._start_worker()

    # ---------- Node handling ----------

    def _init_nodes_from_config_and_storage(self) -> None:
        ids = set(self.cfg.nodes)
        ids.update(self.storage.known_nodes())
        for nid in sorted(ids):
            self._ensure_node(nid)
        if self.node_list.count() and self.node_list.currentRow() < 0:
            self.node_list.setCurrentRow(0)

    def _ensure_node(self, node_id: int) -> NodeView:
        if node_id in self.nodes:
            return self.nodes[node_id]
        view = NodeView(node_id, self.plot, self)
        self.nodes[node_id] = view
        item = QListWidgetItem(f"Node {node_id}")
        item.setData(Qt.UserRole, node_id)
        self.node_list.addItem(item)

        # Load existing local history
        history = self.storage.load_readings(node_id, limit=self.cfg.plot_buffer_points)
        view.replace_history(history)
        if history:
            self.last_sample_ts[node_id] = history[-1].timestamp
            self.expected_interval_s[node_id] = estimate_sampling_interval(history)

        if node_id not in self.cfg.nodes:
            self.cfg.nodes.append(node_id)
            self.cfg.save()
        return view

    def _add_node_dialog(self) -> None:
        nid, ok = QInputDialog.getInt(self, t("add_node"), "Node ID:", 1, 1, 9999999)
        if not ok:
            return
        self._ensure_node(nid)
        self.request_set_nodes.emit(list(self.nodes.keys()))
        self.request_poll_now.emit()

    def _remove_node(self) -> None:
        item = self.node_list.currentItem()
        if not item:
            return
        nid = item.data(Qt.UserRole)
        if nid in self.nodes:
            view = self.nodes.pop(nid)
            view.clear_invalid_regions()
            view.clear_threshold_items()
            for c in (view.curve_t, view.curve_a, view.curve_b):
                self.plot.removeItem(c)
        self.node_list.takeItem(self.node_list.row(item))
        if nid in self.cfg.nodes:
            self.cfg.nodes.remove(nid)
            self.cfg.save()
        self.request_set_nodes.emit(list(self.nodes.keys()))

    def _discover_nodes(self) -> None:
        try:
            found = self.source.discover_nodes()
        except Exception as e:
            QMessageBox.warning(self, t("connection_error"), str(e))
            return
        if not found:
            QMessageBox.information(self, t("discover"), "Auto-discovery returned no nodes.\nAdd them manually.")
            return
        for n in found:
            self._ensure_node(n.node_id)
        self.request_set_nodes.emit(list(self.nodes.keys()))
        self.request_poll_now.emit()

    def _on_node_selected(self) -> None:
        item = self.node_list.currentItem()
        if not item:
            return
        nid = item.data(Qt.UserRole)
        self.current_node = nid
        self._refresh_views_for_current()

    def _refresh_views_for_current(self) -> None:
        nid = self.current_node
        if nid is None or nid not in self.nodes:
            return
        view = self.nodes[nid]
        view.redraw(self.expected_interval_s.get(nid))
        self._refresh_table(view)
        self._refresh_plot_background(view)

    # ---------- Worker signals ----------

    def _on_readings_ready(self, node_id: int, parsed: ParsedCsv) -> None:
        view = self._ensure_node(node_id)
        added = view.merge_readings(parsed.readings)
        if added:
            self.storage.upsert_readings(node_id, added)
        view.trim_to(self.cfg.plot_buffer_points)
        if view.readings:
            self.last_sample_ts[node_id] = view.readings[-1].timestamp
            interval = estimate_sampling_interval(view.readings)
            if interval is not None:
                self.expected_interval_s[node_id] = interval

        # Evaluate latest reading and run alarm pipeline
        if added:
            self._process_alarm(node_id, added[-1])

        if node_id == self.current_node:
            self._refresh_views_for_current()
        self._refresh_link_indicators()

    def _on_connection_state(self, ok: bool, message: str) -> None:
        if ok:
            self.lbl_dot.setStyleSheet("color: #1f8a3a;")
            self.lbl_state.setText(f"{t('connected')} — {self.cfg.connection.gateway_ip}")
        else:
            self.lbl_dot.setStyleSheet("color: #c1272d;")
            txt = t("disconnected")
            if message:
                txt += f": {message[:120]}"
            self.lbl_state.setText(txt)

    def _on_fetch_error(self, node_id: int, message: str) -> None:
        log.warning("node %s fetch error: %s", node_id, message)
        if "401" in message or "403" in message or "auth" in message.lower():
            self.lbl_state.setText(t("auth_error"))

    # ---------- Alarms ----------

    def _process_alarm(self, node_id: int, reading: Reading) -> None:
        if not ALARMS_ENABLED:
            return
        ev = evaluate(reading, self.cfg.thresholds, self.cfg.alarm)
        promoted = self.invalid_tracker.update(node_id, reading, self.cfg.alarm)
        if promoted == Status.CRITICAL and ev.status == Status.INVALID:
            ev.status = Status.CRITICAL
            ev.reasons.append(f"INVALID streak ≥ {self.cfg.alarm.invalid_alarm_minutes} min")

        view = self.nodes.get(node_id)
        if view is None:
            return
        prev = view.last_status
        view.last_status = ev.status

        rising = ev.status in (Status.WARNING, Status.CRITICAL) and ev.status != prev
        if rising:
            if self.cfg.alarm.log_to_csv:
                self.alarm_log.write(node_id, reading, ev)
            if self.cfg.alarm.sound:
                QApplication.beep()
            if self.cfg.alarm.popup and ev.status == Status.CRITICAL:
                self._show_alarm_popup(node_id, ev)

    def _show_alarm_popup(self, node_id: int, ev) -> None:
        msg = QMessageBox(self)
        msg.setIcon(QMessageBox.Critical)
        msg.setWindowTitle(t("alarm_title"))
        msg.setText(f"Node {node_id} — {ev.status.value}")
        msg.setInformativeText("\n".join(ev.reasons) if ev.reasons else "")
        msg.setWindowModality(Qt.NonModal)
        msg.show()
        msg.activateWindow()
        msg.raise_()

    # ---------- View refresh ----------

    def _refresh_table(self, view: NodeView) -> None:
        rows = view.readings[-200:]
        self.table.setRowCount(len(rows))
        for i, r in enumerate(rows):
            ev = evaluate(r, self.cfg.thresholds, self.cfg.alarm) if ALARMS_ENABLED else None
            cells = [
                r.timestamp.strftime("%Y-%m-%d %H:%M:%S"),
                "" if r.temperature is None else f"{r.temperature:.2f}",
                "" if r.a_axis is None else f"{r.a_axis:.4f}",
                "" if r.b_axis is None else f"{r.b_axis:.4f}",
                "" if r.a_variation is None else f"{r.a_variation:.4f}",
                "" if r.b_variation is None else f"{r.b_variation:.4f}",
                ev.status.value if ev is not None else "",
            ]
            for j, txt in enumerate(cells):
                item = QTableWidgetItem(txt)
                if j == 6 and ev is not None:
                    item.setForeground(QColor(STATUS_COLORS[ev.status]))
                    f = item.font()
                    f.setBold(ev.status in (Status.CRITICAL, Status.WARNING))
                    item.setFont(f)
                self.table.setItem(i, j, item)
        self.table.scrollToBottom()

    def _refresh_plot_background(self, view: NodeView) -> None:
        if not ALARMS_ENABLED:
            self.plot.setBackground(PLOT_BG_NORMAL)
            return
        latest = view.readings[-1] if view.readings else None
        bg = PLOT_BG_NORMAL
        if latest is not None:
            ev = evaluate(latest, self.cfg.thresholds, self.cfg.alarm)
            if ev.status == Status.CRITICAL:
                bg = PLOT_BG_CRITICAL
            elif ev.status == Status.WARNING:
                bg = PLOT_BG_WARN
        self.plot.setBackground(bg)

    def _refresh_link_indicators(self) -> None:
        # Mark "node link lost" if last sample older than interval × 3 (or 30 min)
        now = datetime.now()
        for i in range(self.node_list.count()):
            item = self.node_list.item(i)
            nid = item.data(Qt.UserRole)
            last = self.last_sample_ts.get(nid)
            if last is None:
                item.setText(f"Node {nid}  —  no data")
                item.setForeground(QColor("#888"))
                continue
            interval = self.expected_interval_s.get(nid) or 600.0
            stale_after = max(timedelta(seconds=interval * 3), timedelta(minutes=30))
            view = self.nodes.get(nid)
            status = view.last_status if view else Status.OK
            if (now - last) > stale_after:
                item.setText(f"Node {nid}  —  ⚠ {t('node_link_lost')}")
                item.setForeground(QColor(STATUS_COLORS[Status.CRITICAL]))
                if nid == self.current_node:
                    self.lbl_last.setText(f"{last.strftime('%Y-%m-%d %H:%M:%S')} (stale)")
            else:
                item.setText(f"Node {nid}  ({status.value})")
                item.setForeground(QColor(STATUS_COLORS[status]))
                if nid == self.current_node:
                    self.lbl_last.setText(last.strftime("%Y-%m-%d %H:%M:%S"))

    # ---------- Misc actions ----------

    def _first_run_prompt(self) -> None:
        QMessageBox.information(self, t("settings"), t("first_run_message"))
        self._open_settings()

    def _open_settings(self) -> None:
        dlg = SettingsDialog(self.cfg, self)
        if dlg.exec() == dlg.Accepted:
            self.request_set_interval.emit(self.cfg.connection.polling_interval_s)
            self._restart_worker()

    def _export_excel(self) -> None:
        if not self.nodes:
            QMessageBox.information(self, t("export_excel"), "Нет данных для экспорта.")
            return
        path, _ = QFileDialog.getSaveFileName(
            self, t("export_excel"), str(app_dir() / "data" / "export.xlsx"), "Excel (*.xlsx)"
        )
        if not path:
            return
        data = {nid: list(view.readings) for nid, view in self.nodes.items()}
        try:
            export_to_xlsx(Path(path), data, self.cfg.thresholds, self.cfg.alarm)
            QMessageBox.information(self, t("export_excel"), f"OK: {path}")
        except Exception as e:
            QMessageBox.warning(self, t("export_excel"), str(e))

    def _open_data_folder(self) -> None:
        folder = app_dir() / "data"
        folder.mkdir(parents=True, exist_ok=True)
        try:
            if platform.system() == "Windows":
                os.startfile(str(folder))  # type: ignore[attr-defined]
            elif platform.system() == "Darwin":
                subprocess.Popen(["open", str(folder)])
            else:
                subprocess.Popen(["xdg-open", str(folder)])
        except Exception as e:
            QMessageBox.warning(self, t("open_data_folder"), str(e))

    def closeEvent(self, ev) -> None:
        try:
            self.worker.stop()
        except Exception:
            pass
        self.thread.quit()
        self.thread.wait(2000)
        super().closeEvent(ev)
