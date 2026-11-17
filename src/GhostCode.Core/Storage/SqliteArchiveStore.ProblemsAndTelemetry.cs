using GhostCode.Core.Analysis;
using GhostCode.Core.Models;
using Microsoft.Data.Sqlite;

namespace GhostCode.Core.Storage;

public sealed partial class SqliteArchiveStore
{
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
                (SELECT COUNT(*) FROM archive_versions WHERE is_deleted = 0),
                (SELECT COUNT(*) FROM upload_queue q JOIN archive_versions v ON v.id = q.version_id WHERE v.is_deleted = 0 AND (q.status = 'Pending' OR q.status = 'Uploading')),
                (SELECT COUNT(*) FROM upload_queue q JOIN archive_versions v ON v.id = q.version_id WHERE v.is_deleted = 0 AND q.status = 'Failed'),
                (SELECT COUNT(*) FROM upload_queue q JOIN archive_versions v ON v.id = q.version_id WHERE v.is_deleted = 0 AND q.status = 'Synced'),
                (SELECT MAX(q.updated_at) FROM upload_queue q JOIN archive_versions v ON v.id = q.version_id WHERE v.is_deleted = 0 AND q.status = 'Synced'),
                (SELECT COUNT(*) FROM archive_versions WHERE is_deleted = 0 AND is_local_available = 0),
                (SELECT COUNT(*) FROM archive_versions WHERE is_deleted = 1);
            """;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return new ArchiveStats(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            reader.IsDBNull(4) ? null : reader.GetInt64(4),
            reader.GetInt32(5),
            reader.GetInt32(6));
    }

    public async Task<LatestBackupSummary?> GetLatestBackupSummaryAsync(CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, file_name, platform, problem_code, source_path, archive_path, is_local_available, drive_file_id, is_conflict, created_at
            FROM archive_versions
            WHERE is_deleted = 0
            ORDER BY created_at DESC, version_num DESC
            LIMIT 1;
            """;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        bool isLocal = reader.GetInt32(6) != 0;
        string? driveId = reader.IsDBNull(7) ? null : reader.GetString(7);
        bool isConflict = reader.GetInt32(8) != 0;
        var avail = isConflict
            ? VersionAvailability.Conflict
            : (!isLocal
                ? VersionAvailability.CloudOnly
                : (!string.IsNullOrEmpty(driveId) ? VersionAvailability.Synced : VersionAvailability.LocalOnly));

