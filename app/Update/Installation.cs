using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Notipet.Http;

namespace Notipet.Update;

// What Notipet does when Velopack installs, updates or removes it. Velopack
// runs these in a short-lived process from the installed folder and kills it
// after 30 seconds, so each step is quick, local, and never throws.
//
// The installed folder (%LOCALAPPDATA%\NotipetApp\current) keeps its path
// across updates, which is what makes it safe to put on PATH and in the
// Run key. Settings, logs and runtime.json stay in %LOCALAPPDATA%\notipet -
// a separate folder, so removing the app keeps them.
[SupportedOSPlatform("windows10.0.19041.0")]
internal static class Installation
{
    private static string AppDir => AppContext.BaseDirectory.TrimEnd('\\');

    public static void OnInstalled(string version)
    {
        AddToUserPath(AppDir);
        RepointAutostart();
        DaemonLog.Write($"installed v{version} in {AppDir}");
    }

    public static void OnUpdated(string version)
    {
        AddToUserPath(AppDir);
        RepointAutostart();
        DaemonLog.Write($"updated to v{version}");
    }

    public static void OnUninstalling(string version)
    {
        RemoveFromUserPath(AppDir);
        // Only our own entry: a Run key pointing at a dev build elsewhere is
        // not ours to remove.
        if (AutostartPointsInto(AppDir)) Autostart.TrySet(false);
        // Stopped by force, it could not remove its runtime.json; do it here so
        // the CLI is not left pointing at a process that is gone.
        if (StopInstalledDaemon()) RuntimeFile.Delete(Process.GetCurrentProcess().SessionId);
        DaemonLog.Write($"uninstalling v{version} (settings and logs in {Paths.DataDir} are kept)");
    }

    // "Start with Windows" names an exe path. Enabled from an older install
    // or from bin\, it would keep starting that one; point it here.
    private static void RepointAutostart()
    {
        try
        {
            if (Autostart.IsEnabled() && !AutostartPointsInto(AppDir)) Autostart.TrySet(true);
        }
        catch (Exception ex)
        {
            CrashLog.Write("Installation.Autostart", ex);
        }
    }

    private static bool AutostartPointsInto(string dir)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            return key?.GetValue("Notipet") is string value
                   && value.Trim('"').StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    // The tray app running from the installed folder; Velopack replaces or
    // deletes that folder next. Not this process (the hook itself).
    private static bool StopInstalledDaemon()
    {
        var stopped = false;
        foreach (var process in Process.GetProcessesByName("NotipetTray"))
        {
            try
            {
                if (process.Id == Environment.ProcessId) continue;
                var path = process.MainModule?.FileName;
                if (path is not null && path.StartsWith(AppDir + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    process.Kill();
                    process.WaitForExit(5000);
                    stopped = true;
                }
            }
            catch (Exception ex)
            {
                CrashLog.Write("Installation.Stop", ex);
            }
            finally
            {
                process.Dispose();
            }
        }
        return stopped;
    }

    // ----- PATH -----

    // The user's PATH (HKCU\Environment), so `notipet` works in new terminals.
    // Read and written unexpanded, so %USERPROFILE%-style entries survive.
    public static void AddToUserPath(string dir) => EditUserPath(entries =>
        entries.Any(e => SameDir(e, dir)) ? null : entries.Append(dir).ToArray());

    public static void RemoveFromUserPath(string dir) => EditUserPath(entries =>
        entries.Any(e => SameDir(e, dir)) ? entries.Where(e => !SameDir(e, dir)).ToArray() : null);

    private static void EditUserPath(Func<string[], string[]?> edit)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey("Environment", writable: true);
            if (key is null) return;
            var current = key.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
            var kind = key.GetValueNames().Contains("Path", StringComparer.OrdinalIgnoreCase) ? key.GetValueKind("Path") : RegistryValueKind.ExpandString;
            var entries = current.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var edited = edit(entries);
            if (edited is null) return;
            key.SetValue("Path", string.Join(";", edited), kind == RegistryValueKind.String ? RegistryValueKind.String : RegistryValueKind.ExpandString);
            BroadcastEnvironmentChange();
        }
        catch (Exception ex)
        {
            CrashLog.Write("Installation.Path", ex);
        }
    }

    private static bool SameDir(string a, string b) =>
        string.Equals(Environment.ExpandEnvironmentVariables(a).TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    // Tells Explorer (and through it, new terminals) that PATH changed.
    private static void BroadcastEnvironmentChange()
    {
        SendMessageTimeout(new IntPtr(0xFFFF), 0x001A, IntPtr.Zero, "Environment", 0x0002, 2000, out _);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);

    public static bool RunSelfTest()
    {
        // The PATH editing itself, on lists - never on the user's real PATH.
        string[]? Add(string[] entries, string dir) => entries.Any(e => SameDir(e, dir)) ? null : entries.Append(dir).ToArray();
        var dir = @"C:\Users\x\AppData\Local\NotipetApp\current";
        var added = Add(new[] { @"C:\Windows", @"%USERPROFILE%\bin" }, dir);
        if (added is null || added[^1] != dir || added.Length != 3) return false;
        // Already there (any case, trailing slash): no change.
        if (Add(new[] { dir.ToUpperInvariant() + "\\" }, dir) is not null) return false;
        return SameDir(@"%SystemRoot%\", Environment.ExpandEnvironmentVariables(@"%SystemRoot%"));
    }
}
