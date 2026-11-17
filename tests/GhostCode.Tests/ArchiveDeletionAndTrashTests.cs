using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using GhostCode.Core.Analysis;
using GhostCode.Core.Archiver;
using GhostCode.Core.Cloud;
using GhostCode.Core.Models;
using GhostCode.Core.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace GhostCode.Tests;

public sealed class ArchiveDeletionAndTrashTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ghostcode_del_{Guid.NewGuid():N}");

    public ArchiveDeletionAndTrashTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }

    private (GhostCodeConfig Config, SqliteArchiveStore Store, LosslessArchiver Archiver) CreateEnv()
    {
        var cfg = GhostCodeConfig.CreateDefault(_root, _root);
        cfg.ArchiveDir = Path.Combine(_root, "GhostCode");
        cfg.DatabasePath = Path.Combine(_root, "ghostcode.db");
        Directory.CreateDirectory(cfg.ArchiveDir);
        var store = new SqliteArchiveStore(cfg.DatabasePath);
        store.InitializeAsync().GetAwaiter().GetResult();
        var archiver = new LosslessArchiver(cfg, store);
        return (cfg, store, archiver);
    }

    [Fact]
    public async Task DeleteMiddleVersion_LeavesV1AndV3IntactWithStableIdsAndNoRenumbering_AndRestoresFromTrash()
    {
        var (cfg, store, archiver) = CreateEnv();
        var srcDir = Path.Combine(_root, "Work");
        Directory.CreateDirectory(srcDir);
        var srcPath = Path.Combine(srcDir, "Untitled1.cpp");
        var ctx = new ProblemContext(OjPlatform.Ptit, "C04037", "Liệt kê", "https://code.ptit.edu.vn/student/question/C04037", "test", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        byte[] v1Bytes = Encoding.UTF8.GetBytes("#include <iostream>\nint main(){ std::cout << 1 << '\\n'; return 0; }\n");
        byte[] v2Bytes = Encoding.UTF8.GetBytes("#include <iostream>\nint main(){ std::cout << 2 << '\\n'; return 0; }\n");
        byte[] v3Bytes = Encoding.UTF8.GetBytes("#include <iostream>\nint main(){ std::cout << 3 << '\\n'; return 0; }\n");
        File.WriteAllBytes(srcPath, v1Bytes);
        var r1 = await archiver.ProcessFileSaveAsync(srcPath, v1Bytes, ctx);
        File.WriteAllBytes(srcPath, v2Bytes);
        var r2 = await archiver.ProcessFileSaveAsync(srcPath, v2Bytes, ctx);
        File.WriteAllBytes(srcPath, v3Bytes);
        var r3 = await archiver.ProcessFileSaveAsync(srcPath, v3Bytes, ctx);

        Assert.Equal(1, r1.Version!.VersionNum);
        Assert.Equal(2, r2.Version!.VersionNum);
        Assert.Equal(3, r3.Version!.VersionNum);

        var del = await store.MoveVersionToTrashAsync(r2.Version.Id, cfg.GetInternalTrashRoot(), cfg.ArchiveDir, deleteRemote: false, retentionDays: 30);
        Assert.True(del.Success);

        var remaining = await store.GetVersionsByProblemAsync("Ptit", "C04037");
        Assert.Equal(2, remaining.Count);
        Assert.Equal(new[] { 3, 1 }, remaining.Select(v => v.VersionNum).ToArray());
        Assert.Equal(r1.Version.Id, remaining[1].Id);
        Assert.Equal(r3.Version.Id, remaining[0].Id);
        Assert.Equal(r1.Version.Sha256, remaining[1].Sha256);
        Assert.Equal(r3.Version.Sha256, remaining[0].Sha256);

        // Verify remaining version bytes remain untouched and byte-exact
        Assert.Equal(v1Bytes, await archiver.ReadAndVerifyVersionBytesAsync(r1.Version.Id));
        Assert.Equal(v3Bytes, await archiver.ReadAndVerifyVersionBytesAsync(r3.Version.Id));

        // Trash contains v2
        var trash = await store.GetTrashItemsAsync();
        var trashedV2 = Assert.Single(trash);
        Assert.Equal(r2.Version.Id, trashedV2.Version.Id);
        Assert.Equal(2, trashedV2.Version.VersionNum);

        // Restore v2 from Trash cleanly with original ID, version_num=2, metadata & bytes intact
        var restored = await store.RestoreTrashItemAsync(r2.Version.Id);
        Assert.True(restored);

        var afterRestore = await store.GetVersionsByProblemAsync("Ptit", "C04037");
        Assert.Equal(new[] { 3, 2, 1 }, afterRestore.Select(v => v.VersionNum).ToArray());
        Assert.Equal(v2Bytes, await archiver.ReadAndVerifyVersionBytesAsync(r2.Version.Id));
        Assert.Empty(await store.GetTrashItemsAsync());
    }

    [Fact]
    public async Task DeleteWholeProblem_RemovesGroupAndPreservesOriginalWorkingFile_AndRestoresProblemBatch()
    {
        var (cfg, store, archiver) = CreateEnv();
        var srcDir = Path.Combine(_root, "Work");
        Directory.CreateDirectory(srcDir);
        var srcPath = Path.Combine(srcDir, "Untitled1.cpp");
        var ctx = new ProblemContext(OjPlatform.Ptit, "C04037", "Liệt kê", "https://code.ptit.edu.vn/student/question/C04037", "test", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        byte[] v1Bytes = Encoding.UTF8.GetBytes("#include <stdio.h>\nint main(){ printf(\"A\"); return 0; }\n");
        byte[] v2Bytes = Encoding.UTF8.GetBytes("#include <stdio.h>\nint main(){ printf(\"B\"); return 0; }\n");
        File.WriteAllBytes(srcPath, v1Bytes);
        var r1 = await archiver.ProcessFileSaveAsync(srcPath, v1Bytes, ctx);
        File.WriteAllBytes(srcPath, v2Bytes);
        var r2 = await archiver.ProcessFileSaveAsync(srcPath, v2Bytes, ctx);

        var batchResult = await store.MoveProblemToTrashAsync(
            new[] { r1.Version!.Id, r2.Version!.Id },
            ProblemStatementRecord.BuildKey("Ptit", "C04037"),
            cfg.GetInternalTrashRoot(),
            cfg.ArchiveDir,
            deleteRemote: false,
            retentionDays: 30);

        Assert.True(batchResult.Success);
        Assert.Equal(2, batchResult.MovedCount);

        // Original working file MUST remain untouched on disk!
        Assert.True(File.Exists(srcPath));
        Assert.Equal(v2Bytes, File.ReadAllBytes(srcPath));

        Assert.Empty(await store.QueryVersionsAsync());
        var trash = await store.GetTrashItemsAsync();
        Assert.Equal(2, trash.Count);

        var restoredCount = await store.RestoreTrashBatchAsync(batchResult.BatchId!);
        Assert.Equal(2, restoredCount);
        var active = await store.GetVersionsByProblemAsync("Ptit", "C04037");
        Assert.Equal(2, active.Count);
        Assert.Equal(new[] { 2, 1 }, active.Select(v => v.VersionNum).ToArray());
    }

    [Fact]
    public async Task Watcher_DoesNotRecreateDeletedUnchangedVersion_ButArchivesLegitimateFutureEditAsNextVersion()
    {
        var (cfg, store, archiver) = CreateEnv();
        var srcDir = Path.Combine(_root, "Work");
        Directory.CreateDirectory(srcDir);
        var srcPath = Path.Combine(srcDir, "Untitled1.cpp");
        var ctx = new ProblemContext(OjPlatform.Ptit, "C04037", "Liệt kê", "https://code.ptit.edu.vn/student/question/C04037", "test", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        byte[] v1Bytes = Encoding.UTF8.GetBytes("#include <iostream>\nint main(){ int x = 1; return 0; }\n");
        byte[] v2Bytes = Encoding.UTF8.GetBytes("#include <iostream>\nint main(){ int x = 2; return 0; }\n");
        File.WriteAllBytes(srcPath, v1Bytes);
        await archiver.ProcessFileSaveAsync(srcPath, v1Bytes, ctx);
        File.WriteAllBytes(srcPath, v2Bytes);
        var r2 = await archiver.ProcessFileSaveAsync(srcPath, v2Bytes, ctx);

        await store.MoveVersionToTrashAsync(r2.Version!.Id, cfg.GetInternalTrashRoot(), cfg.ArchiveDir, deleteRemote: false, retentionDays: 30);

        // Simulated reconciliation scan with unchanged v2Bytes on disk MUST NOT re-archive deleted v2
        var scanUnchanged = await archiver.ProcessFileSaveAsync(srcPath, v2Bytes, ctx);
        Assert.NotEqual(ArchiveOutcome.CreatedVersion, scanUnchanged.Outcome);
        Assert.Single(await store.GetVersionsByProblemAsync("Ptit", "C04037"));

        // Legitimate new edit MUST archive cleanly as v3 (monotonic, never colliding with trashed v2!)
        byte[] v3Bytes = Encoding.UTF8.GetBytes("#include <iostream>\nint main(){ int x = 3; return 0; }\n");
        File.WriteAllBytes(srcPath, v3Bytes);
        var r3 = await archiver.ProcessFileSaveAsync(srcPath, v3Bytes, ctx);
        Assert.Equal(ArchiveOutcome.CreatedVersion, r3.Outcome);
        Assert.Equal(3, r3.Version!.VersionNum);
    }

    [Fact]
    public async Task LocalDelete_CancelsPendingUpload_AndPreventsCloudPullResurrection_WhilePreservingRemoteCopy()
    {
        var (cfg, store, archiver) = CreateEnv();
        var srcDir = Path.Combine(_root, "Work");
        Directory.CreateDirectory(srcDir);
        var srcPath = Path.Combine(srcDir, "sol.cpp");
        var ctx = new ProblemContext(OjPlatform.Codeforces, "100A", "Test", "https://codeforces.com/problemset/problem/100/A", "test", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        byte[] v1Bytes = Encoding.UTF8.GetBytes("#include <bits/stdc++.h>\nint main(){ return 10; }\n");
        File.WriteAllBytes(srcPath, v1Bytes);
        var r1 = await archiver.ProcessFileSaveAsync(srcPath, v1Bytes, ctx);
        var v1 = r1.Version!;

        // Simulate local delete BEFORE upload succeeded (pending in upload_queue)
        await store.MoveVersionToTrashAsync(v1.Id, cfg.GetInternalTrashRoot(), cfg.ArchiveDir, deleteRemote: false, retentionDays: 30);

        // Pending uploads MUST NOT resurrect deleted v1
        var pending = await store.GetPendingOrRetryableUploadsAsync(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 10_000);
        Assert.Empty(pending);

        // Manifest from another device still containing v1 (sync'd earlier elsewhere) MUST NOT resurrect locally deleted v1!
        var remotePayload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = 2,
            exported_at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            device = "OTHER-PC",
            versions = new[]
            {
                new
                {
                    id = v1.Id,
                    version_num = v1.VersionNum,
                    source_path = v1.SourcePath,
                    file_name = v1.FileName,
                    sha256 = v1.Sha256,
                    norm_hash = v1.NormHash,
                    size_bytes = v1.SizeBytes,
                    problem_code = v1.ProblemCode,
                    problem_title = v1.ProblemTitle,
                    platform = v1.Platform,
                    created_at = v1.CreatedAtUnixMs,
                    drive_file_id = "remote_drive_id_1",
                    is_conflict = false
                }
            }
        });

        await CloudManifestCodec.ReconcileManifestBytesAsync(store, remotePayload);
        Assert.Empty(await store.QueryVersionsAsync());

        // Tombstone remains LocalOnly (no unauthorized remote deletion requested!)
        var tombstones = await store.GetTombstonesAsync();
        var ts = Assert.Single(tombstones, t => t.EntityType == "Version" && t.VersionId == v1.Id);
        Assert.False(ts.DeleteRemote);
        Assert.Equal(RemoteDeleteStatus.LocalOnly, ts.RemoteDeleteState);
    }

    [Fact]
    public async Task ExplicitRemoteDelete_QueuesOffline_RetriesAndSucceedsWhenOnline_AndRespectsNewerRemoteEdits()
    {
        var (cfg, store, archiver) = CreateEnv();
        cfg.DriveEnabled = true;
        cfg.DriveRootFolderId = "root-folder-1";
        cfg.OAuthClient = new GoogleOAuthClientConfig { ClientId = "999000111222-test.apps.googleusercontent.com" };

        var credStore = new DpapiCredentialStore(_root, "gdrive_oauth_tokens.dpapi");
        await credStore.SaveAsync(new GoogleAuthTokens(
            "ya29.valid",
            "1//ref",
            DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds(),
            "test@example.com",
            "Test User"));

        var srcPath = Path.Combine(_root, "Work", "sol.cpp");
        Directory.CreateDirectory(Path.GetDirectoryName(srcPath)!);
        var ctx = new ProblemContext(OjPlatform.Ptit, "C01001", "Sum", "https://code.ptit.edu.vn/student/question/C01001", "test", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        byte[] v1Bytes = Encoding.UTF8.GetBytes("#include <stdio.h>\nint main(){ printf(\"42\"); return 0; }\n");
        File.WriteAllBytes(srcPath, v1Bytes);
        var r1 = await archiver.ProcessFileSaveAsync(srcPath, v1Bytes, ctx);
        await store.SetVersionRemoteFileIdAsync(r1.Version!.Id, "remote_id_xyz");
        await store.MarkUploadAttemptAsync(r1.Version.Id, UploadStatus.Synced, "remote_id_xyz", null, 0);

        // Request explicit local + cloud deletion while temporarily offline
        await store.MoveVersionToTrashAsync(r1.Version.Id, cfg.GetInternalTrashRoot(), cfg.ArchiveDir, deleteRemote: true, retentionDays: 30);

        bool online = false;
        var deletedRemoteIds = new List<string>();
        using var http = new HttpClient(new StubHandler(req =>
        {
            if (req.Method == HttpMethod.Delete && req.RequestUri!.AbsolutePath.Contains("remote_id_xyz", StringComparison.Ordinal))
            {
                if (!online)
                    throw new HttpRequestException("Offline simulation");
                deletedRemoteIds.Add("remote_id_xyz");
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsoluteUri.Contains("files?", StringComparison.Ordinal))
            {
                return JsonResponse(new { files = Array.Empty<object>() });
            }
            if (req.Method == HttpMethod.Post && req.RequestUri!.AbsoluteUri.Contains("upload/drive/v3/files", StringComparison.Ordinal))
            {
                return JsonResponse(new { id = "manifest_id_1" });
            }
            return JsonResponse(new { ok = true });
        }));

        var drive = new GoogleDriveService(cfg, store, credStore, http);
        var offlineReport = await drive.SyncPendingAsync();
        Assert.Contains(await store.GetPendingRemoteDeletionsAsync(), t => t.DriveFileId == "remote_id_xyz" && t.RemoteDeleteState == RemoteDeleteStatus.RemoteDeleteFailed);

        // Recover network -> pending remote delete completes cleanly!
        online = true;
        var onlineReport = await drive.SyncPendingAsync();
        Assert.Contains("remote_id_xyz", deletedRemoteIds);
        Assert.Empty(await store.GetPendingRemoteDeletionsAsync());

        // Multi-device safety: a NEWER version created AFTER deletion on Device B is preserved cleanly!
        var newerTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 60_000;
        var remoteManifest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = 2,
            exported_at = newerTime,
            device = "DEVICE-B",
            versions = new[]
            {
                new
                {
                    id = "newer-version-from-device-b",
                    version_num = 2,
                    source_path = srcPath,
                    file_name = "C01001_Sum.cpp",
                    sha256 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                    norm_hash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                    size_bytes = 55L,
                    problem_code = "C01001",
                    problem_title = "Sum",
                    platform = "Ptit",
                    created_at = newerTime,
                    drive_file_id = "remote_id_newer",
                    is_conflict = false
                }
            }
        });
        await CloudManifestCodec.ReconcileManifestBytesAsync(store, remoteManifest);
        var active = await store.QueryVersionsAsync();
        Assert.Contains(active, v => v.Id == "newer-version-from-device-b");
    }

    [Fact]
    public async Task TrashPermanentDelete_AndExpiredRetentionPurge_CleanlyRemoveTrashedItemsAndPreserveTombstones()
    {
        var (cfg, store, archiver) = CreateEnv();
        var srcDir = Path.Combine(_root, "Work");
        Directory.CreateDirectory(srcDir);
        var srcPath = Path.Combine(srcDir, "test.cpp");
        var ctx = new ProblemContext(OjPlatform.Ptit, "C02001", "Alpha", "", "test", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        byte[] bytes = Encoding.UTF8.GetBytes("int main(){ return 5; }\n");
        File.WriteAllBytes(srcPath, bytes);
        var r1 = await archiver.ProcessFileSaveAsync(srcPath, bytes, ctx);
        await store.MoveVersionToTrashAsync(r1.Version!.Id, cfg.GetInternalTrashRoot(), cfg.ArchiveDir, deleteRemote: false, retentionDays: 30);

        Assert.Single(await store.GetTrashItemsAsync());
        await store.PermanentlyDeleteTrashVersionAsync(r1.Version.Id, alsoRequestRemoteDelete: false);
        Assert.Empty(await store.GetTrashItemsAsync());
        Assert.True(File.Exists(srcPath));
        Assert.NotEmpty(await store.GetTombstonesAsync());
    }

    [Fact]
    public void StorageStatusFormatter_MapsShortSemanticLabels_WithoutConflatingLocalWithError()
    {
        Assert.Equal("Cục bộ", StorageStatusFormatter.GetShortLabel(VersionAvailability.LocalOnly, null, UiLanguage.Vi));
        Assert.Equal("Đã đồng bộ", StorageStatusFormatter.GetShortLabel(VersionAvailability.Synced, UploadStatus.Synced, UiLanguage.Vi));
        Assert.Equal("Chờ đồng bộ", StorageStatusFormatter.GetShortLabel(VersionAvailability.LocalOnly, UploadStatus.Pending, UiLanguage.Vi));
        Assert.Equal("Chỉ đám mây", StorageStatusFormatter.GetShortLabel(VersionAvailability.CloudOnly, null, UiLanguage.Vi));
        Assert.Equal("Lỗi đồng bộ", StorageStatusFormatter.GetShortLabel(VersionAvailability.LocalOnly, UploadStatus.Failed, UiLanguage.Vi));

        Assert.Equal("Local", StorageStatusFormatter.GetShortLabel(VersionAvailability.LocalOnly, null, UiLanguage.En));
        Assert.Equal("Synced", StorageStatusFormatter.GetShortLabel(VersionAvailability.Synced, UploadStatus.Synced, UiLanguage.En));
        Assert.Equal("Pending", StorageStatusFormatter.GetShortLabel(VersionAvailability.LocalOnly, UploadStatus.Pending, UiLanguage.En));
        Assert.Equal("Cloud-only", StorageStatusFormatter.GetShortLabel(VersionAvailability.CloudOnly, null, UiLanguage.En));
        Assert.Equal("Sync error", StorageStatusFormatter.GetShortLabel(VersionAvailability.LocalOnly, UploadStatus.Failed, UiLanguage.En));
    }

    [Fact]
    public async Task RealDatabaseCopyMigration_PreservesAllExistingVersionsAndSupportsTrashRoundTrip()
    {
        var backupParent = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GhostCode_Backups");
        string? latestBackupDb = Directory.Exists(backupParent)
            ? Directory.GetDirectories(backupParent, "pre_phase1_*")
                .OrderByDescending(d => d)
                .Select(d => Path.Combine(d, "AppData", "ghostcode.db"))
                .FirstOrDefault(File.Exists)
            : null;

        if (latestBackupDb is null)
            return; // Non-developer CI runner without user AppData backup

        var testDb = Path.Combine(_root, "real_copy.db");
        File.Copy(latestBackupDb, testDb, overwrite: true);

        int countBefore;
        await using (var preConn = new SqliteConnection($"Data Source={testDb};Pooling=False"))
        {
            await preConn.OpenAsync();
            await using var cmd = preConn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM archive_versions;";
            countBefore = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        }

        var store = new SqliteArchiveStore(testDb);
        await store.InitializeAsync();
        Assert.Empty(Directory.GetFiles(_root, "*.corrupt.*"));

        var afterMigration = await store.QueryVersionsAsync(limit: 500);
        Assert.Equal(countBefore, afterMigration.Count);
        Assert.True((await store.CheckDatabaseHealthAsync()).Healthy);

        if (afterMigration.Count > 0)
        {
            var sample = afterMigration[0];
            var trashDir = Path.Combine(_root, "trash");
            var res = await store.MoveVersionToTrashAsync(sample.Id, trashDir, Path.Combine(_root, "arc"), deleteRemote: false);
            Assert.True(res.Success);
            Assert.Equal(countBefore - 1, (await store.QueryVersionsAsync(limit: 500)).Count);
            Assert.True(await store.RestoreTrashItemAsync(sample.Id));
            Assert.Equal(countBefore, (await store.QueryVersionsAsync(limit: 500)).Count);
        }
    }

    [Fact]
    public async Task RestoreTrashItem_WhenDestinationOccupiedWithDifferentContent_NeverOverwritesAndAllocatesNewSlot()
    {
        var (cfg, store, archiver) = CreateEnv();
        var srcDir = Path.Combine(_root, "Work");
        Directory.CreateDirectory(srcDir);
        var srcPath = Path.Combine(srcDir, "main.cpp");
        var ctx = new ProblemContext(OjPlatform.Ptit, "C03001", "ConflictSafe", "", "test", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        byte[] originalBytes = Encoding.UTF8.GetBytes("#include <iostream>\nint main(){ std::cout << \"original\" << '\\n'; return 0; }\n");
        File.WriteAllBytes(srcPath, originalBytes);
        var r1 = await archiver.ProcessFileSaveAsync(srcPath, originalBytes, ctx);
        Assert.NotNull(r1.Version);
        string originalArchivePath = r1.Version.ArchivePath;

        var moved = await store.MoveVersionToTrashAsync(r1.Version.Id, cfg.GetInternalTrashRoot(), cfg.ArchiveDir, deleteRemote: false);
        Assert.True(moved.Success);
        Assert.False(File.Exists(originalArchivePath));

        byte[] occupierBytes = Encoding.UTF8.GetBytes("// Newer file occupying same historical slot\nint other(){ return 99; }\n");
        Directory.CreateDirectory(Path.GetDirectoryName(originalArchivePath)!);
        await File.WriteAllBytesAsync(originalArchivePath, occupierBytes);

        bool restored = await store.RestoreTrashItemAsync(r1.Version.Id);
        Assert.True(restored);

        // Pre-existing file at originalArchivePath MUST remain 100% intact
        Assert.Equal(occupierBytes, await File.ReadAllBytesAsync(originalArchivePath));

        var restoredVer = Assert.Single(await store.QueryVersionsAsync(), v => v.Id == r1.Version.Id);
        Assert.NotEqual(originalArchivePath, restoredVer.ArchivePath, StringComparer.OrdinalIgnoreCase);
        Assert.True(File.Exists(restoredVer.ArchivePath));
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(restoredVer.ArchivePath));
    }

    [Fact]
    public async Task RestoreTrashItem_WhenDestinationHasIdenticalBytes_PreservesFileAndCleansTrashCopy()
    {
        var (cfg, store, archiver) = CreateEnv();
        var srcDir = Path.Combine(_root, "Work");
        Directory.CreateDirectory(srcDir);
        var srcPath = Path.Combine(srcDir, "same.cpp");
        byte[] bytes = Encoding.UTF8.GetBytes("#include <iostream>\nint main(){ std::cout << 77 << '\\n'; return 0; }\n");
        File.WriteAllBytes(srcPath, bytes);
        var r1 = await archiver.ProcessFileSaveAsync(srcPath, bytes, null);
        Assert.NotNull(r1.Version);

        await store.MoveVersionToTrashAsync(r1.Version.Id, cfg.GetInternalTrashRoot(), cfg.ArchiveDir, deleteRemote: false);
        Directory.CreateDirectory(Path.GetDirectoryName(r1.Version.ArchivePath)!);
        await File.WriteAllBytesAsync(r1.Version.ArchivePath, bytes);

        Assert.True(await store.RestoreTrashItemAsync(r1.Version.Id));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(r1.Version.ArchivePath));
        Assert.Empty(await store.GetTrashItemsAsync());
    }

    [Fact]
    public async Task ReconcileAfterCrash_RevertsInterruptedTrashMoveFromJournal_WithoutDuplicatingVersions()
    {
        var (cfg, store, archiver) = CreateEnv();
        var srcDir = Path.Combine(_root, "Work");
        Directory.CreateDirectory(srcDir);
        var srcPath = Path.Combine(srcDir, "crash.cpp");
        byte[] bytes = Encoding.UTF8.GetBytes("#include <iostream>\nint main(){ std::cout << 123 << '\\n'; return 0; }\n");
        File.WriteAllBytes(srcPath, bytes);
        var r1 = await archiver.ProcessFileSaveAsync(srcPath, bytes, null);
        var v = r1.Version!;

        // Simulate power loss after moving archive file into Trash but before SQLite commit
        var crashBatchDir = Path.Combine(cfg.GetInternalTrashRoot(), "crashbatch");
        Directory.CreateDirectory(crashBatchDir);
        var crashedTrashPath = Path.Combine(crashBatchDir, $"{v.Id}_{v.FileName}");
        File.Move(v.ArchivePath, crashedTrashPath);

        await using (var conn = new SqliteConnection($"Data Source={cfg.DatabasePath}"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO trash_op_journal (op_id, batch_id, op_type, version_id, origin_path, dest_path, expected_sha256, status, created_at)
                VALUES ('op1', 'crashbatch', 'MoveToTrash', $vid, $orig, $dest, $sha, 'Planned', $ts);
                """;
            cmd.Parameters.AddWithValue("$vid", v.Id);
            cmd.Parameters.AddWithValue("$orig", v.ArchivePath);
            cmd.Parameters.AddWithValue("$dest", crashedTrashPath);
            cmd.Parameters.AddWithValue("$sha", v.Sha256);
            cmd.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await cmd.ExecuteNonQueryAsync();
        }

        int recovered = await archiver.ReconcileAfterCrashAsync();
        Assert.True(recovered >= 1);
        Assert.True(File.Exists(v.ArchivePath));
        Assert.False(File.Exists(crashedTrashPath));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(v.ArchivePath));

        var allVersions = await store.QueryVersionsAsync();
        Assert.Single(allVersions);
        Assert.Equal(v.Id, allVersions[0].Id);
    }

    [Fact]
    public async Task RestoreAfterRemoteDeletion_ClearsStaleDriveIdAndRequeuesUpload_WhileLocalDeletePreservesSyncedLink()
    {
        var (cfg, store, archiver) = CreateEnv();
        cfg.DriveEnabled = true;
        cfg.DriveRootFolderId = "root-folder-1";
        cfg.OAuthClient = new GoogleOAuthClientConfig { ClientId = "999000111222-test.apps.googleusercontent.com" };

        var credStore = new DpapiCredentialStore(_root, "gdrive_oauth_tokens.dpapi");
        await credStore.SaveAsync(new GoogleAuthTokens(
            "ya29.valid",
            "1//ref",
            DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds(),
            "test@example.com",
            "Test User"));

        var srcDir = Path.Combine(_root, "Work");
        Directory.CreateDirectory(srcDir);
        var p1 = Path.Combine(srcDir, "local_del.cpp");
        var p2 = Path.Combine(srcDir, "remote_del.cpp");
        byte[] b1 = Encoding.UTF8.GetBytes("#include <iostream>\nint main(){ std::cout << 10 << '\\n'; return 0; }\n");
        byte[] b2 = Encoding.UTF8.GetBytes("#include <iostream>\nint main(){ std::cout << 20 << '\\n'; return 0; }\n");
        File.WriteAllBytes(p1, b1);
        File.WriteAllBytes(p2, b2);

        var r1 = await archiver.ProcessFileSaveAsync(p1, b1, null);
        var r2 = await archiver.ProcessFileSaveAsync(p2, b2, null);
        var vLocal = r1.Version!;
        var vRemote = r2.Version!;

        await store.SetVersionRemoteFileIdAsync(vLocal.Id, "drive_local_1");
        await store.MarkUploadAttemptAsync(vLocal.Id, UploadStatus.Synced, "drive_local_1", null, 0);
        await store.SetVersionRemoteFileIdAsync(vRemote.Id, "drive_remote_2");
        await store.MarkUploadAttemptAsync(vRemote.Id, UploadStatus.Synced, "drive_remote_2", null, 0);

        // 1. Local-only delete preserves remote link on restore
        await store.MoveVersionToTrashAsync(vLocal.Id, cfg.GetInternalTrashRoot(), cfg.ArchiveDir, deleteRemote: false);
        Assert.True(await store.RestoreTrashItemAsync(vLocal.Id));
        var restoredLocal = Assert.Single(await store.QueryVersionsAsync(), v => v.Id == vLocal.Id);
        Assert.Equal("drive_local_1", restoredLocal.DriveFileId);

        // 2. Explicit remote delete + Drive sync completes RemoteDeleted -> restore clears stale DriveFileId and re-queues Pending
        await store.MoveVersionToTrashAsync(vRemote.Id, cfg.GetInternalTrashRoot(), cfg.ArchiveDir, deleteRemote: true);
        using var http = new HttpClient(new StubHandler(req =>
        {
            if (req.Method == HttpMethod.Delete && req.RequestUri!.AbsolutePath.Contains("drive_remote_2", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsoluteUri.Contains("files?", StringComparison.Ordinal))
                return JsonResponse(new { files = Array.Empty<object>() });
            if (req.Method == HttpMethod.Post && req.RequestUri!.AbsoluteUri.Contains("upload/drive/v3/files", StringComparison.Ordinal))
                return JsonResponse(new { id = "manifest_id_2" });
            return JsonResponse(new { ok = true });
        }));
        var drive = new GoogleDriveService(cfg, store, credStore, http);
        await drive.SyncPendingAsync();

        Assert.True(await store.RestoreTrashItemAsync(vRemote.Id));
        var restoredRemote = Assert.Single(await store.QueryVersionsAsync(), v => v.Id == vRemote.Id);
        Assert.Null(restoredRemote.DriveFileId);
        var pendingQueue = await store.GetPendingOrRetryableUploadsAsync(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Assert.Contains(pendingQueue, q => q.Version.Id == vRemote.Id);
        var allQueue = await store.GetAllUploadQueueAsync(limit: 50);
        Assert.Contains(allQueue, q => q.Version.Id == vLocal.Id && q.Queue.Status == UploadStatus.Synced);
    }

    private static HttpResponseMessage JsonResponse(object payload) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_handler(request));
    }
}
