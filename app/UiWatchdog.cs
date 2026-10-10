using System;
using System.Threading;
using Microsoft.UI.Dispatching;

namespace Notipet;

// Notices when the UI thread stops answering, and hands over to a recovery.
//
// A hung UI thread is the worst way for Notipet to fail: the HTTP API runs on
// its own threads and keeps answering, so hooks get their 200s and nothing
// looks wrong - while no pop-up appears, the tray does not respond and the
// windows are frozen. That happened (docs/regression.md, "the UI thread hung in
// MediaPlayer"); whatever the next cause is, it should end in a restart, not
// in silence.
//
// Every few seconds it queues a no-op on the UI thread and checks that the
// previous one ran. Only a long run of misses counts, so a busy moment, a
// modal menu or a sleep/resume (this thread sleeps too) never trips it.
internal sealed class UiWatchdog : IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);
    public const int MissesBeforeRecovery = 12;   // one minute

    private readonly DispatcherQueue _queue;
    private readonly Action _onHung;
    private readonly Thread _thread;
    private volatile bool _stopped;
    private int _answered = 1;

    public UiWatchdog(DispatcherQueue queue, Action onHung)
    {
        _queue = queue;
        _onHung = onHung;
        _thread = new Thread(Loop) { IsBackground = true, Name = "notipet-ui-watchdog" };
        _thread.Start();
    }

    private void Loop()
    {
        var misses = 0;
        while (!_stopped)
        {
            if (Interlocked.Exchange(ref _answered, 0) == 1)
            {
                misses = 0;
                if (!_queue.TryEnqueue(() => Volatile.Write(ref _answered, 1)))
                {
                    // The queue is shutting down: the app is exiting.
                    return;
                }
            }
            else if (++misses >= MissesBeforeRecovery)
            {
                if (!_stopped) _onHung();
                return;
            }
            Thread.Sleep(Interval);
        }
    }

    public void Dispose() => _stopped = true;
}
