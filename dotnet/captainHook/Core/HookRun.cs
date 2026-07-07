using System.Text.Json;
using CaptainHook.Handlers;
using CaptainHook.Wire;

namespace CaptainHook.Core;

// The collapsed (in-process) dispatch pipeline — today's single-shot path,
// extracted from Program.cs so it has two future callers besides Main
// (ADR-0004): the shim's connect-failure fallback and the daemon's serve loop
// (which reuses the registry/dispatch/emit pieces with construction hoisted).
// Streams are injected — Program.cs passes the real Console; tests pass
// StringReader/StringWriter — keeping the "no Console.* outside Program.cs and
// Demo" invariant intact and the stdout contract assertable in-process.
public static class HookRun
{
    /// The default handler wiring, shared by every mode that dispatches.
    /// Register everything BEFORE the Dispatcher ctor — workers spawn from a
    /// registry snapshot.
    public static Registry BuildDefaultRegistry()
    {
        var registry = new Registry()
            .On("SessionStart", new EchoHandler())
            .On("UserPromptSubmit", new EchoHandler())
            .On("PostToolUse", new EchoHandler());

        // Fan-out demo probe: +150ms and a second inject on every UserPromptSubmit.
        // Opt-in via env so a LIVE deployment doesn't tax every real prompt with it.
        if (Environment.GetEnvironmentVariable("CAPTAINHOOK_PROBE") == "1")
            registry.On("UserPromptSubmit", new LatencyProbeHandler(TimeSpan.FromMilliseconds(150)));

        return registry;
    }

    /// Run one hook dispatch in-process: resolve harness, read stdin, dispatch
    /// under the latency budget, write exactly one effect to stdout and the
    /// human trace to stderr. Returns the process exit code.
    /// `dispatchId`: pass the shim-minted id so a collapsed FALLBACK logs under
    /// the same id as the forward attempt it follows — one id, one story in
    /// the trail (ADR-0004 decision 2); null mints a fresh one (direct
    /// collapsed / shim-less runs).
    public static async Task<int> CollapsedAsync(
        Invocation inv,
        TextReader stdin, TextWriter stdout, TextWriter stderr,
        ColdStartProbe? probe = null, string? harnessDir = null, string? dispatchId = null,
        DispatchPolicy? policy = null)
    {
        // Resolve the harness BEFORE touching stdin/stdout: an unknown name must
        // put a clear error on stderr and NOTHING on stdout (the host parses stdout).
        HarnessSpec spec;
        try { spec = new HarnessRegistry(harnessDir).Get(inv.HarnessName); }
        catch (InvalidOperationException ex)
        {
            await stderr.WriteLineAsync($"captAInHook: {ex.Message}");
            return 1;
        }
        probe?.Resolved();

        string raw = await stdin.ReadToEndAsync();
        JsonElement payload;
        try { payload = JsonSerializer.Deserialize<JsonElement>(string.IsNullOrWhiteSpace(raw) ? "{}" : raw); }
        catch { payload = JsonSerializer.Deserialize<JsonElement>("{}"); }

        // The spec knows which payload fields carry the event name / session / cwd;
        // the CLI arg (kebab-case) wins over the payload's own field.
        var evt = Harness.ParseEvent(spec, inv.EventName, payload);
        probe?.Parsed();

        // One short dispatchId per invocation: every structured log line this run
        // emits carries it, so a digest can stitch the whole dispatch back together.
        dispatchId ??= Guid.NewGuid().ToString("N")[..8];

        // Dispatch policy (ADR-0006): an event-level deny short-circuits BEFORE
        // the dispatcher is even built — answered as a valid Noop, no worker
        // asked, no budget spent, and (unlike the normal path below) no
        // CompleteBackgroundAsync because nothing was dispatched. The Noop rides
        // the SAME gate+serialize tail (DeniedStdout) the worked path uses, so
        // its stdout is byte-identical to an uneventful hook by construction
        // (invariant 1). Default null = no policy consulted (today's behavior);
        // phase 5 resolves and passes the policy here and at the daemon's
        // identical seam.
        if (policy is not null && !policy.Evaluate(evt.Type, evt.Cwd, evt.SessionId).Work)
        {
            stdout.Write(DeniedStdout(spec, evt, dispatchId));
            await stderr.WriteLineAsync($"[captAInHook] {evt.Type}  policy: dispatch denied (event-level) — Noop");
            return 0;
        }

        var dispatcher = new Dispatcher(BuildDefaultRegistry(), budget: TimeSpan.FromSeconds(2));
        probe?.DispatcherBuilt();
        var result = await dispatcher.DispatchAsync(evt, dispatchId);
        probe?.Dispatched();

        // Single-shot: drain background work before exit (the queue itself is
        // long-lived for the daemon's sake; a per-invocation process must not
        // exit with effects still queued). Drain BEFORE rendering the trace so
        // side lines still appear in it, exactly as before the queue moved.
        await dispatcher.CompleteBackgroundAsync();

        // Effect -> stdout (gate first: a harness only ever receives effect kinds
        // its spec declared), human trace -> stderr.
        var final = Harness.ApplyCapabilityGate(spec, evt, result.Merged, dispatchId);
        stdout.Write(ResponseAdapters.Get(spec.ResponseAdapter).Serialize(evt, final));
        await stderr.WriteLineAsync(result.Trace.Render());

        probe?.Emit(dispatchId);   // -> JSONL/stderr, never stdout; after the effect is written
        return 0;
    }

    /// The stdout of a policy-denied dispatch: a valid Noop through the SAME
    /// gate+serialize tail a worked dispatch uses (ADR-0006 decision 3). Because
    /// a real dispatch that merges to Noop takes this exact route, the denied
    /// answer is byte-identical to an uneventful hook — invariant 1 holds by
    /// construction. BOTH dispatch sites (this collapsed path and, in phase 5,
    /// DaemonHost.DispatchOneAsync) call this one helper so they cannot drift.
    public static string DeniedStdout(HarnessSpec spec, HookEvent evt, string? dispatchId = null)
    {
        var noop = Harness.ApplyCapabilityGate(spec, evt, new Effect.Noop(), dispatchId);
        return ResponseAdapters.Get(spec.ResponseAdapter).Serialize(evt, noop);
    }
}
