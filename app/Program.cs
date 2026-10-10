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

    // Set when this instance was started by UiWatchdog to replace a hung one.
    public static bool RecoveredFromHang { get; private set; }

    // Set when the previous instance restarted itself because the graphics
    // adapters changed (GraphicsAdapters).
    public static bool RestartedForGraphics { get; private set; }

    [STAThread]
    private static int Main(string[] args)
    {
        // First, before anything else: Velopack runs the installed app with
        // its own arguments to install, update or remove it, handles those
        // here and exits. A normal start returns at once. Nothing is applied
        // at startup on its own - installing an update is always a click.
        Velopack.VelopackApp.Build()
            .OnAfterInstallFastCallback(v => Notipet.Update.Installation.OnInstalled(v.ToString()))
            .OnAfterUpdateFastCallback(v => Notipet.Update.Installation.OnUpdated(v.ToString()))
            .OnBeforeUninstallFastCallback(v => Notipet.Update.Installation.OnUninstalling(v.ToString()))
            .SetAutoApplyOnStartup(false)
            .Run();

        var verb = args.Length > 0 ? args[0].ToLowerInvariant() : "";

        // Started in place of another instance - one whose UI thread hung
        // (UiWatchdog), or one that restarted because the graphics adapters
        // changed (`--restart-after <pid> graphics`): let that one go first,
        // or the single-instance check below would hand straight back to it.
        if (verb == "--restart-after")
        {
            if (args.Length > 1 && int.TryParse(args[1], out var previousPid))
            {
                try { using var previous = System.Diagnostics.Process.GetProcessById(previousPid); previous.WaitForExit(15000); }
                catch { /* already gone */ }
            }
            if (args.Length > 2 && args[2] == "graphics") RestartedForGraphics = true;
            else RecoveredFromHang = true;
            verb = "";
        }

        // The core resolves sound names through whatever catalog the platform
        // installs; on Windows that is the user's Sound control panel scheme.
        SoundResolver.Catalog = WindowsSoundCatalog.Instance;

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
