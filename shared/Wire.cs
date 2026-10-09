using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Notipet.Shared;

// The HTTP contract, shared verbatim by the daemon and the CLI so the two can
// never drift. Everything except the payload text is optional; the daemon fills
// gaps from settings and then from level defaults.

// Who sent a notification and from where. Every field is optional: a sender
// that knows nothing still gets its notification shown, under "Other".
public sealed class SourceInfo
{
    // The agent: claude-code | codex | manual | anything else. There is no
    // separate "agent" field - this one already keys rate limits and settings.
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("label")] public string? Label { get; set; }
    // The thread (Claude session id / Codex thread id). Validated, never
    // truncated: it ends up inside a deep link.
    [JsonPropertyName("session")] public string? Session { get; set; }
    [JsonPropertyName("cwd")] public string? Cwd { get; set; }
    [JsonPropertyName("pid")] public int? Pid { get; set; }
    // Display name the recent window groups by. When missing, the daemon
    // derives it from cwd.
    [JsonPropertyName("project")] public string? Project { get; set; }
    // A short, stable name for the thread ("fix login redirect").
    [JsonPropertyName("threadTitle")] public string? ThreadTitle { get; set; }
    // Claude Desktop's own id for the session (local_...), which is what its
    // claude:// deep link accepts. Not the same as the CLI session id.
    [JsonPropertyName("hostSession")] public string? HostSession { get; set; }
    // Which front end ran the agent: claude-desktop, cli, codex-tui, ...
    [JsonPropertyName("client")] public string? Client { get; set; }
}

public sealed class SoundSpec
{
    // A friendly key into settings.sound.library.
    [JsonPropertyName("name")] public string? Name { get; set; }
    // A Windows AppEvents alias, e.g. "Notification.Default".
    [JsonPropertyName("alias")] public string? Alias { get; set; }
    // An absolute path; rejected unless it resolves under an allowed root.
    [JsonPropertyName("file")] public string? File { get; set; }
    [JsonPropertyName("volume")] public double? Volume { get; set; }
    // once | repeat | until_ack
    [JsonPropertyName("repeat")] public string? Repeat { get; set; }
    [JsonPropertyName("repeatCount")] public int? RepeatCount { get; set; }
    [JsonPropertyName("intervalMs")] public int? IntervalMs { get; set; }
    [JsonPropertyName("maxDurationSec")] public int? MaxDurationSec { get; set; }
    [JsonPropertyName("mute")] public bool? Mute { get; set; }
}

public sealed class NotifyRequest
{
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("body")] public string? Body { get; set; }
    [JsonPropertyName("level")] public string? Level { get; set; }
    [JsonPropertyName("tag")] public string? Tag { get; set; }
    [JsonPropertyName("collapse")] public bool? Collapse { get; set; }
    [JsonPropertyName("source")] public SourceInfo? Source { get; set; }
    [JsonPropertyName("sound")] public SoundSpec? Sound { get; set; }
    [JsonPropertyName("channels")] public List<string>? Channels { get; set; }
    [JsonPropertyName("requiresAck")] public bool? RequiresAck { get; set; }
    [JsonPropertyName("ttlSec")] public int? TtlSec { get; set; }
    [JsonPropertyName("idempotencyKey")] public string? IdempotencyKey { get; set; }
    // A URI the card opens when clicked, instead of the agent's thread. Only
    // http(s) on this PC or a scheme the user listed in settings; anything
    // else is dropped with a warning. Never a command line.
    [JsonPropertyName("open")] public string? Open { get; set; }
}

public sealed class DeliveryResult
{
    [JsonPropertyName("channel")] public string Channel { get; set; } = "";
    // delivered | skipped | failed | disabled | pending
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("referenceId")] public string? ReferenceId { get; set; }
    [JsonPropertyName("detail")] public string? Detail { get; set; }
}

public sealed class NotifyResponse
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("accepted")] public bool Accepted { get; set; }
    [JsonPropertyName("suppressed")] public bool Suppressed { get; set; }
    [JsonPropertyName("suppressedReason")] public string? SuppressedReason { get; set; }
    [JsonPropertyName("collapsedWith")] public string? CollapsedWith { get; set; }
    [JsonPropertyName("nextAllowedAt")] public string? NextAllowedAt { get; set; }
    [JsonPropertyName("level")] public string? Level { get; set; }
    [JsonPropertyName("receivedAt")] public string? ReceivedAt { get; set; }
    [JsonPropertyName("warnings")] public List<string> Warnings { get; set; } = new();
    [JsonPropertyName("deliveries")] public List<DeliveryResult> Deliveries { get; set; } = new();
}

public sealed class ErrorResponse
{
    [JsonPropertyName("error")] public string Error { get; set; } = "";
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("field")] public string? Field { get; set; }
}

public sealed class AckRequest
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("tag")] public string? Tag { get; set; }
    [JsonPropertyName("all")] public bool? All { get; set; }
}

public sealed class AckResponse
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("stopped")] public int Stopped { get; set; }
}

// POST /v1/resolve - "the thing I alerted about is over". Stops that
// notification's alarm and closes its pop-up, and nothing else: unlike /v1/ack
// there is no "all", and a request with no selector is a 400. Selectors narrow
// together (tag AND thread AND agent); id alone picks one notification.
// Whatever already stopped or closed is skipped silently, so an agent can send
// this without knowing whether the user got there first.
public sealed class ResolveRequest
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("tag")] public string? Tag { get; set; }
    // Any of these tags. Hooks use it to name the exact moments they end.
    [JsonPropertyName("tags")] public List<string>? Tags { get; set; }
    // The agent (claude-code | codex | ...), as on the notification.
    [JsonPropertyName("source")] public string? Source { get; set; }
    // The thread, as SourceInfo.session on the notification.
    [JsonPropertyName("session")] public string? Session { get; set; }
}

