# LS Monitoring Avalonia

This is the C#/.NET + Avalonia migration track. The Python app remains in place while this version catches up.

## Restore

```powershell
dotnet restore .\dotnet\LsMonitoring.sln
```

## Run

```powershell
dotnet run --project .\dotnet\LsMonitoring.Avalonia\LsMonitoring.Avalonia.csproj
```

The app loads the existing root `config.json` when run from the repository, so the current gateway IP, username, password, polling interval, and node list are reused. Use the root `config.example.json` as a safe template; real `config.json` is intentionally ignored by Git.

When the sensor is not available, use `Load CSV` in the toolbar and select `fixtures/sample_readings.csv` to verify the graph and recent readings view.

## Test

```powershell
dotnet test .\dotnet\LsMonitoring.sln
```

## Build

```powershell
dotnet build .\dotnet\LsMonitoring.sln
```

## Notes

- `LsMonitoring.Core` is UI-neutral and can be reused by Windows and Linux Avalonia frontends.
- `CsvGatewaySource` already supports the current HTTPS + Basic Auth CMT Edge CSV endpoint.
- `ModbusSource` is intentionally a typed stub until the vendor register map is available.
- Threshold/evaluation and invalid-streak logic is already ported to Core, but alarms remain disabled in the UI for now.
- `Export` writes the current in-memory node readings to an Excel-compatible CSV file.
