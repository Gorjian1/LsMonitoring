# C# + Avalonia Port — Plan & UI/UX Spec

**Status**: Python MVP done and live-verified against the real gateway. User decided to rewrite in **C# (.NET 9) + Avalonia 12**. This document is the migration plan + the new UI/UX spec, written for a fresh model session that won't have prior context.

Read this first, then [CLAUDE_CODE_BRIEF.md](CLAUDE_CODE_BRIEF.md) (the original product brief — still valid for *what* the tool does and the data quirks).

---

## 1. What's already done (Python, in this repo)

A working reference implementation lives in `src/` and `tests/`. Use it as the source of truth for *behavior* — translate, don't reinvent. Notable files:

- `src/parser.py` — CSV parser with sentinel filter and pattern-based column matching
- `src/datasource.py` — `CsvScraperSource` (HTTPS + Basic Auth, three URL fallbacks, discover via `/dataserver/` → networks → nodes → liveness check)
- `src/storage.py` — SQLite schema + idempotent upsert
- `src/alarms.py` — threshold evaluation + `InvalidStreakTracker` + append-only CSV alarm log
- `src/gui/main_window.py` — full PySide6 UI (the one being replaced; **do not** copy its visual style, copy its *behavior*)
- `tests/test_parser.py`, `test_storage_and_export.py`, `test_gui_smoke.py` — 9 passing tests
- `fixtures/sample_readings.csv` — real CSV captured from the live gateway and used as the parser/UI fixture.

## 2. Live gateway facts (verified 2026-05-05)

Single source of truth for the protocol. Keep these constants near `CmtEdgeClient`.

- **Endpoint**: `https://169.254.0.1` (link-local). HTTP/80 returns 301 to HTTPS. Self-signed cert — accept any cert in the `HttpClientHandler`.
- **Stack on the device**: lighttpd/1.4.35 + PHP/7.0.13 + Laravel. FW 1.5.2.
- **Auth**: HTTP Basic, realm `"local admin access"`. No CSRF, no login form, no session needed (sets `laravel_session` cookie which is safe to ignore).
- **Credentials**: set by the user in local config/settings. Do not commit real gateway credentials.

### Endpoints

| Path | What it returns | Use |
|---|---|---|
| `/dataserver/` | HTML root listing networks | Discovery step 1 — scrape `href="/dataserver/network/view/(\d+)"` |
| `/dataserver/network/view/<net_id>` | HTML listing nodes in network | Discovery step 2 — scrape `href="/dataserver/node/(?:view\|edit)/(\d+)"` |
| `/dataserver/current/reading/<node_id>` | **CSV** (`Content-Type: application/octet-stream`) | **Primary data endpoint.** Poll this. Inactive nodes return HTTP 404/500 with `text/html` body — filter by content-type, not status code |
| `/dataserver/node/view/<node_id>` | HTML page with extras: latest engineering values per channel, "Received on" timestamp, RSSI/SF/Frequency table for last ~20 packets, message counters per day, last raw JSON messages | Optional second fetch for the signal-quality UI element |
| `/dataserver/csv/tilt/<node_id>-readings-YYYY-MM.zip` | Zipped monthly CSV archive | Optional backfill |
| `/dataserver/api/*`, `/chart/.../json` | **404 — no JSON API exists in FW 1.5.2** | n/a |

### CSV format quirks

- 9 metadata lines (`"Key",value`), then a header row starting with `"Date-and-time"` plus per-channel columns: `Temp-<NodeID>-Ch<N>`, `Aaxis-...`, `Baxis-...`, `AaxisVariation-...`, `BaxisVariation-...`.
- **Data rows can have fewer columns than the header.** Variation columns are omitted when no reference point is set on the node. Parser must tolerate that.
- Column names depend on Node ID and channel — match by **regex pattern**, not exact name.
- **Sentinel values** around ±18.74° / ±18.59° are sensor overflow. LS-G6-INC15 range is ±15° → `Math.Abs(value) > 15.0` ⇒ INVALID.

### HTTP quirks

- `If-Modified-Since` / `Last-Modified` is **not honored** — server returns 200 with full body even when nothing changed. Don't bother with conditional GET. Body is ~25 KB, cheap.
- `Last-Modified` header IS set and accurately reflects when the gateway last received a packet from the node — usable as a freshness indicator independent of CSV parsing.
- Response time for `/current/reading/<id>` is ~700–800 ms. Chart pages are slow (>4 s timeout).

