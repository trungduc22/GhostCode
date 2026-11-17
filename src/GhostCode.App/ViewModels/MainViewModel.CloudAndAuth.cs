using System.Diagnostics;
using GhostCode.App.Localization;
using GhostCode.Core.Cloud;
using GhostCode.Core.Models;
using GhostCode.Core.Notifications;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GhostCode.App.ViewModels;

public sealed partial class MainViewModel
{
    public bool IsDriveConnected
    {
        get => _isDriveConnected;
        private set
        {
            if (SetProperty(ref _isDriveConnected, value))
            {
                OnPropertyChanged(nameof(DriveConnectionStatusText));
                OnPropertyChanged(nameof(TitleBarDriveBadgeText));
            }
        }
    }

    public string ConnectedEmail
    {
        get => _connectedEmail;
        private set
        {
            if (SetProperty(ref _connectedEmail, value))
                OnPropertyChanged(nameof(DriveConnectionStatusText));
        }
    }

    public string DriveConnectionStatusText => IsDriveConnected
        ? $"{Strings.ConnectedStatus}: {ConnectedEmail}"
        : Strings.NotConnectedStatus;

    public bool IsOneDriveConnected
    {
        get => _isOneDriveConnected;
        private set
        {
            if (SetProperty(ref _isOneDriveConnected, value))
            {
                OnPropertyChanged(nameof(OneDriveConnectionStatusText));
                OnPropertyChanged(nameof(TitleBarDriveBadgeText));
            }
        }
    }

    public string OneDriveEmail
    {
        get => _oneDriveEmail;
        private set
        {
            if (SetProperty(ref _oneDriveEmail, value))
                OnPropertyChanged(nameof(OneDriveConnectionStatusText));
        }
    }

    public string OneDriveConnectionStatusText => IsOneDriveConnected
        ? $"{Strings.ConnectedStatus}: {OneDriveEmail}"
        : Strings.NotConnectedStatus;

    public int SelectedCloudProviderIndex
    {
        get => _selectedCloudProviderIndex;
        set
        {
            if (value < 0 || value > 2 || !SetProperty(ref _selectedCloudProviderIndex, value))
                return;
            _config.PrimaryCloudProvider = (CloudProviderKind)value;
            DriveEnabled = _config.PrimaryCloudProvider != CloudProviderKind.LocalOnly;
            SaveConfigSafe();
            NotifyCloudProviderChanged();
            _ = RefreshHealthAsync();
        }
    }

    public Visibility LocalOnlyCloudCardVisibility =>
        _config.PrimaryCloudProvider == CloudProviderKind.LocalOnly ? Visibility.Visible : Visibility.Collapsed;

    public Visibility GoogleDriveCardVisibility =>
        _config.PrimaryCloudProvider == CloudProviderKind.GoogleDrive ? Visibility.Visible : Visibility.Collapsed;

    public Visibility OneDriveCardVisibility =>
        _config.PrimaryCloudProvider == CloudProviderKind.OneDrive ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ActiveCloudQueueVisibility =>
        _config.PrimaryCloudProvider != CloudProviderKind.LocalOnly ? Visibility.Visible : Visibility.Collapsed;

    public bool IsOAuthInProgress
    {
        get => _isOAuthInProgress;
        private set
        {
            if (SetProperty(ref _isOAuthInProgress, value))
                OnPropertyChanged(nameof(OAuthStageVisibility));
        }
    }

    public Visibility OAuthStageVisibility =>
        IsOAuthInProgress && !string.IsNullOrWhiteSpace(OAuthStageText) ? Visibility.Visible : Visibility.Collapsed;

    private void NotifyCloudProviderChanged()
    {
        OnPropertyChanged(nameof(LocalOnlyCloudCardVisibility));
        OnPropertyChanged(nameof(GoogleDriveCardVisibility));
        OnPropertyChanged(nameof(OneDriveCardVisibility));
        OnPropertyChanged(nameof(ActiveCloudQueueVisibility));
        OnPropertyChanged(nameof(TitleBarDriveBadgeText));
        NotifyCloudSyncStateChanged();
    }

