using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Notipet.Channels;
using Notipet.Core;
using Notipet.Presence;
using Notipet.Rules;
using Notipet.Settings;
using Notipet.Shared;
using Notipet.Sound;

namespace Notipet.Http;

// Everything the routes need, so the route table itself stays a table.
internal sealed class ApiContext
{
    public required Func<AppSettings> Settings { get; init; }
    public required Dispatcher Dispatcher { get; init; }
    public required HistoryStore History { get; init; }
    public required SoundService Sound { get; init; }
    public required Func<IReadOnlyList<INotificationChannel>> Channels { get; init; }
    public required PresenceMonitor Presence { get; init; }
    public required AuthGuard Guard { get; init; }
    public required string InstanceId { get; init; }
    public required string Version { get; init; }
    public required Func<int> Port { get; init; }
    public required Func<IReadOnlyList<string>> Warnings { get; init; }
    public required Action SettingsChanged { get; init; }

    // Changes the at-desk switch; null toggles. Returns the new state. Goes
    // through the tray controller so the menu check mark follows.
    public Func<bool?, bool>? SetAtDesk { get; init; }

    // Quits the daemon. Runs after the response is written.
    public Action? Shutdown { get; init; }

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;
}

[SupportedOSPlatform("windows10.0.19041.0")]
internal static class ApiRoutes
{
    public static RouteTable Build(ApiContext api)
    {
        var routes = new RouteTable();

        routes.Map("GET", "/v1/health", ctx => HealthAsync(api, ctx));
        // Aliases, following the house convention of answering the obvious ones.
        routes.Map("GET", "/health", ctx => HealthAsync(api, ctx));
        routes.Map("GET", "/healthz", ctx => HealthAsync(api, ctx));

        routes.Map("POST", "/v1/notify", ctx => NotifyAsync(api, ctx));
        routes.Map("POST", "/v1/ack", ctx => AckAsync(api, ctx));
        routes.Map("POST", "/v1/mute", ctx => MuteAsync(api, ctx));
        routes.Map("POST", "/v1/test", ctx => TestAsync(api, ctx));
        routes.Map("GET", "/v1/channels", ctx => ChannelsAsync(api, ctx));
        routes.Map("GET", "/v1/history", ctx => HistoryAsync(api, ctx));
        routes.Map("DELETE", "/v1/history", ctx => ClearHistoryAsync(api, ctx));
        routes.Map("POST", "/v1/history/clear", ctx => ClearHistoryAsync(api, ctx));
        routes.Map("POST", "/v1/presence", ctx => PresenceAsync(api, ctx));
        routes.Map("POST", "/v1/shutdown", ctx => ShutdownAsync(api, ctx));

        // Claude Code and Codex can both POST their raw hook JSON here, which
        // is how the native `http` hook type reaches notipet with no CLI in the
        // middle.
        routes.Map("POST", "/hooks/claude-code", ctx => HookAsync(api, ctx, PayloadMapper.SourceClaude));
        routes.Map("POST", "/hooks/codex", ctx => HookAsync(api, ctx, PayloadMapper.SourceCodex));
        routes.Map("POST", "/hooks/agent", ctx => HookAsync(api, ctx, null));

        return routes;
    }

    private static async Task HealthAsync(ApiContext api, HttpListenerContext ctx)
    {
        var response = new HealthResponse
        {
            Ok = true,
            Name = "notipet",
            Version = api.Version,
            InstanceId = api.InstanceId
        };

        // An unauthenticated probe learns only that we are alive and who we are:
        // enough for the CLI to detect a stale runtime.json, not enough to leak
        // what the user is being notified about.
        if (api.Guard.IsAuthenticated(ctx.Request))
        {
            var settings = api.Settings();
            response.Pid = Environment.ProcessId;
            response.Port = api.Port();
            response.StartedAt = api.StartedAt.ToString("o");
            response.UptimeSec = (DateTimeOffset.Now - api.StartedAt).TotalSeconds;
            response.Muted = settings.Mute.Enabled || settings.Mute.Until > DateTimeOffset.Now;
            response.AtDesk = settings.Presence.AtDesk;
            response.Language = settings.Language;
            response.HistoryCount = api.History.Count;
            response.QuietHoursActive = IsQuietNow(settings);
            response.Presence = api.Presence.Current.ToString();
            response.ActiveAlarms = api.Sound.Alarms.Count;
            response.SoundEngine = api.Sound.Describe();
            var warnings = api.Warnings();
            response.Warnings = warnings.Count > 0 ? warnings.ToList() : null;
        }

        await HttpJson.WriteAsync(ctx, 200, response, NotipetJson.Compact.HealthResponse).ConfigureAwait(false);
    }

