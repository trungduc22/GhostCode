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
        UpdateTabButtonStyles();
    }

    public MainViewModel ViewModel { get; }

    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
            _ = ViewModel.SearchArchiveCommand.ExecuteAsync(null);
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
        var accent = Application.Current.Resources.TryGetValue("AccentButtonStyle", out var res) ? res as Style : null;
        ConsoleTabBtn.Style = ViewModel.RunnerTabIndex == 0 ? accent : null;
        IoTabBtn.Style = ViewModel.RunnerTabIndex == 1 ? accent : null;
        SamplesTabBtn.Style = ViewModel.RunnerTabIndex == 2 ? accent : null;
    }

    private async void AddCustomSample_Click(object sender, RoutedEventArgs e)
    {
        var inputBox = new TextBox
        {
            Header = "Custom Sample Input",
            AcceptsReturn = true,
            Height = 90,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas")
        };
        var expectedBox = new TextBox
        {
            Header = "Expected Output",
            AcceptsReturn = true,
            Height = 90,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas")
        };
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(inputBox);
        panel.Children.Add(expectedBox);

        var dialog = new ContentDialog
        {
            Title = "Add Custom Sample Test Case",
            Content = panel,
            PrimaryButtonText = "Save Test",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            await ViewModel.AddCustomSampleAsync(inputBox.Text, expectedBox.Text);
        }
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
