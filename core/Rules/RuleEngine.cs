using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Notipet.Core;
using Notipet.Settings;
using Notipet.Shared;

namespace Notipet.Rules;

internal enum RuleOutcome
{
    Continue,
    Suppress,
    Collapse
}

internal sealed record RuleDecision(
    RuleOutcome Outcome,
    string? Reason = null,
    DateTimeOffset? NextAllowedAt = null,
    HistoryEntry? CollapseInto = null)
{
    public static readonly RuleDecision Continue = new(RuleOutcome.Continue);
    public static RuleDecision Suppress(string reason, DateTimeOffset? nextAllowedAt = null) =>
        new(RuleOutcome.Suppress, reason, nextAllowedAt);
    public static RuleDecision Collapse(HistoryEntry into) =>
        new(RuleOutcome.Collapse, "deduped", null, into);
}

internal sealed class RuleContext
{
    public required AppSettings Settings { get; init; }
    public required HistoryStore History { get; init; }
    public required DateTimeOffset Now { get; init; }
    public Func<bool>? FocusAssistActive { get; init; }
}

internal interface IDeliveryRule
{
    string Name { get; }
    RuleDecision Evaluate(NotificationEnvelope envelope, RuleContext context);
}

// Fixed evaluation order. Cheap and caller-scoped checks first, then global
// ones, so a disabled source never consumes a rate-limit token.
internal sealed class RuleEngine
{
    private readonly IDeliveryRule[] _rules;

    public RuleEngine(RateLimitRule rateLimit)
    {
        _rules = new IDeliveryRule[]
        {
            new TtlRule(),
            new SourceRule(),
            new AlertScopeRule(),
            new MuteRule(),
            new QuietHoursRule(),
            new DedupeRule(),
            rateLimit
        };
    }

    public RuleDecision Evaluate(NotificationEnvelope envelope, RuleContext context)
    {
        foreach (var rule in _rules)
        {
            var decision = rule.Evaluate(envelope, context);
            if (decision.Outcome != RuleOutcome.Continue) return decision;
        }
        return RuleDecision.Continue;
    }
}

// A notification that sat in a queue past its own deadline is stale news.
internal sealed class TtlRule : IDeliveryRule
{
    public string Name => "ttl";

    public RuleDecision Evaluate(NotificationEnvelope envelope, RuleContext context) =>
        envelope.IsExpired ? RuleDecision.Suppress("ttl_expired") : RuleDecision.Continue;
}

internal sealed class SourceRule : IDeliveryRule
{
    public string Name => "source";

    public RuleDecision Evaluate(NotificationEnvelope envelope, RuleContext context)
    {
        var source = context.Settings.Source(envelope.SourceId);
        return source.Enabled ? RuleDecision.Continue : RuleDecision.Suppress("source_disabled");
    }
}

internal sealed class MuteRule : IDeliveryRule
{
    public string Name => "mute";

    public RuleDecision Evaluate(NotificationEnvelope envelope, RuleContext context)
    {
        var mute = context.Settings.Mute;
        var timedMuteActive = mute.Until is { } until && until > context.Now;
        if (!mute.Enabled && !timedMuteActive) return RuleDecision.Continue;

        if (mute.AllowCritical && envelope.Level >= NotificationLevel.Critical) return RuleDecision.Continue;

        return RuleDecision.Suppress("muted", timedMuteActive ? mute.Until : null);
    }
}

internal sealed class QuietHoursRule : IDeliveryRule
{
    public string Name => "quiet_hours";

    public RuleDecision Evaluate(NotificationEnvelope envelope, RuleContext context)
    {
        var quiet = context.Settings.QuietHours;

        var inWindow = quiet.Enabled
            && TryParse(quiet.Start, out var start)
            && TryParse(quiet.End, out var end)
            && IsWithin(TimeOnly.FromDateTime(context.Now.LocalDateTime), start, end);

        // Windows' own do-not-disturb is consulted rather than duplicated, so a
        // user-set focus session is honoured without configuring quiet hours
        // twice.
        //
        // Gated on quiet.Enabled on purpose. This setting lives under
        // quietHours, so evaluating it independently made the nesting a lie:
        // with the defaults (enabled=false, respectFocusAssist=true) a user who
        // had never touched quiet hours still lost every notification below
        // critical the moment Windows reported DND. Silently eating alerts
        // nobody opted out of is the worst thing a notification daemon can do.
        var focusActive = quiet.Enabled
            && quiet.RespectFocusAssist
            && (context.FocusAssistActive?.Invoke() ?? false);

        if (!inWindow && !focusActive) return RuleDecision.Continue;

        // allowLevels is a floor, not an exact match: listing "error" lets
        // error and critical through, which is what someone setting it means.
        var allowed = quiet.AllowLevels ?? new List<string>();
        if (allowed.Count > 0)
        {
            var floor = allowed.Select(level => NotificationLevelParser.Parse(level)).Min();
            if (envelope.Level >= floor) return RuleDecision.Continue;
        }

        return RuleDecision.Suppress(focusActive && !inWindow ? "focus_assist" : "quiet_hours");
    }