### Live nodes on this gateway (network 13456)

Only **6989** is alive (LS-G6-INC15, channel `Ch1`, ~30 s sampling). Nodes `4728`, `7060`, `9495` are dead since 2017–2018 (404/500 on `/current/reading/<id>`). `DiscoverNodes()` must filter accordingly.

## 3. Stack decision

| Concern | Choice |
|---|---|
| Language | **C# / .NET 9** |
| UI framework | **Avalonia 12** (cross-platform, modern, MVVM-first) |
| Theme | Avalonia Fluent dark/light |
| MVVM | Add **CommunityToolkit.Mvvm** only when the UI grows beyond simple code-behind |
| DI / hosting | Add `Microsoft.Extensions.Hosting` only when background services/settings justify it |
| HTTP | `HttpClient`, custom `HttpClientHandler` to ignore cert |
| HTML scraping | Regex is acceptable for current network/node links; use **AngleSharp** later only for slow `/node/view/<id>` details |
| CSV | hand-written reader (the format is too peculiar for CsvHelper) |
| Storage | Deferred for now. Keep in-memory buffers until local history is in scope again |
| Charts | Custom Avalonia drawing first; add **ScottPlot.Avalonia** only if zoom/crosshair/performance demand it |
| Export | Excel-compatible CSV first; add **ClosedXML** only if native `.xlsx` is required |
| Logging | Add file logging later; keep Core independent from a logging framework |
| Password storage | Use `ICredentialStore`. Windows: DPAPI. Linux: Secret Service/libsecret or explicit fallback |
| Sound | `System.Media.SoundPlayer` for simple WAV beeps; embed two short WAVs (warning chime, critical alarm) as resources |
| Single-file publish | `dotnet publish -r win-x64 -c Release --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true` |

Target OS: **Windows 10/11 x64** first. Linux should remain possible, so avoid Windows-only choices in Core.

Important architecture correction: common data-source contracts must not mention CSV. CSV is one implementation detail of `CsvGatewaySource`; future `ModbusSource` must return the same normalized reading batches without pretending to fetch CSV.

## 4. Solution layout

```
LsMonitoring.sln
├── src/
│   ├── LsMonitoring.Core/                # no UI deps — pure logic, fully unit-testable
│   │   ├── Gateway/CmtEdgeClient.cs
│   │   ├── Parser/ReadingsCsvParser.cs
│   │   ├── Parser/NodePageParser.cs      # parses /node/view/<id> HTML for RSSI etc.
│   │   ├── Discovery/NodeDiscovery.cs
│   │   ├── Models/{Reading,ParsedCsv,NodeInfo,RssiSample,Thresholds,AlarmConfig,Status,Evaluation}.cs
│   │   ├── Monitoring/ReadingBuffer.cs   # in-memory for now; no SQLite until history is needed
│   │   ├── Alarms/{AlarmEvaluator,InvalidStreakTracker,AlarmLog}.cs
│   │   ├── Polling/PollingService.cs     # async polling loop; emits normalized reading batches
│   │   ├── Config/AppConfig.cs           # JSON in %LOCALAPPDATA%\LsMonitoring\config.json
│   │   └── Export/ReadingsCsvExporter.cs # Excel-compatible CSV first; native xlsx later if needed
│   │
│   └── LsMonitoring.App/                 # Avalonia
│       ├── App.axaml{,.cs}               # builds Host, registers DI, sets theme
│       ├── ViewModels/{MainWindow,Node,Settings,AlarmBanner}ViewModel.cs
│       ├── Views/MainWindow.axaml
│       ├── Views/NodeCard.axaml
│       ├── Views/HeroPanel.axaml
│       ├── Views/AlarmBanner.axaml
│       ├── Views/SettingsWindow.axaml
│       ├── Controls/RadialGauge.cs       # custom-drawn (Avalonia.Controls.Shapes or SkiaSharp)
│       ├── Controls/Sparkline.cs
│       ├── Controls/TrendPlot.cs         # custom Avalonia chart first; ScottPlot later if needed
│       ├── Services/AlarmSoundService.cs
│       ├── Services/ToastService.cs
│       ├── Themes/Tokens.axaml           # color tokens (see UI spec)
│       ├── Localization/Strings.{ru,en}.resx
│       └── Assets/{warning.wav,critical.wav,icon.ico}
│
├── tests/
│   └── LsMonitoring.Tests/               # xUnit
│       ├── ReadingsCsvParserTests.cs     # uses fixtures/sample_readings.csv
│       ├── NodePageParserTests.cs        # uses a saved /node/view/<id>.html fixture
│       ├── ReadingBufferTests.cs
│       ├── AlarmEvaluatorTests.cs
│       └── DiscoveryTests.cs             # uses HttpMessageHandler mock
│
├── fixtures/
│   ├── sample_readings.csv               # parser/UI fixture captured from live gateway
│   └── node_view_6989.html               # capture once via curl, commit
│
├── CSHARP_PORT_PLAN.md                   # this file
├── CLAUDE_CODE_BRIEF.md                  # original product brief
└── README.md
```

