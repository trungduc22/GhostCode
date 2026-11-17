# Dashboard Redesign, True Performance Optimization & Fast Problem Detection Implementation Plan

> **For agentic workers:** Follow TDD (`test-driven-development`), execute task-by-task (`executing-plans`), verify builds (`-warnaserror`) and tests after each milestone, and commit independently verifiable milestones cleanly.

**Goal:** Redesign GhostCode's Dashboard into a sleek two-column + title-bar-flyout layout, eliminate real idle CPU/RAM bottlenecks (event-driven file monitor debounce, conditional Win32 scan, lightweight DB health queries, no fake working-set trim), and cut browser problem-detection latency below 500 ms p95.

**Architecture:** Compact single-row Title Bar houses adaptive archive/sync status + flyout alongside Search, Browser chip, and Quick Actions; Dashboard renders only Browser chip + Problem Card + Responsive 2-column grid (Recent Archives Left | Protection + Latest Backup Right). Core services replace periodic polling with event signals and cached static probes; Chromium/Firefox extensions prioritize zero-spawn HMAC `/handshake` over Native Messaging spawning.

**Tech Stack:** .NET 10, WinUI 3 (Windows App SDK), SQLite (`Microsoft.Data.Sqlite`), Chromium MV3 + Firefox WebExtension JS, xUnit, Node.js DOM/event test harness.

**Spec:** `docs/superpowers/specs/2026-11-17-dashboard-redesign-perf-design.md`

## Global Constraints

- Preserve all 37 existing archived versions (`file_snapshots=29`, `archive_versions=37`, `upload_queue=37`), DPAPI tokens, and browser pairing secrets.
- Single-row custom title bar only (`Height="48"`), preserving `GlobalSearchBox`, drag regions, passthrough rects, and native caption buttons (`TitleBarRightInsetCol`).
- No `SetProcessWorkingSetSize(-1, -1)` or periodic `GC.Collect()` gimmicks.
- No fabricated backup timestamps or fake browser/cloud test results.

## Review Focus

- **Window narrow (`<= 980 px`) & wide (`1920 px`) title bar**: `TitleBarStatsButton` adapts cleanly without squashing `GlobalSearchBox` or caption buttons; `Passthrough` rects updated cleanly on size changes.
- **Responsive Dashboard breakpoint (`< 720 px` content width)**: Two-column grid stacks cleanly vertically without clipping buttons or overflowing `ScrollViewer` viewport.
- **Paused / Missing watched folder**: Protection block A MUST display amber/red state (never green), while real latest backup block B displays real DB timestamp or honest empty text.
- **Active file monitoring while minimized in tray**: Event-driven `MultiDirectoryMonitor` still archives new file writes within ~500 ms with 0% idle poll CPU.
- **Extension fast path**: `ensureToken` prefers cached `pairing.generated.json` HMAC in `< 5 ms`, falls back cleanly to `sendNativeMessage` when pairing file is missing/corrupt, and never sends background tab events.

---

### Milestone 1: True CPU/RAM Optimization in `GhostCode.Core` (Event-Driven Monitor, Conditional Win32 Fallback & Lean SQL Queries)

**Files:**
- Modify: `src/GhostCode.Core/Monitoring/MultiDirectoryMonitor.cs`
- Modify: `src/GhostCode.Core/Browser/AuthenticatedBrowserBridge.cs`
- Modify: `src/GhostCode.Core/Browser/BrowserIntegrationService.cs`
- Modify: `src/GhostCode.Core/Storage/SqliteArchiveStore.cs`
- Modify: `src/GhostCode.Core/Monitoring/BackupHealthAndRecoveryService.cs`
- Modify: `src/GhostCode.Core/Cloud/GoogleDriveService.cs`
- Modify: `src/GhostCode.Core/Cloud/OneDriveService.cs`
- Test: `tests/GhostCode.Tests/PerformanceAndDetectionTests.cs` (new)

- [ ] Write failing unit tests in `tests/GhostCode.Tests/PerformanceAndDetectionTests.cs`:
  - `SqliteArchiveStore.GetLatestBackupSummaryAsync()` returns actual latest backup record + total version/pending counts without loading 5000 rows, and returns `null` when DB is empty.
  - `MultiDirectoryMonitor` wakes up via event signal to archive a newly written `.cpp` file promptly while staying idle between events.
  - `AuthenticatedBrowserBridge.ShouldRunWin32FallbackScan()` returns `false` when an extension heartbeat is fresh (`<= 30s`) and `true` when expired/absent.
