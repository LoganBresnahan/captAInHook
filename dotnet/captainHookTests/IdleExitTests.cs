using CaptainHook.Core;
using CaptainHook.Wire;
using static CaptainHook.Tests.TestUtil;

namespace CaptainHook.Tests;

// mandatory-idle-exit (ADR-0004 decision 4): captaind has no bounding parent,
// so it must starve and remove itself. Window math runs on the INJECTED
// monotonic clock (FakeClock — real Task.Delay only paces watchdog polls; no
// test waits out a real window), and the trigger is the same drain path as
// SIGTERM, so "idle-exit and drain agree" is structural. Negative asserts
// ("did not exit") are deterministic: a frozen fake clock can never satisfy
// the window, so 1.5s of real patience cannot false-pass.

public class IdleExitTests
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(10);   // fake-clock seconds

    private static string NoHarness => "/tmp/chk-none-" + Guid.NewGuid().ToString("N")[..8];

    private static async Task<Task<int>> StartAsync(TempRuntimeDir dir, Registry reg, FakeClock clock)
    {
        var daemon = Task.Run(() => DaemonHost.RunAsync(dir.Paths, NoHarness, CancellationToken.None,
            reg, drainDeadline: TimeSpan.FromSeconds(5), idleWindow: Window, clock: clock.Now));
        await PollUntilAsync(async () =>
            await ShimClient.TryForwardAsync(dir.Paths.SocketPath,
                new HookRequest("warmup00", "session-start", "claude-code", "{}"u8.ToArray()))
                is ForwardOutcome.Answered,
            TimeSpan.FromSeconds(15), "daemon up");
        return daemon;
    }

    private static async Task<ForwardOutcome> DispatchAsync(TempRuntimeDir dir, string id) =>
        await ShimClient.TryForwardAsync(dir.Paths.SocketPath,
            new HookRequest(id, "user-prompt-submit", "claude-code", "{}"u8.ToArray()));

    [Fact]
    public async Task IdleDaemon_ExitsAfterTheWindow_ThroughTheDrainPath_FilesClean()
    {
        using var log = new CapturedLog();
        using var dir = new TempRuntimeDir();
        var clock = new FakeClock();
        var daemon = await StartAsync(dir, new Registry(), clock);

        clock.Advance(TimeSpan.FromSeconds(11));   // past the window; nothing served since start

        Assert.Equal(0, await daemon.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Contains(log.Events, e => e.Evt == "daemon.idleExit");
        Assert.Contains(log.Events, e => e.Evt == "daemon.drained");   // same exit ramp as SIGTERM
        Assert.False(File.Exists(dir.Paths.SocketPath));
        Assert.False(File.Exists(dir.Paths.PidPath));
        Assert.True(File.Exists(dir.Paths.LockPath), "the lock file stays, always");
    }

    [Fact]
    public async Task Dispatch_RefreshesTheWindow()
    {
        using var dir = new TempRuntimeDir();
        var clock = new FakeClock();
        var daemon = await StartAsync(dir, new Registry(), clock);

        clock.Advance(TimeSpan.FromSeconds(6));
        Assert.IsType<ForwardOutcome.Answered>(await DispatchAsync(dir, "refresh01"));   // stamps t=6s

        clock.Advance(TimeSpan.FromSeconds(6));   // t=12s; idle-for = 6s < 10s
        await Assert.ThrowsAsync<TimeoutException>(
            () => daemon.WaitAsync(TimeSpan.FromMilliseconds(1500)));   // deterministically alive

        clock.Advance(TimeSpan.FromSeconds(5));   // t=17s; idle-for = 11s >= 10s
        Assert.Equal(0, await daemon.WaitAsync(TimeSpan.FromSeconds(15)));
    }

    [Fact]
    public async Task PendingBackgroundWork_DefersExit_ThenEarnsAFullFreshWindow()
    {
        // The memory-write case, end to end: the response is long gone, the
        // idle window has LONG passed, but the queued background effect holds
        // the daemon open — and its completion restarts the window rather
        // than exiting on the spot.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reg = new Registry().On("UserPromptSubmit", new TestHandler("memory", (_, _) =>
            Task.FromResult<Effect>(new Effect.Background(async _ => await gate.Task))));

        using var dir = new TempRuntimeDir();
        var clock = new FakeClock();
        var daemon = await StartAsync(dir, reg, clock);

        Assert.IsType<ForwardOutcome.Answered>(await DispatchAsync(dir, "memhold01"));   // queues the effect

        clock.Advance(TimeSpan.FromSeconds(30));   // 3 windows past — but the queue is non-empty
        await Assert.ThrowsAsync<TimeoutException>(
            () => daemon.WaitAsync(TimeSpan.FromMilliseconds(1500)));   // deferred: pending > 0

        gate.TrySetResult();                        // the write completes at t=30s
        await Assert.ThrowsAsync<TimeoutException>(
            () => daemon.WaitAsync(TimeSpan.FromMilliseconds(1500)));   // fresh window: no instant exit

        clock.Advance(TimeSpan.FromSeconds(11));    // t=41s; idle-for since completion >= window
        Assert.Equal(0, await daemon.WaitAsync(TimeSpan.FromSeconds(15)));
    }
}
