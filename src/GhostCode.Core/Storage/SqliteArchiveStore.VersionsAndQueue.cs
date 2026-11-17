using GhostCode.Core.Models;
using Microsoft.Data.Sqlite;

namespace GhostCode.Core.Storage;

public sealed partial class SqliteArchiveStore
{
    public async Task<FileSnapshotRecord?> GetSnapshotAsync(string sourcePath, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT source_path, sha256, norm_hash, raw_bytes, encoding_name, problem_code, problem_title, platform, updated_at
            FROM file_snapshots WHERE source_path = $p COLLATE NOCASE;
            """;
        cmd.Parameters.AddWithValue("$p", sourcePath);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        return new FileSnapshotRecord(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            (byte[])reader[3],
            reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.GetString(7),
            reader.GetInt64(8));
    }

    public async Task<ArchiveVersionRecord?> GetLatestVersionForSourceAsync(string sourcePath, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, version_num, source_path, archive_path, ready_path, file_name, sha256, norm_hash, size_bytes, problem_code, problem_title, platform, created_at, is_local_available, drive_file_id, is_conflict
            FROM archive_versions
            WHERE source_path = $p COLLATE NOCASE AND is_deleted = 0
            ORDER BY version_num DESC LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$p", sourcePath);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadVersion(reader) : null;
    }

    public async Task<ArchiveVersionRecord?> GetLatestVersionForProblemAsync(
        string platform,
        string problemCode,
        CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, version_num, source_path, archive_path, ready_path, file_name, sha256, norm_hash, size_bytes, problem_code, problem_title, platform, created_at, is_local_available, drive_file_id, is_conflict
            FROM archive_versions
            WHERE platform = $plat COLLATE NOCASE AND problem_code = $code COLLATE NOCASE AND is_deleted = 0
            ORDER BY version_num DESC, created_at DESC LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$plat", platform);
        cmd.Parameters.AddWithValue("$code", problemCode);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadVersion(reader) : null;
    }

    public async Task<ArchiveVersionRecord?> GetLatestOfflineVersionForSourceAsync(
        string sourcePath,
        CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, version_num, source_path, archive_path, ready_path, file_name, sha256, norm_hash, size_bytes, problem_code, problem_title, platform, created_at, is_local_available, drive_file_id, is_conflict
            FROM archive_versions
            WHERE source_path = $p COLLATE NOCASE
              AND (problem_code IS NULL OR problem_code = '' OR platform = 'Offline' COLLATE NOCASE)
              AND is_deleted = 0
            ORDER BY version_num DESC, created_at DESC LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$p", sourcePath);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadVersion(reader) : null;
    }

    public async Task<List<ArchiveVersionRecord>> GetVersionsByProblemAsync(
        string platform,
        string problemCode,
        CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, version_num, source_path, archive_path, ready_path, file_name, sha256, norm_hash, size_bytes, problem_code, problem_title, platform, created_at, is_local_available, drive_file_id, is_conflict
            FROM archive_versions
            WHERE platform = $plat COLLATE NOCASE AND problem_code = $code COLLATE NOCASE AND is_deleted = 0
            ORDER BY version_num DESC, created_at DESC;
            """;
        cmd.Parameters.AddWithValue("$plat", platform);
        cmd.Parameters.AddWithValue("$code", problemCode);
        var list = new List<ArchiveVersionRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(ReadVersion(reader));
        return list;
    }

