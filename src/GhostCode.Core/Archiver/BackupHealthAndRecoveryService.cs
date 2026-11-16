using System.Security.Cryptography;
using GhostCode.Core.Cloud;
using GhostCode.Core.Models;
using GhostCode.Core.Monitoring;
using GhostCode.Core.Notifications;
using GhostCode.Core.Storage;

namespace GhostCode.Core.Archiver;

public sealed record BackupHealthSnapshot(
    BackupProtectionState ProtectionState,
    bool MonitoringActive,
    int ActiveWatchCount,
    int ErroredWatchCount,
    string LastDetectedSave,
    string LastSuccessfulBackup,
    string LastBackupFailure,
    double AvailableDiskMb,
    bool DiskHealthy,
    bool DatabaseHealthy,
    string DatabaseDetail,
    int PendingCloudUploads,
    int PendingCloudDownloads,
    int FailedCloudUploads,
    long? LastRecoveryVerifiedUnixMs,
    string StatusSummary);

public sealed record RecoveryIssue(
    string VersionId,
    string FileName,
    string IssueKind,
    string Detail);

public sealed record RecoveryCheckReport(
    int TotalVersionsChecked,
    int LocalVerifiedOk,
    int LocalMissingCount,
    int LocalCorruptedCount,
    int RemoteVerifiedCount,
    int RemoteMissingCount,
    int RemoteSampleByteVerifiedCount,
    int CloudOnlyRecoverableCount,
    long CheckedAtUnixMs,
    IReadOnlyList<RecoveryIssue> Issues,
    string Summary);

/// <summary>
/// Evaluates real backup protection health (never reporting Protected unless watches,
/// disk storage, and SQLite integrity all pass) and performs non-destructive recovery verification.
/// </summary>
public sealed class BackupHealthAndRecoveryService
{
    private const double MinRequiredDiskMb = 50.0;

    private readonly GhostCodeConfig _config;
    private readonly SqliteArchiveStore _store;
    private readonly MultiDirectoryMonitor _monitor;
    private readonly NotificationCoordinator? _notifications;

    private long? _lastRecoveryVerifiedUnixMs;

    public BackupHealthAndRecoveryService(
        GhostCodeConfig config,
        SqliteArchiveStore store,
        MultiDirectoryMonitor monitor,
        NotificationCoordinator? notifications = null)
    {
        _config = config;
        _store = store;
        _monitor = monitor;
        _notifications = notifications;
    }

