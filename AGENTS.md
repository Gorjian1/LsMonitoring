# LS Monitoring Agent Instructions

## Project Context

LS Monitoring is a desktop monitoring tool for LoadSensing / Worldsensing LS-G6 tilt sensor data.
The active implementation is the C#/.NET 9 + Avalonia app in the root solution.

## Working Rules

- Prefer the existing C# solution and patterns in `LsMonitoring.Core/`, `LsMonitoring.Avalonia/`, and `LsMonitoring.Core.Tests/` for new behavior.
- Keep gateway credentials, private sensor logs, and exported customer data out of Git.
- Do not commit local runtime output: `config.json`, `data/`, `logs/`, `dist/`, `.dotnet-cli-home/`, test results, or cache directories. `build/installer/` is source; other `build/` output is not.
- Treat `.claude/`, `.codex/`, and other AI-tool state directories as local tool state, not project source.
- Make focused changes in the current .NET solution.

## Commands

Use these from the repository root:

```powershell
dotnet restore .\LsMonitoring.sln
dotnet build .\LsMonitoring.sln
dotnet test .\LsMonitoring.sln
dotnet run --project .\LsMonitoring.Avalonia\LsMonitoring.Avalonia.csproj
```
