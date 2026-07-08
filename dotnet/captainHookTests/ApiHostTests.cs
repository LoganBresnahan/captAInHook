using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using CaptainHook.Api;
using CaptainHook.Core;
using CaptainHook.Wire;
using static CaptainHook.Tests.TestUtil;

namespace CaptainHook.Tests;

// api-listener-host (ADR-0007 Phase 1): the loopback management-API listener
// stands up beside the UDS serve loop, routes /api/v1/* (no endpoints wired yet,
// so an AUTHORIZED request 404s as JSON), serves requests CONCURRENTLY, and
// tears down at drain. Requests here carry the bearer token (api.Token) — the
// auth gate landed in Phase 3; the gate's own behavior is pinned in ApiAuthTests.
public class ApiHostTests
{
    [Fact]
    public async Task Listener_Routes404Json_ForEveryUnwiredRoute()
    {
        // A pure listener has no read model, so even the real endpoint paths are
        // unwired; use a genuinely-unknown route so this stays true regardless.
        using var api = ApiHost.Start(FreeTcpPort());

        var (status, body) = await ApiGetAsync(api.Port, api.Token, "/api/v1/nonesuch");
        Assert.Equal(HttpStatusCode.NotFound, status);

        using var doc = JsonDocument.Parse(body);
        Assert.Equal("not_found", doc.RootElement.GetProperty("error").GetString());
        Assert.Equal("/api/v1/nonesuch", doc.RootElement.GetProperty("path").GetString());
    }

    [Fact]
    public async Task Listener_CompletesTwentyParallelRequests()
    {
        // 20 requests in flight at once, all served. Honest scope: instant
        // 404s would also complete under a SERIALIZED accept loop, so this
        // pins parallel-load completion, not the never-await-the-handler rule
        // itself — that rule becomes externally pinnable (and load-bearing)
        // the moment a long-lived response exists: the SSE slice must add the
        // test where a stream is held open WHILE another request answers.
        using var api = ApiHost.Start(FreeTcpPort());

        var results = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(i => ApiGetAsync(api.Port, api.Token, $"/api/v1/probe{i}")));

        Assert.All(results, r => Assert.Equal(HttpStatusCode.NotFound, r.Status));
    }

    [Fact]
    public async Task Stop_IsIdempotent_AndHaltsAccepting()
    {
        var api = ApiHost.Start(FreeTcpPort());
        var port = api.Port;
        api.Stop();
        api.Stop();     // idempotent — a second Stop (and Dispose's Stop) is a no-op
        api.Dispose();

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        await Assert.ThrowsAnyAsync<Exception>(() =>
            client.GetAsync($"http://127.0.0.1:{port}/api/v1/status"));
    }
}

// The API host wired into a REAL daemon over the real rendezvous: it comes up
// beside the UDS serve loop (both answer, concurrently) and tears down when the
// daemon drains — the DaemonHost.RunAsync(apiPort:) seam this slice adds.
public class ApiHostInDaemonTests : IAsyncLifetime
{
    private readonly TempRuntimeDir _dir = new();
    private readonly CancellationTokenSource _stop = new();
    private Task<int>? _daemon;
    private int _apiPort;

    private string Sock => _dir.Paths.SocketPath;
    private static string NoHarnessDir => Path.Combine("/tmp", "chk-none-" + Guid.NewGuid().ToString("N")[..8]);

    public async Task InitializeAsync()
    {
        _apiPort = FreeTcpPort();
        _daemon = Task.Run(() => DaemonHost.RunAsync(_dir.Paths, NoHarnessDir, _stop.Token, apiPort: _apiPort));
        await PollUntilAsync(async () =>
        {
            var probe = await ShimClient.TryForwardAsync(Sock,
                new HookRequest("probe000", "session-start", "claude-code", "{}"u8.ToArray()));
            return probe is ForwardOutcome.Answered;
        }, TimeSpan.FromSeconds(15), "daemon starts listening");
    }

    public async Task DisposeAsync()
    {
        _stop.Cancel();
        if (_daemon is not null)
            Assert.Equal(0, await _daemon.WaitAsync(TimeSpan.FromSeconds(10)));
        _dir.Dispose();
    }

