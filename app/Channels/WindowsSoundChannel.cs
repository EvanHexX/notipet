using System;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Notipet.Core;
using Notipet.Settings;
using Notipet.Sound;

namespace Notipet.Channels;

// The MVP's reason to exist: make a noise.
[SupportedOSPlatform("windows10.0.19041.0")]
internal sealed class WindowsSoundChannel : INotificationChannel
{
    private readonly SoundService _sound;
    private readonly Func<AppSettings> _settings;
    private readonly Func<bool> _atDesk;

    public WindowsSoundChannel(SoundService sound, Func<AppSettings> settings, Func<bool> atDesk)
    {
        _sound = sound;
        _settings = settings;
        _atDesk = atDesk;
    }

    public string Id => AppSettings.Channels_WindowsSound;
    public string DisplayName => Loc.T("Windows sound", "윈도우 사운드");
    public ChannelCapabilities Capabilities => ChannelCapabilities.Sound | ChannelCapabilities.Ack;

    public bool IsEnabled => _settings().Channel(Id).Enabled && _settings().Sound.Enabled;
    public bool IsReady => _sound.Describe().Available;
    public string? LastError => _sound.Describe().LastError;

    public Task<ChannelResult> SendAsync(NotificationEnvelope envelope, CancellationToken ct)
    {
        var settings = _settings();

        if (envelope.Sound?.Mute == true)
        {
            return Task.FromResult(new ChannelResult(Id, DeliveryStatus.Skipped, null, "muted by request"));
        }

        var resolved = SoundResolver.Resolve(
            envelope.Sound, envelope.Level, settings, envelope.Warnings, _atDesk());
        if (resolved.Silent)
        {
            return Task.FromResult(new ChannelResult(Id, DeliveryStatus.Skipped, null, "silenced at desk"));
        }

        var outcome = _sound.Play(resolved, envelope.Level, envelope.Tag);

        if (!outcome.Played)
        {
            // A missing audio endpoint is a channel failure, not a request
            // failure: the balloon still fires and the caller still gets a 200.
            return Task.FromResult(new ChannelResult(Id, DeliveryStatus.Failed, null, outcome.Detail));
        }

        return Task.FromResult(new ChannelResult(Id, DeliveryStatus.Delivered, outcome.AlarmId));
    }

    public Task AckAsync(string? referenceId, CancellationToken ct)
    {
        if (referenceId is null) _sound.Alarms.StopAll();
        else _sound.Alarms.Stop(id: referenceId);
        return Task.CompletedTask;
    }
}
