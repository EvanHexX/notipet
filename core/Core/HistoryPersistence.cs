using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Notipet.Shared;

namespace Notipet.Core;

// Keeps Recent in history.json so a restart - an update, `notipet restart`,
// the watchdog, a graphics driver change - or a reboot does not empty it.
// Changes are written at most every couple of seconds, off the notification
// path; Flush() writes now (before quitting or restarting). An empty history,
// or persistence turned off, means no file.
internal sealed class HistoryPersistence : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(2);
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HistoryStore _store;
    private readonly string _path;
    private readonly Func<bool> _enabled;
    private readonly Timer _timer;
    private readonly object _writeGate = new();
    private int _pending;
    private bool _disposed;

    public HistoryPersistence(HistoryStore store, string path, Func<bool> enabled)
    {
        _store = store;
        _path = path;
        _enabled = enabled;
        _timer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
        _store.Changed += OnChanged;
    }

    private void OnChanged()
    {
        if (_disposed) return;
        Interlocked.Exchange(ref _pending, 1);
        try { _timer.Change(Debounce, Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { }
    }

    // Writes the store as it is now (or deletes the file when it is empty or
    // persistence is off). Safe from any thread; never throws.
    public void Flush()
    {
        lock (_writeGate)
        {
            Interlocked.Exchange(ref _pending, 0);
            try
            {
                var entries = _enabled() ? _store.Recent(int.MaxValue) : Array.Empty<HistoryEntry>();
                Save(_path, entries);
            }
            catch (Exception ex)
            {
                CrashLog.Write("HistoryPersistence.Flush", ex);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _store.Changed -= OnChanged;
        _timer.Dispose();
        if (Interlocked.Exchange(ref _pending, 0) == 1) Flush();
        _disposed = true;
    }

    // ----- the file -----

    public static void Save(string path, IReadOnlyList<HistoryEntry> entries)
    {
        if (entries.Count == 0)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        var file = new HistoryFile { Entries = entries.Select(HistoryFile.Item.From).ToList() };
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(file, Options));
        File.Move(temp, path, overwrite: true);
    }

    // Newest first, as saved. A missing file is an empty history; a broken one
    // is set aside as history.json.bad rather than read or lost silently.
    public static List<HistoryEntry> Load(string path)
    {
        if (!File.Exists(path)) return new List<HistoryEntry>();
        try
        {
            var file = JsonSerializer.Deserialize<HistoryFile>(File.ReadAllText(path), Options);
            return (file?.Entries ?? new List<HistoryFile.Item>())
                .Where(i => !string.IsNullOrWhiteSpace(i.Id))
                .Select(i => i.ToEntry())
                .ToList();
        }
        catch (Exception ex)
        {
            CrashLog.Write("HistoryPersistence.Load", ex);
            try { File.Move(path, path + ".bad", overwrite: true); } catch { }
            return new List<HistoryEntry>();
        }
    }

    public static bool RunSelfTest()
    {
        var dir = Path.Combine(Path.GetTempPath(), "notipet-history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "history.json");
        try
        {
            var entry = new HistoryEntry
            {
                Envelope = new NotificationEnvelope
                {
                    Title = "shop: 테스트 통과", Body = "412/412", Level = NotificationLevel.Success, Tag = "shop:done",
                    SourceId = "codex", SourceSession = "019a2b3c-4d5e-7f00-8a9b-0c1d2e3f4a5b", Project = "shop",
                    ThreadTitle = "checkout", SourceCwd = @"C:\src\shop", OpenUri = "http://127.0.0.1:5000/"
                },
                Accepted = false, SuppressedReason = "thread_off", Count = 3, AppThreadTitle = "Checkout refactor",
                ResolvedAt = DateTimeOffset.Now
            };
            entry.Deliveries.Add(new DeliveryResult { Channel = "windows_sound", Status = "delivered", ReferenceId = "alm_1" });
            var older = new HistoryEntry { Envelope = new NotificationEnvelope { Title = "older" } };

            // Round trip, newest first, every field the cards and rules use.
            Save(path, new[] { entry, older });
            var back = Load(path);
            if (back.Count != 2 || back[1].Envelope.Title != "older") return false;
            var e = back[0];
            if (e.Envelope.Id != entry.Envelope.Id || e.Envelope.ReceivedAt != entry.Envelope.ReceivedAt) return false;
            if (e.Envelope.Title != "shop: 테스트 통과" || e.Envelope.Level != NotificationLevel.Success || e.Envelope.Tag != "shop:done") return false;
            if (e.Envelope.SourceSession != entry.Envelope.SourceSession || e.Envelope.Project != "shop" || e.Envelope.OpenUri != entry.Envelope.OpenUri) return false;
            if (e.Accepted || e.SuppressedReason != "thread_off" || e.Count != 3 || e.AppThreadTitle != "Checkout refactor" || e.ResolvedAt is null) return false;
            if (e.Deliveries.Single().ReferenceId != "alm_1" || e.Envelope.DedupeKey != entry.Envelope.DedupeKey) return false;

            // Restore puts them behind what is already there, once each.
            var store = new HistoryStore(() => 10);
            store.Add(new HistoryEntry { Envelope = new NotificationEnvelope { Title = "new" } });
            store.Restore(back);
            store.Restore(back);
            if (store.Count != 3 || store.Recent(1)[0].Envelope.Title != "new") return false;

            // Persistence writes on Flush, deletes when empty or turned off.
            var on = true;
            using (var persistence = new HistoryPersistence(store, path, () => on))
            {
                persistence.Flush();
                if (Load(path).Count != 3) return false;
                on = false;
                persistence.Flush();
                if (File.Exists(path)) return false;
                on = true;
                store.Clear();
                persistence.Flush();
                if (File.Exists(path)) return false;
            }

            // A broken file is set aside, not fatal.
            File.WriteAllText(path, "{ not json");
            return Load(path).Count == 0 && File.Exists(path + ".bad") && !File.Exists(path);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}

internal sealed class HistoryFile
{
    public int SchemaVersion { get; set; } = 1;
    public List<Item> Entries { get; set; } = new();

    internal sealed class Item
    {
        public string Id { get; set; } = "";
        public DateTimeOffset ReceivedAt { get; set; }
        public DateTimeOffset LastAt { get; set; }
        public string Title { get; set; } = "";
        public string Body { get; set; } = "";
        public string Level { get; set; } = "info";
        public string? Tag { get; set; }
        public bool Collapse { get; set; } = true;
        public string Source { get; set; } = PayloadMapper.SourceManual;
        public string? SourceLabel { get; set; }
        public string? Thread { get; set; }
        public string? Cwd { get; set; }
        public string? Project { get; set; }
        public string? ThreadTitle { get; set; }
        public string? HostSession { get; set; }
        public string? Client { get; set; }
        public string? Open { get; set; }
        public bool Accepted { get; set; }
        public string? SuppressedReason { get; set; }
        public string? CollapsedWith { get; set; }
        public int Count { get; set; } = 1;
        public string? AppThreadTitle { get; set; }
        public DateTimeOffset? ResolvedAt { get; set; }
        public List<DeliveryResult>? Deliveries { get; set; }

        public static Item From(HistoryEntry e) => new()
        {
            Id = e.Envelope.Id,
            ReceivedAt = e.Envelope.ReceivedAt,
            LastAt = e.LastAt,
            Title = e.Envelope.Title,
            Body = e.Envelope.Body,
            Level = NotificationLevelParser.ToWire(e.Envelope.Level),
            Tag = e.Envelope.Tag,
            Collapse = e.Envelope.Collapse,
            Source = e.Envelope.SourceId,
            SourceLabel = e.Envelope.SourceLabel,
            Thread = e.Envelope.SourceSession,
            Cwd = e.Envelope.SourceCwd,
            Project = e.Envelope.Project,
            ThreadTitle = e.Envelope.ThreadTitle,
            HostSession = e.Envelope.HostSession,
            Client = e.Envelope.Client,
            Open = e.Envelope.OpenUri,
            Accepted = e.Accepted,
            SuppressedReason = e.SuppressedReason,
            CollapsedWith = e.CollapsedWith,
            Count = e.Count,
            AppThreadTitle = e.AppThreadTitle,
            ResolvedAt = e.ResolvedAt,
            Deliveries = e.Deliveries.Count > 0 ? e.Deliveries.ToList() : null
        };

        public HistoryEntry ToEntry()
        {
            var entry = new HistoryEntry
            {
                Envelope = new NotificationEnvelope(Id, ReceivedAt)
                {
                    Title = Title,
                    Body = Body,
                    Level = NotificationLevelParser.Parse(Level),
                    Tag = Tag,
                    Collapse = Collapse,
                    SourceId = Source,
                    SourceLabel = SourceLabel,
                    SourceSession = Thread,
                    SourceCwd = Cwd,
                    Project = Project,
                    ThreadTitle = ThreadTitle,
                    HostSession = HostSession,
                    Client = Client,
                    OpenUri = Open
                },
                Accepted = Accepted,
                SuppressedReason = SuppressedReason,
                CollapsedWith = CollapsedWith,
                Count = Math.Max(1, Count),
                LastAt = LastAt == default ? ReceivedAt : LastAt,
                AppThreadTitle = AppThreadTitle,
                ResolvedAt = ResolvedAt
            };
            if (Deliveries is not null) entry.Deliveries.AddRange(Deliveries);
            return entry;
        }
    }
}