    [Fact]
    public async Task Api_ListensBesideTheUdsServeLoop_BothAnswer()
    {
        // The UDS dispatch path still works...
        var outcome = await ShimClient.TryForwardAsync(Sock, new HookRequest(
            "both0001", "user-prompt-submit", "claude-code", "{}"u8.ToArray()));
        Assert.IsType<ForwardOutcome.Answered>(outcome);

        // ...and the HTTP API answers on its own port at the same time — proving
        // the auth path end to end through a REAL daemon: the token comes only
        // from the 0600 api.json (the sole credential source), tokenless is 401,
        // and the discovered token passes the gate to the 404 router.
        var disc = ApiDiscovery.TryRead(_dir.Paths.ApiJsonPath);
        Assert.NotNull(disc);
        Assert.Equal(_apiPort, disc!.Port);

        using (var noauth = new HttpClient())
            Assert.Equal(HttpStatusCode.Unauthorized,
                (await noauth.GetAsync($"http://127.0.0.1:{_apiPort}/api/v1/status")).StatusCode);

        // A real daemon HAS a read model, so /status is a live endpoint (200) —
        // the discovered token passes the gate and the status names this daemon.
        var (status, body) = await ApiGetAsync(_apiPort, disc.Token, "/api/v1/status");
        Assert.Equal(HttpStatusCode.OK, status);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(_dir.Paths.Version, doc.RootElement.GetProperty("version").GetString());
        Assert.Equal(Environment.ProcessId, doc.RootElement.GetProperty("pid").GetInt32());
    }

    [Fact]
    public async Task Api_TearsDown_WhenTheDaemonDrains()
    {
        // Live first (any HTTP status proves the listener is bound).
        Assert.True(await ApiPortAnswersAsync(_apiPort));

        // Trigger drain, wait for a clean exit, then the API port is dead.
        _stop.Cancel();
        Assert.Equal(0, await _daemon!.WaitAsync(TimeSpan.FromSeconds(10)));
        _daemon = null;   // DisposeAsync must not await it again

        Assert.False(await ApiPortAnswersAsync(_apiPort), "API port dead after drain");
    }
}

// port-config-and-cutover (ADR-0007 decision 2): daemon-start port resolution.
// Unset ⇒ the fixed default (the API is ON by default), 0 ⇒ disabled, a valid
// port ⇒ that port, anything malformed ⇒ the default (the CAPTAINHOOK_IDLE_MS
// idiom: a typo must not darken the bookmarkable URL; 0 is the off-switch).
public class ApiPortResolveTests
{
    [Theory]
    [InlineData(null, 4665)]        // unset: default ON
    [InlineData("", 4665)]
    [InlineData("  ", 4665)]
    [InlineData("0", null)]         // the documented off-switch
    [InlineData("8080", 8080)]
    [InlineData("1", 1)]
    [InlineData("65535", 65535)]
    [InlineData("65536", 4665)]     // out of range: default
    [InlineData("-1", 4665)]
    [InlineData("abc", 4665)]
    public void Resolve(string? raw, int? expected) =>
        Assert.Equal(expected, ApiHost.ResolvePort(raw));

    [Fact]
    public void TheDefaultIsHookOnAPhoneKeypad() => Assert.Equal(4665, ApiHost.DefaultPort);
}

// port-config-and-cutover: the retry-bind half of the N1 singleton-port story.
// The port is global — unlike the version-partitioned UDS socket, two daemon
// identities CAN contend for it — so the successor must bind through
// contention: one sync attempt, fast backoff inside the incumbent's drain
// window, one warn past it, then a slow cadence that never gives up until
// Stop. These tests squat the port with a raw TcpListener to play the
// incumbent/squatter.
public class ApiRetryBindTests
{
    private static TcpListener Squat(int port)
    {
        var l = new TcpListener(IPAddress.Loopback, port);
        l.Start();
        return l;
    }

