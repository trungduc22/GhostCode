using GhostCode.Core.Analysis;
using GhostCode.Core.Models;
using GhostCode.Core.Storage;

namespace GhostCode.Core.Archiver;

public enum ArchiveOutcome
{
    UnsupportedFile,
    Deduplicated,
    PreservedPreviousOnReset,
    CreatedVersion
}

public sealed record ArchiveResult(
    ArchiveOutcome Outcome,
    ArchiveVersionRecord? Version,
    string Message);

/// <summary>
/// Lossless, non-destructive source archiver.
/// - Works with or without active browser context.
/// - Preserves original bytes and encoding 100% losslessly.
/// - Maintains immutable historical versions (`v1`, `v2`, ...) using atomic writes.
/// - Preserves previous snapshot when a scratch file is cleared or reset to boilerplate.
/// </summary>
public sealed class LosslessArchiver
{
    private readonly GhostCodeConfig _config;
    private readonly SqliteArchiveStore _store;
    private readonly SemaphoreSlim _archiverLock = new(1, 1);

    public LosslessArchiver(GhostCodeConfig config, SqliteArchiveStore store)
    {
        _config = config;
        _store = store;
    }

    public event Action<ArchiveVersionRecord>? VersionArchived;

    public async Task<ArchiveResult> ProcessFileSaveAsync(
        string sourcePath,
        byte[] rawBytes,
        ProblemContext? activeContext,
        CancellationToken ct = default)
    {
        var fullSource = Path.GetFullPath(sourcePath);
        var lang = SourceAnalyzer.LanguageFromPath(fullSource);
        if (lang is null)
            return new ArchiveResult(ArchiveOutcome.UnsupportedFile, null, "Unsupported extension");

        var (text, encodingName) = SourceAnalyzer.DecodePreservingInfo(rawBytes);

        await _archiverLock.WaitAsync(ct);
        try
        {
            var platformLabel = activeContext?.Platform.ToString() ?? OjPlatform.Offline.ToString();
            var latest = activeContext is not null
                ? await _store.GetLatestVersionForProblemAsync(platformLabel, activeContext.Code, ct)
                : await _store.GetLatestOfflineVersionForSourceAsync(fullSource, ct);

            // Empty or template boilerplate reset: NEVER clobber or erase the previous good snapshot!
            if (SourceAnalyzer.IsTrivialOrTemplate(text, lang.Value))
            {
                var fallbackLatest = latest ?? await _store.GetLatestVersionForSourceAsync(fullSource, ct);
                if (fallbackLatest is not null)
                {
                    return new ArchiveResult(
                        ArchiveOutcome.PreservedPreviousOnReset,
                        fallbackLatest,
                        $"Preserved previous v{fallbackLatest.VersionNum} ({fallbackLatest.FileName}) after template/empty reset");
                }
                return new ArchiveResult(ArchiveOutcome.UnsupportedFile, null, "Empty or boilerplate template ignored");
            }

            var sha256 = SourceAnalyzer.ComputeSha256Hex(rawBytes);
            var normHash = SourceAnalyzer.ComputeNormalizedHash(text);

            // Suppression check: if the user deleted this exact snapshot and the source file hasn't changed yet,
            // do not silently recreate the deleted version on background reconciliation scans.
            // Any future legitimate edit (with a different sha256/normHash) will bypass this check and archive normally.
            if (await _store.IsContentSuppressedByDeletionAsync(fullSource, sha256, normHash, ct))
            {
                return new ArchiveResult(ArchiveOutcome.Deduplicated, latest, "Suppressed unchanged deleted version until next new edit");
            }

            // Exact byte deduplication OR whitespace-only duplicate of the immediately preceding version
            if (latest is not null && (latest.Sha256 == sha256 || latest.NormHash == normHash))
            {
                return new ArchiveResult(ArchiveOutcome.Deduplicated, latest, $"Duplicate content (v{latest.VersionNum})");
            }

            // If offline (no browser context), do not fork an unwanted offline copy when the source file on disk
            // is byte-identical to its last problem-tagged snapshot (e.g., background reconcile after closing browser).
            if (activeContext is null)
            {
                var snap = await _store.GetSnapshotAsync(fullSource, ct);
                if (snap is not null && snap.Sha256 == sha256)
                {
                    var existingForSource = await _store.GetLatestVersionForSourceAsync(fullSource, ct);
                    if (existingForSource is not null)
                    {
                        return new ArchiveResult(ArchiveOutcome.Deduplicated, existingForSource, $"Duplicate content (v{existingForSource.VersionNum})");
                    }
                }
            }

            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var maxExistingVersionNum = await _store.GetMaxVersionNumAsync(platformLabel, activeContext?.Code, fullSource, ct);
            var versionNum = Math.Max(latest?.VersionNum ?? 0, maxExistingVersionNum) + 1;
            var targetExt = SourceAnalyzer.TargetExtension(activeContext, lang.Value, text);
            var baseStem = SourceAnalyzer.BuildProblemBaseName(activeContext, fullSource, text, lang.Value);
            var cleanFileName = $"{baseStem}.{targetExt}";
            var shortHash = sha256[..8];
            var versionId = Guid.NewGuid().ToString("N");

            // 1. Strictly immutable historical version inside isolated internal version directory
            //    keeping the actual filename 100% clean (ProblemID_Title.ext)
            var dateFolder = DateTime.Now.ToString("yyyy-MM-dd");
            var (historyPath, _) = await WriteCleanHistoricalFileAsync(
                _config.GetInternalHistoryRoot(),
                dateFolder,
                versionNum,
                shortHash,
                versionId,
                cleanFileName,
                rawBytes,
                ct);

            // 2. Clean organized copy under GhostCode/<platform>/ (ptitcode, codeforces, vnoj, leetcode, hackerrank, offline)
            var categoryDir = Path.Combine(_config.ArchiveDir, SourceAnalyzer.GetCategoryFolder(activeContext));
            var latestFileName = ResolveNonCollidingLatestName(categoryDir, baseStem, targetExt, sha256, normHash, fullSource, activeContext);
            var latestCopyPath = Path.Combine(categoryDir, latestFileName);
            await WriteReplaceableCopyAtomicallyAsync(latestCopyPath, rawBytes, ct);

            // 3. Ready path points directly to the unified GhostCode/<platform>/ file;
            //    honor explicit custom ReadyDir only if configured separately (e.g., legacy tests).
            string readyPath = latestCopyPath;
            if (!string.IsNullOrWhiteSpace(_config.ReadyDir)
                && !string.Equals(Path.GetFullPath(_config.ReadyDir), Path.GetFullPath(categoryDir), StringComparison.OrdinalIgnoreCase))
            {
                readyPath = Path.Combine(_config.ReadyDir, latestFileName);
                await WriteReplaceableCopyAtomicallyAsync(readyPath, rawBytes, ct);
            }

            var snapshot = new FileSnapshotRecord(
                fullSource,
                sha256,
                normHash,
                rawBytes,
                encodingName,
                activeContext?.Code,
                activeContext?.Title,
                platformLabel,
                nowMs);

            var version = new ArchiveVersionRecord(
                Id: versionId,
                VersionNum: versionNum,
                SourcePath: fullSource,
                ArchivePath: historyPath,
                ReadyPath: readyPath,
                FileName: cleanFileName,
                Sha256: sha256,
                NormHash: normHash,
                SizeBytes: rawBytes.LongLength,
                ProblemCode: activeContext?.Code,
                ProblemTitle: activeContext?.Title,
                Platform: platformLabel,
                CreatedAtUnixMs: nowMs);

            var queueItem = new UploadQueueItem(
                VersionId: version.Id,
                Status: UploadStatus.Pending,
                AttemptCount: 0,
                NextRetryAtUnixMs: 0,
                DriveFileId: null,
                LastError: null,
                UpdatedAtUnixMs: nowMs);

            await _store.InsertVersionAndEnqueueAsync(snapshot, version, queueItem, ct);
            VersionArchived?.Invoke(version);

            return new ArchiveResult(
                ArchiveOutcome.CreatedVersion,
                version,
                $"Archived v{versionNum}: {cleanFileName}");
        }
        finally
        {
            _archiverLock.Release();
        }
    }

