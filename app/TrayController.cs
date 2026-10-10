using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Notipet.Channels;
using Notipet.Core;
using Notipet.Http;
using Notipet.Presence;
using Notipet.Rules;
using Notipet.Settings;
using Notipet.Shared;
using Notipet.Sound;
using Notipet.Tray;
using Notipet.Windows;

namespace Notipet;

// Owns everything with a lifetime: the tray icon, the HTTP server, the sound
// engines, the runtime file, the windows. Follows quota-scope's TrayController
// shape - build the icon, set the menu, show it, dispose in reverse - and is
// the single place side effects happen: the windows ask, this saves.
[SupportedOSPlatform("windows10.0.19041.0")]
internal sealed class TrayController : IDisposable, INotipetHost
{
    // From the build (<Version> in the csproj, or -p:Version= when packing a
    // test release), so the installer, the updater and the API agree.
    public static readonly string AppVersion = BuildVersion.Of(typeof(TrayController).Assembly);

    private readonly DispatcherQueue _dispatcherQueue;
    private readonly AppSettings _settings;
    private readonly SoundService _sound;
    private readonly HistoryStore _history;
    private readonly RateLimitRule _rateLimit = new();
    private readonly Dispatcher _dispatcher;
    private readonly PresenceMonitor _presence;
    private readonly List<INotificationChannel> _channels = new();
    private readonly string _instanceId = Guid.NewGuid().ToString("N");
    private readonly System.Drawing.Icon _appIcon;
    private readonly ThreadTitleLookup _threadTitles = ThreadTitleLookup.ForCurrentUser();
    // Whether each app's URL scheme is registered, asked once per run: the
    // window asks for every card on every refresh.
    private readonly Dictionary<string, bool> _schemes = new(StringComparer.OrdinalIgnoreCase);

    private TrayIconHost? _trayIcon;
    private NotipetHttpServer? _server;
    private SettingsWindow? _settingsWindow;
    private HistoryWindow? _historyWindow;
    private readonly PopupHost _popups;
    private TrayIconState _iconState = TrayIconState.Idle;
    private bool _disposed;

    public TrayController()
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        Paths.EnsureDataDir();
        _settings = AppSettings.Load();
        Loc.SetLanguage(_settings.Language);
        Fluent.SetTheme(_settings.Theme);
        _lookupThreadTitles = _settings.History.LookupThreadTitles;

        // One rendering of the bell serves as the window icon everywhere, so the
        // title bars and the taskbar match the tray.
        _appIcon = TrayIconRenderer.CreateApp(32);

        _sound = new SoundService(() => _settings);
        _history = new HistoryStore(() => _settings.History.KeepInMemory);
        _presence = new PresenceMonitor(() => _settings.Presence.IdleThresholdSec);

        _channels.Add(new WindowsSoundChannel(_sound, () => _settings, () => _settings.Presence.AtDesk));
        // notipet's own pop-ups (PopupSettings); the visual channel uses them
        // instead of the shell balloon when either pop-up option is on.
        _popups = new PopupHost(() => _settings, CardLinkFor, StopAlarms, OpenCardLinkFor, ShowHistory);
        _channels.Add(new TrayBalloonChannel(() => _trayIcon, () => _settings, OnUiThread, () => _popups));

        _dispatcher = new Dispatcher(
            () => _settings,
            new RuleEngine(_rateLimit),
            _rateLimit,
            _history,
            () => _channels,
            PresenceMonitor.IsFocusAssistActive);

        _dispatcher.ThrottleAnnouncement += OnThrottleAnnouncement;
        _history.Changed += () => OnUiThread(() => { RefreshTray(); _historyWindow?.RefreshIfVisible(); });
        _history.Added += LookUpThreadTitle;
        _sound.Alarms.Changed += () => OnUiThread(RefreshTray);

        StartTray();
        StartServer();
        RefreshTray();

