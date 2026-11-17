# GhostCode — Hydrogen Autonomous Takeover Log

**Repository:** `https://github.com/trungduc22/GhostCode`  
**Branch:** `main`  
**Baseline Commit at Takeover:** `bdab766` (Milestone P5)  
**Final Takeover Implementation Commit:** `b4ade2d` (plus docs update on `main`)  
**Date:** 2026-11-17

---

## 1. Phase 0 — Recovered Workspace State & Root Causes Solved

Recovered and committed uncommitted P6 changes cleanly (`6468493`) without destructive git commands:
- `src/GhostCode.App/Diagnostics/UiSelfTestRunner.cs`
- `tests/GhostCode.Tests/Phase7WinUiFunctionalTests.cs`
- `tests/GhostCode.Tests/UxOverhaulAcceptanceTests.cs`

Root causes discovered and fixed during Phase 0 live investigation:
1. **Live `0 archived versions` blocker (`autoname.exe` port squatter + monolithic startup `try/catch`)**:
   - The legacy Rust binary `C:\Users\Duc\Documents\ssh\target\release\autoname.exe` (`PID 14072`) had been left running on `127.0.0.1:48123`.
   - `AuthenticatedBrowserBridge.Start()` threw `SocketException`, which aborted `App.xaml.cs` before `MultiDirectoryMonitor.Start()`, `GoogleDriveService.StartBackgroundSync()`, `MainViewModel.InitializeAsync()`, and `ReconcileOnceAsync()` ever ran.
   - **Fix (`b03977f`)**: Added ephemeral fallback + `ListenerStartError` reporting to `AuthenticatedBrowserBridge.Start()`, isolated every service startup call in `App.xaml.cs` so port conflicts can never block local source monitoring, and stopped the orphaned `autoname.exe` process. Live `%LOCALAPPDATA%\GhostCode\ghostcode.db` immediately reconciled and archived `25` existing user source files (`Untitled1.cpp`, `main.py`, `11111.cpp`, `Untitled5.cpp`–`Untitled9.cpp`, `main.c`, etc.).
2. **Release `dotnet publish` crash (`0xC000027B`)**:
   - `artifacts/Release/GhostCode-win-x64/GhostCode.App.exe` exited immediately with `-1073741189` (`0xC000027B`) because `dotnet publish` omitted `*.xbf` and `GhostCode.App.pri`.
   - **Fix (`b03977f`)**: Added `IncludeCompiledXamlInPublish` MSBuild target (`AfterTargets="ComputeFilesToPublish"`) in `src/GhostCode.App/GhostCode.App.csproj`. Note: run clean `dotnet publish` (without `--no-build`) so `GhostCode.Core.dll` and all XAML outputs publish together cleanly.

---

## 2. Implemented Changes Across Takeover Phases

- **Visual & Localization Polish (Step 2, `b4ade2d`)**:
  - `SettingsPage.xaml` / `WatchedFolderItem`: fixed Monitored Folders `CheckBox` to display `"Thư mục con"` / `"Recursive"` cleanly and cleared redundant folder labels on `ToggleSwitch`.
  - `ArchivePage.xaml` / `ArchivePage.xaml.cs`: refactored Pane 2 action bar into two clean rows so `Mở trong IDE` never truncates at `1280px`; arranged Sample Test buttons into a symmetric `2x2` Grid so `Xóa` never clips; subscribed `ViewModel.PropertyChanged` for `RunnerTabIndex` so tab button active underlines stay synchronized when tabs switch programmatically.
  - `DashboardPage.xaml` / `.xaml.cs` & `MainViewModel.cs`: added `RecentArchivesCard` (`Mã nguồn lưu gần đây`) showing up to 4 recent archived files with one-click `Sao chép`, `Mở`, and `Chạy` when archives exist; fixed `CompactToolchainSummaryText` lookup keys (`"C"` / `"C++"` instead of `"gcc"` / `"g++"`), showing real `Công cụ: GCC ✓ · G++ ✓ · Python ✓ · Dev-C++ ✓`.
  - Localized remaining Vi strings in `CodingTimeTracker.cs`, `IdeStatusRow` (PyCharm runtime label), and `MainViewModel.cs` (Cloud sync progress/pull/download, folder exclusion InfoBar, quick-copy messages, custom sample messages).