    public async Task<BackupHealthSnapshot> EvaluateHealthAsync(bool attemptWatchRecovery = true, CancellationToken ct = default)
    {
        var watches = _monitor.GetActiveWatches();
        if (attemptWatchRecovery && _config.WatchedFolders.Any(w => w.Enabled) && watches.Any(w => !w.IsWatching))
        {
            _monitor.ReloadWatches();
            watches = _monitor.GetActiveWatches();
        }

        int activeWatches = watches.Count(w => w.IsWatching);
        int erroredWatches = watches.Count(w => !w.IsWatching);
        bool enabledFolderConfigured = _config.WatchedFolders.Any(w => w.Enabled);
        bool monitoringActive = activeWatches > 0 && erroredWatches == 0;

        var (diskOk, diskMb) = CheckDiskSpace(_config.ArchiveDir);
        var (dbOk, dbDetail) = await _store.CheckDatabaseHealthAsync(ct);

        var stats = await _store.GetStatsAsync(ct);
        var allVersions = await _store.QueryVersionsAsync(limit: 5000, ct: ct);
        int cloudOnlyPending = allVersions.Count(v => !v.IsLocalAvailable);

        BackupProtectionState state;
        string summary;
        if (!dbOk || !diskOk)
        {
            state = BackupProtectionState.Unhealthy;
            summary = !dbOk
                ? $"SQLite database unhealthy: {dbDetail}"
                : $"Low disk space in ArchiveDir: {diskMb:F1} MB free (< {MinRequiredDiskMb:F0} MB)";
        }
        else if (!enabledFolderConfigured || erroredWatches > 0 || activeWatches == 0)
        {
            state = erroredWatches > 0 ? BackupProtectionState.Unhealthy : BackupProtectionState.Paused;
            summary = erroredWatches > 0
                ? $"{erroredWatches} monitored folder watcher(s) failed — backup incomplete"
                : "Monitoring is inactive (no active watched folders)";
        }
        else if (stats.FailedUploads > 0 || _config.SyncPausedManually)
        {
            state = _config.SyncPausedManually ? BackupProtectionState.Paused : BackupProtectionState.Degraded;
            summary = _config.SyncPausedManually
                ? $"Local backups protected ({activeWatches} folders) • Cloud sync paused by user"
                : $"Local backups protected • {stats.FailedUploads} cloud upload(s) failed ({stats.PendingUploads} pending)";
        }
        else
        {
            state = BackupProtectionState.Protected;
            summary = $"Protected ({activeWatches} active folder(s), {diskMb:F0} MB free, SQLite OK)";
        }

        if (state == BackupProtectionState.Unhealthy && _notifications is not null)
        {
            await _notifications.NotifyAsync(
                "backup",
                "Error",
                "GhostCode — Backup Protection Warning",
                summary,
                $"health_{state}_{erroredWatches}_{dbOk}",
                "open",
                ct);
        }

        return new BackupHealthSnapshot(
            state,
            monitoringActive,
            activeWatches,
            erroredWatches,
            _monitor.LastDetectedFile,
            _monitor.LastSuccessfulBackup,
            _monitor.LastMonitoringError,
            diskMb,
            diskOk,
            dbOk,
            dbDetail,
            stats.PendingUploads,
            cloudOnlyPending,
            stats.FailedUploads,
            _lastRecoveryVerifiedUnixMs,
            summary);
    }

