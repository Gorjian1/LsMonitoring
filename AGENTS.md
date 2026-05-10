# LS Monitoring Agent Instructions

## Project Context

LS Monitoring is a desktop monitoring tool for LoadSensing / Worldsensing LS-G6 tilt sensor data.
The active implementation is the C#/.NET 9 + Avalonia app under `dotnet/`. The Python app is a legacy reference and should stay in place until the Avalonia app is fully verified against the live gateway.

## Working Rules

- Prefer the existing C# solution and patterns under `dotnet/` for new behavior.
- Keep gateway credentials, private sensor logs, and exported customer data out of Git.
- Do not commit local runtime output: `config.json`, `data/`, `logs/`, `build/`, `dist/`, `.dotnet-cli-home/`, test results, or cache directories.
- Treat `.claude/`, `.codex/`, and other AI-tool state directories as local tool state, not project source.
- Make focused changes and keep the Python reference unless the task explicitly asks to remove it.

## Commands

Use these from the repository root:

```powershell
dotnet restore .\dotnet\LsMonitoring.sln
dotnet build .\dotnet\LsMonitoring.sln
dotnet test .\dotnet\LsMonitoring.sln
dotnet run --project .\dotnet\LsMonitoring.Avalonia\LsMonitoring.Avalonia.csproj
```

For the legacy Python app:

```powershell
pip install -r requirements.txt
python .\main.py
```
