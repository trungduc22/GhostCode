using System.Diagnostics;
using GhostCode.App.Localization;
using GhostCode.Core.Browser;
using GhostCode.Core.Models;
using GhostCode.Core.Monitoring;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GhostCode.App.ViewModels;

public sealed partial class MainViewModel
{
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

    public int SelectedAccentPresetIndex
    {
        get => _selectedAccentPresetIndex;
        set
        {
            if (value < 0 || value > 6 || !SetProperty(ref _selectedAccentPresetIndex, value))
                return;
            _config.AccentPreset = (AccentColorPreset)value;
            SaveConfigSafe();
            OnPropertyChanged(nameof(CustomAccentVisibility));
            _applyAccentCallback?.Invoke();
        }
    }

    public string CustomAccentHex
    {
        get => _customAccentHex;
        set
        {
            if (!SetProperty(ref _customAccentHex, value))
                return;
            if (GhostCode.App.Theming.GhostThemePalette.TryParseHexColor(value, out _))
            {
                _config.CustomAccentHex = value.Trim();
                SaveConfigSafe();
                if (_config.AccentPreset == AccentColorPreset.CustomAccent)
                    _applyAccentCallback?.Invoke();
            }
        }
    }

    public Visibility CustomAccentVisibility =>
        _selectedAccentPresetIndex == (int)AccentColorPreset.CustomAccent ? Visibility.Visible : Visibility.Collapsed;

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
                ActiveCodingTimeText = _codingTime.FormatEstimatedDuration(ResolveCurrentProblemKey(), _config.Language);
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
                OnPropertyChanged(nameof(AccentPresetChoices));
                OnPropertyChanged(nameof(AccentColorHeaderText));
                OnPropertyChanged(nameof(AccentColorDescText));
                OnPropertyChanged(nameof(RecentArchivesCardTitle));
                OnPropertyChanged(nameof(NotificationDurationChoices));
                OnPropertyChanged(nameof(NotificationDurationHeaderText));
                OnPropertyChanged(nameof(NotificationDurationDescText));
                NotifyTitleBarStatsChanged();
                OnPropertyChanged(nameof(CompactBrowserSummaryText));
                OnPropertyChanged(nameof(SampleCountBadgeText));
                OnPropertyChanged(nameof(RunnerDiagnosticsHeaderText));
                NotifyLatestBackupAndDashboardLabelsChanged();
                ActiveCodingTimeText = _codingTime.FormatEstimatedDuration(ResolveCurrentProblemKey(), lang);
                _suppressFolderSync = true;
                WatchedFolders.Clear();
                foreach (var wf in _config.WatchedFolders)
                    WatchedFolders.Add(new WatchedFolderItem(wf, SyncWatchedFoldersToConfigAndReload, lang));
                _suppressFolderSync = false;
            }
        }
    }

    public static ThemePreference ToThemePreference(int comboIndex) => comboIndex switch
    {
        1 => ThemePreference.Light,
        2 => ThemePreference.Dark,
        _ => ThemePreference.System
    };

    public static int ToThemeIndex(ThemePreference pref) => pref switch
    {
        ThemePreference.Light => 1,
        ThemePreference.Dark => 2,
        _ => 0
    };

    public int SelectedThemeIndex
    {
        get => _selectedThemeIndex;
        set
        {
            if (value < 0 || !SetProperty(ref _selectedThemeIndex, value))
                return;
            var theme = ToThemePreference(value);
            if (_config.Theme != theme)
            {
                _config.Theme = theme;
                SaveConfigSafe();
            }
            _applyThemeCallback?.Invoke(ToElementTheme(theme));
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

    public void RefreshBrowserAndIdeDiscovery() => RefreshBrowserAndIdeDiscovery(forceToolchainRescan: false);

    public void RefreshBrowserAndIdeDiscovery(bool forceToolchainRescan)
    {
        var browserStates = _bridge.GetBrowserConnectionStates();
        var browserSig = string.Join("|", browserStates.Select(b =>
            $"{b.Kind}:{(b.IsInstalled ? 1 : 0)}:{(b.IsRunning ? 1 : 0)}:{(int)b.ConnectionState}:{b.ExtensionVersion}:{_config.Language}"));

        if (!string.Equals(browserSig, _lastBrowserSignature, StringComparison.Ordinal))
        {
            _lastBrowserSignature = browserSig;
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
            OnPropertyChanged(nameof(CompactBrowserSummaryText));

            var connectedNames = ConnectedBrowsers.Select(x => x.Name).ToList();
            var runningNames = browserStates.Where(b => b.IsRunning).Select(b => b.DisplayName).ToList();
            BrowserStatusSummary = connectedNames.Count > 0
                ? $"{string.Join(", ", connectedNames)} • {Strings.BrowserConnectedBadge}"
                : runningNames.Count > 0
                    ? (_config.Language == UiLanguage.Vi
                        ? $"{string.Join(", ", runningNames)} (Chưa kết nối tiện ích)"
                        : $"{string.Join(", ", runningNames)} (Extension Not Connected)")
                    : Strings.NoConnectedBrowsers;
        }

        var (ides, toolchains) = _toolchains.DiscoverAllCached(_config, forceToolchainRescan);
        var ideSig = string.Join("|", ides.Select(i => $"{i.Name}:{i.IsInstalled}:{i.IsRunning}:{i.ExecutablePath}:{_config.Language}"))
            + "||" + string.Join("|", toolchains.Select(t => $"{t.Kind}:{t.IsAvailable}:{t.ExecutablePath}:{t.Version}:{_config.Language}"));

        if (!string.Equals(ideSig, _lastIdeSignature, StringComparison.Ordinal) || forceToolchainRescan)
        {
            _lastIdeSignature = ideSig;
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

            string FormatToolPill(string kind, string shortName)
            {
                var found = toolchains.FirstOrDefault(t => string.Equals(t.Kind, kind, StringComparison.OrdinalIgnoreCase));
                return found is { IsAvailable: true } ? $"{shortName} ✓" : $"{shortName} ✗";
            }
            var pills = string.Join(" · ", new[]
            {
                FormatToolPill("C", "GCC"),
                FormatToolPill("C++", "G++"),
                FormatToolPill("Python", "Python")
            });
            var ideSuffix = installedIdes.Count > 0 ? $" · {installedIdes[0]} ✓" : string.Empty;
            CompactToolchainSummaryText = vi
                ? $"Công cụ: {pills}{ideSuffix}"
                : $"Tools: {pills}{ideSuffix}";
        }
    }

    private void TestToolchains()
    {
        RefreshBrowserAndIdeDiscovery(forceToolchainRescan: true);
        bool vi = _config.Language == UiLanguage.Vi;
        var summary = string.Join(" • ", Toolchains.Select(t => $"{t.Name}: {t.StatusBadge}"));
        bool anyAvailable = Toolchains.Any(t => t.Info.IsAvailable);
        ShowInfoBar(
            vi ? "Kết quả kiểm tra trình biên dịch" : "Toolchain Check Complete",
            summary,
            anyAvailable ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
    }

    private void ResetToolchainsToAuto()
    {
        CCompilerPath = string.Empty;
        CppCompilerPath = string.Empty;
        PythonInterpreterPath = string.Empty;
        RefreshBrowserAndIdeDiscovery(forceToolchainRescan: true);
        bool vi = _config.Language == UiLanguage.Vi;
        ShowInfoBar(
            vi ? "Đã đặt lại tự động dò tìm" : "Toolchains Reset to Auto",
            CompactToolchainSummaryText,
            InfoBarSeverity.Informational);
    }

    public async Task PrepareBrowserIntegrationAsync(string? kindArg) =>
        await OpenBrowserSetupAsync(kindArg);

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

    public void AddWatchedFolder(string folderPath)
    {
        var vi = _config.Language == UiLanguage.Vi;
        if (string.IsNullOrWhiteSpace(folderPath))
            return;
        if (KnownFoldersResolver.IsDriveRoot(folderPath))
        {
            ShowInfoBar(
                vi ? "Từ chối ổ đĩa gốc" : "Drive Root Refused",
                vi ? "Không theo dõi toàn bộ ổ đĩa gốc để đảm bảo an toàn và hiệu năng." : "Watching an entire drive root is blocked for safety and performance.",
                InfoBarSeverity.Error);
            return;
        }
        if (_monitor.IsExcludedOrOutputDirectory(folderPath))
        {
            ShowInfoBar(
                vi ? "Đã loại trừ thư mục đầu ra" : "Output Directory Excluded",
                vi ? "Không thể theo dõi ArchiveDir và ReadyDir để tránh vòng lặp sao lưu vô hạn." : "ArchiveDir and ReadyDir cannot be monitored to prevent infinite feedback loops.",
                InfoBarSeverity.Error);
            return;
        }
        if (WatchedFolders.Any(w => string.Equals(w.Path, folderPath, StringComparison.OrdinalIgnoreCase)))
        {
            ShowInfoBar(vi ? "Thư mục đã được theo dõi" : "Already Monitored", folderPath, InfoBarSeverity.Informational);
            return;
        }

        _suppressFolderSync = true;
        WatchedFolders.Add(new WatchedFolderItem(new WatchedFolderConfig(folderPath, true, true, Path.GetFileName(folderPath)), SyncWatchedFoldersToConfigAndReload, _config.Language));
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
            WatchedFolders.Add(new WatchedFolderItem(f, SyncWatchedFoldersToConfigAndReload, _config.Language));
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
        _selectedThemeIndex = ToThemeIndex(_config.Theme);
        _selectedNotificationDurationIndex = Array.IndexOf(NotificationDurationValuesSeconds, _config.NotificationDurationSeconds);
        if (_selectedNotificationDurationIndex < 0)
            _selectedNotificationDurationIndex = 3;
        _minimizeToTrayOnClose = _config.MinimizeToTrayOnClose;
        _autoCopyReadyPath = _config.AutoCopyReadyPath;
        _driveEnabled = _config.DriveEnabled;
        _cCompilerPath = _config.CCompilerPath ?? string.Empty;
        _cppCompilerPath = _config.CppCompilerPath ?? string.Empty;
        _pythonInterpreterPath = _config.PythonInterpreterPath ?? string.Empty;
        _defaultIdeForC = string.IsNullOrWhiteSpace(_config.DefaultIdeForC) ? "Auto" : _config.DefaultIdeForC;
        _defaultIdeForCpp = string.IsNullOrWhiteSpace(_config.DefaultIdeForCpp) ? "Auto" : _config.DefaultIdeForCpp;
        _defaultIdeForPython = string.IsNullOrWhiteSpace(_config.DefaultIdeForPython) ? "Auto" : _config.DefaultIdeForPython;
        _selectedCStandard = _config.CStandard;
        _selectedCppStandard = _config.CppStandard;
        _executionTimeoutMs = _config.ExecutionTimeoutMs;
        _executionMemoryLimitMb = _config.ExecutionMemoryLimitMb;
        _selectedConflictPolicyIndex = (int)_config.ConflictPolicy;
        _syncPausedManually = _config.SyncPausedManually;
        _selectedMicaKindIndex = (int)_config.MicaKind;
        _selectedAccentPresetIndex = (int)_config.AccentPreset;
        _customAccentHex = string.IsNullOrWhiteSpace(_config.CustomAccentHex) ? "#2563EB" : _config.CustomAccentHex;
        _selectedNotificationLevelIndex = (int)_config.NotificationLevel;
        _globalHotkeyEnabled = _config.GlobalHotkeyEnabled;
        _globalHotkeyGesture = _config.GlobalHotkeyGesture;
        _captureProblemStatements = _config.CaptureProblemStatements;
        _trackSubmissionVerdicts = _config.TrackSubmissionVerdicts;
        _trimTrailingWhitespaceOnCompare = _config.TrimTrailingWhitespaceOnCompare;
        _estimateActiveCodingTime = _config.EstimateActiveCodingTime;
        _aiAssistanceOptIn = _config.AiAssistanceOptIn;
        _activeCodingTimeText = _codingTime.FormatEstimatedDuration(string.Empty, _config.Language);

        _suppressFolderSync = true;
        WatchedFolders.Clear();
        foreach (var wf in _config.WatchedFolders)
            WatchedFolders.Add(new WatchedFolderItem(wf, SyncWatchedFoldersToConfigAndReload, _config.Language));
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

    private static void OpenFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
    }
}
