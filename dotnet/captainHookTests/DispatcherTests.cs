using CaptainHook.Core;
using static CaptainHook.Tests.TestUtil;

namespace CaptainHook.Tests;

public class DispatcherMergeTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(2);

    private static Task<DispatchResult> Dispatch(params IHandler[] handlers)
    {
        var reg = new Registry().On("UserPromptSubmit", handlers);
        return new Dispatcher(reg, Budget).DispatchAsync(Ev());
    }

    [Fact]
    public async Task MultipleInjects_ConcatenateInRegistrationOrder()
    {
        var result = await Dispatch(
            TestHandler.Returning("a", new Effect.Inject("first")),
            TestHandler.Returning("b", new Effect.Inject("second")),
            TestHandler.Returning("c", new Effect.Inject("third")));

        // Semantic rule: injects are additive — every handler's text reaches the
        // context, newline-joined in the order handlers were registered.
        var inject = Assert.IsType<Effect.Inject>(result.Merged);
        Assert.Equal("first\nsecond\nthird", inject.Text);
    }

    [Fact]
    public async Task DecideDeny_WinsOverEverything()
    {
        var result = await Dispatch(
            TestHandler.Returning("inject", new Effect.Inject("hi")),
            TestHandler.Returning("replace", new Effect.Replace("swap")),
            TestHandler.Returning("ask", new Effect.Decide(Verdict.Ask, "sure?")),
            TestHandler.Returning("deny", new Effect.Decide(Verdict.Deny, "nope")));

        // Semantic rule: deny is the strongest verdict — one deny anywhere in the
        // fan-out blocks the action regardless of what other handlers wanted.
        var decide = Assert.IsType<Effect.Decide>(result.Merged);
        Assert.Equal(Verdict.Deny, decide.Verdict);
        Assert.Equal("nope", decide.Reason);
    }

    [Fact]
    public async Task DecideAsk_WinsOverReplaceAndInject()
    {
        var result = await Dispatch(
            TestHandler.Returning("inject", new Effect.Inject("hi")),
            TestHandler.Returning("ask", new Effect.Decide(Verdict.Ask, "confirm")),
            TestHandler.Returning("replace", new Effect.Replace("swap")));

        var decide = Assert.IsType<Effect.Decide>(result.Merged);
        Assert.Equal(Verdict.Ask, decide.Verdict);
    }

    [Fact]
    public async Task Replace_WinsOverInject_AndLastReplaceWins()
    {
        var result = await Dispatch(
            TestHandler.Returning("inject", new Effect.Inject("hi")),
            TestHandler.Returning("rep1", new Effect.Replace("one")),
            TestHandler.Returning("rep2", new Effect.Replace("two")));

        // Semantic rule: replace supersedes inject, and when several handlers
        // replace, the LAST registered one wins (Merge takes LastOrDefault).
        var rep = Assert.IsType<Effect.Replace>(result.Merged);
        Assert.Equal("two", rep.Text);
    }

    [Fact]
    public async Task NoEffects_MergesToNoop()
    {
        var result = await Dispatch(
            TestHandler.Returning("noop-a", new Effect.Noop()),
            TestHandler.Returning("noop-b", new Effect.Noop()));

        Assert.IsType<Effect.Noop>(result.Merged);
    }

    [Fact]
    public async Task UnregisteredEventType_MergesToNoop()
    {
        var reg = new Registry().On("SessionStart", TestHandler.Returning("x", new Effect.Inject("hi")));
        var result = await new Dispatcher(reg, Budget).DispatchAsync(Ev("SomethingNobodyRegistered"));

        Assert.IsType<Effect.Noop>(result.Merged);
    }
}

public class DispatcherFailureTests
{
    [Fact]
    public async Task Timeout_FailOpen_ContributesNoop_OtherEffectsSurvive()
    {
        var reg = new Registry().On("UserPromptSubmit",
            TestHandler.Hanging("slowpoke"),                       // will blow the budget
            TestHandler.Returning("fast", new Effect.Inject("fast says hi")));
        var dispatcher = new Dispatcher(reg, TimeSpan.FromMilliseconds(200));

        var result = await dispatcher.DispatchAsync(Ev());

        // Semantic rule: fail-open means "this handler's failure is its own" —
        // the dispatch succeeds and the fast handler's inject survives.
        var inject = Assert.IsType<Effect.Inject>(result.Merged);
        Assert.Equal("fast says hi", inject.Text);
    }

    [Fact]
    public async Task Timeout_FailClosed_YieldsMergedDeny()
    {
        var reg = new Registry().On("UserPromptSubmit",
            TestHandler.Hanging("guard", FailMode.Closed),
            TestHandler.Returning("fast", new Effect.Inject("hi")));
        var dispatcher = new Dispatcher(reg, TimeSpan.FromMilliseconds(200));

        var result = await dispatcher.DispatchAsync(Ev());

        // Semantic rule: a fail-closed handler that cannot answer denies by
        // default — safety-critical checks must not be skippable via timeout.
        var decide = Assert.IsType<Effect.Decide>(result.Merged);
        Assert.Equal(Verdict.Deny, decide.Verdict);
        Assert.Contains("guard", decide.Reason);
    }

