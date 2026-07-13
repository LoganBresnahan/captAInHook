using System.Text.Json;
using CaptainHook.Actors;
using CaptainHook.Core;
using CaptainHook.Handlers;
using CaptainHook.Wire;
using static CaptainHook.Tests.TestUtil;

namespace CaptainHook.Tests;

// kill-discipline-teardown (ADR-0010 d6 + N6): the teardown seam (restart
// evicts + disposes the replaced instance; escalation disposes the LAST one —
// MarkDead never re-runs the factory), the kill mechanics (setsid process
// group at spawn; TERM→grace→KILL group-wide), and the daemon drain's child
// phase (children die with the daemon; a budget outlasting the drain deadline
// is cut). Real /bin/sh children where the subject IS process lifecycle; all
// waits are bounded PollUntilAsync/WaitAsync, never sleeps.

/// Factory-made disposable handler: records its disposal, so the teardown
/// seam's who-got-disposed-when is directly observable.
internal sealed class DisposableProbe(
    string tag, Func<HookEvent, bool>? crashWhen, Action<string> onDisposed) : IHandler, IAsyncDisposable
{
    public string Name => "probe";
    public FailMode OnFailure => FailMode.Open;

    public Task<Effect> HandleAsync(HookEvent e, HandlerContext ctx) =>
        crashWhen?.Invoke(e) == true
            ? throw new InvalidOperationException($"{tag} crashed deliberately")
            : Task.FromResult<Effect>(new Effect.Inject(tag));

    public ValueTask DisposeAsync()
    {
        onDisposed(tag);
        return ValueTask.CompletedTask;
    }
}

public class KillDisciplineTests
{
    private static HookEvent Ev(string type = "UserPromptSubmit") => new(
        type, "s-kill", Cwd: null, JsonDocument.Parse("""{"prompt":"hi"}""").RootElement.Clone());

