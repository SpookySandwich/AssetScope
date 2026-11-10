using System.Runtime.InteropServices;
using AssetScope.Core.Diagnostics;
using Microsoft.UI.Dispatching;

namespace AssetScope.App.Services;

/// <summary>
/// Native notification-area integration. A private message-only window owns
/// the icon, so the main WinUI window can be hidden without stopping commands.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private const int WmApp = 0x8000;
    private const int CallbackMessage = WmApp + 73;
    private const int WmLButtonUp = 0x0202;
    private const int WmLButtonDblClk = 0x0203;
    private const int WmRButtonUp = 0x0205;
    private const uint NimAdd = 0x00000000;
    private const uint NimDelete = 0x00000002;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint ImageIcon = 1;
    private const uint LrLoadFromFile = 0x00000010;
    private const uint LrDefaultSize = 0x00000040;
    private const uint MfString = 0x00000000;
    private const uint MfSeparator = 0x00000800;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmReturnCmd = 0x0100;
    private const int OpenCommand = 1001;
    private const int SyncCommand = 1002;
    private const int ExitCommand = 1003;
    private static readonly IntPtr HwndMessage = new(-3);
    private static readonly WndProc WindowProcedure = HandleWindowMessage;
    private static readonly Dictionary<IntPtr, TrayIconService> Instances = [];

    private readonly DispatcherQueue _dispatcher;
    private readonly Action _open;
    private readonly Func<Task> _sync;
    private readonly Action _exit;
    private readonly string _className = $"AssetScope.Tray.{Guid.NewGuid():N}";
    private IntPtr _window;
    private IntPtr _icon;
    private bool _disposed;

    public TrayIconService(DispatcherQueue dispatcher, Action open, Func<Task> sync, Action exit)
    {
        _dispatcher = dispatcher;
        _open = open;
        _sync = sync;
        _exit = exit;
        Create();
    }

    private void Create()
    {
        var module = GetModuleHandle(null);
        var windowClass = new WndClassEx
        {
            cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
            hInstance = module,
            lpszClassName = _className,
            lpfnWndProc = WindowProcedure,
        };
        if (RegisterClassEx(ref windowClass) == 0)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Unable to register the tray window class.");

        _window = CreateWindowEx(0, _className, "assetscope tray", 0, 0, 0, 0, 0,
            HwndMessage, IntPtr.Zero, module, IntPtr.Zero);
        if (_window == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Unable to create the tray window.");
        Instances[_window] = this;

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "assetscope.ico");
        _icon = LoadImage(IntPtr.Zero, iconPath, ImageIcon, 0, 0, LrLoadFromFile | LrDefaultSize);
        if (_icon == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), $"Unable to load tray icon: {iconPath}");

        var data = NotificationData();
        data.uFlags = NifMessage | NifIcon | NifTip;
        data.uCallbackMessage = CallbackMessage;
        data.hIcon = _icon;
        data.szTip = "assetscope";
        if (!ShellNotifyIcon(NimAdd, ref data))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Windows refused the tray icon.");
        AppLog.Info("Tray", "Notification-area icon registered.");
    }

    private NotifyIconData NotificationData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
        hWnd = _window,
        uID = 1,
    };

    private static IntPtr HandleWindowMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == CallbackMessage && Instances.TryGetValue(hwnd, out var instance))
        {
            var mouseMessage = unchecked((int)lParam.ToInt64());
            if (mouseMessage is WmLButtonUp or WmLButtonDblClk)
                instance.Dispatch(instance._open);
            else if (mouseMessage == WmRButtonUp)
                instance.ShowMenu();
            return IntPtr.Zero;
        }
        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        try
        {
            AppendMenu(menu, MfString, OpenCommand, Display.S("TrayOpen"));
            AppendMenu(menu, MfString, SyncCommand, Display.S("TraySync"));
            AppendMenu(menu, MfSeparator, 0, null);
            AppendMenu(menu, MfString, ExitCommand, Display.S("TrayExit"));
            GetCursorPos(out var point);
            SetForegroundWindow(_window);
            var command = TrackPopupMenu(menu, TpmRightButton | TpmReturnCmd,
                point.X, point.Y, 0, _window, IntPtr.Zero);
            if (command == OpenCommand) Dispatch(_open);
            else if (command == SyncCommand) Dispatch(() => _ = _sync());
            else if (command == ExitCommand) Dispatch(_exit);
        }
        finally { DestroyMenu(menu); }
    }

    private void Dispatch(Action action) => _dispatcher.TryEnqueue(() =>
    {
        try { action(); }
        catch (Exception ex) { AppLog.Error("Tray", "Tray command failed.", ex); }
    });

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_window != IntPtr.Zero)
        {
            var data = NotificationData();
            ShellNotifyIcon(NimDelete, ref data);
            Instances.Remove(_window);
            DestroyWindow(_window);
            _window = IntPtr.Zero;
        }
        if (_icon != IntPtr.Zero)
        {
            DestroyIcon(_icon);
            _icon = IntPtr.Zero;
        }
        UnregisterClass(_className, GetModuleHandle(null));
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint cbSize, style;
        [MarshalAs(UnmanagedType.FunctionPtr)] public WndProc lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName, lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID, uFlags, uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    private delegate IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? moduleName);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WndClassEx windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string className, IntPtr instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int cx, int cy, uint load);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW", SetLastError = true)]
    private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, uint flags, int id, string? text);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);
}
