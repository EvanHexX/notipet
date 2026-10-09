using System;
using System.Collections.Generic;

namespace Notipet.Shared;

// Turns an agent's hook payload into a notipet NotifyRequest.
//
// Pure and dependency-free so both the daemon (for the /hooks/* routes) and the
// CLI (for stdin/argv payloads) run the exact same mapping, and so the self-test
// can assert it headlessly.
//
// Guiding rule: never throw and never reject. These payload shapes are external
// contracts owned by Claude Code and Codex; when one changes, notipet must still
// make a noise and say what it saw, not fall over.
public static class PayloadMapper
{
    public const string SourceClaude = "claude-code";
    public const string SourceCodex = "codex";
    public const string SourceManual = "manual";

    public static string LabelFor(string sourceId) => sourceId switch
    {
        SourceClaude => "Claude Code",
        SourceCodex => "Codex",
        // The product's display name; the command and paths stay lower case.
        _ => "Notipet"
    };

    public static NotifyRequest FromHookEvent(AgentHookEvent e, string? sourceHint = null)
    {
        var sourceId = string.IsNullOrWhiteSpace(sourceHint) ? InferSource(e) : AgentIdentity.NormalizeAgent(sourceHint);
        var eventName = e.HookEventName ?? "";
        var level = LevelForHook(eventName, e.NotificationType);

        var body = FirstNonEmpty(
            e.Message,
            DescribeNotificationType(e.NotificationType, e.ToolName),
            Summarize(e.LastAssistantMessage),
            DescribeStop(eventName, e.StopReason));

        // An event we do not recognise still deserves a sound; say which one it
        // was so an unhandled new event is visible instead of silent.
        if (string.IsNullOrWhiteSpace(body))
        {
            body = string.IsNullOrWhiteSpace(eventName) ? "Agent event" : eventName;
        }

        var tag = BuildTag(sourceId, e.SessionId, eventName, e.NotificationType);

        return new NotifyRequest
        {
            Title = BuildTitle(sourceId, e.Cwd),
            Body = body,
            Level = NotificationLevelParser.ToWire(level),
            Tag = tag,
            Collapse = true,
            // Project is left to the CLI (which can look for the repository
            // root on disk) or to the daemon's pure fallback from cwd.
            Source = new SourceInfo
            {
                Id = sourceId,
                Label = LabelFor(sourceId),
                Session = e.SessionId,
                Cwd = e.Cwd,
                ThreadTitle = AgentIdentity.Clean(e.SessionTitle, AgentIdentity.MaxThreadTitle)
            }
        };
    }

    // What one hook event asks of notipet: a notification to show, the earlier
    // moments it ends, or both. The caller resolves first and notifies second,
    // so a turn's own "finished" is never resolved by the event that raised it.
    //
    // Only moments that are over by definition are ended - never "everything
    // from this thread". A prompt the agent stopped on mid-turn is over once
    // the turn has ended; "the turn finished" and "idle for a minute" are over
    // once a new prompt starts a turn. What an agent sent through the skill is
    // ended only by the agent itself (`notipet resolve`): a hook cannot tell
    // whether the user has seen it.
    public static HookPlan PlanForHookEvent(AgentHookEvent e, string? sourceHint = null)
    {
        var request = FromHookEvent(e, sourceHint);
        var sourceId = request.Source?.Id ?? SourceManual;
        var eventName = e.HookEventName?.Trim() ?? "";
        var session = string.IsNullOrWhiteSpace(e.SessionId) ? null : e.SessionId.Trim();

        List<string>? ends = null;
        if (session is not null)
        {
            switch (eventName.ToLowerInvariant())
            {
                case "stop":
                case "stopfailure":
                    ends = PromptTags(sourceId, session);
                    break;
                case "sessionend":
                    ends = PromptTags(sourceId, session);
                    ends.AddRange(IdleTags(sourceId, session));
                    break;
                // Fires on scheduled tasks, background-agent reports and
                // messages from other sessions too, not only on what the user
                // typed - so it ends idleness, which any new turn does, and
                // nothing that says the user has seen something.
                case "userpromptsubmit":
                    ends = IdleTags(sourceId, session);
                    break;
            }
        }

        return new HookPlan
        {
            // A new prompt is not news: the user (or their schedule) just did it.
            Notify = eventName.Equals("UserPromptSubmit", StringComparison.OrdinalIgnoreCase) ? null : request,
            Resolve = ends is null ? null : new ResolveRequest { Source = sourceId, Session = session, Tags = ends }
        };
    }

