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
