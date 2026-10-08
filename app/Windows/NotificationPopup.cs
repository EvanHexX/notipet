using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Notipet.Core;
using Windows.Graphics;
using WinRT.Interop;

namespace Notipet.Windows;

// notipet's own notification pop-up, used instead of the shell balloon when
// the user wants one that stays until clicked or that shows over full-screen
// apps - two things a tray balloon cannot do (PopupSettings).
//
// It never takes the keyboard focus: it is shown without activation, so
// whatever the user was typing into keeps the caret. A click on it stops the
// ringing alarm and closes it; the close button only closes it; "Open in ..."
// jumps to the thread.
[SupportedOSPlatform("windows10.0.19041.0")]
internal sealed class NotificationPopup
{
    public const double WidthDip = 380;

    private readonly Window _window = new();
    private readonly IntPtr _hwnd;
    private readonly DispatcherTimer _timer = new();
    private bool _closed;

    public NotificationEnvelope Envelope { get; }
    public SizeInt32 PixelSize { get; }
    public event Action<NotificationPopup>? Closed;

    public NotificationPopup(NotificationEnvelope envelope, Uri? link, Action onClick, Action onOpenThread)
    {
        Envelope = envelope;
        _hwnd = WindowNative.GetWindowHandle(_window);

        var heightDip = link is null ? 116 : 148;
        var scale = GetDpiForWindow(_hwnd) / 96.0;
        PixelSize = new SizeInt32((int)(WidthDip * scale), (int)(heightDip * scale));

        _window.Content = Build(envelope, link, onClick, onOpenThread);
        _window.Title = envelope.Title;
        try { _window.SystemBackdrop = new DesktopAcrylicBackdrop(); } catch { }

        // A small borderless card: no title bar, no taskbar button, no Alt+Tab
        // entry, not resizable.
        var presenter = OverlappedPresenter.Create();
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.SetBorderAndTitleBar(true, false);
        _window.AppWindow.SetPresenter(presenter);
        _window.AppWindow.IsShownInSwitchers = false;
        _window.AppWindow.Resize(PixelSize);

        _timer.Tick += (_, _) => Close();
        _window.AppWindow.Closing += (_, _) => MarkClosed();
    }

    // overFullScreen: drawn above everything, full-screen apps included.
    // Otherwise, when a full-screen app is in front, the pop-up goes right
    // under it and is there when the user comes out.
    public void Show(PointInt32 position, bool topmost, bool belowForeground)
    {
        if (_window.AppWindow.Presenter is OverlappedPresenter presenter) presenter.IsAlwaysOnTop = topmost;
        _window.AppWindow.Move(position);
        _window.AppWindow.Show(false);   // false: do not activate - never steal focus

        const uint flags = SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow;
        if (topmost)
        {
            // Raise to the top of the topmost band: a full-screen video player
            // is often topmost itself.
            SetWindowPos(_hwnd, HwndTopmost, 0, 0, 0, 0, flags);
        }
        else if (belowForeground)
        {
            var foreground = GetForegroundWindow();
            if (foreground != IntPtr.Zero && foreground != _hwnd) SetWindowPos(_hwnd, foreground, 0, 0, 0, 0, flags);
        }
    }

    // Back to the top of the topmost band, without activation. A full-screen
    // video player that turns topmost after this pop-up appeared would
    // otherwise bury it - and a pop-up that stays until clicked must be seen.
    public void RaiseTopmost()
    {
        if (_closed) return;
        SetWindowPos(_hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    public void MoveTo(PointInt32 position)
    {
        if (!_closed) _window.AppWindow.Move(position);
    }

    // Closes itself after a while - unless the pointer is on it, which pauses
    // the countdown so a pop-up does not vanish while being read.
    public void CloseAfter(TimeSpan delay)
    {
        _timer.Interval = delay;
        _timer.Start();
        if (_window.Content is UIElement root)
        {
            root.PointerEntered += (_, _) => _timer.Stop();
            root.PointerExited += (_, _) => { if (!_closed) _timer.Start(); };
        }
    }

    public void Close()
    {
        if (_closed) return;
        MarkClosed();
        try { _window.Close(); } catch { }
    }

    private void MarkClosed()
    {
        if (_closed) return;
        _closed = true;
        _timer.Stop();
        Closed?.Invoke(this);
    }

    private UIElement Build(NotificationEnvelope envelope, Uri? link, Action onClick, Action onOpenThread)
    {
        // stripe | badge | text | close
        var grid = new Grid { ColumnSpacing = 12, Padding = new Thickness(14, 12, 8, 12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        // Transparent, not null: the gaps between controls must take clicks too.
        grid.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

        grid.Children.Add(Fluent.AgentStripe(envelope.SourceId));

        var badge = Fluent.LevelBadge(envelope.Level, 28);
        badge.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(badge, 1);
        grid.Children.Add(badge);

        var content = new StackPanel { Spacing = 2 };
        content.Children.Add(Fluent.Text(envelope.Title, "BodyStrongTextBlockStyle", wrap: false));
        if (!string.IsNullOrWhiteSpace(envelope.Body))
        {
            var body = Fluent.Text(envelope.Body, "BodyTextBlockStyle");
            body.MaxLines = 2;
            body.TextTrimming = TextTrimming.CharacterEllipsis;
            content.Children.Add(body);
        }

        // Who and where, one line: "Codex · shop · checkout refactor".
        var meta = string.Join("  ·  ", new[] { UiText.Agent(envelope.SourceId), envelope.Project, envelope.ThreadTitle }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        content.Children.Add(Fluent.Text(meta, "CaptionTextBlockStyle", "TextFillColorSecondaryBrush", wrap: false));

        if (link is not null)
        {
            var open = Fluent.IconButton(Glyphs.OpenInApp, UiText.OpenThreadIn(envelope.SourceId), () =>
            {
                onOpenThread();
                onClick();
                Close();
            });
            open.Margin = new Thickness(0, 6, 0, 0);
            content.Children.Add(open);
        }
        Grid.SetColumn(content, 2);
        grid.Children.Add(content);

        var close = Fluent.Xaml<Button>(
            "<Button $NS Background='Transparent' BorderThickness='0' Padding='6' VerticalAlignment='Top' " +
            "CornerRadius='{ThemeResource ControlCornerRadius}'/>");
        close.Content = Fluent.Icon(Glyphs.Cancel, 12);
        var closeLabel = Loc.T("Close", "닫기");
        ToolTipService.SetToolTip(close, closeLabel);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(close, closeLabel);
        close.Click += (_, _) => Close();
        Grid.SetColumn(close, 3);
        grid.Children.Add(close);

        // A click on the card (not on a button) is "I've seen it": the alarm
        // stops and the pop-up goes, like clicking the Windows balloon did.
        ToolTipService.SetToolTip(grid, Loc.T("Click to stop the alarm and close", "클릭하면 알람을 멈추고 닫습니다"));
        grid.Tapped += (_, e) =>
        {
            for (var node = e.OriginalSource as DependencyObject; node is not null && node != grid; node = VisualTreeHelper.GetParent(node))
            {
                if (node is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase) return;
            }
            onClick();
            Close();
        };

        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(grid, $"notipet: {envelope.Title}");
        return grid;
    }

    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
}