    private static HandlerContext Ctx(CancellationToken ct = default) =>
        new(DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10), ct, "kd123456");

    private static ExecHandler Sh(string script, params string[] extra)
    {
        var args = new List<string> { "-c", script, "sh" };
        args.AddRange(extra);
        return new ExecHandler("kill-test", "/bin/sh", args);
    }

    /// /proc-based liveness that treats a zombie as dead: after a group kill
    /// the leader may sit unreaped for a beat, but Z-state means the kill won.
    private static bool Alive(int pid)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            return stat[stat.LastIndexOf(')') + 2] != 'Z';
        }
        catch (Exception) { return false; }
    }

    // ---- kill mechanics -----------------------------------------------------

    [Fact]
    public async Task SetsidSpawn_ChildLeadsItsOwnProcessGroup()
    {
        // The spawn half of the discipline: setsid execs the payload in place,
        // so the child's pgid IS its pid — the precondition for kill(-pid)
        // reaching grandchildren. (Probed at the OS level in platform.md; this
        // pins it end-to-end through the real spawn site.)
        if (ProcessGroup.SetsidPath is null) return;   // xunit 2.x: no dynamic skip — setsid is universal on Linux anyway
        var h = Sh("""read line; pg=$(ps -o pgid= -p $$ | tr -d ' '); printf '{"effect":"inject","text":"%s %s"}\n' "$$" "$pg" """);

        var eff = await h.HandleAsync(Ev(), Ctx());

        var parts = Assert.IsType<Effect.Inject>(eff).Text.Split(' ');
        Assert.Equal(parts[0], parts[1]);
    }

    [Fact]
    public async Task BudgetKill_TakesTheGrandchildWithTheGroup()
    {
        // The reason the group exists: `sleep 300 &` inherits the child's
        // group, so the budget kill reaches it even though the child itself
        // (post-exec) is a different program by then.
        if (ProcessGroup.SetsidPath is null) return;   // xunit 2.x: no dynamic skip — setsid is universal on Linux anyway
        using var tmp = new TempRuntimeDir();
        Directory.CreateDirectory(tmp.Path);
        var pidFile = Path.Combine(tmp.Path, "pids");
        var h = Sh("""sleep 300 & echo "$$ $!" > "$1"; exec sleep 300""", pidFile);

        using var cts = new CancellationTokenSource();
        var run = h.HandleAsync(Ev(), Ctx(cts.Token));

        int child = 0, grandchild = 0;
        await PollUntilAsync(() =>
        {
            if (!File.Exists(pidFile)) return Task.FromResult(false);
            var parts = File.ReadAllText(pidFile).Trim().Split(' ');
            return Task.FromResult(parts.Length == 2
                && int.TryParse(parts[0], out child) && int.TryParse(parts[1], out grandchild));
        }, TimeSpan.FromSeconds(10), "child + grandchild pids recorded");

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        await PollUntilAsync(() => Task.FromResult(!Alive(child)), TimeSpan.FromSeconds(10), "child dead");
        await PollUntilAsync(() => Task.FromResult(!Alive(grandchild)), TimeSpan.FromSeconds(10),
            "grandchild dead — the group kill's whole point");
    }

    [Fact]
    public async Task TermIgnoringChild_EscalatesToSigkill_Loudly()
    {
        // trap '' TERM is inherited through fork+exec, so the WHOLE group
        // shrugs off the SIGTERM; the grace must expire and SIGKILL must land,
        // with the escalation visible as a second exec.kill line (how=kill).
        if (ProcessGroup.SetsidPath is null) return;   // xunit 2.x: no dynamic skip — setsid is universal on Linux anyway
        using var captured = new CapturedLog();
        using var tmp = new TempRuntimeDir();
        Directory.CreateDirectory(tmp.Path);
        var readyFile = Path.Combine(tmp.Path, "trap-armed");
        // The ready file gates the cancel: TERM must not land before the trap
        // is armed, or the child dies politely and there is no escalation.
        var h = Sh("""trap '' TERM; : > "$1"; read line; exec sleep 300""", readyFile);

        using var cts = new CancellationTokenSource();
        var run = h.HandleAsync(Ev(), Ctx(cts.Token));
        await PollUntilAsync(
            () => Task.FromResult(File.Exists(readyFile)),
            TimeSpan.FromSeconds(10), "child spawned with TERM trap armed");

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        // The initiation line lands at TERM time; the escalation line ~2s
        // later (KillGrace), off the dispatch path.
        var init = captured.Events.First(e => e.Evt == "exec.kill");
        var pid = (int)init.Fields.Data!["pid"];
        await PollUntilAsync(() => Task.FromResult(captured.Events.Any(e =>
                e.Evt == "exec.kill" && e.Fields.Data is { } d
                && d.TryGetValue("how", out var how) && (string)how == "kill")),
            TimeSpan.FromSeconds(10), "SIGKILL escalation trailed");
        await PollUntilAsync(() => Task.FromResult(!Alive(pid)), TimeSpan.FromSeconds(10),
            "TERM-ignoring child dead anyway");
    }

    // ---- the teardown seam --------------------------------------------------

    [Fact]
    public async Task SupervisedRestart_DisposesTheReplacedInstance()
    {
        // Factory registration: gen1 crashes, the supervisor re-runs the
        // factory (gen2), and the seam must dispose gen1 — its children must
        // not outlive its generation. Gen2 stays live.
        var made = 0;
        var disposed = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var reg = new Registry().On("UserPromptSubmit", "probe", () =>
        {
            var n = Interlocked.Increment(ref made);
            return new DisposableProbe($"gen{n}", crashWhen: _ => n == 1, disposed.Enqueue);
        });
        var dispatcher = new Dispatcher(reg, TimeSpan.FromSeconds(2));

        await dispatcher.DispatchAsync(Ev());   // gen1 crashes → restart swaps in gen2

        await PollUntilAsync(() => Task.FromResult(disposed.Contains("gen1")),
            TimeSpan.FromSeconds(10), "replaced instance disposed");
        Assert.DoesNotContain("gen2", disposed);
    }

    [Fact]
    public async Task Escalation_DisposesTheLastInstance_TheOrphanHole()
    {
        // MarkDead never re-runs the factory, so no swap will ever evict the
        // final instance — without the escalation hook it orphans (N3). Also
        // pins that the seam uses SubscribeEscalated: the host's OnEscalated
        // assignment below must keep working alongside it.
        var clock = new FakeClock();   // frozen: both crashes inside the window
        var sup = new Supervisor("kd-esc", maxRestarts: 1, TimeSpan.FromSeconds(5), clock.Now);
        var escalated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sup.OnEscalated = (_, _) => escalated.TrySetResult();

        var made = 0;
        var disposed = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var reg = new Registry().On("UserPromptSubmit", "probe", () =>
        {
            var n = Interlocked.Increment(ref made);
            return new DisposableProbe($"gen{n}", crashWhen: _ => true, disposed.Enqueue);
        });
        var dispatcher = new Dispatcher(reg, TimeSpan.FromMilliseconds(300), sup);

        for (var i = 0; i < 20 && !escalated.Task.IsCompleted; i++)
            await dispatcher.DispatchAsync(Ev());
        await escalated.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var last = $"gen{made}";
        await PollUntilAsync(() => Task.FromResult(disposed.Contains(last)),
            TimeSpan.FromSeconds(10), $"escalated worker's last instance ({last}) disposed");
    }

    [Fact]
    public async Task InstanceRegistration_SingletonReusedAcrossRestart_NeverDisposed()
    {
        // Instance registration's documented contract: the factory returns THE
        // SAME object, a restart reuses it — disposing it would break the
        // state-carries-over promise. The reference-equal swap must skip.
        var disposed = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var crash = new[] { true };
        var h = new DisposableProbe("singleton", crashWhen: _ => Volatile.Read(ref crash[0]), disposed.Enqueue);
        var reg = new Registry().On("UserPromptSubmit", h);
        var dispatcher = new Dispatcher(reg, TimeSpan.FromSeconds(2));

        await dispatcher.DispatchAsync(Ev());   // crash → restart, same instance
        Volatile.Write(ref crash[0], false);

        // Prove the singleton is back in service post-restart, then that no
        // disposal ever fired for it.
        await PollUntilAsync(async () =>
            (await dispatcher.DispatchAsync(Ev())).Merged is Effect.Inject,
            TimeSpan.FromSeconds(10), "singleton answering after restart");
        Assert.Empty(disposed);
    }

    [Fact]
    public async Task SharedSingleton_SurvivesSiblingEscalation_DisposedOnceAtDrain()
    {
        // One instance on two events: event A's worker escalates, but B still
        // holds the instance — disposing it then would kill B's live handler
        // out from under it. Only the drain, when every slot lets go, disposes
        // it — exactly once.
        var clock = new FakeClock();
        var sup = new Supervisor("kd-shared", maxRestarts: 1, TimeSpan.FromSeconds(5), clock.Now);
        var escalated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sup.OnEscalated = (_, _) => escalated.TrySetResult();

        var disposed = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var h = new DisposableProbe("shared", crashWhen: e => e.Type == "EvA", disposed.Enqueue);
        var reg = new Registry().On("EvA", h).On("EvB", h);
        var dispatcher = new Dispatcher(reg, TimeSpan.FromMilliseconds(300), sup);

        for (var i = 0; i < 20 && !escalated.Task.IsCompleted; i++)
            await dispatcher.DispatchAsync(Ev("EvA"));
        await escalated.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // B's worker still answers with the shared instance — not disposed.
        await PollUntilAsync(async () =>
            (await dispatcher.DispatchAsync(Ev("EvB"))).Merged is Effect.Inject,
            TimeSpan.FromSeconds(10), "sibling worker still serving the shared instance");
        Assert.Empty(disposed);

        Assert.Equal(1, await dispatcher.DisposeHandlersAsync());
        Assert.Equal(new[] { "shared" }, disposed.ToArray());
    }

    [Fact]
    public async Task DeadLeader_LiveGrandchild_TeardownStillKillsTheGroup()
    {
        // The adversarial repro: `sleep 300 & exit 0` after answering — the
        // LEADER is gone but the grandchild holds the pipes (still tracked by
        // the reaper). Liveness keyed on the leader alone read this as "gone"
        // and never signaled the group; teardown must probe and kill the
        // GROUP.
        if (ProcessGroup.SetsidPath is null) return;   // xunit 2.x: no dynamic skip — setsid is universal on Linux anyway
        var reg = new Registry().On("UserPromptSubmit", "orphaner",
            () => Sh("""sleep 300 & printf '{"effect":"inject","text":"%s"}\n' "$!"; exit 0"""));
        var dispatcher = new Dispatcher(reg, TimeSpan.FromSeconds(5));

        var result = await dispatcher.DispatchAsync(Ev());
        var grandchild = int.Parse(Assert.IsType<Effect.Inject>(result.Merged).Text);
        Assert.True(Alive(grandchild), "grandchild should outlive its exited leader");

        await dispatcher.DisposeHandlersAsync();
        await PollUntilAsync(() => Task.FromResult(!Alive(grandchild)),
            TimeSpan.FromSeconds(5), "grandchild killed through the leaderless group");
    }

    [Fact]
    public async Task DrainAwaitsTheDetachedKill_TermIgnorerDeadWhenDisposeReturns()
    {
        // A budget cancel just before drain: the TERM-ignoring child's
        // SIGKILL escalation lives on a detached task — the drain must AWAIT
        // it (child stays tracked until the kill concludes; evicted
        // instances' disposals are registered pending), or a daemon exit
        // outruns the SIGKILL and orphans the child.
        if (ProcessGroup.SetsidPath is null) return;   // xunit 2.x: no dynamic skip — setsid is universal on Linux anyway
        using var tmp = new TempRuntimeDir();
        Directory.CreateDirectory(tmp.Path);
        var pidFile = Path.Combine(tmp.Path, "stubborn-pid");
        var reg = new Registry().On("UserPromptSubmit", "stubborn",
            () => Sh("""trap '' TERM; echo $$ > "$1"; read line; exec sleep 300""", pidFile),
            FailMode.Open, TimeSpan.FromMilliseconds(300));
        var dispatcher = new Dispatcher(reg, TimeSpan.FromSeconds(5));

        Assert.IsType<Effect.Noop>((await dispatcher.DispatchAsync(Ev())).Merged);   // budget kill fired
        int child = 0;
        await PollUntilAsync(() => Task.FromResult(
                File.Exists(pidFile) && int.TryParse(File.ReadAllText(pidFile).Trim(), out child) && child > 0),
            TimeSpan.FromSeconds(10), "stubborn child pid recorded");

        await dispatcher.DisposeHandlersAsync();
        // The point: dead essentially AT return — the kill was awaited, not
        // left racing the process exit. One short poll for the reap beat.
        await PollUntilAsync(() => Task.FromResult(!Alive(child)),
            TimeSpan.FromSeconds(1), "TERM-ignorer dead when the drain's await returns");
    }

    [Fact]
    public async Task DisposeHandlers_KillsALingeringChild()
    {
        // Reply-then-linger meets teardown: the child answered long ago and is
        // living its own life — the drain's child phase must still end it.
        if (ProcessGroup.SetsidPath is null) return;   // xunit 2.x: no dynamic skip — setsid is universal on Linux anyway
        var reg = new Registry().On("UserPromptSubmit", "linger",
            () => Sh("""printf '{"effect":"inject","text":"%s"}\n' "$$"; exec sleep 300"""));
        var dispatcher = new Dispatcher(reg, TimeSpan.FromSeconds(5));

        var result = await dispatcher.DispatchAsync(Ev());
        var pid = int.Parse(Assert.IsType<Effect.Inject>(result.Merged).Text);
        Assert.True(Alive(pid), "lingering child should be alive after the dispatch returned");

        Assert.Equal(1, await dispatcher.DisposeHandlersAsync());
        await PollUntilAsync(() => Task.FromResult(!Alive(pid)),
            TimeSpan.FromSeconds(10), "lingering child killed at teardown");
    }

    // ---- registration warn + the daemon drain's child phase (N6) ------------

    [Fact]
    public void BudgetBeyondDrain_WarnsAtRegistration_UnderStaysSilent()
    {
        using var captured = new CapturedLog();
        var registry = new Registry();
        HookRun.RegisterExecHandlers(registry, new ExecHandlersResolution.Loaded(
            [
                new ExecEntry("patient", "/bin/true", [], ["Stop"], ExecMode.Oneshot, FailMode.Open,
                    TimeSpan.FromSeconds(60), null, null, null, null),
                new ExecEntry("quick", "/bin/true", [], ["Stop"], ExecMode.Oneshot, FailMode.Open,
                    TimeSpan.FromSeconds(5), null, null, null, null),
            ], []),
            drainBudgetHint: TimeSpan.FromSeconds(10));

        var warn = Assert.Single(captured.Events.ToArray(), e => e.Evt == "handlers.budgetBeyondDrain");
        Assert.Equal("patient", warn.Fields.Data!["entry"]);
        Assert.Contains("drain deadline", warn.Fields.Msg);
    }

    [Fact]
    public async Task DaemonDrain_CutsTheLongDispatch_KillsItsChild_ExitsBounded()
    {
        // THE N6 scenario: a handler budget (30s) outlasting the drain
        // deadline (400ms). Cutover must not wait out the budget: the drain
        // times out, the child phase kills the wedged child group-wide, the
        // cut is trailed (daemon.drainCut), and the daemon exits promptly.
        if (ProcessGroup.SetsidPath is null) return;   // xunit 2.x: no dynamic skip — setsid is universal on Linux anyway
        using var captured = new CapturedLog();
        using var tmp = new TempRuntimeDir();
        Directory.CreateDirectory(tmp.Path);
        var pidFile = Path.Combine(tmp.Path, "wedged-pid");

        var reg = new Registry().On("UserPromptSubmit", "wedger",
            () => Sh("""echo $$ > "$1"; exec sleep 300""", pidFile),
            FailMode.Open, TimeSpan.FromSeconds(30));

        using var stop = new CancellationTokenSource();
        var daemon = Task.Run(() => DaemonHost.RunAsync(
            tmp.Paths, NoHarnessDir(), stop.Token, reg, drainDeadline: TimeSpan.FromMilliseconds(400)));
        await PollUntilAsync(async () =>
            await ShimClient.TryForwardAsync(tmp.Paths.SocketPath,
                new HookRequest("warmup00", "session-start", "claude-code", "{}"u8.ToArray()))
                is ForwardOutcome.Answered,
            TimeSpan.FromSeconds(15), "daemon up");

        var inFlight = ShimClient.TryForwardAsync(tmp.Paths.SocketPath,
            new HookRequest("longone1", "user-prompt-submit", "claude-code", "{}"u8.ToArray()));
        int child = 0;
        await PollUntilAsync(() => Task.FromResult(
                File.Exists(pidFile) && int.TryParse(File.ReadAllText(pidFile).Trim(), out child) && child > 0),
            TimeSpan.FromSeconds(10), "wedged child running inside the dispatch");

        stop.Cancel();   // drain: 400ms in-flight wait, then the child phase cuts

        Assert.Equal(0, await daemon.WaitAsync(TimeSpan.FromSeconds(10)));
        // The cut still ANSWERS: killing the child unwedges the dispatch into
        // fail-open Noop fast enough for the post-kill window to relay it —
        // the N6 claim, pinned (a cut is a degraded answer, not a dead
        // socket).
        Assert.IsType<ForwardOutcome.Answered>(await inFlight.WaitAsync(TimeSpan.FromSeconds(10)));
        await PollUntilAsync(() => Task.FromResult(!Alive(child)),
            TimeSpan.FromSeconds(10), "cut dispatch's child killed by the drain");
        Assert.Contains(captured.Events, e => e.Evt == "daemon.drainTimeout");
        Assert.Contains(captured.Events, e => e.Evt == "daemon.drainChildren");
        Assert.Contains(captured.Events, e => e.Evt == "daemon.drainCut");
    }
}
