using System;
using System.Collections.Generic;
using System.Linq;
using Notipet.Shared;

namespace Notipet.Core;

// What happened to one notification, including why it did not make a sound.
//
// The decisions are recorded deliberately: the first question anyone asks of a
// tool like this is "why didn't I hear anything", and an answer beats a guess.
internal sealed class HistoryEntry
{
    public required NotificationEnvelope Envelope { get; init; }
    public bool Accepted { get; set; }
    public string? SuppressedReason { get; set; }
    public string? CollapsedWith { get; set; }
    public int Count { get; set; } = 1;
    public DateTimeOffset LastAt { get; set; } = DateTimeOffset.Now;
    public List<DeliveryResult> Deliveries { get; } = new();

    // The thread's name as the agent app itself shows it (Codex sidebar, Claude
    // session name), looked up after delivery. Set at most once, off the
    // notification path; see ThreadTitleLookup.
    public string? AppThreadTitle { get; set; }

    // What the card shows: the app's own name for the thread first, because it
    // matches what the user sees there and does not drift between messages
    // the way a title an LLM writes each time does.
    public string? DisplayThreadTitle => AppThreadTitle ?? Envelope.ThreadTitle;

    // When the sender said the moment was over (/v1/resolve) while its alarm
    // or pop-up was still live. Set at most once, under the store's lock.
    public DateTimeOffset? ResolvedAt { get; set; }

    public HistoryEntryDto ToDto() => new()
    {
        Id = Envelope.Id,
        At = Envelope.ReceivedAt.ToString("o"),
        Level = NotificationLevelParser.ToWire(Envelope.Level),
        Title = Envelope.Title,
        Body = Envelope.Body,
        Source = Envelope.SourceId,
        Project = Envelope.Project,
        Thread = Envelope.SourceSession,
        ThreadTitle = DisplayThreadTitle,
        Tag = Envelope.Tag,
        Count = Count,
        Accepted = Accepted,
        SuppressedReason = SuppressedReason,
        ResolvedAt = ResolvedAt?.ToString("o"),
        Deliveries = Deliveries.ToList()
    };
}

// Bounded in-memory ring. Nothing is written to disk: the history can contain
// prompt text, and notipet keeps that in the process that produced it.
internal sealed class HistoryStore
{
    private readonly LinkedList<HistoryEntry> _entries = new();
    private readonly object _gate = new();
    private readonly Func<int> _capacity;

    public event Action? Changed;

    // A new entry (not a collapsed repeat). Raised after Changed, by which
    // point every channel has already been dispatched.
    public event Action<HistoryEntry>? Added;

    public HistoryStore(Func<int> capacity) => _capacity = capacity;

    public void Add(HistoryEntry entry)
    {
        lock (_gate)
        {
            _entries.AddFirst(entry);
            var max = Math.Max(1, _capacity());
            while (_entries.Count > max) _entries.RemoveLast();
        }
        Changed?.Invoke();
        Added?.Invoke(entry);
    }

    // The most recent entry sharing a dedupe key, within the window. Used by the
    // dedupe rule to collapse a repeat rather than sound again.
    public HistoryEntry? FindRecent(string dedupeKey, TimeSpan window)
    {
        var cutoff = DateTimeOffset.Now - window;
        lock (_gate)
        {
            foreach (var entry in _entries)
            {
                if (entry.LastAt < cutoff) break;
                // A resolved moment is over: the same tag again is a new one
                // and must sound, not collapse into the one that ended.
                if (entry.ResolvedAt is not null) continue;
                if (string.Equals(entry.Envelope.DedupeKey, dedupeKey, StringComparison.Ordinal)) return entry;
            }
        }
        return null;
    }

    // Entries a resolve request names, newest first. Matching is in
    // AlarmResolver; this only takes the snapshot under the lock.
    public IReadOnlyList<HistoryEntry> Where(Func<HistoryEntry, bool> match)
    {
        lock (_gate) return _entries.Where(match).ToList();
    }

