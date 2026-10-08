using System;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Notipet;

// Whether a URL scheme (codex://, claude://) has a handler on this machine.
// A thread link is only offered when something will answer it: without a
// registered handler, Windows shows "get an app to open this link".
[SupportedOSPlatform("windows")]
internal static class UrlSchemes
{
    public static bool IsRegistered(string scheme)
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey(scheme);
            return key?.GetValue("URL Protocol") is not null;
        }
        catch
        {
            return false;
        }
    }
}
