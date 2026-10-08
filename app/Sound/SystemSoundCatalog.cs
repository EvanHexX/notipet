using System;
using System.IO;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Notipet.Sound;

// Resolves a Windows sound alias such as "Notification.Default" to the .wav the
// user actually has configured, by reading the scheme the Sound control panel
// writes.
//
// This is what lets the primary engine play the user's own system sounds with
// volume and repeat control. Without it we would be stuck choosing between
// PlaySound (system aliases, but no volume at all) and MediaPlayer (volume, but
// only files we ship) - the catalog makes it one path instead of two.
[SupportedOSPlatform("windows")]
internal static class SystemSoundCatalog
{
    private const string SchemeRoot = @"AppEvents\Schemes\Apps\.Default";

    // Aliases worth offering in the UI later; the resolver accepts any string.
    public static readonly string[] KnownAliases =
    {
        "Notification.Default",
        "Notification.IM",
        "Notification.Looping.Alarm",
        "Notification.Looping.Call",
        "Notification.Reminder",
        "SystemAsterisk",
        "SystemExclamation",
        "SystemHand",
        "SystemNotification",
        "SystemQuestion",
        "WindowsLogon"
    };

    public static string? ResolvePath(string? alias)
    {
        if (string.IsNullOrWhiteSpace(alias)) return null;

        // The alias must not escape the scheme key into an arbitrary registry
        // path: the value can arrive from an HTTP caller.
        if (alias.IndexOfAny(new[] { '\\', '/' }) >= 0) return null;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"{SchemeRoot}\{alias}\.Current");
            if (key?.GetValue(null) is not string raw || string.IsNullOrWhiteSpace(raw)) return null;

            // Stored as REG_EXPAND_SZ, e.g. "%SystemRoot%\Media\Windows Notify.wav".
            var expanded = Environment.ExpandEnvironmentVariables(raw);
            return File.Exists(expanded) ? expanded : null;
        }
        catch
        {
            return null;
        }
    }

    // Last-resort audible fallback when neither the requested alias nor the
    // configured default resolves - silence is the one outcome a notification
    // daemon must not produce quietly.
    public static string? FallbackPath()
    {
        foreach (var alias in new[] { "Notification.Default", "SystemNotification", "SystemAsterisk" })
        {
            var path = ResolvePath(alias);
            if (path is not null) return path;
        }

        var media = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media");
        foreach (var name in new[] { "Windows Notify System Generic.wav", "Windows Notify.wav", "notify.wav", "Windows Ding.wav" })
        {
            var candidate = Path.Combine(media, name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public static bool RunSelfTest()
    {
        // A traversal attempt must be refused rather than reaching the registry.
        if (ResolvePath(@"..\..\Evil") is not null) return false;
        if (ResolvePath("") is not null) return false;
        if (ResolvePath(null) is not null) return false;

        // Resolution itself is environment-dependent, so only assert that a hit
        // is a real file. A miss is legitimate on a stripped-down install.
        var resolved = ResolvePath("Notification.Default");
        if (resolved is not null && !File.Exists(resolved)) return false;
        return true;
    }
}

// The registry catalog as core's ISoundCatalog. Installed in Program.Main
// before anything resolves a sound, self-test included.
[SupportedOSPlatform("windows")]
internal sealed class WindowsSoundCatalog : ISoundCatalog
{
    public static readonly WindowsSoundCatalog Instance = new();
    public string? ResolvePath(string? alias) => SystemSoundCatalog.ResolvePath(alias);
    public string? FallbackPath() => SystemSoundCatalog.FallbackPath();
}
