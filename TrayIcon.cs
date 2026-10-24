using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Yesify;

/// <summary>
/// Notification-area icon plus global media keys, owned by a hidden window that lives as long as the app
/// (the main window comes and goes). Plain Win32, so no WinForms has to be loaded.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    /// <summary>
    /// Per-install name (hash of the exe folder), so separate copies (e.g. a dev build and the published exe)
    /// don't block each other, while launching the same copy twice still finds the running one.
    /// </summary>
    public static readonly string InstanceName = "Yesify." + Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            (Path.GetDirectoryName(Environment.ProcessPath) ?? "").ToLowerInvariant())))[..12];

    public static string WindowTitle => InstanceName + ".TrayHost";

    private const int WM_APP = 0x8000;
    private const int WM_TRAY = WM_APP + 1;
    public const int WM_SHOW_REQUEST = WM_APP + 2; // sent by a second Yesify launch
    public const int WM_EXIT_REQUEST = WM_APP + 3; // sent by `Yesify.exe --exit` (the launcher, before updating)
    private const int WM_HOTKEY = 0x0312;
    private const int WM_CONTEXTMENU = 0x007B;
    private const int NIN_SELECT = 0x0400, NIN_KEYSELECT = 0x0401;

    private const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    private const int NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_INFO = 0x10, NIF_SHOWTIP = 0x80;
    private const int NOTIFYICON_VERSION_4 = 4;

    private const int HotkeyPlayPause = 1, HotkeyNext = 2, HotkeyPrev = 3;
    private const int CmdShow = 1, CmdPlayPause = 2, CmdNext = 3, CmdPrev = 4, CmdExit = 5, CmdMini = 6;

    private readonly HwndSource _host;

    /// <summary>The hidden window that lives as long as the app (the media session is tied to it).</summary>
    public IntPtr Handle => _host.Handle;
    private readonly IntPtr _icon;
    private readonly int _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private string _tip = "Yesify";
    private bool _disposed;

    public event Action? ShowRequested;
    public event Action? MiniPlayerRequested;
    public event Action? PlayPauseRequested;
    public event Action? NextRequested;
    public event Action? PreviousRequested;
    public event Action? ExitRequested;

    /// <summary>Lets the menu say "Pause" or "Play".</summary>
    public Func<bool>? IsPlaying { get; set; }

    public TrayIcon()
    {
        // A hidden top-level tool window (not message-only: popup menus need a real owner window).
        _host = new HwndSource(new HwndSourceParameters(WindowTitle)
        {
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP, never shown
            ExtendedWindowStyle = 0x80,               // WS_EX_TOOLWINDOW: no taskbar button
            Width = 0,
            Height = 0,
        });
        _host.AddHook(WndProc);

        ExtractIconEx(Environment.ProcessPath ?? "", 0, IntPtr.Zero, out _icon, 1);

        // Fails silently if another app already owns the media keys.
        RegisterHotKey(_host.Handle, HotkeyPlayPause, 0, 0xB3);
        RegisterHotKey(_host.Handle, HotkeyNext, 0, 0xB0);
        RegisterHotKey(_host.Handle, HotkeyPrev, 0, 0xB1);

        Add();
    }

    public void SetTooltip(string text)
    {
        // The notification area truncates tooltips at 127 characters.
        _tip = text.Length > 127 ? text[..124] + "..." : text;
        var data = NewData(NIF_TIP | NIF_SHOWTIP);
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    public void ShowBalloon(string title, string text)
    {
        var data = NewData(NIF_INFO);
        data.szInfoTitle = title;
        data.szInfo = text;
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    private void Add()
    {
        var data = NewData(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        Shell_NotifyIcon(NIM_ADD, ref data);
        data.uTimeoutOrVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIcon(NIM_SETVERSION, ref data);
    }

    private NOTIFYICONDATA NewData(int flags) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _host.Handle,
        uID = 1,
        uFlags = flags,
        uCallbackMessage = WM_TRAY,
        hIcon = _icon,
        szTip = _tip,
        szInfo = "",
        szInfoTitle = "",
    };

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_TRAY)
        {
            // NOTIFYICON_VERSION_4: the event is in the low word of lParam. Raw button messages arrive too,
            // so only react to the high-level ones to avoid doing everything twice.
            switch (lParam.ToInt32() & 0xFFFF)
            {
                case NIN_SELECT or NIN_KEYSELECT: ShowRequested?.Invoke(); break;
                case WM_CONTEXTMENU: ShowMenu(); break;
            }
            handled = true;
        }
        else if (msg == WM_HOTKEY)
        {
            switch (wParam.ToInt32())
            {
                case HotkeyPlayPause: PlayPauseRequested?.Invoke(); break;
                case HotkeyNext: NextRequested?.Invoke(); break;
                case HotkeyPrev: PreviousRequested?.Invoke(); break;
            }
            handled = true;
        }
        else if (msg == WM_SHOW_REQUEST)
        {
            ShowRequested?.Invoke();
            handled = true;
        }
        else if (msg == WM_EXIT_REQUEST)
        {
            ExitRequested?.Invoke();
            handled = true;
        }
        else if (msg == _taskbarCreated)
        {
            Add(); // Explorer restarted and forgot our icon.
        }
        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        AppendMenu(menu, 0, CmdShow, "Show Yesify");
        AppendMenu(menu, 0, CmdMini, "Miniplayer");
        AppendMenu(menu, 0x800, 0, null); // separator
        AppendMenu(menu, 0, CmdPlayPause, IsPlaying?.Invoke() == true ? "Pause" : "Play");
        AppendMenu(menu, 0, CmdNext, "Next");
        AppendMenu(menu, 0, CmdPrev, "Previous");
        AppendMenu(menu, 0x800, 0, null);
        AppendMenu(menu, 0, CmdExit, "Exit");
        SetMenuDefaultItem(menu, CmdShow, 0);

        GetCursorPos(out var pt);
        // Required so the menu closes when the user clicks elsewhere.
        SetForegroundWindow(_host.Handle);
        var cmd = TrackPopupMenuEx(menu, 0x0100 /* TPM_RETURNCMD */ | 0x0002 /* TPM_RIGHTBUTTON */, pt.X, pt.Y, _host.Handle, IntPtr.Zero);
        PostMessage(_host.Handle, 0, IntPtr.Zero, IntPtr.Zero); // WM_NULL, per the TrackPopupMenu docs
        DestroyMenu(menu);

        switch (cmd)
        {
            case CmdShow: ShowRequested?.Invoke(); break;
            case CmdMini: MiniPlayerRequested?.Invoke(); break;
            case CmdPlayPause: PlayPauseRequested?.Invoke(); break;
            case CmdNext: NextRequested?.Invoke(); break;
            case CmdPrev: PreviousRequested?.Invoke(); break;
            case CmdExit: ExitRequested?.Invoke(); break;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var data = NewData(0);
        Shell_NotifyIcon(NIM_DELETE, ref data);
        UnregisterHotKey(_host.Handle, HotkeyPlayPause);
        UnregisterHotKey(_host.Handle, HotkeyNext);
        UnregisterHotKey(_host.Handle, HotkeyPrev);
        if (_icon != IntPtr.Zero) DestroyIcon(_icon);
        _host.Dispose();
    }

    // ---------------------------------------------------------------- Win32

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint ExtractIconEx(string file, int index, IntPtr large, out IntPtr small, uint count);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, uint flags, nint id, string? text);
    [DllImport("user32.dll")] private static extern bool SetMenuDefaultItem(IntPtr menu, uint item, uint byPosition);
    [DllImport("user32.dll")] private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string? className, string windowName);
}