    [Fact]
    public async Task Throw_FailClosed_YieldsMergedDeny()
    {
        var reg = new Registry().On("UserPromptSubmit",
            TestHandler.Throwing("guard", FailMode.Closed));
        var dispatcher = new Dispatcher(reg, TimeSpan.FromSeconds(2));

        var result = await dispatcher.DispatchAsync(Ev());

        var decide = Assert.IsType<Effect.Decide>(result.Merged);
        Assert.Equal(Verdict.Deny, decide.Verdict);
    }

    [Fact]
    public async Task Throw_FailOpen_DoesNotPoisonOtherHandlers()
    {
        var reg = new Registry().On("UserPromptSubmit",
            TestHandler.Throwing("flaky"),
            TestHandler.Returning("steady", new Effect.Inject("still here")));
        var dispatcher = new Dispatcher(reg, TimeSpan.FromSeconds(2));

        var result = await dispatcher.DispatchAsync(Ev());

        var inject = Assert.IsType<Effect.Inject>(result.Merged);
        Assert.Equal("still here", inject.Text);
    }

    [Fact]
    public async Task BackgroundEffect_Executes_AndContributesNoLoopEffect()
    {
        var ran = new TaskCompletionSource();
        var reg = new Registry().On("PostToolUse",
            new TestHandler("bg", (_, _) => Task.FromResult<Effect>(
                new Effect.Background(_ => { ran.TrySetResult(); return Task.CompletedTask; }))));
        var dispatcher = new Dispatcher(reg, TimeSpan.FromSeconds(2));

        var result = await dispatcher.DispatchAsync(Ev("PostToolUse"));

        // Since daemon-serve-loop, the side channel is LONG-LIVED: the
        // response never waits on background work (in a daemon it outlives the
        // response by design), so the dispatch may return before the effect
        // runs...
        Assert.IsType<Effect.Noop>(result.Merged);   // never leaks into the merge
        // ...and completing the queue (the single-shot drain-before-exit
        // contract, now explicit) guarantees it ran.
        await dispatcher.CompleteBackgroundAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(ran.Task.IsCompletedSuccessfully, "background effect did not run");
        Assert.Equal(0, dispatcher.BackgroundPending);
    }
}

/// ADR-0006 handler-level exclusion (decision 2 / N3) — smoke tests for the
/// per-dispatch excluded-names filter. The full N3 pin (excluded fail-closed
/// contributes nothing, survivors keep registration-order merge, the excluded
/// Worker is never restarted) is exclusion-ordering-failmode-pins, phase 2;
/// these just prove the mechanism drops the named handler and is dead by
/// default.
public class HandlerExclusionTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task ExcludedHandler_DoesNotRun_SurvivorsMergeInRegistrationOrder()
    {
        var reg = new Registry().On("UserPromptSubmit",
            TestHandler.Returning("a", new Effect.Inject("first")),
            TestHandler.Returning("skip", new Effect.Inject("SKIPPED")),
            TestHandler.Returning("b", new Effect.Inject("second")));

        var result = await new Dispatcher(reg, Budget)
            .DispatchAsync(Ev(), excludedHandlers: new HashSet<string> { "skip" });

        // "As if unregistered for this dispatch": the excluded handler's inject
        // is absent, and the survivors still concatenate in registration order.
        Assert.Equal("first\nsecond", Assert.IsType<Effect.Inject>(result.Merged).Text);
    }

    [Fact]
    public async Task ExcludedFailClosedHandler_ContributesNoDeny()
    {
        // The N3 crux, smoke-tested: a fail-closed handler that WOULD deny on
        // failure contributes nothing when excluded, because it is filtered
        // pre-fan-out and never runs. Fresh registry per dispatcher so the two
        // are fully independent.
        Registry Reg() => new Registry().On("UserPromptSubmit",
            TestHandler.Returning("ok", new Effect.Inject("hi")),
            TestHandler.Throwing("gate", FailMode.Closed));

        // Baseline proves the exclusion is load-bearing: the gate throws under
        // fail-closed, so the un-excluded dispatch denies.
        var denied = await new Dispatcher(Reg(), Budget).DispatchAsync(Ev());
        Assert.Equal(Verdict.Deny, Assert.IsType<Effect.Decide>(denied.Merged).Verdict);

        // Excluded, the gate is silent and the inject survives.
        var allowed = await new Dispatcher(Reg(), Budget)
            .DispatchAsync(Ev(), excludedHandlers: new HashSet<string> { "gate" });
        Assert.Equal("hi", Assert.IsType<Effect.Inject>(allowed.Merged).Text);
    }

    [Fact]
    public async Task NullOrEmptyExclusion_IsTodaysBehavior_ByteIdentical()
    {
        var reg = new Registry().On("UserPromptSubmit",
            TestHandler.Returning("a", new Effect.Inject("x")));
        var d = new Dispatcher(reg, Budget);

        // Both the default-null path and an empty set are the pre-slice
        // behavior: nothing filtered.
        Assert.Equal("x", Assert.IsType<Effect.Inject>((await d.DispatchAsync(Ev())).Merged).Text);
        Assert.Equal("x", Assert.IsType<Effect.Inject>(
            (await d.DispatchAsync(Ev(), excludedHandlers: new HashSet<string>())).Merged).Text);
    }
}

