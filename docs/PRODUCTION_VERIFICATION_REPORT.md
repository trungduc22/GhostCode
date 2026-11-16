# GhostCode — Production Hardening & Verification Report

Baseline commit: `b9a6ff42ae13`  
Verification Date: 2026-11-17

---

## 1. Root Causes Discovered

1. **BUG-A (Incorrect Historical Restoration & Unchecked Integrity)**:
   - `LosslessArchiver.RestoreVersionAsync` (`src/GhostCode.Core/Archiver/LosslessArchiver.cs`) fell back to `file_snapshots` (`GetSnapshotAsync`) when a requested historical archive file was missing, silently substituting newer content (`vN`) when restoring an older version (`v1`). Neither restore nor preview validated file length or SHA-256 checksums.
2. **BUG-B (Non-Immutable Historical Files)**:
   - Historical files under `CodeArchive\History\` were moved with `overwrite: true` (`WriteBytesAtomicallyAsync`), risking clobbering on stem/hash collision or DB reset.
3. **BUG-C (Crash Consistency & Leftover Temp/Upload State)**:
   - SQLite ran with `PRAGMA synchronous=NORMAL` instead of `FULL`; interrupted uploads left in `Uploading` status were skipped on restart; leftover `.tmp.*` files and pre-commit orphaned historical files were not reconciled on startup.
4. **MON-1 / MON-2 / MON-3 (Monitoring Overlap, Self-Exclusion, Diagnostics)**:
   - Overlapping child folders beneath enabled recursive parents spawned duplicate watchers; `ArchiveDir`/`ReadyDir` could be added via UI; `RunDiagnosticTestAsync` left probe artifacts in `ReadyDir` and SQLite; the dashboard lacked last detected file, last backup, error, and diagnostic summary indicators.
   - Additionally uncovered during live WinUI 3 self-test: `GhostCodeConfig.EnsureDefaults` overwrote explicit empty `WatchedFolders: []`, and `MainWindow` UI dispatch always used asynchronous `TryEnqueue` even on UI thread access.
5. **SEC-1 / SEC-2 / SEC-3 (Browser Bridge Security)**:
   - `GET /handshake` returned the long-lived `BridgeAuthToken` based solely on a spoofable `Origin: chrome-extension://...` header; responses sent `Access-Control-Allow-Origin: *`; arbitrary `problem_code` values without valid supported URLs were accepted; stale OJ context persisted until TTL expiry when switching away from an OJ tab; `ClientSecret` and `BridgeAuthToken` appeared unredacted in config/export/UI.
6. **CLOUD-1 / CLOUD-2 (Google Drive OAuth UX & Verification)**:
   - `WaitForOAuthCallbackAsync` sent `"GhostCode connected to Google Drive!"` HTTP 200 to the browser before `ExchangeCodeForTokensAsync` ran; remote revocation failures in `DisconnectAsync` were silently ignored; Drive API uploads did not verify returned `sha256Checksum`.
7. **CI-1 (GitHub Actions Activation & Dependency Scanning)**:
   - `docs/github-actions-ci.yml` is inactive because the local Git Credential Manager OAuth token only holds `read:user, repo, user:email` scopes without `workflow`.

---

## 2. Files Modified & Added