    [Fact]
    public async Task StartRetrying_OnAFreePort_BindsSynchronously()
    {
        // The common case must stay deterministic: no task hop before the bind.
        using var api = ApiHost.StartRetrying(FreeTcpPort(), fastWindow: TimeSpan.FromSeconds(10));
        Assert.True(api.IsListening);

        var (status, _) = await ApiGetAsync(api.Port, api.Token, "/api/v1/nonesuch");
        Assert.Equal(HttpStatusCode.NotFound, status);   // bound + serving (no read model here)
    }

    [Fact]
    public async Task StartRetrying_WarnsOncePastTheWindow_ThenTakesThePortWhenFreed()
    {
        using var captured = new CapturedLog();
        var port = FreeTcpPort();
        var squatter = Squat(port);
        try
        {
            // Tiny window + tight slow cadence so the whole story plays out in
            // well under a second of wall clock.
            using var api = ApiHost.StartRetrying(port,
                fastWindow: TimeSpan.FromMilliseconds(200), slowRetry: TimeSpan.FromMilliseconds(100));
            Assert.False(api.IsListening);   // sync attempt lost to the squatter

            // Past the fast window: exactly one warn, and the host keeps trying.
            await PollUntilAsync(
                () => Task.FromResult(captured.Events.Any(e => e.Evt == "api.bindBlocked")),
                TimeSpan.FromSeconds(10), "the one bind-blocked warn");

            // The squatter leaves (an incumbent finally drained): the slow
            // cadence picks the port up — blocked is never forever.
            squatter.Stop();
            await PollUntilAsync(() => Task.FromResult(api.IsListening),
                TimeSpan.FromSeconds(10), "retry-bind lands after the port frees");

            var (status, _) = await ApiGetAsync(port, api.Token, "/api/v1/nonesuch");
            Assert.Equal(HttpStatusCode.NotFound, status);

            Assert.Single(captured.Events, e => e.Evt == "api.bindBlocked");
            Assert.Single(captured.Events, e => e.Evt == "api.bindContended");
        }
        finally { squatter.Stop(); }
    }

    [Fact]
    public async Task StartRetrying_NeverStealsAnActivelyHeldPort()
    {
        // No co-bind: while an ApiHost actively serves the port, a retrying
        // host must keep failing (the platform probe pinned the cross-process
        // flavor of this; here the in-process prefix table enforces the same).
        using var captured = new CapturedLog();
        var port = FreeTcpPort();
        using var holder = ApiHost.Start(port);
        using var contender = ApiHost.StartRetrying(port,
            fastWindow: TimeSpan.FromMilliseconds(150), slowRetry: TimeSpan.FromMilliseconds(50));

        // Let the contender run past its window into the slow phase: still out.
        await PollUntilAsync(
            () => Task.FromResult(captured.Events.Any(e => e.Evt == "api.bindBlocked")),
            TimeSpan.FromSeconds(10), "contender kept out of the held port");
        Assert.True(holder.IsListening);
        Assert.False(contender.IsListening);

        // The holder still answers — with ITS token (the contender never bound,
        // so it never published one).
        var (status, _) = await ApiGetAsync(port, holder.Token, "/api/v1/nonesuch");
        Assert.Equal(HttpStatusCode.NotFound, status);
    }

    [Fact]
    public async Task Stop_DuringRetry_MeansTheHostNeverBindsLater()
    {
        // The zombie race: a daemon that drains while still retry-binding must
        // never grab the port afterwards — Stop() cancels the backoff and the
        // _gate double-check releases a bind that lands mid-Stop.
        var port = FreeTcpPort();
        var squatter = Squat(port);
        try
        {
            var api = ApiHost.StartRetrying(port,
                fastWindow: TimeSpan.FromMilliseconds(100), slowRetry: TimeSpan.FromMilliseconds(50));
            api.Stop();
            squatter.Stop();   // port frees only AFTER the host was told to stop

            // The port must end up free for anyone else — a straggling retry
            // (at most one attempt can be in flight across Stop) may hold it
            // for microseconds before the gate releases it, hence the poll.
            await PollUntilAsync(() =>
            {
                try { ApiHost.Start(port).Dispose(); return Task.FromResult(true); }
                catch (Exception) { return Task.FromResult(false); }
            }, TimeSpan.FromSeconds(10), "the stopped host left the port free");

            Assert.False(api.IsListening);
            api.Dispose();
        }
        finally { squatter.Stop(); }
    }

