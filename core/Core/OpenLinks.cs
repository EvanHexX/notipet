using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Notipet.Core;

// What a notification may open when its card is clicked (`send --open`).
//
// A click on a notipet card is a click the user trusts, so what it can launch
// is narrowed to things that cannot reach further than the sender already
// could: an http(s) page on this PC, or a URI scheme the user listed in
// settings.json (links.allowedSchemes) - typically their own tool's, such as
// codexbridge://. Never a command line: the value is handed to the shell as a
// URI and nothing else. Some schemes are refused even when listed, because
// they read files, change settings or run code, and their handlers have a
// history of being abused through exactly this kind of link.
internal static class OpenLinks
{
    public const int MaxLength = 2048;

    private static readonly Regex SchemeName = new("^[a-z][a-z0-9+.-]{0,31}$", RegexOptions.CultureInvariant);

    private static readonly HashSet<string> Denied = new(StringComparer.Ordinal)
    {
        "file", "javascript", "vbscript", "data", "shell", "search", "search-ms",
        "res", "mk", "its", "hcp", "jar", "about", "view-source"
    };

    // A scheme that may never be opened, listed or not. Every ms-* handler is
    // Windows' own (ms-settings, ms-msdt, ms-appinstaller, ms-officecmd ...).
    public static bool IsDenied(string scheme) =>
        Denied.Contains(scheme) || scheme.StartsWith("ms-", StringComparison.Ordinal);

    // A well-formed scheme name that a user may list. http/https are not
    // listed - they are allowed for this PC only, and listing them would mean
    // the whole internet.
    public static bool IsListable(string? scheme) =>
        scheme is not null && SchemeName.IsMatch(scheme) && !IsDenied(scheme) && scheme is not ("http" or "https");

    // The link as sent if it may be opened, else null with the reason added to
    // warnings. Never throws: a bad link costs the card its link, not the
    // notification.
    public static string? Accept(string? raw, IEnumerable<string>? allowedSchemes, List<string>? warnings)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var text = raw.Trim();
        var reason = Check(text, allowedSchemes);
        if (reason is null) return text;
        warnings?.Add("open ignored: " + reason);
        return null;
    }

    // Checked again at click time, against the settings of that moment: a
    // scheme taken off the list stops working on cards that already exist.
    public static bool IsAllowed(string? text, IEnumerable<string>? allowedSchemes) =>
        !string.IsNullOrWhiteSpace(text) && Check(text.Trim(), allowedSchemes) is null;

    internal static string? Check(string text, IEnumerable<string>? allowedSchemes)
    {
        if (text.Length > MaxLength) return $"longer than {MaxLength} characters";
        if (text.Any(c => c < 0x20 || c == 0x7f)) return "control characters";
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return "not an absolute URI";

        var scheme = uri.Scheme.ToLowerInvariant();
        if (IsDenied(scheme)) return $"scheme '{scheme}' is never opened";
        if (scheme is "http" or "https")
        {
            return uri.IsLoopback ? null : "http(s) only to this PC (127.0.0.1, localhost)";
        }
        var allowed = allowedSchemes?.Any(s => string.Equals(s, scheme, StringComparison.OrdinalIgnoreCase)) == true;
        return allowed ? null : $"scheme '{scheme}' is not in links.allowedSchemes";
    }

    public static bool RunSelfTest()
    {
        var listed = new[] { "codexbridge" };
        var warnings = new List<string>();

        // A listed custom scheme and a page on this PC open; the text is kept as sent.
        if (Accept(" codexbridge://approve/d-42 ", listed, warnings) != "codexbridge://approve/d-42") return false;
        if (Accept("http://127.0.0.1:5123/x", null, warnings) is null) return false;
        if (Accept("https://localhost/x", null, warnings) is null) return false;
        if (warnings.Count != 0) return false;

        // Everything else is dropped with a reason, never thrown.
        string?[] refused =
        {
            "codexbridge://x",                 // not listed (checked with null below)
            "https://example.com/",            // the internet
            "file:///C:/Windows/System32/calc.exe",
            @"C:\Windows\System32\calc.exe",   // a path is a file: URI
            @"\\server\share\x.exe",
            "ms-settings:privacy",
            "ms-msdt:/id PCWDiagnostic",
            "search-ms:query=x",
            "javascript:alert(1)",
            "calc.exe",                        // not a URI at all
            "codexbridge://a\nb",
            "codexbridge://" + new string('a', MaxLength)
        };
        foreach (var link in refused)
        {
            var before = warnings.Count;
            if (Accept(link, link == "codexbridge://x" ? null : listed, warnings) is not null) return false;
            if (warnings.Count != before + 1) return false;
        }

        // Listing cannot open the denied ones, or the internet.
        var greedy = new[] { "file", "ms-settings", "https", "codexbridge" };
        if (Accept("file:///C:/x", greedy, null) is not null) return false;
        if (Accept("https://example.com", greedy, null) is not null) return false;
        if (IsListable("ms-settings") || IsListable("https") || IsListable("Bad Scheme") || !IsListable("codexbridge")) return false;

        // Taking a scheme off the list closes existing cards' links too.
        if (!IsAllowed("codexbridge://x", listed) || IsAllowed("codexbridge://x", Array.Empty<string>())) return false;
        return true;
    }
}