        _watchdog = new UiWatchdog(_dispatcherQueue, RecoverFromHang);
        _updater.Changed += state => OnUiThread(() => OnUpdateStateChanged(state));
        // First look a few minutes after start, then every few hours; the
        // check itself enforces "on, and a day since the last one".
        _updateTimer = new System.Threading.Timer(_ => AutoCheckForUpdates(), null, TimeSpan.FromMinutes(3), TimeSpan.FromHours(3));
        if (Program.RecoveredFromHang)
        {
            _trayIcon?.ShowNotification("Notipet", Loc.T(
                "Notipet stopped responding and restarted itself. Notifications from before are no longer in Recent.",
                "Notipet이 응답하지 않아 스스로 다시 시작했습니다. 그 전 알림은 최근 알림에 남아 있지 않습니다."), BalloonLevel.Warning);
        }
    }

    private UiWatchdog? _watchdog;

    // From the watchdog's thread: the UI thread has not answered for a
    // minute. Say so in crash.log, take the tray icon down (a plain shell
    // call, it needs no UI thread), start a fresh instance that waits for this
    // one to go, and go. Nothing here may wait on the UI thread.
    private void RecoverFromHang()
    {
        var seconds = (int)(UiWatchdog.Interval.TotalSeconds * UiWatchdog.MissesBeforeRecovery);
        CrashLog.Write("UiWatchdog", new TimeoutException($"UI thread did not answer for {seconds}s; restarting"));
        DaemonLog.Write($"watchdog: UI thread did not answer for {seconds}s; restarting");
        _runtimeHeal?.Dispose();
        try { _trayIcon?.RemoveIcon(); } catch { }
        try { RuntimeFile.Delete(Process.GetCurrentProcess().SessionId); } catch { }
        try
        {
            if (Environment.ProcessPath is { } exe)
            {
                Process.Start(new ProcessStartInfo(exe, $"--restart-after {Environment.ProcessId}") { UseShellExecute = false });
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write("UiWatchdog.Restart", ex);
        }
        Process.GetCurrentProcess().Kill();
    }

    // ----- INotipetHost -----

    public AppSettings Settings => _settings;
    public HistoryStore History => _history;
    public System.Drawing.Icon? AppIcon => _appIcon;
    public int? ApiPort => _server?.Port;
    public string Version => AppVersion;

    public void SettingsChanged()
    {
        _settings.Save();
        // A lowered history limit applies now, not as new alerts push old out.
        _history.Trim();
        ApplyThreadTitleSetting();
        RefreshTray();
    }

    // The "thread names from the agent apps" switch applies to the cards that
    // are already there, not only to the next notification.
    private bool _lookupThreadTitles = true;

    private void ApplyThreadTitleSetting()
    {
        var on = _settings.History.LookupThreadTitles;
        if (on == _lookupThreadTitles) return;
        _lookupThreadTitles = on;
        if (!on)
        {
            _history.ClearAppThreadTitles();
            return;
        }

        // Back on: name the cards that are already there, in one pass and one
        // redraw (the lookup caches the files, so this is cheap per card).
        var generation = _history.TitleGeneration;
        var entries = _history.Recent(int.MaxValue)
            .Where(e => e.AppThreadTitle is null && (e.Envelope.SourceSession is not null || e.Envelope.HostSession is not null))
            .ToList();
        if (entries.Count == 0) return;
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var found = entries.Select(e => (e, _threadTitles.Find(e.Envelope.SourceId, e.Envelope.SourceSession, e.Envelope.HostSession)));
                _history.SetAppThreadTitles(found.ToList(), generation);
            }
            catch (Exception ex)
            {
                CrashLog.Write("ThreadTitleLookup", ex);
            }
        });
    }

    public void ThemeChanged()
    {
        _settings.Save();
        Fluent.SetTheme(_settings.Theme);
        MenuGlyphs.SetMenuTheme(_settings.Theme);
        _settingsWindow?.ApplyTheme();
        _historyWindow?.ApplyTheme();
        _popups.ApplyTheme();
        RefreshTray();
    }

    public void LanguageChanged()
    {
        _settings.Save();
        Loc.SetLanguage(_settings.Language);
        RefreshTray();
        _settingsWindow?.Relocalize();
        _historyWindow?.Relocalize();
    }

    // The settings window's per-level Play button. Bypasses the rule engine on
    // purpose: you are asking to hear this exact sound, not to test whether
    // quiet hours would have let it through.
    public void PreviewLevel(NotificationLevel level) =>
        Preview(level, atDesk: _settings.Presence.AtDesk);

    // Plays what an alert sounds like while at the desk, replacement included.
    public void PreviewAtDeskSound() => Preview(NotificationLevel.Attention, atDesk: true);

    private void Preview(NotificationLevel level, bool atDesk)
    {
        try
        {
            var resolved = SoundResolver.Resolve(null, level, _settings, new List<string>(), atDesk);
            _sound.Alarms.StopAll();
            // Sound work stays off the UI thread (MediaPlayerSoundEngine).
            if (!resolved.Silent) _ = Task.Run(() => _sound.Play(resolved, level, "settings-preview"));
        }
        catch (Exception ex)
        {
            CrashLog.Write("Preview", ex);
        }
    }

    public void StopPreview() => _sound.Alarms.StopAll();

    public void SendTest() => SendTestNotification();

    // Always shows: the menu item, the second-instance signal and
    // `notipet open recent` all mean "show me", never "close it".
    public void ShowHistory()
    {
        OnUiThread(() =>
        {
            _historyWindow ??= new HistoryWindow(this);
            _historyWindow.Activate();
        });
    }

    // The tray icon's left click only: shows the window, or closes it when it
    // is already up. A window created by this click is only ever shown.
    private void ToggleHistory()
    {
        OnUiThread(() =>
        {
            if (_historyWindow is null)
            {
                _historyWindow = new HistoryWindow(this);
                _historyWindow.Activate();
                return;
            }
            _historyWindow.Toggle();
        });
    }

    public void ShowSettings(string? page = null)
    {
        OnUiThread(() =>
        {
            _settingsWindow ??= new SettingsWindow(this);
            _settingsWindow.Activate(page);
        });
    }

    public int ClearHistory() => _history.Clear();

    public bool RemoveHistoryEntry(string id) => _history.Remove(id);

    public bool AutostartEnabled => Autostart.IsEnabled();

    // ----- updates -----

    private readonly Notipet.Update.Updater _updater = new();
    private System.Threading.Timer? _updateTimer;
    private bool _manualCheck;

    public Notipet.Update.UpdateState UpdateState => _updater.State;
    public bool UpdatesSupported => _updater.IsInstalled;
    public event Action? UpdateStateChanged;

    public void CheckForUpdates()
    {
        _manualCheck = true;
        _ = Task.Run(_updater.CheckAsync);
    }

    public void InstallUpdate()
    {
        if (_updater.State.Available is null) return;
        _ = Task.Run(() => _updater.DownloadAndRestartAsync(PrepareForUpdate));
    }

    // Velopack is about to end this process and start the new version. On a
    // pool thread: only things that need no UI thread.
    private void PrepareForUpdate()
    {
        DaemonLog.Write($"quit: updating to v{_updater.State.AvailableVersion}");
        _watchdog?.Dispose();
        _runtimeHeal?.Dispose();
        _runtimeHeal = null;
        try { _sound.Alarms.StopAll(); } catch { }
        try { RuntimeFile.Delete(Process.GetCurrentProcess().SessionId); } catch { }
        try { _trayIcon?.RemoveIcon(); } catch { }
    }

    private void OnUpdateStateChanged(Notipet.Update.UpdateState state)
    {
        RefreshTray();
        UpdateStateChanged?.Invoke();

        var manual = _manualCheck && state.Phase is Notipet.Update.UpdatePhase.UpToDate
            or Notipet.Update.UpdatePhase.Available or Notipet.Update.UpdatePhase.Failed;
        if (manual) _manualCheck = false;

        if (state.Phase == Notipet.Update.UpdatePhase.Available
            && (manual || state.AvailableVersion != _settings.Updates.NotifiedVersion))
        {
            _settings.Updates.NotifiedVersion = state.AvailableVersion;
            _settings.Save();
            _trayIcon?.ShowNotification("Notipet", Loc.T(
                $"Version {state.AvailableVersion} is available. Install it from the tray menu or Settings > About.",
                $"새 버전 {state.AvailableVersion}이 있습니다. 트레이 메뉴나 설정 > 정보에서 설치하세요."), BalloonLevel.Info);
        }
        else if (manual && state.Phase == Notipet.Update.UpdatePhase.UpToDate)
        {
            _trayIcon?.ShowNotification("Notipet", Loc.T($"Notipet {AppVersion} is up to date.", $"Notipet {AppVersion}이 최신 버전입니다."), BalloonLevel.Info);
        }
        else if (manual && state.Phase == Notipet.Update.UpdatePhase.Failed)
        {
            _trayIcon?.ShowNotification("Notipet", Loc.T($"Could not check for updates: {state.Error}", $"업데이트를 확인하지 못했습니다: {state.Error}"), BalloonLevel.Warning);
        }
    }

    // Only when the user turned it on, and at most once a day - counted
    // across restarts. A check only reports; nothing is downloaded.
    private void AutoCheckForUpdates()
    {
        if (_disposed || !_settings.Updates.AutoCheck || !_updater.IsInstalled) return;
        if (_settings.Updates.LastAutoCheck is { } last && DateTimeOffset.Now - last < TimeSpan.FromHours(24)) return;
        OnUiThread(() =>
        {
            _settings.Updates.LastAutoCheck = DateTimeOffset.Now;
            _settings.Save();
        });
        _ = _updater.CheckAsync();
    }

    public bool SetAutostart(bool enabled)
    {
        if (!Autostart.TrySet(enabled)) return false;
        _settings.Autostart = enabled;
        return true;
    }

    public void OpenDataFolder()
    {
        Paths.EnsureDataDir();
        OpenFolder(Paths.DataDir);
    }

    public void OpenFolder(string path)
    {
        try
        {
            if (Directory.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            CrashLog.Write("OpenFolder", ex);
        }
    }

    public Uri? ThreadLink(HistoryEntry entry) => ThreadLinkFor(entry.Envelope);

    private Uri? ThreadLinkFor(NotificationEnvelope envelope)
    {
        var link = ThreadLinks.For(envelope.SourceId, envelope.SourceSession, envelope.HostSession);
        if (link is null) return null;
        if (!_schemes.TryGetValue(link.Scheme, out var registered))
        {
            registered = UrlSchemes.IsRegistered(link.Scheme);
            _schemes[link.Scheme] = registered;
        }
        return registered ? link : null;
    }

    // Runs from a click in the recent window, which makes notipet the
    // foreground process for a moment - the only time Windows lets it hand the
    // foreground on. Without the grant the agent app often just flashes in the
    // taskbar instead of coming up.
    public void OpenThread(HistoryEntry entry) => Launch(ThreadLinkFor(entry.Envelope), "OpenThread");

    public Uri? SenderLink(HistoryEntry entry) => SenderLinkFor(entry.Envelope);

    public void OpenSenderLink(HistoryEntry entry) => Launch(SenderLinkFor(entry.Envelope), "OpenSenderLink");

    public event Action? AlertScopeChanged;

    public void SetThreadAlerts(HistoryEntry entry, bool? on)
    {
        var e = entry.Envelope;
        if (e.SourceSession is null || e.SourceId == PayloadMapper.SourceManual) return;
        var label = entry.AppThreadTitle ?? e.ThreadTitle;
        if (AlertScope.SetThread(_settings.Alerts, e.SourceId, e.SourceSession, on, label, e.Project, DateTimeOffset.Now)) AlertScopeSaved();
    }

    public void SetProjectAlerts(string project, bool? on)
    {
        if (string.IsNullOrWhiteSpace(project)) return;
        if (AlertScope.SetProject(_settings.Alerts, project, on, DateTimeOffset.Now)) AlertScopeSaved();
    }

    private void AlertScopeSaved()
    {
        SettingsChanged();
        AlertScopeChanged?.Invoke();
    }

    // Checked again now, against today's settings: a scheme taken off
    // links.allowedSchemes stops working on cards that already exist.
    private Uri? SenderLinkFor(NotificationEnvelope envelope) =>
        OpenLinks.IsAllowed(envelope.OpenUri, _settings.Links.AllowedSchemes)
        && Uri.TryCreate(envelope.OpenUri, UriKind.Absolute, out var link) ? link : null;

    // What a click on a card opens: the sender's link, else the thread.
    private Uri? CardLinkFor(NotificationEnvelope envelope) => SenderLinkFor(envelope) ?? ThreadLinkFor(envelope);

    private void OpenCardLinkFor(NotificationEnvelope envelope) => Launch(CardLinkFor(envelope), "OpenCardLink");

    // A URI to the shell, as the URI it is - never a command line; OpenLinks
    // and ThreadLinks are the only sources.
    private static void Launch(Uri? link, string what)
    {
        if (link is null) return;
        try
        {
            AllowSetForegroundWindow(AsfwAny);
            Process.Start(new ProcessStartInfo(link.OriginalString) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            CrashLog.Write(what, ex);
        }
    }

    // After delivery, on a worker thread: put the app's own name for the
    // thread on the card. Never on the way to the speaker, never fatal.
    private void LookUpThreadTitle(HistoryEntry entry)
    {
        // The generation is read BEFORE the setting. Turning the switch off sets
        // the flag and only then clears (bumping the generation under the
        // store's lock), so either we see the flag off, or our generation is
        // the old one and the answer is dropped. The other order leaves a gap.
        var generation = _history.TitleGeneration;
        if (!_settings.History.LookupThreadTitles) return;
        var envelope = entry.Envelope;
        if (envelope.SourceSession is null && envelope.HostSession is null) return;

        _ = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var title = _threadTitles.Find(envelope.SourceId, envelope.SourceSession, envelope.HostSession);
                _history.SetAppThreadTitle(entry, title, generation);
            }
            catch (Exception ex)
            {
                CrashLog.Write("ThreadTitleLookup", ex);
            }
        });
    }

    private const int AsfwAny = -1;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    // ----- startup -----

    private void StartTray()
    {
        _trayIcon = new TrayIconHost();
        MenuGlyphs.SetMenuTheme(_settings.Theme);
        _trayIcon.LeftClicked += OnTrayClicked;
        _trayIcon.BalloonClicked += OnBalloonClicked;
        _trayIcon.SessionLockChanged += locked => _presence.SetLocked(locked);
        // Off the UI thread, coalesced: see SoundService.RequestProbe.
        _trayIcon.AudioDeviceChanged += () => _sound.RequestProbe();
        _trayIcon.SetIcon(TrayIconRenderer.Create(TrayIconState.Idle, TrayIconRenderer.NativeSize()));
        _trayIcon.SetMenu(BuildMenu());
        _trayIcon.Show();
    }

    private void StartServer()
    {
        var token = AuthGuard.GenerateToken();

        // Bind first, then build the guard around the port we actually got, then
        // publish runtime.json. Never advertise a port we do not hold.
        var bootstrap = new NotipetHttpServer(new RouteTable(), new AuthGuard(token, 0));
        try
        {
            bootstrap.Start(_settings.Server.Port);
        }
        catch (Exception ex)
        {
            CrashLog.Write("HttpServer.Bind", ex);
            bootstrap.Dispose();
            return;
        }

        var port = bootstrap.Port;
        var warnings = bootstrap.Warnings.ToList();
        bootstrap.Dispose();

        var guard = new AuthGuard(token, port);
        var api = new ApiContext
        {
            Settings = () => _settings,
            Dispatcher = _dispatcher,
            History = _history,
            Alarms = _sound.Alarms,
            SoundEngine = _sound.Describe,
            Channels = () => _channels,
            Presence = () => _presence.Current.ToString(),
            FocusAssistActive = PresenceMonitor.IsFocusAssistActive,
            Guard = guard,
            InstanceId = _instanceId,
            Version = AppVersion,
            Port = () => _server?.Port ?? port,
            Warnings = () => _server?.Warnings ?? (IReadOnlyList<string>)Array.Empty<string>(),
            SettingsChanged = () => OnUiThread(() =>
            {
                RefreshTray();
                // `notipet alerts ...` from an agent or a terminal: the open
                // Settings page and the cards' menus follow.
                AlertScopeChanged?.Invoke();
            }),
            SetAtDesk = value =>
            {
                _settings.Presence.AtDesk = value ?? !_settings.Presence.AtDesk;
                _settings.Save();
                OnUiThread(RefreshTray);
                return _settings.Presence.AtDesk;
            },
            Shutdown = () => OnUiThread(() => Quit("notipet stop / API shutdown")),
            ClosePopups = ids => OnUiThreadAsync(() => _popups.CloseFor(ids), (IReadOnlyCollection<string>)Array.Empty<string>())
        };

        _server = new NotipetHttpServer(ApiRoutes.Build(api), guard);
        try
        {
            _server.Start(port);
        }
        catch (Exception ex)
        {
            CrashLog.Write("HttpServer.Start", ex);
            _server = null;
            return;
        }

        foreach (var warning in warnings) _server.Warnings.Add(warning);

        // A leftover file from a crashed instance is cleared by overwriting it:
        // we have already won the single-instance mutex, so nothing else owns it.
        // Say so first: a predecessor that left its file behind never ran its
        // shutdown - it was killed or died - and nothing else would record it.
        var sessionId = Process.GetCurrentProcess().SessionId;
        if (RuntimeFile.Read(Paths.RuntimePath) is { } left && left.Pid != Environment.ProcessId)
        {
            DaemonLog.Write($"previous instance pid {left.Pid} (v{left.Version}, started {left.StartedAtUtc}) ended without cleaning up - killed, crashed, or the session ended");
        }
        _runtime = RuntimeFile.Describe(_server.Port, token, _instanceId, sessionId, AppVersion);
        RuntimeFile.Write(_runtime);
        DaemonLog.Write($"started v{AppVersion}, port {_server.Port}{(Program.RecoveredFromHang ? ", replacing an instance whose UI thread hung" : "")}");

        // Keep it that way. Something left runtime.json naming a dead instance
        // while this one ran for hours - the CLI and every hook then believed
        // Notipet was down. Whatever does that next time, it lasts 30 s.
        _runtimeHeal = new System.Threading.Timer(_ => HealRuntimeFile(), null, RuntimeHealInterval, RuntimeHealInterval);

        if (_server.Warnings.Count > 0)
        {
            _trayIcon?.ShowNotification("Notipet", string.Join("; ", _server.Warnings), BalloonLevel.Warning);
        }
    }

    private static readonly TimeSpan RuntimeHealInterval = TimeSpan.FromSeconds(30);
    private RuntimeInfo? _runtime;
    private System.Threading.Timer? _runtimeHeal;

    // On a pool thread; touches only the files.
    private void HealRuntimeFile()
    {
        if (_disposed || _runtime is not { } runtime) return;
        try
        {
            if (RuntimeFile.NamesInstance(runtime)) return;
            var found = RuntimeFile.Read(Paths.RuntimePath);
            DaemonLog.Write(found is null
                ? "runtime.json was missing; rewritten"
                : $"runtime.json named pid {found.Pid}, not this instance; rewritten");
            if (!_disposed) RuntimeFile.Write(runtime);
        }
        catch (Exception ex)
        {
            CrashLog.Write("RuntimeFile.Heal", ex);
        }
    }

    // ----- tray -----

    private IReadOnlyList<TrayMenuItem> BuildMenu()
    {
        var items = new List<TrayMenuItem>();

        if (_sound.Alarms.HasActive)
        {
            items.Add(new TrayMenuItem(Loc.T("Stop alarm", "알람 정지"), StopAlarms, Glyph: Glyphs.BellOff));
            items.Add(TrayMenuItem.Separator);
        }

        // Checkable items carry no icon: check marks and icons share a column.
        // The at-desk switch is manual on purpose - the idle timer cannot tell
        // reading from being away, and guessing wrong is annoying either way.
        items.Add(new TrayMenuItem(Loc.T("At my desk", "PC 앞에 있음"), ToggleAtDesk, _settings.Presence.AtDesk));
        items.Add(new TrayMenuItem(Loc.T("Mute", "음소거"), ToggleMute, IsMuted()));
        items.Add(new TrayMenuItem(Loc.T("Mute for 30 minutes", "30분간 음소거"), () => MuteFor(30), Glyph: Glyphs.Clock));
        items.Add(new TrayMenuItem(Loc.T("Quiet hours", "방해금지 시간대"), ToggleQuietHours, _settings.QuietHours.Enabled));
        items.Add(TrayMenuItem.Separator);

        // A window rather than a submenu: a menu item can only show one
        // truncated line, and the body text is the part worth reading.
        items.Add(new TrayMenuItem(Loc.T("Recent notifications...", "최근 알림..."), ShowHistory, Glyph: Glyphs.History));
        items.Add(new TrayMenuItem(Loc.T("Settings...", "설정..."), () => ShowSettings(), Glyph: Glyphs.Settings));
        items.Add(new TrayMenuItem(Loc.T("Test notification", "테스트 알림"), SendTestNotification, Glyph: Glyphs.Bell));
        items.Add(TrayMenuItem.Separator);

        items.Add(new TrayMenuItem(Loc.T("Open data folder", "데이터 폴더 열기"), OpenDataFolder, Glyph: Glyphs.Folder));
        items.Add(new TrayMenuItem(Loc.T("Start with Windows", "Windows 시작 시 실행"), ToggleAutostart, Autostart.IsEnabled()));
        if (_updater.IsInstalled)
        {
            items.Add(_updater.State.Phase == Notipet.Update.UpdatePhase.Available
                ? new TrayMenuItem(Loc.T($"Update to {_updater.State.AvailableVersion}", $"{_updater.State.AvailableVersion}(으)로 업데이트"), InstallUpdate, Glyph: Glyphs.Download)
                : new TrayMenuItem(Loc.T("Check for updates", "업데이트 확인"), CheckForUpdates, Glyph: Glyphs.Refresh));
        }
        items.Add(TrayMenuItem.Separator);
        items.Add(new TrayMenuItem(Loc.T("Quit Notipet", "Notipet 종료"), () => Quit("tray menu"), Glyph: Glyphs.Power));

        return items;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";

    private void RefreshTray()
    {
        if (_disposed || _trayIcon is null) return;

        var state = ComputeIconState();
        if (state != _iconState)
        {
            _iconState = state;
            _trayIcon.SetIcon(TrayIconRenderer.Create(state, TrayIconRenderer.NativeSize()));
        }

        _trayIcon.SetTooltip(BuildTooltip(state));
        _trayIcon.SetMenu(BuildMenu());
    }

    private TrayIconState ComputeIconState()
    {
        if (_sound.Alarms.HasActive) return TrayIconState.Alarming;
        if (IsMuted()) return TrayIconState.Muted;
        // A broken audio device is worth surfacing in the icon: the whole point
        // of the app is the sound, and silently not making one is the failure
        // mode nobody would notice.
        if (_settings.Sound.Enabled && !_sound.Describe().Available) return TrayIconState.SoundUnavailable;
        if (ApiRoutes.IsQuietNow(_settings, PresenceMonitor.IsFocusAssistActive)) return TrayIconState.QuietHours;
        return TrayIconState.Idle;
    }

    private string BuildTooltip(TrayIconState state)
    {
        var lines = new List<string>
        {
            _server is null
                ? Loc.T("Notipet - API not listening", "Notipet - API 중지됨")
                : $"Notipet - 127.0.0.1:{_server.Port}"
        };

        var status = state switch
        {
            TrayIconState.Alarming => Loc.T("Alarm sounding - click to stop", "알람 울림 - 클릭하여 정지"),
            TrayIconState.Muted => Loc.T("Muted", "음소거됨"),
            TrayIconState.QuietHours => Loc.T("Quiet hours", "방해금지 시간대"),
            TrayIconState.SoundUnavailable => Loc.T("No audio device - showing notifications only",
                "오디오 장치 없음 - 알림만 표시"),
            _ => Loc.T("Ready", "대기 중")
        };
        if (_settings.Presence.AtDesk) status += Loc.T(" · at desk", " · PC 앞");
        lines.Add(status);

        var latest = _history.Recent(1).FirstOrDefault();
        if (latest is not null)
        {
            lines.Add($"{latest.LastAt:HH:mm} {Truncate(latest.Envelope.Summary, 48)}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    // ----- actions -----

    private bool IsMuted() => _settings.Mute.Enabled || _settings.Mute.Until > DateTimeOffset.Now;

    private void OnTrayClicked()
    {
        // A click on the icon is the fastest way to silence an alarm, which is
        // what someone reaches for first.
        if (_settings.Sound.StopOnTrayClick && _sound.Alarms.HasActive)
        {
            StopAlarms();
            return;
        }

        // Otherwise show what happened recently - or close it, when it is
        // already up. (This click used to fire a test notification, so an idle
        // click made a noise for no reason.) The order is settled: a ringing
        // alarm is stopped first, and the window comes on the next click.
        ToggleHistory();
    }

    private void OnBalloonClicked() => StopAlarms();

    private void StopAlarms()
    {
        _sound.Alarms.StopAll();
        OnUiThread(RefreshTray);
    }

    private void ToggleAtDesk()
    {
        _settings.Presence.AtDesk = !_settings.Presence.AtDesk;
        _settings.Save();
        RefreshTray();
    }

    private void ToggleMute()
    {
        var muted = IsMuted();
        _settings.Mute.Enabled = !muted;
        _settings.Mute.Until = null;
        _settings.Save();
        if (_settings.Mute.Enabled) _sound.Alarms.StopAll();
        RefreshTray();
    }

    private void MuteFor(int minutes)
    {
        _settings.Mute.Enabled = false;
        _settings.Mute.Until = DateTimeOffset.Now.AddMinutes(minutes);
        _settings.Save();
        _sound.Alarms.StopAll();
        RefreshTray();
    }

    private void ToggleQuietHours()
    {
        _settings.QuietHours.Enabled = !_settings.QuietHours.Enabled;
        _settings.Save();
        RefreshTray();
    }

    private void ToggleAutostart()
    {
        if (SetAutostart(!Autostart.IsEnabled())) _settings.Save();
        RefreshTray();
    }

    public void SendTestNotification()
    {
        var request = new NotifyRequest
        {
            Title = "Notipet",
            Body = Loc.T("Test notification", "테스트 알림"),
            Level = "attention",
            Tag = "notipet:test:" + Guid.NewGuid().ToString("N")[..8],
            Source = new SourceInfo { Id = PayloadMapper.SourceManual }
        };
        if (!EnvelopeFactory.TryCreate(request, PayloadMapper.SourceManual, out var envelope, out _)) return;
        // From a menu or a button, so on the UI thread: dispatch from the pool,
        // as the HTTP path does, so the sound channel never runs here.
        _ = Task.Run(() => _dispatcher.DispatchAsync(envelope!, default));
    }

    private void OnThrottleAnnouncement(string sourceId)
    {
        OnUiThread(() => _trayIcon?.ShowNotification(
            "Notipet",
            Loc.T($"Rate-limiting notifications from {sourceId}", $"{sourceId} 알림을 제한하는 중"),
            BalloonLevel.Warning));
    }

    // Channels and rules run on HTTP worker threads; anything touching the tray
    // window has to come back to the thread that created it.
    private void OnUiThread(Action action)
    {
        if (_disposed) return;
        if (_dispatcherQueue.HasThreadAccess)
        {
            try { action(); } catch (Exception ex) { CrashLog.Write("TrayController.UiThread", ex); }
            return;
        }
        _dispatcherQueue.TryEnqueue(() =>
        {
            try { action(); } catch (Exception ex) { CrashLog.Write("TrayController.UiThread", ex); }
        });
    }

    // The same, for a caller on a worker thread that needs the answer. Gives
    // up after two seconds with the fallback rather than hold an HTTP request
    // on a UI thread that is not answering.
    private async Task<T> OnUiThreadAsync<T>(Func<T> func, T fallback)
    {
        if (_disposed) return fallback;
        if (_dispatcherQueue.HasThreadAccess)
        {
            try { return func(); } catch (Exception ex) { CrashLog.Write("TrayController.UiThread", ex); return fallback; }
        }

        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcherQueue.TryEnqueue(() =>
            {
                try { result.TrySetResult(func()); }
                catch (Exception ex) { CrashLog.Write("TrayController.UiThread", ex); result.TrySetResult(fallback); }
            }))
        {
            return fallback;
        }
        var finished = await Task.WhenAny(result.Task, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
        return finished == result.Task ? await result.Task.ConfigureAwait(false) : fallback;
    }

    public void Quit(string reason)
    {
        DaemonLog.Write("quit: " + reason);
        Dispose();
        try { Microsoft.UI.Xaml.Application.Current.Exit(); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _watchdog?.Dispose();
        _updateTimer?.Dispose();
        // Stop healing before deleting, or the heal would put the file back.
        _runtimeHeal?.Dispose();
        _runtimeHeal = null;
        try { RuntimeFile.Delete(Process.GetCurrentProcess().SessionId); } catch { }
        _settingsWindow?.Close();
        _historyWindow?.Close();
        try { _popups.CloseAll(); } catch { }
        _server?.Dispose();
        _sound.Dispose();
        _trayIcon?.Dispose();
        _trayIcon = null;
        _appIcon.Dispose();
    }
}
