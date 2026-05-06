"""Command-line poller — sanity-check HTTP+parser without GUI.

Usage:
  python -m src.cli --ip 169.254.0.1 --user admin --password XXX [--node 6989]
  python -m src.cli --csv fixtures/sample_readings.csv         # offline test
"""
from __future__ import annotations

import argparse
import logging
import sys
import time
from pathlib import Path

from .alarms import AlarmConfig, evaluate
from .config import AppConfig, Thresholds
from .datasource import CsvScraperSource
from .logging_setup import setup_logging
from .parser import parse_csv

log = logging.getLogger("cli")


def run_offline(csv_path: Path, thresholds: Thresholds, alarm: AlarmConfig) -> int:
    p = parse_csv(csv_path)
    print(f"Node {p.node_id}  Model {p.model}  Channel {p.channel}  Readings {len(p.readings)}")
    for r in p.readings[-10:]:
        ev = evaluate(r, thresholds, alarm)
        print(
            f"  {r.timestamp}  T={r.temperature}  A={r.a_axis}  B={r.b_axis}  "
            f"-> {ev.status.value}  {('; '.join(ev.reasons)) if ev.reasons else ''}"
        )
    return 0


def run_online(args: argparse.Namespace, cfg: AppConfig) -> int:
    src = CsvScraperSource(
        gateway_ip=args.ip or cfg.connection.gateway_ip,
        username=args.user or cfg.connection.username,
        password=args.password if args.password is not None else cfg.connection.password,
        timeout=cfg.connection.request_timeout_s,
    )
    src.connect()

    nodes = [args.node] if args.node else None
    if not nodes:
        log.info("Discovering nodes…")
        discovered = src.discover_nodes()
        if not discovered:
            log.error("No nodes auto-discovered. Pass --node <id>.")
            return 2
        nodes = [n.node_id for n in discovered]
        log.info("Found nodes: %s", nodes)

    interval = args.interval or cfg.connection.polling_interval_s
    try:
        while True:
            for nid in nodes:
                try:
                    p = src.fetch_csv(nid)
                    last = p.latest
                    if last is None:
                        print(f"[node {nid}] no data")
                        continue
                    ev = evaluate(last, cfg.thresholds, cfg.alarm)
                    print(
                        f"[node {nid}] {last.timestamp}  T={last.temperature}  "
                        f"A={last.a_axis}  B={last.b_axis}  -> {ev.status.value}  "
                        f"{('; '.join(ev.reasons)) if ev.reasons else ''}"
                    )
                except Exception as e:
                    log.error("Fetch failed for node %s: %s", nid, e)
            if args.once:
                return 0
            time.sleep(interval)
    except KeyboardInterrupt:
        return 0
    finally:
        src.disconnect()


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description="LS-G6 monitoring CLI")
    ap.add_argument("--ip")
    ap.add_argument("--user")
    ap.add_argument("--password")
    ap.add_argument("--node", type=int, help="Node ID (else: discover)")
    ap.add_argument("--interval", type=int, help="Poll interval, seconds")
    ap.add_argument("--once", action="store_true", help="Single poll, then exit")
    ap.add_argument("--csv", type=Path, help="Offline: parse this CSV file and exit")
    ap.add_argument("-v", "--verbose", action="store_true")
    args = ap.parse_args(argv)

    setup_logging(args.verbose)
    cfg = AppConfig.load()

    if args.csv:
        return run_offline(args.csv, cfg.thresholds, cfg.alarm)
    return run_online(args, cfg)


if __name__ == "__main__":
    sys.exit(main())
