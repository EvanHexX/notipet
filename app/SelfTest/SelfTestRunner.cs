using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Notipet.Core;
using Notipet.Http;
using Notipet.Presence;
using Notipet.Rules;
using Notipet.Settings;
using Notipet.Shared;
using Notipet.Sound;

namespace Notipet.SelfTest;

// Headless verification, runnable as `NotipetTray.exe --self-test` with no tray
// icon, no window and no sound. The house rule is that work is not verified
// until something actually ran, so this is the thing that runs.
[SupportedOSPlatform("windows10.0.19041.0")]
internal static class SelfTestRunner
{
    public static int Run()
    {
        AttachParentConsole();

        var checks = new (string Name, Func<bool> Check)[]
        {
            ("Loc", Loc.RunSelfTest),
            ("NotificationLevelParser", NotificationLevelParser.RunSelfTest),
            ("PayloadMapper", PayloadMapper.RunSelfTest),
            ("AgentIdentity", AgentIdentity.RunSelfTest),
            ("EnvelopeFactory", EnvelopeFactory.RunSelfTest),
            ("ThreadLinks", ThreadLinks.RunSelfTest),
            ("ThreadTitleLookup", ThreadTitleLookup.RunSelfTest),
            ("HistoryGrouping", Notipet.Windows.HistoryGrouping.RunSelfTest),
            ("AppSettings", AppSettings.RunSelfTest),
            ("HistoryStore", HistoryStore.RunSelfTest),
            ("QuietHoursRule", QuietHoursRule.RunSelfTest),
            ("RateLimitRule", RateLimitRule.RunSelfTest),
            ("SystemSoundCatalog", SystemSoundCatalog.RunSelfTest),
            ("SoundResolver", SoundResolver.RunSelfTest),
            ("AlarmRegistry", AlarmRegistry.RunSelfTest),
            ("PresenceMonitor", PresenceMonitor.RunSelfTest),
            ("UiText", Notipet.Windows.UiText.RunSelfTest),
            ("MenuGlyphs", Notipet.Tray.MenuGlyphs.RunSelfTest),
            ("AuthGuard", AuthGuard.RunSelfTest),
            ("RuntimeFile", RuntimeFile.RunSelfTest),
            ("HttpEndToEnd", HttpSelfTest.Run),
        };

        var failures = 0;
        foreach (var (name, check) in checks)
        {
            bool passed;
            try
            {
                passed = check();
            }
            catch (Exception ex)
            {
                passed = false;
                Console.Error.WriteLine($"  {name}: {ex}");
            }
            Console.WriteLine($"[{(passed ? "PASS" : "FAIL")}] {name}");
            if (!passed) failures++;
        }

        Console.WriteLine(failures == 0
            ? $"notipet self-test: all {checks.Length} checks passed"
            : $"notipet self-test: {failures} of {checks.Length} checks FAILED");
        Console.Out.Flush();
        return failures == 0 ? 0 : 1;
    }

    // A WinExe has no console of its own, so a plain `NotipetTray.exe --self-test`
    // from a shell would print into the void. Borrowing the parent's console
    // fixes that - but only when we do not already have one: attaching over an
    // existing console (which is what `dotnet run` gives us) silently replaces
    // the handles Console already cached and swallows every line.
    private static void AttachParentConsole()
    {
        try
        {
            if (GetConsoleWindow() != IntPtr.Zero) return;
            if (!AttachConsole(AttachParentProcess)) return;

            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
        }
        catch
        {
            // Without a console the exit code still carries the verdict.
        }
    }

    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();
}
