from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from src.parser import parse_csv, estimate_sampling_interval, SENSOR_RANGE_DEG

CSV_PATH = Path(__file__).resolve().parents[1] / "fixtures" / "sample_readings.csv"


def test_parse_real_file():
    p = parse_csv(CSV_PATH)
    assert p.node_id == 6989
    assert p.model == "LS-G6-INC15"
    assert p.channel == "Ch1"
    assert len(p.readings) == 90  # rows 11..100
    first = p.readings[0]
    assert first.temperature == 26.8
    assert first.a_axis == -2.8377
    assert first.b_axis == -0.2681
    assert first.a_variation is None
    assert first.b_variation is None
    assert not first.invalid


def test_sentinel_marked_invalid():
    p = parse_csv(CSV_PATH)
    invalid = [r for r in p.readings if r.invalid]
    assert len(invalid) > 10
    for r in invalid:
        bad = (r.a_axis, r.b_axis)
        assert any(v is not None and abs(v) > SENSOR_RANGE_DEG for v in bad)


def test_latest_valid_skips_sentinel():
    p = parse_csv(CSV_PATH)
    # last row should be valid (-0.1023, 0.3481)
    assert p.latest is not None
    assert not p.latest.invalid
    assert p.latest_valid is p.latest


def test_sampling_interval():
    p = parse_csv(CSV_PATH)
    s = estimate_sampling_interval(p.readings)
    assert s == 30.0  # 30-second cadence


def test_short_data_row_is_ok():
    # all data rows in this file are short (4 cols vs 6 in header)
    p = parse_csv(CSV_PATH)
    assert all(r.a_variation is None and r.b_variation is None for r in p.readings)
