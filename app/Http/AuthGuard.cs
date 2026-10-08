using System;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Notipet.Http;

// Threat model, stated plainly because pretending otherwise would be worse:
// loopback is not a trust boundary. Any process running as this user can read
// runtime.json and therefore the token, and DPAPI does not help because it
// unprotects for the same user. The token defends against exactly two things,
// and both are real:
//
//   1. Browser-origin CSRF and DNS rebinding. A page you visit can POST to
//      http://127.0.0.1:<port>/ from JavaScript. Without a token that works.
//   2. Other user accounts and low-integrity processes on the same machine.
//
// The Origin / Sec-Fetch-Site check below is what actually kills (1), and it
// keeps working even if the token leaks into a log somewhere.
internal sealed class AuthGuard
{
    private readonly byte[] _token;
    private readonly int _port;

    public AuthGuard(string token, int port)
    {
        _token = Encoding.UTF8.GetBytes(token);
        _port = port;
    }

    public static string GenerateToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    public bool IsAuthenticated(HttpListenerRequest request)
    {
        if (!PassesBrowserChecks(request)) return false;

        var header = request.Headers["Authorization"];
        if (string.IsNullOrEmpty(header)) return false;
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;

        var presented = Encoding.UTF8.GetBytes(header["Bearer ".Length..].Trim());
        return CryptographicOperations.FixedTimeEquals(presented, _token);
    }

    public void Require(HttpListenerRequest request)
    {
        if (!IsAuthenticated(request)) throw HttpApiException.Unauthorized();
    }

    // Applies to every request, authenticated or not.
    public bool PassesBrowserChecks(HttpListenerRequest request)
    {
        // A browser always sends Origin on a cross-origin request and always
        // sends Sec-Fetch-Site. The CLI sends neither. Refusing anything that
        // looks like it came from a page is therefore free and decisive.
        if (!string.IsNullOrEmpty(request.Headers["Origin"])) return false;

        var fetchSite = request.Headers["Sec-Fetch-Site"];
        if (!string.IsNullOrEmpty(fetchSite) && !fetchSite.Equals("none", StringComparison.OrdinalIgnoreCase)) return false;

        // DNS rebinding defence: a hostile name resolving to 127.0.0.1 arrives
        // with its own name in Host, not ours.
        if (!IsAcceptableHost(request.Headers["Host"])) return false;

        // The loopback binding should make this impossible, but assert it rather
        // than assume it.
        if (request.RemoteEndPoint is { } remote && !IPAddress.IsLoopback(remote.Address)) return false;

        return true;
    }

    public bool IsAcceptableHost(string? host)
    {
        if (string.IsNullOrEmpty(host)) return false;

        var name = host;
        var colon = host.LastIndexOf(':');
        if (colon > 0)
        {
            var portText = host[(colon + 1)..];
            if (!int.TryParse(portText, out var port) || port != _port) return false;
            name = host[..colon];
        }

        return name.Equals("127.0.0.1", StringComparison.Ordinal)
            || name.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || name.Equals("[::1]", StringComparison.Ordinal);
    }

    // Requiring a JSON content type blocks the "simple request" CORS path, which
    // is the only one a page can take without a preflight.
    public static void RequireJsonContentType(HttpListenerRequest request)
    {
        var contentType = request.ContentType;
        if (string.IsNullOrEmpty(contentType)
            || contentType.IndexOf("application/json", StringComparison.OrdinalIgnoreCase) < 0)
        {
            throw HttpApiException.Validation("Content-Type must be application/json");
        }
    }

    public static bool RunSelfTest()
    {
        var token = GenerateToken();
        if (token.Length < 40) return false;
        if (token.Contains('+') || token.Contains('/') || token.Contains('=')) return false;
        if (GenerateToken() == token) return false;

        var guard = new AuthGuard(token, 47811);
        if (!guard.IsAcceptableHost("127.0.0.1:47811")) return false;
        if (!guard.IsAcceptableHost("localhost:47811")) return false;
        if (!guard.IsAcceptableHost("127.0.0.1")) return false;
        if (guard.IsAcceptableHost("evil.test:47811")) return false;
        if (guard.IsAcceptableHost("127.0.0.1:1234")) return false;
        if (guard.IsAcceptableHost("")) return false;
        if (guard.IsAcceptableHost(null)) return false;

        return true;
    }
}
