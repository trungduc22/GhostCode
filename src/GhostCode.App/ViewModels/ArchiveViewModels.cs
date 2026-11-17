using CommunityToolkit.Mvvm.ComponentModel;
using GhostCode.App.Localization;
using GhostCode.Core.Analysis;
using GhostCode.Core.Models;
using GhostCode.Core.Monitoring;
using GhostCode.Core.Notifications;
using GhostCode.Core.Toolchains;

namespace GhostCode.App.ViewModels;

public sealed record ArchiveVersionRow(
    ArchiveVersionRecord Record,
    UiLanguage Language = UiLanguage.Vi,
    UploadStatus? QueueStatus = null,
    CloudProviderKind Provider = CloudProviderKind.GoogleDrive)
{
    public string Id => Record.Id;
    public int VersionNum => Record.VersionNum;
    public string VersionBadge => $"v{Record.VersionNum}";
    public string FileName => Record.FileName;
    public string SourcePath => Record.SourcePath;
    public string ArchivePath => Record.ArchivePath;
    public string? ReadyPath => Record.ReadyPath;
    public string Platform => Record.Platform;
    public bool IsLocalAvailable => Record.IsLocalAvailable;
    public bool IsConflictBranch => Record.IsConflictBranch;
    public string? DriveFileId => Record.DriveFileId;
    public VersionAvailability Availability => Record.Availability;
    public StorageStatusKind StorageKind => StorageStatusFormatter.ResolveKind(Record.Availability, QueueStatus);
    public string AvailabilityBadge => StorageStatusFormatter.GetShortLabel(StorageKind, Language);
    public string StatusTooltip => StorageStatusFormatter.GetTooltip(Record.Availability, QueueStatus, Provider, Language);
    public string ProblemSummary => string.IsNullOrWhiteSpace(Record.ProblemCode)
        ? $"{Record.Platform} • {Record.SizeBytes} B"
        : $"{Record.Platform} • {Record.ProblemCode} — {Record.ProblemTitle}";
    public string ShortSha => Record.Sha256.Length >= 8 ? Record.Sha256[..8] : Record.Sha256;
    public string Sha256Full => Record.Sha256;
    public string SizeText => Record.SizeBytes < 1024 ? $"{Record.SizeBytes} B" : $"{Record.SizeBytes / 1024.0:F1} KB";
    public string TimestampText => DateTimeOffset.FromUnixTimeMilliseconds(Record.CreatedAtUnixMs)
        .ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string CompactTimeText => DateTimeOffset.FromUnixTimeMilliseconds(Record.CreatedAtUnixMs)
        .ToLocalTime().ToString("HH:mm");
    public string CompactMetaText => $"{VersionBadge} · {CompactTimeText} · {AvailabilityBadge}";
    public string SelectorLabel => $"{VersionBadge} · {CompactTimeText} · {AvailabilityBadge}";
}

public sealed record ArchiveFileGroupRow(
    ArchiveVersionRecord Latest,
    int VersionCount,
    UiLanguage Language = UiLanguage.Vi,
    UploadStatus? QueueStatus = null,
    CloudProviderKind Provider = CloudProviderKind.GoogleDrive)
{
    public string Id => Latest.Id;
    public string GroupKey => SourceAnalyzer.BuildLogicalGroupKey(Latest);
    public string FileName => Latest.FileName;
    public string SourcePath => Latest.SourcePath;
    public string LatestVersionBadge => $"v{Latest.VersionNum}";
    public StorageStatusKind StorageKind => StorageStatusFormatter.ResolveKind(Latest.Availability, QueueStatus);
    public string AvailabilityBadge => StorageStatusFormatter.GetShortLabel(StorageKind, Language);
    public string StatusTooltip => StorageStatusFormatter.GetTooltip(Latest.Availability, QueueStatus, Provider, Language);
    public string CompactTitle => !string.IsNullOrWhiteSpace(Latest.ProblemCode) && !string.IsNullOrWhiteSpace(Latest.ProblemTitle)
        ? $"{Latest.ProblemCode} · {Latest.ProblemTitle}"
        : !string.IsNullOrWhiteSpace(Latest.ProblemCode)
            ? Latest.ProblemCode
            : Latest.FileName;
    public string Subtitle => Language == UiLanguage.Vi
        ? $"{VersionCount} phiên bản · {AvailabilityBadge}"
        : $"{VersionCount} versions · {AvailabilityBadge}";
    public string ShortTimeText => DateTimeOffset.FromUnixTimeMilliseconds(Latest.CreatedAtUnixMs)
        .ToLocalTime().ToString("HH:mm");
    public string MetaWithTimeText => $"{Subtitle} · {ShortTimeText}";
    public string OpenButtonLabel => Language == UiLanguage.Vi ? "Mở" : "Open";
    public string CopyButtonLabel => Language == UiLanguage.Vi ? "Sao chép" : "Copy";
    public string RunButtonLabel => Language == UiLanguage.Vi ? "Chạy" : "Run";
    public string RunInArchiveMenuLabel => Language == UiLanguage.Vi ? "Chạy trong Kho lưu trữ" : "Run in Archive";
    public string OpenInIdeMenuLabel => Language == UiLanguage.Vi ? "Mở trong IDE" : "Open in IDE";
}

