# Phase A — Architecture Audit & Defect Reproduction Report

## 1. Environment Findings
- OS: Windows 10 Home Build 19045 (`x64`)
- Actual Known Folders discovered on host:
  - `Desktop`: `C:\Users\Duc\OneDrive\Desktop` (OneDrive-redirected Desktop)
  - `MyDocuments`: `C:\Users\Duc\Documents` (Standard Documents)
  - OneDrive Documents: `C:\Users\Duc\OneDrive\Documents` (contains existing C/C++ scratch files)
- Existing Rust prototype: `crates/autoname-core`, `crates/autoname-app`, `browser-extension/` (preserved untouched during migration).

## 2. Reproduced Root Causes in Existing Prototype

| # | Defect | Exact Location | Impact |
|---|--------|---------------|--------|
| 1 | Hardcoded single directory | `crates/autoname-core/src/config.rs:37-41` (`PathBuf::from(r"C:\Users\Duc\OneDrive\Documents")`) | Fails for other Windows usernames; misses redirected Desktop (`C:\Users\Duc\OneDrive\Desktop`), standard Documents (`C:\Users\Duc\Documents`), and custom IDE project folders. |
| 2 | Non-recursive single-folder watcher | `crates/autoname-core/src/ide_watcher.rs:65-82` (`RecursiveMode::NonRecursive`) | Nested folders are ignored; `Rename` events from editor atomic-save workflows (`temp -> rename`) are dropped; no periodic reconciliation if events are missed. |
| 3 | Hot backup gated on browser context | `crates/autoname-core/src/lib.rs:169-175` (`if is_ptit || curr_snap.context.is_some()`) | Saving `Untitled1.cpp` in Dev-C++ or VS Code without an active OJ browser tab skips hot backup completely. |
| 4 | Destructive in-place overwrite (`similarity >= 0.50`) + volatile snapshots | `crates/autoname-core/src/drive_sync.rs:108-115`, `snapshot_engine.rs` | Modifying a solution overwrites the previous backup if similarity >= 50%; comment headers are injected directly into the archived bytes; snapshot history is lost on restart; non-atomic `fs::write`. |
| 5 | Cloned-state concurrency race condition | `crates/autoname-core/src/lib.rs:108-115` and `140-186` | `EngineHandle` clones `ArchiveState` outside the mutex during Drive sync / file processing and overwrites `g.archive_state = st` afterward, silently losing concurrent writes. |
| 6 | GDI/DIB imitation UI + external `gdrive.cmd` dependency | `crates/autoname-app/src/main.rs`, `crates/autoname-core/src/drive_sync.rs:316-356` | Custom pixel-drawn UI instead of genuine WinUI 3 XAML (`WindowsAppSDK`); Drive integration shells out to an agent-managed `gdrive.cmd` wrapper instead of native OAuth 2.0 PKCE + DPAPI + Drive API v3. |
| 7 | Background tab override, missing TTL, unauthenticated HTTP | `browser-extension/background.js:124-131`, `browser_bridge.rs` | `chrome.tabs.query({})` lets unfocused background tabs overwrite the active problem; no context expiration timestamp; unauthenticated HTTP endpoint. |

## 3. Phase A Status
- Audit completed and root causes verified against source.
- Installing .NET 10 SDK (`dotnet-install.ps1 -Channel 10.0`) to begin Phase B (`GhostCode.Core` + `GhostCode.Tests`).
