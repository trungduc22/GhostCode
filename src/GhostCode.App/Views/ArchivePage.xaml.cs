using GhostCode.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace GhostCode.App.Views;

public sealed partial class ArchivePage : Page
{
    public ArchivePage()
    {
        ViewModel = App.Services.GetRequiredService<MainViewModel>();
        InitializeComponent();
    }

    public MainViewModel ViewModel { get; }

    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
            _ = ViewModel.SearchArchiveCommand.ExecuteAsync(null);
    }

    private async void RestoreVersion_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedVersion is null)
        {
            ViewModel.ShowInfoBar("Select Version", ViewModel.Strings.NoVersionSelected, InfoBarSeverity.Informational);
            return;
        }

        var ext = Path.GetExtension(ViewModel.SelectedVersion.FileName);
        if (string.IsNullOrEmpty(ext)) ext = ".cpp";

        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = Path.GetFileNameWithoutExtension(ViewModel.SelectedVersion.FileName) + "_restored"
        };
        picker.FileTypeChoices.Add("Source File", new List<string> { ext });
        InitializeWithWindow.Initialize(picker, App.MainWindowHandle);

        var file = await picker.PickSaveFileAsync();
        if (file is not null)
        {
            await ViewModel.RestoreSelectedVersionAsync(file.Path);
        }
    }
}
