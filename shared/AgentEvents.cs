using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Notipet.Shared;

// Claude Code hooks and Codex hooks both deliver a single JSON object on stdin
// with snake_case keys. The union of both shapes fits in one tolerant DTO:
// every field is optional, because these are external contracts that drift and
// an unknown shape must degrade to a generic alert rather than crash.
public sealed class AgentHookEvent
{
    [JsonPropertyName("session_id")] public string? SessionId { get; set; }
    [JsonPropertyName("prompt_id")] public string? PromptId { get; set; }
    [JsonPropertyName("transcript_path")] public string? TranscriptPath { get; set; }
    [JsonPropertyName("cwd")] public string? Cwd { get; set; }
    [JsonPropertyName("permission_mode")] public string? PermissionMode { get; set; }
    [JsonPropertyName("hook_event_name")] public string? HookEventName { get; set; }
    [JsonPropertyName("model")] public string? Model { get; set; }
    // Codex only, on turn-scoped events. Claude never sends it, which makes it
    // a clean way to tell the two apart.
    [JsonPropertyName("turn_id")] public string? TurnId { get; set; }
    // Claude, SessionStart / UserPromptSubmit only, and only for a custom title.
    [JsonPropertyName("session_title")] public string? SessionTitle { get; set; }

    // Notification event
    [JsonPropertyName("notification_type")] public string? NotificationType { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }

    // Stop / SubagentStop
    [JsonPropertyName("stop_reason")] public string? StopReason { get; set; }
    [JsonPropertyName("last_assistant_message")] public string? LastAssistantMessage { get; set; }
    [JsonPropertyName("agent_id")] public string? AgentId { get; set; }
    [JsonPropertyName("agent_type")] public string? AgentType { get; set; }

    // Tool events (Codex PreToolUse / PermissionRequest)
    [JsonPropertyName("tool_name")] public string? ToolName { get; set; }
}

// Codex's legacy `notify` config spawns a program with the payload as the FINAL
// argv element, and its keys are kebab-case rather than snake_case.
public sealed class CodexNotifyEvent
{
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("thread-id")] public string? ThreadId { get; set; }
    [JsonPropertyName("turn-id")] public string? TurnId { get; set; }
    [JsonPropertyName("cwd")] public string? Cwd { get; set; }
    [JsonPropertyName("client")] public string? Client { get; set; }
    [JsonPropertyName("input-messages")] public List<string>? InputMessages { get; set; }
    [JsonPropertyName("last-assistant-message")] public string? LastAssistantMessage { get; set; }
}
