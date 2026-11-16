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
            var latest = await _store.GetLatestVersionForSourceAsync(fullSource, ct);

            // Empty or template boilerplate reset: NEVER clobber or erase the previous good snapshot!
            if (SourceAnalyzer.IsTrivialOrTemplate(text, lang.Value))
            {
                if (latest is not null)
                {
                    return new ArchiveResult(
                        ArchiveOutcome.PreservedPreviousOnReset,
                        latest,
                        $"Preserved previous v{latest.VersionNum} ({latest.FileName}) after template/empty reset");
                }
                return new ArchiveResult(ArchiveOutcome.UnsupportedFile, null, "Empty or boilerplate template ignored");
            }

            var sha256 = SourceAnalyzer.ComputeSha256Hex(rawBytes);
            var normHash = SourceAnalyzer.ComputeNormalizedHash(text);

            // Exact byte deduplication OR whitespace-only duplicate of the immediately preceding version
            if (latest is not null && (latest.Sha256 == sha256 || latest.NormHash == normHash))
            {
                return new ArchiveResult(ArchiveOutcome.Deduplicated, latest, $"Duplicate content (v{latest.VersionNum})");
            }

            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var versionNum = (latest?.VersionNum ?? 0) + 1;
            var targetExt = SourceAnalyzer.TargetExtension(activeContext, lang.Value, text);
            var baseStem = SourceAnalyzer.BuildProblemBaseName(activeContext, fullSource, text, lang.Value);
            var shortHash = sha256[..8];
            var versionId = Guid.NewGuid().ToString("N");

            // 1. Strictly immutable historical version (exclusive create, never overwrites existing historical files)
            var dateFolder = DateTime.Now.ToString("yyyy-MM-dd");
            var historyDir = Path.Combine(_config.ArchiveDir, "History", dateFolder);
            var (historyPath, historyFileName) = await WriteImmutableHistoricalFileAsync(
                historyDir,
                baseStem,
                versionNum,
                shortHash,
                versionId,
                targetExt,
                rawBytes,
                ct);

            // 2. Replaceable latest clean copy in category directory (collision-safe for distinct files)
            var categoryDir = Path.Combine(_config.ArchiveDir, SourceAnalyzer.GetCategoryFolder(activeContext));
            var latestFileName = ResolveNonCollidingLatestName(categoryDir, baseStem, targetExt, sha256, normHash, fullSource);
            var latestCopyPath = Path.Combine(categoryDir, latestFileName);
            await WriteReplaceableCopyAtomicallyAsync(latestCopyPath, rawBytes, ct);

            // 3. Replaceable submission copy (`CodePTIT_Ready`) updated atomically for immediate upload/copy
            string? readyPath = null;
            if (!string.IsNullOrWhiteSpace(_config.ReadyDir))
            {
                readyPath = Path.Combine(_config.ReadyDir, latestFileName);
                await WriteReplaceableCopyAtomicallyAsync(readyPath, rawBytes, ct);
            }

            var platformLabel = activeContext?.Platform.ToString() ?? OjPlatform.Offline.ToString();
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
                FileName: historyFileName,
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
                $"Archived v{versionNum}: {historyFileName}");
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
        var historyRoot = Path.Combine(_config.ArchiveDir, "History");
        if (!Directory.Exists(historyRoot))
            return 0;

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

            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var rec = new ArchiveVersionRecord(
                Id: Guid.NewGuid().ToString("N"),
                VersionNum: verNum,
                SourcePath: fullArchive,
                ArchivePath: fullArchive,
                ReadyPath: null,
                FileName: name,
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

            string[] candidates =
            {
                $"{baseStem}_v{versionNum}_{shortHash}.{ext}",
                $"{baseStem}_v{versionNum}_{shortHash}_{versionId[..8]}.{ext}",
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
        string sourcePath)
    {
        // Always group distinctly by source stem when offline so two different scratch files don't collide.
        for (int idx = 1; idx <= 99; idx++)
        {
            var candidate = idx == 1 ? $"{baseStem}.{ext}" : $"{baseStem}_v{idx}.{ext}";
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

                // If same source file is updating its own latest clean copy, allow updating in place
                // (all historical versions remain immutable under ArchiveDir/History/).
                if (baseStem.EndsWith("_" + SourceAnalyzer.SafeStem(Path.GetFileNameWithoutExtension(sourcePath)), StringComparison.OrdinalIgnoreCase))
                    return candidate;
            }
            catch
            {
                return candidate;
            }
        }
        return $"{baseStem}_{sha256[..6]}.{ext}";
    }
}
