using GhostCode.App.Localization;
using GhostCode.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GhostCode.App.ViewModels;

public sealed partial class MainViewModel
{
    public string ActiveCode { get => _activeCode; private set => SetProperty(ref _activeCode, value); }
    public string ActiveTitle { get => _activeTitle; private set => SetProperty(ref _activeTitle, value); }
    public string ActivePlatform { get => _activePlatform; private set => SetProperty(ref _activePlatform, value); }
    public string ActiveUrl { get => _activeUrl; private set => SetProperty(ref _activeUrl, value); }
    public string ActiveBrowserName { get => _activeBrowserName; private set => SetProperty(ref _activeBrowserName, value); }
    public string ActiveFilenamePreview { get => _activeFilenamePreview; private set => SetProperty(ref _activeFilenamePreview, value); }
    public string ActiveContextStateBadge { get => _activeContextStateBadge; private set => SetProperty(ref _activeContextStateBadge, value); }

    public bool HasActiveContext
    {
        get => _hasActiveContext;
        private set
        {
            if (SetProperty(ref _hasActiveContext, value))
                OnPropertyChanged(nameof(TitleBarBrowserBadgeText));
        }
    }

    public bool HasConnectedBrowser => ConnectedBrowsers.Count > 0;
    public Visibility NoConnectedBrowsersVisibility => HasConnectedBrowser ? Visibility.Collapsed : Visibility.Visible;
    public Visibility NoInstalledBrowsersVisibility => InstalledBrowserRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NoFoldersVisible => WatchedFolders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility OnboardingCardVisibility => (WatchedFolders.Count(w => w.IsEnabled) == 0 && ActiveWatches.Count(w => w.IsWatching) == 0 && !_onboardingDismissedForSession)
        ? Visibility.Visible
        : Visibility.Collapsed;

    public string TitleBarBrowserBadgeText
    {
        get
        {
            if (HasConnectedBrowser && HasActiveContext)
                return $"{ConnectedBrowsers[0].Name} • {ActiveCode}";
            if (HasConnectedBrowser)
                return $"{ConnectedBrowsers[0].Name}: {Strings.BrowserConnectedBadge}";
            if (HasActiveContext)
                return ActiveCode;
            if (InstalledBrowserRows.Count > 0)
            {
                return _config.Language == UiLanguage.Vi
                    ? $"{InstalledBrowserRows.Count} trình duyệt (chưa kết nối)"
                    : $"{InstalledBrowserRows.Count} browser(s) (not connected)";
            }
            return _config.Language == UiLanguage.Vi ? "Chưa cài trình duyệt" : "No browsers detected";
        }
    }

    public string TitleBarDriveBadgeText
    {
        get
        {
            bool vi = _config.Language == UiLanguage.Vi;
            if (_config.PrimaryCloudProvider == CloudProviderKind.LocalOnly)
                return vi ? "Lưu trữ: Chỉ trên máy" : "Cloud: Local Only";
            if (_config.SyncPausedManually)
                return vi ? "Đám mây: Tạm dừng" : "Cloud: Paused";
            if (_config.PrimaryCloudProvider == CloudProviderKind.OneDrive)
                return IsOneDriveConnected
                    ? $"OneDrive: {(PendingUploads > 0 ? (vi ? $"{PendingUploads} chờ" : $"{PendingUploads} pending") : Strings.ConnectedStatus)}"
                    : (vi ? "OneDrive: Chưa kết nối" : "OneDrive: Offline");
            return IsDriveConnected
                ? $"Drive: {(PendingUploads > 0 ? (vi ? $"{PendingUploads} chờ" : $"{PendingUploads} pending") : Strings.ConnectedStatus)}"
                : (vi ? "Drive: Chỉ lưu trên máy" : "Drive: Local Only");
        }
    }

    public string IdeStatusSummary { get => _ideStatusSummary; private set => SetProperty(ref _ideStatusSummary, value); }
    public string BrowserStatusSummary { get => _browserStatusSummary; private set => SetProperty(ref _browserStatusSummary, value); }
    public string CompactToolchainSummaryText { get => _compactToolchainSummaryText; private set => SetProperty(ref _compactToolchainSummaryText, value); }

    public string DefaultIdeForC
    {
        get => _defaultIdeForC;
        set
        {
            if (!string.IsNullOrWhiteSpace(value) && SetProperty(ref _defaultIdeForC, value) && _config.DefaultIdeForC != value)
            {
                _config.DefaultIdeForC = value;
                SaveConfigSafe();
            }
        }
    }