    // Prompts the agent stops on in the middle of a turn: a permission dialog
    // (Claude's Notification, Codex's PermissionRequest) or an MCP form.
    private static List<string> PromptTags(string sourceId, string session) => new()
    {
        BuildTag(sourceId, session, "Notification", "permission_prompt"),
        BuildTag(sourceId, session, "Notification", "elicitation_dialog"),
        BuildTag(sourceId, session, "Notification", "elicitation_url_dialog"),
        BuildTag(sourceId, session, "PermissionRequest", null)
    };

    // Waiting since the turn ended: the turn-finished notice and Claude's
    // one-minute idle reminder.
    private static List<string> IdleTags(string sourceId, string session) => new()
    {
        BuildTag(sourceId, session, "Stop", null),
        BuildTag(sourceId, session, "Notification", "idle_prompt")
    };

    public static NotifyRequest FromCodexNotify(CodexNotifyEvent e)
    {
        // `notify` only ever fires agent-turn-complete; anything else is new and
        // should still get through as plain info.
        var level = string.Equals(e.Type, "agent-turn-complete", StringComparison.OrdinalIgnoreCase)
            ? NotificationLevel.Success
            : NotificationLevel.Info;

        var body = FirstNonEmpty(Summarize(e.LastAssistantMessage), e.Type, "Turn complete");

        return new NotifyRequest
        {
            Title = BuildTitle(SourceCodex, e.Cwd),
            Body = body,
            Level = NotificationLevelParser.ToWire(level),
            Tag = BuildTag(SourceCodex, e.ThreadId ?? e.TurnId, e.Type, null),
            Collapse = true,
            // The thread is thread-id only. A turn id changes every turn: used
            // as a thread it would split one conversation into a card per turn
            // and build a deep link to nothing. (The tag above keeps its old
            // fallback so dedupe keys do not change between versions.)
            Source = new SourceInfo
            {
                Id = SourceCodex,
                Label = LabelFor(SourceCodex),
                Session = e.ThreadId,
                Cwd = e.Cwd,
                Client = AgentIdentity.Clean(e.Client, AgentIdentity.MaxClient)
            }
        };
    }

    // The fallback used when a Codex `notify` payload cannot even be parsed --
    // openai/codex#25141 lets input-messages grow until it blows the Windows
    // command-line limit, so an unparseable argv is an expected case, not a bug.
    public static NotifyRequest GenericFallback(string sourceId, string reason)
    {
        return new NotifyRequest
        {
            Title = LabelFor(sourceId),
            Body = reason,
            Level = NotificationLevelParser.ToWire(NotificationLevel.Info),
            Tag = sourceId + ":fallback",
            Collapse = true,
            Source = new SourceInfo { Id = sourceId, Label = LabelFor(sourceId) }
        };
    }

    public static NotificationLevel LevelForHook(string? hookEventName, string? notificationType)
    {
        // notification_type is the more specific signal, so it wins.
        switch (notificationType?.Trim().ToLowerInvariant())
        {
            case "agent_needs_input":
            case "permission_prompt":
            case "idle_prompt":
            case "elicitation_dialog":
            case "elicitation_url_dialog":
                return NotificationLevel.Attention;
            case "agent_completed":
                return NotificationLevel.Success;
        }

        return hookEventName?.Trim().ToLowerInvariant() switch
        {
            "stop" => NotificationLevel.Success,
            "subagentstop" => NotificationLevel.Info,
            "permissionrequest" => NotificationLevel.Attention,
            "stopfailure" => NotificationLevel.Error,
            "sessionend" => NotificationLevel.Info,
            "notification" => NotificationLevel.Attention,
            _ => NotificationLevel.Info
        };
    }

