using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Notipet.Shared;

namespace Notipet.Cli;

// The shim the agent hooks call, and a small admin tool for people.
//
// Two rules shape everything here:
//   1. Start fast. A hook sits on the agent's critical path, which is why this
//      is a separate NativeAOT console exe rather than a flag on the WinUI exe.
//   2. Never change the agent's behaviour. Exit code 0 even when delivery
//      failed, unless --strict is asked for, and nothing on stdout in hook
//      mode: a few hook events inject stdout into the agent's context.
internal static class Program
{
    private const string CliVersion = "1.2.0";
    private static readonly TimeSpan DefaultBudget = TimeSpan.FromMilliseconds(1500);

    private static async Task<int> Main(string[] args)
    {
        // Titles and bodies are often Korean; the default console code page
        // would print them as question marks.
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }

        try
        {
            return await RunAsync(args).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("notipet: " + ex.Message);
            return Fail(args);
        }
    }

    private static int Fail(string[] args) => HasFlag(args, "--strict") ? 1 : 0;

    private static async Task<int> RunAsync(string[] args)
    {
        var verb = args.Length > 0 ? args[0].ToLowerInvariant() : "";

        if (args.Length > 1 && (HasFlag(args, "--help") || HasFlag(args, "-h")) && Help.Has(verb))
        {
            Help.Print(verb);
            return 0;
        }

        switch (verb)
        {
            case "--self-test": return RunSelfTest();
            case "--help" or "-h" or "help" or "/?":
                Help.Print(args.Length > 1 ? args[1].ToLowerInvariant() : null);
                return 0;
            case "--version" or "version": return await VersionAsync(args).ConfigureAwait(false);
            case "send" or "alert":
                // `alert` with no title/body is how some hook configs call us
                // (Codex's notify appends its JSON after our own args), so a
                // trailing payload or piped stdin wins over an empty send.
                if (OptionValue(args, "--title") is null && OptionValue(args, "--body") is null)
                {
                    var hooked = await ReadHookPayloadAsync(args).ConfigureAwait(false);
                    if (hooked is not null)
                    {
                        var silent = HasFlag(args, "--verbose") ? args : args.Append("--quiet").ToArray();
                        return await SendAsync(hooked, silent).ConfigureAwait(false);
                    }
                }
                return await SendAsync(BuildFromFlags(args), args).ConfigureAwait(false);
            case "test": return await SendAsync(TestRequest(args), args).ConfigureAwait(false);
            case "ping": return await PingAsync(args).ConfigureAwait(false);
            case "status": return await StatusAsync(args).ConfigureAwait(false);
            case "doctor": return await DoctorAsync(args).ConfigureAwait(false);
            case "ack": return await AckAsync(args).ConfigureAwait(false);
            case "mute": return await MuteAsync(args).ConfigureAwait(false);
            case "unmute": return await MuteAsync(new[] { "mute", "off" }.Concat(args.Skip(1)).ToArray()).ConfigureAwait(false);
            case "desk" or "at-desk": return await DeskAsync(args).ConfigureAwait(false);
            case "history": return await HistoryAsync(args).ConfigureAwait(false);
            case "open": return await OpenAsync(args).ConfigureAwait(false);
            case "start": return await StartAsync(args).ConfigureAwait(false);
            case "stop": return await StopAsync(args).ConfigureAwait(false);
            case "restart":
                await StopAsync(args).ConfigureAwait(false);
                return await StartAsync(args).ConfigureAwait(false);
            case "install-hooks": return InstallHooks(args);
            case "install-skill": return SkillInstaller.Run(args);
        }

        if (verb.Length > 0 && !verb.StartsWith('-') && !verb.StartsWith('{'))
        {
            Console.Error.WriteLine($"notipet: unknown command '{args[0]}'. Try `notipet help`.");
            return Fail(args);
        }

        // No recognised verb: this is a hook invocation. Work out which kind.
        var request = await ReadHookPayloadAsync(args).ConfigureAwait(false);
        if (request is null)
        {
            Help.Print(null);
            return Fail(args);
        }

        // Hook mode stays silent on stdout unless asked. UserPromptSubmit and
        // SessionStart hooks inject stdout into the agent's context, and a JSON
        // blob there is noise at best.
        var quietArgs = HasFlag(args, "--verbose") ? args : args.Append("--quiet").ToArray();
        return await SendAsync(request, quietArgs).ConfigureAwait(false);
    }

    // ----- hook payloads -----

    // A hook payload, with what the payload does not carry (Claude Desktop's
    // session id, the repository root) filled in from the hook's environment -
    // the agent passes its own environment to hook commands.
    private static async Task<NotifyRequest?> ReadHookPayloadAsync(string[] args)
    {
        var request = await ReadHookJsonAsync(args).ConfigureAwait(false);
        if (request is not null)
        {
            try { FillIdentity(request, RealEnv, RealGitProbe, RealCeiling()); }
            catch { /* identity is decoration; the notification still goes */ }
        }
        return request;
    }

    // Detection order matters: an explicit flag wins, then Codex's argv-tail
    // JSON, then stdin.
    private static async Task<NotifyRequest?> ReadHookJsonAsync(string[] args)
    {
        var sourceHint = OptionValue(args, "--agent") ?? OptionValue(args, "--source");

        var explicitJson = OptionValue(args, "--json-payload") ?? OptionValue(args, "--json");
        if (explicitJson is not null && explicitJson.TrimStart().StartsWith('{')) return ParseHookJson(explicitJson, sourceHint);

        // Codex's legacy `notify` spawns the program with the payload as the
        // final argument. That payload can also be missing entirely when it
        // overflows the Windows command line (openai/codex#25141), so a parse
        // failure here is an expected case rather than a bug.
        if (args.Length > 0)
        {
            var last = args[^1].Trim();
            if (last.StartsWith('{'))
            {
                var parsed = ParseHookJson(last, sourceHint);
                return parsed ?? PayloadMapper.GenericFallback(
                    sourceHint ?? PayloadMapper.SourceCodex, "Turn complete (payload unreadable)");
            }
        }

        if (HasFlag(args, "--stdin") || Console.IsInputRedirected)
        {
            var stdin = await ReadStdinAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(stdin)) return ParseHookJson(stdin, sourceHint);
        }

        return null;
    }

    private static NotifyRequest? ParseHookJson(string json, string? sourceHint)
    {
        // Claude Code hooks and Codex hooks both use snake_case on stdin; Codex's
        // legacy notify uses kebab-case with a "type" field. Try the kebab shape
        // only when it actually looks like one.
        try
        {
            if (json.Contains("\"last-assistant-message\"", StringComparison.Ordinal)
                || json.Contains("\"turn-id\"", StringComparison.Ordinal)
                || json.Contains("\"thread-id\"", StringComparison.Ordinal))
            {
                var codex = JsonSerializer.Deserialize(json, NotipetJson.Compact.CodexNotifyEvent);
                if (codex is not null) return PayloadMapper.FromCodexNotify(codex);
            }

            var hook = JsonSerializer.Deserialize(json, NotipetJson.Compact.AgentHookEvent);
            return hook is null ? null : PayloadMapper.FromHookEvent(hook, sourceHint);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // A hook whose stdin never closes would hang the agent, so the read is
    // bounded and a timeout just means "no payload".
    private static async Task<string?> ReadStdinAsync(TimeSpan timeout)
    {
        var read = Task.Run(() => Console.In.ReadToEnd());
        var finished = await Task.WhenAny(read, Task.Delay(timeout)).ConfigureAwait(false);
        return finished == read ? await read.ConfigureAwait(false) : null;
    }

    // ----- send -----

    // ----- who and where -----

    private static string? RealEnv(string name)
    {
        try { return Environment.GetEnvironmentVariable(name); }
        catch { return null; }
    }

    // What <dir>\.git is: null (absent), "" (a directory: a repository root),
    // or the text of a .git FILE (a linked worktree or submodule). Only the
    // first few hundred bytes are read; it is one line.
    private static string? RealGitProbe(string dir)
    {
        var git = Path.Combine(dir, ".git");
        if (Directory.Exists(git)) return "";
        if (!File.Exists(git)) return null;
        using var stream = new FileStream(git, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var buffer = new byte[512];
        var read = stream.Read(buffer, 0, buffer.Length);
        return System.Text.Encoding.UTF8.GetString(buffer, 0, read).Split('\n')[0].Trim();
    }

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // Where the walk up for a repository stops: the user's profile folder.
    private static string? RealCeiling()
    {
        try { return NonEmpty(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)); }
        catch { return null; }
    }

    // `--cwd .` must mean this folder, not a project called ".".
    private static string? FullPathOrRaw(string? path)
    {
        if (path is null) return null;
        try { return Path.GetFullPath(path); }
        catch { return path; }
    }

    // The agent the CLI is running inside, judged from the variables each one
    // sets for its tool and hook subprocesses. Null when it cannot tell - in
    // neither, or in both (one agent started from inside the other).
    //   Claude Code: CLAUDE_CODE_SESSION_ID (documented: equals the hook
    //                session_id), CLAUDECODE=1.
    //   Codex:       CODEX_SESSION_ID (root thread), CODEX_THREAD_ID (current
    //                thread) - injected into shell commands by codex-rs.
    internal static string? DetectAgent(Func<string, string?> env)
    {
        var claude = NonEmpty(env("CLAUDE_CODE_SESSION_ID")) is not null || env("CLAUDECODE") == "1";
        var codex = NonEmpty(env("CODEX_SESSION_ID")) is not null || NonEmpty(env("CODEX_THREAD_ID")) is not null;
        if (claude == codex) return null;
        return claude ? PayloadMapper.SourceClaude : PayloadMapper.SourceCodex;
    }

    // Fills in what the sender did not say, from the agent's own environment.
    // Explicit values always win; nothing here can fail a send. Reads only the
    // named variables - never the whole environment - and nothing on disk but
    // `.git` markers on the way up from cwd.
    internal static void FillIdentity(NotifyRequest request, Func<string, string?> env, Func<string, string?>? gitProbe, string? ceiling = null)
    {
        var source = request.Source ??= new SourceInfo();
        var agent = AgentIdentity.NormalizeAgent(source.Id);
        source.Id = agent;
        source.Label ??= PayloadMapper.LabelFor(agent);

        if (agent == PayloadMapper.SourceClaude)
        {
            var envSession = NonEmpty(env("CLAUDE_CODE_SESSION_ID"));
            source.Session ??= envSession;
            // Claude Desktop's own id for this session - the one its claude://
            // link accepts. Only when the environment is about the same session
            // the notification is about.
            if (source.HostSession is null && (envSession is null || envSession == source.Session))
            {
                var host = NonEmpty(env("CLAUDE_CODE_HOST_SESSION_ID"));
                if (AgentIdentity.IsClaudeHostSession(host)) source.HostSession = host;
            }
            source.Client ??= NonEmpty(env("CLAUDE_CODE_ENTRYPOINT"));
        }
        else if (agent == PayloadMapper.SourceCodex)
        {
            // The root session first: hooks report the parent for subagents,
            // so this keeps a skill's notifications in the same thread.
            source.Session ??= NonEmpty(env("CODEX_SESSION_ID")) ?? NonEmpty(env("CODEX_THREAD_ID"));
        }

        if (source.Project is null)
        {
            // Claude documents CLAUDE_PROJECT_DIR for hooks: the project root,
            // which stays put when the session wanders into a worktree.
            var root = agent == PayloadMapper.SourceClaude ? NonEmpty(env("CLAUDE_PROJECT_DIR")) : null;
            source.Project = AgentIdentity.ProjectFromPath(root ?? source.Cwd, gitProbe, ceiling);
        }
    }

    // ----- send -----

    private static NotifyRequest BuildFromFlags(string[] args) => BuildFromFlags(args, RealEnv, RealGitProbe, RealCeiling());

    internal static NotifyRequest BuildFromFlags(string[] args, Func<string, string?> env, Func<string, string?>? gitProbe, string? ceiling = null)
    {
        // --agent is the friendlier name for --source. Without either, the
        // agent the CLI runs inside (if it can tell), else "manual".
        var named = OptionValue(args, "--agent") ?? OptionValue(args, "--source");
        var sourceId = named is not null ? AgentIdentity.NormalizeAgent(named) : DetectAgent(env) ?? PayloadMapper.SourceManual;

        // `--body -` reads the body from stdin, so output can be piped in:
        //   dotnet test | notipet send --title "tests" --body -
        var body = OptionValue(args, "--body") ?? OptionValue(args, "--message") ?? "";
        if (body == "-")
        {
            body = ReadStdinAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult()?.Trim() ?? "";
        }

        var channels = OptionValue(args, "--channels")?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(c => c.ToLowerInvariant() switch
            {
                "sound" => "windows_sound",
                "balloon" or "notification" or "tray" => "tray_balloon",
                _ => c
            })
            .ToList();

        var request = new NotifyRequest
        {
            Title = OptionValue(args, "--title") ?? PayloadMapper.LabelFor(sourceId),
            Body = body,
            Level = OptionValue(args, "--level"),
            Tag = OptionValue(args, "--tag"),
            Source = new SourceInfo
            {
                Id = sourceId,
                Label = PayloadMapper.LabelFor(sourceId),
                Cwd = FullPathOrRaw(NonEmpty(OptionValue(args, "--cwd"))) ?? SafeCurrentDirectory(),
                Project = NonEmpty(OptionValue(args, "--project")),
                Session = NonEmpty(OptionValue(args, "--thread") ?? OptionValue(args, "--session")),
                ThreadTitle = NonEmpty(OptionValue(args, "--thread-title"))
            },
            Sound = BuildSound(args),
            Channels = channels is { Count: > 0 } ? channels : null,
            TtlSec = int.TryParse(OptionValue(args, "--ttl"), out var ttl) ? ttl : null
        };
        FillIdentity(request, env, gitProbe, ceiling);
        return request;
    }

    private static string? SafeCurrentDirectory()
    {
        try { return Environment.CurrentDirectory; } catch { return null; }
    }

    private static SoundSpec? BuildSound(string[] args)
    {
        var repeat = OptionValue(args, "--repeat");
        var sound = OptionValue(args, "--sound");
        var volume = OptionValue(args, "--volume");
        var silent = HasFlag(args, "--silent");
        if (repeat is null && sound is null && volume is null && !silent) return null;

        // --sound takes a library name or a Windows alias; aliases contain a
        // dot or start with "System", library names do not.
        var isAlias = sound is not null && (sound.Contains('.') || sound.StartsWith("System", StringComparison.OrdinalIgnoreCase));
        return new SoundSpec
        {
            Name = isAlias ? null : sound,
            Alias = isAlias ? sound : null,
            Repeat = repeat,
            Volume = double.TryParse(volume, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null,
            Mute = silent ? true : null
        };
    }

    private static NotifyRequest TestRequest(string[] args) => new()
    {
        Title = "notipet",
        Body = "Test notification",
        Level = OptionValue(args, "--level") ?? "attention",
        Tag = "notipet:cli-test:" + Guid.NewGuid().ToString("N")[..8],
        Source = new SourceInfo { Id = PayloadMapper.SourceManual }
    };

    private static async Task<int> SendAsync(NotifyRequest request, string[] args)
    {
        var daemon = await RuntimeDiscovery.FindOrLaunchAsync(
            allowLaunch: !HasFlag(args, "--no-launch"),
            budget: TimeSpan.FromMilliseconds(2500)).ConfigureAwait(false);

        if (daemon is null)
        {
            Console.Error.WriteLine("notipet: daemon not running");
            return Fail(args);
        }

        if (HasFlag(args, "--fire-and-forget"))
        {
            _ = RuntimeDiscovery.PostAsync(daemon.Info, "/v1/notify", request, NotipetJson.Compact.NotifyRequest, DefaultBudget);
            return 0;
        }

        var (ok, status, body) = await RuntimeDiscovery.PostAsync(
            daemon.Info, "/v1/notify", request, NotipetJson.Compact.NotifyRequest, DefaultBudget).ConfigureAwait(false);

        if (!ok)
        {
            Console.Error.WriteLine($"notipet: notify failed ({status}) {body}");
            return Fail(args);
        }

        if (HasFlag(args, "--quiet")) return 0;
        if (HasFlag(args, "--json")) { Console.WriteLine(body); return 0; }

        var response = TryParse(body, NotipetJson.Compact.NotifyResponse);
        if (response is null) { Console.WriteLine(body); return 0; }
        Console.WriteLine(Describe(response));
        return 0;
    }

    public static string Describe(NotifyResponse r)
    {
        var sb = new StringBuilder();
        if (r.Accepted)
        {
            var parts = r.Deliveries.Select(d => $"{Channel(d.Channel)} {d.Status}" + (d.Detail is { Length: > 0 } ? $" ({d.Detail})" : ""));
            sb.Append($"sent [{r.Level}]: {string.Join(", ", parts)}");
        }
        else
        {
            sb.Append($"not sent: {r.SuppressedReason}");
            if (r.CollapsedWith is not null) sb.Append($" (same as {r.CollapsedWith})");
            if (r.NextAllowedAt is not null && DateTimeOffset.TryParse(r.NextAllowedAt, out var next))
            {
                sb.Append($", next allowed {next.ToLocalTime():HH:mm:ss}");
            }
        }
        if (r.Warnings.Count > 0) sb.Append($"  - {string.Join("; ", r.Warnings)}");
        return sb.ToString();
    }

    private static string Channel(string id) => id switch
    {
        "windows_sound" => "sound",
        "tray_balloon" => "notification",
        _ => id
    };

    // ----- read-only commands -----

    private static async Task<int> VersionAsync(string[] args)
    {
        Console.WriteLine($"notipet cli {CliVersion}");
        var daemon = await RuntimeDiscovery.FindAsync(TimeSpan.FromMilliseconds(800)).ConfigureAwait(false);
        Console.WriteLine(daemon is null ? "daemon      not running" : $"daemon      {daemon.Info.Version} (pid {daemon.Info.Pid})");
        return 0;
    }

    private static async Task<int> PingAsync(string[] args)
    {
        var info = RuntimeDiscovery.ReadFile();
        if (info is null)
        {
            Console.WriteLine("notipet: not running (no runtime.json)");
            return Fail(args);
        }

        var daemon = await RuntimeDiscovery.FindAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        if (daemon is null)
        {
            Console.WriteLine($"notipet: not running (stale runtime.json, pid {info.Pid})");
            return Fail(args);
        }

        Console.WriteLine($"ok  version={daemon.Info.Version}  port={daemon.Info.Port}  pid={daemon.Info.Pid}");
        return 0;
    }

    private static async Task<int> StatusAsync(string[] args)
    {
        var daemon = await RuntimeDiscovery.FindAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        if (daemon is null)
        {
            if (HasFlag(args, "--json")) Console.WriteLine("{\"ok\":false,\"running\":false}");
            else Console.WriteLine("notipet: not running");
            return Fail(args);
        }

        var (_, _, body) = await RuntimeDiscovery.SendAsync(daemon.Info, HttpMethod.Get, "/v1/health", null, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        if (HasFlag(args, "--json")) { Console.WriteLine(body); return 0; }

        var health = TryParse(body, NotipetJson.Compact.HealthResponse);
        if (health is null)
        {
            Console.WriteLine("notipet: no response");
            return Fail(args);
        }

        Row("version", health.Version);
        Row("port", health.Port?.ToString());
        Row("pid", health.Pid?.ToString());
        Row("uptime", health.UptimeSec is { } up ? FormatDuration(TimeSpan.FromSeconds(up)) : null);
        Row("muted", YesNo(health.Muted));
        Row("at desk", YesNo(health.AtDesk));
        Row("quiet hours", health.QuietHoursActive == true ? "active now" : "not active");
        Row("presence", health.Presence);
        Row("alarms", health.ActiveAlarms?.ToString());
        Row("history", health.HistoryCount is { } n ? $"{n} kept" : null);
        Row("language", health.Language);
        Row("sound", health.SoundEngine is { } s ? $"{s.Name} ({(s.Available ? "ok" : "unavailable")})" : null);
        if (health.SoundEngine?.LastError is { Length: > 0 } error) Row("sound error", error);
        foreach (var warning in health.Warnings ?? new List<string>()) Row("warning", warning);
        return 0;
    }

    private static async Task<int> HistoryAsync(string[] args)
    {
        var sub = args.Length > 1 ? args[1].ToLowerInvariant() : "";
        var daemon = await RuntimeDiscovery.FindAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        if (daemon is null)
        {
            Console.Error.WriteLine("notipet: not running");
            return Fail(args);
        }

        if (sub == "clear")
        {
            var (ok, _, clearBody) = await RuntimeDiscovery.SendAsync(daemon.Info, HttpMethod.Delete, "/v1/history", null, DefaultBudget).ConfigureAwait(false);
            var cleared = TryParse(clearBody, NotipetJson.Compact.ClearHistoryResponse);
            Console.WriteLine(ok && cleared is not null ? $"cleared {cleared.Cleared}" : $"notipet: clear failed {clearBody}");
            return ok ? 0 : Fail(args);
        }

        var limit = int.TryParse(OptionValue(args, "--limit") ?? OptionValue(args, "-n"), out var n) ? Math.Clamp(n, 1, 500) : 20;
        var (_, _, body) = await RuntimeDiscovery.SendAsync(daemon.Info, HttpMethod.Get, $"/v1/history?limit={limit}", null, DefaultBudget).ConfigureAwait(false);
        if (HasFlag(args, "--json")) { Console.WriteLine(body); return 0; }

        var history = TryParse(body, NotipetJson.Compact.HistoryResponse);
        if (history is null || history.Entries.Count == 0)
        {
            Console.WriteLine("(nothing yet)");
            return 0;
        }

        foreach (var e in history.Entries)
        {
            var time = DateTimeOffset.TryParse(e.At, out var at) ? at.ToLocalTime().ToString("MM-dd HH:mm") : e.At;
            var count = e.Count > 1 ? $" x{e.Count}" : "";
            var state = e.Accepted ? "" : $"  [not sent: {e.SuppressedReason}]";
            var project = string.IsNullOrWhiteSpace(e.Project) ? "" : $"[{e.Project}] ";
            Console.WriteLine($"{time}  {e.Level,-9} {e.Source,-12} {project}{e.Title}{count}{state}");
            if (HasFlag(args, "--short")) continue;
            if (!string.IsNullOrWhiteSpace(e.ThreadTitle))
            {
                Console.WriteLine($"{"",29}# {PayloadMapper.Summarize(e.ThreadTitle, 100)}");
            }
            if (!string.IsNullOrWhiteSpace(e.Body))
            {
                Console.WriteLine($"{"",29}{PayloadMapper.Summarize(e.Body, 100)}");
            }
        }
        return 0;
    }

    // Everything worth checking when "I didn't hear anything", in one place.
    private static async Task<int> DoctorAsync(string[] args)
    {
        var problems = 0;
        void Check(bool ok, string label, string detail, bool warnOnly = false)
        {
            var mark = ok ? "ok  " : warnOnly ? "warn" : "FAIL";
            if (!ok && !warnOnly) problems++;
            Console.WriteLine($"[{mark}] {label,-18} {detail}");
        }

        var exe = Environment.ProcessPath ?? "notipet.exe";
        var daemonExe = RuntimeDiscovery.DaemonPath(preferRunning: true);
        Check(daemonExe is not null, "daemon exe", daemonExe ?? "NotipetTray.exe not found next to the CLI");

        var info = RuntimeDiscovery.ReadFile();
        var daemon = await RuntimeDiscovery.FindAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        Check(daemon is not null, "daemon running",
            daemon is not null ? $"pid {daemon.Info.Pid}, port {daemon.Info.Port}, v{daemon.Info.Version}"
            : info is null ? "not running - start it with `notipet start`"
            : $"stale runtime.json (pid {info.Pid}) - `notipet start` replaces it");

        if (daemon is not null)
        {
            var (_, _, body) = await RuntimeDiscovery.SendAsync(daemon.Info, HttpMethod.Get, "/v1/health", null, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            var health = TryParse(body, NotipetJson.Compact.HealthResponse);
            if (health is not null)
            {
                Check(health.SoundEngine?.Available == true, "sound engine",
                    health.SoundEngine is { } s ? $"{s.Name}{(s.LastError is { Length: > 0 } e ? " - " + e : "")}" : "unknown");
                Check(health.Muted != true, "muted", health.Muted == true ? "yes - `notipet unmute`" : "no", warnOnly: true);
                Check(health.QuietHoursActive != true, "quiet hours", health.QuietHoursActive == true ? "active now - low levels are held back" : "not active", warnOnly: true);
                Check(true, "at desk", health.AtDesk == true ? "yes - long alarms are shortened" : "no");
            }
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var claudeSettings = Path.Combine(home, ".claude", "settings.json");
        var claudeHooked = FileMentions(claudeSettings, "notipet");
        Check(claudeHooked, "claude hooks", claudeHooked ? claudeSettings : "not configured - `notipet install-hooks --claude`", warnOnly: true);

        var codexConfig = Path.Combine(home, ".codex", "config.toml");
        var codexHooked = FileMentions(codexConfig, "notipet");
        Check(codexHooked, "codex hooks", codexHooked ? codexConfig : "not configured - `notipet install-hooks --codex`", warnOnly: true);

        var skill = SkillInstaller.ClaudeSkillPath();
        Check(File.Exists(skill), "claude skill", File.Exists(skill) ? skill : "not installed - `notipet install-skill`", warnOnly: true);

        Check(true, "cli", exe);
        Console.WriteLine(problems == 0 ? "\nno problems found" : $"\n{problems} problem(s) found");
        return problems == 0 ? 0 : Fail(args);
    }

    private static bool FileMentions(string path, string needle)
    {
        try { return File.Exists(path) && File.ReadAllText(path).Contains(needle, StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    // ----- state-changing commands -----

    private static async Task<int> AckAsync(string[] args)
    {
        var daemon = await RequireDaemon(args).ConfigureAwait(false);
        if (daemon is null) return Fail(args);

        var request = new AckRequest
        {
            Id = OptionValue(args, "--id"),
            Tag = OptionValue(args, "--tag"),
            All = HasFlag(args, "--all") ? true : null
        };

        var (ok, _, body) = await RuntimeDiscovery.PostAsync(daemon.Info, "/v1/ack", request, NotipetJson.Compact.AckRequest, DefaultBudget).ConfigureAwait(false);
        var response = TryParse(body, NotipetJson.Compact.AckResponse);
        Console.WriteLine(HasFlag(args, "--json") ? body : response is not null ? $"stopped {response.Stopped} alarm(s)" : body);
        return ok ? 0 : Fail(args);
    }

    private static async Task<int> MuteAsync(string[] args)
    {
        var daemon = await RequireDaemon(args).ConfigureAwait(false);
        if (daemon is null) return Fail(args);

        // `notipet mute off` reads better than `notipet mute --muted false`.
        var argument = args.Length > 1 ? args[1].ToLowerInvariant() : "";
        var request = new MuteRequest();
        if (argument is "off" or "false" or "no")
        {
            request.Muted = false;
        }
        else
        {
            request.Muted = true;
            if (TryParseDuration(argument, out var minutes)) request.Minutes = minutes;
            else if (argument.Length > 0 && !argument.StartsWith('-'))
            {
                Console.Error.WriteLine($"notipet: '{args[1]}' is not a duration - use e.g. 30m, 2h or off");
                return Fail(args);
            }
        }

        var (ok, _, body) = await RuntimeDiscovery.PostAsync(daemon.Info, "/v1/mute", request, NotipetJson.Compact.MuteRequest, DefaultBudget).ConfigureAwait(false);
        var response = TryParse(body, NotipetJson.Compact.MuteResponse);
        if (HasFlag(args, "--json") || response is null) Console.WriteLine(body);
        else if (!response.Muted) Console.WriteLine("unmuted");
        else if (response.Until is not null && DateTimeOffset.TryParse(response.Until, out var until)) Console.WriteLine($"muted until {until.ToLocalTime():HH:mm}");
        else Console.WriteLine("muted (until you unmute)");
        return ok ? 0 : Fail(args);
    }

    private static async Task<int> DeskAsync(string[] args)
    {
        var daemon = await RequireDaemon(args).ConfigureAwait(false);
        if (daemon is null) return Fail(args);

        var argument = args.Length > 1 ? args[1].ToLowerInvariant() : "";
        if (argument is "" or "status")
        {
            var health = await RuntimeDiscovery.GetAsync(daemon.Info, "/v1/health", NotipetJson.Compact.HealthResponse, DefaultBudget).ConfigureAwait(false);
            Console.WriteLine(health?.AtDesk == true ? "at desk" : "away");
            return 0;
        }

        bool? value = argument switch
        {
            "on" or "yes" or "true" or "here" => true,
            "off" or "no" or "false" or "away" => false,
            "toggle" => null,
            _ => throw new ArgumentException($"'{args[1]}' - use on, off or toggle")
        };

        var (ok, _, body) = await RuntimeDiscovery.PostAsync(daemon.Info, "/v1/presence",
            new PresenceRequest { AtDesk = value }, NotipetJson.Compact.PresenceRequest, DefaultBudget).ConfigureAwait(false);
        var response = TryParse(body, NotipetJson.Compact.PresenceResponse);
        Console.WriteLine(HasFlag(args, "--json") || response is null ? body : response.AtDesk ? "at desk" : "away");
        return ok ? 0 : Fail(args);
    }

    private static async Task<int> OpenAsync(string[] args)
    {
        var what = args.Length > 1 ? args[1].ToLowerInvariant() : "recent";
        if (what is not ("recent" or "history" or "settings" or "options"))
        {
            Console.Error.WriteLine($"notipet: open what? use `recent` or `settings`");
            return Fail(args);
        }

        // The window belongs to the running daemon; a second launch of the
        // daemon exe signals it to open one.
        var daemon = await RuntimeDiscovery.FindOrLaunchAsync(allowLaunch: true, TimeSpan.FromMilliseconds(3000)).ConfigureAwait(false);
        var exe = RuntimeDiscovery.DaemonPath(preferRunning: true);
        if (daemon is null || exe is null)
        {
            Console.Error.WriteLine("notipet: daemon not running");
            return Fail(args);
        }

        var arguments = what is "settings" or "options" ? "--settings" : "";
        Process.Start(new ProcessStartInfo(exe, arguments) { UseShellExecute = true });
        return 0;
    }

    private static async Task<int> StartAsync(string[] args)
    {
        var running = await RuntimeDiscovery.FindAsync(TimeSpan.FromMilliseconds(800)).ConfigureAwait(false);
        if (running is not null)
        {
            Console.WriteLine($"already running (pid {running.Info.Pid})");
            return 0;
        }

        var daemon = await RuntimeDiscovery.FindOrLaunchAsync(allowLaunch: true, TimeSpan.FromSeconds(8)).ConfigureAwait(false);
        if (daemon is null)
        {
            Console.Error.WriteLine("notipet: could not start the daemon (NotipetTray.exe not found, or it did not come up)");
            return Fail(args);
        }
        Console.WriteLine($"started (pid {daemon.Info.Pid}, port {daemon.Info.Port})");
        return 0;
    }

    private static async Task<int> StopAsync(string[] args)
    {
        var daemon = await RuntimeDiscovery.FindAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        if (daemon is null)
        {
            Console.WriteLine("not running");
            return 0;
        }

        var pid = daemon.Info.Pid;
        var (ok, _, body) = await RuntimeDiscovery.SendAsync(daemon.Info, HttpMethod.Post, "/v1/shutdown", "{}", DefaultBudget).ConfigureAwait(false);
        if (!ok)
        {
            Console.Error.WriteLine($"notipet: stop failed {body}");
            return Fail(args);
        }

        // Wait for the process to actually go, so `restart` and publish scripts
        // do not race it for the exe lock.
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (process.HasExited) break;
            }
            catch (ArgumentException)
            {
                break;
            }
            await Task.Delay(150).ConfigureAwait(false);
        }
        Console.WriteLine($"stopped (pid {pid})");
        return 0;
    }

    private static async Task<Daemon?> RequireDaemon(string[] args)
    {
        var daemon = await RuntimeDiscovery.FindAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        if (daemon is null) Console.Error.WriteLine("notipet: not running - `notipet start`");
        return daemon;
    }

    private static int InstallHooks(string[] args)
    {
        var exe = Environment.ProcessPath ?? "notipet.exe";
        var wantsClaude = HasFlag(args, "--claude") || !HasFlag(args, "--codex");
        var wantsCodex = HasFlag(args, "--codex") || !HasFlag(args, "--claude");

        if (wantsClaude)
        {
            Console.WriteLine("# Claude Code - add to %USERPROFILE%\\.claude\\settings.json");
            Console.WriteLine(HookSnippets.ClaudeCommandHook(exe));
            Console.WriteLine();
            Console.WriteLine("# Or, with the daemon's port pinned in settings.json (server.port),");
            Console.WriteLine("# the leaner http hook that skips this CLI entirely:");
            Console.WriteLine(HookSnippets.ClaudeHttpHook(RuntimeDiscovery.ReadFile()?.Port ?? 47811));
            Console.WriteLine();
        }

        if (wantsCodex)
        {
            Console.WriteLine("# Codex - add to %USERPROFILE%\\.codex\\config.toml");
            Console.WriteLine("# Leave any existing `notify = [...]` line alone: it is a single slot");
            Console.WriteLine("# and overwriting it would break whatever already owns it.");
            Console.WriteLine(HookSnippets.CodexHooks(exe));
        }

        Console.WriteLine();
        Console.WriteLine("Printed only. Paste these in yourself, or see integrations/ for full files.");
        return 0;
    }

    // ----- helpers -----

    private static T? TryParse<T>(string json, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) where T : class
    {
        try { return JsonSerializer.Deserialize(json, type); } catch { return null; }
    }

    private static void Row(string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) Console.WriteLine($"{label,-13}{value}");
    }

    private static string YesNo(bool? value) => value == true ? "yes" : "no";

    private static string FormatDuration(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h"
        : t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m"
        : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m {t.Seconds}s"
        : $"{(int)t.TotalSeconds}s";

    public static bool TryParseDuration(string value, out int minutes)
    {
        minutes = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var text = value.Trim().ToLowerInvariant();
        var multiplier = 1;
        if (text.EndsWith('h')) { multiplier = 60; text = text[..^1]; }
        else if (text.EndsWith('m')) { text = text[..^1]; }

        if (!int.TryParse(text, out var amount) || amount <= 0) return false;
        minutes = amount * multiplier;
        return true;
    }

    internal static string? OptionValue(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        }
        return null;
    }

    internal static bool HasFlag(string[] args, string name)
    {
        foreach (var arg in args)
        {
            if (arg.Equals(name, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // ----- self-test -----

    private static int RunSelfTest()
    {
        var checks = new (string Name, Func<bool> Check)[]
        {
            ("NotificationLevelParser", NotificationLevelParser.RunSelfTest),
            ("PayloadMapper", PayloadMapper.RunSelfTest),
            ("AgentIdentity", AgentIdentity.RunSelfTest),
            ("RuntimeDiscovery", RuntimeDiscovery.RunSelfTest),
            ("ArgParsing", ArgParsingSelfTest),
            ("Describe", DescribeSelfTest),
            ("SkillInstaller", SkillInstaller.RunSelfTest),
            ("Help", Help.RunSelfTest),
        };

        var failures = 0;
        foreach (var (name, check) in checks)
        {
            bool passed;
            try { passed = check(); }
            catch (Exception ex) { passed = false; Console.Error.WriteLine($"  {name}: {ex}"); }
            Console.WriteLine($"[{(passed ? "PASS" : "FAIL")}] {name}");
            if (!passed) failures++;
        }

        Console.WriteLine(failures == 0
            ? $"notipet cli self-test: all {checks.Length} checks passed"
            : $"notipet cli self-test: {failures} of {checks.Length} checks FAILED");
        return failures == 0 ? 0 : 1;
    }

    private static bool ArgParsingSelfTest()
    {
        var args = new[] { "send", "--title", "hello", "--level", "warn", "--strict" };
        if (OptionValue(args, "--title") != "hello") return false;
        if (OptionValue(args, "--level") != "warn") return false;
        if (!HasFlag(args, "--strict")) return false;
        if (HasFlag(args, "--quiet")) return false;
        // A flag at the end with no value must not read past the array.
        if (OptionValue(args, "--strict") is not null) return false;
        if (OptionValue(args, "--missing") is not null) return false;

        // Never the real environment: this self-test may itself be running
        // inside Claude Code or Codex.
        static string? NoEnv(string _) => null;
        NotifyRequest Build(params string[] a) => BuildFromFlags(a, NoEnv, null);

        var built = Build(args);
        if (built.Title != "hello" || built.Level != "warn") return false;
        if (built.Source?.Id != PayloadMapper.SourceManual) return false;
        // No sound flags means no sound block, so settings decide.
        if (built.Sound is not null) return false;

        var withSound = Build("send", "--title", "x", "--repeat", "until-ack");
        if (withSound.Sound?.Repeat != "until-ack") return false;

        // --sound tells an alias from a library name.
        if (Build("send", "--title", "x", "--sound", "Notification.IM").Sound?.Alias != "Notification.IM") return false;
        if (Build("send", "--title", "x", "--sound", "ding").Sound?.Name != "ding") return false;
        if (Build("send", "--title", "x", "--silent").Sound?.Mute != true) return false;

        // Friendly channel names map to channel ids.
        var channels = Build("send", "--title", "x", "--channels", "sound, balloon").Channels;
        if (channels is null || channels.Count != 2 || channels[0] != "windows_sound" || channels[1] != "tray_balloon") return false;

        // --- who and where ---

        // Explicit flags land in the source block; --agent is --source.
        var placed = Build("send", "--title", "x", "--agent", "claude", "--project", "shop",
            "--thread", "s-1", "--thread-title", "Checkout refactor", "--cwd", @"E:\Work\shop");
        if (placed.Source?.Id != PayloadMapper.SourceClaude) return false;
        if (placed.Source.Project != "shop" || placed.Source.Session != "s-1" || placed.Source.ThreadTitle != "Checkout refactor") return false;
        if (Build("send", "--title", "x", "--source", "codex").Source?.Id != PayloadMapper.SourceCodex) return false;

        // With no --project, the cwd's repository name; with no flags at all
        // and no agent around, still a valid manual notification.
        if (Build("send", "--title", "x", "--cwd", @"C:\src\notipet").Source?.Project != "notipet") return false;
        var lone = Build("send", "--body", "only a body", "--cwd", @"D:\x");
        if (lone.Source?.Id != PayloadMapper.SourceManual || lone.Source.Session is not null || lone.Source.HostSession is not null) return false;
        if (lone.Title != "notipet") return false;

        // Inside Claude Desktop: agent, thread and host session come from the
        // environment, and the default title names the agent.
        var claudeEnv = new Dictionary<string, string>
        {
            ["CLAUDECODE"] = "1",
            ["CLAUDE_CODE_SESSION_ID"] = "0b7c3a5e-1111-2222-3333-444455556666",
            ["CLAUDE_CODE_HOST_SESSION_ID"] = "local_6f1c2a7e-0b1d-4c55-9a77-1e2f3a4b5c6d",
            ["CLAUDE_CODE_ENTRYPOINT"] = "claude-desktop",
        };
        string? ClaudeEnv(string n) => claudeEnv.TryGetValue(n, out var v) ? v : null;
        var inClaude = BuildFromFlags(new[] { "send", "--body", "done", "--cwd", @"C:\src\notipet" }, ClaudeEnv, null);
        if (inClaude.Source?.Id != PayloadMapper.SourceClaude || inClaude.Title != "Claude Code") return false;
        if (inClaude.Source.Session != "0b7c3a5e-1111-2222-3333-444455556666") return false;
        if (inClaude.Source.HostSession != "local_6f1c2a7e-0b1d-4c55-9a77-1e2f3a4b5c6d") return false;
        if (inClaude.Source.Client != "claude-desktop") return false;
        // An explicit --source beats the environment, and then nothing of
        // Claude's is borrowed.
        var overridden = BuildFromFlags(new[] { "send", "--title", "x", "--source", "manual" }, ClaudeEnv, null);
        if (overridden.Source?.Id != PayloadMapper.SourceManual || overridden.Source.HostSession is not null) return false;
        // A host id about a different session than the notification is not used.
        var otherThread = BuildFromFlags(new[] { "send", "--title", "x", "--thread", "different" }, ClaudeEnv, null);
        if (otherThread.Source?.Session != "different" || otherThread.Source.HostSession is not null) return false;
        // A malformed host id is not passed on.
        claudeEnv["CLAUDE_CODE_HOST_SESSION_ID"] = "local_../../x";
        if (BuildFromFlags(new[] { "send", "--title", "x" }, ClaudeEnv, null).Source?.HostSession is not null) return false;

        // Inside Codex: the root session wins over the current thread.
        var codexEnv = new Dictionary<string, string>
        {
            ["CODEX_SESSION_ID"] = "019a2b3c-4d5e-7f00-8a9b-0c1d2e3f4a5b",
            ["CODEX_THREAD_ID"] = "019a2b3c-0000-7f00-8a9b-0c1d2e3f4a5b",
        };
        string? CodexEnv(string n) => codexEnv.TryGetValue(n, out var v) ? v : null;
        var inCodex = BuildFromFlags(new[] { "send", "--title", "x" }, CodexEnv, null);
        if (inCodex.Source?.Id != PayloadMapper.SourceCodex || inCodex.Source.Session != "019a2b3c-4d5e-7f00-8a9b-0c1d2e3f4a5b") return false;
        codexEnv.Remove("CODEX_SESSION_ID");
        if (BuildFromFlags(new[] { "send", "--title", "x" }, CodexEnv, null).Source?.Session != "019a2b3c-0000-7f00-8a9b-0c1d2e3f4a5b") return false;

        // Inside both (one agent launched from the other): no guessing.
        string? BothEnv(string n) => ClaudeEnv(n) ?? CodexEnv(n);
        if (BuildFromFlags(new[] { "send", "--title", "x" }, BothEnv, null).Source?.Id != PayloadMapper.SourceManual) return false;

        // A hook payload is filled the same way: Claude Desktop's host id and
        // CLAUDE_PROJECT_DIR over a worktree cwd.
        claudeEnv["CLAUDE_CODE_HOST_SESSION_ID"] = "local_6f1c2a7e-0b1d-4c55-9a77-1e2f3a4b5c6d";
        claudeEnv["CLAUDE_PROJECT_DIR"] = @"C:\src\notipet";
        // The cwd is somewhere else entirely, so only CLAUDE_PROJECT_DIR can
        // produce "notipet" (the cwd alone would give "sub").
        var hooked = ParseHookJson("{\"session_id\":\"0b7c3a5e-1111-2222-3333-444455556666\",\"hook_event_name\":\"Stop\"," +
                                   "\"cwd\":\"D:\\\\elsewhere\\\\sub\"}", "claude-code")!;
        FillIdentity(hooked, ClaudeEnv, null);
        if (hooked.Source?.HostSession != "local_6f1c2a7e-0b1d-4c55-9a77-1e2f3a4b5c6d" || hooked.Source.Project != "notipet") return false;
        // CLAUDE_PROJECT_DIR is Claude's; a Codex hook ignores it.
        var codexHook = ParseHookJson("{\"session_id\":\"019a2b3c-4d5e-7f00-8a9b-0c1d2e3f4a5b\",\"hook_event_name\":\"Stop\"," +
                                      "\"turn_id\":\"t\",\"cwd\":\"D:\\\\elsewhere\\\\sub\"}", "codex")!;
        FillIdentity(codexHook, BothEnv, null);
        if (codexHook.Source?.Project != "sub" || codexHook.Source.HostSession is not null) return false;
        // A relative --cwd is resolved, never a project called ".".
        if (Build("send", "--title", "x", "--cwd", ".").Source?.Project == ".") return false;

        // Codex's argv-tail payload is recognised and mapped.
        var codex = ParseHookJson("{\"type\":\"agent-turn-complete\",\"turn-id\":\"7\",\"last-assistant-message\":\"done\"}", null);
        if (codex?.Level != "success") return false;
        if (codex.Source?.Id != PayloadMapper.SourceCodex) return false;

        // Claude's stdin payload is recognised and mapped.
        var claude = ParseHookJson("{\"session_id\":\"s\",\"hook_event_name\":\"Notification\",\"notification_type\":\"agent_needs_input\"}", null);
        if (claude?.Level != "attention") return false;

        // Broken JSON is a null, not a crash.
        if (ParseHookJson("{ broken", null) is not null) return false;

        if (!TryParseDuration("30", out var m) || m != 30) return false;
        if (!TryParseDuration("30m", out m) || m != 30) return false;
        if (!TryParseDuration("2h", out m) || m != 120) return false;
        if (TryParseDuration("off", out _)) return false;
        if (TryParseDuration("", out _)) return false;
        if (TryParseDuration("-5", out _)) return false;

        return true;
    }

    private static bool DescribeSelfTest()
    {
        var sent = Describe(new NotifyResponse
        {
            Accepted = true,
            Level = "success",
            Deliveries = new List<DeliveryResult>
            {
                new() { Channel = "windows_sound", Status = "delivered" },
                new() { Channel = "tray_balloon", Status = "delivered" }
            }
        });
        if (sent != "sent [success]: sound delivered, notification delivered") return false;

        var held = Describe(new NotifyResponse { Accepted = false, SuppressedReason = "muted" });
        if (held != "not sent: muted") return false;
        return true;
    }
}
