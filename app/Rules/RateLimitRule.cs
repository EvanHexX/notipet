using System;
using System.Collections.Generic;
using Notipet.Core;
using Notipet.Settings;
using Notipet.Shared;

namespace Notipet.Rules;

// Token bucket, one per source plus a global one.
//
// Claude Code hooks can fire on every tool call. Without this the tray becomes a
// machine gun and the tool gets uninstalled within an hour, so the rate limiter
// is not an optimisation - it is what makes the thing usable.
internal sealed class RateLimitRule : IDeliveryRule
{
    private sealed class Bucket
    {
        public double Tokens;
        public DateTimeOffset LastRefill;
    }

    private readonly Dictionary<string, Bucket> _perSource = new(StringComparer.OrdinalIgnoreCase);
    private readonly Bucket _global = new();
    private readonly object _gate = new();
    private bool _initialized;

    // So the tray can say "notipet is rate-limiting X" at most occasionally,
    // rather than replacing the flood it just suppressed with a flood of its own.
    public DateTimeOffset LastThrottleNotice { get; set; } = DateTimeOffset.MinValue;
    public static readonly TimeSpan ThrottleNoticeInterval = TimeSpan.FromMinutes(10);

    public string Name => "rate_limit";

    public RuleDecision Evaluate(NotificationEnvelope envelope, RuleContext context)
    {
        var settings = context.Settings.RateLimit;
        if (!settings.Enabled) return RuleDecision.Continue;

        lock (_gate)
        {
            if (!_initialized)
            {
                _global.Tokens = settings.Global.Capacity;
                _global.LastRefill = context.Now;
                _initialized = true;
            }

            var source = GetBucket(envelope.SourceId, settings.PerSource, context.Now);

            Refill(source, settings.PerSource, context.Now);
            Refill(_global, settings.Global, context.Now);

            // critical skips the per-source bucket but never the global one: a
            // single misbehaving source must not be able to monopolise the
            // machine's attention just by labelling everything critical.
            var needsSourceToken = envelope.Level < NotificationLevel.Critical;

            if (needsSourceToken && source.Tokens < 1)
            {
                return RuleDecision.Suppress("rate_limited", NextAllowed(source, settings.PerSource, context.Now));
            }
            if (_global.Tokens < 1)
            {
                return RuleDecision.Suppress("rate_limited", NextAllowed(_global, settings.Global, context.Now));
            }

            if (needsSourceToken) source.Tokens -= 1;
            _global.Tokens -= 1;
            return RuleDecision.Continue;
        }
    }

    public bool ShouldAnnounceThrottle(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (now - LastThrottleNotice < ThrottleNoticeInterval) return false;
            LastThrottleNotice = now;
            return true;
        }
    }

    private Bucket GetBucket(string sourceId, BucketSettings settings, DateTimeOffset now)
    {
        if (!_perSource.TryGetValue(sourceId, out var bucket))
        {
            bucket = new Bucket { Tokens = settings.Capacity, LastRefill = now };
            _perSource[sourceId] = bucket;
        }
        return bucket;
    }

    private static void Refill(Bucket bucket, BucketSettings settings, DateTimeOffset now)
    {
        var elapsed = (now - bucket.LastRefill).TotalMinutes;
        if (elapsed <= 0) return;
        bucket.LastRefill = now;
        bucket.Tokens = Math.Min(settings.Capacity, bucket.Tokens + elapsed * settings.RefillPerMinute);
    }

    private static DateTimeOffset? NextAllowed(Bucket bucket, BucketSettings settings, DateTimeOffset now)
    {
        if (settings.RefillPerMinute <= 0) return null;
        var needed = 1 - bucket.Tokens;
        return now.AddMinutes(needed / settings.RefillPerMinute);
    }

    public static bool RunSelfTest()
    {
        var settings = new AppSettings();
        settings.Normalize();
        settings.RateLimit.PerSource = new BucketSettings { Capacity = 3, RefillPerMinute = 6 };
        settings.RateLimit.Global = new BucketSettings { Capacity = 100, RefillPerMinute = 100 };

        var rule = new RateLimitRule();
        var history = new HistoryStore(() => 10);
        var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        RuleDecision Fire(NotificationLevel level, DateTimeOffset at) =>
            rule.Evaluate(
                new NotificationEnvelope { SourceId = "claude-code", Level = level },
                new RuleContext { Settings = settings, History = history, Now = at });

        // The bucket drains.
        for (var i = 0; i < 3; i++)
        {
            if (Fire(NotificationLevel.Info, now).Outcome != RuleOutcome.Continue) return false;
        }
        var blocked = Fire(NotificationLevel.Info, now);
        if (blocked.Outcome != RuleOutcome.Suppress || blocked.Reason != "rate_limited") return false;
        if (blocked.NextAllowedAt is null) return false;

        // critical bypasses the per-source bucket while it is empty.
        if (Fire(NotificationLevel.Critical, now).Outcome != RuleOutcome.Continue) return false;

        // It refills over time: 6/min means one token back in 10 seconds.
        if (Fire(NotificationLevel.Info, now.AddSeconds(11)).Outcome != RuleOutcome.Continue) return false;

        // A different source has its own bucket and is unaffected.
        var other = rule.Evaluate(
            new NotificationEnvelope { SourceId = "codex", Level = NotificationLevel.Info },
            new RuleContext { Settings = settings, History = history, Now = now });
        if (other.Outcome != RuleOutcome.Continue) return false;

        // The global bucket stops even critical traffic once exhausted.
        settings.RateLimit.Global = new BucketSettings { Capacity = 0, RefillPerMinute = 0 };
        var exhausted = new RateLimitRule();
        var globalBlocked = exhausted.Evaluate(
            new NotificationEnvelope { SourceId = "x", Level = NotificationLevel.Critical },
            new RuleContext { Settings = settings, History = history, Now = now });
        if (globalBlocked.Outcome != RuleOutcome.Suppress) return false;

        // Disabling the limiter lets everything through.
        settings.RateLimit.Enabled = false;
        if (Fire(NotificationLevel.Info, now).Outcome != RuleOutcome.Continue) return false;

        // The throttle notice is announced at most once per interval.
        var announcer = new RateLimitRule();
        if (!announcer.ShouldAnnounceThrottle(now)) return false;
        if (announcer.ShouldAnnounceThrottle(now.AddMinutes(1))) return false;
        if (!announcer.ShouldAnnounceThrottle(now.Add(ThrottleNoticeInterval).AddSeconds(1))) return false;

        return true;
    }
}