    public string DefaultIdeForCpp
    {
        get => _defaultIdeForCpp;
        set
        {
            if (!string.IsNullOrWhiteSpace(value) && SetProperty(ref _defaultIdeForCpp, value) && _config.DefaultIdeForCpp != value)
            {
                _config.DefaultIdeForCpp = value;
                SaveConfigSafe();
            }
        }
    }

    public string DefaultIdeForPython
    {
        get => _defaultIdeForPython;
        set
        {
            if (!string.IsNullOrWhiteSpace(value) && SetProperty(ref _defaultIdeForPython, value) && _config.DefaultIdeForPython != value)
            {
                _config.DefaultIdeForPython = value;
                SaveConfigSafe();
            }
        }
    }

    public int TotalVersions
    {
        get => _totalVersions;
        private set
        {
            if (SetProperty(ref _totalVersions, value))
                NotifyTitleBarStatsChanged();
        }
    }

    public int PendingUploads
    {
        get => _pendingUploads;
        private set
        {
            if (SetProperty(ref _pendingUploads, value))
            {
                OnPropertyChanged(nameof(TitleBarDriveBadgeText));
                NotifyTitleBarStatsChanged();
            }
        }
    }

    public int SyncedUploads
    {
        get => _syncedUploads;
        private set
        {
            if (SetProperty(ref _syncedUploads, value))
                NotifyTitleBarStatsChanged();
        }
    }

    public int FailedUploads
    {
        get => _failedUploads;
        private set
        {
            if (SetProperty(ref _failedUploads, value))
                NotifyTitleBarStatsChanged();
        }
    }

    public string LastSyncTimeText { get => _lastSyncTimeText; private set => SetProperty(ref _lastSyncTimeText, value); }

    public string TitleBarStatsWideText
    {
        get
        {
            bool vi = _config.Language == UiLanguage.Vi;
            if (FailedUploads > 0)
                return vi ? $"{TotalVersions} bản lưu · {FailedUploads} lỗi tải lên" : $"{TotalVersions} saved · {FailedUploads} failed";
            if (PendingUploads > 0)
                return vi ? $"{TotalVersions} bản lưu · {PendingUploads} chờ" : $"{TotalVersions} saved · {PendingUploads} pending";
            return vi ? $"{TotalVersions} bản lưu" : $"{TotalVersions} saved";
        }
    }

    public string TitleBarStatsCompactText => FailedUploads > 0
        ? $"{TotalVersions} · !{FailedUploads}"
        : PendingUploads > 0
            ? $"{TotalVersions} · {PendingUploads}"
            : $"{TotalVersions}";

    public string TitleBarStatsTooltipText
    {
        get
        {
            bool vi = _config.Language == UiLanguage.Vi;
            return vi
                ? $"Đã lưu: {TotalVersions} | Chờ tải: {PendingUploads} | Đã đồng bộ: {SyncedUploads} | Lỗi: {FailedUploads}\n{TitleBarDriveBadgeText}"
                : $"Saved: {TotalVersions} | Pending: {PendingUploads} | Synced: {SyncedUploads} | Failed: {FailedUploads}\n{TitleBarDriveBadgeText}";
        }
    }