public sealed record UploadQueueRow(UploadQueueView View, UiLanguage Language = UiLanguage.Vi)
{
    public string FileName => View.Version.FileName;
    public string StatusText => Language == UiLanguage.Vi
        ? View.Queue.Status switch
        {
            UploadStatus.Synced => "Đã đồng bộ",
            UploadStatus.Failed => "Thất bại",
            UploadStatus.Uploading => "Đang tải lên",
            _ => "Đang chờ"
        }
        : View.Queue.Status.ToString();
    public string DetailText => View.Queue.Status switch
    {
        UploadStatus.Synced => $"ID: {View.Queue.DriveFileId}",
        UploadStatus.Failed => Language == UiLanguage.Vi
            ? $"Lần thử: {View.Queue.AttemptCount} • {UserFriendlyErrorService.MapError(ErrorCategory.NetworkOffline, View.Queue.LastError, Language).Title}"
            : $"Attempts: {View.Queue.AttemptCount} • {View.Queue.LastError}",
        _ => Language == UiLanguage.Vi
            ? $"Lần thử: {View.Queue.AttemptCount} • Đang chờ đồng bộ"
            : $"Attempts: {View.Queue.AttemptCount} • Waiting for sync"
    };
    public string UpdatedText => DateTimeOffset.FromUnixTimeMilliseconds(View.Queue.UpdatedAtUnixMs)
        .ToLocalTime().ToString("HH:mm:ss");
}

public sealed record MonitorEventRow(MonitorEventRecord Event)
{
    public string Header => $"{Event.EventType} — {Path.GetFileName(Event.FilePath)}";
    public string Detail => $"{Event.Message} ({Event.FilePath})";
    public string TimeText => DateTimeOffset.FromUnixTimeMilliseconds(Event.OccurredAtUnixMs)
        .ToLocalTime().ToString("HH:mm:ss");
}

public sealed record ArchiveHistoryEventRow(ArchiveHistoryEvent Event)
{
    public string EventType => Event.EventType;
    public string Summary => Event.Summary;
    public string DeviceName => Event.DeviceName;
    public string TimeText => DateTimeOffset.FromUnixTimeMilliseconds(Event.OccurredAtUnixMs)
        .ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
}

public sealed record ActiveWatchRow(ActiveWatchInfo Info, UiLanguage Language = UiLanguage.Vi)
{
    public string FolderPath => Info.FolderPath;
    public bool IsWatching => Info.IsWatching || Info.IsCoveredByParent;
    public string StatusText => Language == UiLanguage.Vi
        ? (Info.IsWatching
            ? (Info.Recursive ? "Đang giám sát (gồm thư mục con)" : "Đang giám sát (chỉ thư mục gốc)")
            : Info.IsCoveredByParent
                ? "Được bao phủ bởi thư mục cha"
                : $"Lỗi: {Info.Error}")
        : (Info.IsWatching
            ? (Info.Recursive ? "Watching (Recursive)" : "Watching (Top-Level)")
            : Info.IsCoveredByParent
                ? (Info.Error ?? "Covered by parent folder")
                : $"Error: {Info.Error}");
}

