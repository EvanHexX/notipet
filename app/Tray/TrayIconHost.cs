using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Notipet.Tray;

// Menu model for the native tray context menu (Win32 TrackPopupMenu).
// Glyph is a Segoe Fluent Icons code point drawn beside the item. Checkable
// items leave it null: the menu shares one column between check marks and
// icons (MNS_CHECKORBMP), so a checked item shows its check there instead.
internal sealed record TrayMenuItem(
    string? Text,
    Action? Invoke,
    bool IsChecked = false,
    IReadOnlyList<TrayMenuItem>? Submenu = null,
    string? Glyph = null)
{
    public static TrayMenuItem Separator => new(null, null);
    public bool IsSeparator => Text is null;
}

internal enum BalloonLevel
{
    None,
    Info,
    Warning,
    Error
}

// Thin seam over the tray implementation.
internal interface ITrayIcon : IDisposable
{
    event Action? LeftClicked;
    event Action? BalloonClicked;
    event Action<bool>? SessionLockChanged;
    event Action? AudioDeviceChanged;
    void SetIcon(System.Drawing.Icon icon);
    void SetTooltip(string text);
    void SetMenu(IReadOnlyList<TrayMenuItem> items);
    void ShowNotification(string title, string message, BalloonLevel level);
    void Show();
}

// Native Shell_NotifyIcon host, carried over from quota-scope, which wrote it to
// replace H.NotifyIcon.WinUI after that library's internal SUBCLASSPROC delegate
// was garbage-collected while still registered and killed the app via FailFast
// ("callback on a garbage collected delegate"). Every native callback here is
// rooted in an instance field for the window's lifetime. Must be created on the
// UI thread.
//
// Three additions over the original, all of which notipet needs:
//   - dwInfoFlags, so balloons can carry a level icon and NIIF_NOSOUND
//   - NIN_BALLOONUSERCLICK, so clicking a balloon acknowledges an alarm
//   - checkable items and submenus, for mute / quiet hours / recent history
[SupportedOSPlatform("windows")]
internal sealed class TrayIconHost : ITrayIcon
{
    private const uint WmTrayCallback = 0x8000 + 1; // WM_APP + 1
    private const uint WmLbuttonup = 0x0202;
    private const uint WmLbuttondblclk = 0x0203;
    private const uint WmRbuttonup = 0x0205;
    private const uint WmContextMenu = 0x007B;
    private const uint WmWtsSessionChange = 0x02B1;
    private const uint WmDeviceChange = 0x0219;
    private const uint NinBalloonUserClick = 0x0405; // WM_USER + 5

    private const int WtsSessionLock = 0x7;
    private const int WtsSessionUnlock = 0x8;

    private readonly WndProcDelegate _wndProc; // GC root
    private readonly IntPtr _hwnd;
    private readonly uint _taskbarCreatedMessage;
    private System.Drawing.Icon? _icon;
    private string _tooltip = "";
    private IReadOnlyList<TrayMenuItem> _menuItems = Array.Empty<TrayMenuItem>();
    private bool _added;
    private bool _disposed;

    // A double-click arrives as DOWN, UP, DBLCLK, UP - two "clicks". With the
    // left click toggling the recent window, that opened and immediately
    // closed it, and during an alarm it stopped the alarm and then opened the
    // window. The UP that finishes a double-click is swallowed instead - but
    // only right after it, so a lost UP can never eat a later, real click.
    private long _doubleClickAt = -1;   // -1: none pending
    private bool _sessionNotificationsRegistered;

    public event Action? LeftClicked;
    public event Action? BalloonClicked;
    public event Action<bool>? SessionLockChanged;
    public event Action? AudioDeviceChanged;

    public TrayIconHost()
    {
        _wndProc = WndProc;
        var hInstance = GetModuleHandle(null);
        const string className = "NotipetTrayWindow";
        var wndClass = new WndClassEx
        {
            cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = hInstance,
            lpszClassName = className
        };
        if (RegisterClassEx(ref wndClass) == 0)
        {
            throw new InvalidOperationException($"RegisterClassEx failed ({Marshal.GetLastWin32Error()}).");
        }

        // A real (hidden) top-level window, not message-only: the TaskbarCreated
        // broadcast used for explorer-restart recovery is not sent to
        // message-only windows, and neither is WM_WTSSESSION_CHANGE.
        _hwnd = CreateWindowEx(0, className, string.Empty, 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException($"CreateWindowEx failed ({Marshal.GetLastWin32Error()}).");
        }
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

        // Before any menu exists, so the first one already follows the theme.
        MenuGlyphs.EnableSystemThemeMenus();

        // Lock/unlock is the only reliable way to know the user stepped away,
        // because GetLastInputInfo stops updating on a locked desktop.
        _sessionNotificationsRegistered = WTSRegisterSessionNotification(_hwnd, NotifyForThisSession);
    }

