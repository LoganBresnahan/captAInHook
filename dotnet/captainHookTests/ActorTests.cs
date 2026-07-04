using CaptainHook.Actors;
using static CaptainHook.Tests.TestUtil;

namespace CaptainHook.Tests;

// Every test here drives the F# actor layer through its C#-facing facade
// (Counter / Supervisor / ActorRef), so each one doubles as interop coverage.
public class CounterTests
{
    private static Supervisor NewSup() => new("test", maxRestarts: 3, TimeSpan.FromSeconds(5));

    [Fact]
    public async Task GetCount_StartsAtZero()
    {
        var counter = Counter.Supervised(NewSup(), "counter-zero");
        Assert.Equal(0, await counter.GetCountAsync());
    }

    [Fact]
    public async Task Increments_AccumulateFifo()
    {
        var counter = Counter.Supervised(NewSup(), "counter-fifo");
        counter.Increment(1);
        counter.Increment(2);
        counter.Increment(3);

        // The mailbox is FIFO and GetCount queues BEHIND the increments, so a
        // single ask observes all three without any polling.
        Assert.Equal(6, await counter.GetCountAsync());
    }
}

public class SupervisionTests
{
    // Generous but hard-bounded: each probe that lands on a dead mailbox costs
    // a full 2s ask-timeout, so a 5s window only fits ~2 probes; 10s keeps the
    // test deterministic on cold/loaded CI without ever letting it hang.
    private static readonly TimeSpan RestartWait = TimeSpan.FromSeconds(10);

    /// Crash the counter and wait (bounded) until the supervisor's factory
    /// restart is observable as count reset to 0. Requires count != 0 first,
    /// otherwise "restarted" and "never crashed" look identical.
    private static async Task CrashAndAwaitRestart(Counter counter, string what)
    {
        counter.Increment(1);
        await PollUntilAsync(async () => await CountOrMinusOne(counter) >= 1,
            RestartWait, $"{what}: pre-crash increment visible");
        counter.Boom();
        await PollUntilAsync(async () => await CountOrMinusOne(counter) == 0,
            RestartWait, $"{what}: count reset to 0 after restart");
    }

    [Fact]
    public async Task Boom_SupervisorRestarts_CountResetsToZero()
    {
        var sup = new Supervisor("restart-test", maxRestarts: 3, TimeSpan.FromSeconds(5));
        var counter = Counter.Supervised(sup, "counter-r1");

        counter.Increment(5);
        Assert.Equal(5, await counter.GetCountAsync());

        counter.Boom();

        // Restart = factory re-run = fresh state. Poll with a bounded timeout;
        // the crash->supervisor->restart hop is async but should be near-instant.
        await PollUntilAsync(async () => await CountOrMinusOne(counter) == 0,
            RestartWait, "count reset after restart");
    }

    [Fact]
    public async Task SameCounterReference_StillWorksAfterRestart()
    {
        var sup = new Supervisor("swap-test", maxRestarts: 3, TimeSpan.FromSeconds(5));
        var counter = Counter.Supervised(sup, "counter-swap");

        await CrashAndAwaitRestart(counter, "swap-test");

        // The SAME Counter (and its inner ActorRef) must route to the NEW
        // mailbox — this is the ActorRef.Swap contract that saves the C# host
        // from posting into a dead agent.
        counter.Increment(42);
        await PollUntilAsync(async () => await CountOrMinusOne(counter) == 42,
            RestartWait, "post-restart increment visible via the same reference");
    }

    [Fact]
    public async Task OneForOne_CrashingOneChild_DoesNotResetSibling()
    {
        var sup = new Supervisor("iso-test", maxRestarts: 3, TimeSpan.FromSeconds(5));
        var victim = Counter.Supervised(sup, "counter-victim");
        var bystander = Counter.Supervised(sup, "counter-bystander");

        victim.Increment(5);
        bystander.Increment(7);
        Assert.Equal(5, await victim.GetCountAsync());
        Assert.Equal(7, await bystander.GetCountAsync());

        victim.Boom();
        await PollUntilAsync(async () => await CountOrMinusOne(victim) == 0,
            RestartWait, "victim restarted");

        // one_for_one: only the crashed child is restarted; the sibling's state
        // is untouched by its neighbor's failure.
        Assert.Equal(7, await bystander.GetCountAsync());
    }

