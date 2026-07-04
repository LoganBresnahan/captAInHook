using System.Diagnostics;
using System.Threading.Channels;
using CaptainHook.Actors;

namespace CaptainHook.Core;

/// A CHILD SPEC, not a live handler: everything the dispatcher needs to
/// (re)create a handler — its name, fail mode, and a factory. This is the OTP
/// child-spec idea ported to the registry: "restart" means "run the factory
/// again", so a restarted worker gets a genuinely fresh handler.
internal sealed record HandlerSpec(string Name, FailMode OnFailure, Func<IHandler> Factory);

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

    /// Spec registration: the factory runs once at Dispatcher construction and
    /// once per supervised restart, so a restart yields a genuinely fresh
    /// handler (fresh state, not just a fresh mailbox).
    public Registry On(string eventType, string name, Func<IHandler> factory, FailMode onFailure = FailMode.Open)
    {
        _specs.Add((eventType, new HandlerSpec(name, onFailure, factory)));
        return this;
    }

    /// Everything the Dispatcher needs to spawn one worker per registration.
    internal IReadOnlyList<(string EventType, HandlerSpec Spec)> Specs => _specs;
}

public sealed record DispatchResult(Effect Merged, Trace Trace);

public sealed class Dispatcher
{
    private sealed record Outcome(string Name, Effect Effect);

    /// A registered handler, converged onto the actor layer: the spec's name
    /// and fail mode ride along in C#, while the handler itself lives inside a
    /// supervised F# Worker actor and is only ever reached via ask.
    private sealed record Runner(string Name, FailMode OnFailure, Worker<(HookEvent, HandlerContext), Effect> Worker);

    private readonly TimeSpan _budget;
    private readonly Dictionary<string, List<Runner>> _runners = new();