    public CloudSyncState CurrentCloudSyncState
    {
        get
        {
            if (_config.PrimaryCloudProvider == CloudProviderKind.LocalOnly || !_config.DriveEnabled)
                return CloudSyncState.LocalOnly;
            if (IsSyncing)
                return CloudSyncState.Syncing;

            bool isGoogle = _config.PrimaryCloudProvider == CloudProviderKind.GoogleDrive;
            bool connected = isGoogle ? IsDriveConnected : IsOneDriveConnected;
            var err = _lastCloudServiceError ?? (isGoogle ? _drive.LastServiceError : null);
            if (err is not null)
            {
                return err.Category switch
                {
                    ErrorCategory.CloudApiDisabled => CloudSyncState.DriveApiDisabled,
                    ErrorCategory.AuthenticationExpired or ErrorCategory.CloudInsufficientPermissions => CloudSyncState.AuthExpired,
                    ErrorCategory.CloudQuotaExceeded => CloudSyncState.QuotaExceeded,
                    ErrorCategory.NetworkOffline or ErrorCategory.ConnectionTimeout => CloudSyncState.NetworkOffline,
                    _ => CloudSyncState.SyncFailed
                };
            }

            if (!connected)
                return CloudSyncState.NotConnected;
            if (FailedUploads > 0)
                return CloudSyncState.SyncFailed;
            if (PendingUploads > 0)
                return CloudSyncState.PendingUpload;
            if (SyncedUploads > 0)
                return CloudSyncState.Synced;
            return CloudSyncState.AccountConnected;
        }
    }

    public string CloudSyncStatusText
    {
        get
        {
            bool vi = _config.Language == UiLanguage.Vi;
            return CurrentCloudSyncState switch
            {
                CloudSyncState.LocalOnly => vi ? "Chỉ lưu trên máy" : "Local Storage Only",
                CloudSyncState.NotConnected => vi ? "Chưa kết nối tài khoản" : "Not Connected",
                CloudSyncState.AccountConnected => vi ? "Đã kết nối tài khoản" : "Account Connected",
                CloudSyncState.Syncing => vi ? "Đang đồng bộ" : "Syncing",
                CloudSyncState.PendingUpload => vi ? $"Đang chờ tải lên ({PendingUploads})" : $"Pending Upload ({PendingUploads})",
                CloudSyncState.DriveApiDisabled => vi ? "Chưa bật Google Drive API" : "Google Drive API Disabled",
                CloudSyncState.NetworkOffline => vi ? "Mất kết nối Internet" : "Internet Disconnected",
                CloudSyncState.AuthExpired => vi ? "Cần đăng nhập lại" : "Re-authentication Required",
                CloudSyncState.QuotaExceeded => vi ? "Hết dung lượng" : "Cloud Storage Full",
                CloudSyncState.SyncFailed => vi ? "Đồng bộ thất bại" : "Sync Failed",
                CloudSyncState.Synced => vi ? "Đã đồng bộ" : "Synced",
                _ => vi ? "Chưa rõ" : "Unknown"
            };
        }
    }

    public string CloudSyncStatusDetailText
    {
        get
        {
            bool vi = _config.Language == UiLanguage.Vi;
            return CurrentCloudSyncState switch
            {
                CloudSyncState.DriveApiDisabled => vi
                    ? "GhostCode chưa thể đồng bộ mã nguồn vì Google Drive API chưa được bật trong dự án Google Cloud đang sử dụng. Các phiên bản vẫn được lưu cục bộ."
                    : "Google Drive API is disabled in the active Google Cloud project. Local versions remain safely saved.",
                CloudSyncState.PendingUpload => vi
                    ? $"{PendingUploads} phiên bản đang chờ tải lên; đã đồng bộ {SyncedUploads}."
                    : $"{PendingUploads} version(s) waiting to upload; {SyncedUploads} synced.",
                CloudSyncState.Synced => vi
                    ? $"Tất cả {SyncedUploads} phiên bản đã đồng bộ an toàn."
                    : $"All {SyncedUploads} version(s) safely synchronized.",
                CloudSyncState.AuthExpired => vi
                    ? "Phiên xác thực đã hết hạn hoặc thiếu quyền Drive. Hãy đăng nhập lại."
                    : "Authorization expired or lacks Drive scope. Please sign in again.",
                _ => _lastCloudServiceError?.ShortMessage ?? DriveConnectionStatusText
            };
        }
    }

    public Visibility CloudSyncStateOkVisibility =>
        CurrentCloudSyncState is CloudSyncState.Synced or CloudSyncState.AccountConnected
            ? Visibility.Visible : Visibility.Collapsed;

    public Visibility CloudSyncStateWarnVisibility =>
        CurrentCloudSyncState is CloudSyncState.NotConnected or CloudSyncState.Syncing or CloudSyncState.PendingUpload or CloudSyncState.DriveApiDisabled or CloudSyncState.NetworkOffline
            ? Visibility.Visible : Visibility.Collapsed;

    public Visibility CloudSyncStateErrorVisibility =>
        CurrentCloudSyncState is CloudSyncState.AuthExpired or CloudSyncState.QuotaExceeded or CloudSyncState.SyncFailed
            ? Visibility.Visible : Visibility.Collapsed;

