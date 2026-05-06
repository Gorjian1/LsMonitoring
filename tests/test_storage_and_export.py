import sys
import tempfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from src.alarms import AlarmConfig, evaluate
from src.config import Thresholds
from src.gui.exporter import export_to_xlsx
from src.parser import parse_csv
from src.storage import Storage

CSV_PATH = Path(__file__).resolve().parents[1] / "fixtures" / "sample_readings.csv"


def test_storage_roundtrip(tmp_path: Path):
    p = parse_csv(CSV_PATH)
    s = Storage(tmp_path / "h.sqlite")
    n = s.upsert_readings(p.node_id, p.readings)
    assert n == len(p.readings)
    # idempotent
    n2 = s.upsert_readings(p.node_id, p.readings)
    assert n2 == 0
    loaded = s.load_readings(p.node_id)
    assert len(loaded) == len(p.readings)
    assert loaded[0].timestamp == p.readings[0].timestamp
    assert loaded[-1].a_axis == p.readings[-1].a_axis


def test_export_xlsx(tmp_path: Path):
    p = parse_csv(CSV_PATH)
    out = tmp_path / "export.xlsx"
    export_to_xlsx(out, {p.node_id: p.readings}, Thresholds(), AlarmConfig())
    assert out.exists()
    assert out.stat().st_size > 1000


def test_evaluate_status():
    p = parse_csv(CSV_PATH)
    th = Thresholds(warning_a=2.5, critical_a=5.0, same_for_ab=True)
    al = AlarmConfig()
    statuses = {evaluate(r, th, al).status.value for r in p.readings}
    assert "OK" in statuses
    assert "WARNING" in statuses or "CRITICAL" in statuses
    assert any(r.invalid and evaluate(r, th, al).status.value == "CRITICAL" for r in p.readings)
