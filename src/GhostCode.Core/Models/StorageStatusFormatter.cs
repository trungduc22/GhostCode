namespace GhostCode.Core.Models;

public enum StorageStatusKind
{
    Local,
    Synced,
    Pending,
    CloudOnly,
    SyncError,
    Conflict
}

/// <summary>
/// Single source of truth for compact, semantically honest archive storage labels
/// and provider-aware tooltips across Archive list rows, version metadata, and Trash.
/// </summary>
public static class StorageStatusFormatter
{
    public static StorageStatusKind ResolveKind(VersionAvailability availability, UploadStatus? uploadStatus)
    {
        if (availability == VersionAvailability.CloudOnly)
            return StorageStatusKind.CloudOnly;
        if (availability == VersionAvailability.Conflict)
            return StorageStatusKind.Conflict;
        if (availability == VersionAvailability.Synced)
            return StorageStatusKind.Synced;
        if (uploadStatus == UploadStatus.Failed || availability == VersionAvailability.Error)
            return StorageStatusKind.SyncError;
        if (uploadStatus is UploadStatus.Pending or UploadStatus.Uploading)
            return StorageStatusKind.Pending;
        return StorageStatusKind.Local;
    }

    public static string GetShortLabel(VersionAvailability availability, UploadStatus? uploadStatus, UiLanguage lang) =>
        GetShortLabel(ResolveKind(availability, uploadStatus), lang);

    public static string GetShortLabel(StorageStatusKind kind, UiLanguage lang)
    {
        bool vi = lang == UiLanguage.Vi;
        return kind switch
        {
            StorageStatusKind.Local => vi ? "Cục bộ" : "Local",
            StorageStatusKind.Synced => vi ? "Đã đồng bộ" : "Synced",
            StorageStatusKind.Pending => vi ? "Chờ đồng bộ" : "Pending",
            StorageStatusKind.CloudOnly => vi ? "Chỉ đám mây" : "Cloud-only",
            StorageStatusKind.SyncError => vi ? "Lỗi đồng bộ" : "Sync error",
            StorageStatusKind.Conflict => vi ? "Xung đột" : "Conflict",
            _ => vi ? "Cục bộ" : "Local"
        };
    }

    public static string GetTooltip(
        VersionAvailability availability,
        UploadStatus? uploadStatus,
        CloudProviderKind provider,
        UiLanguage lang)
    {
        bool vi = lang == UiLanguage.Vi;
        string providerName = provider switch
        {
            CloudProviderKind.GoogleDrive => "Google Drive",
            CloudProviderKind.OneDrive => "OneDrive",
            _ => vi ? "đám mây" : "cloud storage"
        };

        return ResolveKind(availability, uploadStatus) switch
        {
            StorageStatusKind.Local => vi
                ? "Phiên bản này hiện được lưu trên máy và chưa có bản sao xác nhận trên đám mây."
                : "This version is stored locally on this PC and has no confirmed cloud backup yet.",
            StorageStatusKind.Synced => vi
                ? $"Phiên bản này đã được lưu trên máy và xác nhận đồng bộ lên {providerName}."
                : $"This version is stored locally and verified synced to {providerName}.",
            StorageStatusKind.Pending => vi
                ? $"Phiên bản đã lưu an toàn trên máy và đang xếp hàng đồng bộ lên {providerName}."
                : $"Saved safely on this PC and queued for upload to {providerName}.",
            StorageStatusKind.CloudOnly => vi
                ? $"Phiên bản hiện chỉ có trên {providerName} và chưa tải tệp nguồn về máy."
                : $"Currently stored on {providerName} only; click Download to save locally.",
            StorageStatusKind.SyncError => vi
                ? $"Bản lưu cục bộ vẫn an toàn, nhưng lần đồng bộ lên {providerName} gần nhất gặp lỗi."
                : $"Local backup is safe, but the latest upload attempt to {providerName} failed.",
            StorageStatusKind.Conflict => vi
                ? $"Phát hiện nhánh xung đột với {providerName}; cả hai phiên bản đều được giữ an toàn."
                : $"Detected a sync conflict branch against {providerName}; both versions are preserved safely.",
            _ => string.Empty
        };
    }
}
