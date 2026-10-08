using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Notipet.Presence;

internal enum PresenceState
{
    Active,
    Idle,
    Away,
    Locked,
    FullScreen
}

// Tracks whether the user is actually at the desk.
//
// Phase 1 only records this and exposes it on /v1/health; phase 3 routes on it
// (sound at the desk, phone when away). It is built now because the correctness
// detail below is easy to get wrong and expensive to retrofit.
[SupportedOSPlatform("windows")]
internal sealed class PresenceMonitor
{
    private readonly Func<int> _idleThresholdSec;

    // GetLastInputInfo reports nothing while the desktop is locked, so lock
    // state has to arrive out of band - from WM_WTSSESSION_CHANGE, which the
    // tray's hidden window forwards here. This is the single most-missed detail
    // in presence detection: without it a locked machine looks "active" forever
    // at whatever the last input time was.
    private bool _locked;

    public PresenceMonitor(Func<int> idleThresholdSec) => _idleThresholdSec = idleThresholdSec;

    public void SetLocked(bool locked) => _locked = locked;

    public PresenceState Current
    {
        get
        {
            if (_locked) return PresenceState.Locked;

            var state = NotificationState();
            if (state is QUNS_RUNNING_D3D_FULL_SCREEN or QUNS_PRESENTATION_MODE) return PresenceState.FullScreen;

            var idle = IdleTime();
            if (idle >= TimeSpan.FromSeconds(Math.Max(10, _idleThresholdSec()))) return PresenceState.Away;
            if (idle >= TimeSpan.FromMinutes(1)) return PresenceState.Idle;
            return PresenceState.Active;
        }
    }

    public static TimeSpan IdleTime()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;

        // dwTime is a 32-bit tick count that wraps every ~49 days; doing the
        // subtraction in uint makes the wrap work out instead of producing a
        // wildly negative idle time.
        var elapsed = unchecked((uint)Environment.TickCount - info.dwTime);
        return TimeSpan.FromMilliseconds(elapsed);
    }

    // Windows' own do-not-disturb state, consulted rather than duplicated.
    //
    // Only the two states that represent a deliberate user choice count:
    // QUNS_QUIET_TIME (they turned on Do Not Disturb / a Focus session) and
    // QUNS_PRESENTATION_MODE (they explicitly set presentation mode).
    //
    // QUNS_BUSY and QUNS_RUNNING_D3D_FULL_SCREEN are excluded even though the
    // Windows docs group them with the others. QUNS_BUSY fires for *any*
    // full-screen app, and someone reading docs or watching a build in a
    // full-screen terminal is sitting right there and available - suppressing
    // then is precisely backwards. Use PresenceState.FullScreen if a caller
    // genuinely wants to know about full-screen; it is a different question.
    public static bool IsFocusAssistActive()
    {
        var state = NotificationState();
        return state is QUNS_QUIET_TIME or QUNS_PRESENTATION_MODE;
    }

    private static int NotificationState()
    {
        try
        {
            return SHQueryUserNotificationState(out var state) == 0 ? state : 0;
        }
        catch
        {
            return 0;
        }
    }

    private const int QUNS_BUSY = 2;
    private const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;
    private const int QUNS_PRESENTATION_MODE = 4;
    private const int QUNS_QUIET_TIME = 6;

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    public static bool RunSelfTest()
    {
        // The API calls themselves are environment-dependent; assert only what
        // must hold on any machine.
        var idle = IdleTime();
        if (idle < TimeSpan.Zero) return false;
        if (idle > TimeSpan.FromDays(60)) return false;

        var monitor = new PresenceMonitor(() => 300);
        var state = monitor.Current;
        if (state == PresenceState.Locked) return false; // we are running interactively

        // A lock notification must win over whatever the idle timer says.
        monitor.SetLocked(true);
        if (monitor.Current != PresenceState.Locked) return false;
        monitor.SetLocked(false);
        if (monitor.Current == PresenceState.Locked) return false;

        return true;
    }
}
