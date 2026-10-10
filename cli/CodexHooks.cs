using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Notipet.Cli;

// notipet's Codex hooks, written by `notipet install-hooks --codex --write`
// into %USERPROFILE%\.codex\config.toml (or $CODEX_HOME\config.toml) as one
// managed block of [[hooks.*]] tables. No TOML parser: the block is taken out
// and put back whole, in the same place, and the command refuses files whose
// hooks are written in a form a plain append could break.
//
// Why config.toml and not the user hooks.json next to it: on Codex
// 0.162.0-alpha.17.2, `codex exec` ran config.toml hooks but not hooks.json ones,
// although `hooks/list` showed both (docs/modules/codex_plugin.md). An earlier
// 1.5.1 build wrote hooks.json; --write takes those entries out again.
//
// What Codex does with these (the module doc has the sources):
// - `command` is a whole command line, run through cmd.exe /C. There is no
//   `args` field; Codex drops one silently.
// - Every hook is trusted by a key (file, event, group index, handler index)
//   and a hash of its definition. So the block goes back where it was, and its
//   text (command, timeout, status message) changes only when it is worth asking
//   every user to trust the hooks again.
// - Codex runs the hooks of every source, so a second copy rings twice.
internal static class CodexHooks
{
    private sealed record Hook(string Event, bool Async, string Korean, string English);

    // Stop also ends this thread's approval alarm; UserPromptSubmit only ends
    // what is over (PayloadMapper.PlanForHookEvent) and never makes a prompt wait.
    private static readonly Hook[] Hooks =
    {
        new("Stop", false, "Notipet · 턴 완료 알림 및 권한 알람 정리", "Notipet · turn finished; ending its approval alarm"),
        new("PermissionRequest", false, "Notipet · 권한 승인 대기 알림", "Notipet · waiting for approval"),
        new("UserPromptSubmit", true, "Notipet · 새 입력 시 이전 완료 알림 정리", "Notipet · new prompt; ending the last turn's alerts"),
    };

    private const int TimeoutSeconds = 10;
    private const string Marker = "# notipet hooks - written by `notipet install-hooks --codex --write`, which --remove undoes. Leave `notify` alone.";

