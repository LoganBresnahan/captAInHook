using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using CaptainHook.Actors;

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
    public static async Task<int> RunAsync(
        RendezvousPaths? pathsOverride = null, string? harnessDir = null, CancellationToken ct = default,
        Registry? registry = null, TimeSpan? drainDeadline = null)
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

        // Drain triggers: real SIGTERM/SIGINT in production, `ct` in tests —
        // one linked source, one code path. ctx.Cancel = true claims the
        // signal so the runtime does not tear the process down under us.
        using var drainCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM,
            sctx => { sctx.Cancel = true; drainCts.Cancel(); });
        using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT,
            sctx => { sctx.Cancel = true; drainCts.Cancel(); });

        var active = 0;   // in-flight connections; drain waits for 0
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
            _ = Task.Run(async () =>
            {
                try { await ServeConnectionAsync(conn, harnesses, dispatcher); }
                finally { Interlocked.Decrement(ref active); }
            }, CancellationToken.None);
        }

        // ---- DRAIN (decision 4): the deadline covers BOTH phases ------------
        // Close the listener first: new connects get refused, so their shims
        // fall back to collapsed — no hook waits on a dying daemon.
        listener.Close();
        var deadline = drainDeadline ?? TimeSpan.FromSeconds(10);
        Log.Info("daemon", "daemon.drainStart", new LogFields
        {
            Data = new Dictionary<string, object>
            {
                ["active"] = Volatile.Read(ref active),
                ["backgroundPending"] = dispatcher.BackgroundPending,
                ["deadlineMs"] = deadline.TotalMilliseconds,
            },
        });

        // Phase 1: in-flight dispatches. Their responses must still be relayed
        // — an accepted request is a promise. Monotonic deadline (Stopwatch).
        var sw = Stopwatch.StartNew();
        while (Volatile.Read(ref active) > 0 && sw.Elapsed < deadline)
            await Task.Delay(20, CancellationToken.None);

        // Phase 2: the Background queue — work that by design outlives its
        // responses (the memory-write scheduled by a session's last hook is
        // exactly what lives here). In-flight is zero (or timed out), so no
        // new enqueues can arrive: completing the queue IS the drain.
        var drained = false;
        var remaining = deadline - sw.Elapsed;
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
        Socket conn, ReloadingHarnessRegistry harnesses, Dispatcher dispatcher)
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

            var res = await DispatchOneAsync(req, harnesses, dispatcher);
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
        HookRequest req, ReloadingHarnessRegistry harnesses, Dispatcher dispatcher)
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

        // The shim minted the id; adopt it — one id stitches the shim half and
        // this half into one story in the trail (ADR-0004 decision 2).
        var result = await dispatcher.DispatchAsync(evt, req.DispatchId);

        var final = Harness.ApplyCapabilityGate(spec, evt, result.Merged, req.DispatchId);
        var stdout = ResponseAdapters.Get(spec.ResponseAdapter).Serialize(evt, final);
        return new HookResponse(0, Encoding.UTF8.GetBytes(stdout), result.Trace.Render());
    }
}