    // Marks entries resolved, once each. Returns how many were not already.
    public int MarkResolved(IEnumerable<HistoryEntry> entries, DateTimeOffset at)
    {
        var marked = 0;
        lock (_gate)
        {
            foreach (var entry in entries)
            {
                if (entry.ResolvedAt is not null || !_entries.Contains(entry)) continue;
                entry.ResolvedAt = at;
                marked++;
            }
        }
        if (marked > 0) Changed?.Invoke();
        return marked;
    }

    public void NoteCollapse(HistoryEntry entry)
    {
        lock (_gate)
        {
            entry.Count++;
            entry.LastAt = DateTimeOffset.Now;
        }
        Changed?.Invoke();
    }

    // Bumped by ClearAppThreadTitles. A lookup takes the value before it starts
    // reading files and hands it back with its answer; an answer from before a
    // clear is dropped under the same lock the clear holds. (Checking the
    // setting outside the lock left a window: a lookup could pass the check,
    // the user could switch the feature off, and the stale name landed anyway.)
    private int _titleGeneration;

    public int TitleGeneration
    {
        get { lock (_gate) return _titleGeneration; }
    }

    // Records a thread title found after the fact. A no-op when the entry was
    // removed meanwhile, already has one, or the names were cleared since the
    // lookup began - so a slow lookup cannot resurrect or overwrite anything.
    public bool SetAppThreadTitle(HistoryEntry entry, string? title, int generation)
    {
        if (!TrySetAppThreadTitle(entry, title, generation)) return false;
        Changed?.Invoke();
        return true;
    }

    // Many at once, with one Changed at the end: turning the lookup back on
    // re-reads every card, and a redraw per card would make the window stutter.
    public int SetAppThreadTitles(IEnumerable<(HistoryEntry Entry, string? Title)> found, int generation)
    {
        var set = 0;
        foreach (var (entry, title) in found)
        {
            if (TrySetAppThreadTitle(entry, title, generation)) set++;
        }
        if (set > 0) Changed?.Invoke();
        return set;
    }

    private bool TrySetAppThreadTitle(HistoryEntry entry, string? title, int generation)
    {
        if (string.IsNullOrWhiteSpace(title)) return false;
        lock (_gate)
        {
            if (generation != _titleGeneration) return false;
            if (entry.AppThreadTitle is not null || !_entries.Contains(entry)) return false;
            entry.AppThreadTitle = title;
        }
        return true;
    }

    // Drops every name read from the agent apps - for when the user turns that
    // off: the setting promises those files are not used, so names that were
    // already read must not linger on the cards, and none still on its way may
    // land afterwards.
    public int ClearAppThreadTitles()
    {
        var cleared = 0;
        lock (_gate)
        {
            _titleGeneration++;
            foreach (var entry in _entries)
            {
                if (entry.AppThreadTitle is null) continue;
                entry.AppThreadTitle = null;
                cleared++;
            }
        }
        if (cleared > 0) Changed?.Invoke();
        return cleared;
    }

    public IReadOnlyList<HistoryEntry> Recent(int limit)
    {
        lock (_gate) return _entries.Take(Math.Max(0, limit)).ToList();
    }

    public int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    public int Clear()
    {
        int cleared;
        lock (_gate)
        {
            cleared = _entries.Count;
            _entries.Clear();
        }
        if (cleared > 0) Changed?.Invoke();
        return cleared;
    }

    public bool Remove(string id)
    {
        bool removed = false;
        lock (_gate)
        {
            for (var node = _entries.First; node is not null; node = node.Next)
            {
                if (!string.Equals(node.Value.Envelope.Id, id, StringComparison.Ordinal)) continue;
                _entries.Remove(node);
                removed = true;
                break;
            }
        }
        if (removed) Changed?.Invoke();
        return removed;
    }

    // Applies a lowered capacity immediately. Without this, shrinking the limit
    // in settings would only take effect as new notifications pushed old ones
    // out, which looks like the setting did nothing.
    public void Trim()
    {
        bool trimmed = false;
        lock (_gate)
        {
            var max = Math.Max(1, _capacity());
            while (_entries.Count > max)
            {
                _entries.RemoveLast();
                trimmed = true;
            }
        }
        if (trimmed) Changed?.Invoke();
    }

