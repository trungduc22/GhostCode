using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GhostCode.App.Localization;
using GhostCode.Core.Analysis;
using GhostCode.Core.Archiver;
using GhostCode.Core.Browser;
using GhostCode.Core.Cloud;
using GhostCode.Core.Models;
using GhostCode.Core.Monitoring;
using GhostCode.Core.Storage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GhostCode.App.ViewModels;

public sealed record ArchiveVersionRow(ArchiveVersionRecord Record)
{
    public string Id => Record.Id;
    public string VersionBadge => $"v{Record.VersionNum}";
    public string FileName => Record.FileName;
    public string SourcePath => Record.SourcePath;
    public string ArchivePath => Record.ArchivePath;
    public string? ReadyPath => Record.ReadyPath;
    public string Platform => Record.Platform;
    public string ProblemSummary => string.IsNullOrWhiteSpace(Record.ProblemCode)
        ? $"{Record.Platform} • {Record.SizeBytes} B"
        : $"{Record.Platform} • {Record.ProblemCode} — {Record.ProblemTitle}";
    public string ShortSha => Record.Sha256.Length >= 8 ? Record.Sha256[..8] : Record.Sha256;
    public string TimestampText => DateTimeOffset.FromUnixTimeMilliseconds(Record.CreatedAtUnixMs)
        .ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
}