    public void SetIcon(System.Drawing.Icon icon)
    {
        var previous = _icon;
        _icon = icon;
        if (_added)
        {
            var data = BuildData(NifIcon);
            data.hIcon = icon.Handle;
            Shell_NotifyIcon(NimModify, ref data);
        }
        previous?.Dispose();
    }

    public void SetTooltip(string text)
    {
        _tooltip = text.Length <= 127 ? text : text[..127];
        if (_added)
        {
            var data = BuildData(NifTip);
            data.szTip = _tooltip;
            Shell_NotifyIcon(NimModify, ref data);
        }
    }

    public void SetMenu(IReadOnlyList<TrayMenuItem> items) => _menuItems = items;

    public void ShowNotification(string title, string message, BalloonLevel level)
    {
        if (!_added) return;
        var data = BuildData(NifInfo);
        data.szInfoTitle = title.Length <= 63 ? title : title[..63];
        data.szInfo = message.Length <= 255 ? message : message[..255];

        // NIIF_NOSOUND is the important one. Without it the shell plays its own
        // ding on top of whatever the sound channel is playing, and every single
        // notification double-sounds.
        data.dwInfoFlags = NiifNoSound | level switch
        {
            BalloonLevel.Info => NiifInfo,
            BalloonLevel.Warning => NiifWarning,
            BalloonLevel.Error => NiifError,
            _ => NiifNone
        };
        Shell_NotifyIcon(NimModify, ref data);
    }

    public void Show() => AddIcon();

    private void AddIcon()
    {
        if (_disposed) return;
        var data = BuildData(NifMessage | NifIcon | NifTip);
        data.uCallbackMessage = WmTrayCallback;
        data.hIcon = _icon?.Handle ?? IntPtr.Zero;
        data.szTip = _tooltip;
        _added = Shell_NotifyIcon(NimAdd, ref data);
    }

    private NotifyIconData BuildData(uint flags)
    {
        return new NotifyIconData
        {
            cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = flags,
            szTip = "",
            szInfo = "",
            szInfoTitle = ""
        };
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WmTrayCallback)
        {
            switch ((uint)(lParam.ToInt64() & 0xFFFF))
            {
                case WmLbuttondblclk:
                    _doubleClickAt = Environment.TickCount64;
                    break;
                case WmLbuttonup:
                    if (_doubleClickAt >= 0 && Environment.TickCount64 - _doubleClickAt < 1000)
                    {
                        _doubleClickAt = -1;
                        break;
                    }
                    LeftClicked?.Invoke();
                    break;
                case WmRbuttonup:
                case WmContextMenu:
                    ShowContextMenu();
                    break;
                case NinBalloonUserClick:
                    BalloonClicked?.Invoke();
                    break;
            }
            return IntPtr.Zero;
        }
        if (msg == WmWtsSessionChange)
        {
            var reason = (int)wParam;
            if (reason == WtsSessionLock) SessionLockChanged?.Invoke(true);
            else if (reason == WtsSessionUnlock) SessionLockChanged?.Invoke(false);
            return IntPtr.Zero;
        }
        if (msg == WmDeviceChange)
        {
            // A headset reconnecting is the common case; re-probe rather than
            // leaving the sound engine latched as unavailable.
            AudioDeviceChanged?.Invoke();
            return IntPtr.Zero;
        }
        if (msg == _taskbarCreatedMessage && _taskbarCreatedMessage != 0)
        {
            // Explorer restarted: re-add the icon.
            _added = false;
            AddIcon();
            return IntPtr.Zero;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        var items = _menuItems;
        if (items.Count == 0) return;

        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;

        var submenus = new List<IntPtr>();
        var bitmaps = new List<IntPtr>();
        try
        {
            uint id = 1;
            var actions = new Dictionary<uint, Action>();
            var glyphSize = Math.Max(16, (int)Math.Round(16 * GetDpiForWindow(_hwnd) / 96.0));
            var dark = MenuGlyphs.MenusAreDark();
            Build(menu, items, ref id, actions, submenus, bitmaps, glyphSize, dark);

            // Required so the menu closes when clicking elsewhere.
            SetForegroundWindow(_hwnd);
            GetCursorPos(out var cursor);
            var chosen = TrackPopupMenuEx(menu, TpmReturnCmd | TpmRightButton, cursor.X, cursor.Y, _hwnd, IntPtr.Zero);
            PostMessage(_hwnd, 0 /*WM_NULL*/, IntPtr.Zero, IntPtr.Zero);
            if (chosen != 0 && actions.TryGetValue((uint)chosen, out var action))
            {
                action();
            }
        }
        finally
        {
            foreach (var submenu in submenus) DestroyMenu(submenu);
            DestroyMenu(menu);
            // The menu does not own item bitmaps; they outlive it unless freed.
            foreach (var bitmap in bitmaps) MenuGlyphs.Delete(bitmap);
        }
    }

