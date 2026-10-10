using System;
using System.IO;

namespace Notipet;

// daemon.log: when the daemon started and why it stopped, one line each.
//
// Added after a daemon vanished with nothing in crash.log or the event log,
// and the next one ran for hours that the CLI could not find (its
// runtime.json named the dead one). A crash log only speaks when something
// throws; this one also covers a clean quit, `notipet stop`, the watchdog's
// restart, and - on the next start - a predecessor that never got to say
// goodbye. Small and bounded: past MaxBytes the file moves to daemon.log.1.
// Never throws, and never contains notification text.
internal static class DaemonLog
{
    public const long MaxBytes = 256 * 1024;
    private static readonly object Gate = new();

    // As CrashLog.OverridePath: the self-tests point it at a scratch file.
    public static string? OverridePath { get; set; }

    public static string LogPath => OverridePath ?? Path.Combine(Paths.DataDir, "daemon.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                var path = LogPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                {
                    File.Move(path, path + ".1", overwrite: true);
                }
                File.AppendAllText(path,
                    $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] pid {Environment.ProcessId}: {message}{Environment.NewLine}");
            }
        }
        catch
        {
        }
    }

    public static bool RunSelfTest()
    {
        var dir = Path.Combine(Path.GetTempPath(), "notipet-daemonlog-" + Guid.NewGuid().ToString("N"));
        var previous = OverridePath;
        try
        {
            OverridePath = Path.Combine(dir, "daemon.log");
            Write("started");
            Write("quit: tray menu");
            var lines = File.ReadAllLines(OverridePath);
            if (lines.Length != 2 || !lines[1].EndsWith($"pid {Environment.ProcessId}: quit: tray menu", StringComparison.Ordinal)) return false;

            // Bounded: an oversized log rolls over to .1 and starts again.
            File.WriteAllText(OverridePath, new string('x', (int)MaxBytes + 1));
            Write("after roll");
            return File.Exists(OverridePath + ".1") && File.ReadAllLines(OverridePath).Length == 1;
        }
        finally
        {
            OverridePath = previous;
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