    /// <summary>
    /// Reads and validates exact historical bytes against expected byte count and SHA-256.
    /// Never falls back to a newer/older snapshot if the exact version file is missing or corrupted.
    /// </summary>
    public async Task<byte[]> ReadAndVerifyVersionBytesAsync(string versionId, CancellationToken ct = default)
    {
        var version = await _store.GetVersionByIdAsync(versionId, ct)
            ?? throw new InvalidOperationException($"Archive version '{versionId}' was not found in the database.");

        if (!version.IsLocalAvailable || version.ArchivePath.StartsWith("cloud://", StringComparison.OrdinalIgnoreCase))
        {
            throw new FileNotFoundException(
                $"Version v{version.VersionNum} ('{version.FileName}') exists only on Google Drive (Cloud-Only). Download it first before previewing, restoring, or running.",
                version.FileName);
        }

        var archiveFullPath = Path.GetFullPath(version.ArchivePath);
        if (!File.Exists(archiveFullPath))
        {
            throw new FileNotFoundException(
                $"Historical archive file for v{version.VersionNum} ('{version.FileName}') is missing at '{archiveFullPath}'. Restoration aborted to prevent substituting another version.",
                archiveFullPath);
        }

        var bytes = await File.ReadAllBytesAsync(archiveFullPath, ct);
        var actualSha = SourceAnalyzer.ComputeSha256Hex(bytes);
        if (bytes.LongLength != version.SizeBytes || !string.Equals(actualSha, version.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Integrity verification failed for v{version.VersionNum} ('{version.FileName}'): expected {version.SizeBytes} bytes (SHA-256 {version.Sha256}), got {bytes.LongLength} bytes (SHA-256 {actualSha}).");
        }

        return bytes;
    }

    public async Task RestoreVersionAsync(string versionId, string destinationPath, CancellationToken ct = default)
    {
        var version = await _store.GetVersionByIdAsync(versionId, ct)
            ?? throw new InvalidOperationException($"Archive version '{versionId}' was not found in the database.");
        var bytes = await ReadAndVerifyVersionBytesAsync(versionId, ct);
        await WriteReplaceableCopyAtomicallyAsync(destinationPath, bytes, ct);
        await _store.RecordArchiveHistoryEventAsync(new ArchiveHistoryEvent(
            Guid.NewGuid().ToString("N"),
            "VersionRestored",
            version.SourcePath,
            version.Id,
            $"Restored v{version.VersionNum} ({version.FileName}) -> {destinationPath}",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Environment.MachineName), ct);
    }

    /// <summary>
    /// Cleans leftover temporary files and registers orphaned historical archive files
    /// written before an unexpected termination prior to SQLite transaction commit.
    /// </summary>
    public async Task<int> ReconcileAfterCrashAsync(CancellationToken ct = default)
    {
        SweepTempFiles(_config.ArchiveDir);
        SweepTempFiles(_config.ReadyDir);

        int recovered = 0;
        var roots = new[] { _config.GetInternalHistoryRoot(), Path.Combine(_config.ArchiveDir, "History") }
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var historyRoot in roots)
        {
            foreach (var file in Directory.EnumerateFiles(historyRoot, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                var name = Path.GetFileName(file);
                if (name.StartsWith('.') || name.Contains(".tmp.", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (SourceAnalyzer.LanguageFromPath(file) is null)
                    continue;

                var fullArchive = Path.GetFullPath(file);
                if (await _store.GetVersionByArchivePathAsync(fullArchive, ct) is not null)
                    continue;

                byte[] bytes;
                try
                {
                    bytes = await File.ReadAllBytesAsync(fullArchive, ct);
                }
                catch
                {
                    continue;
                }

                if (bytes.Length == 0)
                    continue;

                var sha = SourceAnalyzer.ComputeSha256Hex(bytes);
                var (text, _) = SourceAnalyzer.DecodePreservingInfo(bytes);
                var normHash = SourceAnalyzer.ComputeNormalizedHash(text);
                int verNum = 1;
                var match = System.Text.RegularExpressions.Regex.Match(Path.GetFileNameWithoutExtension(name), @"_v(\d+)_");
                if (match.Success && int.TryParse(match.Groups[1].Value, out int parsedVer) && parsedVer > 0)
                    verNum = parsedVer;
                else
                {
                    var parentDirName = Path.GetFileName(Path.GetDirectoryName(fullArchive) ?? string.Empty);
                    var dirMatch = System.Text.RegularExpressions.Regex.Match(parentDirName, @"^v(\d+)_");
                    if (dirMatch.Success && int.TryParse(dirMatch.Groups[1].Value, out int dirVer) && dirVer > 0)
                        verNum = dirVer;
                }

                var cleanName = SourceAnalyzer.StripLegacyVersionAndHashSuffix(name);
                var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var rec = new ArchiveVersionRecord(
                    Id: Guid.NewGuid().ToString("N"),
                    VersionNum: verNum,
                    SourcePath: fullArchive,
                    ArchivePath: fullArchive,
                    ReadyPath: null,
                    FileName: cleanName,
                    Sha256: sha,
                    NormHash: normHash,
                    SizeBytes: bytes.LongLength,
                    ProblemCode: null,
                    ProblemTitle: "Recovered Orphan",
                    Platform: "Recovered",
                    CreatedAtUnixMs: nowMs);

                var queue = new UploadQueueItem(rec.Id, UploadStatus.Pending, 0, 0, null, null, nowMs);
                await _store.InsertRecoveredVersionAsync(rec, queue, ct);
                recovered++;
            }
        }

        return recovered;
    }

    private static void SweepTempFiles(string? root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(file);
                if (name.StartsWith(".tmp.", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains(".tmp.", StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(file); } catch { }
                }
            }
        }
        catch { }
    }

    public static async Task<(string Path, string FileName)> WriteCleanHistoricalFileAsync(
        string historyRoot,
        string dateFolder,
        int versionNum,
        string shortHash,
        string versionId,
        string cleanFileName,
        byte[] bytes,
        CancellationToken ct = default)
    {
        var baseSlot = System.IO.Path.Combine(historyRoot, dateFolder);
        Directory.CreateDirectory(baseSlot);
        var idTag = string.IsNullOrEmpty(versionId) ? "ver" : (versionId.Length >= 8 ? versionId[..8] : versionId);
        string[] slotDirs =
        {
            System.IO.Path.Combine(baseSlot, $"v{versionNum}_{shortHash}"),
            System.IO.Path.Combine(baseSlot, $"v{versionNum}_{shortHash}_{idTag}"),
            System.IO.Path.Combine(baseSlot, $"v{versionNum}_{shortHash}_{Guid.NewGuid():N}")
        };

        foreach (var slotDir in slotDirs)
        {
            Directory.CreateDirectory(slotDir);
            var targetPath = System.IO.Path.Combine(slotDir, cleanFileName);
            if (File.Exists(targetPath))
                continue;

            var tempPath = System.IO.Path.Combine(slotDir, $".tmp.{Guid.NewGuid():N}");
            try
            {
                await using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await fs.WriteAsync(bytes, ct);
                    await fs.FlushAsync(ct);
                    fs.Flush(flushToDisk: true);
                }

                try
                {
                    File.Move(tempPath, targetPath, overwrite: false);
                    return (targetPath, cleanFileName);
                }
                catch (IOException) when (File.Exists(targetPath))
                {
                    try { File.Delete(tempPath); } catch { }
                }
            }
            catch
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                throw;
            }
        }

        throw new IOException($"Unable to allocate unique immutable archive slot inside '{baseSlot}'.");
    }