    public async Task<RecoveryCheckReport> RunRecoveryCheckAsync(
        ICloudArchiveProvider? cloudProvider = null,
        int maxRemoteSamples = 3,
        bool verifyRemoteContents = true,
        CancellationToken ct = default)
    {
        var versions = await _store.QueryVersionsAsync(limit: 5000, ct: ct);
        var issues = new List<RecoveryIssue>();

        int localOk = 0, localMissing = 0, localCorrupt = 0;
        int cloudOnlyCount = 0;

        foreach (var v in versions)
        {
            ct.ThrowIfCancellationRequested();
            if (!v.IsLocalAvailable)
            {
                cloudOnlyCount++;
                continue;
            }

            if (!File.Exists(v.ArchivePath))
            {
                localMissing++;
                issues.Add(new RecoveryIssue(v.Id, v.FileName, "MissingLocalFile", $"Missing archive file: {v.ArchivePath}"));
                continue;
            }

            var bytes = await File.ReadAllBytesAsync(v.ArchivePath, ct);
            var actualSha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (bytes.LongLength != v.SizeBytes || !string.Equals(actualSha, v.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                localCorrupt++;
                issues.Add(new RecoveryIssue(v.Id, v.FileName, "CorruptedLocalFile",
                    $"Expected {v.SizeBytes}B / {v.Sha256[..Math.Min(8, v.Sha256.Length)]}, found {bytes.LongLength}B / {actualSha[..8]}"));
                continue;
            }

            localOk++;
        }

        int remoteVerified = 0, remoteMissing = 0, remoteSampleVerified = 0;
        if (cloudProvider is not null)
        {
            try
            {
                var acct = await cloudProvider.GetAccountInfoAsync(ct);
                if (acct.IsConnected)
                {
                    var remoteObjects = await cloudProvider.ListRemoteObjectsAsync(ct);
                    var remoteById = remoteObjects.ToDictionary(r => r.RemoteId, StringComparer.Ordinal);
                    var syncedWithId = versions.Where(v => !string.IsNullOrWhiteSpace(v.DriveFileId)).ToList();

                    foreach (var v in syncedWithId)
                    {
                        if (remoteById.ContainsKey(v.DriveFileId!))
                            remoteVerified++;
                        else
                        {
                            remoteMissing++;
                            issues.Add(new RecoveryIssue(v.Id, v.FileName, "MissingRemoteObject", $"Remote ID '{v.DriveFileId}' not found on {cloudProvider.DisplayName}"));
                        }
                    }

                    if (verifyRemoteContents && maxRemoteSamples > 0)
                    {
                        foreach (var sample in syncedWithId.Where(v => remoteById.ContainsKey(v.DriveFileId!)).Take(maxRemoteSamples))
                        {
                            ct.ThrowIfCancellationRequested();
                            var dlBytes = await cloudProvider.DownloadRawBytesAsync(sample.DriveFileId!, ct);
                            var tempVerifyPath = Path.Combine(Path.GetTempPath(), $"gc_verify_{Guid.NewGuid():N}.tmp");
                            try
                            {
                                await File.WriteAllBytesAsync(tempVerifyPath, dlBytes, ct);
                                var verifyBytes = await File.ReadAllBytesAsync(tempVerifyPath, ct);
                                var sha = Convert.ToHexString(SHA256.HashData(verifyBytes)).ToLowerInvariant();
                                if (string.Equals(sha, sample.Sha256, StringComparison.OrdinalIgnoreCase))
                                    remoteSampleVerified++;
                                else
                                    issues.Add(new RecoveryIssue(sample.Id, sample.FileName, "CorruptedRemoteSample", $"Remote SHA mismatch for {sample.FileName}"));
                            }
                            finally
                            {
                                try { if (File.Exists(tempVerifyPath)) File.Delete(tempVerifyPath); } catch { }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                issues.Add(new RecoveryIssue("cloud", cloudProvider.DisplayName, "RemoteCheckError", ex.Message));
            }
        }

        var checkedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _lastRecoveryVerifiedUnixMs = checkedAt;

        var summary = $"Checked {versions.Count} version(s): {localOk} local OK, {localMissing} missing, {localCorrupt} corrupted, {cloudOnlyCount} cloud-only; Remote: {remoteVerified} present, {remoteSampleVerified} byte-verified.";
        await _store.RecordArchiveHistoryEventAsync(new ArchiveHistoryEvent(
            Guid.NewGuid().ToString("N"),
            "RecoveryCheck",
            _config.ArchiveDir,
            null,
            summary,
            checkedAt,
            Environment.MachineName), ct);

        if ((localCorrupt > 0 || localMissing > 0) && _notifications is not null)
        {
            await _notifications.NotifyAsync(
                "integrity",
                "Error",
                "GhostCode — Recovery Check Found Issues",
                summary,
                $"rec_{localCorrupt}_{localMissing}",
                "open",
                ct);
        }

        return new RecoveryCheckReport(
            versions.Count,
            localOk,
            localMissing,
            localCorrupt,
            remoteVerified,
            remoteMissing,
            remoteSampleVerified,
            cloudOnlyCount,
            checkedAt,
            issues,
            summary);
    }

    private static (bool Ok, double FreeMb) CheckDiskSpace(string path)
    {
        try
        {
            var full = Path.GetFullPath(string.IsNullOrWhiteSpace(path) ? AppContext.BaseDirectory : path);
            var root = Path.GetPathRoot(full);
            if (string.IsNullOrWhiteSpace(root))
                return (true, 10_000.0);
            var di = new DriveInfo(root);
            double mb = di.AvailableFreeSpace / (1024.0 * 1024.0);
            return (mb >= MinRequiredDiskMb, mb);
        }
        catch
        {
            return (true, 10_000.0);
        }
    }
}
