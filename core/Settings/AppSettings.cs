using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Notipet.Shared;

namespace Notipet.Settings;

internal sealed class ServerSettings
{
    // 0 means "pick an ephemeral port", which makes a port conflict essentially
    // impossible. Pin it only when using Claude Code's `http` hook type, which
    // needs a stable URL.
    public int Port { get; set; }
    public bool RequireAuth { get; set; } = true;
    public int MaxBodyBytes { get; set; } = 65536;
    public int RemoteTimeoutMs { get; set; } = 2000;
}

internal sealed class SoundSettings
{
    public bool Enabled { get; set; } = true;
    public double Volume { get; set; } = 0.7;
    // auto | mediaplayer | playsound
    public string Engine { get; set; } = "auto";
    public int MaxDurationSec { get; set; } = 120;
    public bool StopOnTrayClick { get; set; } = true;
    public Dictionary<string, string> Library { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> AllowedRoots { get; set; } = new()
    {
        @"%LOCALAPPDATA%\notipet\sounds",
        @"%SystemRoot%\Media"
    };
    public Dictionary<string, SoundSpec> ByLevel { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static Dictionary<string, SoundSpec> DefaultByLevel() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["info"] = new SoundSpec { Alias = "Notification.Default", Repeat = "once" },
        ["success"] = new SoundSpec { Alias = "Notification.IM", Repeat = "once" },
        // The permission prompt is the highest-value alert: loud enough to
        // notice from across the room, short enough not to be a siren.
        //
        // intervalMs is the silence *after* a sound ends, not a period from one
        // start to the next, so these are shorter than they look: the default
        // alarm alias is a 5-second file.
        ["attention"] = new SoundSpec { Alias = "Notification.Looping.Alarm", Repeat = "repeat", RepeatCount = 2, IntervalMs = 700 },
        ["warn"] = new SoundSpec { Alias = "SystemExclamation", Repeat = "once" },
        ["error"] = new SoundSpec { Alias = "SystemHand", Repeat = "repeat", RepeatCount = 3, IntervalMs = 700 },
        ["critical"] = new SoundSpec { Alias = "SystemExclamation", Repeat = "until_ack", IntervalMs = 1500 }
    };
}

internal sealed class MuteSettings
{
    public bool Enabled { get; set; }
    public DateTimeOffset? Until { get; set; }
    public bool AllowCritical { get; set; } = true;
}

internal sealed class QuietHoursSettings
{
    public bool Enabled { get; set; }
    public string Start { get; set; } = "23:00";
    public string End { get; set; } = "08:00";
    public List<string> AllowLevels { get; set; } = new() { "critical" };
    public bool RespectFocusAssist { get; set; } = true;
}

internal sealed class BucketSettings
{
    public int Capacity { get; set; }
    public int RefillPerMinute { get; set; }
}

internal sealed class RateLimitSettings
{
    public bool Enabled { get; set; } = true;
    public BucketSettings PerSource { get; set; } = new() { Capacity = 5, RefillPerMinute = 10 };
    public BucketSettings Global { get; set; } = new() { Capacity = 20, RefillPerMinute = 60 };
}

internal sealed class DedupeSettings
{
    public bool Enabled { get; set; } = true;
    public int WindowSec { get; set; } = 30;
    public bool Collapse { get; set; } = true;
}

internal sealed class PresenceSettings
{
    public int IdleThresholdSec { get; set; } = 300;
    public int PollSec { get; set; } = 15;

    // Set from the tray menu: "I am sitting here right now". Deliberately a
    // manual switch rather than an inference - the idle timer cannot tell
    // reading from being away, and guessing wrong in either direction is
    // annoying in a different way each time.
    public bool AtDesk { get; set; }

    // While AtDesk, collapse long repeating alarms into short ones. You are
    // looking at the screen; you do not need a siren.
    public bool ShortenAlarmsAtDesk { get; set; } = true;

    // The cap applied to an alarm that was shortened this way.
    public int AtDeskMaxRepeats { get; set; } = 1;

    // While AtDesk, play this sound instead of each level's own - typically a
    // soft chime in place of a 5-second alarm. Shortening still applies.
    public bool ReplaceSoundAtDesk { get; set; }

