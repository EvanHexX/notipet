using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Notipet.Settings;
using Notipet.Shared;
using Notipet.Sound;

namespace Notipet.Core;

// "The thing I alerted about is over" - POST /v1/resolve, sent by an agent
// that finished what it had called the user for, and by the hooks at the end
// of a turn. It exists so a moment handled from somewhere else (a reply from a
// phone, a fix the agent made itself) stops ringing at an empty desk.
//
// Two promises:
//   1. Only what was named. Each matched notification's own alarm (by the id
//      its sound delivery recorded) and its own pop-up - never "all alarms",
//      and a request that names nothing is refused before it gets here.
//   2. Quiet when there is nothing to do. The user may have stopped the alarm
//      and closed the card already; then this changes nothing and says 0.
internal sealed class AlarmResolver
{
    private readonly HistoryStore _history;
    private readonly AlarmRegistry _alarms;

    // Closes the pop-ups of these notifications and returns the ids it
    // actually closed (or took off the "+N more" card). Pop-ups belong to the
    // platform, so this is a seam; without one, nothing is closed.
    private readonly Func<IReadOnlyCollection<string>, Task<IReadOnlyCollection<string>>> _closePopups;

    public AlarmResolver(HistoryStore history, AlarmRegistry alarms,
        Func<IReadOnlyCollection<string>, Task<IReadOnlyCollection<string>>>? closePopups = null)
    {
        _history = history;
        _alarms = alarms;
        _closePopups = closePopups ?? (_ => Task.FromResult<IReadOnlyCollection<string>>(Array.Empty<string>()));
    }

    // An agent alone is not a selector: "everything Codex ever sent" is not a
    // moment that ended.
    public static bool HasSelector(ResolveRequest request) =>
        !string.IsNullOrWhiteSpace(request.Id)
        || EnvelopeFactory.NormalizeTag(request.Tag) is not null
        || request.Tags?.Any(t => EnvelopeFactory.NormalizeTag(t) is not null) == true
        || !string.IsNullOrWhiteSpace(request.Session);

    public async Task<ResolveResponse> ResolveAsync(ResolveRequest request)
    {
        var response = new ResolveResponse { Ok = true };
        if (!HasSelector(request)) return response;

        var id = NonEmpty(request.Id);
        var tags = Tags(request);
        var source = NonEmpty(request.Source) is { } named ? AgentIdentity.NormalizeAgent(named) : null;
        var session = NonEmpty(request.Session);

        // Suppressed notifications never rang or showed; resolved ones are done.
        var matched = _history.Where(e => e.Accepted && e.ResolvedAt is null && Matches(e, id, tags, source, session));
        if (matched.Count == 0) return response;

        var live = new HashSet<HistoryEntry>();
        foreach (var entry in matched)
        {
            foreach (var alarmId in AlarmIds(entry))
            {
                var stopped = _alarms.Stop(id: alarmId);
                if (stopped == 0) continue;
                response.AlarmsStopped += stopped;
                live.Add(entry);
            }
        }

        var closed = await _closePopups(matched.Select(e => e.Envelope.Id).ToList()).ConfigureAwait(false);
        response.PopupsClosed = closed.Count;
        foreach (var entry in matched)
        {
            if (closed.Contains(entry.Envelope.Id)) live.Add(entry);
        }

        // Only what was still live is marked: a card the user already dealt
        // with does not get a "resolved" label it never needed.
        response.Resolved = _history.MarkResolved(live, DateTimeOffset.Now);
        return response;
    }

    // Selectors narrow together: every one given must hold.
    internal static bool Matches(HistoryEntry entry, string? id, HashSet<string>? tags, string? source, string? session)
    {
        var envelope = entry.Envelope;
        if (id is not null && !string.Equals(envelope.Id, id, StringComparison.Ordinal)) return false;
        if (tags is not null && (envelope.Tag is null || !tags.Contains(envelope.Tag))) return false;
        if (source is not null && !string.Equals(envelope.SourceId, source, StringComparison.Ordinal)) return false;
        if (session is not null && !string.Equals(envelope.SourceSession, session, StringComparison.Ordinal)) return false;
        return true;
    }

    // The alarm a notification started is recorded on its sound delivery.
    private static IEnumerable<string> AlarmIds(HistoryEntry entry) => entry.Deliveries
        .Where(d => d.Channel == AppSettings.Channels_WindowsSound && !string.IsNullOrEmpty(d.ReferenceId))
        .Select(d => d.ReferenceId!);

