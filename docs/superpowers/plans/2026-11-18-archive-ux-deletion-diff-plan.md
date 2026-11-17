# GhostCode Archive UX, Safe Deletion & GitHub-Style Diff Implementation Plan

> **For agentic workers:** Execute milestone-by-milestone inside `.worktrees/archive-ux`, committing after each verified step.

**Goal:** Deliver safe two-level deletion + 30-day Trash + cloud tombstones, clean single-row Archive toolbar/metadata/short labels/local search, and full-width GitHub-style Unified & Side-by-Side Diff with full test & visual verification.

**Architecture:** Additive SQLite schema migrations (`archive_versions` soft-delete columns + `archive_tombstones`), watcher SHA-256 suppression in `LosslessArchiver`, `DeleteRemoteFileAsync` + manifest tombstone reconciliation across Drive/OneDrive, `SourceDiffEngine` hunk/unified-line enhancements, and responsive WinUI 3 `ArchivePage` + `TrashDialog` + `VersionDetailsDialog`.

**Tech Stack:** C# / .NET 10, WinUI 3 (`Microsoft.WindowsAppSDK`), `Microsoft.Data.Sqlite`, `CommunityToolkit.Mvvm`, `xunit`.

**Spec:** `docs/superpowers/specs/2026-11-18-archive-ux-deletion-diff-design.md`

## Global Constraints
- Never modify original monitored working files (`Untitled1.cpp`, etc.).
- Never renumber remaining versions when deleting (`v1, v2, v3` -> delete `v2` -> `v1, v3`).
- Never silently delete remote Drive/OneDrive files on local/Shift delete without explicit user choice.
- Preserve compatibility with the real 43-version backup database (`C:\Users\Duc\AppData\Local\GhostCode_Backups\pre_phase1_*`).
- Never use global keyboard hooks; detect Shift only on native button click (`InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)`).

---

### Milestone 1: Safe Deletion, Trash, Cloud Tombstones & Watcher Suppression (`Core` + TDD Tests)
- **Files:** `DomainModels.cs`, `SqliteArchiveStore.cs`, `LosslessArchiver.cs`, `ICloudArchiveProvider.cs`, `CloudManifestCodec.cs`, `GoogleDriveService.cs`, `OneDriveService.cs`, `StorageStatusFormatter.cs`
- **Tests:** `tests/GhostCode.Tests/ArchiveDeletionAndTrashTests.cs` (written TDD first: 3-version middle delete without renumbering + SHA-256 verify + Trash restore; whole-problem delete preserving original `Untitled1.cpp` + problem restore; watcher SHA-256 suppression + future edit archiving; Trash permanent purge + 30-day expiry; offline/cloud tombstone reconciliation + anti-resurrection + conflict preservation; short status mapping; real DB copy migration smoke test).

### Milestone 2: Enhanced GitHub-Style Diff Engine (`Core` + TDD Tests)
- **Files:** `SourceDiffEngine.cs`
- **Tests:** `tests/GhostCode.Tests/ArchiveDiffEngineTests.cs` (v1→v2, v2→v3, missing gaps, identical versions, empty files, CRLF preservation, added/removed/modified hunk indices, unified lines with line numbers, large >2000-line bounded latency).

### Milestone 3: Archive UI Overhaul — Search, Toolbar, Metadata, Short Labels, Trash & GitHub-Style Diff (`App` + Functional/Visual Tests)
- **Files:** `UiStrings.cs`, `MainViewModel.cs`, `ArchivePage.xaml`, `ArchivePage.xaml.cs`, `MainWindow.xaml.cs`, `UiSelfTestRunner.cs`
- **Tests:** `tests/GhostCode.Tests/Phase7WinUiFunctionalTests.cs` + `UiSelfTestRunner` UI automation screenshots at `1100×720`, `1280×720`, `1366×768`, `1920×1080` (Light & Dark, Archive overview, Delete confirmation dialog, Trash view, Unified diff, Side-by-Side diff, Undo Shift+Delete).