    // A Windows alias, "library:<name>", or "none" for no sound at all (the
    // balloon still appears). "none" is the honest version of "I can see the
    // screen, just show me".
    public string AtDeskSound { get; set; } = "Notification.Default";
}

internal sealed class ChannelSettings
{
    public bool Enabled { get; set; }
    // The only outbound-network gate. Settable from the settings UI behind an
    // explicit confirmation, never over the HTTP API - so neither a local
    // process nor an agent can flip it.
    public bool OutboundNetworkApproved { get; set; }
    public string? Provider { get; set; }
    public string? Endpoint { get; set; }
    public string? Key { get; set; }
}

internal sealed class SourceSettings
{
    public bool Enabled { get; set; } = true;
    public string? LevelFloor { get; set; }
}

// Updates (installed builds only). Checking reaches GitHub, so it happens
// when the user clicks - or once a day if they turn AutoCheck on here. It is
// off by default and settable only in Settings, never over the API.
// Installing is always a click, whatever this says.
internal sealed class UpdateSettings
{
    public bool AutoCheck { get; set; }
    public DateTimeOffset? LastAutoCheck { get; set; }
    // The newest version already announced, so a daily check says it once.
    public string? NotifiedVersion { get; set; }
}

internal sealed class HistorySettings
{
    public int KeepInMemory { get; set; } = 200;

    // Show each thread under the name the agent app gives it (Codex
    // session_index.jsonl, Claude ~/.claude/sessions). Read-only and local,
    // but it does open other programs' files, so it can be turned off.
    public bool LookupThreadTitles { get; set; } = true;
}

// Settings model plus load/save. Follows quota-scope's AppSettings shape:
// defaults on any read failure, lazily materialised sections, indented output.
// One deliberate difference - JsonExtensionData on the root - so an older build
// does not silently erase a newer build's keys on save.
// notipet's own pop-up, used instead of the Windows notification when either
// option is on. The shell's tray balloon can do neither: Windows decides how
// long it stays (a few seconds), and it holds notifications back while a
// full-screen app is in front.
internal sealed class PopupSettings
{
    // Levels whose pop-up stays on screen until clicked (or closed), however
    // long that takes - e.g. attention and critical stay, a finished turn
    // closes by itself. Wire names: info, success, attention, warn, error,
    // critical. Empty: nothing stays.
    public List<string> StayLevels { get; set; } = new();

    // 1.2.0 had one switch for every level. Read once, turned into
    // StayLevels (all levels) by Normalize(), and never written again.
    [JsonPropertyName("stayUntilClicked")]
    public bool? LegacyStayUntilClicked { get; set; }

    // Shown above full-screen apps too (games in borderless/windowed mode,
    // full-screen video, presentations). An exclusive-mode full-screen game
    // owns the display and cannot be drawn over by anyone.
    public bool ShowOverFullscreen { get; set; }

    // How long a pop-up that does not stay waits before it closes itself.
    public int TimeoutSec { get; set; } = 8;

    public bool Stays(NotificationLevel level) =>
        StayLevels.Contains(NotificationLevelParser.ToWire(level), StringComparer.OrdinalIgnoreCase);

