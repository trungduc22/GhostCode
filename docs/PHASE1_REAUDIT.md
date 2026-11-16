# Phase 1 — Re-Audit Report (Baseline Commit `b9a6ff42ae13`)

Audited files across `GhostCode.Core`, `GhostCode.App`, `browser-extension`, `GhostCode.Tests`, and CI configuration.

---

## Issue Checklist & Root Causes

| ID | Severity | Component / Exact Location | Root Cause & Impact |
|----|----------|-----------------------------|---------------------|
| **BUG-A1** | Critical | `src/GhostCode.Core/Archiver/LosslessArchiver.cs:158-176` (`RestoreVersionAsync`) | When `version.ArchivePath` is missing on disk, falls back to `_store.GetSnapshotAsync(version.SourcePath)` (which only holds the *latest* snapshot). Restoring `v1` can silently write `v5` bytes. |
| **BUG-A2** | Critical | `src/GhostCode.Core/Archiver/LosslessArchiver.cs:164-175` | Restoration never verifies `bytes.LongLength == version.SizeBytes` or `SHA-256 == version.Sha256`, allowing silent corruption on restore. |
| **BUG-A3** | High | `src/GhostCode.App/ViewModels/MainViewModel.cs:505-515, 725-749` | Source preview also falls back to latest `GetSnapshotAsync` without hash verification; `RestoreSelectedVersionAsync` does not surface errors via `InfoBar` before throwing from an `async void` click handler. |
| **BUG-B1** | Critical | `src/GhostCode.Core/Archiver/LosslessArchiver.cs:92, 178-200` | Historical files under `ArchiveDir\History\` are written using `WriteBytesAtomicallyAsync` (`File.Move(..., overwrite: true)`), so historical targets are not strictly immutable (`overwrite: false`). |
| **BUG-B2** | High | `src/GhostCode.Core/Archiver/LosslessArchiver.cs` & `SqliteArchiveStore.cs` | No startup crash reconciliation exists to sweep leftover `.tmp.*` files or register orphaned historical files written to disk before an aborted SQLite commit. |
| **BUG-C1** | High | `src/GhostCode.Core/Storage/SqliteArchiveStore.cs:88, 352` | Uses `PRAGMA synchronous=NORMAL` rather than `FULL`; crashed uploads left in `Uploading` status are never picked up again by `GetPendingOrRetryableUploadsAsync`. |
| **MON-1** | High | `src/GhostCode.Core/Monitoring/MultiDirectoryMonitor.cs:77-129` | Overlapping recursive/child folders can register redundant `FileSystemWatcher` instances; `ArchiveDir` / `ReadyDir` are not explicitly blocked in `MainViewModel.AddWatchedFolder` or `ReloadWatches`. |
| **MON-2** | Medium | `src/GhostCode.Core/Monitoring/MultiDirectoryMonitor.cs:362-414` | `RunDiagnosticTestAsync` deletes the temp source file on the watched folder, but leaves its diagnostic archive file, ready copy, and SQLite/upload rows behind. |
| **MON-3** | Medium | `src/GhostCode.App/ViewModels/MainViewModel.cs`, `DashboardPage.xaml` | UI lacks dedicated visible status fields for **Last detected file**, **Last successful backup**, **Last monitoring error**, and **Last diagnostic result**. |
| **SEC-1** | High | `src/GhostCode.Core/Browser/AuthenticatedBrowserBridge.cs:229-249` | `GET /handshake` trusts spoofable `Origin: chrome-extension://...` and returns the static long-lived `_config.BridgeAuthToken`; response headers send `Access-Control-Allow-Origin: *`. |
| **SEC-2** | High | `src/GhostCode.Core/Browser/AuthenticatedBrowserBridge.cs:308-324` | Accepts `problem_code` even when `payload.url` is missing or not on a supported OJ domain; no explicit `/context/clear` endpoint when switching away from an OJ tab. |
| **SEC-3** | Medium | `src/GhostCode.App/ViewModels/MainViewModel.cs:306`, `DomainModels.cs:62-64, 244` | `BridgeAuthToken` is shown verbatim in Settings UI and `ExportToJson()`, and `OAuthClient.ClientSecret` is serialized in plaintext inside `config.json`. |
| **CLOUD-1** | High | `src/GhostCode.Core/Cloud/GoogleDriveService.cs:149-182` | `WaitForOAuthCallbackAsync` returns `"GhostCode connected to Google Drive!"` HTML to the browser *before* `ExchangeCodeForTokensAsync` runs or succeeds. |
| **CLOUD-2** | High | `src/GhostCode.Core/Cloud/GoogleDriveService.cs:271-292, 494-504` | `DisconnectAsync` swallows remote revocation failures without reporting them; `UploadMultipartAsync` does not verify returned Drive `sha256Checksum`. |
| **CI-1** | High | `docs/github-actions-ci.yml` vs `.github/workflows/ci.yml` | Workflow sits inactive in `docs/github-actions-ci.yml` because cached GitHub OAuth credentials lack the `workflow` scope (`read:user, repo, user:email`), and vulnerability/static checks are not included. |