    public bool TitleBarStatsHasError => FailedUploads > 0;
    public Visibility TitleBarStatsWarningVisibility => FailedUploads > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility TitleBarStatsNormalVisibility => FailedUploads == 0 ? Visibility.Visible : Visibility.Collapsed;
    public string TitleBarStatsFlyoutHeader => _config.Language == UiLanguage.Vi ? "Thống kê lưu trữ & đồng bộ" : "Archive & Sync Summary";
    public string StatsFlyoutSavedLabel => Strings.TotalVersionsLabel;
    public string StatsFlyoutPendingLabel => Strings.PendingUploadsLabel;
    public string StatsFlyoutSyncedLabel => Strings.SyncedUploadsLabel;
    public string StatsFlyoutFailedLabel => Strings.FailedUploadsLabel;
    public string StatsFlyoutCloudStatusText => TitleBarDriveBadgeText;
    public string DashboardViewAllText => _config.Language == UiLanguage.Vi ? "Xem tất cả" : "View all";
    public string RecentArchivesSectionTitle => _config.Language == UiLanguage.Vi ? "Mã nguồn lưu gần đây" : "Recent Source Archives";
    public string RecentArchivesEmptyText => _config.Language == UiLanguage.Vi
        ? "Chưa có mã nguồn nào được lưu. Hãy lưu một file code trong thư mục đang theo dõi."
        : "No archived code yet. Save a source file inside any monitored folder.";
    public string ProtectionSectionTitle => _config.Language == UiLanguage.Vi ? "Trạng thái bảo vệ" : "Protection Status";
    public string LatestBackupSectionTitle => _config.Language == UiLanguage.Vi ? "Bản lưu thành công gần nhất" : "Latest Successful Backup";
    public string LatestBackupEmptyText => _config.Language == UiLanguage.Vi
        ? "Chưa có bản sao lưu nào được ghi nhận."
        : "No backup recorded yet.";
    public string OpenShortText => _config.Language == UiLanguage.Vi ? "Mở" : "Open";
    public string MoreActionsTooltipText => _config.Language == UiLanguage.Vi ? "Tùy chọn khác" : "More options";
    public string ProblemDetailsTooltipText => _config.Language == UiLanguage.Vi ? "Chi tiết trình duyệt & URL" : "Browser & URL details";
    public string DefaultIdesHeaderText => _config.Language == UiLanguage.Vi ? "IDE mặc định" : "Default IDEs";
    public string AdvancedToolchainExpanderText => _config.Language == UiLanguage.Vi
        ? "Cấu hình nâng cao (đường dẫn, chuẩn ngôn ngữ & giới hạn chạy)"
        : "Advanced Settings (Executable Paths, Standards & Limits)";

    public bool IsProblemDetailsExpanded
    {
        get => _isProblemDetailsExpanded;
        set
        {
            if (SetProperty(ref _isProblemDetailsExpanded, value))
                OnPropertyChanged(nameof(ProblemDetailsVisibility));
        }
    }

    public Visibility ProblemDetailsVisibility => IsProblemDetailsExpanded ? Visibility.Visible : Visibility.Collapsed;

    public int ProtectionStateLevel
    {
        get => _protectionStateLevel;
        private set
        {
            if (SetProperty(ref _protectionStateLevel, value))
            {
                OnPropertyChanged(nameof(ProtectionOkVisibility));
                OnPropertyChanged(nameof(ProtectionAttentionVisibility));
                OnPropertyChanged(nameof(ProtectionErrorVisibility));
            }
        }
    }

    public Visibility ProtectionOkVisibility => ProtectionStateLevel == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ProtectionAttentionVisibility => ProtectionStateLevel == 1 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ProtectionErrorVisibility => ProtectionStateLevel == 2 ? Visibility.Visible : Visibility.Collapsed;
    public string ProtectionHeadlineText { get => _protectionHeadlineText; private set => SetProperty(ref _protectionHeadlineText, value); }
    public string ProtectionDetailText { get => _protectionDetailText; private set => SetProperty(ref _protectionDetailText, value); }

    public Visibility HasLatestBackupVisibility => _latestBackup is not null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NoLatestBackupVisibility => _latestBackup is null ? Visibility.Visible : Visibility.Collapsed;
    public string LatestBackupTitle => _latestBackup?.FileName ?? string.Empty;
    public string LatestBackupTimestampText => _latestBackup is null
        ? string.Empty
        : (_config.Language == UiLanguage.Vi
            ? $"Sao lưu lúc {DateTimeOffset.FromUnixTimeMilliseconds(_latestBackup.CreatedAtUnixMs).ToLocalTime():HH:mm:ss} · {_latestBackup.Platform}"
            : $"Backed up at {DateTimeOffset.FromUnixTimeMilliseconds(_latestBackup.CreatedAtUnixMs).ToLocalTime():HH:mm:ss} · {_latestBackup.Platform}");
    public string LatestBackupStatusText => _latestBackup is null
        ? string.Empty
        : UiStrings.ForAvailability(_latestBackup.Availability, _config.Language);

    private void NotifyTitleBarStatsChanged()
    {
        OnPropertyChanged(nameof(TitleBarStatsWideText));
        OnPropertyChanged(nameof(TitleBarStatsCompactText));
        OnPropertyChanged(nameof(TitleBarStatsTooltipText));
        OnPropertyChanged(nameof(TitleBarStatsHasError));
        OnPropertyChanged(nameof(TitleBarStatsWarningVisibility));
        OnPropertyChanged(nameof(TitleBarStatsNormalVisibility));
        OnPropertyChanged(nameof(StatsFlyoutCloudStatusText));
        NotifyCloudSyncStateChanged();
    }

