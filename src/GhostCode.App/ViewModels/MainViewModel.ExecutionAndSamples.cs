using GhostCode.App.Localization;
using GhostCode.Core.Analysis;
using GhostCode.Core.Execution;
using GhostCode.Core.Models;
using GhostCode.Core.Notifications;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GhostCode.App.ViewModels;

public sealed partial class MainViewModel
{
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
            {
                OnPropertyChanged(nameof(IoOutputText));
                OnPropertyChanged(nameof(StderrPanelVisibility));
            }
        }
    }

    public Visibility StderrPanelVisibility => string.IsNullOrWhiteSpace(StderrText) ? Visibility.Collapsed : Visibility.Visible;

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

    public string RunnerDiagnosticLogText
    {
        get => _runnerDiagnosticLogText;
        private set
        {
            if (SetProperty(ref _runnerDiagnosticLogText, value))
                OnPropertyChanged(nameof(HasRunnerDiagnosticsVisibility));
        }
    }

    public Visibility HasRunnerDiagnosticsVisibility =>
        string.IsNullOrWhiteSpace(_runnerDiagnosticLogText) ? Visibility.Collapsed : Visibility.Visible;

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
                var prevSample = _activeSampleInputText;
                _activeSampleInputText = value?.InputText ?? string.Empty;
                _activeSampleOutputText = value?.ExpectedOutput ?? string.Empty;
                if (value is not null && (string.IsNullOrEmpty(StdinText) || StdinText == prevSample))
                    StdinText = value.InputText;
                OnPropertyChanged(nameof(SelectedSampleInputPreview));
                OnPropertyChanged(nameof(SelectedSampleExpectedPreview));
            }
        }
    }

    public string SampleCountBadgeText
    {
        get
        {
            var capturedCount = SampleTests.Count(s => !s.IsCustom);
            var customCount = SampleTests.Count - capturedCount;
            var vi = _config.Language == UiLanguage.Vi;
            if (capturedCount > 0 && customCount > 0)
                return vi
                    ? $"{capturedCount} test mẫu từ đề + {customCount} test tự thêm"
                    : $"{capturedCount} captured + {customCount} custom test(s)";
            if (capturedCount > 0)
                return vi
                    ? $"Tìm thấy {capturedCount} test mẫu từ đề bài"
                    : $"{capturedCount} sample test(s) captured";
            if (customCount > 0)
                return vi
                    ? $"Chưa có test mẫu từ đề ({customCount} test tự thêm)"
                    : $"No page samples ({customCount} custom test(s))";
            return vi
                ? "Chưa tìm thấy test mẫu từ đề bài."
                : "No sample tests detected from the problem page.";
        }
    }

    public Visibility HasSamplesVisibility => SampleTests.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NoSamplesVisibility => SampleTests.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public string RunnerDiagnosticsHeaderText => _config.Language == UiLanguage.Vi ? "Chi tiết biên dịch & chẩn đoán" : "Compiler & Diagnostics";

    public string SelectedSampleInputPreview => SelectedSampleTest?.InputText ?? string.Empty;
    public string SelectedSampleExpectedPreview => SelectedSampleTest?.ExpectedOutput ?? string.Empty;
    public string CurrentProblemStatementSummary { get => _currentProblemStatementSummary; private set => SetProperty(ref _currentProblemStatementSummary, value); }
    public string LatestVerdictBadgeText { get => _latestVerdictBadgeText; private set => SetProperty(ref _latestVerdictBadgeText, value); }
    public string SampleRunSummaryText { get => _sampleRunSummaryText; private set => SetProperty(ref _sampleRunSummaryText, value); }
    public string SampleComparisonDiffText { get => _sampleComparisonDiffText; private set => SetProperty(ref _sampleComparisonDiffText, value); }
    public string TimelineSummaryText { get => _timelineSummaryText; private set => SetProperty(ref _timelineSummaryText, value); }
    public string ActiveCodingTimeText { get => _activeCodingTimeText; private set => SetProperty(ref _activeCodingTimeText, value); }

    public bool IsExecuting
    {
        get => _isExecuting;
        private set
        {
            if (SetProperty(ref _isExecuting, value))
            {
                OnPropertyChanged(nameof(CanRunSelectedVersion));
                OnPropertyChanged(nameof(PrimaryRunButtonText));
                RunCodeCommand.NotifyCanExecuteChanged();
                RunOrStopCodeCommand.NotifyCanExecuteChanged();
                StopExecutionCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public async Task RunOrStopCodeAsync()
    {
        if (IsExecuting)
        {
            StopExecution();
            return;
        }
        await RunSelectedVersionAsync();
    }

    public async Task RunSelectedVersionAsync()
    {
        if (SelectedVersion is null || IsExecuting)
            return;

        bool vi = _config.Language == UiLanguage.Vi;
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
        RunnerDiagnosticLogText = string.Empty;
        ConsoleLogText = string.Empty;
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

            if (!string.IsNullOrWhiteSpace(res.Stdout))
                ConsoleLogText = res.Stdout;
            else if (!string.IsNullOrWhiteSpace(res.Stderr))
                ConsoleLogText = res.Stderr;
            else if (res.Status == ExecutionStatus.CompileError)
                ConsoleLogText = res.CompilerOutput;
            else if (string.IsNullOrWhiteSpace(ConsoleLogText))
                ConsoleLogText = vi ? "(Không có kết quả xuất ra STDOUT)" : "(No STDOUT output)";

            if (!string.IsNullOrWhiteSpace(res.CompilerOutput) || !string.IsNullOrWhiteSpace(res.CommandLineDisplay))
            {
                var diagParts = new List<string>();
                if (!string.IsNullOrWhiteSpace(res.CommandLineDisplay))
                    diagParts.Add(res.CommandLineDisplay);
                if (!string.IsNullOrWhiteSpace(res.CompilerOutput))
                    diagParts.Add(res.CompilerOutput);
                RunnerDiagnosticLogText = string.Join("\n\n", diagParts);
            }

            RunnerMetricsText = string.Empty;
            var memStr = $"{(res.PeakMemoryMb ?? 0):F2} MB";
            RunnerStatusText = res.Status switch
            {
                ExecutionStatus.CompileError => vi ? "Lỗi biên dịch" : "Compile Error",
                ExecutionStatus.TimeLimitExceeded => vi ? "Quá thời gian chạy" : "Time Limit Exceeded",
                ExecutionStatus.Cancelled => vi ? "Đã dừng" : "Stopped",
                _ => res.ExitCode == 0
                    ? (vi ? $"Hoàn thành · Exit 0 · {res.ElapsedMs} ms · {memStr}" : $"Finished · Exit 0 · {res.ElapsedMs} ms · {memStr}")
                    : (vi ? $"Kết thúc · Exit {res.ExitCode?.ToString() ?? "-"} · {res.ElapsedMs} ms · {memStr}" : $"Exited · Exit {res.ExitCode?.ToString() ?? "-"} · {res.ElapsedMs} ms · {memStr}")
            };

            if (res.Status == ExecutionStatus.TimeLimitExceeded)
                ShowFriendlyError(ErrorCategory.ExecutionTimeout, $"{res.ElapsedMs} ms");
            else if (res.Status == ExecutionStatus.CompileError
                && (res.CompilerOutput.Contains("not found", StringComparison.OrdinalIgnoreCase)
                    || res.CompilerOutput.Contains("not detected", StringComparison.OrdinalIgnoreCase)))
                ShowFriendlyError(ErrorCategory.CompilerMissing, res.CompilerOutput);
        }
        catch (OperationCanceledException)
        {
            RunnerStatusText = vi ? "Đã dừng" : "Stopped";
            RunnerMetricsText = string.Empty;
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
        RunnerDiagnosticLogText = string.Empty;
        ConsoleLogText = Strings.ConsoleOutputEmpty;
        RunnerMetricsText = string.Empty;
        RunnerStatusText = Strings.StatusReady;
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

    private bool TryCopyRawTextToClipboard(string text, string successLabel)
    {
        if (_clipboardCopyWithRetry is null)
            return false;

        bool ok = _clipboardCopyWithRetry(text);
        var vi = _config.Language == UiLanguage.Vi;
        if (ok)
            ShowInfoBar(vi ? "Đã sao chép" : "Copied", successLabel, InfoBarSeverity.Success);
        else
            ShowInfoBar(
                vi ? "Bảng nhớ tạm đang bận" : "Clipboard Busy",
                vi ? "Clipboard Windows đang bị ứng dụng khác khóa tạm thời." : "Windows clipboard is temporarily locked by another application.",
                InfoBarSeverity.Warning);
        return ok;
    }

    private async Task CopySelectedCodeAsync()
    {
        var vi = _config.Language == UiLanguage.Vi;
        if (SelectedVersion is null || !SelectedVersion.IsLocalAvailable)
        {
            ShowInfoBar(vi ? "Không thể sao chép" : "Cannot Copy", Strings.NoVersionSelected, InfoBarSeverity.Warning);
            return;
        }

        var bytes = await _archiver.ReadAndVerifyVersionBytesAsync(SelectedVersion.Id);
        var (exactText, _) = SourceAnalyzer.DecodePreservingInfo(bytes);
        TryCopyRawTextToClipboard(exactText, vi
            ? $"Đã sao chép {SelectedVersion.VersionBadge} ({SelectedVersion.FileName})."
            : $"Copied {SelectedVersion.VersionBadge} ({SelectedVersion.FileName}) exact source.");
    }

    private async Task CopyLatestProblemCodeAsync()
    {
        var vi = _config.Language == UiLanguage.Vi;
        var target = await ResolveTargetVersionForQuickCopyAsync();
        if (target is null)
        {
            ShowInfoBar(vi ? "Chưa có tệp đích" : "No Target File", vi ? "Hãy chọn một tệp bài làm trước khi sao chép." : "Select a problem file explicitly before copying.", InfoBarSeverity.Warning);
            return;
        }

        var bytes = await _archiver.ReadAndVerifyVersionBytesAsync(target.Id);
        var (exactText, _) = SourceAnalyzer.DecodePreservingInfo(bytes);
        TryCopyRawTextToClipboard(exactText, vi
            ? $"Đã sao chép bản mới nhất {target.FileName} (v{target.VersionNum})."
            : $"Copied latest {target.FileName} (v{target.VersionNum}).");
    }

    private void CopyActiveSampleInput()
    {
        var vi = _config.Language == UiLanguage.Vi;
        if (string.IsNullOrEmpty(_activeSampleInputText))
        {
            ShowInfoBar(vi ? "Chưa có dữ liệu vào mẫu" : "No Sample Input", vi ? "Chưa phát hiện hoặc chọn test mẫu nào." : "No sample input detected or selected yet.", InfoBarSeverity.Informational);
            return;
        }
        TryCopyRawTextToClipboard(_activeSampleInputText, vi ? "Đã sao chép Input mẫu." : "Copied sample input.");
    }

    private void CopyActiveSampleOutput()
    {
        var vi = _config.Language == UiLanguage.Vi;
        if (string.IsNullOrEmpty(_activeSampleOutputText))
        {
            ShowInfoBar(vi ? "Chưa có kết quả mẫu" : "No Sample Output", vi ? "Chưa có kết quả mẫu kỳ vọng nào." : "No expected sample output available yet.", InfoBarSeverity.Informational);
            return;
        }
        TryCopyRawTextToClipboard(_activeSampleOutputText, vi ? "Đã sao chép Output mẫu." : "Copied sample output.");
    }

    private void CopyActiveProblemUrl()
    {
        var vi = _config.Language == UiLanguage.Vi;
        var url = !string.IsNullOrWhiteSpace(ActiveUrl) ? ActiveUrl : SelectedVersion?.Record.ProblemCode ?? string.Empty;
        if (string.IsNullOrWhiteSpace(url))
        {
            ShowInfoBar(vi ? "Chưa có URL bài tập" : "No Problem URL", vi ? "Chưa có URL bài tập đang hoạt động." : "No active problem URL available.", InfoBarSeverity.Informational);
            return;
        }
        TryCopyRawTextToClipboard(url, vi ? $"Đã sao chép URL: {url}" : $"Copied URL: {url}");
    }

    private void CopySelectedSourcePath()
    {
        var path = SelectedVersion?.SourcePath ?? SelectedQuickActionTarget?.SourcePath ?? string.Empty;
        if (string.IsNullOrWhiteSpace(path))
            return;
        TryCopyRawTextToClipboard(path, _config.Language == UiLanguage.Vi ? $"Đã sao chép đường dẫn: {path}" : $"Copied source path: {path}");
    }

    public async Task OpenRecentArchiveGroupAsync(ArchiveFileGroupRow? row, bool autoRun = false)
    {
        if (row is null)
            return;
        SelectedFileGroup = row;
        RequestNavigate("Archive");
        await SelectArchiveVersionByIdAsync(row.Latest.Id);
        if (autoRun)
            await RunSelectedVersionAsync();
    }

    public async Task CopyRecentArchiveGroupCodeAsync(ArchiveFileGroupRow? row)
    {
        if (row is null || !row.Latest.IsLocalAvailable)
            return;
        var bytes = await _archiver.ReadAndVerifyVersionBytesAsync(row.Latest.Id);
        var (exactText, _) = SourceAnalyzer.DecodePreservingInfo(bytes);
        TryCopyRawTextToClipboard(exactText, _config.Language == UiLanguage.Vi
            ? $"Đã sao chép {row.FileName} (v{row.Latest.VersionNum})."
            : $"Copied {row.FileName} (v{row.Latest.VersionNum}).");
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

    public void OpenQuickActionsPanel() => IsQuickActionsOpen = true;
    private void ToggleQuickActionsPanel() => IsQuickActionsOpen = !IsQuickActionsOpen;

    private async Task RefreshQuickActionsDataAsync()
    {
        var list = await _store.QueryVersionsAsync(null, "All", 250);
        QuickActionsCandidates.Clear();
        var grouped = list.GroupBy(SourceAnalyzer.BuildLogicalGroupKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ArchiveFileGroupRow(g.OrderByDescending(x => x.VersionNum).ThenByDescending(x => x.CreatedAtUnixMs).First(), g.Count(), _config.Language))
            .ToList();
        foreach (var g in grouped)
            QuickActionsCandidates.Add(g);

        ArchiveFileGroupRow? exactMatch = null;
        if (HasActiveContext)
            exactMatch = grouped.FirstOrDefault(g => string.Equals(g.Latest.ProblemCode, ActiveCode, StringComparison.OrdinalIgnoreCase));

        if (exactMatch is not null)
        {
            SelectedQuickActionTarget = exactMatch;
            QuickActionsAmbiguityNote = _config.Language == UiLanguage.Vi
                ? $"Khớp bài tập đang mở: {ActiveCode}"
                : $"Matched active problem {ActiveCode}";
        }
        else if (SelectedFileGroup is not null)
        {
            SelectedQuickActionTarget = grouped.FirstOrDefault(g => string.Equals(g.GroupKey, SelectedFileGroup.GroupKey, StringComparison.OrdinalIgnoreCase));
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
        _loadSourceCts?.Cancel();
        _loadSourceCts?.Dispose();
        var cts = new CancellationTokenSource();
        _loadSourceCts = cts;
        var ct = cts.Token;

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
            var bytes = await _archiver.ReadAndVerifyVersionBytesAsync(row.Id, ct);
            if (ct.IsCancellationRequested)
                return;
            var (text, _) = SourceAnalyzer.DecodePreservingInfo(bytes);
            SelectedVersionSource = text;
            if (IsDiffModeActive)
                await RecomputeDiffAsync();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            SelectedVersionSource = $"// [INTEGRITY ERROR] {UserFriendlyErrorService.RedactSecrets(ex.Message)}";
            ShowFriendlyException(ex, ErrorCategory.ArchiveIntegrityFailure);
        }
        finally
        {
            if (!ct.IsCancellationRequested)
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
        OnPropertyChanged(nameof(SampleCountBadgeText));
        OnPropertyChanged(nameof(HasSamplesVisibility));
        OnPropertyChanged(nameof(NoSamplesVisibility));
        SelectedSampleTest = SampleTests.FirstOrDefault(s => s.Id == prevId) ?? SampleTests.FirstOrDefault();

        var subs = await _store.GetSubmissionsForProblemAsync(effCode, 10);
        if (subs.Count > 0)
        {
            var top = subs[0];
            var tag = top.IsVersionCertain
                ? (vi ? "đã khớp" : "verified")
                : (vi ? "chưa khớp bản" : "unlinked");
            LatestVerdictBadgeText = vi ? $"Chấm online: {top.Verdict} ({tag})" : $"Judge: {top.Verdict} ({tag})";
        }
        else
        {
            LatestVerdictBadgeText = vi ? "Chưa có kết quả chấm online" : "No Judge Verdict";
        }

        var timeline = await _store.GetTimelineForProblemAsync(effCode, 50);
        TimelineEntries.Clear();
        foreach (var te in timeline)
            TimelineEntries.Add(te);
        TimelineSummaryText = timeline.Count == 0
            ? (vi ? $"Dòng thời gian ({effCode}): Chưa có sự kiện nào." : $"Timeline ({effCode}): No events recorded yet.")
            : (vi ? "Dòng thời gian (UTC): " : "Timeline (UTC): ") + string.Join(" | ", timeline.Take(4).Select(t =>
                $"[{DateTimeOffset.FromUnixTimeMilliseconds(t.OccurredAtUnixMs).UtcDateTime:HH:mm:ss}] {t.EventType}"));

        ActiveCodingTimeText = _codingTime.FormatEstimatedDuration(effCode, _config.Language);
    }

    public async Task RunSelectedSampleAsync()
    {
        var vi = _config.Language == UiLanguage.Vi;
        if (SelectedVersion is null || SelectedSampleTest is null)
        {
            ShowInfoBar(
                vi ? "Chạy test mẫu" : "Run Sample",
                vi ? "Hãy chọn một phiên bản mã nguồn và một test mẫu trước." : "Select both an archived version and a sample test case first.",
                InfoBarSeverity.Informational);
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
        var vi = _config.Language == UiLanguage.Vi;
        var key = ResolveCurrentProblemKey();
        if (string.IsNullOrEmpty(key))
        {
            ShowInfoBar(
                vi ? "Test tùy chỉnh" : "Custom Sample",
                vi ? "Hãy chọn một bài làm trước khi thêm test riêng." : "Select a problem first to attach a custom test case.",
                InfoBarSeverity.Warning);
            return;
        }
        var added = await _sampleRunner.AddCustomSampleAsync(key, inputText, expectedOutput, ActiveUrl);
        SampleTests.Add(added);
        OnPropertyChanged(nameof(SampleCountBadgeText));
        OnPropertyChanged(nameof(HasSamplesVisibility));
        OnPropertyChanged(nameof(NoSamplesVisibility));
        SelectedSampleTest = added;
        ShowInfoBar(
            vi ? "Đã thêm test riêng" : "Custom Test Added",
            vi ? $"Đã thêm Test riêng #{added.CaseIndex}." : $"Added Custom Test #{added.CaseIndex}.",
            InfoBarSeverity.Success);
    }

    public async Task DeleteSelectedSampleAsync()
    {
        if (SelectedSampleTest is null)
            return;
        var toDelete = SelectedSampleTest;
        await _store.DeleteSampleTestAsync(toDelete.Id);
        SampleTests.Remove(toDelete);
        OnPropertyChanged(nameof(SampleCountBadgeText));
        OnPropertyChanged(nameof(HasSamplesVisibility));
        OnPropertyChanged(nameof(NoSamplesVisibility));
        SelectedSampleTest = SampleTests.FirstOrDefault();
    }
}
