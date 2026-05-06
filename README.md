# LS Monitoring

Desktop monitoring tool for LoadSensing / Worldsensing LS-G6 tilt sensor data.

The repository currently contains the working Python prototype and the active C#/.NET + Avalonia migration. The Avalonia app is the main direction: it keeps the real-time gateway polling, parsing, in-memory chart data, node discovery, CSV loading, and export logic while the Python app remains as a behavior reference.

## Current Status

- C# solution lives in `dotnet/`.
- `LsMonitoring.Core` contains platform-neutral gateway, parser, polling, buffer, alarm-evaluation, and export logic.
- `LsMonitoring.Avalonia` contains the cross-platform desktop UI.
- Python files under `src/` are kept temporarily until the Avalonia version fully replaces them.
- SQLite/history is intentionally deferred for now.
- UI alarms are temporarily disabled; the app currently focuses on showing live data as-is.

## Requirements

- .NET SDK 9.0, pinned by `global.json`.
- Windows 10/11 for the first target build.
- Linux should remain possible through Avalonia once runtime-specific publish commands are added.

For the legacy Python app:

- Python 3.11+
- Dependencies from `requirements.txt`

## Configuration

Real local configuration is stored in `config.json` and is ignored by Git.

Create it from the safe example:

```powershell
Copy-Item .\config.example.json .\config.json
```

Then set the gateway password through the app or by filling `connection.password_b64` with a base64-encoded password. Do not commit real gateway credentials.

## Run Avalonia App

```powershell
dotnet restore .\dotnet\LsMonitoring.sln
dotnet run --project .\dotnet\LsMonitoring.Avalonia\LsMonitoring.Avalonia.csproj
```

When the sensor is unavailable, use `Load CSV` in the app and select `fixtures/sample_readings.csv` to verify parsing, charting, and recent readings.

## Test

```powershell
dotnet test .\dotnet\LsMonitoring.sln
```

## Build

```powershell
dotnet build .\dotnet\LsMonitoring.sln
```

## Publish

Windows self-contained single-file build:

```powershell
dotnet publish .\dotnet\LsMonitoring.Avalonia\LsMonitoring.Avalonia.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

Linux self-contained build, when needed:

```powershell
dotnet publish .\dotnet\LsMonitoring.Avalonia\LsMonitoring.Avalonia.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

## Legacy Python App

```powershell
pip install -r requirements.txt
python .\main.py
```

The Python version is kept as the reference implementation during migration and should not be deleted until the Avalonia app is fully verified against the live gateway.

## Repository Hygiene

- Do not commit `config.json`, `dist/`, `build/`, `data/`, `logs/`, `.dotnet-cli-home/`, or IDE/build outputs.
- Do not commit real gateway credentials or exported private sensor logs.
- Keep `fixtures/sample_readings.csv` only as the current parser/UI fixture.
- Choose and add a `LICENSE` file before publishing this as a public open-source repository.
