using GhostCode.App.Interop;
using GhostCode.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GhostCode.App.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        ViewModel = App.Services.GetRequiredService<MainViewModel>();
        InitializeComponent();
        Loaded += (_, _) => UpdateHiberDeskAccess();
    }

    public MainViewModel ViewModel { get; }

    private void UpdateHiberDeskAccess()
    {
        bool vi = App.Services.GetRequiredService<GhostCode.Core.Models.GhostCodeConfig>().Language == GhostCode.Core.Models.UiLanguage.Vi;
        var peer = App.CompanionPeer;
        HiberDeskAccessButton.IsEnabled = peer is not null;
        HiberDeskAccessText.Text = vi
            ? "Chia sẻ số liệu kho lưu trữ và trạng thái sao lưu với HiberDesk, đồng thời cho phép mở lịch sử lưu trữ."
            : "Share archive counts and backup status with HiberDesk and allow it to open archive history.";
        HiberDeskAccessButton.Content = peer?.Enabled == true
            ? (vi ? "Thu hồi quyền HiberDesk" : "Revoke HiberDesk access")
            : (vi ? "Cho phép HiberDesk" : "Allow HiberDesk");
    }

    private async void HiberDeskAccess_Click(object sender, RoutedEventArgs e)
    {
        if (App.CompanionPeer is not { } peer)
            return;
        HiberDeskAccessButton.IsEnabled = false;
        try { await peer.SetEnabledAsync(!peer.Enabled); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ViewModel.ShowInfoBar("HiberDesk", "The local authorization could not be saved.", InfoBarSeverity.Warning);
        }
        finally { UpdateHiberDeskAccess(); }
    }

    private Task<string?> PickFolderAsync() =>
        FilePickerHelpers.PickSingleFolderAsync(App.MainWindowHandle);

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var path = await PickFolderAsync();
        if (!string.IsNullOrWhiteSpace(path))
            ViewModel.AddWatchedFolder(path);
    }

    private void RemoveFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: WatchedFolderItem item })
            ViewModel.RemoveWatchedFolder(item);
    }

    private async void BrowseArchiveDir_Click(object sender, RoutedEventArgs e)
    {
        var path = await PickFolderAsync();
        if (!string.IsNullOrWhiteSpace(path))
            ViewModel.SetArchiveDirectory(path);
    }

    private async void BrowseReadyDir_Click(object sender, RoutedEventArgs e)
    {
        var path = await PickFolderAsync();
        if (!string.IsNullOrWhiteSpace(path))
            ViewModel.SetReadyDirectory(path);
    }

    private async void ExportConfig_Click(object sender, RoutedEventArgs e)
    {
        var json = ViewModel.ExportConfigJson();
        var box = new TextBox
        {
            Text = json,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            MaxHeight = 360
        };

        var dialog = new ContentDialog
        {
            Title = "GhostCode Configuration JSON",
            Content = box,
            CloseButtonText = "Close",
            XamlRoot = this.XamlRoot
        };
        await dialog.ShowAsync();
    }
}