    private static string InferSource(AgentHookEvent e)
    {
        // Agent-only fields first, then a path sniff, then a neutral default.
        if (!string.IsNullOrEmpty(e.NotificationType)) return SourceClaude;
        if (!string.IsNullOrEmpty(e.PromptId)) return SourceClaude;
        if (!string.IsNullOrEmpty(e.SessionTitle)) return SourceClaude;
        if (!string.IsNullOrEmpty(e.TurnId)) return SourceCodex;

        var path = e.TranscriptPath ?? "";
        if (path.IndexOf(".claude", StringComparison.OrdinalIgnoreCase) >= 0) return SourceClaude;
        if (path.IndexOf(".codex", StringComparison.OrdinalIgnoreCase) >= 0) return SourceCodex;

        return SourceManual;
    }

    // "Claude Code - notipet": the repo name matters as much as the agent name
    // when three sessions are running at once.
    private static string BuildTitle(string sourceId, string? cwd)
    {
        var label = LabelFor(sourceId);
        var project = LastSegment(cwd);
        return string.IsNullOrEmpty(project) ? label : label + " - " + project;
    }

    public static string LastSegment(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        var trimmed = path.TrimEnd('\\', '/');
        var index = trimmed.LastIndexOfAny(new[] { '\\', '/' });
        return index >= 0 && index < trimmed.Length - 1 ? trimmed[(index + 1)..] : trimmed;
    }

    private static string BuildTag(string sourceId, string? session, string? eventName, string? notificationType)
    {
        var parts = new List<string> { sourceId };
        // Plain truncation, not the ellipsis kind: a tag is a dedupe key, not
        // display text, and a stray character would split it into two keys.
        if (!string.IsNullOrWhiteSpace(session)) parts.Add(Truncate(session!, 12));
        if (!string.IsNullOrWhiteSpace(eventName)) parts.Add(eventName!);
        if (!string.IsNullOrWhiteSpace(notificationType)) parts.Add(notificationType!);
        return string.Join(":", parts);
    }

    private static string DescribeNotificationType(string? notificationType, string? toolName)
    {
        var tool = string.IsNullOrWhiteSpace(toolName) ? "" : " (" + toolName + ")";
        return notificationType?.Trim().ToLowerInvariant() switch
        {
            "agent_needs_input" => "Waiting for your input" + tool,
            "permission_prompt" => "Permission needed" + tool,
            "idle_prompt" => "Idle, waiting for you",
            "agent_completed" => "Finished",
            "elicitation_dialog" or "elicitation_url_dialog" => "Needs a response" + tool,
            _ => ""
        };
    }

    private static string DescribeStop(string? eventName, string? stopReason)
    {
        if (string.IsNullOrWhiteSpace(eventName)) return "";
        if (eventName.Equals("Stop", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(stopReason) ? "Turn complete" : "Turn complete (" + stopReason + ")";
        }
        if (eventName.Equals("SubagentStop", StringComparison.OrdinalIgnoreCase)) return "Subagent finished";
        if (eventName.Equals("SessionEnd", StringComparison.OrdinalIgnoreCase)) return "Session ended";
        if (eventName.Equals("PermissionRequest", StringComparison.OrdinalIgnoreCase)) return "Permission needed";
        return "";
    }

    // The balloon shows 255 characters at most, so a long final message is
    // collapsed to its first line rather than truncated mid-sentence.
    public static string Summarize(string? text, int max = 180)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var flat = text.Replace("\r", " ").Replace("\n", " ").Trim();
        while (flat.Contains("  ", StringComparison.Ordinal)) flat = flat.Replace("  ", " ");
        return Shorten(flat, max);
    }

    public static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    public static string Shorten(string value, int max)
    {
        if (value.Length <= max) return value;
        return max <= 1 ? value[..max] : value[..(max - 1)] + "\u2026";
    }

