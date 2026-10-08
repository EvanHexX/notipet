using System;
using System.Runtime.Versioning;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace Notipet.Sound;

// Primary engine. Costs no new NuGet - the net10.0-windows10.0.19041.0 TFM
// already projects the Windows SDK - and gives volume control, mp3/wav/flac,
// and a real failure signal, none of which PlaySound offers.
[SupportedOSPlatform("windows10.0.19041.0")]
internal sealed class MediaPlayerSoundEngine : ISoundEngine
{
    private bool _available = true;
    private string? _lastError;
    private bool _disposed;

    public string Name => "mediaplayer";
    public bool IsAvailable => _available && !_disposed;
    public string? LastError => _lastError;

    public ISoundHandle? Play(ResolvedSound sound)
    {
        if (_disposed) return null;
        // MediaPlayer needs a real file; an alias-only sound belongs to the
        // fallback engine.
        if (sound.Path is null) return null;

        MediaPlayer? player = null;
        try
        {
            player = new MediaPlayer();

            // In an unpackaged process the default SMTC integration tries to
            // register system media transport controls: it can throw outright,
            // and when it does not it hijacks the user's media keys. This line
            // is the difference between the engine working and not.
            player.CommandManager.IsEnabled = false;

            // Tell Windows this is an alert so it ducks music and calls
            // correctly instead of competing with them.
            player.AudioCategory = MediaPlayerAudioCategory.Alerts;
            player.AutoPlay = false;
            player.Volume = Math.Clamp(sound.Volume, 0d, 1d);
            player.IsLoopingEnabled = false;
            player.Source = MediaSource.CreateFromUri(new Uri(sound.Path));

            var handle = new Handle(player);
            player.MediaFailed += (_, args) =>
            {
                MarkFailed($"MediaFailed: {args.Error} {args.ErrorMessage}".Trim());
                handle.MarkEnded();
            };
            player.MediaEnded += (_, _) => handle.MarkEnded();

            player.Play();
            _lastError = null;
            _available = true;
            return handle;
        }
        catch (Exception ex)
        {
            MarkFailed(ex.Message);
            try { player?.Dispose(); } catch { }
            return null;
        }
    }

    public void Probe()
    {
        if (_disposed) return;
        try
        {
            // Constructing and disposing a player is the cheapest honest test
            // that an audio endpoint exists.
            using var probe = new MediaPlayer();
            probe.CommandManager.IsEnabled = false;
            _available = true;
            _lastError = null;
        }
        catch (Exception ex)
        {
            MarkFailed(ex.Message);
        }
    }

    private void MarkFailed(string message)
    {
        _available = false;
        _lastError = message;
    }

    public void Dispose() => _disposed = true;

    private sealed class Handle : ISoundHandle
    {
        private MediaPlayer? _player;
        private readonly object _gate = new();

        // RunContinuationsAsynchronously so the awaiting loop never resumes
        // inside a MediaPlayer event callback, which runs on a WinRT thread.
        private readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Handle(MediaPlayer player) => _player = player;

        public Task Completion => _completion.Task;

        public void MarkEnded() => Stop();

        public void Stop()
        {
            MediaPlayer? player;
            lock (_gate)
            {
                player = _player;
                _player = null;
            }

            // Signal first: a caller waiting on Completion should be released
            // even if tearing the player down throws.
            _completion.TrySetResult();

            if (player is null) return;
            try { player.Pause(); } catch { }
            try { player.Dispose(); } catch { }
        }

        public void Dispose() => Stop();
    }
}
