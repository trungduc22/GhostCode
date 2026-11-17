using GhostCode.Core.Models;

namespace GhostCode.Core.Cloud;

public sealed record CloudObjectMetadata(
    string RemoteId,
    string Name,
    long SizeBytes,
    string? Sha256,
    long ModifiedUnixMs);

public sealed record CloudAccountInfo(
    CloudProviderKind Provider,
    bool IsConnected,
    string AccountLabel,
    string? Detail);

/// <summary>
/// Provider-agnostic cloud archive contract enabling independent Google Drive and
/// Microsoft OneDrive synchronization plus non-destructive cross-provider migration.
/// </summary>
public interface ICloudArchiveProvider
{
    CloudProviderKind ProviderKind { get; }
    string DisplayName { get; }
    bool IsConfigured { get; }

    Task<CloudAccountInfo> GetAccountInfoAsync(CancellationToken ct = default);
    Task<DisconnectResult> DisconnectAsync(CancellationToken ct = default);
    Task<DriveSyncReport> SyncPendingAsync(IProgress<DriveSyncProgress>? progress = null, CancellationToken ct = default);
    Task<DrivePullReport> PullAndReconcileAsync(IProgress<DriveSyncProgress>? progress = null, CancellationToken ct = default);
    Task<DriveDownloadResult> DownloadVersionAsync(string versionId, CancellationToken ct = default);
    Task<(DriveSyncReport PushReport, DrivePullReport PullReport)> SyncBidirectionalAsync(IProgress<DriveSyncProgress>? progress = null, CancellationToken ct = default);
    Task<bool> PublishManifestAsync(CancellationToken ct = default);
    Task<IReadOnlyList<CloudObjectMetadata>> ListRemoteObjectsAsync(CancellationToken ct = default);
    Task<byte[]> DownloadRawBytesAsync(string remoteId, CancellationToken ct = default);
    Task<string> UploadVersionBytesAsync(ArchiveVersionRecord version, byte[] rawBytes, CancellationToken ct = default);
    Task<bool> DeleteRemoteFileAsync(string remoteId, CancellationToken ct = default);
}
