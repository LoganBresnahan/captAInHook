using System.Diagnostics;
using System.Threading.Channels;
using CaptainHook.Actors;

namespace CaptainHook.Core;

/// A CHILD SPEC, not a live handler: everything the dispatcher needs to
/// (re)create a handler — its name, fail mode, a factory, and (ADR-0010
/// decision 9) an optional per-handler budget that may EXCEED the dispatcher
/// default. This is the OTP child-spec idea ported to the registry: "restart"
/// means "run the factory again", so a restarted worker gets a genuinely
/// fresh handler.
internal sealed record HandlerSpec(string Name, FailMode OnFailure, Func<IHandler> Factory, TimeSpan? Budget = null);

public sealed class Registry
{
    // Registration order is preserved end-to-end: Merge's inject-concatenation
    // and last-replace-wins rules depend on it.
    private readonly List<(string EventType, HandlerSpec Spec)> _specs = new();

    /// Instance registration. Each handler is wrapped as a spec whose factory
    /// returns THE SAME instance — so instance-registered handlers are NOT
    /// state-reset by a supervisor restart (fine for stateless handlers, which
    /// is what this overload is for). Use the factory overload when a restart
    /// must produce fresh state.
    public Registry On(string eventType, params IHandler[] handlers)
    {
        foreach (var h in handlers)
            _specs.Add((eventType, new HandlerSpec(h.Name, h.OnFailure, () => h)));
        return this;
    }

    /// Instance registration with a per-handler budget window (ADR-0010 d9):
    /// unbounded by design — larger OR smaller than the dispatcher default.
    public Registry On(string eventType, IHandler handler, TimeSpan budget)
    {
        _specs.Add((eventType, new HandlerSpec(handler.Name, handler.OnFailure, () => handler,
            CheckBudget(budget, $"{eventType}/{handler.Name}"))));
        return this;
    }

    /// Spec registration: the factory runs once at Dispatcher construction and
    /// once per supervised restart, so a restart yields a genuinely fresh
    /// handler (fresh state, not just a fresh mailbox).
    public Registry On(string eventType, string name, Func<IHandler> factory, FailMode onFailure = FailMode.Open,
                       TimeSpan? budget = null)
    {
        _specs.Add((eventType, new HandlerSpec(name, onFailure, factory,
            budget is { } b ? CheckBudget(b, $"{eventType}/{name}") : null)));
        return this;
    }

    /// "Unbounded by design" still lives inside int-millisecond ask-window
    /// math (windowMs = budgetMs + graceMs in the F# ask layer): a budget
    /// past ~24.8 days would overflow NEGATIVE and fault every dispatch of
    /// that handler with a misleading trail (caught by this slice's
    /// adversarial verify). Reject at REGISTRATION — a config bug should
    /// surface at construction, loudly, not per-dispatch.
    internal static TimeSpan CheckBudget(TimeSpan budget, string owner)
    {
        const double maxMs = int.MaxValue - 3_600_000;   // an hour of grace headroom
        if (budget <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(budget),
                $"{owner}: budget must be positive (got {budget})");
        if (budget.TotalMilliseconds > maxMs)
            throw new ArgumentOutOfRangeException(nameof(budget),
                $"{owner}: budget exceeds the ask-window ceiling (~24.8 days; int-ms math)");
        return budget;
    }

    /// Everything the Dispatcher needs to spawn one worker per registration.
    internal IReadOnlyList<(string EventType, HandlerSpec Spec)> Specs => _specs;
}

public sealed record DispatchResult(Effect Merged, Trace Trace);

/// One handler's registration plus its live supervision state, as plain data —
/// the read API's view (ADR-0007 get-handlers). Generation is the supervised
/// worker's restart count; Dead is true once it has escalated past its window.
public sealed record HandlerStatus(
    string EventType, string Name, FailMode OnFailure, int Generation, bool Dead);

public sealed class Dispatcher
{
    private sealed record Outcome(string Name, Effect Effect);

    /// A registered handler, converged onto the actor layer: the spec's name,
    /// fail mode, and RESOLVED budget window (per-handler override or the
    /// dispatcher default, with its grace) ride along in C#, while the handler
    /// itself lives inside a supervised F# Worker actor and is only ever
    /// reached via ask.
    private sealed record Runner(string Name, FailMode OnFailure, TimeSpan Budget, TimeSpan Grace, bool BudgetOverridden,
                                 Worker<(HookEvent, HandlerContext), Effect> Worker);

