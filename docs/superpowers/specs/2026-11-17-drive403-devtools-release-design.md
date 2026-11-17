# GhostCode — Google Drive 403 Fix, Development Tools Compact UI & First Public GitHub Release (Design)

Date: 2026-11-17  
Base commit on `main`: `ac66f85`  
Worktree / Branch: `.worktrees/release-v010` (`feat/release-v010`)  
Target Release Version: `v0.1.0-beta.1`

## 1. Root Causes Identified in Phase 0

1. **Google Drive HTTP 403 `SERVICE_DISABLED` misclassified as `"Không có kết nối mạng"`**:
   - In `GoogleDriveService.cs`, `EnsureRemoteFolderIdAsync`, `UploadMultipartAsync`, and `ListFolderFilesAsync` threw generic `InvalidOperationException($"... ({statusCode}): {rawJson}")`.
   - `EnsureRemoteFolderIdAsync` failures returned `DriveSyncReport(0, items.Count, 0, true, "...")` with no structured error metadata.
   - `MainViewModel.SyncNowAsync()` and `PullCloudArchiveAsync()` hardcoded `ErrorCategory.NetworkOffline` for *every* non-auth-loss failure (`report.FailedCount > 0 || !string.IsNullOrEmpty(pull.Error)`).
   - No `CloudApiDisabled` error category existed in `UserFriendlyErrorService.cs`, no Google API error JSON parser extracted `error.status`, `error.details[].reason` (`SERVICE_DISABLED`), `error.errors[].reason` (`accessNotConfigured`), or `metadata.activationUrl`, and `GoogleDriveService.BackgroundLoopAsync` kept retrying every 20 s without cooldown.
2. **Oversized Development Tools layout**:
   - `DevelopmentToolsPage.xaml` used oversized full-width rows with inline executable paths and three separate full-width `SettingsCard` controls for C / C++ / Python default IDE choices.
3. **Missing public Windows installer, release CI, and up-to-date docs**:
   - Only manual PowerShell install scripts existed (`packaging/Install-GhostCode.ps1`); no Inno Setup `.iss` script generating `GhostCode-Setup-x64.exe`, no verified `GhostCode-Portable-win-x64.zip` + `SHA256SUMS.txt` generator, no `.github/workflows/release.yml`, and `README.md` still referenced old MV3 v1.2 / 30 tests / `CodePTIT_Ready`.

## 2. Design Architecture

### P0 — Structured Google Cloud Error Parsing & Real Sync State
- Add `ErrorCategory.CloudApiDisabled`, `ErrorCategory.CloudInsufficientPermissions`, and `ErrorCategory.CloudRateLimited` in `UserFriendlyErrorService.cs`.
- Create `src/GhostCode.Core/Cloud/GoogleApiErrorParser.cs` producing `CloudServiceErrorDetails` + typed `CloudServiceException`:
  - Parses HTTP status, `error.status`, `error.message`, `error.errors[].reason`, `error.details[].reason`, `error.details[].metadata.service`, `error.details[].metadata.activationUrl`, `error.details[].metadata.consumer`.
  - Validates `activationUrl` strictly against `https://console.developers.google.com/...` and `https://console.cloud.google.com/...` (safe fallback: `https://console.cloud.google.com/apis/library/drive.googleapis.com`).
  - Compares API project number against configured OAuth `ClientId` prefix (`{projectNumber}-...apps.googleusercontent.com`) and notes consistency/mismatch cleanly without leaking secrets.
- `ErrorCategory.CloudApiDisabled` Vietnamese copy:
  - Title: `"Google Drive API chưa được kích hoạt"`
  - Explanation: `"GhostCode chưa thể đồng bộ mã nguồn vì Google Drive API chưa được bật trong dự án Google Cloud đang sử dụng. Các phiên bản vẫn được lưu cục bộ."`
  - Action: `"Bật Google Drive API"` (`open_google_drive_api`).
- `GoogleDriveService` pauses automatic retry on `CloudApiDisabled` (10 min cooldown, queue items kept `Pending`/preserved untouched — never deleted, never falsely marked `Synced`), exposes `ClearApiConfigurationPause()` on manual retry, and populates `CloudServiceErrorDetails` on `DriveSyncReport` / `DrivePullReport`.
- Introduce `CloudSyncState` (`AccountConnected`, `Syncing`, `PendingUpload`, `DriveApiDisabled`, `NetworkOffline`, `AuthExpired`, `QuotaExceeded`, `SyncFailed`, `Synced`) displayed with semantic colors on `CloudPage` plus dedicated recovery strip (`Bật Google Drive API` / `Thử lại`).

### P1 — Compact Development Tools UI
- Compact IDE & compiler list rows (`Padding="10,7"`, `MinHeight="44"`), executable paths moved to `ToolTipService.ToolTip`, single responsive `IDE mặc định` grid (3 columns wide, stacked narrow), and `<Expander>` for manual compiler overrides + limits.

### P2–P5 — Real Windows Installer, Portable Bundle, Release Workflow & README
- Package `v0.1.0-beta.1` via `packaging/GhostCode.iss` (`PrivilegesRequired=lowest`, `{localappdata}\Programs\GhostCode`, Start Menu + optional Desktop shortcut, HKCU Native Messaging registration, safe upgrade & non-destructive uninstall preserving `{localappdata}\GhostCode` & `Documents\GhostCode`).
- `packaging/Build-PortableBundle.ps1` produces `GhostCode-Portable-win-x64.zip` + `GhostCode-Setup-x64.exe` + `SHA256SUMS.txt` and enforces zero bundled user secrets.
- `.github/workflows/release.yml` runs on `v*` / `workflow_dispatch` with `contents: write`.
- `README.md` rewritten cleanly for end users, including Google Cloud Desktop OAuth + Google Drive API enabling & Testing consent limitations.
