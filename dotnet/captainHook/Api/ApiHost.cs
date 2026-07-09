using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
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
// Auth (auth-token-origin, ADR-0007 decision 6): every request clears the
// Host + Origin + bearer-token gate (ApiAuthGate) BEFORE the router, so the
// whole TCP surface is credentialed — the gate is live even though this slice
// still wires no endpoints (an authorized request 404s; an unauthorized one
// never reaches the router). Endpoints (Phase 4) inherit the gate for free.
public sealed class ApiHost : IDisposable
{
    /// "HOOK" on a phone keypad (ADR-0007 decision 2) — fixed so the GUI's URL
    /// is bookmarkable. CAPTAINHOOK_API_PORT overrides; 0 disables.
    public const int DefaultPort = 4665;

    private readonly object _gate = new();               // orders bind-install vs Stop
    private readonly CancellationTokenSource _stop = new();
    private readonly RendezvousPaths? _rendezvous;       // null in pure-listener tests: no discovery file
    private readonly ApiAuthGate _auth;                  // the pure Host/Origin/token decision
    private readonly ApiReadModel? _read;                // null => read endpoints 404 (no daemon behind us)
    private readonly ApiPolicyWriter? _writer;           // null => PUT /policy 404s (no writable path)
    private readonly SseOptions? _sse;                   // null => /events 404s (no trail to tail)
    private readonly string? _uiDir;                     // null => /ui stays fully gated + 404s (no GUI staged)
    private readonly Action? _onRequest;                 // the daemon's idle-clock stamp (decision 7)

    /// A policy body larger than this is refused with 413 before any parse — a
    /// dispatch policy is KiB at most, so this only bounds a hostile/broken client
    /// streaming unbounded bytes into memory on the loopback surface.
    private const int MaxPolicyBytes = 1 << 20;   // 1 MiB
    private HttpListener? _http;                          // installed under _gate once bound
    private int _openStreams;                            // live SSE subscriptions (the idle-defer signal)
    private bool _listening;
    private volatile bool _stopping;

    private ApiHost(
        int port, RendezvousPaths? rendezvous, ApiReadModel? readModel, ApiPolicyWriter? writer,
        SseOptions? sse, Action? onRequest, string? uiDir)
    {
        Port = port;
        _rendezvous = rendezvous;
        _read = readModel;
        _writer = writer;
        _sse = sse;
        _uiDir = uiDir;
        _onRequest = onRequest;
        // 256 bits from the CSPRNG, hex so it survives a header and JSON without
        // encoding ambiguity. Minted once per host — a superseded daemon's token
        // dies with it (the successor mints its own; ADR-0007 decision 6).
        Token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        _auth = new ApiAuthGate(port, Token);
    }

    /// The configured loopback port — fixed up front; whether it is currently
    /// BOUND is IsListening (a retrying host may not hold the port yet).
    public int Port { get; }

    /// The per-daemon bearer token published in api.json — the sole credential
    /// the auth gate (next slice) will require. Tests without a rendezvous read
    /// it here; production clients read it from the 0600 file.
    public string Token { get; }

