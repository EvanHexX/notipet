using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Notipet.Core;
using Windows.ApplicationModel.DataTransfer;

namespace Notipet.Windows;

// Recent notifications as Fluent cards.
//
// Replaces a tray submenu that could only show one truncated line per entry -
// exactly the part you did not need. The body that says why the agent stopped,
// and the reason a notification was suppressed, are the whole point of
// looking, and neither fits in a menu item.
[SupportedOSPlatform("windows10.0.19041.0")]
internal sealed class HistoryWindow
{
    private enum Filter { All, Delivered, Suppressed }

    // Rendering is bounded independently of the history limit: a thousand
    // cards is a slow window, and nobody scrolls that far.
    private const int PageSize = 100;

    private readonly Window _window = new();
    private readonly INotipetHost _host;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(30) };

    private Grid _titleBar = null!;
    private TextBlock _count = null!;
    private StackPanel _list = null!;
    private Filter _filter = Filter.All;
    private int _shown = PageSize;

    // Collapsed project groups, by group key. Kept here, not in the controls:
    // Refresh rebuilds every card on each new notification and every 30 s.
    private readonly HashSet<string> _collapsed = new(StringComparer.OrdinalIgnoreCase);

    // This refresh's group headers, to put keyboard focus back after a fold.
    private readonly Dictionary<string, Button> _headers = new(StringComparer.OrdinalIgnoreCase);

    public HistoryWindow(INotipetHost host)
    {
        _host = host;
        Build();
        Fluent.Chrome(_window, 600, 720, nearTray: true, host.AppIcon);

        // Relative times ("3 min ago") go stale while the window is open.
        _clock.Tick += (_, _) => Refresh();
        _window.AppWindow.Changed += (_, _) =>
        {
            if (_window.AppWindow.IsVisible) _clock.Start();
            else _clock.Stop();
        };
    }

    public void Activate()
    {
        _shown = PageSize;
        Refresh();
        Fluent.BringToFront(_window);
        _clock.Start();
    }

    // The tray icon's click: close the window when it is up, otherwise bring
    // it up (from hidden, minimised, or buried under other windows).
    public void Toggle()
    {
        if (Fluent.IsInFront(_window)) _window.AppWindow.Hide();
        else Activate();
    }

    public void Close()
    {
        _clock.Stop();
        try { _window.Close(); } catch { }
    }

    // Called when the language changes: everything with text is rebuilt.
    public void Relocalize()
    {
        Build();
        Refresh();
    }

    private void Build()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        _titleBar = Fluent.TitleBar(_window, Loc.T("notipet - Recent", "notipet - 최근 알림"));
        root.Children.Add(_titleBar);

        var header = BuildHeader();
        Grid.SetRow(header, 1);
        root.Children.Add(header);

        _list = new StackPanel { Spacing = 8, Padding = new Thickness(20, 4, 20, 20) };
        var scroll = new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 2);
        root.Children.Add(scroll);

        Fluent.HideOnEscape(_window, root);
        _window.Content = root;
        _window.Title = Loc.T("notipet - Recent", "notipet - 최근 알림");
    }

    private Grid BuildHeader()
    {
        var header = new Grid { Padding = new Thickness(20, 4, 20, 12), ColumnSpacing = 8, RowSpacing = 10 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        titleRow.Children.Add(Fluent.Text(Loc.T("Recent notifications", "최근 알림"), "TitleTextBlockStyle", wrap: false));
        _count = Fluent.Secondary("", "BodyTextBlockStyle");
        _count.VerticalAlignment = VerticalAlignment.Bottom;
        _count.Margin = new Thickness(0, 0, 0, 3);
        titleRow.Children.Add(_count);
        header.Children.Add(titleRow);

        // Filter on the left of the second row, commands on the right.
        var filter = new ComboBox { MinWidth = 150 };
        filter.Items.Add(Loc.T("All", "전체"));
        filter.Items.Add(Loc.T("Delivered", "전달됨"));
        filter.Items.Add(Loc.T("Suppressed", "차단됨"));
        filter.SelectedIndex = (int)_filter;
        filter.SelectionChanged += (_, _) =>
        {
            _filter = (Filter)Math.Max(0, filter.SelectedIndex);
            _shown = PageSize;
            Refresh();
        };
        Grid.SetRow(filter, 1);
        header.Children.Add(filter);

        var commands = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        commands.Children.Add(IconOnly(Glyphs.Refresh, Loc.T("Refresh", "새로 고침"), Refresh));
        commands.Children.Add(IconOnly(Glyphs.Settings, Loc.T("Settings", "옵션"), () => _host.ShowSettings("history")));
        commands.Children.Add(BuildClearButton());
        Grid.SetRow(commands, 1);
        Grid.SetColumn(commands, 1);
        header.Children.Add(commands);

        return header;
    }

    // Clearing cannot be undone, so it asks first - in a flyout anchored to the
    // button, which is lighter than a dialog and keeps the eye in one place.
    private Button BuildClearButton()
    {
        var clear = Fluent.IconButton(Glyphs.Delete, Loc.T("Clear", "비우기"), () => { });

        var confirm = new Flyout();
        var panel = new StackPanel { Spacing = 12, MaxWidth = 260 };
        panel.Children.Add(Fluent.Text(Loc.T("Clear all recent notifications?", "최근 알림을 모두 지울까요?"), "BodyStrongTextBlockStyle"));
        panel.Children.Add(Fluent.Secondary(Loc.T("This cannot be undone.", "되돌릴 수 없습니다.")));
        panel.Children.Add(Fluent.IconButton(Glyphs.Delete, Loc.T("Clear all", "모두 지우기"), () =>
        {
            _host.ClearHistory();
            confirm.Hide();
            Refresh();
        }, accent: true));
        confirm.Content = panel;
        clear.Flyout = confirm;
        return clear;
    }

    public void Refresh()
    {
        if (_list is null) return;

        var all = _host.History.Recent(int.MaxValue);
        var filtered = _filter switch
        {
            Filter.Delivered => all.Where(e => e.Accepted).ToList(),
            Filter.Suppressed => all.Where(e => !e.Accepted).ToList(),
            _ => all.ToList()
        };

        _count.Text = Loc.T($"{all.Count} kept", $"{all.Count}개 보관 중");
        _list.Children.Clear();
        _headers.Clear();

        if (filtered.Count == 0)
        {
            _list.Children.Add(EmptyState(all.Count == 0));
            return;
        }

        // Paging counts cards, not groups, so "Show more" means the same thing
        // it always did; the shown cards are then split by project.
        var now = DateTimeOffset.Now;
        var groups = HistoryGrouping.Group(filtered.Take(_shown).ToList());
        for (var i = 0; i < groups.Count; i++)
        {
            var group = groups[i];
            var collapsed = _collapsed.Contains(group.Key);
            _list.Children.Add(BuildGroupHeader(group, collapsed, first: i == 0));
            if (collapsed) continue;
            foreach (var entry in group.Entries)
            {
                _list.Children.Add(BuildCard(entry, now));
            }
        }

        if (filtered.Count > _shown)
        {
            var more = Fluent.IconButton(Glyphs.History,
                Loc.T($"Show {Math.Min(PageSize, filtered.Count - _shown)} more", $"{Math.Min(PageSize, filtered.Count - _shown)}개 더 보기"),
                () => { _shown += PageSize; Refresh(); });
            more.HorizontalAlignment = HorizontalAlignment.Center;
            more.Margin = new Thickness(0, 8, 0, 0);
            _list.Children.Add(more);
        }
    }

    // A project's header: chevron, name, how many cards. The whole row is one
    // button that folds the group, so it is a big target and reads as one
    // thing to a screen reader. Hovering shows the folders it came from - two
    // repositories can share a folder name.
    private UIElement BuildGroupHeader(HistoryGroup group, bool collapsed, bool first)
    {
        var name = group.Project ?? UiText.OtherProject();

        // A grid, not a horizontal StackPanel: a StackPanel gives the name
        // unlimited width, so a long one is cut off mid-glyph instead of ending
        // in an ellipsis, and pushes the count out of sight.
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var chevron = Fluent.Icon(collapsed ? Glyphs.ChevronRight : Glyphs.ChevronDown, 12, "TextFillColorSecondaryBrush");
        chevron.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(chevron);
        var folder = Fluent.Icon(group.IsOther ? Glyphs.Filter : Glyphs.Folder, 14, "TextFillColorSecondaryBrush");
        folder.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(folder, 1);
        row.Children.Add(folder);
        var title = Fluent.Text(name, "BodyStrongTextBlockStyle", wrap: false);
        title.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(title, 2);
        row.Children.Add(title);
        var count = Fluent.Secondary(group.Entries.Count.ToString(), "BodyTextBlockStyle");
        count.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(count, 3);
        row.Children.Add(count);

        var header = Fluent.Xaml<Button>(
            "<Button $NS Background='Transparent' BorderThickness='0' Padding='6,4' " +
            "HorizontalAlignment='Stretch' HorizontalContentAlignment='Left' " +
            "CornerRadius='{ThemeResource ControlCornerRadius}'/>");
        header.Content = row;
        header.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        header.Margin = new Thickness(-6, first ? 0 : 12, 0, 0);
        _headers[group.Key] = header;

        var tip = group.IsOther
            ? Loc.T("Notifications that did not say which project they came from",
                    "어느 프로젝트에서 왔는지 알려 주지 않은 알림")
            : group.Paths.Count > 0 ? string.Join(Environment.NewLine, group.Paths) : name;
        ToolTipService.SetToolTip(header, tip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(header,
            $"{name}, {group.Entries.Count}, " + (collapsed ? Loc.T("collapsed", "접힘") : Loc.T("expanded", "펼침")));

        header.Click += (sender, _) =>
        {
            // Refresh rebuilds every header, including this one. Put focus on
            // the new one, the way it arrived (keyboard stays keyboard), so a
            // keyboard user can fold it back and a screen reader announces the
            // new state.
            var how = sender is Control { FocusState: not FocusState.Unfocused } control ? control.FocusState : FocusState.Programmatic;
            if (!_collapsed.Remove(group.Key)) _collapsed.Add(group.Key);
            Refresh();
            if (_headers.TryGetValue(group.Key, out var rebuilt)) rebuilt.Focus(how);
        };
        return header;
    }

    private UIElement EmptyState(bool nothingAtAll)
    {
        var panel = new StackPanel
        {
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 80, 0, 0)
        };
        var icon = Fluent.Icon(nothingAtAll ? Glyphs.Bell : Glyphs.Filter, 40, "TextFillColorTertiaryBrush");
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        panel.Children.Add(icon);

        var title = Fluent.Text(
            nothingAtAll ? Loc.T("No notifications yet", "아직 알림이 없습니다") : Loc.T("Nothing matches this filter", "이 필터에 맞는 알림이 없습니다"),
            "SubtitleTextBlockStyle");
        title.HorizontalAlignment = HorizontalAlignment.Center;
        title.TextAlignment = TextAlignment.Center;
        panel.Children.Add(title);

        if (nothingAtAll)
        {
            var hint = Fluent.Secondary(Loc.T(
                "When Claude Code or Codex needs you, it shows up here.",
                "Claude Code나 Codex가 나를 찾으면 여기에 나타납니다."), "BodyTextBlockStyle");
            hint.HorizontalAlignment = HorizontalAlignment.Center;
            hint.TextAlignment = TextAlignment.Center;
            panel.Children.Add(hint);
        }
        return panel;
    }

    private UIElement BuildCard(HistoryEntry entry, DateTimeOffset now)
    {
        var envelope = entry.Envelope;
        var link = _host.ThreadLink(entry);

        // Columns: agent stripe | level badge | text | actions. A card whose
        // thread can be opened is itself clickable, with a hand cursor.
        var grid = link is null ? new Grid { ColumnSpacing = 12 } : new LinkGrid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var stripe = Fluent.AgentStripe(envelope.SourceId);
        ToolTipService.SetToolTip(stripe, UiText.Agent(envelope.SourceId));
        grid.Children.Add(stripe);

        var badge = Fluent.LevelBadge(envelope.Level);
        badge.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(badge, 1);
        grid.Children.Add(badge);

        var content = new StackPanel { Spacing = 4 };
        content.Children.Add(Fluent.Text(envelope.Title, "BodyStrongTextBlockStyle"));

        var thread = BuildThreadLine(entry, link);
        if (thread is not null) content.Children.Add(thread);

        TextBlock? body = null;
        if (!string.IsNullOrWhiteSpace(envelope.Body))
        {
            // Wrapped and never truncated - the reason this window exists.
            body = Fluent.Text(envelope.Body);
            body.IsTextSelectionEnabled = true;
            content.Children.Add(body);
        }

        // The agent moved from this line to its own chip below.
        var meta = new[]
        {
            UiText.Level(envelope.Level),
            UiText.Relative(entry.LastAt, now),
            entry.Count > 1 ? $"×{entry.Count}" : null
        };
        var metaText = Fluent.Secondary(string.Join("  ·  ", meta.Where(m => !string.IsNullOrEmpty(m))));
        ToolTipService.SetToolTip(metaText, entry.LastAt.ToString("yyyy-MM-dd HH:mm:ss"));
        content.Children.Add(metaText);

        var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 2, 0, 0) };
        chips.Children.Add(Fluent.AgentChip(envelope.SourceId, UiText.Agent(envelope.SourceId)));
        if (!entry.Accepted)
        {
            chips.Children.Add(Fluent.Chip(
                Loc.T("Suppressed: ", "차단됨: ") + UiText.Reason(entry.SuppressedReason),
                Glyphs.BellOff, "SystemFillColorCautionBackgroundBrush"));
        }
        else
        {
            foreach (var delivery in entry.Deliveries)
            {
                var glyph = delivery.Channel == "windows_sound" ? Glyphs.Volume : Glyphs.Message;
                var background = delivery.Status == "failed"
                    ? "SystemFillColorCriticalBackgroundBrush"
                    : "SystemFillColorNeutralBackgroundBrush";
                chips.Children.Add(Fluent.Chip(
                    $"{UiText.Channel(delivery.Channel)} {UiText.Status(delivery.Status)}", glyph, background));
            }
        }
        content.Children.Add(chips);

        if (!string.IsNullOrWhiteSpace(envelope.SourceCwd))
        {
            // A grid so a long path ends in an ellipsis instead of being cut.
            var cwd = new Grid { ColumnSpacing = 6 };
            cwd.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            cwd.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            cwd.Children.Add(Fluent.Icon(Glyphs.Folder, 12, "TextFillColorTertiaryBrush"));
            var path = Fluent.Text(envelope.SourceCwd!, "CaptionTextBlockStyle", "TextFillColorTertiaryBrush", wrap: false);
            Grid.SetColumn(path, 1);
            ToolTipService.SetToolTip(path, envelope.SourceCwd);
            cwd.Children.Add(path);
            content.Children.Add(cwd);
        }

        Grid.SetColumn(content, 2);
        grid.Children.Add(content);

        var actions = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Top };
        if (link is not null)
        {
            actions.Children.Add(IconOnly(Glyphs.OpenInApp, UiText.OpenThreadIn(envelope.SourceId), () => _host.OpenThread(entry)));
        }
        actions.Children.Add(IconOnly(Glyphs.Copy, Loc.T("Copy", "복사"), () => Copy(entry)));
        if (!string.IsNullOrWhiteSpace(envelope.SourceCwd) && Directory.Exists(envelope.SourceCwd))
        {
            actions.Children.Add(IconOnly(Glyphs.Folder, Loc.T("Open folder", "폴더 열기"), () => _host.OpenFolder(envelope.SourceCwd!)));
        }
        actions.Children.Add(IconOnly(Glyphs.Cancel, Loc.T("Remove", "삭제"), () =>
        {
            _host.RemoveHistoryEntry(envelope.Id);
            Refresh();
        }));
        Grid.SetColumn(actions, 3);
        grid.Children.Add(actions);

        if (link is not null)
        {
            // Click anywhere on the card to open the thread - except on its
            // buttons, which do their own thing, and on the body, where a
            // click (or the first click of a double-click) is how text gets
            // selected. A transparent background makes the gaps clickable too.
            grid.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
            grid.Tapped += (_, e) =>
            {
                for (var node = e.OriginalSource as DependencyObject; node is not null && node != grid;
                     node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node))
                {
                    if (node is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase || node == body) return;
                }
                _host.OpenThread(entry);
            };
        }

        var card = Fluent.Card(grid, "14,12");
        // Suppressed entries recede: they did not reach you, and the ones that
        // did are what you usually came to find.
        if (!entry.Accepted) card.Opacity = 0.78;
        return card;
    }

    // The thread a card belongs to: its name, or a short id when nothing gave
    // it a name. When the agent's app can open it, the line is a link that goes
    // straight there - the "which window was that?" hunt, skipped.
    private UIElement? BuildThreadLine(HistoryEntry entry, Uri? link)
    {
        var envelope = entry.Envelope;
        var title = entry.DisplayThreadTitle;
        var id = envelope.SourceSession;
        if (title is null && id is null && link is null) return null;

        var text = title ?? ShortId(id) ?? UiText.OpenThreadIn(envelope.SourceId);
        var brush = link is null ? "TextFillColorSecondaryBrush" : "AccentTextFillColorPrimaryBrush";

        // Icon | label (takes the rest, ends in an ellipsis) | open glyph. A
        // grid, because a horizontal StackPanel would give the label unlimited
        // width: a long title would be cut mid-glyph and push the open glyph -
        // the only sign this line is a link - out of sight.
        var row = new Grid { ColumnSpacing = 6 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = Fluent.Icon(Glyphs.Thread, 12, brush);
        icon.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(icon);
        var label = Fluent.Text(text, "CaptionTextBlockStyle", brush, wrap: false);
        label.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(label, 1);
        row.Children.Add(label);

        var details = string.Join(Environment.NewLine, new[] { title, id }.Where(s => !string.IsNullOrEmpty(s)));
        if (link is null)
        {
            if (details.Length > 0) ToolTipService.SetToolTip(row, details);
            return row;
        }

        var open = Fluent.Icon(Glyphs.OpenInApp, 10, brush);
        open.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(open, 2);
        row.Children.Add(open);

        var action = UiText.OpenThreadIn(envelope.SourceId);
        var button = new HyperlinkButton { Content = row, Padding = new Thickness(0), MinHeight = 0 };
        ToolTipService.SetToolTip(button, details.Length > 0 ? action + Environment.NewLine + details : action);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, $"{action}: {text}");
        button.Click += (_, _) => _host.OpenThread(entry);
        return button;
    }

    // A label for a thread with no name: the END of its id. Codex ids are
    // UUIDv7, whose first eight hex digits are a timestamp - every thread
    // started in the same minute would get the same label. The tail is random
    // in v4 and v7 alike.
    internal static string? ShortId(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        var compact = id.Replace("-", "");
        return "#" + (compact.Length > 8 ? compact[^8..] : compact);
    }

    private static void Copy(HistoryEntry entry)
    {
        try
        {
            var text = string.IsNullOrWhiteSpace(entry.Envelope.Body)
                ? entry.Envelope.Title
                : entry.Envelope.Title + Environment.NewLine + entry.Envelope.Body;
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
        }
        catch (Exception ex)
        {
            CrashLog.Write("HistoryWindow.Copy", ex);
        }
    }

    // A grid with the hand cursor, for a card that opens something when
    // clicked. ProtectedCursor can only be set from a subclass.
    private sealed class LinkGrid : Grid
    {
        public LinkGrid() =>
            ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.Hand);
    }

    private static Button IconOnly(string glyph, string tooltip, Action onClick)
    {
        var button = Fluent.Xaml<Button>(
            "<Button $NS Background='Transparent' BorderThickness='0' Padding='8,6' " +
            "CornerRadius='{ThemeResource ControlCornerRadius}'/>");
        button.Content = Fluent.Icon(glyph, 14);
        ToolTipService.SetToolTip(button, tooltip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, tooltip);
        button.Click += (_, _) => onClick();
        return button;
    }
}
