# GhostCode Architecture

GhostCode is a native Windows 10/11 (`win-x64`) coding companion built on **.NET 10**, **C#**, **WinUI 3 (`Microsoft.WindowsAppSDK`)**, and **SQLite WAL**.

## Solution Layout

```text
GhostCode.sln
├── src/
│   ├── GhostCode.Core/              # Platform-independent archival, monitoring, execution & cloud core
│   │   ├── Ai/                      # Optional opt-in Copilot assistance helper
│   │   ├── Analysis/                # URL/title parsers, encoding detection & GitHub-style LCS SourceDiffEngine
│   │   ├── Archiver/                # LosslessArchiver + ArchiveMigrationService (atomic immutable history)
│   │   ├── Browser/                 # AuthenticatedBrowserBridge (.Transport), BrowserIntegrationService & Chrome/FF NM
│   │   ├── Cloud/                   # GoogleDriveService (.Api), OneDriveService, CloudTransferHelpers, ErrorParser, DpapiCredentialStore
│   │   ├── Execution/               # Process runner & SampleTestRunnerService (job objects/memory/time limits)
│   │   ├── Models/                  # Domain records, GhostCodeConfig, StorageStatusFormatter, Crash/Telemetry models
│   │   ├── Monitoring/              # MultiDirectoryMonitor, KnownFoldersResolver, BackupHealthAndRecoveryService
│   │   ├── Notifications/           # NotificationCoordinator & UserFriendlyErrorService
│   │   ├── Storage/                 # SqliteArchiveStore (.VersionsAndQueue, .ProblemsAndTelemetry, .Trash)
│   │   └── Toolchains/              # ToolchainDiscoveryService & IdeLauncherService
│   └── GhostCode.App/               # Native WinUI 3 Desktop App
│       ├── ViewModels/              # MainViewModel + partial domain slices (.ArchiveAndTrash, .CloudAndAuth, .DashboardAndStats, .ExecutionAndSamples, .SettingsAndDevices) + ArchiveViewModels
│       ├── Views/                   # DashboardPage, ArchivePage, CloudPage, DevelopmentToolsPage, SettingsPage
│       ├── Converters/              # WinUI value converters (including ArchiveConverters)
│       ├── Theming/                 # GhostThemePalette (Mica & custom accent palettes)
│       ├── Localization/            # Bilingual Vi/En UI string bundles
│       └── Diagnostics/             # UiSelfTestRunner & WindowScreenshotCapture
├── tests/
│   └── GhostCode.Tests/             # Automated unit, integration, security, UI & compatibility tests
├── browser-extension/               # Manifest V3 extension (Chrome, Edge, Thorium)
├── browser-extension-firefox/       # WebExtension (Firefox)
└── packaging/                       # Portable bundle, Inno Setup installer & clean-machine verification scripts
```

## Core Data Flows

1. **Lossless Archiving & Immutable Snapshots**:
   - `MultiDirectoryMonitor` watches user-configured directories (`FileSystemWatcher` + periodic safety reconciliation), never altering original source files.
   - Normalized AST/whitespace-aware comparison detects meaningful edits; `LosslessArchiver` writes SHA-256-verified snapshots into `%USERPROFILE%\Documents\GhostCode\...` and inserts metadata into SQLite WAL (`archive_versions` + `file_snapshots` + `upload_queue`).
2. **Two-Level Safe Deletion & Cloud Tombstones**:
   - Deleting a version or problem group moves metadata and snapshot files into GhostCode's 30-day recoverable Trash (`is_deleted = 1` + `TrashArchivePath` inside `.ghostcode_trash/`) and supports `Shift + Delete` quick confirmation bypass via `InputKeyboardSource` state.
   - Local deletion keeps remote Google Drive / OneDrive files untouched unless explicitly requested by the user. `archive_tombstones` (`VersionId`, `RemoteFileDeleted`, `ContentSuppression`) prevent deleted items from resurfacing during cloud pulls or watcher scans while allowing new future edits to archive normally.
3. **Browser Problem Detection**:
   - Browser extensions communicate over Chromium/Firefox Native Messaging (`com.ghostcode.bridge`) or authenticated loopback HTTP (`127.0.0.1` + HMAC-SHA256 pairing proof -> rotatable short-lived `X-GhostCode-Token`).
   - Falls back cleanly to throttled Win32 window-title scanning when extensions are absent, and captures problem statements, sample test cases, and submission verdicts into SQLite.
4. **Cloud Synchronization**:
   - Supports **Google Drive** (REST v3 + PKCE) and **OneDrive** (Microsoft Graph + PKCE), using shared helpers in `CloudTransferHelpers` and Windows DPAPI (`DpapiCredentialStore`) for token/secret encryption.
   - Structured HTTP error parsing classifies disabled Cloud APIs (`SERVICE_DISABLED` 403), auth expiration, and offline states without tight retry loops or queue data loss.

## Building & Testing

```powershell
# Full Release x64 build (warnings treated as errors)
dotnet build GhostCode.sln -c Release -p:Platform=x64 -warnaserror

# Headless core test suite (CI-safe)
dotnet test GhostCode.sln -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName!~Phase7WinUiFunctionalTests"

# Full test suite including interactive WinUI 3 UI self-test (Windows desktop session)
dotnet test GhostCode.sln -c Release -p:Platform=x64 --no-build

# Release packaging (Portable ZIP + Inno Setup x64 EXE + SHA256SUMS.txt)
pwsh -NoProfile -ExecutionPolicy Bypass -File packaging/Build-PortableBundle.ps1 -Version "0.1.0-beta.1"
```
