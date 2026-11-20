# NativeShellKit M12 migration state

Baseline `4bc43e284701a25171438f342261bcd6cc484d3b`. The existing release workflow 44217711931 passed at that commit. This checkpoint records the pre-migration audit on `migration/nativeshellkit-ghostcode`. SDK candidate `0.1.0-alpha.2` is VERIFIED in the clean SDK consumer. HiberDesk pilot acceptance opened the GhostCode migration. See the host checkpoint below.

## Contracts to preserve

- GhostCode.App targets .NET 10 for Windows, uses Windows App SDK 2.6.5 and Microsoft DI 11.0.0. GhostCode.Core stays independent of WinUI.
- Keep the existing config path, configured archive root, SQLite catalog, and `.ghostcode/history` and `.ghostcode/trash` paths.
- Keep DPAPI entropy `GhostCode.OAuth.DPAPI.v1` and `GhostCode.OAuthClientSecret.DPAPI.v1`.
- Keep `com.ghostcode.bridge`, current browser manifests, and Inno AppId `{8F9C31E2-7C2A-4D4B-9D8F-2B9E6C41A010}`.
- Keep monitoring, snapshots, diff, restore, browser transport, queued cloud sync, toolchain discovery and local execution in their product projects.

| Gate | Status |
| --- | --- |
| Release workflow for baseline 44217711931 | VERIFIED |
| Local archive/diff baseline | VERIFIED, 10/10 fixture tests from PhaseBArchiverTests and ArchiveDiffEngineTests |
| Migration build, real GUI, packaging, notifications and performance | NOT TESTED |
| Pilot gate | VERIFIED, HiberDesk acceptance and installer rollback documented at commit 495e027 |

The baseline audit recorded the application, installer and data contracts. Rollback uses the baseline artifact and its existing data. Test the archive and catalog on copies before installer or recovery acceptance. The existing release workflow is tag/manual triggered. This branch creates no release.

## M12.2 — SDK hosting

GhostCode.App now pins `NativeShellKit.Hosting` and `NativeShellKit.Diagnostics` at `0.1.0-alpha.2`, from SDK commit `2a73d5f`. The SDK owns application DI and lifecycle. Legacy config, archive/database, DPAPI and browser activation remain GhostCode-owned. SDK metadata is under `trungduc22/ghostcode` inside the selected application data directory. Tray, theme and notification delivery are retained at this checkpoint.

| Gate | Status |
| --- | --- |
| Baseline and SDK Release x64 builds | VERIFIED, zero build warnings/errors |
| Archive and diff baseline | VERIFIED, 10/10 |
| Actual isolated WinUI application | VERIFIED, 36/36 checks and 14 captures. Host, archive save/restore, SQLite, dashboard, dialogs and tray were exercised. |
| First test fixture attempt | FAILED, preconfigured watcher and paused cloud sync contradicted the first-run and protected-state test assumptions. Fixed the fixture and reran the same build. |
| Native notification submission, installer upgrade, cross-app tests | NOT TESTED |

Use an empty local feed populated by `Pack-NuGet.ps1 -OutputDir <checkout>/artifacts/m12/sdk-feed` at the pinned SDK commit before restoring. The migration-only Windows workflow runs the isolated GUI check. For rollback, build `4bc43e2` and reuse the existing config, DPAPI and archive files. Keep the separate SDK metadata under the data root.
