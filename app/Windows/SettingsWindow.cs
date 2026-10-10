using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Notipet.Settings;
using Notipet.Shared;
using Notipet.Sound;

namespace Notipet.Windows;

// Settings, laid out like the Windows 11 Settings app: a navigation pane of
// pages, each page a column of cards with an icon, a title, a line of
// explanation and the control on the right.
//
// Immediate-apply, following quota-scope: every control writes to AppSettings
// and tells the host, which saves and redraws the tray. There is no OK/Cancel,
// so there is no state to get out of sync.
//
// Two hard-won rules apply here (docs/regression.md): no NumberBox - it kills
// this process at render time - and App.xaml must keep XamlControlsResources.
// Compiling and --self-test catch neither, so every change to this file has to
// be checked by actually opening the window.
[SupportedOSPlatform("windows10.0.19041.0")]
internal sealed class SettingsWindow
{
    private static readonly string[] Levels = { "info", "success", "attention", "warn", "error", "critical" };
    // 0 stands for "until acknowledged"; 1 is "once". One picker instead of a
    // mode combo plus a count combo that was only enabled for one of the modes.
    private const int UntilAck = 0;
    private static readonly int[] CountChoices = { 1, 2, 3, 4, 5, 6, 8, 10, UntilAck };
    // 0 = no deadline; see AppSettings.UnlimitedAlarmSeconds.
    private static readonly int[] MaxDurationChoices = { 30, 60, 120, 180, 300, 600, AppSettings.UnlimitedAlarmSeconds };
    private static readonly int[] IntervalChoices = { 300, 500, 700, 1000, 1500, 2000, 3000, 5000 };
    private static readonly int[] HistoryChoices = { 20, 50, 100, 200, 500, 1000 };

    private readonly Window _window = new();
    private readonly INotipetHost _host;
    private AppSettings Settings => _host.Settings;

    private Grid _titleBar = null!;
    private NavigationView _nav = null!;
    private ScrollViewer _content = null!;
    private string _page = "general";

    public SettingsWindow(INotipetHost host)
    {
        _host = host;
        Build();
        Fluent.Chrome(_window, 940, 720, nearTray: false, host.AppIcon);
    }

