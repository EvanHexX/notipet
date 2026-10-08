using System;
using System.Threading.Tasks;

namespace Notipet.Sound;

// One playback in flight. Disposing it stops the sound.
internal interface ISoundHandle : IDisposable
{
    void Stop();

    // Completes when the sound finishes on its own, fails, or is stopped.
    //
    // The repeat loop waits on this before timing the gap to the next play.
    // Without it the loop was a fixed period measured from the *start* of each
    // play, so any sound longer than the interval got cut off and restarted -
    // a 5-second alarm on a 1.2-second interval restarted four times and never
    // once finished.
    Task Completion { get; }
}

internal interface ISoundEngine : IDisposable
{
    string Name { get; }

    // False once construction or a play has failed. Never latched permanently:
    // a Bluetooth headset reconnecting is the common case, so Probe() clears it.
    bool IsAvailable { get; }

    string? LastError { get; }

    // Returns null when the engine could not start playback. Callers fall back
    // to the next engine rather than treating this as fatal.
    ISoundHandle? Play(ResolvedSound sound);

    // Re-check availability, e.g. after WM_DEVICECHANGE or on the next alert.
    void Probe();
}
