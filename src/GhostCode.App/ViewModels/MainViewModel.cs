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

/// <summary>
/// Central MVVM ViewModel bridging WinUI 3 views with GhostCode.Core services.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
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
    private int _selectedAccentPresetIndex;
    private string _customAccentHex = "#2563EB";
    private int _selectedNotificationLevelIndex;
    private int _selectedNotificationDurationIndex = 3;
    private CancellationTokenSource? _infoBarDismissCts;
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
    private string _compactToolchainSummaryText = string.Empty;
    private string _defaultIdeForC = "Auto";
    private string _defaultIdeForCpp = "Auto";
    private string _defaultIdeForPython = "Auto";
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
    private string _runnerDiagnosticLogText = string.Empty;
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
    private string? _infoBarActionUrl;
    private CloudServiceErrorDetails? _lastCloudServiceError;
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
    private Action? _applyAccentCallback;
    private bool _isWindowHiddenInTray;
    private bool _dirtyWhileHidden;
    private CancellationTokenSource? _coalescedRefreshCts;
    private CancellationTokenSource? _loadSourceCts;
    private string _loadedProblemDataKey = string.Empty;
    private int _protectionStateLevel = 1; // 0=Protected(Green), 1=Attention/Paused(Amber), 2=Unhealthy/Error(Red)
    private string _protectionHeadlineText = "Đang kiểm tra...";
    private string _protectionDetailText = string.Empty;
    private LatestBackupSummary? _latestBackup;
    private bool _isProblemDetailsExpanded;
    private string _lastBrowserSignature = string.Empty;
    private string _lastIdeSignature = string.Empty;

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
        _activeCodingTimeText = _codingTime.FormatEstimatedDuration(string.Empty, config.Language);

        LoadFieldsFromConfig();

        RefreshAllCommand = new AsyncRelayCommand(RefreshAllAsync);
        RefreshDiscoveryCommand = new RelayCommand(() => RefreshProcessesAndWatches(forceToolchainRescan: true));
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
        RunOrStopCodeCommand = new AsyncRelayCommand(RunOrStopCodeAsync, () => IsExecuting || CanRunSelectedVersion);
        OpenDevToolsPageCommand = new RelayCommand(() => RequestNavigate("DevTools"));
        TestToolchainCommand = new RelayCommand(TestToolchains);
        ResetToolchainsToAutoCommand = new RelayCommand(ResetToolchainsToAuto);
        OpenLatestBackupCommand = new AsyncRelayCommand(OpenLatestBackupAsync);
        ToggleProblemDetailsCommand = new RelayCommand(() => IsProblemDetailsExpanded = !IsProblemDetailsExpanded);
        OpenDriveApiConsoleCommand = new RelayCommand(OpenDriveApiConsole);

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

        _discoveryRefreshTimer = new Timer(
            _ =>
            {
                if (!_isWindowHiddenInTray)
                    _dispatch(() => RefreshBrowserAndIdeDiscovery(forceToolchainRescan: false));
            },
            null,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(30));
    }

    public ObservableCollection<ActiveWatchRow> ActiveWatches { get; } = new();
    public ObservableCollection<MonitorEventRow> RecentEvents { get; } = new();
    public ObservableCollection<ArchiveHistoryEventRow> HistoryEvents { get; } = new();
    public ObservableCollection<ArchiveVersionRow> ArchiveVersions { get; } = new();
    public ObservableCollection<ArchiveFileGroupRow> ArchiveFileGroups { get; } = new();
    public ObservableCollection<ArchiveFileGroupRow> RecentDashboardArchives { get; } = new();
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
    public ObservableCollection<UnifiedDiffLineRow> DiffUnifiedLines { get; } = new();
    public ObservableCollection<TrashedArchiveVersionRow> TrashItems { get; } = new();
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

    public IReadOnlyList<string> IdeChoices { get; } =
        new[] { "Auto", "Dev-C++", "VS Code", "Visual Studio", "PyCharm" };

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

    public IReadOnlyList<string> AccentPresetChoices => _config.Language == UiLanguage.Vi
        ? new[]
        {
            "Ghost Blue (Mặc định)",
            "Tím hiện đại (Violet)",
            "Xanh ngọc (Teal)",
            "Ngọc lục bảo (Emerald)",
            "Hổ phách (Amber)",
            "Theo màu nhấn Windows",
            "Tùy chỉnh (#Hex)"
        }
        : new[]
        {
            "Ghost Blue (Default)",
            "Violet",
            "Teal",
            "Emerald",
            "Amber",
            "Windows Accent",
            "Custom Hex"
        };

    public string AccentColorHeaderText => _config.Language == UiLanguage.Vi ? "Bảng màu & màu nhấn" : "Color Theme & Accent";
    public string AccentColorDescText => _config.Language == UiLanguage.Vi
        ? "Chọn bảng màu Ghost Blue mặc định, các bộ màu chọn sẵn hoặc đồng bộ màu Windows."
        : "Choose Signature Ghost Blue, curated presets, or Windows system accent while preserving Mica.";
    public string RecentArchivesCardTitle => _config.Language == UiLanguage.Vi ? "Mã nguồn lưu gần đây" : "Recent Archived Files";
    public Visibility HasRecentDashboardArchivesVisibility => RecentDashboardArchives.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NoRecentDashboardArchivesVisibility => RecentDashboardArchives.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public async Task OpenLatestBackupAsync()
    {
        if (_latestBackup is null)
            return;
        RequestNavigate("Archive");
        await SelectArchiveVersionByIdAsync(_latestBackup.VersionId);
    }

    private static readonly int[] NotificationDurationValuesSeconds = { 3, 5, 10, 15, 30, 0 };

    public IReadOnlyList<string> NotificationDurationChoices => _config.Language == UiLanguage.Vi
        ? new[]
        {
            "3 giây",
            "5 giây",
            "10 giây",
            "15 giây (mặc định)",
            "30 giây",
            "Không tự đóng"
        }
        : new[]
        {
            "3 seconds",
            "5 seconds",
            "10 seconds",
            "15 seconds (Default)",
            "30 seconds",
            "Do not auto-dismiss"
        };

    public string NotificationDurationHeaderText => _config.Language == UiLanguage.Vi
        ? "Thời gian hiển thị thông báo"
        : "Notification Display Duration";
    public string NotificationDurationDescText => _config.Language == UiLanguage.Vi
        ? "Tự động ẩn thông báo thường sau thời gian chọn; lỗi quan trọng vẫn giữ đến khi bạn đóng."
        : "Auto-dismiss informational notices; critical errors remain visible until dismissed.";

    public int SelectedNotificationDurationIndex
    {
        get => _selectedNotificationDurationIndex;
        set
        {
            if (value < 0 || value >= NotificationDurationValuesSeconds.Length || !SetProperty(ref _selectedNotificationDurationIndex, value))
                return;
            _config.NotificationDurationSeconds = NotificationDurationValuesSeconds[value];
            SaveConfigSafe();
        }
    }

    public string CompactBrowserSummaryText
    {
        get
        {
            bool vi = _config.Language == UiLanguage.Vi;
            if (ConnectedBrowsers.Count > 0)
                return $"{string.Join(", ", ConnectedBrowsers.Select(b => b.Name))} · {(vi ? "Đã kết nối" : "Connected")}";
            return vi ? "Chưa kết nối trình duyệt" : "No browser connected";
        }
    }

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
    public IAsyncRelayCommand RunOrStopCodeCommand { get; }
    public IRelayCommand OpenDevToolsPageCommand { get; }
    public IRelayCommand TestToolchainCommand { get; }
    public IRelayCommand ResetToolchainsToAutoCommand { get; }
    public IAsyncRelayCommand OpenLatestBackupCommand { get; }
    public IRelayCommand ToggleProblemDetailsCommand { get; }
    public IRelayCommand OpenDriveApiConsoleCommand { get; }

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
                OnPropertyChanged(nameof(PrimaryRunButtonText));
                _ = RefreshHealthAsync();
            }
        }
    }

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetProperty(ref _searchQuery, value))
                TriggerDebouncedArchiveFilter();
        }
    }

    public string SelectedPlatformFilter
    {
        get => _selectedPlatformFilter;
        set
        {
            if (SetProperty(ref _selectedPlatformFilter, value))
                _ = ApplyArchiveFilterFromCacheAsync();
        }
    }

    public int SelectedAvailabilityFilterIndex
    {
        get => _selectedAvailabilityFilterIndex;
        set
        {
            if (SetProperty(ref _selectedAvailabilityFilterIndex, value))
                _ = ApplyArchiveFilterFromCacheAsync();
        }
    }

    public ArchiveFileGroupRow? SelectedFileGroup
    {
        get => _selectedFileGroup;
        set
        {
            if (_suppressCollectionResetSelection && value is null)
                return;
            if (SetProperty(ref _selectedFileGroup, value))
            {
                if (value is not null)
                    _ = LoadVersionsForFileGroupAsync(value.Latest, preferredVersionId: null);
                else if (!_suppressCollectionResetSelection)
                    ClearSelectedGroupState();
            }
        }
    }

    public ArchiveVersionRow? SelectedVersion
    {
        get => _selectedVersion;
        set
        {
            if (_suppressCollectionResetSelection && value is null)
                return;
            if (SetProperty(ref _selectedVersion, value))
            {
                OnPropertyChanged(nameof(SelectedVersionIsCloudOnly));
                OnPropertyChanged(nameof(CanRunSelectedVersion));
                OnPropertyChanged(nameof(SelectedVersionHeaderText));
                OnPropertyChanged(nameof(HasSelectedVersion));
                RunCodeCommand.NotifyCanExecuteChanged();
                RunOrStopCodeCommand.NotifyCanExecuteChanged();
                _ = LoadSelectedVersionSourceAsync(value);
            }
        }
    }

    public bool HasSelectedVersion => SelectedVersion is not null;

    public string SelectedVersionSource
    {
        get => _selectedVersionSource;
        private set => SetProperty(ref _selectedVersionSource, value);
    }

    public string SelectedVersionHeaderText => SelectedVersion is null
        ? Strings.NoVersionSelected
        : SelectedVersion.CompactMetaText;

    public bool SelectedVersionIsCloudOnly => SelectedVersion is not null && !SelectedVersion.IsLocalAvailable;
    public bool CanRunSelectedVersion => SelectedVersion is not null && SelectedVersion.IsLocalAvailable && !IsExecuting;
    public string PrimaryRunButtonText => IsExecuting ? Strings.StopRunButton : Strings.RunCodeButton;

    public Visibility ArchiveEmptyStateVisibility =>
        (ArchiveFileGroups.Count == 0
         && string.IsNullOrWhiteSpace(SearchQuery)
         && SelectedPlatformFilter == "All"
         && SelectedAvailabilityFilterIndex == 0)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility ArchiveWorkspaceVisibility =>
        ArchiveEmptyStateVisibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

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

    public ArchiveFileGroupRow? SelectedQuickActionTarget
    {
        get => _selectedQuickActionTarget;
        set
        {
            if (SetProperty(ref _selectedQuickActionTarget, value))
            {
                OnPropertyChanged(nameof(HasQuickActionTarget));
                _ = RefreshQuickActionsPreviewAsync();
            }
        }
    }

    public bool HasQuickActionTarget => SelectedQuickActionTarget is not null && SelectedQuickActionTarget.Latest.IsLocalAvailable;

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
    public string BridgeInfoText => $"127.0.0.1:{_bridge.ListeningPort}  •  HMAC Paired Session (Secret Hidden)";
    public string LastDetectedFileText { get => _lastDetectedFileText; private set => SetProperty(ref _lastDetectedFileText, value); }
    public string LastSuccessfulBackupText { get => _lastSuccessfulBackupText; private set => SetProperty(ref _lastSuccessfulBackupText, value); }
    public string LastMonitoringErrorText { get => _lastMonitoringErrorText; private set => SetProperty(ref _lastMonitoringErrorText, value); }
    public string LastDiagnosticResultText { get => _lastDiagnosticResultText; private set => SetProperty(ref _lastDiagnosticResultText, value); }
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
        Func<bool, string, (bool Success, string Detail)> configureHotkey,
        Action? applyAccent = null)
    {
        _applyMicaCallback = applyMica;
        _configureHotkeyCallback = configureHotkey;
        _applyAccentCallback = applyAccent;
        _applyAccentCallback?.Invoke();
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
        _coalescedRefreshCts?.Cancel();
        RefreshProcessesAndWatches();
        await RefreshStatsAndEventsAsync();
        await RefreshArchiveAsync();
        await RefreshCloudStateAsync();
        await RefreshHealthAsync();
    }

    public void SetWindowHiddenInTray(bool hidden)
    {
        if (_isWindowHiddenInTray == hidden)
            return;

        _isWindowHiddenInTray = hidden;
        if (hidden)
        {
            try { _discoveryRefreshTimer?.Change(Timeout.Infinite, Timeout.Infinite); } catch { }
            return;
        }

        try { _discoveryRefreshTimer?.Change(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30)); } catch { }
        if (_dirtyWhileHidden)
        {
            _dirtyWhileHidden = false;
            _dispatch(() => _ = RefreshAllAsync());
        }
        else
        {
            _dispatch(() => RefreshProcessesAndWatches());
        }
    }

    public async Task RefreshArchiveAsync()
    {
        _cachedActiveVersions = await _store.QueryVersionsAsync(null, "All", 500);
        _cachedUploadStatuses = await _store.GetUploadStatusesByVersionIdsAsync(_cachedActiveVersions.Select(v => v.Id));
        var stats = await _store.GetStatsAsync();
        TrashCount = stats.TrashedVersions;
        await ApplyArchiveFilterFromCacheAsync();
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
        await LoadVersionsForFileGroupAsync(target, target.Id);
    }

    private async Task LoadVersionsForFileGroupAsync(ArchiveVersionRecord anchor, string? preferredVersionId)
    {
        var anchorKey = SourceAnalyzer.BuildLogicalGroupKey(anchor);
        var groupMatch = ArchiveFileGroups.FirstOrDefault(g => string.Equals(g.GroupKey, anchorKey, StringComparison.OrdinalIgnoreCase));
        if (groupMatch is not null && !string.Equals(_selectedFileGroup?.GroupKey, groupMatch.GroupKey, StringComparison.OrdinalIgnoreCase))
        {
            _selectedFileGroup = groupMatch;
            OnPropertyChanged(nameof(SelectedFileGroup));
        }

        var versions = !string.IsNullOrWhiteSpace(anchor.ProblemCode)
            ? await _store.GetVersionsByProblemAsync(anchor.Platform, anchor.ProblemCode)
            : await _store.GetOfflineVersionsBySourceAsync(anchor.SourcePath);
        var ordered = versions.OrderByDescending(x => x.VersionNum).ThenByDescending(x => x.CreatedAtUnixMs).ToList();
        var statusMap = await _store.GetUploadStatusesByVersionIdsAsync(ordered.Select(v => v.Id));

        ArchiveVersionRow? pick;
        var prevBaseId = _compareWithVersion?.Id;
        var prevTargetId = _diffTargetVersion?.Id;
        _suppressCollectionResetSelection = true;
        try
        {
            SelectedFileVersions.Clear();
            foreach (var v in ordered)
            {
                statusMap.TryGetValue(v.Id, out var qSt);
                SelectedFileVersions.Add(new ArchiveVersionRow(v, _config.Language, qSt, _config.PrimaryCloudProvider));
            }

            pick = preferredVersionId is not null
                ? SelectedFileVersions.FirstOrDefault(v => v.Id == preferredVersionId)
                : SelectedFileVersions.FirstOrDefault();

            if (!string.IsNullOrEmpty(prevBaseId))
            {
                var mappedBase = SelectedFileVersions.FirstOrDefault(v => v.Id == prevBaseId);
                if (mappedBase is not null)
                    SetProperty(ref _compareWithVersion, mappedBase, nameof(CompareWithVersion));
            }
            if (!string.IsNullOrEmpty(prevTargetId))
            {
                var mappedTarget = SelectedFileVersions.FirstOrDefault(v => v.Id == prevTargetId);
                if (mappedTarget is not null)
                    SetProperty(ref _diffTargetVersion, mappedTarget, nameof(DiffTargetVersion));
            }
        }
        finally
        {
            _suppressCollectionResetSelection = false;
        }

        OnPropertyChanged(nameof(CanCompareVersions));
        OnPropertyChanged(nameof(CompareButtonTooltipText));
        OnPropertyChanged(nameof(HasSelectedFileVersions));

        if (pick is not null)
        {
            if (SetProperty(ref _selectedVersion, pick, nameof(SelectedVersion)))
            {
                OnPropertyChanged(nameof(SelectedVersionIsCloudOnly));
                OnPropertyChanged(nameof(CanRunSelectedVersion));
                OnPropertyChanged(nameof(SelectedVersionHeaderText));
                OnPropertyChanged(nameof(HasSelectedVersion));
                RunCodeCommand.NotifyCanExecuteChanged();
                RunOrStopCodeCommand.NotifyCanExecuteChanged();
            }
            await LoadSelectedVersionSourceAsync(pick);
        }
        else
        {
            ClearSelectedVersionState();
        }
    }

    public void RequestNavigate(string tag) => NavigationRequested?.Invoke(tag);

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

    private void OnContextChanged(ProblemContext? ctx) => _dispatch(() =>
    {
        UpdateContext(ctx);
        if (ctx is not null)
        {
            var key = ProblemStatementRecord.BuildKey(ctx.Platform.ToString(), ctx.Code);
            if (!string.Equals(key, _loadedProblemDataKey, StringComparison.OrdinalIgnoreCase))
            {
                _loadedProblemDataKey = key;
                _ = LoadProblemDataForSelectionAsync(ctx.Platform.ToString(), ctx.Code);
            }
        }
        else
        {
            _loadedProblemDataKey = string.Empty;
        }
    });

    private void OnProblemStatementReceived(ProblemStatementRecord stmt, IReadOnlyList<SampleTestCaseRecord> samples) =>
        _dispatch(() =>
        {
            _loadedProblemDataKey = ProblemStatementRecord.BuildKey(stmt.Platform, stmt.ProblemCode);
            _ = LoadProblemDataForSelectionAsync(stmt.Platform, stmt.ProblemCode);
        });

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

    private void ScheduleCoalescedRefreshAsync(int delayMs = 350)
    {
        if (_isWindowHiddenInTray)
        {
            _dirtyWhileHidden = true;
            return;
        }

        _coalescedRefreshCts?.Cancel();
        _coalescedRefreshCts?.Dispose();
        var cts = new CancellationTokenSource();
        _coalescedRefreshCts = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delayMs, cts.Token);
                if (!cts.Token.IsCancellationRequested)
                    _dispatch(() => _ = RefreshAllAsync());
            }
            catch (OperationCanceledException) { }
        });
    }

    private void OnVersionArchived(ArchiveVersionRecord version)
    {
        _codingTime.RecordActivity(string.IsNullOrWhiteSpace(version.ProblemCode) ? version.SourcePath : version.ProblemCode, version.CreatedAtUnixMs);
        _dispatch(() =>
        {
            if (_config.AutoCopyReadyPath && !string.IsNullOrWhiteSpace(version.ReadyPath))
                _clipboardCopyWithRetry?.Invoke(version.ReadyPath);
            ScheduleCoalescedRefreshAsync();
        });
    }

    private void OnMonitorEventRecorded(MonitorEventRecord ev)
    {
        _dispatch(() =>
        {
            LastDetectedFileText = FormatMonitorStatus(_monitor.LastDetectedFile);
            LastSuccessfulBackupText = FormatMonitorStatus(_monitor.LastSuccessfulBackup);
            LastMonitoringErrorText = FormatMonitorStatus(_monitor.LastMonitoringError);
            if (_isWindowHiddenInTray)
            {
                _dirtyWhileHidden = true;
                return;
            }
            RecentEvents.Insert(0, new MonitorEventRow(ev));
            while (RecentEvents.Count > 50)
                RecentEvents.RemoveAt(RecentEvents.Count - 1);
        });
    }

    private void OnReconciliationCompleted(int archivedCount)
    {
        if (archivedCount > 0)
            ScheduleCoalescedRefreshAsync(250);
    }

    public void Dispose()
    {
        _discoveryRefreshTimer?.Dispose();
        _discoveryRefreshTimer = null;
        _coalescedRefreshCts?.Cancel();
        _coalescedRefreshCts?.Dispose();
        _coalescedRefreshCts = null;
        _loadSourceCts?.Cancel();
        _loadSourceCts?.Dispose();
        _loadSourceCts = null;
        _infoBarDismissCts?.Cancel();
        _infoBarDismissCts?.Dispose();
        _infoBarDismissCts = null;
        _searchDebounceCts?.Cancel();
        _searchDebounceCts?.Dispose();
        _searchDebounceCts = null;
        _diffRecomputeCts?.Cancel();
        _diffRecomputeCts?.Dispose();
        _diffRecomputeCts = null;
        _execCts?.Cancel();
        _execCts?.Dispose();
        _oauthCts?.Cancel();
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