    private static void Build(
        IntPtr menu,
        IReadOnlyList<TrayMenuItem> items,
        ref uint id,
        Dictionary<uint, Action> actions,
        List<IntPtr> submenus,
        List<IntPtr> bitmaps,
        int glyphSize,
        bool dark)
    {
        // Check marks and icons share one column, so icon and non-icon items
        // line up instead of the menu growing a second gutter.
        var info = new MenuInfo { cbSize = (uint)Marshal.SizeOf<MenuInfo>(), fMask = MimStyle, dwStyle = MnsCheckOrBmp };
        SetMenuInfo(menu, ref info);

        foreach (var item in items)
        {
            if (item.IsSeparator)
            {
                AppendMenu(menu, MfSeparator, UIntPtr.Zero, null);
                continue;
            }

            if (item.Submenu is { Count: > 0 })
            {
                var child = CreatePopupMenu();
                if (child == IntPtr.Zero) continue;
                submenus.Add(child);
                Build(child, item.Submenu, ref id, actions, submenus, bitmaps, glyphSize, dark);
                AppendMenu(menu, MfString | MfPopup, new UIntPtr((ulong)child.ToInt64()), item.Text);
                continue;
            }

            var flags = MfString;
            if (item.Invoke is null) flags |= MfGrayed;
            else actions[id] = item.Invoke;
            if (item.IsChecked) flags |= MfChecked;

            AppendMenu(menu, flags, new UIntPtr(id), item.Text);

            if (item.Glyph is not null && !item.IsChecked)
            {
                // This runs inside the window procedure, where an exception
                // escaping into native code kills the process. An icon is
                // decoration; a menu without one is fine.
                IntPtr bitmap;
                try { bitmap = MenuGlyphs.Render(item.Glyph, glyphSize, dark); }
                catch { bitmap = IntPtr.Zero; }
                if (bitmap != IntPtr.Zero)
                {
                    bitmaps.Add(bitmap);
                    var mii = new MenuItemInfo
                    {
                        cbSize = (uint)Marshal.SizeOf<MenuItemInfo>(),
                        fMask = MiimBitmap,
                        hbmpItem = bitmap
                    };
                    SetMenuItemInfo(menu, id, false, ref mii);
                }
            }
            id++;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_sessionNotificationsRegistered)
        {
            try { WTSUnRegisterSessionNotification(_hwnd); } catch { }
            _sessionNotificationsRegistered = false;
        }
        if (_added)
        {
            var data = BuildData(0);
            Shell_NotifyIcon(NimDelete, ref data);
            _added = false;
        }
        DestroyWindow(_hwnd);
        _icon?.Dispose();
        _icon = null;
    }

    // ----- interop -----

    private const uint NimAdd = 0x0;
    private const uint NimModify = 0x1;
    private const uint NimDelete = 0x2;
    private const uint NifMessage = 0x01;
    private const uint NifIcon = 0x02;
    private const uint NifTip = 0x04;
    private const uint NifInfo = 0x10;
    private const uint NiifNone = 0x00;
    private const uint NiifInfo = 0x01;
    private const uint NiifWarning = 0x02;
    private const uint NiifError = 0x03;
    private const uint NiifNoSound = 0x10;
    private const uint MfString = 0x0000;
    private const uint MfGrayed = 0x0001;
    private const uint MfChecked = 0x0008;
    private const uint MfPopup = 0x0010;
    private const uint MfSeparator = 0x0800;
    private const uint TpmReturnCmd = 0x0100;
    private const uint TpmRightButton = 0x0002;
    private const int NotifyForThisSession = 0;

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW")]
    private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "RegisterClassExW")]
    private static extern ushort RegisterClassEx(ref WndClassEx wndClass);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateWindowExW")]
    private static extern IntPtr CreateWindowEx(
        uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "DefWindowProcW")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterWindowMessageW")]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "AppendMenuW")]
    private static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string? text);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpmParams);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const uint MimStyle = 0x00000010;
    private const uint MnsCheckOrBmp = 0x04000000;
    private const uint MiimBitmap = 0x00000080;

    [StructLayout(LayoutKind.Sequential)]
    private struct MenuInfo
    {
        public uint cbSize;
        public uint fMask;
        public uint dwStyle;
        public uint cyMax;
        public IntPtr hbrBack;
        public uint dwContextHelpID;
        public UIntPtr dwMenuData;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MenuItemInfo
    {
        public uint cbSize;
        public uint fMask;
        public uint fType;
        public uint fState;
        public uint wID;
        public IntPtr hSubMenu;
        public IntPtr hbmpChecked;
        public IntPtr hbmpUnchecked;
        public UIntPtr dwItemData;
        public IntPtr dwTypeData;
        public uint cch;
        public IntPtr hbmpItem;
    }

    [DllImport("user32.dll")]
    private static extern bool SetMenuInfo(IntPtr menu, ref MenuInfo info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SetMenuItemInfoW")]
    private static extern bool SetMenuItemInfo(IntPtr menu, uint item, bool byPosition, ref MenuItemInfo info);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSRegisterSessionNotification(IntPtr hwnd, int flags);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSUnRegisterSessionNotification(IntPtr hwnd);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