    private static bool TryParse(string? value, out TimeOnly time) =>
        TimeOnly.TryParseExact(value ?? "", "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    // Overnight windows wrap past midnight, which is the classic off-by-one in
    // this kind of rule: 23:00-08:00 has to mean "at or after 23:00, OR before
    // 08:00", not "between" in the numeric sense.
    public static bool IsWithin(TimeOnly now, TimeOnly start, TimeOnly end)
    {
        // A degenerate window covers nothing rather than everything: silencing
        // the app forever is never what someone meant by start == end.
        if (start == end) return false;
        return start < end
            ? now >= start && now < end
            : now >= start || now < end;
    }

    public static bool RunSelfTest()
    {
        TimeOnly T(int h, int m) => new(h, m);

        // Overnight window.
        var start = T(23, 0);
        var end = T(8, 0);
        if (!IsWithin(T(23, 30), start, end)) return false;
        if (!IsWithin(T(23, 0), start, end)) return false;
        if (!IsWithin(T(0, 30), start, end)) return false;
        if (!IsWithin(T(7, 59), start, end)) return false;
        if (IsWithin(T(8, 0), start, end)) return false;
        if (IsWithin(T(8, 1), start, end)) return false;
        if (IsWithin(T(12, 0), start, end)) return false;
        if (IsWithin(T(22, 59), start, end)) return false;

        // Same-day window.
        if (!IsWithin(T(13, 0), T(12, 0), T(14, 0))) return false;
        if (IsWithin(T(11, 59), T(12, 0), T(14, 0))) return false;
        if (IsWithin(T(14, 0), T(12, 0), T(14, 0))) return false;

        // Degenerate window silences nothing.
        if (IsWithin(T(3, 0), T(9, 0), T(9, 0))) return false;

        // And the rule itself, end to end.
        var settings = new AppSettings();
        settings.Normalize();
        settings.QuietHours.Enabled = true;
        settings.QuietHours.Start = "00:00";
        settings.QuietHours.End = "23:59";
        settings.QuietHours.RespectFocusAssist = false;

        var rule = new QuietHoursRule();
        var context = new RuleContext
        {
            Settings = settings,
            History = new HistoryStore(() => 10),
            Now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero)
        };

        var info = new NotificationEnvelope { Level = NotificationLevel.Info };
        if (rule.Evaluate(info, context).Outcome != RuleOutcome.Suppress) return false;

        // Focus Assist must not suppress anything while quiet hours is off.
        // This is the regression that ate real notifications: the defaults are
        // enabled=false + respectFocusAssist=true, and evaluating the latter
        // independently meant a user who never configured quiet hours lost
        // every alert below critical whenever Windows reported DND.
        var focusOnly = new AppSettings();
        focusOnly.Normalize();
        focusOnly.QuietHours.Enabled = false;
        focusOnly.QuietHours.RespectFocusAssist = true;
        var focusContext = new RuleContext
        {
            Settings = focusOnly,
            History = new HistoryStore(() => 10),
            Now = context.Now,
            FocusAssistActive = () => true   // pretend Windows says "do not disturb"
        };
        if (rule.Evaluate(info, focusContext).Outcome != RuleOutcome.Continue) return false;

        // With quiet hours on, the same signal does suppress, and says which
        // of the two reasons it was.
        focusOnly.QuietHours.Enabled = true;
        focusOnly.QuietHours.Start = "00:00";
        focusOnly.QuietHours.End = "00:01";   // a window that excludes ~now
        var suppressed = rule.Evaluate(info, focusContext);
        if (suppressed.Outcome != RuleOutcome.Suppress) return false;
        if (suppressed.Reason != "focus_assist") return false;

        // ...and turning the sub-setting off restores delivery even then.
        focusOnly.QuietHours.RespectFocusAssist = false;
        if (rule.Evaluate(info, focusContext).Outcome != RuleOutcome.Continue) return false;

        // critical is on the allow list by default and must still get through.
        var critical = new NotificationEnvelope { Level = NotificationLevel.Critical };
        if (rule.Evaluate(critical, context).Outcome != RuleOutcome.Continue) return false;

        // A floor of "warn" lets warn, error and critical through but not info.
        settings.QuietHours.AllowLevels = new List<string> { "warn" };
        if (rule.Evaluate(info, context).Outcome != RuleOutcome.Suppress) return false;
        if (rule.Evaluate(new NotificationEnvelope { Level = NotificationLevel.Warn }, context).Outcome != RuleOutcome.Continue) return false;
        if (rule.Evaluate(new NotificationEnvelope { Level = NotificationLevel.Error }, context).Outcome != RuleOutcome.Continue) return false;
        settings.QuietHours.AllowLevels = new List<string> { "critical" };

        settings.QuietHours.Enabled = false;
        if (rule.Evaluate(info, context).Outcome != RuleOutcome.Continue) return false;

        // A malformed time must not silence everything.
        settings.QuietHours.Enabled = true;
        settings.QuietHours.Start = "not-a-time";
        if (rule.Evaluate(info, context).Outcome != RuleOutcome.Continue) return false;

        return true;
    }
}

internal sealed class DedupeRule : IDeliveryRule
{
    public string Name => "dedupe";

    public RuleDecision Evaluate(NotificationEnvelope envelope, RuleContext context)
    {
        var dedupe = context.Settings.Dedupe;
        if (!dedupe.Enabled || dedupe.WindowSec <= 0) return RuleDecision.Continue;

        var previous = context.History.FindRecent(envelope.DedupeKey, TimeSpan.FromSeconds(dedupe.WindowSec));
        if (previous is null) return RuleDecision.Continue;

        // Collapse folds the repeat into the existing entry and stays quiet;
        // without collapse it is simply dropped. Either way it does not sound
        // twice - a permission prompt that fires three times is one event.
        return dedupe.Collapse && envelope.Collapse
            ? RuleDecision.Collapse(previous)
            : RuleDecision.Suppress("deduped");
    }
}
