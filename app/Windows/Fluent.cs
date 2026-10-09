using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Notipet.Shared;
using Windows.Graphics;
using WinRT.Interop;

namespace Notipet.Windows;

// Segoe Fluent Icons code points. Each was rendered and checked by eye before
// being used here - a wrong code point draws a plausible but wrong icon, and
// nothing else would catch it.
internal static class Glyphs
{
    public const string Settings = "\uE713";
    public const string Volume = "\uE767";
    public const string Speakers = "\uE7F5";
    public const string Mute = "\uE74F";
    public const string Bell = "\uEA8F";
    public const string BellOff = "\uE7ED";
    public const string Moon = "\uE708";
    public const string History = "\uE81C";
    public const string Delete = "\uE74D";
    public const string Refresh = "\uE72C";
    public const string Globe = "\uE774";
    public const string Language = "\uF2B7";
    public const string Info = "\uE946";
    public const string Completed = "\uE930";
    public const string Warning = "\uE7BA";
    public const string ErrorCircle = "\uE783";
    public const string ErrorBadge = "\uEA39";
    public const string Important = "\uE8C9";
    public const string Play = "\uE768";
    public const string Stop = "\uE71A";
    public const string Copy = "\uE8C8";
    public const string Folder = "\uE838";
    public const string Person = "\uE77B";
    public const string Pc = "\uE977";
    public const string Clock = "\uE823";
    public const string Power = "\uE7E8";
    public const string Code = "\uE943";
    public const string Terminal = "\uE756";
    public const string Robot = "\uE99A";
    public const string Message = "\uE91C";
    public const string Filter = "\uE71C";
    public const string Checklist = "\uF0E3";
    public const string Diagnostic = "\uE9D9";
    public const string Schedule = "\uEC92";
    public const string Cancel = "\uE711";
    public const string OpenInApp = "\uE8A7";
    public const string ChevronDown = "\uE70D";
    public const string ChevronRight = "\uE76C";
    public const string Thread = "\uE8BD";
    public const string Pin = "\uE718";
    public const string FullScreen = "\uE740";
    public const string More = "\uE712";

    public static string ForLevel(NotificationLevel level) => level switch
    {
        NotificationLevel.Success => Completed,
        NotificationLevel.Attention => Bell,
        NotificationLevel.Warn => Warning,
        NotificationLevel.Error => ErrorBadge,
        NotificationLevel.Critical => ErrorCircle,
        _ => Info
    };
}

// Code-behind building blocks for a Fluent look.
//
// Brushes are attached through {ThemeResource} via XamlReader rather than read
// once from Application.Resources: a brush looked up in code is frozen to the
// theme at startup, so switching Windows between light and dark would leave
// cards painted in the old theme. ThemeResource re-resolves on the fly.
[SupportedOSPlatform("windows10.0.19041.0")]
internal static class Fluent
{
    private const string Ns = "xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'";

    public static T Xaml<T>(string markup) where T : class =>
        (T)XamlReader.Load(markup.Replace("$NS", Ns));

    public static TextBlock Text(string text, string style = "BodyTextBlockStyle", string? foreground = null, bool wrap = true)
    {
        var fg = foreground is null ? "" : $" Foreground='{{ThemeResource {foreground}}}'";
        var block = Xaml<TextBlock>($"<TextBlock $NS Style='{{StaticResource {style}}}'{fg}/>");
        block.Text = text;
        block.TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        if (!wrap) block.TextTrimming = TextTrimming.CharacterEllipsis;
        return block;
    }

    public static TextBlock Secondary(string text, string style = "CaptionTextBlockStyle") =>
        Text(text, style, "TextFillColorSecondaryBrush");

    public static FontIcon Icon(string glyph, double size = 16, string? foreground = null)
    {
        var fg = foreground is null ? "" : $" Foreground='{{ThemeResource {foreground}}}'";
        var icon = Xaml<FontIcon>($"<FontIcon $NS FontSize='{size}'{fg}/>");
        icon.Glyph = glyph;
        return icon;
    }

    public static Border Card(UIElement child, string padding = "16,12")
    {
        var card = Xaml<Border>(
            "<Border $NS Background='{ThemeResource CardBackgroundFillColorDefaultBrush}' " +
            "BorderBrush='{ThemeResource CardStrokeColorDefaultBrush}' BorderThickness='1' " +
            $"CornerRadius='{{ThemeResource ControlCornerRadius}}' Padding='{padding}'/>");
        card.Child = child;
        return card;
    }

