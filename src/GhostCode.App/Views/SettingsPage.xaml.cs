using GhostCode.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace GhostCode.App.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        ViewModel = App.Services.GetRequiredService<MainViewModel>();
        InitializeComponent();
    }

    public MainViewModel ViewModel { get; }

    private async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary
        };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, App.MainWindowHandle);
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

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

    private async void PrepareBrowser_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string kindKey })
            await ViewModel.PrepareBrowserIntegrationAsync(kindKey);
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
