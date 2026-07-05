using System.Diagnostics;
using CaptainHook.Actors;
using CaptainHook.Core;
using static CaptainHook.Tests.TestUtil;

namespace CaptainHook.Tests;

// timeout-fault-classification (ADR-0004 decision 5): timeout is not fault.
// These tests pin the three carry-in verdicts from ADR-0002 —
//   (c) honored cancellation restarts but never counts toward escalation,
//   (a) a wedge (received, never answered) is abandoned-and-respawned AND counts,
//   (b) asks against an escalated (dead) worker fail fast instead of burning
//       the budget —
// plus the backlog case: an ask that was never received doesn't penalize the
// worker. All timing uses explicit budgets/graces and TCS gates or frozen
// clocks; polling is bounded (PollUntilAsync), never a fixed sleep.

public class TimeoutClassificationTests
{
    [Fact]
    public async Task HonoredCancellation_RestartsWorker_ButNeverEscalates()
    {
        // maxRestarts: 0 on a frozen clock — ANY counted fault escalates
        // immediately. Two honored-cancellation timeouts in a row must
        // therefore leave the worker alive iff cancellation is uncounted.
        var clock = new FakeClock();
        var sup = new Supervisor("class-cancel", maxRestarts: 0, TimeSpan.FromSeconds(5), clock.Now);
        var escalated = false;
        sup.OnEscalated = (_, _) => escalated = true;

        var hang = new[] { true };
        var built = 0;
        var reg = new Registry().On("UserPromptSubmit", "slow", () =>
        {
            Interlocked.Increment(ref built);
            return new TestHandler("slow", async (_, ctx) =>
            {
                if (Volatile.Read(ref hang[0]))
                    await Task.Delay(TimeSpan.FromSeconds(30), ctx.Ct);   // honors the token
                return new Effect.Inject("slow finally answered");
            });
        });
        // Generous grace so the cancellation reply ALWAYS lands inside the ask
        // window, however loaded the machine — the classification must be
        // "cancelled", never a flaky "wedged".
        var dispatcher = new Dispatcher(reg, TimeSpan.FromMilliseconds(200), sup, grace: TimeSpan.FromSeconds(5));

        var r1 = await dispatcher.DispatchAsync(Ev());
        Assert.IsType<Effect.Noop>(r1.Merged);                       // fail-open degrade
        Assert.Contains("TIMEOUT(cancelled)", r1.Trace.Render());    // classified, not generic

        var r2 = await dispatcher.DispatchAsync(Ev());
        Assert.Contains("TIMEOUT(cancelled)", r2.Trace.Render());

        Assert.False(escalated, "honored cancellation must not count toward the restart window");

        // The worker restarted each time (its mailbox died via reply-then-crash)
        // — factory re-ran — and is alive to answer once the handler behaves.
        Volatile.Write(ref hang[0], false);
        Effect.Inject? inject = null;
        await PollUntilAsync(async () =>
        {
            var r = await dispatcher.DispatchAsync(Ev());
            if (r.Merged is Effect.Inject i) { inject = i; return true; }
            return false;
        }, TimeSpan.FromSeconds(10), "worker alive after uncounted cancellation restarts");
        Assert.Equal("slow finally answered", inject!.Text);
        Assert.True(Volatile.Read(ref built) >= 3, $"factory should re-run per restart (built {built})");
    }

    [Fact]
    public async Task Wedge_IsAbandonedAndRespawned_FreshWorkerAnswers()
    {
        // A token-IGNORING handler: the budget fires, nothing replies. The ask
        // layer must classify the silence as a wedge (the message WAS
        // received), the supervisor abandons the instance and re-runs the
        // factory, and the next dispatch reaches a fresh worker.
        var clock = new FakeClock();
        var sup = new Supervisor("class-wedge", maxRestarts: 3, TimeSpan.FromSeconds(5), clock.Now);

        var wedge = new[] { true };
        var built = 0;
        var reg = new Registry().On("UserPromptSubmit", "wedger", () =>
        {
            Interlocked.Increment(ref built);
            return new TestHandler("wedger", async (_, _) =>
            {
                if (Volatile.Read(ref wedge[0]))
                    await Task.Delay(TimeSpan.FromSeconds(30));   // IGNORES ctx.Ct — the defect
                return new Effect.Inject("fresh worker answered");
            });
        });
        var dispatcher = new Dispatcher(reg, TimeSpan.FromMilliseconds(150), sup, grace: TimeSpan.FromMilliseconds(150));

        var r1 = await dispatcher.DispatchAsync(Ev());
        Assert.IsType<Effect.Noop>(r1.Merged);
        Assert.Contains("TIMEOUT(wedged)", r1.Trace.Render());

        // Abandon-and-respawn: the stuck task is leaked, the factory re-runs,
        // and a well-behaved dispatch reaches the REPLACEMENT instance.
        Volatile.Write(ref wedge[0], false);
        Effect.Inject? inject = null;
        await PollUntilAsync(async () =>
        {
            var r = await dispatcher.DispatchAsync(Ev());
            if (r.Merged is Effect.Inject i) { inject = i; return true; }
            return false;
        }, TimeSpan.FromSeconds(10), "respawned worker answers after abandon");
        Assert.Equal("fresh worker answered", inject!.Text);
        Assert.True(Volatile.Read(ref built) >= 2, $"abandon must re-run the factory (built {built})");
    }

