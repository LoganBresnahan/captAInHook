using CaptainHook.Core;
using CaptainHook.Wire;
using static CaptainHook.Tests.TestUtil;

namespace CaptainHook.Tests;

// sigterm-drain (ADR-0004 decision 4): a dying daemon keeps its promises.
// In-flight dispatches get their responses; queued/running Background effects
// — which by design outlive their responses — complete before exit; a blown
// deadline exits anyway but VISIBLY (daemon.drainTimeout). All scenarios use
// TCS gates, never timing guesses; the drain trigger is the ct seam (one code
// path with real SIGTERM).

public class DrainTests
{
    private static string NoHarness => "/tmp/chk-none-" + Guid.NewGuid().ToString("N")[..8];

    private static async Task<Task<int>> StartDaemonAsync(TempRuntimeDir dir, Registry reg,
        CancellationToken ct, TimeSpan? deadline = null)
    {
        var daemon = Task.Run(() => DaemonHost.RunAsync(dir.Paths, NoHarness, ct, reg, deadline));
        await PollUntilAsync(async () =>
            await ShimClient.TryForwardAsync(dir.Paths.SocketPath,
                new HookRequest("warmup00", "session-start", "claude-code", "{}"u8.ToArray()))
                is ForwardOutcome.Answered,
            TimeSpan.FromSeconds(15), "daemon up");
        return daemon;
    }

    [Fact]
    public async Task InFlightDispatch_GetsItsResponse_EvenWhenDrainFiresMidHandler()
    {
        // An accepted request is a promise: SIGTERM lands while the handler is
        // parked inside the daemon — the response must still be relayed.
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reg = new Registry().On("UserPromptSubmit", new TestHandler("slow", async (_, ctx) =>
        {
            entered.TrySetResult();
            await gate.Task.WaitAsync(ctx.Ct);
            return new Effect.Inject("finished during drain");
        }));

        using var dir = new TempRuntimeDir();
        using var stop = new CancellationTokenSource();
        var daemon = await StartDaemonAsync(dir, reg, stop.Token);

        var inFlight = ShimClient.TryForwardAsync(dir.Paths.SocketPath,
            new HookRequest("inflight1", "user-prompt-submit", "claude-code", "{}"u8.ToArray()));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));   // request is inside the handler

        stop.Cancel();                                            // drain begins mid-dispatch
        gate.TrySetResult();                                      // handler finishes

        var outcome = await inFlight.WaitAsync(TimeSpan.FromSeconds(10));
        var a = Assert.IsType<ForwardOutcome.Answered>(outcome);
        Assert.Contains("finished during drain", System.Text.Encoding.UTF8.GetString(a.StdoutBytes));
        Assert.Equal(0, await daemon.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task QueuedBackgroundEffect_CompletesBeforeExit_TheMemoryWriteCase()
    {
        // The scenario the roadmap warns about by name: a session's last hook
        // schedules a memory write as a Background effect; its response is
        // long gone when the daemon is told to die. The write must happen.
        var bgGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bgRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reg = new Registry().On("UserPromptSubmit", new TestHandler("memory", (_, _) =>
            Task.FromResult<Effect>(new Effect.Background(async _ =>
            {
                await bgGate.Task;         // still pending when drain starts
                bgRan.TrySetResult();
            }))));

        using var dir = new TempRuntimeDir();
        using var stop = new CancellationTokenSource();
        var daemon = await StartDaemonAsync(dir, reg, stop.Token);

        // Dispatch completes and responds — the background work is now queued.
        var outcome = await ShimClient.TryForwardAsync(dir.Paths.SocketPath,
            new HookRequest("memwrite1", "user-prompt-submit", "claude-code", "{}"u8.ToArray()));
        Assert.IsType<ForwardOutcome.Answered>(outcome);
        Assert.False(bgRan.Task.IsCompleted, "background work should still be pending");

        stop.Cancel();              // drain: exit must wait for the queue
        bgGate.TrySetResult();      // let the memory write proceed

        Assert.Equal(0, await daemon.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(bgRan.Task.IsCompletedSuccessfully, "drain exited without running the queued background effect");
    }

    [Fact]
    public async Task BlownDeadline_ExitsAnyway_NeverHangsTheProcess()
    {
        // A background effect that never finishes must not turn SIGTERM into a
        // hang: the deadline expires, daemon.drainTimeout is logged, exit 0.
        var reg = new Registry().On("UserPromptSubmit", new TestHandler("stuck", (_, _) =>
            Task.FromResult<Effect>(new Effect.Background(_ => Task.Delay(Timeout.InfiniteTimeSpan)))));

        using var log = new CapturedLog();
        using var dir = new TempRuntimeDir();
        using var stop = new CancellationTokenSource();
        var daemon = await StartDaemonAsync(dir, reg, stop.Token, deadline: TimeSpan.FromMilliseconds(400));

        Assert.IsType<ForwardOutcome.Answered>(await ShimClient.TryForwardAsync(dir.Paths.SocketPath,
            new HookRequest("stuck0001", "user-prompt-submit", "claude-code", "{}"u8.ToArray())));

        stop.Cancel();
        Assert.Equal(0, await daemon.WaitAsync(TimeSpan.FromSeconds(10)));   // bounded exit
        Assert.Contains(log.Events, e => e.Evt == "daemon.drainTimeout");    // and the drop is visible
    }

    [Fact]
    public async Task DuringDrain_NewConnectsAreRefused_ShimsFallBack()
    {
        // The listener closes first: a hook arriving mid-drain must get
        // NotDelivered (collapse + respawn later), never a hang on a backlog.
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reg = new Registry().On("UserPromptSubmit", new TestHandler("slow", async (_, ctx) =>
        {
            entered.TrySetResult();
            await gate.Task.WaitAsync(ctx.Ct);
            return new Effect.Inject("done");
        }));

        using var dir = new TempRuntimeDir();
        using var stop = new CancellationTokenSource();
        var daemon = await StartDaemonAsync(dir, reg, stop.Token);

        var inFlight = ShimClient.TryForwardAsync(dir.Paths.SocketPath,
            new HookRequest("holdopen1", "user-prompt-submit", "claude-code", "{}"u8.ToArray()));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        stop.Cancel();   // drain begins; listener closes while the dispatch holds the daemon open

        // The listener close races the cancel by a beat — poll until refused.
        await PollUntilAsync(async () =>
            await ShimClient.TryForwardAsync(dir.Paths.SocketPath,
                new HookRequest("late00001", "user-prompt-submit", "claude-code", "{}"u8.ToArray()))
                is ForwardOutcome.NotDelivered,
            TimeSpan.FromSeconds(5), "mid-drain connect is refused");

        gate.TrySetResult();
        Assert.IsType<ForwardOutcome.Answered>(await inFlight.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, await daemon.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task AfterDrain_SocketAndPidfileGone_LockFileStays()
    {
        using var dir = new TempRuntimeDir();
        using var stop = new CancellationTokenSource();
        var daemon = await StartDaemonAsync(dir, new Registry(), stop.Token);

        stop.Cancel();
        Assert.Equal(0, await daemon.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.False(File.Exists(dir.Paths.SocketPath), "graceful exit unlinks the socket");
        Assert.False(File.Exists(dir.Paths.PidPath), "graceful exit removes the pidfile");
        Assert.True(File.Exists(dir.Paths.LockPath), "the lock file stays, always");
    }
}
