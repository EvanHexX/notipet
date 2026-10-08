using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using Notipet.Settings;
using Notipet.Shared;

namespace Notipet.Sound;

internal enum RepeatMode
{
    Once,
    Repeat,
    UntilAck
}

// A request's sound spec after settings and level defaults have been folded in.
// Path may be null when only an alias resolved, in which case the fallback
// engine plays it by alias.
internal sealed record ResolvedSound(
    string? Path,
    string? Alias,
    double Volume,
    RepeatMode Repeat,
    int RepeatCount,
    int IntervalMs,
    int MaxDurationSec,
    bool Silent = false)
{
    public bool HasSource => Path is not null || Alias is not null;

    // "No sound" chosen as the at-desk replacement: the channel reports a skip
    // rather than a failure, and the balloon still appears.
    public static ResolvedSound SilentSound => new(null, null, 0, RepeatMode.Once, 1, 0, 1, true);
}

[SupportedOSPlatform("windows")]
internal static class SoundResolver
{
    public static RepeatMode ParseRepeat(string? value, RepeatMode fallback = RepeatMode.Once) =>
        value?.Trim().ToLowerInvariant().Replace("-", "_") switch
        {
            "once" => RepeatMode.Once,
            "repeat" => RepeatMode.Repeat,
            "until_ack" or "untilack" => RepeatMode.UntilAck,
            _ => fallback
        };

    // Resolution order for the audio source: an explicit file, then a named
    // entry in the library, then an alias, then the level default, then the
    // catalog fallback. Each step that is refused adds a warning so the caller
    // can see why it got a different sound than it asked for.
    public static ResolvedSound Resolve(
        SoundSpec? request,
        NotificationLevel level,
        AppSettings settings,
        List<string> warnings,
        bool atDesk = false)
    {
        var levelDefault = settings.SoundForLevel(level);

        string? path = null;
        string? alias = null;

        if (!string.IsNullOrWhiteSpace(request?.File))
        {
            if (TryAcceptFile(request!.File!, settings, out var accepted))
            {
                path = accepted;
            }
            else
            {
                warnings.Add("sound.file rejected: outside the allowed sound roots or missing");
            }
        }

        if (path is null && !string.IsNullOrWhiteSpace(request?.Name))
        {
            if (settings.Sound.Library.TryGetValue(request!.Name!, out var libraryPath)
                && TryAcceptFile(libraryPath, settings, out var accepted))
            {
                path = accepted;
            }
            else
            {
                warnings.Add($"sound.name '{request!.Name}' is not in the sound library");
            }
        }

        if (path is null)
        {
            alias = FirstNonEmpty(request?.Alias, levelDefault.Alias);
            path = SystemSoundCatalog.ResolvePath(alias);
            if (path is null && !string.IsNullOrWhiteSpace(request?.Alias))
            {
                // Keep the alias around: PlaySound can still play it by name
                // even though MediaPlayer needs a real file.
                warnings.Add($"sound.alias '{request!.Alias}' did not resolve to a file");
            }
        }

        if (path is null && alias is null)
        {
            path = SystemSoundCatalog.FallbackPath();
        }

        var volume = Math.Clamp(
            settings.Sound.Volume * (request?.Volume ?? levelDefault.Volume ?? 1d),
            0d, 1d);

        var repeat = request?.Repeat is not null
            ? ParseRepeat(request.Repeat, ParseRepeat(levelDefault.Repeat))
            : ParseRepeat(levelDefault.Repeat);

        var repeatCount = Math.Clamp(request?.RepeatCount ?? levelDefault.RepeatCount ?? 3, 1, 50);
        var intervalMs = Math.Clamp(request?.IntervalMs ?? levelDefault.IntervalMs ?? 1200, 200, 60000);

        // The per-request cap can only ever shorten the alarm, never extend it
        // past the settings cap - which Normalize() has already clamped to
        // AbsoluteMaxAlarmSeconds.
        int maxDuration;
        if (settings.Sound.MaxDurationSec == AppSettings.UnlimitedAlarmSeconds)
        {
            // "No deadline" in settings. A request or level default that names
            // a duration still shortens it, and only until_ack may go without
            // one - a repeat alarm already ends after its count, and anything
            // else keeps the code cap.
            var asked = request?.MaxDurationSec ?? levelDefault.MaxDurationSec;
            maxDuration = asked is > 0
                ? Math.Clamp(asked.Value, 1, AppSettings.AbsoluteMaxAlarmSeconds)
                : repeat == RepeatMode.UntilAck
                    ? AppSettings.UnlimitedAlarmSeconds
                    : AppSettings.AbsoluteMaxAlarmSeconds;
        }
        else
        {
            var requestedMax = request?.MaxDurationSec ?? levelDefault.MaxDurationSec ?? settings.Sound.MaxDurationSec;
            maxDuration = Math.Clamp(
                Math.Min(requestedMax, settings.Sound.MaxDurationSec),
                1, AppSettings.AbsoluteMaxAlarmSeconds);
        }

        // At the desk, the user can swap every level's sound for one gentler
        // sound - or for silence, when the balloon on screen is enough.
        if (atDesk && settings.Presence.ReplaceSoundAtDesk)
        {
            var replacement = settings.Presence.AtDeskSound?.Trim() ?? "";
            if (replacement.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add("silenced: at desk");
                return ResolvedSound.SilentSound;
            }

            string? replacedPath = null;
            string? replacedAlias = null;
            if (replacement.StartsWith("library:", StringComparison.OrdinalIgnoreCase))
            {
                var name = replacement["library:".Length..];
                if (settings.Sound.Library.TryGetValue(name, out var libraryPath)
                    && TryAcceptFile(libraryPath, settings, out var accepted))
                {
                    replacedPath = accepted;
                }
            }
            else if (replacement.Length > 0)
            {
                replacedAlias = replacement;
                replacedPath = SystemSoundCatalog.ResolvePath(replacement);
            }

            // A replacement that does not resolve keeps the original sound
            // rather than going silent: silence was not what was asked for.
            if (replacedPath is not null || replacedAlias is not null)
            {
                path = replacedPath;
                alias = replacedAlias;
                warnings.Add("replaced: at desk");
            }
        }

        // Sitting in front of the screen turns a long alarm into a short one.
        // A siren is for getting someone back to the desk; when they are
        // already there it is just unpleasant, and unpleasant tools get muted.
        if (atDesk && settings.Presence.ShortenAlarmsAtDesk && repeat != RepeatMode.Once)
        {
            var cap = Math.Max(1, settings.Presence.AtDeskMaxRepeats);
            repeat = cap <= 1 ? RepeatMode.Once : RepeatMode.Repeat;
            repeatCount = Math.Min(repeatCount, cap);
            // An until_ack alarm must not outlive the shortening either - not
            // even the unlimited one, which is meant for an empty chair.
            maxDuration = maxDuration == AppSettings.UnlimitedAlarmSeconds ? 30 : Math.Min(maxDuration, 30);
            warnings.Add("shortened: at desk");
        }

        return new ResolvedSound(path, alias, volume, repeat, repeatCount, intervalMs, maxDuration);
    }