    public async Task<List<ArchiveVersionRecord>> GetOfflineVersionsBySourceAsync(
        string sourcePath,
        CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, version_num, source_path, archive_path, ready_path, file_name, sha256, norm_hash, size_bytes, problem_code, problem_title, platform, created_at, is_local_available, drive_file_id, is_conflict
            FROM archive_versions
            WHERE source_path = $p COLLATE NOCASE
              AND (problem_code IS NULL OR problem_code = '' OR platform = 'Offline' COLLATE NOCASE)
              AND is_deleted = 0
            ORDER BY version_num DESC, created_at DESC;
            """;
        cmd.Parameters.AddWithValue("$p", sourcePath);
        var list = new List<ArchiveVersionRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(ReadVersion(reader));
        return list;
    }

    public async Task<int> ApplyMigratedVersionsAsync(
        IReadOnlyList<(string VersionId, string NewArchivePath, string? NewReadyPath, string NewFileName, string SourcePath)> updates,
        CancellationToken ct = default)
    {
        if (updates.Count == 0)
            return 0;

        await _writeLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
            int updated = 0;

            foreach (var u in updates)
            {
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    UPDATE archive_versions
                    SET archive_path = $ap,
                        ready_path = $rp,
                        file_name = $fn
                    WHERE id = $id;
                    """;
                cmd.Parameters.AddWithValue("$ap", u.NewArchivePath);
                cmd.Parameters.AddWithValue("$rp", (object?)u.NewReadyPath ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$fn", u.NewFileName);
                cmd.Parameters.AddWithValue("$id", u.VersionId);
                updated += await cmd.ExecuteNonQueryAsync(ct);
            }

            await InsertHistoryEventInTxAsync(conn, tx, new ArchiveHistoryEvent(
                Guid.NewGuid().ToString("N"),
                "ArchiveMigrated",
                updates[0].SourcePath,
                updates[0].VersionId,
                $"Migrated {updated} archive version(s) to unified GhostCode folder cleanly",
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Environment.MachineName), ct);

            await tx.CommitAsync(ct);
            return updated;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task InsertVersionAndEnqueueAsync(
        FileSnapshotRecord snapshot,
        ArchiveVersionRecord version,
        UploadQueueItem queueItem,
        CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

            await using (var snapCmd = conn.CreateCommand())
            {
                snapCmd.Transaction = tx;
                snapCmd.CommandText = """
                    INSERT INTO file_snapshots (source_path, sha256, norm_hash, raw_bytes, encoding_name, problem_code, problem_title, platform, updated_at)
                    VALUES ($sp, $sha, $nh, $bytes, $enc, $code, $title, $plat, $ts)
                    ON CONFLICT(source_path) DO UPDATE SET
                        sha256 = excluded.sha256,
                        norm_hash = excluded.norm_hash,
                        raw_bytes = excluded.raw_bytes,
                        encoding_name = excluded.encoding_name,
                        problem_code = excluded.problem_code,
                        problem_title = excluded.problem_title,
                        platform = excluded.platform,
                        updated_at = excluded.updated_at;
                    """;
                snapCmd.Parameters.AddWithValue("$sp", snapshot.SourcePath);
                snapCmd.Parameters.AddWithValue("$sha", snapshot.Sha256);
                snapCmd.Parameters.AddWithValue("$nh", snapshot.NormHash);
                snapCmd.Parameters.AddWithValue("$bytes", snapshot.RawBytes);
                snapCmd.Parameters.AddWithValue("$enc", snapshot.EncodingName);
                snapCmd.Parameters.AddWithValue("$code", (object?)snapshot.ProblemCode ?? DBNull.Value);
                snapCmd.Parameters.AddWithValue("$title", (object?)snapshot.ProblemTitle ?? DBNull.Value);
                snapCmd.Parameters.AddWithValue("$plat", snapshot.Platform);
                snapCmd.Parameters.AddWithValue("$ts", snapshot.UpdatedAtUnixMs);
                await snapCmd.ExecuteNonQueryAsync(ct);
            }

            await using (var verCmd = conn.CreateCommand())
            {
                verCmd.Transaction = tx;
                verCmd.CommandText = """
                    INSERT INTO archive_versions (id, version_num, source_path, archive_path, ready_path, file_name, sha256, norm_hash, size_bytes, problem_code, problem_title, platform, created_at, is_local_available, drive_file_id, is_conflict)
                    VALUES ($id, $vn, $sp, $ap, $rp, $fn, $sha, $nh, $sz, $code, $title, $plat, $ts, $loc, $did, $conf);
                    """;
                verCmd.Parameters.AddWithValue("$id", version.Id);
                verCmd.Parameters.AddWithValue("$vn", version.VersionNum);
                verCmd.Parameters.AddWithValue("$sp", version.SourcePath);
                verCmd.Parameters.AddWithValue("$ap", version.ArchivePath);
                verCmd.Parameters.AddWithValue("$rp", (object?)version.ReadyPath ?? DBNull.Value);
                verCmd.Parameters.AddWithValue("$fn", version.FileName);
                verCmd.Parameters.AddWithValue("$sha", version.Sha256);
                verCmd.Parameters.AddWithValue("$nh", version.NormHash);
                verCmd.Parameters.AddWithValue("$sz", version.SizeBytes);
                verCmd.Parameters.AddWithValue("$code", (object?)version.ProblemCode ?? DBNull.Value);
                verCmd.Parameters.AddWithValue("$title", (object?)version.ProblemTitle ?? DBNull.Value);
                verCmd.Parameters.AddWithValue("$plat", version.Platform);
                verCmd.Parameters.AddWithValue("$ts", version.CreatedAtUnixMs);
                verCmd.Parameters.AddWithValue("$loc", version.IsLocalAvailable ? 1 : 0);
                verCmd.Parameters.AddWithValue("$did", (object?)version.DriveFileId ?? DBNull.Value);
                verCmd.Parameters.AddWithValue("$conf", version.IsConflictBranch ? 1 : 0);
                await verCmd.ExecuteNonQueryAsync(ct);
            }

            await using (var qCmd = conn.CreateCommand())
            {
                qCmd.Transaction = tx;
                qCmd.CommandText = """
                    INSERT INTO upload_queue (version_id, status, attempt_count, next_retry_at, drive_file_id, last_error, updated_at)
                    VALUES ($vid, $st, $att, $next, $did, $err, $ts)
                    ON CONFLICT(version_id) DO UPDATE SET
                        status = excluded.status,
                        attempt_count = excluded.attempt_count,
                        next_retry_at = excluded.next_retry_at,
                        drive_file_id = COALESCE(excluded.drive_file_id, upload_queue.drive_file_id),
                        last_error = excluded.last_error,
                        updated_at = excluded.updated_at;
                    """;
                qCmd.Parameters.AddWithValue("$vid", queueItem.VersionId);
                qCmd.Parameters.AddWithValue("$st", queueItem.Status.ToString());
                qCmd.Parameters.AddWithValue("$att", queueItem.AttemptCount);
                qCmd.Parameters.AddWithValue("$next", queueItem.NextRetryAtUnixMs);
                qCmd.Parameters.AddWithValue("$did", (object?)queueItem.DriveFileId ?? DBNull.Value);
                qCmd.Parameters.AddWithValue("$err", (object?)queueItem.LastError ?? DBNull.Value);
                qCmd.Parameters.AddWithValue("$ts", queueItem.UpdatedAtUnixMs);
                await qCmd.ExecuteNonQueryAsync(ct);
            }

            await InsertHistoryEventInTxAsync(conn, tx, new ArchiveHistoryEvent(
                Guid.NewGuid().ToString("N"),
                "VersionCreated",
                version.SourcePath,
                version.Id,
                $"Archived v{version.VersionNum} ({version.FileName})",
                version.CreatedAtUnixMs,
                Environment.MachineName), ct);

            // A new edited save supersedes any prior deletion content-suppression for this source path
            await using (var clrCmd = conn.CreateCommand())
            {
                clrCmd.Transaction = tx;
                clrCmd.CommandText = "DELETE FROM archive_tombstones WHERE entity_type = 'ContentSuppression' AND source_path = $sp COLLATE NOCASE;";
                clrCmd.Parameters.AddWithValue("$sp", version.SourcePath);
                await clrCmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<List<ArchiveVersionRecord>> GetVersionsBySourceAsync(string sourcePath, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, version_num, source_path, archive_path, ready_path, file_name, sha256, norm_hash, size_bytes, problem_code, problem_title, platform, created_at, is_local_available, drive_file_id, is_conflict
            FROM archive_versions
            WHERE source_path = $p COLLATE NOCASE AND is_deleted = 0
            ORDER BY version_num DESC;
            """;
        cmd.Parameters.AddWithValue("$p", sourcePath);
        var list = new List<ArchiveVersionRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(ReadVersion(reader));
        return list;
    }

    public async Task<ArchiveVersionRecord?> GetVersionByIdAsync(string versionId, CancellationToken ct = default) =>
        await GetVersionByIdAsync(versionId, includeDeleted: false, ct);

    public async Task<ArchiveVersionRecord?> GetVersionByIdAsync(string versionId, bool includeDeleted, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = includeDeleted
            ? """
              SELECT id, version_num, source_path, archive_path, ready_path, file_name, sha256, norm_hash, size_bytes, problem_code, problem_title, platform, created_at, is_local_available, drive_file_id, is_conflict
              FROM archive_versions WHERE id = $id;
              """
            : """
              SELECT id, version_num, source_path, archive_path, ready_path, file_name, sha256, norm_hash, size_bytes, problem_code, problem_title, platform, created_at, is_local_available, drive_file_id, is_conflict
              FROM archive_versions WHERE id = $id AND is_deleted = 0;
              """;
        cmd.Parameters.AddWithValue("$id", versionId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadVersion(reader) : null;
    }

    public async Task<ArchiveVersionRecord?> GetVersionByArchivePathAsync(string archivePath, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, version_num, source_path, archive_path, ready_path, file_name, sha256, norm_hash, size_bytes, problem_code, problem_title, platform, created_at, is_local_available, drive_file_id, is_conflict
            FROM archive_versions WHERE archive_path = $ap COLLATE NOCASE AND is_deleted = 0 LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$ap", archivePath);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadVersion(reader) : null;
    }

    public async Task InsertRecoveredVersionAsync(
        ArchiveVersionRecord version,
        UploadQueueItem queueItem,
        CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

            await using (var verCmd = conn.CreateCommand())
            {
                verCmd.Transaction = tx;
                verCmd.CommandText = """
                    INSERT OR IGNORE INTO archive_versions (id, version_num, source_path, archive_path, ready_path, file_name, sha256, norm_hash, size_bytes, problem_code, problem_title, platform, created_at, is_local_available, drive_file_id, is_conflict)
                    VALUES ($id, $vn, $sp, $ap, $rp, $fn, $sha, $nh, $sz, $code, $title, $plat, $ts, $loc, $did, $conf);
                    """;
                verCmd.Parameters.AddWithValue("$id", version.Id);
                verCmd.Parameters.AddWithValue("$vn", version.VersionNum);
                verCmd.Parameters.AddWithValue("$sp", version.SourcePath);
                verCmd.Parameters.AddWithValue("$ap", version.ArchivePath);
                verCmd.Parameters.AddWithValue("$rp", (object?)version.ReadyPath ?? DBNull.Value);
                verCmd.Parameters.AddWithValue("$fn", version.FileName);
                verCmd.Parameters.AddWithValue("$sha", version.Sha256);
                verCmd.Parameters.AddWithValue("$nh", version.NormHash);
                verCmd.Parameters.AddWithValue("$sz", version.SizeBytes);
                verCmd.Parameters.AddWithValue("$code", (object?)version.ProblemCode ?? DBNull.Value);
                verCmd.Parameters.AddWithValue("$title", (object?)version.ProblemTitle ?? DBNull.Value);
                verCmd.Parameters.AddWithValue("$plat", version.Platform);
                verCmd.Parameters.AddWithValue("$ts", version.CreatedAtUnixMs);
                verCmd.Parameters.AddWithValue("$loc", version.IsLocalAvailable ? 1 : 0);
                verCmd.Parameters.AddWithValue("$did", (object?)version.DriveFileId ?? DBNull.Value);
                verCmd.Parameters.AddWithValue("$conf", version.IsConflictBranch ? 1 : 0);
                await verCmd.ExecuteNonQueryAsync(ct);
            }

            await using (var qCmd = conn.CreateCommand())
            {
                qCmd.Transaction = tx;
                qCmd.CommandText = """
                    INSERT OR IGNORE INTO upload_queue (version_id, status, attempt_count, next_retry_at, drive_file_id, last_error, updated_at)
                    VALUES ($vid, $st, $att, $next, $did, $err, $ts);
                    """;
                qCmd.Parameters.AddWithValue("$vid", queueItem.VersionId);
                qCmd.Parameters.AddWithValue("$st", queueItem.Status.ToString());
                qCmd.Parameters.AddWithValue("$att", queueItem.AttemptCount);
                qCmd.Parameters.AddWithValue("$next", queueItem.NextRetryAtUnixMs);
                qCmd.Parameters.AddWithValue("$did", (object?)queueItem.DriveFileId ?? DBNull.Value);
                qCmd.Parameters.AddWithValue("$err", (object?)queueItem.LastError ?? DBNull.Value);
                qCmd.Parameters.AddWithValue("$ts", queueItem.UpdatedAtUnixMs);
                await qCmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task DeleteVersionAsync(string versionId, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

            string? sourcePath = null;
            await using (var selCmd = conn.CreateCommand())
            {
                selCmd.Transaction = tx;
                selCmd.CommandText = "SELECT source_path FROM archive_versions WHERE id = $id;";
                selCmd.Parameters.AddWithValue("$id", versionId);
                sourcePath = Convert.ToString(await selCmd.ExecuteScalarAsync(ct));
            }

            await using (var delQueue = conn.CreateCommand())
            {
                delQueue.Transaction = tx;
                delQueue.CommandText = "DELETE FROM upload_queue WHERE version_id = $id;";
                delQueue.Parameters.AddWithValue("$id", versionId);
                await delQueue.ExecuteNonQueryAsync(ct);
            }

            await using (var delVer = conn.CreateCommand())
            {
                delVer.Transaction = tx;
                delVer.CommandText = "DELETE FROM archive_versions WHERE id = $id;";
                delVer.Parameters.AddWithValue("$id", versionId);
                await delVer.ExecuteNonQueryAsync(ct);
            }

            if (!string.IsNullOrEmpty(sourcePath))
            {
                await using var checkCmd = conn.CreateCommand();
                checkCmd.Transaction = tx;
                checkCmd.CommandText = "SELECT COUNT(*) FROM archive_versions WHERE source_path = $sp COLLATE NOCASE;";
                checkCmd.Parameters.AddWithValue("$sp", sourcePath);
                var remaining = Convert.ToInt64(await checkCmd.ExecuteScalarAsync(ct));
                if (remaining == 0)
                {
                    await using var delSnap = conn.CreateCommand();
                    delSnap.Transaction = tx;
                    delSnap.CommandText = "DELETE FROM file_snapshots WHERE source_path = $sp COLLATE NOCASE;";
                    delSnap.Parameters.AddWithValue("$sp", sourcePath);
                    await delSnap.ExecuteNonQueryAsync(ct);
                }
            }

            await tx.CommitAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<List<ArchiveVersionRecord>> QueryVersionsAsync(
        string? search = null,
        string? platformFilter = null,
        int limit = 200,
        CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, version_num, source_path, archive_path, ready_path, file_name, sha256, norm_hash, size_bytes, problem_code, problem_title, platform, created_at, is_local_available, drive_file_id, is_conflict
            FROM archive_versions
            WHERE is_deleted = 0
              AND ($plat IS NULL OR $plat = '' OR $plat = 'All' OR platform = $plat)
              AND ($q IS NULL OR $q = '' OR file_name LIKE '%' || $q || '%' OR source_path LIKE '%' || $q || '%' OR IFNULL(problem_code,'') LIKE '%' || $q || '%' OR IFNULL(problem_title,'') LIKE '%' || $q || '%')
            ORDER BY created_at DESC, version_num DESC
            LIMIT $lim;
            """;
        cmd.Parameters.AddWithValue("$plat", (object?)platformFilter ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$q", (object?)search ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$lim", limit);
        var list = new List<ArchiveVersionRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(ReadVersion(reader));
        return list;
    }

    public async Task<List<UploadQueueView>> GetPendingOrRetryableUploadsAsync(long nowUnixMs, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT q.version_id, q.status, q.attempt_count, q.next_retry_at, q.drive_file_id, q.last_error, q.updated_at,
                   v.id, v.version_num, v.source_path, v.archive_path, v.ready_path, v.file_name, v.sha256, v.norm_hash, v.size_bytes, v.problem_code, v.problem_title, v.platform, v.created_at, v.is_local_available, v.drive_file_id, v.is_conflict
            FROM upload_queue q
            JOIN archive_versions v ON v.id = q.version_id
            WHERE (q.status = 'Pending' OR (q.status = 'Failed' AND q.next_retry_at <= $now))
              AND v.is_local_available = 1
              AND v.is_deleted = 0
            ORDER BY v.created_at ASC;
            """;
        cmd.Parameters.AddWithValue("$now", nowUnixMs);
        return await ReadQueueViewsAsync(cmd, ct);
    }

    public async Task<List<UploadQueueView>> GetAllUploadQueueAsync(int limit = 100, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT q.version_id, q.status, q.attempt_count, q.next_retry_at, q.drive_file_id, q.last_error, q.updated_at,
                   v.id, v.version_num, v.source_path, v.archive_path, v.ready_path, v.file_name, v.sha256, v.norm_hash, v.size_bytes, v.problem_code, v.problem_title, v.platform, v.created_at, v.is_local_available, v.drive_file_id, v.is_conflict
            FROM upload_queue q
            JOIN archive_versions v ON v.id = q.version_id
            WHERE v.is_deleted = 0
            ORDER BY q.updated_at DESC
            LIMIT $lim;
            """;
        cmd.Parameters.AddWithValue("$lim", limit);
        return await ReadQueueViewsAsync(cmd, ct);
    }

    public async Task MarkUploadAttemptAsync(
        string versionId,
        UploadStatus status,
        string? driveFileId,
        string? error,
        long nextRetryAtUnixMs,
        CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

            await using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    UPDATE upload_queue
                    SET status = $st,
                        attempt_count = CASE WHEN $st = 'Failed' THEN attempt_count + 1 ELSE attempt_count END,
                        next_retry_at = $next,
                        drive_file_id = COALESCE($did, drive_file_id),
                        last_error = $err,
                        updated_at = $ts
                    WHERE version_id = $vid;
                    """;
                cmd.Parameters.AddWithValue("$st", status.ToString());
                cmd.Parameters.AddWithValue("$next", nextRetryAtUnixMs);
                cmd.Parameters.AddWithValue("$did", (object?)driveFileId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$err", (object?)error ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                cmd.Parameters.AddWithValue("$vid", versionId);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            if (!string.IsNullOrEmpty(driveFileId))
            {
                await using var vCmd = conn.CreateCommand();
                vCmd.Transaction = tx;
                vCmd.CommandText = "UPDATE archive_versions SET drive_file_id = COALESCE($did, drive_file_id) WHERE id = $vid;";
                vCmd.Parameters.AddWithValue("$did", driveFileId);
                vCmd.Parameters.AddWithValue("$vid", versionId);
                await vCmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task ResetFailedUploadsForRetryAsync(CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE upload_queue
                SET status = 'Pending', next_retry_at = 0, last_error = NULL, updated_at = $ts
                WHERE status = 'Failed' OR status = 'Uploading';
                """;
            cmd.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task RecordArchiveHistoryEventAsync(ArchiveHistoryEvent ev, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
            await InsertHistoryEventInTxAsync(conn, tx, ev, ct);
            await tx.CommitAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<List<ArchiveHistoryEvent>> GetArchiveHistoryEventsAsync(int limit = 200, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, event_type, source_path, version_id, summary, occurred_at, device_name
            FROM archive_history_events
            ORDER BY occurred_at DESC
            LIMIT $lim;
            """;
        cmd.Parameters.AddWithValue("$lim", limit);
        var list = new List<ArchiveHistoryEvent>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new ArchiveHistoryEvent(
                r.GetString(0),
                r.GetString(1),
                r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3),
                r.GetString(4),
                r.GetInt64(5),
                r.GetString(6)));
        }
        return list;
    }

    public async Task UpsertRemoteVersionMetadataAsync(ArchiveVersionRecord remoteVer, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

            // 1. Exact ID or (source_path + sha256) match -> link DriveFileId without duplicating or un-deleting
            string? existingId = null;
            await using (var findCmd = conn.CreateCommand())
            {
                findCmd.Transaction = tx;
                findCmd.CommandText = """
                    SELECT id FROM archive_versions
                    WHERE id = $id OR (sha256 = $sha AND (source_path = $sp COLLATE NOCASE OR file_name = $fn COLLATE NOCASE))
                    LIMIT 1;
                    """;
                findCmd.Parameters.AddWithValue("$id", remoteVer.Id);
                findCmd.Parameters.AddWithValue("$sha", remoteVer.Sha256);
                findCmd.Parameters.AddWithValue("$sp", remoteVer.SourcePath);
                findCmd.Parameters.AddWithValue("$fn", remoteVer.FileName);
                existingId = Convert.ToString(await findCmd.ExecuteScalarAsync(ct));
            }

            if (!string.IsNullOrEmpty(existingId))
            {
                await using var upd = conn.CreateCommand();
                upd.Transaction = tx;
                upd.CommandText = "UPDATE archive_versions SET drive_file_id = COALESCE($did, drive_file_id) WHERE id = $id;";
                upd.Parameters.AddWithValue("$did", (object?)remoteVer.DriveFileId ?? DBNull.Value);
                upd.Parameters.AddWithValue("$id", existingId);
                await upd.ExecuteNonQueryAsync(ct);
                await tx.CommitAsync(ct);
                return;
            }

            // 1b. Deletion tombstone check: never resurrect locally or remotely deleted versions
            //     (only genuinely newer edits created after a problem-level tombstone on another device are imported)
            if (await IsRemoteVersionBlockedByTombstoneInTxAsync(conn, tx, remoteVer, ct))
            {
                await tx.CommitAsync(ct);
                return;
            }

            // 2. Two-device offline edit conflict detection on same (source_path, version_num) with different SHA-256
            int versionNum = remoteVer.VersionNum;
            bool isConflict = false;
            await using (var confCmd = conn.CreateCommand())
            {
                confCmd.Transaction = tx;
                confCmd.CommandText = "SELECT COUNT(*) FROM archive_versions WHERE source_path = $sp COLLATE NOCASE AND version_num = $vn;";
                confCmd.Parameters.AddWithValue("$sp", remoteVer.SourcePath);
                confCmd.Parameters.AddWithValue("$vn", versionNum);
                if (Convert.ToInt64(await confCmd.ExecuteScalarAsync(ct)) > 0)
                {
                    isConflict = true;
                    await using var maxCmd = conn.CreateCommand();
                    maxCmd.Transaction = tx;
                    maxCmd.CommandText = "SELECT COALESCE(MAX(version_num), 0) FROM archive_versions WHERE source_path = $sp COLLATE NOCASE;";
                    maxCmd.Parameters.AddWithValue("$sp", remoteVer.SourcePath);
                    versionNum = Convert.ToInt32(await maxCmd.ExecuteScalarAsync(ct)) + 1;
                }
            }

            var cloudPath = $"cloud://{remoteVer.DriveFileId ?? remoteVer.Id}";
            await using (var insCmd = conn.CreateCommand())
            {
                insCmd.Transaction = tx;
                insCmd.CommandText = """
                    INSERT OR IGNORE INTO archive_versions (id, version_num, source_path, archive_path, ready_path, file_name, sha256, norm_hash, size_bytes, problem_code, problem_title, platform, created_at, is_local_available, drive_file_id, is_conflict)
                    VALUES ($id, $vn, $sp, $ap, NULL, $fn, $sha, $nh, $sz, $code, $title, $plat, $ts, 0, $did, $conf);
                    """;
                insCmd.Parameters.AddWithValue("$id", remoteVer.Id);
                insCmd.Parameters.AddWithValue("$vn", versionNum);
                insCmd.Parameters.AddWithValue("$sp", remoteVer.SourcePath);
                insCmd.Parameters.AddWithValue("$ap", cloudPath);
                insCmd.Parameters.AddWithValue("$fn", remoteVer.FileName);
                insCmd.Parameters.AddWithValue("$sha", remoteVer.Sha256);
                insCmd.Parameters.AddWithValue("$nh", remoteVer.NormHash);
                insCmd.Parameters.AddWithValue("$sz", remoteVer.SizeBytes);
                insCmd.Parameters.AddWithValue("$code", (object?)remoteVer.ProblemCode ?? DBNull.Value);
                insCmd.Parameters.AddWithValue("$title", (object?)remoteVer.ProblemTitle ?? DBNull.Value);
                insCmd.Parameters.AddWithValue("$plat", remoteVer.Platform);
                insCmd.Parameters.AddWithValue("$ts", remoteVer.CreatedAtUnixMs);
                insCmd.Parameters.AddWithValue("$did", (object?)remoteVer.DriveFileId ?? DBNull.Value);
                insCmd.Parameters.AddWithValue("$conf", isConflict ? 1 : 0);
                await insCmd.ExecuteNonQueryAsync(ct);
            }

            await InsertHistoryEventInTxAsync(conn, tx, new ArchiveHistoryEvent(
                Guid.NewGuid().ToString("N"),
                isConflict ? "ConflictDetected" : "RemoteVersionDiscovered",
                remoteVer.SourcePath,
                remoteVer.Id,
                isConflict
                    ? $"Preserved divergent cloud version v{remoteVer.VersionNum} as conflict branch v{versionNum} ({remoteVer.FileName})"
                    : $"Discovered remote cloud version v{versionNum} ({remoteVer.FileName})",
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Environment.MachineName), ct);

            await tx.CommitAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task MarkVersionDownloadedLocallyAsync(string versionId, string localArchivePath, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

            await using (var upd = conn.CreateCommand())
            {
                upd.Transaction = tx;
                upd.CommandText = "UPDATE archive_versions SET is_local_available = 1, archive_path = $ap WHERE id = $id;";
                upd.Parameters.AddWithValue("$ap", Path.GetFullPath(localArchivePath));
                upd.Parameters.AddWithValue("$id", versionId);
                await upd.ExecuteNonQueryAsync(ct);
            }

            await InsertHistoryEventInTxAsync(conn, tx, new ArchiveHistoryEvent(
                Guid.NewGuid().ToString("N"),
                "VersionDownloaded",
                localArchivePath,
                versionId,
                $"Downloaded verified cloud archive copy to {Path.GetFileName(localArchivePath)}",
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Environment.MachineName), ct);

            await tx.CommitAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task SetVersionRemoteFileIdAsync(string versionId, string remoteFileId, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE archive_versions SET drive_file_id = $did WHERE id = $vid;";
            cmd.Parameters.AddWithValue("$did", remoteFileId);
            cmd.Parameters.AddWithValue("$vid", versionId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static async Task InsertHistoryEventInTxAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        ArchiveHistoryEvent ev,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR IGNORE INTO archive_history_events (id, event_type, source_path, version_id, summary, occurred_at, device_name)
            VALUES ($id, $et, $sp, $vid, $sum, $ts, $dev);
            """;
        cmd.Parameters.AddWithValue("$id", ev.Id);
        cmd.Parameters.AddWithValue("$et", ev.EventType);
        cmd.Parameters.AddWithValue("$sp", ev.SourcePath);
        cmd.Parameters.AddWithValue("$vid", (object?)ev.VersionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sum", ev.Summary);
        cmd.Parameters.AddWithValue("$ts", ev.OccurredAtUnixMs);
        cmd.Parameters.AddWithValue("$dev", ev.DeviceName);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static ArchiveVersionRecord ReadVersion(SqliteDataReader r, int offset = 0) =>
        new(
            r.GetString(offset + 0),
            r.GetInt32(offset + 1),
            r.GetString(offset + 2),
            r.GetString(offset + 3),
            r.IsDBNull(offset + 4) ? null : r.GetString(offset + 4),
            r.GetString(offset + 5),
            r.GetString(offset + 6),
            r.GetString(offset + 7),
            r.GetInt64(offset + 8),
            r.IsDBNull(offset + 9) ? null : r.GetString(offset + 9),
            r.IsDBNull(offset + 10) ? null : r.GetString(offset + 10),
            r.GetString(offset + 11),
            r.GetInt64(offset + 12),
            r.GetInt64(offset + 13) != 0,
            r.IsDBNull(offset + 14) ? null : r.GetString(offset + 14),
            r.GetInt64(offset + 15) != 0);

    private static async Task<List<UploadQueueView>> ReadQueueViewsAsync(SqliteCommand cmd, CancellationToken ct)
    {
        var list = new List<UploadQueueView>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var status = Enum.TryParse<UploadStatus>(r.GetString(1), out var parsed) ? parsed : UploadStatus.Pending;
            var q = new UploadQueueItem(
                r.GetString(0),
                status,
                r.GetInt32(2),
                r.GetInt64(3),
                r.IsDBNull(4) ? null : r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5),
                r.GetInt64(6));
            var v = ReadVersion(r, 7);
            list.Add(new UploadQueueView(q, v));
        }
        return list;
    }
}