    public Visibility DriveApiDisabledBannerVisibility =>
        CurrentCloudSyncState == CloudSyncState.DriveApiDisabled ? Visibility.Visible : Visibility.Collapsed;

    public string DriveApiDisabledTitleText => _config.Language == UiLanguage.Vi
        ? "Google Drive API chưa được kích hoạt"
        : "Google Drive API Not Enabled";

    public string DriveApiEnableButtonText => _config.Language == UiLanguage.Vi
        ? "Bật Google Drive API"
        : "Enable Google Drive API";

    public string DriveRetrySyncButtonText => _config.Language == UiLanguage.Vi
        ? "Thử lại"
        : "Retry";

    private void NotifyCloudSyncStateChanged()
    {
        OnPropertyChanged(nameof(CurrentCloudSyncState));
        OnPropertyChanged(nameof(CloudSyncStatusText));
        OnPropertyChanged(nameof(CloudSyncStatusDetailText));
        OnPropertyChanged(nameof(CloudSyncStateOkVisibility));
        OnPropertyChanged(nameof(CloudSyncStateWarnVisibility));
        OnPropertyChanged(nameof(CloudSyncStateErrorVisibility));
        OnPropertyChanged(nameof(DriveApiDisabledBannerVisibility));
        OnPropertyChanged(nameof(DriveApiDisabledTitleText));
        OnPropertyChanged(nameof(DriveApiEnableButtonText));
        OnPropertyChanged(nameof(DriveRetrySyncButtonText));
    }

    public string OneDriveClientId { get => _oneDriveClientId; set => SetProperty(ref _oneDriveClientId, value); }
    public string OneDriveTenantId { get => _oneDriveTenantId; set => SetProperty(ref _oneDriveTenantId, value); }
    public string OAuthClientId { get => _oAuthClientId; set => SetProperty(ref _oAuthClientId, value); }
    public string OAuthClientSecret { get => _oAuthClientSecret; set => SetProperty(ref _oAuthClientSecret, value); }

    public bool IsSyncing
    {
        get => _isSyncing;
        private set
        {
            if (SetProperty(ref _isSyncing, value))
                NotifyCloudSyncStateChanged();
        }
    }

    public double SyncProgressPercent { get => _syncProgressPercent; private set => SetProperty(ref _syncProgressPercent, value); }
    public string SyncProgressText { get => _syncProgressText; private set => SetProperty(ref _syncProgressText, value); }
    public bool HasOAuthClientConfigured => _drive.HasOAuthClientConfigured;
    public string OAuthStageText { get => _oAuthStageText; private set => SetProperty(ref _oAuthStageText, value); }

    public bool InfoBarOpen { get => _infoBarOpen; set => SetProperty(ref _infoBarOpen, value); }
    public string InfoBarTitle { get => _infoBarTitle; private set => SetProperty(ref _infoBarTitle, value); }
    public string InfoBarMessage { get => _infoBarMessage; private set => SetProperty(ref _infoBarMessage, value); }
    public InfoBarSeverity InfoBarSeverity { get => _infoBarSeverity; private set => SetProperty(ref _infoBarSeverity, value); }
    public string InfoBarTechnicalDetails { get => _infoBarTechnicalDetails; private set { if (SetProperty(ref _infoBarTechnicalDetails, value)) OnPropertyChanged(nameof(InfoBarHasDetailsVisibility)); } }
    public Visibility InfoBarHasDetailsVisibility => string.IsNullOrWhiteSpace(InfoBarTechnicalDetails) ? Visibility.Collapsed : Visibility.Visible;
    public string InfoBarActionLabel { get => _infoBarActionLabel; private set { if (SetProperty(ref _infoBarActionLabel, value)) OnPropertyChanged(nameof(InfoBarActionVisibility)); } }
    public Visibility InfoBarActionVisibility => string.IsNullOrWhiteSpace(InfoBarActionLabel) ? Visibility.Collapsed : Visibility.Visible;

    public async Task RefreshCloudStateAsync()
    {
        var tokens = await _credentialStore.LoadAsync();
        IsDriveConnected = tokens is not null;
        ConnectedEmail = tokens?.Email ?? string.Empty;

        if (_oneDrive is not null)
        {
            var od = await _oneDrive.GetAccountInfoAsync();
            IsOneDriveConnected = od.IsConnected;
            OneDriveEmail = od.AccountLabel;
        }

        var queue = await _store.GetAllUploadQueueAsync(150);
        UploadQueue.Clear();
        foreach (var q in queue)
            UploadQueue.Add(new UploadQueueRow(q, _config.Language));

        if (!IsOAuthInProgress)
        {
            OAuthStageText = string.Empty;
            OnPropertyChanged(nameof(OAuthStageVisibility));
        }
        NotifyCloudProviderChanged();
    }

