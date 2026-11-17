# GhostCode — Hydrogen Autonomous Takeover Log

**Branch:** `main`  
**Baseline Commit at Takeover:** `bdab766` (Milestone P5: `feat(devtools): move IDE and compiler configuration into dedicated Development Tools page and simplify Dashboard`)  
**Date:** 2026-11-17

---

## 1. Phase 0 — Recovered Workspace State

When taking over from the previous agent, the working tree contained tested, uncommitted Milestone P6 work alongside local verification artifacts:

| File | Status at Takeover | Action Taken |
| :--- | :--- | :--- |
| `src/GhostCode.App/Diagnostics/UiSelfTestRunner.cs` | Modified (uncommitted) | Preserved; verifies 5-page navigation, empty/populated states, multi-resolution (`1280x720`, `1366x768`, `1920x1080`) Dark/Light layouts, and captures 9 PNG screenshots (`00..08`) |
| `tests/GhostCode.Tests/Phase7WinUiFunctionalTests.cs` | Modified (uncommitted) | Preserved; asserts P0–P5 functional checks and 9 screenshot outputs |
| `tests/GhostCode.Tests/UxOverhaulAcceptanceTests.cs` | Modified (uncommitted) | Preserved; adds `P4_ScenarioG_CompilerErrorSeparatesCompilerOutputFromStdout` |
| `artifacts/screenshots/00..08.png` | Untracked build verification captures | Visually inspected; 6 UI/localization defects logged below |
| `artifacts/Release/GhostCode-win-x64/` | Untracked Release publish directory | Audited; discovered `dotnet publish` omitted `.xbf` and `GhostCode.App.pri` causing `0xC000027B` crash on launch (scheduled for fix) |

Previous commits already landed on `main`:
- `e3c20ad` — P0: OneDrive `ReparsePoint` traversal fix, startup initialization ordering, folder onboarding card.
- `b055de9` — P1: IDM-like assisted extension installer, `--native-messaging-host` headless stdio mode, Thorium/Edge/Chrome/Firefox staging.
- `df7fafd` — P2: 17-category `UserFriendlyErrorService` and Vi/En localization pass.
- `20f7f35` — P3: Compact 3-state `Drive` page (`Google Drive` / `OneDrive` / `LocalOnly`) and OAuth cancellation/timeout fix.
- `9fc822d` — P4: Single unified Run/Stop action, separated STDOUT vs STDERR, Archive empty state.
- `bdab766` — P5: Dedicated `DevelopmentToolsPage` (`Công cụ lập trình`), compact Dashboard IDE summary.

---

## 2. Phase 0 Audit — Known Issues & Blockers Found Immediately

1. **Critical Release Publish Crash (`0xC000027B`)**:
   - `artifacts/Release/GhostCode-win-x64/GhostCode.App.exe` exited immediately with `-1073741189` (`0xC000027B`) because `dotnet publish` omitted `App.xbf`, `MainWindow.xbf`, `Views/*.xbf`, and `GhostCode.App.pri`.
   - `src/GhostCode.App/bin/x64/Release/net10.0-windows10.0.19041.0/win-x64/GhostCode.App.exe` (547 files, containing all `.xbf` and `.pri`) started cleanly.
2. **Baseline RAM & CPU Overhead (measured on `bin/x64/Release/.../GhostCode.App.exe`)**:
   - Startup (`T+3s`): `WS = 90.34 MB`, `Private WS = 28.60 MB`, `Private Bytes = 30.43 MB`, `Threads = 20`, `Handles = 631`, `CPU = 2.95s`
   - Open Idle (`T+13s`): `WS = 147.77 MB`, `Private WS = 45.95 MB`, `Private Bytes = 59.36 MB`, `Threads = 29`, `Handles = 918`, `CPU = 5.88s` (`+2.93s` CPU consumed in 10s wall time due to `_discoveryRefreshTimer` running `ReadRegistryUninstallDisplayNames()`, `Process.GetProcesses()`, and filesystem probes every 5s plus 2s Win32 window polling).
   - Tray Idle (`Tray+5s`): `WS = 148.21 MB`, `Private WS = 46.19 MB`, `Private Bytes = 59.78 MB`, `Threads = 29`, `Handles = 920`, `CPU = 6.03s`.
3. **Live Reconciliation Investigation Needed**:
   - Live `%LOCALAPPDATA%\GhostCode\ghostcode.db` still showed `file_snapshots = 0` and `archive_versions = 0` after an 18s `GhostCode.App.exe` launch even with `.cpp`/`.py` files in watched directories (`C:\Users\Duc\Documents\main.py`, `C:\Users\Duc\OneDrive\Documents\11111.cpp`). Needs root-cause inspection and fix.
4. **Visual & Localization Defects Spotted in `00..08.png`**:
   - `03_settings.png`: Monitored folders `CheckBox` bound to `{x:Bind Label}` instead of `"Thư mục con"` / `"Recursive"`; `"Output Directory Excluded"` InfoBar still English in Vi mode.
   - `02_archive_runner.png`: Archive Pane 2 toolbar squishes `Mở trong IDE` `SplitButton` at `1280px` width.
   - `07_samples_timeline.png`: Programmatic `RunnerTabIndex` changes don't refresh tab underline; Sample action buttons clip `"Xóa"` at `1280px`; `"Coding time estimation disabled (Opt-in in Settings)"` and `"Added Custom Test #1."` / `"Copied"` appear in English under Vi mode.
   - `04_cloud.png`: `SyncProgressText` shows `"Starting Google Drive bidirectional sync..."` in English under Vi mode.
   - `08_devtools.png`: PyCharm row shows `"Not installed (Python runtime: ...)"` in English under Vi mode.

---

## 3. Takeover Roadmap (Status Tracking)

- [x] **P0–P5** Core architectural, browser installer, localization, Drive UI, Archive runner, DevTools split (commits `e3c20ad`..`bdab766`)
- [x] **P6 (Recovered)** Visual self-test harness + unit/acceptance tests (`UiSelfTestRunner`, `Phase7WinUiFunctionalTests`, `UxOverhaulAcceptanceTests`)
- [ ] **Takeover Step 1**: Diagnose live reconciliation 0-versions issue & fix Release publish `.xbf`/`.pri` packaging
- [ ] **Takeover Step 2**: Fix 6 screenshot visual/localization polish defects & Dashboard priority quick-access
- [ ] **Takeover Step 3 (Phase 5)**: Premium Compact Color Design (`Ghost Blue` default + 7 Accent Presets + Semantic Status Palette + Light/Dark Mica contrast)
- [ ] **Takeover Step 4 (Phase 6)**: RAM & CPU profiling and optimization (static IDE cache, tray timer pausing, event-driven UI updates, bounded state) with before/after measurements
- [ ] **Takeover Step 5 (Phase 7)**: Full 16-item real-world verification checklist, live tray archival verification, Release x64 portable bundle, git push, and final verified summary