public sealed record BrowserStatusRow(BrowserStatusInfo Info, UiLanguage Language)
{
    public string KindKey => Info.Kind.ToString();
    public string Name => Info.DisplayName;
    public bool IsConnected => Info.ConnectionState == BrowserConnectionState.ExtensionConnected;
    public bool IsInstalled => Info.IsInstalled;
    public bool CanConfigureOrRepair => Info.IsInstalled;
    public string StateBadge => Info.ConnectionState == BrowserConnectionState.ExtensionConnected
        ? $"{Info.DisplayName} — {UiStrings.ForBrowserState(Info.ConnectionState, Language)}"
        : UiStrings.ForBrowserState(Info.ConnectionState, Language);
    public string ActionButtonLabel => UiStrings.ForBrowserActionLabel(Info.ConnectionState, Language);
    public string VersionText => string.IsNullOrWhiteSpace(Info.ExtensionVersion)
        ? (Info.Version ?? string.Empty)
        : $"Ext v{Info.ExtensionVersion}";
    public string DetailText
    {
        get
        {
            var badge = UiStrings.ForBrowserState(Info.ConnectionState, Language);
            return string.IsNullOrWhiteSpace(Info.ExecutablePath)
                ? badge
                : $"{badge} • {Info.ExecutablePath}";
        }
    }
    public string CompactDetailText => UiStrings.ForBrowserState(Info.ConnectionState, Language);
    public string PathTooltip => string.IsNullOrWhiteSpace(Info.ExecutablePath) ? CompactDetailText : Info.ExecutablePath;
}

public sealed record IdeStatusRow(IdeStatusInfo Info, UiLanguage Language = UiLanguage.Vi)
{
    public string Name => Info.Name;
    public string StatusBadge => Language == UiLanguage.Vi
        ? (Info.IsRunning ? "Đang chạy" : Info.IsInstalled ? "Đã cài đặt" : "Chưa cài đặt")
        : (Info.IsRunning ? "Running" : Info.IsInstalled ? "Installed" : "Not Installed");
    public bool IsPositiveState => Info.IsRunning || Info.IsInstalled;
    public string VersionText => Info.Version ?? string.Empty;
    public string ToolchainText
    {
        get
        {
            var summary = Info.ToolchainSummary ?? string.Empty;
            if (Language == UiLanguage.Vi && summary.StartsWith("Not installed (Python runtime:", StringComparison.OrdinalIgnoreCase))
                summary = summary.Replace("Not installed (Python runtime:", "Chưa cài đặt (Môi trường Python:", StringComparison.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(summary))
                return Info.ExecutablePath ?? (Language == UiLanguage.Vi ? "Chưa phát hiện" : "Not detected");
            return string.IsNullOrWhiteSpace(Info.ExecutablePath)
                ? summary
                : $"{summary} • {Info.ExecutablePath}";
        }
    }
    public string CompactSummary
    {
        get
        {
            var summary = Info.ToolchainSummary ?? string.Empty;
            if (Language == UiLanguage.Vi && summary.StartsWith("Not installed (Python runtime:", StringComparison.OrdinalIgnoreCase))
                summary = summary.Replace("Not installed (Python runtime:", "Chưa cài đặt (Môi trường Python:", StringComparison.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(summary))
                return summary;
            if (!string.IsNullOrWhiteSpace(Info.Version))
                return $"v{Info.Version}";
            return Info.IsInstalled
                ? (Language == UiLanguage.Vi ? "Sẵn sàng" : "Ready")
                : (Language == UiLanguage.Vi ? "Chưa cài đặt" : "Not installed");
        }
    }
    public string PathTooltip => !string.IsNullOrWhiteSpace(Info.ExecutablePath) ? Info.ExecutablePath : CompactSummary;
}

public sealed record ToolchainRow(ToolchainInfo Info, UiLanguage Language = UiLanguage.Vi)
{
    public string Name => Info.DisplayName;
    public bool IsAvailable => Info.IsAvailable;
    public string StatusBadge => Language == UiLanguage.Vi
        ? (Info.IsAvailable ? "Sẵn sàng" : "Chưa có")
        : (Info.IsAvailable ? "Ready" : "Unavailable");
    public string VersionText => Info.Version ?? string.Empty;
    public string DetailText => Info.IsAvailable
        ? $"{Info.ExecutablePath} ({Info.DefaultFlags})"
        : (Language == UiLanguage.Vi && string.IsNullOrWhiteSpace(Info.ExecutablePath) ? "Chưa tìm thấy trong PATH" : Info.StatusDetail);
    public string CompactDetail => Info.IsAvailable
        ? (!string.IsNullOrWhiteSpace(Info.DefaultFlags) ? $"{Info.DefaultFlags}" : (Language == UiLanguage.Vi ? "Sẵn sàng" : "Ready"))
        : (Language == UiLanguage.Vi && string.IsNullOrWhiteSpace(Info.ExecutablePath) ? "Chưa tìm thấy trong PATH" : Info.StatusDetail);
    public string PathTooltip => !string.IsNullOrWhiteSpace(Info.ExecutablePath) ? Info.ExecutablePath : CompactDetail;
}

