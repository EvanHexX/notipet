using System;
using System.Collections.Generic;
using System.Linq;
using Notipet.Core;
using Notipet.Settings;
using Notipet.Shared;

namespace Notipet.Rules;

// Which projects and threads may ring. The common setups are "everything,
// except this noisy project / that thread" and "nothing, except the project or
// thread I am waiting on", so: a mode (all | selected) plus explicit on/off
// rules for projects and threads. Most specific wins - a thread's own rule,
// then its project's, then the mode.
//
// Only agent notifications are in scope. A manual send or the tray's test is
// someone asking for that sound now, and critical is for what must not be
// missed (as with mute's allowCritical).
internal sealed record AlertVerdict(bool Rings, string Because);

internal static class AlertScope
{
    public const string ThreadOn = "thread_on";
    public const string ThreadOff = "thread_off";
    public const string ProjectOn = "project_on";
    public const string ProjectOff = "project_off";
    public const string ModeAll = "mode_all";
    public const string NotSelected = "not_selected";
    public const string Exempt = "exempt";

    // Enough for every thread anyone remembers; the oldest go first.
    public const int MaxEntries = 200;

    // sourceId null: an agent, but which one is unknown (matches either).
    public static AlertVerdict Decide(AlertScopeSettings scope, string? sourceId, string? thread, string? project, NotificationLevel level)
    {
        if (sourceId == PayloadMapper.SourceManual || level >= NotificationLevel.Critical) return new(true, Exempt);

        if (FindThread(scope, sourceId, thread) is { } t) return new(t.On, t.On ? ThreadOn : ThreadOff);
        if (FindProject(scope, project) is { } p) return new(p.On, p.On ? ProjectOn : ProjectOff);
        return scope.Mode == AlertScopeModes.Selected ? new(false, NotSelected) : new(true, ModeAll);
    }

    public static AlertScopeEntry? FindThread(AlertScopeSettings scope, string? agent, string? thread) =>
        string.IsNullOrWhiteSpace(thread)
            ? null
            : scope.Threads.LastOrDefault(e => e.Key == thread
                && (e.Agent is null || agent is null || string.Equals(e.Agent, agent, StringComparison.OrdinalIgnoreCase)));

    public static AlertScopeEntry? FindProject(AlertScopeSettings scope, string? project) =>
        string.IsNullOrWhiteSpace(project)
            ? null
            : scope.Projects.LastOrDefault(e => string.Equals(e.Key, project.Trim(), StringComparison.OrdinalIgnoreCase));

    // on: true / false sets a rule, null removes it (back to the mode).
    // Returns whether anything changed.
    public static bool SetThread(AlertScopeSettings scope, string agent, string thread, bool? on, string? label, string? project, DateTimeOffset now)
    {
        var existing = FindThread(scope, agent, thread);
        if (on is null)
        {
            return existing is not null && scope.Threads.RemoveAll(e => e.Key == thread && SameAgent(e.Agent, agent)) > 0;
        }
        if (existing is not null && existing.On == on && SameAgent(existing.Agent, agent))
        {
            // Same rule; keep the newest name for the list.
            if (!string.IsNullOrWhiteSpace(label)) existing.Label = label;
            if (!string.IsNullOrWhiteSpace(project)) existing.Project = project;
            return false;
        }
        scope.Threads.RemoveAll(e => e.Key == thread && SameAgent(e.Agent, agent));
        scope.Threads.Add(new AlertScopeEntry
        {
            Key = thread,
            Agent = agent,
            On = on.Value,
            Label = Clean(label),
            Project = Clean(project),
            Since = now
        });
        Trim(scope.Threads);
        return true;
    }

