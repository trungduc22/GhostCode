# GhostCode Source Refactoring, Cleanup & MIT Licensing — Design Spec

## Baseline (`768491a`)
- Tracked files: `156`
- `.cs`: `72` files (`25,599` LOC)
- `.rs`: `19` files (`5,971` LOC — unused legacy prototype in `crates/`)
- Top oversized C# classes: `MainViewModel.cs` (`3,962` LOC), `SqliteArchiveStore.cs` (`1,663` LOC), `GoogleDriveService.cs` (`1,187` LOC), `AuthenticatedBrowserBridge.cs` (`1,084` LOC).

## Decisions
1. **Safe Removal of Obsolete Rust Prototype (`crates/`, `Cargo.toml`, `Cargo.lock`, `.cargo/`)**:
   - Verified zero references in `GhostCode.sln`, `.csproj`, `src/`, `tests/`, `packaging/`, or `.github/`.
   - Remove via `git rm` (zero history rewrite; remains recoverable from Git history).
2. **Cohesive Decomposition Without Breaking XAML `x:Bind`, SQLite Compatibility, or Config**:
   - Split row/item view-model records out of `MainViewModel.cs` into `ViewModels/ArchiveViewModels.cs`.
   - Split `MainViewModel` (`partial`) cleanly into domain units: Core (`MainViewModel.cs`), `MainViewModel.ArchiveAndTrash.cs` (from Phase 1), `MainViewModel.CloudAndAuth.cs`, `MainViewModel.ExecutionAndSamples.cs`, `MainViewModel.SettingsAndDevices.cs`.
   - Split `SqliteArchiveStore` (`partial`) into Core/Schema (`SqliteArchiveStore.cs`), `SqliteArchiveStore.VersionsAndQueue.cs`, `SqliteArchiveStore.ProblemsAndTelemetry.cs`, and `SqliteArchiveStore.Trash.cs`.
   - Split `AuthenticatedBrowserBridge` cleanly into Lifecycle/Context (`AuthenticatedBrowserBridge.cs`) and HTTP/NativeMessaging Transport (`AuthenticatedBrowserBridge.Transport.cs`).
   - Consolidate real OAuth PKCE loopback HTTP listener + form token exchange duplication between `GoogleDriveService` and `OneDriveService` into `CloudTransferHelpers.WaitForLoopbackOAuthCodeAsync` & `ExchangeFormTokenAsync` (and remove duplicate private `Base64UrlEncode` / `ParseQueryString` in `GoogleDriveService`).
3. **MIT Licensing & Third-Party Attribution**:
   - Add root `LICENSE` (`MIT`, `Copyright (c) 2026 trungduc22`).
   - Add `THIRD_PARTY_NOTICES.md` accurately listing actual licenses (`MIT`, `Apache-2.0`, `Microsoft Software License Terms` for `Microsoft.WindowsAppSDK`) without relicensing non-MIT third-party components.
   - Include `LICENSE` & `THIRD_PARTY_NOTICES.md` in Portable ZIP, Inno Setup Installer (`{app}`), `README.md`, and `docs/ARCHITECTURE.md`.