## 5. Migration TODO (full list, in execution order)

### Phase A — Solution skeleton

1. `dotnet new sln -n LsMonitoring`. Create `LsMonitoring.Core` (classlib, net9.0), `LsMonitoring.Avalonia` (Avalonia application template), `LsMonitoring.Core.Tests` (xunit). Wire references.
2. Keep dependencies lean first: Avalonia + xUnit. Add MVVM/Hosting/ScottPlot/ClosedXML/SQLite only when the next feature requires them.
3. Keep `fixtures/sample_readings.csv` as the parser fixture.
4. Capture `https://169.254.0.1/dataserver/node/view/6989` HTML later only if signal-quality/RSSI UI becomes current scope.

### Phase B — Core (no UI)

5. **`Sources/CsvGatewaySource.cs`** — `HttpClient` with cert-ignore handler and persistent `Authorization: Basic` header. Methods for root/network/current CSV. All async, all take `CancellationToken`.
6. **`Parser/ReadingsCsvParser.cs`** — port `src/parser.py` 1:1. Public types: `Reading` (record), `ParsedCsv`. Method `Parse(ReadOnlySpan<byte>)` or `Parse(Stream)`. Sentinel `Math.Abs(v) > 15.0`.
7. **`Parser/NodePageParser.cs`** — optional later. `/node/view/<id>` is slow; never call it in the main polling loop.
8. **`Discovery/NodeDiscovery.cs`** — orchestrates: GET /, scrape network IDs, GET each network page, scrape node IDs, then for each node call `GetCurrentReadingAsync` and **filter by content-type == octet-stream + parseable CSV**. Return `IReadOnlyList<NodeInfo>`. Inactive nodes are filtered out, *not* errored.
9. **Storage** — deferred. Keep `ReadingBuffer` in memory. Add SQLite later only when local history is back in scope.
10. **`Alarms/AlarmEvaluator.cs`** — port `evaluate()`. Same statuses (`OK | WARNING | CRITICAL | INVALID`). Same modes (`absolute | variation`). Same `same_for_ab` shortcut.
11. **`Alarms/InvalidStreakTracker.cs`** — promote INVALID → CRITICAL after `invalid_alarm_minutes`. Per-node state.
12. **`Alarms/AlarmLog.cs`** — append CSV at `%LOCALAPPDATA%\LsMonitoring\data\alarms.csv`.
13. **`Config/AppConfig.cs`** — JSON compatible with the current Python config for migration. Introduce `ICredentialStore` before shipping; do not hard-wire Windows DPAPI into cross-platform code.
14. **`Polling/PollingService.cs`** — periodic async loop, fetches all configured nodes sequentially. Events: `ReadingsArrived(int nodeId, NodeReadings)`, `FetchFailed(int nodeId, Exception)`, `ConnectionStateChanged(bool ok, string?)`.
15. **`Export/ReadingsCsvExporter.cs`** — Excel-compatible CSV now. Native `.xlsx` with ClosedXML later if required.

### Phase C — Tests

