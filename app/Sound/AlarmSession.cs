using System;
using System.Threading;
using System.Threading.Tasks;
using Notipet.Shared;

namespace Notipet.Sound;

// One alarm in flight: plays its sound once, a fixed number of times, or until
// acknowledged, and stops on demand.
//
// The repeat gap uses a timer rather than MediaPlayer.IsLoopingEnabled because
// a controllable silence between plays is the point - a gapless loop is a siren.
internal sealed class AlarmSession : IDisposable
{
    private readonly ISoundEngine[] _engines;
    private readonly ResolvedSound _sound;
    private readonly CancellationTokenSource _cts = new();
    private ISoundHandle? _current;
    private readonly object _gate = new();
    private int _stopped;

    public string Id { get; }
    public string? Tag { get; }
    public NotificationLevel Level { get; }
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    // Set when playback could not start on any engine, so the channel can report
    // failed instead of claiming a delivery it did not make.
    public string? FailureReason { get; private set; }

    // The registry subscribes to Finished *after* Start() has already spawned
    // the loop, so a loop that ends immediately - a failed engine, an expired
    // deadline - can fire Finished before anyone is listening. The registry
    // checks this so such a session is never left registered forever.
    public bool IsStopped => Volatile.Read(ref _stopped) != 0;

    public event Action<AlarmSession>? Finished;

    public AlarmSession(string id, string? tag, NotificationLevel level, ResolvedSound sound, ISoundEngine[] engines)
    {
        Id = id;
        Tag = tag;
        Level = level;
        _sound = sound;
        _engines = engines;
    }

    // Plays the first iteration synchronously so the caller learns immediately
    // whether any engine could produce sound, then hands the rest to a loop.
    // Finished fires exactly once, from Stop(), and only for repeating alarms.
    public bool Start()
    {
        var handle = PlayOnce();
        if (handle is null)
        {
            FailureReason = FirstEngineError() ?? "no audio endpoint";
            return false;
        }

        SetCurrent(handle);

        if (_sound.Repeat == RepeatMode.Once)
        {
            // Nothing to schedule and nothing to acknowledge: a one-shot is over
            // before anyone could stop it, so it is never registered as active.
            return true;
        }

        _ = Task.Run(() => LoopAsync(_cts.Token));
        return true;
    }

    private async Task LoopAsync(CancellationToken token)
    {
        try
        {
            // MaxDurationSec <= 0 is the user's explicit "no deadline" for an
            // until_ack alarm; the loop then ends only on Stop() (ack, tray
            // click, balloon click, exit). DateTimeOffset.MaxValue would
            // overflow Task.Delay, so WaitAsync clamps the wait instead.
            var deadline = _sound.MaxDurationSec <= 0
                ? DateTimeOffset.MaxValue
                : StartedAt.AddSeconds(_sound.MaxDurationSec);
            var played = 1;

            while (!token.IsCancellationRequested)
            {
                // Wait for the sound to actually finish before timing the gap.
                // intervalMs is silence *between* plays, not a fixed period from
                // one start to the next: with a period, any sound longer than
                // the interval was cut off and restarted, which is what made a
                // 5-second alarm on a 1.2-second interval sound like a stutter
                // that never completed.
                var current = CurrentHandle();
                if (current is not null && !await WaitAsync(current.Completion, deadline, token).ConfigureAwait(false))
                {
                    break;
                }

                // The hard cap applies to every repeating mode, not just
                // until_ack, so no configuration can produce an endless alarm.
                if (DateTimeOffset.UtcNow >= deadline) break;
                if (_sound.Repeat == RepeatMode.Repeat && played >= _sound.RepeatCount) break;

                if (!await WaitAsync(Task.Delay(_sound.IntervalMs, token), deadline, token).ConfigureAwait(false)) break;
                if (DateTimeOffset.UtcNow >= deadline) break;

                var handle = PlayOnce();
                if (handle is null) break;
                SetCurrent(handle);
                played++;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            CrashLog.Write("AlarmSession.Loop", ex);
        }
        finally
        {
            Stop();
        }
    }

    private ISoundHandle? PlayOnce()
    {
        foreach (var engine in _engines)
        {
            if (!engine.IsAvailable) engine.Probe();
            if (!engine.IsAvailable) continue;

            var handle = engine.Play(_sound);
            if (handle is not null) return handle;
        }
        return null;
    }

    private string? FirstEngineError()
    {
        foreach (var engine in _engines)
        {
            if (engine.LastError is { Length: > 0 } error) return $"{engine.Name}: {error}";
        }
        return null;
    }

    private ISoundHandle? CurrentHandle()
    {
        lock (_gate) return _current;
    }

    // Waits for `work`, but never past the alarm's hard deadline and never past
    // cancellation. Returns false when the loop should stop.
    private static async Task<bool> WaitAsync(Task work, DateTimeOffset deadline, CancellationToken token)
    {
        var remaining = deadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero) return false;

        try
        {
            // Task.Delay rejects anything past uint.MaxValue milliseconds, and
            // an unlimited alarm's deadline is DateTimeOffset.MaxValue. Capping
            // the wait at a day leaves an alarm nobody stopped in 24 hours to
            // end there, which is a ceiling no real alarm reaches.
            var timeout = Task.Delay(remaining > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : remaining, token);
            var finished = await Task.WhenAny(work, timeout).ConfigureAwait(false);
            if (finished == timeout) return false;
            await work.ConfigureAwait(false);
            return !token.IsCancellationRequested;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private void SetCurrent(ISoundHandle handle)
    {
        ISoundHandle? previous;
        lock (_gate)
        {
            previous = _current;
            _current = handle;
        }
        previous?.Stop();
    }

    public void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;

        try { _cts.Cancel(); } catch { }

        ISoundHandle? handle;
        lock (_gate)
        {
            handle = _current;
            _current = null;
        }
        handle?.Stop();

        Finished?.Invoke(this);
    }

    public void Dispose()
    {
        Stop();
        _cts.Dispose();
    }
}