- [ ] Run targeted test command (`dotnet test`) and confirm compilation/failure on new members.
- [ ] Implement:
  - `MultiDirectoryMonitor.cs`: Add `SemaphoreSlim _pendingSignal = new(0, 1)`; `EnqueueDebounced` releases signal safely if count==0; `DebounceWorkerAsync` waits on `_pendingSignal.WaitAsync(timeout, ct)` (`Timeout.Infinite` when `_pending.IsEmpty`, earliest due delta otherwise).
  - `SqliteArchiveStore.cs`: Add `GetLatestBackupSummaryAsync(CancellationToken ct = default)` (`SELECT ... FROM archive_versions ORDER BY created_at_unix_ms DESC LIMIT 1`).
  - `BackupHealthAndRecoveryService.cs`: Use `GetLatestBackupSummaryAsync()` + `GetStatsAsync()` instead of `QueryVersionsAsync(limit: 5000)` on routine `EvaluateHealthAsync`.
  - `AuthenticatedBrowserBridge.cs`: Expose `internal bool ShouldRunWin32FallbackScan()`, skip `ForceWin32ScanNow()` in `Win32ScannerLoopAsync` when extension session is fresh (sleeping up to 10s between liveness checks), and pre-check window title substring / cache PID-to-browser-name inside `ScanVisibleBrowserWindows()` to avoid calling `Process.GetProcessById` on every OS window.
  - `BrowserIntegrationService.cs`: Cache static browser executable path + version discovery for 60 s (`forceRescan` bypasses cache) while still checking live running process names cheaply once per call.
  - `GoogleDriveService.cs` & `OneDriveService.cs`: Signal-assisted loop (`SemaphoreSlim` / longer 60 s idle backoff when `LocalOnly` or disabled, waking on `NotifyWorkAvailable()`).
- [ ] Run `dotnet build -warnaserror` + `dotnet test`, commit Milestone 1 cleanly.

---

### Milestone 2: Sub-500ms Problem Detection (`browser-extension`, `browser-extension-firefox`, `AuthenticatedBrowserBridge`, `MainViewModel`)

**Files:**
- Modify: `browser-extension/background.js`
- Modify: `browser-extension/content.js`
- Modify: `browser-extension-firefox/background.js`
- Modify: `browser-extension-firefox/content.js`
- Modify: `src/GhostCode.Core/Browser/AuthenticatedBrowserBridge.cs`
- Modify: `src/GhostCode.App/ViewModels/MainViewModel.cs`
- Modify: `tests/GhostCode.Tests/JsFixtures/verify_content_extraction.mjs`
- Modify: `tests/GhostCode.Tests/PerformanceAndDetectionTests.cs`

- [ ] Add failing latency & event-handling tests:
  - In `PerformanceAndDetectionTests.cs`: Benchmark 25 rapid `ApplyContextPayload` transitions across distinct problems on `AuthenticatedBrowserBridge`, asserting **p95 <= 50 ms** in-core (leaving massive headroom for < 500 ms UI/browser end-to-end) and verifying zero missed problem transitions even when initial title is empty then populates 50 ms later.
  - In `verify_content_extraction.mjs`: Add SPA `pushState` URL transition + duplicate suppression + no-polling verification assertions.
- [ ] Run tests to confirm failure on new assertions.
- [ ] Implement:
  - `browser-extension/background.js` (+ Firefox `background.js`):
    - Cache `cachedPairing` from `pairing.generated.json`; in `ensureToken()`, try fast WebCrypto HMAC `POST /handshake` FIRST (1–3 ms) and ONLY call `tryNativeHandshake()` as fallback if pairing JSON fails/missing.
    - Deduplicate `processTab()` synchronously by tab key so `tabs.onActivated` / `tabs.onUpdated` fires ONE immediate `POST /context` without waiting for `scripting.executeScript`.
    - Relax background keepalive/fallback intervals to `10000 ms` (`sendHeartbeat` skipped if recent `/context` already refreshed heartbeat within 6s).
  - `browser-extension/content.js` (+ Firefox `content.js`):
    - Remove the blind 5-second `setInterval` DOM scan.
    - Hook `history.pushState`, `history.replaceState`, `popstate`, `hashchange`, and `document.visibilitychange` (`!document.hidden`) for immediate event-driven context emission.
    - Only call `extractProblemPayload()` (heavy `querySelectorAll`) when URL/code changes or a scoped DOM change affects problem/statement containers (debounced `200 ms`), and avoid redundant `document.title` writes.
- [ ] Run `node verify_content_extraction.mjs` + `dotnet test`, commit Milestone 2 cleanly.

---

### Milestone 3: Title Bar Adaptive Stats Flyout, Complete Dashboard Restructure, & True App Memory Cleanup

