using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Notipet.Shared;

namespace Notipet.Sound;

// Tracks the repeating alarms that are currently sounding, so every
// acknowledgement path - the HTTP /v1/ack route, the tray menu, a tray click, a
// balloon click, app exit - converges on one place.
//
// One-shot sounds are not tracked: they are over before anyone could stop them.
internal sealed class AlarmRegistry : IDisposable
{
    // Three at once is already more than anyone can parse by ear.
    public const int MaxConcurrent = 3;

    private readonly Dictionary<string, AlarmSession> _active = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public event Action? Changed;

    public int Count
    {
        get { lock (_gate) return _active.Count; }
    }

    public bool HasActive => Count > 0;

    public IReadOnlyList<AlarmSession> Snapshot()
    {
        lock (_gate) return _active.Values.ToList();
    }

    // Makes room for a new alarm, or refuses it. A louder alarm preempts the
    // quietest one already sounding; an equally quiet one is turned away so a
    // burst of identical alerts cannot stack into noise.
    public bool TryAdmit(NotificationLevel level, out AlarmSession? preempted)
    {
        preempted = null;
        lock (_gate)
        {
            if (_active.Count < MaxConcurrent) return true;

            var quietest = _active.Values.OrderBy(a => a.Level).ThenBy(a => a.StartedAt).First();
            if (quietest.Level >= level) return false;

            preempted = quietest;
            return true;
        }
    }

    public void Add(AlarmSession session)
    {
        lock (_gate) _active[session.Id] = session;
        session.Finished += OnFinished;

        // Start() spawns the repeat loop before we get here, so a loop that
        // ended immediately already raised Finished with nobody subscribed.
        // Without this the entry would sit in the registry forever, and the
        // tray would show an alarm that is not making any sound.
        if (session.IsStopped)
        {
            OnFinished(session);
            return;
        }

        Changed?.Invoke();
    }

    private void OnFinished(AlarmSession session)
    {
        bool removed;
        lock (_gate) removed = _active.Remove(session.Id);
        if (removed) Changed?.Invoke();
    }

    public int Stop(string? id = null, string? tag = null, bool all = false)
    {
        List<AlarmSession> targets;
        lock (_gate)
        {
            targets = _active.Values.Where(a =>
                all
                || (id is not null && string.Equals(a.Id, id, StringComparison.Ordinal))
                || (tag is not null && string.Equals(a.Tag, tag, StringComparison.Ordinal)))
                .ToList();
        }

        foreach (var target in targets) target.Stop();
        return targets.Count;
    }

    public int StopAll() => Stop(all: true);

    // Exercises admission and acknowledgement logic without ever
    // producing sound: the self-test has to stay silent so it can run in CI.
    public static bool RunSelfTest()
    {
        var registry = new AlarmRegistry();
        var silent = new ResolvedSound(null, null, 1, RepeatMode.UntilAck, 1, 1000, 60);
        var fake = new FakeEngine();
        var sessions = new List<AlarmSession>();

        for (var i = 0; i < AlarmRegistry.MaxConcurrent; i++)
        {
            if (!registry.TryAdmit(NotificationLevel.Attention, out _)) return false;
            var session = new AlarmSession("a" + i, "tag" + i, NotificationLevel.Attention, silent, new ISoundEngine[] { fake });
            if (!session.Start()) return false;
            registry.Add(session);
            sessions.Add(session);
        }
        if (registry.Count != AlarmRegistry.MaxConcurrent) return false;

        // A fourth alarm at the same level is turned away.
        if (registry.TryAdmit(NotificationLevel.Attention, out _)) return false;

        // A louder one preempts the quietest.
        if (!registry.TryAdmit(NotificationLevel.Critical, out var preempted)) return false;
        if (preempted is null) return false;
        preempted.Stop();
        if (registry.Count != AlarmRegistry.MaxConcurrent - 1) return false;

        // Acknowledgement by tag stops exactly one.
        var remaining = registry.Count;
        var byTag = registry.Stop(tag: sessions[^1].Tag);
        if (byTag != 1 || registry.Count != remaining - 1) return false;

        // Stopping everything empties the registry, and stopping twice is safe.
        registry.StopAll();
        if (registry.Count != 0) return false;
        if (registry.StopAll() != 0) return false;
        foreach (var session in sessions) session.Stop();

        if (!RepeatWaitsForCompletion()) return false;
        if (!AlreadyFinishedSessionIsNotLeaked()) return false;
        return true;
    }