16. `ReadingsCsvParserTests` — port the 5 Python tests against `sample_readings.csv`. Asserts: 90 rows, model `LS-G6-INC15`, channel `Ch1`, 18+ invalid rows, sampling interval = 30 s, latest is valid, short-row tolerance.
17. `NodePageParserTests` — fixture `node_view_6989.html`. Asserts: receivedOn parses, ≥1 RSSI sample, channel count.
18. `ReadingBufferTests` — idempotent merge, timestamp ordering, trim behavior.
19. `AlarmEvaluatorTests` — known thresholds produce expected statuses on the fixture (parity with `test_evaluate_status`).
20. `DiscoveryTests` — `HttpMessageHandler` mock returning canned root + network HTML + per-node responses. Asserts: only nodes with octet-stream CSV are kept.

### Phase D — UI shell

21. Keep startup simple until the app needs DI. Add `IHost` only when settings, logging, storage and services justify the extra structure.
22. **`Themes/Tokens.axaml`** — color tokens (see UI spec §7). One ResourceDictionary per theme variant.
23. **`Views/MainWindow.axaml`** + `MainWindowViewModel` — root grid (top bar / sidebar / content / status bar).
24. **`Views/NodeCard.axaml`** + `NodeViewModel` — per-node card. Bind to `NodeViewModel.SparklinePoints`, `LastSampleAge`, `RssiBars`, `StatusColor`.
25. **`Controls/RadialGauge.cs`** — custom drawn. Properties: `Value`, `Min`, `Max`, `WarningThreshold`, `CriticalThreshold`, `IsInvalid`. Use `OnRender` with `DrawingContext`. Animate the needle position via Avalonia transitions.
26. **`Controls/Sparkline.cs`** — minimal line, last N points, color from `StatusColor` dependency property.
27. **`Controls/TrendPlot.cs`** — custom Avalonia rendering first. Add ScottPlot later if zoom/crosshair becomes necessary.
28. **`Views/HeroPanel.axaml`** — three large tiles (T number, A gauge, B gauge). Subtle 80 ms flash on update.
29. **`Views/AlarmBanner.axaml`** — out-of-flow banner with slide animation, Acknowledge / Mute 5min buttons. Bound to `AlarmBannerViewModel`.
30. **`Views/SettingsWindow.axaml`** — vertical TabStrip (Connection / Thresholds / Alarms / Display). Real-time validation. Live preview gauge in Thresholds tab.
31. **`Services/AlarmSoundService.cs`** — plays `warning.wav` or `critical.wav` (embedded). Respects OS volume.
32. **`Services/ToastService.cs`** — non-blocking toast for warnings / fetch errors / "Acknowledged".
33. **Localization** — RU default, EN fallback. All UI strings in `.resx`. Language picker in Settings/Display.

### Phase E — Ship

34. **Manual e2e against the live gateway** as soon as the sensor is available again. Checklist:
    - Discover finds only node 6989 (4728/7060/9495 filtered out).
    - Live polling shows new readings every ~30 s.
    - Sentinel ±18.x value shows up as INVALID in card + table.
    - Forcing a low warning/critical threshold (e.g. `0.5°` warning, `1.0°` critical) triggers banner + sound.
    - Stopping the gateway (or pulling the cable) flips the dot to red and ages the cards.
    - Excel export opens cleanly in Excel with colored rows.
    - In-memory view stays stable during long polling sessions.
    - First-run flow with empty config triggers Settings/Connection prompt.
35. `dotnet publish` produces `LSMonitoring.exe` < 80 MB single-file. Verify it runs on a clean Windows VM without .NET installed.
36. **Only after all of E passes and the C# app has replaced the Python app operationally**: delete `src/` (Python), `tests/`, `main.py`, `requirements.txt`, `build_exe.py`, `.gitignore` Python lines. Update `README.md` to describe the C# version. Keep `CLAUDE_CODE_BRIEF.md` for reference.

---

## 6. UI/UX spec

The Python UI was functional but visually generic ("фу" per the user). The new UI must look modern and convey state at a glance. Principles, layout, and component spec follow.

### 6.1 Principles

1. **Calm-when-OK, loud-when-wrong.** Quiet dark UI in normal state, with subtle motion. CRITICAL turns the screen red and slides a banner in — impossible to miss.
2. **One glance = current state.** Everything important is visible without scrolling or clicking.
3. **No blocking modals.** Toasts and inline banners only. `MessageBox` is reserved for confirmations like "delete node", never for alarms.
4. **Tabular numerals + monospace** for sensor values. Digits don't jitter on update.
5. **Dark theme by default** (control-room context). Light theme is a Settings toggle.
6. **All strings via Localization** (RU/EN), Russian default.

