# Avalonia Migration TODO

## Goal

Move the working LS-G6 monitoring prototype from Python/PySide to C#/.NET + Avalonia while keeping the current Python app usable until the new app is complete.

## Target Architecture

- `LsMonitoring.Core`: platform-neutral logic for CSV parsing, gateway HTTP scraping, Modbus source skeleton, config, polling, reading buffers.
- `LsMonitoring.Avalonia`: cross-platform desktop UI for Windows now and Linux later.
- `LsMonitoring.Core.Tests`: parser and polling/data-source tests using the current sample CSV.

## Migration Steps

- [x] Create .NET solution under `dotnet/`.
- [x] Port CSV parser exactly enough to pass current sample-file behavior.
- [x] Move the sample gateway CSV fixture to `fixtures/sample_readings.csv`.
- [x] Port config loading/saving with existing `config.json` compatibility.
- [x] Port `CsvScraperSource` with HTTPS, Basic Auth, self-signed certificate support, node discovery, and current CSV fetching.
- [x] Add `ModbusSource` as a typed stub until the official register map arrives.
- [x] Add polling service that merges readings by timestamp and never blocks the UI thread.
- [x] Build an Avalonia UI with node list, connection status, A/B/T plot, and recent rows.
- [x] Move in-memory reading merge/trim logic into Core.
- [x] Add Start/Stop/Poll/Discover controls.
- [x] Add local CSV loading for UI checks when the sensor is unavailable.
- [x] Replace text-only readings output with a column-based recent readings view.
- [x] Add latest A/B/T/sample summary panels.
- [x] Port threshold/evaluation core logic while keeping UI alarms disabled.
- [x] Port stale-node detection based on estimated sampling interval.
- [x] Add Excel-compatible CSV export for current in-memory node data.
- [x] Keep alarms disabled temporarily, matching the current Python app state.
- [x] Verify with unit tests and local build.

## Later

- [x] Add proper settings dialog and secure password storage.
- [x] Add Telegram bot integration to send critical data alerts exclusively to authorized users (by chat ID).
- [x] Re-enable thresholds/alarms as a UI toggle.
- [ ] Add Modbus TCP implementation after vendor register map is known.
- [ ] Produce per-platform single-file publishes: `win-x64` first, then `linux-x64` if needed.