    private readonly TimeSpan _budget;
    // Case-INSENSITIVE event lookup: a casing difference must never split the
    // event space — "userPromptSubmit" in a handlers.json entry registers a
    // worker that MUST fire when the canonical "UserPromptSubmit" dispatches
    // (the ADR-0006 silent-grant lesson's registration-side twin, caught live
    // by this slice's adversarial verify). DispatchPolicy matches events
    // case-insensitively for exactly the same reason.
    private readonly Dictionary<string, List<Runner>> _runners = new(StringComparer.OrdinalIgnoreCase);

    // The LONG-LIVED background queue (ADR-0004: built here, once). Background
    // effects outlive their responses in a daemon by design: DispatchAsync
    // enqueues and returns; this single consumer runs them in order. Collapsed
    // mode awaits CompleteBackgroundAsync before exit — same drain-before-exit
    // contract as before, relocated. _sidePending is the bookkeeping the
    // sigterm-drain and mandatory-idle-exit slices share ("a non-empty
    // background queue defers exit") — one counter, both readers.
    private sealed record SideWork(string Name, HookEvent E, string? DispatchId, Effect.Background Effect, Trace Trace);
    private readonly Channel<SideWork> _side = Channel.CreateUnbounded<SideWork>();
    private readonly Task _sideConsumer;
    private int _sidePending;

    /// Workers are spawned HERE, from a snapshot of the registry — register all
    /// handlers BEFORE constructing the Dispatcher; later Registry.On calls are
    /// not picked up. The optional supervisor is the test seam (inject one
    /// built on a fake clock to make restart-window math deterministic).
    /// `grace` (default: 10% of the handler's EFFECTIVE budget, clamped
    /// 100ms..1s) widens the ask window past the budget so a token-honoring
    /// handler's cancellation reply lands INSIDE it — that reply leaves the
    /// handler AT the budget and needs a beat to travel (ADR-0004 decision
    /// 5). Worst-case dispatch wall time is max over its handlers of
    /// (budget_i + grace_i), paid only when a handler never answers
    /// (ADR-0010 d9: per-handler windows).
    public Dispatcher(Registry registry, TimeSpan budget, Supervisor? supervisor = null, TimeSpan? grace = null)
    {
        _budget = Registry.CheckBudget(budget, "dispatcher default");
        // Grace scales with each handler's EFFECTIVE budget (ADR-0010 d9: a
        // 10-minute window deserves more reply-travel headroom than a 100ms
        // one — a fixed grace mis-sized against a small budget would turn an
        // honoring handler's cancellation reply into a counted wedge). An
        // explicit ctor grace (the classification-test seam) still wins for
        // every handler.
        var graceOverride = grace;
        _sideConsumer = Task.Run(ConsumeSideAsync);
        var sup = supervisor ?? new Supervisor("dispatcher", maxRestarts: 3, TimeSpan.FromSeconds(5));

        // ONE worker per (eventType, spec) registration. The worker is generic
        // — the F# side never sees HookEvent/Effect (dependency arrow: C# host
        // -> F# lib); the (event, ctx) tuple flows through it opaquely and only
        // the delegate below ever looks inside.
        var idCounts = new Dictionary<string, int>();
        foreach (var (eventType, spec) in registry.Specs)
        {
            var baseId = $"{eventType}/{spec.Name}";
            var n = idCounts.TryGetValue(baseId, out var c) ? c + 1 : 1;
            idCounts[baseId] = n;
            var id = n == 1 ? baseId : $"{baseId}#{n}";   // disambiguate name collisions

            var worker = Worker<(HookEvent, HandlerContext), Effect>.Supervised(sup, id, () =>
            {
                // Child spec in action: a restart re-runs THIS, so factory-
                // registered handlers come back with fresh state.
                var h = spec.Factory();
                return req => h.HandleAsync(req.Item1, req.Item2);
            });

            var effective = spec.Budget ?? _budget;
            if (!_runners.TryGetValue(eventType, out var list))
                _runners[eventType] = list = new List<Runner>();
            list.Add(new Runner(spec.Name, spec.OnFailure, effective,
                graceOverride ?? GraceFor(effective), spec.Budget is not null, worker));
        }
    }

    private static TimeSpan GraceFor(TimeSpan budget) =>
        TimeSpan.FromMilliseconds(Math.Clamp(budget.TotalMilliseconds * 0.10, 100, 1000));

