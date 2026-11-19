using GhostCode.Core.Analysis;
using GhostCode.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NativeShellKit.WinUI.Mvvm;

namespace GhostCode.App.ViewModels;

public sealed partial class MainViewModel
{
    private List<ArchiveVersionRecord> _cachedActiveVersions = new();
    private Dictionary<string, UploadStatus> _cachedUploadStatuses = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _searchDebounceCts;
    private CancellationTokenSource? _diffRecomputeCts;
    private bool _suppressCollectionResetSelection;

    private ArchiveVersionRow? _diffTargetVersion;
    private IReadOnlyList<int> _sideBySideHunks = Array.Empty<int>();
    private IReadOnlyList<int> _unifiedHunks = Array.Empty<int>();
    private int _currentHunkCursor = -1;
    private string _diffHeaderTitle = string.Empty;
    private string _diffVersionPairText = string.Empty;
    private string _diffAddedCountText = "+0";
    private string _diffRemovedCountText = "−0";
    private string _diffModifiedCountText = "~0";
    private string _diffNoticeText = string.Empty;

    private int _trashCount;
    private string? _lastUndoBatchId;
    private string? _lastUndoVersionId;
    private bool _lastUndoWasProblem;

    public event Action<int, bool>? DiffScrollRequested;

    public bool HasSelectedFileVersions => SelectedFileVersions.Count > 0;
    public bool CanCompareVersions => SelectedFileVersions.Count >= 2;
    public string CompareButtonTooltipText => CanCompareVersions
        ? (_config.Language == UiLanguage.Vi ? "So sánh hai phiên bản" : "Compare two versions")
        : (_config.Language == UiLanguage.Vi ? "Cần ít nhất 2 phiên bản để so sánh." : "Need at least 2 versions to compare.");

    public ArchiveVersionRow? CompareWithVersion
    {
        get => _compareWithVersion;
        set
        {
            if ((_suppressCollectionResetSelection || (IsDiffModeActive && SelectedFileVersions.Count >= 2)) && value is null)
                return;
            if (SetProperty(ref _compareWithVersion, value) && IsDiffModeActive)
                _ = RecomputeDiffAsync();
        }
    }