    /// Live SSE subscriptions. The daemon's idle watchdog reads this: an open
    /// stream is an attached observer and defers idle-exit (ADR-0007 decision 7)
    /// — for the CURRENT lock-holder only, because Stop() at drain start
    /// terminates every stream and zeroes this.
    public int OpenStreams => Volatile.Read(ref _openStreams);

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
    /// `readModel` backs the read endpoints; null => they 404 (no daemon state).
    /// `sse` names the trail file /events tails; null => /events 404s.
    /// `onRequest` fires on every arriving request — the daemon stamps its idle
    /// clock with it (ADR-0007 decision 7).
    public static ApiHost Start(
        int port, RendezvousPaths? rendezvous = null, ApiReadModel? readModel = null,
        ApiPolicyWriter? writer = null, SseOptions? sse = null, Action? onRequest = null,
        string? uiDir = null)
    {
        var host = new ApiHost(port, rendezvous, readModel, writer, sse, onRequest, uiDir);
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
    /// the api.json path + identity to publish once the bind lands; `readModel`
    /// backs the read endpoints; `sse` names the trail /events tails.
    public static ApiHost StartRetrying(
        int port, TimeSpan fastWindow, RendezvousPaths? rendezvous = null,
        ApiReadModel? readModel = null, ApiPolicyWriter? writer = null, SseOptions? sse = null,
        Action? onRequest = null, TimeSpan? slowRetry = null, string? uiDir = null)
    {
        var host = new ApiHost(port, rendezvous, readModel, writer, sse, onRequest, uiDir);
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

    // Every request clears the auth gate BEFORE the router sees it, then the
    // router answers. No endpoints are wired in this slice, so an authorized
    // request still 404s — but an unauthorized one never reaches the router.
    // The read endpoints (Phase 4) hang GET /status,/policy,/harnesses,/handlers
    // off the method-plus-path shape below; they inherit this gate for free.
    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            // Any API request resets the daemon's idle clock (decision 7) —
            // stamped before the gate on purpose: even a 401 proves someone is
            // interacting with this daemon, and a warm daemon is capacity.
            _onRequest?.Invoke();
            var req = ctx.Request;
            // ADR-0008 decision 2: the /ui static shell is the ONE bearer-exempt
            // surface — a top-level navigation cannot carry the Authorization
            // header, so the shell must load without it and then authenticate
            // itself (the fragment handoff). The exemption exists only when a UI
            // dir is actually configured (a pure API host keeps every path fully
            // gated), and it is bearer-ONLY: Host + Origin still apply via
            // EvaluateShell, and the router below never sees a UI path, so no
            // data route can ride the exemption.
            var isUi = _uiDir is not null && IsUiPath(req.Url?.AbsolutePath ?? "");
            var rejection = isUi
                ? _auth.EvaluateShell(req.UserHostName, req.Headers["Origin"])
                : _auth.Evaluate(req.UserHostName, req.Headers["Origin"], req.Headers["Authorization"]);
            if (rejection is { } rej)
            {
                if (rej.Status == 401)
                    ctx.Response.AddHeader("WWW-Authenticate", "Bearer");   // RFC 7235
                await ApiJson.WriteAsync(ctx.Response, rej.Status, new { error = rej.Error });
                return;
            }
            if (isUi) await ServeUiAsync(ctx);
            else await RouteAsync(ctx);
        }
        catch (Exception ex)
        {
            // The API is off the sacred hook/stdout path (ADR-0007 d1): a broken
            // response fails this request only, never the daemon or a dispatch.
            Log.Warn("api", "api.handlerError", new LogFields { Msg = ex.Message });
            try { ctx.Response.Abort(); } catch { /* peer already gone */ }
        }
    }

    // The API surface: GET /api/v1/{status,policy,harnesses,handlers} (decision
    // 3) rendered from the ApiReadModel — the same live objects the dispatch path
    // uses; GET /events, the SSE trail tail (decision 5); and PUT /policy, the one
    // write verb (decision 4). Each capability is gated on the collaborator that
    // backs it (no read model / writer / sse => that route 404s: a pure-listener
    // test has no daemon behind it). GET /policy carries a content-hash ETag
    // header — the token PUT /policy's If-Match consumes. GET /events is a
    // LONG-LIVED response, which is exactly why the accept loop never awaits
    // handlers. Unknown route or wrong method → 404.
    private async Task RouteAsync(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url?.AbsolutePath ?? "";
        if (_sse is not null && ctx.Request.HttpMethod == "GET" && path == "/api/v1/events")
        {
            await ServeEventsAsync(ctx);
            return;
        }
        if (_writer is not null && ctx.Request.HttpMethod == "PUT" && path == "/api/v1/policy")
        {
            await ServePolicyPutAsync(ctx);
            return;
        }
        if (_read is not null && ctx.Request.HttpMethod == "GET")
        {
            switch (path)
            {
                case "/api/v1/status":
                    await ApiJson.WriteAsync(ctx.Response, 200, _read.Status(OpenStreams));
                    return;
                case "/api/v1/harnesses":
                    await ApiJson.WriteAsync(ctx.Response, 200, _read.Harnesses());
                    return;
                case "/api/v1/handlers":
                    await ApiJson.WriteAsync(ctx.Response, 200, _read.Handlers());
                    return;
                case "/api/v1/policy":
                    var policy = _read.Policy();
                    if (policy.Etag is not null) ctx.Response.AddHeader("ETag", policy.Etag);
                    await ApiJson.WriteAsync(ctx.Response, 200, policy);
                    return;
            }
        }
        await ApiJson.WriteAsync(ctx.Response, 404, new { error = "not_found", path });
    }