public sealed record GlobalSearchSuggestion(ArchiveVersionRecord Record, string MatchKind, UiLanguage Language)
{
    public string Id => Record.Id;
    public string DisplayTitle => string.IsNullOrWhiteSpace(Record.ProblemCode)
        ? $"{Record.FileName} (v{Record.VersionNum})"
        : $"[{Record.ProblemCode}] {Record.ProblemTitle} — {Record.FileName} (v{Record.VersionNum})";
    public string Subtitle => $"{Record.Platform} • {MatchKind} • {UiStrings.ForAvailability(Record.Availability, Language)}";
}

public sealed record DiffLineRow(DiffLinePair Pair)
{
    public DiffLineKind Kind => Pair.Kind;
    public string LeftNumText => Pair.LeftLineNumber?.ToString() ?? string.Empty;
    public string LeftText => Pair.LeftText;
    public string RightNumText => Pair.RightLineNumber?.ToString() ?? string.Empty;
    public string RightText => Pair.RightText;
    public string LeftKindTag => Pair.Kind switch
    {
        DiffLineKind.Removed => "−",
        DiffLineKind.Modified => "~",
        _ => " "
    };
    public string RightKindTag => Pair.Kind switch
    {
        DiffLineKind.Added => "+",
        DiffLineKind.Modified => "~",
        _ => " "
    };
    public string KindTag => Pair.Kind switch
    {
        DiffLineKind.Added => "+",
        DiffLineKind.Removed => "−",
        DiffLineKind.Modified => "~",
        _ => " "
    };
}

public sealed record UnifiedDiffLineRow(UnifiedDiffLine Line)
{
    public DiffLineKind Kind => Line.Kind;
    public string OldNumText => Line.OldLineNumber?.ToString() ?? string.Empty;
    public string NewNumText => Line.NewLineNumber?.ToString() ?? string.Empty;
    public string PrefixGlyph => Line.PrefixGlyph == '-' ? "−" : Line.PrefixGlyph.ToString();
    public string Text => Line.Text;
}

public sealed record TrashedArchiveVersionRow(TrashedArchiveVersionRecord Entry, UiLanguage Language = UiLanguage.Vi)
{
    public string Id => Entry.Version.Id;
    public string? BatchId => Entry.TrashBatchId;
    public string Title => !string.IsNullOrWhiteSpace(Entry.Version.ProblemCode)
        ? $"{Entry.Version.ProblemCode} · v{Entry.Version.VersionNum}"
        : $"{Entry.Version.FileName} · v{Entry.Version.VersionNum}";
    public string Subtitle
    {
        get
        {
            var dt = DateTimeOffset.FromUnixTimeMilliseconds(Entry.DeletedAtUnixMs).ToLocalTime().ToString("dd/MM HH:mm");
            int daysLeft = Math.Max(0, (int)Math.Ceiling((Entry.ExpiresAtUnixMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) / 86_400_000.0));
            bool hasCloud = !string.IsNullOrWhiteSpace(Entry.Version.DriveFileId);
            return Language == UiLanguage.Vi
                ? $"{Entry.Version.FileName} · Xóa {dt} · Còn {daysLeft} ngày · {(hasCloud ? "Có bản đám mây" : "Cục bộ")}"
                : $"{Entry.Version.FileName} · Deleted {dt} · {daysLeft}d left · {(hasCloud ? "Cloud copy kept" : "Local")}";
        }
    }
    public bool HasRemoteCopy => !string.IsNullOrWhiteSpace(Entry.Version.DriveFileId);
}

public sealed class WatchedFolderItem : ObservableObject
{
    private readonly Action _onChanged;
    private readonly UiLanguage _language;
    private bool _isEnabled;
    private bool _isRecursive;

    public WatchedFolderItem(WatchedFolderConfig cfg, Action onChanged, UiLanguage language = UiLanguage.Vi)
    {
        Path = cfg.Path;
        Label = string.IsNullOrWhiteSpace(cfg.Label) ? System.IO.Path.GetFileName(cfg.Path) : cfg.Label;
        _isEnabled = cfg.Enabled;
        _isRecursive = cfg.Recursive;
        _onChanged = onChanged;
        _language = language;
    }

    public string Path { get; }
    public string Label { get; }
    public string RecursiveLabel => _language == UiLanguage.Vi ? "Thư mục con" : "Recursive";

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (SetProperty(ref _isEnabled, value))
                _onChanged();
        }
    }

    public bool IsRecursive
    {
        get => _isRecursive;
        set
        {
            if (SetProperty(ref _isRecursive, value))
                _onChanged();
        }
    }

    public WatchedFolderConfig ToConfig() => new(Path, IsEnabled, IsRecursive, Label);
}
