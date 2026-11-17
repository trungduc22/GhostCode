using GhostCode.App.Interop;
using GhostCode.App.ViewModels;
using GhostCode.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GhostCode.App.Views;

public sealed partial class DashboardPage : Page
{
    public DashboardPage()
    {
        ViewModel = App.Services.GetRequiredService<MainViewModel>();
        InitializeComponent();
    }

    public MainViewModel ViewModel { get; }

    private async void AutoDetectFolders_Click(object sender, RoutedEventArgs e)
    {
        var candidates = ViewModel.DiscoverCandidateFolders();
        bool isVi = ViewModel.Config.Language == UiLanguage.Vi;
        if (candidates.Count == 0)
        {
            ViewModel.ShowInfoBar(
                isVi ? "Không tìm thấy thư mục mới" : "No New Folders Found",
                isVi ? "Tất cả thư mục Desktop / Documents hiện có đều đã được thêm hoặc không tồn tại." : "All available Desktop / Documents folders are already added or missing.",
                InfoBarSeverity.Informational);
            return;
        }

        var checkBoxes = new List<(CheckBox Box, WatchedFolderConfig Folder)>();
        var listPanel = new StackPanel { Spacing = 8 };
        listPanel.Children.Add(new TextBlock
        {
            Text = isVi
                ? "GhostCode tìm thấy các thư mục mã nguồn sau. Hãy chọn thư mục bạn muốn bảo vệ:"
                : "GhostCode found the following folders. Select which ones to protect:",
            TextWrapping = TextWrapping.Wrap
        });

        foreach (var c in candidates)
        {
            var cb = new CheckBox
            {
                Content = $"{c.Label} — {c.Path}",
                IsChecked = true
            };
            checkBoxes.Add((cb, c));
            listPanel.Children.Add(cb);
        }

        var dialog = new ContentDialog
        {
            Title = ViewModel.ExpStrings.OnboardingAutoDetectButton,
            Content = listPanel,
            PrimaryButtonText = isVi ? "Xác nhận theo dõi" : "Confirm & Watch",
            CloseButtonText = isVi ? "Hủy" : "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            var chosen = checkBoxes.Where(x => x.Box.IsChecked == true).Select(x => x.Folder).ToList();
            if (chosen.Count > 0)
                await ViewModel.AddWatchedFoldersAndReconcileAsync(chosen);
        }
    }

    private async void PickFolderOnboarding_Click(object sender, RoutedEventArgs e)
    {
        var path = await FilePickerHelpers.PickSingleFolderAsync(App.MainWindowHandle);
        if (!string.IsNullOrWhiteSpace(path))
            ViewModel.AddWatchedFolder(path);
    }

    private async void ConfigureBrowserExtension_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string kindKey })
            await ViewModel.OpenBrowserSetupCommand.ExecuteAsync(kindKey);
    }
}
