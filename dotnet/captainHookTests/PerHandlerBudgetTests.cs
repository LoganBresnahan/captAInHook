using CaptainHook.Core;
using static CaptainHook.Tests.TestUtil;

namespace CaptainHook.Tests;

// per-handler-budget-windows (ADR-0010 decision 9 / d6): each handler's ask
// window is ITS OWN budget (+ grace) — an override may EXCEED the dispatcher
// default, which a min-clamp under one shared token could never express. The
// dispatch completes when every handler has answered or hit its own window.

public class PerHandlerBudgetTests
{
    [Fact]
    public async Task OverrideExtends_HandlerOutlivesTheDispatcherDefault()
    {
        // Default 200ms; the handler needs ~600ms and declares 2s. Under the
        // old shared-token design its token fired at 200ms; here it answers.
        var reg = new Registry().On("UserPromptSubmit",
            new TestHandler("slow-but-allowed", async (_, ctx) =>
            {
                await Task.Delay(600, ctx.Ct);
                return new Effect.Inject("took my time");
            }),
            budget: TimeSpan.FromSeconds(2));
        var dispatcher = new Dispatcher(reg, budget: TimeSpan.FromMilliseconds(200));

        var r = await dispatcher.DispatchAsync(Ev());

        var inj = Assert.IsType<Effect.Inject>(r.Merged);
        Assert.Equal("took my time", inj.Text);
    }

    [Fact]
    public async Task OverrideTightens_HonoredCancelInsideAGenerousDefault()
    {
        // Default 10s; the handler declares 150ms and honors its token. The
        // dispatch must resolve on the HANDLER's window (fail-open Noop),
        // never burning the default.
        var reg = new Registry().On("UserPromptSubmit",
            new TestHandler("tight", async (_, ctx) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ctx.Ct);   // never completes
                return new Effect.Inject("unreachable");
            }),
            budget: TimeSpan.FromMilliseconds(150));
        var dispatcher = new Dispatcher(reg, budget: TimeSpan.FromSeconds(10));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = await dispatcher.DispatchAsync(Ev());
        sw.Stop();

        Assert.IsType<Effect.Noop>(r.Merged);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5),
            $"dispatch resolved in {sw.Elapsed.TotalMilliseconds:F0}ms — must ride the 150ms handler window, not the 10s default");
        Assert.Contains("TIMEOUT(cancelled)", r.Trace.Render());
    }

    [Fact]
    public async Task CtxDeadline_ReflectsTheHandlersOwnBudget()
    {
        TimeSpan observed = TimeSpan.Zero;
        var reg = new Registry().On("UserPromptSubmit",
            new TestHandler("deadline-probe", (_, ctx) =>
            {
                observed = ctx.Deadline - DateTimeOffset.UtcNow;
                return Task.FromResult<Effect>(new Effect.Noop());
            }),
            budget: TimeSpan.FromSeconds(30));
        var dispatcher = new Dispatcher(reg, budget: TimeSpan.FromMilliseconds(200));

        await dispatcher.DispatchAsync(Ev());

        Assert.True(observed > TimeSpan.FromSeconds(20),
            $"ctx.Deadline gave {observed.TotalSeconds:F1}s — must reflect the 30s override, not the 200ms default");
    }

    [Fact]
    public async Task MixedFanOut_EachHandlerRidesItsOwnWindow_OrderPreserved()
    {
        // A fast default-budget handler and a slow extended one on the same
        // event: both effects merge, inject order = registration order.
        var reg = new Registry()
            .On("UserPromptSubmit", TestHandler.Returning("fast", new Effect.Inject("A")))
            .On("UserPromptSubmit",
                new TestHandler("slow", async (_, ctx) =>
                {
                    await Task.Delay(500, ctx.Ct);
                    return new Effect.Inject("B");
                }),
                budget: TimeSpan.FromSeconds(2));
        var dispatcher = new Dispatcher(reg, budget: TimeSpan.FromMilliseconds(250));

        var r = await dispatcher.DispatchAsync(Ev());

        Assert.Equal("A\nB", Assert.IsType<Effect.Inject>(r.Merged).Text);
    }

    [Fact]
    public async Task Trail_OverriddenBudgetOnHandlerLines_DefaultOnDispatchStart()
    {
        using var captured = new CapturedLog();
        var reg = new Registry()
            .On("UserPromptSubmit", TestHandler.Returning("plain", new Effect.Noop()))
            .On("UserPromptSubmit", TestHandler.Returning("boosted", new Effect.Noop()),
                budget: TimeSpan.FromSeconds(7));
        var dispatcher = new Dispatcher(reg, budget: TimeSpan.FromSeconds(2));

        await dispatcher.DispatchAsync(Ev(), "bud12345");

        var events = captured.Events.ToArray();
        var start = Assert.Single(events, e => e.Evt == "dispatch.start");
        Assert.Equal(2000d, start.Fields.Data!["budgetMs"]);   // the DEFAULT, honestly labeled

        var oks = events.Where(e => e.Evt == "handler.ok").ToList();
        var plain = Assert.Single(oks, e => (string)e.Fields.Data!["handler"] == "plain");
        var boosted = Assert.Single(oks, e => (string)e.Fields.Data!["handler"] == "boosted");
        Assert.False(plain.Fields.Data!.ContainsKey("budgetMs"));      // default: no noise
        Assert.Equal(7000d, boosted.Fields.Data!["budgetMs"]);         // override: named
    }

    [Fact]
    public async Task FactoryOverload_CarriesBudgetToo()
    {
        var reg = new Registry().On("UserPromptSubmit", "made-fresh",
            () => new TestHandler("made-fresh", async (_, ctx) =>
            {
                await Task.Delay(500, ctx.Ct);
                return new Effect.Inject("fresh and slow");
            }),
            budget: TimeSpan.FromSeconds(2));
        var dispatcher = new Dispatcher(reg, budget: TimeSpan.FromMilliseconds(200));

        var r = await dispatcher.DispatchAsync(Ev());
        Assert.Equal("fresh and slow", Assert.IsType<Effect.Inject>(r.Merged).Text);
    }
}
