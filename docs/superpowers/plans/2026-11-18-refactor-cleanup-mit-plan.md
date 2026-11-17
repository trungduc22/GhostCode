# GhostCode Source Refactoring, Cleanup & MIT Licensing Plan

**Goal:** Reduce monolithic complexity and duplication cleanly while preserving 100% compatibility and shipping open-source MIT + accurate third-party notices.

**Spec:** `docs/superpowers/specs/2026-11-18-refactor-cleanup-mit-design.md`

- [ ] **Milestone 1**: Remove obsolete `crates/`, `Cargo.toml`, `Cargo.lock`, `.cargo/`, check `.gitignore`; verify `dotnet build` & `dotnet test` pass.
- [ ] **Milestone 2**: Extract shared OAuth loopback callback + token exchange helpers into `CloudTransferHelpers.cs`; remove duplicate `Base64UrlEncode`/`ParseQueryString` in `GoogleDriveService.cs` & shared PKCE/HTTP plumbing in `OneDriveService.cs`.
- [ ] **Milestone 3**: Decompose `SqliteArchiveStore.cs` (`VersionsAndQueue`, `ProblemsAndTelemetry`) & `AuthenticatedBrowserBridge.cs` (`Transport`).
- [ ] **Milestone 4**: Extract `ArchiveViewModels.cs` and split `MainViewModel.cs` into `CloudAndAuth`, `ExecutionAndSamples`, `SettingsAndDevices` partial domain files (< 800 LOC per file).
- [ ] **Milestone 5**: Add `LICENSE`, `THIRD_PARTY_NOTICES.md`, `docs/ARCHITECTURE.md`, update `README.md`, update `Build-PortableBundle.ps1` / `GhostCode.iss` / `release.yml`, add `LicenseAndCompatibilityTests`, run full verification + before/after metrics comparison.
