using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Notipet.Channels;
using Notipet.Rules;
using Notipet.Settings;
using Notipet.Shared;

namespace Notipet.Core;

// Runs a notification through the rules and then through the channels.
//
// Two structural guarantees worth stating, because they are the whole point of
// the design:
//   1. Local channels run to completion before any remote channel is touched,
//      so a failing or slow push service can never delay or prevent the sound.
//   2. Every channel runs inside its own try/catch, so one broken channel is a
//      failed delivery line in the response, not a failed request.
internal sealed class Dispatcher
{
    private readonly Func<AppSettings> _settings;
    private readonly RuleEngine _rules;
    private readonly RateLimitRule _rateLimit;
    private readonly HistoryStore _history;
    private readonly Func<IReadOnlyList<INotificationChannel>> _channels;
    private readonly Func<bool>? _focusAssist;

    // Raised when a notification was rate-limited and it is time to say so out
    // loud (at most once per interval).
    public event Action<string>? ThrottleAnnouncement;

    public Dispatcher(
        Func<AppSettings> settings,
        RuleEngine rules,
        RateLimitRule rateLimit,
        HistoryStore history,
        Func<IReadOnlyList<INotificationChannel>> channels,
        Func<bool>? focusAssist = null)
    {
        _settings = settings;
        _rules = rules;
        _rateLimit = rateLimit;
        _history = history;
        _channels = channels;
        _focusAssist = focusAssist;
    }

    public async Task<NotifyResponse> DispatchAsync(NotificationEnvelope envelope, CancellationToken ct)
    {
        var settings = _settings();
        var response = new NotifyResponse
        {
            Ok = true,
            Id = envelope.Id,
            Level = NotificationLevelParser.ToWire(envelope.Level),
            ReceivedAt = envelope.ReceivedAt.ToString("o")
        };

        var decision = _rules.Evaluate(envelope, new RuleContext
        {
            Settings = settings,
            History = _history,
            Now = DateTimeOffset.Now,
            FocusAssistActive = _focusAssist
        });

        var entry = new HistoryEntry { Envelope = envelope };

        if (decision.Outcome == RuleOutcome.Collapse && decision.CollapseInto is { } previous)
        {
            _history.NoteCollapse(previous);
            response.Accepted = false;
            response.Suppressed = true;
            response.SuppressedReason = "deduped";
            response.CollapsedWith = previous.Envelope.Id;
            response.Warnings.AddRange(envelope.Warnings);
            return response;
        }

        if (decision.Outcome == RuleOutcome.Suppress)
        {
            entry.Accepted = false;
            entry.SuppressedReason = decision.Reason;
            _history.Add(entry);

            response.Accepted = false;
            response.Suppressed = true;
            response.SuppressedReason = decision.Reason;
            response.NextAllowedAt = decision.NextAllowedAt?.ToString("o");
            response.Warnings.AddRange(envelope.Warnings);

            // Replacing a suppressed flood with a flood of "I suppressed that"
            // would defeat the purpose, so this is throttled hard.
            if (decision.Reason == "rate_limited" && _rateLimit.ShouldAnnounceThrottle(DateTimeOffset.Now))
            {
                ThrottleAnnouncement?.Invoke(envelope.SourceId);
            }
            return response;
        }

        var targets = SelectChannels(envelope, settings, response);
        if (targets.Count == 0)
        {
            entry.Accepted = false;
            entry.SuppressedReason = "all_channels_disabled";
            _history.Add(entry);

            response.Accepted = false;
            response.Suppressed = true;
            response.SuppressedReason = "all_channels_disabled";
            response.Warnings.AddRange(envelope.Warnings);
            return response;
        }

        // Local first, remote last - this ordering is what makes guarantee (1)
        // structural rather than a promise.
        foreach (var channel in targets.OrderBy(c => c.IsRemote ? 1 : 0))
        {
            var result = await SendSafelyAsync(channel, envelope, ct).ConfigureAwait(false);
            entry.Deliveries.Add(result.ToDto());
            response.Deliveries.Add(result.ToDto());
        }

        entry.Accepted = true;
        _history.Add(entry);

        response.Accepted = true;
        response.Warnings.AddRange(envelope.Warnings);
        return response;
    }

    private async Task<ChannelResult> SendSafelyAsync(
        INotificationChannel channel, NotificationEnvelope envelope, CancellationToken ct)
    {
        try
        {
            if (!channel.IsRemote)
            {
                return await channel.SendAsync(envelope, ct).ConfigureAwait(false);
            }

            // A remote channel gets its own budget and can never extend the HTTP
            // response: the hook that called us is on the agent's critical path.
            using var timeout = new CancellationTokenSource(_settings().Server.RemoteTimeoutMs);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            return await channel.SendAsync(envelope, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new ChannelResult(channel.Id, DeliveryStatus.Pending, null, "timed out");
        }
        catch (Exception ex)
        {
            CrashLog.Write($"Channel:{channel.Id}", ex);
            return new ChannelResult(channel.Id, DeliveryStatus.Failed, null, ex.Message);
        }
    }

    private List<INotificationChannel> SelectChannels(
        NotificationEnvelope envelope, AppSettings settings, NotifyResponse response)
    {
        var all = _channels();
        var selected = new List<INotificationChannel>();

        foreach (var channel in all)
        {
            // An explicit request narrows the set but can never enable a channel
            // the user turned off.
            if (envelope.RequestedChannels is { Count: > 0 }
                && !envelope.RequestedChannels.Contains(channel.Id, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!channel.IsEnabled)
            {
                if (envelope.RequestedChannels is { Count: > 0 })
                {
                    response.Deliveries.Add(new ChannelResult(channel.Id, DeliveryStatus.Disabled).ToDto());
                }
                continue;
            }

            selected.Add(channel);
        }

        if (envelope.RequestedChannels is { Count: > 0 })
        {
            foreach (var requested in envelope.RequestedChannels)
            {
                if (!all.Any(c => string.Equals(c.Id, requested, StringComparison.OrdinalIgnoreCase)))
                {
                    envelope.Warnings.Add($"unknown channel '{requested}' ignored");
                }
            }
        }

        return selected;
    }
}