    public ArchiveVersionRow? DiffTargetVersion
    {
        get => _diffTargetVersion;
        set
        {
            if ((_suppressCollectionResetSelection || (IsDiffModeActive && SelectedFileVersions.Count >= 2)) && value is null)
                return;
            if (SetProperty(ref _diffTargetVersion, value) && IsDiffModeActive)
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
                OnPropertyChanged(nameof(RunnerPaneVisibility));
                OnPropertyChanged(nameof(SourcePaneColumnSpan));
                if (value)
                    _ = RecomputeDiffAsync();
                else
                    ReleaseDiffBuffers();
            }
        }
    }

    public Visibility DiffModeVisibility => IsDiffModeActive ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SourceViewerVisibility => IsDiffModeActive ? Visibility.Collapsed : Visibility.Visible;
    public Visibility RunnerPaneVisibility => IsDiffModeActive ? Visibility.Collapsed : Visibility.Visible;
    public int SourcePaneColumnSpan => IsDiffModeActive ? 2 : 1;

    public bool DiffUseUnifiedMode
    {
        get => _diffUseUnifiedMode;
        set
        {
            if (SetProperty(ref _diffUseUnifiedMode, value))
            {
                OnPropertyChanged(nameof(DiffSideBySideVisibility));
                OnPropertyChanged(nameof(DiffUnifiedVisibility));
                _currentHunkCursor = -1;
            }
        }
    }

    public Visibility DiffSideBySideVisibility => !DiffUseUnifiedMode ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DiffUnifiedVisibility => DiffUseUnifiedMode ? Visibility.Visible : Visibility.Collapsed;
    public string DiffSummaryText { get => _diffSummaryText; private set => SetProperty(ref _diffSummaryText, value); }
    public string DiffUnifiedText { get => _diffUnifiedText; private set => SetProperty(ref _diffUnifiedText, value); }
    public string DiffHeaderTitle { get => _diffHeaderTitle; private set => SetProperty(ref _diffHeaderTitle, value); }
    public string DiffVersionPairText { get => _diffVersionPairText; private set => SetProperty(ref _diffVersionPairText, value); }
    public string DiffAddedCountText { get => _diffAddedCountText; private set => SetProperty(ref _diffAddedCountText, value); }
    public string DiffRemovedCountText { get => _diffRemovedCountText; private set => SetProperty(ref _diffRemovedCountText, value); }
    public string DiffModifiedCountText { get => _diffModifiedCountText; private set => SetProperty(ref _diffModifiedCountText, value); }

    public string DiffNoticeText
    {
        get => _diffNoticeText;
        private set
        {
            if (SetProperty(ref _diffNoticeText, value))
                OnPropertyChanged(nameof(DiffNoticeVisibility));
        }
    }
    public Visibility DiffNoticeVisibility => string.IsNullOrWhiteSpace(_diffNoticeText) ? Visibility.Collapsed : Visibility.Visible;

    public int TrashCount
    {
        get => _trashCount;
        private set
        {
            if (SetProperty(ref _trashCount, value))
                OnPropertyChanged(nameof(TrashButtonLabel));
        }
    }

    public string TrashButtonLabel => _config.Language == UiLanguage.Vi
        ? (TrashCount > 0 ? $"Thùng rác ({TrashCount})" : "Thùng rác")
        : (TrashCount > 0 ? $"Trash ({TrashCount})" : "Trash");

    private void TriggerDebouncedArchiveFilter()
    {
        _searchDebounceCts?.Cancel();
        _searchDebounceCts?.Dispose();
        var cts = new CancellationTokenSource();
        _searchDebounceCts = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(180, cts.Token);
                _dispatch(() => _ = ApplyArchiveFilterFromCacheAsync());
            }
            catch (OperationCanceledException) { }
        });
    }

    public async Task ApplyArchiveFilterFromCacheAsync()
    {
        if (_cachedActiveVersions.Count == 0 && ArchiveFileGroups.Count == 0)
            _cachedActiveVersions = await _store.QueryVersionsAsync(null, "All", 500);

        IEnumerable<ArchiveVersionRecord> filtered = _cachedActiveVersions;

        if (!string.Equals(SelectedPlatformFilter, "All", StringComparison.OrdinalIgnoreCase))
            filtered = filtered.Where(v => string.Equals(v.Platform, SelectedPlatformFilter, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(SearchQuery))
        {
            var normQ = SourceAnalyzer.StripDiacritics(SearchQuery).Trim().ToLowerInvariant();
            filtered = filtered.Where(v =>
            {
                var meta = SourceAnalyzer.StripDiacritics($"{v.FileName} {v.ProblemCode} {v.ProblemTitle} {v.Platform} {v.SourcePath} v{v.VersionNum}")
                    .ToLowerInvariant();
                return meta.Contains(normQ, StringComparison.Ordinal);
            });
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
            filtered = filtered.Where(v => v.Availability == f);

        var list = filtered.ToList();
        var selectedId = SelectedVersion?.Id;
        var selectedGroupKey = SelectedFileGroup?.GroupKey;

        _suppressCollectionResetSelection = true;
        try
        {
            var rows = list.Select(version => new ArchiveVersionRow(version, _config.Language,
                _cachedUploadStatuses.TryGetValue(version.Id, out var status) ? status : null,
                _config.PrimaryCloudProvider)).ToArray();
            ArchiveVersions.ApplyKeyedDiff(rows, row => row.Id);

            var groups = rows.GroupBy(row => SourceAnalyzer.BuildLogicalGroupKey(row.Record), StringComparer.OrdinalIgnoreCase)
                .Select(group =>
            {
                var latest = group.OrderByDescending(row => row.VersionNum)
                    .ThenByDescending(row => row.Record.CreatedAtUnixMs).First();
                return new ArchiveFileGroupRow(latest.Record, group.Count(), latest.Language, latest.QueueStatus, latest.Provider);
            }).ToArray();
            ArchiveFileGroups.ApplyKeyedDiff(groups, row => row.GroupKey.ToUpperInvariant());
        }
        finally
        {
            _suppressCollectionResetSelection = false;
        }

        if (string.IsNullOrWhiteSpace(SearchQuery) && SelectedPlatformFilter == "All" && SelectedAvailabilityFilterIndex == 0)
        {
            RecentDashboardArchives.ApplyKeyedDiff(ArchiveFileGroups.Take(6).ToArray(), row => row.GroupKey.ToUpperInvariant());
            OnPropertyChanged(nameof(HasRecentDashboardArchivesVisibility));
            OnPropertyChanged(nameof(NoRecentDashboardArchivesVisibility));
        }

        OnPropertyChanged(nameof(ArchiveEmptyStateVisibility));
        OnPropertyChanged(nameof(ArchiveWorkspaceVisibility));
        OnPropertyChanged(nameof(TrashButtonLabel));

        if (!string.IsNullOrEmpty(selectedId))
        {
            var foundVer = ArchiveVersions.FirstOrDefault(x => x.Id == selectedId);
            if (foundVer is not null)
            {
                await LoadVersionsForFileGroupAsync(foundVer.Record, foundVer.Id);
                return;
            }
        }

        if (!string.IsNullOrEmpty(selectedGroupKey))
        {
            var matchGrp = ArchiveFileGroups.FirstOrDefault(g => string.Equals(g.GroupKey, selectedGroupKey, StringComparison.OrdinalIgnoreCase));
            if (matchGrp is not null)
            {
                SelectedFileGroup = matchGrp;
                return;
            }
        }

        if (ArchiveFileGroups.Count > 0)
        {
            SelectedFileGroup = ArchiveFileGroups[0];
        }
        else
        {
            ClearSelectedGroupState();
        }
    }

    private void ClearSelectedGroupState()
    {
        SelectedFileVersions.Clear();
        OnPropertyChanged(nameof(HasSelectedFileVersions));
        OnPropertyChanged(nameof(CanCompareVersions));
        OnPropertyChanged(nameof(CompareButtonTooltipText));
        ClearSelectedVersionState();
    }

    private void ClearSelectedVersionState()
    {
        if (SetProperty(ref _selectedVersion, null, nameof(SelectedVersion)))
        {
            OnPropertyChanged(nameof(SelectedVersionIsCloudOnly));
            OnPropertyChanged(nameof(CanRunSelectedVersion));
            OnPropertyChanged(nameof(SelectedVersionHeaderText));
            OnPropertyChanged(nameof(HasSelectedVersion));
            RunCodeCommand.NotifyCanExecuteChanged();
            RunOrStopCodeCommand.NotifyCanExecuteChanged();
        }
        SelectedVersionSource = string.Empty;
        if (IsDiffModeActive)
            IsDiffModeActive = false;
    }

    public void EnterDiffModeForCurrentSelection(ArchiveVersionRow? preferredTarget = null)
    {
        if (SelectedFileVersions.Count < 2)
        {
            ShowInfoBar(
                _config.Language == UiLanguage.Vi ? "Chưa đủ phiên bản" : "Insufficient Versions",
                CompareButtonTooltipText,
                InfoBarSeverity.Informational);
            return;
        }

        var orderedAsc = SelectedFileVersions.OrderBy(v => v.VersionNum).ThenBy(v => v.Record.CreatedAtUnixMs).ToList();
        var target = preferredTarget ?? SelectedVersion ?? orderedAsc[^1];
        int targetIdx = orderedAsc.FindIndex(v => v.Id == target.Id);
        var baseline = targetIdx > 0
            ? orderedAsc[targetIdx - 1]
            : orderedAsc.FirstOrDefault(v => v.Id != target.Id) ?? target;

        _compareWithVersion = baseline;
        _diffTargetVersion = target;
        OnPropertyChanged(nameof(CompareWithVersion));
        OnPropertyChanged(nameof(DiffTargetVersion));
        IsDiffModeActive = true;
    }

    private void ToggleDiffMode()
    {
        if (!IsDiffModeActive)
            EnterDiffModeForCurrentSelection(SelectedVersion);
        else
            IsDiffModeActive = false;
    }

    public void ExitDiffMode() => IsDiffModeActive = false;

    private void ReleaseDiffBuffers()
    {
        DiffSideBySideLines.Clear();
        DiffUnifiedLines.Clear();
        _sideBySideHunks = Array.Empty<int>();
        _unifiedHunks = Array.Empty<int>();
        _currentHunkCursor = -1;
        DiffSummaryText = string.Empty;
        DiffUnifiedText = string.Empty;
        DiffNoticeText = string.Empty;
    }

    public void JumpDiffChange(int delta)
    {
        var hunks = DiffUseUnifiedMode ? _unifiedHunks : _sideBySideHunks;
        if (hunks.Count == 0)
            return;

        if (_currentHunkCursor < 0)
            _currentHunkCursor = delta >= 0 ? 0 : hunks.Count - 1;
        else
            _currentHunkCursor = (_currentHunkCursor + delta + hunks.Count) % hunks.Count;

        DiffScrollRequested?.Invoke(hunks[_currentHunkCursor], DiffUseUnifiedMode);
    }

    private async Task RecomputeDiffAsync()
    {
        _diffRecomputeCts?.Cancel();
        _diffRecomputeCts?.Dispose();
        var cts = new CancellationTokenSource();
        _diffRecomputeCts = cts;
        var ct = cts.Token;

        DiffSideBySideLines.Clear();
        DiffUnifiedLines.Clear();
        _sideBySideHunks = Array.Empty<int>();
        _unifiedHunks = Array.Empty<int>();
        _currentHunkCursor = -1;
        DiffNoticeText = string.Empty;

        bool vi = _config.Language == UiLanguage.Vi;
        var titleStem = SelectedFileGroup?.CompactTitle ?? SelectedVersion?.FileName ?? "GhostCode";
        DiffHeaderTitle = vi ? $"{titleStem} · So sánh mã nguồn" : $"{titleStem} · Code Diff";

        if (SelectedFileVersions.Count < 2)
        {
            DiffVersionPairText = string.Empty;
            DiffAddedCountText = "+0";
            DiffRemovedCountText = "−0";
            DiffModifiedCountText = "~0";
            DiffNoticeText = vi ? "Cần ít nhất 2 phiên bản để so sánh." : "At least 2 versions are required to compare.";
            return;
        }

        var baseline = CompareWithVersion;
        var target = DiffTargetVersion ?? SelectedVersion;
        if (baseline is null || target is null)
        {
            DiffNoticeText = vi ? "Hãy chọn phiên bản nguồn và phiên bản đích." : "Select both baseline and target versions.";
            return;
        }

        DiffVersionPairText = $"{baseline.VersionBadge} → {target.VersionBadge}";

        if (string.Equals(baseline.Id, target.Id, StringComparison.OrdinalIgnoreCase))
        {
            DiffAddedCountText = vi ? "+0 thêm" : "+0 added";
            DiffRemovedCountText = vi ? "−0 xóa" : "−0 removed";
            DiffModifiedCountText = vi ? "~0 sửa" : "~0 modified";
            DiffNoticeText = vi ? "Cả hai phía đang chọn cùng một phiên bản." : "Both selectors point to the exact same version.";
            DiffUnifiedText = string.Empty;
            DiffSummaryText = DiffNoticeText;
            return;
        }

        if (!baseline.IsLocalAvailable || !target.IsLocalAvailable)
        {
            DiffNoticeText = vi
                ? "Hãy tải về cục bộ cả hai phiên bản trước khi so sánh."
                : "Download both cloud-only versions locally before comparing.";
            DiffSummaryText = DiffNoticeText;
            return;
        }

        var leftBytes = await _archiver.ReadAndVerifyVersionBytesAsync(baseline.Id, ct);
        var rightBytes = await _archiver.ReadAndVerifyVersionBytesAsync(target.Id, ct);
        if (ct.IsCancellationRequested)
            return;

        var (leftText, _) = SourceAnalyzer.DecodePreservingInfo(leftBytes);
        var (rightText, _) = SourceAnalyzer.DecodePreservingInfo(rightBytes);

        var diff = SourceDiffEngine.ComputeDiff(
            leftText,
            rightText,
            $"{baseline.FileName} ({baseline.VersionBadge})",
            $"{target.FileName} ({target.VersionBadge})");

        if (ct.IsCancellationRequested)
            return;

        DiffSideBySideLines.Clear();
        DiffUnifiedLines.Clear();
        foreach (var line in diff.SideBySideLines)
            DiffSideBySideLines.Add(new DiffLineRow(line));
        foreach (var uLine in diff.UnifiedLineRows)
            DiffUnifiedLines.Add(new UnifiedDiffLineRow(uLine));

        _sideBySideHunks = diff.SideBySideHunkIndices;
        _unifiedHunks = diff.UnifiedHunkIndices;

        DiffAddedCountText = vi ? $"+{diff.AddedLines} thêm" : $"+{diff.AddedLines} added";
        DiffRemovedCountText = vi ? $"−{diff.RemovedLines} xóa" : $"−{diff.RemovedLines} removed";
        DiffModifiedCountText = vi ? $"~{diff.ModifiedLines} sửa" : $"~{diff.ModifiedLines} modified";
        DiffSummaryText = $"{DiffAddedCountText}  {DiffRemovedCountText}  {DiffModifiedCountText} ({DiffVersionPairText})";
        DiffUnifiedText = diff.UnifiedDiffText;

        if (!diff.HasChanges)
        {
            DiffNoticeText = vi
                ? "Hai phiên bản không có thay đổi về nội dung."
                : "No content differences between the selected versions.";
        }
    }

    public async Task<bool> DeleteVersionToTrashAsync(
        ArchiveVersionRow? target,
        bool deleteRemote = false,
        bool showUndoToast = true)
    {
        if (target is null)
            return false;

        bool vi = _config.Language == UiLanguage.Vi;
        var res = await _store.MoveVersionToTrashAsync(
            target.Id,
            _config.GetInternalTrashRoot(),
            _config.ArchiveDir,
            deleteRemote: deleteRemote,
            retentionDays: _config.TrashRetentionDays);

        if (!res.Success)
            return false;

        _lastUndoBatchId = res.BatchId;
        _lastUndoVersionId = target.Id;
        _lastUndoWasProblem = false;

        await RefreshArchiveAsync();

        if (showUndoToast)
        {
            ShowInfoBar(
                vi ? "Đã chuyển vào Thùng rác" : "Moved to Trash",
                vi ? $"Đã xóa phiên bản {target.VersionBadge}." : $"Deleted version {target.VersionBadge}.",
                InfoBarSeverity.Informational,
                actionLabel: vi ? "Hoàn tác" : "Undo",
                actionKey: "undo_trash");
        }
        return true;
    }

    public async Task<bool> DeleteProblemToTrashAsync(
        ArchiveFileGroupRow? group,
        bool deleteRemote = false,
        bool showUndoToast = true)
    {
        if (group is null)
            return false;

        bool vi = _config.Language == UiLanguage.Vi;
        var matchingVersions = !string.IsNullOrWhiteSpace(group.Latest.ProblemCode)
            ? await _store.GetVersionsByProblemAsync(group.Latest.Platform, group.Latest.ProblemCode)
            : await _store.GetOfflineVersionsBySourceAsync(group.Latest.SourcePath);

        if (matchingVersions.Count == 0)
            return false;

        var problemKey = !string.IsNullOrWhiteSpace(group.Latest.ProblemCode)
            ? ProblemStatementRecord.BuildKey(group.Latest.Platform, group.Latest.ProblemCode)
            : null;

        var res = await _store.MoveProblemToTrashAsync(
            matchingVersions.Select(v => v.Id).ToList(),
            problemKey,
            _config.GetInternalTrashRoot(),
            _config.ArchiveDir,
            deleteRemote: deleteRemote,
            retentionDays: _config.TrashRetentionDays);

        if (!res.Success)
            return false;

        _lastUndoBatchId = res.BatchId;
        _lastUndoVersionId = null;
        _lastUndoWasProblem = true;

        await RefreshArchiveAsync();

        if (showUndoToast)
        {
            ShowInfoBar(
                vi ? "Đã chuyển bài vào Thùng rác" : "Problem Moved to Trash",
                vi ? $"Đã xóa {group.CompactTitle} ({res.MovedCount} phiên bản)." : $"Deleted {group.CompactTitle} ({res.MovedCount} versions).",
                InfoBarSeverity.Informational,
                actionLabel: vi ? "Hoàn tác" : "Undo",
                actionKey: "undo_trash");
        }
        return true;
    }

    public async Task<int> GetProblemGroupVersionCountAsync(ArchiveFileGroupRow? group)
    {
        if (group is null) return 0;
        var list = !string.IsNullOrWhiteSpace(group.Latest.ProblemCode)
            ? await _store.GetVersionsByProblemAsync(group.Latest.Platform, group.Latest.ProblemCode)
            : await _store.GetOfflineVersionsBySourceAsync(group.Latest.SourcePath);
        return list.Count;
    }

    public async Task UndoLastTrashAsync()
    {
        bool vi = _config.Language == UiLanguage.Vi;
        int restored = 0;
        if (!string.IsNullOrWhiteSpace(_lastUndoBatchId))
            restored = await _store.RestoreTrashBatchAsync(_lastUndoBatchId);
        else if (!string.IsNullOrWhiteSpace(_lastUndoVersionId))
            restored = await _store.RestoreTrashItemAsync(_lastUndoVersionId) ? 1 : 0;

        var restoreTargetId = _lastUndoVersionId;
        _lastUndoBatchId = null;
        _lastUndoVersionId = null;

        await RefreshArchiveAsync();
        if (!string.IsNullOrWhiteSpace(restoreTargetId))
            await SelectArchiveVersionByIdAsync(restoreTargetId);

        if (restored > 0)
        {
            ShowInfoBar(
                vi ? "Đã hoàn tác" : "Restored",
                _lastUndoWasProblem
                    ? (vi ? $"Đã khôi phục {restored} phiên bản từ Thùng rác." : $"Restored {restored} version(s) from Trash.")
                    : (vi ? "Đã khôi phục phiên bản vừa xóa." : "Restored deleted version."),
                InfoBarSeverity.Success);
        }
    }

    public async Task RefreshTrashAsync()
    {
        await _store.PurgeExpiredTrashAsync(_config.TrashRetentionDays);
        var items = await _store.GetTrashItemsAsync();
        TrashItems.Clear();
        foreach (var item in items)
            TrashItems.Add(new TrashedArchiveVersionRow(item, _config.Language));
        TrashCount = items.Count;
    }

    public async Task RestoreTrashRowAsync(TrashedArchiveVersionRow? row)
    {
        if (row is null) return;
        await _store.RestoreTrashItemAsync(row.Id);
        await RefreshTrashAsync();
        await RefreshArchiveAsync();
    }

    public async Task PurgeTrashRowAsync(TrashedArchiveVersionRow? row, bool alsoRequestRemoteDelete = false)
    {
        if (row is null) return;
        await _store.PermanentlyDeleteTrashVersionAsync(row.Id, alsoRequestRemoteDelete);
        await RefreshTrashAsync();
        await RefreshArchiveAsync();
    }

    public async Task EmptyTrashAsync(bool alsoRequestRemoteDelete = false)
    {
        await _store.EmptyTrashAsync(alsoRequestRemoteDelete);
        await RefreshTrashAsync();
        await RefreshArchiveAsync();
    }

    public void CopyMetadataValue(string? value, string label)
    {
        if (!string.IsNullOrWhiteSpace(value))
            TryCopyRawTextToClipboard(value, $"{label}: {value}");
    }
}