### 6.2 Main window layout (1280×800 baseline)

```
┌──────────────────────────────────────────────────────────────────────────────────┐
│ [ALARM BANNER — slides in from top on CRITICAL, otherwise hidden]                │
├──────────────────────────────────────────────────────────────────────────────────┤
│ ●pulse  169.254.0.1  •  Connected  •  Next poll in 3s  •  4806 msgs total       │ ← 36px top bar
├─────────────────────┬────────────────────────────────────────────────────────────┤
│ NODES         + ⟳   │  ▆ Node 6989 — LS-G6-INC15 — Ch1                          │
│ ┌────────────────┐  │  ─────────────────────────────────────────────────────────│
│ │● 6989  INC15  │  │   ┌──────────┐  ┌──────────┐  ┌──────────┐                │
│ │  Last 32s ago  │  │   │   23.6   │  │  -1.03°  │  │  +18.59° │ ← HERO         │
│ │  ▁▃▂▅█▆▃▁  ●● │  │   │   °C     │  │          │  │ INVALID  │   3 large      │
│ │   |OK|   ▓▓▓▒ │  │   │ ────────╮│  │ Gauge A  │  │ Gauge B  │   tiles        │
│ └────────────────┘  │   │  T  ▶▶▶  │  │  •──•──• │  │  •──•──• │                │
│ ┌────────────────┐  │   │  cool    │  │  green   │  │   RED    │                │
│ │○ 4728  offline │  │   └──────────┘  └──────────┘  └──────────┘                │
│ │  no current   │  │   Δ since prev: -0.02°  -0.07°  +18.86°                   │
│ └────────────────┘  │  ─────────────────────────────────────────────────────────│
│                     │  ╔════════════════════════════════════════════════╗  ⏵play │
│                     │  ║  HISTORY CHART (last 6h, scrollable)            ║       │
│                     │  ║  T (grey) / A (blue) / B (purple)              ║       │
│                     │  ║  warning band light, critical band red          ║       │
│                     │  ║  invalid intervals — dashed grey vertical bands ║       │
│                     │  ╚════════════════════════════════════════════════╝       │
│                     │  ─────────────────────────────────────────────────────────│
│                     │  Last 200 readings   [filter: All▾]  [export ▼]           │
│                     │  ┌───────────────────────────────────────────────────┐    │
│                     │  │ 15:23:30  23.6°  -1.03  +18.59  • CRITICAL  …    │    │
│                     │  │ 15:23:00  23.6°  -1.04  -0.31   ○ OK              │    │
│                     │  └───────────────────────────────────────────────────┘    │
├─────────────────────┴────────────────────────────────────────────────────────────┤
│ Sampling ~30s   •   ▂▄▆ RSSI -16 dBm   •   SF7   •   FW 1.5.2   •   ⚙ Settings │
└──────────────────────────────────────────────────────────────────────────────────┘
```

### 6.3 Components

**Top bar (36 px, fixed.)** Left: pulsing green ●, gateway IP, connection status, *countdown to next poll*, total messages. Right: Discover, Settings, Export buttons. No icon toolbar with "Save" — this isn't Word.

**Node sidebar (240 px, scrollable.)** Each node = 88 px card:
- 12 px colored indicator on the left (OK green pulse / WARNING amber / CRITICAL red pulse / OFFLINE grey).
- Node ID + model in caption type.
- "Last 32s ago" — auto-updating relative time.
- **Sparkline** of last 50 points on axis A (or whichever is in focus) — 80×20 px, color reflects last status.
- **RSSI bars** (4 levels: ≥−50 dBm → 4, ≥−70 → 3, ≥−90 → 2, else 1).

Click selects. Right-click context menu: Rename, Set thresholds for this node, Remove. The ⟳ button re-discovers; the + button adds a node manually.

**Hero panel (3 tiles, ~220 px tall.)**
- **T tile**: number 56 pt monospace tabular, units underneath in small caps, mini "5-min trend" indicator (▲▼– with color).
- **A tile / B tile**: radial gauge.
  - 270° arc, scale −15..+15°.
  - Green zone (`|x| < warning`), amber zone (`warning..critical`), red zone (`>critical`) drawn from settings — always visible so you can see "how close to the threshold".
  - Black needle animates to new value (250 ms ease-out).
  - Inside: large number, `°` label, and a second row "Δ −0.07°" (delta from previous reading).
  - INVALID → gauge blurs and goes grey, large overlay "INVALID — sensor overflow".
  - CRITICAL → gauge background pulses red (1 s period).

