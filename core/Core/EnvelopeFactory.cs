using System;
using System.Collections.Generic;
using System.Linq;
using Notipet.Shared;

namespace Notipet.Core;

// Turns a wire NotifyRequest into a validated envelope.
//
// Policy: over-long values are truncated and reported in warnings rather than
// rejected, and an unknown level degrades to info. A caller that sends slightly
// wrong input should still get a notification - the alternative is an agent hook
// that silently stops working after a payload shape changes.
internal static class EnvelopeFactory
{
    public const int MaxTitle = 200;
    public const int MaxBody = 2000;
    public const int MaxTag = 128;
    public const int MaxChannels = 8;

    // allowedSchemes: settings' links.allowedSchemes, for `open`. Without it
    // only http(s) on this PC is accepted.
    public static bool TryCreate(
        NotifyRequest request,
        string? sourceHintId,
        out NotificationEnvelope? envelope,
        out string? validationError,
        IReadOnlyCollection<string>? allowedSchemes = null)
    {
        envelope = null;
        validationError = null;

        var title = (request.Title ?? "").Trim();
        var body = (request.Body ?? "").Trim();

        // The one genuine hard requirement: a notification with no text is not
        // a notification.
        if (title.Length == 0 && body.Length == 0)
        {
            validationError = "either title or body is required";
            return false;
        }

        var warnings = new List<string>();

        if (title.Length > MaxTitle)
        {
            title = title[..MaxTitle];
            warnings.Add("title truncated");
        }
        if (body.Length > MaxBody)
        {
            body = body[..MaxBody];
            warnings.Add("body truncated");
        }

        var tag = NormalizeTag(request.Tag);
        if (request.Tag?.Trim() is { Length: > MaxTag }) warnings.Add("tag truncated");

        var level = NotificationLevelParser.Parse(request.Level, out var recognized);
        if (!recognized) warnings.Add($"unknown level '{request.Level}', treated as info");

        // When no title was given, the source name is a better balloon header
        // than an empty one. One spelling per agent: `claude` from an LLM and
        // `claude-code` from a hook are the same sender.
        var sourceId = AgentIdentity.NormalizeAgent(FirstNonEmpty(request.Source?.Id, sourceHintId, PayloadMapper.SourceManual));
        if (title.Length == 0) title = PayloadMapper.LabelFor(sourceId);

        // Who and where. None of these can fail the request: a missing or odd
        // value is dropped (with a warning) and the card lands under "Other".
        var source = request.Source;
        var project = CleanBounded(source?.Project, AgentIdentity.MaxProject, "project", warnings);
        // The pure fallback only: the daemon never walks the disk on the way
        // to the speaker. The CLI resolves the repository root before sending.
        project ??= AgentIdentity.ProjectFromPath(source?.Cwd);

        var thread = source?.Session?.Trim();
        if (string.IsNullOrEmpty(thread)) thread = null;
        else if (!AgentIdentity.IsThreadId(thread))
        {
            // Never truncated or repaired: it goes into a URL.
            thread = null;
            warnings.Add("thread ignored: invalid id");
        }

        var hostSession = source?.HostSession?.Trim();
        if (string.IsNullOrEmpty(hostSession)) hostSession = null;
        else if (!AgentIdentity.IsClaudeHostSession(hostSession))
        {
            hostSession = null;
            warnings.Add("hostSession ignored: invalid id");
        }

        var threadTitle = CleanBounded(source?.ThreadTitle, AgentIdentity.MaxThreadTitle, "threadTitle", warnings);

        var channels = request.Channels?
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxChannels)
            .ToList();
        if (channels is { Count: 0 }) channels = null;