    public static string CodexHome() =>
        Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } home
            ? home
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

    public static string CommandLine(string exePath) => $"\"{exePath}\" --source codex";

    // A handler is notipet's when it runs a notipet.exe (any path, any
    // arguments) or the script of the 1.5.1 development plugin.
    private static bool IsOurs(string? command) =>
        command is not null
        && (command.Contains("notipet.exe", StringComparison.OrdinalIgnoreCase)
            || command.Contains("notipet-hook.cmd", StringComparison.OrdinalIgnoreCase));

    // ----- the block -----

    public static IReadOnlyList<string> Block(string exePath, bool korean)
    {
        var lines = new List<string> { Marker };
        foreach (var hook in Hooks)
        {
            if (lines.Count > 1) lines.Add("");
            lines.Add($"[[hooks.{hook.Event}]]");
            lines.Add($"[[hooks.{hook.Event}.hooks]]");
            lines.Add("type = \"command\"");
            lines.Add($"command = {TomlString(CommandLine(exePath))}");
            lines.Add($"timeout = {TimeoutSeconds}");
            if (hook.Async) lines.Add("async = true");
            lines.Add($"statusMessage = {TomlString(korean ? hook.Korean : hook.English)}");
        }
        return lines;
    }

    // A literal string ('...') unless the text has a quote or newline in it.
    private static string TomlString(string text) =>
        text.Contains('\'', StringComparison.Ordinal) || text.Contains('\n', StringComparison.Ordinal)
            ? "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal) + "\""
            : "'" + text + "'";

    // ----- config.toml -----

    // Takes out every [[hooks.X]] block whose commands all run notipet.exe,
    // with a "# notipet ..." comment right above it and one blank line after.
    // Anything else - other hooks, `notify`, a block that mixes notipet with
    // another command - is left alone; MentionsNotipetHook then still finds
    // it. `at` is the line where the first removed block started, or -1.
    public static List<string> StripFromConfigToml(IReadOnlyList<string> source, out int removed, out int at) =>
        StripFromConfigToml(source, out removed, out at, out _);

    // blankAfter: whether the last block taken out had a blank line after it.
    private static List<string> StripFromConfigToml(IReadOnlyList<string> source, out int removed, out int at, out bool blankAfter)
    {
        removed = 0;
        at = -1;
        blankAfter = false;
        var lines = source.ToList();
        var i = 0;
        while (i < lines.Count)
        {
            var eventName = ArrayHeaderEvent(lines[i]);
            if (eventName is null || eventName.Contains('.', StringComparison.Ordinal))
            {
                i++;
                continue;
            }

            // The block runs to the next header that is not its own handler list.
            var end = i + 1;
            while (end < lines.Count)
            {
                if (lines[end].TrimStart().StartsWith('[') && ArrayHeaderEvent(lines[end]) != eventName + ".hooks") break;
                end++;
            }
            // Trailing blank lines and comments belong to whatever follows.
            while (end > i + 1 && (lines[end - 1].Trim().Length == 0 || lines[end - 1].TrimStart().StartsWith('#'))) end--;

            var commands = lines.Skip(i).Take(end - i).Where(l => l.TrimStart().StartsWith("command", StringComparison.Ordinal)).ToList();
            if (commands.Count == 0 || !commands.All(IsOurs))
            {
                i = end;
                continue;
            }

            var start = i;
            if (start > 0 && lines[start - 1].TrimStart().StartsWith("# notipet", StringComparison.OrdinalIgnoreCase)) start--;
            blankAfter = end < lines.Count && lines[end].Trim().Length == 0;
            if (blankAfter) end++;
            lines.RemoveRange(start, end - start);
            if (at < 0) at = start;
            removed++;
            i = start;
        }
        return lines;
    }

    // "[[hooks.Stop]]" -> "Stop", "[[hooks.Stop.hooks]]" -> "Stop.hooks".
    private static string? ArrayHeaderEvent(string line)
    {
        var t = line.Trim();
        var comment = t.IndexOf('#', StringComparison.Ordinal);
        if (comment > 0) t = t[..comment].TrimEnd();
        if (!t.StartsWith("[[", StringComparison.Ordinal) || !t.EndsWith("]]", StringComparison.Ordinal)) return null;
        var name = t[2..^2].Trim();
        return name.StartsWith("hooks.", StringComparison.Ordinal) ? name["hooks.".Length..] : null;
    }

    // A line under [hooks...] that still runs notipet.exe.
    public static bool MentionsNotipetHook(string toml)
    {
        var inHooks = false;
        foreach (var raw in SplitLines(toml))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                var name = line.Trim('[', ']', ' ');
                inHooks = name == "hooks" || name.StartsWith("hooks.", StringComparison.Ordinal);
                continue;
            }
            if (inHooks && IsOurs(line)) return true;
        }
        return false;
    }

    // Hooks written as values (`Stop = [...]` under [hooks], `hooks.Stop = ...`,
    // `hooks = {...}`) rather than [[hooks.X]] tables. Appending tables for the
    // same event would then be a TOML error that stops Codex from starting, so
    // --write refuses instead.
    public static string? InlineHooks(string toml)
    {
        string? section = null;
        foreach (var raw in SplitLines(toml))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith('['))
            {
                section = line.Trim('[', ']', ' ');
                continue;
            }
            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0) continue;
            var key = line[..eq].Trim().Replace("\"", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal);
            var full = section is null ? key : section + "." + key;
            if (full == "hooks") return line;
            foreach (var hook in Hooks)
            {
                if (full == "hooks." + hook.Event) return line;
            }
        }
        return null;
    }

    private static string[] SplitLines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

    // The file with notipet's block in place (or, with remove, without it).
    // Null when the file's own hooks make an edit unsafe; `problem` says why.
    public static string? Apply(string? toml, string exePath, bool korean, bool remove, out string? problem)
    {
        problem = null;
        toml ??= "";
        var newline = toml.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : Environment.NewLine;
        var lines = StripFromConfigToml(SplitLines(toml), out _, out var at, out var blankAfter);
        var stripped = string.Join("\n", lines);
        if (MentionsNotipetHook(stripped))
        {
            problem = "a hook there runs notipet.exe in a form this command does not take out safely - remove it by hand first, or Codex runs it twice";
            return null;
        }
        if (!remove && InlineHooks(stripped) is { } inline)
        {
            problem = $"its hooks are written as values (`{inline}`), so adding [[hooks.*]] tables could break the file - add notipet's by hand (`notipet install-hooks --codex` prints them)";
            return null;
        }

        if (!remove)
        {
            var block = Block(exePath, korean).ToList();
            if (at < 0)
            {
                // At the end, after one blank line; the file ends with a newline.
                while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
                if (lines.Count > 0) lines.Add("");
                lines.AddRange(block);
                lines.Add("");
            }
            else
            {
                // Where it was, so the trust keys (indexes) stay the same.
                if (at < lines.Count && blankAfter) block.Add("");
                else if (lines.Count > 0 && lines[^1].Trim().Length > 0) lines.Add("");
                lines.InsertRange(at, block);
                if (lines[^1].Length > 0) lines.Add("");
            }
        }
        return string.Join(newline, lines);
    }

    // ----- hooks.json (an earlier 1.5.1 build wrote there) -----

    private static string? Str(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;

    // hooks.json without notipet's handlers, or null when it had none or is not
    // JSON that can be edited.
    public static JsonObject? WithoutOurs(string json)
    {
        JsonObject? root;
        try { root = JsonNode.Parse(json) as JsonObject; }
        catch (JsonException) { return null; }
        if (root?["hooks"] is not JsonObject events) return null;
        var changed = false;
        foreach (var name in events.Select(e => e.Key).ToList())
        {
            if (events[name] is not JsonArray groups) continue;
            for (var i = groups.Count - 1; i >= 0; i--)
            {
                if (groups[i]?["hooks"] is not JsonArray handlers) continue;
                for (var j = handlers.Count - 1; j >= 0; j--)
                {
                    if (IsOurs(Str(handlers[j]?["command"])) || IsOurs(Str(handlers[j]?["commandWindows"])))
                    {
                        handlers.RemoveAt(j);
                        changed = true;
                    }
                }
                if (handlers.Count == 0) groups.RemoveAt(i);
            }
            if (groups.Count == 0) events.Remove(name);
        }
        return changed ? root : null;
    }

    public static bool HooksJsonHasOurs(string? json) => json is not null && WithoutOurs(json) is not null;

    private static string Serialize(JsonNode node)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            node.WriteTo(writer);
        }
        return Encoding.UTF8.GetString(stream.ToArray()) + Environment.NewLine;
    }

    // ----- install-hooks --codex --write / --remove -----

    public static int Install(string codexHome, string exePath, bool remove, bool korean, TextWriter output)
    {
        var configPath = Path.Combine(codexHome, "config.toml");
        var hooksJsonPath = Path.Combine(codexHome, "hooks.json");
        var stamp = DateTime.Now.ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture);

        var existing = File.Exists(configPath) ? File.ReadAllText(configPath) : null;
        var updated = Apply(existing, exePath, korean, remove, out var problem);
        if (updated is null)
        {
            output.WriteLine($"notipet: left {configPath} as it is: {problem}.");
            return 1;
        }
        if (updated == (existing ?? ""))
        {
            output.WriteLine(remove ? $"no notipet hooks in {configPath}" : $"already up to date: {configPath}");
        }
        else
        {
            Directory.CreateDirectory(codexHome);
            if (existing is not null) File.Copy(configPath, configPath + ".bak-notipet-" + stamp, overwrite: true);
            File.WriteAllText(configPath, updated, new UTF8Encoding(false));
            output.WriteLine($"{(remove ? "took notipet's hooks out of" : "wrote notipet's hooks to")} {configPath}{(existing is not null ? " (backup next to it)" : "")}");
        }

        // An earlier 1.5.1 build put them in hooks.json; Codex runs every
        // source, so that copy goes.
        if (File.Exists(hooksJsonPath) && WithoutOurs(File.ReadAllText(hooksJsonPath)) is { } cleaned)
        {
            File.Copy(hooksJsonPath, hooksJsonPath + ".bak-notipet-" + stamp, overwrite: true);
            if (cleaned["hooks"] is JsonObject { Count: 0 } && cleaned.Count == 1) File.Delete(hooksJsonPath);
            else File.WriteAllText(hooksJsonPath, Serialize(cleaned), new UTF8Encoding(false));
            output.WriteLine($"took notipet's hooks out of {hooksJsonPath} (backup next to it)");
        }

        if (!remove)
        {
            output.WriteLine();
            output.WriteLine("New Codex threads pick these up. If Codex asks you to review new hooks (`/hooks`), trust these.");
            output.WriteLine($"They run: {CommandLine(exePath)}");
        }
        return 0;
    }

    // Status messages in the user's Windows display language: Korean or English.
    public static bool PrefersKorean()
    {
        try
        {
            return OperatingSystem.IsWindows() && (GetUserDefaultUILanguage() & 0x3FF) == 0x12;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("kernel32.dll")]
    private static extern ushort GetUserDefaultUILanguage();

    // ----- self-test -----

    public static bool RunSelfTest()
    {
        const string exe = @"C:\x\notipet.exe";
        var user = string.Join("\r\n",
            "notify = [\"computer-use.exe\", \"turn-ended\"]",
            "",
            "[[skills.config]]",
            "enabled = false",
            "",
            "[[hooks.PreToolUse]]",
            "matcher = \"Bash\"",
            "[[hooks.PreToolUse.hooks]]",
            "type = \"command\"",
            "command = 'policy.exe'",
            "");

        // Added at the end: three events, the argument inside the command line
        // (Codex has no `args`), CRLF kept; then a second run changes nothing.
        var once = Apply(user, exe, korean: false, remove: false, out _)!;
        if (!once.StartsWith(user.TrimEnd(), StringComparison.Ordinal) || !once.EndsWith("\r\n", StringComparison.Ordinal)) return false;
        if (!once.Contains("command = '\"C:\\x\\notipet.exe\" --source codex'", StringComparison.Ordinal) || once.Contains("args", StringComparison.Ordinal)) return false;
        foreach (var hook in Hooks)
        {
            if (!once.Contains($"[[hooks.{hook.Event}]]\r\n[[hooks.{hook.Event}.hooks]]", StringComparison.Ordinal)) return false;
        }
        if (Apply(once, exe, false, false, out _) != once) return false;
        if (!Apply(user, exe, korean: true, false, out _)!.Contains("권한 승인 대기", StringComparison.Ordinal)) return false;

        // Something the user added after it stays after it, and the block is
        // replaced where it stands (its trust keys do not move).
        var later = once + "[tui]\r\nnotifications = true\r\n";
        if (Apply(later, exe, false, false, out _) != later) return false;
        var moved = Apply(later.Replace("\"C:\\x\\notipet.exe\"", "\"C:\\old\\notipet.exe\"", StringComparison.Ordinal), exe, false, false, out _);
        if (moved != later) return false;

        // Remove gives back the user's file.
        var removed = Apply(later, exe, false, remove: true, out _)!;
        if (removed.TrimEnd() != (user.TrimEnd() + "\r\n\r\n[tui]\r\nnotifications = true").TrimEnd() || MentionsNotipetHook(removed)) return false;

        // What older versions wrote (a comment above, `args` Codex ignored,
        // status messages a user added) is taken out and replaced.
        var legacy = user + "\r\n# notipet (added by notipet install-hooks). Leave `notify` above alone.\r\n" +
                     "[[hooks.Stop]]\r\n[[hooks.Stop.hooks]]\r\nstatusMessage = \"Notipet\"\r\ntype = \"command\"\r\ncommand = 'C:\\x\\notipet.exe'\r\nargs = [\"--source\", \"codex\"]\r\ntimeout = 10\r\n";
        var upgraded = Apply(legacy, exe, false, false, out _)!;
        if (upgraded.Contains("args", StringComparison.Ordinal) || upgraded.Contains("added by notipet", StringComparison.Ordinal) || upgraded.Split("[[hooks.Stop]]").Length != 2) return false;
        if (!upgraded.Contains("policy.exe", StringComparison.Ordinal) || !upgraded.Contains("notify = [", StringComparison.Ordinal)) return false;

        // Refused: a block mixing notipet with another command, and hooks
        // written as values.
        if (Apply("[[hooks.Stop]]\n[[hooks.Stop.hooks]]\ncommand = 'C:\\x\\notipet.exe'\n[[hooks.Stop.hooks]]\ncommand = 'other.exe'\n", exe, false, false, out var p1) is not null || p1 is null) return false;
        if (Apply("[hooks]\nStop = []\n", exe, false, false, out var p2) is not null || p2 is null) return false;
        if (Apply("hooks.PermissionRequest = []\n", exe, false, false, out _) is not null) return false;
        if (Apply("[hooks.state.\"k\"]\ntrusted_hash = \"x\"\n", exe, false, false, out _) is null) return false;   // not a hook definition

        // An empty or missing file gets just the block.
        if (Apply(null, exe, false, false, out _) is not { } fresh || !fresh.StartsWith(Marker, StringComparison.Ordinal)) return false;

        // hooks.json from the earlier build: only notipet's entries go.
        var json = """{ "hooks": { "Stop": [ { "hooks": [ { "type": "command", "command": "\"C:\\x\\notipet.exe\" --source codex" } ] }, { "hooks": [ { "type": "command", "command": "other.exe" } ] } ] } }""";
        if (WithoutOurs(json) is not { } clean || HooksJsonHasOurs(Serialize(clean)) || clean["hooks"]?["Stop"] is not JsonArray { Count: 1 }) return false;
        if (WithoutOurs("""{ "hooks": { "Stop": [ { "hooks": [ { "type": "command", "command": "other.exe" } ] } ] } }""") is not null) return false;

        // End to end in a scratch CODEX_HOME: write, write again, remove.
        var home = Path.Combine(Path.GetTempPath(), "notipet-codex-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(home);
            var config = Path.Combine(home, "config.toml");
            File.WriteAllText(config, legacy);
            File.WriteAllText(Path.Combine(home, "hooks.json"), json);
            if (Install(home, exe, remove: false, korean: false, TextWriter.Null) != 0) return false;
            var written = File.ReadAllText(config);
            if (!MentionsNotipetHook(written) || HooksJsonHasOurs(File.ReadAllText(Path.Combine(home, "hooks.json")))) return false;
            if (Install(home, exe, remove: false, korean: false, TextWriter.Null) != 0 || File.ReadAllText(config) != written) return false;
            if (Install(home, exe, remove: true, korean: false, TextWriter.Null) != 0 || MentionsNotipetHook(File.ReadAllText(config))) return false;
            return Directory.GetFiles(home, "*.bak-notipet-*").Length >= 2;
        }
        finally
        {
            try { Directory.Delete(home, recursive: true); } catch { }
        }
    }
}