**Files:**
- Modify: `src/GhostCode.App/Localization/UiStrings.cs` (add bilingual strings for new compact Dashboard cards, semantic protection states, and title-bar stats flyout)
- Modify: `src/GhostCode.App/Interop/TrayIconManager.cs` (remove fake `SetProcessWorkingSetSize(-1, -1)` trim on minimize; measure honest restore/idle memory)
- Modify: `src/GhostCode.App/ViewModels/MainViewModel.cs`:
  - Expose compact title-bar stats properties (`TitleBarStatsWideText`, `TitleBarStatsCompactText`, `TitleBarStatsTooltipText`, `TitleBarStatsHasError`, `StatsFlyoutSummaryText`), `RecentDashboardArchives` (up to 6 rows, copy/open), semantic `ProtectionStateLevel` (`0=Ok/Green`, `1=Attention/Amber`, `2=Error/Red`), `ProtectionStatusTitle`, `ProtectionStatusDetail`, real DB-backed `LatestBackupTitle` / `LatestBackupTimeText` / `LatestBackupStatusText` / `HasLatestBackup`.
  - Separate fast UI context updates cleanly from heavy discovery refreshes (`_discoveryRefreshTimer` every 30 s when visible, skipped while hidden in tray); dirty-track `_archiveListDirty` so `RefreshStatsAndEventsAsync` only queries aggregates + latest backup without reloading 500 `ArchiveVersionRow` objects when not needed; dispose/cancel search & source preview CTS properly.
- Modify: `src/GhostCode.App/MainWindow.xaml` & `.xaml.cs`:
  - Replace redundant adjacent Drive chip with single adaptive `TitleBarStatsButton` (`TitleBarStatsWide` / `Compact` / `Icon` toggled cleanly on `CustomTitleBarGrid.SizeChanged`), carrying interactive `Flyout` showing all 4 stats + provider status + quick Sync/Check/Open Drive actions, plus accurate `Passthrough` non-client rect updates.
- Modify: `src/GhostCode.App/Views/DashboardPage.xaml` & `.xaml.cs`:
  - Clean responsive 2-column layout (`43* / 57*` >= 720 px content width; stacked vertically < 720 px), NO 4 stat cards, NO full-width Quick Actions / DevTools / ActiveWatches / HistoryEvents / Diagnostics cards on Dashboard; collapsible details popover on Detected Problem card; Copy + Open + Overflow (`...`) on Recent Archives.
- Modify: `src/GhostCode.App/Views/SettingsPage.xaml` (ensure compact expander holds monitoring diagnostic/recent events summary so zero functionality is lost).
- Update & expand: `src/GhostCode.App/Diagnostics/UiSelfTestRunner.cs` + `tests/GhostCode.Tests/Phase7WinUiFunctionalTests.cs`

- [ ] Update / write failing WinUI ViewModel tests (verifying TitleBar stats text formatting, semantic Protection state when folder missing/disabled vs active, real DB-backed `LatestBackupTitle` never fabricated, and conditional `RefreshArchiveAsync` allocation avoidance).
- [ ] Run `dotnet test` to confirm RED state on new ViewModel tests.
- [ ] Implement Title Bar flyout, Dashboard restructure, ViewModel cleanups, Settings diagnostic expander, and remove fake `TryTrimWorkingSet()`.
- [ ] Run `dotnet build GhostCode.sln -c Release -p:Platform=x64 -warnaserror` + `dotnet test`, capture screenshots across `1100x720`, `1280x720`, `1366x768`, `1920x1080` (collapsed & expanded Nav, Light & Dark, Vi & En) and inspect images with `read_file`.
- [ ] Commit Milestone 3 cleanly.

---

### Milestone 4: Real-World Benchmarks, Regression Suite, Live Migration + Tray Backup Test, Code Review & Merge to `main`

- [ ] Publish clean Release bundle to `artifacts/Release/GhostCode-win-x64` (never `--no-build`), verify `.xbf` and `GhostCode.App.pri`.
- [ ] Run full **AFTER** benchmarks (same methodology on isolated copy of user's 37-version DB + config):
  - Startup / Open T+3s, Open-Idle T+15s (`WS`, `Private Working Set`, `Private Bytes`, `Managed Heap`, `CPU%`, `Threads`, `Handles`)
  - Tray Hidden T+15s, Window Restore latency, 20x repeated navigation memory stability
  - Real file-write local archival while minimized in tray
  - 25-trial problem detection latency (median & p95)
- [ ] Run real migration check safely on live `%LOCALAPPDATA%\GhostCode` (or verify unified `GhostCode/` folder state cleanly) preserving all 37 existing versions & queue items.
- [ ] Perform code review of `git diff main..HEAD`, fast-forward merge into `main`, remove `.worktrees/dashboard-perf-detection`, run final `dotnet test` on `main`, and push if ready / deliver complete verification report.
