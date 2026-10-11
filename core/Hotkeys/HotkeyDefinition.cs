using System.Collections.Generic;
using System.Text;

namespace Notipet.Hotkeys;

// A global hotkey as text and back: "Ctrl+Shift+Win+N" <-> (MOD_CONTROL |
// MOD_SHIFT | MOD_WIN, 'N'). Parsing ignores case and order; Format always
// writes Ctrl+Alt+Shift+Win+Key. Copied from quota-scope (app/Hotkeys), not
// linked - see docs/PROJECT_MAP.md.
internal sealed record HotkeyDefinition(uint Modifiers, uint VirtualKey)
{
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;

    // Opens Recent notifications (settings.history.hotkey). Ctrl+Alt+N was
    // the first idea, but JetBrains IDEs (Inline) and VS Code's Code Runner
    // use it, and Ctrl+Alt is AltGr on many European layouts (Polish AltGr+N
    // types ń) - a global hotkey would take those away. Win+Shift+N and
    // Win+Alt+N belong to OneNote, Win+N to the notification centre.
    public const string DefaultRecent = "Ctrl+Shift+Win+N";

    public static bool TryParse(string? text, out HotkeyDefinition definition)
    {
        definition = null!;
        if (string.IsNullOrWhiteSpace(text)) return false;

        uint modifiers = 0;
        uint virtualKey = 0;
        var hasKey = false;

        foreach (var raw in text.Split('+'))
        {
            var token = raw.Trim().ToUpperInvariant();
            if (token.Length == 0) return false;
            switch (token)
            {
                case "CTRL":
                case "CONTROL":
                    modifiers |= ModControl;
                    break;
                case "ALT":
                    modifiers |= ModAlt;
                    break;
                case "SHIFT":
                    modifiers |= ModShift;
                    break;
                case "WIN":
                case "WINDOWS":
                    modifiers |= ModWin;
                    break;
                default:
                    if (hasKey || !TryParseKey(token, out virtualKey)) return false;
                    hasKey = true;
                    break;
            }
        }

        // A global hotkey without a modifier would swallow normal typing, and
        // Shift alone is just a capital letter.
        if (!hasKey || (modifiers & ~ModShift) == 0) return false;
        definition = new HotkeyDefinition(modifiers, virtualKey);
        return true;
    }

    public string Format()
    {
        var builder = new StringBuilder();
        if ((Modifiers & ModControl) != 0) builder.Append("Ctrl+");
        if ((Modifiers & ModAlt) != 0) builder.Append("Alt+");
        if ((Modifiers & ModShift) != 0) builder.Append("Shift+");
        if ((Modifiers & ModWin) != 0) builder.Append("Win+");
        builder.Append(KeyName(VirtualKey));
        return builder.ToString();
    }

    public static bool IsSupportedKey(uint virtualKey) => KeyName(virtualKey).Length > 0;

    // Whether a captured combination can be a global hotkey at all.
    public bool IsUsable => IsSupportedKey(VirtualKey) && (Modifiers & ~ModShift) != 0;

    private static bool TryParseKey(string token, out uint virtualKey)
    {
        virtualKey = 0;
        if (token.Length == 1)
        {
            var c = token[0];
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                virtualKey = c;
                return true;
            }
            return false;
        }

        if (token.Length is 2 or 3 && token[0] == 'F' && int.TryParse(token[1..], out var f) && f is >= 1 and <= 24)
        {
            virtualKey = (uint)(0x70 + f - 1);
            return true;
        }

        if (NamedKeys.TryGetValue(token, out var vk))
        {
            virtualKey = vk;
            return true;
        }
        return false;
    }

    private static string KeyName(uint virtualKey)
    {
        if (virtualKey is >= 'A' and <= 'Z' or >= '0' and <= '9')
        {
            return ((char)virtualKey).ToString();
        }
        if (virtualKey is >= 0x70 and <= 0x87)
        {
            return $"F{virtualKey - 0x70 + 1}";
        }
        foreach (var pair in NamedKeys)
        {
            if (pair.Value == virtualKey) return Canonical(pair.Key);
        }
        return "";
    }

    private static string Canonical(string upper) => upper switch
    {
        "PAGEUP" => "PageUp",
        "PAGEDOWN" => "PageDown",
        _ => upper[0] + upper[1..].ToLowerInvariant()
    };

    // First of each value wins in KeyName, so the canonical name comes first.
    private static readonly Dictionary<string, uint> NamedKeys = new()
    {
        ["SPACE"] = 0x20,
        ["TAB"] = 0x09,
        ["ENTER"] = 0x0D,
        ["RETURN"] = 0x0D,
        ["PAGEUP"] = 0x21,
        ["PGUP"] = 0x21,
        ["PAGEDOWN"] = 0x22,
        ["PGDN"] = 0x22,
        ["END"] = 0x23,
        ["HOME"] = 0x24,
        ["LEFT"] = 0x25,
        ["UP"] = 0x26,
        ["RIGHT"] = 0x27,
        ["DOWN"] = 0x28,
        ["INSERT"] = 0x2D,
        ["DELETE"] = 0x2E,
        ["DEL"] = 0x2E
    };

    public static bool RunSelfTest()
    {
        // The default round-trips in canonical order.
        if (!TryParse(DefaultRecent, out var recent)
            || recent.Modifiers != (ModControl | ModShift | ModWin)
            || recent.VirtualKey != 'N'
            || recent.Format() != DefaultRecent)
        {
            return false;
        }

        if (!TryParse("win + shift + ctrl + n", out var shuffled) || shuffled != recent) return false;
        if (!TryParse("WIN+SPACE", out var named) || named.Format() != "Win+Space") return false;
        if (!TryParse("alt+f12", out var fn) || fn.VirtualKey != 0x7B || fn.Format() != "Alt+F12") return false;
        if (!TryParse("Ctrl+PgDn", out var paging) || paging.Format() != "Ctrl+PageDown") return false;

        // Empty, modifier only, no modifier, Shift only, unknown key, two keys.
        foreach (var invalid in new[] { null, "", "  ", "Ctrl+", "Ctrl", "N", "F5", "Shift+N", "Ctrl+Foo", "Ctrl+A+B", "Ctrl++N" })
        {
            if (TryParse(invalid, out _)) return false;
        }
        if (new HotkeyDefinition(ModShift, 'N').IsUsable || !recent.IsUsable) return false;

        return TryParse(fn.Format(), out var reparsed) && reparsed == fn;
    }
}
