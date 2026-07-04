using System.Diagnostics;
using CaptainHook.Actors;
using CaptainHook.Core;
using static CaptainHook.Tests.TestUtil;

namespace CaptainHook.Tests;

// These tests pin the DISPATCHER/ACTOR CONVERGENCE semantics: every registered
// handler now lives inside a supervised F# Worker actor, and a dispatch is an
// ask against that worker. The properties below did not exist before the
// convergence (self-healing state, escalation-as-degradation, reply-then-crash
// latency, per-worker serialization) — they are the point of the change.

/// A deliberately STATEFUL handler: `_calls` mutates BEFORE any crash, so a
/// crash always leaves the instance "corrupted" (its counter has advanced).
/// Whether a later dispatch sees that corruption is exactly what distinguishes
/// factory registration (fresh instance per restart) from instance
/// registration (same instance forever).
internal sealed class StatefulHandler(Func<bool> crashNext) : IHandler
{
    private int _calls;   // single worker => strictly sequential access

    public string Name => "stateful";
    public FailMode OnFailure => FailMode.Open;

    public Task<Effect> HandleAsync(HookEvent e, HandlerContext ctx)
    {
        var n = ++_calls;   // state mutates first — the "corruption"
        if (crashNext())
            throw new InvalidOperationException($"stateful corrupted at call {n}");
        return Task.FromResult<Effect>(new Effect.Inject($"calls={n}"));
    }
}

public class DispatcherSelfHealingTests
{
    // Modest per-dispatch budget: a probe that lands on a worker mid-restart
    // burns this as an ask timeout, so keep it small and the poll window large.
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RestartWait = TimeSpan.FromSeconds(10);

    /// Re-dispatch (bounded) until the merged effect is an Inject that carries
    /// the stateful handler's output. Dispatches that land while the worker is
    /// dead mid-restart merge without a "calls=" line (fail-open Noop) — those
    /// simply mean "restart not observable yet", so the poller retries.
    private static async Task<Effect.Inject> DispatchUntilStatefulInject(Dispatcher d, string what)
    {
        Effect.Inject? got = null;
        await PollUntilAsync(async () =>
        {
            var r = await d.DispatchAsync(Ev());
            if (r.Merged is Effect.Inject i && i.Text.Contains("calls="))
            {
                got = i;
                return true;
            }
            return false;
        }, RestartWait, what);
        return got!;
    }

    [Fact]
    public async Task FactoryRegistration_CrashRestart_YieldsFreshHandlerState()
    {
        // THE HEADLINE PROPERTY: a factory-registered handler that corrupts its
        // own state and crashes comes back CLEAN, because a supervisor restart
        // re-runs the registry factory (the OTP child-spec idea) — the old,
        // corrupted instance is simply dropped. Before the convergence a thrown
        // exception was converted to a fail-mode effect and the SAME poisoned
        // instance handled the next dispatch.
        var crash = new[] { true };
        var built = 0;
        var reg = new Registry()
            .On("UserPromptSubmit", "stateful",
                () => { Interlocked.Increment(ref built); return new StatefulHandler(() => Volatile.Read(ref crash[0])); })
            .On("UserPromptSubmit", TestHandler.Returning("steady", new Effect.Inject("steady says hi")));
        var dispatcher = new Dispatcher(reg, Budget);

        // Dispatch 1: the handler advances _calls to 1 (corruption) and throws.
        // Fail-open: its failure is its own — the sibling's inject survives.
        var r1 = await dispatcher.DispatchAsync(Ev());
        var survived = Assert.IsType<Effect.Inject>(r1.Merged);
        Assert.Equal("steady says hi", survived.Text);

        // Stop crashing, then wait (bounded, poll — never a fixed sleep) until
        // a dispatch through the SAME dispatcher reaches a live stateful worker.
        Volatile.Write(ref crash[0], false);
        var inject = await DispatchUntilStatefulInject(dispatcher, "restarted stateful worker answers");

        // Fresh state: the restarted instance starts at _calls=0, so its first
        // real answer is calls=1. Had the corrupted instance survived (the old
        // pre-convergence behavior), this would read calls=2 or higher.
        Assert.Equal("calls=1\nsteady says hi", inject.Text);
        Assert.True(Volatile.Read(ref built) >= 2,
            $"registry factory should have re-run on restart (built {built} time(s))");
    }

