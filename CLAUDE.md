# Working context — LsMonitoring fix-plan effort

Read this together with `AGENTS.md` (project rules) and `FIX_PLAN.md` (the
authoritative, checkbox-tracked task list). This file is the running memory for
the multi-phase cleanup effort so a fresh session can resume without re-deriving
everything.

## Branch

- Develop on `claude/program-edits-phases-QWHzZ`. Commit + push there. Never
  push elsewhere without explicit permission. Do not open a PR unless asked.

## The task

Work through `FIX_PLAN.md` phase by phase (M1 → M2 → M3 → …). For each phase:

1. Read the phase in `FIX_PLAN.md`.
2. **Verify the plan against the CURRENT code before implementing.** The plan was
   written against an older state; several items are stale (see "Key drift"
   below). Adapt the solution to the code as it actually is, and note the
   adaptation in the phase's `> Сделано:` note.
3. Implement, then **build and run tests** (see "Build env").
4. Mark the phase `## [x] …` and add a short `> Сделано:` note describing what
   was actually done (and any deviation from the plan). Notes are in Russian to
   match the plan; the user communicates in Russian.
5. Commit (clear message) and push. One commit per phase.

## Progress (as of last session)

- **M1-1 … M1-4** — done (pre-existing).
- **M2-1, M2-2** — done by the `codex/find-plan-for-handling-program-edits`
  branch (reviewed, verified correct, merged into the working branch).
- **M2-3** — done: `OnReadingsReady(..., bool live = true)`; CSV preview passes
  `live: false` so it never fires real alerts.
- **M3-1** — done: `TelegramAlertService` is `IAsyncDisposable`; `BotHost`
  disposes the superseded poller; `MainWindow` shares one `_alertHttpClient`
  across Email/SMS/Gotify services; `MessagesDialog` shares one `_testHttpClient`.
- **M3-2** — done: `MessagesDialog` borrows `LocalhostRunTunnelService` /
  `LocalGotifyService` from `MainWindow` (single owner) instead of `CreateDefault()`.
- **M3-3** — done: `BotHost.Configure` → `ConfigureAsync`; fully stops the old
  poller before starting a new one (await `DisposeAsync`), serialized by a
  `SemaphoreSlim`; `Program.cs` awaits it. Kills the 409 getUpdates race.
- **M3-4** — done: TOFU cert pinning in `CsvGatewaySource`
  (`ValidateServerCertificate` + `ObservedThumbprint`), new
  `connection.cert_thumbprint` config, `MainWindow.CreateGatewaySource()` +
  `PersistLearnedThumbprintIfNeeded`.
- **M3-5** — done: `CsvGatewaySource.DiscoverNodesAsync` `int.Parse` → `int.TryParse`
  with `continue` (guards against overflow on long digit runs); `MainWindow.AddNode`
  rejects Node ID ≤ 0 with a status-bar message; `SettingsDialog` gained a hidden
  `ValidationText` plate and refuses to save an empty (trimmed) gateway IP.
- **M4-1** — done: relay `HasValidBearerToken` now uses
  `CryptographicOperations.FixedTimeEquals` (constant-time Bearer compare) instead
  of `string.Equals(..., Ordinal)`; prefix checked separately with `StartsWith`.
- **NEXT → M4-2** — rate-limit the relay: ASP.NET `AddRateLimiter` fixed-window
  keyed per installation/IP on `/api/alerts/email`, return 429 on overflow.
- After M4-2: M4-3 (pin+hash Gotify), then the M5 (QoL) section.

## Key drift between the plan and current code

- **Telegram was moved out of the desktop into a companion process** (M1-2). The
  single getUpdates poller now lives in `LsMonitoring.TelegramBot/BotHost.cs`;
  the desktop talks to it over a localhost HTTP API via
  `TelegramCompanionService`. So plan items that reference desktop-side
  `TelegramAlertService` / `ReconfigureTelegramAlerts` / `StopTelegramAlerts`
  (e.g. M3-1, M3-3) had to be re-aimed at `BotHost`. Expect more of this in
  later phases — always grep the current code first.

## Build env (IMPORTANT — ephemeral container)

The web/cloud container has **no .NET SDK preinstalled** and is wiped between
sessions. nuget.org and the Ubuntu apt repos are reachable; most other hosts are
allowlist-blocked (`dot.net` install script is blocked).

To build/test:

```bash
sudo apt-get update && sudo apt-get install -y dotnet-sdk-10.0   # 9.0 not in repo
```

`global.json` pins SDK `9.0.313` with `rollForward: latestFeature`, which
**rejects** the installed SDK 10. For a build only, temporarily relax it and
revert (do NOT commit the change):

```bash
cp global.json /tmp/global.json.bak
sed -i 's/"latestFeature"/"latestMajor"/' global.json
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
dotnet build LsMonitoring.sln -c Debug
# tests: projects target net9.0 but only the 10.0 runtime is installed →
DOTNET_ROLL_FORWARD=Major dotnet test LsMonitoring.Core.Tests/LsMonitoring.Core.Tests.csproj
cp /tmp/global.json.bak global.json    # restore the pin; keep it out of commits
```

Last verified state: full solution builds with **0 warnings / 0 errors**; tests
**31 passed, 5 skipped, 0 failed** (the skipped ones are environment-gated).

Tip: a SessionStart hook could install the SDK automatically (skill
`session-start-hook`) — offered to the user, not yet set up.

## Conventions

- Match surrounding C# style. Keep changes focused to the active phase.
- Never commit `global.json` SDK-pin tweaks, `config.json`, `logs/`, `data/`,
  build output, or AI-tool state.