    private static HashSet<string>? Tags(ResolveRequest request)
    {
        var all = new List<string?> { request.Tag };
        if (request.Tags is not null) all.AddRange(request.Tags);
        var tags = all.Select(EnvelopeFactory.NormalizeTag).OfType<string>().ToHashSet(StringComparer.Ordinal);
        return tags.Count == 0 ? null : tags;
    }

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static bool RunSelfTest()
    {
        var history = new HistoryStore(() => 50);
        var alarms = new AlarmRegistry();
        var open = new HashSet<string>(StringComparer.Ordinal);
        var resolver = new AlarmResolver(history, alarms, ids =>
        {
            var closed = ids.Where(open.Remove).ToList();
            return Task.FromResult<IReadOnlyCollection<string>>(closed);
        });

        // Two threads raised the same moment; thread A also sent something of
        // its own. Each has a ringing alarm and an open pop-up.
        HistoryEntry Raise(string thread, string tag, string alarmId, bool accepted = true)
        {
            var entry = new HistoryEntry
            {
                Envelope = new NotificationEnvelope
                {
                    Title = "t", Tag = tag, SourceId = PayloadMapper.SourceClaude, SourceSession = thread,
                    Level = NotificationLevel.Attention
                },
                Accepted = accepted
            };
            if (accepted)
            {
                var session = AlarmRegistry.StartSilentForTest(alarmId, tag, NotificationLevel.Attention);
                alarms.Add(session);
                entry.Deliveries.Add(new DeliveryResult { Channel = AppSettings.Channels_WindowsSound, Status = "delivered", ReferenceId = alarmId });
                open.Add(entry.Envelope.Id);
            }
            history.Add(entry);
            return entry;
        }

        try
        {
            var a = Raise("thread-a", "shop:needs-input", "alm_a");
            var aOther = Raise("thread-a", "shop:done", "alm_a2");
            var b = Raise("thread-b", "shop:needs-input", "alm_b");
            var suppressed = Raise("thread-c", "shop:needs-input", "alm_x", accepted: false);
            if (alarms.Count != 3) return false;

            // Nothing named, nothing done - and an agent alone is not a name.
            if (HasSelector(new ResolveRequest())) return false;
            if (HasSelector(new ResolveRequest { Source = "codex", Tags = new List<string> { " " } })) return false;
            if (resolver.ResolveAsync(new ResolveRequest()).Result.Resolved != 0 || alarms.Count != 3) return false;

            // Thread A's "needs input": that alarm and that pop-up, nothing else.
            var first = resolver.ResolveAsync(new ResolveRequest
            {
                Tag = " shop:needs-input ", Source = "claude", Session = "thread-a"
            }).Result;
            if (first.Resolved != 1 || first.AlarmsStopped != 1 || first.PopupsClosed != 1) return false;
            if (alarms.Count != 2 || open.Contains(a.Envelope.Id)) return false;
            if (!open.Contains(aOther.Envelope.Id) || !open.Contains(b.Envelope.Id)) return false;
            if (a.ResolvedAt is null || aOther.ResolvedAt is not null || b.ResolvedAt is not null) return false;
            // A notification that was held back never rang: nothing to end.
            if (resolver.ResolveAsync(new ResolveRequest { Session = "thread-c" }).Result.Resolved != 0) return false;
            if (suppressed.ResolvedAt is not null) return false;

            // Again: already over, so a quiet no-op.
            var again = resolver.ResolveAsync(new ResolveRequest { Tag = "shop:needs-input", Session = "thread-a" }).Result;
            if (again.Resolved != 0 || again.AlarmsStopped != 0 || again.PopupsClosed != 0) return false;

            // The user got there first: alarm stopped, card closed. Still 0,
            // and the card is not labelled resolved.
            alarms.Stop(id: "alm_b");
            open.Remove(b.Envelope.Id);
            var late = resolver.ResolveAsync(new ResolveRequest { Session = "thread-b" }).Result;
            if (late.Resolved != 0 || b.ResolvedAt is not null) return false;

            // A resolved moment no longer swallows the next one through dedupe.
            if (history.FindRecent(a.Envelope.DedupeKey, TimeSpan.FromMinutes(1)) is not null) return false;
            if (history.FindRecent(aOther.Envelope.DedupeKey, TimeSpan.FromMinutes(1)) != aOther) return false;

            // By id alone: exactly that one.
            var byId = resolver.ResolveAsync(new ResolveRequest { Id = aOther.Envelope.Id }).Result;
            if (byId.Resolved != 1 || alarms.Count != 0) return false;
            return a.ToDto().ResolvedAt is not null;
        }
        finally
        {
            alarms.StopAll();
        }
    }
}
