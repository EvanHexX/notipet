using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Notipet.Shared;

namespace Notipet.Http;

// Writes and clears %LOCALAPPDATA%\notipet\runtime.json, the file the CLI and
// the hooks use to find a running daemon without hardcoding a port.
internal static class RuntimeFile
{
    // Written only after the listener is actually bound: never advertise a port
    // we have not got.
    public static void Write(RuntimeInfo info)
    {
        Paths.EnsureDataDir();

        // A breadcrumb that outlives this process. runtime.json is deleted on
        // exit, and without this `notipet start` after `notipet stop` had no
        // way to find the exe unless the CLI happened to sit next to it.
        try
        {
            if (!string.IsNullOrWhiteSpace(info.ExePath)) File.WriteAllText(Paths.DaemonPathFile, info.ExePath);
        }
        catch (Exception ex)
        {
            CrashLog.Write("RuntimeFile.DaemonPath", ex);
        }

        foreach (var path in new[] { Paths.RuntimePath, Paths.RuntimePathForSession(info.SessionId) })
        {
            try
            {
                var temp = path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(info, NotipetJson.Pretty.RuntimeInfo));
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception ex)
            {
                CrashLog.Write("RuntimeFile.Write", ex);
            }
        }
    }

    public static void Delete(int sessionId)
    {
        foreach (var path in new[] { Paths.RuntimePath, Paths.RuntimePathForSession(sessionId) })
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    public static RuntimeInfo? Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize(File.ReadAllText(path), NotipetJson.Compact.RuntimeInfo);
        }
        catch
        {
            return null;
        }
    }

    // True when the file describes a process that is no longer our daemon.
    //
    // The PID-reuse check is the one people skip, and it is exactly why a
    // crashed instance's leftover file is dangerous: the OS will happily hand
    // that number to an unrelated process, and then the CLI talks to nothing.
    public static bool IsStale(RuntimeInfo? info)
    {
        if (info is null || info.Pid <= 0) return true;

        try
        {
            using var process = Process.GetProcessById(info.Pid);
            if (process.HasExited) return true;

            if (DateTimeOffset.TryParse(info.ProcessStartTimeUtc, out var recorded))
            {
                var actual = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
                // A second of tolerance: the recorded value is round-tripped
                // through text and the two clocks need not agree exactly.
                if (Math.Abs((actual - recorded).TotalSeconds) > 1) return true;
            }
            return false;
        }
        catch (ArgumentException)
        {
            // No process with that id.
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        catch
        {
            // Cannot tell (access denied, for instance). Treat as live and let
            // the health probe decide, rather than killing a working instance.
            return false;
        }
    }

    public static RuntimeInfo Describe(int port, string token, string instanceId, int sessionId, string version)
    {
        using var current = Process.GetCurrentProcess();
        return new RuntimeInfo
        {
            SchemaVersion = 1,
            InstanceId = instanceId,
            Pid = current.Id,
            ProcessStartTimeUtc = new DateTimeOffset(current.StartTime.ToUniversalTime(), TimeSpan.Zero).ToString("o"),
            SessionId = sessionId,
            Port = port,
            BaseUrl = $"http://127.0.0.1:{port}",
            Token = token,
            Version = version,
            ExePath = Environment.ProcessPath ?? "",
            StartedAtUtc = DateTimeOffset.UtcNow.ToString("o")
        };
    }

    public static bool RunSelfTest()
    {
        var dir = Path.Combine(Path.GetTempPath(), "notipet-runtime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "runtime.json");
        try
        {
            var info = Describe(47811, "tok", "inst", 1, "1.0.0");

            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(info, NotipetJson.Pretty.RuntimeInfo));
            File.Move(temp, path, overwrite: true);

            var read = Read(path);
            if (read is null) return false;
            if (read.Port != 47811 || read.Token != "tok" || read.InstanceId != "inst") return false;

            // We describe ourselves, so we are not stale.
            if (IsStale(read)) return false;

            // A pid that cannot exist is stale.
            if (!IsStale(new RuntimeInfo { Pid = 0 })) return false;
            if (!IsStale(null)) return false;

            // A live pid with the wrong start time is PID reuse, not our daemon.
            var reused = Read(path)!;
            reused.ProcessStartTimeUtc = DateTimeOffset.UtcNow.AddDays(-3).ToString("o");
            if (!IsStale(reused)) return false;

            // A missing or corrupt file reads as null rather than throwing.
            if (Read(Path.Combine(dir, "nope.json")) is not null) return false;
            File.WriteAllText(path, "{ broken");
            if (Read(path) is not null) return false;

            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