    private static async Task NotifyAsync(ApiContext api, HttpListenerContext ctx)
    {
        api.Guard.Require(ctx.Request);
        AuthGuard.RequireJsonContentType(ctx.Request);

        var request = await HttpJson.ReadAsync(
            ctx, api.Settings().Server.MaxBodyBytes, NotipetJson.Compact.NotifyRequest).ConfigureAwait(false);

        if (!EnvelopeFactory.TryCreate(request, null, out var envelope, out var error))
        {
            throw HttpApiException.Validation(error!, "title");
        }

        var response = await api.Dispatcher.DispatchAsync(envelope!, CancellationToken.None).ConfigureAwait(false);
        await HttpJson.WriteAsync(ctx, 200, response, NotipetJson.Compact.NotifyResponse).ConfigureAwait(false);
    }

    private static async Task HookAsync(ApiContext api, HttpListenerContext ctx, string? sourceHint)
    {
        api.Guard.Require(ctx.Request);
        AuthGuard.RequireJsonContentType(ctx.Request);

        var hook = await HttpJson.ReadAsync(
            ctx, api.Settings().Server.MaxBodyBytes, NotipetJson.Compact.AgentHookEvent).ConfigureAwait(false);

        var request = PayloadMapper.FromHookEvent(hook, sourceHint);

        // The mapper always produces something usable, so a failure here would
        // be a bug rather than bad input - fall back rather than 400 a hook.
        if (!EnvelopeFactory.TryCreate(request, sourceHint, out var envelope, out _))
        {
            var fallback = PayloadMapper.GenericFallback(sourceHint ?? PayloadMapper.SourceManual, "Agent event");
            EnvelopeFactory.TryCreate(fallback, sourceHint, out envelope, out _);
        }

        var response = await api.Dispatcher.DispatchAsync(envelope!, CancellationToken.None).ConfigureAwait(false);
        await HttpJson.WriteAsync(ctx, 200, response, NotipetJson.Compact.NotifyResponse).ConfigureAwait(false);
    }

    private static async Task AckAsync(ApiContext api, HttpListenerContext ctx)
    {
        api.Guard.Require(ctx.Request);
        AuthGuard.RequireJsonContentType(ctx.Request);

        var request = await HttpJson.ReadAsync(
            ctx, api.Settings().Server.MaxBodyBytes, NotipetJson.Compact.AckRequest).ConfigureAwait(false);

        // No selector at all means "make it stop", which is what someone
        // reaching for this endpoint in a hurry means.
        var all = request.All ?? (request.Id is null && request.Tag is null);
        var stopped = api.Sound.Alarms.Stop(request.Id, request.Tag, all);

        await HttpJson.WriteAsync(ctx, 200, new AckResponse { Ok = true, Stopped = stopped },
            NotipetJson.Compact.AckResponse).ConfigureAwait(false);
    }

    private static async Task MuteAsync(ApiContext api, HttpListenerContext ctx)
    {
        api.Guard.Require(ctx.Request);
        AuthGuard.RequireJsonContentType(ctx.Request);

        var request = await HttpJson.ReadAsync(
            ctx, api.Settings().Server.MaxBodyBytes, NotipetJson.Compact.MuteRequest).ConfigureAwait(false);

        var settings = api.Settings();
        var muted = request.Muted ?? true;

        if (muted && request.Minutes is > 0)
        {
            settings.Mute.Enabled = false;
            settings.Mute.Until = DateTimeOffset.Now.AddMinutes(request.Minutes.Value);
        }
        else
        {
            settings.Mute.Enabled = muted;
            settings.Mute.Until = null;
        }

        settings.Save();
        api.SettingsChanged();

        await HttpJson.WriteAsync(ctx, 200, new MuteResponse
        {
            Ok = true,
            Muted = settings.Mute.Enabled || settings.Mute.Until > DateTimeOffset.Now,
            Until = settings.Mute.Until?.ToString("o")
        }, NotipetJson.Compact.MuteResponse).ConfigureAwait(false);
    }

    private static async Task TestAsync(ApiContext api, HttpListenerContext ctx)
    {
        api.Guard.Require(ctx.Request);

        var level = QueryValue(ctx, "level") ?? "attention";
        var request = new NotifyRequest
        {
            Title = "notipet",
            Body = Loc.T("Test notification", "테스트 알림"),
            Level = level,
            // A test that got swallowed by dedupe would be a bad test.
            Tag = "notipet:test:" + Guid.NewGuid().ToString("N")[..8],
            Source = new SourceInfo { Id = PayloadMapper.SourceManual }
        };

        EnvelopeFactory.TryCreate(request, PayloadMapper.SourceManual, out var envelope, out _);
        var response = await api.Dispatcher.DispatchAsync(envelope!, CancellationToken.None).ConfigureAwait(false);
        await HttpJson.WriteAsync(ctx, 200, response, NotipetJson.Compact.NotifyResponse).ConfigureAwait(false);
    }