    /// Exactly "/ui", "/ui/", or "/ui/<asset>" — nothing else is UI-shaped. A
    /// prefix like "/uifoo" is NOT (the separator check), so no data route can
    /// be named into the bearer exemption.
    private static bool IsUiPath(string path) =>
        path == "/ui" || path == "/ui/" || path.StartsWith("/ui/", StringComparison.Ordinal);

    // GET /ui (ui-static-route, ADR-0008 decision 2): the daemon as a dumb
    // static-byte server for the GUI shell — disk streaming from the staged
    // ui/ dir, a small MIME map, and the traversal guard in ResolveUiFile.
    // INERT by contract: this method may serve bytes from _uiDir and NOTHING
    // else — no daemon state, no token, no computed content may ever be
    // written here (a data leak into the unauthenticated shell would breach
    // the gate; pinned by ApiUiRouteTests' byte-identity + token-absence
    // assertions). Off the sacred hook/stdout path like the rest of the API.
    private async Task ServeUiAsync(HttpListenerContext ctx)
    {
        if (ctx.Request.HttpMethod != "GET")
        {
            await ApiJson.WriteAsync(ctx.Response, 404, new { error = "not_found" });
            return;
        }
        var abs = ctx.Request.Url?.AbsolutePath ?? "/ui";
        var rel = abs is "/ui" or "/ui/" ? "index.html" : abs["/ui/".Length..];
        var file = ResolveUiFile(_uiDir!, rel);
        if (file is null)
        {
            await ApiJson.WriteAsync(ctx.Response, 404, new { error = "not_found" });
            return;
        }
        var resp = ctx.Response;
        resp.StatusCode = 200;
        resp.ContentType = Mime(file);
        // The shell must never cache stale against a redeployed ui/ (Vite hashes
        // asset names, but index.html keeps its name across builds); loopback
        // reads are ~free, so no-cache everywhere is the simple, safe uniform.
        resp.AddHeader("Cache-Control", "no-cache");
        await using var fs = new FileStream(
            file, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, useAsync: true);
        resp.ContentLength64 = fs.Length;
        await fs.CopyToAsync(resp.OutputStream, _stop.Token);
        resp.OutputStream.Close();
    }

    /// The traversal guard, factored pure so the security logic is unit-tested
    /// directly (like ApiAuthGate): map a decoded request-relative path to a
    /// file INSIDE uiDir, or null. Null on: rooted paths, NUL bytes, any
    /// canonicalized escape from uiDir (dot-dot in any encoding the transport
    /// decoded), the root itself, directories, and files that don't exist.
    /// The prefix check appends the separator so a SIBLING dir sharing the
    /// prefix ("ui2/") can never pass. HttpListener pre-decodes percent
    /// escapes in AbsolutePath and Uri collapses literal dot segments before
    /// we ever see them — this guard assumes NEITHER, so it holds even if the
    /// transport's canonicalization changes underneath. Symlinks inside uiDir
    /// are trusted: the dir's contents are our own staged build output.
    internal static string? ResolveUiFile(string uiDir, string rel)
    {
        if (rel.Length == 0) rel = "index.html";
        if (rel.Contains('\0') || Path.IsPathRooted(rel)) return null;
        string root, full;
        try
        {
            // Trim a trailing separator so the prefix check below (root + sep)
            // is well-formed: GetFullPath("/x/ui/") keeps the slash on Linux,
            // which would make `root + sep` == "/x/ui//" and fail EVERY file —
            // a silently-dark GUI. Production passes no trailing slash, but a
            // config that does must degrade to serving, not to 404-everything.
            root = Path.GetFullPath(uiDir).TrimEnd(Path.DirectorySeparatorChar);
            full = Path.GetFullPath(Path.Combine(root, rel));
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return null;   // unmappable request path — never an escape
        }
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return null;
        return File.Exists(full) ? full : null;
    }

    /// The closed MIME map for what a Vite build emits — data selects, code
    /// implements (the house pattern); anything unrecognized is an opaque
    /// download, never text/html (no content-sniffing a stray file into a
    /// script-bearing type).
    private static string Mime(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" or ".mjs" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".json" or ".map" => "application/json; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".ico" => "image/x-icon",
        ".woff2" => "font/woff2",
        ".txt" => "text/plain; charset=utf-8",
        _ => "application/octet-stream",
    };

