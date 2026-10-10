using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Notipet.Cli;

// notipet's Claude Code hooks, in one place for every way they are installed:
// printed by `install-hooks --claude`, written into ~/.claude/settings.json by
// `--write` (and taken out by `--remove`), and generated as the Claude plugin's
// hooks/hooks.json (`--plugin`, kept in step by scripts/check-claude-plugin.ps1).
//
// Exec form - `command` plus `args` - so no shell is involved: Claude Code
// spawns the exe directly, the same with or without Git Bash on Windows.
// `async` keeps notipet off the turn's critical path.
//
// Claude Code runs a plugin's hooks and the settings files' hooks side by
// side; only identical handlers across settings files are run once. So the
// plugin and a settings.json copy ring twice - --remove exists for moving to
// the plugin, and `doctor` warns.
internal static class ClaudeHooks
{
    private sealed record Hook(string Event, string? Matcher);

    private static readonly Hook[] Hooks =
    {
        // The moments Claude needs the user, and the turn's own "finished".
        new("Notification", "agent_needs_input|agent_completed|permission_prompt|idle_prompt"),
        new("Stop", null),
        new("SubagentStop", ".*"),
        // Ends what is over (the last turn's "finished"); notifies nothing.
        new("UserPromptSubmit", null),
    };

    public const string PluginId = "notipet@notipet";
    private const int TimeoutSeconds = 10;

    public static string ClaudeHome() =>
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    // The event map: { "Notification": [ { matcher, hooks: [ handler ] } ], ... }.
    public static JsonObject EventMap(string command)
    {
        var map = new JsonObject();
        foreach (var hook in Hooks) map[hook.Event] = new JsonArray(Group(hook, command));
        return map;
    }

    private static JsonObject Group(Hook hook, string command)
    {
        var group = new JsonObject();
        if (hook.Matcher is not null) group["matcher"] = hook.Matcher;
        group["hooks"] = new JsonArray(new JsonObject
        {
            ["type"] = "command",
            ["command"] = command,
            ["args"] = new JsonArray("--source", "claude-code"),
            ["timeout"] = TimeoutSeconds,
            ["async"] = true
        });
        return group;
    }

    // The plugin's hooks/hooks.json: `notipet.exe` from PATH, which the
    // installer sets, since one file serves every machine.
    public static string PluginFile() => Serialize(new JsonObject
    {
        ["description"] = "notipet: ring when Claude needs you or finishes, and stop alarms that are over. Windows; needs the notipet app.",
        ["hooks"] = EventMap("notipet.exe")
    });

    // A handler is notipet's when its command is a notipet executable.
    private static bool IsOurs(JsonNode? handler)
    {
        var command = Str(handler?["command"]);
        if (command is null) return false;
        var name = command.Trim().Trim('"');
        return name.Contains("notipet.exe", StringComparison.OrdinalIgnoreCase)
               || string.Equals(Path.GetFileName(name.Split(' ')[0]), "notipet", StringComparison.OrdinalIgnoreCase);
    }