    // A session whose loop ended before the registry subscribed must not stay
    // registered. Left unhandled this leaks an alarm that is silent but shows
    // as active forever, and /v1/ack can never clear it.
    private static bool AlreadyFinishedSessionIsNotLeaked()
    {
        var registry = new AlarmRegistry();
        var sound = new ResolvedSound(null, "fake", 1, RepeatMode.UntilAck, 1, 1, 60);
        var session = new AlarmSession("done", "t", NotificationLevel.Critical, sound, new ISoundEngine[] { new FakeEngine() });

        if (!session.Start()) return false;
        session.Stop();               // finishes before Add() subscribes
        registry.Add(session);

        return registry.Count == 0;
    }

    // The regression that made a long alarm stutter: the repeat loop used a
    // fixed period from the start of each play, so a sound longer than the
    // interval was cut off and restarted. It must now wait for the sound to
    // finish before timing the gap.
    private static bool RepeatWaitsForCompletion()
    {
        // A 1ms gap, so only waiting on completion can hold the second play back.
        var sound = new ResolvedSound(null, "fake", 1, RepeatMode.Repeat, 2, 1, 60);
        var engine = new FakeEngine(completeImmediately: false);
        var session = new AlarmSession("wait", null, NotificationLevel.Attention, sound, new ISoundEngine[] { engine });

        if (!session.Start()) return false;
        if (engine.PlayCount != 1) return false;

        // The sound has not ended, so no matter how long we wait there must be
        // no second play.
        Thread.Sleep(150);
        if (engine.PlayCount != 1) return false;

        // Let it end; the second play may now happen.
        engine.Last!.Finish();
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (engine.PlayCount < 2 && DateTime.UtcNow < deadline) Thread.Sleep(10);
        if (engine.PlayCount != 2) return false;

        // repeatCount is 2, so finishing the second play ends the alarm rather
        // than starting a third.
        engine.Last!.Finish();
        Thread.Sleep(150);
        if (engine.PlayCount != 2) return false;

        session.Stop();
        return true;
    }

    // A started until-acknowledged alarm that never touches an audio device,
    // for other self-tests that need something live to stop.
    internal static AlarmSession StartSilentForTest(string id, string? tag, NotificationLevel level)
    {
        var sound = new ResolvedSound(null, "fake", 1, RepeatMode.UntilAck, 1, 1000, 60);
        var session = new AlarmSession(id, tag, level, sound, new ISoundEngine[] { new FakeEngine() });
        session.Start();
        return session;
    }

    // Reports success without touching an audio device. Its handles complete
    // only when told to, so a test can assert that the repeat loop really does
    // wait for a sound to finish instead of restarting on a timer.
    private sealed class FakeEngine : ISoundEngine
    {
        private readonly bool _completeImmediately;
        public int PlayCount;
        public FakeHandle? Last { get; private set; }

        public FakeEngine(bool completeImmediately = true) => _completeImmediately = completeImmediately;

        public string Name => "fake";
        public bool IsAvailable => true;
        public string? LastError => null;
        public void Probe() { }
        public void Dispose() { }

        public ISoundHandle? Play(ResolvedSound sound)
        {
            Interlocked.Increment(ref PlayCount);
            var handle = new FakeHandle();
            if (_completeImmediately) handle.Finish();
            Last = handle;
            return handle;
        }

        internal sealed class FakeHandle : ISoundHandle
        {
            private readonly TaskCompletionSource _tcs =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            public Task Completion => _tcs.Task;
            public void Finish() => _tcs.TrySetResult();
            public void Stop() => _tcs.TrySetResult();
            public void Dispose() => Stop();
        }
    }

    public void Dispose() => StopAll();
}