    public void Activate(string? page = null)
    {
        if (page is not null) _page = page;
        try
        {
            Build();
            Fluent.BringToFront(_window);

            // Otherwise the focus lands on the first nav item, which then
            // opens with a focus rectangle around it. Queued: on a first show,
            // XAML places its own initial focus after this returns.
            // Pointer, not Programmatic: no focus rectangle until Tab is pressed.
            _window.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => _content.Focus(FocusState.Pointer));
        }
        catch (Exception ex)
        {
            // A settings window must never take the daemon down with it.
            CrashLog.Write("SettingsWindow.Activate", ex);
        }
    }

    public void Close()
    {
        try { _window.Close(); } catch { }
    }

    public void Relocalize() => Build();

    public void ApplyTheme() => Fluent.ApplyTheme(_window);

    private void Changed() => _host.SettingsChanged();

    // ----- shell -----

    private void Build()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // "<what> - <app>", like the Recent window and Windows' own
        // convention: the part that tells two windows apart comes first, in
        // the taskbar and Alt+Tab.
        var title = Loc.T("Settings - Notipet", "설정 - Notipet");
        _titleBar = Fluent.TitleBar(_window, title);
        root.Children.Add(_titleBar);

        _nav = new NavigationView
        {
            PaneDisplayMode = NavigationViewPaneDisplayMode.Left,
            IsSettingsVisible = false,
            IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
            IsPaneToggleButtonVisible = false,
            IsTitleBarAutoPaddingEnabled = false,
            OpenPaneLength = 230
        };
        AddPage("general", Glyphs.Settings, Loc.T("General", "일반"));
        AddPage("sound", Glyphs.Volume, Loc.T("Sound", "사운드"));
        AddPage("desk", Glyphs.Person, Loc.T("At my desk", "자리 착석"));
        AddPage("quiet", Glyphs.Moon, Loc.T("Quiet hours", "방해금지"));
        AddPage("history", Glyphs.History, Loc.T("Recent notifications", "최근 알림"));
        AddPage("about", Glyphs.Info, Loc.T("About", "정보"));

        // A tab stop, so it can take the focus when the window opens.
        _content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, IsTabStop = true };
        _nav.Content = _content;
        _nav.SelectionChanged += (_, e) =>
        {
            if (e.SelectedItem is NavigationViewItem item && item.Tag is string tag)
            {
                _page = tag;
                ShowPage();
            }
        };

        Grid.SetRow(_nav, 1);
        root.Children.Add(_nav);

        Fluent.HideOnEscape(_window, root);
        _window.Content = root;
        Fluent.ApplyTheme(_window);
        _window.Title = title;

        _nav.SelectedItem = _nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (string)i.Tag == _page)
                            ?? _nav.MenuItems[0];
        ShowPage();
    }

    private void AddPage(string tag, string glyph, string label)
    {
        _nav.MenuItems.Add(new NavigationViewItem
        {
            Content = label,
            Tag = tag,
            Icon = Fluent.Icon(glyph)
        });
    }

    private void ShowPage()
    {
        var page = new StackPanel { Spacing = 4, Padding = new Thickness(32, 20, 32, 32), MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Stretch };
        try
        {
            switch (_page)
            {
                case "sound": BuildSound(page); break;
                case "desk": BuildDesk(page); break;
                case "quiet": BuildQuiet(page); break;
                case "history": BuildHistory(page); break;
                case "about": BuildAbout(page); break;
                default: BuildGeneral(page); break;
            }
        }
        catch (Exception ex)
        {
            // Construction-time failures show up in the page instead of taking
            // the daemon down. (Render-time XAML failures cannot be caught here;
            // that is why new controls must be tried in a real window.)
            CrashLog.Write($"SettingsWindow.{_page}", ex);
            page.Children.Add(Fluent.Text($"[{_page}] {ex.GetType().Name}: {ex.Message}"));
        }
        _content.Content = page;
    }

    // ----- pages -----

    private void BuildGeneral(StackPanel page)
    {
        page.Children.Add(Fluent.PageHeader(Loc.T("General", "일반")));

        var language = new ComboBox { MinWidth = 180 };
        language.Items.Add(Loc.T("Windows default", "Windows 설정 따름"));
        language.Items.Add("English");
        language.Items.Add("한국어");
        language.SelectedIndex = Settings.Language switch { "EN" => 1, "KO" => 2, _ => 0 };
        language.SelectionChanged += (_, _) =>
        {
            var picked = language.SelectedIndex switch { 1 => "EN", 2 => "KO", _ => "System" };
            if (picked == Settings.Language) return;
            Settings.Language = picked;
            // Rebuilds this window too; deferred so the ComboBox is not torn
            // down inside its own event.
            _window.DispatcherQueue.TryEnqueue(() => _host.LanguageChanged());
        };
        page.Children.Add(Fluent.SettingCard(Glyphs.Language,
            Loc.T("Language", "언어"),
            Loc.T("Menus, windows and notifications from Notipet itself", "메뉴, 창, Notipet 자체 알림에 쓰이는 언어"),
            language));

        var theme = new ComboBox { MinWidth = 180 };
        theme.Items.Add(Loc.T("Windows default", "Windows 설정 따름"));
        theme.Items.Add(Loc.T("Light", "라이트"));
        theme.Items.Add(Loc.T("Dark", "다크"));
        theme.SelectedIndex = Settings.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 };
        theme.SelectionChanged += (_, _) =>
        {
            var picked = theme.SelectedIndex switch { 1 => "Light", 2 => "Dark", _ => "System" };
            if (picked == Settings.Theme) return;
            Settings.Theme = picked;
            _window.DispatcherQueue.TryEnqueue(() => _host.ThemeChanged());
        };
        page.Children.Add(Fluent.SettingCard(Glyphs.Theme,
            Loc.T("Theme", "테마"),
            Loc.T("Windows, pop-ups and the tray menu", "창, 알림 창, 트레이 메뉴의 밝기"),
            theme));

        page.Children.Add(Fluent.SettingCard(Glyphs.Power,
            Loc.T("Start with Windows", "Windows 시작 시 실행"),
            Loc.T("So the first alert after a reboot is not lost",
                  "재부팅 뒤 첫 알림도 놓치지 않습니다"),
            Fluent.Toggle(_host.AutostartEnabled, on =>
            {
                if (_host.SetAutostart(on)) Changed();
            })));

        page.Children.Add(Fluent.GroupHeader(Loc.T("Notifications", "알림")));

        page.Children.Add(Fluent.SettingCard(Glyphs.Message,
            Loc.T("Show a notification", "알림 표시"),
            Loc.T("The pop-up next to the tray, alongside the sound", "소리와 함께 트레이 옆에 뜨는 알림"),
            Fluent.Toggle(Settings.Channel(AppSettings.Channels_TrayBalloon).Enabled, on =>
            {
                Settings.Channel(AppSettings.Channels_TrayBalloon).Enabled = on;
                Changed();
            })));

        // Either one switches from the Windows notification to notipet's own
        // pop-up: the shell decides how long a balloon stays and holds it back
        // over full-screen apps, so neither is possible with it.
        page.Children.Add(BuildStayLevels());

        page.Children.Add(Fluent.SettingCard(Glyphs.FullScreen,
            Loc.T("Show over full-screen apps", "전체화면 앱 위에도 표시"),
            Loc.T("Video, presentations, borderless games - not exclusive full-screen",
                  "영상, 발표, 창 모드 게임 위에도 뜹니다 (독점 전체화면 제외)"),
            Fluent.Toggle(Settings.Popup.ShowOverFullscreen, on => { Settings.Popup.ShowOverFullscreen = on; Changed(); })));

        page.Children.Add(Fluent.SettingCard(Glyphs.BellOff,
            Loc.T("Click the tray icon to stop an alarm", "트레이 아이콘 클릭으로 알람 정지"),
            Loc.T("Otherwise a click opens Recent notifications", "꺼 두면 클릭은 최근 알림만 엽니다"),
            Fluent.Toggle(Settings.Sound.StopOnTrayClick, on =>
            {
                Settings.Sound.StopOnTrayClick = on;
                Changed();
            })));

        page.Children.Add(Fluent.SettingCard(Glyphs.Bell,
            Loc.T("Send a test notification", "테스트 알림 보내기"),
            Loc.T("Uses the Attention level, through the same rules as a real one", "주의 레벨로, 실제 알림과 같은 규칙을 거칩니다"),
            Fluent.IconButton(Glyphs.Play, Loc.T("Send", "보내기"), _host.SendTest)));
    }

    private void BuildSound(StackPanel page)
    {
        page.Children.Add(Fluent.PageHeader(Loc.T("Sound", "사운드")));

        page.Children.Add(Fluent.SettingCard(Glyphs.Volume,
            Loc.T("Play sounds", "소리 재생"),
            Loc.T("Turn off to get notifications without any sound", "끄면 소리 없이 알림만 표시합니다"),
            Fluent.Toggle(Settings.Sound.Enabled, on => { Settings.Sound.Enabled = on; Changed(); })));

        var volumeValue = Fluent.Secondary($"{Settings.Sound.Volume:P0}", "BodyTextBlockStyle");
        volumeValue.MinWidth = 44;
        volumeValue.VerticalAlignment = VerticalAlignment.Center;
        var volume = new Slider { Minimum = 0, Maximum = 100, Value = Math.Round(Settings.Sound.Volume * 100), Width = 220 };
        volume.ValueChanged += (_, e) =>
        {
            Settings.Sound.Volume = e.NewValue / 100.0;
            volumeValue.Text = $"{Settings.Sound.Volume:P0}";
            Changed();
        };
        var volumeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        volumeRow.Children.Add(volume);
        volumeRow.Children.Add(volumeValue);
        page.Children.Add(Fluent.SettingCard(Glyphs.Speakers,
            Loc.T("Volume", "볼륨"),
            Loc.T("Every level; the Windows volume applies on top", "모든 레벨에 적용 (Windows 볼륨과 곱해짐)"),
            volumeRow));

        var maxDuration = NumberPicker(MaxDurationChoices, Settings.Sound.MaxDurationSec, DurationLabel,
            v => { Settings.Sound.MaxDurationSec = v; Changed(); });
        maxDuration.MinWidth = 140;
        page.Children.Add(Fluent.SettingCard(Glyphs.Clock,
            Loc.T("Longest alarm", "알람 최대 지속 시간"),
            Loc.T("\"Until acknowledged\" stops here too, unless set to \"No limit\"",
                  "\"확인할 때까지\"도 이 시간에 멈춥니다 (\"무제한\" 제외)"),
            maxDuration));

        page.Children.Add(Fluent.GroupHeader(Loc.T("Sound per level", "레벨별 사운드")));
        var note = Fluent.Secondary(Loc.T(
            "The interval is the silence after a sound finishes, not a fixed beat: a 5-second alarm with a 0.7 s interval plays 5 s, pauses 0.7 s, plays again.",
            "간격은 소리가 끝난 뒤의 공백이지 고정 박자가 아닙니다. 5초 알람에 0.7초 간격이면 5초 재생 → 0.7초 쉼 → 다시 재생입니다."));
        note.Margin = new Thickness(2, 0, 0, 8);
        page.Children.Add(note);

        foreach (var level in Levels)
        {
            try
            {
                page.Children.Add(BuildLevelCard(level));
            }
            catch (Exception ex)
            {
                CrashLog.Write($"SettingsWindow.Level.{level}", ex);
                page.Children.Add(Fluent.Text($"[{level}] {ex.Message}"));
            }
        }
    }

    // Which levels' pop-ups stay until clicked: a checkbox per level, under
    // the card's heading. Any one checked switches to notipet's own pop-up.
    private UIElement BuildStayLevels()
    {
        var header = new Grid { ColumnSpacing = 16 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = Fluent.Icon(Glyphs.Pin, 20);
        icon.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(icon);
        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(Fluent.Text(Loc.T("Keep notifications until clicked", "클릭할 때까지 알림 유지")));
        text.Children.Add(Fluent.Secondary(Loc.T(
            "Checked levels stay until clicked; the rest close after a few seconds",
            "체크한 레벨은 클릭할 때까지 남고, 나머지는 몇 초 뒤 닫힙니다")));
        Grid.SetColumn(text, 1);
        header.Children.Add(text);

        var boxes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(36, 0, 0, 0) };
        foreach (var key in Levels)
        {
            var level = NotificationLevelParser.Parse(key);
            var box = new CheckBox
            {
                Content = UiText.Level(level),
                IsChecked = Settings.Popup.Stays(level),
                MinWidth = 0,
                Margin = new Thickness(0, 0, 12, 0)
            };
            ToolTipService.SetToolTip(box, UiText.LevelDescription(level));
            box.Checked += (_, _) => SetStay(key, true);
            box.Unchecked += (_, _) => SetStay(key, false);
            boxes.Children.Add(box);
        }
        return Fluent.ExpandedCard(header, boxes);
    }

    private void SetStay(string key, bool stays)
    {
        var levels = Settings.Popup.StayLevels;
        levels.RemoveAll(l => string.Equals(l, key, StringComparison.OrdinalIgnoreCase));
        if (stays) levels.Add(key);
        // Keep the file readable: severity order, not click order.
        Settings.Popup.StayLevels = Levels.Where(l => levels.Contains(l, StringComparer.OrdinalIgnoreCase)).ToList();
        Changed();
    }

    private UIElement BuildLevelCard(string key)
    {
        var level = NotificationLevelParser.Parse(key);
        var spec = Settings.Sound.ByLevel.TryGetValue(key, out var existing) && existing is not null
            ? existing
            : SoundSettings.DefaultByLevel()[key];
        Settings.Sound.ByLevel[key] = spec;

        // Header row: badge, name, what the level is for, and a play button.
        var header = new Grid { ColumnSpacing = 14 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var levelIcon = Fluent.LevelIcon(level, 20);
        levelIcon.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(levelIcon);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        text.Children.Add(Fluent.Text($"{UiText.Level(level)}  ({key})", "BodyStrongTextBlockStyle"));
        text.Children.Add(Fluent.Secondary(UiText.LevelDescription(level)));
        Grid.SetColumn(text, 1);
        header.Children.Add(text);
        var play = Fluent.IconButton(Glyphs.Play, Loc.T("Play", "듣기"), () => _host.PreviewLevel(level));
        play.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(play, 2);
        header.Children.Add(play);

        // Body row: the four choices, each captioned.
        var soundOptions = SoundOptions(includeNone: false);
        var currentSound = !string.IsNullOrWhiteSpace(spec.Name) ? "library:" + spec.Name : spec.Alias;
        var sound = Picker(soundOptions, currentSound, UiText.Sound, picked =>
        {
            if (picked.StartsWith("library:", StringComparison.Ordinal))
            {
                spec.Name = picked["library:".Length..];
                spec.Alias = null;
            }
            else
            {
                spec.Alias = picked;
                spec.Name = null;
            }
            Changed();
        }, 200);

        var mode = SoundResolver.ParseRepeat(spec.Repeat);
        var currentCount = mode switch
        {
            RepeatMode.UntilAck => UntilAck,
            RepeatMode.Once => 1,
            _ => Math.Max(1, spec.RepeatCount ?? 2)
        };
        ComboBox interval = null!;
        var count = NumberPicker(CountChoices, currentCount, CountLabel, v =>
        {
            spec.Repeat = v switch { UntilAck => "until_ack", 1 => "once", _ => "repeat" };
            if (v > 1) spec.RepeatCount = v;
            interval.IsEnabled = v != 1;   // a single play has no gap
            Changed();
        });
        interval = NumberPicker(IntervalChoices, spec.IntervalMs ?? 700,
            v => v >= 1000 ? Loc.T($"{v / 1000.0:0.#} s", $"{v / 1000.0:0.#}초") : $"{v}ms",
            v => { spec.IntervalMs = v; Changed(); });
        interval.IsEnabled = currentCount != 1;

        // Star columns so the pickers share the card's width instead of
        // running off its right edge at their minimum sizes.
        var body = new Grid { ColumnSpacing = 12, Margin = new Thickness(46, 0, 0, 0) };
        foreach (var weight in new[] { 2.0, 1.4, 1.0 })
        {
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(weight, GridUnitType.Star) });
        }
        var cells = new (string Caption, ComboBox Control)[]
        {
            (Loc.T("Sound", "소리"), sound),
            (Loc.T("Plays", "재생 횟수"), count),
            (Loc.T("Interval", "간격"), interval)
        };
        for (var i = 0; i < cells.Length; i++)
        {
            cells[i].Control.MinWidth = 0;
            cells[i].Control.HorizontalAlignment = HorizontalAlignment.Stretch;
            var cell = Captioned(cells[i].Caption, cells[i].Control);
            Grid.SetColumn(cell, i);
            body.Children.Add(cell);
        }

        return Fluent.ExpandedCard(header, body);
    }

    private void BuildDesk(StackPanel page)
    {
        page.Children.Add(Fluent.PageHeader(Loc.T("At my desk", "자리 착석")));
        var intro = Fluent.Secondary(Loc.T(
            "A long alarm is for calling you back to the desk. While you are sitting here looking at the screen it is just noise - so tell Notipet you are here.",
            "긴 알람은 자리로 불러들이기 위한 것입니다. 이미 화면을 보고 있을 때는 소음일 뿐이니, 자리에 있다고 알려 주세요."), "BodyTextBlockStyle");
        intro.Margin = new Thickness(2, 0, 0, 12);
        page.Children.Add(intro);

        page.Children.Add(Fluent.SettingCard(Glyphs.Person,
            Loc.T("I am at my desk now", "지금 PC 앞에 있음"),
            Loc.T("Same switch as \"At my desk\" in the tray menu", "트레이 메뉴의 \"PC 앞에 있음\"과 같은 스위치입니다"),
            Fluent.Toggle(Settings.Presence.AtDesk, on => { Settings.Presence.AtDesk = on; Changed(); })));

        page.Children.Add(Fluent.GroupHeader(Loc.T("While I am at my desk", "PC 앞에 있을 때")));

        var repeats = NumberPicker(new[] { 1, 2, 3 }, Settings.Presence.AtDeskMaxRepeats,
            v => v == 1 ? Loc.T("Play once", "1회만") : Loc.T($"{v} times", $"{v}회"),
            v => { Settings.Presence.AtDeskMaxRepeats = v; Changed(); });
        repeats.IsEnabled = Settings.Presence.ShortenAlarmsAtDesk;
        page.Children.Add(Fluent.SettingCard(Glyphs.Clock,
            Loc.T("Shorten long alarms", "긴 알람을 짧게"),
            Loc.T("Repeating and until-acknowledged alarms play only this many times", "반복·확인할 때까지 알람을 이 횟수만 재생합니다"),
            Row(repeats, Fluent.Toggle(Settings.Presence.ShortenAlarmsAtDesk, on =>
            {
                Settings.Presence.ShortenAlarmsAtDesk = on;
                repeats.IsEnabled = on;
                Changed();
            }))));

        // Replacement sound: a softer sound in place of every level's own, or
        // silence when the notification on screen is enough.
        var replacement = Picker(SoundOptions(includeNone: true), Settings.Presence.AtDeskSound, UiText.Sound, picked =>
        {
            Settings.Presence.AtDeskSound = picked;
            Changed();
        }, 200);
        replacement.IsEnabled = Settings.Presence.ReplaceSoundAtDesk;
        var preview = Fluent.IconButton(Glyphs.Play, Loc.T("Play", "듣기"), _host.PreviewAtDeskSound);
        preview.IsEnabled = Settings.Presence.ReplaceSoundAtDesk;

        page.Children.Add(Fluent.SettingCard(Glyphs.Bell,
            Loc.T("Use a different sound", "다른 소리로 대체"),
            Loc.T("One sound for every level; \"No sound\" shows notifications silently",
                  "모든 레벨을 이 소리로. \"소리 없음\"이면 알림만 뜹니다"),
            Fluent.Toggle(Settings.Presence.ReplaceSoundAtDesk, on =>
            {
                Settings.Presence.ReplaceSoundAtDesk = on;
                replacement.IsEnabled = on;
                preview.IsEnabled = on;
                Changed();
            })));

        var replacementRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(52, 0, 0, 0) };
        replacementRow.Children.Add(Captioned(Loc.T("Replacement sound", "대체 소리"), replacement));
        var previewWrap = Captioned(" ", preview);
        replacementRow.Children.Add(previewWrap);
        page.Children.Add(Fluent.Card(replacementRow));
    }

    private void BuildQuiet(StackPanel page)
    {
        page.Children.Add(Fluent.PageHeader(Loc.T("Quiet hours", "방해금지")));

        page.Children.Add(Fluent.SettingCard(Glyphs.Clock,
            Loc.T("Use quiet hours", "방해금지 시간대 사용"),
            Loc.T("Notifications below the level you allow are held back during this time",
                  "이 시간에는 허용한 레벨보다 낮은 알림을 막습니다"),
            Fluent.Toggle(Settings.QuietHours.Enabled, on => { Settings.QuietHours.Enabled = on; Changed(); })));

        var start = TimeBox(Settings.QuietHours.Start, v => { Settings.QuietHours.Start = v; Changed(); });
        var end = TimeBox(Settings.QuietHours.End, v => { Settings.QuietHours.End = v; Changed(); });
        // Under the label rather than beside it: two TimePickers are wider than
        // the right-hand column and crushed the label to one glyph per line.
        var rangeHeader = new Grid { ColumnSpacing = 16 };
        rangeHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        rangeHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var rangeIcon = Fluent.Icon(Glyphs.Schedule, 20);
        rangeIcon.VerticalAlignment = VerticalAlignment.Center;
        rangeHeader.Children.Add(rangeIcon);
        var rangeText = new StackPanel { Spacing = 2 };
        rangeText.Children.Add(Fluent.Text(Loc.T("From / to", "시작 / 종료")));
        rangeText.Children.Add(Fluent.Secondary(Loc.T(
            "A range that passes midnight (23:00 to 08:00) works as expected",
            "자정을 넘는 구간(23:00 → 08:00)도 그대로 동작합니다")));
        Grid.SetColumn(rangeText, 1);
        rangeHeader.Children.Add(rangeText);
        var rangeBody = Row(start, Fluent.Secondary("→", "BodyTextBlockStyle"), end);
        rangeBody.Margin = new Thickness(36, 0, 0, 0);
        page.Children.Add(Fluent.ExpandedCard(rangeHeader, rangeBody));

        var floorLevels = Levels.Select(NotificationLevelParser.Parse).ToArray();
        var floor = new ComboBox { MinWidth = 160 };
        foreach (var l in floorLevels) floor.Items.Add(UiText.Level(l));
        var currentFloor = NotificationLevelParser.Parse((Settings.QuietHours.AllowLevels ?? new List<string>()).FirstOrDefault() ?? "critical");
        floor.SelectedIndex = Math.Max(0, Array.IndexOf(floorLevels, currentFloor));
        floor.SelectionChanged += (_, _) =>
        {
            if (floor.SelectedIndex < 0) return;
            Settings.QuietHours.AllowLevels = new List<string> { Levels[floor.SelectedIndex] };
            Changed();
        };
        page.Children.Add(Fluent.SettingCard(Glyphs.Warning,
            Loc.T("Still let through", "그래도 통과시킬 레벨"),
            Loc.T("This level and anything more severe", "이 레벨과 그보다 심각한 것"),
            floor));

        page.Children.Add(Fluent.SettingCard(Glyphs.Moon,
            Loc.T("Respect Windows Do Not Disturb", "Windows 방해 금지 존중"),
            Loc.T("Only while quiet hours is on",
                  "방해금지 시간대를 켰을 때만 적용됩니다"),
            Fluent.Toggle(Settings.QuietHours.RespectFocusAssist, on => { Settings.QuietHours.RespectFocusAssist = on; Changed(); })));

        page.Children.Add(Fluent.GroupHeader(Loc.T("Mute", "음소거")));
        page.Children.Add(Fluent.SettingCard(Glyphs.ErrorCircle,
            Loc.T("Let critical alerts through while muted", "음소거 중에도 긴급 알림 허용"),
            Loc.T("Critical is for things that must not be missed", "긴급은 놓치면 안 되는 것을 위한 레벨입니다"),
            Fluent.Toggle(Settings.Mute.AllowCritical, on => { Settings.Mute.AllowCritical = on; Changed(); })));
    }

    private void BuildHistory(StackPanel page)
    {
        page.Children.Add(Fluent.PageHeader(Loc.T("Recent notifications", "최근 알림")));

        var keep = NumberPicker(HistoryChoices, Settings.History.KeepInMemory,
            v => Loc.T($"{v} notifications", $"{v}개"),
            v =>
            {
                Settings.History.KeepInMemory = v;
                // The host trims straight away, so lowering the limit is
                // visible now rather than only as new alerts arrive.
                Changed();
            });
        page.Children.Add(Fluent.SettingCard(Glyphs.History,
            Loc.T("Keep", "보관할 알림 수"),
            Loc.T("In memory only - cleared when Notipet quits",
                  "메모리에만 보관되어 Notipet을 끄면 사라집니다"),
            keep));

        page.Children.Add(Fluent.SettingCard(Glyphs.Thread,
            Loc.T("Show thread names from the agent apps", "에이전트 앱의 스레드 이름 표시"),
            Loc.T("The names Codex and Claude give them. Reads their local files only",
                  "Codex·Claude가 붙인 이름. 각 앱의 로컬 파일만 읽습니다"),
            Fluent.Toggle(Settings.History.LookupThreadTitles, on => { Settings.History.LookupThreadTitles = on; Changed(); })));

        var countText = Fluent.Secondary(Loc.T($"{_host.History.Count} kept now", $"현재 {_host.History.Count}개 보관 중"), "BodyTextBlockStyle");

        page.Children.Add(Fluent.SettingCard(Glyphs.Checklist,
            Loc.T("Open Recent notifications", "최근 알림 열기"),
            null,
            Fluent.IconButton(Glyphs.History, Loc.T("Open", "열기"), _host.ShowHistory)));

        var clear = Fluent.IconButton(Glyphs.Delete, Loc.T("Clear", "비우기"), () => { });
        var confirm = new Flyout();
        var confirmPanel = new StackPanel { Spacing = 12, MaxWidth = 260 };
        confirmPanel.Children.Add(Fluent.Text(Loc.T("Clear all recent notifications?", "최근 알림을 모두 지울까요?"), "BodyStrongTextBlockStyle"));
        confirmPanel.Children.Add(Fluent.IconButton(Glyphs.Delete, Loc.T("Clear all", "모두 지우기"), () =>
        {
            var cleared = _host.ClearHistory();
            confirm.Hide();
            countText.Text = Loc.T($"Cleared {cleared}", $"{cleared}개 지웠습니다");
        }, accent: true));
        confirm.Content = confirmPanel;
        clear.Flyout = confirm;

        page.Children.Add(Fluent.SettingCard(Glyphs.Delete,
            Loc.T("Clear recent notifications", "최근 알림 비우기"),
            null,
            Row(countText, clear)));
    }

    private void BuildAbout(StackPanel page)
    {
        page.Children.Add(Fluent.PageHeader(Loc.T("About", "정보")));

        var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        brand.Children.Add(Fluent.LevelBadge(NotificationLevel.Info, 48));
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        names.Children.Add(Fluent.Text("Notipet", "SubtitleTextBlockStyle"));
        names.Children.Add(Fluent.Secondary(Loc.T(
            $"Version {_host.Version} - makes a noise when an AI coding agent needs you",
            $"버전 {_host.Version} — AI 코딩 에이전트가 나를 찾을 때 소리로 알려 줍니다")));
        brand.Children.Add(names);
        page.Children.Add(Fluent.Card(brand, "16"));

        page.Children.Add(Fluent.GroupHeader(Loc.T("Updates", "업데이트")));
        page.Children.Add(BuildUpdateCard());
        page.Children.Add(Fluent.SettingCard(Glyphs.Clock,
            Loc.T("Check for updates automatically", "자동으로 업데이트 확인"),
            Loc.T("Once a day, asks GitHub whether there is a new version. Installing is still up to you.",
                  "하루 한 번 GitHub에 새 버전이 있는지만 묻습니다. 설치는 직접 누를 때만 합니다."),
            Fluent.Toggle(Settings.Updates.AutoCheck, on => { Settings.Updates.AutoCheck = on; Changed(); })));

        page.Children.Add(Fluent.GroupHeader(Loc.T("Where things are", "위치")));

        page.Children.Add(Fluent.SettingCard(Glyphs.Folder,
            Loc.T("Data folder", "데이터 폴더"),
            Paths.DataDir,
            Fluent.IconButton(Glyphs.Folder, Loc.T("Open", "열기"), _host.OpenDataFolder)));

        page.Children.Add(Fluent.SettingCard(Glyphs.Code,
            Loc.T("Local API", "로컬 API"),
            _host.ApiPort is { } port
                ? Loc.T($"http://127.0.0.1:{port} - token in runtime.json", $"http://127.0.0.1:{port} — 토큰은 runtime.json에")
                : Loc.T("Not listening", "대기 중 아님"),
            null));

        var cli = Path.Combine(AppContext.BaseDirectory, "notipet.exe");
        page.Children.Add(Fluent.SettingCard(Glyphs.Terminal,
            Loc.T("Command line", "명령줄"),
            File.Exists(cli) ? cli + Loc.T("  -  run `notipet help`", "  —  `notipet help` 참고") : Loc.T("notipet.exe not found next to the app", "앱 옆에 notipet.exe가 없습니다"),
            null));

        // docs\ sits beside bin\ when running from the repository.
        var docs = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "docs"));
        if (Directory.Exists(docs))
        {
            page.Children.Add(Fluent.SettingCard(Glyphs.Checklist,
                Loc.T("Documentation", "문서"),
                docs,
                Fluent.IconButton(Glyphs.Folder, Loc.T("Open", "열기"), () => _host.OpenFolder(docs))));
        }
    }

    // The update card: what the last check found and the one button that
    // fits - check, or install. Updated in place when the state changes.
    private TextBlock? _updateText;
    private Button? _updateButton;
    private bool _updateSubscribed;

    private UIElement BuildUpdateCard()
    {
        var grid = new Grid { ColumnSpacing = 16, MinHeight = 44 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = Fluent.Icon(Glyphs.Download, 20);
        icon.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(icon);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        text.Children.Add(Fluent.Text(Loc.T($"Notipet {_host.Version}", $"Notipet {_host.Version}")));
        _updateText = Fluent.Secondary("");
        text.Children.Add(_updateText);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        _updateButton = Fluent.IconButton(Glyphs.Refresh, "", () =>
        {
            if (_host.UpdateState.Phase == Notipet.Update.UpdatePhase.Available) _host.InstallUpdate();
            else _host.CheckForUpdates();
        });
        _updateButton.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_updateButton, 2);
        grid.Children.Add(_updateButton);

        if (!_updateSubscribed)
        {
            _updateSubscribed = true;
            _host.UpdateStateChanged += ShowUpdateState;
        }
        ShowUpdateState();
        return Fluent.Card(grid);
    }

    private void ShowUpdateState()
    {
        if (_updateText is null || _updateButton is null) return;
        var state = _host.UpdateState;
        _updateText.Text = UiText.UpdateStatus(state, _host.UpdatesSupported);
        _updateButton.Visibility = _host.UpdatesSupported ? Visibility.Visible : Visibility.Collapsed;
        _updateButton.IsEnabled = state.Phase is not (Notipet.Update.UpdatePhase.Checking
            or Notipet.Update.UpdatePhase.Downloading or Notipet.Update.UpdatePhase.Restarting);
        var label = state.Phase == Notipet.Update.UpdatePhase.Available
            ? Loc.T($"Install {state.AvailableVersion}", $"{state.AvailableVersion} 설치")
            : Loc.T("Check now", "지금 확인");
        if (_updateButton.Content is StackPanel row && row.Children.Count > 1 && row.Children[1] is TextBlock caption) caption.Text = label;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_updateButton, label);
    }

    // ----- helpers -----

    private List<string> SoundOptions(bool includeNone)
    {
        var options = new List<string>();
        if (includeNone) options.Add("none");
        options.AddRange(SystemSoundCatalog.KnownAliases);
        options.AddRange(Settings.Sound.Library.Keys.Select(k => "library:" + k));
        return options;
    }

    private static ComboBox Picker(IReadOnlyList<string> values, string? current, Func<string, string> label, Action<string> onPick, double width)
    {
        var box = new ComboBox { MinWidth = width };
        foreach (var value in values) box.Items.Add(label(value));
        var index = -1;
        for (var i = 0; i < values.Count; i++)
        {
            if (string.Equals(values[i], current, StringComparison.OrdinalIgnoreCase)) { index = i; break; }
        }
        box.SelectedIndex = Math.Max(0, index);
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedIndex >= 0 && box.SelectedIndex < values.Count) onPick(values[box.SelectedIndex]);
        };
        return box;
    }

    // Presets rather than free-entry numbers: NumberBox kills this process at
    // render time (docs/regression.md), and for values with a handful of
    // sensible settings a picker is friendlier anyway. Selects the entry
    // nearest the current value, so a hand-edited settings.json never shows a
    // blank box.
    private static string CountLabel(int v) => v switch
    {
        UntilAck => Loc.T("Until acknowledged", "확인할 때까지"),
        1 => Loc.T("Once", "1회"),
        _ => Loc.T($"{v} times", $"{v}회")
    };

    private static string DurationLabel(int sec) => sec switch
    {
        AppSettings.UnlimitedAlarmSeconds => Loc.T("No limit", "무제한"),
        < 60 => Loc.T($"{sec} s", $"{sec}초"),
        _ => Loc.T($"{sec / 60} min", $"{sec / 60}분")
    };

    private static ComboBox NumberPicker(IReadOnlyList<int> choices, int current, Func<int, string> label, Action<int> onPick)
    {
        var box = new ComboBox { MinWidth = 120 };
        foreach (var choice in choices) box.Items.Add(label(choice));
        var nearest = 0;
        for (var i = 1; i < choices.Count; i++)
        {
            if (Math.Abs(choices[i] - current) < Math.Abs(choices[nearest] - current)) nearest = i;
        }
        box.SelectedIndex = nearest;
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedIndex >= 0 && box.SelectedIndex < choices.Count) onPick(choices[box.SelectedIndex]);
        };
        return box;
    }

    private static TimePicker TimeBox(string value, Action<string> onPick)
    {
        var parsed = TimeSpan.TryParse(value, out var t) ? t : TimeSpan.Zero;
        var picker = new TimePicker
        {
            ClockIdentifier = "24HourClock",
            MinuteIncrement = 5,
            Time = parsed,
            MinWidth = 0
        };
        picker.TimeChanged += (_, e) => onPick($"{e.NewTime.Hours:00}:{e.NewTime.Minutes:00}");
        return picker;
    }

    private static StackPanel Captioned(string caption, FrameworkElement control)
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(Fluent.Secondary(caption));
        panel.Children.Add(control);
        return panel;
    }

    private static StackPanel Row(params UIElement[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
        foreach (var child in children)
        {
            if (child is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(child);
        }
        return row;
    }
}