public sealed class ResolveResponse
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    // Notifications that still had something live (an alarm, a pop-up).
    [JsonPropertyName("resolved")] public int Resolved { get; set; }
    [JsonPropertyName("alarmsStopped")] public int AlarmsStopped { get; set; }
    [JsonPropertyName("popupsClosed")] public int PopupsClosed { get; set; }
}

public sealed class MuteRequest
{
    [JsonPropertyName("muted")] public bool? Muted { get; set; }
    [JsonPropertyName("minutes")] public int? Minutes { get; set; }
}

public sealed class MuteResponse
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("muted")] public bool Muted { get; set; }
    [JsonPropertyName("until")] public string? Until { get; set; }
}

// POST /v1/presence. A null atDesk toggles, which is what a hotkey or a
// one-word CLI command wants.
public sealed class PresenceRequest
{
    [JsonPropertyName("atDesk")] public bool? AtDesk { get; set; }
}

public sealed class PresenceResponse
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("atDesk")] public bool AtDesk { get; set; }
}

public sealed class ClearHistoryResponse
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("cleared")] public int Cleared { get; set; }
}

public sealed class OkResponse
{
    [JsonPropertyName("ok")] public bool Ok { get; set; } = true;
    [JsonPropertyName("message")] public string? Message { get; set; }
}

public sealed class SoundEngineInfo
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("available")] public bool Available { get; set; }
    [JsonPropertyName("lastError")] public string? LastError { get; set; }
}

public sealed class ChannelInfo
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("ready")] public bool Ready { get; set; }
    [JsonPropertyName("lastError")] public string? LastError { get; set; }
}

public sealed class ChannelsResponse
{
    [JsonPropertyName("channels")] public List<ChannelInfo> Channels { get; set; } = new();
}

public sealed class HealthResponse
{
    [JsonPropertyName("ok")] public bool Ok { get; set; } = true;
    [JsonPropertyName("name")] public string Name { get; set; } = "notipet";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("instanceId")] public string InstanceId { get; set; } = "";
    // Everything below is only populated for an authenticated caller: an
    // unauthenticated probe must not leak state.
    [JsonPropertyName("pid")] public int? Pid { get; set; }
    [JsonPropertyName("port")] public int? Port { get; set; }
    [JsonPropertyName("startedAt")] public string? StartedAt { get; set; }
    [JsonPropertyName("uptimeSec")] public double? UptimeSec { get; set; }
    [JsonPropertyName("muted")] public bool? Muted { get; set; }
    [JsonPropertyName("atDesk")] public bool? AtDesk { get; set; }
    [JsonPropertyName("language")] public string? Language { get; set; }
    [JsonPropertyName("historyCount")] public int? HistoryCount { get; set; }
    [JsonPropertyName("quietHoursActive")] public bool? QuietHoursActive { get; set; }
    [JsonPropertyName("presence")] public string? Presence { get; set; }
    [JsonPropertyName("activeAlarms")] public int? ActiveAlarms { get; set; }
    [JsonPropertyName("soundEngine")] public SoundEngineInfo? SoundEngine { get; set; }
    [JsonPropertyName("warnings")] public List<string>? Warnings { get; set; }
}

public sealed class HistoryEntryDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("at")] public string At { get; set; } = "";
    [JsonPropertyName("level")] public string Level { get; set; } = "";
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("body")] public string? Body { get; set; }
    [JsonPropertyName("source")] public string? Source { get; set; }
    [JsonPropertyName("project")] public string? Project { get; set; }
    [JsonPropertyName("thread")] public string? Thread { get; set; }
    [JsonPropertyName("threadTitle")] public string? ThreadTitle { get; set; }
    [JsonPropertyName("tag")] public string? Tag { get; set; }
    [JsonPropertyName("count")] public int Count { get; set; }
    [JsonPropertyName("accepted")] public bool Accepted { get; set; }
    [JsonPropertyName("suppressedReason")] public string? SuppressedReason { get; set; }
    // When the sender said it was over (/v1/resolve) while its alarm or
    // pop-up was still live.
    [JsonPropertyName("resolvedAt")] public string? ResolvedAt { get; set; }
    // What a click on the card opens, when the sender gave one (send --open).
    [JsonPropertyName("open")] public string? Open { get; set; }
    [JsonPropertyName("deliveries")] public List<DeliveryResult> Deliveries { get; set; } = new();
}

public sealed class HistoryResponse
{
    [JsonPropertyName("entries")] public List<HistoryEntryDto> Entries { get; set; } = new();
}

// %LOCALAPPDATA%\notipet\runtime.json - written by the daemon after the
// listener is bound, read by the CLI to find the running instance.
public sealed class RuntimeInfo
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("instanceId")] public string InstanceId { get; set; } = "";
    [JsonPropertyName("pid")] public int Pid { get; set; }
    // Guards against PID reuse: a live process with a different start time is
    // not our daemon, it is whatever the OS handed that number to next.
    [JsonPropertyName("processStartTimeUtc")] public string ProcessStartTimeUtc { get; set; } = "";
    [JsonPropertyName("sessionId")] public int SessionId { get; set; }
    [JsonPropertyName("port")] public int Port { get; set; }
    [JsonPropertyName("baseUrl")] public string BaseUrl { get; set; } = "";
    [JsonPropertyName("token")] public string Token { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("exePath")] public string ExePath { get; set; } = "";
    [JsonPropertyName("startedAtUtc")] public string StartedAtUtc { get; set; } = "";
}
