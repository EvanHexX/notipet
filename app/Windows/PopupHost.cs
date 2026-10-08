using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using Microsoft.UI.Windowing;
using Notipet.Core;
using Notipet.Presence;
using Notipet.Settings;
using Windows.Graphics;

namespace Notipet.Windows;

// Shows notipet's own pop-ups, stacked upward from the bottom-right corner of
// the primary screen's work area (above the taskbar), newest at the bottom
// like Windows' own. When one closes the rest close the gap.
//
// Bounded: a burst of notifications must not cover the screen, so past
// MaxVisible the oldest goes. (The rate limit already caps bursts; this caps
// what is left on screen when nobody is there to click.)
[SupportedOSPlatform("windows10.0.19041.0")]
internal sealed class PopupHost
{
    public const int MaxVisible = 4;
    private const int MarginDip = 12;
    private const int GapDip = 8;

    private readonly Func<AppSettings> _settings;
    private readonly Func<NotificationEnvelope, Uri?> _link;
    private readonly Action _stopAlarms;
    private readonly Action<NotificationEnvelope> _openThread;
    private readonly List<NotificationPopup> _open = new();

    // While pop-ups are open and "show over full-screen apps" is on, they are
    // put back on top every couple of seconds: a window that turns full-screen
    // and topmost after a pop-up appeared would otherwise cover it.
    private readonly Microsoft.UI.Xaml.DispatcherTimer _keepOnTop = new() { Interval = TimeSpan.FromSeconds(2) };

    public PopupHost(Func<AppSettings> settings, Func<NotificationEnvelope, Uri?> link, Action stopAlarms, Action<NotificationEnvelope> openThread)
    {
        _settings = settings;
        _link = link;
        _stopAlarms = stopAlarms;
        _openThread = openThread;
        _keepOnTop.Tick += (_, _) =>
        {
            if (_open.Count == 0 || !_settings().Popup.ShowOverFullscreen) { _keepOnTop.Stop(); return; }
            // Oldest first, so the newest ends up on top.
            foreach (var popup in _open.ToArray()) popup.RaiseTopmost();
        };
    }

    public int OpenCount => _open.Count;

    // Must run on the UI thread.
    public void Show(NotificationEnvelope envelope)
    {
        var options = _settings().Popup;
        var fullScreen = PresenceMonitor.IsFullScreenForeground();

        var popup = new NotificationPopup(envelope, _link(envelope), _stopAlarms, () => _openThread(envelope));
        popup.Closed += closed =>
        {
            _open.Remove(closed);
            Layout();
        };
        _open.Add(popup);
        while (_open.Count > MaxVisible) _open[0].Close();

        var positions = Positions();
        var topmost = options.ShowOverFullscreen || !fullScreen;
        popup.Show(positions[^1], topmost, belowForeground: fullScreen && !options.ShowOverFullscreen);
        Layout();
        if (options.ShowOverFullscreen)
        {
            foreach (var older in _open.ToArray()) older.RaiseTopmost();
            _keepOnTop.Start();
        }

        if (!options.StayUntilClicked) popup.CloseAfter(TimeSpan.FromSeconds(options.TimeoutSec));
    }

    public void CloseAll()
    {
        foreach (var popup in _open.ToArray()) popup.Close();
    }

    private void Layout()
    {
        var positions = Positions();
        for (var i = 0; i < _open.Count; i++) _open[i].MoveTo(positions[i]);
    }

    // One position per open pop-up, oldest first: the newest sits at the
    // bottom, older ones above it.
    private List<PointInt32> Positions()
    {
        var work = DisplayArea.Primary.WorkArea;
        var scale = _open.Count > 0 ? (double)_open[0].PixelSize.Width / NotificationPopup.WidthDip : 1.0;
        var margin = (int)(MarginDip * scale);
        var gap = (int)(GapDip * scale);

        var positions = new PointInt32[_open.Count];
        var bottom = work.Y + work.Height - margin;
        for (var i = _open.Count - 1; i >= 0; i--)
        {
            var size = _open[i].PixelSize;
            var top = bottom - size.Height;
            positions[i] = new PointInt32(work.X + work.Width - size.Width - margin, top);
            bottom = top - gap;
        }
        return new List<PointInt32>(positions);
    }
}