- **Phase 5 — Premium Compact Color Design (`b4ade2d`)**:
  - Added `AccentColorPreset` enum (`GhostBlue` default, `Violet`, `Teal`, `Emerald`, `Amber`, `WindowsAccent`, `CustomAccent`) and `CustomAccentHex` to `GhostCodeConfig`.
  - Added `src/GhostCode.App/Theming/GhostThemePalette.cs` + base semantic brushes in `App.xaml` (`GhostAccentBrush`, `GhostAccentSoftBrush`, `GhostSuccess*Brush`, `GhostActive*Brush`, `GhostWarning*Brush`, `GhostError*Brush`, `GhostNeutral*Brush`) updating WinUI accent resources dynamically across Light, Dark, and System themes while preserving native Mica backdrops and title bar caption contrast.
  - Added `Bảng màu & màu nhấn` / `Color Theme & Accent` `SettingsCard` in `SettingsPage.xaml` with live color swatch preview and validated hex input.
- **Phase 6 — RAM & CPU Optimization (`b4ade2d`)**:
  - Added `ToolchainDiscoveryService.DiscoverAllCached` (5-minute cache for registry Uninstall keys, directory scans, and `--version` probes; lightweight process name check only).
  - Increased `_discoveryRefreshTimer` interval from `5s` to `15s`, paused it cleanly when minimized to System Tray (`SetWindowHiddenInTray`), and diffed browser/IDE signatures before rebuilding UI `ObservableCollection`s.
  - Added `ScheduleCoalescedRefreshAsync` (collapses burst archive events into one refresh and defers heavy UI collection rebuilds while minimized in tray), removed redundant full IDE/browser rediscovery from every `OnMonitorEventRecorded` event, fixed fire-and-forget `SelectedVersionSource` loading race in `LoadVersionsForFileGroupAsync`, and slowed background `ReconcileLoopAsync` (`30s` -> `120s`) and adaptive `Win32ScannerLoopAsync` (`2s` -> `4s`/`6s`).
  - Applied one-time `SetProcessWorkingSetSize(-1, -1)` trim on entering System Tray (`TrayIconManager.HideToTray` / `OnAppWindowClosing`).

---

## 3. Phase 6 Real-World Performance Benchmark (Before vs. After)

Measured directly on this machine using Win32 process & CIM performance counters against `GhostCode.App.exe` (x64 Release):

| Scenario | Metric | Baseline Before (`bin/x64/Release`) | Optimized After (`artifacts/Release/GhostCode-win-x64`) | Notes |
| :--- | :--- | ---: | ---: | :--- |
| **Startup (T+3s)** | Working Set (MB) | 90.34 MB | 98.69 MB | Real DB now populated with 25 archived files (vs. 0 at baseline) |
| **Startup (T+3s)** | Private Working Set (MB) | 28.60 MB | **26.54 MB** | -7.2% |
| **Startup (T+3s)** | Private Bytes (MB) | 30.43 MB | 33.57 MB | Includes populated Archive/Dashboard state |
| **Startup (T+3s)** | CPU Time (s) | 2.95 s | **1.67 s** | **-43.4%** startup CPU |
| **Open Idle (T+13s / T+18s)** | Working Set (MB) | 147.77 MB | 154.39 MB | Full 5-page XAML + Recent Archives populated |
| **Open Idle (T+13s / T+18s)** | Private Working Set (MB) | 45.95 MB | 48.42 MB | — |
| **Tray Idle (+3s / +5s)** | Working Set (MB) | 148.21 MB | **14.23 MB** | **-90.4%** immediately on minimize to tray |
| **Tray Idle (+3s / +5s)** | Private Working Set (MB) | 46.19 MB | **5.64 MB** | **-87.8%** |
| **Tray Settled (+15s in Tray)** | Working Set (MB) | 148.21 MB | **47.46 MB** | **-68.0%** vs. baseline tray |
| **Tray Settled (+15s in Tray)** | Private Working Set (MB) | 46.19 MB | **13.11 MB** | **-71.6%** vs. baseline tray |
| **Tray Settled (+12s Delta)** | CPU Time Added (s) | ~0.30 s / 10s (est from +0.15s / 5s) | **+0.27 s / 12s** | Minimal background load; real-time watcher active |
| **Live Tray Monitoring Save Burst** | Working Set / Priv WS | N/A | 64.08 MB / **18.69 MB** | Archived `v1` & `v2` `.cpp` saves while window remained hidden |

---

## 4. Phase 7 Verification Ledger (16-Item Checklist — Strictly Observed)