        var result = new NotificationEnvelope
        {
            Title = title,
            Body = body,
            Level = level,
            Tag = tag,
            Collapse = request.Collapse ?? true,
            SourceId = sourceId,
            SourceLabel = request.Source?.Label ?? PayloadMapper.LabelFor(sourceId),
            SourceSession = thread,
            SourceCwd = request.Source?.Cwd,
            Project = project,
            ThreadTitle = threadTitle,
            HostSession = hostSession,
            Client = AgentIdentity.Clean(source?.Client, AgentIdentity.MaxClient),
            OpenUri = OpenLinks.Accept(request.Open, allowedSchemes, warnings),
            Sound = request.Sound,
            RequestedChannels = channels,
            TtlSec = request.TtlSec
        };
        result.Warnings.AddRange(warnings);

        envelope = result;
        return true;
    }

    // One line of display text, shortened with an ellipsis (and a warning) when
    // it is over the limit. Null when there is nothing left to show.
    // A tag as the envelope keeps it. Also what a resolve request is matched
    // with, so a tag that was trimmed or cut on the way in still matches.
    public static string? NormalizeTag(string? tag)
    {
        var trimmed = tag?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        return trimmed.Length > MaxTag ? trimmed[..MaxTag] : trimmed;
    }

    private static string? CleanBounded(string? value, int max, string name, List<string> warnings)
    {
        var flat = AgentIdentity.Clean(value, int.MaxValue);
        if (flat is null || flat.Length <= max) return flat;
        warnings.Add($"{name} shortened");
        return PayloadMapper.Shorten(flat, max);
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value)) return value!.Trim();
        }
        return PayloadMapper.SourceManual;
    }

    public static bool RunSelfTest()
    {
        // Empty payload is the one rejection.
        if (TryCreate(new NotifyRequest(), null, out _, out var error)) return false;
        if (error is null) return false;

        // Body-only is fine and the title falls back to the source label.
        if (!TryCreate(new NotifyRequest { Body = "done", Source = new SourceInfo { Id = "codex" } },
                null, out var bodyOnly, out _)) return false;
        if (bodyOnly!.Title != "Codex") return false;

        // Over-long values are truncated with a warning, not rejected.
        var long_ = new NotifyRequest
        {
            Title = new string('t', MaxTitle + 50),
            Body = new string('b', MaxBody + 50),
            Tag = new string('g', MaxTag + 50)
        };
        if (!TryCreate(long_, null, out var truncated, out _)) return false;
        if (truncated!.Title.Length != MaxTitle) return false;
        if (truncated.Body.Length != MaxBody) return false;
        if (truncated.Tag!.Length != MaxTag) return false;
        if (truncated.Warnings.Count != 3) return false;

        // An unknown level degrades to info and says so.
        if (!TryCreate(new NotifyRequest { Title = "x", Level = "screaming" }, null, out var odd, out _)) return false;
        if (odd!.Level != NotificationLevel.Info) return false;
        if (!odd.Warnings.Any(w => w.Contains("unknown level", StringComparison.Ordinal))) return false;

        // The source hint is used only when the request does not carry one.
        if (!TryCreate(new NotifyRequest { Title = "x" }, "claude-code", out var hinted, out _)) return false;
        if (hinted!.SourceId != "claude-code") return false;
        if (!TryCreate(new NotifyRequest { Title = "x", Source = new SourceInfo { Id = "codex" } },
                "claude-code", out var explicitSource, out _)) return false;
        if (explicitSource!.SourceId != "codex") return false;

        // Channel lists are de-duplicated and capped.
        var many = new NotifyRequest
        {
            Title = "x",
            Channels = Enumerable.Range(0, 20).Select(i => "c" + i).Concat(new[] { "c0", "C0" }).ToList()
        };
        if (!TryCreate(many, null, out var capped, out _)) return false;
        if (capped!.RequestedChannels!.Count != MaxChannels) return false;

        // An expired TTL is detectable.
        if (!TryCreate(new NotifyRequest { Title = "x", TtlSec = 0 }, null, out var noTtl, out _)) return false;
        if (noTtl!.IsExpired) return false;

        // --- who and where ---

        // Nothing about the sender: still a notification, filed under Other.
        if (!TryCreate(new NotifyRequest { Title = "bare" }, null, out var bare, out _)) return false;
        if (bare!.Project is not null || bare.SourceSession is not null || bare.ThreadTitle is not null) return false;
        if (bare.Warnings.Count != 0) return false;

        // Project falls back to the cwd; an explicit one wins.
        if (!TryCreate(new NotifyRequest { Title = "x", Source = new SourceInfo { Cwd = @"C:\src\notipet" } },
                null, out var fromCwd, out _)) return false;
        if (fromCwd!.Project != "notipet") return false;
        if (!TryCreate(new NotifyRequest { Title = "x", Source = new SourceInfo { Cwd = @"C:\src\notipet", Project = "Notipet App" } },
                null, out var named, out _)) return false;
        if (named!.Project != "Notipet App") return false;

        // Display text is flattened and bounded, with a warning when shortened.
        var wordy = new NotifyRequest
        {
            Title = "x",
            Source = new SourceInfo { Project = new string('p', 300), ThreadTitle = "fix\nlogin  " + new string('t', 300) }
        };
        if (!TryCreate(wordy, null, out var bounded, out _)) return false;
        if (bounded!.Project!.Length != AgentIdentity.MaxProject) return false;
        if (bounded.ThreadTitle!.Length != AgentIdentity.MaxThreadTitle) return false;
        if (!bounded.ThreadTitle.StartsWith("fix login ", StringComparison.Ordinal)) return false;
        if (bounded.Warnings.Count != 2) return false;

        // A thread id that could change a URL is dropped, never repaired.
        foreach (var bad in new[] { "a/b", "x?prompt=hi", "a b", "a&b" })
        {
            if (!TryCreate(new NotifyRequest { Title = "x", Source = new SourceInfo { Session = bad } }, null, out var dropped, out _)) return false;
            if (dropped!.SourceSession is not null) return false;
            if (!dropped.Warnings.Any(w => w.StartsWith("thread ignored", StringComparison.Ordinal))) return false;
        }
        var uuid = "019a2b3c-4d5e-7f00-8a9b-0c1d2e3f4a5b";
        if (!TryCreate(new NotifyRequest { Title = "x", Source = new SourceInfo { Session = uuid } }, null, out var kept, out _)) return false;
        if (kept!.SourceSession != uuid) return false;

        // Only a well-formed Claude Desktop id survives as a host session.
        if (!TryCreate(new NotifyRequest { Title = "x", Source = new SourceInfo { HostSession = "local_abc-123" } }, null, out var host, out _)) return false;
        if (host!.HostSession != "local_abc-123") return false;
        if (!TryCreate(new NotifyRequest { Title = "x", Source = new SourceInfo { HostSession = "local_../x" } }, null, out var badHost, out _)) return false;
        if (badHost!.HostSession is not null) return false;

        // `claude` from an LLM is the same agent as `claude-code` from a hook.
        if (!TryCreate(new NotifyRequest { Title = "x", Source = new SourceInfo { Id = "Claude" } }, null, out var spelled, out _)) return false;
        if (spelled!.SourceId != PayloadMapper.SourceClaude) return false;

        // Untagged twins from different projects or threads stay separate;
        // within the same one they still collapse.
        NotificationEnvelope Twin(string? project, string? thread) => new() { Title = "t", Body = "b", SourceId = "codex", Project = project, SourceSession = thread };
        if (Twin("a", "1").DedupeKey == Twin("b", "1").DedupeKey) return false;
        if (Twin("a", "1").DedupeKey == Twin("a", "2").DedupeKey) return false;
        if (Twin("a", "1").DedupeKey != Twin("a", "1").DedupeKey) return false;

        // A shared tag still collapses within one thread, but two threads using
        // the same tag ("shop:needs-input") are two notifications.
        NotificationEnvelope Tagged(string? thread) => new() { Title = "t", Tag = "shop:needs-input", SourceSession = thread };
        if (Tagged("1").DedupeKey != Tagged("1").DedupeKey) return false;
        if (Tagged("1").DedupeKey == Tagged("2").DedupeKey) return false;
        // No thread known: the tag alone, exactly as before.
        if (Tagged(null).DedupeKey != "shop:needs-input") return false;

        return true;
    }
}
