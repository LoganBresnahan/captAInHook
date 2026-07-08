using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using CaptainHook.Actors;
using CaptainHook.Api;
using CaptainHook.Wire;

namespace CaptainHook.Core;

// captaind (ADR-0004 decisions 1–3): build the registry, dispatcher, and
// supervised workers ONCE, then serve dispatches over the socket. Environment
// (CAPTAINHOOK_*) is read at daemon start and is daemon-start configuration;
// harness specs are the deliberate exception (ReloadingHarnessRegistry keeps
// ADR-0003's edit-a-spec-effective-next-hook contract via one stat per
// dispatch). One connection per dispatch; the response carries the effect's
// stdout bytes VERBATIM. SIGTERM drain and idle-exit are later slices — until
// they land the daemon serves until killed, and a kill is safe: the kernel
// releases the lock, the next winner unlinks the stale socket.
public static class DaemonHost
{
    /// Serve until SIGTERM/SIGINT (production) or `ct` (tests) triggers the
    /// DRAIN (ADR-0004 decision 4): stop accepting, finish in-flight
    /// dispatches AND queued/running Background effects up to `drainDeadline`
    /// (default 10s), then clean up (unlink socket, remove pidfile — the lock
    /// file stays, always) and exit 0. Returns 0 after losing the spawn race
    /// too (some other daemon won — mission accomplished); 1 when the
    /// rendezvous itself is unusable. `registry` is the test seam for handler
    /// behavior; production uses the default wiring.
    /// `idleWindow` (default 30min, env CAPTAINHOOK_IDLE_MS via Program.cs):
    /// MANDATORY idle-exit — captaind has no bounding parent, so a forgotten
    /// or superseded daemon must starve and remove itself; this is also the
    /// version-cutover reaper. `clock` is the monotonic source (TickCount64),
    /// injectable per the house invariant.
    public static async Task<int> RunAsync(
        RendezvousPaths? pathsOverride = null, string? harnessDir = null, CancellationToken ct = default,
        Registry? registry = null, TimeSpan? drainDeadline = null,
        TimeSpan? idleWindow = null, Func<long>? clock = null, string? policyPath = null,
        int? apiPort = null)
    {
        // Daemon-start configuration: the pretty stderr sink defaults OFF in
        // daemon mode — the record is the JSONL file; stderr points at
        // /dev/null anyway once spawned detached. An explicit env setting
        // wins. (The Log sink resolves this lazily on first emit, so setting
        // it here, before any daemon-mode log line, is authoritative.)
        if (Environment.GetEnvironmentVariable("CAPTAINHOOK_LOG_STDERR") is null)
            Environment.SetEnvironmentVariable("CAPTAINHOOK_LOG_STDERR", "off");

        RendezvousPaths paths;
        try
        {
            paths = pathsOverride ?? RendezvousPaths.Resolve();
        }
        catch (Exception ex)
        {
            Log.Error("daemon", "daemon.rendezvousFailed", new LogFields { Msg = ex.Message });
            return 1;
        }

        using var rv = DaemonRendezvous.TryAcquire(paths);
        if (rv is null)
        {
            // Lost the spawn race: a same-version daemon already exists or is
            // warming. Exactly what should happen — exit 0, never linger.
            Log.Info("daemon", "daemon.lostRace", new LogFields
            {
                Data = new Dictionary<string, object> { ["version"] = paths.Version },
            });
            return 0;
        }

        // ---- warm up: everything expensive happens BEFORE the bind ----------
        var harnesses = new ReloadingHarnessRegistry(harnessDir);
        _ = harnesses.Current.Known;                     // force the initial spec load
        // Dispatch policy warms with everything else: resolved once here, then
        // re-read per dispatch only when the file's (mtime,size) moves (ADR-0006
        // decision 6). Absent when no path is configured (tests) => allow all.
        var policy = new ReloadingPolicy(policyPath);
        var dispatcher = new Dispatcher(registry ?? HookRun.BuildDefaultRegistry(), budget: TimeSpan.FromSeconds(2));

        // Listening ⟺ ready: the first connect a shim ever makes against this
        // daemon already finds warm workers.
        using var listener = rv.BindWhenWarm();
        Log.Info("daemon", "daemon.listening", new LogFields
        {
            Data = new Dictionary<string, object>
            {
                ["socket"] = paths.SocketPath,
                ["version"] = paths.Version,
                ["pid"] = Environment.ProcessId,
            },
        });

        // Management API (ADR-0007): a loopback HttpListener beside this UDS
        // serve loop, started only when a port arrives — and only now, after
        // the socket bind, because the API is a face on a serving daemon.
        // Production passes ApiHost.ResolvePort's answer via Program.cs
        // (default 4665 / CAPTAINHOOK_API_PORT / 0-disable); tests pass an
        // explicit port or none. The port is a global singleton (N1) — a
        // draining incumbent may still hold it — so this retry-binds: fast
        // attempts spanning the incumbent's drain deadline, then a warn and a
        // slow cadence, never fatal. Isolated from the dispatch path by
        // construction — its own listener and tasks, sharing no mutable state
        // with the serve loop below.
        var drainBudget = drainDeadline ?? TimeSpan.FromSeconds(10);
        using var api = apiPort is int apiP
            ? ApiHost.StartRetrying(apiP, fastWindow: drainBudget, rendezvous: paths)
            : null;

        // Drain triggers: real SIGTERM/SIGINT in production, `ct` in tests —
        // one linked source, one code path. ctx.Cancel = true claims the
        // signal so the runtime does not tear the process down under us.
        using var drainCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM,
            sctx => { sctx.Cancel = true; drainCts.Cancel(); });
        using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT,
            sctx => { sctx.Cancel = true; drainCts.Cancel(); });

        var active = 0;   // in-flight connections; drain waits for 0

        // ---- mandatory idle-exit (decision 4) --------------------------------
        // Monotonic window; the BASELINE IS PROCESS START (decision 3: a daemon
        // that wedges before ever serving is still reapable). Activity =
        // serving connections OR a non-empty background queue — the watchdog
        // refreshes the stamp while either holds, so background COMPLETION
        // restarts the window (the session-final memory write keeps the daemon
        // open, then it gets a full window before exit). The trigger is the
        // SAME drainCts as SIGTERM: idle-exit and drain agree by construction.
        var clk = clock ?? (() => Environment.TickCount64);
        var idleMs = (long)(idleWindow ?? TimeSpan.FromMinutes(30)).TotalMilliseconds;
        var lastActive = new[] { clk() };
        var idleTick = TimeSpan.FromMilliseconds(Math.Clamp(idleMs / 8.0, 50, 5000));
        _ = Task.Run(async () =>
        {
            while (!drainCts.IsCancellationRequested)
            {
                try { await Task.Delay(idleTick, drainCts.Token); }
                catch (OperationCanceledException) { return; }
                if (Volatile.Read(ref active) > 0 || dispatcher.BackgroundPending > 0)
                {
                    Volatile.Write(ref lastActive[0], clk());
                    continue;
                }
                var idleFor = clk() - Volatile.Read(ref lastActive[0]);
                if (idleFor >= idleMs)
                {
                    Log.Info("daemon", "daemon.idleExit", new LogFields
                    {
                        Data = new Dictionary<string, object>
                        {
                            ["idleMs"] = idleFor,
                            ["windowMs"] = idleMs,
                        },
                    });
                    drainCts.Cancel();
                    return;
                }
            }
        }, CancellationToken.None);

        while (!drainCts.IsCancellationRequested)
        {
            Socket conn;
            try { conn = await listener.AcceptAsync(drainCts.Token); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log.Error("daemon", "daemon.acceptError", new LogFields { Msg = ex.Message });
                break;
            }
            // One task per connection: dispatches run concurrently; per-worker
            // serialization inside the dispatcher is what actually orders work
            // (the concurrency-audit slice soaks exactly this).
            Interlocked.Increment(ref active);
            Volatile.Write(ref lastActive[0], clk());
            _ = Task.Run(async () =>
            {
                try { await ServeConnectionAsync(conn, harnesses, dispatcher, policy); }
                finally
                {
                    Volatile.Write(ref lastActive[0], clk());
                    Interlocked.Decrement(ref active);
                }
            }, CancellationToken.None);
        }

        // ---- DRAIN (decision 4): the deadline covers BOTH phases ------------
        // Close the listener first: new connects get refused, so their shims
        // fall back to collapsed — no hook waits on a dying daemon.
        listener.Close();
        // The API's half of the N1 port handoff: releasing the singleton port
        // HERE — drain start, not exit — is what lets a successor's retry-bind
        // land while this daemon finishes in-flight hooks. Stop() also halts a
        // still-retrying bind so a draining daemon never grabs the port back.
        // The SSE slice grows this into terminating open streams (`using var
        // api` still disposes at method exit as backstop).
        api?.Stop();
        Log.Info("daemon", "daemon.drainStart", new LogFields
        {
            Data = new Dictionary<string, object>
            {
                ["active"] = Volatile.Read(ref active),
                ["backgroundPending"] = dispatcher.BackgroundPending,
                ["deadlineMs"] = drainBudget.TotalMilliseconds,
            },
        });

        // Phase 1: in-flight dispatches. Their responses must still be relayed
        // — an accepted request is a promise. Monotonic deadline (Stopwatch).
        var sw = Stopwatch.StartNew();
        while (Volatile.Read(ref active) > 0 && sw.Elapsed < drainBudget)
            await Task.Delay(20, CancellationToken.None);

        // Phase 2: the Background queue — work that by design outlives its
        // responses (the memory-write scheduled by a session's last hook is
        // exactly what lives here). In-flight is zero (or timed out), so no
        // new enqueues can arrive: completing the queue IS the drain.
        var drained = false;
        var remaining = drainBudget - sw.Elapsed;
        if (Volatile.Read(ref active) == 0 && remaining > TimeSpan.Zero)
        {
            try
            {
                await dispatcher.CompleteBackgroundAsync().WaitAsync(remaining);
                drained = true;
            }
            catch (TimeoutException) { /* fall through to the timeout log */ }
        }

        if (drained)
        {
            Log.Info("daemon", "daemon.drained", new LogFields { DurMs = sw.Elapsed.TotalMilliseconds });
        }
        else
        {
            // Deadline expired with work still pending: exit anyway — but the
            // dropped work is VISIBLE, never silent.
            Log.Warn("daemon", "daemon.drainTimeout", new LogFields
            {
                DurMs = sw.Elapsed.TotalMilliseconds,
                Data = new Dictionary<string, object>
                {
                    ["active"] = Volatile.Read(ref active),
                    ["backgroundPending"] = dispatcher.BackgroundPending,
                },
            });
        }
        return 0;   // `using rv` unlinks socket + pidfile; the lock file stays
    }

    /// One connection = one dispatch: read a request frame, dispatch, answer,
    /// close. Failures are the CONNECTION's problem, never the daemon's — log
    /// and carry on serving.
    private static async Task ServeConnectionAsync(
        Socket conn, ReloadingHarnessRegistry harnesses, Dispatcher dispatcher, ReloadingPolicy policy)
    {
        using var _ = conn;
        await using var stream = new NetworkStream(conn, ownsSocket: false);
        // Read deadline: a client that connects and never sends must not pin
        // a connection task forever.
        using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            var payload = await Frame.ReadAsync(stream, readCts.Token);
            if (payload is null) return;   // clean EOF before a request: peer gave up

            HookRequest req;
            try
            {
                req = HookRequest.Decode(payload);
            }
            catch (InvalidDataException ex)
            {
                Log.Warn("daemon", "daemon.badRequest", new LogFields { Msg = ex.Message });
                await Frame.WriteAsync(stream, new HookResponse(1, [], $"captAInHook: {ex.Message}").Encode());
                return;
            }

            var res = await DispatchOneAsync(req, harnesses, dispatcher, policy);
            await Frame.WriteAsync(stream, res.Encode());
        }
        catch (Exception ex)
        {
            // Truncated frame, mid-write disconnect, deadline: this dispatch's
            // client is gone or broken; the serve loop is unaffected.
            Log.Warn("daemon", "daemon.connError", new LogFields { Msg = ex.Message });
        }
    }

    /// The daemon-side dispatch pipeline — the collapsed pipeline with
    /// construction hoisted: resolve spec (reloading registry), parse, dispatch
    /// on the SHARED dispatcher under the shim's dispatchId, gate, serialize.
    private static async Task<HookResponse> DispatchOneAsync(
        HookRequest req, ReloadingHarnessRegistry harnesses, Dispatcher dispatcher, ReloadingPolicy policy)
    {
        HarnessSpec spec;
        try
        {
            spec = harnesses.Current.Get(req.HarnessName);
        }
        catch (InvalidOperationException ex)
        {
            // Unknown --harness, decided daemon-side: exit 1, zero stdout bytes.
            return new HookResponse(1, [], $"captAInHook: {ex.Message}");
        }

        var raw = Encoding.UTF8.GetString(req.StdinBytes);
        JsonElement payload;
        try { payload = JsonSerializer.Deserialize<JsonElement>(string.IsNullOrWhiteSpace(raw) ? "{}" : raw); }
        catch { payload = JsonSerializer.Deserialize<JsonElement>("{}"); }
        var evt = Harness.ParseEvent(spec, req.EventName, payload);

        // Dispatch policy (ADR-0006): the SAME shared gate the collapsed path
        // calls, at the identical seam — an event-level deny (or a Malformed
        // file) answers a byte-identical Noop without dispatching; otherwise the
        // handler exclusions ride into the fan-out. policy.Current is the
        // stat-gated resolution — an edit is effective next hook, no re-parse
        // per hook (ReloadingPolicy).
        var gate = HookRun.PolicyGateFor(policy.Current, spec, evt, req.DispatchId);
        if (gate.IsShortCircuit)
            return new HookResponse(0, Encoding.UTF8.GetBytes(gate.DeniedStdout!), gate.TraceLine!);

        // The shim minted the id; adopt it — one id stitches the shim half and
        // this half into one story in the trail (ADR-0004 decision 2).
        var result = await dispatcher.DispatchAsync(evt, req.DispatchId, gate.Excluded);

        var final = Harness.ApplyCapabilityGate(spec, evt, result.Merged, req.DispatchId);
        var stdout = ResponseAdapters.Get(spec.ResponseAdapter).Serialize(evt, final);
        return new HookResponse(0, Encoding.UTF8.GetBytes(stdout), result.Trace.Render());
    }
}
