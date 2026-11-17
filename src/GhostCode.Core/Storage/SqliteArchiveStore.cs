using GhostCode.Core.Analysis;
using GhostCode.Core.Models;
using Microsoft.Data.Sqlite;

namespace GhostCode.Core.Storage;

public sealed record ArchiveSearchResult(
    ArchiveVersionRecord Version,
    string MatchKind);

public sealed record ProblemTimelineEntry(
    string Id,
    string EventType,
    string Summary,
    long OccurredAtUnixMs,
    string DeviceOrSource,
    string? VersionId,
    string? Detail);

/// <summary>
/// Thread-safe, transactional SQLite WAL store for Last-Known-Good snapshots,
/// immutable historical backup versions, upload queue, and monitoring events.
/// </summary>
public sealed partial class SqliteArchiveStore : IAsyncDisposable
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
            catch (SqliteCorruptionException)
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

    private sealed class SqliteCorruptionException(string message) : Exception(message);

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
            string? checkResult;
            try
            {
                checkResult = Convert.ToString(await checkCmd.ExecuteScalarAsync(ct));
            }
            catch (SqliteException ex)
            {
                throw new SqliteCorruptionException($"SQLite integrity error: {ex.Message}");
            }
            if (!string.Equals(checkResult, "ok", StringComparison.OrdinalIgnoreCase))
                throw new SqliteCorruptionException($"Integrity check failed: {checkResult}");
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            PRAGMA busy_timeout=5000;
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
                created_at INTEGER NOT NULL,
                is_local_available INTEGER NOT NULL DEFAULT 1,
                drive_file_id TEXT,
                is_conflict INTEGER NOT NULL DEFAULT 0,
                is_deleted INTEGER NOT NULL DEFAULT 0,
                deleted_at INTEGER NOT NULL DEFAULT 0,
                trash_batch_id TEXT,
                delete_scope TEXT,
                deleted_origin_device TEXT,
                trash_archive_path TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_versions_source ON archive_versions(source_path, version_num DESC);
            CREATE INDEX IF NOT EXISTS idx_versions_problem ON archive_versions(platform, problem_code, version_num DESC);
            CREATE INDEX IF NOT EXISTS idx_versions_created ON archive_versions(created_at DESC);
            CREATE UNIQUE INDEX IF NOT EXISTS idx_versions_archive_path ON archive_versions(archive_path);

            CREATE TABLE IF NOT EXISTS archive_tombstones (
                id TEXT PRIMARY KEY,
                entity_type TEXT NOT NULL,
                version_id TEXT,
                problem_key TEXT,
                source_path TEXT,
                platform TEXT,
                problem_code TEXT,
                content_sha256 TEXT,
                content_norm_hash TEXT,
                drive_file_id TEXT,
                delete_remote INTEGER NOT NULL DEFAULT 0,
                remote_delete_state TEXT NOT NULL DEFAULT 'LocalOnly',
                attempt_count INTEGER NOT NULL DEFAULT 0,
                last_error TEXT,
                deleted_at INTEGER NOT NULL,
                updated_at INTEGER NOT NULL,
                device_name TEXT NOT NULL,
                expires_at INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_tombstones_version ON archive_tombstones(version_id);
            CREATE INDEX IF NOT EXISTS idx_tombstones_source_sha ON archive_tombstones(source_path, content_sha256);
            CREATE INDEX IF NOT EXISTS idx_tombstones_remote_state ON archive_tombstones(delete_remote, remote_delete_state);

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

            CREATE TABLE IF NOT EXISTS archive_history_events (
                id TEXT PRIMARY KEY,
                event_type TEXT NOT NULL,
                source_path TEXT NOT NULL,
                version_id TEXT,
                summary TEXT NOT NULL,
                occurred_at INTEGER NOT NULL,
                device_name TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS problem_statements (
                problem_key TEXT PRIMARY KEY,
                platform TEXT NOT NULL,
                problem_code TEXT NOT NULL,
                title TEXT NOT NULL,
                url TEXT NOT NULL,
                description TEXT NOT NULL,
                input_spec TEXT NOT NULL,
                output_spec TEXT NOT NULL,
                constraints TEXT NOT NULL,
                time_limit TEXT NOT NULL,
                memory_limit TEXT NOT NULL,
                interactive_or_custom INTEGER NOT NULL DEFAULT 0,
                captured_at INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS problem_sample_tests (
                id TEXT PRIMARY KEY,
                problem_key TEXT NOT NULL,
                case_index INTEGER NOT NULL,
                input_text TEXT NOT NULL,
                expected_output TEXT NOT NULL,
                source_url TEXT NOT NULL,
                is_custom INTEGER NOT NULL DEFAULT 0,
                captured_at INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_sample_tests_key ON problem_sample_tests(problem_key, case_index);

            CREATE TABLE IF NOT EXISTS submission_records (
                id TEXT PRIMARY KEY,
                platform TEXT NOT NULL,
                problem_code TEXT NOT NULL,
                submission_id TEXT,
                submitted_at INTEGER NOT NULL,
                language TEXT NOT NULL,
                verdict TEXT NOT NULL,
                runtime_ms INTEGER,
                memory_kb INTEGER,
                version_id TEXT,
                is_version_certain INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_submissions_problem ON submission_records(problem_code, submitted_at DESC);

            CREATE TABLE IF NOT EXISTS notification_history (
                id TEXT PRIMARY KEY,
                category TEXT NOT NULL,
                severity TEXT NOT NULL,
                title TEXT NOT NULL,
                message TEXT NOT NULL,
                dedup_key TEXT NOT NULL,
                occurred_at INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_notif_occurred ON notification_history(occurred_at DESC);

            UPDATE upload_queue
            SET status = 'Pending', updated_at = $now
            WHERE status = 'Uploading';
            """;
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await cmd.ExecuteNonQueryAsync(ct);

        await EnsureArchiveVersionColumnsAsync(conn, ct);
    }

    private static async Task EnsureArchiveVersionColumnsAsync(SqliteConnection conn, CancellationToken ct)
    {
        var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var infoCmd = conn.CreateCommand())
        {
            infoCmd.CommandText = "PRAGMA table_info(archive_versions);";
            await using var reader = await infoCmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                cols.Add(reader.GetString(1));
        }

        string[] migrations =
        {
            cols.Contains("is_local_available") ? "" : "ALTER TABLE archive_versions ADD COLUMN is_local_available INTEGER NOT NULL DEFAULT 1;",
            cols.Contains("drive_file_id") ? "" : "ALTER TABLE archive_versions ADD COLUMN drive_file_id TEXT;",
            cols.Contains("is_conflict") ? "" : "ALTER TABLE archive_versions ADD COLUMN is_conflict INTEGER NOT NULL DEFAULT 0;",
            cols.Contains("is_deleted") ? "" : "ALTER TABLE archive_versions ADD COLUMN is_deleted INTEGER NOT NULL DEFAULT 0;",
            cols.Contains("deleted_at") ? "" : "ALTER TABLE archive_versions ADD COLUMN deleted_at INTEGER NOT NULL DEFAULT 0;",
            cols.Contains("trash_batch_id") ? "" : "ALTER TABLE archive_versions ADD COLUMN trash_batch_id TEXT;",
            cols.Contains("delete_scope") ? "" : "ALTER TABLE archive_versions ADD COLUMN delete_scope TEXT;",
            cols.Contains("deleted_origin_device") ? "" : "ALTER TABLE archive_versions ADD COLUMN deleted_origin_device TEXT;",
            cols.Contains("trash_archive_path") ? "" : "ALTER TABLE archive_versions ADD COLUMN trash_archive_path TEXT;"
        };
        foreach (var sql in migrations.Where(s => !string.IsNullOrEmpty(s)))
        {
            await using var alt = conn.CreateCommand();
            alt.CommandText = sql;
            await alt.ExecuteNonQueryAsync(ct);
        }

        await using var idxCmd = conn.CreateCommand();
        idxCmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_versions_deleted ON archive_versions(is_deleted, deleted_at DESC);";
        await idxCmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<(bool Healthy, string Detail)> CheckDatabaseHealthAsync(CancellationToken ct = default)
    {
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA quick_check;";
            var res = Convert.ToString(await cmd.ExecuteScalarAsync(ct));
            bool ok = string.Equals(res, "ok", StringComparison.OrdinalIgnoreCase);
            return (ok, ok ? "SQLite WAL quick_check OK" : $"SQLite quick_check: {res}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public ValueTask DisposeAsync()
    {
        _writeLock.Dispose();
        SqliteConnection.ClearAllPools();
        return ValueTask.CompletedTask;
    }
}
