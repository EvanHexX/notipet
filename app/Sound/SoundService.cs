using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Threading;
using Notipet.Settings;
using Notipet.Shared;

namespace Notipet.Sound;

internal sealed record SoundPlayOutcome(bool Played, string? AlarmId, string? Detail);

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
                return new SoundPlayOutcome(false, null, "collapsed into an alarm already sounding");
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
