# NativeShellKit M12 migration state

Baseline `4bc43e284701a25171438f342261bcd6cc484d3b`. The existing release workflow 44217711931 passed at that commit. This checkpoint records the pre-migration audit on `migration/nativeshellkit-ghostcode`. SDK candidate `0.1.0-alpha.2` is VERIFIED in the clean SDK consumer. GhostCode adoption is NOT TESTED pending the HiberDesk pilot.

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
| Migration start | BLOCKED by the required HiberDesk verification gate |

Only this state file changed. The application, installer and data contracts retain the baseline. Rollback uses the baseline artifact and its existing data. Test the archive and catalog on copies before installer or recovery acceptance. The existing release workflow is tag/manual triggered. This branch creates no release.