    private void NotifyLatestBackupAndDashboardLabelsChanged()
    {
        OnPropertyChanged(nameof(TitleBarStatsFlyoutHeader));
        OnPropertyChanged(nameof(StatsFlyoutSavedLabel));
        OnPropertyChanged(nameof(StatsFlyoutPendingLabel));
        OnPropertyChanged(nameof(StatsFlyoutSyncedLabel));
        OnPropertyChanged(nameof(StatsFlyoutFailedLabel));
        OnPropertyChanged(nameof(DashboardViewAllText));
        OnPropertyChanged(nameof(RecentArchivesSectionTitle));
        OnPropertyChanged(nameof(RecentArchivesEmptyText));
        OnPropertyChanged(nameof(ProtectionSectionTitle));
        OnPropertyChanged(nameof(LatestBackupSectionTitle));
        OnPropertyChanged(nameof(LatestBackupEmptyText));
        OnPropertyChanged(nameof(OpenShortText));
        OnPropertyChanged(nameof(MoreActionsTooltipText));
        OnPropertyChanged(nameof(ProblemDetailsTooltipText));
        OnPropertyChanged(nameof(DefaultIdesHeaderText));
        OnPropertyChanged(nameof(AdvancedToolchainExpanderText));
        OnPropertyChanged(nameof(HasLatestBackupVisibility));
        OnPropertyChanged(nameof(NoLatestBackupVisibility));
        OnPropertyChanged(nameof(LatestBackupTitle));
        OnPropertyChanged(nameof(LatestBackupTimestampText));
        OnPropertyChanged(nameof(LatestBackupStatusText));
        NotifyTitleBarStatsChanged();
    }

    public async Task RefreshHealthAsync()
    {
        var snap = await _healthService.EvaluateHealthAsync(attemptWatchRecovery: false);
        BackupProtectionBadge = UiStrings.ForProtectionBadge(snap.ProtectionState, _config.Language);
        BackupHealthSummary = snap.StatusSummary;

        bool vi = _config.Language == UiLanguage.Vi;
        ProtectionStateLevel = snap.ProtectionState switch
        {
            BackupProtectionState.Protected => 0,
            BackupProtectionState.Unhealthy => 2,
            _ => 1
        };

        ProtectionHeadlineText = snap.ProtectionState switch
        {
            BackupProtectionState.Protected => vi
                ? $"Đang bảo vệ · {snap.ActiveWatchCount} thư mục"
                : $"Protected · {snap.ActiveWatchCount} folder(s)",
            BackupProtectionState.Unhealthy when snap.ErroredWatchCount > 0 => vi
                ? $"Cần chú ý · {snap.ErroredWatchCount} thư mục không khả dụng"
                : $"Attention · {snap.ErroredWatchCount} unavailable folder(s)",
            BackupProtectionState.Unhealthy => vi
                ? "Lỗi bảo vệ · Cần kiểm tra"
                : "Protection Error · Check storage",
            BackupProtectionState.Degraded => vi
                ? $"Cần chú ý · {snap.FailedCloudUploads} lỗi tải lên"
                : $"Attention · {snap.FailedCloudUploads} failed upload(s)",
            _ when snap.ActiveWatchCount == 0 => vi
                ? "Tạm dừng · Chưa có thư mục theo dõi"
                : "Paused · No active folders",
            _ => vi
                ? $"Bảo vệ cục bộ · {snap.ActiveWatchCount} thư mục (đồng bộ tạm dừng)"
                : $"Local only · {snap.ActiveWatchCount} folder(s) (sync paused)"
        };
        ProtectionDetailText = snap.StatusSummary;
        OnPropertyChanged(nameof(OnboardingCardVisibility));
    }

