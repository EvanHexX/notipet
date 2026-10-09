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
// Bounded: a burst of notifications must not cover the screen. Past
// MaxVisible the oldest card goes - but a card that was meant to stay until
// clicked is not dropped silently: it is counted on an overflow card at the
// top ("+3 more"), which opens Recent notifications, where every one of them
// still is. A card that would have closed by itself anyway just closes.
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
    private readonly Action _showHistory;
    private readonly List<NotificationPopup> _open = new();

    // Cards that stayed until clicked but were pushed off the stack, by
    // notification id - so a resolved one can be taken off the count too.
    private readonly HashSet<string> _overflowIds = new(StringComparer.Ordinal);
    private NotificationPopup? _overflowCard;

    // While pop-ups are open and "show over full-screen apps" is on, they are
    // put back on top every couple of seconds: a window that turns full-screen
    // and topmost after a pop-up appeared would otherwise cover it.
    private readonly Microsoft.UI.Xaml.DispatcherTimer _keepOnTop = new() { Interval = TimeSpan.FromSeconds(2) };

    public PopupHost(Func<AppSettings> settings, Func<NotificationEnvelope, Uri?> link, Action stopAlarms,
        Action<NotificationEnvelope> openThread, Action showHistory)
    {
        _settings = settings;
        _link = link;
        _stopAlarms = stopAlarms;
        _openThread = openThread;
        _showHistory = showHistory;
        _keepOnTop.Tick += (_, _) =>
        {
            if (Cards().Count == 0 || !_settings().Popup.ShowOverFullscreen) { _keepOnTop.Stop(); return; }
            // Oldest first, so the newest ends up on top.
            foreach (var card in Cards()) card.RaiseTopmost();
        };
    }

    public int OpenCount => _open.Count;
    public int OverflowCount => _overflowIds.Count;

    // Must run on the UI thread.
    public void Show(NotificationEnvelope envelope)
    {
        var options = _settings().Popup;
        var fullScreen = PresenceMonitor.IsFullScreenForeground();
        var topmost = options.ShowOverFullscreen || !fullScreen;
        var belowForeground = fullScreen && !options.ShowOverFullscreen;

        var popup = NotificationPopup.ForNotification(envelope, _link(envelope), options.Stays(envelope.Level),
            _stopAlarms, () => _openThread(envelope));
        popup.Closed += closed =>
        {
            _open.Remove(closed);
            Layout();
        };
        _open.Add(popup);

        // Make room. With an overflow card on screen it takes one of the slots.
        while (_open.Count > (_overflowIds.Count > 0 ? MaxVisible - 1 : MaxVisible))
        {
            var oldest = _open[0];
            if (oldest.Stays && oldest.Envelope is { } pushed) _overflowIds.Add(pushed.Id);
            oldest.Close();
        }
        UpdateOverflowCard(topmost, belowForeground);

        var positions = Positions();
        popup.Show(positions[^1], topmost, belowForeground);
        Layout();
        if (options.ShowOverFullscreen)
        {
            foreach (var card in Cards()) card.RaiseTopmost();
            _keepOnTop.Start();
        }

        if (!popup.Stays) popup.CloseAfter(TimeSpan.FromSeconds(options.TimeoutSec));
    }

    // The sender said these are over (/v1/resolve): close their cards and take
    // them off the "+N more" count. Returns the ids that were on screen or in
    // the count; one already closed is simply not there. Must run on the UI
    // thread.
    public IReadOnlyCollection<string> CloseFor(IReadOnlyCollection<string> ids)
    {
        var wanted = new HashSet<string>(ids, StringComparer.Ordinal);
        var closed = new List<string>();
        foreach (var popup in _open.ToArray())
        {
            if (popup.Envelope is not { } envelope || !wanted.Contains(envelope.Id)) continue;
            popup.Close();
            closed.Add(envelope.Id);
        }

        var uncounted = _overflowIds.RemoveWhere(id =>
        {
            if (!wanted.Contains(id)) return false;
            closed.Add(id);
            return true;
        });
        if (uncounted > 0)
        {
            if (_overflowIds.Count == 0) ClearOverflow();
            else _overflowCard?.SetOverflowCount(_overflowIds.Count);
        }
        return closed;
    }

    // The theme changed in Settings: cards already on screen follow it.
    public void ApplyTheme()
    {
        foreach (var card in Cards()) card.ApplyTheme();
    }

    public void CloseAll()
    {
        foreach (var popup in _open.ToArray()) popup.Close();
        ClearOverflow();
    }

    private void UpdateOverflowCard(bool topmost, bool belowForeground)
    {
        if (_overflowIds.Count == 0) return;
        if (_overflowCard is not null)
        {
            _overflowCard.SetOverflowCount(_overflowIds.Count);
            return;
        }

        _overflowCard = NotificationPopup.ForOverflow(_overflowIds.Count,
            onOpen: () => { ClearOverflow(); _showHistory(); },
            onDismiss: ClearOverflow);
        _overflowCard.Closed += _ =>
        {
            _overflowCard = null;
            _overflowIds.Clear();
            Layout();
        };
        // Placed by Layout(); shown where the stack's top will be.
        _overflowCard.Show(Positions()[0], topmost, belowForeground);
    }

    private void ClearOverflow()
    {
        _overflowIds.Clear();
        var card = _overflowCard;
        _overflowCard = null;
        card?.Close();
        Layout();
    }

    // What is on screen, top to bottom: the overflow card (it stands for the
    // oldest ones), then the cards oldest first.
    private List<NotificationPopup> Cards()
    {
        var cards = new List<NotificationPopup>(_open.Count + 1);
        if (_overflowCard is not null) cards.Add(_overflowCard);
        cards.AddRange(_open);
        return cards;
    }

    private void Layout()
    {
        var cards = Cards();
        var positions = Positions();
        for (var i = 0; i < cards.Count; i++) cards[i].MoveTo(positions[i]);
    }

    // One position per card in Cards() order: the newest sits at the bottom,
    // older ones above it, the overflow card on top.
    private List<PointInt32> Positions()
    {
        var cards = Cards();
        var work = DisplayArea.Primary.WorkArea;
        var scale = cards.Count > 0 ? (double)cards[0].PixelSize.Width / NotificationPopup.WidthDip : 1.0;
        var margin = (int)(MarginDip * scale);
        var gap = (int)(GapDip * scale);

        var positions = new PointInt32[cards.Count];
        var bottom = work.Y + work.Height - margin;
        for (var i = cards.Count - 1; i >= 0; i--)
        {
            var size = cards[i].PixelSize;
            var top = bottom - size.Height;
            positions[i] = new PointInt32(work.X + work.Width - size.Width - margin, top);
            bottom = top - gap;
        }
        return new List<PointInt32>(positions);
    }
}
