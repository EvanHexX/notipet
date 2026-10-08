using System;

namespace Notipet.Shared;

// Severity of a notification. Drives the default sound, the balloon icon, and
// which delivery rules let it through. Ordered from quietest to loudest.
public enum NotificationLevel
{
    Info = 0,
    Success = 1,
    Attention = 2,
    Warn = 3,
    Error = 4,
    Critical = 5
}

public static class NotificationLevelParser
{
    // Unknown values map to Info rather than failing: the level string is an
    // external contract and a newer caller must not break an older daemon.
    public static NotificationLevel Parse(string? value, out bool recognized)
    {
        recognized = true;
        switch (value?.Trim().ToLowerInvariant())
        {
            case "info" or "information" or "debug" or "low": return NotificationLevel.Info;
            case "success" or "ok" or "done" or "complete": return NotificationLevel.Success;
            case "attention" or "needs-input" or "needs_input" or "prompt": return NotificationLevel.Attention;
            case "warn" or "warning": return NotificationLevel.Warn;
            case "error" or "fail" or "failure": return NotificationLevel.Error;
            case "critical" or "fatal" or "emergency": return NotificationLevel.Critical;
            case null or "": recognized = true; return NotificationLevel.Info;
            default: recognized = false; return NotificationLevel.Info;
        }
    }

    public static NotificationLevel Parse(string? value) => Parse(value, out _);

    public static string ToWire(NotificationLevel level) => level switch
    {
        NotificationLevel.Success => "success",
        NotificationLevel.Attention => "attention",
        NotificationLevel.Warn => "warn",
        NotificationLevel.Error => "error",
        NotificationLevel.Critical => "critical",
        _ => "info"
    };

    public static bool RunSelfTest()
    {
        if (Parse("INFO") != NotificationLevel.Info) return false;
        if (Parse("Critical") != NotificationLevel.Critical) return false;
        if (Parse("  warn  ") != NotificationLevel.Warn) return false;
        if (Parse(null) != NotificationLevel.Info) return false;
        if (Parse("") != NotificationLevel.Info) return false;

        // Unknown must degrade to Info and report itself as unrecognized so the
        // response can carry a warning instead of a 400.
        if (Parse("bogus", out var recognized) != NotificationLevel.Info) return false;
        if (recognized) return false;
        if (Parse("error", out var known) != NotificationLevel.Error || !known) return false;

        if (ToWire(NotificationLevel.Attention) != "attention") return false;
        if (Parse(ToWire(NotificationLevel.Error)) != NotificationLevel.Error) return false;
        return true;
    }
}
