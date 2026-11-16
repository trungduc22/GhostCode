using System.Security.Cryptography;
using System.Text;
using GhostCode.Core.Archiver;
using GhostCode.Core.Models;
using GhostCode.Core.Storage;

namespace GhostCode.Core.Cloud;

/// <summary>
/// Shared OAuth PKCE, exponential-backoff, SHA-256 verification, and cloud restore utilities
/// used across cloud storage providers without duplicating archival logic.
/// </summary>
public static class CloudTransferHelpers
{
    public static (string Verifier, string Challenge, string State) CreatePkceParameters()
    {
        var verifier = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64UrlEncode(RandomNumberGenerator.GetBytes(16));
        return (verifier, challenge, state);
    }

    public static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static Dictionary<string, string> ParseQueryString(string query)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var q = query.TrimStart('?');
        foreach (var pair in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = pair.IndexOf('=');
            if (idx < 0) continue;
            var k = Uri.UnescapeDataString(pair[..idx]);
            var v = Uri.UnescapeDataString(pair[(idx + 1)..]);
            dict[k] = v;
        }
        return dict;
    }

    public static long ComputeNextRetryUnixMs(int currentAttempts)
    {
        var exponent = Math.Clamp(currentAttempts, 0, 7);
        var delaySeconds = Math.Min(3600, 15 * (1 << exponent));
        return DateTimeOffset.UtcNow.AddSeconds(delaySeconds).ToUnixTimeMilliseconds();
    }

    public static async Task<DriveDownloadResult> VerifyAndPersistDownloadedVersionAsync(
        GhostCodeConfig config,
        SqliteArchiveStore store,
        ArchiveVersionRecord version,
        byte[] downloadedBytes,
        CancellationToken ct = default)
    {
        var actualSha = Convert.ToHexString(SHA256.HashData(downloadedBytes)).ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(version.Sha256)
            && !string.Equals(actualSha, version.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return new DriveDownloadResult(
                false,
                null,
                $"Downloaded SHA-256 mismatch for {version.FileName}: expected {version.Sha256}, got {actualSha}.");
        }

        var historyDir = Path.Combine(config.ArchiveDir, "History");
        var stem = Path.GetFileNameWithoutExtension(version.FileName);
        if (string.IsNullOrWhiteSpace(stem)) stem = "restored";
        var ext = Path.GetExtension(version.FileName).TrimStart('.');
        if (string.IsNullOrWhiteSpace(ext)) ext = "txt";
        var shortHash = actualSha[..Math.Min(8, actualSha.Length)];

        var (archivePath, _) = await LosslessArchiver.WriteImmutableHistoricalFileAsync(
            historyDir,
            stem,
            version.VersionNum,
            shortHash,
            version.Id,
            ext,
            downloadedBytes,
            ct);

        await store.MarkVersionDownloadedLocallyAsync(version.Id, archivePath, ct);
        return new DriveDownloadResult(true, archivePath, null);
    }

    public static async Task<(int DownloadedCount, int FailedCount, string? LastError)> DownloadAllCloudOnlyVersionsAsync(
        ICloudArchiveProvider provider,
        SqliteArchiveStore store,
        IProgress<DriveSyncProgress>? progress = null,
        CancellationToken ct = default)
    {
        var all = await store.QueryVersionsAsync(limit: 10_000, ct: ct);
        var pending = all.Where(v => !v.IsLocalAvailable && !string.IsNullOrWhiteSpace(v.DriveFileId)).ToList();
        int ok = 0, failed = 0;
        string? lastError = null;
        for (int i = 0; i < pending.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new DriveSyncProgress(i, pending.Count, pending[i].FileName, (double)i / Math.Max(1, pending.Count) * 100.0));
            var res = await provider.DownloadVersionAsync(pending[i].Id, ct);
            if (res.Success) ok++;
            else { failed++; lastError = res.Error; }
        }
        progress?.Report(new DriveSyncProgress(pending.Count, pending.Count, string.Empty, 100.0));
        return (ok, failed, lastError);
    }
}
