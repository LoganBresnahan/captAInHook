using System.Net;
using CaptainHook.Actors;

namespace CaptainHook.Api;

// api-listener-host (ADR-0007 decision 1): the management API's front door — a
// loopback HttpListener run as a task BESIDE the UDS serve loop and
// structurally isolated from it. The API is a face on a serving daemon, never a
// reason one exists, so DaemonHost binds the socket and warms the workers
// first, then starts this. The shim never learns the API exists (aot-boundary
// rule 1): the listener lives in the JIT engine, not the wire lib.
//
// This slice stands up the MECHANISM only: the accept loop, a /api/v1 router
// with NO endpoints wired yet (every route 404s as JSON), and the reflection-
// STJ writer the read endpoints reuse (ApiJson). Two siblings finish the story
// and are deliberately NOT here (ADR-0007 § Implementation plan): the port
// default / CAPTAINHOOK_API_PORT / 0-disable and the singleton-port drain-start
// cutover (port-config-and-cutover), and the bearer-token + Origin gate
// (auth-token-origin). Until port-config lands, this host binds a caller-
// supplied port and is OFF in production — tests drive it with an explicit port.
public sealed class ApiHost : IDisposable
{
    private readonly HttpListener _http;
    private readonly Task _accept;
    private volatile bool _stopping;

    private ApiHost(HttpListener http) => (_http, _accept) = (http, Task.Run(AcceptLoopAsync));

    /// The bound loopback port. Tests read it; the production port arrives from
    /// Program.cs in the port-config slice.
    public int Port { get; private init; }

    /// Bind loopback-only on `port` and start accepting. Call only when the
    /// daemon is warm (DaemonHost has already bound the UDS socket).
    public static ApiHost Start(int port)
    {
        var http = new HttpListener();
        http.Prefixes.Add($"http://127.0.0.1:{port}/");
        try { http.Start(); }
        catch { ((IDisposable)http).Dispose(); throw; }

        Log.Info("api", "api.listening", new LogFields
        {
            Data = new Dictionary<string, object> { ["port"] = port },
        });
        return new ApiHost(http) { Port = port };
    }

    // The loop's ONLY job is accept-and-hand-off: each context runs on its own
    // task and the loop returns to accepting immediately, so requests run
    // CONCURRENTLY (bounded by the thread pool, not by connections). Awaiting the
    // handler here would serialize the whole API — a long-lived SSE stream (later
    // slice) would then wedge it. Never await the handler in this loop.
    private async Task AcceptLoopAsync()
    {
        while (!_stopping)
        {
            HttpListenerContext ctx;
            try { ctx = await _http.GetContextAsync(); }
            catch (HttpListenerException) { break; }   // Stop()/Close() woke us
            catch (ObjectDisposedException) { break; }
            catch (InvalidOperationException) { break; } // listener no longer listening
            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    // Router skeleton: no endpoints wired in this slice, so every route is Not
    // Found. The read endpoints (Phase 4) hang GET /status,/policy,/harnesses,
    // /handlers off this method-plus-path shape; the auth gate (auth-token-origin)
    // wraps it. The shape — one place, reflection-STJ body, JSON errors — is set
    // here so those slices only add cases.
    private static async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            await ApiJson.WriteAsync(ctx.Response, 404,
                new { error = "not_found", path = ctx.Request.Url?.AbsolutePath ?? "" });
        }
        catch (Exception ex)
        {
            // The API is off the sacred hook/stdout path (ADR-0007 d1): a broken
            // response fails this request only, never the daemon or a dispatch.
            Log.Warn("api", "api.handlerError", new LogFields { Msg = ex.Message });
            try { ctx.Response.Abort(); } catch { /* peer already gone */ }
        }
    }

    /// Stop accepting (idempotent). DaemonHost calls this at DRAIN START so a
    /// draining daemon stops answering the API while it finishes in-flight
    /// hooks. This is the seam the port-cutover slice grows into also
    /// terminating open SSE streams here, freeing the singleton port for the
    /// successor before the incumbent's drain deadline.
    public void Stop()
    {
        if (_stopping) return;
        _stopping = true;
        try { _http.Stop(); } catch { /* already stopped */ }
        Log.Info("api", "api.stopped", new LogFields
        {
            Data = new Dictionary<string, object> { ["port"] = Port },
        });
    }

    public void Dispose()
    {
        Stop();
        try { _http.Close(); } catch { /* best-effort */ }
    }
}
