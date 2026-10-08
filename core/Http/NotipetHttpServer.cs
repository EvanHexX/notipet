using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Notipet.Http;

internal delegate Task RouteHandler(HttpListenerContext context);

// (method, path) -> handler, with one terminal 404, mirroring the route tables
// in the house Node servers.
internal sealed class RouteTable
{
    private readonly Dictionary<(string Method, string Path), RouteHandler> _routes = new();

    public RouteTable Map(string method, string path, RouteHandler handler)
    {
        _routes[(method.ToUpperInvariant(), path.TrimEnd('/'))] = handler;
        return this;
    }

    public RouteHandler? Resolve(string method, string path) =>
        _routes.TryGetValue((method.ToUpperInvariant(), path.TrimEnd('/')), out var handler) ? handler : null;
}

// Loopback-only JSON server on HttpListener.
//
// HttpListener over ASP.NET Core, deliberately: this process already ships a
// self-contained WindowsAppSDK plus a self-contained runtime, and adding the
// ASP.NET Core shared framework for five JSON routes would add tens of megabytes
// and a second application lifetime to fight with the tray's. HttpListener is
// already in Microsoft.NETCore.App, needs no admin and no URL ACL for a
// 127.0.0.1 prefix, and is one object the tray controller can own.
internal sealed class NotipetHttpServer : IDisposable
{
    private readonly RouteTable _routes;
    private readonly AuthGuard _guard;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    public int Port { get; private set; }
    public List<string> Warnings { get; } = new();

    public NotipetHttpServer(RouteTable routes, AuthGuard guard)
    {
        _routes = routes;
        _guard = guard;
    }

    // Never fails to start over a port conflict: a notification daemon that
    // silently is not running is the worst possible outcome.
    public void Start(int preferredPort)
    {
        var attempts = new List<int>();
        if (preferredPort > 0)
        {
            for (var offset = 0; offset < 10; offset++) attempts.Add(preferredPort + offset);
        }

        foreach (var port in attempts)
        {
            if (TryBind(port))
            {
                if (port != preferredPort)
                {
                    Warnings.Add($"port {preferredPort} was unavailable; listening on {port}");
                }
                return;
            }
        }

        // Ephemeral fallback, and the default when no port is pinned.
        for (var retry = 0; retry < 5; retry++)
        {
            if (TryBind(FindFreePort()))
            {
                if (preferredPort > 0)
                {
                    Warnings.Add($"port {preferredPort} and the 9 after it were unavailable; listening on {Port}");
                }
                return;
            }
        }

        throw new InvalidOperationException("could not bind any loopback port");
    }

    private bool TryBind(int port)
    {
        var listener = new HttpListener();
        // Only 127.0.0.1. A non-loopback prefix would need an admin URL ACL,
        // and exposing this API off-machine is never wanted.
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        try
        {
            listener.Start();
        }
        catch (HttpListenerException)
        {
            listener.Close();
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        _listener = listener;
        Port = port;
        _cts = new CancellationTokenSource();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(listener, _cts.Token));
        return true;
    }

    // HttpListener cannot bind port 0, so the free port is found with a throwaway
    // socket and then handed over. That leaves a small window in which something
    // else could take it, which is why Start retries.
    private static int FindFreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try
        {
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }

    private async Task AcceptLoopAsync(HttpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (HttpListenerException)
            {
                return; // listener stopped
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (InvalidOperationException)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(context), CancellationToken.None);
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            // Applies to every request including the unauthenticated health
            // probe, so a web page cannot even enumerate the API.
            if (!_guard.PassesBrowserChecks(context.Request))
            {
                await HttpJson.WriteErrorAsync(context, HttpApiException.Unauthorized()).ConfigureAwait(false);
                return;
            }

            var path = context.Request.Url?.AbsolutePath ?? "/";
            var handler = _routes.Resolve(context.Request.HttpMethod, path);
            if (handler is null)
            {
                await HttpJson.WriteErrorAsync(context, HttpApiException.NotFound()).ConfigureAwait(false);
                return;
            }

            await handler(context).ConfigureAwait(false);
        }
        catch (HttpApiException api)
        {
            try { await HttpJson.WriteErrorAsync(context, api).ConfigureAwait(false); } catch { }
        }
        catch (Exception ex)
        {
            CrashLog.Write("HttpServer.Handle", ex);
            try
            {
                await HttpJson.WriteErrorAsync(context, new HttpApiException(500, "internal_error")).ConfigureAwait(false);
            }
            catch
            {
            }
        }
        finally
        {
            try { context.Response.Close(); } catch { }
        }
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        try { _listener?.Close(); } catch { }
        try { _acceptLoop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _cts?.Dispose();
        _listener = null;
    }
}
