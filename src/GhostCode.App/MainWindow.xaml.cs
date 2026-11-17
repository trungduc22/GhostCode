using GhostCode.App.Interop;
using GhostCode.App.ViewModels;
using GhostCode.App.Views;
using GhostCode.Core.Models;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;
using WinRT.Interop;

namespace GhostCode.App;

public sealed record TitleBarGeometrySnapshot(
    bool ExtendsContentIntoTitleBar,
    double TitleBarTop,
    double TitleBarBottom,
    double SearchBoxTop,
    double SearchBoxBottom,
    double SearchBoxLeft,
    double SearchBoxRight,
    double LogoTop,
    double LogoBottom,
    double StatusTop,
    double StatusBottom);

public sealed partial class MainWindow : Window
{
    private readonly AppWindow _appWindow;
    private readonly WindowId _windowId;
    private TrayIconManager? _tray;
    private CancellationTokenSource? _searchDebounceCts;

    public MainWindow(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();

        var hwnd = WindowNative.GetWindowHandle(this);
        App.SetMainWindowHandle(hwnd);
        _windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        _appWindow = AppWindow.GetFromWindowId(_windowId);

        Title = ViewModel.Strings.AppTitle;
        ExtendsContentIntoTitleBar = true;
        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            _appWindow.TitleBar.ExtendsContentIntoTitleBar = true;
            _appWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
            _appWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
            _appWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        }
        SetTitleBar(CustomTitleBarGrid);