This panel is the product's main value-prop. One look should tell the user: OK / alarm / link lost.

**History chart (flex, ~280–360 px tall.)** Start with the custom Avalonia `TrendPlot`; migrate to ScottPlot.Avalonia only when interactive zoom/crosshair becomes necessary.
- Three series: T (thin grey, right axis), A (blue), B (purple) on a shared X axis (time).
- Threshold bands: warning = translucent amber horizontal zone, critical = translucent red. Re-render instantly when thresholds change in Settings.
- Invalid intervals: dashed grey vertical bands (more restrained than the Python solid grey blocks).
- Gaps in data: NaN-break (no straight lines through holes).
- Default mode "Live": rolling window (last 6 h). When user drags or scrolls → switches to "Pan" mode, ⏮ "Back to live" button appears.
- Crosshair tooltip on hover shows all three values + status + RSSI of the nearest packet.

**Alarm banner (slide-in, hidden by default.)** On the first CRITICAL of a session:
- 56 px banner slides out from under the top bar. Background `#c1272d`, white text.
- Left: pulsing ⚠ icon, text `Node 6989 — CRITICAL: |B|=18.59° ≥ critical 10°  •  hh:mm:ss`.
- Right: `Acknowledge` and `Mute 5min` buttons. Acknowledge hides the banner but the node card stays in CRITICAL state.
- Sound plays once. New CRITICALs within 30 s update the text without re-sliding.
- WARNING does **not** slide the banner — only a subtle update on the hero tile + a short ping sound.

**Settings (separate window, 560×640.)** Vertical TabStrip on the left, content on the right:
- **Connection**: IP, login, password (masked, "Show" button), "Test connection" with green/red checkmark and response time. Store credentials through `ICredentialStore` so Windows and Linux can use different secure backends.
- **Thresholds**: absolute/variation radio; sliders + numbers for warning/critical/A/B (or shared); a small live-preview gauge showing how the zones move as you drag.
- **Alarms**: sound/popup/log checkboxes, invalid-behavior dropdown, minutes spinbox. "Test alarm" button plays sound and shows the banner for 3 s.
- **Display**: language (RU/EN), theme (dark/light/system), chart sample length, polling interval with hint "~30 s sampling — no point polling faster than 5–10 s".

**Status bar (24 px, bottom.)** Sampling rate, RSSI/SF, FW version, ⚙ Settings shortcut. Lower priority info — doesn't compete for attention.

### 6.4 State transitions

| Event | Behavior |
|---|---|
| First run | Translucent onboarding overlay: "Enter gateway IP and password" → opens Settings/Connection. Sidebar/charts disabled, greyed. |
| Successful connect | Top-bar dot turns green, pulses; status becomes "Connected". Discovery runs automatically — found nodes slide in (200 ms). |
| Bad credentials | Top-bar dot red, "Auth failed". Settings/Connection highlights password field with red border. Toast "Invalid login/password" with "Open Settings" button. |
| Connection drop | Dot turns amber, "Reconnecting…" with countdown. Cards grey out but last data stays visible. |
| Polling tick | Brief edge highlight on the dot (200 ms). Top-bar countdown resets. |
| New reading | Hero numbers do a brief "fresh" flash (background tinted 80 ms → fade). Sidebar sparkline appends + slides. Chart scrolls. |
| Status change OK→WARNING | Card transitions color (250 ms). "Ping" sound. Tile background turns amber. No banner. |
| Status change OK→CRITICAL | Alarm sound (loops until Acknowledge). Banner slides in. Hero tile pulses red. Card pulses. |
| Long INVALID streak | Promote to CRITICAL automatically (per `invalid_alarm_minutes`). |
| Node link lost | Card → grey, ⊘ icon, "No data 12m". Hero panel greys, large overlay "Connection to node lost". Chart freezes. |
| User clicks node | Hero/chart/table swap in ~60 ms (data already in memory — no spinners). |

### 6.5 Color tokens