    [Fact]
    public async Task Rebind_ThroughTimeWaitResidue_IsImmediate()
    {
        // Platform pin: the drain-start handoff only works because managed
        // HttpListener binds with SO_REUSEADDR-equivalent options — TIME_WAIT
        // sockets left by served connections do NOT block the successor. If a
        // runtime update ever regresses this, fail HERE, not in production
        // cutover. Server-side close (Connection: close) is the residue-
        // producing worst case.
        var port = FreeTcpPort();
        using var a = ApiHost.Start(port);
        for (var i = 0; i < 3; i++)
        {
            using var client = new HttpClient();
            using var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/api/v1/x");
            req.Headers.ConnectionClose = true;
            await client.SendAsync(req);
        }
        a.Stop();   // release the port with the residue still live (Dispose re-Stop is a no-op)

        using var b = ApiHost.Start(port);   // throws if TIME_WAIT blocks the bind
        Assert.True(b.IsListening);
    }
}

// The N1 handoff end to end, with REAL daemons: two identities, one port. The
// incumbent releases at DRAIN START (not exit) and the successor's retry-bind
// picks the port up while the incumbent is still finishing in-flight work.
public class ApiCutoverTests
{
    [Fact]
    public async Task Successor_TakesThePort_WhileTheIncumbentIsStillDraining()
    {
        using var captured = new CapturedLog();
        using var dirA = new TempRuntimeDir();
        using var dirB = new TempRuntimeDir();
        var port = FreeTcpPort();
        using var ctA = new CancellationTokenSource();
        using var ctB = new CancellationTokenSource();
        using var silent = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

        // Incumbent A: owns the port. A hanging handler gives its drain real
        // in-flight work to finish.
        var regA = new Registry().On("UserPromptSubmit", TestHandler.Hanging("straggler"));
        var a = Task.Run(() => DaemonHost.RunAsync(dirA.Paths, NoHarnessDir(), ctA.Token,
            registry: regA, apiPort: port));
        Task<int>? b = null;
        try
        {
            await PollUntilAsync(async () => await ShimClient.TryForwardAsync(dirA.Paths.SocketPath,
                    new HookRequest("cutprb01", "session-start", "claude-code", "{}"u8.ToArray()))
                is ForwardOutcome.Answered, TimeSpan.FromSeconds(15), "incumbent serving");
            // Liveness, not auth: any HTTP status proves A holds the port. This
            // test is about the port handoff; the gate is pinned in ApiAuthTests.
            Assert.True(await ApiPortAnswersAsync(port), "incumbent's API port answers");

            // Successor B: same port, its own identity — its sync attempt loses
            // to A, so it goes into background retry (api.bindContended proves
            // the contention actually happened; hooks serve fine throughout).
            b = Task.Run(() => DaemonHost.RunAsync(dirB.Paths, NoHarnessDir(), ctB.Token, apiPort: port));
            await PollUntilAsync(async () => await ShimClient.TryForwardAsync(dirB.Paths.SocketPath,
                    new HookRequest("cutprb02", "session-start", "claude-code", "{}"u8.ToArray()))
                is ForwardOutcome.Answered, TimeSpan.FromSeconds(15), "successor serving hooks portless");
            Assert.Single(captured.Events, e => e.Evt == "api.bindContended");

            // Pin A's drain open: a silent connection (accepted, nothing sent)
            // holds `active` up to the 10s read deadline, and a real dispatch
            // on the hanging handler rides behind it. FIFO accept means once
            // the dispatch has STARTED, the earlier silent connection is being
            // served too — both are in `active` before we pull the trigger.
            await silent.ConnectAsync(new UnixDomainSocketEndPoint(dirA.Paths.SocketPath));
            var inflight = ShimClient.TryForwardAsync(dirA.Paths.SocketPath,
                new HookRequest("cutslow1", "user-prompt-submit", "claude-code", "{}"u8.ToArray()));
            await PollUntilAsync(() => Task.FromResult(captured.Events.Any(e =>
                    e.Evt == "dispatch.start" && e.Fields.DispatchId == "cutslow1")),
                TimeSpan.FromSeconds(10), "straggler dispatch in flight on the incumbent");

            ctA.Cancel();

            // The release half: A stops its API at drain START — long before exit.
            await PollUntilAsync(
                () => Task.FromResult(captured.Events.Any(e => e.Evt == "api.stopped")),
                TimeSpan.FromSeconds(10), "incumbent released the port at drain start");

            // The acquire half: B's retry lands and the same port answers again
            // under new management while A still drains the silent connection.
            // (Answers = bound; A's listener is Stop()ed, so only B can answer.)
            await PollUntilAsync(() => ApiPortAnswersAsync(port),
                TimeSpan.FromSeconds(10), "successor bound the released port");

            // Handoff order, pinned by enqueue order (not wall clock, which a
            // starved machine can stretch): A enqueues api.stopped BEFORE it
            // releases the port, and B can only bind after the release — so
            // stopped-before-takeover is deterministic, machine speed aside.
            var events = captured.Events.ToArray();
            var stoppedAt = Array.FindIndex(events, e => e.Evt == "api.stopped");
            var takeoverAt = Array.FindLastIndex(events, e => e.Evt == "api.listening");
            Assert.True(0 <= stoppedAt && stoppedAt < takeoverAt,
                "successor bound before the incumbent released");

            // Let A finish: the silent connection closes (EOF), the in-flight
            // dispatch was honored across the drain, exit is clean.
            silent.Close();
            Assert.IsType<ForwardOutcome.Answered>(await inflight);
            Assert.Equal(0, await a.WaitAsync(TimeSpan.FromSeconds(15)));

            // Release-at-drain-START, not at exit: A's own event sequence has
            // api.stopped strictly before its drain-end marker.
            events = captured.Events.ToArray();
            var drainEndAt = Array.FindIndex(events,
                e => e.Evt is "daemon.drained" or "daemon.drainTimeout");
            Assert.True(stoppedAt < drainEndAt,
                "the port must be released at drain start, not at daemon exit");

            ctB.Cancel();
            Assert.Equal(0, await b.WaitAsync(TimeSpan.FromSeconds(15)));
        }
        finally
        {
            // A failed assert must not leak live daemons into the next test's
            // sink (B's one-shot bindBlocked warn fires ~10s in — right around
            // test unwind on failure paths).
            ctA.Cancel();
            ctB.Cancel();
            try { await a.WaitAsync(TimeSpan.FromSeconds(15)); } catch { /* asserted above on the green path */ }
            if (b is not null)
                try { await b.WaitAsync(TimeSpan.FromSeconds(15)); } catch { /* same */ }
        }
    }

