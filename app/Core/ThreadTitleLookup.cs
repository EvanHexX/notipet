using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Notipet.Shared;

namespace Notipet.Core;

// Finds the name the agent app itself shows for a thread, so a card reads
// "Checkout refactor" - the same words as the Codex sidebar or the Claude
// session list - instead of an id or a title an LLM rewrote for this message.
//
// Both sources are other programs' local files, read-only and best-effort:
//   Codex:  $CODEX_HOME/session_index.jsonl - append-only {id, thread_name,
//           updated_at}; the last line for an id wins (codex-rs session_index.rs).
//   Claude: ~/.claude/sessions/<pid>.json - {sessionId, hostSessionId, name},
//           one per live session. Undocumented; only *.json is ever opened.
// Formats can change with any update, so every failure is a null, never an
// exception, and nothing here runs on the way to the speaker: TrayController
// calls it after the history entry exists, on a worker thread.
internal sealed class ThreadTitleLookup
{
    private const long MaxIndexBytes = 4 * 1024 * 1024;
    private const int MaxSessionFiles = 500;
    private const long MaxSessionFileBytes = 256 * 1024;
    private static readonly TimeSpan ClaudeRescan = TimeSpan.FromSeconds(5);

    private readonly string? _codexIndex;
    private readonly string? _claudeSessions;
    private readonly object _gate = new();

    private (long Length, DateTime WriteUtc) _codexStamp;
    private Dictionary<string, string> _codexTitles = new(StringComparer.OrdinalIgnoreCase);

    private DateTime _claudeScannedUtc = DateTime.MinValue;
    private Dictionary<string, string> _claudeTitles = new(StringComparer.OrdinalIgnoreCase);

    public ThreadTitleLookup(string? codexIndex, string? claudeSessions)
    {
        _codexIndex = codexIndex;
        _claudeSessions = claudeSessions;
    }

    // The default locations, honouring CODEX_HOME and CLAUDE_CONFIG_DIR the
    // way the agents themselves do.
    public static ThreadTitleLookup ForCurrentUser()
    {
        string? Under(string? root, params string[] parts)
        {
            if (string.IsNullOrWhiteSpace(root)) return null;
            try
            {
                var all = new string[parts.Length + 1];
                all[0] = root;
                parts.CopyTo(all, 1);
                return Path.Combine(all);
            }
            catch { return null; }
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        var claudeHome = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        return new ThreadTitleLookup(
            Under(string.IsNullOrWhiteSpace(codexHome) ? Under(home, ".codex") : codexHome, "session_index.jsonl"),
            Under(string.IsNullOrWhiteSpace(claudeHome) ? Under(home, ".claude") : claudeHome, "sessions"));
    }

    public string? Find(string agent, string? threadId, string? hostSession)
    {
        try
        {
            return agent switch
            {
                PayloadMapper.SourceCodex when threadId is not null => FindCodex(threadId),
                PayloadMapper.SourceClaude => FindClaude(threadId, hostSession),
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    private string? FindCodex(string threadId)
    {
        if (_codexIndex is null) return null;
        var info = new FileInfo(_codexIndex);
        if (!info.Exists) return null;

        lock (_gate)
        {
            var stamp = (info.Length, info.LastWriteTimeUtc);
            if (stamp != _codexStamp)
            {
                _codexTitles = ParseCodexIndex(ReadTail(_codexIndex, MaxIndexBytes));
                _codexStamp = stamp;
            }
            return _codexTitles.TryGetValue(threadId, out var title) ? title : null;
        }
    }

    private string? FindClaude(string? sessionId, string? hostSession)
    {
        if (_claudeSessions is null || (sessionId is null && hostSession is null)) return null;

        lock (_gate)
        {
            if (DateTime.UtcNow - _claudeScannedUtc > ClaudeRescan)
            {
                _claudeTitles = ScanClaudeSessions(_claudeSessions);
                _claudeScannedUtc = DateTime.UtcNow;
            }
            if (hostSession is not null && _claudeTitles.TryGetValue(hostSession, out var byHost)) return byHost;
            if (sessionId is not null && _claudeTitles.TryGetValue(sessionId, out var bySession)) return bySession;
            return null;
        }
    }

    // Lines are {"id":..,"thread_name":..,"updated_at":..}; later lines rename.
    internal static Dictionary<string, string> ParseCodexIndex(string text)
    {
        var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length < 2 || line[0] != '{') continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                var id = StringProp(root, "id");
                var name = AgentIdentity.Clean(StringProp(root, "thread_name"), AgentIdentity.MaxThreadTitle);
                if (id is not null && name is not null) titles[id] = name;
            }
            catch (JsonException)
            {
                // A torn last line while Codex is writing, or a format change.
            }
        }
        return titles;
    }

    // {sessionId, hostSessionId, name}, keyed both ways: a hook knows the
    // session id, the CLI inside Claude Desktop also knows the host id.
    internal static Dictionary<string, string> ScanClaudeSessions(string directory)
    {
        var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(directory)) return titles;

        var seen = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
        {
            // Exactly .json: the folder also holds files that are none of our
            // business, and a wildcard is not a promise about extensions.
            if (!string.Equals(Path.GetExtension(file), ".json", StringComparison.OrdinalIgnoreCase)) continue;
            if (++seen > MaxSessionFiles) break;
            try
            {
                var info = new FileInfo(file);
                if (info.Length > MaxSessionFileBytes) continue;
                using var doc = JsonDocument.Parse(ReadShared(file));
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                var name = AgentIdentity.Clean(StringProp(root, "name"), AgentIdentity.MaxThreadTitle);
                if (name is null) continue;
                if (StringProp(root, "sessionId") is { } session) titles[session] = name;
                if (StringProp(root, "hostSessionId") is { } host) titles[host] = name;
            }
            catch
            {
                // Being rewritten right now, or not ours to understand.
            }
        }
        return titles;
    }

