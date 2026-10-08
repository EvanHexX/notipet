using System;
using System.IO;

namespace Notipet;

// Appends unhandled-exception details to crash.log so abnormal exits can be
// diagnosed from the field. Logging must never make things worse, so every
// failure here is swallowed.
internal static class CrashLog
{
    private static readonly object Gate = new();

    public static string LogPath => Paths.CrashLogPath;

    public static void Write(string source, Exception? exception)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Paths.DataDir);
                File.AppendAllText(LogPath,
                    $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {source}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
            }
        }
        catch
        {
        }
    }
}