        return new LatestBackupSummary(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            avail,
            reader.GetInt64(9));
    }

    public async Task<List<ArchiveSearchResult>> SearchArchiveAsync(string query, int limit = 60, CancellationToken ct = default)
    {
        var all = await QueryVersionsAsync(limit: 2000, ct: ct);
        if (string.IsNullOrWhiteSpace(query))
        {
            return all.Take(limit)
                .Select(v => new ArchiveSearchResult(v, v.IsLocalAvailable ? "Metadata" : "Cloud Metadata"))
                .ToList();
        }

        var normQuery = SourceAnalyzer.StripDiacritics(query).Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(normQuery))
            return new List<ArchiveSearchResult>();

        var results = new List<ArchiveSearchResult>();
        foreach (var v in all)
        {
            ct.ThrowIfCancellationRequested();
            var ext = Path.GetExtension(v.FileName).TrimStart('.').ToLowerInvariant();
            var langHint = ext switch
            {
                "cpp" or "cc" or "cxx" => "cpp c++",
                "c" => "c",
                "py" => "python py",
                _ => ext
            };
            var combinedMeta = SourceAnalyzer.StripDiacritics(
                $"{v.FileName} {v.ProblemCode} {v.ProblemTitle} {v.Platform} {v.SourcePath} v{v.VersionNum} {langHint}")
                .ToLowerInvariant();

            if (combinedMeta.Contains(normQuery, StringComparison.Ordinal))
            {
                results.Add(new ArchiveSearchResult(v, v.IsLocalAvailable ? "Metadata" : "Cloud Metadata"));
                if (results.Count >= limit)
                    break;
                continue;
            }

            if (v.IsLocalAvailable && File.Exists(v.ArchivePath))
            {
                try
                {
                    var fi = new FileInfo(v.ArchivePath);
                    if (fi.Length <= 512 * 1024)
                    {
                        var text = await File.ReadAllTextAsync(v.ArchivePath, ct);
                        var normBody = SourceAnalyzer.StripDiacritics(text).ToLowerInvariant();
                        if (normBody.Contains(normQuery, StringComparison.Ordinal))
                        {
                            results.Add(new ArchiveSearchResult(v, "Source Content"));
                            if (results.Count >= limit)
                                break;
                        }
                    }
                }
                catch { }
            }
        }

        return results;
    }

    public async Task UpsertProblemStatementAsync(ProblemStatementRecord rec, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO problem_statements (problem_key, platform, problem_code, title, url, description, input_spec, output_spec, constraints, time_limit, memory_limit, interactive_or_custom, captured_at)
                VALUES ($pk, $pl, $pc, $title, $url, $desc, $inSpec, $outSpec, $cons, $tl, $ml, $inter, $ts)
                ON CONFLICT(problem_key) DO UPDATE SET
                    platform=excluded.platform,
                    problem_code=excluded.problem_code,
                    title=excluded.title,
                    url=excluded.url,
                    description=excluded.description,
                    input_spec=excluded.input_spec,
                    output_spec=excluded.output_spec,
                    constraints=excluded.constraints,
                    time_limit=excluded.time_limit,
                    memory_limit=excluded.memory_limit,
                    interactive_or_custom=excluded.interactive_or_custom,
                    captured_at=CASE WHEN excluded.captured_at > problem_statements.captured_at THEN excluded.captured_at ELSE problem_statements.captured_at END;
                """;
            cmd.Parameters.AddWithValue("$pk", rec.ProblemKey);
            cmd.Parameters.AddWithValue("$pl", rec.Platform);
            cmd.Parameters.AddWithValue("$pc", rec.ProblemCode);
            cmd.Parameters.AddWithValue("$title", rec.Title);
            cmd.Parameters.AddWithValue("$url", rec.Url);
            cmd.Parameters.AddWithValue("$desc", rec.Description);
            cmd.Parameters.AddWithValue("$inSpec", rec.InputSpec);
            cmd.Parameters.AddWithValue("$outSpec", rec.OutputSpec);
            cmd.Parameters.AddWithValue("$cons", rec.Constraints);
            cmd.Parameters.AddWithValue("$tl", rec.TimeLimit);
            cmd.Parameters.AddWithValue("$ml", rec.MemoryLimit);
            cmd.Parameters.AddWithValue("$inter", rec.InteractiveOrCustomJudge ? 1 : 0);
            cmd.Parameters.AddWithValue("$ts", rec.CapturedAtUnixMs);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<ProblemStatementRecord?> GetProblemStatementAsync(string problemKey, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT problem_key, platform, problem_code, title, url, description, input_spec, output_spec, constraints, time_limit, memory_limit, interactive_or_custom, captured_at
            FROM problem_statements
            WHERE problem_key = $pk COLLATE NOCASE OR problem_code = $pk COLLATE NOCASE
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$pk", problemKey);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct))
            return null;
        return ReadProblemStatement(r);
    }

    public async Task<List<ProblemStatementRecord>> GetAllProblemStatementsAsync(int limit = 1000, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT problem_key, platform, problem_code, title, url, description, input_spec, output_spec, constraints, time_limit, memory_limit, interactive_or_custom, captured_at
            FROM problem_statements
            ORDER BY captured_at DESC
            LIMIT $lim;
            """;
        cmd.Parameters.AddWithValue("$lim", limit);
        var list = new List<ProblemStatementRecord>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(ReadProblemStatement(r));
        return list;
    }

    public async Task UpsertSampleTestAsync(SampleTestCaseRecord tc, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO problem_sample_tests (id, problem_key, case_index, input_text, expected_output, source_url, is_custom, captured_at)
                VALUES ($id, $pk, $idx, $inTxt, $outTxt, $url, $custom, $ts)
                ON CONFLICT(id) DO UPDATE SET
                    case_index=excluded.case_index,
                    input_text=excluded.input_text,
                    expected_output=excluded.expected_output,
                    source_url=excluded.source_url,
                    is_custom=excluded.is_custom,
                    captured_at=excluded.captured_at;
                """;
            cmd.Parameters.AddWithValue("$id", tc.Id);
            cmd.Parameters.AddWithValue("$pk", tc.ProblemKey);
            cmd.Parameters.AddWithValue("$idx", tc.CaseIndex);
            cmd.Parameters.AddWithValue("$inTxt", tc.InputText);
            cmd.Parameters.AddWithValue("$outTxt", tc.ExpectedOutput);
            cmd.Parameters.AddWithValue("$url", tc.SourceUrl);
            cmd.Parameters.AddWithValue("$custom", tc.IsCustom ? 1 : 0);
            cmd.Parameters.AddWithValue("$ts", tc.CapturedAtUnixMs);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<List<SampleTestCaseRecord>> GetSampleTestsAsync(string problemKey, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, problem_key, case_index, input_text, expected_output, source_url, is_custom, captured_at
            FROM problem_sample_tests
            WHERE problem_key = $pk COLLATE NOCASE
            ORDER BY is_custom ASC, case_index ASC, captured_at ASC;
            """;
        cmd.Parameters.AddWithValue("$pk", problemKey);
        var list = new List<SampleTestCaseRecord>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(ReadSampleTest(r));
        return list;
    }

    public async Task<List<SampleTestCaseRecord>> GetAllSampleTestsAsync(int limit = 2000, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, problem_key, case_index, input_text, expected_output, source_url, is_custom, captured_at
            FROM problem_sample_tests
            ORDER BY problem_key ASC, case_index ASC
            LIMIT $lim;
            """;
        cmd.Parameters.AddWithValue("$lim", limit);
        var list = new List<SampleTestCaseRecord>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(ReadSampleTest(r));
        return list;
    }

    public async Task DeleteSampleTestAsync(string testId, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM problem_sample_tests WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", testId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task UpsertSubmissionRecordAsync(SubmissionRecord sub, CancellationToken ct = default)
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
                    INSERT INTO submission_records (id, platform, problem_code, submission_id, submitted_at, language, verdict, runtime_ms, memory_kb, version_id, is_version_certain)
                    VALUES ($id, $pl, $pc, $sid, $ts, $lang, $vrd, $rt, $mem, $vid, $cert)
                    ON CONFLICT(id) DO UPDATE SET
                        verdict=excluded.verdict,
                        runtime_ms=COALESCE(excluded.runtime_ms, submission_records.runtime_ms),
                        memory_kb=COALESCE(excluded.memory_kb, submission_records.memory_kb),
                        version_id=COALESCE(excluded.version_id, submission_records.version_id),
                        is_version_certain=CASE WHEN excluded.is_version_certain = 1 THEN 1 ELSE submission_records.is_version_certain END;
                    """;
                cmd.Parameters.AddWithValue("$id", sub.Id);
                cmd.Parameters.AddWithValue("$pl", sub.Platform);
                cmd.Parameters.AddWithValue("$pc", sub.ProblemCode);
                cmd.Parameters.AddWithValue("$sid", (object?)sub.SubmissionId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$ts", sub.SubmittedAtUnixMs);
                cmd.Parameters.AddWithValue("$lang", sub.Language);
                cmd.Parameters.AddWithValue("$vrd", sub.Verdict.ToString());
                cmd.Parameters.AddWithValue("$rt", (object?)sub.RuntimeMs ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$mem", (object?)sub.MemoryKb ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$vid", (object?)sub.VersionId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$cert", sub.IsVersionCertain ? 1 : 0);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            var certNote = sub.IsVersionCertain && !string.IsNullOrEmpty(sub.VersionId)
                ? "verified version match"
                : "version link uncertain";
            await InsertHistoryEventInTxAsync(conn, tx, new ArchiveHistoryEvent(
                $"sub_{sub.Id}_{sub.Verdict}",
                "SubmissionVerdict",
                sub.ProblemCode,
                sub.IsVersionCertain ? sub.VersionId : null,
                $"[{sub.Platform}] {sub.ProblemCode} Submission {sub.SubmissionId ?? sub.Id}: {sub.Verdict} ({certNote})",
                sub.SubmittedAtUnixMs,
                sub.Platform), ct);

            await tx.CommitAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<List<SubmissionRecord>> GetSubmissionsForProblemAsync(string problemCode, int limit = 100, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, platform, problem_code, submission_id, submitted_at, language, verdict, runtime_ms, memory_kb, version_id, is_version_certain
            FROM submission_records
            WHERE problem_code = $pc COLLATE NOCASE
            ORDER BY submitted_at DESC
            LIMIT $lim;
            """;
        cmd.Parameters.AddWithValue("$pc", problemCode);
        cmd.Parameters.AddWithValue("$lim", limit);
        var list = new List<SubmissionRecord>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(ReadSubmission(r));
        return list;
    }

    public async Task<List<SubmissionRecord>> GetAllSubmissionsAsync(int limit = 1000, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, platform, problem_code, submission_id, submitted_at, language, verdict, runtime_ms, memory_kb, version_id, is_version_certain
            FROM submission_records
            ORDER BY submitted_at DESC
            LIMIT $lim;
            """;
        cmd.Parameters.AddWithValue("$lim", limit);
        var list = new List<SubmissionRecord>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(ReadSubmission(r));
        return list;
    }

    public async Task<SubmissionRecord?> GetLatestVerdictForVersionAsync(string versionId, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, platform, problem_code, submission_id, submitted_at, language, verdict, runtime_ms, memory_kb, version_id, is_version_certain
            FROM submission_records
            WHERE version_id = $vid AND is_version_certain = 1
            ORDER BY submitted_at DESC
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$vid", versionId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadSubmission(r) : null;
    }

    public async Task RecordNotificationAsync(NotificationRecord nr, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR IGNORE INTO notification_history (id, category, severity, title, message, dedup_key, occurred_at)
                VALUES ($id, $cat, $sev, $title, $msg, $dk, $ts);
                """;
            cmd.Parameters.AddWithValue("$id", nr.Id);
            cmd.Parameters.AddWithValue("$cat", nr.Category);
            cmd.Parameters.AddWithValue("$sev", nr.Severity);
            cmd.Parameters.AddWithValue("$title", nr.Title);
            cmd.Parameters.AddWithValue("$msg", nr.Message);
            cmd.Parameters.AddWithValue("$dk", nr.DedupKey);
            cmd.Parameters.AddWithValue("$ts", nr.OccurredAtUnixMs);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<List<NotificationRecord>> GetRecentNotificationsAsync(int limit = 100, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, category, severity, title, message, dedup_key, occurred_at
            FROM notification_history
            ORDER BY occurred_at DESC
            LIMIT $lim;
            """;
        cmd.Parameters.AddWithValue("$lim", limit);
        var list = new List<NotificationRecord>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new NotificationRecord(
                r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetInt64(6)));
        }
        return list;
    }

    public async Task<List<ProblemTimelineEntry>> GetTimelineForProblemAsync(string problemCode, int limit = 200, CancellationToken ct = default)
    {
        var entries = new List<ProblemTimelineEntry>();
        var needle = problemCode.Trim();
        var versions = await QueryVersionsAsync(search: needle, limit: 500, ct: ct);
        var matchingVersionIds = versions
            .Where(v => string.Equals(v.ProblemCode, needle, StringComparison.OrdinalIgnoreCase))
            .Select(v => v.Id)
            .ToHashSet(StringComparer.Ordinal);
        var matchingSourcePaths = versions
            .Where(v => string.Equals(v.ProblemCode, needle, StringComparison.OrdinalIgnoreCase))
            .Select(v => v.SourcePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var history = await GetArchiveHistoryEventsAsync(1000, ct);
        foreach (var h in history)
        {
            bool matches = (h.VersionId is not null && matchingVersionIds.Contains(h.VersionId))
                || matchingSourcePaths.Contains(h.SourcePath)
                || string.Equals(h.SourcePath, needle, StringComparison.OrdinalIgnoreCase)
                || h.Summary.Contains(needle, StringComparison.OrdinalIgnoreCase);
            if (matches)
            {
                entries.Add(new ProblemTimelineEntry(h.Id, h.EventType, h.Summary, h.OccurredAtUnixMs, h.DeviceName, h.VersionId, h.SourcePath));
            }
        }

        return entries
            .OrderByDescending(e => e.OccurredAtUnixMs)
            .Take(limit)
            .OrderBy(e => e.OccurredAtUnixMs)
            .ToList();
    }

    private static ProblemStatementRecord ReadProblemStatement(SqliteDataReader r) =>
        new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
            r.GetString(5), r.GetString(6), r.GetString(7), r.GetString(8), r.GetString(9),
            r.GetString(10), r.GetInt64(11) != 0, r.GetInt64(12));

    private static SampleTestCaseRecord ReadSampleTest(SqliteDataReader r) =>
        new(r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetString(3), r.GetString(4),
            r.GetString(5), r.GetInt64(6) != 0, r.GetInt64(7));

    private static SubmissionRecord ReadSubmission(SqliteDataReader r) =>
        new(
            r.GetString(0),
            r.GetString(1),
            r.GetString(2),
            r.IsDBNull(3) ? null : r.GetString(3),
            r.GetInt64(4),
            r.GetString(5),
            Enum.TryParse<SubmissionVerdict>(r.GetString(6), true, out var v) ? v : SubmissionVerdict.Unknown,
            r.IsDBNull(7) ? null : r.GetInt32(7),
            r.IsDBNull(8) ? null : r.GetInt32(8),
            r.IsDBNull(9) ? null : r.GetString(9),
            r.GetInt64(10) != 0);
}