    public async Task RunRecoveryCheckAsync()
    {
        IsBusy = true;
        try
        {
            var rep = await _healthService.RunRecoveryCheckAsync(ResolveActiveCloudProvider());
            RecoverySummaryText = rep.Summary;
            await RefreshHealthAsync();
            await RefreshStatsAndEventsAsync();
            ShowInfoBar(
                _config.Language == UiLanguage.Vi ? "Hoàn tất kiểm tra phục hồi" : "Recovery Check Complete",
                rep.Summary,
                (rep.LocalCorruptedCount > 0 || rep.LocalMissingCount > 0) ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ToggleSyncPause()
    {
        SyncPausedManually = !SyncPausedManually;
        var vi = _config.Language == UiLanguage.Vi;
        ShowInfoBar(
            SyncPausedManually ? (vi ? "Đã tạm dừng đồng bộ" : "Sync Paused") : (vi ? "Đã tiếp tục đồng bộ" : "Sync Resumed"),
            SyncPausedManually
                ? (vi ? "Sao lưu cục bộ vẫn hoạt động; tạm dừng tải lên đám mây." : "Local archival remains active; cloud uploads are paused.")
                : (vi ? "Đã tiếp tục đồng bộ đám mây nền." : "Background cloud synchronization resumed."),
            InfoBarSeverity.Informational);
    }

    public void RefreshProcessesAndWatches() => RefreshProcessesAndWatches(forceToolchainRescan: false);

    public void RefreshProcessesAndWatches(bool forceToolchainRescan)
    {
        RefreshBrowserAndIdeDiscovery(forceToolchainRescan);

        LastDetectedFileText = FormatMonitorStatus(_monitor.LastDetectedFile);
        LastSuccessfulBackupText = FormatMonitorStatus(_monitor.LastSuccessfulBackup);
        LastMonitoringErrorText = FormatMonitorStatus(_monitor.LastMonitoringError);
        LastDiagnosticResultText = FormatMonitorStatus(_monitor.LastDiagnosticSummary);

        var watches = _monitor.GetActiveWatches();
        ActiveWatches.Clear();
        foreach (var w in watches)
            ActiveWatches.Add(new ActiveWatchRow(w, _config.Language));
        OnPropertyChanged(nameof(OnboardingCardVisibility));
    }

    private string FormatMonitorStatus(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw == "None")
            return UxStrings.NoneText;
        if (raw == "Not run yet")
            return UxStrings.NotRunYetText;
        return raw;
    }

    public async Task RefreshStatsAndEventsAsync()
    {
        var stats = await _store.GetStatsAsync();
        TotalVersions = stats.TotalVersions;
        PendingUploads = stats.PendingUploads;
        SyncedUploads = stats.SyncedUploads;
        FailedUploads = stats.FailedUploads;
        LastSyncTimeText = stats.LastSuccessfulSyncUnixMs is long ts
            ? DateTimeOffset.FromUnixTimeMilliseconds(ts).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
            : UxStrings.NeverText;

        _latestBackup = await _store.GetLatestBackupSummaryAsync();
        NotifyLatestBackupAndDashboardLabelsChanged();

        var events = await _store.GetRecentMonitorEventsAsync(25);
        RecentEvents.Clear();
        foreach (var ev in events)
            RecentEvents.Add(new MonitorEventRow(ev));

        var history = await _store.GetArchiveHistoryEventsAsync(40);
        HistoryEvents.Clear();
        foreach (var h in history)
            HistoryEvents.Add(new ArchiveHistoryEventRow(h));

        var notifs = await _store.GetRecentNotificationsAsync(20);
        NotificationHistory.Clear();
        foreach (var nr in notifs)
            NotificationHistory.Add(nr);
    }

    private void UpdateContext(ProblemContext? ctx)
    {
        if (ctx is null)
        {
            HasActiveContext = false;
            ActiveCode = "-";
            ActiveTitle = Strings.NoActiveContext;
            ActivePlatform = _config.Language == UiLanguage.Vi ? "Ngoại tuyến" : "Offline";
            ActiveUrl = string.Empty;
            ActiveBrowserName = string.Empty;
            ActiveFilenamePreview = string.Empty;
            ActiveContextStateBadge = _config.Language == UiLanguage.Vi ? "Ngoại tuyến" : "Offline";
            return;
        }

        var ageMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - ctx.SeenAtUnixMs;
        var isFresh = ageMs <= _config.ContextExpirationSeconds * 1000L;
        HasActiveContext = true;
        ActiveCode = ctx.Code;
        ActiveTitle = ctx.Title;
        ActivePlatform = ctx.Platform.ToString();
        ActiveUrl = ctx.Url;
        ActiveBrowserName = ctx.BrowserName ?? string.Empty;
        ActiveFilenamePreview = ctx.FilenamePreview;
        ActiveContextStateBadge = isFresh ? Strings.ProblemActiveBadge : Strings.ProblemStaleBadge;
    }
}
