using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Threading;
using Notipet.Settings;
using Notipet.Shared;

namespace Notipet.Sound;

// Skipped: not played on purpose (the alarm limit), as opposed to a failure.
internal sealed record SoundPlayOutcome(bool Played, string? AlarmId, string? Detail, bool Skipped = false);

// Owns the engines and the alarm registry, and is the only thing that decides
// which engine plays a given sound.
[SupportedOSPlatform("windows10.0.19041.0")]
internal sealed class SoundService : IDisposable
{
    private readonly Func<AppSettings> _settings;
    private readonly MediaPlayerSoundEngine _mediaPlayer = new();
    private readonly PlaySoundEngine _playSound = new();
    private int _sequence;

    public AlarmRegistry Alarms { get; } = new();

    public SoundService(Func<AppSettings> settings) => _settings = settings;

    // "auto" prefers MediaPlayer and keeps PlaySound behind it; the explicit
    // modes exist so a user with a broken audio stack can pin the one that works.
    private ISoundEngine[] EngineChain()
    {
        return _settings().Sound.Engine?.Trim().ToLowerInvariant() switch
        {
            "mediaplayer" => new ISoundEngine[] { _mediaPlayer },
            "playsound" => new ISoundEngine[] { _playSound },
            _ => new ISoundEngine[] { _mediaPlayer, _playSound }
        };
    }

    public SoundEngineInfo Describe()
    {
        var chain = EngineChain();
        foreach (var engine in chain)
        {
            if (engine.IsAvailable)
            {
                return new SoundEngineInfo { Name = engine.Name, Available = true, LastError = engine.LastError };
            }
        }
        var last = chain.Length > 0 ? chain[^1] : null;
        return new SoundEngineInfo
        {
            Name = "none",
            Available = false,
            LastError = last?.LastError ?? "no audio endpoint"
        };
    }

    // A device change (WM_DEVICECHANGE) asks for a re-probe. Never on the
    // caller's thread, never two at once, and a burst becomes one: Windows
    // sends these in runs - a headset connecting, waking from sleep - and
    // probing on the UI thread is what hung the daemon. A MediaPlayer's
    // Dispose waits with the message loop running, the next device message
    // re-entered the probe there, and the second MediaPlayer's construction
    // waited on the first forever (docs/regression.md).
    //
    // 0 idle, 1 running, 2 running with another request waiting.
    private int _probeState;

    public void RequestProbe()
    {
        while (true)
        {
            var state = Volatile.Read(ref _probeState);
            if (state == 0)
            {
                if (Interlocked.CompareExchange(ref _probeState, 1, 0) != 0) continue;
                _ = System.Threading.Tasks.Task.Run(ProbeLoopAsync);
                return;
            }
            if (state == 1 && Interlocked.CompareExchange(ref _probeState, 2, 1) != 1) continue;
            return;
        }
    }

    private async System.Threading.Tasks.Task ProbeLoopAsync()
    {
        while (true)
        {
            // Let the burst settle, then probe once for all of it.
            await System.Threading.Tasks.Task.Delay(1000).ConfigureAwait(false);
            try { ProbeEngines(); }
            catch (Exception ex) { CrashLog.Write("SoundService.Probe", ex); }

            // Nothing came in meanwhile: done. Otherwise go round once more.
            if (Interlocked.CompareExchange(ref _probeState, 0, 1) == 1) return;
            Interlocked.Exchange(ref _probeState, 1);
        }
    }

    // Re-probe after a device change. Failures are never latched permanently:
    // a Bluetooth headset reconnecting must not require an app restart.
    public void ProbeEngines()
    {
        _mediaPlayer.Probe();
        _playSound.Probe();
    }

    public SoundPlayOutcome Play(ResolvedSound sound, NotificationLevel level, string? tag)
    {
        if (!sound.HasSource) return new SoundPlayOutcome(false, null, "no sound source resolved");

        var id = "alm_" + Interlocked.Increment(ref _sequence).ToString("D4");
        var session = new AlarmSession(id, tag, level, sound, EngineChain());

        if (sound.Repeat != RepeatMode.Once)
        {
            if (!Alarms.TryAdmit(level, out var preempted))
            {
                // Not a failure: the limit doing its job. Reported as a skip
                // so the card does not show a red "sound failed".
                return new SoundPlayOutcome(false, null,
                    $"{AlarmRegistry.MaxConcurrent} alarms already sounding", Skipped: true);
            }
            preempted?.Stop();
        }

        if (!session.Start())
        {
            return new SoundPlayOutcome(false, null, session.FailureReason ?? "playback failed");
        }

        if (sound.Repeat != RepeatMode.Once) Alarms.Add(session);
        return new SoundPlayOutcome(true, sound.Repeat != RepeatMode.Once ? id : null, null);
    }

    public void Dispose()
    {
        Alarms.Dispose();
        _mediaPlayer.Dispose();
        _playSound.Dispose();
    }

}
