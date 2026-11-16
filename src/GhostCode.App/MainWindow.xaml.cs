using GhostCode.App.Interop;
using GhostCode.App.ViewModels;
using GhostCode.App.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using WinRT.Interop;

namespace GhostCode.App;

public sealed partial class MainWindow : Window
{
    private TrayIconManager? _tray;

    public MainWindow(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();

        Title = ViewModel.Strings.AppTitle;
        ExtendsContentIntoTitleBar = false;

        var hwnd = WindowNative.GetWindowHandle(this);
        App.SetMainWindowHandle(hwnd);

        ViewModel.AttachUiHooks(
            action => DispatcherQueue.TryEnqueue(() => action()),
            theme => RootGrid.RequestedTheme = theme,
            CopyTextToClipboard);

        RootNav.SelectedItem = RootNav.MenuItems[0];
        ContentFrame.Navigate(typeof(DashboardPage));
    }

    public MainViewModel ViewModel { get; }

    public void AttachTray(Action onExitRequested)
    {
        _tray?.Dispose();
        _tray = new TrayIconManager(this, ViewModel, onExitRequested);
        _tray.AppWindow.Resize(new SizeInt32(1160, 760));
    }

    public void DisposeTray()
    {
        _tray?.Dispose();
        _tray = null;
    }

    private void RootNav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item)
            return;

        var targetType = item.Tag?.ToString() switch
        {
            "Archive" => typeof(ArchivePage),
            "Cloud" => typeof(CloudPage),
            "Settings" => typeof(SettingsPage),
            _ => typeof(DashboardPage)
        };

        if (ContentFrame.CurrentSourcePageType != targetType)
            ContentFrame.Navigate(targetType);
    }

    private static void CopyTextToClipboard(string text)
    {
        try
        {
            var pkg = new DataPackage();
            pkg.SetText(text);
            Clipboard.SetContent(pkg);
        }
        catch
        {
            // Clipboard may be locked briefly by another process
        }
    }
}
