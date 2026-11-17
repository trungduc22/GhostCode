using System.Text.Json;
using GhostCode.Core.Analysis;
using GhostCode.Core.Models;
using GhostCode.Core.Monitoring;
using GhostCode.Core.Storage;

namespace GhostCode.Core.Archiver;

public sealed record UnifiedMigrationReport(
    bool Executed,
    int MigratedVersionsCount,
    int VerifiedFilesCount,
    int MissingSourceCount,
    string TargetRoot,
    string Summary);

/// <summary>
/// Safely migrates legacy GhostCodeArchive / CodePTIT_Ready / _vN_hash archives
/// into the unified GhostCode/{platform}/.ghostcode layout without deleting originals.
/// </summary>
public static class UnifiedArchiveMigrationService
{
    public static async Task<UnifiedMigrationReport> MigrateIfNeededAsync(
        GhostCodeConfig config,
        string? configPath,
        SqliteArchiveStore store,
        CancellationToken ct = default)
    {
        var oldArchiveDir = config.ArchiveDir;
        var targetRoot = ResolveTargetUnifiedRoot(oldArchiveDir);
        var markerPath = Path.Combine(targetRoot, ".ghostcode", "migration_v1.json");

        var versions = await store.QueryVersionsAsync(limit: 50_000, ct: ct);
        bool needsMigration = !string.Equals(Path.GetFullPath(oldArchiveDir), Path.GetFullPath(targetRoot), StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrWhiteSpace(config.ReadyDir)
            || versions.Any(v => NeedsVersionMigration(v, targetRoot));

        if (!needsMigration && File.Exists(markerPath))
            return new UnifiedMigrationReport(false, 0, 0, 0, targetRoot, "Already unified.");

        Directory.CreateDirectory(targetRoot);
        Directory.CreateDirectory(Path.Combine(targetRoot, ".ghostcode", "history"));
        foreach (var cat in new[] { "ptitcode", "codeforces", "vnoj", "leetcode", "hackerrank", "offline" })
            Directory.CreateDirectory(Path.Combine(targetRoot, cat));

        var updates = new List<(string VersionId, string NewArchivePath, string? NewReadyPath, string NewFileName, string SourcePath)>();
        var latestByGroup = new Dictionary<string, (ArchiveVersionRecord Ver, string CleanName, byte[] Bytes)>(StringComparer.OrdinalIgnoreCase);

        int verified = 0;
        int missing = 0;
        var historyRoot = Path.Combine(targetRoot, ".ghostcode", "history");

        foreach (var v in versions.OrderBy(x => x.VersionNum).ThenBy(x => x.CreatedAtUnixMs))
        {
            ct.ThrowIfCancellationRequested();
            var cleanName = ComputeCleanFileName(v);
            var groupKey = SourceAnalyzer.BuildLogicalGroupKey(v);

            if (!v.IsLocalAvailable || string.IsNullOrWhiteSpace(v.ArchivePath) || v.ArchivePath.StartsWith("cloud://", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(v.FileName, cleanName, StringComparison.Ordinal))
                    updates.Add((v.Id, v.ArchivePath, v.ReadyPath, cleanName, v.SourcePath));
                continue;
            }

            if (!File.Exists(v.ArchivePath))
            {
                missing++;
                continue;
            }

            var bytes = await File.ReadAllBytesAsync(v.ArchivePath, ct);
            var actualSha = SourceAnalyzer.ComputeSha256Hex(bytes);
            if (!string.Equals(actualSha, v.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                missing++;
                continue;
            }

            var shortHash = actualSha[..Math.Min(8, actualSha.Length)];
            var dateFolder = DateTimeOffset.FromUnixTimeMilliseconds(v.CreatedAtUnixMs).ToLocalTime().ToString("yyyy-MM-dd");
            string targetArchivePath = v.ArchivePath;

            if (!IsInsideDirectory(v.ArchivePath, historyRoot) || !string.Equals(Path.GetFileName(v.ArchivePath), cleanName, StringComparison.Ordinal))
            {
                var (copiedPath, _) = await LosslessArchiver.WriteCleanHistoricalFileAsync(
                    historyRoot,
                    dateFolder,
                    v.VersionNum,
                    shortHash,
                    v.Id,
                    cleanName,
                    bytes,
                    ct);
                var copiedSha = SourceAnalyzer.ComputeSha256Hex(await File.ReadAllBytesAsync(copiedPath, ct));
                if (!string.Equals(copiedSha, v.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException($"SHA-256 verification failed during migration for '{copiedPath}'.");
                targetArchivePath = copiedPath;
            }

            var category = SourceAnalyzer.GetCategoryFolderForPlatform(v.Platform);
            var organizedPath = Path.Combine(targetRoot, category, cleanName);
            latestByGroup[groupKey] = (v, cleanName, bytes);
            verified++;

            if (!string.Equals(targetArchivePath, v.ArchivePath, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(organizedPath, v.ReadyPath, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(cleanName, v.FileName, StringComparison.Ordinal))
            {
                updates.Add((v.Id, targetArchivePath, organizedPath, cleanName, v.SourcePath));
            }
        }

        // Write latest clean organized copy for each problem/source into GhostCode/<platform>/
        foreach (var (_, item) in latestByGroup)
        {
            var category = SourceAnalyzer.GetCategoryFolderForPlatform(item.Ver.Platform);
            var organizedPath = Path.Combine(targetRoot, category, item.CleanName);
            await LosslessArchiver.WriteReplaceableCopyAtomicallyAsync(organizedPath, item.Bytes, ct);
            var orgSha = SourceAnalyzer.ComputeSha256Hex(await File.ReadAllBytesAsync(organizedPath, ct));
            if (!string.Equals(orgSha, item.Ver.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"SHA-256 verification failed for organized copy '{organizedPath}'.");
        }

        int applied = await store.ApplyMigratedVersionsAsync(updates, ct);

        config.ArchiveDir = targetRoot;
        config.ReadyDir = string.Empty;
        if (!string.IsNullOrWhiteSpace(configPath))
        {
            try { config.Save(configPath); } catch { }
        }

        try
        {
            var receipt = JsonSerializer.Serialize(new
            {
                migrated_at_utc = DateTimeOffset.UtcNow,
                previous_archive_dir = oldArchiveDir,
                target_root = targetRoot,
                migrated_versions = applied,
                verified_files = verified,
                missing_files = missing
            }, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(markerPath, receipt, ct);
        }
        catch { }

        return new UnifiedMigrationReport(
            true,
            applied,
            verified,
            missing,
            targetRoot,
            $"Migrated {applied} version(s), verified {verified} SHA-256 file(s).");
    }

    private static string ResolveTargetUnifiedRoot(string currentArchiveDir)
    {
        if (string.IsNullOrWhiteSpace(currentArchiveDir))
            return Path.Combine(KnownFoldersResolver.ResolveDefaultStorageBase(), "GhostCode");

        var trimmed = Path.GetFullPath(currentArchiveDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var folderName = Path.GetFileName(trimmed);
        if (string.Equals(folderName, "GhostCodeArchive", StringComparison.OrdinalIgnoreCase))
        {
            var parent = Path.GetDirectoryName(trimmed);
            if (!string.IsNullOrEmpty(parent))
                return Path.Combine(parent, "GhostCode");
        }
        return trimmed;
    }

    private static bool NeedsVersionMigration(ArchiveVersionRecord v, string targetRoot)
    {
        var historyRoot = Path.Combine(targetRoot, ".ghostcode", "history");
        if (v.IsLocalAvailable && !string.IsNullOrWhiteSpace(v.ArchivePath) && !v.ArchivePath.StartsWith("cloud://", StringComparison.OrdinalIgnoreCase))
        {
            if (!IsInsideDirectory(v.ArchivePath, historyRoot))
                return true;
        }
        var clean = ComputeCleanFileName(v);
        return !string.Equals(v.FileName, clean, StringComparison.Ordinal);
    }

    private static string ComputeCleanFileName(ArchiveVersionRecord v)
    {
        if (!string.IsNullOrWhiteSpace(v.ProblemCode))
        {
            var ext = Path.GetExtension(v.FileName).TrimStart('.');
            if (string.IsNullOrWhiteSpace(ext)) ext = "cpp";
            var normTitle = SourceAnalyzer.NormalizeTitle(v.ProblemTitle ?? string.Empty);
            var stem = string.IsNullOrEmpty(normTitle)
                ? SourceAnalyzer.SafeStem(v.ProblemCode)
                : SourceAnalyzer.SafeStem($"{v.ProblemCode}_{normTitle}");
            return $"{stem}.{ext}";
        }
        return SourceAnalyzer.StripLegacyVersionAndHashSuffix(v.FileName);
    }

    private static bool IsInsideDirectory(string candidatePath, string dirPath)
    {
        var fullCand = Path.GetFullPath(candidatePath);
        var fullDir = Path.GetFullPath(dirPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullCand.StartsWith(fullDir, StringComparison.OrdinalIgnoreCase);
    }
}
