using GhostCode.App.ViewModels;
using GhostCode.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI.Core;
using WinRT.Interop;

namespace GhostCode.App.Views;

public sealed partial class ArchivePage : Page
{
    public ArchivePage()
    {
        ViewModel = App.Services.GetRequiredService<MainViewModel>();
        InitializeComponent();
        UpdateTabButtonStyles();
        UpdateDiffModeButtonStyles();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.DiffScrollRequested += OnDiffScrollRequested;
        Unloaded += OnPageUnloaded;
    }

    public MainViewModel ViewModel { get; }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.DiffScrollRequested -= OnDiffScrollRequested;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.RunnerTabIndex))
            UpdateTabButtonStyles();
        else if (e.PropertyName is nameof(MainViewModel.DiffUseUnifiedMode) or nameof(MainViewModel.IsDiffModeActive))
        {
            UpdateDiffModeButtonStyles();
            if (e.PropertyName == nameof(MainViewModel.IsDiffModeActive))
            {
                RunnerColDef.Width = ViewModel.IsDiffModeActive
                    ? new GridLength(0, GridUnitType.Pixel)
                    : new GridLength(1, GridUnitType.Star);
                if (ViewModel.IsDiffModeActive)
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        DiffBaseCombo.SelectedItem = ViewModel.CompareWithVersion;
                        DiffTargetCombo.SelectedItem = ViewModel.DiffTargetVersion;
                    });
                }
            }
        }
    }

    private void OnDiffScrollRequested(int rowIndex, bool unified)
    {
        if (unified && rowIndex >= 0 && rowIndex < ViewModel.DiffUnifiedLines.Count)
        {
            DiffUnifiedListView.ScrollIntoView(ViewModel.DiffUnifiedLines[rowIndex], ScrollIntoViewAlignment.Leading);
        }
        else if (!unified && rowIndex >= 0 && rowIndex < ViewModel.DiffSideBySideLines.Count)
        {
            DiffSideBySideListView.ScrollIntoView(ViewModel.DiffSideBySideLines[rowIndex], ScrollIntoViewAlignment.Leading);
        }
    }

    private static bool IsShiftHeldOnCurrentThread()
    {
        var state = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
        return (state & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
    }

    private void SetupFoldersFromArchive_Click(object sender, RoutedEventArgs e) =>
        ViewModel.RequestNavigate("Settings");

    private void ArchiveSearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args) =>
        _ = ViewModel.ApplyArchiveFilterFromCacheAsync();

    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
            _ = ViewModel.ApplyArchiveFilterFromCacheAsync();
    }

    private void SourceViewerPane_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Keep the action toolbar strictly horizontal on 1 single row:
        // progressively move secondary actions into the overflow menu rather than clipping or wrapping.
        double w = e.NewSize.Width;
        bool hideRestore = !ViewModel.IsDiffModeActive && w > 0 && w < 520;
        bool hideIde = !ViewModel.IsDiffModeActive && w > 0 && w < 440;
        bool hideCompare = !ViewModel.IsDiffModeActive && w > 0 && w < 350;

        RestoreVersionBtn.Visibility = hideRestore ? Visibility.Collapsed : Visibility.Visible;
        OpenIdeBtn.Visibility = hideIde ? Visibility.Collapsed : Visibility.Visible;
        CompareDiffBtn.Visibility = hideCompare ? Visibility.Collapsed : Visibility.Visible;
        ToolbarOverflowBtn.Visibility = (hideRestore || hideIde || hideCompare) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ConsoleTab_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.RunnerTabIndex = 0;
        UpdateTabButtonStyles();
    }

    private void IoTab_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.RunnerTabIndex = 1;
        UpdateTabButtonStyles();
    }

    private void SamplesTab_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.RunnerTabIndex = 2;
        UpdateTabButtonStyles();
    }

    private void UpdateTabButtonStyles()
    {
        var activeBg = Application.Current.Resources.TryGetValue("CardBackgroundFillColorSecondaryBrush", out var bg) ? bg as Brush : null;
        var activeBorder = Application.Current.Resources.TryGetValue("AccentFillColorDefaultBrush", out var br) ? br as Brush : null;
        var transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

        void Apply(Button btn, bool active)
        {
            btn.Background = active ? (activeBg ?? transparent) : transparent;
            btn.BorderBrush = active ? (activeBorder ?? transparent) : transparent;
            btn.BorderThickness = active ? new Thickness(0, 0, 0, 2) : new Thickness(0);
            btn.FontWeight = active ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
        }

        Apply(ConsoleTabBtn, ViewModel.RunnerTabIndex == 0);
        Apply(IoTabBtn, ViewModel.RunnerTabIndex == 1);
        Apply(SamplesTabBtn, ViewModel.RunnerTabIndex == 2);
    }

    private void UpdateDiffModeButtonStyles()
    {
        var activeBg = Application.Current.Resources.TryGetValue("CardBackgroundFillColorDefaultBrush", out var bg) ? bg as Brush : null;
        var activeBorder = Application.Current.Resources.TryGetValue("AccentFillColorDefaultBrush", out var br) ? br as Brush : null;
        var transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

        bool unified = ViewModel.DiffUseUnifiedMode;
        DiffSideBySideBtn.Background = !unified ? (activeBg ?? transparent) : transparent;
        DiffSideBySideBtn.BorderBrush = !unified ? (activeBorder ?? transparent) : transparent;
        DiffSideBySideBtn.FontWeight = !unified ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;

        DiffUnifiedBtn.Background = unified ? (activeBg ?? transparent) : transparent;
        DiffUnifiedBtn.BorderBrush = unified ? (activeBorder ?? transparent) : transparent;
        DiffUnifiedBtn.FontWeight = unified ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
    }

    private void CompareButton_Click(object sender, RoutedEventArgs e) =>
        ViewModel.EnterDiffModeForCurrentSelection(ViewModel.SelectedVersion);

    private void DiffModeSideBySide_Click(object sender, RoutedEventArgs e) =>
        ViewModel.DiffUseUnifiedMode = false;

    private void DiffModeUnified_Click(object sender, RoutedEventArgs e) =>
        ViewModel.DiffUseUnifiedMode = true;

    private void PrevDiffChange_Click(object sender, RoutedEventArgs e) =>
        ViewModel.JumpDiffChange(-1);

    private void NextDiffChange_Click(object sender, RoutedEventArgs e) =>
        ViewModel.JumpDiffChange(+1);

    private void CloseDiff_Click(object sender, RoutedEventArgs e) =>
        ViewModel.ExitDiffMode();

    private void VersionMenuView_Click(object sender, RoutedEventArgs e) =>
        ViewModel.ExitDiffMode();

    private ArchiveFileGroupRow? ResolveGroupFromMenu(object sender)
    {
        if (sender is FrameworkElement fe && fe.Tag is string key)
            return ViewModel.ArchiveFileGroups.FirstOrDefault(g => string.Equals(g.GroupKey, key, StringComparison.OrdinalIgnoreCase));
        return ViewModel.SelectedFileGroup;
    }

    private void GroupContextOpen_Click(object sender, RoutedEventArgs e)
    {
        var g = ResolveGroupFromMenu(sender);
        if (g is not null)
        {
            ViewModel.SelectedFileGroup = g;
            ViewModel.ExitDiffMode();
        }
    }

    private void GroupContextCopyLatest_Click(object sender, RoutedEventArgs e)
    {
        var g = ResolveGroupFromMenu(sender);
        if (g is not null)
            _ = ViewModel.CopyRecentArchiveGroupCodeAsync(g);
    }

    private void GroupContextCompare_Click(object sender, RoutedEventArgs e)
    {
        var g = ResolveGroupFromMenu(sender);
        if (g is not null)
        {
            ViewModel.SelectedFileGroup = g;
            ViewModel.EnterDiffModeForCurrentSelection(null);
        }
    }

    private async void GroupContextDetails_Click(object sender, RoutedEventArgs e)
    {
        var g = ResolveGroupFromMenu(sender);
        if (g is not null)
        {
            ViewModel.SelectedFileGroup = g;
            await ShowVersionDetailsDialogAsync(ViewModel.SelectedVersion ?? new ArchiveVersionRow(g.Latest, ViewModel.Config.Language));
        }
    }

    private async void GroupContextDelete_Click(object sender, RoutedEventArgs e)
    {
        var g = ResolveGroupFromMenu(sender);
        if (g is null) return;

        if (IsShiftHeldOnCurrentThread())
        {
            await ViewModel.DeleteProblemToTrashAsync(g, deleteRemote: false, showUndoToast: true);
            return;
        }
        await ConfirmAndDeleteProblemAsync(g);
    }

    private async void DeleteSelectedVersion_Click(object sender, RoutedEventArgs e)
    {
        var target = ViewModel.SelectedVersion;
        if (target is null) return;

        if (IsShiftHeldOnCurrentThread())
        {
            await ViewModel.DeleteVersionToTrashAsync(target, deleteRemote: false, showUndoToast: true);
            return;
        }
        await ConfirmAndDeleteVersionAsync(target);
    }

    internal ContentDialog CreateDeleteVersionDialog(ArchiveVersionRow target, out CheckBox? deleteRemoteCheck)
    {
        bool vi = ViewModel.Config.Language == UiLanguage.Vi;
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = vi
                ? "Phiên bản này sẽ được chuyển vào Thùng rác GhostCode. Bạn có thể khôi phục lại sau."
                : "This version will move to GhostCode Trash. You can restore it later.",
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(new TextBlock
        {
            Text = vi
                ? $"Tệp làm việc gốc trên máy ({Path.GetFileName(target.SourcePath)}) sẽ không bị xóa."
                : $"Your original working file ({Path.GetFileName(target.SourcePath)}) will remain untouched.",
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap
        });

        deleteRemoteCheck = null;
        if (!string.IsNullOrWhiteSpace(target.DriveFileId))
        {
            deleteRemoteCheck = new CheckBox
            {
                Content = vi ? "Xóa luôn bản sao trên đám mây (Google Drive / OneDrive)" : "Also delete remote copy from cloud storage",
                IsChecked = false
            };
            panel.Children.Add(deleteRemoteCheck);
        }

        return new ContentDialog
        {
            Title = vi ? $"Xóa phiên bản {target.VersionBadge}?" : $"Delete version {target.VersionBadge}?",
            Content = panel,
            PrimaryButtonText = vi ? "Xóa" : "Delete",
            CloseButtonText = vi ? "Hủy" : "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
    }

    private async Task ConfirmAndDeleteVersionAsync(ArchiveVersionRow target)
    {
        var dialog = CreateDeleteVersionDialog(target, out var remoteCheck);
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
            await ViewModel.DeleteVersionToTrashAsync(target, deleteRemote: remoteCheck?.IsChecked == true, showUndoToast: true);
    }

    internal async Task<ContentDialog> CreateDeleteProblemDialogAsync(ArchiveFileGroupRow group, Action<CheckBox?> bindCheck)
    {
        bool vi = ViewModel.Config.Language == UiLanguage.Vi;
        int count = await ViewModel.GetProblemGroupVersionCountAsync(group);
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = vi
                ? $"Toàn bộ {count} phiên bản thuộc bài này sẽ được chuyển vào Thùng rác GhostCode."
                : $"All {count} archived version(s) of this problem will move to GhostCode Trash.",
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(new TextBlock
        {
            Text = vi
                ? $"Tệp mã nguồn gốc trên máy ({Path.GetFileName(group.SourcePath)}) không thuộc quyền xóa của GhostCode và sẽ được giữ nguyên."
                : $"Original file ({Path.GetFileName(group.SourcePath)}) is not owned by GhostCode and will NOT be deleted.",
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap
        });

        CheckBox? remoteCheck = null;
        if (!string.IsNullOrWhiteSpace(group.Latest.DriveFileId))
        {
            remoteCheck = new CheckBox
            {
                Content = vi ? "Xóa luôn các bản sao trên đám mây (nếu có)" : "Also delete synced remote copies",
                IsChecked = false
            };
            panel.Children.Add(remoteCheck);
        }
        bindCheck(remoteCheck);

        return new ContentDialog
        {
            Title = vi ? $"Xóa bài {group.CompactTitle}?" : $"Delete archive {group.CompactTitle}?",
            Content = panel,
            PrimaryButtonText = vi ? "Xóa" : "Delete",
            CloseButtonText = vi ? "Hủy" : "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
    }

    private async Task ConfirmAndDeleteProblemAsync(ArchiveFileGroupRow group)
    {
        CheckBox? remoteCheck = null;
        var dialog = await CreateDeleteProblemDialogAsync(group, cb => remoteCheck = cb);
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
            await ViewModel.DeleteProblemToTrashAsync(group, deleteRemote: remoteCheck?.IsChecked == true, showUndoToast: true);
    }

    private async void VersionDetails_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedVersion is not null)
            await ShowVersionDetailsDialogAsync(ViewModel.SelectedVersion);
    }

    private async Task ShowVersionDetailsDialogAsync(ArchiveVersionRow row)
    {
        bool vi = ViewModel.Config.Language == UiLanguage.Vi;
        var rootPanel = new StackPanel { Spacing = 8, MinWidth = 440 };

        void AddFieldRow(string label, string? value, bool allowCopy = true)
        {
            var box = new Grid { ColumnSpacing = 8 };
            box.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(135) });
            box.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            box.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var lbl = new TextBlock
            {
                Text = label,
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                VerticalAlignment = VerticalAlignment.Center
            };
            var val = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(value) ? "-" : value,
                FontSize = 12,
                FontFamily = new FontFamily("Consolas"),
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(lbl, 0);
            Grid.SetColumn(val, 1);
            box.Children.Add(lbl);
            box.Children.Add(val);

            if (allowCopy && !string.IsNullOrWhiteSpace(value))
            {
                var copyBtn = new Button
                {
                    Padding = new Thickness(8, 3, 8, 3),
                    FontSize = 11,
                    Content = vi ? "Chép" : "Copy"
                };
                copyBtn.Click += (_, _) => ViewModel.CopyMetadataValue(value, label);
                Grid.SetColumn(copyBtn, 2);
                box.Children.Add(copyBtn);
            }
            rootPanel.Children.Add(box);
        }

        AddFieldRow(vi ? "Tên tệp lưu trữ" : "Archive Filename", row.FileName, allowCopy: true);
        AddFieldRow(vi ? "Đường dẫn gốc" : "Source Path", row.SourcePath, allowCopy: true);
        AddFieldRow(vi ? "Tệp lưu lịch sử" : "Archive Path", row.ArchivePath, allowCopy: true);
        AddFieldRow("SHA-256", row.Sha256Full, allowCopy: true);
        AddFieldRow(vi ? "Dung lượng" : "File Size", $"{row.SizeText} ({row.Record.SizeBytes} B)", allowCopy: false);
        AddFieldRow(vi ? "Thời gian" : "Timestamp", row.TimestampText, allowCopy: false);
        AddFieldRow(vi ? "Mã phiên bản" : "Version ID", row.Id, allowCopy: true);
        AddFieldRow(vi ? "Bài tập" : "Problem", $"{row.Platform} · {row.Record.ProblemCode ?? "-"} {row.Record.ProblemTitle}".Trim(), allowCopy: false);
        AddFieldRow(vi ? "Trạng thái" : "Sync State", $"{row.AvailabilityBadge} ({row.StatusTooltip})", allowCopy: false);

        var dialog = new ContentDialog
        {
            Title = vi ? $"Chi tiết phiên bản {row.VersionBadge}" : $"Version {row.VersionBadge} Details",
            Content = new ScrollViewer { MaxHeight = 420, Content = rootPanel },
            CloseButtonText = vi ? "Đóng" : "Close",
            XamlRoot = XamlRoot
        };
        await dialog.ShowAsync();
    }

    private async void OpenTrashDialog_Click(object sender, RoutedEventArgs e)
    {
        var dialog = await CreateTrashDialogAsync();
        await dialog.ShowAsync();
    }

    internal async Task<ContentDialog> CreateTrashDialogAsync()
    {
        await ViewModel.RefreshTrashAsync();
        bool vi = ViewModel.Config.Language == UiLanguage.Vi;

        var container = new StackPanel { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Stretch };
        container.Children.Add(new TextBlock
        {
            Text = vi
                ? $"Các phiên bản đã xóa được lưu tạm {ViewModel.Config.TrashRetentionDays} ngày. Bạn có thể khôi phục nguyên trạng hoặc xóa vĩnh viễn."
                : $"Deleted versions stay recoverable for {ViewModel.Config.TrashRetentionDays} days before automatic cleanup.",
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap
        });

        var emptyLabel = new TextBlock
        {
            Text = vi ? "Thùng rác hiện đang trống." : "GhostCode Trash is currently empty.",
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 16, 0, 16),
            Visibility = ViewModel.TrashItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed
        };

        var itemsListPanel = new StackPanel { Spacing = 6 };
        void RebuildRows()
        {
            itemsListPanel.Children.Clear();
            emptyLabel.Visibility = ViewModel.TrashItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            foreach (var item in ViewModel.TrashItems.ToList())
            {
                var card = new Border
                {
                    Padding = new Thickness(10, 8, 10, 8),
                    CornerRadius = new CornerRadius(6),
                    BorderThickness = new Thickness(1),
                    BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                    Background = (Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"]
                };
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var textCol = new StackPanel { Spacing = 2 };
                textCol.Children.Add(new TextBlock { Text = item.Title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 13 });
                textCol.Children.Add(new TextBlock
                {
                    Text = item.Subtitle,
                    FontSize = 11,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                    TextTrimming = TextTrimming.CharacterEllipsis
                });

                var restoreBtn = new Button { Content = vi ? "Khôi phục" : "Restore", FontSize = 11, Padding = new Thickness(8, 4, 8, 4) };
                restoreBtn.Click += async (_, _) =>
                {
                    await ViewModel.RestoreTrashRowAsync(item);
                    RebuildRows();
                };

                var purgeBtn = new Button { Content = vi ? "Xóa hẳn" : "Purge", FontSize = 11, Padding = new Thickness(8, 4, 8, 4) };
                purgeBtn.Click += async (_, _) =>
                {
                    await ViewModel.PurgeTrashRowAsync(item, alsoRequestRemoteDelete: false);
                    RebuildRows();
                };

                Grid.SetColumn(textCol, 0);
                Grid.SetColumn(restoreBtn, 1);
                Grid.SetColumn(purgeBtn, 2);
                row.Children.Add(textCol);
                row.Children.Add(restoreBtn);
                row.Children.Add(purgeBtn);
                card.Child = row;
                itemsListPanel.Children.Add(card);
            }
        }

        RebuildRows();
        container.Children.Add(emptyLabel);
        container.Children.Add(new ScrollViewer { MaxHeight = 320, Content = itemsListPanel });

        var dialog = new ContentDialog
        {
            Title = vi ? $"Thùng rác GhostCode ({ViewModel.TrashItems.Count})" : $"GhostCode Trash ({ViewModel.TrashItems.Count})",
            Content = container,
            PrimaryButtonText = ViewModel.TrashItems.Count > 0 ? (vi ? "Dọn sạch thùng rác" : "Empty Trash") : string.Empty,
            CloseButtonText = vi ? "Đóng" : "Close",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };

        dialog.PrimaryButtonClick += async (s, args) =>
        {
            if (ViewModel.TrashItems.Count == 0)
                return;
            args.Cancel = true;
            await ViewModel.EmptyTrashAsync(alsoRequestRemoteDelete: false);
            dialog.Hide();
        };

        return dialog;
    }

    private async void AddCustomSample_Click(object sender, RoutedEventArgs e)
    {
        bool vi = ViewModel.Config.Language == UiLanguage.Vi;
        var inputBox = new TextBox
        {
            Header = vi ? "Dữ liệu đầu vào (STDIN)" : "Custom Sample Input",
            AcceptsReturn = true,
            Height = 90,
            FontFamily = new FontFamily("Consolas")
        };
        var expectedBox = new TextBox
        {
            Header = vi ? "Kết quả kỳ vọng" : "Expected Output",
            AcceptsReturn = true,
            Height = 90,
            FontFamily = new FontFamily("Consolas")
        };
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(inputBox);
        panel.Children.Add(expectedBox);

        var dialog = new ContentDialog
        {
            Title = vi ? "Thêm test mẫu riêng" : "Add Custom Sample Test Case",
            Content = panel,
            PrimaryButtonText = vi ? "Lưu test" : "Save Test",
            CloseButtonText = vi ? "Hủy" : "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
            await ViewModel.AddCustomSampleAsync(inputBox.Text, expectedBox.Text);
    }

    private void IoSplitter_ManipulationDelta(object sender, ManipulationDeltaRoutedEventArgs e)
    {
        var totalH = IoSplitContainer.ActualHeight;
        if (totalH <= 180)
            return;

        var currentStdinH = StdinRowDef.ActualHeight;
        var nextStdinH = Math.Clamp(currentStdinH + e.Delta.Translation.Y, 80, totalH - 100);
        var nextOutputH = Math.Max(90, totalH - 10 - nextStdinH);
        StdinRowDef.Height = new GridLength(nextStdinH, GridUnitType.Pixel);
        OutputRowDef.Height = new GridLength(nextOutputH, GridUnitType.Pixel);
    }

    private async void RestoreVersion_Click(object sender, RoutedEventArgs e)
    {
        bool vi = ViewModel.Config.Language == UiLanguage.Vi;
        if (ViewModel.SelectedVersion is null)
        {
            ViewModel.ShowInfoBar(vi ? "Chưa chọn phiên bản" : "Select Version", ViewModel.Strings.NoVersionSelected, InfoBarSeverity.Informational);
            return;
        }

        var ext = Path.GetExtension(ViewModel.SelectedVersion.FileName);
        if (string.IsNullOrEmpty(ext)) ext = ".cpp";

        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = Path.GetFileNameWithoutExtension(ViewModel.SelectedVersion.FileName) + "_restored"
        };
        picker.FileTypeChoices.Add(vi ? "Tệp mã nguồn" : "Source File", new List<string> { ext });
        InitializeWithWindow.Initialize(picker, App.MainWindowHandle);

        var file = await picker.PickSaveFileAsync();
        if (file is not null)
            await ViewModel.RestoreSelectedVersionAsync(file.Path);
    }
}