    /// Workers are spawned HERE, from a snapshot of the registry — register all
    /// handlers BEFORE constructing the Dispatcher; later Registry.On calls are
    /// not picked up. The optional supervisor is the test seam (inject one
    /// built on a fake clock to make restart-window math deterministic).
    public Dispatcher(Registry registry, TimeSpan budget, Supervisor? supervisor = null)
    {
        _budget = budget;
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

            if (!_runners.TryGetValue(eventType, out var list))
                _runners[eventType] = list = new List<Runner>();
            list.Add(new Runner(spec.Name, spec.OnFailure, worker));
        }
    }

    public async Task<DispatchResult> DispatchAsync(HookEvent e, string? dispatchId = null)
    {
        var handlers = _runners.TryGetValue(e.Type, out var list)
            ? (IReadOnlyList<Runner>)list : Array.Empty<Runner>();
        var trace = new Trace(e.Type, _budget, handlers.Count);

        // Structured trail alongside the human Trace: dispatch.start now, and a
        // span that stamps durMs onto dispatch.done when the whole run finishes.
        Log.Info("dispatcher", "dispatch.start", Fields(e, dispatchId,
            data: new Dictionary<string, object>
            {
                ["handlers"] = handlers.Count,
                ["budgetMs"] = _budget.TotalMilliseconds,
            }));
        using var span = Log.Span("dispatcher", "dispatch.done", Fields(e, dispatchId));

        using var budgetCts = new CancellationTokenSource(_budget);

        // FAN-OUT: every handler runs concurrently on the work-stealing thread
        // pool. This is the .NET analogue of BEAM's Task.async_stream or Node's
        // Promise.allSettled — but backed by real, parallel threads.
        var outcomes = await Task.WhenAll(
            handlers.Select(h => RunGuarded(h, e, dispatchId, budgetCts.Token, trace)));

        // Route fire-and-forget effects onto a channel; keep loop-effects to
        // merge. System.Threading.Channels = a CSP-style queue with backpressure
        // — the primitive I'd use for the audit / memory-write pipeline in anger.
        var side = Channel.CreateUnbounded<Outcome>();
        var loop = new List<Effect>();
        foreach (var o in outcomes)
        {
            if (o.Effect is Effect.Background) await side.Writer.WriteAsync(o);
            else loop.Add(o.Effect);
        }
        side.Writer.Complete();

        // Drain background work before exit (single-shot mode). In daemon mode
        // this consumer would be long-lived and never block the response.
        await foreach (var o in side.Reader.ReadAllAsync())
        {
            var sw = Stopwatch.StartNew();
            try
            {
                await ((Effect.Background)o.Effect).Run(CancellationToken.None);
                trace.Side(o.Name, sw.Elapsed, true);
                Log.Info("dispatcher", "side.ok", Fields(e, dispatchId, sw.Elapsed.TotalMilliseconds,
                    data: Handler(o.Name)));
            }
            catch (Exception ex)
            {
                trace.Side(o.Name, sw.Elapsed, false, ex.Message);
                Log.Error("dispatcher", "side.error", Fields(e, dispatchId, sw.Elapsed.TotalMilliseconds,
                    msg: ex.Message, data: Handler(o.Name)));
            }
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

    private async Task<Outcome> RunGuarded(Runner r, HookEvent e, string? dispatchId, CancellationToken budgetCt, Trace trace)
    {
        var sw = Stopwatch.StartNew();
        var ctx = new HandlerContext(DateTimeOffset.UtcNow + _budget, budgetCt);
        try
        {
            // Dispatch IS an ask: the latency budget doubles as the ask timeout,
            // and WaitAsync on the dispatch-wide token stays as a backstop for
            // whichever fires first. A handler crash comes back as the original
            // exception (reply-then-crash inside the worker), so the catch
            // blocks below see exactly what direct invocation used to throw —
            // while the crash ALSO reaches the supervisor for restart/escalate.
            var eff = await r.Worker.AskAsync((e, ctx), (int)_budget.TotalMilliseconds).WaitAsync(budgetCt);
            trace.Handler(r.Name, sw.Elapsed, "ok");
            Log.Info("dispatcher", "handler.ok", Fields(e, dispatchId, sw.Elapsed.TotalMilliseconds,
                data: Handler(r.Name)));
            return new Outcome(r.Name, eff);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            // OperationCanceledException: the budget token fired (handler honored
            // ctx.Ct, or WaitAsync cut it loose). TimeoutException: the ask timed
            // out — a token-ignoring handler, or a worker already escalated to
            // dead. All of them are the same story: no answer within budget.
            trace.Handler(r.Name, sw.Elapsed, $"TIMEOUT -> fail-{Mode(r)}");
            Log.Warn("dispatcher", "handler.timeout", Fields(e, dispatchId, sw.Elapsed.TotalMilliseconds,
                data: Handler(r.Name, failMode: Mode(r))));
            return new Outcome(r.Name, Fail(r));
        }
        catch (Exception ex)
        {
            trace.Handler(r.Name, sw.Elapsed, $"ERROR -> fail-{Mode(r)} ({ex.Message})");
            Log.Error("dispatcher", "handler.error", Fields(e, dispatchId, sw.Elapsed.TotalMilliseconds,
                msg: ex.Message, data: Handler(r.Name, failMode: Mode(r))));
            return new Outcome(r.Name, Fail(r));
        }
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
    private readonly List<string> _lines = new();

    public void Handler(string name, TimeSpan took, string status) =>
        _lines.Add($"  handler {name,-14} {took.TotalMilliseconds,7:F1}ms  {status}");

    public void Side(string name, TimeSpan took, bool ok, string? err = null) =>
        _lines.Add($"  side    {name,-14} {took.TotalMilliseconds,7:F1}ms  {(ok ? "ok" : "ERR: " + err)}");

    public string Render() =>
        $"[captAInHook] {ev}  ({handlerCount} handler(s), budget {budget.TotalMilliseconds:F0}ms)\n" +
        (_lines.Count > 0 ? string.Join("\n", _lines) : "  (no handlers registered)");
}