    private ICloudArchiveProvider? ResolveActiveCloudProvider() => _config.PrimaryCloudProvider switch
    {
        CloudProviderKind.GoogleDrive => _drive,
        CloudProviderKind.OneDrive => _oneDrive,
        _ => null
    };

    public async Task DownloadSelectedVersionAsync()
    {
        if (SelectedVersion is null)
            return;

        var provider = ResolveActiveCloudProvider();
        if (provider is null)
        {
            ShowInfoBar("Local-Only Mode Active", "Switch Primary Cloud Provider to Google Drive or OneDrive to download remote versions.", InfoBarSeverity.Informational);
            return;
        }

        IsSyncing = true;
        try
        {
            var targetId = SelectedVersion.Id;
            var res = await provider.DownloadVersionAsync(targetId);
            await RefreshArchiveAsync();
            if (res.Success)
            {
                await SelectArchiveVersionByIdAsync(targetId);
                ShowInfoBar("Downloaded & Verified", $"Saved SHA-256 verified archive locally at {res.LocalArchivePath}", InfoBarSeverity.Success);
            }
            else
            {
                ShowInfoBar("Download Failed", res.Error ?? "Unknown error", InfoBarSeverity.Error);
            }
        }
        finally
        {
            IsSyncing = false;
        }
    }

