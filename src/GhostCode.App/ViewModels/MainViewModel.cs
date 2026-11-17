using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GhostCode.App.Localization;
using GhostCode.Core.Ai;
using GhostCode.Core.Analysis;
using GhostCode.Core.Archiver;
using GhostCode.Core.Browser;
using GhostCode.Core.Cloud;
using GhostCode.Core.Execution;
using GhostCode.Core.Models;
using GhostCode.Core.Monitoring;
using GhostCode.Core.Notifications;
using GhostCode.Core.Storage;
using GhostCode.Core.Toolchains;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GhostCode.App.ViewModels;

public sealed record ArchiveVersionRow(ArchiveVersionRecord Record, UiLanguage Language = UiLanguage.Vi)
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
    public string AvailabilityBadge => UiStrings.ForAvailability(Record.Availability, Language);
    public string ProblemSummary => string.IsNullOrWhiteSpace(Record.ProblemCode)
        ? $"{Record.Platform} • {Record.SizeBytes} B"
        : $"{Record.Platform} • {Record.ProblemCode} — {Record.ProblemTitle}";
    public string ShortSha => Record.Sha256.Length >= 8 ? Record.Sha256[..8] : Record.Sha256;
    public string TimestampText => DateTimeOffset.FromUnixTimeMilliseconds(Record.CreatedAtUnixMs)
        .ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string SelectorLabel => $"{VersionBadge} • {TimestampText} • {ShortSha} [{AvailabilityBadge}]";
}

public sealed record ArchiveFileGroupRow(ArchiveVersionRecord Latest, int VersionCount, UiLanguage Language = UiLanguage.Vi)
{
    public string Id => Latest.Id;
    public string FileName => Latest.FileName;
    public string SourcePath => Latest.SourcePath;
    public string LatestVersionBadge => $"v{Latest.VersionNum} ({VersionCount})";
    public string AvailabilityBadge => UiStrings.ForAvailability(Latest.Availability, Language);
    public string Subtitle => string.IsNullOrWhiteSpace(Latest.ProblemCode)
        ? $"{Latest.Platform} • {AvailabilityBadge}"
        : $"{Latest.Platform} • {Latest.ProblemCode} • {AvailabilityBadge}";
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
}

public sealed record IdeStatusRow(IdeStatusInfo Info, UiLanguage Language = UiLanguage.Vi)
{
    public string Name => Info.Name;
    public string StatusBadge => Language == UiLanguage.Vi
        ? (Info.IsRunning ? "Đang chạy" : Info.IsInstalled ? "Đã cài đặt" : "Chưa cài đặt")
        : (Info.IsRunning ? "Running" : Info.IsInstalled ? "Installed" : "Not Installed");
    public string VersionText => Info.Version ?? string.Empty;
    public string ToolchainText => string.IsNullOrWhiteSpace(Info.ToolchainSummary)
        ? Info.ExecutablePath ?? (Language == UiLanguage.Vi ? "Chưa phát hiện" : "Not detected")
        : $"{Info.ToolchainSummary} • {Info.ExecutablePath}";
}

public sealed record ToolchainRow(ToolchainInfo Info, UiLanguage Language = UiLanguage.Vi)
{
    public string Name => Info.DisplayName;
    public string StatusBadge => Language == UiLanguage.Vi
        ? (Info.IsAvailable ? "Sẵn sàng" : "Chưa có")
        : (Info.IsAvailable ? "Ready" : "Unavailable");
    public string VersionText => Info.Version ?? string.Empty;
    public string DetailText => Info.IsAvailable
        ? $"{Info.ExecutablePath} ({Info.DefaultFlags})"
        : (Language == UiLanguage.Vi && string.IsNullOrWhiteSpace(Info.ExecutablePath) ? "Chưa tìm thấy trong PATH" : Info.StatusDetail);
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
    public string LeftNumText => Pair.LeftLineNumber?.ToString() ?? string.Empty;
    public string LeftText => Pair.LeftText;
    public string RightNumText => Pair.RightLineNumber?.ToString() ?? string.Empty;
    public string RightText => Pair.RightText;
    public string KindTag => Pair.Kind switch
    {
        DiffLineKind.Added => "+",
        DiffLineKind.Removed => "-",
        DiffLineKind.Modified => "~",
        _ => " "
    };
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
/// Central MVVM ViewModel bridging WinUI 3 views with GhostCode.Core services.
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
    private readonly BrowserIntegrationService _browserIntegration;
    private readonly ToolchainDiscoveryService _toolchains;
    private readonly CodeExecutionService _runner;
    private readonly BackupHealthAndRecoveryService _healthService;
    private readonly IdeLauncherService _ideLauncher;
    private readonly NotificationCoordinator _notifications;
    private readonly OneDriveService? _oneDrive;
    private readonly CloudMigrationService? _migrationService;
    private readonly SampleTestRunnerService _sampleRunner;
    private readonly CodingTimeTracker _codingTime;
    private readonly CopilotAssistanceService _copilot;

    private Func<Action, bool> _dispatch = action => { action(); return true; };
    private Action<ElementTheme>? _applyThemeCallback;
    private Func<string, bool>? _clipboardCopyWithRetry;
    private Action<MicaAppearanceKind>? _applyMicaCallback;
    private Func<bool, string, (bool Success, string Detail)>? _configureHotkeyCallback;
    private CancellationTokenSource? _execCts;
    private Timer? _discoveryRefreshTimer;

    private UiTextBundle _strings;
    private ExpansionTextBundle _expStrings;
    private UxOverhaulTextBundle _uxStrings;
    private ArchiveVersionRow? _compareWithVersion;
    private bool _isDiffModeActive;
    private bool _diffUseUnifiedMode;
    private string _diffSummaryText = string.Empty;
    private string _diffUnifiedText = string.Empty;

    private bool _isQuickActionsOpen;
    private ArchiveFileGroupRow? _selectedQuickActionTarget;
    private string _quickActionsLatestPreview = string.Empty;
    private string _quickActionsAmbiguityNote = string.Empty;
    private string _activeSampleInputText = string.Empty;
    private string _activeSampleOutputText = string.Empty;
    private SampleTestCaseRecord? _selectedSampleTest;
    private string _currentProblemStatementSummary = "Select an archived problem or browse a supported OJ problem to load its statement and sample cases.";
    private string _latestVerdictBadgeText = "No Judge Verdict";
    private string _sampleRunSummaryText = "Not run yet";
    private string _sampleComparisonDiffText = string.Empty;
    private string _timelineSummaryText = "Timeline: no recorded events for this problem yet.";
    private string _activeCodingTimeText = "Coding time estimation disabled (Opt-in in Settings)";
    private int _selectedMicaKindIndex;
    private int _selectedNotificationLevelIndex;
    private bool _globalHotkeyEnabled = true;
    private string _globalHotkeyGesture = "Ctrl+Shift+F9";
    private bool _captureProblemStatements = true;
    private bool _trackSubmissionVerdicts = true;
    private bool _trimTrailingWhitespaceOnCompare = true;
    private bool _estimateActiveCodingTime;
    private bool _aiAssistanceOptIn;

    private string _backupHealthSummary = "Checking...";
    private string _backupProtectionBadge = "Checking";
    private string _recoverySummaryText = "Not run yet";
    private string _globalHotkeyStatusText = "Ctrl+Shift+F9";
    private bool _syncPausedManually;
    private string _activeCode = "-";
    private string _activeTitle = string.Empty;
    private string _activePlatform = "Offline";
    private string _activeUrl = string.Empty;
    private string _activeBrowserName = string.Empty;
    private string _activeFilenamePreview = string.Empty;
    private string _activeContextStateBadge = "Offline";
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
    private int _selectedAvailabilityFilterIndex;
    private ArchiveFileGroupRow? _selectedFileGroup;
    private ArchiveVersionRow? _selectedVersion;
    private string _selectedVersionSource = string.Empty;

    private string _stdinText = string.Empty;
    private string _stdoutText = string.Empty;
    private string _stderrText = string.Empty;
    private string _consoleLogText = string.Empty;
    private string _runnerStatusText = string.Empty;
    private string _runnerMetricsText = string.Empty;
    private int _runnerTabIndex = 1; // Default to I/O split view or toggleable Console/I/O
    private bool _isExecuting;

    private bool _isDriveConnected;
    private string _connectedEmail = string.Empty;
    private bool _isOneDriveConnected;
    private string _oneDriveEmail = string.Empty;
    private int _selectedCloudProviderIndex;
    private string _oAuthClientId = string.Empty;
    private string _oAuthClientSecret = string.Empty;
    private string _oneDriveClientId = string.Empty;
    private string _oneDriveTenantId = "consumers";
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
    private string _cCompilerPath = string.Empty;
    private string _cppCompilerPath = string.Empty;
    private string _pythonInterpreterPath = string.Empty;
    private string _selectedCStandard = "c11";
    private string _selectedCppStandard = "auto";
    private double _executionTimeoutMs = 5000;
    private double _executionMemoryLimitMb = 256;
    private int _selectedConflictPolicyIndex;

    private string _lastDetectedFileText = "None";
    private string _lastSuccessfulBackupText = "None";
    private string _lastMonitoringErrorText = "None";
    private string _lastDiagnosticResultText = "Not run yet";
    private string _oAuthStageText = "Idle";

    private bool _infoBarOpen;
    private string _infoBarTitle = string.Empty;
    private string _infoBarMessage = string.Empty;
    private string _infoBarTechnicalDetails = string.Empty;
    private string _infoBarActionLabel = string.Empty;
    private string _infoBarActionKey = string.Empty;
    private InfoBarSeverity _infoBarSeverity = InfoBarSeverity.Informational;
    private bool _isBusy;
    private bool _suppressFolderSync;
    private bool _onboardingDismissedForSession;

    private bool _isBrowserSetupOpen;
    private string _browserSetupBrowserName = string.Empty;
    private string _browserSetupBrowserKindKey = string.Empty;
    private string _browserSetupExtensionPath = string.Empty;
    private string _browserSetupPageUrl = string.Empty;
    private string _browserSetupStatusText = string.Empty;
    private bool _isOAuthInProgress;
    private CancellationTokenSource? _oauthCts;