    public static async Task<(string Path, string FileName)> WriteImmutableHistoricalFileAsync(
        string historyDir,
        string baseStem,
        int versionNum,
        string shortHash,
        string versionId,
        string ext,
        byte[] bytes,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(historyDir);
        var tempPath = System.IO.Path.Combine(historyDir, $".tmp.{Guid.NewGuid():N}");
        try
        {
            await using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await fs.WriteAsync(bytes, ct);
                await fs.FlushAsync(ct);
                fs.Flush(flushToDisk: true);
            }

            var idTag = string.IsNullOrEmpty(versionId) ? "ver" : (versionId.Length >= 8 ? versionId[..8] : versionId);
            string[] candidates =
            {
                $"{baseStem}_v{versionNum}_{shortHash}.{ext}",
                $"{baseStem}_v{versionNum}_{shortHash}_{idTag}.{ext}",
                $"{baseStem}_v{versionNum}_{shortHash}_{Guid.NewGuid():N}.{ext}"
            };

            foreach (var candidateName in candidates)
            {
                var candidatePath = System.IO.Path.Combine(historyDir, candidateName);
                try
                {
                    File.Move(tempPath, candidatePath, overwrite: false);
                    return (candidatePath, candidateName);
                }
                catch (IOException) when (File.Exists(candidatePath))
                {
                    // Never overwrite an existing historical archive file; try next unique name
                }
            }

            throw new IOException($"Unable to allocate unique immutable archive filename inside '{historyDir}'.");
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            throw;
        }
    }

    public static Task WriteBytesAtomicallyAsync(string targetPath, byte[] bytes, CancellationToken ct = default) =>
        WriteReplaceableCopyAtomicallyAsync(targetPath, bytes, ct);

    public static async Task WriteReplaceableCopyAtomicallyAsync(string targetPath, byte[] bytes, CancellationToken ct = default)
    {
        var fullTarget = Path.GetFullPath(targetPath);
        var dir = Path.GetDirectoryName(fullTarget);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tempPath = $"{fullTarget}.tmp.{Guid.NewGuid():N}";
        try
        {
            await using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await fs.WriteAsync(bytes, ct);
                await fs.FlushAsync(ct);
                fs.Flush(flushToDisk: true);
            }
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    File.Move(tempPath, fullTarget, overwrite: true);
                    return;
                }
                catch (Exception ex) when (attempt < 4 && (ex is IOException || ex is UnauthorizedAccessException))
                {
                    await Task.Delay(40 * (attempt + 1), ct);
                }
            }
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            throw;
        }
    }

    private static string ResolveNonCollidingLatestName(
        string categoryDir,
        string baseStem,
        string ext,
        string sha256,
        string normHash,
        string sourcePath,
        ProblemContext? activeContext = null)
    {
        // Clean user-facing filename first: ProblemID_Title.ext
        var primary = $"{baseStem}.{ext}";
        var primaryFull = Path.Combine(categoryDir, primary);
        if (!File.Exists(primaryFull))
            return primary;

        if (activeContext is not null)
        {
            // When associated with an explicit problem context, always keep its organized clean filename up to date
            // (all immutable previous versions live safely in .ghostcode/history/).
            return primary;
        }

        for (int idx = 1; idx <= 99; idx++)
        {
            var candidate = idx == 1 ? primary : $"{baseStem}_{idx}.{ext}";
            var full = Path.Combine(categoryDir, candidate);
            if (!File.Exists(full))
                return candidate;

            try
            {
                var existingBytes = File.ReadAllBytes(full);
                if (SourceAnalyzer.ComputeSha256Hex(existingBytes) == sha256)
                    return candidate;
                var (existingText, _) = SourceAnalyzer.DecodePreservingInfo(existingBytes);
                if (SourceAnalyzer.ComputeNormalizedHash(existingText) == normHash)
                    return candidate;

                if (baseStem.EndsWith("_" + SourceAnalyzer.SafeStem(Path.GetFileNameWithoutExtension(sourcePath)), StringComparison.OrdinalIgnoreCase))
                    return candidate;
            }
            catch
            {
                return candidate;
            }
        }
        return primary;
    }
}
