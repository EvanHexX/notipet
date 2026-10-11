using System;
using System.Collections.Generic;
using System.Threading;
using Notipet.Shared;

namespace Notipet.Core;

// A validated notify request, ready for the rule engine and the channels.
// Distinct from the wire NotifyRequest so nothing downstream has to deal with
// nulls, over-long strings, or an unparsed level.
internal sealed class NotificationEnvelope
{
    private static int _sequence;

    public string Id { get; }
    public DateTimeOffset ReceivedAt { get; } = DateTimeOffset.Now;
    public string Title { get; init; } = "";
    public string Body { get; init; } = "";
    public NotificationLevel Level { get; init; }
    public string? Tag { get; init; }
    public bool Collapse { get; init; } = true;
    public string SourceId { get; init; } = PayloadMapper.SourceManual;
    public string? SourceLabel { get; init; }
    // The thread id, already validated (AgentIdentity.IsThreadId) - safe to put
    // in a deep link. Null when the sender did not say or said something odd.
    public string? SourceSession { get; init; }
    public string? SourceCwd { get; init; }
    // Display name the recent window groups by; null shows as "Other".
    public string? Project { get; init; }
    // What the sender called the thread. The window prefers the title the agent
    // app itself shows (HistoryEntry.AppThreadTitle) when one can be found.
    public string? ThreadTitle { get; init; }
    // Claude Desktop's local_ session id, validated; the claude:// link key.
    public string? HostSession { get; init; }
    public string? Client { get; init; }
    // What a click on the card opens instead of the thread; already checked
    // against OpenLinks when the notification arrived.
    public string? OpenUri { get; init; }
    public SoundSpec? Sound { get; init; }
    public IReadOnlyList<string>? RequestedChannels { get; init; }
    public int? TtlSec { get; init; }
    public List<string> Warnings { get; } = new();

    public NotificationEnvelope()
    {
        // Sortable and readable in a log, unique within a run.
        Id = $"ntp_{DateTime.UtcNow:yyyyMMddHHmmss}_{Interlocked.Increment(ref _sequence):x4}";
    }

    // Read back from history.json: the id and time it first arrived with.
    public NotificationEnvelope(string id, DateTimeOffset receivedAt)
    {
        Id = id;
        ReceivedAt = receivedAt;
    }

    // The dedupe key. A caller-supplied tag wins; otherwise the content itself
    // identifies the notification, so a hook that fires three times for the same
    // prompt still collapses even without a tag.
    //
    // Either way the thread is part of the key. Two conversations in one repo
    // that both follow the skill's `<repo>:needs-input` tag would otherwise
    // collapse into one card: the second makes no sound, and the card's link
    // opens the first. Hook tags already carry the session, so for them this
    // changes nothing. Without a tag the project counts too, so two sessions
    // saying "Tests passed" stay two cards in their own groups.
    public string DedupeKey => !string.IsNullOrWhiteSpace(Tag)
        ? SourceSession is null ? Tag! : $"{Tag}|{SourceSession}"
        : $"{SourceId}|{Project}|{SourceSession}|{Title}|{Body}";

    public bool IsExpired => TtlSec is > 0 && DateTimeOffset.Now > ReceivedAt.AddSeconds(TtlSec.Value);

    // Single line for the tray tooltip and the history submenu.
    public string Summary => string.IsNullOrWhiteSpace(Body) ? Title : $"{Title}: {Body}";
}
