using System.ComponentModel;
using System.Runtime.InteropServices;
using GhostCode.App.ViewModels;
using GhostCode.Core.Hotkeys;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using NativeShellKit.Abstractions.Tray;
using NativeShellKit.Windows.Interop;
using WinRT.Interop;

namespace GhostCode.App.Interop;

public sealed class TrayIconManager : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int GLOBAL_HOTKEY_ID = 0x4743;
    private const uint MOD_NOREPEAT = 0x4000;
    private const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;
    private const nuint HotkeySubclassId = 1001;

    private readonly Window _window;
    private readonly MainViewModel _viewModel;
    private readonly Action? _onHotkeyPressed;
    private readonly Action _onExitRequested;
    private readonly nint _hwnd;
    private readonly SubclassProc _subclassDelegate;
    private readonly ITrayService _tray;
    private readonly IWin32Interop _interop = Win32Interop.Default;

    private bool _hotkeyRegistered;
    private bool _isExiting;
    private bool _disposed;

    public TrayIconManager(Window window, MainViewModel viewModel, ITrayService tray,
        Action onExitRequested, Action? onHotkeyPressed = null)
    {
        _window = window;
        _viewModel = viewModel;
        _onHotkeyPressed = onHotkeyPressed;
        _onExitRequested = onExitRequested;
        _hwnd = WindowNative.GetWindowHandle(window);
        AppWindow = window.AppWindow;

        _subclassDelegate = HotkeyWindowCallback;
        _interop.SetWindowSubclass(_hwnd, _subclassDelegate, HotkeySubclassId);
        _tray = tray;
        _tray.RestoreRequested += OnRestoreRequested;
        _tray.FullExitRequested += OnFullExitRequested;

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        if (File.Exists(iconPath))
        {
            AppWindow.SetIcon(iconPath);
            HasValidIcon = true;
        }

        UpdateMenu();
        _tray.Initialize(_hwnd, iconPath);
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        AppWindow.Closing += OnAppWindowClosing;
        ConfigureGlobalHotkey(_viewModel.Config.GlobalHotkeyEnabled, _viewModel.Config.GlobalHotkeyGesture);
    }

    public AppWindow AppWindow { get; }
    public bool IsIconAdded => _tray.IsIconActive;
    public bool HasValidIcon { get; }
    public bool IsGlobalHotkeyRegistered => _hotkeyRegistered;
    public string GlobalHotkeyStatusText { get; private set; } = "Disabled";
    public static bool IsTaskbarPresent => Win32Interop.Default.FindWindow("Shell_TrayWnd", null) != nint.Zero;

    public (bool Success, string Detail) ConfigureGlobalHotkey(bool enabled, string gesture)
    {
        if (_hotkeyRegistered)
        {
            UnregisterHotKey(_hwnd, GLOBAL_HOTKEY_ID);
            _hotkeyRegistered = false;
        }

        if (!enabled || string.IsNullOrWhiteSpace(gesture))
        {
            GlobalHotkeyStatusText = "Disabled";
            return (true, GlobalHotkeyStatusText);
        }

        if (!TryParseHotkeyGesture(gesture, out uint mods, out uint vk))
        {
            GlobalHotkeyStatusText = $"Invalid hotkey syntax: {gesture}";
            return (false, GlobalHotkeyStatusText);
        }

        bool ok = RegisterHotKey(_hwnd, GLOBAL_HOTKEY_ID, mods | MOD_NOREPEAT, vk);
        if (ok)
        {
            _hotkeyRegistered = true;
            GlobalHotkeyStatusText = $"Active ({gesture})";
            return (true, GlobalHotkeyStatusText);
        }

        int err = Marshal.GetLastWin32Error();
        GlobalHotkeyStatusText = err == ERROR_HOTKEY_ALREADY_REGISTERED
            ? $"Hotkey conflict ({gesture} is already claimed by another application)"
            : $"Hotkey registration failed (Win32 {err})";
        return (false, GlobalHotkeyStatusText);
    }

    public static bool TryParseHotkeyGesture(string gesture, out uint modifiers, out uint virtualKey) =>
        GlobalHotkeyParser.TryParse(gesture, out modifiers, out virtualKey);

    public void HideToTray()
    {
        _tray.HideToTray();
        _viewModel.SetWindowHiddenInTray(true);
    }

    public void ShowAndActivateWindow() => _tray.ShowFromTray();

    private void OnRestoreRequested(object? sender, EventArgs args)
    {
        _window.Activate();
        _viewModel.SetWindowHiddenInTray(false);
    }

    private void OnFullExitRequested(object? sender, EventArgs args) => _onExitRequested();

    public void RequestFullExit()
    {
        if (_isExiting)
            return;

        _isExiting = true;
        _tray.RequestFullExit();
    }

    public void PrepareForExternalShutdown() => _isExiting = true;

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_isExiting)
            return;

        _tray.MinimizeToTrayOnClose = _viewModel.MinimizeToTrayOnClose;
        if (_tray.HandleWindowClosing())
        {
            args.Cancel = true;
            _viewModel.SetWindowHiddenInTray(true);
            return;
        }

        args.Cancel = true;
        RequestFullExit();
    }

    private nint HotkeyWindowCallback(nint hwnd, uint message, nint wParam, nint lParam, nuint subclassId, nint referenceData)
    {
        if (message == WM_HOTKEY && wParam.ToInt32() == GLOBAL_HOTKEY_ID)
        {
            Enqueue(() =>
            {
                ShowAndActivateWindow();
                _onHotkeyPressed?.Invoke();
            });
            return nint.Zero;
        }
        return _interop.DefSubclassProc(hwnd, message, wParam, lParam);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MainViewModel.Strings))
            Enqueue(UpdateMenu);
    }

    private void UpdateMenu()
    {
        var strings = _viewModel.Strings;
        _tray.SetMenuItems([
            new(1, strings.TrayOpen, () => Enqueue(ShowAndActivateWindow)),
            new(2, strings.TraySyncNow, () => Enqueue(() => _ = _viewModel.SyncNowCommand.ExecuteAsync(null))),
            new(3, strings.TrayOpenArchive, () => Enqueue(() => _viewModel.OpenArchiveFolderCommand.Execute(null))),
            new(0, "", IsSeparator: true),
            new(4, strings.TrayExit, () => Enqueue(RequestFullExit))
        ]);
    }

    private void Enqueue(Action action) => _window.DispatcherQueue.TryEnqueue(() =>
    {
        if (!_disposed && !_isExiting)
            action();
    });

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (_hotkeyRegistered)
        {
            UnregisterHotKey(_hwnd, GLOBAL_HOTKEY_ID);
            _hotkeyRegistered = false;
        }
        AppWindow.Closing -= OnAppWindowClosing;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _tray.RestoreRequested -= OnRestoreRequested;
        _tray.FullExitRequested -= OnFullExitRequested;
        _tray.Dispose();
        _interop.RemoveWindowSubclass(_hwnd, _subclassDelegate, HotkeySubclassId);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
