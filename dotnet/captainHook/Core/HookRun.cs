using System.Text.Json;
using CaptainHook.Actors;
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
    /// registry snapshot. `handlersPath` is ADR-0010 d4's registration file:
    /// null ⇒ zero exec handlers (the test-safe default, the policyPath
    /// idiom); Program.cs passes ExecHandlersFile.ResolvePath() so all three
    /// production entry points read the same file.
    public static Registry BuildDefaultRegistry(string? handlersPath = null, TimeSpan? harnessTimeoutHint = null,
                                                TimeSpan? drainBudgetHint = null)
    {
        var registry = new Registry()
            .On("SessionStart", new EchoHandler())
            .On("UserPromptSubmit", new EchoHandler())
            .On("PostToolUse", new EchoHandler());

        // Fan-out demo probe: +150ms and a second inject on every UserPromptSubmit.
        // Opt-in via env so a LIVE deployment doesn't tax every real prompt with it.
        if (Environment.GetEnvironmentVariable("CAPTAINHOOK_PROBE") == "1")
            registry.On("UserPromptSubmit", new LatencyProbeHandler(TimeSpan.FromMilliseconds(150)));

        // Exec handlers register AFTER the coded set, in file order — the
        // registration order Merge depends on stays deterministic: coded
        // first, then handlers.json top to bottom.
        if (handlersPath is not null)
            RegisterExecHandlers(registry, ExecHandlersFile.Resolve(handlersPath), harnessTimeoutHint,
                                 drainBudgetHint);

        return registry;
    }

    /// ADR-0010 d4's registration semantics over a resolved handlers file —
    /// one shared routine so the collapsed and daemon paths cannot drift.
    /// Absent is silent (zero-config default). Malformed registers NOTHING
    /// and is loud (`handlers.malformed`). Loaded registers each valid
    /// oneshot entry via the FACTORY overload (a supervised restart gets a
    /// fresh handler — the shape the resident slice requires); invalid
    /// entries arrive pre-skipped from the parser (`handlers.entrySkipped`);
    /// resident entries parse as valid data but are SKIPPED loudly until the
    /// resident-child-runtime slice lands their runtime. A oneshot entry on a
    /// before-tools event draws `handlers.slowShape` (d7: loud guidance — a
    /// cold interpreter per TOOL CALL re-imposes the tax item 12 killed).
    /// `harnessTimeoutHint` is decision 9's informational boundary: the
    /// harness's own hook-command timeout (from the default HarnessSpec's
    /// hookTimeoutHintMs). A budget past it draws
    /// `handlers.budgetBeyondHarness` — loud, never enforced, never
    /// auto-synced into harness config. `drainBudgetHint` is the N6
    /// boundary, same doctrine: the daemon's drain deadline — a budget whose
    /// ask window (budget + grace) outlasts it can be CUT at cutover or
    /// idle-exit (child killed, effect lost), so registration says so
    /// (`handlers.budgetBeyondDrain`). Collapsed mode passes null: no
    /// daemon, no drain to be cut by.
    public static void RegisterExecHandlers(Registry registry, ExecHandlersResolution resolution,
                                            TimeSpan? harnessTimeoutHint = null,
                                            TimeSpan? drainBudgetHint = null)
    {
        switch (resolution)
        {
            case ExecHandlersResolution.Absent:
                return;

            case ExecHandlersResolution.Malformed m:
                Log.Error("handlers", "handlers.malformed", new LogFields { Msg = m.Error });
                return;

            case ExecHandlersResolution.Loaded(var entries, var skipped):
                foreach (var s in skipped)
                    Log.Warn("handlers", "handlers.entrySkipped", new LogFields
                    {
                        Msg = string.Join("; ", s.Violations),
                        Data = new Dictionary<string, object> { ["entry"] = s.Label },
                    });

                foreach (var entry in entries)
                {
                    // Loudness symmetry: parse-valid fields the CURRENT
                    // slices cannot honor yet are loud (adversarial-verify
                    // find). env/passEnv/cwd are enforced as of the
                    // child-env-allowlist slice; only the resident-slice
                    // field remains inert on a oneshot entry.
                    if (entry.ReadinessTimeout is not null && entry.Mode == ExecMode.Oneshot)
                        Log.Warn("handlers", "handlers.fieldIgnored", new LogFields
                        {
                            Msg = "not yet enforced (lands with the resident slice): readinessTimeoutMs",
                            Data = new Dictionary<string, object> { ["entry"] = entry.Name },
                        });

                    if (harnessTimeoutHint is { } hint && entry.Budget is { } b && b > hint)
                        Log.Warn("handlers", "handlers.budgetBeyondHarness", new LogFields
                        {
                            Msg = $"budget {b.TotalMilliseconds:F0}ms exceeds the harness's hook timeout (~{hint.TotalMilliseconds:F0}ms): the harness may abandon the shim before the answer — the daemon still completes the work, but the effect is lost (ADR-0010 d9)",
                            Data = new Dictionary<string, object> { ["entry"] = entry.Name },
                        });

                    if (drainBudgetHint is { } drain && entry.Budget is { } db
                        && db + Dispatcher.GraceFor(db) > drain)
                        Log.Warn("handlers", "handlers.budgetBeyondDrain", new LogFields
                        {
                            Msg = $"budget {db.TotalMilliseconds:F0}ms (+grace) exceeds the daemon's drain deadline ({drain.TotalMilliseconds:F0}ms): a dispatch still running at cutover or idle-exit is CUT — its child is killed and the effect degrades to the fail mode (ADR-0010 N6)",
                            Data = new Dictionary<string, object> { ["entry"] = entry.Name },
                        });

                    if (entry.Mode == ExecMode.Resident)
                    {
                        Log.Warn("handlers", "handlers.entrySkipped", new LogFields
                        {
                            Msg = "mode 'resident' is not yet runnable — parses as valid, lands with the resident-child-runtime slice",
                            Data = new Dictionary<string, object> { ["entry"] = entry.Name },
                        });
                        continue;
                    }

                    foreach (var evt in entry.Events)
                    {
                        if (evt == "PreToolUse")
                            Log.Warn("handlers", "handlers.slowShape", new LogFields
                            {
                                HookEvent = evt,
                                Msg = $"oneshot handler '{entry.Name}' on a before-tools event: a process spawn on EVERY tool call, serially, on the agent's critical path — prefer mode 'resident'",
                                Data = new Dictionary<string, object> { ["entry"] = entry.Name },
                            });
                        var e = entry;   // capture per closure
                        registry.On(evt, e.Name,
                            () => new ExecHandler(e.Name, e.Command, e.Args, e.OnFailure,
                                                  e.Env, e.PassEnv, e.Cwd),
                            e.OnFailure, e.Budget);
                    }
                }
                return;
        }
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
        string? policyPath = null, string? handlersPath = null)
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

        // Dispatch policy (ADR-0006): the ONE shared gate both wire sites call
        // (this and DaemonHost.DispatchOneAsync) so they cannot drift. A
        // short-circuit answers a valid Noop BEFORE the dispatcher is built — no
        // worker asked, no budget spent, and (unlike the normal path below) no
        // CompleteBackgroundAsync, because nothing was dispatched. Otherwise the
        // gate's handler exclusions ride into the fan-out. policyPath null =
        // Absent = allow all (today's behavior); Program.cs passes the resolved
        // default path.
        var gate = PolicyGateFor(policyPath, spec, evt, dispatchId);
        if (gate.IsShortCircuit)
        {
            stdout.Write(gate.DeniedStdout!);
            await stderr.WriteLineAsync(gate.TraceLine);
            return 0;
        }

        var dispatcher = new Dispatcher(BuildDefaultRegistry(handlersPath, spec.HookTimeoutHint), budget: TimeSpan.FromSeconds(2));
        probe?.DispatcherBuilt();
        var result = await dispatcher.DispatchAsync(evt, dispatchId, gate.Excluded);
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
    /// construction. BOTH dispatch sites call this one helper so they can't drift.
    public static string DeniedStdout(HarnessSpec spec, HookEvent evt, string? dispatchId = null)
    {
        var noop = Harness.ApplyCapabilityGate(spec, evt, new Effect.Noop(), dispatchId);
        return ResponseAdapters.Get(spec.ResponseAdapter).Serialize(evt, noop);
    }

    /// The dispatch-policy gate (ADR-0006 phase 5): the SINGLE shared entry both
    /// wire sites call, so the daemon and collapsed paths cannot drift when most
    /// runs exercise only one. Resolves the file at `policyPath` (null => Absent
    /// => allow all), then gates. Resolve runs per call — hot reload is free;
    /// phase 6 adds the (mtime,size) stat-gate to skip the re-parse.
    public static PolicyGate PolicyGateFor(string? policyPath, HarnessSpec spec, HookEvent evt, string? dispatchId) =>
        PolicyGateFor(
            policyPath is null ? new PolicyResolution.Absent() : PolicyResolution.Resolve(policyPath),
            spec, evt, dispatchId);

    /// The pure gate over an already-resolved policy — Work=false (event-level
    /// deny OR a Malformed file) short-circuits to a byte-identical Noop plus a
    /// trace line (the Malformed case names the fault); Work=true proceeds,
    /// carrying the handler exclusions. Every non-happy outcome leaves a
    /// structured trail line (policy.skip / policy.malformed / policy.exclude) —
    /// emitted HERE, in the one shared gate, so the daemon and collapsed trails
    /// cannot drift either. The plain proceed-with-no-exclusions path is silent.
    public static PolicyGate PolicyGateFor(PolicyResolution resolution, HarnessSpec spec, HookEvent evt, string? dispatchId)
    {
        var outcome = resolution.Evaluate(evt.Type, evt.Cwd, evt.SessionId);
        if (outcome.Work)
        {
            if (outcome.ExcludedHandlers.Count > 0)
                Log.Info("policy", "policy.exclude", PolicyFields(evt, dispatchId,
                    data: new Dictionary<string, object> { ["excluded"] = string.Join(",", outcome.ExcludedHandlers) }));
            return PolicyGate.Proceed(outcome.ExcludedHandlers);
        }

        string trace;
        if (resolution is PolicyResolution.Malformed m)
        {
            // Decision 4: unparseable policy Noops every hook LOUDLY.
            trace = $"[captAInHook] {evt.Type}  policy: MALFORMED ({m.Error}) — every hook denied";
            Log.Warn("policy", "policy.malformed", PolicyFields(evt, dispatchId, msg: m.Error));
        }
        else
        {
            trace = $"[captAInHook] {evt.Type}  policy: dispatch denied (event-level)";
            Log.Info("policy", "policy.skip", PolicyFields(evt, dispatchId));
        }
        return PolicyGate.ShortCircuit(DeniedStdout(spec, evt, dispatchId), trace);
    }

    private static LogFields PolicyFields(HookEvent evt, string? dispatchId,
                                          string? msg = null, IDictionary<string, object>? data = null) =>
        new() { DispatchId = dispatchId, SessionId = evt.SessionId, HookEvent = evt.Type, Msg = msg, Data = data };
}

/// The result of the policy gate at a wire site. A short-circuit carries the
/// byte-identical Noop stdout and the trace line to emit (the dispatcher is
/// never built); otherwise Excluded names the handlers to drop from the
/// fan-out (empty = none). One type, both sites — the anti-drift seam.
public sealed record PolicyGate(string? DeniedStdout, string? TraceLine, IReadOnlySet<string> Excluded)
{
    private static readonly IReadOnlySet<string> None = new HashSet<string>();
    public static PolicyGate Proceed(IReadOnlySet<string> excluded) => new(null, null, excluded);
    public static PolicyGate ShortCircuit(string stdout, string trace) => new(stdout, trace, None);
    public bool IsShortCircuit => DeniedStdout is not null;
}
