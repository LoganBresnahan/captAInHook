using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using CaptainHook.Actors;
using CaptainHook.Wire;

namespace CaptainHook.Api;

// api-listener-host + port-config-and-cutover (ADR-0007 decisions 1–2): the
// management API's front door — a loopback HttpListener run as a task BESIDE
// the UDS serve loop and structurally isolated from it. The API is a face on a
// serving daemon, never a reason one exists, so DaemonHost binds the socket and
// warms the workers first, then starts this. The shim never learns the API
// exists (aot-boundary rule 1): the listener lives in the JIT engine, not the
// wire lib.
//
// The port is a GLOBAL SINGLETON the way the UDS socket never was (N1): the
// socket rendezvous is version-partitioned, but two daemon identities that
// briefly coexist during a deploy contend for ONE port. The cutover contract:
//   * the incumbent releases the port at DRAIN START (DaemonHost calls Stop()
//     before waiting out in-flight dispatches), not at exit;
//   * the successor binds through StartRetrying — one synchronous attempt (the
//     common free-port case stays deterministic), then a fast backoff spanning
//     the incumbent's drain deadline, then ONE warn (api.bindBlocked) and a
//     slow steady cadence that never gives up until Stop. Never-fatal, and the
//     slow phase means an incumbent that lingers to idle-exit (up to the idle
//     window — nothing SIGTERMs it on deploy) hands the port over whenever it
//     finally drains, not never. Hooks outrank the API throughout: bind state
//     is invisible to the dispatch path, and the retry task touches none of
//     the idle-exit bookkeeping, so a daemon blocked off its port still serves
//     and still reaps itself.
// Platform facts underneath (probed, pinned by ApiRetryBindTests, recorded in
// doc/platform.md § Loopback TCP): an active listener blocks a second bind
// cross-process (no accidental co-bind steal); TIME_WAIT residue from a .NET
// incumbent does NOT block a .NET successor's bind (the .NET Unix PAL sets
// SO_REUSEADDR on every TCP bind — HttpListener itself sets nothing, and the
// guarantee is .NET↔.NET: a non-.NET prior occupant that closed server-side
// can block up to ~60s, which the slow cadence absorbs); a blocked bind
// surfaces as HttpListenerException.
//
// Discovery + credential (api-json-discovery, ADR-0007 decisions 2+6): on bind
// the host writes a 0600 api.json (port, token, pid, identity) beside the
// socket and mints a random bearer token — the SOLE credential source. The
// file exists iff this host holds the port: written under the same lock that
// flips `_listening` true, deleted when Stop flips it false, so a client never
// reads a port+token for a listener that has already handed the port off.
//
// Endpoints and the auth GATE are later slices (ADR-0007 § Implementation
// plan): the token is minted and published here, but every route still 404s
// and nothing is yet checked — auth-token-origin adds the check and must land
// before any endpoint is exposed.
public sealed class ApiHost : IDisposable
{
    /// "HOOK" on a phone keypad (ADR-0007 decision 2) — fixed so the GUI's URL
    /// is bookmarkable. CAPTAINHOOK_API_PORT overrides; 0 disables.
    public const int DefaultPort = 4665;

    private readonly object _gate = new();               // orders bind-install vs Stop
    private readonly CancellationTokenSource _stop = new();
    private readonly RendezvousPaths? _rendezvous;       // null in pure-listener tests: no discovery file
    private HttpListener? _http;                          // installed under _gate once bound
    private bool _listening;
    private volatile bool _stopping;

    private ApiHost(int port, RendezvousPaths? rendezvous)
    {
        Port = port;
        _rendezvous = rendezvous;
        // 256 bits from the CSPRNG, hex so it survives a header and JSON without
        // encoding ambiguity. Minted once per host — a superseded daemon's token
        // dies with it (the successor mints its own; ADR-0007 decision 6).
        Token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    }

    /// The configured loopback port — fixed up front; whether it is currently
    /// BOUND is IsListening (a retrying host may not hold the port yet).
    public int Port { get; }

    /// The per-daemon bearer token published in api.json — the sole credential
    /// the auth gate (next slice) will require. Tests without a rendezvous read
    /// it here; production clients read it from the 0600 file.
    public string Token { get; }

    /// True while the listener is bound and accepting. False before a retrying
    /// bind lands and after Stop().
    public bool IsListening { get { lock (_gate) return _listening; } }

