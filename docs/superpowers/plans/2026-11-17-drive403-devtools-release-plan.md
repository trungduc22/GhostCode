# Google Drive 403 Fix, Development Tools UI & First GitHub Release Plan

> **For agentic workers:** Follow TDD (`test-driven-development`), execute milestone-by-milestone (`executing-plans`), verify builds (`-warnaserror`) and tests, review diff (`requesting-code-review`), and commit each milestone cleanly.

**Goal:** Fix Google Drive `SERVICE_DISABLED` 403 classification & queue recovery, compact the Development Tools page cleanly, build & verify `GhostCode-Setup-x64.exe` + `GhostCode-Portable-win-x64.zip`, add `.github/workflows/release.yml`, update `README.md`, and ship `v0.1.0-beta.1`.

**Spec:** `docs/superpowers/specs/2026-11-17-drive403-devtools-release-design.md`

---

### Milestone 1: Google Drive 403 (`SERVICE_DISABLED`) Error Parser, Queue Recovery & Sync States

**Files:**
- Create: `src/GhostCode.Core/Cloud/GoogleApiErrorParser.cs`
- Modify: `src/GhostCode.Core/Notifications/UserFriendlyErrorService.cs`
- Modify: `src/GhostCode.Core/Cloud/GoogleDriveService.cs`
- Modify: `src/GhostCode.Core/Models/DomainModels.cs`
- Modify: `src/GhostCode.App/ViewModels/MainViewModel.cs`
- Modify: `src/GhostCode.App/Views/CloudPage.xaml` & `CloudPage.xaml.cs`
- Test: `tests/GhostCode.Tests/GoogleDriveErrorAndQueueTests.cs` (new)

- [ ] Write failing TDD tests in `tests/GhostCode.Tests/GoogleDriveErrorAndQueueTests.cs`:
  - Parse exact Google Drive 403 `SERVICE_DISABLED` / `accessNotConfigured` JSON payload -> `CloudApiDisabled`, extract project `999000111222`, extract & validate `activationUrl`.
  - Reject untrusted non-Google `activationUrl` (`https://evil.example/...`) -> falls back safely to official Google Cloud Drive API Console URL.
  - Parse `UNAUTHENTICATED`/`invalid_grant` (401), `storageQuotaExceeded` (403), `insufficientPermissions` (403), `rateLimitExceeded` (429), and verify project consistency check (`ClientId` project match vs mismatch).
  - Simulated `SERVICE_DISABLED` HTTP response in `GoogleDriveService.SyncPendingAsync()` preserves all pending queue items intact (never deletes, never marks `Synced`), stops hammering remaining items, enters `PausedUntilUtc` API-disabled cooldown, and recovers immediately when `Retry` succeeds later without restart!
- [ ] Run targeted test (`dotnet test --filter GoogleDriveErrorAndQueueTests`) -> confirm RED failure.
- [ ] Implement `GoogleApiErrorParser`, `UserFriendlyErrorService.CloudApiDisabled`, `GoogleDriveService` structured exception handling + queue preservation + cooldown + verification, and `MainViewModel` + `CloudPage.xaml` 9-state semantic indicator & `Bật Google Drive API` / `Thử lại` actions.
- [ ] Verify all unit + integration tests GREEN (`-warnaserror`) and commit Milestone 1.

---

### Milestone 2: Compact & Responsive Development Tools UI (`P1`)

**Files:**
- Modify: `src/GhostCode.App/Views/DevelopmentToolsPage.xaml`
- Modify: `src/GhostCode.App/Views/DevelopmentToolsPage.xaml.cs`
- Modify: `src/GhostCode.App/ViewModels/MainViewModel.cs`
- Modify: `src/GhostCode.App/Diagnostics/UiSelfTestRunner.cs`
- Modify: `tests/GhostCode.Tests/Phase7WinUiFunctionalTests.cs`

- [ ] Add responsive DevTools density/geometry checks to `UiSelfTestRunner.cs` (`DevToolsCompactAndResponsiveLayout`: checks compact IDE rows <= ~64px, `DefaultIdesGrid` present, no horizontal scroll across `1100x720`, `1280x720`, `1366x768`, `1920x1080`).
- [ ] Restructure `DevelopmentToolsPage.xaml` (+ responsive breakpoint in `DevelopmentToolsPage.xaml.cs` for `DefaultIdesGrid`), keeping `NavigationCacheMode="Enabled"`, tooltips for long executable paths, compact compiler list + collapsible advanced overrides `<Expander>`, and compact browser list.
- [ ] Build Release (`-warnaserror`), run full 94+ test suite including WinUI 3 UI test, visually inspect updated `08_devtools.png`, and commit Milestone 2.

---

### Milestone 3: Packaging (`GhostCode-Setup-x64.exe` & `GhostCode-Portable-win-x64.zip`) + Safe Upgrade & Uninstall Tests

**Files:**
- Modify: `src/GhostCode.App/GhostCode.App.csproj` (SemVer `0.1.0-beta.1`)
- Create: `packaging/GhostCode.iss` (Inno Setup 6 per-user installer script)
- Create: `packaging/Verify-ReleasePackaging.ps1` (packaging, secret-leak, portable-launch, fresh-install, upgrade, and non-destructive uninstall verification)
- Modify: `packaging/Build-PortableBundle.ps1` (clean publish + `.xbf`/`.pri` check + zero-secret verification + ZIP + `SHA256SUMS.txt` + optional `-BuildInstaller` support)

- [ ] Run `Build-PortableBundle.ps1 -Version 0.1.0-beta.1 -BuildInstaller` producing both `GhostCode-Setup-x64.exe`, `GhostCode-Portable-win-x64.zip`, and `SHA256SUMS.txt`.
- [ ] Run real Windows installer/upgrade/uninstall & portable verification via `Verify-ReleasePackaging.ps1` (preserving user archives & HKCU cleanup cleanly) and record real file sizes & SHA-256 checksums.
- [ ] Commit Milestone 3 cleanly.

---

### Milestone 4: GitHub Actions Workflow (`.github/workflows/release.yml`), Public `README.md`, & Ship GitHub Release

**Files:**
- Create: `.github/workflows/release.yml`
- Modify: `README.md`

- [ ] Author `.github/workflows/release.yml` (`windows-latest`, `workflow_dispatch` & `v*`, `contents: write`, build/test/publish/validate/Inno Setup/ZIP/SHA-256/GitHub Release).
- [ ] Rewrite `README.md` cleanly covering Windows support, browsers, Installer & Portable quickstart, local backup guarantee, Google Drive API activation & OAuth Testing limitations, OneDrive, and real screenshots.
- [ ] Merge `feat/release-v010` cleanly into `main`, run final `dotnet test`, push `main` + tag `v0.1.0-beta.1` (if GitHub Actions/Release triggers automatically or check workflow status via `gh` / GitHub API / `browser` subagent only if needed, accurately reporting whether Release published cleanly or blocked!).