    [Fact]
    public async Task ChronicWedger_Escalates_ThenAsksFailFast()
    {
        // Wedges COUNT (each one leaks a stuck task — carry-in a), so a
        // chronic wedger escalates; and once dead, asks return in ~0ms instead
        // of burning budget+grace per dispatch (carry-in b).
        var clock = new FakeClock();   // frozen: every wedge lands inside the window
        var sup = new Supervisor("class-chronic", maxRestarts: 0, TimeSpan.FromSeconds(5), clock.Now);
        var escalated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sup.OnEscalated = (_, _) => escalated.TrySetResult();

        var reg = new Registry().On("UserPromptSubmit", "wedger",
            () => new TestHandler("wedger", async (_, _) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30));   // always wedges
                return new Effect.Noop();
            }));
        var budget = TimeSpan.FromMilliseconds(300);
        var dispatcher = new Dispatcher(reg, budget, sup, grace: TimeSpan.FromMilliseconds(200));

        // One wedge is enough: maxRestarts=0 means the first COUNTED fault escalates.
        var r1 = await dispatcher.DispatchAsync(Ev());
        Assert.Contains("TIMEOUT(wedged)", r1.Trace.Render());
        await escalated.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Dead worker: the ask must fail fast — well under the budget it used
        // to burn (300ms + 200ms grace before this change).
        var sw = Stopwatch.StartNew();
        var r2 = await dispatcher.DispatchAsync(Ev());
        sw.Stop();
        Assert.IsType<Effect.Noop>(r2.Merged);
        Assert.Contains("DEAD", r2.Trace.Render());
        Assert.True(sw.Elapsed < budget,
            $"dead-worker ask took {sw.Elapsed.TotalMilliseconds:F0}ms — must fail fast, not burn the {budget.TotalMilliseconds:F0}ms budget");
    }

    [Fact]
    public async Task Backlog_NeverReceivedAsk_DoesNotCountAgainstTheWorker()
    {
        // Dispatch 1 parks INSIDE the handler (receipt marked) and honors its
        // token; dispatch 2 queues BEHIND it (receipt never marked). Verdicts:
        // d1 = cancelled (uncounted), d2 = backlogged (uncounted). With
        // maxRestarts=0, ANY counted fault would escalate — so "no escalation"
        // proves both classifications stayed uncounted.
        var clock = new FakeClock();
        var sup = new Supervisor("class-backlog", maxRestarts: 0, TimeSpan.FromSeconds(5), clock.Now);
        var escalated = false;
        sup.OnEscalated = (_, _) => escalated = true;

        var park = new[] { true };
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reg = new Registry().On("UserPromptSubmit", "busy", () =>
            new TestHandler("busy", async (_, ctx) =>
            {
                if (Volatile.Read(ref park[0]))
                {
                    firstEntered.TrySetResult();
                    await Task.Delay(TimeSpan.FromSeconds(30), ctx.Ct);   // honors the token
                }
                return new Effect.Inject("busy answered");
            }));
        var dispatcher = new Dispatcher(reg, TimeSpan.FromMilliseconds(300), sup, grace: TimeSpan.FromMilliseconds(500));

        var d1 = dispatcher.DispatchAsync(Ev());
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));   // d1 is inside the handler
        var d2 = dispatcher.DispatchAsync(Ev());                       // d2 queues behind it

        var r1 = await d1;
        var r2 = await d2;
        Assert.Contains("TIMEOUT(cancelled)", r1.Trace.Render());
        Assert.Contains("TIMEOUT(backlogged)", r2.Trace.Render());
        Assert.False(escalated, "neither honored cancellation nor backlog may count toward escalation");

        // The worker is alive (restarted uncounted after d1's crash) and serves.
        Volatile.Write(ref park[0], false);
        Effect.Inject? inject = null;
        await PollUntilAsync(async () =>
        {
            var r = await dispatcher.DispatchAsync(Ev());
            if (r.Merged is Effect.Inject i) { inject = i; return true; }
            return false;
        }, TimeSpan.FromSeconds(10), "worker alive after cancelled + backlogged dispatches");
        Assert.Equal("busy answered", inject!.Text);
    }

    [Fact]
    public async Task CrashingHandler_StillCountsAndEscalates_ClassificationDoesNotSoftenFaults()
    {
        // The flip side: real crashes must still count exactly as before —
        // classification narrows what counts, it must not stop counting faults.
        var clock = new FakeClock();
        var sup = new Supervisor("class-crash", maxRestarts: 1, TimeSpan.FromSeconds(5), clock.Now);
        var escalated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sup.OnEscalated = (_, _) => escalated.TrySetResult();

        var reg = new Registry().On("UserPromptSubmit", TestHandler.Throwing("flaky"));
        var dispatcher = new Dispatcher(reg, TimeSpan.FromSeconds(1), sup);

        for (var i = 0; i < 20 && !escalated.Task.IsCompleted; i++)
            await dispatcher.DispatchAsync(Ev());
        await escalated.Task.WaitAsync(TimeSpan.FromSeconds(10));   // 2 crashes > maxRestarts=1
    }
}
