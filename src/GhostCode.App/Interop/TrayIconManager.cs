using System.Runtime.InteropServices;
using GhostCode.App.ViewModels;
using GhostCode.Core.Hotkeys;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace GhostCode.App.Interop;

/// <summary>
/// Manages the Windows System Tray icon (Shell_NotifyIconW), right-click context menu,
/// double-click window restoration, and Minimize-to-Tray close interception.
/// </summary>
public sealed class TrayIconManager : IDisposable
{
    private const uint WM_APP = 0x8000;
    private const uint WM_TRAYICON = WM_APP + 101;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;

    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_MODIFY = 0x00000001;
    private const uint NIM_DELETE = 0x00000002;
    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;

    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x00000010;
    private const uint LR_DEFAULTSIZE = 0x00000040;

    private const uint MF_STRING = 0x00000000;
    private const uint MF_SEPARATOR = 0x00000800;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_RIGHTBUTTON = 0x0002;

    private const uint CMD_OPEN = 1;
    private const uint CMD_SYNC = 2;
    private const uint CMD_OPEN_ARCHIVE = 3;
    private const uint CMD_EXIT = 4;

    private const int SW_SHOW = 5;
    private const int SW_RESTORE = 9;
    private const int WM_HOTKEY = 0x0312;
    private const int GLOBAL_HOTKEY_ID = 0x4743;
    private const uint MOD_NOREPEAT = 0x4000;
    private const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;
    private static readonly nuint SubclassId = 1001;

    private readonly Window _window;
    private readonly MainViewModel _viewModel;
    private readonly Action _onExitRequested;
    private readonly Action? _onHotkeyPressed;
    private readonly IntPtr _hwnd;
    private readonly AppWindow _appWindow;
    private readonly SubclassProc _subclassDelegate;
    private readonly uint _taskbarCreatedMsg;

    private IntPtr _hIcon;
    private bool _iconAdded;
    private bool _hotkeyRegistered;
    private bool _isExiting;
    private bool _disposed;

    public TrayIconManager(Window window, MainViewModel viewModel, Action onExitRequested, Action? onHotkeyPressed = null)
    {
        _window = window;
        _viewModel = viewModel;
        _onExitRequested = onExitRequested;
        _onHotkeyPressed = onHotkeyPressed;
        _hwnd = WindowNative.GetWindowHandle(window);

        var windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);

        _subclassDelegate = WindowSubclassCallback;
        SetWindowSubclass(_hwnd, _subclassDelegate, SubclassId, IntPtr.Zero);
        _taskbarCreatedMsg = RegisterWindowMessageW("TaskbarCreated");

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        if (File.Exists(iconPath))
        {
            _appWindow.SetIcon(iconPath);
            _hIcon = LoadImageW(IntPtr.Zero, iconPath, IMAGE_ICON, 32, 32, LR_LOADFROMFILE | LR_DEFAULTSIZE);
        }

