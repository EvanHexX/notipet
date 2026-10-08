using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Notipet.Channels;
using Notipet.Core;
using Notipet.Http;
using Notipet.Presence;
using Notipet.Rules;
using Notipet.Settings;
using Notipet.Shared;
using Notipet.Sound;

namespace Notipet.SelfTest;

// Starts a real NotipetHttpServer on an ephemeral port with a stub channel and
// drives it over a socket.
//
// This is the most valuable check in the suite: it exercises the actual delivery
// path - bind, route, auth, body limit, envelope, rules, dispatch - headlessly,
// without a tray icon, a window, or a sound.
[SupportedOSPlatform("windows10.0.19041.0")]
internal static class HttpSelfTest
{
    public static bool Run()
    {
        // This test drives routes that save settings. It must only ever touch
        // its own throwaway file, never the user's - so fingerprint the real
        // one and fail loudly if it changes.
        var realBefore = Fingerprint(Paths.SettingsPath);
        var passed = RunCore();
        if (Fingerprint(Paths.SettingsPath) != realBefore)
        {
            Console.Error.WriteLine("  HttpSelfTest: the user's real settings.json was modified by the test");
            return false;
        }
        return passed;
    }

    private static string Fingerprint(string path)
    {
        try
        {
            return File.Exists(path)
                ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))
                : "missing";
        }
        catch
        {
            return "unreadable";
        }
    }

    private static bool RunCore()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), "notipet-http-" + Guid.NewGuid().ToString("N") + ".json");
        var settings = AppSettings.Load(settingsPath);
        // Keep the stub channel the only one, and take the rate limiter out of
        // the way so the test asserts routing rather than throttling.
        settings.Channel(AppSettings.Channels_WindowsSound).Enabled = false;
        settings.Channel(AppSettings.Channels_TrayBalloon).Enabled = false;
        settings.RateLimit.Enabled = false;
        settings.Dedupe.Enabled = false;

        var stub = new StubChannel();
        var channels = new List<INotificationChannel> { stub };
        var history = new HistoryStore(() => settings.History.KeepInMemory);
        var rateLimit = new RateLimitRule();
        var sound = new SoundService(() => settings);

        var dispatcher = new Dispatcher(
            () => settings,
            new RuleEngine(rateLimit),
            rateLimit,
            history,
            () => channels);

        var token = AuthGuard.GenerateToken();
        NotipetHttpServer? server = null;
        var shutdownRequested = false;

        try
        {
            // Bind first so the guard can be built with the real port; the guard
            // validates Host against it.
            var probe = new NotipetHttpServer(new RouteTable(), new AuthGuard(token, 0));
            probe.Start(0);
            var port = probe.Port;
            probe.Dispose();

            var guard = new AuthGuard(token, port);
            var api = new ApiContext
            {
                Settings = () => settings,
                Dispatcher = dispatcher,
                History = history,
                Sound = sound,
                Channels = () => channels,
                Presence = new PresenceMonitor(() => 300),
                Guard = guard,
                InstanceId = "selftest",
                Version = "test",
                Port = () => port,
                Warnings = () => Array.Empty<string>(),
                SettingsChanged = () => { },
                Shutdown = () => shutdownRequested = true
            };

            server = new NotipetHttpServer(ApiRoutes.Build(api), guard);
            server.Start(port);
            if (server.Port != port) return false;

            var host = $"127.0.0.1:{server.Port}";

            // Alive, unauthenticated: allowed, but it must not leak state.
            var health = Send(server.Port, "GET /v1/health HTTP/1.1", host, null, null, null);
            if (health.Status != 200) return false;
            if (!health.Body.Contains("\"instanceId\":\"selftest\"", StringComparison.Ordinal)) return false;
            if (health.Body.Contains("soundEngine", StringComparison.Ordinal)) return false;
            if (health.Headers.Contains("Access-Control-Allow-Origin", StringComparison.OrdinalIgnoreCase)) return false;

            // Authenticated health carries the detail.
            var healthAuth = Send(server.Port, "GET /v1/health HTTP/1.1", host, token, null, null);
            if (healthAuth.Status != 200) return false;
            if (!healthAuth.Body.Contains("soundEngine", StringComparison.Ordinal)) return false;

            // No token, a wrong token: both refused.
            var body = "{\"title\":\"hi\",\"body\":\"there\"}";
            if (Send(server.Port, "POST /v1/notify HTTP/1.1", host, null, body, null).Status != 401) return false;
            if (Send(server.Port, "POST /v1/notify HTTP/1.1", host, "not-the-token", body, null).Status != 401) return false;

            // The happy path, and the stub really received it.
            stub.Reset();
            var notify = Send(server.Port, "POST /v1/notify HTTP/1.1", host, token, body, null);
            if (notify.Status != 200) return false;
            if (!notify.Body.Contains("\"accepted\":true", StringComparison.Ordinal)) return false;
            if (stub.Received.Count != 1) return false;
            if (stub.Received[0].Title != "hi" || stub.Received[0].Body != "there") return false;

            // A valid token plus a browser Origin is still refused: this is the
            // check that survives the token leaking.
            if (Send(server.Port, "POST /v1/notify HTTP/1.1", host, token, body, "http://evil.test").Status != 401) return false;
            // ... and so is Sec-Fetch-Site from a real page.
            if (Send(server.Port, "POST /v1/notify HTTP/1.1", host, token, body, null, "cross-site").Status != 401) return false;

            // A rebinding attempt arrives with someone else's name in Host.
            if (Send(server.Port, "POST /v1/notify HTTP/1.1", "evil.test", token, body, null).Status != 401) return false;

            // Oversized body is refused by the limit, not by the parser.
            var huge = "{\"title\":\"" + new string('x', 100_000) + "\"}";
            if (Send(server.Port, "POST /v1/notify HTTP/1.1", host, token, huge, null).Status != 413) return false;

            // Malformed JSON and an empty payload are both 400s with a reason.
            if (Send(server.Port, "POST /v1/notify HTTP/1.1", host, token, "{ not json", null).Status != 400) return false;
            var empty = Send(server.Port, "POST /v1/notify HTTP/1.1", host, token, "{}", null);
            if (empty.Status != 400) return false;
            if (!empty.Body.Contains("validation_failed", StringComparison.Ordinal)) return false;

            // An unknown route 404s rather than falling through to anything.
            if (Send(server.Port, "GET /nope HTTP/1.1", host, token, null, null).Status != 404) return false;

            // The hook route maps a raw Claude payload without a CLI involved.
            stub.Reset();
            var hookBody = "{\"session_id\":\"s9\",\"hook_event_name\":\"Notification\"," +
                           "\"notification_type\":\"agent_needs_input\",\"cwd\":\"C:\\\\src\\\\notipet\"}";
            var hook = Send(server.Port, "POST /hooks/claude-code HTTP/1.1", host, token, hookBody, null);
            if (hook.Status != 200) return false;
            if (stub.Received.Count != 1) return false;
            if (stub.Received[0].Level != NotificationLevel.Attention) return false;
            if (stub.Received[0].Title != "Claude Code - notipet") return false;

            // Suppression is a 200 with a reason, never a 4xx: a hook that sees
            // a non-2xx may misbehave, and quiet hours are not a client error.
            settings.Mute.Enabled = true;
            var muted = Send(server.Port, "POST /v1/notify HTTP/1.1", host, token, body, null);
            if (muted.Status != 200) return false;
            if (!muted.Body.Contains("\"suppressedReason\":\"muted\"", StringComparison.Ordinal)) return false;
            if (!muted.Body.Contains("\"accepted\":false", StringComparison.Ordinal)) return false;
            settings.Mute.Enabled = false;

            // History records what happened, including the suppression.
            var historyResponse = Send(server.Port, "GET /v1/history?limit=10 HTTP/1.1", host, token, null, null);
            if (historyResponse.Status != 200) return false;
            if (!historyResponse.Body.Contains("\"suppressedReason\":\"muted\"", StringComparison.Ordinal)) return false;

            // Who and where travel the whole way: request -> envelope -> history.
            var placed = "{\"title\":\"placed\",\"tag\":\"selftest:placed\",\"source\":{\"id\":\"Claude\"," +
                         "\"project\":\"shop\",\"threadTitle\":\"Checkout refactor\",\"session\":\"s-77\"}}";
            if (Send(server.Port, "POST /v1/notify HTTP/1.1", host, token, placed, null).Status != 200) return false;
            var placedHistory = Send(server.Port, "GET /v1/history?limit=50 HTTP/1.1", host, token, null, null).Body;
            if (!placedHistory.Contains("\"project\":\"shop\"", StringComparison.Ordinal)) return false;
            if (!placedHistory.Contains("\"threadTitle\":\"Checkout refactor\"", StringComparison.Ordinal)) return false;
            if (!placedHistory.Contains("\"thread\":\"s-77\"", StringComparison.Ordinal)) return false;
            // The agent's spelling is normalised on the way in - checked on
            // that entry itself, since an earlier hook entry also says
            // claude-code and would satisfy a search of the whole response.
            var stored = history.Recent(int.MaxValue).FirstOrDefault(e => e.Envelope.Tag == "selftest:placed");
            if (stored is null || stored.Envelope.SourceId != "claude-code" || stored.Envelope.Project != "shop") return false;

            // An old client sending none of it, and a newer one sending fields
            // this daemon has never heard of, are both just fine.
            var oldShape = "{\"title\":\"old\",\"tag\":\"selftest:old\",\"source\":{\"id\":\"codex\",\"cwd\":\"E:\\\\Work\\\\shop\"}}";
            if (Send(server.Port, "POST /v1/notify HTTP/1.1", host, token, oldShape, null).Status != 200) return false;
            var future = "{\"title\":\"future\",\"tag\":\"selftest:future\",\"source\":{\"id\":\"codex\",\"workspace\":{\"x\":1},\"rank\":3}}";
            if (Send(server.Port, "POST /v1/notify HTTP/1.1", host, token, future, null).Status != 200) return false;
            // A thread id that could bend a URL is dropped, not a 400.
            var crooked = "{\"title\":\"crooked\",\"tag\":\"selftest:crooked\",\"source\":{\"id\":\"codex\",\"session\":\"x?prompt=hi\"}}";
            var crookedResponse = Send(server.Port, "POST /v1/notify HTTP/1.1", host, token, crooked, null);
            if (crookedResponse.Status != 200 || !crookedResponse.Body.Contains("thread ignored", StringComparison.Ordinal)) return false;

            // Channels are enumerable, with their readiness.
            var channelList = Send(server.Port, "GET /v1/channels HTTP/1.1", host, token, null, null);
            if (channelList.Status != 200 || !channelList.Body.Contains("stub", StringComparison.Ordinal)) return false;

            // Ack answers even when nothing is sounding.
            var ack = Send(server.Port, "POST /v1/ack HTTP/1.1", host, token, "{\"all\":true}", null);
            if (ack.Status != 200 || !ack.Body.Contains("\"stopped\":0", StringComparison.Ordinal)) return false;

            // Presence: an empty body toggles, an explicit value sets.
            settings.Presence.AtDesk = false;
            var toggled = Send(server.Port, "POST /v1/presence HTTP/1.1", host, token, "{}", null);
            if (toggled.Status != 200 || !toggled.Body.Contains("\"atDesk\":true", StringComparison.Ordinal)) return false;
            var set = Send(server.Port, "POST /v1/presence HTTP/1.1", host, token, "{\"atDesk\":false}", null);
            if (set.Status != 200 || !set.Body.Contains("\"atDesk\":false", StringComparison.Ordinal)) return false;
            if (settings.Presence.AtDesk) return false;
            if (Send(server.Port, "POST /v1/presence HTTP/1.1", host, null, "{}", null).Status != 401) return false;

            // Clearing history needs the token, empties the store, and reports
            // how many entries went.
            if (Send(server.Port, "DELETE /v1/history HTTP/1.1", host, null, null, null).Status != 401) return false;
            var before = history.Count;
            if (before == 0) return false;
            var cleared = Send(server.Port, "DELETE /v1/history HTTP/1.1", host, token, null, null);
            if (cleared.Status != 200 || !cleared.Body.Contains($"\"cleared\":{before}", StringComparison.Ordinal)) return false;
            if (history.Count != 0) return false;

            // Shutdown answers first and acts after, and refuses without a token.
            if (Send(server.Port, "POST /v1/shutdown HTTP/1.1", host, null, "{}", null).Status != 401) return false;
            if (shutdownRequested) return false;
            if (Send(server.Port, "POST /v1/shutdown HTTP/1.1", host, token, "{}", null).Status != 200) return false;
            var waitUntil = DateTime.UtcNow.AddSeconds(2);
            while (!shutdownRequested && DateTime.UtcNow < waitUntil) Thread.Sleep(20);
            if (!shutdownRequested) return false;

            // A channel that throws must not fail the request.
            stub.Reset();
            stub.ThrowOnSend = true;
            var broken = Send(server.Port, "POST /v1/notify HTTP/1.1", host, token, body, null);
            if (broken.Status != 200) return false;
            if (!broken.Body.Contains("\"status\":\"failed\"", StringComparison.Ordinal)) return false;
            stub.ThrowOnSend = false;

            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("  HttpSelfTest: " + ex);
            return false;
        }
        finally
        {
            server?.Dispose();
            sound.Dispose();
            try { if (File.Exists(settingsPath)) File.Delete(settingsPath); } catch { }
        }
    }

    private readonly record struct Reply(int Status, string Headers, string Body);

    // A hand-rolled request rather than HttpClient, for two reasons: the app
    // project deliberately contains no HTTP client at all so that "notipet never
    // talks to the network" is structural, and raw headers make it trivial to
    // forge the browser-shaped requests the guard is supposed to refuse.
    private static Reply Send(
        int port, string requestLine, string host, string? token,
        string? body, string? origin, string? fetchSite = null)
    {
        using var client = new TcpClient();
        client.Connect("127.0.0.1", port);
        using var stream = client.GetStream();

        var request = new StringBuilder();
        request.Append(requestLine).Append("\r\n");
        request.Append("Host: ").Append(host).Append("\r\n");
        request.Append("Connection: close\r\n");
        if (token is not null) request.Append("Authorization: Bearer ").Append(token).Append("\r\n");
        if (origin is not null) request.Append("Origin: ").Append(origin).Append("\r\n");
        if (fetchSite is not null) request.Append("Sec-Fetch-Site: ").Append(fetchSite).Append("\r\n");
        if (body is not null)
        {
            var bytes = Encoding.UTF8.GetByteCount(body);
            request.Append("Content-Type: application/json\r\n");
            request.Append("Content-Length: ").Append(bytes).Append("\r\n");
        }
        request.Append("\r\n");
        if (body is not null) request.Append(body);

        var payload = Encoding.UTF8.GetBytes(request.ToString());
        stream.Write(payload, 0, payload.Length);
        stream.Flush();

        using var reader = new StreamReader(stream, Encoding.UTF8);
        var raw = reader.ReadToEnd();

        var split = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var head = split < 0 ? raw : raw[..split];
        var content = split < 0 ? "" : raw[(split + 4)..];

        var statusLine = head.Split("\r\n")[0].Split(' ');
        var status = statusLine.Length > 1 && int.TryParse(statusLine[1], out var parsed) ? parsed : 0;
        return new Reply(status, head, content);
    }

    private sealed class StubChannel : INotificationChannel
    {
        public List<NotificationEnvelope> Received { get; } = new();
        public bool ThrowOnSend { get; set; }

        public string Id => "stub";
        public string DisplayName => "stub";
        public ChannelCapabilities Capabilities => ChannelCapabilities.Visual;
        public bool IsEnabled => true;
        public bool IsReady => true;
        public string? LastError => null;

        public void Reset() => Received.Clear();

        public Task<ChannelResult> SendAsync(NotificationEnvelope envelope, CancellationToken ct)
        {
            if (ThrowOnSend) throw new InvalidOperationException("stub failure");
            Received.Add(envelope);
            return Task.FromResult(new ChannelResult(Id, DeliveryStatus.Delivered));
        }
    }
}
