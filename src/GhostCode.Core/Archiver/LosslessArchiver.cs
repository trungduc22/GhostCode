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

            // 1. Immutable historical version (exact lossless bytes, never overwritten)
            var dateFolder = DateTime.Now.ToString("yyyy-MM-dd");
            var historyDir = Path.Combine(_config.ArchiveDir, "History", dateFolder);
            var historyFileName = $"{baseStem}_v{versionNum}_{shortHash}.{targetExt}";
            var historyPath = Path.Combine(historyDir, historyFileName);
            await WriteBytesAtomicallyAsync(historyPath, rawBytes, ct);

            // 2. Clean named latest copy in category directory (collision-safe for distinct files)
            var categoryDir = Path.Combine(_config.ArchiveDir, SourceAnalyzer.GetCategoryFolder(activeContext));
            var latestFileName = ResolveNonCollidingLatestName(categoryDir, baseStem, targetExt, sha256, normHash, fullSource);
            var latestCopyPath = Path.Combine(categoryDir, latestFileName);
            await WriteBytesAtomicallyAsync(latestCopyPath, rawBytes, ct);

            // 3. Ready submission copy (`CodePTIT_Ready`) updated atomically for immediate upload/copy
            string? readyPath = null;
            if (!string.IsNullOrWhiteSpace(_config.ReadyDir))
            {
                readyPath = Path.Combine(_config.ReadyDir, latestFileName);
                await WriteBytesAtomicallyAsync(readyPath, rawBytes, ct);
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
                Id: Guid.NewGuid().ToString("N"),
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

    public async Task RestoreVersionAsync(string versionId, string destinationPath, CancellationToken ct = default)
    {
        var version = await _store.GetVersionByIdAsync(versionId, ct)
            ?? throw new InvalidOperationException($"Archive version '{versionId}' was not found.");

        byte[] bytes;
        if (File.Exists(version.ArchivePath))
        {
            bytes = await File.ReadAllBytesAsync(version.ArchivePath, ct);
        }
        else
        {
            var snap = await _store.GetSnapshotAsync(version.SourcePath, ct)
                ?? throw new FileNotFoundException($"Archive file missing at '{version.ArchivePath}' and no snapshot fallback available.");
            bytes = snap.RawBytes;
        }

        await WriteBytesAtomicallyAsync(destinationPath, bytes, ct);
    }

    public static async Task WriteBytesAtomicallyAsync(string targetPath, byte[] bytes, CancellationToken ct = default)
    {
        var fullTarget = Path.GetFullPath(targetPath);
        var dir = Path.GetDirectoryName(fullTarget);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tempPath = $"{fullTarget}.tmp.{Guid.NewGuid():N}";
        try
        {
            await using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, useAsync: true))
            {
                await fs.WriteAsync(bytes, ct);
                await fs.FlushAsync(ct);
            }
            File.Move(tempPath, fullTarget, overwrite: true);
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
