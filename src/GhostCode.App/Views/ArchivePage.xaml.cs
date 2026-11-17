using GhostCode.App.ViewModels;
using GhostCode.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Unloaded += (_, _) => ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.RunnerTabIndex))
            UpdateTabButtonStyles();
    }

    public MainViewModel ViewModel { get; }

    private void SetupFoldersFromArchive_Click(object sender, RoutedEventArgs e) =>
        ViewModel.RequestNavigate("Settings");

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