    // notipet's own pop-up is used for every notification as soon as any
    // level stays or over-full-screen is on - mixing it with Windows'
    // notification by level would look like two different apps.
    [JsonIgnore]
    public bool UseOwnPopup => StayLevels.Count > 0 || ShowOverFullscreen;
}

// What a notification's card may open (`send --open`). http(s) on this PC
// is always allowed; any other URI scheme only when listed here - typically
// your own tool's, e.g. "codexbridge". Set in this file only: the API cannot
// change it, or any sender could allow itself. See OpenLinks for the schemes
// that stay refused even when listed.
internal sealed class LinkSettings
{
    public List<string> AllowedSchemes { get; set; } = new();
}

internal sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;
    public string Language { get; set; } = "System";
    // System (follow Windows) | Light | Dark. notipet's windows, pop-ups and
    // tray menu.
    public string Theme { get; set; } = "System";
    public bool Autostart { get; set; }
    public ServerSettings Server { get; set; } = new();
    public SoundSettings Sound { get; set; } = new();
    public MuteSettings Mute { get; set; } = new();
    public QuietHoursSettings QuietHours { get; set; } = new();
    public RateLimitSettings RateLimit { get; set; } = new();
    public DedupeSettings Dedupe { get; set; } = new();
    public PresenceSettings Presence { get; set; } = new();
    public Dictionary<string, ChannelSettings> Channels { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, SourceSettings> Sources { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HistorySettings History { get; set; } = new();
    public UpdateSettings Updates { get; set; } = new();
    public PopupSettings Popup { get; set; } = new();
    public LinkSettings Links { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // Where this instance came from, and therefore where Save() writes. Without
    // it Save() always wrote the user's real settings.json - so a test that
    // loaded a throwaway copy and then hit any route that saves overwrote the
    // user's settings with test values. That happened; see docs/regression.md.
    [JsonIgnore]
    public string? SourcePath { get; private set; }

    public static AppSettings Load() => Load(Paths.SettingsPath);

    public static AppSettings Load(string path)
    {
        AppSettings settings;
        try
        {
            settings = File.Exists(path)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options) ?? new AppSettings()
                : new AppSettings();
        }
        catch
        {
            // A corrupt settings file must not stop the daemon from making noise.
            settings = new AppSettings();
        }

        settings.Normalize();
        settings.SourcePath = path;
        return settings;
    }

    // Writes back to wherever this instance was loaded from. An instance built
    // with `new` (never loaded) has no home and refuses to guess one.
    public bool Save() => SourcePath is { Length: > 0 } path && Save(path);

    public bool Save(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
            return true;
        }
        catch (Exception ex)
        {
            CrashLog.Write("AppSettings.Save", ex);
            return false;
        }
    }

    // Fills in anything a hand-edited or older file left out, and clamps values
    // that would otherwise be actively harmful.
    public void Normalize()
    {
        Server ??= new ServerSettings();
        Sound ??= new SoundSettings();
        Mute ??= new MuteSettings();
        QuietHours ??= new QuietHoursSettings();
        RateLimit ??= new RateLimitSettings();
        Dedupe ??= new DedupeSettings();
        Presence ??= new PresenceSettings();
        History ??= new HistorySettings();
        Popup ??= new PopupSettings();
        Links ??= new LinkSettings();
        Updates ??= new UpdateSettings();
        // Scheme names only, lower case, each once; never one OpenLinks refuses.
        Links.AllowedSchemes = (Links.AllowedSchemes ?? new List<string>())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim().TrimEnd(':').ToLowerInvariant())
            .Where(Notipet.Core.OpenLinks.IsListable)
            .Distinct()
            .ToList();
        Popup.TimeoutSec = Math.Clamp(Popup.TimeoutSec, 3, 120);
        Popup.StayLevels ??= new List<string>();
        if (Popup.LegacyStayUntilClicked == true && Popup.StayLevels.Count == 0)
        {
            Popup.StayLevels = NotificationLevelNames.ToList();
        }
        Popup.LegacyStayUntilClicked = null;
        // Known names only, one spelling, each once.
        Popup.StayLevels = Popup.StayLevels
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim().ToLowerInvariant())
            .Where(name => NotificationLevelNames.Contains(name))
            .Distinct()
            .ToList();
        Channels ??= new Dictionary<string, ChannelSettings>(StringComparer.OrdinalIgnoreCase);
        Sources ??= new Dictionary<string, SourceSettings>(StringComparer.OrdinalIgnoreCase);

        RateLimit.PerSource ??= new BucketSettings { Capacity = 5, RefillPerMinute = 10 };
        RateLimit.Global ??= new BucketSettings { Capacity = 20, RefillPerMinute = 60 };
        Sound.Library ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Sound.ByLevel ??= new Dictionary<string, SoundSpec>(StringComparer.OrdinalIgnoreCase);
        QuietHours.AllowLevels ??= new List<string> { "critical" };

        if (Sound.AllowedRoots is null || Sound.AllowedRoots.Count == 0)
        {
            Sound.AllowedRoots = new List<string> { @"%LOCALAPPDATA%\notipet\sounds", @"%SystemRoot%\Media" };
        }

        foreach (var (level, spec) in SoundSettings.DefaultByLevel())
        {
            if (!Sound.ByLevel.ContainsKey(level)) Sound.ByLevel[level] = spec;
        }

        foreach (var id in new[] { Channels_WindowsSound, Channels_TrayBalloon })
        {
            if (!Channels.TryGetValue(id, out var channel)) Channels[id] = new ChannelSettings { Enabled = true };
            else if (channel is null) Channels[id] = new ChannelSettings { Enabled = true };
        }
        foreach (var id in new[] { Channels_WindowsToast, Channels_MobilePush })
        {
            if (!Channels.ContainsKey(id)) Channels[id] = new ChannelSettings { Enabled = false };
        }

