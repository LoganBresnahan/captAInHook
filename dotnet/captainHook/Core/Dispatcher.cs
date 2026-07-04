using System.Diagnostics;
using System.Threading.Channels;
using CaptainHook.Actors;

namespace CaptainHook.Core;

public sealed class Registry
{
    private readonly Dictionary<string, List<IHandler>> _byEvent = new();

    public Registry On(string eventType, params IHandler[] handlers)
    {
        if (!_byEvent.TryGetValue(eventType, out var list))
            _byEvent[eventType] = list = new List<IHandler>();
        list.AddRange(handlers);
        return this;
    }

    public IReadOnlyList<IHandler> For(string eventType) =>
        _byEvent.TryGetValue(eventType, out var list) ? list : Array.Empty<IHandler>();
}

public sealed record DispatchResult(Effect Merged, Trace Trace);

public sealed class Dispatcher(Registry registry, TimeSpan budget)
{
    private sealed record Outcome(string Name, Effect Effect);

    public async Task<DispatchResult> DispatchAsync(HookEvent e, string? dispatchId = null)
    {
        var handlers = registry.For(e.Type);
        var trace = new Trace(e.Type, budget, handlers.Count);

        // Structured trail alongside the human Trace: dispatch.start now, and a
        // span that stamps durMs onto dispatch.done when the whole run finishes.
        Log.Info("dispatcher", "dispatch.start", Fields(e, dispatchId,
            data: new Dictionary<string, object>
            {
                ["handlers"] = handlers.Count,
                ["budgetMs"] = budget.TotalMilliseconds,
            }));
        using var span = Log.Span("dispatcher", "dispatch.done", Fields(e, dispatchId));

        using var budgetCts = new CancellationTokenSource(budget);

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

    private async Task<Outcome> RunGuarded(IHandler h, HookEvent e, string? dispatchId, CancellationToken budgetCt, Trace trace)
    {
        var sw = Stopwatch.StartNew();
        var ctx = new HandlerContext(DateTimeOffset.UtcNow + budget, budgetCt);
        try
        {
            // WaitAsync enforces the budget even on handlers that ignore the token.
            var eff = await h.HandleAsync(e, ctx).WaitAsync(budgetCt);
            trace.Handler(h.Name, sw.Elapsed, "ok");
            Log.Info("dispatcher", "handler.ok", Fields(e, dispatchId, sw.Elapsed.TotalMilliseconds,
                data: Handler(h.Name)));
            return new Outcome(h.Name, eff);
        }
        catch (OperationCanceledException)
        {
            trace.Handler(h.Name, sw.Elapsed, $"TIMEOUT -> fail-{Mode(h)}");
            Log.Warn("dispatcher", "handler.timeout", Fields(e, dispatchId, sw.Elapsed.TotalMilliseconds,
                data: Handler(h.Name, failMode: Mode(h))));
            return new Outcome(h.Name, Fail(h));
        }
        catch (Exception ex)
        {
            trace.Handler(h.Name, sw.Elapsed, $"ERROR -> fail-{Mode(h)} ({ex.Message})");
            Log.Error("dispatcher", "handler.error", Fields(e, dispatchId, sw.Elapsed.TotalMilliseconds,
                msg: ex.Message, data: Handler(h.Name, failMode: Mode(h))));
            return new Outcome(h.Name, Fail(h));
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

    private static string Mode(IHandler h) => h.OnFailure == FailMode.Closed ? "closed" : "open";

    private static Effect Fail(IHandler h) => h.OnFailure == FailMode.Closed
        ? new Effect.Decide(Verdict.Deny, $"{h.Name} failed under fail-closed policy")
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