    private static string FirstNonEmpty(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate)) return candidate!.Trim();
        }
        return "";
    }

    public static bool RunSelfTest()
    {
        // Claude permission prompt: the highest-value case.
        var claude = new AgentHookEvent
        {
            SessionId = "8f3a1234567890",
            HookEventName = "Notification",
            NotificationType = "agent_needs_input",
            Cwd = @"C:\src\notipet",
            ToolName = "Bash"
        };
        var r = FromHookEvent(claude);
        if (r.Level != "attention") return false;
        if (r.Title != "Claude Code - notipet") return false;
        if (r.Source?.Id != SourceClaude) return false;
        if (r.Tag != "claude-code:8f3a12345678:Notification:agent_needs_input") return false;
        if (!r.Body!.StartsWith("Waiting for your input", StringComparison.Ordinal)) return false;

        // Claude turn complete.
        var stop = new AgentHookEvent
        {
            SessionId = "s1",
            PromptId = "p1",
            HookEventName = "Stop",
            StopReason = "end_turn",
            LastAssistantMessage = "All 12 tests pass.",
            Cwd = "/home/u/proj"
        };
        var rs = FromHookEvent(stop);
        if (rs.Level != "success") return false;
        if (rs.Body != "All 12 tests pass.") return false;
        if (rs.Title != "Claude Code - proj") return false;

        // An event name nobody has taught us about must still alert, carrying
        // the raw name so the gap is visible.
        var unknown = new AgentHookEvent { HookEventName = "BrandNewEvent", SessionId = "x" };
        var ru = FromHookEvent(unknown, SourceClaude);
        if (ru.Level != "info") return false;
        if (ru.Body != "BrandNewEvent") return false;

        // A completely empty payload must not produce an empty notification.
        var empty = FromHookEvent(new AgentHookEvent(), SourceCodex);
        if (string.IsNullOrWhiteSpace(empty.Body)) return false;
        if (empty.Title != "Codex") return false;

        // Codex legacy notify.
        var codex = new CodexNotifyEvent
        {
            Type = "agent-turn-complete",
            ThreadId = "b5f6c1c2",
            Cwd = "/Users/example/project",
            LastAssistantMessage = "Rename complete\nand verified."
        };
        var rc = FromCodexNotify(codex);
        if (rc.Level != "success") return false;
        if (rc.Source?.Id != SourceCodex) return false;
        if (rc.Title != "Codex - project") return false;
        if (rc.Body != "Rename complete and verified.") return false;
        if (rc.Source?.Session != "b5f6c1c2") return false;

        // A turn id is not a thread: without thread-id there is no thread.
        var turnOnly = FromCodexNotify(new CodexNotifyEvent { Type = "agent-turn-complete", TurnId = "7", Client = "codex-tui" });
        if (turnOnly.Source?.Session is not null) return false;
        if (turnOnly.Source?.Client != "codex-tui") return false;

        // Hooks carry the session as the thread, and a custom session title
        // when Claude sends one. Project is not decided here.
        if (r.Source?.Session != "8f3a1234567890") return false;
        if (r.Source?.Project is not null) return false;
        var titled = FromHookEvent(new AgentHookEvent { HookEventName = "UserPromptSubmit", SessionTitle = " login\nfix " });
        if (titled.Source?.ThreadTitle != "login fix") return false;
        if (titled.Source?.Id != SourceClaude) return false;

        // A hint is normalised, so `--source claude` is the same agent.
        if (FromHookEvent(new AgentHookEvent { HookEventName = "Stop" }, "Claude").Source?.Id != SourceClaude) return false;
        // turn_id only appears on Codex hooks.
        if (InferSource(new AgentHookEvent { HookEventName = "Stop", TurnId = "t1" }) != SourceCodex) return false;

        // Codex hooks (stdin) reach the same mapper as Claude's.
        var codexHook = new AgentHookEvent { HookEventName = "PermissionRequest", ToolName = "shell", SessionId = "c1" };
        if (FromHookEvent(codexHook, SourceCodex).Level != "attention") return false;

        // Source inference without an explicit hint.
        if (InferSource(new AgentHookEvent { TranscriptPath = @"C:\Users\u\.codex\sessions\a.jsonl" }) != SourceCodex) return false;
        if (InferSource(new AgentHookEvent { NotificationType = "idle_prompt" }) != SourceClaude) return false;

        if (LastSegment(@"C:\src\notipet\") != "notipet") return false;
        if (LastSegment(null) != "") return false;
        if (Shorten("abcdef", 3) != "ab\u2026") return false;

        if (GenericFallback(SourceCodex, "Codex turn complete").Level != "info") return false;

        return PlansEndOnlyWhatIsOver();
    }

    private static bool PlansEndOnlyWhatIsOver()
    {
        const string session = "8f3a1234567890";
        var permission = FromHookEvent(new AgentHookEvent
        {
            SessionId = session, HookEventName = "Notification", NotificationType = "permission_prompt"
        }, SourceClaude);
        var idle = FromHookEvent(new AgentHookEvent
        {
            SessionId = session, HookEventName = "Notification", NotificationType = "idle_prompt"
        }, SourceClaude);

        // A prompt raises an alert and ends nothing.
        var prompted = PlanForHookEvent(new AgentHookEvent
        {
            SessionId = session, HookEventName = "Notification", NotificationType = "permission_prompt"
        }, SourceClaude);
        if (prompted.Notify is null || prompted.Resolve is not null) return false;

        // The end of the turn ends that prompt - by the exact tag it was
        // raised with, in that thread - and still announces itself.
        var stop = PlanForHookEvent(new AgentHookEvent { SessionId = session, HookEventName = "Stop" }, SourceClaude);
        if (stop.Notify is null || stop.Resolve is null) return false;
        if (stop.Resolve.Session != session || stop.Resolve.Source != SourceClaude) return false;
        if (!stop.Resolve.Tags!.Contains(permission.Tag!)) return false;
        // ...but not the idle reminder, and never its own notice.
        if (stop.Resolve.Tags.Contains(idle.Tag!) || stop.Resolve.Tags.Contains(stop.Notify.Tag!)) return false;

        // A new prompt ends the idle reminder and the last turn's notice, not
        // a prompt, and is not itself a notification.
        var next = PlanForHookEvent(new AgentHookEvent { SessionId = session, HookEventName = "UserPromptSubmit" }, SourceClaude);
        if (next.Notify is not null || next.Resolve is null) return false;
        if (!next.Resolve.Tags!.Contains(idle.Tag!) || !next.Resolve.Tags.Contains(stop.Notify.Tag!)) return false;
        if (next.Resolve.Tags.Contains(permission.Tag!)) return false;

        // Codex: Stop ends its PermissionRequest.
        var codexPermission = FromHookEvent(new AgentHookEvent { SessionId = "c1", HookEventName = "PermissionRequest" }, SourceCodex);
        var codexStop = PlanForHookEvent(new AgentHookEvent { SessionId = "c1", HookEventName = "Stop", TurnId = "t1" }, SourceCodex);
        if (codexStop.Resolve is null || !codexStop.Resolve.Tags!.Contains(codexPermission.Tag!)) return false;

        // Without a thread there is nothing safe to end.
        if (PlanForHookEvent(new AgentHookEvent { HookEventName = "Stop" }, SourceClaude).Resolve is not null) return false;
        // An unknown event still alerts and ends nothing.
        var unknown = PlanForHookEvent(new AgentHookEvent { SessionId = session, HookEventName = "BrandNewEvent" }, SourceClaude);
        return unknown.Notify is not null && unknown.Resolve is null;
    }
}

// See PayloadMapper.PlanForHookEvent.
public sealed class HookPlan
{
    public NotifyRequest? Notify { get; init; }
    public ResolveRequest? Resolve { get; init; }
}