    // The Windows 11 Settings row: icon, header, description, and a control on
    // the right. The shape everyone already knows how to read.
    public static Border SettingCard(string glyph, string header, string? description, UIElement? control)
    {
        var grid = new Grid { ColumnSpacing = 16, MinHeight = 44 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = Icon(glyph, 20);
        icon.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(icon);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        text.Children.Add(Text(header, "BodyTextBlockStyle"));
        if (!string.IsNullOrWhiteSpace(description)) text.Children.Add(Secondary(description));
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        if (control is FrameworkElement element)
        {
            element.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(element, 2);
            grid.Children.Add(element);
        }

        return Card(grid);
    }

    // A setting card with a second row of controls underneath, for things
    // that do not fit on the right (the per-level sound picker).
    public static Border ExpandedCard(UIElement header, UIElement body)
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(header);
        panel.Children.Add(body);
        return Card(panel);
    }

    public static TextBlock GroupHeader(string text)
    {
        var header = Text(text, "BodyStrongTextBlockStyle");
        header.Margin = new Thickness(2, 20, 0, 6);
        return header;
    }

    public static TextBlock PageHeader(string text)
    {
        // Subtitle, not Title: the window's own title bar already names it,
        // and a 28 px heading under it shouted the page name a second time.
        var header = Text(text, "SubtitleTextBlockStyle");
        header.Margin = new Thickness(0, 0, 0, 8);
        return header;
    }

    public static ToggleSwitch Toggle(bool isOn, Action<bool> changed)
    {
        var toggle = new ToggleSwitch
        {
            IsOn = isOn,
            OnContent = Loc.T("On", "켬"),
            OffContent = Loc.T("Off", "끔"),
            MinWidth = 0
        };
        toggle.Toggled += (_, _) => changed(toggle.IsOn);
        return toggle;
    }

