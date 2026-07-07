using System.Net;
using System.Text.Json;

namespace CaptainHook.Api;

// Reflection-based System.Text.Json response writing (ADR-0007 decision 3: the
// host is JIT, so a source-generated context buys nothing here — plain
// reflection STJ is the right tool). One place so every endpoint renders the
// same way; the read endpoints (Phase 4) reuse WriteAsync for their DTOs.
public static class ApiJson
{
    // Web defaults: camelCase names, case-insensitive read — the shape a
    // browser/JS client expects, matching the GUI item 6 will build.
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// Write `body` as a JSON response with HTTP `status`. Payloads are small
    /// and loopback-only, so buffer once, set Content-Length, and close.
    public static async Task WriteAsync(HttpListenerResponse response, int status, object body)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body, Options);
        response.StatusCode = status;
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }
}