| # | Check | Status | Verification Evidence |
| :--- | :--- | :--- | :--- |
| 1 | Clean build (`dotnet build GhostCode.sln -c Release -p:Platform=x64 -warnaserror`) | **VERIFIED** | `0 Warning(s), 0 Error(s)` (`task-2943`) |
| 2 | Full test suite (`dotnet test GhostCode.sln -c Release -p:Platform=x64 --no-build`) | **VERIFIED** | `82/82` passed including `Phase7WinUiFunctionalTests` & `UxOverhaulAcceptanceTests` (`task-2943`) |
| 3 | Live Release binary starts cleanly (`artifacts/Release/GhostCode-win-x64/GhostCode.App.exe`) | **VERIFIED** | Launched cleanly & stayed responsive with live tray/bridge (`task-2959`, `nativehost` test) |
| 4 | Edge/Thorium/Chrome/Firefox detection + staged extensions | **VERIFIED** | Live staged `chromium` & `firefox` folders + `NativeMessaging` JSONs verified on disk; `BrowserDiscovery` unit tests & UI test detected real Edge + Thorium (`01_dashboard.png`) |
| 5 | Extension pairing handshake (`--native-messaging-host` + HTTP `/handshake`) | **VERIFIED** | Real stdio handshake against live `GhostCode.App.exe` returned `NativeHostExit: 0 ok: True port: 48123 browser: Thorium hasToken: True` |
| 6 | Live unprompted extension tab detection inside interactive Edge/Thorium window | **NOT TESTED** | Verified up to real Edge/Thorium executable discovery, extension staging, `--native-messaging-host` handshake, and loopback HTTP `/handshake` + `/context`; human loading unpacked folder inside live Edge/Thorium browser UI not automated |
| 7 | Real monitored files & OneDrive folder traversal (`Desktop` / `Documents` / `OneDrive\Documents`) | **VERIFIED** | Live `%LOCALAPPDATA%\GhostCode\ghostcode.db` reconciled `25` real user `.cpp`/`.c`/`.py` files; minimized-to-tray live write test created immutable `v1` + `v2` SHA256-verified snapshots (`task-2959`) |
| 8 | Protected-folder onboarding + Archive empty state | **VERIFIED** | Live UI self-test (`FreshUserOnboardingAndArchiveEmptyState`, `00_onboarding_empty.png`) |
| 9 | Copy Code, Open in IDE, Compare Diff, Restore, SHA256 Tamper Rejection | **VERIFIED** | Verified in `Phase7WinUiFunctionalTests` (`CopyCodeCommand`, `VersionDiffAndSamples`, `RestoreAndIntegrityCheck`, `02_archive_runner.png`) |
| 10 | Live C++ / Python Runner (STDOUT vs Compiler STDERR, STDIN, Sample Runner) | **VERIFIED** | `LiveCodeRunnerSplitIo` (`stdout='579'`), `P4_ScenarioG` compile error separation (`CompilerOutput` non-empty, `Stdout` empty), `Sample Passed (164 ms)` (`07_samples_timeline.png`) |
| 11 | Drive 3-option provider UI (`Google Drive`, `OneDrive`, `LocalOnly`) & OAuth state | **VERIFIED** | `DriveThreeOptionExclusivityAndNoStaleOAuth` (`local=True, oneDrive=True, gDrive=True`), DPAPI token preservation (`P3_CloudProviderSwitchToLocalOnly`, `04_cloud.png`) |
| 12 | Development Tools (`Công cụ lập trình`) separation & Dashboard compactness | **VERIFIED** | `CompactDashboardDevToolsSummary` (`GCC ✓ · G++ ✓ · Python ✓ · Dev-C++ ✓`), `08_devtools.png`, `05_dark_dashboard.png` |
| 13 | Vietnamese localization sweep across UI & InfoBars | **VERIFIED** | `VietnameseLocalizationNoEnglishLeaks` + visual inspection of all 9 screenshots (`00..08.png`) |
| 14 | Ghost Blue default + 7 Accent Presets + Mica & Light/Dark themes (`1280x720`, `1366x768`, `1920x1080`) | **VERIFIED** | `P5_AccentPresetPersistenceAndViCodingTimeLocalization`, `LightDarkThemeSwitch`, `MicaAppearanceSwitch`, `MultiResolutionAndThemeLayout`, and visual inspection of `01..08.png` |
| 15 | Minimize-to-Tray background operation & working-set trim | **VERIFIED** | `Tray+15s` settled at `47.46 MB` WS / `13.11 MB` Private WS and archived `v1`+`v2` live `.cpp` saves while hidden |
| 16 | Live Google Drive / OneDrive remote cloud round-trip with real account OAuth | **NOT TESTED** | Bidirectional sync, checksum validation, backoff & migration verified against mocked HTTP handlers (`Phase7BidirectionalDriveSyncTests`, `ExpansionP2CloudAndMigrationTests`); interactive live browser OAuth consent with user Google account not executed unattended |
