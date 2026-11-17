using System.Text;
using System.Text.Json;
using GhostCode.Core.Models;
using GhostCode.Core.Storage;

namespace GhostCode.Core.Cloud;

/// <summary>
/// Provider-agnostic JSON serializer & reconciler for GhostCode cloud archive manifest v2.
/// Preserves versions, problem statements, sample test cases, submissions, and history events
/// while excluding secrets, OAuth tokens, passwords, and pairing keys.
/// </summary>
public static class CloudManifestCodec
{
    public const string ManifestFileName = "ghostcode_archive_manifest.v2.json";

    public static async Task<byte[]> BuildManifestBytesAsync(SqliteArchiveStore store, CancellationToken ct = default)
    {
        var versions = await store.QueryVersionsAsync(limit: 5000, ct: ct);
        var events = await store.GetArchiveHistoryEventsAsync(1000, ct);
        var statements = await store.GetAllProblemStatementsAsync(1000, ct);
        var tests = await store.GetAllSampleTestsAsync(2000, ct);
        var submissions = await store.GetAllSubmissionsAsync(1000, ct);
        var remoteTombstones = await store.GetTombstonesAsync(remoteDeleteOnly: true, ct);

        var obj = new
        {
            schema = 2,
            exported_at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            device = Environment.MachineName,
            versions = versions.Select(v => new
            {
                id = v.Id,
                version_num = v.VersionNum,
                source_path = v.SourcePath,
                file_name = v.FileName,
                sha256 = v.Sha256,
                norm_hash = v.NormHash,
                size_bytes = v.SizeBytes,
                problem_code = v.ProblemCode,
                problem_title = v.ProblemTitle,
                platform = v.Platform,
                created_at = v.CreatedAtUnixMs,
                drive_file_id = v.DriveFileId,
                is_conflict = v.IsConflictBranch
            }),
            events = events.Select(e => new
            {
                id = e.Id,
                event_type = e.EventType,
                source_path = e.SourcePath,
                version_id = e.VersionId,
                summary = e.Summary,
                occurred_at = e.OccurredAtUnixMs,
                device_name = e.DeviceName
            }),
            statements = statements.Select(s => new
            {
                problem_key = s.ProblemKey,
                platform = s.Platform,
                problem_code = s.ProblemCode,
                title = s.Title,
                url = s.Url,
                description = s.Description,
                input_spec = s.InputSpec,
                output_spec = s.OutputSpec,
                constraints = s.Constraints,
                time_limit = s.TimeLimit,
                memory_limit = s.MemoryLimit,
                interactive_or_custom = s.InteractiveOrCustomJudge,
                captured_at = s.CapturedAtUnixMs
            }),
            sample_tests = tests.Select(t => new
            {
                id = t.Id,
                problem_key = t.ProblemKey,
                case_index = t.CaseIndex,
                input_text = t.InputText,
                expected_output = t.ExpectedOutput,
                source_url = t.SourceUrl,
                is_custom = t.IsCustom,
                captured_at = t.CapturedAtUnixMs
            }),
            submissions = submissions.Select(sub => new
            {
                id = sub.Id,
                platform = sub.Platform,
                problem_code = sub.ProblemCode,
                submission_id = sub.SubmissionId,
                submitted_at = sub.SubmittedAtUnixMs,
                language = sub.Language,
                verdict = sub.Verdict.ToString(),
                runtime_ms = sub.RuntimeMs,
                memory_kb = sub.MemoryKb,
                version_id = sub.VersionId,
                is_version_certain = sub.IsVersionCertain
            }),
            tombstones = remoteTombstones.Select(t => new
            {
                id = t.Id,
                entity_type = t.EntityType,
                version_id = t.VersionId,
                problem_key = t.ProblemKey,
                source_path = t.SourcePath,
                platform = t.Platform,
                problem_code = t.ProblemCode,
                content_sha256 = t.ContentSha256,
                drive_file_id = t.DriveFileId,
                delete_remote = t.DeleteRemote,
                deleted_at = t.DeletedAtUnixMs,
                device = t.DeviceName
            })
        };

        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(obj));
    }

    public static async Task<(int Discovered, int NewCloudOnly, int Conflicts, int EventsImported, HashSet<string> SeenRemoteIds)> ReconcileManifestBytesAsync(
        SqliteArchiveStore store,
        byte[] manifestBytes,
        CancellationToken ct = default)
    {
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var beforeAll = await store.QueryVersionsAsync(limit: 10_000, ct: ct);
        var existingById = beforeAll.ToDictionary(v => v.Id, StringComparer.OrdinalIgnoreCase);
        var conflictBeforeCount = beforeAll.Count(x => x.IsConflictBranch);

        int discovered = 0, newCloud = 0, conflicts = 0, eventsImported = 0;

        using var mdoc = JsonDocument.Parse(manifestBytes);
        var root = mdoc.RootElement;

        // Reconcile explicit remote deletion tombstones first (conflict-safe: never erases newer local edits)
        if (root.TryGetProperty("tombstones", out var tombArray) && tombArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var te in tombArray.EnumerateArray())
            {
                var tomb = ParseManifestTombstone(te);
                if (tomb is not null && tomb.DeleteRemote)
                    await store.ApplyRemoteTombstoneAsync(tomb, ct);
            }
        }

        if (root.TryGetProperty("versions", out var verArray) && verArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var ve in verArray.EnumerateArray())
            {
                var rec = ParseManifestVersion(ve);
                if (rec is null)
                    continue;
                discovered++;
                if (!string.IsNullOrEmpty(rec.DriveFileId))
                    seenIds.Add(rec.DriveFileId);

                bool existed = existingById.ContainsKey(rec.Id);
                await store.UpsertRemoteVersionMetadataAsync(rec, ct);
                if (!existed)
                {
                    var inserted = await store.GetVersionByIdAsync(rec.Id, ct);
                    if (inserted is not null)
                    {
                        existingById[inserted.Id] = inserted;
                        if (!inserted.IsLocalAvailable) newCloud++;
                        if (inserted.IsConflictBranch) conflicts++;
                    }
                }
            }
        }

        if (root.TryGetProperty("events", out var evArray) && evArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var ee in evArray.EnumerateArray())
            {
                var ev = ParseManifestEvent(ee);
                if (ev is null) continue;
                await store.RecordArchiveHistoryEventAsync(ev, ct);
                eventsImported++;
            }
        }

        if (root.TryGetProperty("statements", out var stArray) && stArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var se in stArray.EnumerateArray())
            {
                var st = ParseStatement(se);
                if (st is not null)
                    await store.UpsertProblemStatementAsync(st, ct);
            }
        }

        if (root.TryGetProperty("sample_tests", out var tcArray) && tcArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var te in tcArray.EnumerateArray())
            {
                var tc = ParseSampleTest(te);
                if (tc is not null)
                    await store.UpsertSampleTestAsync(tc, ct);
            }
        }

        if (root.TryGetProperty("submissions", out var subArray) && subArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var su in subArray.EnumerateArray())
            {
                var sub = ParseSubmission(su);
                if (sub is not null)
                    await store.UpsertSubmissionRecordAsync(sub, ct);
            }
        }

        return (discovered, newCloud, conflicts, eventsImported, seenIds);
    }

    public static ArchiveVersionRecord? ParseManifestVersion(JsonElement ve)
    {
        var id = ve.TryGetProperty("id", out var p) ? p.GetString() : null;
        var fileName = ve.TryGetProperty("file_name", out var fn) ? fn.GetString() : null;
        var sha = ve.TryGetProperty("sha256", out var sh) ? sh.GetString() : null;
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(sha))
            return null;

        var ver = ve.TryGetProperty("version_num", out var vn) ? vn.GetInt32() : 1;
        var sp = ve.TryGetProperty("source_path", out var spp) ? (spp.GetString() ?? fileName) : fileName;
        var nh = ve.TryGetProperty("norm_hash", out var nhp) ? (nhp.GetString() ?? sha) : sha;
        var sz = ve.TryGetProperty("size_bytes", out var szp) ? szp.GetInt64() : 0;
        var pc = ve.TryGetProperty("problem_code", out var pcp) ? pcp.GetString() : null;
        var pt = ve.TryGetProperty("problem_title", out var ptp) ? ptp.GetString() : null;
        var pl = ve.TryGetProperty("platform", out var plp) ? (plp.GetString() ?? "Unknown") : "Unknown";
        var ts = ve.TryGetProperty("created_at", out var tsp) ? tsp.GetInt64() : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var did = ve.TryGetProperty("drive_file_id", out var dip) ? dip.GetString() : null;
        var conf = ve.TryGetProperty("is_conflict", out var cp) && cp.ValueKind == JsonValueKind.True;
        return new ArchiveVersionRecord(
            id, ver, sp, $"cloud://{did ?? id}", null, fileName, sha, nh, sz, pc, pt, pl, ts,
            IsLocalAvailable: false, DriveFileId: did, IsConflictBranch: conf);
    }

    private static ArchiveTombstoneRecord? ParseManifestTombstone(JsonElement te)
    {
        var id = te.TryGetProperty("id", out var idp) ? idp.GetString() : null;
        var et = te.TryGetProperty("entity_type", out var etp) ? etp.GetString() : null;
        var vid = te.TryGetProperty("version_id", out var vip) ? vip.GetString() : null;
        bool dr = te.TryGetProperty("delete_remote", out var drp) && drp.ValueKind == JsonValueKind.True;
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(et) || !dr)
            return null;

        long dt = te.TryGetProperty("deleted_at", out var dtp) ? dtp.GetInt64() : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return new ArchiveTombstoneRecord(
            id,
            et,
            vid,
            te.TryGetProperty("problem_key", out var pkp) ? pkp.GetString() : null,
            te.TryGetProperty("source_path", out var spp) ? spp.GetString() : null,
            te.TryGetProperty("platform", out var plp) ? plp.GetString() : null,
            te.TryGetProperty("problem_code", out var pcp) ? pcp.GetString() : null,
            te.TryGetProperty("content_sha256", out var shp) ? shp.GetString() : null,
            null,
            te.TryGetProperty("drive_file_id", out var dfp) ? dfp.GetString() : null,
            DeleteRemote: true,
            RemoteDeleteStatus.RemoteDeleted,
            0,
            null,
            dt,
            now,
            te.TryGetProperty("device", out var dvp) ? (dvp.GetString() ?? "Remote") : "Remote",
            dt + 30L * 86_400_000L);
    }

    private static ArchiveHistoryEvent? ParseManifestEvent(JsonElement ee)
    {
        var id = ee.TryGetProperty("id", out var ip) ? ip.GetString() : null;
        var et = ee.TryGetProperty("event_type", out var etp) ? etp.GetString() : null;
        var sum = ee.TryGetProperty("summary", out var sump) ? sump.GetString() : null;
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(et) || string.IsNullOrWhiteSpace(sum))
            return null;
        var sp = ee.TryGetProperty("source_path", out var spp) ? (spp.GetString() ?? string.Empty) : string.Empty;
        var vid = ee.TryGetProperty("version_id", out var vip) ? vip.GetString() : null;
        var ts = ee.TryGetProperty("occurred_at", out var tsp) ? tsp.GetInt64() : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var dev = ee.TryGetProperty("device_name", out var dp) ? (dp.GetString() ?? "Cloud") : "Cloud";
        return new ArchiveHistoryEvent(id, et, sp, vid, sum, ts, dev);
    }

    private static ProblemStatementRecord? ParseStatement(JsonElement se)
    {
        var key = se.TryGetProperty("problem_key", out var pk) ? pk.GetString() : null;
        var code = se.TryGetProperty("problem_code", out var pc) ? pc.GetString() : null;
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(code)) return null;
        return new ProblemStatementRecord(
            key,
            se.TryGetProperty("platform", out var pl) ? (pl.GetString() ?? "Unknown") : "Unknown",
            code,
            se.TryGetProperty("title", out var ti) ? (ti.GetString() ?? string.Empty) : string.Empty,
            se.TryGetProperty("url", out var ur) ? (ur.GetString() ?? string.Empty) : string.Empty,
            se.TryGetProperty("description", out var de) ? (de.GetString() ?? string.Empty) : string.Empty,
            se.TryGetProperty("input_spec", out var ins) ? (ins.GetString() ?? string.Empty) : string.Empty,
            se.TryGetProperty("output_spec", out var outs) ? (outs.GetString() ?? string.Empty) : string.Empty,
            se.TryGetProperty("constraints", out var co) ? (co.GetString() ?? string.Empty) : string.Empty,
            se.TryGetProperty("time_limit", out var tl) ? (tl.GetString() ?? string.Empty) : string.Empty,
            se.TryGetProperty("memory_limit", out var ml) ? (ml.GetString() ?? string.Empty) : string.Empty,
            se.TryGetProperty("interactive_or_custom", out var ic) && ic.ValueKind == JsonValueKind.True,
            se.TryGetProperty("captured_at", out var ts) ? ts.GetInt64() : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    private static SampleTestCaseRecord? ParseSampleTest(JsonElement te)
    {
        var id = te.TryGetProperty("id", out var idp) ? idp.GetString() : null;
        var pk = te.TryGetProperty("problem_key", out var pkp) ? pkp.GetString() : null;
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(pk)) return null;
        return new SampleTestCaseRecord(
            id,
            pk,
            te.TryGetProperty("case_index", out var idx) ? idx.GetInt32() : 1,
            te.TryGetProperty("input_text", out var inp) ? (inp.GetString() ?? string.Empty) : string.Empty,
            te.TryGetProperty("expected_output", out var exp) ? (exp.GetString() ?? string.Empty) : string.Empty,
            te.TryGetProperty("source_url", out var url) ? (url.GetString() ?? string.Empty) : string.Empty,
            te.TryGetProperty("is_custom", out var cu) && cu.ValueKind == JsonValueKind.True,
            te.TryGetProperty("captured_at", out var ts) ? ts.GetInt64() : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    private static SubmissionRecord? ParseSubmission(JsonElement su)
    {
        var id = su.TryGetProperty("id", out var idp) ? idp.GetString() : null;
        var code = su.TryGetProperty("problem_code", out var pc) ? pc.GetString() : null;
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(code)) return null;
        var verdictStr = su.TryGetProperty("verdict", out var vp) ? vp.GetString() : "Unknown";
        return new SubmissionRecord(
            id,
            su.TryGetProperty("platform", out var pl) ? (pl.GetString() ?? "Unknown") : "Unknown",
            code,
            su.TryGetProperty("submission_id", out var sid) ? sid.GetString() : null,
            su.TryGetProperty("submitted_at", out var ts) ? ts.GetInt64() : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            su.TryGetProperty("language", out var lg) ? (lg.GetString() ?? string.Empty) : string.Empty,
            Enum.TryParse<SubmissionVerdict>(verdictStr, true, out var v) ? v : SubmissionVerdict.Unknown,
            su.TryGetProperty("runtime_ms", out var rt) && rt.ValueKind == JsonValueKind.Number ? rt.GetInt32() : null,
            su.TryGetProperty("memory_kb", out var mem) && mem.ValueKind == JsonValueKind.Number ? mem.GetInt32() : null,
            su.TryGetProperty("version_id", out var vid) ? vid.GetString() : null,
            su.TryGetProperty("is_version_certain", out var cert) && cert.ValueKind == JsonValueKind.True);
    }
}