    private static string? Str(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;

    // settings.json with notipet's hooks set in place (or, with remove, taken
    // out). Everything else is kept. Throws JsonException on a file that is not
    // a JSON object, or whose "hooks" is not one.
    public static JsonObject Apply(string? settingsJson, string exePath, bool remove)
    {
        var root = string.IsNullOrWhiteSpace(settingsJson)
            ? new JsonObject()
            : JsonNode.Parse(settingsJson, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) as JsonObject
              ?? throw new JsonException("settings.json is not a JSON object");
        if (root["hooks"] is not JsonObject events)
        {
            if (root["hooks"] is not null) throw new JsonException("\"hooks\" is not an object");
            if (remove) return root;
            events = new JsonObject();
            root["hooks"] = events;
        }

        foreach (var name in events.Select(e => e.Key).ToList())
        {
            if (events[name] is not JsonArray groups) continue;
            var at = StripOurs(groups);
            var hook = Hooks.FirstOrDefault(h => h.Event == name);
            if (!remove && hook is not null) groups.Insert(at < 0 ? groups.Count : Math.Min(at, groups.Count), Group(hook, exePath));
            if (groups.Count == 0) events.Remove(name);
        }
        if (!remove)
        {
            foreach (var hook in Hooks.Where(h => events[h.Event] is null)) events[hook.Event] = new JsonArray(Group(hook, exePath));
        }
        if (events.Count == 0) root.Remove("hooks");
        return root;
    }

    private static int StripOurs(JsonArray groups)
    {
        var first = -1;
        for (var i = groups.Count - 1; i >= 0; i--)
        {
            if (groups[i]?["hooks"] is not JsonArray handlers) continue;
            var had = false;
            for (var j = handlers.Count - 1; j >= 0; j--)
            {
                if (!IsOurs(handlers[j])) continue;
                handlers.RemoveAt(j);
                had = true;
            }
            if (!had) continue;
            first = i;
            if (handlers.Count == 0) groups.RemoveAt(i);
        }
        return first;
    }

    public static bool HasOurs(string? settingsJson)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(settingsJson)) return false;
            var root = JsonNode.Parse(settingsJson, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (root?["hooks"] is not JsonObject events) return false;
            return events.Any(e => e.Value is JsonArray groups
                && groups.Any(g => g?["hooks"] is JsonArray handlers && handlers.Any(IsOurs)));
        }
        catch
        {
            return false;
        }
    }

    // Whether settings.json enables the notipet plugin (from any marketplace).
    public static bool PluginEnabled(string? settingsJson)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(settingsJson)) return false;
            var root = JsonNode.Parse(settingsJson, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            return root?["enabledPlugins"] is JsonObject plugins
                   && plugins.Any(p => p.Key.StartsWith("notipet@", StringComparison.OrdinalIgnoreCase)
                                       && p.Value is JsonValue v && v.TryGetValue<bool>(out var on) && on);
        }
        catch
        {
            return false;
        }
    }

    public static string Serialize(JsonNode node)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            node.WriteTo(writer);
        }
        return Encoding.UTF8.GetString(stream.ToArray()) + Environment.NewLine;
    }

    // ----- install-hooks --claude --write / --remove -----

    public static int Install(string claudeHome, string exePath, bool remove, TextWriter output)
    {
        var path = Path.Combine(claudeHome, "settings.json");
        var existing = File.Exists(path) ? File.ReadAllText(path) : null;
        JsonObject updated;
        try
        {
            updated = Apply(existing, exePath, remove);
        }
        catch (JsonException ex)
        {
            output.WriteLine($"notipet: left {path} as it is: it is not JSON this command can edit ({ex.Message}).");
            return 1;
        }

        var same = existing is not null && Same(existing, updated);
        if (same || (existing is null && remove))
        {
            output.WriteLine(remove ? $"no notipet hooks in {path}" : $"already up to date: {path}");
        }
        else
        {
            Directory.CreateDirectory(claudeHome);
            if (existing is not null)
            {
                var stamp = DateTime.Now.ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture);
                File.Copy(path, path + ".bak-notipet-" + stamp, overwrite: true);
            }
            File.WriteAllText(path, Serialize(updated), new UTF8Encoding(false));
            output.WriteLine($"{(remove ? "took notipet's hooks out of" : "wrote notipet's hooks to")} {path}{(existing is not null ? " (backup next to it)" : "")}");
        }

        if (!remove)
        {
            if (PluginEnabled(existing))
            {
                output.WriteLine($"notipet: the {PluginId} plugin is enabled too, and Claude Code runs both - every hook would ring twice.");
                output.WriteLine("         Keep one: `notipet install-hooks --claude --remove`, or disable the plugin.");
            }
            output.WriteLine("New Claude Code sessions pick these up.");
        }
        return 0;
    }

    private static bool Same(string existingJson, JsonNode updated)
    {
        try { return JsonNode.DeepEquals(JsonNode.Parse(existingJson, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }), updated); }
        catch { return false; }
    }

    // ----- self-test -----

    public static bool RunSelfTest()
    {
        const string exe = @"C:\x\notipet.exe";

        // From nothing: four events, exec form, async.
        var fresh = Apply(null, exe, remove: false);
        foreach (var hook in Hooks)
        {
            if (fresh["hooks"]?[hook.Event] is not JsonArray { Count: 1 } groups) return false;
            if (Str(groups[0]?["matcher"]) != hook.Matcher) return false;
            var handler = groups[0]?["hooks"]?[0];
            if (Str(handler?["command"]) != exe || handler?["args"] is not JsonArray { Count: 2 } || handler["async"]?.GetValue<bool>() != true) return false;
        }

        // Someone's settings: other keys, other hooks and their order stay;
        // an old notipet entry (shell form, bin path) is replaced where it was.
        var user = """
            { "model": "opus", "enabledPlugins": { "other@x": true },
              "hooks": {
                "Stop": [
                  { "hooks": [ { "type": "command", "command": "C:\\old\\bin\\notipet.exe --source claude-code" } ] },
                  { "hooks": [ { "type": "command", "command": "say done" } ] } ],
                "PreToolUse": [ { "matcher": "Bash", "hooks": [ { "type": "command", "command": "guard.exe" } ] } ] } }
            """;
        var once = Apply(user, exe, remove: false);
        if (Str(once["model"]) != "opus" || once["enabledPlugins"]?["other@x"] is null) return false;
        if (once["hooks"]?["Stop"] is not JsonArray { Count: 2 } stop || Str(stop[0]?["hooks"]?[0]?["command"]) != exe || Str(stop[1]?["hooks"]?[0]?["command"]) != "say done") return false;
        if (Str(once["hooks"]?["PreToolUse"]?[0]?["hooks"]?[0]?["command"]) != "guard.exe") return false;
        if (!JsonNode.DeepEquals(once, Apply(Serialize(once), exe, remove: false))) return false;

        // Remove takes out only ours; an empty "hooks" goes too.
        var removed = Apply(Serialize(once), exe, remove: true);
        if (HasOurs(Serialize(removed)) || !HasOurs(Serialize(once))) return false;
        if (removed["hooks"]?["Stop"] is not JsonArray { Count: 1 } || removed["hooks"]?["Notification"] is not null) return false;
        if (Apply(Serialize(fresh), exe, remove: true)["hooks"] is not null) return false;

        // Not JSON it can edit: refused.
        try { Apply("{ \"hooks\": [] }", exe, false); return false; } catch (JsonException) { }

        // The plugin's file: notipet.exe from PATH, wrapped in "hooks".
        var plugin = JsonNode.Parse(PluginFile());
        if (Str(plugin?["hooks"]?["Stop"]?[0]?["hooks"]?[0]?["command"]) != "notipet.exe") return false;

        // Plugin detection reads enabledPlugins.
        if (!PluginEnabled("{\"enabledPlugins\":{\"notipet@notipet\":true}}") || PluginEnabled("{\"enabledPlugins\":{\"notipet@notipet\":false}}")) return false;

        // End to end in a scratch config dir: write, again, remove.
        var home = Path.Combine(Path.GetTempPath(), "notipet-claude-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(home);
            var path = Path.Combine(home, "settings.json");
            File.WriteAllText(path, user);
            if (Install(home, exe, remove: false, TextWriter.Null) != 0 || !HasOurs(File.ReadAllText(path))) return false;
            var written = File.ReadAllText(path);
            if (Install(home, exe, remove: false, TextWriter.Null) != 0 || File.ReadAllText(path) != written) return false;
            if (Install(home, exe, remove: true, TextWriter.Null) != 0 || HasOurs(File.ReadAllText(path))) return false;
            return Directory.GetFiles(home, "*.bak-notipet-*").Length >= 2;
        }
        finally
        {
            try { Directory.Delete(home, recursive: true); } catch { }
        }
    }
}