    [Fact]
    public async Task InstanceRegistration_CrashRestart_KeepsHandlerState()
    {
        // THE DOCUMENTED CONTRACT, pinned so it stays a conscious choice:
        // instance registration wraps the handler in a spec whose factory
        // returns THE SAME object, so a supervisor restart replaces the mailbox
        // but NOT the state. Only the factory overload buys the state reset.
        var crash = new[] { false };
        var handler = new StatefulHandler(() => Volatile.Read(ref crash[0]));
        var reg = new Registry().On("UserPromptSubmit", handler);
        var dispatcher = new Dispatcher(reg, Budget);

        // Baseline: first call on the shared instance.
        var r1 = await dispatcher.DispatchAsync(Ev());
        Assert.Equal("calls=1", Assert.IsType<Effect.Inject>(r1.Merged).Text);

        // Crash: _calls advances to 2, then the throw crashes the worker
        // (reply-then-crash). Fail-open with no sibling => merged Noop.
        Volatile.Write(ref crash[0], true);
        var r2 = await dispatcher.DispatchAsync(Ev());
        Assert.IsType<Effect.Noop>(r2.Merged);

        // After the restart the SAME instance answers — its counter continues
        // from the pre-crash value (2 calls so far + this one = 3). A fresh
        // instance would have answered calls=1.
        Volatile.Write(ref crash[0], false);
        Effect.Inject? inject = null;
        await PollUntilAsync(async () =>
        {
            var r = await dispatcher.DispatchAsync(Ev());
            if (r.Merged is Effect.Inject i) { inject = i; return true; }
            return false;
        }, TimeSpan.FromSeconds(10), "restarted instance-registered worker answers");
        Assert.Equal("calls=3", inject!.Text);
    }
}

public class DispatcherEscalationTests
{
    // Small budget on purpose: after escalation the dead worker burns the full
    // ask timeout on every dispatch, so the budget IS the degradation cost.
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(300);