    public static bool SetProject(AlertScopeSettings scope, string project, bool? on, DateTimeOffset now)
    {
        var key = project.Trim();
        var existing = FindProject(scope, key);
        if (on is null)
        {
            return existing is not null && scope.Projects.RemoveAll(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase)) > 0;
        }
        if (existing is not null && existing.On == on) return false;
        scope.Projects.RemoveAll(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase));
        scope.Projects.Add(new AlertScopeEntry { Key = key, On = on.Value, Since = now });
        Trim(scope.Projects);
        return true;
    }

    public static bool SetMode(AlertScopeSettings scope, string mode)
    {
        var normalized = NormalizeMode(mode);
        if (normalized is null || normalized == scope.Mode) return false;
        scope.Mode = normalized;
        return true;
    }

    public static string? NormalizeMode(string? mode) => mode?.Trim().ToLowerInvariant() switch
    {
        "all" or "everything" => AlertScopeModes.All,
        "selected" or "only" or "on-only" => AlertScopeModes.Selected,
        _ => null
    };

    // A hand-edited file: known mode, no blank keys, one rule per key, capped.
    public static void Normalize(AlertScopeSettings scope)
    {
        scope.Mode = NormalizeMode(scope.Mode) ?? AlertScopeModes.All;
        scope.Projects = Dedupe(scope.Projects, e => e.Key.Trim().ToLowerInvariant());
        foreach (var p in scope.Projects) { p.Key = p.Key.Trim(); p.Agent = null; }
        scope.Threads = Dedupe(scope.Threads, e => (e.Agent ?? "").ToLowerInvariant() + "|" + e.Key.Trim());
        foreach (var t in scope.Threads)
        {
            t.Key = t.Key.Trim();
            t.Agent = string.IsNullOrWhiteSpace(t.Agent) ? null : AgentIdentity.NormalizeAgent(t.Agent);
        }
        Trim(scope.Projects);
        Trim(scope.Threads);
    }

    private static List<AlertScopeEntry> Dedupe(List<AlertScopeEntry>? entries, Func<AlertScopeEntry, string> key)
    {
        var kept = new List<AlertScopeEntry>();
        foreach (var entry in (entries ?? new List<AlertScopeEntry>()).Where(e => e is not null && !string.IsNullOrWhiteSpace(e.Key)))
        {
            // The later rule wins, as it does in Decide.
            kept.RemoveAll(e => key(e) == key(entry));
            kept.Add(entry);
        }
        return kept;
    }

    private static void Trim(List<AlertScopeEntry> entries)
    {
        while (entries.Count > MaxEntries)
        {
            var oldest = entries.OrderBy(e => e.Since ?? DateTimeOffset.MinValue).First();
            entries.Remove(oldest);
        }
    }

    private static bool SameAgent(string? a, string? b) =>
        a is null || b is null || string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string? Clean(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim().Length > 120 ? text.Trim()[..120] : text.Trim();

    public static bool RunSelfTest()
    {
        var now = new DateTimeOffset(2026, 10, 11, 9, 0, 0, TimeSpan.Zero);
        var scope = new AlertScopeSettings();
        const string thread = "019a2b3c-4d5e-7f00-8a9b-0c1d2e3f4a5b";
        bool Rings(string source, string? t, string? p, NotificationLevel l = NotificationLevel.Success) => Decide(scope, source, t, p, l).Rings;

        // Mode all: everything rings until something is turned off.
        if (!Rings("codex", thread, "shop")) return false;

        // A project off silences its threads; another project still rings.
        SetProject(scope, "Shop", false, now);
        if (Rings("codex", thread, "shop") || !Rings("codex", "other", "api")) return false;
        if (Decide(scope, "codex", thread, "shop", NotificationLevel.Success).Because != ProjectOff) return false;

        // A thread's own rule beats its project's.
        SetThread(scope, "codex", thread, true, "checkout", "shop", now);
        if (!Rings("codex", thread, "shop")) return false;
        // ...but only for that agent's thread.
        if (Rings("claude-code", thread, "shop")) return false;

        // Selected mode: only what is turned on.
        SetMode(scope, "selected");
        if (!Rings("codex", thread, "shop") || Rings("codex", "x", "api")) return false;
        if (Decide(scope, "codex", "x", "api", NotificationLevel.Info).Because != NotSelected) return false;
        SetProject(scope, "api", true, now);
        if (!Rings("codex", "x", "api")) return false;

        // Manual sends, tests and critical are exempt.
        if (!Rings(PayloadMapper.SourceManual, null, "nothing") || !Rings("codex", "x", "nothing", NotificationLevel.Critical)) return false;

        // No thread or project: the mode decides.
        if (Rings("codex", null, null)) return false;

        // Reset goes back to the mode; setting the same rule again is no change.
        if (!SetThread(scope, "codex", thread, null, null, null, now) || SetThread(scope, "codex", thread, null, null, null, now)) return false;
        if (Rings("codex", thread, "shop")) return false;   // project shop is off
        if (!SetProject(scope, "api", false, now) || SetProject(scope, "API", false, now)) return false;

        // A hand-edited file: unknown mode -> all; duplicates -> the last one.
        var edited = new AlertScopeSettings
        {
            Mode = "whatever",
            Projects = new() { new() { Key = "shop", On = false }, new() { Key = " Shop ", On = true }, new() { Key = " " } }
        };
        Normalize(edited);
        if (edited.Mode != AlertScopeModes.All || edited.Projects.Count != 1 || !edited.Projects[0].On || edited.Projects[0].Key != "Shop") return false;

        // Capped: the oldest thread rules go.
        var many = new AlertScopeSettings();
        for (var i = 0; i < MaxEntries + 5; i++) SetThread(many, "codex", "t" + i, false, null, null, now.AddMinutes(i));
        if (many.Threads.Count != MaxEntries || FindThread(many, "codex", "t0") is not null || FindThread(many, "codex", "t204") is null) return false;

        // The rule, end to end: suppressed with the reason.
        var settings = new AppSettings();
        settings.Normalize();
        settings.Alerts.Mode = AlertScopeModes.Selected;
        var rule = new AlertScopeRule();
        var context = new RuleContext { Settings = settings, History = new HistoryStore(() => 10), Now = now };
        var decision = rule.Evaluate(new NotificationEnvelope { SourceId = "codex", Project = "shop", Level = NotificationLevel.Success }, context);
        return decision.Outcome == RuleOutcome.Suppress && decision.Reason == NotSelected;
    }
}

// In the rule order right after the source switch: the user said this project
// or thread should not ring, so it does not spend a rate-limit token either.
internal sealed class AlertScopeRule : IDeliveryRule
{
    public string Name => "alert_scope";

    public RuleDecision Evaluate(NotificationEnvelope envelope, RuleContext context)
    {
        var verdict = AlertScope.Decide(context.Settings.Alerts, envelope.SourceId, envelope.SourceSession, envelope.Project, envelope.Level);
        return verdict.Rings ? RuleDecision.Continue : RuleDecision.Suppress(verdict.Because);
    }
}
