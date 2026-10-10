using System;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Notipet;

// notipet has no main window: it is a tray daemon. The app object exists only
// to own the dispatcher queue the tray icon and the HTTP handlers marshal onto,
// and to keep the process alive with no window open.
public partial class App : Application
{
    private TrayController? _controller;
    private readonly EventWaitHandle? _showSignal;
    private readonly EventWaitHandle? _settingsSignal;
    private readonly CancellationTokenSource _shutdown = new();

    internal App(EventWaitHandle? showSignal = null, EventWaitHandle? settingsSignal = null)
    {
        _showSignal = showSignal;
        _settingsSignal = settingsSignal;
        InitializeComponent();

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            CrashLog.Write("AppDomain.UnhandledException", args.ExceptionObject as Exception);
            if (args.IsTerminating) DaemonLog.Write("terminating: unhandled exception (see crash.log)");
        };
        // The last word on any exit that runs managed code - a clean quit
        // logs its reason first, so this line after it is expected. A kill
        // leaves nothing; the next start reports that instead.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => DaemonLog.Write($"process exit (code {Environment.ExitCode})");
        UnhandledException += (_, args) =>
        {
            CrashLog.Write("App.UnhandledException", args.Exception);
            // A notification daemon that dies on one bad notification is worse
            // than one that logs and keeps listening.
            args.Handled = true;
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Without a window, the default shutdown mode would exit as soon as
        // OnLaunched returns.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;

        try
        {
            _controller = new TrayController();
        }
        catch (Exception ex)
        {
            CrashLog.Write("TrayController.Start", ex);
            Exit();
            return;
        }

        WatchForSecondLaunch();
    }

    // A second launch signals the running instance rather than starting a rival
    // daemon. The friendly response is to show what has been happening - it used
    // to fire a test notification, which meant double-clicking the exe made a
    // noise instead of telling you anything.
    private void WatchForSecondLaunch()
    {
        var queue = DispatcherQueue.GetForCurrentThread();
        Watch(_showSignal, () => _controller?.ShowHistory(), "notipet-show-watch", queue);
        Watch(_settingsSignal, () => _controller?.ShowSettings(), "notipet-settings-watch", queue);
    }

    private void Watch(EventWaitHandle? signal, Action action, string name, DispatcherQueue queue)
    {
        if (signal is null) return;

        var thread = new Thread(() =>
        {
            while (!_shutdown.IsCancellationRequested)
            {
                try
                {
                    if (!signal.WaitOne(500)) continue;
                }
                catch
                {
                    return;
                }
                queue.TryEnqueue(() => action());
            }
        })
        {
            IsBackground = true,
            Name = name
        };
        thread.Start();
    }

    internal void Shutdown()
    {
        _shutdown.Cancel();
        _controller?.Dispose();
        _controller = null;
    }
}