/// ADR-0006 N3 pins — the adversarial pass for handler-level exclusion's
/// interaction with registration order and fail modes (the ADR names this the
/// slice that pins N3). "As if unregistered for this dispatch": an excluded
/// handler contributes NOTHING — not its effect, not its fail-mode deny — and
/// the machinery around it (merge order, the supervised Worker's state) is left
/// undisturbed.
public class ExclusionSemanticsPins
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(2);
    private static IReadOnlySet<string> Exclude(params string[] names) => new HashSet<string>(names);

    [Fact]
    public async Task ExcludingMiddleHandler_SurvivorsKeepRegistrationOrder()
    {
        var reg = new Registry().On("UserPromptSubmit",
            TestHandler.Returning("a", new Effect.Inject("1")),
            TestHandler.Returning("mid", new Effect.Inject("MID")),
            TestHandler.Returning("b", new Effect.Inject("2")),
            TestHandler.Returning("c", new Effect.Inject("3")));

        var result = await new Dispatcher(reg, Budget).DispatchAsync(Ev(), excludedHandlers: Exclude("mid"));

        // The hole a middle exclusion leaves closes without reordering — Merge's
        // registration-order concatenation is undisturbed.
        Assert.Equal("1\n2\n3", Assert.IsType<Effect.Inject>(result.Merged).Text);
    }

    [Fact]
    public async Task ExcludedFailClosedGate_ContributesNoDeny_AmongSurvivors()
    {
        // Strengthened N3: a fail-closed gate sitting BETWEEN two survivors. Its
        // fail-mode deny would win the whole merge (deny beats everything) — but
        // excluded, it never runs, so the survivors' injects merge cleanly.
        Registry Reg() => new Registry().On("UserPromptSubmit",
            TestHandler.Returning("front", new Effect.Inject("front")),
            TestHandler.Throwing("gate", FailMode.Closed),
            TestHandler.Returning("back", new Effect.Inject("back")));

        var denied = await new Dispatcher(Reg(), Budget).DispatchAsync(Ev());
        Assert.Equal(Verdict.Deny, Assert.IsType<Effect.Decide>(denied.Merged).Verdict);

        var worked = await new Dispatcher(Reg(), Budget).DispatchAsync(Ev(), excludedHandlers: Exclude("gate"));
        Assert.Equal("front\nback", Assert.IsType<Effect.Inject>(worked.Merged).Text);
    }

    [Fact]
    public async Task ExcludedHandler_SupervisedWorker_IsNeverRestarted_StatePersists()
    {
        // The sharp N3 pin: exclusion FILTERS, it does not restart. A restart
        // re-runs the factory and wipes handler state (ConvergenceTests pins that
        // reset happens on a real crash-restart). Here the same stateful worker
        // must survive being skipped, its counter continuing — proof the
        // exclusion left the Worker untouched rather than tearing it down.
        var reg = new Registry()
            .On("UserPromptSubmit", "stateful", () => new StatefulHandler(() => false));
        var d = new Dispatcher(reg, Budget);

        // Run it: counter -> 1.
        Assert.Equal("calls=1", Assert.IsType<Effect.Inject>((await d.DispatchAsync(Ev())).Merged).Text);
        // Exclude it: never asked, so it does not advance — merged Noop (no
        // other handler registered for the event).
        Assert.IsType<Effect.Noop>((await d.DispatchAsync(Ev(), excludedHandlers: Exclude("stateful"))).Merged);
        // Run it again: the counter CONTINUES to 2. Had exclusion restarted the
        // worker, a fresh instance would answer calls=1.
        Assert.Equal("calls=2", Assert.IsType<Effect.Inject>((await d.DispatchAsync(Ev())).Merged).Text);
    }

    [Fact]
    public async Task ExcludingEveryHandler_IsAnEmptyFanOut_NotAnError()
    {
        var reg = new Registry().On("UserPromptSubmit",
            TestHandler.Returning("a", new Effect.Inject("x")),
            TestHandler.Returning("b", new Effect.Inject("y")));

        var result = await new Dispatcher(reg, Budget).DispatchAsync(Ev(), excludedHandlers: Exclude("a", "b"));

        // Identical shape to an event with no registered handlers: a clean Noop.
        Assert.IsType<Effect.Noop>(result.Merged);
    }

    [Fact]
    public async Task ExcludingUnregisteredName_IsHarmless()
    {
        var reg = new Registry().On("UserPromptSubmit",
            TestHandler.Returning("a", new Effect.Inject("x")));

        // A name absent from the fan-out simply never matches the filter — the
        // evaluator may pass handler-deny names for handlers this event doesn't
        // have, with no special-casing.
        var result = await new Dispatcher(reg, Budget).DispatchAsync(Ev(), excludedHandlers: Exclude("ghost"));
        Assert.Equal("x", Assert.IsType<Effect.Inject>(result.Merged).Text);
    }
}