    public async Task<DispatchResult> DispatchAsync(HookEvent e, string? dispatchId = null,
                                                    IReadOnlySet<string>? excludedHandlers = null)
    {
        var registered = _runners.TryGetValue(e.Type, out var list)
            ? (IReadOnlyList<Runner>)list : Array.Empty<Runner>();
        // Handler-level exclusion (ADR-0006 decision 2 / N3): drop named
        // handlers from THIS dispatch's view — "as if unregistered for this
        // dispatch". Order-preserving (.Where over the registration-ordered
        // list, which Merge depends on), and pre-fan-out, so an excluded
        // fail-closed handler never runs and therefore contributes no Deny.
        // The snapshot registry and each excluded handler's supervised Worker
        // are untouched — filtered, never restarted (a restart wipes state).
        // DEAD until the evaluator wires it in (phases 3+): the default null
        // path is today's behavior, byte-identical.
        var handlers = excludedHandlers is { Count: > 0 }
            ? registered.Where(r => !excludedHandlers.Contains(r.Name)).ToList()
            : registered;
        var trace = new Trace(e.Type, _budget, handlers.Count);

        // Structured trail alongside the human Trace: dispatch.start now, and a
        // span that stamps durMs onto dispatch.done when the whole run finishes.
        // budgetMs here is the dispatcher DEFAULT; a handler whose spec
        // overrides it carries its own budgetMs on its handler.* trail lines
        // (ADR-0010 d9 trail truthfulness).
        Log.Info("dispatcher", "dispatch.start", Fields(e, dispatchId,
            data: new Dictionary<string, object>
            {
                ["handlers"] = handlers.Count,
                ["budgetMs"] = _budget.TotalMilliseconds,
            }));
        using var span = Log.Span("dispatcher", "dispatch.done", Fields(e, dispatchId));

        // FAN-OUT: every handler runs concurrently on the work-stealing thread
        // pool, each under its OWN budget window (ADR-0010 d9 — the dispatch
        // completes when every handler has answered or hit its own window;
        // WhenAll gives that for free). This is the .NET analogue of BEAM's
        // Task.async_stream or Node's Promise.allSettled — but backed by real,
        // parallel threads.
        var outcomes = await Task.WhenAll(
            handlers.Select(h => RunGuarded(h, e, dispatchId, trace)));

        // Route fire-and-forget effects onto the LONG-LIVED side channel; keep
        // loop-effects to merge. The response never waits on background work —
        // the daemon's consumer runs it after this method returns; collapsed
        // mode drains via CompleteBackgroundAsync before exit.
        var loop = new List<Effect>();
        foreach (var o in outcomes)
        {
            if (o.Effect is Effect.Background bg)
            {
                Interlocked.Increment(ref _sidePending);
                if (!_side.Writer.TryWrite(new SideWork(o.Name, e, dispatchId, bg, trace)))
                {
                    // Unreachable while the drain's active==0 guard holds (no
                    // dispatch can be mid-flight when the writer completes) —
                    // but if that invariant ever slips, a silently dropped
                    // background effect is the nightmare: be LOUD instead.
                    Interlocked.Decrement(ref _sidePending);
                    Log.Error("dispatcher", "side.dropped", Fields(e, dispatchId,
                        msg: "background effect enqueued after queue completion — dropped",
                        data: Handler(o.Name)));
                }
            }
            else loop.Add(o.Effect);
        }

        var merged = Merge(loop);
        span.Complete(Fields(e, dispatchId,
            data: new Dictionary<string, object>
            {
                ["handlers"] = handlers.Count,
                ["effect"] = merged.GetType().Name,
            }));
        return new DispatchResult(merged, trace);
    }

    /// Background effects not yet completed — queued or running. The idle and
    /// drain machinery reads this: a non-empty queue defers exit.
    public int BackgroundPending => Volatile.Read(ref _sidePending);

    /// A plain-data view of every registered handler for the read API (ADR-0007
    /// get-handlers): event, name, fail mode, and the supervised worker's live
    /// generation + dead flag. Read-only projection off the SAME runners the
    /// dispatch path uses — no parallel registry to drift. Registration order is
    /// preserved within each event (the order Merge depends on); the rich F# DU
    /// stays inside the actor lib — only the int/bool accessors cross.
    public IReadOnlyList<HandlerStatus> Snapshot() =>
        _runners.SelectMany(kv => kv.Value.Select(r =>
            new HandlerStatus(kv.Key, r.Name, r.OnFailure, r.Worker.Generation, r.Worker.IsDead)))
            .ToList();

