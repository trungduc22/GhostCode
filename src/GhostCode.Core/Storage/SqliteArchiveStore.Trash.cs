using GhostCode.Core.Analysis;
using GhostCode.Core.Models;
using Microsoft.Data.Sqlite;

namespace GhostCode.Core.Storage;

public sealed partial class SqliteArchiveStore
{
    public async Task<int> GetMaxVersionNumAsync(
        string? platform,
        string? problemCode,
        string? sourcePath,
        CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        if (!string.IsNullOrWhiteSpace(problemCode) && !string.Equals(platform, OjPlatform.Offline.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            cmd.CommandText = """
                SELECT COALESCE(MAX(version_num), 0)
                FROM archive_versions
                WHERE platform = $plat COLLATE NOCASE AND problem_code = $code COLLATE NOCASE;
                """;
            cmd.Parameters.AddWithValue("$plat", platform ?? string.Empty);
            cmd.Parameters.AddWithValue("$code", problemCode);
        }
        else if (!string.IsNullOrWhiteSpace(sourcePath))
        {
            cmd.CommandText = """
                SELECT COALESCE(MAX(version_num), 0)
                FROM archive_versions
                WHERE source_path = $sp COLLATE NOCASE
                  AND (problem_code IS NULL OR problem_code = '' OR platform = 'Offline' COLLATE NOCASE);
                """;
            cmd.Parameters.AddWithValue("$sp", sourcePath);
        }
        else
        {
            return 0;
        }

        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    public async Task<Dictionary<string, UploadStatus>> GetUploadStatusesByVersionIdsAsync(
        IEnumerable<string> versionIds,
        CancellationToken ct = default)
    {
        var ids = versionIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var map = new Dictionary<string, UploadStatus>(StringComparer.OrdinalIgnoreCase);
        if (ids.Count == 0)
            return map;

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT version_id, status FROM upload_queue;";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var wanted = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(ct))
        {
            var vid = reader.GetString(0);
            if (!wanted.Contains(vid))
                continue;
            if (Enum.TryParse<UploadStatus>(reader.GetString(1), true, out var st))
                map[vid] = st;
        }
        return map;
    }

    public async Task<(bool Success, ArchiveVersionRecord? DeletedVersion, string? BatchId)> MoveVersionToTrashAsync(
        string versionId,
        string? trashRootDir,
        string? archiveRootDir,
        bool deleteRemote = false,
        int retentionDays = 30,
        CancellationToken ct = default)
    {
        var res = await MoveVersionsToTrashInternalAsync(
            new[] { versionId },
            entityType: "Version",
            problemKey: null,
            trashRootDir: trashRootDir,
            archiveRootDir: archiveRootDir,
            deleteRemote: deleteRemote,
            retentionDays: retentionDays,
            ct: ct);
        return (res.Success, res.FirstVersion, res.BatchId);
    }

    public async Task<MoveToTrashResult> MoveProblemToTrashAsync(
        IReadOnlyList<string> versionIds,
        string? problemKey,
        string? trashRootDir,
        string? archiveRootDir,
        bool deleteRemote = false,
        int retentionDays = 30,
        CancellationToken ct = default)
    {
        var res = await MoveVersionsToTrashInternalAsync(
            versionIds,
            entityType: "Problem",
            problemKey: problemKey,
            trashRootDir: trashRootDir,
            archiveRootDir: archiveRootDir,
            deleteRemote: deleteRemote,
            retentionDays: retentionDays,
            ct: ct);
        return new MoveToTrashResult(res.Success, res.MovedCount, res.BatchId);
    }

    private async Task<(bool Success, int MovedCount, string? BatchId, ArchiveVersionRecord? FirstVersion)> MoveVersionsToTrashInternalAsync(
        IReadOnlyList<string> versionIds,
        string entityType,
        string? problemKey,
        string? trashRootDir,
        string? archiveRootDir,
        bool deleteRemote,
        int retentionDays,
        CancellationToken ct)
    {
        var ids = versionIds.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (ids.Count == 0)
            return (false, 0, null, null);

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var expiresAt = now + Math.Max(1, retentionDays) * 86_400_000L;
        var batchId = Guid.NewGuid().ToString("N");
        var scope = deleteRemote ? ArchiveDeletionScope.LocalAndCloudRequested : ArchiveDeletionScope.LocalTrash;
        var movedPhysicalFiles = new List<(string Origin, string Dest)>();

        await _writeLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

            var targets = new List<ArchiveVersionRecord>();
            foreach (var id in ids)
            {
                await using var sel = conn.CreateCommand();
                sel.Transaction = tx;
                sel.CommandText = """
                    SELECT id, version_num, source_path, archive_path, ready_path, file_name, sha256, norm_hash, size_bytes, problem_code, problem_title, platform, created_at, is_local_available, drive_file_id, is_conflict
                    FROM archive_versions
                    WHERE id = $id AND is_deleted = 0;
                    """;
                sel.Parameters.AddWithValue("$id", id);
                await using var r = await sel.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct))
                    targets.Add(ReadVersion(r));
            }

            if (targets.Count == 0)
            {
                await tx.RollbackAsync(ct);
                return (false, 0, null, null);
            }

            if (string.IsNullOrWhiteSpace(problemKey) && targets.Count > 0)
            {
                var first = targets[0];
                if (!string.IsNullOrWhiteSpace(first.ProblemCode))
                    problemKey = ProblemStatementRecord.BuildKey(first.Platform, first.ProblemCode);
            }

            foreach (var v in targets)
            {
                string? trashPath = null;
                if (!string.IsNullOrWhiteSpace(trashRootDir) && v.IsLocalAvailable && File.Exists(v.ArchivePath))
                {
                    var batchDir = Path.Combine(trashRootDir, batchId);
                    Directory.CreateDirectory(batchDir);
                    trashPath = Path.Combine(batchDir, $"{v.Id}_{Path.GetFileName(v.FileName)}");
                    File.Move(v.ArchivePath, trashPath, overwrite: true);
                    movedPhysicalFiles.Add((v.ArchivePath, trashPath));
                }

                await using (var upd = conn.CreateCommand())
                {
                    upd.Transaction = tx;
                    upd.CommandText = """
                        UPDATE archive_versions
                        SET is_deleted = 1,
                            deleted_at = $delAt,
                            trash_batch_id = $batch,
                            delete_scope = $scope,
                            deleted_origin_device = $dev,
                            trash_archive_path = COALESCE($tp, trash_archive_path)
                        WHERE id = $id;
                        """;
                    upd.Parameters.AddWithValue("$delAt", now);
                    upd.Parameters.AddWithValue("$batch", batchId);
                    upd.Parameters.AddWithValue("$scope", scope.ToString());
                    upd.Parameters.AddWithValue("$dev", Environment.MachineName);
                    upd.Parameters.AddWithValue("$tp", (object?)trashPath ?? DBNull.Value);
                    upd.Parameters.AddWithValue("$id", v.Id);
                    await upd.ExecuteNonQueryAsync(ct);
                }

                // Prevent upload queue from resurrecting deleted version while preserving Synced ledger markers
                await using (var qCmd = conn.CreateCommand())
                {
                    qCmd.Transaction = tx;
                    qCmd.CommandText = """
                        UPDATE upload_queue
                        SET status = 'Cancelled', updated_at = $now
                        WHERE version_id = $id AND status IN ('Pending', 'Uploading', 'Failed');
                        """;
                    qCmd.Parameters.AddWithValue("$now", now);
                    qCmd.Parameters.AddWithValue("$id", v.Id);
                    await qCmd.ExecuteNonQueryAsync(ct);
                }

                bool hasRemote = !string.IsNullOrWhiteSpace(v.DriveFileId);
                var remoteState = (deleteRemote && hasRemote) ? RemoteDeleteStatus.PendingRemoteDelete : RemoteDeleteStatus.LocalOnly;
                await UpsertTombstoneInTxAsync(conn, tx, new ArchiveTombstoneRecord(
                    $"ver:{v.Id}",
                    "Version",
                    v.Id,
                    problemKey,
                    v.SourcePath,
                    v.Platform,
                    v.ProblemCode,
                    v.Sha256,
                    v.NormHash,
                    v.DriveFileId,
                    deleteRemote,
                    remoteState,
                    0,
                    null,
                    now,
                    now,
                    Environment.MachineName,
                    expiresAt), ct);

                // Watcher content suppression until a genuinely new edit changes the content hash
                await UpsertTombstoneInTxAsync(conn, tx, new ArchiveTombstoneRecord(
                    $"sup:{v.SourcePath.ToLowerInvariant()}|{v.Sha256}",
                    "ContentSuppression",
                    v.Id,
                    problemKey,
                    v.SourcePath,
                    v.Platform,
                    v.ProblemCode,
                    v.Sha256,
                    v.NormHash,
                    v.DriveFileId,
                    false,
                    RemoteDeleteStatus.LocalOnly,
                    0,
                    null,
                    now,
                    now,
                    Environment.MachineName,
                    expiresAt), ct);
            }

            if (string.Equals(entityType, "Problem", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(problemKey))
            {
                var sample = targets[0];
                await UpsertTombstoneInTxAsync(conn, tx, new ArchiveTombstoneRecord(
                    $"prob:{problemKey.ToUpperInvariant()}",
                    "Problem",
                    null,
                    problemKey,
                    sample.SourcePath,
                    sample.Platform,
                    sample.ProblemCode,
                    null,
                    null,
                    null,
                    deleteRemote,
                    deleteRemote ? RemoteDeleteStatus.PendingRemoteDelete : RemoteDeleteStatus.LocalOnly,
                    0,
                    null,
                    now,
                    now,
                    Environment.MachineName,
                    expiresAt), ct);
            }

            // Reconcile GhostCode organized ready_path inside ArchiveDir without ever touching original working source_path
            foreach (var readyGroup in targets.Where(t => !string.IsNullOrWhiteSpace(t.ReadyPath)).GroupBy(t => t.ReadyPath!, StringComparer.OrdinalIgnoreCase))
            {
                await ReconcileReadyCopyAfterDeleteInTxAsync(conn, tx, readyGroup.Key, archiveRootDir, ct);
            }

            await InsertHistoryEventInTxAsync(conn, tx, new ArchiveHistoryEvent(
                Guid.NewGuid().ToString("N"),
                entityType == "Problem" ? "ProblemMovedToTrash" : "VersionMovedToTrash",
                targets[0].SourcePath,
                targets[0].Id,
                entityType == "Problem"
                    ? $"Moved {targets.Count} version(s) of {targets[0].ProblemCode ?? targets[0].FileName} to Trash"
                    : $"Moved v{targets[0].VersionNum} ({targets[0].FileName}) to Trash",
                now,
                Environment.MachineName), ct);

            await tx.CommitAsync(ct);
            return (true, targets.Count, batchId, targets[0]);
        }
        catch
        {
            foreach (var (origin, dest) in movedPhysicalFiles)
            {
                try
                {
                    if (File.Exists(dest) && !File.Exists(origin))
                    {
                        var dir = Path.GetDirectoryName(origin);
                        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                        File.Move(dest, origin, overwrite: true);
                    }
                }
                catch { }
            }
            throw;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<bool> RestoreTrashItemAsync(string versionId, CancellationToken ct = default)
    {
        var count = await RestoreTrashVersionsInternalAsync(new[] { versionId }, ct);
        return count > 0;
    }

    public async Task<int> RestoreTrashBatchAsync(string batchId, CancellationToken ct = default)
    {
        var ids = new List<string>();
        await using (var conn = new SqliteConnection(_connectionString))
        {
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id FROM archive_versions WHERE is_deleted = 1 AND trash_batch_id = $b;";
            cmd.Parameters.AddWithValue("$b", batchId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                ids.Add(r.GetString(0));
        }
        return await RestoreTrashVersionsInternalAsync(ids, ct);
    }

    private async Task<int> RestoreTrashVersionsInternalAsync(IReadOnlyList<string> versionIds, CancellationToken ct)
    {
        var ids = versionIds.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (ids.Count == 0)
            return 0;

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        int restored = 0;

        await _writeLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

            foreach (var id in ids)
            {
                ArchiveVersionRecord? v = null;
                string? trashPath = null;
                await using (var sel = conn.CreateCommand())
                {
                    sel.Transaction = tx;
                    sel.CommandText = """
                        SELECT id, version_num, source_path, archive_path, ready_path, file_name, sha256, norm_hash, size_bytes, problem_code, problem_title, platform, created_at, is_local_available, drive_file_id, is_conflict, trash_archive_path
                        FROM archive_versions
                        WHERE id = $id AND is_deleted = 1;
                        """;
                    sel.Parameters.AddWithValue("$id", id);
                    await using var r = await sel.ExecuteReaderAsync(ct);
                    if (await r.ReadAsync(ct))
                    {
                        v = ReadVersion(r);
                        trashPath = r.IsDBNull(16) ? null : r.GetString(16);
                    }
                }
                if (v is null)
                    continue;

                if (!string.IsNullOrWhiteSpace(trashPath) && File.Exists(trashPath))
                {
                    var bytes = await File.ReadAllBytesAsync(trashPath, ct);
                    var actualSha = SourceAnalyzer.ComputeSha256Hex(bytes);
                    if (!string.Equals(actualSha, v.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"Trash payload SHA-256 verification failed for {v.FileName}.");

                    var originDir = Path.GetDirectoryName(v.ArchivePath);
                    if (!string.IsNullOrEmpty(originDir))
                        Directory.CreateDirectory(originDir);
                    File.Move(trashPath, v.ArchivePath, overwrite: true);
                }

                await using (var upd = conn.CreateCommand())
                {
                    upd.Transaction = tx;
                    upd.CommandText = """
                        UPDATE archive_versions
                        SET is_deleted = 0,
                            deleted_at = 0,
                            trash_batch_id = NULL,
                            delete_scope = NULL,
                            deleted_origin_device = NULL,
                            trash_archive_path = NULL
                        WHERE id = $id;
                        """;
                    upd.Parameters.AddWithValue("$id", v.Id);
                    await upd.ExecuteNonQueryAsync(ct);
                }

                // Clear local deletion tombstones & watcher suppression for this restored version
                await using (var delT = conn.CreateCommand())
                {
                    delT.Transaction = tx;
                    delT.CommandText = """
                        DELETE FROM archive_tombstones
                        WHERE version_id = $id
                           OR (entity_type = 'ContentSuppression' AND source_path = $sp COLLATE NOCASE AND content_sha256 = $sha);
                        """;
                    delT.Parameters.AddWithValue("$id", v.Id);
                    delT.Parameters.AddWithValue("$sp", v.SourcePath);
                    delT.Parameters.AddWithValue("$sha", v.Sha256);
                    await delT.ExecuteNonQueryAsync(ct);
                }

                if (!string.IsNullOrWhiteSpace(v.ProblemCode))
                {
                    var pKey = ProblemStatementRecord.BuildKey(v.Platform, v.ProblemCode).ToUpperInvariant();
                    await using var delProbT = conn.CreateCommand();
                    delProbT.Transaction = tx;
                    delProbT.CommandText = "DELETE FROM archive_tombstones WHERE entity_type = 'Problem' AND UPPER(problem_key) = $pk;";
                    delProbT.Parameters.AddWithValue("$pk", pKey);
                    await delProbT.ExecuteNonQueryAsync(ct);
                }

                // Re-enable Pending upload only if previously cancelled before syncing remotely
                await using (var rq = conn.CreateCommand())
                {
                    rq.Transaction = tx;
                    rq.CommandText = """
                        UPDATE upload_queue
                        SET status = 'Pending', updated_at = $now
                        WHERE version_id = $id AND status = 'Cancelled' AND (drive_file_id IS NULL OR drive_file_id = '');
                        """;
                    rq.Parameters.AddWithValue("$now", now);
                    rq.Parameters.AddWithValue("$id", v.Id);
                    await rq.ExecuteNonQueryAsync(ct);
                }

                if (!string.IsNullOrWhiteSpace(v.ReadyPath) && File.Exists(v.ArchivePath))
                    await ReconcileReadyCopyAfterDeleteInTxAsync(conn, tx, v.ReadyPath, null, ct);

                await InsertHistoryEventInTxAsync(conn, tx, new ArchiveHistoryEvent(
                    Guid.NewGuid().ToString("N"),
                    "RestoredFromTrash",
                    v.SourcePath,
                    v.Id,
                    $"Restored v{v.VersionNum} ({v.FileName}) from Trash",
                    now,
                    Environment.MachineName), ct);

                restored++;
            }

            await tx.CommitAsync(ct);
            return restored;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<List<TrashedArchiveVersionRecord>> GetTrashItemsAsync(CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT v.id, v.version_num, v.source_path, v.archive_path, v.ready_path, v.file_name, v.sha256, v.norm_hash, v.size_bytes, v.problem_code, v.problem_title, v.platform, v.created_at, v.is_local_available, v.drive_file_id, v.is_conflict,
                   v.deleted_at, v.trash_batch_id, v.delete_scope, v.deleted_origin_device,
                   COALESCE(t.expires_at, v.deleted_at + 2592000000),
                   COALESCE(t.remote_delete_state, 'LocalOnly')
            FROM archive_versions v
            LEFT JOIN archive_tombstones t ON t.id = ('ver:' || v.id)
            WHERE v.is_deleted = 1
            ORDER BY v.deleted_at DESC, v.version_num DESC;
            """;

        var list = new List<TrashedArchiveVersionRecord>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var ver = ReadVersion(r, 0);
            long delAt = r.GetInt64(16);
            string? batch = r.IsDBNull(17) ? null : r.GetString(17);
            var scopeStr = r.IsDBNull(18) ? nameof(ArchiveDeletionScope.LocalTrash) : r.GetString(18);
            var scope = Enum.TryParse<ArchiveDeletionScope>(scopeStr, true, out var sc) ? sc : ArchiveDeletionScope.LocalTrash;
            var dev = r.IsDBNull(19) ? Environment.MachineName : r.GetString(19);
            long expAt = r.GetInt64(20);
            var rStateStr = r.IsDBNull(21) ? nameof(RemoteDeleteStatus.LocalOnly) : r.GetString(21);
            var rState = Enum.TryParse<RemoteDeleteStatus>(rStateStr, true, out var rs) ? rs : RemoteDeleteStatus.LocalOnly;
            list.Add(new TrashedArchiveVersionRecord(ver, delAt, batch, scope, dev, expAt, rState));
        }
        return list;
    }

    public async Task<bool> PermanentlyDeleteTrashVersionAsync(
        string versionId,
        bool alsoRequestRemoteDelete = false,
        CancellationToken ct = default)
    {
        return await PurgeTrashVersionsInternalAsync(new[] { versionId }, alsoRequestRemoteDelete, ct) > 0;
    }

    public async Task<int> EmptyTrashAsync(bool alsoRequestRemoteDelete = false, CancellationToken ct = default)
    {
        var ids = new List<string>();
        await using (var conn = new SqliteConnection(_connectionString))
        {
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id FROM archive_versions WHERE is_deleted = 1;";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                ids.Add(r.GetString(0));
        }
        return await PurgeTrashVersionsInternalAsync(ids, alsoRequestRemoteDelete, ct);
    }

    public async Task<int> PurgeExpiredTrashAsync(int retentionDays = 30, CancellationToken ct = default)
    {
        long cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - Math.Max(1, retentionDays) * 86_400_000L;
        var ids = new List<string>();
        await using (var conn = new SqliteConnection(_connectionString))
        {
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id FROM archive_versions WHERE is_deleted = 1 AND deleted_at > 0 AND deleted_at <= $cut;";
            cmd.Parameters.AddWithValue("$cut", cutoff);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                ids.Add(r.GetString(0));
        }
        return await PurgeTrashVersionsInternalAsync(ids, alsoRequestRemoteDelete: false, ct);
    }

    private async Task<int> PurgeTrashVersionsInternalAsync(
        IReadOnlyList<string> versionIds,
        bool alsoRequestRemoteDelete,
        CancellationToken ct)
    {
        var ids = versionIds.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (ids.Count == 0)
            return 0;

        int purged = 0;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        await _writeLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

            var touchedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var touchedProblemKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var id in ids)
            {
                ArchiveVersionRecord? v = null;
                string? trashPath = null;
                await using (var sel = conn.CreateCommand())
                {
                    sel.Transaction = tx;
                    sel.CommandText = """
                        SELECT id, version_num, source_path, archive_path, ready_path, file_name, sha256, norm_hash, size_bytes, problem_code, problem_title, platform, created_at, is_local_available, drive_file_id, is_conflict, trash_archive_path
                        FROM archive_versions WHERE id = $id AND is_deleted = 1;
                        """;
                    sel.Parameters.AddWithValue("$id", id);
                    await using var r = await sel.ExecuteReaderAsync(ct);
                    if (await r.ReadAsync(ct))
                    {
                        v = ReadVersion(r);
                        trashPath = r.IsDBNull(16) ? null : r.GetString(16);
                    }
                }
                if (v is null)
                    continue;

                if (!string.IsNullOrWhiteSpace(trashPath) && File.Exists(trashPath))
                {
                    try { File.Delete(trashPath); } catch { }
                }
                if (v.IsLocalAvailable && File.Exists(v.ArchivePath))
                {
                    try { File.Delete(v.ArchivePath); } catch { }
                }

                if (alsoRequestRemoteDelete && !string.IsNullOrWhiteSpace(v.DriveFileId))
                {
                    await using var updT = conn.CreateCommand();
                    updT.Transaction = tx;
                    updT.CommandText = """
                        UPDATE archive_tombstones
                        SET delete_remote = 1, remote_delete_state = 'PendingRemoteDelete', updated_at = $now
                        WHERE version_id = $id;
                        """;
                    updT.Parameters.AddWithValue("$now", now);
                    updT.Parameters.AddWithValue("$id", v.Id);
                    await updT.ExecuteNonQueryAsync(ct);
                }

                await using (var delQ = conn.CreateCommand())
                {
                    delQ.Transaction = tx;
                    delQ.CommandText = "DELETE FROM upload_queue WHERE version_id = $id;";
                    delQ.Parameters.AddWithValue("$id", v.Id);
                    await delQ.ExecuteNonQueryAsync(ct);
                }
                await using (var delV = conn.CreateCommand())
                {
                    delV.Transaction = tx;
                    delV.CommandText = "DELETE FROM archive_versions WHERE id = $id;";
                    delV.Parameters.AddWithValue("$id", v.Id);
                    await delV.ExecuteNonQueryAsync(ct);
                }

                touchedSources.Add(v.SourcePath);
                if (!string.IsNullOrWhiteSpace(v.ProblemCode))
                    touchedProblemKeys.Add(ProblemStatementRecord.BuildKey(v.Platform, v.ProblemCode));
                purged++;
            }

            foreach (var sp in touchedSources)
            {
                await using var chk = conn.CreateCommand();
                chk.Transaction = tx;
                chk.CommandText = "SELECT COUNT(*) FROM archive_versions WHERE source_path = $sp COLLATE NOCASE;";
                chk.Parameters.AddWithValue("$sp", sp);
                if (Convert.ToInt64(await chk.ExecuteScalarAsync(ct)) == 0)
                {
                    await using var delSnap = conn.CreateCommand();
                    delSnap.Transaction = tx;
                    delSnap.CommandText = "DELETE FROM file_snapshots WHERE source_path = $sp COLLATE NOCASE;";
                    delSnap.Parameters.AddWithValue("$sp", sp);
                    await delSnap.ExecuteNonQueryAsync(ct);
                }
            }

            foreach (var pk in touchedProblemKeys)
            {
                var parts = pk.Split("::", 2);
                if (parts.Length != 2) continue;
                await using var chk = conn.CreateCommand();
                chk.Transaction = tx;
                chk.CommandText = "SELECT COUNT(*) FROM archive_versions WHERE platform = $plat COLLATE NOCASE AND problem_code = $code COLLATE NOCASE;";
                chk.Parameters.AddWithValue("$plat", parts[0]);
                chk.Parameters.AddWithValue("$code", parts[1]);
                if (Convert.ToInt64(await chk.ExecuteScalarAsync(ct)) == 0)
                {
                    await using var d1 = conn.CreateCommand();
                    d1.Transaction = tx;
                    d1.CommandText = "DELETE FROM problem_statements WHERE problem_key = $pk COLLATE NOCASE;";
                    d1.Parameters.AddWithValue("$pk", pk);
                    await d1.ExecuteNonQueryAsync(ct);

                    await using var d2 = conn.CreateCommand();
                    d2.Transaction = tx;
                    d2.CommandText = "DELETE FROM problem_sample_tests WHERE problem_key = $pk COLLATE NOCASE;";
                    d2.Parameters.AddWithValue("$pk", pk);
                    await d2.ExecuteNonQueryAsync(ct);
                }
            }

            await tx.CommitAsync(ct);
            return purged;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<bool> IsContentSuppressedByDeletionAsync(
        string sourcePath,
        string sha256,
        string normHash,
        CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*)
            FROM archive_tombstones
            WHERE entity_type = 'ContentSuppression'
              AND source_path = $sp COLLATE NOCASE
              AND (content_sha256 = $sha OR content_norm_hash = $nh);
            """;
        cmd.Parameters.AddWithValue("$sp", sourcePath);
        cmd.Parameters.AddWithValue("$sha", sha256);
        cmd.Parameters.AddWithValue("$nh", normHash);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct)) > 0;
    }

    public async Task<List<ArchiveTombstoneRecord>> GetTombstonesAsync(bool remoteDeleteOnly = false, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = remoteDeleteOnly
            ? """
              SELECT id, entity_type, version_id, problem_key, source_path, platform, problem_code, content_sha256, content_norm_hash, drive_file_id, delete_remote, remote_delete_state, attempt_count, last_error, deleted_at, updated_at, device_name, expires_at
              FROM archive_tombstones
              WHERE delete_remote = 1
              ORDER BY deleted_at DESC;
              """
            : """
              SELECT id, entity_type, version_id, problem_key, source_path, platform, problem_code, content_sha256, content_norm_hash, drive_file_id, delete_remote, remote_delete_state, attempt_count, last_error, deleted_at, updated_at, device_name, expires_at
              FROM archive_tombstones
              ORDER BY deleted_at DESC;
              """;
        return await ReadTombstonesAsync(cmd, ct);
    }

    public async Task<List<ArchiveTombstoneRecord>> GetPendingRemoteDeletionsAsync(CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, entity_type, version_id, problem_key, source_path, platform, problem_code, content_sha256, content_norm_hash, drive_file_id, delete_remote, remote_delete_state, attempt_count, last_error, deleted_at, updated_at, device_name, expires_at
            FROM archive_tombstones
            WHERE delete_remote = 1
              AND drive_file_id IS NOT NULL
              AND drive_file_id != ''
              AND remote_delete_state IN ('PendingRemoteDelete', 'RemoteDeleteFailed')
            ORDER BY deleted_at ASC;
            """;
        return await ReadTombstonesAsync(cmd, ct);
    }

    public async Task MarkRemoteDeletionResultAsync(string tombstoneId, bool success, string? errorMessage, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE archive_tombstones
                SET remote_delete_state = $st,
                    attempt_count = attempt_count + 1,
                    last_error = $err,
                    updated_at = $now
                WHERE id = $id;
                """;
            cmd.Parameters.AddWithValue("$st", success ? RemoteDeleteStatus.RemoteDeleted.ToString() : RemoteDeleteStatus.RemoteDeleteFailed.ToString());
            cmd.Parameters.AddWithValue("$err", (object?)errorMessage ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("$id", tombstoneId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task ApplyRemoteTombstoneAsync(ArchiveTombstoneRecord remoteTombstone, CancellationToken ct = default)
    {
        if (!remoteTombstone.DeleteRemote || string.IsNullOrWhiteSpace(remoteTombstone.VersionId))
            return;

        await _writeLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

            ArchiveVersionRecord? local = null;
            await using (var sel = conn.CreateCommand())
            {
                sel.Transaction = tx;
                sel.CommandText = """
                    SELECT id, version_num, source_path, archive_path, ready_path, file_name, sha256, norm_hash, size_bytes, problem_code, problem_title, platform, created_at, is_local_available, drive_file_id, is_conflict
                    FROM archive_versions WHERE id = $id;
                    """;
                sel.Parameters.AddWithValue("$id", remoteTombstone.VersionId);
                await using var r = await sel.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct))
                    local = ReadVersion(r);
            }

            // Conflict guard: never erase a newer local version edited/restored AFTER the remote deletion timestamp
            if (local is not null && local.CreatedAtUnixMs <= remoteTombstone.DeletedAtUnixMs)
            {
                await using var upd = conn.CreateCommand();
                upd.Transaction = tx;
                upd.CommandText = "UPDATE archive_versions SET is_deleted = 1, deleted_at = $dt, delete_scope = 'RemoteTombstone', deleted_origin_device = $dev WHERE id = $id AND is_deleted = 0;";
                upd.Parameters.AddWithValue("$dt", remoteTombstone.DeletedAtUnixMs);
                upd.Parameters.AddWithValue("$dev", remoteTombstone.DeviceName);
                upd.Parameters.AddWithValue("$id", local.Id);
                await upd.ExecuteNonQueryAsync(ct);
            }

            await UpsertTombstoneInTxAsync(conn, tx, remoteTombstone with { RemoteDeleteState = RemoteDeleteStatus.RemoteDeleted }, ct);
            await tx.CommitAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static async Task<bool> IsRemoteVersionBlockedByTombstoneInTxAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        ArchiveVersionRecord remoteVer,
        CancellationToken ct)
    {
        await using (var verCmd = conn.CreateCommand())
        {
            verCmd.Transaction = tx;
            verCmd.CommandText = """
                SELECT COUNT(*)
                FROM archive_tombstones
                WHERE entity_type = 'Version'
                  AND version_id = $vid
                  AND remote_delete_state != 'ConflictRestored';
                """;
            verCmd.Parameters.AddWithValue("$vid", remoteVer.Id);
            if (Convert.ToInt64(await verCmd.ExecuteScalarAsync(ct)) > 0)
                return true;
        }

        if (!string.IsNullOrWhiteSpace(remoteVer.ProblemCode))
        {
            var pk = ProblemStatementRecord.BuildKey(remoteVer.Platform, remoteVer.ProblemCode).ToUpperInvariant();
            await using var probCmd = conn.CreateCommand();
            probCmd.Transaction = tx;
            probCmd.CommandText = """
                SELECT MAX(deleted_at)
                FROM archive_tombstones
                WHERE entity_type = 'Problem'
                  AND UPPER(problem_key) = $pk
                  AND remote_delete_state != 'ConflictRestored';
                """;
            probCmd.Parameters.AddWithValue("$pk", pk);
            var obj = await probCmd.ExecuteScalarAsync(ct);
            if (obj is not null && obj != DBNull.Value)
            {
                long probDeletedAt = Convert.ToInt64(obj);
                if (remoteVer.CreatedAtUnixMs <= probDeletedAt)
                    return true;
            }
        }

        return false;
    }

    private static async Task ReconcileReadyCopyAfterDeleteInTxAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        string readyPath,
        string? archiveRootDir,
        CancellationToken ct)
    {
        // Only manage copies inside GhostCode's own archive directory; never touch user working folders
        if (!string.IsNullOrWhiteSpace(archiveRootDir))
        {
            var fullReady = Path.GetFullPath(readyPath);
            var fullRoot = Path.GetFullPath(archiveRootDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!fullReady.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                return;
        }

        await using var nextCmd = conn.CreateCommand();
        nextCmd.Transaction = tx;
        nextCmd.CommandText = """
            SELECT archive_path, is_local_available
            FROM archive_versions
            WHERE ready_path = $rp COLLATE NOCASE AND is_deleted = 0
            ORDER BY version_num DESC, created_at DESC
            LIMIT 1;
            """;
        nextCmd.Parameters.AddWithValue("$rp", readyPath);
        await using var r = await nextCmd.ExecuteReaderAsync(ct);
        if (await r.ReadAsync(ct))
        {
            var nextArchivePath = r.GetString(0);
            bool nextLocal = r.GetInt64(1) != 0;
            if (nextLocal && File.Exists(nextArchivePath))
            {
                var dir = Path.GetDirectoryName(readyPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.Copy(nextArchivePath, readyPath, overwrite: true);
            }
        }
        else if (File.Exists(readyPath))
        {
            try { File.Delete(readyPath); } catch { }
        }
    }

    private static async Task UpsertTombstoneInTxAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        ArchiveTombstoneRecord t,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO archive_tombstones (
                id, entity_type, version_id, problem_key, source_path, platform, problem_code,
                content_sha256, content_norm_hash, drive_file_id, delete_remote, remote_delete_state,
                attempt_count, last_error, deleted_at, updated_at, device_name, expires_at)
            VALUES (
                $id, $et, $vid, $pk, $sp, $plat, $code,
                $sha, $nh, $did, $dr, $rds,
                $att, $err, $dt, $ut, $dev, $exp)
            ON CONFLICT(id) DO UPDATE SET
                drive_file_id = COALESCE(excluded.drive_file_id, archive_tombstones.drive_file_id),
                delete_remote = MAX(archive_tombstones.delete_remote, excluded.delete_remote),
                remote_delete_state = excluded.remote_delete_state,
                updated_at = excluded.updated_at,
                expires_at = MAX(archive_tombstones.expires_at, excluded.expires_at);
            """;
        cmd.Parameters.AddWithValue("$id", t.Id);
        cmd.Parameters.AddWithValue("$et", t.EntityType);
        cmd.Parameters.AddWithValue("$vid", (object?)t.VersionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$pk", (object?)t.ProblemKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sp", (object?)t.SourcePath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$plat", (object?)t.Platform ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$code", (object?)t.ProblemCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sha", (object?)t.ContentSha256 ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$nh", (object?)t.ContentNormHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$did", (object?)t.DriveFileId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$dr", t.DeleteRemote ? 1 : 0);
        cmd.Parameters.AddWithValue("$rds", t.RemoteDeleteState.ToString());
        cmd.Parameters.AddWithValue("$att", t.AttemptCount);
        cmd.Parameters.AddWithValue("$err", (object?)t.LastError ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$dt", t.DeletedAtUnixMs);
        cmd.Parameters.AddWithValue("$ut", t.UpdatedAtUnixMs);
        cmd.Parameters.AddWithValue("$dev", t.DeviceName);
        cmd.Parameters.AddWithValue("$exp", t.ExpiresAtUnixMs);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<List<ArchiveTombstoneRecord>> ReadTombstonesAsync(SqliteCommand cmd, CancellationToken ct)
    {
        var list = new List<ArchiveTombstoneRecord>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var rState = Enum.TryParse<RemoteDeleteStatus>(r.GetString(11), true, out var parsed)
                ? parsed
                : RemoteDeleteStatus.LocalOnly;
            list.Add(new ArchiveTombstoneRecord(
                r.GetString(0),
                r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5),
                r.IsDBNull(6) ? null : r.GetString(6),
                r.IsDBNull(7) ? null : r.GetString(7),
                r.IsDBNull(8) ? null : r.GetString(8),
                r.IsDBNull(9) ? null : r.GetString(9),
                r.GetInt64(10) != 0,
                rState,
                r.GetInt32(12),
                r.IsDBNull(13) ? null : r.GetString(13),
                r.GetInt64(14),
                r.GetInt64(15),
                r.GetString(16),
                r.GetInt64(17)));
        }
        return list;
    }
}
