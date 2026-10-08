using System;
using System.Threading;
using Microsoft.UI.Dispatching;
using Notipet.SelfTest;
using Notipet.Settings;
using Notipet.Shared;
using Notipet.Sound;
using Notipet.Tray;

namespace Notipet;

internal static class Program
{
    private const string SingleInstanceMutexName = @"Local\Notipet.SingleInstance";
    private const string ShowSignalEventName = @"Local\Notipet.Show";
    private const string SettingsSignalEventName = @"Local\Notipet.ShowSettings";

    [STAThread]
    private static int Main(string[] args)
    {
        var verb = args.Length > 0 ? args[0].ToLowerInvariant() : "";

        // Both diagnostic paths must run before any XAML or WinRT
        // initialisation so they stay headless.
        if (verb is "--self-test" or "-t")
        {
            return SelfTestRunner.Run();
        }

        if (verb == "--test-sound")
        {
            return TestSound(args.Length > 1 ? args[1] : "attention");
        }

        // Build-time helper: writes app/Assets/Notipet.ico from the same bell
        // renderer the tray uses, so the exe icon never drifts from it.
        if (verb == "--export-icon" && args.Length > 1)
        {
            var ok = IconExport.Write(args[1]);
            if (args.Length > 2) ok &= IconExport.WriteTraySheet(args[2]);
            return ok ? 0 : 1;
        }

        if (verb is "--help" or "-h" or "/?")
        {
            Console.WriteLine("notipet - notification daemon for AI coding agents");
            Console.WriteLine("  (no arguments)        run in the system tray");
            Console.WriteLine("  --self-test           run headless checks and exit");
            Console.WriteLine("  --test-sound [level]  play one level's sound and exit");
            Console.WriteLine("  --settings            open Settings on the running instance");
            return 0;
        }

        using var mutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var isFirstInstance);
        using var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalEventName);
        using var settingsSignal = new EventWaitHandle(false, EventResetMode.AutoReset, SettingsSignalEventName);
        if (!isFirstInstance)
        {
            // A second launch drives the instance already running, so a shortcut
            // to the exe is a usable way in rather than a no-op.
            if (verb == "--settings") settingsSignal.Set();
            else showSignal.Set();
            return 0;
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Microsoft.UI.Xaml.Application.Start(callbackParams =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App(showSignal, settingsSignal);
        });
        return 0;
    }

    // Proves the audio path on its own, before any HTTP or tray code is
    // involved - the sound engine is the MVP's only novel dependency, so it is
    // worth being able to test in isolation.
    private static int TestSound(string levelName)
    {
        var settings = AppSettings.Load();
        var level = NotificationLevelParser.Parse(levelName, out var recognized);
        if (!recognized) Console.Error.WriteLine($"unknown level '{levelName}', using info");

        using var sound = new SoundService(() => settings);
        var warnings = new System.Collections.Generic.List<string>();
        var resolved = SoundResolver.Resolve(null, level, settings, warnings);

        Console.WriteLine($"level   : {NotificationLevelParser.ToWire(level)}");
        Console.WriteLine($"source  : {resolved.Path ?? resolved.Alias ?? "(none)"}");
        Console.WriteLine($"volume  : {resolved.Volume:0.00}");
        var maxText = resolved.MaxDurationSec <= 0 ? "no limit" : $"{resolved.MaxDurationSec}s";
        Console.WriteLine($"repeat  : {resolved.Repeat} x{resolved.RepeatCount} every {resolved.IntervalMs}ms, max {maxText}");
        foreach (var warning in warnings) Console.WriteLine($"warning : {warning}");

        var outcome = sound.Play(resolved, level, "cli-test");
        if (!outcome.Played)
        {
            Console.Error.WriteLine($"playback failed: {outcome.Detail}");
            return 1;
        }

        Console.WriteLine($"engine  : {sound.Describe().Name}");
        // Repeating levels would otherwise be cut off by process exit; give the
        // alarm a few seconds so the mode is actually audible.
        var hold = resolved.Repeat == RepeatMode.Once
            ? 2000
            : resolved.MaxDurationSec <= 0 ? 8000 : Math.Min(8000, resolved.MaxDurationSec * 1000);
        Thread.Sleep(hold);
        sound.Alarms.StopAll();
        return 0;
    }
}