        foreach (var id in new[] { PayloadMapper.SourceClaude, PayloadMapper.SourceCodex, PayloadMapper.SourceManual })
        {
            if (!Sources.ContainsKey(id)) Sources[id] = new SourceSettings { Enabled = true };
        }

        Sound.Volume = Math.Clamp(Sound.Volume, 0d, 1d);
        // Hard ceiling regardless of what the file says: a misconfigured hook
        // firing until_ack in a loop must not leave a siren running all day.
        Sound.MaxDurationSec = Sound.MaxDurationSec == UnlimitedAlarmSeconds
            ? UnlimitedAlarmSeconds
            : Math.Clamp(Sound.MaxDurationSec, 1, AbsoluteMaxAlarmSeconds);
        Server.MaxBodyBytes = Math.Clamp(Server.MaxBodyBytes, 1024, 1024 * 1024);
        Server.RemoteTimeoutMs = Math.Clamp(Server.RemoteTimeoutMs, 100, 30000);
        Server.Port = Server.Port is < 0 or > 65535 ? 0 : Server.Port;
        Dedupe.WindowSec = Math.Clamp(Dedupe.WindowSec, 0, 3600);
        Presence.PollSec = Math.Clamp(Presence.PollSec, 1, 600);
        Presence.IdleThresholdSec = Math.Clamp(Presence.IdleThresholdSec, 10, 86400);
        Presence.AtDeskMaxRepeats = Math.Clamp(Presence.AtDeskMaxRepeats, 1, 10);
        if (string.IsNullOrWhiteSpace(Presence.AtDeskSound)) Presence.AtDeskSound = "Notification.Default";
        Language = Language?.Trim().ToUpperInvariant() switch
        {
            "EN" or "ENGLISH" => "EN",
            "KO" or "KOREAN" => "KO",
            _ => "System"
        };
        Theme = Theme?.Trim().ToLowerInvariant() switch
        {
            "light" => "Light",
            "dark" => "Dark",
            _ => "System"
        };
        History.KeepInMemory = Math.Clamp(History.KeepInMemory, 10, 5000);
    }

    // The six levels by wire name, in severity order.
    public static readonly string[] NotificationLevelNames = { "info", "success", "attention", "warn", "error", "critical" };

    public const int AbsoluteMaxAlarmSeconds = 600;

    // sound.maxDurationSec == 0 means "no deadline". It applies to until_ack
    // alarms only, and only because the user chose it in Settings: an alarm
    // that rings until acknowledged has an off switch by definition, and the
    // cap was silencing the siren in exactly the case it exists for - the user
    // away from the desk for longer than the cap. Every other repeat mode, and
    // any per-request duration, is still bounded by AbsoluteMaxAlarmSeconds, so
    // a misfiring hook cannot invent an endless alarm on its own.
    public const int UnlimitedAlarmSeconds = 0;

    public const string Channels_WindowsSound = "windows_sound";
    public const string Channels_TrayBalloon = "tray_balloon";
    public const string Channels_WindowsToast = "windows_toast";
    public const string Channels_MobilePush = "mobile_push";

    public ChannelSettings Channel(string id)
    {
        if (!Channels.TryGetValue(id, out var settings) || settings is null)
        {
            settings = new ChannelSettings { Enabled = id is Channels_WindowsSound or Channels_TrayBalloon };
            Channels[id] = settings;
        }
        return settings;
    }

    public SourceSettings Source(string id)
    {
        if (!Sources.TryGetValue(id, out var settings) || settings is null)
        {
            settings = new SourceSettings { Enabled = true };
            Sources[id] = settings;
        }
        return settings;
    }

    public SoundSpec SoundForLevel(NotificationLevel level)
    {
        var key = NotificationLevelParser.ToWire(level);
        if (Sound.ByLevel.TryGetValue(key, out var spec) && spec is not null) return spec;
        return SoundSettings.DefaultByLevel()[key];
    }

    private static string WriteTemp(string path, string json)
    {
        File.WriteAllText(path, json);
        return path;
    }

    public static bool RunSelfTest()
    {
        var temp = Path.Combine(Path.GetTempPath(), "notipet-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var path = Path.Combine(temp, "settings.json");
        try
        {
            // A missing file yields defaults, not an exception.
            var fresh = Load(path);
            if (fresh.Sound.ByLevel.Count < 6) return false;
            if (!fresh.Channel(Channels_WindowsSound).Enabled) return false;
            if (fresh.Channel(Channels_MobilePush).Enabled) return false;
            if (fresh.Channel(Channels_MobilePush).OutboundNetworkApproved) return false;

            fresh.Sound.Volume = 0.42;
            // Save() goes back to where Load() read from - never to the real
            // settings file for an instance loaded from somewhere else.
            if (fresh.SourcePath != path) return false;
            if (!fresh.Save()) return false;
            if (new AppSettings().Save()) return false;
            var reloaded = Load(path);
            if (Math.Abs(reloaded.Sound.Volume - 0.42) > 0.0001) return false;

            // A file written by a newer build keeps its unknown keys on save.
            File.WriteAllText(path, "{\"schemaVersion\":1,\"futureKey\":{\"a\":1}}");
            var withExtra = Load(path);
            if (withExtra.Extra is null || !withExtra.Extra.ContainsKey("futureKey")) return false;
            withExtra.Save(path);
            if (!File.ReadAllText(path).Contains("futureKey", StringComparison.Ordinal)) return false;
            // Missing sections must have been materialised, not left null.
            if (withExtra.Sound.ByLevel.Count < 6) return false;

            // Corrupt input degrades to defaults rather than throwing.
            File.WriteAllText(path, "{ this is not json");
            if (Load(path).Sound.Volume != 0.7) return false;

            // Clamps.
            var wild = new AppSettings();
            wild.Sound.MaxDurationSec = 99999;
            wild.Sound.Volume = 5;
            wild.Server.Port = -7;
            wild.Normalize();
            if (wild.Sound.MaxDurationSec != AbsoluteMaxAlarmSeconds) return false;
            if (wild.Sound.Volume != 1d) return false;
            if (wild.Server.Port != 0) return false;

            // ...but zero is not a stray value to clamp away: it is the user's
            // "no limit", and Normalize() must leave it alone.
            var endless = new AppSettings();
            endless.Sound.MaxDurationSec = UnlimitedAlarmSeconds;
            endless.Normalize();
            if (endless.Sound.MaxDurationSec != UnlimitedAlarmSeconds) return false;

            // Pop-ups that stay: per level. The 1.2.0 single switch becomes
            // "every level" once, and is not written back.
            var legacy = Load(WriteTemp(path, "{\"popup\":{\"stayUntilClicked\":true}}"));
            if (legacy.Popup.StayLevels.Count != 6 || legacy.Popup.LegacyStayUntilClicked is not null) return false;
            if (!legacy.Popup.Stays(NotificationLevel.Info) || !legacy.Popup.UseOwnPopup) return false;
            legacy.Save(path);
            if (File.ReadAllText(path).Contains("stayUntilClicked", StringComparison.Ordinal)) return false;

            var picked = Load(WriteTemp(path, "{\"popup\":{\"stayLevels\":[\"Attention\",\"critical\",\"critical\",\"loud\"]}}"));
            if (picked.Popup.StayLevels.Count != 2) return false;     // normalised, de-duplicated, unknown dropped
            if (!picked.Popup.Stays(NotificationLevel.Attention) || picked.Popup.Stays(NotificationLevel.Success)) return false;

            var none = new AppSettings();
            none.Normalize();
            if (none.Popup.UseOwnPopup || none.Popup.Stays(NotificationLevel.Critical)) return false;
            if (none.Links.AllowedSchemes.Count != 0) return false;
            if (none.Theme != "System") return false;
            // Updates are checked only on request unless the user opts in.
            if (none.Updates.AutoCheck) return false;
            var themed = new AppSettings { Theme = " dark " };
            themed.Normalize();
            if (themed.Theme != "Dark") return false;
            themed.Theme = "purple";
            themed.Normalize();
            if (themed.Theme != "System") return false;

            // Listed link schemes: one spelling, and never one that stays refused.
            var links = new AppSettings();
            links.Links.AllowedSchemes = new List<string> { " CodexBridge: ", "codexbridge", "file", "ms-settings", "https", "bad scheme", "" };
            links.Normalize();
            if (!links.Links.AllowedSchemes.SequenceEqual(new[] { "codexbridge" })) return false;

            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }
}