    [Fact]
    public async Task Daemon_ServesHooks_WhenTheApiPortIsSquatted()
    {
        // N1's floor: the API never outranks hooks. A daemon whose port is
        // taken (and stays taken) serves dispatches and exits clean anyway.
        using var dir = new TempRuntimeDir();
        var port = FreeTcpPort();
        var squatter = new TcpListener(IPAddress.Loopback, port);
        squatter.Start();
        using var ct = new CancellationTokenSource();
        var daemon = Task.Run(() => DaemonHost.RunAsync(dir.Paths, NoHarnessDir(), ct.Token, apiPort: port));
        try
        {
            await PollUntilAsync(async () => await ShimClient.TryForwardAsync(dir.Paths.SocketPath,
                    new HookRequest("squat001", "user-prompt-submit", "claude-code", "{}"u8.ToArray()))
                is ForwardOutcome.Answered, TimeSpan.FromSeconds(15), "daemon serves with its API port squatted");

            ct.Cancel();
            Assert.Equal(0, await daemon.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            // Never leak a live daemon (still slow-retrying the squatted port)
            // past this test's sink on a failed assert.
            ct.Cancel();
            try { await daemon.WaitAsync(TimeSpan.FromSeconds(15)); } catch { /* asserted above on the green path */ }
            squatter.Stop();
        }
    }
}
