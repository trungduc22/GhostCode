# GhostCode — Dashboard Redesign, True Performance Optimization & Fast Problem Detection (Design)

Date: 2026-11-17  
Base commit on `main`: `c0d4be0`  
Branch / Worktree: `feat/dashboard-perf-detection` (`.worktrees/dashboard-perf-detection`)

## 1. Intent & Why

User feedback on real daily coding usage (Thorium + CodePTIT, ~37 saved versions, ~37 pending Drive uploads) shows that core archival and browser detection work, but three structural defects limit comfort:

1. **Dashboard clutter & layout imbalance**: Over-sized 4-card statistics grid, permanent full-width Quick Actions/IDE/Watch-Folder/History/Diagnostics sections, and right-edge alignment issues force excessive scrolling and obscure the core questions (*What problem am I solving? Recent code? Am I protected? Latest backup?*).
2. **Avoidable background CPU/RAM usage**: Continuous 120 ms polling in `MultiDirectoryMonitor.DebounceWorkerAsync`, periodic `EnumWindows` + `Process.GetProcessById` Win32 scans even when an extension is healthy, repeated 20 s credential polling in idle/LocalOnly cloud loops, oversized `QueryVersionsAsync(limit: 5000)` allocations during routine health checks, and artificial `SetProcessWorkingSetSize(-1, -1)` trims in `TrayIconManager`.
3. **Slow initial / tab-switch problem recognition**: `browser-extension/background.js` attempts `chrome.runtime.sendNativeMessage` (spawning a fresh `.NET` `GhostCode.App.exe --native-messaging-host` process!) **before** checking fast loopback HMAC pairing via `pairing.generated.json`; `content.js` re-scans the DOM every 5 seconds and mutates/observes broadly without SPA history hooks.

## 2. Recorded Baseline (Commit `c0d4be0`, 37 real DB versions)

- Live SQLite DB (`%LOCALAPPDATA%\GhostCode\ghostcode.db`): `file_snapshots=29`, `archive_versions=37`, `upload_queue=37`, `archive_history_events=39`, `monitor_events=64`, `problem_statements=0`, `problem_sample_tests=0`.
- Baseline Runtime (`Open T+3s` -> `Open-Idle T+15s` over 12 s on isolated config+DB copy):
  - `Open T+3s`: Working Set `152.12 MB`, Private Bytes `63.78 MB`, Threads `34`, Handles `1014`, CPU total `2828.1 ms`.
  - `Open-Idle T+15s`: Working Set `161.32 MB`, Private Bytes `70.64 MB`, Threads `33`, Handles `1023`, **Idle CPU delta `1218.8 ms` (`10.16%` of 1 core)**.

## 3. Considered Approaches & Decision

### Dashboard & Title Bar
- *Rejected*: Keeping 4 metric cards in a smaller strip on Dashboard — still duplicates stats and wastes vertical space.
- *Chosen*: Single-row adaptive stats/sync flyout button inside `MainWindow`'s integrated custom title bar (`37 bản lưu · 37 chờ` at wide width -> compact badge at medium -> icon+badge/tooltip at narrow), plus a focused 3-part `DashboardPage`:
  1. Compact Top Section: clickable slim `Browser` status strip (`Thorium · Đã kết nối`) + compact `Detected Problem` card showing Platform, Problem ID, Title, Clean Filename, and collapsible `?`/`ⓘ` secondary details (browser/URL).
  2. Responsive 2-Column Grid (`ColumnDefinitions 43* / 57*` at `>= 720 px` content width, stacking vertically below `720 px`):
     - **Left Column**: `Mã nguồn lưu gần đây` (4–6 recent logical source groups in virtualized height-bounded `ListView`, compact Copy + Open + `...` overflow menu, header `Xem tất cả` link to Archive).
     - **Right Column**: **Block A (`Trạng thái bảo vệ`)** with semantic green/amber/red state and real folder count (`Đang bảo vệ · N thư mục`), and **Block B (`Bản lưu thành công gần nhất`)** showing real latest backup filename, exact timestamp, status, and optional `Mở` action.
  3. Preserved features relocated cleanly: watched-folder list/diagnostics/events live in `SettingsPage` + `ArchivePage` + `CloudPage` + `DevelopmentToolsPage`; compact `Kiểm tra hệ thống` / `Đồng bộ ngay` / `Tải từ đám mây` / `Mở thư mục` actions remain reachable via the Title Bar Stats Flyout and Protection overflow menu.

### Performance & Detection
- *Chosen*:
  - Event-driven `MultiDirectoryMonitor` debounce queue (`SemaphoreSlim(0, 1)` + computed earliest-due wait; zero idle wakeups).
  - Skip Win32 `EnumWindows` + `Process.GetProcessById` scanning in `AuthenticatedBrowserBridge` whenever a browser extension heartbeat is fresh (`<= 30s`), and cache `pid -> procName` briefly per scan when fallback is active.
  - Cloud sync background loops skip `LocalOnly` / disabled states efficiently via signal-based wakeup rather than polling DPAPI disk storage every 20 s.
  - Lightweight aggregate SQL query (`GetLatestBackupSummaryAsync`) for Health Check / Latest Backup instead of loading 5000 full rows.
  - Conditional `RefreshArchiveAsync` dirty-tracking and cancellation tokens on searches/source reads so Dashboard navigation never rebuilds 500 UI row objects needlessly.
  - Remove `SetProcessWorkingSetSize(-1, -1)` working-set trimming gimmick from `TrayIconManager`; set `NavigationCacheMode="Enabled"` cleanly on light static navigation pages while keeping source preview & collection sizes bounded.
  - Reorder `browser-extension/background.js` `ensureToken()` to authenticate in `< 3 ms` via `pairing.generated.json` WebCrypto HMAC first (falling back to Native Messaging only if pairing JSON is absent/rejected), filter `tabs.onActivated`/`tabs.onUpdated` cleanly without duplicate `query()` stalls, and add `history.pushState`/`replaceState`/`popstate`/`visibilitychange` hooks + scoped `MutationObserver` in `content.js` & `browser-extension-firefox/` removing the 5 s blind DOM poll.

## 4. Global Constraints

- Preserve C# / .NET 10 / WinUI 3 + Chromium MV3 / Firefox extensions without rewriting archive/cloud/pairing security engines.
- Never drop `archive_versions`, `file_snapshots`, `upload_queue`, credentials, or legacy files; never run `SetProcessWorkingSetSize(-1, -1)` to fake lower RAM.
- All labels bilingual (`Vi` / `En`) with zero English leaks in Vi mode; zero horizontal overflow across `1100x720`, `1280x720`, `1366x768`, `1920x1080`.
