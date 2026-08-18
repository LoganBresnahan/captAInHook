using System.Text;
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
    /// `collapsedEvent` is the phase-6 degrade seam: the daemon passes null
    /// (warm every resident child at start, for every event); a COLLAPSED run
    /// passes the one event it is about to dispatch, so only that event's
    /// exec handlers register — a resident child for an unrelated event never
    /// spuriously spawns for a single-shot hook (ADR-0010 d3: "spawn, serve
    /// THE one dispatch, terminate at drain"). The teardown-at-drain half is
    /// `CollapsedAsync`'s `DisposeHandlersAsync` call.
    public static Registry BuildDefaultRegistry(string? handlersPath = null, TimeSpan? harnessTimeoutHint = null,
                                                TimeSpan? drainBudgetHint = null, string? collapsedEvent = null)
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
                                 drainBudgetHint, collapsedEvent);

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
    /// (`handlers.budgetBeyondDrain`). Collapsed mode passes a null drain
    /// hint (no daemon, no drain to be cut by).
    /// (phase 6) `collapsedEvent`: the daemon passes null; a COLLAPSED run
    /// passes the one event it is about to dispatch. Non-null both FILTERS
    /// registration to that event (so an unrelated event's resident child
    /// never spawns for a single-shot hook) and marks the degrade — a
    /// resident entry runs the SAME ResidentExecHandler, its lifecycle merely
    /// spawn->serve-one->die there (bounded by CollapsedAsync's
    /// DisposeHandlersAsync), so a fail-closed gate genuinely RUNS (real
    /// verdict or fail-closed deny) instead of the old interim stub.
    public static void RegisterExecHandlers(Registry registry, ExecHandlersResolution resolution,
                                            TimeSpan? harnessTimeoutHint = null,
                                            TimeSpan? drainBudgetHint = null,
                                            string? collapsedEvent = null)
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
                    // Collapsed-degrade filter: register only the exec
                    // handlers that can fire THIS dispatch — an entry whose
                    // events don't include the collapsed event registers
                    // nothing (and, for a resident entry, spawns nothing).
                    var events = collapsedEvent is null
                        ? entry.Events
                        : entry.Events.Where(ev => string.Equals(ev, collapsedEvent, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (events.Count == 0) continue;

                    // The reload fingerprint (ADR-0010 phase 7): every worker
                    // this entry registers carries it, so a later reload's
                    // Dispatcher.Reconcile can tell an unchanged entry (keep the
                    // warm child) from an edited one (replace it). Config-only
                    // by design — event + name are the worker's id, not its
                    // version — so editing an entry's events list churns only
                    // the added/removed events' children.
                    var fingerprint = ExecFingerprint(entry);

                    // Loudness symmetry: parse-valid fields the CURRENT
                    // slices cannot honor yet are loud (adversarial-verify
                    // find). env/passEnv/cwd are enforced as of the
                    // child-env-allowlist slice; only the resident-slice
                    // field remains inert on a oneshot entry.
                    if (entry.ReadinessTimeout is not null && entry.Mode == ExecMode.Oneshot)
                        Log.Warn("handlers", "handlers.fieldIgnored", new LogFields
                        {
                            Msg = "readinessTimeoutMs applies only to mode 'resident' — ignored on this oneshot entry",
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
                        // Compare against the EFFECTIVE readiness (the 10s
                        // default counts too — a defaulted-readiness /
                        // tight-budget entry warms past its budget just the
                        // same).
                        var effReadiness = entry.ReadinessTimeout ?? ResidentExecHandler.DefaultReadinessTimeout;
                        var effBudget = entry.Budget ?? TimeSpan.FromSeconds(2);
                        if (effReadiness > effBudget)
                            Log.Warn("handlers", "handlers.readinessBeyondBudget", new LogFields
                            {
                                Msg = $"readiness window {effReadiness.TotalMilliseconds:F0}ms exceeds the entry's budget " +
                                      $"({effBudget.TotalMilliseconds:F0}ms): dispatches arriving during warm-up take the " +
                                      "fail mode until the child readies (loud, uncounted)",
                                Data = new Dictionary<string, object> { ["entry"] = entry.Name },
                            });

                        // Fan-out is a DAEMON truth (N children warm at once);
                        // in a collapsed run only the dispatched event's child
                        // spawns, so the warn would mislead.
                        if (collapsedEvent is null && entry.Events.Count > 1)
                            Log.Info("handlers", "handlers.residentFanout", new LogFields
                            {
                                Msg = $"resident entry spans {entry.Events.Count} events ⇒ {entry.Events.Count} independent children (one per event, state not shared — externalize shared state)",
                                Data = new Dictionary<string, object> { ["entry"] = entry.Name },
                            });
                        // The degrade (ADR-0010 d3): in collapsed mode the
                        // resident child runs spawn→serve-one→die, bounded by
                        // CollapsedAsync's DisposeHandlersAsync — visible so
                        // the daemon-down path is never a silent surprise.
                        if (collapsedEvent is not null)
                            Log.Info("handlers", "handlers.residentDegraded", new LogFields
                            {
                                HookEvent = collapsedEvent,
                                Msg = $"no daemon: resident '{entry.Name}' runs oneshot-lifecycle for this collapsed dispatch (spawn, serve, terminate)",
                                Data = new Dictionary<string, object> { ["entry"] = entry.Name },
                            });
                        foreach (var evt in events)
                        {
                            var e2 = entry;
                            var evt2 = evt;
                            registry.On(evt, e2.Name,
                                () => new ResidentExecHandler(e2.Name, e2.Command, e2.Args, e2.OnFailure,
                                                              e2.Env, e2.PassEnv, e2.Cwd,
                                                              e2.ReadinessTimeout, evt2),
                                e2.OnFailure, e2.Budget, reloadFingerprint: fingerprint);
                        }
                        continue;
                    }

                    foreach (var evt in events)
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
                            e.OnFailure, e.Budget, reloadFingerprint: fingerprint);
                    }
                }
                return;
        }
    }

    /// A reload fingerprint for an exec entry (ADR-0010 phase 7): a stable
    /// string over every field that changes the CHILD — command, args (ordered:
    /// argv is semantic), mode, fail mode, budget, readiness, env (key-sorted),
    /// passEnv (sorted: a set, order is not semantic), cwd. The event and name
    /// are the worker's IDENTITY (its id), so they are deliberately ABSENT here:
    /// editing an entry's events list adds/removes whole workers rather than
    /// churning the survivors' warm children. Two reloads whose entry produces
    /// the same fingerprint ⇒ Reconcile keeps the child.
    ///
    /// Framing is LENGTH-PREFIXED (LEN:VALUE) with a count before every list, so
    /// the encoding is INJECTIVE — no two distinct configs collide whatever
    /// bytes a command/arg/env-pair/cwd contains. A naive separator scheme is
    /// NOT safe: args ["a","b"] and ["a\x1fb"] would share a "\x1f"-joined form,
    /// and env {"A":"B=C"} (a value with "=", a NORMAL config) and {"A=B":"C"}
    /// would share "A=B=C" — a silent stale-child bug the adversarial verify
    /// targets. Length prefixes make the boundaries unambiguous. (internal for
    /// the injectivity unit test.)
    internal static string ExecFingerprint(ExecEntry e)
    {
        var sb = new StringBuilder("v1");
        void Str(string s) => sb.Append('|').Append(s.Length).Append(':').Append(s);
        void Num(long n) => sb.Append('|').Append(n);

        Str(e.Command);
        Num(e.Args.Count);
        foreach (var a in e.Args) Str(a);
        Num((int)e.Mode);
        Num((int)e.OnFailure);
        Num(e.Budget?.Ticks ?? -1);
        Num(e.ReadinessTimeout?.Ticks ?? -1);
        var envKeys = e.Env.Keys.OrderBy(x => x, StringComparer.Ordinal).ToList();
        Num(envKeys.Count);
        foreach (var k in envKeys) { Str(k); Str(e.Env[k]); }
        var pass = e.PassEnv.OrderBy(x => x, StringComparer.Ordinal).ToList();
        Num(pass.Count);
        foreach (var p in pass) Str(p);
        Str(e.Cwd ?? "");
        return sb.ToString();
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
        // An INTERNAL harness cannot answer a hook (ADR-0017 d5): it has no
        // wire format, so serializing here would put the empty string on the
        // sacred channel. Refused exactly like an unknown name — clear stderr,
        // zero stdout bytes — which is also what keeps "internal never reaches
        // a stdout-serialize path" true for a caller who simply typed it.
        if (!spec.AnswersHooks)
        {
            await stderr.WriteLineAsync(
                $"captAInHook: harness '{spec.Name}' is internal — it has no hook wire format and cannot answer a hook");
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

        // The Dispatcher ctor EAGER-SPAWNS resident children (degrade —
        // ADR-0010 d3). It lives INSIDE the try so any throw from
        // BuildDefaultRegistry/DispatchAsync/emit is reaped by the finally.
        // The ctor itself is non-throwing AFTER any spawn by contract
        // (StartCoreAsync swallows every spawn failure into a Failed
        // transition, worker ids are de-duped, budgets are pre-checked at
        // registration), so a ctor that threw would have spawned nothing to
        // orphan — `dispatcher` stays null and the finally is correctly a
        // no-op.
        Dispatcher? dispatcher = null;
        try
        {
            // Degrade-at-construction (phase 6): the collapsed run passes its
            // ONE event so BuildDefaultRegistry registers only that event's
            // exec handlers — a resident child for an unrelated event never
            // spawns for this single-shot hook — and a resident entry runs the
            // real ResidentExecHandler, spawn→serve-one→die.
            dispatcher = new Dispatcher(
                BuildDefaultRegistry(handlersPath, spec.HookTimeoutHint, collapsedEvent: evt.Type),
                budget: TimeSpan.FromSeconds(2));
            probe?.DispatcherBuilt();
            var result = await dispatcher.DispatchAsync(evt, dispatchId, gate.Excluded);
            probe?.Dispatched();

            // Single-shot: drain background work before exit (the queue itself
            // is long-lived for the daemon's sake; a per-invocation process
            // must not exit with effects still queued). Drain BEFORE rendering
            // the trace so side lines still appear in it, exactly as before the
            // queue moved.
            await dispatcher.CompleteBackgroundAsync();

            // Effect -> stdout (gate first: a harness only ever receives effect
            // kinds its spec declared), human trace -> stderr.
            var final = Harness.ApplyCapabilityGate(spec, evt, result.Merged, dispatchId);
            stdout.Write(ResponseAdapters.Get(spec.ResponseAdapter).Serialize(evt, final));
            await stderr.WriteLineAsync(result.Trace.Render());

            probe?.Emit(dispatchId);   // -> JSONL/stderr, never stdout; after the effect is written
            return 0;
        }
        finally
        {
            // Teardown-at-drain, ordered AFTER the stdout answer (the promise
            // is kept first): kill any exec child this run spawned — the whole
            // point of the degrade is that a collapsed run NEVER orphans a
            // resident child (N3 fires exactly when the daemon is down). In a
            // `finally` so a mid-dispatch throw still reaps. Bounded so a
            // stuck child can't hang the hook — 8s dominates a single child's
            // dispose worst case (a wedged in-flight spawn's ~5s startTask
            // join + the 2s kill grace); children dispose in parallel, so
            // multiple entries don't stack. Null-guarded per the ctor
            // contract above (null ⇒ nothing spawned ⇒ nothing to reap).
            try { if (dispatcher is not null) await dispatcher.DisposeHandlersAsync().WaitAsync(TimeSpan.FromSeconds(8)); }
            catch (TimeoutException)
            {
                Log.Warn("handlers", "handlers.collapsedTeardownTimeout", new LogFields
                {
                    DispatchId = dispatchId,
                    Msg = "collapsed handler teardown outran its bound; exiting anyway — run doctor for orphans",
                });
            }
        }
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
        var ruling = DecidePolicy(resolution, evt, dispatchId);
        return ruling.Work
            ? PolicyGate.Proceed(ruling.Excluded)
            : PolicyGate.ShortCircuit(DeniedStdout(spec, evt, dispatchId), ruling.TraceLine!);
    }

    /// The policy DECISION and its trail lines, with no wire format anywhere
    /// near it — split out of `PolicyGateFor` for ADR-0017 d5's internal event.
    ///
    /// An internal dispatch (`MailNudge`) has no stdout and nobody waiting for
    /// an answer, so a denial there is **logged, not answered** (N3). It could
    /// not call `PolicyGateFor` at all, because that helper's short-circuit is
    /// a serialized Noop — the one thing an internal event must never produce.
    /// Copying the three trail lines into the nudge path instead would put the
    /// consent surface's own record in two places, which is exactly what the
    /// shared gate was extracted to prevent. So the decision and its lines live
    /// HERE, one emitter, and only the callers that own a stdout go on to build
    /// one.
    public static PolicyRuling DecidePolicy(PolicyResolution resolution, HookEvent evt, string? dispatchId)
    {
        var outcome = resolution.Evaluate(evt.Type, evt.Cwd, evt.SessionId);
        if (outcome.Work)
        {
            if (outcome.ExcludedHandlers.Count > 0)
                Log.Info("policy", "policy.exclude", PolicyFields(evt, dispatchId,
                    data: new Dictionary<string, object> { ["excluded"] = string.Join(",", outcome.ExcludedHandlers) }));
            return PolicyRuling.Proceed(outcome.ExcludedHandlers);
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
        return PolicyRuling.Deny(trace);
    }

    private static LogFields PolicyFields(HookEvent evt, string? dispatchId,
                                          string? msg = null, IDictionary<string, object>? data = null) =>
        new() { DispatchId = dispatchId, SessionId = evt.SessionId, HookEvent = evt.Type, Msg = msg, Data = data };
}

/// What the policy said, before anybody asks what to WRITE about it. Work=false
/// carries the trace line the decision already logged; a caller with a stdout
/// turns that into a byte-identical Noop (`PolicyGateFor`), and a caller
/// without one — the internal nudge path — simply stops.
public sealed record PolicyRuling(bool Work, IReadOnlySet<string> Excluded, string? TraceLine)
{
    private static readonly IReadOnlySet<string> None = new HashSet<string>();
    public static PolicyRuling Proceed(IReadOnlySet<string> excluded) => new(true, excluded, null);
    public static PolicyRuling Deny(string trace) => new(false, None, trace);
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

/// The daemon's handlers.json view (ADR-0010 phase 7, the hot-reload half):
/// resolve once at daemon start (the initial registry already reflects the
/// file — the ctor just SEEDS the stamp so the first hook does not re-reconcile
/// what start already built), then per dispatch take a cheap (mtime,size) stamp
/// and RECONCILE the dispatcher's exec workers ONLY when the file moves.
/// Mirrors ReloadingPolicy's stat-gate exactly — the benign race, the
/// poison-and-advance discipline — but where ReloadingPolicy swaps an immutable
/// resolution, this drives Dispatcher.Reconcile, which diffs the workers so
/// unchanged entries keep their warm children (no churn) and a malformed edit
/// kills every resident (no keep-last-good, ADR-0010 d4). The collapsed path is
/// single-shot and just builds once, so only the long-lived daemon needs this.
/// Stamp comparison is EQUALITY on the wall-clock mtime (change detection, like
/// content identity), never interval math — the monotonic rule is untouched.
public sealed class ReloadingHandlers
{
    private readonly string _path;
    private readonly Dispatcher _dispatcher;
    private readonly TimeSpan? _harnessTimeoutHint;
    private readonly TimeSpan? _drainBudgetHint;
    private readonly object _lock = new();
    private volatile string _stamp;

    public ReloadingHandlers(string handlersPath, Dispatcher dispatcher,
                             TimeSpan? harnessTimeoutHint = null, TimeSpan? drainBudgetHint = null)
    {
        _path = handlersPath;
        _dispatcher = dispatcher;
        _harnessTimeoutHint = harnessTimeoutHint;
        _drainBudgetHint = drainBudgetHint;
        _stamp = Stamp(handlersPath);
    }

    /// Stat-gate + reconcile, called per dispatch before the fan-out. The fast
    /// path (unchanged) is a single stat + string compare, lock-free. On a
    /// change exactly one caller wins the reconcile under the lock (double-
    /// checked against the freshest stamp); the others see the advanced stamp
    /// and skip. Reconcile itself is fast — a dictionary diff + supervisor
    /// add/remove, with child kills fire-and-forget through the teardown seam —
    /// so the triggering hook is barely delayed, and a just-added resident is
    /// still warming and takes fail-mode until its readiness handshake, exactly
    /// as at daemon start.
    public void MaybeReload()
    {
        var s = Stamp(_path);
        if (s == _stamp) return;
        lock (_lock)
        {
            s = Stamp(_path);
            if (s == _stamp) return;   // another dispatch won the reconcile
            // BuildDefaultRegistry re-validates the file end-to-end on EVERY
            // reload: it logs handlers.malformed / handlers.entrySkipped and
            // every registration warn (slowShape, budgetBeyond*,
            // residentFanout, ...), and a malformed file yields ZERO exec
            // handlers so the reconcile removes every resident. Daemon mode ⇒
            // collapsedEvent null ⇒ warm all events.
            var fresh = HookRun.BuildDefaultRegistry(_path, _harnessTimeoutHint, _drainBudgetHint);
            var summary = _dispatcher.Reconcile(fresh);
            _stamp = s;
            Log.Info("handlers", "handlers.reload", new LogFields
            {
                Data = new Dictionary<string, object>
                {
                    ["path"] = _path,
                    ["added"] = summary.Added,
                    ["removed"] = summary.Removed,
                    ["changed"] = summary.Changed,
                    ["kept"] = summary.Kept,
                },
            });
        }
    }

    /// Identical to ReloadingPolicy.Stamp: (mtime,size) with the <dir>/<absent>
    /// sentinels so a directory appearing where the file belongs, or the file
    /// vanishing, each FLIP the stamp (reconcile to zero exec handlers) rather
    /// than aliasing a real state.
    private static string Stamp(string path)
    {
        if (Directory.Exists(path)) return "<dir>";
        var fi = new FileInfo(path);
        return fi.Exists ? $"{fi.LastWriteTimeUtc.Ticks}|{fi.Length}" : "<absent>";
    }
}