- `src/GhostCode.Core/Archiver/LosslessArchiver.cs`: Removed `GetSnapshotAsync` fallback; added `ReadAndVerifyVersionBytesAsync` (exact size + SHA-256 verification), `WriteImmutableHistoricalFileAsync` (`overwrite: false` exclusive historical naming), `WriteReplaceableCopyAtomicallyAsync` (flush + transient lock retry), and `ReconcileAfterCrashAsync`.
- `src/GhostCode.Core/Storage/SqliteArchiveStore.cs`: Enabled `PRAGMA synchronous=FULL`, `PRAGMA busy_timeout=5000`, `idx_versions_archive_path`, crashed `Uploading` reset on `InitializeAsync`, `GetVersionByArchivePathAsync`, `InsertRecoveredVersionAsync`, `DeleteVersionAsync`.
- `src/GhostCode.Core/Monitoring/MultiDirectoryMonitor.cs`: Added `IsExcludedOrOutputDirectory`, folder normalization/deduplication, parent recursive-watch coverage detection, probe cleanup in `RunDiagnosticTestAsync`, and `LastDetectedFile` / `LastSuccessfulBackup` / `LastMonitoringError` / `LastDiagnosticSummary`.
- `src/GhostCode.Core/Analysis/SourceAnalyzer.cs`: Strict domain suffix matching (`IsDomainMatch`).
- `src/GhostCode.Core/Browser/AuthenticatedBrowserBridge.cs` & `src/GhostCode.Core/Browser/NativeMessagingHost.cs`: Rejected `GET /handshake`; added HMAC-SHA256 `POST /handshake`, ephemeral rotatable session tokens, strict extension-only CORS headers, `/context/clear`, `ApplyContextPayload` URL/control-char validation, pairing file generation, and Chromium Native Messaging host implementation.
- `browser-extension/manifest.json` (`v1.3.0`) & `browser-extension/background.js`: Added deterministic RSA SPKI `"key"`, WebCrypto HMAC handshake, `nativeMessaging` probe, `/context/clear` on non-OJ tab switch, and `isDomainMatch`.
- `src/GhostCode.Core/Models/DomainModels.cs`, `src/GhostCode.Core/Cloud/DpapiCredentialStore.cs`, `src/GhostCode.Core/Cloud/GoogleDriveService.cs`: Redacted secrets in `ExportToJson()`, excluded `ClientSecret` from `config.json` in favor of DPAPI `client_secret.dpapi`, deferred OAuth browser success HTML until after verified PKCE token exchange + DPAPI save, added `OAuthConnectionStage` + `DisconnectResult`, and verified Drive `sha256Checksum`.
- `src/GhostCode.App/`: Updated `MainViewModel.cs`, `DashboardPage.xaml`, `CloudPage.xaml(.cs)`, `TrayIconManager.cs` (`TaskbarCreated` auto-recovery), `MainWindow.xaml.cs`, `App.xaml.cs`, and added `Diagnostics/UiSelfTestRunner.cs`.
- Tests & CI docs: Added `tests/GhostCode.Tests/Phase2And3IntegrityAndMonitoringTests.cs`, `tests/GhostCode.Tests/Phase4And5SecurityAndDriveRegressionTests.cs`, `tests/GhostCode.Tests/Phase7WinUiFunctionalTests.cs`, updated `PhaseDCloudAndBrowserTests.cs`, `AcceptanceTests.cs`, `docs/PHASE1_REAUDIT.md`, `docs/github-actions-ci.yml`, and `.gitignore`.

---

## 3. Build, Dependency Audit & Test Results

- **Release Build**:
  - Command: `dotnet build GhostCode.sln -c Release -p:Platform=x64 --no-restore -warnaserror`
  - Result: **Succeeded — 0 Warning(s), 0 Error(s)** (`GhostCode.Core.dll`, `GhostCode.Tests.dll`, `GhostCode.App.exe`).
- **Dependency Vulnerability Scan**:
  - Command: `dotnet list GhostCode.sln package --vulnerable --include-transitive`
  - Result: **0 vulnerable packages** across `GhostCode.Core`, `GhostCode.Tests`, and `GhostCode.App`.
- **Full Automated Test Suite**:
  - Command: `dotnet test GhostCode.sln -c Release -p:Platform=x64 --no-build --verbosity normal`
  - Result: **Total: 51, Passed: 51, Failed: 0, Skipped: 0**.

---

## 4. Verification Classification Matrix

### Verified by Automated Test
- 51/51 xUnit tests covering `PhaseBArchiverTests` (6), `PhaseCMonitoringTests` (3), `PhaseDCloudAndBrowserTests` (3), `AcceptanceTests` (`Scenario01`–`Scenario18`), `Phase2And3IntegrityAndMonitoringTests` (10), `Phase4And5SecurityAndDriveRegressionTests` (10), and `Phase7WinUiFunctionalTests` (1).
- Loopback Drive OAuth failure/success HTML sequencing, DPAPI encryption (`credentials.dpapi` and `client_secret.dpapi`), remote `sha256Checksum` mismatch failure, remote revoke failure reporting, HMAC pairing handshake, nonce replay rejection, token rotation, `/context/clear`, Native Messaging stdio framing, and SQLite crash recovery.

