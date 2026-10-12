using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace Notipet;

// Copies of runtime.json that a packaged (MSIX) app keeps for itself.
//
// Windows redirects a packaged desktop app's new files under %LOCALAPPDATA% -
// and those of every process it starts, hooks included - into
// %LOCALAPPDATA%\Packages\<family>\LocalCache\Local, and inside that app the
// copy hides the real file. The Claude desktop app is one. A daemon once
// started by a Claude Code hook wrote its runtime.json there; long after it
// was gone, every Claude Code hook read that copy, took notipet for down,
// started it again (`--background`, which found it running and quit), and the
// notification was lost. Before 1.6.3 that start opened Recent instead.
//
// The daemon, running outside any package, deletes those copies: without one
// the app reads the real file. A daemon that itself runs inside a package
// cannot - its own files are the copies - so it only says so (daemon.log,
// `notipet doctor`).
internal static class PackageShadows
{
    public static string PackagesDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");

    // runtime*.json under Packages\*\LocalCache\Local\notipet. Missing folders
    // and unreadable ones are skipped.
    public static List<string> Find(string packagesDir)
    {
        var found = new List<string>();
        try
        {
            if (!Directory.Exists(packagesDir)) return found;
            foreach (var package in Directory.EnumerateDirectories(packagesDir))
            {
                var data = Path.Combine(package, "LocalCache", "Local", Paths.AppFolderName);
                try
                {
                    if (!Directory.Exists(data)) continue;
                    found.AddRange(Directory.GetFiles(data, "runtime*.json"));
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
        return found;
    }

    // A copy that names this very process: then this process is the one being
    // redirected - its own runtime.json landed in a package's LocalCache, and
    // nothing outside that package can find it. Package identity is no test:
    // a daemon started (or restarted) by a process inside the Claude app was
    // redirected with GetCurrentPackageFullName reporting no package, and for
    // hours deleted and rewrote its own file every 30 s while Codex saw a dead
    // pid.
    public static string? FindOwn(string packagesDir, int pid)
    {
        foreach (var path in Find(packagesDir))
        {
            if (NamesPid(path, pid)) return path;
        }
        return null;
    }

    private static bool NamesPid(string path, int pid) =>
        Http.RuntimeFile.Read(path) is { } info && info.Pid == pid;

    // Deletes them, except one naming ownPid (FindOwn); returns the ones it
    // deleted.
    public static List<string> Remove(string packagesDir, int ownPid = 0)
    {
        var removed = new List<string>();
        foreach (var path in Find(packagesDir))
        {
            try
            {
                if (ownPid > 0 && NamesPid(path, ownPid)) continue;
                File.Delete(path);
                removed.Add(path);
            }
            catch
            {
            }
        }
        return removed;
    }

    // The package this process runs inside, or null. Its files under
    // %LOCALAPPDATA% are then the copies other processes cannot see.
    public static string? CurrentPackage()
    {
        try
        {
            uint length = 0;
            if (GetCurrentPackageFullName(ref length, null) != ErrorInsufficientBuffer || length == 0) return null;
            var buffer = new char[length];
            return GetCurrentPackageFullName(ref length, buffer) == 0 ? new string(buffer, 0, (int)length - 1) : null;
        }
        catch
        {
            return null;
        }
    }

    // "Claude_2.31226.1.0_x64__pzs8sxrjxfjjc" -> "Claude"; the folder name -> itself.
    public static string Describe(string path)
    {
        var relative = Path.GetRelativePath(PackagesDir, path);
        var family = relative.Split(Path.DirectorySeparatorChar)[0];
        var underscore = family.IndexOf('_');
        return underscore > 0 ? family[..underscore] : family;
    }

    private const int ErrorInsufficientBuffer = 122;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, [Out] char[]? packageFullName);

    public static bool RunSelfTest()
    {
        var root = Path.Combine(Path.GetTempPath(), "notipet-packages-" + Guid.NewGuid().ToString("N"));
        try
        {
            var shadow = Path.Combine(root, "Claude_pzs8sxrjxfjjc", "LocalCache", "Local", Paths.AppFolderName);
            var other = Path.Combine(root, "Other_abc", "LocalCache", "Local", "other");
            Directory.CreateDirectory(shadow);
            Directory.CreateDirectory(other);
            File.WriteAllText(Path.Combine(shadow, "runtime.json"), "{}");
            File.WriteAllText(Path.Combine(shadow, "runtime-1.json"), "{}");
            File.WriteAllText(Path.Combine(shadow, "crash.log"), "kept");
            File.WriteAllText(Path.Combine(other, "runtime.json"), "{}");

            if (Find(root).Count != 2) return false;
            if (Remove(root).Count != 2 || Find(root).Count != 0) return false;
            // Only notipet's runtime files go.
            if (!File.Exists(Path.Combine(shadow, "crash.log")) || !File.Exists(Path.Combine(other, "runtime.json"))) return false;
            if (Find(Path.Combine(root, "missing")).Count != 0) return false;

            // A copy naming this process means this process is redirected: it
            // is found, and kept; another pid's copy still goes.
            File.WriteAllText(Path.Combine(shadow, "runtime.json"), "{\"pid\": " + Environment.ProcessId + "}");
            File.WriteAllText(Path.Combine(shadow, "runtime-1.json"), "{\"pid\": 1}");
            if (FindOwn(root, Environment.ProcessId) is null || FindOwn(root, 2) is not null) return false;
            if (Remove(root, Environment.ProcessId).Count != 1 || !File.Exists(Path.Combine(shadow, "runtime.json"))) return false;
            // The self-test runs unpackaged.
            return CurrentPackage() is null;
        }
        catch
        {
            return false;
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