        var logoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "ghostcode_logo.png");
        if (File.Exists(logoPath))
            TitleBarLogoImage.Source = new BitmapImage(new Uri(logoPath));

        CustomTitleBarGrid.Loaded += (_, _) => UpdateTitleBarRegions();
        CustomTitleBarGrid.SizeChanged += (_, _) => UpdateTitleBarRegions();
        GlobalSearchBox.SizeChanged += (_, _) => UpdateTitleBarRegions();
        TitleBarBrowserButton.SizeChanged += (_, _) => UpdateTitleBarRegions();
        TitleBarStatsButton.SizeChanged += (_, _) => UpdateTitleBarRegions();
        QuickActionsTitleBarButton.SizeChanged += (_, _) => UpdateTitleBarRegions();
        RootGrid.ActualThemeChanged += (_, _) => ApplyTitleBarThemeColors();
        RootGrid.PreviewKeyDown += RootGrid_PreviewKeyDown;

        ViewModel.AttachUiHooks(
            action =>
            {
                if (DispatcherQueue.HasThreadAccess)
                {
                    action();
                    return true;
                }
                return DispatcherQueue.TryEnqueue(() => action());
            },
            theme =>
            {
                RootGrid.RequestedTheme = theme;
                ApplyTitleBarThemeColors();
            },
            TryCopyTextToClipboard);
        ViewModel.AttachExpansionHooks(
            ApplyMicaBackdrop,
            (enabled, gesture) => _tray is not null
                ? _tray.ConfigureGlobalHotkey(enabled, gesture)
                : (false, "Tray inactive"),
            ApplyTitleBarThemeColors);
        ApplyMicaBackdrop(ViewModel.Config.MicaKind);
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.Strings))
                Title = ViewModel.Strings.AppTitle;
        };
        ViewModel.NavigationRequested += tag => NavigateToTag(tag);

        RootNav.SelectedItem = RootNav.MenuItems[0];
        ContentFrame.Navigate(typeof(DashboardPage));
    }

    public MainViewModel ViewModel { get; }
    public ElementTheme CurrentRootTheme => RootGrid.RequestedTheme;
    public Type? CurrentPageType => ContentFrame.CurrentSourcePageType;
    public object? CurrentPageContent => ContentFrame.Content;
    public TrayIconManager? TrayManager => _tray;
    public AutoSuggestBox TitleBarSearchBox => GlobalSearchBox;

    public bool NavigateToTag(string tag)
    {
        var item = RootNav.MenuItems
            .OfType<NavigationViewItem>()
            .FirstOrDefault(i => string.Equals(i.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase));
        if (item is null)
            return false;
        RootNav.SelectedItem = item;
        return true;
    }

    public TitleBarGeometrySnapshot GetTitleBarGeometrySnapshot()
    {
        var titleBounds = GetBoundsRelativeToRoot(CustomTitleBarGrid);
        var searchBounds = GetBoundsRelativeToRoot(GlobalSearchBox);
        var logoBounds = GetBoundsRelativeToRoot(TitleBarLogoImage);
        var statusBounds = GetBoundsRelativeToRoot(TitleBarStatusPanel);
        return new TitleBarGeometrySnapshot(
            ExtendsContentIntoTitleBar,
            titleBounds.Top,
            titleBounds.Bottom,
            searchBounds.Top,
            searchBounds.Bottom,
            searchBounds.Left,
            searchBounds.Right,
            logoBounds.Top,
            logoBounds.Bottom,
            statusBounds.Top,
            statusBounds.Bottom);
    }

    private Rect GetBoundsRelativeToRoot(FrameworkElement el)
    {
        if (el.ActualWidth <= 0 || el.ActualHeight <= 0)
            return Rect.Empty;
        try
        {
            var transform = el.TransformToVisual(RootGrid);
            return transform.TransformBounds(new Rect(0, 0, el.ActualWidth, el.ActualHeight));
        }
        catch
        {
            return Rect.Empty;
        }
    }

    private void UpdateTitleBarRegions()
    {
        if (RootGrid.XamlRoot is null)
            return;

        UpdateAdaptiveTitleBarStatsLayout();

        var scale = RootGrid.XamlRoot.RasterizationScale;
        if (scale <= 0) scale = 1.0;

        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            var rightInsetDip = Math.Max(138.0, _appWindow.TitleBar.RightInset / scale);
            TitleBarRightInsetCol.MinWidth = rightInsetDip;
        }

        try
        {
            var nonClient = InputNonClientPointerSource.GetForWindowId(_windowId);
            var passthrough = new List<RectInt32>();

            foreach (FrameworkElement interactive in new FrameworkElement[]
                     { GlobalSearchBox, TitleBarBrowserButton, TitleBarStatsButton, QuickActionsTitleBarButton })
            {
                if (interactive.Visibility != Visibility.Visible)
                    continue;
                var rect = ToPhysicalRect(interactive, scale);
                if (rect.Width > 0 && rect.Height > 0)
                    passthrough.Add(rect);
            }

            nonClient.SetRegionRects(NonClientRegionKind.Passthrough, passthrough.ToArray());
        }
        catch
        {
            // Older OS builds without InputNonClientPointerSource fallback gracefully
        }
    }

    private void UpdateAdaptiveTitleBarStatsLayout()
    {
        double w = CustomTitleBarGrid.ActualWidth;
        if (w <= 0)
            return;

        if (w >= 1180)
        {
            TitleBarBrowserButton.Visibility = Visibility.Visible;
            TitleBarStatsWideTextBlock.Visibility = Visibility.Visible;
            TitleBarStatsCompactTextBlock.Visibility = Visibility.Collapsed;
        }
        else if (w >= 960)
        {
            TitleBarBrowserButton.Visibility = Visibility.Visible;
            TitleBarStatsWideTextBlock.Visibility = Visibility.Collapsed;
            TitleBarStatsCompactTextBlock.Visibility = Visibility.Visible;
        }
        else
        {
            TitleBarBrowserButton.Visibility = Visibility.Collapsed;
            TitleBarStatsWideTextBlock.Visibility = Visibility.Collapsed;
            TitleBarStatsCompactTextBlock.Visibility = Visibility.Collapsed;
        }
    }

    private void TitleBarBrowserButton_Click(object sender, RoutedEventArgs e) =>
        NavigateToTag("DevTools");

    private RectInt32 ToPhysicalRect(FrameworkElement element, double scale)
    {
        var rect = GetBoundsRelativeToRoot(element);
        if (rect.IsEmpty)
            return default;
        return new RectInt32(
            (int)Math.Floor(rect.X * scale),
            (int)Math.Floor(rect.Y * scale),
            (int)Math.Ceiling(rect.Width * scale),
            (int)Math.Ceiling(rect.Height * scale));
    }

    private void ApplyTitleBarThemeColors()
    {
        var resolvedTheme = RootGrid.ActualTheme == ElementTheme.Default
            ? (Application.Current.RequestedTheme == ApplicationTheme.Dark ? ElementTheme.Dark : ElementTheme.Light)
            : RootGrid.ActualTheme;

        GhostCode.App.Theming.GhostThemePalette.Apply(resolvedTheme, ViewModel.Config.AccentPreset, ViewModel.Config.CustomAccentHex);

        if (!AppWindowTitleBar.IsCustomizationSupported())
            return;

        var isDark = resolvedTheme == ElementTheme.Dark;
        _appWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        _appWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        _appWindow.TitleBar.ButtonForegroundColor = isDark ? Colors.White : Colors.Black;
        _appWindow.TitleBar.ButtonHoverForegroundColor = isDark ? Colors.White : Colors.Black;
        _appWindow.TitleBar.ButtonHoverBackgroundColor = isDark
            ? Windows.UI.Color.FromArgb(30, 255, 255, 255)
            : Windows.UI.Color.FromArgb(20, 0, 0, 0);
    }

    public void FocusGlobalSearch()
    {
        GlobalSearchBox.Focus(FocusState.Programmatic);
    }

    public async Task TriggerGlobalSearchQueryAsync(string query)
    {
        GlobalSearchBox.Text = query;
        var suggestions = await ViewModel.UpdateGlobalSearchSuggestionsAsync(query);
        GlobalSearchBox.ItemsSource = suggestions;
        if (suggestions.Count > 0)
        {
            NavigateToTag("Archive");
            await ViewModel.SelectArchiveVersionByIdAsync(suggestions[0].Id);
        }
        else
        {
            ViewModel.SearchQuery = query;
            NavigateToTag("Archive");
            await ViewModel.RefreshArchiveAsync();
        }
    }

    private void SearchShortcut_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        FocusGlobalSearch();
        args.Handled = true;
    }

    private async void GlobalSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
            return;

        _searchDebounceCts?.Cancel();
        _searchDebounceCts?.Dispose();
        _searchDebounceCts = new CancellationTokenSource();
        var ct = _searchDebounceCts.Token;
        var query = sender.Text;

        try
        {
            await Task.Delay(120, ct);
            var results = await ViewModel.UpdateGlobalSearchSuggestionsAsync(query, ct);
            if (!ct.IsCancellationRequested)
                sender.ItemsSource = results;
        }
        catch (OperationCanceledException) { }
    }

    private void GlobalSearchBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is GlobalSearchSuggestion sug)
            sender.Text = sug.DisplayTitle;
    }

    private async void GlobalSearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (args.ChosenSuggestion is GlobalSearchSuggestion chosen)
        {
            NavigateToTag("Archive");
            await ViewModel.SelectArchiveVersionByIdAsync(chosen.Id);
            return;
        }

        await TriggerGlobalSearchQueryAsync(args.QueryText);
    }

    private void GlobalSearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            GlobalSearchBox.Text = string.Empty;
            GlobalSearchBox.ItemsSource = null;
            RootNav.Focus(FocusState.Programmatic);
            e.Handled = true;
        }
    }

    public void ApplyMicaBackdrop(MicaAppearanceKind kind)
    {
        try
        {
            if (kind == MicaAppearanceKind.SolidFallback)
            {
                SystemBackdrop = null;
                return;
            }

            SystemBackdrop = new MicaBackdrop
            {
                Kind = kind == MicaAppearanceKind.MicaAlt ? MicaKind.BaseAlt : MicaKind.Base
            };
        }
        catch
        {
            SystemBackdrop = null;
        }
    }

    public void AttachTray(Action onExitRequested)
    {
        _tray?.Dispose();
        _tray = new TrayIconManager(this, ViewModel, onExitRequested, () => ViewModel.OpenQuickActionsPanel());
        ViewModel.AttachExpansionHooks(
            ApplyMicaBackdrop,
            (enabled, gesture) => _tray.ConfigureGlobalHotkey(enabled, gesture));
        ViewModel.GlobalHotkeyStatusText = _tray.GlobalHotkeyStatusText;
        _tray.AppWindow.Resize(new SizeInt32(1280, 820));
        UpdateTitleBarRegions();
        ApplyTitleBarThemeColors();
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
            "DevTools" => typeof(DevelopmentToolsPage),
            "Settings" => typeof(SettingsPage),
            _ => typeof(DashboardPage)
        };

        if (ContentFrame.CurrentSourcePageType != targetType)
            ContentFrame.Navigate(targetType);
    }

    private void RootGrid_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            if (ViewModel.IsQuickActionsOpen)
            {
                ViewModel.IsQuickActionsOpen = false;
                e.Handled = true;
            }
            else if (ViewModel.IsBrowserSetupOpen)
            {
                ViewModel.IsBrowserSetupOpen = false;
                e.Handled = true;
            }
        }
    }

    private void QuickActionsOpenHistory_Click(object sender, RoutedEventArgs e)
    {
        var target = ViewModel.SelectedQuickActionTarget;
        ViewModel.IsQuickActionsOpen = false;
        if (target is not null)
        {
            var matching = ViewModel.ArchiveFileGroups.FirstOrDefault(g =>
                string.Equals(g.SourcePath, target.SourcePath, StringComparison.OrdinalIgnoreCase));
            if (matching is not null)
                ViewModel.SelectedFileGroup = matching;
        }
        NavigateToTag("archive");
    }

    private bool _browserOnboardingChecked;

    public async Task PromptBrowserOnboardingIfNeededAsync()
    {
        if (_browserOnboardingChecked || RootGrid.XamlRoot is null)
            return;
        _browserOnboardingChecked = true;

        var pending = ViewModel.GetUnconnectedUnpromptedBrowsers();
        if (pending.Count == 0)
            return;

        bool isVi = ViewModel.Config.Language == UiLanguage.Vi;
        var names = string.Join(", ", pending.Select(p => p.Name));
        var dontAskBox = new CheckBox
        {
            Content = isVi ? "Không hỏi lại với trình duyệt này" : "Don't ask again for these browsers",
            IsChecked = false
        };

        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(new TextBlock
        {
            Text = isVi
                ? $"GhostCode phát hiện trình duyệt trên máy: {names}.\nCài tiện ích mở rộng để tự động nhận diện bài tập CodePTIT, Codeforces, VNOJ, LeetCode và HackerRank."
                : $"GhostCode detected installed browsers: {names}.\nInstall the extension to automatically detect problems on CodePTIT, Codeforces, VNOJ, LeetCode, and HackerRank.",
            TextWrapping = TextWrapping.Wrap
        });
        body.Children.Add(dontAskBox);

        var dialog = new ContentDialog
        {
            Title = isVi ? "Kết nối GhostCode với trình duyệt?" : "Connect GhostCode to your browser?",
            Content = body,
            PrimaryButtonText = isVi ? "Cài tiện ích" : "Install Extension",
            CloseButtonText = isVi ? "Để sau" : "Later",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot
        };

        try
        {
            var res = await dialog.ShowAsync();
            if (dontAskBox.IsChecked == true)
                ViewModel.DismissBrowserPrompts(pending.Select(p => p.KindKey));

            if (res == ContentDialogResult.Primary)
                await ViewModel.OpenBrowserSetupAsync(pending[0].KindKey, launchBrowserNow: true);
        }
        catch
        {
            // Ignore dialog reentrancy errors during shutdown/navigation
        }
    }

    private static bool TryCopyTextToClipboard(string text)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                var pkg = new DataPackage();
                pkg.SetText(text);
                Clipboard.SetContent(pkg);
                return true;
            }
            catch
            {
                if (attempt < 3)
                    Thread.Sleep(25);
            }
        }
        return false;
    }
}
