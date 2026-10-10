using System;
using System.IO;

namespace Notipet;

// Appends unhandled-exception details to crash.log so abnormal exits can be
// diagnosed from the field. Logging must never make things worse, so every
// failure here is swallowed.
internal static class CrashLog
{
    private static readonly object Gate = new();

    // Where Write goes instead, when set. The self-tests point it at a
    // scratch file: their stub channel throws on purpose, and those entries
    // filled the user's crash.log and buried the real ones.
    public static string? OverridePath { get; set; }

    public static string LogPath => OverridePath ?? Paths.CrashLogPath;

    public static void Write(string source, Exception? exception)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath,
                    $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {source}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
            }
        }
        catch
        {
        }
    }
}