        _appWindow.Closing += OnAppWindowClosing;
        AddOrUpdateTrayIcon();
        ConfigureGlobalHotkey(_viewModel.Config.GlobalHotkeyEnabled, _viewModel.Config.GlobalHotkeyGesture);
    }

    public AppWindow AppWindow => _appWindow;
    public bool IsIconAdded => _iconAdded;
    public bool HasValidIcon => _hIcon != IntPtr.Zero;
    public bool IsGlobalHotkeyRegistered => _hotkeyRegistered;
    public string GlobalHotkeyStatusText { get; private set; } = "Disabled";
    public static bool IsTaskbarPresent => FindWindowW("Shell_TrayWnd", null) != IntPtr.Zero;

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
        _appWindow.Hide();
        _viewModel.SetWindowHiddenInTray(true);
        TryTrimWorkingSet();
    }

    public void ShowAndActivateWindow()
    {
        _appWindow.Show();
        if (IsIconic(_hwnd))
            ShowWindow(_hwnd, SW_RESTORE);
        else
            ShowWindow(_hwnd, SW_SHOW);
        SetForegroundWindow(_hwnd);
        _window.Activate();
        _viewModel.SetWindowHiddenInTray(false);
    }

    public void RequestFullExit()
    {
        if (_isExiting)
            return;
        _isExiting = true;
        RemoveTrayIcon();
        _onExitRequested();
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!_isExiting && _viewModel.MinimizeToTrayOnClose)
        {
            args.Cancel = true;
            _appWindow.Hide();
            _viewModel.SetWindowHiddenInTray(true);
            TryTrimWorkingSet();
            return;
        }

        if (!_isExiting)
        {
            _isExiting = true;
            RemoveTrayIcon();
            _onExitRequested();
        }
    }

    private void AddOrUpdateTrayIcon()
    {
        var nid = new NOTIFYICONDATAW
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_TIP | (_hIcon != IntPtr.Zero ? NIF_ICON : 0),
            uCallbackMessage = WM_TRAYICON,
            hIcon = _hIcon,
            szTip = "GhostCode — Lossless Code Archiver"
        };

        if (!_iconAdded)
        {
            _iconAdded = Shell_NotifyIconW(NIM_ADD, ref nid);
        }
        else
        {
            Shell_NotifyIconW(NIM_MODIFY, ref nid);
        }
    }

    private void RemoveTrayIcon()
    {
        if (!_iconAdded)
            return;
        var nid = new NOTIFYICONDATAW
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = 1
        };
        Shell_NotifyIconW(NIM_DELETE, ref nid);
        _iconAdded = false;
    }

    private IntPtr WindowSubclassCallback(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, nuint uIdSubclass, IntPtr dwRefData)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == GLOBAL_HOTKEY_ID)
        {
            _window.DispatcherQueue.TryEnqueue(() =>
            {
                ShowAndActivateWindow();
                _onHotkeyPressed?.Invoke();
            });
            return IntPtr.Zero;
        }

        if (_taskbarCreatedMsg != 0 && msg == _taskbarCreatedMsg)
        {
            _iconAdded = false;
            AddOrUpdateTrayIcon();
            return IntPtr.Zero;
        }
        if (msg == WM_TRAYICON)
        {
            int mouseEvent = (int)(lParam.ToInt64() & 0xFFFF);
            if (mouseEvent == WM_LBUTTONDBLCLK)
            {
                _window.DispatcherQueue.TryEnqueue(ShowAndActivateWindow);
            }
            else if (mouseEvent == WM_RBUTTONUP)
            {
                ShowContextMenu();
            }
            return IntPtr.Zero;
        }

        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
            return;

        try
        {
            var s = _viewModel.Strings;
            AppendMenuW(menu, MF_STRING, CMD_OPEN, s.TrayOpen);
            AppendMenuW(menu, MF_STRING, CMD_SYNC, s.TraySyncNow);
            AppendMenuW(menu, MF_STRING, CMD_OPEN_ARCHIVE, s.TrayOpenArchive);
            AppendMenuW(menu, MF_SEPARATOR, 0, null);
            AppendMenuW(menu, MF_STRING, CMD_EXIT, s.TrayExit);

            GetCursorPos(out var pt);
            SetForegroundWindow(_hwnd);
            uint selected = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);

            if (selected == CMD_OPEN)
                _window.DispatcherQueue.TryEnqueue(ShowAndActivateWindow);
            else if (selected == CMD_SYNC)
                _window.DispatcherQueue.TryEnqueue(() => _ = _viewModel.SyncNowCommand.ExecuteAsync(null));
            else if (selected == CMD_OPEN_ARCHIVE)
                _window.DispatcherQueue.TryEnqueue(() => _viewModel.OpenArchiveFolderCommand.Execute(null));
            else if (selected == CMD_EXIT)
                _window.DispatcherQueue.TryEnqueue(RequestFullExit);
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

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
        _appWindow.Closing -= OnAppWindowClosing;
        RemoveTrayIcon();
        RemoveWindowSubclass(_hwnd, _subclassDelegate, SubclassId);
        if (_hIcon != IntPtr.Zero)
        {
            DestroyIcon(_hIcon);
            _hIcon = IntPtr.Zero;
        }
    }

    private delegate IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, nuint uIdSubclass, IntPtr dwRefData);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImageW(IntPtr hInst, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, nuint uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, nuint uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, nuint uIdSubclass);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string lpString);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, nint dwMinimumWorkingSetSize, nint dwMaximumWorkingSetSize);

    private static void TryTrimWorkingSet()
    {
        try { SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1); }
        catch { }
    }
}