    [Fact]
    public async Task RestartIntensityExceeded_EscalatesForThatChild()
    {
        // Frozen fake clock: every crash "happens" at t=0, so no amount of
        // machine load can age attempts out of the window. (With wall-clock
        // time, slow dead-mailbox probes could stretch the three crashes past
        // the real 5s window and escalation would legitimately never fire.)
        var clock = new FakeClock();
        var sup = new Supervisor("intensity-test", maxRestarts: 2, TimeSpan.FromSeconds(5), clock.Now);
        var escalated = new TaskCompletionSource<(string Id, Exception Err)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        sup.OnEscalated = (id, err) => escalated.TrySetResult((id, err));

        var counter = Counter.Supervised(sup, "counter-crashy");

        // Crashes 1 and 2 fit inside maxRestarts=2 -> restart. Wait for each
        // restart before crashing again, otherwise the Boom races the mailbox swap.
        await CrashAndAwaitRestart(counter, "crash 1");
        await CrashAndAwaitRestart(counter, "crash 2");

        // Crash 3 blows the budget (attempts > maxRestarts) -> escalate, no restart.
        counter.Boom();
        var (id, err) = await escalated.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("counter-crashy", id);
        Assert.IsType<InvalidOperationException>(err);
    }

    [Fact]
    public async Task Ask_AfterEscalation_FaultsWithTimeout_InsteadOfHanging()
    {
        var clock = new FakeClock();   // frozen — see RestartIntensityExceeded
        var sup = new Supervisor("dead-ask-test", maxRestarts: 2, TimeSpan.FromSeconds(5), clock.Now);
        var escalated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sup.OnEscalated = (_, _) => escalated.TrySetResult();

        var counter = Counter.Supervised(sup, "counter-doomed");
        await CrashAndAwaitRestart(counter, "crash 1");
        await CrashAndAwaitRestart(counter, "crash 2");
        counter.Boom();
        await escalated.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // The child is permanently dead (no restart after escalation), so the
        // ask's built-in timeout must surface as a fault — never a hang. The
        // outer WaitAsync bounds the test even if that contract were broken.
        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => counter.GetCountAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(ex is TimeoutException || ex.InnerException is TimeoutException,
            $"expected a TimeoutException (possibly wrapped), got {ex}");
    }

    [Fact]
    public async Task SlidingWindow_PrunesAgedAttempts_NoFalseEscalation()
    {
        // The test wall-clock time made impossible: proving attempts AGE OUT.
        // maxRestarts=1 means a second attempt inside the window escalates —
        // so if pruning were broken, crash 2 below would escalate instead of
        // restarting. With the clock injected, "6 seconds pass" is one line,
        // not a real sleep (and no NTP step can fake or break it).
        var clock = new FakeClock();
        var sup = new Supervisor("window-test", maxRestarts: 1, TimeSpan.FromSeconds(5), clock.Now);
        var escalated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sup.OnEscalated = (_, _) => escalated.TrySetResult();

        var counter = Counter.Supervised(sup, "counter-windowed");

        // Crash 1 at t=0: only attempt in window -> restart.
        await CrashAndAwaitRestart(counter, "crash 1");

        // Past the window: crash 1 must be pruned, so crash 2 is again the only
        // in-window attempt -> restart, NOT escalation.
        clock.Advance(TimeSpan.FromSeconds(6));
        await CrashAndAwaitRestart(counter, "crash 2 after window expiry");
        Assert.False(escalated.Task.IsCompleted, "aged-out attempt must not count toward escalation");

        // Crash 3 with the clock frozen at t=6s: two in-window attempts -> escalate.
        counter.Boom();
        await escalated.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