    // A caller on the loopback API must not be able to make the daemon open an
    // arbitrary file off the disk, so a path is only accepted when it resolves
    // inside one of the configured roots.
    public static bool TryAcceptFile(string candidate, AppSettings settings, out string? accepted)
    {
        accepted = null;
        try
        {
            var full = System.IO.Path.GetFullPath(Environment.ExpandEnvironmentVariables(candidate));
            if (!File.Exists(full)) return false;

            foreach (var root in settings.Sound.AllowedRoots)
            {
                if (string.IsNullOrWhiteSpace(root)) continue;
                var rootFull = System.IO.Path.GetFullPath(Environment.ExpandEnvironmentVariables(root));
                if (!rootFull.EndsWith(System.IO.Path.DirectorySeparatorChar)) rootFull += System.IO.Path.DirectorySeparatorChar;
                if (full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                {
                    accepted = full;
                    return true;
                }
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return null;
    }

    public static bool RunSelfTest()
    {
        var settings = new AppSettings();
        settings.Normalize();
        var warnings = new List<string>();

        if (ParseRepeat("until-ack") != RepeatMode.UntilAck) return false;
        if (ParseRepeat("UNTIL_ACK") != RepeatMode.UntilAck) return false;
        if (ParseRepeat("nonsense") != RepeatMode.Once) return false;
        if (ParseRepeat("nonsense", RepeatMode.Repeat) != RepeatMode.Repeat) return false;

        // Level defaults carry through.
        var critical = Resolve(null, NotificationLevel.Critical, settings, warnings);
        if (critical.Repeat != RepeatMode.UntilAck) return false;
        var info = Resolve(null, NotificationLevel.Info, settings, warnings);
        if (info.Repeat != RepeatMode.Once) return false;

        // A file outside the allowed roots is refused and says so.
        warnings.Clear();
        var outside = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "notipet-not-allowed.wav");
        File.WriteAllBytes(outside, new byte[] { 1, 2, 3 });
        try
        {
            var rejected = Resolve(new SoundSpec { File = outside }, NotificationLevel.Info, settings, warnings);
            if (rejected.Path == outside) return false;
            if (warnings.Count == 0) return false;
            if (TryAcceptFile(outside, settings, out _)) return false;
        }
        finally
        {
            try { File.Delete(outside); } catch { }
        }

        // A file inside an allowed root is accepted.
        var mediaRoot = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media");
        if (Directory.Exists(mediaRoot))
        {
            var files = Directory.GetFiles(mediaRoot, "*.wav");
            if (files.Length > 0 && !TryAcceptFile(files[0], settings, out _)) return false;
        }

        // A missing file is refused even inside an allowed root.
        if (TryAcceptFile(System.IO.Path.Combine(mediaRoot, "does-not-exist-12345.wav"), settings, out _)) return false;

        // Volume multiplies settings by request and clamps.
        settings.Sound.Volume = 0.5;
        var loud = Resolve(new SoundSpec { Volume = 4 }, NotificationLevel.Info, settings, warnings);
        if (loud.Volume != 1d) return false;
        var half = Resolve(new SoundSpec { Volume = 0.5 }, NotificationLevel.Info, settings, warnings);
        if (Math.Abs(half.Volume - 0.25) > 0.0001) return false;

        // At the desk, a repeating alarm collapses to a short one; away, it
        // stays long. This is the whole point of the setting.
        settings.Presence.ShortenAlarmsAtDesk = true;
        settings.Presence.AtDeskMaxRepeats = 1;
        var away = Resolve(null, NotificationLevel.Critical, settings, warnings, atDesk: false);
        if (away.Repeat != RepeatMode.UntilAck) return false;
        var seated = Resolve(null, NotificationLevel.Critical, settings, warnings, atDesk: true);
        if (seated.Repeat != RepeatMode.Once) return false;
        if (seated.MaxDurationSec > 30) return false;

        // A cap above one keeps it repeating, just briefly.
        settings.Presence.AtDeskMaxRepeats = 2;
        var seatedTwice = Resolve(null, NotificationLevel.Critical, settings, warnings, atDesk: true);
        if (seatedTwice.Repeat != RepeatMode.Repeat || seatedTwice.RepeatCount != 2) return false;

        // Turning the feature off leaves the alarm alone even while seated.
        settings.Presence.ShortenAlarmsAtDesk = false;
        if (Resolve(null, NotificationLevel.Critical, settings, warnings, atDesk: true).Repeat != RepeatMode.UntilAck) return false;
        settings.Presence.ShortenAlarmsAtDesk = true;

        // Replacement swaps the sound while at the desk, and only then.
        settings.Presence.ReplaceSoundAtDesk = true;
        settings.Presence.AtDeskSound = "SystemAsterisk";
        var replaced = Resolve(null, NotificationLevel.Attention, settings, warnings, atDesk: true);
        if (replaced.Alias != "SystemAsterisk") return false;
        var notReplaced = Resolve(null, NotificationLevel.Attention, settings, warnings, atDesk: false);
        if (notReplaced.Alias == "SystemAsterisk") return false;

        // "none" silences the sound but is not a missing-source failure.
        settings.Presence.AtDeskSound = "none";
        var silent = Resolve(null, NotificationLevel.Critical, settings, warnings, atDesk: true);
        if (!silent.Silent) return false;

        // An unresolvable replacement keeps the original instead of going quiet.
        settings.Presence.AtDeskSound = "library:does-not-exist";
        var kept = Resolve(null, NotificationLevel.Attention, settings, warnings, atDesk: true);
        if (kept.Silent || !kept.HasSource) return false;
        settings.Presence.ReplaceSoundAtDesk = false;

        // A one-shot is already short; shortening must not turn it into
        // something else or add a spurious warning.
        var oneShot = Resolve(null, NotificationLevel.Info, settings, warnings, atDesk: true);
        if (oneShot.Repeat != RepeatMode.Once) return false;

        // The absolute alarm cap cannot be raised from a request.
        var runaway = Resolve(
            new SoundSpec { Repeat = "until_ack", MaxDurationSec = 99999 },
            NotificationLevel.Critical, settings, warnings);
        if (runaway.MaxDurationSec > settings.Sound.MaxDurationSec) return false;
        if (runaway.MaxDurationSec > AppSettings.AbsoluteMaxAlarmSeconds) return false;

        // "No limit" in settings: an until_ack alarm gets no deadline, because
        // the cap was silencing the siren while the user was away - the one
        // case it is for. This is the behaviour the user asked for after an
        // attention alarm went quiet two minutes before they came back.
        var previousMax = settings.Sound.MaxDurationSec;
        settings.Sound.MaxDurationSec = AppSettings.UnlimitedAlarmSeconds;
        var endless = Resolve(null, NotificationLevel.Critical, settings, warnings);
        if (endless.Repeat != RepeatMode.UntilAck) return false;
        if (endless.MaxDurationSec != AppSettings.UnlimitedAlarmSeconds) return false;

        // Everything else still ends. A repeat alarm is bounded by its count,
        // but its deadline must not become infinite along the way.
        var bounded = Resolve(null, NotificationLevel.Error, settings, warnings);
        if (bounded.Repeat != RepeatMode.Repeat) return false;
        if (bounded.MaxDurationSec != AppSettings.AbsoluteMaxAlarmSeconds) return false;

        // A request that names a duration still shortens an unlimited alarm.
        var asked = Resolve(
            new SoundSpec { Repeat = "until_ack", MaxDurationSec = 45 },
            NotificationLevel.Critical, settings, warnings);
        if (asked.MaxDurationSec != 45) return false;

        // And sitting at the desk still cuts it down: unlimited is for an
        // empty chair, not for the person sitting in it.
        settings.Presence.ShortenAlarmsAtDesk = true;
        settings.Presence.AtDeskMaxRepeats = 1;
        var seatedEndless = Resolve(null, NotificationLevel.Critical, settings, warnings, atDesk: true);
        if (seatedEndless.MaxDurationSec is AppSettings.UnlimitedAlarmSeconds or > 30) return false;
        settings.Sound.MaxDurationSec = previousMax;

        return true;
    }
}