    private static async Task ChannelsAsync(ApiContext api, HttpListenerContext ctx)
    {
        api.Guard.Require(ctx.Request);
        var response = new ChannelsResponse
        {
            Channels = api.Channels().Select(c => c.Describe()).ToList()
        };
        await HttpJson.WriteAsync(ctx, 200, response, NotipetJson.Compact.ChannelsResponse).ConfigureAwait(false);
    }

    private static async Task HistoryAsync(ApiContext api, HttpListenerContext ctx)
    {
        api.Guard.Require(ctx.Request);
        var limit = int.TryParse(QueryValue(ctx, "limit"), out var parsed) ? Math.Clamp(parsed, 1, 500) : 50;
        var response = new HistoryResponse
        {
            Entries = api.History.Recent(limit).Select(e => e.ToDto()).ToList()
        };
        await HttpJson.WriteAsync(ctx, 200, response, NotipetJson.Compact.HistoryResponse).ConfigureAwait(false);
    }

    private static async Task ClearHistoryAsync(ApiContext api, HttpListenerContext ctx)
    {
        api.Guard.Require(ctx.Request);
        var cleared = api.History.Clear();
        await HttpJson.WriteAsync(ctx, 200, new ClearHistoryResponse { Ok = true, Cleared = cleared },
            NotipetJson.Compact.ClearHistoryResponse).ConfigureAwait(false);
    }

    private static async Task PresenceAsync(ApiContext api, HttpListenerContext ctx)
    {
        api.Guard.Require(ctx.Request);
        AuthGuard.RequireJsonContentType(ctx.Request);

        var request = await HttpJson.ReadAsync(
            ctx, api.Settings().Server.MaxBodyBytes, NotipetJson.Compact.PresenceRequest).ConfigureAwait(false);

        bool atDesk;
        if (api.SetAtDesk is not null)
        {
            atDesk = api.SetAtDesk(request.AtDesk);
        }
        else
        {
            var settings = api.Settings();
            settings.Presence.AtDesk = request.AtDesk ?? !settings.Presence.AtDesk;
            settings.Save();
            api.SettingsChanged();
            atDesk = settings.Presence.AtDesk;
        }

        await HttpJson.WriteAsync(ctx, 200, new PresenceResponse { Ok = true, AtDesk = atDesk },
            NotipetJson.Compact.PresenceResponse).ConfigureAwait(false);
    }

    // Lets `notipet stop` and the publish script quit the daemon without a
    // human reaching for the tray. Behind the same token and browser checks as
    // everything else, so a web page cannot use it to knock the daemon over.
    private static async Task ShutdownAsync(ApiContext api, HttpListenerContext ctx)
    {
        api.Guard.Require(ctx.Request);
        await HttpJson.WriteAsync(ctx, 200, new OkResponse { Ok = true, Message = "shutting down" },
            NotipetJson.Compact.OkResponse).ConfigureAwait(false);

        // After the response is on the wire, so the caller hears back.
        _ = Task.Run(async () =>
        {
            await Task.Delay(150).ConfigureAwait(false);
            api.Shutdown?.Invoke();
        });
    }

    // Hand-rolled rather than pulling in System.Web.HttpUtility: the API has
    // exactly two query parameters and both are simple scalars.
    private static string? QueryValue(HttpListenerContext ctx, string key)
    {
        var query = ctx.Request.Url?.Query;
        if (string.IsNullOrEmpty(query)) return null;

        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = pair.IndexOf('=');
            if (split <= 0) continue;
            if (!Uri.UnescapeDataString(pair[..split]).Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            return Uri.UnescapeDataString(pair[(split + 1)..]);
        }
        return null;
    }

    public static bool IsQuietNow(AppSettings settings)
    {
        var rule = new QuietHoursRule();
        var probe = new NotificationEnvelope { Level = NotificationLevel.Info };
        var decision = rule.Evaluate(probe, new RuleContext
        {
            Settings = settings,
            History = new HistoryStore(() => 1),
            Now = DateTimeOffset.Now,
            FocusAssistActive = PresenceMonitor.IsFocusAssistActive
        });
        return decision.Outcome == RuleOutcome.Suppress;
    }
}