    public static bool RunSelfTest()
    {
        var store = new HistoryStore(() => 3);
        HistoryEntry Make(string tag) => new()
        {
            Envelope = new NotificationEnvelope { Title = "t", Body = "b", Tag = tag },
            Accepted = true
        };

        for (var i = 0; i < 5; i++) store.Add(Make("tag" + i));

        // The ring drops the oldest rather than growing without bound.
        var recent = store.Recent(10);
        if (recent.Count != 3) return false;
        if (recent[0].Envelope.Tag != "tag4") return false;

        // A matching key inside the window is found, and collapsing bumps the
        // count on the existing entry instead of adding a new one.
        var found = store.FindRecent("tag4", TimeSpan.FromMinutes(1));
        if (found is null) return false;
        store.NoteCollapse(found);
        if (found.Count != 2) return false;
        if (store.Recent(10).Count != 3) return false;

        // Outside the window, nothing matches.
        if (store.FindRecent("tag4", TimeSpan.Zero) is not null) return false;
        // A key that was evicted is gone.
        if (store.FindRecent("tag0", TimeSpan.FromMinutes(1)) is not null) return false;

        // With no tag, identical content still shares a dedupe key.
        var a = new NotificationEnvelope { Title = "x", Body = "y", SourceId = "codex" };
        var b = new NotificationEnvelope { Title = "x", Body = "y", SourceId = "codex" };
        if (a.DedupeKey != b.DedupeKey) return false;
        if (a.Id == b.Id) return false;

        // Remove one, then clear the rest.
        var survivors = store.Recent(10);
        if (!store.Remove(survivors[0].Envelope.Id)) return false;
        if (store.Remove("no-such-id")) return false;
        if (store.Count != survivors.Count - 1) return false;
        if (store.Clear() != survivors.Count - 1) return false;
        if (store.Count != 0) return false;

        // Lowering the capacity trims at once, keeping the newest.
        var cap = 5;
        var resizable = new HistoryStore(() => cap);
        for (var i = 0; i < 5; i++) resizable.Add(Make("r" + i));
        cap = 2;
        resizable.Trim();
        var kept = resizable.Recent(10);
        if (kept.Count != 2 || kept[0].Envelope.Tag != "r4") return false;

        // Who and where reach the wire, and the app's title beats the sender's.
        var titled = new HistoryEntry
        {
            Envelope = new NotificationEnvelope
            {
                Title = "t", SourceId = "codex", Project = "shop",
                SourceSession = "019a2b3c-4d5e-7f00-8a9b-0c1d2e3f4a5b", ThreadTitle = "from the llm"
            }
        };
        var dto = titled.ToDto();
        if (dto.Project != "shop" || dto.Thread != "019a2b3c-4d5e-7f00-8a9b-0c1d2e3f4a5b" || dto.ThreadTitle != "from the llm") return false;
        resizable.Add(titled);
        var generation = resizable.TitleGeneration;
        if (!resizable.SetAppThreadTitle(titled, "Checkout refactor", generation)) return false;
        if (titled.ToDto().ThreadTitle != "Checkout refactor") return false;
        // Set once; a second answer does not overwrite the first.
        if (resizable.SetAppThreadTitle(titled, "other", generation)) return false;
        // An entry that is gone stays gone.
        var removed = new HistoryEntry { Envelope = new NotificationEnvelope { Title = "gone" } };
        if (resizable.SetAppThreadTitle(removed, "late", generation)) return false;
        // Turning the lookup off takes the app names back off the cards...
        if (resizable.ClearAppThreadTitles() != 1) return false;
        if (titled.ToDto().ThreadTitle != "from the llm") return false;
        if (resizable.ClearAppThreadTitles() != 0) return false;
        // ...and an answer from a lookup that started before that is dropped.
        if (resizable.SetAppThreadTitle(titled, "stale", generation)) return false;
        if (titled.AppThreadTitle is not null) return false;
        // A fresh lookup after the clear is fine, in bulk too.
        if (resizable.SetAppThreadTitles(new[] { (titled, (string?)"fresh") }, resizable.TitleGeneration) != 1) return false;
        if (titled.ToDto().ThreadTitle != "fresh") return false;

        return true;
    }
}