    /// Dispatch (bounded iterations) until the supervisor escalates the crashy
    /// worker. Each dispatch either crashes the handler (one restart-window
    /// attempt — reply-then-crash makes it immediate) or lands mid-restart and
    /// times out after `Budget`; both are bounded, so the loop cannot hang.
    private static async Task CrashUntilEscalated(Dispatcher d, Task escalated)
    {
        for (var i = 0; i < 20 && !escalated.IsCompleted; i++)
            await d.DispatchAsync(Ev());
        await escalated.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Escalation_FailOpen_DeadWorkerDegradesToNoop_SiblingSurvives()
    {
        // Frozen fake clock: every crash "happens" at t=0, so both crashes are
        // always inside the 5s window and escalation fires after exactly
        // maxRestarts+1 = 2 crashes — no machine load can age attempts out.
        var clock = new FakeClock();
        var sup = new Supervisor("esc-open", maxRestarts: 1, TimeSpan.FromSeconds(5), clock.Now);
        var escalated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sup.OnEscalated = (_, _) => escalated.TrySetResult();

        var reg = new Registry().On("UserPromptSubmit",
            TestHandler.Throwing("flaky"),
            TestHandler.Returning("steady", new Effect.Inject("steady says hi")));
        var dispatcher = new Dispatcher(reg, Budget, sup);

        await CrashUntilEscalated(dispatcher, escalated.Task);

        // ESCALATION DEGRADES, NOT BREAKS: the flaky worker is now permanently
        // dead, so its ask burns the 300ms budget and surfaces TimeoutException
        // -> fail-open Noop. The dispatch as a whole still completes and the
        // sibling on the same event still contributes its effect.
        var result = await dispatcher.DispatchAsync(Ev());
        var inject = Assert.IsType<Effect.Inject>(result.Merged);
        Assert.Equal("steady says hi", inject.Text);
    }

    [Fact]
    public async Task Escalation_FailClosed_DeadWorkerDegradesToDeny()
    {
        var clock = new FakeClock();   // frozen — see the fail-open test above
        var sup = new Supervisor("esc-closed", maxRestarts: 1, TimeSpan.FromSeconds(5), clock.Now);
        var escalated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sup.OnEscalated = (_, _) => escalated.TrySetResult();

        var reg = new Registry().On("UserPromptSubmit",
            TestHandler.Throwing("guard", FailMode.Closed),
            TestHandler.Returning("steady", new Effect.Inject("hi")));
        var dispatcher = new Dispatcher(reg, Budget, sup);

        await CrashUntilEscalated(dispatcher, escalated.Task);

        // A fail-CLOSED handler that can no longer answer (dead worker, ask
        // timeout) must still deny: safety checks are not skippable by dying.
        // Deny then wins the merge over the sibling's inject.
        var result = await dispatcher.DispatchAsync(Ev());
        var decide = Assert.IsType<Effect.Decide>(result.Merged);
        Assert.Equal(Verdict.Deny, decide.Verdict);
        Assert.Contains("guard", decide.Reason);
    }
}

public class DispatcherAskSemanticsTests
{
    [Fact]
    public async Task ThrowingHandler_CompletesFast_ErrorReplyDoesNotBurnAskTimeout()
    {
        // REPLY-THEN-CRASH: the worker replies with the exception BEFORE it
        // crashes, so the dispatcher learns the outcome immediately instead of
        // waiting out the ask timeout on a corpse. With a 5s budget, a throwing
        // handler's dispatch must finish far sooner — the 2.5s bound is loose
        // enough for cold-start/CI noise while still proving the reply beat
        // the timeout path.
        var reg = new Registry().On("UserPromptSubmit", TestHandler.Throwing("flaky"));
        var dispatcher = new Dispatcher(reg, TimeSpan.FromSeconds(5));

        var sw = Stopwatch.StartNew();
        var result = await dispatcher.DispatchAsync(Ev());
        sw.Stop();

        Assert.IsType<Effect.Noop>(result.Merged);   // fail-open conversion, as before
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2.5),
            $"throwing dispatch took {sw.Elapsed.TotalMilliseconds:F0}ms — error reply should not wait out the 5s ask timeout");
    }

    [Fact]
    public async Task ConcurrentDispatches_SerializePerWorker_NoInterleavedExecution()
    {
        // One worker = one mailbox = one message at a time: the actor loop
        // awaits the handler to completion before receiving the next Work, so
        // two overlapping dispatches through the same dispatcher may never
        // interleave INSIDE a single handler. The first run parks on a gate
        // (guaranteeing the dispatches overlap in time); the recorded sequence
        // must still be strictly enter/exit-ordered.
        //
        // Determinism note: a correct implementation ALWAYS yields the strict
        // sequence, whatever the thread timing — the test can never flake. A
        // hypothetically broken (concurrent) worker would record enter:2 while
        // run 1 is parked on the gate whenever dispatch 2's message lands in
        // that window, which this shape makes as wide as possible without a
        // sleep-based negative wait.
        var events = new List<string>();
        var calls = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var handler = new TestHandler("serial", async (_, ctx) =>
        {
            var n = Interlocked.Increment(ref calls);
            lock (events) events.Add($"enter:{n}");
            if (n == 1)
            {
                firstEntered.TrySetResult();
                await gate.Task.WaitAsync(ctx.Ct);   // budget-bounded: cannot hang the suite
            }
            lock (events) events.Add($"exit:{n}");
            return new Effect.Inject($"run:{n}");
        });
        var reg = new Registry().On("UserPromptSubmit", handler);
        var dispatcher = new Dispatcher(reg, TimeSpan.FromSeconds(5));

        var d1 = dispatcher.DispatchAsync(Ev());
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));   // run 1 is now parked inside the worker
        var d2 = dispatcher.DispatchAsync(Ev());                       // overlapping dispatch, same worker

        gate.TrySetResult();
        var r1 = await d1;
        var r2 = await d2;

        Assert.Equal("run:1", Assert.IsType<Effect.Inject>(r1.Merged).Text);
        Assert.Equal("run:2", Assert.IsType<Effect.Inject>(r2.Merged).Text);
        lock (events)
            Assert.Equal(new[] { "enter:1", "exit:1", "enter:2", "exit:2" }, events);
    }
}
