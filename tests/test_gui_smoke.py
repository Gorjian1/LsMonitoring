"""Headless smoke test — boots MainWindow with offscreen Qt and feeds a local
parsed CSV through the worker signal path, no real network involved."""
import os
import sys
import math
from pathlib import Path

os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import pytest

from PySide6.QtWidgets import QApplication

from src.config import AppConfig
from src.parser import parse_csv

CSV_PATH = Path(__file__).resolve().parents[1] / "fixtures" / "sample_readings.csv"


@pytest.fixture(scope="module")
def qapp():
    app = QApplication.instance() or QApplication(sys.argv)
    yield app


def test_main_window_constructs_and_ingests_csv(qapp, tmp_path, monkeypatch):
    # redirect app_dir to tmp so we don't touch user's real data
    from src import config as cfg_mod
    monkeypatch.setattr(cfg_mod, "app_dir", lambda: tmp_path)

    # also rebind in main_window module (it already imported app_dir)
    from src.gui import main_window as mw_mod
    monkeypatch.setattr(mw_mod, "app_dir", lambda: tmp_path)

    cfg = AppConfig()
    cfg.connection.password = "dummy"  # skip first-run prompt
    cfg.nodes = [6989]

    win = mw_mod.MainWindow(cfg)
    try:
        # Inject parsed CSV through the worker-signal handler (no network)
        parsed = parse_csv(CSV_PATH)
        win._on_readings_ready(6989, parsed)

        view = win.nodes[6989]
        assert len(view.readings) == len(parsed.readings)
        assert any(r.invalid for r in view.readings)

        # Selecting the node should not crash
        win.node_list.setCurrentRow(0)
        qapp.processEvents()

        # Force redraw + table refresh
        win._refresh_views_for_current()
        assert win.table.rowCount() > 0

        _, a_values = view.curve_a.getData()
        assert any(v is not None and not math.isnan(v) and abs(v) > 15 for v in a_values)
        assert not view.threshold_items
    finally:
        win.close()
        qapp.processEvents()
