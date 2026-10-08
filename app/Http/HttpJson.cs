using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Notipet.Shared;

namespace Notipet.Http;

// Ported from the house Node servers' json()/readJson()/requireInternalSecret()
// helpers: one place that writes a JSON body, one that reads one, and one
// exception type that a single top-level handler turns into a status code.
internal sealed class HttpApiException : Exception
{
    public int Status { get; }
    public string Code { get; }
    public string? Field { get; }

    public HttpApiException(int status, string code, string? message = null, string? field = null)
        : base(message ?? code)
    {
        Status = status;
        Code = code;
        Field = field;
    }

    public static HttpApiException Unauthorized(string? message = null) => new(401, "unauthorized", message);
    public static HttpApiException BadJson() => new(400, "invalid_json", "request body is not valid JSON");
    public static HttpApiException Validation(string message, string? field = null) => new(400, "validation_failed", message, field);
    public static HttpApiException TooLarge(int limit) => new(413, "payload_too_large", $"request body exceeds {limit} bytes");
    public static HttpApiException NotFound() => new(404, "not_found");
}

internal static class HttpJson
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static async Task WriteAsync<T>(HttpListenerContext context, int status, T body, JsonTypeInfo<T> typeInfo)
    {
        var payload = Utf8NoBom.GetBytes(JsonSerializer.Serialize(body, typeInfo));
        var response = context.Response;
        response.StatusCode = status;
        response.ContentType = "application/json; charset=utf-8";
        // Kept from the house convention: nothing about this API is cacheable,
        // and a caching layer in between would be actively wrong.
        response.Headers["Cache-Control"] = "no-store";
        response.Headers["X-Content-Type-Options"] = "nosniff";
        response.ContentLength64 = payload.Length;
        await response.OutputStream.WriteAsync(payload).ConfigureAwait(false);
    }

    public static Task WriteErrorAsync(HttpListenerContext context, HttpApiException error) =>
        WriteAsync(context, error.Status,
            new ErrorResponse { Error = error.Code, Message = error.Message, Field = error.Field },
            NotipetJson.Compact.ErrorResponse);

    public static async Task<T> ReadAsync<T>(HttpListenerContext context, int maxBytes, JsonTypeInfo<T> typeInfo)
        where T : class
    {
        // Trust ContentLength when it is present, but never trust it alone: a
        // caller can lie, so the copy below is capped regardless.
        if (context.Request.ContentLength64 > maxBytes) throw HttpApiException.TooLarge(maxBytes);

        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await context.Request.InputStream.ReadAsync(chunk).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes) throw HttpApiException.TooLarge(maxBytes);
            buffer.Write(chunk, 0, read);
        }

        if (buffer.Length == 0) throw HttpApiException.BadJson();

        try
        {
            buffer.Position = 0;
            var parsed = await JsonSerializer.DeserializeAsync(buffer, typeInfo, CancellationToken.None).ConfigureAwait(false);
            return parsed ?? throw HttpApiException.BadJson();
        }
        catch (JsonException)
        {
            throw HttpApiException.BadJson();
        }
    }
}