    /// Daemon-start port resolution for Program.cs (decision 2): unset ⇒ the
    /// default (the API is on by default), "0" ⇒ null = disabled, 1..65535 ⇒
    /// that port. Malformed values fall back to the default, silently — the
    /// same idiom as CAPTAINHOOK_IDLE_MS; a typo must not darken the GUI's
    /// bookmarked URL. 0 is the one documented off-switch.
    public static int? ResolvePort(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return DefaultPort;
        if (!int.TryParse(raw, out var port)) return DefaultPort;
        if (port == 0) return null;
        return port is > 0 and <= 65535 ? port : DefaultPort;
    }

    /// Bind loopback-only on `port` NOW or throw — for callers that know the
    /// port is free (tests). Production goes through StartRetrying. `rendezvous`
    /// (when supplied) is where the 0600 api.json is written on bind and removed
    /// on stop; null skips the file (pure-listener tests read Token directly).
    public static ApiHost Start(int port, RendezvousPaths? rendezvous = null)
    {
        var host = new ApiHost(port, rendezvous);
        if (host.TryBindOnce() is { } err) ExceptionDispatchInfo.Capture(err).Throw();
        _ = Task.Run(host.AcceptLoopAsync);
        return host;
    }

    /// The production entry (DaemonHost): one synchronous bind attempt — on a
    /// free port this is Start() with no task hop — then background retries.
    /// `fastWindow` is the incumbent's drain deadline: within it, attempts back
    /// off 100ms→1s (a drain-start release is picked up in ≤1s); past it, one
    /// warn, then `slowRetry` (default 5s) forever until Stop. Never throws:
    /// bind failure is a warn, never fatal (ADR-0007 N1). `rendezvous` carries
    /// the api.json path + identity to publish once the bind lands.
    public static ApiHost StartRetrying(
        int port, TimeSpan fastWindow, RendezvousPaths? rendezvous = null, TimeSpan? slowRetry = null)
    {
        var host = new ApiHost(port, rendezvous);
        if (host.TryBindOnce() is not { } err)
        {
            _ = Task.Run(host.AcceptLoopAsync);
            return host;
        }
        Log.Info("api", "api.bindContended", new LogFields
        {
            Msg = err.Message,
            Data = new Dictionary<string, object> { ["port"] = port },
        });
        var tok = host._stop.Token;   // snapshot before Dispose can touch the source
        _ = Task.Run(() => host.RetryBindThenAcceptAsync(
            fastWindow, slowRetry ?? TimeSpan.FromSeconds(5), tok));
        return host;
    }