    public async Task PullCloudArchiveAsync()
    {
        var vi = _config.Language == UiLanguage.Vi;
        var provider = ResolveActiveCloudProvider();
        if (provider is null)
        {
            ShowInfoBar(
                vi ? "Chế độ chỉ lưu trên máy" : "Local-Only Mode",
                vi ? "Đã bỏ qua kéo dữ liệu đám mây vì đang chọn chế độ chỉ lưu trên máy." : "Cloud pull skipped because Local-Only mode is selected.",
                InfoBarSeverity.Informational);
            return;
        }

        IsSyncing = true;
        SyncProgressPercent = 0;
        SyncProgressText = vi ? "Đang kéo danh mục đám mây..." : "Pulling cloud index...";
        try
        {
            var progress = new Progress<DriveSyncProgress>(p =>
            {
                SyncProgressPercent = p.PercentComplete;
                SyncProgressText = p.Total > 0
                    ? $"{(vi ? "Đang kéo" : "Pull")} {p.Completed}/{p.Total}: {p.CurrentFileName}"
                    : (vi ? "Đang chờ" : "Idle");
            });
            var pull = await provider.PullAndReconcileAsync(progress);
            SaveConfigSafe();
            await RefreshAllAsync();

            if (!pull.Connected)
            {
                if (pull.ErrorDetails is not null)
                    ShowFriendlyErrorDetails(pull.ErrorDetails, InfoBarSeverity.Warning);
                else
                    ShowFriendlyError(ErrorCategory.AuthenticationExpired, pull.Error, InfoBarSeverity.Warning);
            }
            else if (pull.ErrorDetails is not null)
            {
                ShowFriendlyErrorDetails(pull.ErrorDetails, InfoBarSeverity.Error);
            }
            else if (!string.IsNullOrEmpty(pull.Error))
            {
                var mapped = UserFriendlyErrorService.ClassifyAndMap(new InvalidOperationException(pull.Error), _config.Language, ErrorCategory.NetworkOffline);
                ShowFriendlyInfo(mapped, InfoBarSeverity.Error);
            }
            else
                ShowInfoBar(
                    _config.Language == UiLanguage.Vi ? "Đã đồng bộ danh mục đám mây" : "Cloud Archive Reconciled",
                    _config.Language == UiLanguage.Vi
                        ? $"Phát hiện {pull.DiscoveredRemoteVersions} phiên bản • Chỉ trên đám mây: {pull.NewCloudOnlyCount} • Xung đột giữ nguyên: {pull.ConflictBranchCount}"
                        : $"Discovered {pull.DiscoveredRemoteVersions} remote versions • New Cloud-Only: {pull.NewCloudOnlyCount} • Conflicts preserved: {pull.ConflictBranchCount}",
                    InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowFriendlyException(ex, ErrorCategory.NetworkOffline);
        }
        finally
        {
            IsSyncing = false;
        }
    }

    public async Task DownloadAllCloudAsync()
    {
        var vi = _config.Language == UiLanguage.Vi;
        var provider = ResolveActiveCloudProvider();
        if (provider is null)
        {
            ShowInfoBar(
                vi ? "Chế độ chỉ lưu trên máy" : "Local-Only Mode",
                vi ? "Đã bỏ qua tải xuống đám mây vì đang chọn chế độ chỉ lưu trên máy." : "Cloud download skipped because Local-Only mode is selected.",
                InfoBarSeverity.Informational);
            return;
        }

        IsSyncing = true;
        try
        {
            var progress = new Progress<DriveSyncProgress>(p =>
            {
                SyncProgressPercent = p.PercentComplete;
                SyncProgressText = p.Total > 0
                    ? $"{(vi ? "Đang tải" : "Download")} {p.Completed}/{p.Total}: {p.CurrentFileName}"
                    : (vi ? "Đang chờ" : "Idle");
            });
            var (ok, failed, err) = await CloudTransferHelpers.DownloadAllCloudOnlyVersionsAsync(provider, _store, progress);
            await RefreshAllAsync();
            ShowInfoBar(
                failed > 0 ? (vi ? "Tải xuống chưa hoàn tất" : "Download Partial") : (vi ? "Đã tải tất cả tệp đám mây" : "All Cloud Files Downloaded"),
                vi ? $"Đã tải & xác minh: {ok}, Lỗi: {failed}. {err}".Trim() : $"Downloaded & verified: {ok}, Failed: {failed}. {err}".Trim(),
                failed > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
        }
        finally
        {
            IsSyncing = false;
        }
    }

    public async Task SaveOAuthClientAsync(string clientId, string clientSecret)
    {
        _config.OAuthClient = new GoogleOAuthClientConfig(clientId.Trim(), string.Empty);
        await _credentialStore.SaveClientSecretAsync(clientSecret);
        OAuthClientId = _config.OAuthClient.ClientId;
        OAuthClientSecret = clientSecret.Trim();
        SaveConfigSafe();
        OnPropertyChanged(nameof(HasOAuthClientConfigured));
        ShowInfoBar("OAuth Client Saved", "Desktop OAuth Client ID saved and secret protected via Windows DPAPI.", InfoBarSeverity.Success);
    }

    public async Task ConnectGoogleAsync()
    {
        if (!_drive.HasOAuthClientConfigured)
        {
            ShowInfoBar(
                _config.Language == UiLanguage.Vi ? "Chưa cấu hình OAuth Client ID" : "OAuth Client ID Required",
                _config.Language == UiLanguage.Vi
                    ? "Mở 'Cài đặt nâng cao' -> 'Cấu hình OAuth Client...' để nhập Google Desktop OAuth Client ID."
                    : "Open Advanced Settings -> Configure OAuth Client to enter your Google Desktop OAuth Client ID.",
                InfoBarSeverity.Warning);
            return;
        }

        await RunBrowserOAuthAsync("Google Drive", "Google", ct => _drive.ConnectWithBrowserAsync(ct: ct));
    }

    private async Task RunBrowserOAuthAsync(
        string providerDisplayName,
        string shortName,
        Func<CancellationToken, Task<GoogleAuthTokens>> connectFunc)
    {
        CancelOAuth();
        _oauthCts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        IsBusy = true;
        IsOAuthInProgress = true;
        bool vi = _config.Language == UiLanguage.Vi;
        try
        {
            var tokens = await connectFunc(_oauthCts.Token);
            IsOAuthInProgress = false;
            OAuthStageText = string.Empty;
            await RefreshCloudStateAsync();
            ShowInfoBar(
                vi ? $"Đã kết nối {providerDisplayName}" : $"{providerDisplayName} Connected",
                vi ? $"Đã đăng nhập: {tokens.Email}" : $"Signed in as {tokens.Email}.",
                InfoBarSeverity.Success);
        }
        catch (OperationCanceledException)
        {
            ShowInfoBar(
                vi ? $"Đã hủy đăng nhập {shortName}" : $"{providerDisplayName} Sign-In Cancelled",
                vi ? "Quá trình xác thực đã được hủy hoặc hết thời gian chờ." : "Authorization was cancelled or timed out.",
                InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            ShowFriendlyException(ex, ErrorCategory.AuthenticationExpired);
        }
        finally
        {
            IsBusy = false;
            IsOAuthInProgress = false;
            OAuthStageText = string.Empty;
            OnPropertyChanged(nameof(OAuthStageVisibility));
            _oauthCts?.Dispose();
            _oauthCts = null;
        }
    }

    private void CancelOAuth()
    {
        try { _oauthCts?.Cancel(); } catch { }
        IsOAuthInProgress = false;
        OAuthStageText = string.Empty;
        OnPropertyChanged(nameof(OAuthStageVisibility));
    }

    public async Task DisconnectGoogleAsync()
    {
        IsBusy = true;
        try
        {
            var vi = _config.Language == UiLanguage.Vi;
            _lastCloudServiceError = null;
            _drive.ClearApiConfigurationPause();
            var res = await _drive.DisconnectAsync();
            SaveConfigSafe();
            await RefreshCloudStateAsync();
            NotifyCloudSyncStateChanged();
            if (res.RemoteRevocationSucceeded)
                ShowInfoBar(
                    vi ? "Đã ngắt kết nối" : "Disconnected",
                    vi ? "Đã thu hồi phiên Google OAuth và xóa token khỏi kho lưu trữ an toàn DPAPI." : "Google OAuth tokens revoked remotely and wiped from DPAPI store.",
                    InfoBarSeverity.Informational);
            else
                ShowInfoBar(
                    vi ? "Đã ngắt kết nối cục bộ" : "Disconnected Locally",
                    vi ? $"Đã xóa token DPAPI, nhưng không thể thu hồi từ xa: {res.RemoteError}" : $"DPAPI tokens cleared, but remote revocation failed: {res.RemoteError}",
                    InfoBarSeverity.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task SaveOneDriveClientAsync(string clientId, string tenantId)
    {
        var cleanTenant = string.IsNullOrWhiteSpace(tenantId) ? "consumers" : tenantId.Trim();
        _config.OneDriveOAuthClient = new MicrosoftOAuthClientConfig(clientId.Trim(), cleanTenant);
        OneDriveClientId = _config.OneDriveOAuthClient.ClientId;
        OneDriveTenantId = _config.OneDriveOAuthClient.TenantId;
        SaveConfigSafe();
        ShowInfoBar("OneDrive Client Saved", "Microsoft Entra App Client ID saved cleanly.", InfoBarSeverity.Success);
        await Task.CompletedTask;
    }

    public async Task ConnectOneDriveAsync()
    {
        if (_oneDrive is null || !_oneDrive.IsConfigured)
        {
            ShowInfoBar(
                _config.Language == UiLanguage.Vi ? "Chưa cấu hình OneDrive Client ID" : "OneDrive Client ID Required",
                _config.Language == UiLanguage.Vi
                    ? "Mở 'Cài đặt nâng cao' -> 'Cấu hình OneDrive OAuth...' để nhập Microsoft Client ID."
                    : "Open Advanced Settings -> Configure OneDrive OAuth first.",
                InfoBarSeverity.Warning);
            return;
        }

        await RunBrowserOAuthAsync("OneDrive", "OneDrive", ct => _oneDrive.ConnectWithBrowserAsync(ct: ct));
    }

    public async Task DisconnectOneDriveAsync()
    {
        if (_oneDrive is null)
            return;

        IsBusy = true;
        try
        {
            await _oneDrive.DisconnectAsync();
            SaveConfigSafe();
            await RefreshCloudStateAsync();
            ShowInfoBar("OneDrive Disconnected", "Local DPAPI OneDrive tokens wiped.", InfoBarSeverity.Informational);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task MigrateCloudProviderAsync(bool googleToOneDrive)
    {
        if (_migrationService is null || _oneDrive is null)
        {
            ShowInfoBar("Migration Unavailable", "OneDrive and CloudMigrationService must be registered.", InfoBarSeverity.Warning);
            return;
        }

        IsSyncing = true;
        SyncProgressPercent = 0;
        SyncProgressText = googleToOneDrive ? "Migrating Google Drive -> OneDrive..." : "Migrating OneDrive -> Google Drive...";
        try
        {
            ICloudArchiveProvider source = googleToOneDrive ? _drive : _oneDrive;
            ICloudArchiveProvider dest = googleToOneDrive ? _oneDrive : _drive;
            var progress = new Progress<DriveSyncProgress>(p =>
            {
                SyncProgressPercent = p.PercentComplete;
                SyncProgressText = p.Total > 0 ? $"Migrate {p.Completed}/{p.Total}: {p.CurrentFileName}" : "Finalizing...";
            });
            var report = await _migrationService.MigrateAsync(source, dest, progress);
            await RefreshAllAsync();
            ShowInfoBar(
                report.Success ? "Cloud Migration Complete" : "Cloud Migration Failed",
                report.Summary,
                report.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        }
        finally
        {
            IsSyncing = false;
        }
    }

    public async Task SyncNowAsync()
    {
        var vi = _config.Language == UiLanguage.Vi;
        var provider = ResolveActiveCloudProvider();
        if (provider is null)
        {
            ShowInfoBar(
                vi ? "Chế độ chỉ lưu trên máy" : "Local-Only Mode",
                vi ? "Đã bỏ qua đồng bộ đám mây (chế độ chỉ lưu trên máy giữ toàn bộ bản sao lưu trên thiết bị)." : "Cloud sync skipped (Local-Only mode keeps all backups strictly on this device).",
                InfoBarSeverity.Informational);
            return;
        }

        _drive.ClearApiConfigurationPause();
        _lastCloudServiceError = null;
        IsSyncing = true;
        SyncProgressPercent = 0;
        SyncProgressText = vi
            ? $"Đang bắt đầu đồng bộ hai chiều {provider.DisplayName}..."
            : $"Starting {provider.DisplayName} bidirectional sync...";
        try
        {
            var progress = new Progress<DriveSyncProgress>(p =>
            {
                SyncProgressPercent = p.PercentComplete;
                SyncProgressText = p.Total > 0 ? $"{p.Completed}/{p.Total}: {p.CurrentFileName}" : (vi ? "Đang chờ" : "Idle");
            });
            var (report, pull) = await provider.SyncBidirectionalAsync(progress);
            SaveConfigSafe();
            _lastCloudServiceError = report.ErrorDetails ?? pull.ErrorDetails;
            await RefreshAllAsync();

            var effectiveDetails = report.ErrorDetails ?? pull.ErrorDetails;
            if (effectiveDetails is not null)
            {
                ShowFriendlyErrorDetails(effectiveDetails, effectiveDetails.Category == ErrorCategory.CloudApiDisabled ? InfoBarSeverity.Warning : InfoBarSeverity.Error);
            }
            else if (!report.Connected)
            {
                ShowFriendlyError(ErrorCategory.AuthenticationExpired, report.LastError, InfoBarSeverity.Warning);
            }
            else if (report.FailedCount > 0 || !string.IsNullOrEmpty(pull.Error))
            {
                var mapped = UserFriendlyErrorService.ClassifyAndMap(
                    new InvalidOperationException(report.LastError ?? pull.Error ?? "Sync failed"),
                    _config.Language,
                    ErrorCategory.NetworkOffline);
                ShowFriendlyInfo(mapped, InfoBarSeverity.Warning);
            }
            else
            {
                _lastCloudServiceError = null;
                ShowInfoBar(
                    _config.Language == UiLanguage.Vi ? "Đồng bộ thành công" : "Bidirectional Sync Complete",
                    _config.Language == UiLanguage.Vi
                        ? $"{provider.DisplayName}: Đã tải lên {report.UploadedCount} (Bỏ qua trùng lặp: {report.SkippedDuplicateCount}) • Đám mây mới: {pull.NewCloudOnlyCount}"
                        : $"{provider.DisplayName}: Uploaded {report.UploadedCount} (Deduped: {report.SkippedDuplicateCount}) • Cloud Discovered: {pull.NewCloudOnlyCount} • Conflicts: {pull.ConflictBranchCount}",
                    InfoBarSeverity.Success);
            }
        }
        catch (Exception ex)
        {
            ShowFriendlyException(ex, ErrorCategory.NetworkOffline);
        }
        finally
        {
            IsSyncing = false;
            NotifyCloudSyncStateChanged();
        }
    }

    public async Task RetryFailedAsync()
    {
        await _store.ResetFailedUploadsForRetryAsync();
        await SyncNowAsync();
    }

    public void ShowInfoBar(string title, string message, InfoBarSeverity severity, string technicalDetails = "", string actionLabel = "", string actionKey = "", string? actionUrl = null)
    {
        _dispatch(() =>
        {
            if (InfoBarOpen
                && InfoBarSeverity == severity
                && string.Equals(InfoBarTitle, title, StringComparison.Ordinal)
                && string.Equals(InfoBarMessage, message, StringComparison.Ordinal))
            {
                _infoBarActionUrl = actionUrl;
                RestartInfoBarAutoDismissTimerLocked(severity);
                return;
            }

            InfoBarTitle = title;
            InfoBarMessage = message;
            InfoBarSeverity = severity;
            InfoBarTechnicalDetails = UserFriendlyErrorService.RedactSecrets(technicalDetails);
            InfoBarActionLabel = actionLabel;
            _infoBarActionKey = actionKey;
            _infoBarActionUrl = actionUrl;
            InfoBarOpen = true;
            RestartInfoBarAutoDismissTimerLocked(severity);
        });
    }

    private void RestartInfoBarAutoDismissTimerLocked(InfoBarSeverity severity)
    {
        _infoBarDismissCts?.Cancel();
        _infoBarDismissCts?.Dispose();
        _infoBarDismissCts = null;

        // Errors stay visible until user dismisses or acts; Warnings stay unless user selects a timed duration > 0 and it isn't persistent error
        if (severity == InfoBarSeverity.Error)
            return;

        int seconds = _config.NotificationDurationSeconds;
        if (seconds <= 0)
            return;

        if (severity == InfoBarSeverity.Warning)
            seconds = Math.Max(seconds, 20);

        var cts = new CancellationTokenSource();
        _infoBarDismissCts = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds), cts.Token);
                if (!cts.Token.IsCancellationRequested)
                    _dispatch(() => InfoBarOpen = false);
            }
            catch (OperationCanceledException) { }
        });
    }

    public void ShowFriendlyError(ErrorCategory category, string? rawTechnicalDetails = null, InfoBarSeverity severity = InfoBarSeverity.Error)
    {
        var info = UserFriendlyErrorService.MapError(category, rawTechnicalDetails, _config.Language);
        ShowFriendlyInfo(info, severity);
    }

    public void ShowFriendlyErrorDetails(CloudServiceErrorDetails details, InfoBarSeverity severity = InfoBarSeverity.Error)
    {
        _lastCloudServiceError = details;
        NotifyCloudSyncStateChanged();
        var info = UserFriendlyErrorService.MapError(details, _config.Language);
        ShowFriendlyInfo(info, severity);
    }

    public void ShowFriendlyInfo(FriendlyErrorInfo info, InfoBarSeverity severity = InfoBarSeverity.Error) =>
        ShowInfoBar(info.Title, info.Explanation, severity, info.RedactedTechnicalDetails, info.ActionLabel, info.ActionKey, info.ActionUrl);

    public void ShowFriendlyException(Exception ex, ErrorCategory fallback = ErrorCategory.RuntimeError)
    {
        var info = UserFriendlyErrorService.ClassifyAndMap(ex, _config.Language, fallback);
        ShowFriendlyInfo(info, InfoBarSeverity.Error);
    }

    private void OpenDriveApiConsole()
    {
        var proj = _lastCloudServiceError?.ProjectNumber ?? GoogleApiErrorParser.ExtractProjectNumberFromClientId(_config.OAuthClient.ClientId);
        var safeUrl = GoogleApiErrorParser.ValidateGoogleConsoleUrl(_lastCloudServiceError?.ValidatedActivationUrl ?? _infoBarActionUrl, proj);
        try
        {
            Process.Start(new ProcessStartInfo(safeUrl) { UseShellExecute = true });
        }
        catch { }
    }

    private void CopyErrorDetails()
    {
        var payload = $"{InfoBarTitle}\n{InfoBarMessage}\n\n{InfoBarTechnicalDetails}".Trim();
        if (!string.IsNullOrEmpty(payload))
            TryCopyRawTextToClipboard(payload, _config.Language == UiLanguage.Vi ? "Đã sao chép chi tiết lỗi." : "Copied error details.");
    }

    private void ExecuteFriendlyErrorAction()
    {
        switch (_infoBarActionKey)
        {
            case "retry_sync":
                _ = SyncNowAsync();
                break;
            case "open_google_drive_api":
                OpenDriveApiConsole();
                break;
            case "connect_cloud":
                NavigationRequested?.Invoke("Cloud");
                break;
            case "open_cloud":
                NavigationRequested?.Invoke("Cloud");
                break;
            case "open_settings":
                NavigationRequested?.Invoke("Settings");
                break;
            case "open_archive":
                NavigationRequested?.Invoke("Archive");
                break;
            case "open_devtools":
                NavigationRequested?.Invoke("DevTools");
                break;
            case "open_browser_setup":
                var firstPending = InstalledBrowserRows.FirstOrDefault(b => !b.IsConnected)?.KindKey ?? "Edge";
                _ = OpenBrowserSetupAsync(firstPending);
                break;
            case "run_recovery":
                _ = RunRecoveryCheckAsync();
                break;
            case "undo_trash":
                _ = UndoLastTrashAsync();
                break;
        }
    }

    private void OnDriveSyncCompleted(DriveSyncReport report) => ScheduleCoalescedRefreshAsync(250);

    private void OnAuthStageChanged(OAuthConnectionStage stage, string message) => _dispatch(() =>
    {
        bool vi = _config.Language == UiLanguage.Vi;
        OAuthStageText = stage switch
        {
            OAuthConnectionStage.WaitingForBrowserCallback => vi
                ? "Đang chờ bạn xác nhận đăng nhập trong trình duyệt..."
                : "Waiting for sign-in confirmation in your browser...",
            OAuthConnectionStage.CallbackReceivedExchangingToken => vi
                ? "Đã nhận phản hồi, đang hoàn tất đăng nhập..."
                : "Authorization received, completing sign-in...",
            _ => string.Empty
        };
        OnPropertyChanged(nameof(OAuthStageVisibility));
    });
}
