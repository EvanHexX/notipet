using System;
using System.Collections.Generic;
using Notipet.Core;
using Notipet.Http;
using Notipet.Rules;
using Notipet.Settings;
using Notipet.Shared;
using Notipet.Sound;

namespace Notipet;

// The core's headless checks, in one list, so every front end runs the same
// ones: the Windows daemon adds its platform checks (sound engines, tray
// glyphs, the HTTP end-to-end run) on top in SelfTestRunner, and a macOS front
// end would do the same. Nothing here may open a window, make a sound, or
// write the user's real settings.
public static class CoreSelfTests
{
    public static IReadOnlyList<(string Name, Func<bool> Check)> All { get; } = new (string, Func<bool>)[]
    {
        ("Loc", Loc.RunSelfTest),
        ("NotificationLevelParser", NotificationLevelParser.RunSelfTest),
        ("PayloadMapper", PayloadMapper.RunSelfTest),
        ("AgentIdentity", AgentIdentity.RunSelfTest),
        ("EnvelopeFactory", EnvelopeFactory.RunSelfTest),
        ("ThreadLinks", ThreadLinks.RunSelfTest),
        ("OpenLinks", OpenLinks.RunSelfTest),
        ("ThreadTitleLookup", ThreadTitleLookup.RunSelfTest),
        ("HistoryGrouping", HistoryGrouping.RunSelfTest),
        ("AppSettings", AppSettings.RunSelfTest),
        ("HistoryStore", HistoryStore.RunSelfTest),
        ("QuietHoursRule", QuietHoursRule.RunSelfTest),
        ("AlertScope", AlertScope.RunSelfTest),
        ("RateLimitRule", RateLimitRule.RunSelfTest),
        ("SoundResolver", SoundResolver.RunSelfTest),
        ("AlarmRegistry", AlarmRegistry.RunSelfTest),
        ("AlarmResolver", AlarmResolver.RunSelfTest),
        ("AuthGuard", AuthGuard.RunSelfTest),
        ("RuntimeFile", RuntimeFile.RunSelfTest),
        ("DaemonLog", DaemonLog.RunSelfTest),
    };
}
