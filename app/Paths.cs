using System;
using System.IO;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Notipet;

// Every file notipet owns lives under %LOCALAPPDATA%\notipet, with one
// exception: if a settings.json sits next to the exe we treat that as portable
// mode and prefer it. quota-scope stores settings next to the exe, but that is
// a compatibility lock-in on a shipped app rather than a principle, and it
// breaks the moment the app is installed under Program Files.
internal static class Paths
{
    public const string AppFolderName = "notipet";

    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppFolderName);

    public static string PortableSettingsPath =>
        Path.Combine(AppContext.BaseDirectory, "settings.json");

    public static string SettingsPath =>
        File.Exists(PortableSettingsPath) ? PortableSettingsPath : Path.Combine(DataDir, "settings.json");

    public static string CrashLogPath => Path.Combine(DataDir, "crash.log");

    public static string SoundsDir => Path.Combine(DataDir, "sounds");

    public static string RuntimePath => Path.Combine(DataDir, "runtime.json");

    // Where the daemon exe was last started from; kept after exit.
    public static string DaemonPathFile => Path.Combine(DataDir, "daemon.path");

    // Two logon sessions (console + RDP, or fast user switching) each run their
    // own daemon because the single-instance mutex is Local\-scoped, but they
    // share %LOCALAPPDATA%. Writing a session-scoped file alongside the plain
    // one lets a CLI in either session find its own daemon.
    public static string RuntimePathForSession(int sessionId) =>
        Path.Combine(DataDir, $"runtime-{sessionId}.json");

    // Creates the data directory with a DACL granting only the current user.
    // runtime.json holds the API token in plaintext - inherent to same-user
    // loopback IPC - so at minimum keep other accounts on the box out of it.
    [SupportedOSPlatform("windows")]
    public static void EnsureDataDir()
    {
        if (Directory.Exists(DataDir)) return;

        try
        {
            var security = new DirectorySecurity();
            var user = WindowsIdentity.GetCurrent().User;
            if (user is not null)
            {
                security.SetOwner(user);
                security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                security.AddAccessRule(new FileSystemAccessRule(
                    user,
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));
                security.CreateDirectory(DataDir);
                return;
            }
        }
        catch
        {
            // Fall through: an inherited-permission directory still beats no
            // directory at all, and the token is not the last line of defence.
        }

        Directory.CreateDirectory(DataDir);
    }
}