    private async Task RetryBindThenAcceptAsync(TimeSpan fastWindow, TimeSpan slowRetry, CancellationToken tok)
    {
        try
        {
            var sw = Stopwatch.StartNew();   // monotonic deadline, house invariant 2
            var delay = TimeSpan.FromMilliseconds(100);
            var warned = false;
            while (!_stopping)
            {
                try { await Task.Delay(delay, tok); }
                catch (OperationCanceledException) { return; }
                var err = TryBindOnce();
                if (err is null)
                {
                    // Stop can race the install: it observed _http under the
                    // gate and is stopping that listener — don't start accepting.
                    if (_stopping) return;
                    await AcceptLoopAsync();
                    return;
                }
                if (_stopping) return;   // stopping: no warn after api.stopped, no further attempts
                if (!warned && sw.Elapsed >= fastWindow)
                {
                    warned = true;
                    Log.Warn("api", "api.bindBlocked", new LogFields
                    {
                        Msg = err.Message,
                        DurMs = sw.Elapsed.TotalMilliseconds,
                        Data = new Dictionary<string, object> { ["port"] = Port },
                    });
                }
                delay = warned ? slowRetry
                    : TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 1000));
            }
        }
        catch (Exception ex)
        {
            // The retry task is fire-and-forget: nothing may escape unobserved.
            // (The accept loop guards itself, so this covers the bind loop only.)
            Log.Error("api", "api.loopCrashed", new LogFields
            {
                Msg = ex.Message,
                Data = new Dictionary<string, object> { ["loop"] = "bind" },
            });
        }
    }

    /// One bind attempt; null on success (listener installed, api.listening
    /// logged). The _gate double-check closes the Stop-during-bind race: a
    /// daemon past drain start must never come (back) up on the singleton port
    /// its successor is retry-binding, so a bind that lands after Stop() is
    /// released immediately.
    private Exception? TryBindOnce()
    {
        var http = new HttpListener();
        http.Prefixes.Add($"http://127.0.0.1:{Port}/");
        try { http.Start(); }
        catch (Exception ex) when (ex is HttpListenerException or SocketException)
        {
            ((IDisposable)http).Dispose();
            return ex;
        }
        lock (_gate)
        {
            if (_stopping)
            {
                try { http.Stop(); } catch { /* releasing anyway */ }
                ((IDisposable)http).Dispose();
                return new InvalidOperationException("stopped while binding");
            }
            _http = http;
            _listening = true;
            // Publish discovery UNDER the gate, atomic with _listening=true, so
            // "api.json exists ⟺ we hold the port" holds against a racing Stop:
            // either we write here then Stop deletes, or Stop wins the gate and
            // we return above without writing. Never both.
            PublishDiscovery();
        }
        Log.Info("api", "api.listening", new LogFields
        {
            Data = new Dictionary<string, object> { ["port"] = Port },
        });
        return null;
    }

    // Write the 0600 api.json for this bind, or degrade honestly: a write
    // failure (disk full, perms) leaves the API listening but UNDISCOVERABLE
    // (no client can learn the token) — degraded, never fatal to hooks, so it
    // must not throw out of the gate or un-bind the port. Callers hold _gate.
    private void PublishDiscovery()
    {
        if (_rendezvous is null) return;
        try
        {
            ApiDiscovery.Write(_rendezvous.ApiJsonPath,
                new ApiDiscovery(Port, Token, Environment.ProcessId, _rendezvous.Version));
        }
        catch (Exception ex)
        {
            Log.Warn("api", "api.discoveryFailed", new LogFields
            {
                Msg = ex.Message,
                Data = new Dictionary<string, object> { ["path"] = _rendezvous.ApiJsonPath },
            });
        }
    }

    // Remove this host's own api.json (best-effort). Version-partitioned, so it
    // is always OURS to delete — never a successor's. Callers hold _gate, paired
    // with _listening=false, so the file cannot outlive the port we held.
    private void UnpublishDiscovery()
    {
        if (_rendezvous is null) return;
        try { File.Delete(_rendezvous.ApiJsonPath); } catch { /* best-effort, doctor backstops a leak */ }
    }

    // The loop's ONLY job is accept-and-hand-off: each context runs on its own
    // task and the loop returns to accepting immediately, so requests run
    // CONCURRENTLY (bounded by the thread pool, not by connections). Awaiting the
    // handler here would serialize the whole API — a long-lived SSE stream (later
    // slice) would then wedge it. Never await the handler in this loop.
    private async Task AcceptLoopAsync()
    {
        try
        {
            var http = _http!;   // set before this loop starts, on both entry paths
            while (!_stopping)
            {
                HttpListenerContext ctx;
                try { ctx = await http.GetContextAsync(); }
                catch (HttpListenerException) { break; }   // Stop()/Close() woke us
                catch (ObjectDisposedException) { break; }
                catch (InvalidOperationException) { break; } // listener no longer listening
                _ = Task.Run(() => HandleAsync(ctx));
            }
        }
        catch (Exception ex)
        {
            // Fire-and-forget task: nothing may escape unobserved, and a dead
            // accept loop must not keep the port bound-but-deaf — release it
            // visibly (Stop is idempotent) so a restart can reclaim it.
            Log.Error("api", "api.loopCrashed", new LogFields
            {
                Msg = ex.Message,
                Data = new Dictionary<string, object> { ["loop"] = "accept" },
            });
            Stop();
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

    /// Stop accepting AND stop retrying (idempotent). DaemonHost calls this at
    /// DRAIN START: the port frees for the successor's retry-bind while the
    /// incumbent finishes in-flight hooks — the N1 handoff's release half. This
    /// is also the seam the SSE slice grows into terminating open streams, so a
    /// draining daemon can never be pinned alive by a subscriber (decision 7).
    public void Stop()
    {
        HttpListener? bound;
        lock (_gate)
        {
            if (_stopping) return;
            _stopping = true;
            bound = _http;
            _listening = false;
            // Delete under the gate, atomic with _listening=false: a draining
            // incumbent stops advertising its port+token the instant it decides
            // to release, so no client reads stale credentials mid-cutover.
            UnpublishDiscovery();
        }
        // Enqueue api.stopped BEFORE releasing the port: a successor's
        // api.listening can then never precede it, so the trail's cutover
        // story reads deterministically (stopped → listening, always).
        Log.Info("api", "api.stopped", new LogFields
        {
            Data = new Dictionary<string, object> { ["port"] = Port, ["wasBound"] = bound is not null },
        });
        // Wake a sleeping retry backoff so it exits promptly. A concurrent
        // Dispose (its Stop early-returned on _stopping) may have disposed the
        // source already — that race must not throw out of an idempotent Stop.
        try { _stop.Cancel(); } catch (ObjectDisposedException) { /* flags are set; the loop still exits */ }
        if (bound is not null)
            try { bound.Stop(); } catch { /* already stopped */ }
    }

    public void Dispose()
    {
        Stop();
        try { _http?.Close(); } catch { /* best-effort */ }
        _stop.Dispose();   // always canceled first, so no late Task.Delay can register
    }
}
