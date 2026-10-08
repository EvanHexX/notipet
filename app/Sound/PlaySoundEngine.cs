using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Notipet.Sound;

// Fallback engine: one winmm P/Invoke, no dependencies, works when MediaPlayer
// cannot construct.
//
// Two limitations the caller must know about, both documented rather than
// hidden: it cannot control volume at all, and winmm has a single playback slot
// per process, so this engine plays one sound at a time and stopping one stops
// them all. Acceptable for a fallback; not acceptable as the primary.
[SupportedOSPlatform("windows")]
internal sealed class PlaySoundEngine : ISoundEngine
{
    private const uint SND_ASYNC = 0x0001;
    private const uint SND_NODEFAULT = 0x0002;
    private const uint SND_ALIAS = 0x00010000;
    private const uint SND_FILENAME = 0x00020000;

    private bool _available = true;
    private string? _lastError;
    private bool _disposed;

    public string Name => "playsound";
    public bool IsAvailable => _available && !_disposed;
    public string? LastError => _lastError;

    public ISoundHandle? Play(ResolvedSound sound)
    {
        if (_disposed) return null;

        try
        {
            bool ok;
            if (sound.Path is not null)
            {
                ok = PlaySoundW(sound.Path, IntPtr.Zero, SND_ASYNC | SND_FILENAME | SND_NODEFAULT);
            }
            else if (sound.Alias is not null)
            {
                ok = PlaySoundW(sound.Alias, IntPtr.Zero, SND_ASYNC | SND_ALIAS | SND_NODEFAULT);
            }
            else
            {
                return null;
            }

            if (!ok)
            {
                _available = false;
                _lastError = $"PlaySound failed ({Marshal.GetLastWin32Error()})";
                return null;
            }

            _available = true;
            _lastError = null;
            return new Handle();
        }
        catch (Exception ex)
        {
            _available = false;
            _lastError = ex.Message;
            return null;
        }
    }

    public void Probe()
    {
        // winmm gives no way to ask whether an endpoint exists without playing
        // something, so assume it is usable again and let the next Play decide.
        if (!_disposed) _available = true;
    }

    public void Dispose()
    {
        _disposed = true;
        StopAll();
    }

    private static void StopAll()
    {
        try { PlaySoundW(null, IntPtr.Zero, 0); } catch { }
    }

    [DllImport("winmm.dll", CharSet = CharSet.Unicode, EntryPoint = "PlaySoundW", SetLastError = true)]
    private static extern bool PlaySoundW(string? pszSound, IntPtr hmod, uint fdwSound);

    // winmm's async playback has no completion callback, so unlike the
    // MediaPlayer engine this one cannot know when a sound actually ended. The
    // repeat loop still needs *something* to wait on, so Completion resolves
    // after a fixed estimate. That is an approximation, and it is why the
    // fallback engine's repeat timing is looser than the primary's - stated
    // here rather than hidden, because a wrong guess shows up as a clipped
    // sound and someone will come looking.
    private static readonly TimeSpan AssumedPlaybackLength = TimeSpan.FromSeconds(2.5);

    private sealed class Handle : ISoundHandle
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _stopped;

        public Handle()
        {
            _ = Task.Delay(AssumedPlaybackLength, _cts.Token)
                .ContinueWith(_ => _completion.TrySetResult(), TaskScheduler.Default);
        }

        public Task Completion => _completion.Task;

        // Process-wide: winmm has one slot, so this necessarily silences any
        // other sound this engine started.
        public void Stop()
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
            try { _cts.Cancel(); } catch { }
            _completion.TrySetResult();
            StopAll();
        }

        public void Dispose()
        {
            Stop();
            _cts.Dispose();
        }
    }
}