public sealed record UploadQueueRow(UploadQueueView View)
{
    public string FileName => View.Version.FileName;
    public string StatusText => View.Queue.Status.ToString();
    public string DetailText => View.Queue.Status switch
    {
        UploadStatus.Synced => $"Drive ID: {View.Queue.DriveFileId}",
        UploadStatus.Failed => $"Attempts: {View.Queue.AttemptCount} • {View.Queue.LastError}",
        _ => $"Attempts: {View.Queue.AttemptCount} • Waiting for sync"
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

public sealed record ActiveWatchRow(ActiveWatchInfo Info)
{
    public string FolderPath => Info.FolderPath;
    public string StatusText => Info.IsWatching
        ? (Info.Recursive ? "Watching (Recursive)" : "Watching (Top-Level)")
        : $"Error: {Info.Error}";
}

public sealed class WatchedFolderItem : ObservableObject
{
    private readonly Action _onChanged;
    private bool _isEnabled;
    private bool _isRecursive;

    public WatchedFolderItem(WatchedFolderConfig cfg, Action onChanged)
    {
        Path = cfg.Path;
        Label = string.IsNullOrWhiteSpace(cfg.Label) ? System.IO.Path.GetFileName(cfg.Path) : cfg.Label;
        _isEnabled = cfg.Enabled;
        _isRecursive = cfg.Recursive;
        _onChanged = onChanged;
    }

    public string Path { get; }
    public string Label { get; }

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

/// <summary>
/// Central MVVM ViewModel bridging WinUI 3 pages with GhostCode.Core services.
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly GhostCodeConfig _config;
    private readonly string _configPath;
    private readonly SqliteArchiveStore _store;
    private readonly LosslessArchiver _archiver;
    private readonly MultiDirectoryMonitor _monitor;
    private readonly AuthenticatedBrowserBridge _bridge;
    private readonly GoogleDriveService _drive;
    private readonly DpapiCredentialStore _credentialStore;

    private Func<Action, bool> _dispatch = action => { action(); return true; };
    private Action<ElementTheme>? _applyThemeCallback;
    private Action<string>? _clipboardCopyCallback;

    private UiTextBundle _strings;
    private string _activeCode = string.Empty;
    private string _activeTitle = string.Empty;
    private string _activePlatform = "Offline";
    private string _activeUrl = string.Empty;
    private bool _hasActiveContext;
    private string _ideStatusSummary = "Detecting...";
    private string _browserStatusSummary = "Checking...";
    private int _totalVersions;
    private int _pendingUploads;
    private int _syncedUploads;
    private int _failedUploads;
    private string _lastSyncTimeText = "Never";

    private string _searchQuery = string.Empty;
    private string _selectedPlatformFilter = "All";
    private ArchiveVersionRow? _selectedVersion;
    private string _selectedVersionSource = string.Empty;

    private bool _isDriveConnected;
    private string _connectedEmail = string.Empty;
    private string _oAuthClientId = string.Empty;
    private string _oAuthClientSecret = string.Empty;
    private bool _isSyncing;
    private double _syncProgressPercent;
    private string _syncProgressText = string.Empty;

    private string _archiveDir = string.Empty;
    private string _readyDir = string.Empty;
    private int _selectedLanguageIndex;
    private int _selectedThemeIndex;
    private bool _minimizeToTrayOnClose;
    private bool _autoCopyReadyPath;
    private bool _driveEnabled;

    private string _lastDetectedFileText = "None";
    private string _lastSuccessfulBackupText = "None";
    private string _lastMonitoringErrorText = "None";
    private string _lastDiagnosticResultText = "Not run yet";
    private string _oAuthStageText = "Idle";

    private bool _infoBarOpen;
    private string _infoBarTitle = string.Empty;
    private string _infoBarMessage = string.Empty;
    private InfoBarSeverity _infoBarSeverity = InfoBarSeverity.Informational;
    private bool _isBusy;
    private bool _suppressFolderSync;

    public MainViewModel(
        GhostCodeConfig config,
        string configPath,
        SqliteArchiveStore store,
        LosslessArchiver archiver,
        MultiDirectoryMonitor monitor,
        AuthenticatedBrowserBridge bridge,
        GoogleDriveService drive,
        DpapiCredentialStore credentialStore)
    {
        _config = config;
        _configPath = configPath;
        _store = store;
        _archiver = archiver;
        _monitor = monitor;
        _bridge = bridge;
        _drive = drive;
        _credentialStore = credentialStore;
        _strings = UiStrings.For(config.Language);

        LoadFieldsFromConfig();

        RefreshAllCommand = new AsyncRelayCommand(RefreshAllAsync);
        RunDiagnosticCommand = new AsyncRelayCommand(RunDiagnosticTestAsync);
        SyncNowCommand = new AsyncRelayCommand(SyncNowAsync);
        RetryFailedCommand = new AsyncRelayCommand(RetryFailedAsync);
        ConnectGoogleCommand = new AsyncRelayCommand(ConnectGoogleAsync);
        DisconnectGoogleCommand = new AsyncRelayCommand(DisconnectGoogleAsync);
        SearchArchiveCommand = new AsyncRelayCommand(RefreshArchiveAsync);
        OpenArchiveFolderCommand = new RelayCommand(() => OpenFolder(_config.ArchiveDir));
        OpenReadyFolderCommand = new RelayCommand(() => OpenFolder(_config.ReadyDir));
        CopySelectedCodeCommand = new RelayCommand(CopySelectedCode);

        _archiver.VersionArchived += OnVersionArchived;
        _monitor.EventRecorded += OnMonitorEventRecorded;
        _bridge.ContextChanged += OnContextChanged;
        _drive.SyncCompleted += OnDriveSyncCompleted;
        _drive.AuthStageChanged += OnAuthStageChanged;
    }

    public ObservableCollection<ActiveWatchRow> ActiveWatches { get; } = new();
    public ObservableCollection<MonitorEventRow> RecentEvents { get; } = new();
    public ObservableCollection<ArchiveVersionRow> ArchiveVersions { get; } = new();
    public ObservableCollection<UploadQueueRow> UploadQueue { get; } = new();
    public ObservableCollection<WatchedFolderItem> WatchedFolders { get; } = new();

    public IReadOnlyList<string> PlatformFilters { get; } =
        new[] { "All", "Ptit", "Codeforces", "Vnoj", "LeetCode", "HackerRank", "Offline", "Other" };

    public IAsyncRelayCommand RefreshAllCommand { get; }
    public IAsyncRelayCommand RunDiagnosticCommand { get; }
    public IAsyncRelayCommand SyncNowCommand { get; }
    public IAsyncRelayCommand RetryFailedCommand { get; }
    public IAsyncRelayCommand ConnectGoogleCommand { get; }
    public IAsyncRelayCommand DisconnectGoogleCommand { get; }
    public IAsyncRelayCommand SearchArchiveCommand { get; }
    public IRelayCommand OpenArchiveFolderCommand { get; }
    public IRelayCommand OpenReadyFolderCommand { get; }
    public IRelayCommand CopySelectedCodeCommand { get; }

    public UiTextBundle Strings
    {
        get => _strings;
        private set => SetProperty(ref _strings, value);
    }

    public string ActiveCode { get => _activeCode; private set => SetProperty(ref _activeCode, value); }
    public string ActiveTitle { get => _activeTitle; private set => SetProperty(ref _activeTitle, value); }
    public string ActivePlatform { get => _activePlatform; private set => SetProperty(ref _activePlatform, value); }
    public string ActiveUrl { get => _activeUrl; private set => SetProperty(ref _activeUrl, value); }
    public bool HasActiveContext { get => _hasActiveContext; private set => SetProperty(ref _hasActiveContext, value); }
    public string IdeStatusSummary { get => _ideStatusSummary; private set => SetProperty(ref _ideStatusSummary, value); }
    public string BrowserStatusSummary { get => _browserStatusSummary; private set => SetProperty(ref _browserStatusSummary, value); }

    public int TotalVersions { get => _totalVersions; private set => SetProperty(ref _totalVersions, value); }
    public int PendingUploads { get => _pendingUploads; private set => SetProperty(ref _pendingUploads, value); }
    public int SyncedUploads { get => _syncedUploads; private set => SetProperty(ref _syncedUploads, value); }
    public int FailedUploads { get => _failedUploads; private set => SetProperty(ref _failedUploads, value); }
    public string LastSyncTimeText { get => _lastSyncTimeText; private set => SetProperty(ref _lastSyncTimeText, value); }

    public string SearchQuery
    {
        get => _searchQuery;
        set => SetProperty(ref _searchQuery, value);
    }

    public string SelectedPlatformFilter
    {
        get => _selectedPlatformFilter;
        set
        {
            if (SetProperty(ref _selectedPlatformFilter, value))
                _ = RefreshArchiveAsync();
        }
    }

    public ArchiveVersionRow? SelectedVersion
    {
        get => _selectedVersion;
        set
        {
            if (SetProperty(ref _selectedVersion, value))
                _ = LoadSelectedVersionSourceAsync(value);
        }
    }

    public string SelectedVersionSource
    {
        get => _selectedVersionSource;
        private set => SetProperty(ref _selectedVersionSource, value);
    }

    public bool IsDriveConnected
    {
        get => _isDriveConnected;
        private set
        {
            if (SetProperty(ref _isDriveConnected, value))
                OnPropertyChanged(nameof(DriveConnectionStatusText));
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

    public string OAuthClientId { get => _oAuthClientId; set => SetProperty(ref _oAuthClientId, value); }
    public string OAuthClientSecret { get => _oAuthClientSecret; set => SetProperty(ref _oAuthClientSecret, value); }
    public bool IsSyncing { get => _isSyncing; private set => SetProperty(ref _isSyncing, value); }
    public double SyncProgressPercent { get => _syncProgressPercent; private set => SetProperty(ref _syncProgressPercent, value); }
    public string SyncProgressText { get => _syncProgressText; private set => SetProperty(ref _syncProgressText, value); }
    public bool HasOAuthClientConfigured => _drive.HasOAuthClientConfigured;
    public string BridgeInfoText => $"127.0.0.1:{_bridge.ListeningPort}  •  HMAC Paired Session (Secret Hidden)";

    public string LastDetectedFileText { get => _lastDetectedFileText; private set => SetProperty(ref _lastDetectedFileText, value); }
    public string LastSuccessfulBackupText { get => _lastSuccessfulBackupText; private set => SetProperty(ref _lastSuccessfulBackupText, value); }
    public string LastMonitoringErrorText { get => _lastMonitoringErrorText; private set => SetProperty(ref _lastMonitoringErrorText, value); }
    public string LastDiagnosticResultText { get => _lastDiagnosticResultText; private set => SetProperty(ref _lastDiagnosticResultText, value); }
    public string OAuthStageText { get => _oAuthStageText; private set => SetProperty(ref _oAuthStageText, value); }

    public string ArchiveDir { get => _archiveDir; private set => SetProperty(ref _archiveDir, value); }
    public string ReadyDir { get => _readyDir; private set => SetProperty(ref _readyDir, value); }

    public int SelectedLanguageIndex
    {
        get => _selectedLanguageIndex;
        set
        {
            if (value < 0 || !SetProperty(ref _selectedLanguageIndex, value))
                return;
            var lang = value == 0 ? UiLanguage.Vi : UiLanguage.En;
            if (_config.Language != lang)
            {
                _config.Language = lang;
                Strings = UiStrings.For(lang);
                OnPropertyChanged(nameof(DriveConnectionStatusText));
                SaveConfigSafe();
            }
        }
    }

    public int SelectedThemeIndex
    {
        get => _selectedThemeIndex;
        set
        {
            if (value < 0 || !SetProperty(ref _selectedThemeIndex, value))
                return;
            var theme = (ThemePreference)value;
            if (_config.Theme != theme)
            {
                _config.Theme = theme;
                SaveConfigSafe();
                _applyThemeCallback?.Invoke(ToElementTheme(theme));
            }
        }
    }

    public bool MinimizeToTrayOnClose
    {
        get => _minimizeToTrayOnClose;
        set
        {
            if (SetProperty(ref _minimizeToTrayOnClose, value) && _config.MinimizeToTrayOnClose != value)
            {
                _config.MinimizeToTrayOnClose = value;
                SaveConfigSafe();
            }
        }
    }

    public bool AutoCopyReadyPath
    {
        get => _autoCopyReadyPath;
        set
        {
            if (SetProperty(ref _autoCopyReadyPath, value) && _config.AutoCopyReadyPath != value)
            {
                _config.AutoCopyReadyPath = value;
                SaveConfigSafe();
            }
        }
    }

    public bool DriveEnabled
    {
        get => _driveEnabled;
        set
        {
            if (SetProperty(ref _driveEnabled, value) && _config.DriveEnabled != value)
            {
                _config.DriveEnabled = value;
                SaveConfigSafe();
            }
        }
    }

    public bool InfoBarOpen { get => _infoBarOpen; set => SetProperty(ref _infoBarOpen, value); }
    public string InfoBarTitle { get => _infoBarTitle; private set => SetProperty(ref _infoBarTitle, value); }
    public string InfoBarMessage { get => _infoBarMessage; private set => SetProperty(ref _infoBarMessage, value); }
    public InfoBarSeverity InfoBarSeverity { get => _infoBarSeverity; private set => SetProperty(ref _infoBarSeverity, value); }
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }

    public GhostCodeConfig Config => _config;

    public void AttachUiHooks(
        Func<Action, bool> dispatcher,
        Action<ElementTheme> applyTheme,
        Action<string> clipboardCopy)
    {
        _dispatch = dispatcher;
        _applyThemeCallback = applyTheme;
        _clipboardCopyCallback = clipboardCopy;
        _applyThemeCallback(ToElementTheme(_config.Theme));
    }

    public static ElementTheme ToElementTheme(ThemePreference pref) => pref switch
    {
        ThemePreference.Dark => ElementTheme.Dark,
        ThemePreference.Light => ElementTheme.Light,
        _ => ElementTheme.Default
    };

    public async Task InitializeAsync()
    {
        OAuthClientSecret = await _credentialStore.LoadClientSecretAsync();
        UpdateContext(_bridge.GetActiveContext());
        await RefreshAllAsync();
    }

    public async Task RefreshAllAsync()
    {
        RefreshProcessesAndWatches();
        await RefreshStatsAndEventsAsync();
        await RefreshArchiveAsync();
        await RefreshCloudStateAsync();
    }

    public void RefreshProcessesAndWatches()
    {
        var ide = IdeProcessDetector.Detect();
        IdeStatusSummary = ide.ActiveIdeSummary;
        var browsers = new List<string>();
        if (ide.Edge) browsers.Add("Edge");
        if (ide.Thorium) browsers.Add("Thorium");
        BrowserStatusSummary = browsers.Count == 0
            ? "No supported browser running"
            : $"{string.Join(", ", browsers)} • Port {_bridge.ListeningPort}";

        LastDetectedFileText = _monitor.LastDetectedFile;
        LastSuccessfulBackupText = _monitor.LastSuccessfulBackup;
        LastMonitoringErrorText = _monitor.LastMonitoringError;
        LastDiagnosticResultText = _monitor.LastDiagnosticSummary;

        var watches = _monitor.GetActiveWatches();
        ActiveWatches.Clear();
        foreach (var w in watches)
            ActiveWatches.Add(new ActiveWatchRow(w));
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
            : "Never";

        var events = await _store.GetRecentMonitorEventsAsync(40);
        RecentEvents.Clear();
        foreach (var ev in events)
            RecentEvents.Add(new MonitorEventRow(ev));
    }

    public async Task RefreshArchiveAsync()
    {
        var list = await _store.QueryVersionsAsync(SearchQuery, SelectedPlatformFilter, 250);
        var selectedId = SelectedVersion?.Id;
        ArchiveVersions.Clear();
        foreach (var v in list)
            ArchiveVersions.Add(new ArchiveVersionRow(v));

        if (!string.IsNullOrEmpty(selectedId))
            SelectedVersion = ArchiveVersions.FirstOrDefault(x => x.Id == selectedId);
    }

    public async Task RefreshCloudStateAsync()
    {
        var tokens = await _credentialStore.LoadAsync();
        IsDriveConnected = tokens is not null;
        ConnectedEmail = tokens?.Email ?? string.Empty;

        var queue = await _store.GetAllUploadQueueAsync(150);
        UploadQueue.Clear();
        foreach (var q in queue)
            UploadQueue.Add(new UploadQueueRow(q));
    }

    public async Task RunDiagnosticTestAsync()
    {
        IsBusy = true;
        try
        {
            var res = await _monitor.RunDiagnosticTestAsync();
            await RefreshAllAsync();
            ShowInfoBar(
                res.Success ? "Monitoring Diagnostic Passed" : "Monitoring Diagnostic Failed",
                res.Summary,
                res.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        }
        catch (Exception ex)
        {
            ShowInfoBar("Diagnostic Error", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<bool> RestoreSelectedVersionAsync(string destinationPath)
    {
        if (SelectedVersion is null)
            return false;

        try
        {
            await _archiver.RestoreVersionAsync(SelectedVersion.Id, destinationPath);
            ShowInfoBar(
                "Version Restored",
                $"Restored {SelectedVersion.VersionBadge} ({SelectedVersion.FileName}) -> {destinationPath}",
                InfoBarSeverity.Success);
            return true;
        }
        catch (Exception ex)
        {
            ShowInfoBar("Restore Failed (Integrity Check)", ex.Message, InfoBarSeverity.Error);
            return false;
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
                "OAuth Client ID Required",
                "Click 'Configure OAuth Client...' first to enter your Google Cloud Desktop OAuth Client ID.",
                InfoBarSeverity.Warning);
            return;
        }

        IsBusy = true;
        try
        {
            var tokens = await _drive.ConnectWithBrowserAsync();
            await RefreshCloudStateAsync();
            ShowInfoBar("Google Drive Connected", $"Signed in as {tokens.Email}.", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowInfoBar("Google Sign-In Failed", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task DisconnectGoogleAsync()
    {
        IsBusy = true;
        try
        {
            var res = await _drive.DisconnectAsync();
            SaveConfigSafe();
            await RefreshCloudStateAsync();
            if (res.RemoteRevocationSucceeded)
            {
                ShowInfoBar("Disconnected", "Google OAuth tokens revoked remotely and wiped from DPAPI store.", InfoBarSeverity.Informational);
            }
            else
            {
                ShowInfoBar("Disconnected Locally", $"DPAPI tokens cleared, but remote revocation failed: {res.RemoteError}", InfoBarSeverity.Warning);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task SyncNowAsync()
    {
        IsSyncing = true;
        SyncProgressPercent = 0;
        SyncProgressText = "Starting upload...";
        try
        {
            var progress = new Progress<DriveSyncProgress>(p =>
            {
                SyncProgressPercent = p.PercentComplete;
                SyncProgressText = p.Total > 0 ? $"{p.Completed}/{p.Total}: {p.CurrentFileName}" : "Idle";
            });
            var report = await _drive.SyncPendingAsync(progress);
            SaveConfigSafe(); // Persist cached DriveRootFolderId if created
            await RefreshAllAsync();

            if (!report.Connected)
                ShowInfoBar("Cloud Sync Paused", report.LastError ?? "Not connected", InfoBarSeverity.Warning);
            else if (report.FailedCount > 0)
                ShowInfoBar("Sync Completed With Errors", $"Uploaded: {report.UploadedCount}, Failed: {report.FailedCount}. {report.LastError}", InfoBarSeverity.Warning);
            else
                ShowInfoBar("Cloud Sync Complete", $"Uploaded: {report.UploadedCount} (Deduped: {report.SkippedDuplicateCount})", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowInfoBar("Sync Error", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            IsSyncing = false;
        }
    }

    public async Task RetryFailedAsync()
    {
        await _store.ResetFailedUploadsForRetryAsync();
        await SyncNowAsync();
    }

    public void AddWatchedFolder(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
            return;
        if (KnownFoldersResolver.IsDriveRoot(folderPath))
        {
            ShowInfoBar("Drive Root Refused", "Watching an entire drive root is blocked for safety and performance.", InfoBarSeverity.Error);
            return;
        }
        if (_monitor.IsExcludedOrOutputDirectory(folderPath))
        {
            ShowInfoBar("Output Directory Excluded", "ArchiveDir and ReadyDir cannot be monitored to prevent infinite feedback loops.", InfoBarSeverity.Error);
            return;
        }
        if (WatchedFolders.Any(w => string.Equals(w.Path, folderPath, StringComparison.OrdinalIgnoreCase)))
        {
            ShowInfoBar("Already Monitored", folderPath, InfoBarSeverity.Informational);
            return;
        }

        _suppressFolderSync = true;
        WatchedFolders.Add(new WatchedFolderItem(new WatchedFolderConfig(folderPath, true, true, Path.GetFileName(folderPath)), SyncWatchedFoldersToConfigAndReload));
        _suppressFolderSync = false;
        SyncWatchedFoldersToConfigAndReload();
        ShowInfoBar("Folder Added", $"Now monitoring: {folderPath}", InfoBarSeverity.Success);
    }

    public void RemoveWatchedFolder(WatchedFolderItem item)
    {
        _suppressFolderSync = true;
        WatchedFolders.Remove(item);
        _suppressFolderSync = false;
        SyncWatchedFoldersToConfigAndReload();
        ShowInfoBar("Folder Removed", item.Path, InfoBarSeverity.Informational);
    }

    public void SetArchiveDirectory(string newPath)
    {
        if (string.IsNullOrWhiteSpace(newPath) || KnownFoldersResolver.IsDriveRoot(newPath))
            return;
        _config.ArchiveDir = newPath;
        ArchiveDir = newPath;
        SaveConfigSafe();
        _monitor.ReloadWatches();
        RefreshProcessesAndWatches();
        ShowInfoBar("Archive Path Updated", newPath, InfoBarSeverity.Success);
    }

    public void SetReadyDirectory(string newPath)
    {
        if (string.IsNullOrWhiteSpace(newPath) || KnownFoldersResolver.IsDriveRoot(newPath))
            return;
        _config.ReadyDir = newPath;
        ReadyDir = newPath;
        SaveConfigSafe();
        _monitor.ReloadWatches();
        RefreshProcessesAndWatches();
        ShowInfoBar("PTIT Ready Path Updated", newPath, InfoBarSeverity.Success);
    }

    public string ExportConfigJson() => _config.ExportToJson();

    public void ShowInfoBar(string title, string message, InfoBarSeverity severity)
    {
        _dispatch(() =>
        {
            InfoBarTitle = title;
            InfoBarMessage = message;
            InfoBarSeverity = severity;
            InfoBarOpen = true;
        });
    }

    private void LoadFieldsFromConfig()
    {
        _oAuthClientId = _config.OAuthClient.ClientId;
        _oAuthClientSecret = _config.OAuthClient.ClientSecret;
        _archiveDir = _config.ArchiveDir;
        _readyDir = _config.ReadyDir;
        _selectedLanguageIndex = _config.Language == UiLanguage.Vi ? 0 : 1;
        _selectedThemeIndex = (int)_config.Theme;
        _minimizeToTrayOnClose = _config.MinimizeToTrayOnClose;
        _autoCopyReadyPath = _config.AutoCopyReadyPath;
        _driveEnabled = _config.DriveEnabled;

        _suppressFolderSync = true;
        WatchedFolders.Clear();
        foreach (var wf in _config.WatchedFolders)
            WatchedFolders.Add(new WatchedFolderItem(wf, SyncWatchedFoldersToConfigAndReload));
        _suppressFolderSync = false;
    }

    private void SyncWatchedFoldersToConfigAndReload()
    {
        if (_suppressFolderSync)
            return;
        _config.WatchedFolders = WatchedFolders.Select(w => w.ToConfig()).ToList();
        SaveConfigSafe();
        _monitor.ReloadWatches();
        RefreshProcessesAndWatches();
    }

    private void SaveConfigSafe()
    {
        try { _config.Save(_configPath); }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to save config: {ex.Message}");
        }
    }

    private void CopySelectedCode()
    {
        if (!string.IsNullOrEmpty(SelectedVersionSource))
        {
            _clipboardCopyCallback?.Invoke(SelectedVersionSource);
            ShowInfoBar("Copied", "Source code copied to clipboard.", InfoBarSeverity.Success);
        }
    }

    private async Task LoadSelectedVersionSourceAsync(ArchiveVersionRow? row)
    {
        if (row is null)
        {
            SelectedVersionSource = string.Empty;
            return;
        }

        try
        {
            var bytes = await _archiver.ReadAndVerifyVersionBytesAsync(row.Id);
            var (text, _) = SourceAnalyzer.DecodePreservingInfo(bytes);
            SelectedVersionSource = text;
        }
        catch (Exception ex)
        {
            SelectedVersionSource = $"// [INTEGRITY ERROR] {ex.Message}";
            ShowInfoBar("Archive Integrity Error", ex.Message, InfoBarSeverity.Error);
        }
    }

    private void UpdateContext(ProblemContext? ctx)
    {
        if (ctx is null)
        {
            HasActiveContext = false;
            ActiveCode = "-";
            ActiveTitle = Strings.NoActiveContext;
            ActivePlatform = "Offline";
            ActiveUrl = string.Empty;
            return;
        }

        HasActiveContext = true;
        ActiveCode = ctx.Code;
        ActiveTitle = ctx.Title;
        ActivePlatform = ctx.Platform.ToString();
        ActiveUrl = ctx.Url;
    }

    private void OnContextChanged(ProblemContext? ctx)
    {
        _dispatch(() => UpdateContext(ctx));
    }

    private void OnVersionArchived(ArchiveVersionRecord version)
    {
        _dispatch(() =>
        {
            if (_config.AutoCopyReadyPath && !string.IsNullOrWhiteSpace(version.ReadyPath))
                _clipboardCopyCallback?.Invoke(version.ReadyPath);
            _ = RefreshAllAsync();
        });
    }

    private void OnMonitorEventRecorded(MonitorEventRecord ev)
    {
        _dispatch(() =>
        {
            RecentEvents.Insert(0, new MonitorEventRow(ev));
            while (RecentEvents.Count > 50)
                RecentEvents.RemoveAt(RecentEvents.Count - 1);
            RefreshProcessesAndWatches();
        });
    }

    private void OnDriveSyncCompleted(DriveSyncReport report)
    {
        _dispatch(() => { _ = RefreshAllAsync(); });
    }

    private void OnAuthStageChanged(OAuthConnectionStage stage, string message)
    {
        _dispatch(() => { OAuthStageText = $"{stage}: {message}"; });
    }

    private static void OpenFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
    }

    public void Dispose()
    {
        _archiver.VersionArchived -= OnVersionArchived;
        _monitor.EventRecorded -= OnMonitorEventRecorded;
        _bridge.ContextChanged -= OnContextChanged;
        _drive.SyncCompleted -= OnDriveSyncCompleted;
        _drive.AuthStageChanged -= OnAuthStageChanged;
    }
}
