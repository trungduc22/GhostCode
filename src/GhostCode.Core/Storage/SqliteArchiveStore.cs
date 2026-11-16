using GhostCode.Core.Models;
using Microsoft.Data.Sqlite;

namespace GhostCode.Core.Storage;

/// <summary>
/// Thread-safe, transactional SQLite WAL store for Last-Known-Good snapshots,
/// immutable historical backup versions, upload queue, and monitoring events.
/// </summary>
public sealed class SqliteArchiveStore : IAsyncDisposable
{
    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public SqliteArchiveStore(string dbPath)
    {
        _dbPath = dbPath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();
    }

    public string DatabasePath => _dbPath;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            var dir = Path.GetDirectoryName(_dbPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            try
            {
                await CreateSchemaAndVerifyAsync(ct);
            }
            catch (SqliteException)
            {
                BackupCorruptedDatabase();
                await CreateSchemaAndVerifyAsync(ct);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void BackupCorruptedDatabase()
    {
        SqliteConnection.ClearAllPools();
        var stamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var path = _dbPath + suffix;
            if (File.Exists(path))
            {
                try
                {
                    File.Move(path, $"{path}.corrupt.{stamp}.bak", overwrite: true);
                }
                catch { }
            }
        }
    }

    private async Task CreateSchemaAndVerifyAsync(CancellationToken ct)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using (var checkCmd = conn.CreateCommand())
        {
            checkCmd.CommandText = "PRAGMA quick_check;";
            var checkResult = Convert.ToString(await checkCmd.ExecuteScalarAsync(ct));
            if (!string.Equals(checkResult, "ok", StringComparison.OrdinalIgnoreCase))
                throw new SqliteException($"Integrity check failed: {checkResult}", 11);
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA foreign_keys=ON;

            CREATE TABLE IF NOT EXISTS file_snapshots (
                source_path TEXT PRIMARY KEY,
                sha256 TEXT NOT NULL,
                norm_hash TEXT NOT NULL,
                raw_bytes BLOB NOT NULL,
                encoding_name TEXT NOT NULL,
                problem_code TEXT,
                problem_title TEXT,
                platform TEXT NOT NULL,
                updated_at INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS archive_versions (
                id TEXT PRIMARY KEY,
                version_num INTEGER NOT NULL,
                source_path TEXT NOT NULL,
                archive_path TEXT NOT NULL,
                ready_path TEXT,
                file_name TEXT NOT NULL,
                sha256 TEXT NOT NULL,
                norm_hash TEXT NOT NULL,
                size_bytes INTEGER NOT NULL,
                problem_code TEXT,
                problem_title TEXT,
                platform TEXT NOT NULL,
                created_at INTEGER NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_versions_source ON archive_versions(source_path, version_num DESC);
            CREATE INDEX IF NOT EXISTS idx_versions_created ON archive_versions(created_at DESC);

            CREATE TABLE IF NOT EXISTS upload_queue (
                version_id TEXT PRIMARY KEY,
                status TEXT NOT NULL,
                attempt_count INTEGER NOT NULL,
                next_retry_at INTEGER NOT NULL,
                drive_file_id TEXT,
                last_error TEXT,
                updated_at INTEGER NOT NULL,
                FOREIGN KEY(version_id) REFERENCES archive_versions(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS monitor_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                folder_path TEXT NOT NULL,
                file_path TEXT NOT NULL,
                event_type TEXT NOT NULL,
                message TEXT NOT NULL,
                occurred_at INTEGER NOT NULL
            );
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

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
            SELECT id, version_num, source_path, archive_path, ready_path, file_name, sha256, norm_hash, size_bytes, problem_code, problem_title, platform, created_at
            FROM archive_versions
            WHERE source_path = $p COLLATE NOCASE
            ORDER BY version_num DESC LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$p", sourcePath);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadVersion(reader) : null;
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
                    INSERT INTO archive_versions (id, version_num, source_path, archive_path, ready_path, file_name, sha256, norm_hash, size_bytes, problem_code, problem_title, platform, created_at)
                    VALUES ($id, $vn, $sp, $ap, $rp, $fn, $sha, $nh, $sz, $code, $title, $plat, $ts);
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
            SELECT id, version_num, source_path, archive_path, ready_path, file_name, sha256, norm_hash, size_bytes, problem_code, problem_title, platform, created_at
            FROM archive_versions
            WHERE source_path = $p COLLATE NOCASE
            ORDER BY version_num DESC;
            """;
        cmd.Parameters.AddWithValue("$p", sourcePath);
        var list = new List<ArchiveVersionRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(ReadVersion(reader));
        return list;
    }

    public async Task<ArchiveVersionRecord?> GetVersionByIdAsync(string versionId, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, version_num, source_path, archive_path, ready_path, file_name, sha256, norm_hash, size_bytes, problem_code, problem_title, platform, created_at
            FROM archive_versions WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", versionId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadVersion(reader) : null;
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
            SELECT id, version_num, source_path, archive_path, ready_path, file_name, sha256, norm_hash, size_bytes, problem_code, problem_title, platform, created_at
            FROM archive_versions
            WHERE ($plat IS NULL OR $plat = '' OR $plat = 'All' OR platform = $plat)
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
                   v.id, v.version_num, v.source_path, v.archive_path, v.ready_path, v.file_name, v.sha256, v.norm_hash, v.size_bytes, v.problem_code, v.problem_title, v.platform, v.created_at
            FROM upload_queue q
            JOIN archive_versions v ON v.id = q.version_id
            WHERE (q.status = 'Pending' OR (q.status = 'Failed' AND q.next_retry_at <= $now))
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
                   v.id, v.version_num, v.source_path, v.archive_path, v.ready_path, v.file_name, v.sha256, v.norm_hash, v.size_bytes, v.problem_code, v.problem_title, v.platform, v.created_at
            FROM upload_queue q
            JOIN archive_versions v ON v.id = q.version_id
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
            await using var cmd = conn.CreateCommand();
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

    public async Task RecordMonitorEventAsync(MonitorEventRecord ev, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO monitor_events (folder_path, file_path, event_type, message, occurred_at)
                VALUES ($fp, $file, $et, $msg, $ts);
                DELETE FROM monitor_events WHERE id NOT IN (SELECT id FROM monitor_events ORDER BY id DESC LIMIT 500);
                """;
            cmd.Parameters.AddWithValue("$fp", ev.FolderPath);
            cmd.Parameters.AddWithValue("$file", ev.FilePath);
            cmd.Parameters.AddWithValue("$et", ev.EventType);
            cmd.Parameters.AddWithValue("$msg", ev.Message);
            cmd.Parameters.AddWithValue("$ts", ev.OccurredAtUnixMs);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<List<MonitorEventRecord>> GetRecentMonitorEventsAsync(int limit = 50, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, folder_path, file_path, event_type, message, occurred_at FROM monitor_events ORDER BY id DESC LIMIT $lim;";
        cmd.Parameters.AddWithValue("$lim", limit);
        var list = new List<MonitorEventRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new MonitorEventRecord(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt64(5)));
        }
        return list;
    }

    public async Task<ArchiveStats> GetStatsAsync(CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM archive_versions),
                (SELECT COUNT(*) FROM upload_queue WHERE status = 'Pending' OR status = 'Uploading'),
                (SELECT COUNT(*) FROM upload_queue WHERE status = 'Failed'),
                (SELECT COUNT(*) FROM upload_queue WHERE status = 'Synced'),
                (SELECT MAX(updated_at) FROM upload_queue WHERE status = 'Synced');
            """;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return new ArchiveStats(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            reader.IsDBNull(4) ? null : reader.GetInt64(4));
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
            r.GetInt64(offset + 12));

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

    public ValueTask DisposeAsync()
    {
        _writeLock.Dispose();
        SqliteConnection.ClearAllPools();
        return ValueTask.CompletedTask;
    }
}