    // PUT /api/v1/policy (put-policy-write, ADR-0007 decision 4): the API as
    // editor of the file. Read the body (bounded — a policy is KiB, and this is
    // the one route that consumes a request entity), hand it to the writer, and
    // map its closed outcome set 1:1 to HTTP. The atomic write and the strict
    // validation live in ApiPolicyWriter (directly unit-tested); this method is
    // only the HTTP shell.
    private async Task ServePolicyPutAsync(HttpListenerContext ctx)
    {
        byte[]? body;
        try { body = await ReadBodyAsync(ctx.Request, MaxPolicyBytes, _stop.Token); }
        catch (OperationCanceledException)
        {
            // The daemon is draining while we read the body — a routine cutover,
            // not a handler error (mirrors ServeEventsAsync's OCE swallow). The
            // file is untouched (the read precedes the write); the client retries
            // on the successor. Answer 503 best-effort — the socket may already be
            // gone with the listener.
            try { await ApiJson.WriteAsync(ctx.Response, 503, new { error = "draining" }); }
            catch { /* peer/listener already gone */ }
            return;
        }
        if (body is null)
        {
            await ApiJson.WriteAsync(ctx.Response, 413,
                new { error = "payload_too_large", limit = MaxPolicyBytes });
            return;
        }

        switch (_writer!.Write(body, ctx.Request.Headers["If-Match"]))
        {
            case PolicyWriteOutcome.Written w:
                // The tag we WROTE is authoritative for the client's next If-Match.
                ctx.Response.AddHeader("ETag", w.Etag);
                // Echo the freshly-resolved policy so the GUI re-renders from one
                // round-trip (mirrors GET). _read is always present when _writer
                // is — DaemonHost wires both from the same policyPath — but a
                // concurrent PUT could make _read.Policy()'s body reflect a newer
                // file than the ETag header; benign under the guarded-not-locked
                // contract (d4), and the header stays the tag this PUT installed.
                object echo = _read is not null ? _read.Policy() : new { ok = true, etag = w.Etag };
                await ApiJson.WriteAsync(ctx.Response, 200, echo);
                return;
            case PolicyWriteOutcome.Invalid inv:
                await ApiJson.WriteAsync(ctx.Response, 422,
                    new { error = "invalid_policy", violations = inv.Violations });
                return;
            case PolicyWriteOutcome.Mismatch m:
                if (m.Current is not null) ctx.Response.AddHeader("ETag", m.Current);
                await ApiJson.WriteAsync(ctx.Response, 412,
                    new { error = "etag_mismatch", current = m.Current });
                return;
            case PolicyWriteOutcome.Failed f:
                await ApiJson.WriteAsync(ctx.Response, 500,
                    new { error = "write_failed", detail = f.Message });
                return;
            default:
                // PolicyWriteOutcome is a closed DU; a new case reaching here is a
                // programming error, not a request one. Throw rather than fall
                // through to a silent empty response (HandleAsync logs + aborts).
                throw new InvalidOperationException("unhandled policy write outcome");
        }
    }

    // Read the request body into memory with a hard cap, independent of the
    // client's declared Content-Length (which a hostile client can lie about):
    // returns null the moment the stream exceeds `cap`, so the caller answers 413
    // without having buffered more than `cap` + one chunk.
    private static async Task<byte[]?> ReadBodyAsync(HttpListenerRequest req, int cap, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buf = new byte[8192];
        int n;
        while ((n = await req.InputStream.ReadAsync(buf, ct)) > 0)
        {
            if (ms.Length + n > cap) return null;
            ms.Write(buf, 0, n);
        }
        return ms.ToArray();
    }

    // GET /api/v1/events (sse-trail-tail, ADR-0007 decision 5): a live SSE tail
    // of the JSONL trail file. Runs for the CONNECTION's lifetime inside this
    // request's own task; ends on client hang-up or Stop() — the same `_stop`
    // the drain path cancels, so a draining daemon terminates every stream at
    // drain start (never pinned alive by a subscriber; decision 7's
    // current-lock-holder-only edge). `Last-Event-ID` resumes at that byte
    // offset; without it the stream starts at the file's current end ("from
    // now"). Note for item 6's GUI: browser EventSource cannot send the bearer
    // header — use fetch-streaming (which can) and hand-roll reconnect off the
    // last received id.
    private async Task ServeEventsAsync(HttpListenerContext ctx)
    {
        long? lastId = long.TryParse(ctx.Request.Headers["Last-Event-ID"], out var v) && v >= 0
            ? v : null;

        var resp = ctx.Response;
        resp.StatusCode = 200;
        resp.ContentType = "text/event-stream";
        resp.AddHeader("Cache-Control", "no-store");
        resp.KeepAlive = true;
        var output = resp.OutputStream;

        Interlocked.Increment(ref _openStreams);
        try
        {
            // Reconnect hint first — also the first flush, which commits the
            // response headers so the client sees the stream is live.
            await WriteFrameAsync(output, "retry: 1000\n\n", _stop.Token);

            var subscription = new TrailSubscription(
                _sse!.TrailPath, lastId, _sse.Poll, _sse.Heartbeat, _sse.Capacity);
            await subscription.RunAsync(
                (e, ct) => WriteFrameAsync(output, Frame(e), ct), _stop.Token);
        }
        catch (OperationCanceledException)
        {
            // A request racing drain start: a routine end, not a handler error —
            // don't let it reach HandleAsync's catch as api.handlerError noise.
        }
        finally
        {
            // The idle-defer watchdog trusts this counter with the daemon's
            // LIFETIME: a leaked increment is an immortal daemon that defeats
            // the version-cutover reaper — hence the finally.
            Interlocked.Decrement(ref _openStreams);
            try { resp.Close(); } catch { /* peer already gone */ }
        }
    }