| Token | Dark | Light | Use |
|---|---|---|---|
| `bg.canvas` | `#0d1117` | `#f6f8fa` | window background |
| `bg.surface` | `#161b22` | `#ffffff` | cards, tiles |
| `bg.elevated` | `#1f2937` | `#f0f3f6` | hero panel, top bar |
| `border.subtle` | `#30363d` | `#d0d7de` | dividers |
| `text.primary` | `#e6edf3` | `#1f2328` | values, headings |
| `text.muted` | `#8b949e` | `#656d76` | captions, meta |
| `accent.brand` | `#2f81f7` | `#0969da` | A-axis curve, primary buttons |
| `state.ok` | `#238636` | `#1a7f37` | OK |
| `state.warn` | `#d29922` | `#bf8700` | WARNING |
| `state.crit` | `#f85149` | `#cf222e` | CRITICAL |
| `state.invalid` | `#768390` | `#6e7781` | INVALID / no data |

### 6.6 Typography

- UI: **Inter** or **Segoe UI Variable** (sysfont fallback).
- Sensor values: **JetBrains Mono** or **Cascadia Code** (monospaced with tabular figures — digits don't jitter).
- Hero number: 56 pt, weight 600.
- Tile heading: 12 pt, uppercase, letter-spacing 1.5 %.
- Captions: 11 pt.

### 6.7 Animation budget

- Any state-change animation ≤ 250 ms.
- `fade-in` for new elements 120 ms; `slide` 200 ms ease-out.
- Pulse: 1 s period, subtle amplitude (background ±6 %) for normal; sharper (1 s, ±20 % + glow) for CRITICAL.
- No always-running spinners — the screen must not "compete" with the data.

### 6.8 Anti-patterns (don't)

- No "spacey" gradient hype.
- No emoji in the chrome (one ⚠ in the alarm banner is OK).
- No carousel/slider, no bento dashboard with 12 empty tiles.
- Don't surface raw engineering noise (frequency hopping, sequence counters) on the main view — collapse it into a "Details" disclosure under HERO.

---

## 7. Operational notes for the porting model

- **Don't delete the Python code until phase E is fully green.** It's the behavior reference; tests will help verify parity.
- **Test fixtures before writing parser code.** `fixtures/sample_readings.csv` is already committed as the CSV fixture; capture `node_view_6989.html` only when `/node/view/<id>` parsing enters scope. Then write any new parsers TDD-style against fixtures.
- **Gateway access is temporary.** The user said "while I still have access". Run the e2e checklist (phase E step 34) early, not at the end — even if the UI is half-done, validate `CmtEdgeClient` + `NodeDiscovery` against the real device ASAP so you don't lose the chance.
- **Credentials**: supplied by the user through local config/settings. **Don't hardcode or commit them.** First run prompts via Settings/Connection. Use `ICredentialStore`: DPAPI on Windows, Linux backend/fallback later.
- **Don't guess Modbus registers.** The Python version has a `ModbusSource` stub that throws `NotImplementedError`. Mirror that. The user is waiting on a register map from Worldsensing; once it arrives, a `ModbusGatewayClient` can be added without changing the rest of the architecture.
- **Don't try to change node sampling rate from the tool.** That's done via Worldsensing's Android app over USB. The Python brief covers this; same applies in C#.
- **Localization**: every visible string goes through `Strings.resx`. Russian default. Don't ship hardcoded English placeholders.
- **First-run UX matters.** The user won't read a README. The empty-config path must guide them.

## 8. Reference: Python files to study before porting each piece

| Porting task | Read first |
|---|---|
| `ReadingsCsvParser` | `src/parser.py` |
| `CmtEdgeClient` + `NodeDiscovery` | `src/datasource.py` |
| Future `HistoryStore` | `src/storage.py` |
| `AlarmEvaluator` + `InvalidStreakTracker` + `AlarmLog` | `src/alarms.py` |
| `AppConfig` | `src/config.py` |
| `PollingService` | `src/gui/worker.py` |
| Export parity | `src/gui/exporter.py` |
| Behavior of the old GUI (what to *keep*, not how it looked) | `src/gui/main_window.py` |

The original product brief — data quirks, sentinels, LoRa behavior, sampling-rate caveats — is in [CLAUDE_CODE_BRIEF.md](CLAUDE_CODE_BRIEF.md). Re-read it; nothing in §1–§6 of *that* document changes for the C# version.
