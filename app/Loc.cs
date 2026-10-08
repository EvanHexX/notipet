using System;
using System.Globalization;

namespace Notipet;

// Minimal two-language string helper, carried over from quota-scope. Call sites
// pass both languages inline; "System" resolves from the current UI culture.
internal static class Loc
{
    public static bool IsKorean { get; private set; }

    public static void SetLanguage(string? setting)
    {
        IsKorean = setting?.ToUpperInvariant() switch
        {
            "KO" or "KOREAN" => true,
            "EN" or "ENGLISH" => false,
            _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("ko", StringComparison.OrdinalIgnoreCase)
        };
    }

    public static string T(string en, string ko) => IsKorean ? ko : en;

    public static bool RunSelfTest()
    {
        var original = IsKorean;
        try
        {
            SetLanguage("KO");
            if (!IsKorean || T("Mute", "\uc74c\uc18c\uac70") != "\uc74c\uc18c\uac70") return false;
            SetLanguage("en");
            if (IsKorean || T("Mute", "\uc74c\uc18c\uac70") != "Mute") return false;
            SetLanguage("System");
            return true;
        }
        finally
        {
            IsKorean = original;
        }
    }
}