    /// One SseEvent as a text/event-stream frame. Trail lines are shipped as
    /// opaque `data:` payloads (JSONL is single-line by construction — no
    /// embedded newlines to escape) with the byte offset as `id:`; a reset
    /// re-anchors the client's Last-Event-ID to 0 (the id space restarted);
    /// heartbeats are comments — EventSource ignores them, dead sockets don't.
    private static string Frame(SseEvent e) => e switch
    {
        SseEvent.Line l => $"id: {l.Id}\ndata: {l.Text}\n\n",
        SseEvent.Reset => "event: reset\nid: 0\ndata: {}\n\n",
        SseEvent.Gap g => $"event: gap\ndata: {{\"dropped\":{g.Dropped}}}\n\n",   // no id: resume recovers the gap
        SseEvent.Heartbeat => ": hb\n\n",
        _ => throw new InvalidOperationException($"unrenderable SSE event {e.GetType().Name}"),
    };

    private static async Task WriteFrameAsync(Stream output, string frame, CancellationToken ct)
    {
        await output.WriteAsync(Encoding.UTF8.GetBytes(frame), ct);
        await output.FlushAsync(ct);
    }

    /// Stop accepting, stop retrying, AND terminate every open SSE stream
    /// (idempotent). DaemonHost calls this at DRAIN START: the port frees for
    /// the successor's retry-bind while the incumbent finishes in-flight hooks
    /// — the N1 handoff's release half — and canceling `_stop` ends each
    /// subscription's writer loop, so a draining daemon is never pinned alive
    /// by a subscriber (decision 7's current-lock-holder-only edge; the
    /// EventSource reconnect then lands on the successor).
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
        // Wake a sleeping retry backoff AND every SSE writer riding this token.
        // The source is deliberately never disposed (one per daemon lifetime,
        // no timers — trivial): a concurrent Stop∥Dispose could otherwise
        // dispose it between _stopping being set and this Cancel, swallowing
        // the ONLY cancellation the token-riding SSE writers would ever see.
        _stop.Cancel();
        // Listener teardown must NEVER block the drain thread: on Linux the
        // managed HttpListener's Stop()/Close() BLOCK behind a write wedged on
        // a zero-window client (probed; doc/platform.md § Loopback TCP) — a
        // synchronous call here turns one stalled subscriber into an unkillable
        // daemon that never releases the version lock (SIGTERM is claimed by
        // the drain registration; only SIGKILL would work). The port frees the
        // INSTANT Stop() begins, even while blocked, so backgrounding costs the
        // handoff nothing; the brief wait keeps the healthy path (returns in
        // ~ms) synchronous for deterministic rebind in tests and cutover.
        // Healthy streams are ended by the token + their own resp.Close — the
        // listener teardown was never what terminated them; a wedged write
        // dies with the connection at process exit.
        if (bound is not null)
        {
            var teardown = Task.Run(() => { try { bound.Stop(); } catch { /* already stopped */ } });
            teardown.Wait(TimeSpan.FromSeconds(2));   // ms when healthy; proceed when wedged
        }
    }

    public void Dispose()
    {
        Stop();
        // Same non-blocking rule as Stop: Close() blocks behind wedged writes
        // and terminates nothing for healthy streams — best-effort, bounded.
        Task.Run(() => { try { _http?.Close(); } catch { /* best-effort */ } })
            .Wait(TimeSpan.FromSeconds(2));
    }
}