    // Icon + label, the Fluent command button shape.
    public static Button IconButton(string glyph, string label, Action onClick, bool accent = false)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        content.Children.Add(Icon(glyph, 14));
        content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });

        var button = accent
            ? Xaml<Button>("<Button $NS Style='{StaticResource AccentButtonStyle}'/>")
            : new Button();
        button.Content = content;
        // Content that is a panel gives the button no accessible name, so
        // screen readers (and UI Automation) saw an unnamed button.
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, label);
        button.Click += (_, _) => onClick();
        return button;
    }

    // A circle with the level's icon, tinted with the matching system status
    // colour so severity reads before the text does.
    public static Border LevelBadge(NotificationLevel level, double size = 32)
    {
        var (fg, bg) = level switch
        {
            NotificationLevel.Success => ("SystemFillColorSuccessBrush", "SystemFillColorSuccessBackgroundBrush"),
            NotificationLevel.Attention => ("SystemFillColorCautionBrush", "SystemFillColorCautionBackgroundBrush"),
            NotificationLevel.Warn => ("SystemFillColorCautionBrush", "SystemFillColorCautionBackgroundBrush"),
            NotificationLevel.Error => ("SystemFillColorCriticalBrush", "SystemFillColorCriticalBackgroundBrush"),
            NotificationLevel.Critical => ("SystemFillColorCriticalBrush", "SystemFillColorCriticalBackgroundBrush"),
            _ => ("AccentTextFillColorPrimaryBrush", "SystemFillColorAttentionBackgroundBrush")
        };

        var badge = Xaml<Border>(
            $"<Border $NS Width='{size}' Height='{size}' CornerRadius='{size / 2}' " +
            $"Background='{{ThemeResource {bg}}}'/>");
        var icon = Icon(Glyphs.ForLevel(level), size * 0.5, fg);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment = VerticalAlignment.Center;
        badge.Child = icon;
        return badge;
    }

    // A small rounded label, used for status ("Suppressed: muted").
    public static Border Chip(string text, string? glyph = null, string background = "SystemFillColorNeutralBackgroundBrush")
    {
        var chip = Xaml<Border>(
            $"<Border $NS CornerRadius='4' Padding='8,2,8,3' Background='{{ThemeResource {background}}}'/>");
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (glyph is not null)
        {
            var icon = Icon(glyph, 11, "TextFillColorSecondaryBrush");
            icon.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(icon);
        }
        row.Children.Add(Secondary(text));
        chip.Child = row;
        return chip;
    }

    // Which agent sent a card, by colour. Fixed mid-tones that read on light
    // and dark alike, and deliberately none of the status colours the level
    // badge uses (green, yellow, red, accent blue) - an agent's colour must
    // never read as severity. Unknown senders get the neutral stroke colour.
    private static string AgentBrush(string agent) => agent switch
    {
        Notipet.Shared.PayloadMapper.SourceClaude => "#D97757",
        Notipet.Shared.PayloadMapper.SourceCodex => "#8E6CEF",
        _ => "{ThemeResource ControlStrongStrokeColorDefaultBrush}"
    };

    // A dot in the agent's colour, always next to the agent's name: colour is
    // never the only carrier. (A stripe down the card's edge said the same
    // thing a second time, louder.)
    public static Microsoft.UI.Xaml.Shapes.Ellipse AgentDot(string agent, double size = 8)
    {
        var dot = Xaml<Microsoft.UI.Xaml.Shapes.Ellipse>($"<Ellipse $NS Width='{size}' Height='{size}' Fill='{AgentBrush(agent)}'/>");
        dot.VerticalAlignment = VerticalAlignment.Center;
        return dot;
    }

    // The agent's name with its dot, as a chip.
    public static Border AgentChip(string agent, string label)
    {
        var chip = Xaml<Border>(
            "<Border $NS CornerRadius='4' Padding='8,2,8,3' Background='{ThemeResource SystemFillColorNeutralBackgroundBrush}'/>");
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(AgentDot(agent));
        row.Children.Add(Text(label, "CaptionTextBlockStyle", wrap: false));
        chip.Child = row;
        return chip;
    }

    // Draws our own title bar so the Mica backdrop runs edge to edge, the way
    // Windows 11 apps look. Returns the element that must be passed to
    // SetTitleBar so dragging still works.
    public static Grid TitleBar(Window window, string title)
    {
        var bar = new Grid { Height = 48, Padding = new Thickness(16, 0, 0, 0) };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
        var icon = Icon(Glyphs.Bell, 16, "AccentTextFillColorPrimaryBrush");
        row.Children.Add(icon);
        var caption = Text(title, "CaptionTextBlockStyle", wrap: false);
        caption.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(caption);
        bar.Children.Add(row);

        window.ExtendsContentIntoTitleBar = true;
        window.SetTitleBar(bar);
        return bar;
    }

    public static void SetTitleText(Grid titleBar, string title)
    {
        if (titleBar.Children.Count > 0
            && titleBar.Children[0] is StackPanel row
            && row.Children.Count > 1
            && row.Children[1] is TextBlock caption)
        {
            caption.Text = title;
        }
    }

    // Size, placement, backdrop and icon for a tool window.
    public static void Chrome(Window window, int width, int height, bool nearTray, System.Drawing.Icon? appIcon)
    {
        try { window.SystemBackdrop = new MicaBackdrop(); } catch { }

        var hwnd = WindowNative.GetWindowHandle(window);
        var scale = GetDpiForWindow(hwnd) / 96.0;
        var w = (int)(width * scale);
        var h = (int)(height * scale);
        window.AppWindow.Resize(new SizeInt32(w, h));

        GetCursorPos(out var cursor);
        var work = DisplayArea.GetFromPoint(new PointInt32(cursor.X, cursor.Y), DisplayAreaFallback.Nearest).WorkArea;
        var margin = (int)(16 * scale);
        window.AppWindow.Move(nearTray
            ? new PointInt32(work.X + Math.Max(0, work.Width - w - margin), work.Y + Math.Max(0, work.Height - h - margin))
            : new PointInt32(work.X + Math.Max(0, (work.Width - w) / 2), work.Y + Math.Max(0, (work.Height - h) / 2)));

        if (appIcon is not null)
        {
            try { window.AppWindow.SetIcon(Win32Interop.GetIconIdFromIcon(appIcon.Handle)); } catch { }
        }

        // Hide instead of destroying: closing the last window would otherwise
        // take the tray app down, and reopening would lose state.
        window.AppWindow.Closing += (_, e) =>
        {
            e.Cancel = true;
            window.AppWindow.Hide();
        };
    }

    // Shows a window and actually puts it in front, which Show()/Activate()
    // alone do not do.
    //
    // A tray click leaves some other application as the foreground process, and
    // Windows then refuses our SetForegroundWindow: an already-open window
    // stayed buried behind whatever was on top, so clicking the tray icon
    // looked like nothing happened - like a dead process. The documented way
    // out is to attach to the foreground thread's input queue first, which
    // makes the two threads share a foreground state, and to fall back to a
    // topmost flick that at least raises the window without stealing focus.
    public static void BringToFront(Window window)
    {
        var hwnd = WindowNative.GetWindowHandle(window);
        window.AppWindow.Show(true);

        // Show() does not undo a minimise.
        if (window.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
        {
            try { presenter.Restore(); } catch { }
        }

        window.Activate();
        if (SetForegroundWindow(hwnd)) return;

        var foreground = GetForegroundWindow();
        var owner = foreground == IntPtr.Zero ? 0u : GetWindowThreadProcessId(foreground, out _);
        var self = GetCurrentThreadId();
        if (owner != 0 && owner != self && AttachThreadInput(self, owner, true))
        {
            try
            {
                BringWindowToTop(hwnd);
                SetForegroundWindow(hwnd);
            }
            finally
            {
                AttachThreadInput(self, owner, false);
            }
            return;
        }

        // Last resort: raise it without focus rather than leave it hidden.
        SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        SetWindowPos(hwnd, HwndNoTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    // "Is this window up?" - visible, not minimised, and not covered by any
    // other window. Decides whether a tray click closes the window or raises it.
    //
    // Not GetForegroundWindow(): pressing the mouse on the notification area
    // activates the taskbar before our click message arrives, so on a real
    // click the foreground is Shell_TrayWnd, never us, and a toggle built on it
    // would never close. (A synthetic PostMessage does not move the foreground,
    // so a script cannot see that difference - it was found by reasoning about
    // the shell, and has to be checked by hand.) Instead this walks the windows
    // above ours in z-order and asks whether any of them overlaps it, ignoring
    // the topmost band (taskbar, overflow flyout, Start) and our own popups.
    public static bool IsInFront(Window window)
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(window);
            if (!window.AppWindow.IsVisible || IsIconic(hwnd) || IsCloaked(hwnd)) return false;
            if (GetForegroundWindow() == hwnd) return true;
            if (!TryFrame(hwnd, out var ours)) return true;

            var above = GetWindow(hwnd, GwHwndPrev);
            for (var guard = 0; above != IntPtr.Zero && guard < 2048; guard++, above = GetWindow(above, GwHwndPrev))
            {
                if (!IsWindowVisible(above) || IsIconic(above) || IsCloaked(above)) continue;
                var exStyle = (long)GetWindowLongPtr(above, GwlExStyle);
                if ((exStyle & (WsExTopmost | WsExToolWindow | WsExNoActivate)) != 0) continue;
                if (GetWindow(above, GwOwner) == hwnd) continue;   // our own flyouts and popups
                if (!TryFrame(above, out var theirs)) continue;
                if (theirs.Right <= theirs.Left || theirs.Bottom <= theirs.Top) continue;

                var overlaps = theirs.Left < ours.Right && ours.Left < theirs.Right
                               && theirs.Top < ours.Bottom && ours.Top < theirs.Bottom;
                if (overlaps) return false;
            }
            return true;
        }
        catch
        {
            // Unsure: raising is the harmless answer, hiding is not.
            return false;
        }
    }

    private static bool IsCloaked(IntPtr hwnd) =>
        DwmGetWindowAttribute(hwnd, DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    // The visible frame. GetWindowRect includes the ~7px invisible resize
    // border, which would make two windows snapped side by side "overlap".
    private static bool TryFrame(IntPtr hwnd, out NativeRect rect) =>
        DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out rect, Marshal.SizeOf<NativeRect>()) == 0;

    // Esc hides, as it does in every Windows tool window.
    public static void HideOnEscape(Window window, UIElement root)
    {
        var escape = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = global::Windows.System.VirtualKey.Escape };
        escape.Invoked += (_, e) =>
        {
            e.Handled = true;
            window.AppWindow.Hide();
        };
        root.KeyboardAccelerators.Add(escape);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNoTopmost = new(-2);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attachTo, uint attachFrom, bool attach);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private const uint GwHwndPrev = 3;
    private const uint GwOwner = 4;
    private const int GwlExStyle = -20;
    private const long WsExTopmost = 0x00000008;
    private const long WsExToolWindow = 0x00000080;
    private const long WsExNoActivate = 0x08000000;
    private const int DwmwaExtendedFrameBounds = 9;
    private const int DwmwaCloaked = 14;

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out NativeRect value, int size);
}