    public MainViewModel(
        GhostCodeConfig config,
        string configPath,
        SqliteArchiveStore store,
        LosslessArchiver archiver,
        MultiDirectoryMonitor monitor,
        AuthenticatedBrowserBridge bridge,
        GoogleDriveService drive,
        DpapiCredentialStore credentialStore,
        BrowserIntegrationService? browserIntegration = null,
        ToolchainDiscoveryService? toolchains = null,
        CodeExecutionService? runner = null,
        BackupHealthAndRecoveryService? healthService = null,
        IdeLauncherService? ideLauncher = null,
        NotificationCoordinator? notifications = null,
        OneDriveService? oneDrive = null,
        CloudMigrationService? migrationService = null,
        SampleTestRunnerService? sampleRunner = null,
        CodingTimeTracker? codingTime = null,
        CopilotAssistanceService? copilot = null)
    {
        _config = config;
        _configPath = configPath;
        _store = store;
        _archiver = archiver;
        _monitor = monitor;
        _bridge = bridge;
        _drive = drive;
        _credentialStore = credentialStore;
        _browserIntegration = browserIntegration ?? new BrowserIntegrationService();
        _toolchains = toolchains ?? new ToolchainDiscoveryService();
        _runner = runner ?? new CodeExecutionService(_config, _store, _archiver, _toolchains);
        _notifications = notifications ?? new NotificationCoordinator(_config, _store);
        _healthService = healthService ?? new BackupHealthAndRecoveryService(_config, _store, _monitor, _notifications);
        _ideLauncher = ideLauncher ?? new IdeLauncherService(_config, _toolchains, _archiver);
        _oneDrive = oneDrive;
        _migrationService = migrationService;
        _sampleRunner = sampleRunner ?? new SampleTestRunnerService(_config, _store, _runner);
        _codingTime = codingTime ?? new CodingTimeTracker(_config);
        _copilot = copilot ?? new CopilotAssistanceService(_config);
        _strings = UiStrings.For(config.Language);
        _expStrings = UiStrings.ExpansionFor(config.Language);
        _uxStrings = UxOverhaulStrings.For(config.Language);
        _runnerStatusText = _strings.StatusReady;
        _consoleLogText = _strings.ConsoleOutputEmpty;
        _lastDetectedFileText = _uxStrings.NoneText;
        _lastSuccessfulBackupText = _uxStrings.NoneText;
        _lastMonitoringErrorText = _uxStrings.NoneText;
        _lastDiagnosticResultText = _uxStrings.NotRunYetText;
        _lastSyncTimeText = _uxStrings.NeverText;
        _recoverySummaryText = _uxStrings.NotRunYetText;
        _oAuthStageText = string.Empty;

        LoadFieldsFromConfig();

        RefreshAllCommand = new AsyncRelayCommand(RefreshAllAsync);
        RefreshDiscoveryCommand = new RelayCommand(RefreshProcessesAndWatches);
        RunDiagnosticCommand = new AsyncRelayCommand(RunDiagnosticTestAsync);
        RunRecoveryCheckCommand = new AsyncRelayCommand(RunRecoveryCheckAsync);
        ToggleSyncPauseCommand = new RelayCommand(ToggleSyncPause);
        SyncNowCommand = new AsyncRelayCommand(SyncNowAsync);
        PullCloudArchiveCommand = new AsyncRelayCommand(PullCloudArchiveAsync);
        DownloadAllCloudCommand = new AsyncRelayCommand(DownloadAllCloudAsync);
        DownloadSelectedVersionCommand = new AsyncRelayCommand(DownloadSelectedVersionAsync);
        RetryFailedCommand = new AsyncRelayCommand(RetryFailedAsync);
        ConnectGoogleCommand = new AsyncRelayCommand(ConnectGoogleAsync);
        DisconnectGoogleCommand = new AsyncRelayCommand(DisconnectGoogleAsync);
        SearchArchiveCommand = new AsyncRelayCommand(RefreshArchiveAsync);
        RunCodeCommand = new AsyncRelayCommand(RunSelectedVersionAsync, () => CanRunSelectedVersion);
        StopExecutionCommand = new RelayCommand(StopExecution, () => IsExecuting);
        ClearConsoleCommand = new RelayCommand(ClearConsole);
        PrepareBrowserIntegrationCommand = new AsyncRelayCommand<string?>(PrepareBrowserIntegrationAsync);
        OpenArchiveFolderCommand = new RelayCommand(() => OpenFolder(_config.ArchiveDir));
        OpenReadyFolderCommand = new RelayCommand(() => OpenFolder(_config.ReadyDir));
        CopySelectedCodeCommand = new AsyncRelayCommand(CopySelectedCodeAsync);
        CopyLatestProblemCodeCommand = new AsyncRelayCommand(CopyLatestProblemCodeAsync);
        CopySampleInputCommand = new RelayCommand(CopyActiveSampleInput);
        CopySampleOutputCommand = new RelayCommand(CopyActiveSampleOutput);
        CopyProblemUrlCommand = new RelayCommand(CopyActiveProblemUrl);
        CopySourcePathCommand = new RelayCommand(CopySelectedSourcePath);
        OpenInIdeCommand = new AsyncRelayCommand(OpenSelectedVersionInIdeAsync);
        OpenOriginalInIdeCommand = new AsyncRelayCommand(OpenOriginalSourceInIdeAsync);
        ToggleDiffModeCommand = new RelayCommand(ToggleDiffMode);
        ToggleDiffStyleCommand = new RelayCommand(() => DiffUseUnifiedMode = !DiffUseUnifiedMode);
        ToggleQuickActionsCommand = new RelayCommand(ToggleQuickActionsPanel);
        CloseQuickActionsCommand = new RelayCommand(() => IsQuickActionsOpen = false);
        OpenQuickActionsInIdeCommand = new AsyncRelayCommand(OpenQuickActionsTargetInIdeAsync);
        ConnectOneDriveCommand = new AsyncRelayCommand(ConnectOneDriveAsync);
        DisconnectOneDriveCommand = new AsyncRelayCommand(DisconnectOneDriveAsync);
        MigrateToOneDriveCommand = new AsyncRelayCommand(() => MigrateCloudProviderAsync(googleToOneDrive: true));
        MigrateToGoogleDriveCommand = new AsyncRelayCommand(() => MigrateCloudProviderAsync(googleToOneDrive: false));
        RunSelectedSampleCommand = new AsyncRelayCommand(RunSelectedSampleAsync);
        RunAllSamplesCommand = new AsyncRelayCommand(RunAllSamplesAsync);
        DeleteSelectedSampleCommand = new AsyncRelayCommand(DeleteSelectedSampleAsync);
        DismissOnboardingCommand = new RelayCommand(DismissOnboarding);
        OpenBrowserSetupCommand = new AsyncRelayCommand<string?>(kind => OpenBrowserSetupAsync(kind));
        CopyStagedExtensionPathCommand = new RelayCommand(CopyStagedExtensionPath);
        OpenStagedExtensionFolderCommand = new RelayCommand(() => OpenFolder(BrowserSetupExtensionPath));
        OpenBrowserExtensionsPageCommand = new RelayCommand(OpenTargetBrowserExtensionsPage);
        CloseBrowserSetupCommand = new RelayCommand(() => IsBrowserSetupOpen = false);
        CopyErrorDetailsCommand = new RelayCommand(CopyErrorDetails);
        ExecuteFriendlyErrorActionCommand = new RelayCommand(ExecuteFriendlyErrorAction);
        CancelOAuthCommand = new RelayCommand(CancelOAuth);

        _archiver.VersionArchived += OnVersionArchived;
        _monitor.EventRecorded += OnMonitorEventRecorded;
        _monitor.ReconciliationCompleted += OnReconciliationCompleted;
        _bridge.ContextChanged += OnContextChanged;
        _bridge.BrowserConnectionsChanged += OnBrowserConnectionsChanged;
        _bridge.ProblemStatementReceived += OnProblemStatementReceived;
        _bridge.SubmissionVerdictReceived += OnSubmissionVerdictReceived;
        _drive.SyncCompleted += OnDriveSyncCompleted;
        _drive.AuthStageChanged += OnAuthStageChanged;
        if (_oneDrive is not null)
        {
            _oneDrive.SyncCompleted += OnDriveSyncCompleted;
            _oneDrive.AuthStageChanged += OnAuthStageChanged;
        }

        _discoveryRefreshTimer = new Timer(_ => _dispatch(RefreshBrowserAndIdeDiscovery), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    public ObservableCollection<ActiveWatchRow> ActiveWatches { get; } = new();
    public ObservableCollection<MonitorEventRow> RecentEvents { get; } = new();
    public ObservableCollection<ArchiveHistoryEventRow> HistoryEvents { get; } = new();
    public ObservableCollection<ArchiveVersionRow> ArchiveVersions { get; } = new();
    public ObservableCollection<ArchiveFileGroupRow> ArchiveFileGroups { get; } = new();
    public ObservableCollection<ArchiveVersionRow> SelectedFileVersions { get; } = new();
    public ObservableCollection<UploadQueueRow> UploadQueue { get; } = new();
    public ObservableCollection<WatchedFolderItem> WatchedFolders { get; } = new();
    public ObservableCollection<BrowserStatusRow> BrowserStatuses { get; } = new();
    public ObservableCollection<BrowserStatusRow> InstalledBrowserRows { get; } = new();
    public ObservableCollection<BrowserStatusRow> ConnectedBrowsers { get; } = new();
    public ObservableCollection<IdeStatusRow> IdeStatuses { get; } = new();
    public ObservableCollection<ToolchainRow> Toolchains { get; } = new();
    public ObservableCollection<GlobalSearchSuggestion> GlobalSearchSuggestions { get; } = new();
    public ObservableCollection<DiffLineRow> DiffSideBySideLines { get; } = new();
    public ObservableCollection<ArchiveFileGroupRow> QuickActionsCandidates { get; } = new();
    public ObservableCollection<SampleTestCaseRecord> SampleTests { get; } = new();
    public ObservableCollection<ProblemTimelineEntry> TimelineEntries { get; } = new();
    public ObservableCollection<NotificationRecord> NotificationHistory { get; } = new();

    public IReadOnlyList<string> PlatformFilters { get; } =
        new[] { "All", "Ptit", "Codeforces", "Vnoj", "LeetCode", "HackerRank", "Offline", "Other" };

    public IReadOnlyList<string> CStandardChoices { get; } =
        new[] { "c11", "c17", "c99", "gnu11" };

    public IReadOnlyList<string> CppStandardChoices { get; } =
        new[] { "auto", "c++17", "c++14", "c++11", "c++20" };

    public IReadOnlyList<string> AvailabilityFilterLabels => new[]
    {
        Strings.AvailabilityAll,
        Strings.AvailabilityLocalOnly,
        Strings.AvailabilitySynced,
        Strings.AvailabilityCloudOnly,
        Strings.AvailabilityConflict
    };

    public IReadOnlyList<string> ThemeChoices => new[]
    {
        Strings.ThemeSystem,
        Strings.ThemeLight,
        Strings.ThemeDark
    };

    public IReadOnlyList<string> MicaChoices => new[]
    {
        UxStrings.MicaBaseLabel,
        UxStrings.MicaAltLabel,
        UxStrings.MicaSolidLabel
    };

    public IReadOnlyList<string> NotificationLevelChoices => new[]
    {
        UxStrings.NotifAllLabel,
        UxStrings.NotifWarnErrLabel,
        UxStrings.NotifDisabledLabel
    };

    public IReadOnlyList<string> CloudProviderChoices => new[]
    {
        UxStrings.DriveProviderGoogle,
        UxStrings.DriveProviderOneDrive,
        UxStrings.DriveProviderNone
    };

    public IReadOnlyList<string> ConflictPolicyChoices => _config.Language == UiLanguage.Vi
        ? new[]
        {
            "Giữ cả 2 nhánh (Không ghi đè)",
            "Ưu tiên bản trên máy",
            "Ưu tiên bản đám mây"
        }
        : new[]
        {
            "Preserve Both Branches (Never Overwrite)",
            "Prefer Local",
            "Prefer Remote"
        };

    public event Action<string>? NavigationRequested;

    public IAsyncRelayCommand RefreshAllCommand { get; }
    public IRelayCommand RefreshDiscoveryCommand { get; }
    public IAsyncRelayCommand RunDiagnosticCommand { get; }
    public IAsyncRelayCommand SyncNowCommand { get; }
    public IAsyncRelayCommand PullCloudArchiveCommand { get; }
    public IAsyncRelayCommand DownloadAllCloudCommand { get; }
    public IAsyncRelayCommand DownloadSelectedVersionCommand { get; }
    public IAsyncRelayCommand RetryFailedCommand { get; }
    public IAsyncRelayCommand ConnectGoogleCommand { get; }
    public IAsyncRelayCommand DisconnectGoogleCommand { get; }
    public IAsyncRelayCommand SearchArchiveCommand { get; }
    public IAsyncRelayCommand RunCodeCommand { get; }
    public IRelayCommand StopExecutionCommand { get; }
    public IRelayCommand ClearConsoleCommand { get; }
    public IAsyncRelayCommand<string?> PrepareBrowserIntegrationCommand { get; }
    public IRelayCommand OpenArchiveFolderCommand { get; }
    public IRelayCommand OpenReadyFolderCommand { get; }
    public IAsyncRelayCommand CopySelectedCodeCommand { get; }
    public IAsyncRelayCommand CopyLatestProblemCodeCommand { get; }
    public IRelayCommand CopySampleInputCommand { get; }
    public IRelayCommand CopySampleOutputCommand { get; }
    public IRelayCommand CopyProblemUrlCommand { get; }
    public IRelayCommand CopySourcePathCommand { get; }
    public IAsyncRelayCommand OpenInIdeCommand { get; }
    public IAsyncRelayCommand OpenOriginalInIdeCommand { get; }
    public IRelayCommand ToggleDiffModeCommand { get; }
    public IRelayCommand ToggleDiffStyleCommand { get; }
    public IRelayCommand ToggleQuickActionsCommand { get; }
    public IRelayCommand CloseQuickActionsCommand { get; }
    public IAsyncRelayCommand OpenQuickActionsInIdeCommand { get; }
    public IAsyncRelayCommand RunRecoveryCheckCommand { get; }
    public IRelayCommand ToggleSyncPauseCommand { get; }
    public IAsyncRelayCommand ConnectOneDriveCommand { get; }
    public IAsyncRelayCommand DisconnectOneDriveCommand { get; }
    public IAsyncRelayCommand MigrateToOneDriveCommand { get; }
    public IAsyncRelayCommand MigrateToGoogleDriveCommand { get; }
    public IAsyncRelayCommand RunSelectedSampleCommand { get; }
    public IAsyncRelayCommand RunAllSamplesCommand { get; }
    public IAsyncRelayCommand DeleteSelectedSampleCommand { get; }
    public IRelayCommand DismissOnboardingCommand { get; }
    public IAsyncRelayCommand<string?> OpenBrowserSetupCommand { get; }
    public IRelayCommand CopyStagedExtensionPathCommand { get; }
    public IRelayCommand OpenStagedExtensionFolderCommand { get; }
    public IRelayCommand OpenBrowserExtensionsPageCommand { get; }
    public IRelayCommand CloseBrowserSetupCommand { get; }
    public IRelayCommand CopyErrorDetailsCommand { get; }
    public IRelayCommand ExecuteFriendlyErrorActionCommand { get; }
    public IRelayCommand CancelOAuthCommand { get; }

    public UxOverhaulTextBundle UxStrings
    {
        get => _uxStrings;
        private set
        {
            if (SetProperty(ref _uxStrings, value))
            {
                OnPropertyChanged(nameof(MicaChoices));
                OnPropertyChanged(nameof(NotificationLevelChoices));
                OnPropertyChanged(nameof(CloudProviderChoices));
                OnPropertyChanged(nameof(ConflictPolicyChoices));
                OnPropertyChanged(nameof(TitleBarDriveBadgeText));
            }
        }
    }

    public ExpansionTextBundle ExpStrings
    {
        get => _expStrings;
        private set
        {
            if (SetProperty(ref _expStrings, value))
                OnPropertyChanged(nameof(SyncPauseButtonText));
        }
    }

    public UiTextBundle Strings
    {
        get => _strings;
        private set
        {
            if (SetProperty(ref _strings, value))
            {
                OnPropertyChanged(nameof(AvailabilityFilterLabels));
                OnPropertyChanged(nameof(ThemeChoices));
                OnPropertyChanged(nameof(TitleBarBrowserBadgeText));
                OnPropertyChanged(nameof(TitleBarDriveBadgeText));
                OnPropertyChanged(nameof(DriveConnectionStatusText));
                OnPropertyChanged(nameof(NoFoldersVisible));
                OnPropertyChanged(nameof(OnboardingCardVisibility));
                _ = RefreshHealthAsync();
            }
        }
    }

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

    public int TotalVersions { get => _totalVersions; private set => SetProperty(ref _totalVersions, value); }
    public int PendingUploads
    {
        get => _pendingUploads;
        private set
        {
            if (SetProperty(ref _pendingUploads, value))
                OnPropertyChanged(nameof(TitleBarDriveBadgeText));
        }
    }
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

    public int SelectedAvailabilityFilterIndex
    {
        get => _selectedAvailabilityFilterIndex;
        set
        {
            if (SetProperty(ref _selectedAvailabilityFilterIndex, value))
                _ = RefreshArchiveAsync();
        }
    }

    public ArchiveFileGroupRow? SelectedFileGroup
    {
        get => _selectedFileGroup;
        set
        {
            if (SetProperty(ref _selectedFileGroup, value) && value is not null)
                _ = LoadVersionsForFileGroupAsync(value.SourcePath, preferredVersionId: null);
        }
    }

    public ArchiveVersionRow? SelectedVersion
    {
        get => _selectedVersion;
        set
        {
            if (SetProperty(ref _selectedVersion, value))
            {
                OnPropertyChanged(nameof(SelectedVersionIsCloudOnly));
                OnPropertyChanged(nameof(CanRunSelectedVersion));
                OnPropertyChanged(nameof(SelectedVersionHeaderText));
                RunCodeCommand.NotifyCanExecuteChanged();
                _ = LoadSelectedVersionSourceAsync(value);
            }
        }
    }

    public string SelectedVersionSource
    {
        get => _selectedVersionSource;
        private set => SetProperty(ref _selectedVersionSource, value);
    }

    public string SelectedVersionHeaderText => SelectedVersion is null
        ? Strings.NoVersionSelected
        : $"{SelectedVersion.FileName} ({SelectedVersion.VersionBadge}) • SHA: {SelectedVersion.ShortSha} • {SelectedVersion.AvailabilityBadge}";

    public bool SelectedVersionIsCloudOnly => SelectedVersion is not null && !SelectedVersion.IsLocalAvailable;
    public bool CanRunSelectedVersion => SelectedVersion is not null && SelectedVersion.IsLocalAvailable && !IsExecuting;

    public string StdinText { get => _stdinText; set => SetProperty(ref _stdinText, value); }
    public string StdoutText
    {
        get => _stdoutText;
        private set
        {
            if (SetProperty(ref _stdoutText, value))
                OnPropertyChanged(nameof(IoOutputText));
        }
    }
    public string StderrText
    {
        get => _stderrText;
        private set
        {
            if (SetProperty(ref _stderrText, value))
                OnPropertyChanged(nameof(IoOutputText));
        }
    }
    public string IoOutputText
    {
        get
        {
            if (string.IsNullOrEmpty(StderrText))
                return StdoutText;
            if (string.IsNullOrEmpty(StdoutText))
                return $"[STDERR]\n{StderrText}";
            return $"{StdoutText}\n[STDERR]\n{StderrText}";
        }
    }
    public string ConsoleLogText { get => _consoleLogText; private set => SetProperty(ref _consoleLogText, value); }
    public string RunnerStatusText { get => _runnerStatusText; private set => SetProperty(ref _runnerStatusText, value); }
    public string RunnerMetricsText { get => _runnerMetricsText; private set => SetProperty(ref _runnerMetricsText, value); }

    public int RunnerTabIndex
    {
        get => _runnerTabIndex;
        set
        {
            if (SetProperty(ref _runnerTabIndex, value))
            {
                OnPropertyChanged(nameof(ConsoleTabVisibility));
                OnPropertyChanged(nameof(IoTabVisibility));
                OnPropertyChanged(nameof(SamplesTabVisibility));
            }
        }
    }

    public Visibility ConsoleTabVisibility => RunnerTabIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility IoTabVisibility => RunnerTabIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SamplesTabVisibility => RunnerTabIndex == 2 ? Visibility.Visible : Visibility.Collapsed;

    public SampleTestCaseRecord? SelectedSampleTest
    {
        get => _selectedSampleTest;
        set
        {
            if (SetProperty(ref _selectedSampleTest, value))
            {
                _activeSampleInputText = value?.InputText ?? string.Empty;
                _activeSampleOutputText = value?.ExpectedOutput ?? string.Empty;
                OnPropertyChanged(nameof(SelectedSampleInputPreview));
                OnPropertyChanged(nameof(SelectedSampleExpectedPreview));
            }
        }
    }

    public string SelectedSampleInputPreview => SelectedSampleTest?.InputText ?? string.Empty;
    public string SelectedSampleExpectedPreview => SelectedSampleTest?.ExpectedOutput ?? string.Empty;
    public string CurrentProblemStatementSummary { get => _currentProblemStatementSummary; private set => SetProperty(ref _currentProblemStatementSummary, value); }
    public string LatestVerdictBadgeText { get => _latestVerdictBadgeText; private set => SetProperty(ref _latestVerdictBadgeText, value); }
    public string SampleRunSummaryText { get => _sampleRunSummaryText; private set => SetProperty(ref _sampleRunSummaryText, value); }
    public string SampleComparisonDiffText { get => _sampleComparisonDiffText; private set => SetProperty(ref _sampleComparisonDiffText, value); }
    public string TimelineSummaryText { get => _timelineSummaryText; private set => SetProperty(ref _timelineSummaryText, value); }
    public string ActiveCodingTimeText { get => _activeCodingTimeText; private set => SetProperty(ref _activeCodingTimeText, value); }

    public int SelectedMicaKindIndex
    {
        get => _selectedMicaKindIndex;
        set
        {
            if (value < 0 || value > 2 || !SetProperty(ref _selectedMicaKindIndex, value))
                return;
            _config.MicaKind = (MicaAppearanceKind)value;
            SaveConfigSafe();
            _applyMicaCallback?.Invoke(_config.MicaKind);
        }
    }

    public int SelectedNotificationLevelIndex
    {
        get => _selectedNotificationLevelIndex;
        set
        {
            if (value < 0 || value > 2 || !SetProperty(ref _selectedNotificationLevelIndex, value))
                return;
            _config.NotificationLevel = (NotificationLevel)value;
            SaveConfigSafe();
        }
    }

    public bool GlobalHotkeyEnabled
    {
        get => _globalHotkeyEnabled;
        set
        {
            if (SetProperty(ref _globalHotkeyEnabled, value) && _config.GlobalHotkeyEnabled != value)
            {
                _config.GlobalHotkeyEnabled = value;
                SaveConfigSafe();
                if (_configureHotkeyCallback is not null)
                {
                    var res = _configureHotkeyCallback(_config.GlobalHotkeyEnabled, _config.GlobalHotkeyGesture);
                    GlobalHotkeyStatusText = res.Detail;
                }
            }
        }
    }

    public string GlobalHotkeyGesture
    {
        get => _globalHotkeyGesture;
        set
        {
            if (SetProperty(ref _globalHotkeyGesture, value ?? string.Empty))
            {
                _config.GlobalHotkeyGesture = _globalHotkeyGesture.Trim();
                SaveConfigSafe();
                if (_configureHotkeyCallback is not null)
                {
                    var res = _configureHotkeyCallback(_config.GlobalHotkeyEnabled, _config.GlobalHotkeyGesture);
                    GlobalHotkeyStatusText = res.Detail;
                }
            }
        }
    }

    public bool CaptureProblemStatements
    {
        get => _captureProblemStatements;
        set
        {
            if (SetProperty(ref _captureProblemStatements, value) && _config.CaptureProblemStatements != value)
            {
                _config.CaptureProblemStatements = value;
                SaveConfigSafe();
            }
        }
    }

    public bool TrackSubmissionVerdicts
    {
        get => _trackSubmissionVerdicts;
        set
        {
            if (SetProperty(ref _trackSubmissionVerdicts, value) && _config.TrackSubmissionVerdicts != value)
            {
                _config.TrackSubmissionVerdicts = value;
                SaveConfigSafe();
            }
        }
    }

    public bool TrimTrailingWhitespaceOnCompare
    {
        get => _trimTrailingWhitespaceOnCompare;
        set
        {
            if (SetProperty(ref _trimTrailingWhitespaceOnCompare, value) && _config.TrimTrailingWhitespaceOnCompare != value)
            {
                _config.TrimTrailingWhitespaceOnCompare = value;
                SaveConfigSafe();
            }
        }
    }

    public bool EstimateActiveCodingTime
    {
        get => _estimateActiveCodingTime;
        set
        {
            if (SetProperty(ref _estimateActiveCodingTime, value) && _config.EstimateActiveCodingTime != value)
            {
                _config.EstimateActiveCodingTime = value;
                SaveConfigSafe();
                ActiveCodingTimeText = _codingTime.FormatEstimatedDuration(ResolveCurrentProblemKey());
            }
        }
    }

    public bool AiAssistanceOptIn
    {
        get => _aiAssistanceOptIn;
        set
        {
            if (SetProperty(ref _aiAssistanceOptIn, value) && _config.AiAssistanceOptIn != value)
            {
                _config.AiAssistanceOptIn = value;
                SaveConfigSafe();
                OnPropertyChanged(nameof(CopilotStatusText));
            }
        }
    }

    public string CopilotStatusText => _copilot.GetAvailabilityStatus();

    public bool IsExecuting
    {
        get => _isExecuting;
        private set
        {
            if (SetProperty(ref _isExecuting, value))
            {
                OnPropertyChanged(nameof(CanRunSelectedVersion));
                RunCodeCommand.NotifyCanExecuteChanged();
                StopExecutionCommand.NotifyCanExecuteChanged();
            }
        }
    }

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
    }

    public string OneDriveClientId { get => _oneDriveClientId; set => SetProperty(ref _oneDriveClientId, value); }
    public string OneDriveTenantId { get => _oneDriveTenantId; set => SetProperty(ref _oneDriveTenantId, value); }

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
                ExpStrings = UiStrings.ExpansionFor(lang);
                UxStrings = UxOverhaulStrings.For(lang);
                RunnerStatusText = Strings.StatusReady;
                SaveConfigSafe();
                UpdateContext(_bridge.GetActiveContext());
                RefreshProcessesAndWatches();
                _ = RefreshStatsAndEventsAsync();
                _ = RefreshCloudStateAsync();
                _ = RefreshArchiveAsync();
                OnPropertyChanged(nameof(BrowserSetupTitle));
                OnPropertyChanged(nameof(BrowserSetupStep1));
                OnPropertyChanged(nameof(BrowserSetupStep2));
                OnPropertyChanged(nameof(BrowserSetupStep3));
                OnPropertyChanged(nameof(BrowserSetupStep4));
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

    public string CCompilerPath
    {
        get => _cCompilerPath;
        set
        {
            if (SetProperty(ref _cCompilerPath, value))
            {
                _config.CCompilerPath = value?.Trim() ?? string.Empty;
                SaveConfigSafe();
            }
        }
    }

    public string CppCompilerPath
    {
        get => _cppCompilerPath;
        set
        {
            if (SetProperty(ref _cppCompilerPath, value))
            {
                _config.CppCompilerPath = value?.Trim() ?? string.Empty;
                SaveConfigSafe();
            }
        }
    }

    public string PythonInterpreterPath
    {
        get => _pythonInterpreterPath;
        set
        {
            if (SetProperty(ref _pythonInterpreterPath, value))
            {
                _config.PythonInterpreterPath = value?.Trim() ?? string.Empty;
                SaveConfigSafe();
            }
        }
    }

    public string SelectedCStandard
    {
        get => _selectedCStandard;
        set
        {
            if (!string.IsNullOrWhiteSpace(value) && SetProperty(ref _selectedCStandard, value) && _config.CStandard != value)
            {
                _config.CStandard = value;
                SaveConfigSafe();
            }
        }
    }

    public string SelectedCppStandard
    {
        get => _selectedCppStandard;
        set
        {
            if (!string.IsNullOrWhiteSpace(value) && SetProperty(ref _selectedCppStandard, value) && _config.CppStandard != value)
            {
                _config.CppStandard = value;
                SaveConfigSafe();
            }
        }
    }

    public double ExecutionTimeoutMs
    {
        get => _executionTimeoutMs;
        set
        {
            var clamped = Math.Clamp(value, 500, 60_000);
            if (SetProperty(ref _executionTimeoutMs, clamped) && _config.ExecutionTimeoutMs != (int)clamped)
            {
                _config.ExecutionTimeoutMs = (int)clamped;
                SaveConfigSafe();
            }
        }
    }

    public double ExecutionMemoryLimitMb
    {
        get => _executionMemoryLimitMb;
        set
        {
            var clamped = Math.Clamp(value, 32, 4096);
            if (SetProperty(ref _executionMemoryLimitMb, clamped) && _config.ExecutionMemoryLimitMb != (int)clamped)
            {
                _config.ExecutionMemoryLimitMb = (int)clamped;
                SaveConfigSafe();
            }
        }
    }

    public int SelectedConflictPolicyIndex
    {
        get => _selectedConflictPolicyIndex;
        set
        {
            if (value < 0 || value > 2 || !SetProperty(ref _selectedConflictPolicyIndex, value))
                return;
            var policy = (ConflictResolutionPolicy)value;
            if (_config.ConflictPolicy != policy)
            {
                _config.ConflictPolicy = policy;
                SaveConfigSafe();
            }
        }
    }

    public ArchiveVersionRow? CompareWithVersion
    {
        get => _compareWithVersion;
        set
        {
            if (SetProperty(ref _compareWithVersion, value) && IsDiffModeActive)
                _ = RecomputeDiffAsync();
        }
    }

    public bool IsDiffModeActive
    {
        get => _isDiffModeActive;
        set
        {
            if (SetProperty(ref _isDiffModeActive, value))
            {
                OnPropertyChanged(nameof(DiffModeVisibility));
                OnPropertyChanged(nameof(SourceViewerVisibility));
                if (value) _ = RecomputeDiffAsync();
            }
        }
    }

    public Visibility DiffModeVisibility => IsDiffModeActive ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SourceViewerVisibility => IsDiffModeActive ? Visibility.Collapsed : Visibility.Visible;

    public bool DiffUseUnifiedMode
    {
        get => _diffUseUnifiedMode;
        set
        {
            if (SetProperty(ref _diffUseUnifiedMode, value))
            {
                OnPropertyChanged(nameof(DiffSideBySideVisibility));
                OnPropertyChanged(nameof(DiffUnifiedVisibility));
            }
        }
    }

    public Visibility DiffSideBySideVisibility => !DiffUseUnifiedMode ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DiffUnifiedVisibility => DiffUseUnifiedMode ? Visibility.Visible : Visibility.Collapsed;
    public string DiffSummaryText { get => _diffSummaryText; private set => SetProperty(ref _diffSummaryText, value); }
    public string DiffUnifiedText { get => _diffUnifiedText; private set => SetProperty(ref _diffUnifiedText, value); }

    public bool IsQuickActionsOpen
    {
        get => _isQuickActionsOpen;
        set
        {
            if (SetProperty(ref _isQuickActionsOpen, value))
            {
                OnPropertyChanged(nameof(QuickActionsVisibility));
                if (value) _ = RefreshQuickActionsDataAsync();
            }
        }
    }

    public Visibility QuickActionsVisibility => IsQuickActionsOpen ? Visibility.Visible : Visibility.Collapsed;

    public bool IsBrowserSetupOpen
    {
        get => _isBrowserSetupOpen;
        set
        {
            if (SetProperty(ref _isBrowserSetupOpen, value))
                OnPropertyChanged(nameof(BrowserSetupOverlayVisibility));
        }
    }

    public Visibility BrowserSetupOverlayVisibility => IsBrowserSetupOpen ? Visibility.Visible : Visibility.Collapsed;
    public string BrowserSetupBrowserName { get => _browserSetupBrowserName; private set { if (SetProperty(ref _browserSetupBrowserName, value)) OnPropertyChanged(nameof(BrowserSetupTitle)); } }
    public string BrowserSetupBrowserKindKey { get => _browserSetupBrowserKindKey; private set => SetProperty(ref _browserSetupBrowserKindKey, value); }
    public string BrowserSetupExtensionPath { get => _browserSetupExtensionPath; private set => SetProperty(ref _browserSetupExtensionPath, value); }
    public string BrowserSetupPageUrl { get => _browserSetupPageUrl; private set => SetProperty(ref _browserSetupPageUrl, value); }
    public string BrowserSetupStatusText { get => _browserSetupStatusText; private set => SetProperty(ref _browserSetupStatusText, value); }

    public string BrowserSetupTitle => _config.Language == UiLanguage.Vi
        ? $"Cài đặt & Kết nối tiện ích GhostCode cho {BrowserSetupBrowserName}"
        : $"Install & Connect GhostCode Extension for {BrowserSetupBrowserName}";

    public string BrowserSetupStep1 => _config.Language == UiLanguage.Vi
        ? $"Bước 1: Mở trang quản lý tiện ích ({BrowserSetupPageUrl}) trên {BrowserSetupBrowserName}."
        : $"Step 1: Open the extensions page ({BrowserSetupPageUrl}) in {BrowserSetupBrowserName}.";

    public string BrowserSetupStep2 => _config.Language == UiLanguage.Vi
        ? (BrowserSetupBrowserKindKey == "Firefox"
            ? "Bước 2: Chọn 'This Firefox' -> 'Load Temporary Add-on...'."
            : "Bước 2: Bật công tắc 'Chế độ nhà phát triển' (Developer mode) ở góc trên.")
        : (BrowserSetupBrowserKindKey == "Firefox"
            ? "Step 2: Click 'This Firefox' -> 'Load Temporary Add-on...'."
            : "Step 2: Enable 'Developer mode' in the top corner.");

    public string BrowserSetupStep3 => _config.Language == UiLanguage.Vi
        ? (BrowserSetupBrowserKindKey == "Firefox"
            ? "Bước 3: Chọn tệp manifest.json trong thư mục tiện ích bên dưới (đường dẫn đã tự sao chép)."
            : "Bước 3: Nhấn 'Tải tiện ích đã giải nén' (Load unpacked) và chọn thư mục bên dưới (đường dẫn đã sao chép sẵn).")
        : (BrowserSetupBrowserKindKey == "Firefox"
            ? "Step 3: Select manifest.json inside the staged extension folder below (path already copied)."
            : "Step 3: Click 'Load unpacked' and select the folder below (path already copied to clipboard).");

    public string BrowserSetupStep4 => _config.Language == UiLanguage.Vi
        ? "Bước 4: Quay lại GhostCode — cửa sổ này sẽ tự đóng khi tiện ích kết nối thành công."
        : "Step 4: Return to GhostCode — this guide closes automatically once the extension connects.";

    public ArchiveFileGroupRow? SelectedQuickActionTarget
    {
        get => _selectedQuickActionTarget;
        set
        {
            if (SetProperty(ref _selectedQuickActionTarget, value))
                _ = RefreshQuickActionsPreviewAsync();
        }
    }

    public string QuickActionsLatestPreview { get => _quickActionsLatestPreview; private set => SetProperty(ref _quickActionsLatestPreview, value); }
    public string QuickActionsAmbiguityNote { get => _quickActionsAmbiguityNote; private set => SetProperty(ref _quickActionsAmbiguityNote, value); }

    public string BackupHealthSummary { get => _backupHealthSummary; private set => SetProperty(ref _backupHealthSummary, value); }
    public string BackupProtectionBadge { get => _backupProtectionBadge; private set => SetProperty(ref _backupProtectionBadge, value); }
    public string RecoverySummaryText { get => _recoverySummaryText; private set => SetProperty(ref _recoverySummaryText, value); }
    public string GlobalHotkeyStatusText { get => _globalHotkeyStatusText; set => SetProperty(ref _globalHotkeyStatusText, value); }

    public bool SyncPausedManually
    {
        get => _syncPausedManually;
        set
        {
            if (SetProperty(ref _syncPausedManually, value))
            {
                _config.SyncPausedManually = value;
                SaveConfigSafe();
                OnPropertyChanged(nameof(SyncPauseButtonText));
                _ = RefreshHealthAsync();
            }
        }
    }

    public string SyncPauseButtonText => SyncPausedManually ? ExpStrings.SyncResumeButton : ExpStrings.SyncPauseButton;

    public bool InfoBarOpen { get => _infoBarOpen; set => SetProperty(ref _infoBarOpen, value); }
    public string InfoBarTitle { get => _infoBarTitle; private set => SetProperty(ref _infoBarTitle, value); }
    public string InfoBarMessage { get => _infoBarMessage; private set => SetProperty(ref _infoBarMessage, value); }
    public InfoBarSeverity InfoBarSeverity { get => _infoBarSeverity; private set => SetProperty(ref _infoBarSeverity, value); }
    public string InfoBarTechnicalDetails { get => _infoBarTechnicalDetails; private set { if (SetProperty(ref _infoBarTechnicalDetails, value)) OnPropertyChanged(nameof(InfoBarHasDetailsVisibility)); } }
    public Visibility InfoBarHasDetailsVisibility => string.IsNullOrWhiteSpace(InfoBarTechnicalDetails) ? Visibility.Collapsed : Visibility.Visible;
    public string InfoBarActionLabel { get => _infoBarActionLabel; private set { if (SetProperty(ref _infoBarActionLabel, value)) OnPropertyChanged(nameof(InfoBarActionVisibility)); } }
    public Visibility InfoBarActionVisibility => string.IsNullOrWhiteSpace(InfoBarActionLabel) ? Visibility.Collapsed : Visibility.Visible;
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }

    public GhostCodeConfig Config => _config;

    public void AttachUiHooks(
        Func<Action, bool> dispatcher,
        Action<ElementTheme> applyTheme,
        Action<string> clipboardCopy) =>
        AttachUiHooks(dispatcher, applyTheme, text => { clipboardCopy(text); return true; });

    public void AttachUiHooks(
        Func<Action, bool> dispatcher,
        Action<ElementTheme> applyTheme,
        Func<string, bool> clipboardCopyWithRetry)
    {
        _dispatch = dispatcher;
        _applyThemeCallback = applyTheme;
        _clipboardCopyWithRetry = clipboardCopyWithRetry;
        _applyThemeCallback(ToElementTheme(_config.Theme));
    }

    public void AttachExpansionHooks(
        Action<MicaAppearanceKind> applyMica,
        Func<bool, string, (bool Success, string Detail)> configureHotkey)
    {
        _applyMicaCallback = applyMica;
        _configureHotkeyCallback = configureHotkey;
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
        await RefreshHealthAsync();
    }

    public async Task RefreshHealthAsync()
    {
        var snap = await _healthService.EvaluateHealthAsync(attemptWatchRecovery: false);
        BackupProtectionBadge = UiStrings.ForProtectionBadge(snap.ProtectionState, _config.Language);
        BackupHealthSummary = snap.StatusSummary;
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
            ShowInfoBar("Recovery Check Complete", rep.Summary,
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
        ShowInfoBar(
            SyncPausedManually ? "Sync Paused" : "Sync Resumed",
            SyncPausedManually ? "Local archival remains active; cloud uploads are paused." : "Background cloud synchronization resumed.",
            InfoBarSeverity.Informational);
    }

    public void RefreshBrowserAndIdeDiscovery()
    {
        var browserStates = _bridge.GetBrowserConnectionStates();
        BrowserStatuses.Clear();
        InstalledBrowserRows.Clear();
        ConnectedBrowsers.Clear();
        foreach (var b in browserStates)
        {
            var row = new BrowserStatusRow(b, _config.Language);
            BrowserStatuses.Add(row);
            if (b.IsInstalled)
                InstalledBrowserRows.Add(row);
            if (row.IsConnected)
                ConnectedBrowsers.Add(row);
        }

        OnPropertyChanged(nameof(HasConnectedBrowser));
        OnPropertyChanged(nameof(NoConnectedBrowsersVisibility));
        OnPropertyChanged(nameof(NoInstalledBrowsersVisibility));
        OnPropertyChanged(nameof(TitleBarBrowserBadgeText));

        var connectedNames = ConnectedBrowsers.Select(x => x.Name).ToList();
        var runningNames = browserStates.Where(b => b.IsRunning).Select(b => b.DisplayName).ToList();
        BrowserStatusSummary = connectedNames.Count > 0
            ? $"{string.Join(", ", connectedNames)} • {Strings.BrowserConnectedBadge}"
            : runningNames.Count > 0
                ? (_config.Language == UiLanguage.Vi
                    ? $"{string.Join(", ", runningNames)} (Chưa kết nối tiện ích)"
                    : $"{string.Join(", ", runningNames)} (Extension Not Connected)")
                : Strings.NoConnectedBrowsers;

        var (ides, toolchains) = _toolchains.DiscoverAll(_config);
        IdeStatuses.Clear();
        foreach (var i in ides)
            IdeStatuses.Add(new IdeStatusRow(i, _config.Language));

        Toolchains.Clear();
        foreach (var t in toolchains)
            Toolchains.Add(new ToolchainRow(t, _config.Language));

        var runningIdes = ides.Where(i => i.IsRunning).Select(i => i.Name).ToList();
        var installedIdes = ides.Where(i => i.IsInstalled).Select(i => i.Name).ToList();
        bool vi = _config.Language == UiLanguage.Vi;
        IdeStatusSummary = runningIdes.Count > 0
            ? (vi ? $"Đang chạy: {string.Join(", ", runningIdes)}" : $"Running: {string.Join(", ", runningIdes)}")
            : installedIdes.Count > 0
                ? (vi ? $"Đã cài đặt: {string.Join(", ", installedIdes)}" : $"Installed: {string.Join(", ", installedIdes)}")
                : (vi ? "Chưa phát hiện IDE" : "No IDE detected");
    }

    public void RefreshProcessesAndWatches()
    {
        RefreshBrowserAndIdeDiscovery();

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

        var events = await _store.GetRecentMonitorEventsAsync(40);
        RecentEvents.Clear();
        foreach (var ev in events)
            RecentEvents.Add(new MonitorEventRow(ev));

        var history = await _store.GetArchiveHistoryEventsAsync(80);
        HistoryEvents.Clear();
        foreach (var h in history)
            HistoryEvents.Add(new ArchiveHistoryEventRow(h));

        var notifs = await _store.GetRecentNotificationsAsync(30);
        NotificationHistory.Clear();
        foreach (var nr in notifs)
            NotificationHistory.Add(nr);
    }

    public async Task RefreshArchiveAsync()
    {
        List<ArchiveVersionRecord> list;
        if (string.IsNullOrWhiteSpace(SearchQuery))
        {
            list = await _store.QueryVersionsAsync(null, SelectedPlatformFilter, 500);
        }
        else
        {
            var found = await _store.SearchArchiveAsync(SearchQuery, 500);
            list = found.Select(f => f.Version)
                .Where(v => SelectedPlatformFilter == "All" || string.Equals(v.Platform, SelectedPlatformFilter, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        VersionAvailability? availFilter = SelectedAvailabilityFilterIndex switch
        {
            1 => VersionAvailability.LocalOnly,
            2 => VersionAvailability.Synced,
            3 => VersionAvailability.CloudOnly,
            4 => VersionAvailability.Conflict,
            _ => null
        };
        if (availFilter is VersionAvailability f)
            list = list.Where(v => v.Availability == f).ToList();

        var selectedId = SelectedVersion?.Id;
        var selectedPath = SelectedFileGroup?.SourcePath;

        ArchiveVersions.Clear();
        foreach (var v in list)
            ArchiveVersions.Add(new ArchiveVersionRow(v, _config.Language));

        ArchiveFileGroups.Clear();
        foreach (var grp in list.GroupBy(v => v.SourcePath, StringComparer.OrdinalIgnoreCase))
        {
            var latest = grp.OrderByDescending(x => x.VersionNum).ThenByDescending(x => x.CreatedAtUnixMs).First();
            ArchiveFileGroups.Add(new ArchiveFileGroupRow(latest, grp.Count(), _config.Language));
        }

        if (!string.IsNullOrEmpty(selectedId))
        {
            var foundVer = ArchiveVersions.FirstOrDefault(x => x.Id == selectedId);
            if (foundVer is not null)
            {
                await LoadVersionsForFileGroupAsync(foundVer.SourcePath, foundVer.Id);
                return;
            }
        }

        if (!string.IsNullOrEmpty(selectedPath))
        {
            var matchGrp = ArchiveFileGroups.FirstOrDefault(g => string.Equals(g.SourcePath, selectedPath, StringComparison.OrdinalIgnoreCase));
            if (matchGrp is not null)
                SelectedFileGroup = matchGrp;
        }
    }

    public async Task<IReadOnlyList<GlobalSearchSuggestion>> UpdateGlobalSearchSuggestionsAsync(string query, CancellationToken ct = default)
    {
        var results = await _store.SearchArchiveAsync(query, 20, ct);
        var mapped = results.Select(r => new GlobalSearchSuggestion(r.Version, r.MatchKind, _config.Language)).ToList();
        GlobalSearchSuggestions.Clear();
        foreach (var item in mapped)
            GlobalSearchSuggestions.Add(item);
        return mapped;
    }

    public async Task SelectArchiveVersionByIdAsync(string versionId)
    {
        var target = await _store.GetVersionByIdAsync(versionId);
        if (target is null)
            return;

        SelectedPlatformFilter = "All";
        SelectedAvailabilityFilterIndex = 0;
        SearchQuery = string.Empty;
        await RefreshArchiveAsync();
        await LoadVersionsForFileGroupAsync(target.SourcePath, target.Id);
    }

    private async Task LoadVersionsForFileGroupAsync(string sourcePath, string? preferredVersionId)
    {
        var groupMatch = ArchiveFileGroups.FirstOrDefault(g => string.Equals(g.SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase));
        if (groupMatch is not null && _selectedFileGroup?.SourcePath != groupMatch.SourcePath)
        {
            _selectedFileGroup = groupMatch;
            OnPropertyChanged(nameof(SelectedFileGroup));
        }

        var versions = await _store.GetVersionsBySourceAsync(sourcePath);
        SelectedFileVersions.Clear();
        foreach (var v in versions.OrderByDescending(x => x.VersionNum))
            SelectedFileVersions.Add(new ArchiveVersionRow(v, _config.Language));

        var pick = preferredVersionId is not null
            ? SelectedFileVersions.FirstOrDefault(v => v.Id == preferredVersionId)
            : SelectedFileVersions.FirstOrDefault();

        if (pick is not null)
            SelectedVersion = pick;
    }

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

    public async Task RunSelectedVersionAsync()
    {
        if (SelectedVersion is null || IsExecuting)
            return;

        if (!SelectedVersion.IsLocalAvailable)
        {
            ShowInfoBar("Cloud-Only Version", Strings.CloudOnlyNotice, InfoBarSeverity.Warning);
            return;
        }

        _execCts?.Dispose();
        _execCts = new CancellationTokenSource();
        IsExecuting = true;
        StdoutText = string.Empty;
        StderrText = string.Empty;
        ConsoleLogText = $"[{DateTime.Now:HH:mm:ss}] Compiling & running {SelectedVersion.FileName} ({SelectedVersion.VersionBadge})...\n";
        RunnerStatusText = Strings.StatusCompiling;
        RunnerMetricsText = string.Empty;

        try
        {
            var res = await _runner.ExecuteArchivedVersionAsync(
                new CodeExecutionRequest(SelectedVersion.Id, StdinText, _config.ExecutionTimeoutMs, _config.ExecutionMemoryLimitMb),
                chunk => _dispatch(() => ConsoleLogText += chunk),
                _execCts.Token);

            StdoutText = res.Stdout;
            StderrText = string.IsNullOrWhiteSpace(res.CompilerOutput)
                ? res.Stderr
                : string.IsNullOrWhiteSpace(res.Stderr)
                    ? res.CompilerOutput
                    : $"{res.CompilerOutput}\n{res.Stderr}";
            if (!string.IsNullOrWhiteSpace(res.CompilerOutput))
                ConsoleLogText += $"\n[COMPILER]\n{res.CompilerOutput}\n";
            if (!string.IsNullOrWhiteSpace(res.Stderr))
                ConsoleLogText += $"\n[STDERR]\n{res.Stderr}";
            ConsoleLogText += $"\n--- {res.StatusBadgeText} • Exit {res.ExitCode?.ToString() ?? "-"} • {res.ElapsedMs} ms • PeakMem: {(res.PeakMemoryMb ?? 0):F2} MB ---\n";

            RunnerMetricsText = $"Exit {res.ExitCode?.ToString() ?? "-"} • {res.ElapsedMs} ms • {(res.PeakMemoryMb ?? 0):F2} MB";
            RunnerStatusText = res.Status == ExecutionStatus.Finished && res.ExitCode == 0 ? Strings.StatusPassed : res.StatusBadgeText;
        }
        catch (OperationCanceledException)
        {
            RunnerStatusText = Strings.StatusFailed;
            RunnerMetricsText = "Stopped by user";
            ConsoleLogText += "\n[STOPPED BY USER]\n";
        }
        finally
        {
            IsExecuting = false;
        }
    }

    public void StopExecution()
    {
        try { _execCts?.Cancel(); } catch { }
    }

    public void ClearConsole()
    {
        StdoutText = string.Empty;
        StderrText = string.Empty;
        ConsoleLogText = Strings.ConsoleOutputEmpty;
        RunnerMetricsText = string.Empty;
        RunnerStatusText = Strings.StatusReady;
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
            await RefreshStatsAndEventsAsync();
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
                await LoadVersionsForFileGroupAsync(SelectedVersion?.SourcePath ?? string.Empty, targetId);
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
        var provider = ResolveActiveCloudProvider();
        if (provider is null)
        {
            ShowInfoBar("Local-Only Mode", "Cloud pull skipped because Local-Only mode is selected.", InfoBarSeverity.Informational);
            return;
        }

        IsSyncing = true;
        SyncProgressPercent = 0;
        SyncProgressText = "Pulling cloud index...";
        try
        {
            var progress = new Progress<DriveSyncProgress>(p =>
            {
                SyncProgressPercent = p.PercentComplete;
                SyncProgressText = p.Total > 0 ? $"Pull {p.Completed}/{p.Total}: {p.CurrentFileName}" : "Idle";
            });
            var pull = await provider.PullAndReconcileAsync(progress);
            SaveConfigSafe();
            await RefreshAllAsync();

            if (!pull.Connected)
                ShowFriendlyError(ErrorCategory.AuthenticationExpired, pull.Error, InfoBarSeverity.Warning);
            else if (!string.IsNullOrEmpty(pull.Error))
                ShowFriendlyError(ErrorCategory.NetworkOffline, pull.Error, InfoBarSeverity.Error);
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
        var provider = ResolveActiveCloudProvider();
        if (provider is null)
        {
            ShowInfoBar("Local-Only Mode", "Cloud download skipped because Local-Only mode is selected.", InfoBarSeverity.Informational);
            return;
        }

        IsSyncing = true;
        try
        {
            var progress = new Progress<DriveSyncProgress>(p =>
            {
                SyncProgressPercent = p.PercentComplete;
                SyncProgressText = p.Total > 0 ? $"Download {p.Completed}/{p.Total}: {p.CurrentFileName}" : "Idle";
            });
            var (ok, failed, err) = await CloudTransferHelpers.DownloadAllCloudOnlyVersionsAsync(provider, _store, progress);
            await RefreshAllAsync();
            ShowInfoBar(
                failed > 0 ? "Download Partial" : "All Cloud Files Downloaded",
                $"Downloaded & verified: {ok}, Failed: {failed}. {err}".Trim(),
                failed > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
        }
        finally
        {
            IsSyncing = false;
        }
    }

    public async Task PrepareBrowserIntegrationAsync(string? kindArg)
    {
        await OpenBrowserSetupAsync(kindArg);
    }

    public async Task OpenBrowserSetupAsync(string? kindArg, bool launchBrowserNow = true)
    {
        var kind = Enum.TryParse<BrowserKind>(kindArg, ignoreCase: true, out var parsed)
            ? parsed
            : BrowserKind.Edge;

        BrowserIntegrationResult result = await Task.Run(() =>
            _browserIntegration.LaunchAssistedInstall(kind, _config, _bridge.ListeningPort, launchExternalProcesses: launchBrowserNow));

        var info = _bridge.GetBrowserConnectionStates().FirstOrDefault(b => b.Kind == kind);
        BrowserSetupBrowserName = info?.DisplayName ?? kind.ToString();
        BrowserSetupBrowserKindKey = kind.ToString();
        BrowserSetupExtensionPath = result.ExtensionDirectory;
        BrowserSetupPageUrl = result.ExtensionsPageUrl;
        BrowserSetupStatusText = UiStrings.ForBrowserState(BrowserConnectionState.GuidedInstalling, _config.Language);
        OnPropertyChanged(nameof(BrowserSetupStep1));
        OnPropertyChanged(nameof(BrowserSetupStep2));
        OnPropertyChanged(nameof(BrowserSetupStep3));
        OnPropertyChanged(nameof(BrowserSetupStep4));
        _clipboardCopyWithRetry?.Invoke(result.ExtensionDirectory);

        RefreshBrowserAndIdeDiscovery();
        IsBrowserSetupOpen = true;
    }

    private void CopyStagedExtensionPath()
    {
        if (!string.IsNullOrWhiteSpace(BrowserSetupExtensionPath))
        {
            TryCopyRawTextToClipboard(
                BrowserSetupExtensionPath,
                _config.Language == UiLanguage.Vi
                    ? $"Đã sao chép đường dẫn thư mục tiện ích: {BrowserSetupExtensionPath}"
                    : $"Copied extension directory: {BrowserSetupExtensionPath}");
        }
    }

    private void OpenTargetBrowserExtensionsPage()
    {
        if (!Enum.TryParse<BrowserKind>(BrowserSetupBrowserKindKey, ignoreCase: true, out var kind))
            return;
        var result = _browserIntegration.LaunchAssistedInstall(kind, _config, _bridge.ListeningPort, launchExternalProcesses: true);
        BrowserSetupExtensionPath = result.ExtensionDirectory;
        BrowserSetupPageUrl = result.ExtensionsPageUrl;
    }

    public IReadOnlyList<BrowserStatusRow> GetUnconnectedUnpromptedBrowsers()
    {
        return InstalledBrowserRows
            .Where(b => !b.IsConnected
                && !_config.DismissedBrowserPrompts.Contains(b.KindKey, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    public void DismissBrowserPrompts(IEnumerable<string> kindKeys)
    {
        bool changed = false;
        foreach (var k in kindKeys)
        {
            if (!_config.DismissedBrowserPrompts.Contains(k, StringComparer.OrdinalIgnoreCase))
            {
                _config.DismissedBrowserPrompts.Add(k);
                changed = true;
            }
        }
        if (changed)
            SaveConfigSafe();
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

        CancelOAuth();
        _oauthCts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        IsBusy = true;
        IsOAuthInProgress = true;
        try
        {
            var tokens = await _drive.ConnectWithBrowserAsync(ct: _oauthCts.Token);
            IsOAuthInProgress = false;
            OAuthStageText = string.Empty;
            await RefreshCloudStateAsync();
            ShowInfoBar(
                _config.Language == UiLanguage.Vi ? "Đã kết nối Google Drive" : "Google Drive Connected",
                _config.Language == UiLanguage.Vi ? $"Đã đăng nhập: {tokens.Email}" : $"Signed in as {tokens.Email}.",
                InfoBarSeverity.Success);
        }
        catch (OperationCanceledException)
        {
            ShowInfoBar(
                _config.Language == UiLanguage.Vi ? "Đã hủy đăng nhập Google" : "Google Sign-In Cancelled",
                _config.Language == UiLanguage.Vi ? "Quá trình xác thực đã được hủy hoặc hết thời gian chờ." : "Authorization was cancelled or timed out.",
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
            var res = await _drive.DisconnectAsync();
            SaveConfigSafe();
            await RefreshCloudStateAsync();
            if (res.RemoteRevocationSucceeded)
                ShowInfoBar("Disconnected", "Google OAuth tokens revoked remotely and wiped from DPAPI store.", InfoBarSeverity.Informational);
            else
                ShowInfoBar("Disconnected Locally", $"DPAPI tokens cleared, but remote revocation failed: {res.RemoteError}", InfoBarSeverity.Warning);
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

        CancelOAuth();
        _oauthCts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        IsBusy = true;
        IsOAuthInProgress = true;
        try
        {
            var tokens = await _oneDrive.ConnectWithBrowserAsync(ct: _oauthCts.Token);
            IsOAuthInProgress = false;
            OAuthStageText = string.Empty;
            await RefreshCloudStateAsync();
            ShowInfoBar(
                _config.Language == UiLanguage.Vi ? "Đã kết nối OneDrive" : "OneDrive Connected",
                _config.Language == UiLanguage.Vi ? $"Đã đăng nhập: {tokens.Email}" : $"Signed in as {tokens.Email}.",
                InfoBarSeverity.Success);
        }
        catch (OperationCanceledException)
        {
            ShowInfoBar(
                _config.Language == UiLanguage.Vi ? "Đã hủy đăng nhập OneDrive" : "OneDrive Sign-In Cancelled",
                _config.Language == UiLanguage.Vi ? "Quá trình xác thực đã được hủy hoặc hết thời gian chờ." : "Authorization was cancelled or timed out.",
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
        var provider = ResolveActiveCloudProvider();
        if (provider is null)
        {
            ShowInfoBar("Local-Only Mode", "Cloud sync skipped (Local-Only mode keeps all backups strictly on this device).", InfoBarSeverity.Informational);
            return;
        }

        IsSyncing = true;
        SyncProgressPercent = 0;
        SyncProgressText = $"Starting {provider.DisplayName} bidirectional sync...";
        try
        {
            var progress = new Progress<DriveSyncProgress>(p =>
            {
                SyncProgressPercent = p.PercentComplete;
                SyncProgressText = p.Total > 0 ? $"{p.Completed}/{p.Total}: {p.CurrentFileName}" : "Idle";
            });
            var (report, pull) = await provider.SyncBidirectionalAsync(progress);
            SaveConfigSafe();
            await RefreshAllAsync();

            if (!report.Connected)
                ShowFriendlyError(ErrorCategory.AuthenticationExpired, report.LastError, InfoBarSeverity.Warning);
            else if (report.FailedCount > 0 || !string.IsNullOrEmpty(pull.Error))
                ShowFriendlyError(ErrorCategory.NetworkOffline, report.LastError ?? pull.Error, InfoBarSeverity.Warning);
            else
                ShowInfoBar(
                    _config.Language == UiLanguage.Vi ? "Đồng bộ thành công" : "Bidirectional Sync Complete",
                    _config.Language == UiLanguage.Vi
                        ? $"{provider.DisplayName}: Đã tải lên {report.UploadedCount} (Bỏ qua trùng lặp: {report.SkippedDuplicateCount}) • Đám mây mới: {pull.NewCloudOnlyCount}"
                        : $"{provider.DisplayName}: Uploaded {report.UploadedCount} (Deduped: {report.SkippedDuplicateCount}) • Cloud Discovered: {pull.NewCloudOnlyCount} • Conflicts: {pull.ConflictBranchCount}",
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
        _onboardingDismissedForSession = false;
        SyncWatchedFoldersToConfigAndReload();
        OnPropertyChanged(nameof(NoFoldersVisible));
        OnPropertyChanged(nameof(OnboardingCardVisibility));
        _ = Task.Run(async () => { try { await _monitor.ReconcileOnceAsync(); } catch { } });
        _ = RefreshHealthAsync();
        ShowInfoBar(
            _config.Language == UiLanguage.Vi ? "Đã thêm thư mục theo dõi" : "Folder Added",
            _config.Language == UiLanguage.Vi ? $"Đang theo dõi: {folderPath}" : $"Now monitoring: {folderPath}",
            InfoBarSeverity.Success);
    }

    public IReadOnlyList<WatchedFolderConfig> DiscoverCandidateFolders()
    {
        return KnownFoldersResolver.DiscoverDefaultWatchedFolders()
            .Where(d => !WatchedFolders.Any(w => string.Equals(w.Path, d.Path, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    public async Task AddWatchedFoldersAndReconcileAsync(IEnumerable<WatchedFolderConfig> folders)
    {
        int added = 0;
        _suppressFolderSync = true;
        foreach (var f in folders)
        {
            if (string.IsNullOrWhiteSpace(f.Path) || KnownFoldersResolver.IsDriveRoot(f.Path) || _monitor.IsExcludedOrOutputDirectory(f.Path))
                continue;
            if (WatchedFolders.Any(w => string.Equals(w.Path, f.Path, StringComparison.OrdinalIgnoreCase)))
                continue;
            WatchedFolders.Add(new WatchedFolderItem(f, SyncWatchedFoldersToConfigAndReload));
            added++;
        }
        _suppressFolderSync = false;

        if (added > 0)
        {
            _onboardingDismissedForSession = false;
            SyncWatchedFoldersToConfigAndReload();
            OnPropertyChanged(nameof(NoFoldersVisible));
            OnPropertyChanged(nameof(OnboardingCardVisibility));
            await _monitor.ReconcileOnceAsync();
            await RefreshAllAsync();
        }
    }

    private void DismissOnboarding()
    {
        _onboardingDismissedForSession = true;
        OnPropertyChanged(nameof(OnboardingCardVisibility));
    }

    public void RemoveWatchedFolder(WatchedFolderItem item)
    {
        _suppressFolderSync = true;
        WatchedFolders.Remove(item);
        _suppressFolderSync = false;
        SyncWatchedFoldersToConfigAndReload();
        OnPropertyChanged(nameof(NoFoldersVisible));
        OnPropertyChanged(nameof(OnboardingCardVisibility));
        _ = RefreshHealthAsync();
        ShowInfoBar(
            _config.Language == UiLanguage.Vi ? "Đã gỡ thư mục" : "Folder Removed",
            item.Path,
            InfoBarSeverity.Informational);
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

    public void ShowInfoBar(string title, string message, InfoBarSeverity severity, string technicalDetails = "", string actionLabel = "", string actionKey = "")
    {
        _dispatch(() =>
        {
            InfoBarTitle = title;
            InfoBarMessage = message;
            InfoBarSeverity = severity;
            InfoBarTechnicalDetails = UserFriendlyErrorService.RedactSecrets(technicalDetails);
            InfoBarActionLabel = actionLabel;
            _infoBarActionKey = actionKey;
            InfoBarOpen = true;
        });
    }

    public void ShowFriendlyError(ErrorCategory category, string? rawTechnicalDetails = null, InfoBarSeverity severity = InfoBarSeverity.Error)
    {
        var info = UserFriendlyErrorService.MapError(category, rawTechnicalDetails, _config.Language);
        ShowInfoBar(info.Title, info.Explanation, severity, info.RedactedTechnicalDetails, info.ActionLabel, info.ActionKey);
    }

    public void ShowFriendlyException(Exception ex, ErrorCategory fallback = ErrorCategory.RuntimeError)
    {
        var info = UserFriendlyErrorService.ClassifyAndMap(ex, _config.Language, fallback);
        ShowInfoBar(info.Title, info.Explanation, InfoBarSeverity.Error, info.RedactedTechnicalDetails, info.ActionLabel, info.ActionKey);
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
        }
    }

    private void LoadFieldsFromConfig()
    {
        _oAuthClientId = _config.OAuthClient.ClientId;
        _oAuthClientSecret = _config.OAuthClient.ClientSecret;
        _oneDriveClientId = _config.OneDriveOAuthClient.ClientId;
        _oneDriveTenantId = string.IsNullOrWhiteSpace(_config.OneDriveOAuthClient.TenantId) ? "consumers" : _config.OneDriveOAuthClient.TenantId;
        _selectedCloudProviderIndex = (int)_config.PrimaryCloudProvider;
        _archiveDir = _config.ArchiveDir;
        _readyDir = _config.ReadyDir;
        _selectedLanguageIndex = _config.Language == UiLanguage.Vi ? 0 : 1;
        _selectedThemeIndex = (int)_config.Theme;
        _minimizeToTrayOnClose = _config.MinimizeToTrayOnClose;
        _autoCopyReadyPath = _config.AutoCopyReadyPath;
        _driveEnabled = _config.DriveEnabled;
        _cCompilerPath = _config.CCompilerPath ?? string.Empty;
        _cppCompilerPath = _config.CppCompilerPath ?? string.Empty;
        _pythonInterpreterPath = _config.PythonInterpreterPath ?? string.Empty;
        _selectedCStandard = _config.CStandard;
        _selectedCppStandard = _config.CppStandard;
        _executionTimeoutMs = _config.ExecutionTimeoutMs;
        _executionMemoryLimitMb = _config.ExecutionMemoryLimitMb;
        _selectedConflictPolicyIndex = (int)_config.ConflictPolicy;
        _syncPausedManually = _config.SyncPausedManually;
        _selectedMicaKindIndex = (int)_config.MicaKind;
        _selectedNotificationLevelIndex = (int)_config.NotificationLevel;
        _globalHotkeyEnabled = _config.GlobalHotkeyEnabled;
        _globalHotkeyGesture = _config.GlobalHotkeyGesture;
        _captureProblemStatements = _config.CaptureProblemStatements;
        _trackSubmissionVerdicts = _config.TrackSubmissionVerdicts;
        _trimTrailingWhitespaceOnCompare = _config.TrimTrailingWhitespaceOnCompare;
        _estimateActiveCodingTime = _config.EstimateActiveCodingTime;
        _aiAssistanceOptIn = _config.AiAssistanceOptIn;
        _activeCodingTimeText = _codingTime.FormatEstimatedDuration(string.Empty);

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
        _ = RefreshHealthAsync();
    }

    private void SaveConfigSafe()
    {
        try { _config.Save(_configPath); }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to save config: {ex.Message}");
        }
    }

    private bool TryCopyRawTextToClipboard(string text, string successLabel)
    {
        if (_clipboardCopyWithRetry is null)
            return false;

        bool ok = _clipboardCopyWithRetry(text);
        if (ok)
            ShowInfoBar("Copied", successLabel, InfoBarSeverity.Success);
        else
            ShowInfoBar("Clipboard Busy", "Windows clipboard is temporarily locked by another application.", InfoBarSeverity.Warning);
        return ok;
    }

    private async Task CopySelectedCodeAsync()
    {
        if (SelectedVersion is null || !SelectedVersion.IsLocalAvailable)
        {
            ShowInfoBar("Cannot Copy", Strings.NoVersionSelected, InfoBarSeverity.Warning);
            return;
        }

        // Preserve exact verified historical text cleanly (no Markdown fences, no version change)
        var bytes = await _archiver.ReadAndVerifyVersionBytesAsync(SelectedVersion.Id);
        var (exactText, _) = SourceAnalyzer.DecodePreservingInfo(bytes);
        TryCopyRawTextToClipboard(exactText, $"Copied {SelectedVersion.VersionBadge} ({SelectedVersion.FileName}) exact source.");
    }

    private async Task CopyLatestProblemCodeAsync()
    {
        var target = await ResolveTargetVersionForQuickCopyAsync();
        if (target is null)
        {
            ShowInfoBar("No Target File", "Select a problem file explicitly before copying.", InfoBarSeverity.Warning);
            return;
        }

        var bytes = await _archiver.ReadAndVerifyVersionBytesAsync(target.Id);
        var (exactText, _) = SourceAnalyzer.DecodePreservingInfo(bytes);
        TryCopyRawTextToClipboard(exactText, $"Copied latest {target.FileName} (v{target.VersionNum}).");
    }

    private void CopyActiveSampleInput()
    {
        if (string.IsNullOrEmpty(_activeSampleInputText))
        {
            ShowInfoBar("No Sample Input", "No sample input detected or selected yet.", InfoBarSeverity.Informational);
            return;
        }
        TryCopyRawTextToClipboard(_activeSampleInputText, "Copied sample input.");
    }

    private void CopyActiveSampleOutput()
    {
        if (string.IsNullOrEmpty(_activeSampleOutputText))
        {
            ShowInfoBar("No Sample Output", "No expected sample output available yet.", InfoBarSeverity.Informational);
            return;
        }
        TryCopyRawTextToClipboard(_activeSampleOutputText, "Copied sample output.");
    }

    private void CopyActiveProblemUrl()
    {
        var url = !string.IsNullOrWhiteSpace(ActiveUrl) ? ActiveUrl : SelectedVersion?.Record.ProblemCode ?? string.Empty;
        if (string.IsNullOrWhiteSpace(url))
        {
            ShowInfoBar("No Problem URL", "No active problem URL available.", InfoBarSeverity.Informational);
            return;
        }
        TryCopyRawTextToClipboard(url, $"Copied URL: {url}");
    }

    private void CopySelectedSourcePath()
    {
        var path = SelectedVersion?.SourcePath ?? SelectedQuickActionTarget?.SourcePath ?? string.Empty;
        if (string.IsNullOrWhiteSpace(path))
            return;
        TryCopyRawTextToClipboard(path, $"Copied source path: {path}");
    }

    private async Task<ArchiveVersionRecord?> ResolveTargetVersionForQuickCopyAsync()
    {
        if (SelectedQuickActionTarget is not null && SelectedQuickActionTarget.Latest.IsLocalAvailable)
            return SelectedQuickActionTarget.Latest;

        if (SelectedFileVersions.Count > 0 && SelectedFileVersions[0].IsLocalAvailable)
            return SelectedFileVersions[0].Record;

        if (HasActiveContext)
        {
            var all = await _store.QueryVersionsAsync(ActiveCode, limit: 50);
            var matched = all.Where(v => v.IsLocalAvailable && string.Equals(v.ProblemCode, ActiveCode, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(v => v.CreatedAtUnixMs)
                .FirstOrDefault();
            if (matched is not null)
                return matched;
        }

        return null;
    }

    private async Task OpenSelectedVersionInIdeAsync()
    {
        if (SelectedVersion is null)
            return;
        var res = await _ideLauncher.OpenVersionInIdeAsync(SelectedVersion.Record, preferOriginalSource: false);
        ShowInfoBar(res.Success ? $"Opened in {res.IdeName}" : "IDE Launch Failed", res.Message,
            res.Success ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
    }

    private async Task OpenOriginalSourceInIdeAsync()
    {
        if (SelectedVersion is null)
            return;
        var res = await _ideLauncher.OpenVersionInIdeAsync(SelectedVersion.Record, preferOriginalSource: true);
        ShowInfoBar(res.Success ? $"Opened Original in {res.IdeName}" : "IDE Launch Failed", res.Message,
            res.Success ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
    }

    private async Task OpenQuickActionsTargetInIdeAsync()
    {
        var target = await ResolveTargetVersionForQuickCopyAsync();
        if (target is null)
        {
            ShowInfoBar("Select File", "Please choose a target file in the Quick Actions selector first.", InfoBarSeverity.Warning);
            return;
        }
        var res = await _ideLauncher.OpenVersionInIdeAsync(target, preferOriginalSource: File.Exists(target.SourcePath));
        ShowInfoBar(res.Success ? $"Opened in {res.IdeName}" : "IDE Launch Failed", res.Message,
            res.Success ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
    }

    private void ToggleDiffMode()
    {
        if (!IsDiffModeActive && SelectedFileVersions.Count >= 2 && CompareWithVersion is null)
            CompareWithVersion = SelectedFileVersions.FirstOrDefault(v => v.Id != SelectedVersion?.Id);
        IsDiffModeActive = !IsDiffModeActive;
    }

    private async Task RecomputeDiffAsync()
    {
        DiffSideBySideLines.Clear();
        if (SelectedVersion is null || CompareWithVersion is null)
        {
            DiffSummaryText = "Select two versions to compare.";
            DiffUnifiedText = string.Empty;
            return;
        }

        if (!SelectedVersion.IsLocalAvailable || !CompareWithVersion.IsLocalAvailable)
        {
            DiffSummaryText = "Download both Cloud-Only versions before comparing.";
            DiffUnifiedText = string.Empty;
            return;
        }

        var leftBytes = await _archiver.ReadAndVerifyVersionBytesAsync(CompareWithVersion.Id);
        var rightBytes = await _archiver.ReadAndVerifyVersionBytesAsync(SelectedVersion.Id);
        var (leftText, _) = SourceAnalyzer.DecodePreservingInfo(leftBytes);
        var (rightText, _) = SourceAnalyzer.DecodePreservingInfo(rightBytes);

        var diff = SourceDiffEngine.ComputeDiff(
            leftText,
            rightText,
            $"{CompareWithVersion.FileName} ({CompareWithVersion.VersionBadge})",
            $"{SelectedVersion.FileName} ({SelectedVersion.VersionBadge})");

        foreach (var line in diff.SideBySideLines)
            DiffSideBySideLines.Add(new DiffLineRow(line));

        DiffSummaryText = $"+{diff.AddedLines} added • -{diff.RemovedLines} removed • ~{diff.ModifiedLines} modified ({CompareWithVersion.VersionBadge} -> {SelectedVersion.VersionBadge})";
        DiffUnifiedText = diff.UnifiedDiffText;
    }

    public void OpenQuickActionsPanel() => IsQuickActionsOpen = true;
    private void ToggleQuickActionsPanel() => IsQuickActionsOpen = !IsQuickActionsOpen;

    private async Task RefreshQuickActionsDataAsync()
    {
        var list = await _store.QueryVersionsAsync(null, "All", 250);
        QuickActionsCandidates.Clear();
        var grouped = list.GroupBy(v => v.SourcePath, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ArchiveFileGroupRow(g.OrderByDescending(x => x.VersionNum).ThenByDescending(x => x.CreatedAtUnixMs).First(), g.Count(), _config.Language))
            .ToList();
        foreach (var g in grouped)
            QuickActionsCandidates.Add(g);

        ArchiveFileGroupRow? exactMatch = null;
        if (HasActiveContext)
        {
            exactMatch = grouped.FirstOrDefault(g => string.Equals(g.Latest.ProblemCode, ActiveCode, StringComparison.OrdinalIgnoreCase));
        }

        // Never silently pick an unrelated file when active problem cannot be matched
        if (exactMatch is not null)
        {
            SelectedQuickActionTarget = exactMatch;
            QuickActionsAmbiguityNote = _config.Language == UiLanguage.Vi
                ? $"Khớp bài tập đang mở: {ActiveCode}"
                : $"Matched active problem {ActiveCode}";
        }
        else if (SelectedFileGroup is not null)
        {
            SelectedQuickActionTarget = grouped.FirstOrDefault(g => string.Equals(g.SourcePath, SelectedFileGroup.SourcePath, StringComparison.OrdinalIgnoreCase));
            QuickActionsAmbiguityNote = string.Empty;
        }
        else
        {
            SelectedQuickActionTarget = null;
            QuickActionsAmbiguityNote = grouped.Count == 0
                ? (_config.Language == UiLanguage.Vi ? "Chưa có tệp nào được lưu." : "No archived files yet.")
                : UxStrings.QuickActionsSelectHint;
            QuickActionsLatestPreview = string.Empty;
        }
    }

    private async Task RefreshQuickActionsPreviewAsync()
    {
        if (SelectedQuickActionTarget is null || !SelectedQuickActionTarget.Latest.IsLocalAvailable)
        {
            QuickActionsLatestPreview = string.Empty;
            return;
        }
        try
        {
            var bytes = await _archiver.ReadAndVerifyVersionBytesAsync(SelectedQuickActionTarget.Latest.Id);
            var (text, _) = SourceAnalyzer.DecodePreservingInfo(bytes);
            var lines = text.Replace("\r\n", "\n").Split('\n');
            QuickActionsLatestPreview = lines.Length > 10 ? string.Join('\n', lines.Take(10)) + "\n..." : text;
        }
        catch (Exception ex)
        {
            QuickActionsLatestPreview = $"// {ex.Message}";
        }
    }

    private async Task LoadSelectedVersionSourceAsync(ArchiveVersionRow? row)
    {
        if (row is null)
        {
            SelectedVersionSource = string.Empty;
            return;
        }

        if (!row.IsLocalAvailable)
        {
            SelectedVersionSource = $"// [CLOUD ONLY]\n// {Strings.CloudOnlyNotice}\n// File: {row.FileName}\n// SHA-256: {row.Record.Sha256}\n// Problem: {row.ProblemSummary}";
            return;
        }

        try
        {
            var bytes = await _archiver.ReadAndVerifyVersionBytesAsync(row.Id);
            var (text, _) = SourceAnalyzer.DecodePreservingInfo(bytes);
            SelectedVersionSource = text;
            if (IsDiffModeActive)
                await RecomputeDiffAsync();
        }
        catch (Exception ex)
        {
            SelectedVersionSource = $"// [INTEGRITY ERROR] {UserFriendlyErrorService.RedactSecrets(ex.Message)}";
            ShowFriendlyException(ex, ErrorCategory.ArchiveIntegrityFailure);
        }
        finally
        {
            await LoadProblemDataForSelectionAsync(row.Record.Platform, row.Record.ProblemCode);
        }
    }

    private string ResolveCurrentProblemKey()
    {
        if (SelectedVersion is not null && !string.IsNullOrWhiteSpace(SelectedVersion.Record.ProblemCode))
            return ProblemStatementRecord.BuildKey(SelectedVersion.Record.Platform, SelectedVersion.Record.ProblemCode!);
        if (HasActiveContext)
            return ProblemStatementRecord.BuildKey(ActivePlatform, ActiveCode);
        return string.Empty;
    }

    private async Task LoadProblemDataForSelectionAsync(string? platform, string? problemCode)
    {
        var effCode = !string.IsNullOrWhiteSpace(problemCode) ? problemCode : (HasActiveContext ? ActiveCode : string.Empty);
        var effPlat = !string.IsNullOrWhiteSpace(platform) ? platform : ActivePlatform;
        bool vi = _config.Language == UiLanguage.Vi;
        if (string.IsNullOrWhiteSpace(effCode) || effCode == "-")
        {
            SampleTests.Clear();
            SelectedSampleTest = null;
            CurrentProblemStatementSummary = vi
                ? "Chọn một bài đã lưu có mã bài tập hoặc mở đề bài trên trình duyệt đã kết nối tiện ích."
                : "Select an archived problem with a recognized problem code or browse a problem with extension connected.";
            LatestVerdictBadgeText = vi ? "Chưa có kết quả chấm" : "No Judge Verdict";
            return;
        }

        var key = ProblemStatementRecord.BuildKey(effPlat ?? "Unknown", effCode);
        var stmt = await _store.GetProblemStatementAsync(key);
        if (stmt is null && !string.IsNullOrWhiteSpace(ActivePlatform) && !string.Equals(ActivePlatform, "Offline", StringComparison.OrdinalIgnoreCase))
        {
            key = ProblemStatementRecord.BuildKey(ActivePlatform, effCode);
            stmt = await _store.GetProblemStatementAsync(key);
        }

        if (stmt is not null)
        {
            var limits = string.Join(" • ", new[] { stmt.TimeLimit, stmt.MemoryLimit }.Where(s => !string.IsNullOrWhiteSpace(s)));
            var checker = stmt.InteractiveOrCustomJudge ? " [Interactive / Custom Checker]" : string.Empty;
            var bodySnippet = stmt.Description.Length > 350 ? stmt.Description[..350] + "..." : stmt.Description;
            CurrentProblemStatementSummary = $"[{stmt.ProblemCode}] {stmt.Title}{checker}{(string.IsNullOrWhiteSpace(limits) ? "" : $"\nLimits: {limits}")}\n{bodySnippet}";
        }
        else
        {
            CurrentProblemStatementSummary = vi
                ? $"Bài {effCode} (chưa thu thập nội dung đề — mở tab bài tập khi tiện ích đã kết nối)."
                : $"Problem {effCode} (statement not captured yet — open problem tab with extension connected).";
        }

        var prevId = SelectedSampleTest?.Id;
        var samples = await _store.GetSampleTestsAsync(key);
        SampleTests.Clear();
        foreach (var tc in samples)
            SampleTests.Add(tc);
        SelectedSampleTest = SampleTests.FirstOrDefault(s => s.Id == prevId) ?? SampleTests.FirstOrDefault();

        var subs = await _store.GetSubmissionsForProblemAsync(effCode, 10);
        if (subs.Count > 0)
        {
            var top = subs[0];
            var tag = top.IsVersionCertain
                ? (vi ? "đã khớp phiên bản" : "verified version")
                : (vi ? "chưa xác minh phiên bản" : "version link unverified");
            LatestVerdictBadgeText = $"{top.Verdict} ({tag})";
        }
        else
        {
            LatestVerdictBadgeText = vi ? "Chưa có kết quả chấm" : "No Judge Verdict";
        }

        var timeline = await _store.GetTimelineForProblemAsync(effCode, 50);
        TimelineEntries.Clear();
        foreach (var te in timeline)
            TimelineEntries.Add(te);
        TimelineSummaryText = timeline.Count == 0
            ? (vi ? $"Dòng thời gian ({effCode}): Chưa có sự kiện nào." : $"Timeline ({effCode}): No events recorded yet.")
            : (vi ? "Dòng thời gian (UTC): " : "Timeline (UTC): ") + string.Join(" | ", timeline.Take(4).Select(t =>
                $"[{DateTimeOffset.FromUnixTimeMilliseconds(t.OccurredAtUnixMs).UtcDateTime:HH:mm:ss}] {t.EventType}"));

        ActiveCodingTimeText = _codingTime.FormatEstimatedDuration(effCode);
    }

    public async Task RunSelectedSampleAsync()
    {
        if (SelectedVersion is null || SelectedSampleTest is null)
        {
            ShowInfoBar("Run Sample", "Select both an archived version and a sample test case first.", InfoBarSeverity.Informational);
            return;
        }
        IsExecuting = true;
        try
        {
            var res = await _sampleRunner.RunSingleSampleAsync(SelectedVersion.Id, SelectedSampleTest);
            SampleRunSummaryText = res.DiffSummary;
            SampleComparisonDiffText = !string.IsNullOrWhiteSpace(res.CompilerOrStderr) && !res.Passed
                ? $"{res.StatusLabel}\n{res.CompilerOrStderr}\n\n{res.UnifiedDiffText}"
                : res.UnifiedDiffText;
            RunnerStatusText = res.StatusLabel;
            RunnerMetricsText = $"{res.ElapsedMs} ms";
        }
        finally
        {
            IsExecuting = false;
        }
    }

    public async Task RunAllSamplesAsync()
    {
        var key = ResolveCurrentProblemKey();
        if (SelectedVersion is null || string.IsNullOrEmpty(key))
        {
            ShowInfoBar("Run All Samples", "Select an archived problem version with a recognized problem code.", InfoBarSeverity.Informational);
            return;
        }
        IsExecuting = true;
        try
        {
            var suite = await _sampleRunner.RunAllSamplesAsync(SelectedVersion.Id, key);
            SampleRunSummaryText = suite.SummaryLabel;
            SampleComparisonDiffText = string.Join(
                "\n\n",
                suite.CaseResults.Select(r => $"--- Case #{r.CaseIndex}{(r.IsCustom ? " (Custom)" : "")}: {r.StatusLabel} ({r.ElapsedMs} ms) ---\n{r.UnifiedDiffText}"));
            RunnerStatusText = suite.SummaryLabel;
        }
        finally
        {
            IsExecuting = false;
        }
    }

    public async Task AddCustomSampleAsync(string inputText, string expectedOutput)
    {
        var key = ResolveCurrentProblemKey();
        if (string.IsNullOrEmpty(key))
        {
            ShowInfoBar("Custom Sample", "Select a problem first to attach a custom test case.", InfoBarSeverity.Warning);
            return;
        }
        var added = await _sampleRunner.AddCustomSampleAsync(key, inputText, expectedOutput, ActiveUrl);
        SampleTests.Add(added);
        SelectedSampleTest = added;
        ShowInfoBar("Custom Test Added", $"Added Custom Test #{added.CaseIndex}.", InfoBarSeverity.Success);
    }

    public async Task DeleteSelectedSampleAsync()
    {
        if (SelectedSampleTest is null)
            return;
        var toDelete = SelectedSampleTest;
        await _store.DeleteSampleTestAsync(toDelete.Id);
        SampleTests.Remove(toDelete);
        SelectedSampleTest = SampleTests.FirstOrDefault();
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

    private void OnContextChanged(ProblemContext? ctx) => _dispatch(() =>
    {
        UpdateContext(ctx);
        if (ctx is not null)
            _ = LoadProblemDataForSelectionAsync(ctx.Platform.ToString(), ctx.Code);
    });

    private void OnProblemStatementReceived(ProblemStatementRecord stmt, IReadOnlyList<SampleTestCaseRecord> samples) =>
        _dispatch(() => { _ = LoadProblemDataForSelectionAsync(stmt.Platform, stmt.ProblemCode); });

    private void OnSubmissionVerdictReceived(SubmissionRecord sub) =>
        _dispatch(() =>
        {
            _ = LoadProblemDataForSelectionAsync(sub.Platform, sub.ProblemCode);
            _ = RefreshStatsAndEventsAsync();
        });

    private void OnBrowserConnectionsChanged() => _dispatch(() =>
    {
        RefreshBrowserAndIdeDiscovery();
        SaveConfigSafe();

        if (IsBrowserSetupOpen && !string.IsNullOrWhiteSpace(BrowserSetupBrowserKindKey))
        {
            var connectedTarget = ConnectedBrowsers.FirstOrDefault(b =>
                string.Equals(b.KindKey, BrowserSetupBrowserKindKey, StringComparison.OrdinalIgnoreCase));
            if (connectedTarget is not null)
            {
                IsBrowserSetupOpen = false;
                ShowInfoBar(
                    _config.Language == UiLanguage.Vi ? "Đã kết nối trình duyệt" : "Browser Connected",
                    _config.Language == UiLanguage.Vi
                        ? $"{connectedTarget.Name} đã kết nối với GhostCode và sẵn sàng nhận diện bài tập."
                        : $"{connectedTarget.Name} is now connected to GhostCode.",
                    InfoBarSeverity.Success);
            }
        }
    });

    private void OnVersionArchived(ArchiveVersionRecord version)
    {
        _codingTime.RecordActivity(string.IsNullOrWhiteSpace(version.ProblemCode) ? version.SourcePath : version.ProblemCode, version.CreatedAtUnixMs);
        _dispatch(() =>
        {
            if (_config.AutoCopyReadyPath && !string.IsNullOrWhiteSpace(version.ReadyPath))
                _clipboardCopyWithRetry?.Invoke(version.ReadyPath);
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

    private void OnReconciliationCompleted(int archivedCount)
    {
        _dispatch(() =>
        {
            _ = RefreshAllAsync();
        });
    }

    private void OnDriveSyncCompleted(DriveSyncReport report) => _dispatch(() => { _ = RefreshAllAsync(); });

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

    private static void OpenFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
    }

    public void Dispose()
    {
        _discoveryRefreshTimer?.Dispose();
        _discoveryRefreshTimer = null;
        _execCts?.Dispose();
        _oauthCts?.Dispose();
        _archiver.VersionArchived -= OnVersionArchived;
        _monitor.EventRecorded -= OnMonitorEventRecorded;
        _monitor.ReconciliationCompleted -= OnReconciliationCompleted;
        _bridge.ContextChanged -= OnContextChanged;
        _bridge.BrowserConnectionsChanged -= OnBrowserConnectionsChanged;
        _bridge.ProblemStatementReceived -= OnProblemStatementReceived;
        _bridge.SubmissionVerdictReceived -= OnSubmissionVerdictReceived;
        _drive.SyncCompleted -= OnDriveSyncCompleted;
        _drive.AuthStageChanged -= OnAuthStageChanged;
        if (_oneDrive is not null)
        {
            _oneDrive.SyncCompleted -= OnDriveSyncCompleted;
            _oneDrive.AuthStageChanged -= OnAuthStageChanged;
        }
    }
}