### Verified by Actual Windows Runtime Behavior
- **Real WinUI 3 Binary Launch & UI Walk (`Phase7WinUiFunctionalTests` & `Scenario18`)**: Launched compiled Release `GhostCode.App.exe`, verified `<MicaBackdrop />`, navigated all 4 `NavigationView` pages, exercised folder add/recursive/remove/exclusion guards, Light/Dark theme toggles, Vi/En localization, Diagnostic test button, live watcher capture, Archive search, source preview, Copy Code, byte-exact restore, tampered-file integrity error handling, DPAPI client secret save, and `AppWindow` hide/restore.
- **Real Windows Known Folder + Real MinGW64 Compiler (`Phase2And3IntegrityAndMonitoringTests`)**: Confirmed `KnownFoldersResolver` resolves host `C:\Users\Duc\OneDrive\Desktop`, monitored live file creation inside a temporary Desktop subfolder, compiled source with host `C:\Program Files (x86)\Dev-Cpp\MinGW64\bin\g++.exe`, handled template reset and subsequent rewrite with byte-exact restore across `v1`/`v2`/`v3`, and verified `C:\Users\Duc\OneDrive\Desktop\Untitled1.cpp` (`366` bytes) stayed untouched.

### Blocked by External Dependency
1. **Verified against Real Google Drive (`BLOCKED`)**: No user Google Cloud Desktop OAuth Client ID or interactive Google sign-in consent is present on the host (`%LOCALAPPDATA%\GhostCode\config.json` does not exist yet). Per policy, no credentials were fabricated or bypassed.
2. **GitHub Actions Remote Workflow Activation (`BLOCKED`)**: Git credential token scopes on host are `read:user, repo, user:email` (missing `workflow` scope), so pushing `.github/workflows/ci.yml` is rejected by GitHub. Validated workflow definition is provided in `docs/github-actions-ci.yml`. To activate CI: copy `docs/github-actions-ci.yml` to `.github/workflows/ci.yml` directly in GitHub Web UI or push with a token having `workflow` scope.

### Not Tested
- **Physical System Tray Icon Insertion on `WinSta0\Default` Explorer Taskbar**: Automated command runners execute inside an isolated Windows Desktop object (`Desktop=exebox-*` where `FindWindowW("Shell_TrayWnd", null) == 0`), so `Shell_NotifyIconW(NIM_ADD)` was verified structurally (976-byte `NOTIFYICONDATAW`, valid `HICON`, `TaskbarCreated` handler) and hide/restore was verified live, while actual Explorer taskbar tray icon insertion requires launching `GhostCode.App.exe` directly in the user's unlocked `WinSta0\Default` desktop session.
- **Interactive Dev-C++ MDI GUI Keystrokes**: Tested with real Windows Known Folder Desktop filesystem events and Dev-C++'s `C:\Program Files (x86)\Dev-Cpp\MinGW64\bin\g++.exe` compiler; GUI window keystroke injection (`Ctrl+S`/`F11` inside `devcpp.exe`) was not automated.

### Failed
- **0 failing tests** (after fixing `Scenario06` transient file-move lock contention and all Phase 1–7 bugs).

---

## 5. Known Limitations & Explicit Recommendation

- **Known Limitations**:
  - Google Drive cloud upload remains unverified against live Google API servers until the user supplies their own Google Cloud Desktop OAuth Client ID and completes interactive sign-in.
  - GitHub Actions requires manual activation via `docs/github-actions-ci.yml` unless `workflow` token scope is granted.
- **GO / NO-GO Recommendation**:
  - **GO for Daily Local Source Archival & WinUI 3 Desktop Use**: Local file monitoring across Known Folders (Desktop/Documents/OneDrive) and custom folders, browser-optional hot backups, strictly immutable SHA-256-verified historical versioning, crash recovery, and authenticated browser pairing meet production integrity requirements.
  - **Conditional GO for Cloud Sync**: Enable after configuring a real Desktop OAuth Client ID in **Google Drive -> Configure OAuth Client...** and verifying one live sign-in + file sync in the UI.