    /// Single-shot mode: no more dispatches will come — complete the side
    /// queue and wait for the consumer to finish everything already enqueued.
    /// The dispatcher is done after this; daemon mode calls it only at drain.
    public async Task CompleteBackgroundAsync()
    {
        _side.Writer.Complete();
        await _sideConsumer;
    }

    private async Task ConsumeSideAsync()
    {
        await foreach (var w in _side.Reader.ReadAllAsync())
        {
            var sw = Stopwatch.StartNew();
            try
            {
                await w.Effect.Run(CancellationToken.None);
                w.Trace.Side(w.Name, sw.Elapsed, true);
                Log.Info("dispatcher", "side.ok", Fields(w.E, w.DispatchId, sw.Elapsed.TotalMilliseconds,
                    data: Handler(w.Name)));
            }
            catch (Exception ex)
            {
                w.Trace.Side(w.Name, sw.Elapsed, false, ex.Message);
                Log.Error("dispatcher", "side.error", Fields(w.E, w.DispatchId, sw.Elapsed.TotalMilliseconds,
                    msg: ex.Message, data: Handler(w.Name)));
            }
            finally
            {
                Interlocked.Decrement(ref _sidePending);
            }
        }
    }

    private async Task<Outcome> RunGuarded(Runner r, HookEvent e, string? dispatchId, Trace trace)
    {
        var sw = Stopwatch.StartNew();
        // Per-handler window (ADR-0010 d9): each runner gets its own CTS and
        // deadline sized to ITS budget — an override may exceed the dispatcher
        // default (a min-clamp under one shared token could only ever shorten).
        using var budgetCts = new CancellationTokenSource(r.Budget);
        var ctx = new HandlerContext(DateTimeOffset.UtcNow + r.Budget, budgetCts.Token, dispatchId);
        try
        {
            // Dispatch IS a CLASSIFIED ask (ADR-0004 decision 5): the budget
            // token cancels the handler at budget; the ask itself waits budget
            // + grace so an honoring handler's cancellation reply lands inside
            // the window and "no reply" is unambiguous. Classification of what
            // no-reply MEANS (wedge vs backlog) happens in the ask layer, which
            // reports wedges to the supervisor — the supervisor owns all
            // counting; this method only converts outcomes to effects and logs.
            var res = await r.Worker.AskClassifiedAsync(
                (e, ctx), (int)r.Budget.TotalMilliseconds, (int)r.Grace.TotalMilliseconds, dispatchId ?? "");
            switch (res.Status)
            {
                case AskStatus.Ok:
                    trace.Handler(r.Name, sw.Elapsed, "ok");
                    Log.Info("dispatcher", "handler.ok", Fields(e, dispatchId, sw.Elapsed.TotalMilliseconds,
                        data: HandlerData(r)));
                    return new Outcome(r.Name, res.Reply);

                case AskStatus.Faulted when res.Error is OperationCanceledException:
                    // The handler HONORED its budget token: a timeout, not a
                    // fault. (The supervisor sees the same OCE via
                    // reply-then-crash and restarts the worker WITHOUT counting
                    // it — carry-in c.) Chronic slowness stays visible here.
                    return Timeout(r, e, dispatchId, sw, trace, "cancelled");

                case AskStatus.Wedged:
                    // Received but never answered: the ask layer has reported
                    // it; the supervisor abandons the worker and counts it
                    // (carry-in a). The stuck task is leaked, deliberately.
                    return Timeout(r, e, dispatchId, sw, trace, "wedged");

                case AskStatus.Backlogged:
                    // Never received — queued behind a busy sibling dispatch.
                    // Backlog, not a defect: nothing counted against the worker
                    // (sustained backlog is the router evidence ADR-0004 d6
                    // waits for).
                    return Timeout(r, e, dispatchId, sw, trace, "backlogged");

                case AskStatus.Dead:
                    // Escalated worker: fail fast in ~0ms instead of burning
                    // the budget per dispatch (carry-in b).
                    trace.Handler(r.Name, sw.Elapsed, $"DEAD -> fail-{Mode(r)}");
                    Log.Warn("dispatcher", "handler.dead", Fields(e, dispatchId, sw.Elapsed.TotalMilliseconds,
                        data: HandlerData(r, Mode(r))));
                    return new Outcome(r.Name, Fail(r));

                default:   // AskStatus.Faulted, non-cancellation: the handler crashed
                    trace.Handler(r.Name, sw.Elapsed, $"ERROR -> fail-{Mode(r)} ({res.Error.Message})");
                    Log.Error("dispatcher", "handler.error", Fields(e, dispatchId, sw.Elapsed.TotalMilliseconds,
                        msg: res.Error.Message, data: HandlerData(r, Mode(r))));
                    return new Outcome(r.Name, Fail(r));
            }
        }
        catch (Exception ex)
        {
            // Belt-and-braces: the classified ask routes every expected outcome
            // into a status; anything landing here is infrastructure failure.
            trace.Handler(r.Name, sw.Elapsed, $"ERROR -> fail-{Mode(r)} ({ex.Message})");
            Log.Error("dispatcher", "handler.error", Fields(e, dispatchId, sw.Elapsed.TotalMilliseconds,
                msg: ex.Message, data: HandlerData(r, Mode(r))));
            return new Outcome(r.Name, Fail(r));
        }
    }