    private static string? StringProp(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() is { Length: > 0 } s ? s : null
            : null;

    // Opened with full sharing: the agents keep appending while we read.
    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    // The newest part of an append-only file. A cut first line is dropped.
    private static string ReadTail(string path, long maxBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var start = Math.Max(0, stream.Length - maxBytes);
        stream.Seek(start, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        if (start > 0) reader.ReadLine();
        return reader.ReadToEnd();
    }

    public static bool RunSelfTest()
    {
        // Later lines rename; junk and torn lines are skipped.
        var index = string.Join("\n",
            "{\"id\":\"019a2b3c-4d5e-7f00-8a9b-0c1d2e3f4a5b\",\"thread_name\":\"First name\",\"updated_at\":\"x\"}",
            "not json",
            "{\"id\":\"019a2b3c-4d5e-7f00-8a9b-0c1d2e3f4a5b\",\"thread_name\":\"Checkout\\nrefactor\",\"updated_at\":\"y\"}",
            "{\"id\":\"other\",\"thread_name\":\"\"}",
            "{\"id\":\"torn\",\"thread_na");
        var titles = ParseCodexIndex(index);
        if (!titles.TryGetValue("019a2b3c-4d5e-7f00-8a9b-0c1d2e3f4a5b", out var title) || title != "Checkout refactor") return false;
        if (titles.ContainsKey("other") || titles.ContainsKey("torn")) return false;

        // Files on disk, in a scratch folder that is removed afterwards.
        var dir = Path.Combine(Path.GetTempPath(), "notipet-selftest-" + Guid.NewGuid().ToString("N"));
        try
        {
            var sessions = Path.Combine(dir, "sessions");
            Directory.CreateDirectory(sessions);
            File.WriteAllText(Path.Combine(sessions, "123.json"),
                "{\"pid\":123,\"sessionId\":\"s-1\",\"hostSessionId\":\"local_abc\",\"name\":\"Fix login\"}");
            File.WriteAllText(Path.Combine(sessions, "456.json"), "{ broken");
            // Only *.json is ever read.
            File.WriteAllText(Path.Combine(sessions, "123.key"), "{\"sessionId\":\"s-1\",\"name\":\"must not be read\"}");
            var codex = Path.Combine(dir, "session_index.jsonl");
            File.WriteAllText(codex, index);

            var lookup = new ThreadTitleLookup(codex, sessions);
            if (lookup.Find(PayloadMapper.SourceCodex, "019a2b3c-4d5e-7f00-8a9b-0c1d2e3f4a5b", null) != "Checkout refactor") return false;
            if (lookup.Find(PayloadMapper.SourceClaude, "s-1", null) != "Fix login") return false;
            if (lookup.Find(PayloadMapper.SourceClaude, null, "local_abc") != "Fix login") return false;
            if (lookup.Find(PayloadMapper.SourceClaude, "nope", null) is not null) return false;
            // Wrong agent, unknown agent, missing ids: null, never a throw.
            if (lookup.Find(PayloadMapper.SourceCodex, "s-1", null) is not null) return false;
            if (lookup.Find(PayloadMapper.SourceManual, "s-1", null) is not null) return false;
            if (lookup.Find(PayloadMapper.SourceCodex, null, null) is not null) return false;

            // Missing files are a null too.
            var nowhere = new ThreadTitleLookup(Path.Combine(dir, "absent.jsonl"), Path.Combine(dir, "absent"));
            if (nowhere.Find(PayloadMapper.SourceCodex, "x", null) is not null) return false;
            if (nowhere.Find(PayloadMapper.SourceClaude, "x", "local_x") is not null) return false;
            return true;
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
