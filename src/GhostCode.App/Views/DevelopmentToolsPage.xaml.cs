using GhostCode.App.Interop;
using GhostCode.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GhostCode.App.Views;

public sealed partial class DevelopmentToolsPage : Page
{
    private bool _isStackedDefaultIdes;

    public DevelopmentToolsPage()
    {
        ViewModel = App.Services.GetRequiredService<MainViewModel>();
        InitializeComponent();
        Loaded += (_, _) => UpdateDefaultIdesLayout(ActualWidth);
        SizeChanged += (_, e) => UpdateDefaultIdesLayout(e.NewSize.Width);
    }

    public MainViewModel ViewModel { get; }

    private void UpdateDefaultIdesLayout(double pageWidth)
    {
        if (pageWidth <= 0)
            return;

        bool stack = pageWidth < 680;
        if (stack == _isStackedDefaultIdes)
            return;

        _isStackedDefaultIdes = stack;
        if (stack)
        {
            DefaultIdeCol0.Width = new GridLength(1, GridUnitType.Star);
            DefaultIdeCol1.Width = new GridLength(0, GridUnitType.Pixel);
            DefaultIdeCol2.Width = new GridLength(0, GridUnitType.Pixel);
            Grid.SetRow(DefaultIdeCPanel, 0); Grid.SetColumn(DefaultIdeCPanel, 0);
            Grid.SetRow(DefaultIdeCppPanel, 1); Grid.SetColumn(DefaultIdeCppPanel, 0);
            Grid.SetRow(DefaultIdePyPanel, 2); Grid.SetColumn(DefaultIdePyPanel, 0);
        }
        else
        {
            DefaultIdeCol0.Width = new GridLength(1, GridUnitType.Star);
            DefaultIdeCol1.Width = new GridLength(1, GridUnitType.Star);
            DefaultIdeCol2.Width = new GridLength(1, GridUnitType.Star);
            Grid.SetRow(DefaultIdeCPanel, 0); Grid.SetColumn(DefaultIdeCPanel, 0);
            Grid.SetRow(DefaultIdeCppPanel, 0); Grid.SetColumn(DefaultIdeCppPanel, 1);
            Grid.SetRow(DefaultIdePyPanel, 0); Grid.SetColumn(DefaultIdePyPanel, 2);
        }
    }

    private async void BrowseCCompiler_Click(object sender, RoutedEventArgs e)
    {
        var path = await FilePickerHelpers.PickExecutableAsync(App.MainWindowHandle);
        if (!string.IsNullOrWhiteSpace(path))
            ViewModel.CCompilerPath = path;
    }

    private async void BrowseCppCompiler_Click(object sender, RoutedEventArgs e)
    {
        var path = await FilePickerHelpers.PickExecutableAsync(App.MainWindowHandle);
        if (!string.IsNullOrWhiteSpace(path))
            ViewModel.CppCompilerPath = path;
    }

    private async void BrowsePython_Click(object sender, RoutedEventArgs e)
    {
        var path = await FilePickerHelpers.PickExecutableAsync(App.MainWindowHandle);
        if (!string.IsNullOrWhiteSpace(path))
            ViewModel.PythonInterpreterPath = path;
    }

    private async void ConfigureBrowserExtension_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string kindKey })
            await ViewModel.OpenBrowserSetupCommand.ExecuteAsync(kindKey);
    }
}
