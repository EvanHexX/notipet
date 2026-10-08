using System;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Notipet.Core;
using Notipet.Settings;
using Notipet.Shared;
using Notipet.Tray;

namespace Notipet.Channels;

// The MVP's visual channel.
//
// Windows 11 renders a shell balloon as a toast, so this already looks like a
// modern notification. What it does not give is action buttons or Action Center
// persistence - that needs AppNotificationManager, which in an unpackaged app
// needs an AUMID and a Start-menu shortcut and can fail silently. That trade is
// why the MVP ships the proven path and leaves the rich toast to its own
// channel, off by default.
[SupportedOSPlatform("windows")]
internal sealed class TrayBalloonChannel : INotificationChannel
{
    private readonly Func<ITrayIcon?> _tray;
    private readonly Func<AppSettings> _settings;
    private readonly Action<Action> _onUiThread;

    public TrayBalloonChannel(Func<ITrayIcon?> tray, Func<AppSettings> settings, Action<Action> onUiThread)
    {
        _tray = tray;
        _settings = settings;
        _onUiThread = onUiThread;
    }

    public string Id => AppSettings.Channels_TrayBalloon;
    public string DisplayName => Loc.T("Tray notification", "트레이 알림");
    public ChannelCapabilities Capabilities => ChannelCapabilities.Visual;

    public bool IsEnabled => _settings().Channel(Id).Enabled;
    public bool IsReady => _tray() is not null;
    public string? LastError => IsReady ? null : "tray icon not created";

    public Task<ChannelResult> SendAsync(NotificationEnvelope envelope, CancellationToken ct)
    {
        var tray = _tray();
        if (tray is null) return Task.FromResult(new ChannelResult(Id, DeliveryStatus.Failed, null, LastError));

        var level = envelope.Level switch
        {
            NotificationLevel.Error or NotificationLevel.Critical => BalloonLevel.Error,
            NotificationLevel.Warn or NotificationLevel.Attention => BalloonLevel.Warning,
            _ => BalloonLevel.Info
        };

        // Shell_NotifyIcon must be called from the thread that owns the window,
        // and we arrive here on an HTTP worker.
        _onUiThread(() => tray.ShowNotification(envelope.Title, envelope.Body, level));
        return Task.FromResult(new ChannelResult(Id, DeliveryStatus.Delivered));
    }
}
