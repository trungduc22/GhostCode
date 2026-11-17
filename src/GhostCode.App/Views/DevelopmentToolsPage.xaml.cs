using GhostCode.App.Interop;
using GhostCode.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GhostCode.App.Views;

public sealed partial class DevelopmentToolsPage : Page
{
    public DevelopmentToolsPage()
    {
        ViewModel = App.Services.GetRequiredService<MainViewModel>();
        InitializeComponent();
    }

    public MainViewModel ViewModel { get; }

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
}