    private Outcome Timeout(Runner r, HookEvent e, string? dispatchId, Stopwatch sw, Trace trace, string classification)
    {
        trace.Handler(r.Name, sw.Elapsed, $"TIMEOUT({classification}) -> fail-{Mode(r)}");
        var data = HandlerData(r, Mode(r));
        data["classification"] = classification;
        Log.Warn("dispatcher", "handler.timeout", Fields(e, dispatchId, sw.Elapsed.TotalMilliseconds, data: data));
        return new Outcome(r.Name, Fail(r));
    }

    // Correlation bundle every dispatcher event carries: who asked (sessionId),
    // which lifecycle event, and the per-invocation dispatchId from Program.cs.
    private static LogFields Fields(HookEvent e, string? dispatchId, double? durMs = null,
                                    string? msg = null, IDictionary<string, object>? data = null) =>
        new()
        {
            DispatchId = dispatchId,
            SessionId = e.SessionId,
            HookEvent = e.Type,
            DurMs = durMs,
            Msg = msg,
            Data = data,
        };

    /// Handler(...) plus ADR-0010 d9 trail truthfulness: a spec-overridden
    /// budget rides on that handler's own trail lines (dispatch.start's
    /// budgetMs stays the dispatcher default).
    private static Dictionary<string, object> HandlerData(Runner r, string? failMode = null)
    {
        var d = Handler(r.Name, failMode);
        if (r.BudgetOverridden) d["budgetMs"] = r.Budget.TotalMilliseconds;
        return d;
    }

    private static Dictionary<string, object> Handler(string name, string? failMode = null)
    {
        var d = new Dictionary<string, object> { ["handler"] = name };
        if (failMode is not null) d["failMode"] = $"fail-{failMode}";
        return d;
    }

    private static string Mode(Runner r) => r.OnFailure == FailMode.Closed ? "closed" : "open";

    private static Effect Fail(Runner r) => r.OnFailure == FailMode.Closed
        ? new Effect.Decide(Verdict.Deny, $"{r.Name} failed under fail-closed policy")
        : new Effect.Noop();

    // Deterministic merge: deny wins, then ask, then replace-output, then
    // injects concatenate in registration order.
    private static Effect Merge(IReadOnlyList<Effect> effects)
    {
        if (effects.OfType<Effect.Decide>().FirstOrDefault(d => d.Verdict == Verdict.Deny) is { } deny) return deny;
        if (effects.OfType<Effect.Decide>().FirstOrDefault(d => d.Verdict == Verdict.Ask) is { } ask) return ask;
        if (effects.OfType<Effect.Replace>().LastOrDefault() is { } rep) return rep;
        var injects = effects.OfType<Effect.Inject>().Select(i => i.Text).ToList();
        return injects.Count > 0 ? new Effect.Inject(string.Join("\n", injects)) : new Effect.Noop();
    }
}

public sealed class Trace(string ev, TimeSpan budget, int handlerCount)
{
    // Locked: the long-lived background consumer appends Side lines after the
    // response is built in daemon mode, so writes can race a Render.
    private readonly List<string> _lines = new();

    public void Handler(string name, TimeSpan took, string status)
    {
        lock (_lines) _lines.Add($"  handler {name,-14} {took.TotalMilliseconds,7:F1}ms  {status}");
    }

    public void Side(string name, TimeSpan took, bool ok, string? err = null)
    {
        lock (_lines) _lines.Add($"  side    {name,-14} {took.TotalMilliseconds,7:F1}ms  {(ok ? "ok" : "ERR: " + err)}");
    }

    public string Render()
    {
        lock (_lines)
            return $"[captAInHook] {ev}  ({handlerCount} handler(s), budget {budget.TotalMilliseconds:F0}ms)\n" +
                   (_lines.Count > 0 ? string.Join("\n", _lines) : "  (no handlers registered)");
    }
}
