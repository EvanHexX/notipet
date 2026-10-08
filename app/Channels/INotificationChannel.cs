using System;
using System.Threading;
using System.Threading.Tasks;
using Notipet.Core;
using Notipet.Shared;

namespace Notipet.Channels;

[Flags]
internal enum ChannelCapabilities
{
    None = 0,
    Sound = 1,
    Visual = 2,
    Remote = 4,
    Actions = 8,
    Ack = 16
}

internal enum DeliveryStatus
{
    Delivered,
    Skipped,
    Failed,
    Disabled,
    Pending
}

internal sealed record ChannelResult(
    string ChannelId,
    DeliveryStatus Status,
    string? ReferenceId = null,
    string? Detail = null)
{
    public DeliveryResult ToDto() => new()
    {
        Channel = ChannelId,
        Status = Status switch
        {
            DeliveryStatus.Delivered => "delivered",
            DeliveryStatus.Skipped => "skipped",
            DeliveryStatus.Failed => "failed",
            DeliveryStatus.Disabled => "disabled",
            _ => "pending"
        },
        ReferenceId = ReferenceId,
        Detail = Detail
    };
}

// A place a notification can come out.
//
// By contract SendAsync never throws; the dispatcher wraps it anyway, because a
// contract is not an enforcement mechanism and one broken channel must not take
// the others down with it.
internal interface INotificationChannel
{
    string Id { get; }
    string DisplayName { get; }
    ChannelCapabilities Capabilities { get; }

    bool IsEnabled { get; }

    // Enabled but not usable - no audio endpoint, no endpoint configured, a
    // registration that failed. Reported separately so the reason is visible.
    bool IsReady { get; }

    string? LastError { get; }

    // True for anything that leaves the machine. The dispatcher runs these last
    // and under a budget so a slow remote call can never delay the local sound.
    bool IsRemote => Capabilities.HasFlag(ChannelCapabilities.Remote);

    Task<ChannelResult> SendAsync(NotificationEnvelope envelope, CancellationToken ct);

    Task AckAsync(string? referenceId, CancellationToken ct) => Task.CompletedTask;

    ChannelInfo Describe() => new()
    {
        Id = Id,
        DisplayName = DisplayName,
        Enabled = IsEnabled,
        Ready = IsReady,
        LastError = LastError
    };
}
