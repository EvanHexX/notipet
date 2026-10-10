using System.Reflection;

namespace Notipet.Shared;

// The version the build was stamped with: <Version> in the csproj, or
// -p:Version= on the command line (scripts/pack.ps1 does that for test
// releases). The "+commit" suffix the SDK appends is dropped.
public static class BuildVersion
{
    public static string Of(Assembly assembly)
    {
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(informational)) return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        var plus = informational.IndexOf('+');
        return plus < 0 ? informational : informational[..plus];
    }
}
