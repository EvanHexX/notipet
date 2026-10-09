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
    private TextBlock? _overflowText;
    private bool _closed;

    // Null for the overflow card.
    public NotificationEnvelope? Envelope { get; }
    public SizeInt32 PixelSize { get; }

    // Stays until clicked (PopupSettings.StayLevels for its level). When the
    // stack is full such a card is not dropped silently: it is counted on the
    // overflow card instead.
    public bool Stays { get; init; }

    public event Action<NotificationPopup>? Closed;

    public static NotificationPopup ForNotification(NotificationEnvelope envelope, Uri? link, bool stays, Action onClick, Action onOpenThread)
    {
        var popup = new NotificationPopup(envelope, 116, envelope.Title) { Stays = stays };
        popup._window.Content = popup.Build(envelope, link, onClick, onOpenThread);
        return popup;
    }

    // "+N more": the cards that stayed until clicked but no longer fit on
    // screen. Clicking it opens Recent notifications, where they all are.
    public static NotificationPopup ForOverflow(int count, Action onOpen, Action onDismiss)
    {
        var popup = new NotificationPopup(null, 60, "notipet");
        popup._window.Content = popup.BuildOverflow(onOpen, onDismiss);
        popup.SetOverflowCount(count);
        return popup;
    }

    private NotificationPopup(NotificationEnvelope? envelope, double heightDip, string title)
    {
        Envelope = envelope;
        _hwnd = WindowNative.GetWindowHandle(_window);

        var scale = GetDpiForWindow(_hwnd) / 96.0;
        PixelSize = new SizeInt32((int)(WidthDip * scale), (int)(heightDip * scale));

        _window.Title = title;
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

    public void SetOverflowCount(int count)
    {
        if (_overflowText is null) return;
        _overflowText.Text = Loc.T($"+{count} more waiting for you", $"외 {count}개 알림이 더 있습니다");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName((UIElement)_window.Content, $"notipet: {_overflowText.Text}");
    }

    private UIElement BuildOverflow(Action onOpen, Action onDismiss)
    {
        // bell | "+N more" + "Open Recent notifications" | close
        var grid = new Grid { ColumnSpacing = 12, Padding = new Thickness(18, 8, 8, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

        var icon = Fluent.Icon(Glyphs.History, 18, "AccentTextFillColorPrimaryBrush");
        icon.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(icon);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 0 };
        _overflowText = Fluent.Text("", "BodyStrongTextBlockStyle", wrap: false);
        text.Children.Add(_overflowText);
        text.Children.Add(Fluent.Text(Loc.T("Click to open Recent notifications", "클릭하면 최근 알림을 엽니다"),
            "CaptionTextBlockStyle", "TextFillColorSecondaryBrush", wrap: false));
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var close = Fluent.Xaml<Button>(
            "<Button $NS Background='Transparent' BorderThickness='0' Padding='6' VerticalAlignment='Center' " +
            "CornerRadius='{ThemeResource ControlCornerRadius}'/>");
        close.Content = Fluent.Icon(Glyphs.Cancel, 12);
        var closeLabel = Loc.T("Close", "닫기");
        ToolTipService.SetToolTip(close, closeLabel);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(close, closeLabel);
        close.Click += (_, _) => { onDismiss(); Close(); };
        Grid.SetColumn(close, 2);
        grid.Children.Add(close);

        grid.Tapped += (_, e) =>
        {
            for (var node = e.OriginalSource as DependencyObject; node is not null && node != grid; node = VisualTreeHelper.GetParent(node))
            {
                if (node is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase) return;
            }
            onOpen();
            Close();
        };
        return grid;
    }

    private UIElement Build(NotificationEnvelope envelope, Uri? link, Action onClick, Action onOpenThread)
    {
        // badge | text | open + close
        var grid = new Grid { ColumnSpacing = 12, Padding = new Thickness(14, 12, 8, 12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        // Transparent, not null: the gaps between controls must take clicks too.
        grid.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

        var badge = Fluent.LevelBadge(envelope.Level, 28);
        badge.VerticalAlignment = VerticalAlignment.Top;
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

        // Who and where, one line: "• Codex · shop · checkout refactor". The
        // dot is the agent's colour, as on the Recent notifications card.
        var metaRow = new Grid { ColumnSpacing = 6, Margin = new Thickness(0, 2, 0, 0) };
        metaRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        metaRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        metaRow.Children.Add(Fluent.AgentDot(envelope.SourceId, 7));
        var meta = string.Join("  ·  ", new[] { UiText.Agent(envelope.SourceId), envelope.Project, envelope.ThreadTitle }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        var metaText = Fluent.Text(meta, "CaptionTextBlockStyle", "TextFillColorSecondaryBrush", wrap: false);
        Grid.SetColumn(metaText, 1);
        metaRow.Children.Add(metaText);
        content.Children.Add(metaRow);
        Grid.SetColumn(content, 1);
        grid.Children.Add(content);

        // Open sits next to close as an icon: as a labelled button on a row of
        // its own it made every pop-up a third taller, and four of them
        // stacked reached halfway up the screen.
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        if (link is not null)
        {
            actions.Children.Add(SmallButton(Glyphs.OpenInApp, UiText.OpenThreadIn(envelope.SourceId), () =>
            {
                onOpenThread();
                onClick();
                Close();
            }));
        }
        actions.Children.Add(SmallButton(Glyphs.Cancel, Loc.T("Close", "닫기"), Close));
        Grid.SetColumn(actions, 2);
        grid.Children.Add(actions);

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

    private static Button SmallButton(string glyph, string label, Action onClick)
    {
        var button = Fluent.Xaml<Button>(
            "<Button $NS Background='Transparent' BorderThickness='0' Padding='6' " +
            "CornerRadius='{ThemeResource ControlCornerRadius}'/>");
        button.Content = Fluent.Icon(glyph, 12);
        ToolTipService.SetToolTip(button, label);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, label);
        button.Click += (_, _) => onClick();
        return button;
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
