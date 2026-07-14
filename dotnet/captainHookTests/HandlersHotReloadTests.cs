using System.Text.Json;
using CaptainHook.Actors;
using CaptainHook.Core;
using CaptainHook.Handlers;
using static CaptainHook.Tests.TestUtil;

namespace CaptainHook.Tests;

// handlers-hot-reload (ADR-0010 phase 7, adversarial-verify slice): the runtime
// registry-refresh seam. Dispatcher.Reconcile diffs the reloadable (exec)
// workers by a config fingerprint keyed on a stable worker id — unchanged
// entries keep their WARM child untouched (no churn), removed entries
// drain-then-die (not GC), changed entries replace behind the old kill, and a
// malformed/empty reload kills ALL residents (no keep-last-good). The stat-gate
// (ReloadingHandlers) mirrors ReloadingPolicy. Real /bin/sh children throughout;
// every wait is a bounded PollUntilAsync — the state under test IS OS-process
// lifecycle. Supervisor.Remove (the one new F# member) gets a focused unit test.
public class HandlersHotReloadTests : IDisposable
{
    private readonly TempRuntimeDir _tmp = new();

    public HandlersHotReloadTests()
    {
        Directory.CreateDirectory(_tmp.Path);
        ChildRecords.OverrideDir = Path.Combine(_tmp.Path, "children");   // never the live tree
    }

    public void Dispose()
    {
        ChildRecords.OverrideDir = null;
        _tmp.Dispose();
    }

    // A lock-step echo server: handshake, then answer every envelope with its
    // own pid and the mandatory dispatchId echo.
    private const string EchoServer =
        """
        echo '{"ready":1}'
        while read l; do
          id="${l#*\"dispatchId\":\"}"; id="${id%%\"*}"
          printf '{"effect":"inject","text":"pong %s","dispatchId":"%s"}\n' "$$" "$id"
        done
        """;

    private static HookEvent Uev => new("UserPromptSubmit", "s-hr", null,
        JsonDocument.Parse("""{"prompt":"hi"}""").RootElement.Clone());

    // ---- helpers -----------------------------------------------------------

    private static ExecEntry ResidentEntry(string name, string script, string[] events, params string[] extraArgs)
    {
        var args = new List<string> { "-c", script, "sh" };
        args.AddRange(extraArgs);
        return new ExecEntry(name, "/bin/sh", args, events, ExecMode.Resident, FailMode.Open,
                             null, null, new Dictionary<string, string>(), [], null);
    }

    private static Registry BuildRegistry(params ExecEntry[] entries)
    {
        var reg = new Registry();
        HookRun.RegisterExecHandlers(reg, new ExecHandlersResolution.Loaded(entries.ToList(), []));
        return reg;
    }

    private static Registry EmptyExecRegistry()   // what a malformed file reconciles to
    {
        var reg = new Registry();
        HookRun.RegisterExecHandlers(reg, new ExecHandlersResolution.Malformed("test: forced malformed"));
        return reg;
    }

    private static bool Alive(int pid)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            return stat[stat.LastIndexOf(')') + 2] != 'Z';
        }
        catch (Exception) { return false; }
    }

    private static bool TryPid(string body, out int pid)
    {
        pid = 0;
        var at = body.IndexOf("pong ", StringComparison.Ordinal);
        if (at < 0) return false;
        var digits = new string(body[(at + 5)..].TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out pid);
    }

    /// Dispatch until the resident child answers a real pong (fail-open Noop
    /// while it warms), returning its pid.
    private static async Task<int> FirstPongAsync(Dispatcher d, string what = "resident answers")
    {
        int pid = 0;
        var n = 0;
        await PollUntilAsync(async () =>
        {
            var r = await d.DispatchAsync(Uev, $"poll{n++:D4}");
            return r.Merged is Effect.Inject i && TryPid(i.Text, out pid);
        }, TimeSpan.FromSeconds(15), what);
        return pid;
    }

    private static string RecordPath(int pid) => Path.Combine(ChildRecords.Dir, $"child-{pid}.json");

    // ======================================================================
    //  Dispatcher.Reconcile — the diff engine (hand-built registries)
    // ======================================================================

    [Fact]
    public async Task Reconcile_UnchangedEntry_KeepsWarmChild_NoChurn()
    {
        // THE no-churn guarantee: reconciling with a byte-identical entry must
        // leave the warm child running — same pid, never killed.
        if (ProcessGroup.SetsidPath is null) return;   // xunit 2.x: no dynamic skip
        using var captured = new CapturedLog();
        var dispatcher = new Dispatcher(BuildRegistry(ResidentEntry("echo", EchoServer, ["UserPromptSubmit"])),
                                        TimeSpan.FromSeconds(5));
        try
        {
            var pid1 = await FirstPongAsync(dispatcher);
            Assert.True(Alive(pid1));

            var summary = dispatcher.Reconcile(BuildRegistry(ResidentEntry("echo", EchoServer, ["UserPromptSubmit"])));
            Assert.Equal(new ReconcileSummary(Added: 0, Removed: 0, Changed: 0, Kept: 1), summary);

            Assert.True(Alive(pid1), "an unchanged entry's child must survive the reload untouched");
            var again = Assert.IsType<Effect.Inject>((await dispatcher.DispatchAsync(Uev, "after001")).Merged);
            Assert.True(TryPid(again.Text, out var pid2));
            Assert.Equal(pid1, pid2);   // the SAME warm child answered
            Assert.DoesNotContain(captured.Events, e => e.Evt == "exec.spawn" && !Equals(e.Fields.Data!["pid"], pid1));
        }
        finally { await dispatcher.DisposeHandlersAsync(); }
    }

    [Fact]
    public async Task Reconcile_AddedEntry_SpawnsAndServes_ExistingUntouched()
    {
        if (ProcessGroup.SetsidPath is null) return;
        var dispatcher = new Dispatcher(BuildRegistry(ResidentEntry("a", EchoServer, ["UserPromptSubmit"])),
                                        TimeSpan.FromSeconds(5));
        try
        {
            var pidA = await FirstPongAsync(dispatcher);

            var summary = dispatcher.Reconcile(BuildRegistry(
                ResidentEntry("a", EchoServer, ["UserPromptSubmit"]),
                ResidentEntry("b", EchoServer, ["Stop"])));
            Assert.Equal(1, summary.Added);
            Assert.Equal(1, summary.Kept);
            Assert.Equal(0, summary.Removed + summary.Changed);

            // B answers on its own event once warm; A's child never moved.
            int pidB = 0; var n = 0;
            await PollUntilAsync(async () =>
            {
                var r = await dispatcher.DispatchAsync(
                    new HookEvent("Stop", "s-hr", null, JsonDocument.Parse("{}").RootElement.Clone()), $"b{n++:D4}");
                return r.Merged is Effect.Inject i && TryPid(i.Text, out pidB);
            }, TimeSpan.FromSeconds(15), "added resident B serves");
            Assert.NotEqual(pidA, pidB);
            Assert.True(Alive(pidA), "adding B must not churn A");
        }
        finally { await dispatcher.DisposeHandlersAsync(); }
    }

    [Fact]
    public async Task Reconcile_RemovedEntry_KillsChild_NoLongerDispatches()
    {
        if (ProcessGroup.SetsidPath is null) return;
        var dispatcher = new Dispatcher(BuildRegistry(
            ResidentEntry("a", EchoServer, ["UserPromptSubmit"]),
            ResidentEntry("b", EchoServer, ["UserPromptSubmit"])), TimeSpan.FromSeconds(5));
        try
        {
            await FirstPongAsync(dispatcher);
            // Both children up: two distinct pids seen on exec.spawn.
            var summary = dispatcher.Reconcile(BuildRegistry(ResidentEntry("a", EchoServer, ["UserPromptSubmit"])));
            Assert.Equal(1, summary.Removed);
            Assert.Equal(1, summary.Kept);

            // B is gone from the fan-out; only A's worker remains on the event.
            var live = dispatcher.Snapshot().Where(h => h.EventType == "UserPromptSubmit").Select(h => h.Name).ToList();
            Assert.Contains("a", live);
            Assert.DoesNotContain("b", live);
        }
        finally { await dispatcher.DisposeHandlersAsync(); }
    }

    [Fact]
    public async Task Reconcile_ChangedConfig_ReplacesChild_OldDies()
    {
        // A differing fingerprint (extra argv) replaces the worker: the old
        // child is killed, a fresh one spawns behind it and serves.
        if (ProcessGroup.SetsidPath is null) return;
        var dispatcher = new Dispatcher(BuildRegistry(ResidentEntry("a", EchoServer, ["UserPromptSubmit"])),
                                        TimeSpan.FromSeconds(5));
        try
        {
            var pidOld = await FirstPongAsync(dispatcher);

            var summary = dispatcher.Reconcile(BuildRegistry(
                ResidentEntry("a", EchoServer, ["UserPromptSubmit"], "v2")));   // extra arg ⇒ new fingerprint
            Assert.Equal(1, summary.Changed);
            Assert.Equal(0, summary.Added + summary.Removed + summary.Kept);

            await PollUntilAsync(() => Task.FromResult(!Alive(pidOld)),
                TimeSpan.FromSeconds(8), "changed entry's OLD child dies");

            var pidNew = await FirstPongAsync(dispatcher, "replacement child serves");
            Assert.NotEqual(pidOld, pidNew);
            Assert.True(Alive(pidNew));
        }
        finally { await dispatcher.DisposeHandlersAsync(); }
    }

    [Fact]
    public async Task Reconcile_ChangeMidDispatch_DoesNotMisattributeOldChildDeath()
    {
        // The phase-7 adversarial-verify HIGH: a CHANGE mid-conversation does
        // Remove+Spawn at the same id, minting a fresh handle whose generation
        // resets to 1. The reload kills the OLD child under the in-flight
        // dispatch → the old worker's mailbox crashes → ChildExit. If that
        // signal carried the (aliasing) generation, the supervisor would charge
        // it to the REPLACEMENT and spuriously restart the just-warmed new
        // child. The globally-monotonic epoch makes the retired signal stale.
        // Proof: the replacement worker never restarts (Generation stays 1).
        if (ProcessGroup.SetsidPath is null) return;
        using var captured = new CapturedLog();
        var midfile = Path.Combine(_tmp.Path, "mid.flag");
        // Handshake, then on the FIRST envelope: signal mid-conversation and
        // hang — so the dispatch is provably awaiting an answer when the CHANGE
        // kills this child. (Budget is generous so the RELOAD, not the budget,
        // is what kills it.)
        var slow = "echo '{\"ready\":1}'\n" +
                   "read l\n" +
                   $"touch '{midfile}'\n" +
                   "sleep 30\n";
        var dispatcher = new Dispatcher(BuildRegistry(
            new ExecEntry("w", "/bin/sh", ["-c", slow, "sh"], ["UserPromptSubmit"], ExecMode.Resident,
                FailMode.Open, null, null, new Dictionary<string, string>(), [], null)),
            TimeSpan.FromSeconds(20));
        try
        {
            await PollUntilAsync(() => Task.FromResult(captured.Events.Any(e => e.Evt == "exec.ready")),
                TimeSpan.FromSeconds(15), "old child ready");

            var d1 = dispatcher.DispatchAsync(Uev, "slow0001");
            await PollUntilAsync(() => Task.FromResult(File.Exists(midfile)),
                TimeSpan.FromSeconds(10), "dispatch is mid-conversation");

            // CHANGE to a fast echo server — kills the old child under d1.
            var s = dispatcher.Reconcile(BuildRegistry(
                new ExecEntry("w", "/bin/sh", ["-c", EchoServer, "sh"], ["UserPromptSubmit"], ExecMode.Resident,
                    FailMode.Open, null, null, new Dictionary<string, string>(), [], null)));
            Assert.Equal(1, s.Changed);

            Assert.IsType<Effect.Noop>((await d1).Merged);   // d1 degrades to fail-open

            var pidNew = await FirstPongAsync(dispatcher, "replacement serves");
            // Dispatch several times: a spurious restart (the bug) would churn
            // the child to a new pid and bump Generation past 1.
            for (var i = 0; i < 5; i++)
            {
                var r = Assert.IsType<Effect.Inject>((await dispatcher.DispatchAsync(Uev, $"chk{i:D5}")).Merged);
                Assert.True(TryPid(r.Text, out var pid));
                Assert.Equal(pidNew, pid);
            }
            var w = Assert.Single(dispatcher.Snapshot(), h => h.Name == "w");
            Assert.Equal(1, w.Generation);   // replacement never spuriously restarted
            Assert.False(w.Dead);
        }
        finally { await dispatcher.DisposeHandlersAsync(); }
    }

    [Fact]
    public async Task Reconcile_ToEmpty_KillsAllResidents_NoKeepLastGood()
    {
        // Malformed/absent ⇒ a fresh registry with ZERO exec handlers ⇒ every
        // resident becomes a removal. This is the malformed-reload contract.
        if (ProcessGroup.SetsidPath is null) return;
        var dispatcher = new Dispatcher(BuildRegistry(
            ResidentEntry("a", EchoServer, ["UserPromptSubmit"]),
            ResidentEntry("b", EchoServer, ["Stop"])), TimeSpan.FromSeconds(5));
        try
        {
            var pidA = await FirstPongAsync(dispatcher);

            var summary = dispatcher.Reconcile(EmptyExecRegistry());
            Assert.Equal(2, summary.Removed);
            Assert.Equal(0, summary.Kept + summary.Added + summary.Changed);

            await PollUntilAsync(() => Task.FromResult(!Alive(pidA)),
                TimeSpan.FromSeconds(8), "malformed reload kills every resident");
            Assert.Empty(dispatcher.Snapshot());   // no exec handlers left (no coded in this registry)
        }
        finally { await dispatcher.DisposeHandlersAsync(); }
    }

    [Fact]
    public async Task Reconcile_ChangingEventsList_ChurnsOnlyTheAffectedEvent()
    {
        // The fingerprint EXCLUDES the event, so an entry moving from [UPS] to
        // [UPS, Stop] keeps the UPS child warm and merely ADDS a Stop child —
        // no churn on the survivor.
        if (ProcessGroup.SetsidPath is null) return;
        var dispatcher = new Dispatcher(BuildRegistry(ResidentEntry("a", EchoServer, ["UserPromptSubmit"])),
                                        TimeSpan.FromSeconds(5));
        try
        {
            var pidUps = await FirstPongAsync(dispatcher);

            var summary = dispatcher.Reconcile(BuildRegistry(ResidentEntry("a", EchoServer, ["UserPromptSubmit", "Stop"])));
            Assert.Equal(1, summary.Added);   // the new Stop worker
            Assert.Equal(1, summary.Kept);    // the UPS worker, untouched
            Assert.Equal(0, summary.Removed + summary.Changed);

            Assert.True(Alive(pidUps), "the surviving event's child must not churn");
            Assert.Equal(pidUps, (await FirstPongAsync(dispatcher, "same UPS child")));
        }
        finally { await dispatcher.DisposeHandlersAsync(); }
    }

    [Fact]
    public async Task Reconcile_AfterTeardown_RefusesNewSpawn()
    {
        // A reconcile racing (or following) a drain must not resurrect a live
        // child: TrackSwap refuses admission once torn down, so a just-added
        // worker's IEagerStart never spawns.
        if (ProcessGroup.SetsidPath is null) return;
        var dispatcher = new Dispatcher(BuildRegistry(ResidentEntry("a", EchoServer, ["UserPromptSubmit"])),
                                        TimeSpan.FromSeconds(5));
        await FirstPongAsync(dispatcher);
        await dispatcher.DisposeHandlersAsync();   // torn down

        using var captured = new CapturedLog();
        var summary = dispatcher.Reconcile(BuildRegistry(
            ResidentEntry("a", EchoServer, ["UserPromptSubmit"]),
            ResidentEntry("c", EchoServer, ["Stop"])));
        // C is "added" bookkeeping-wise, but its child never spawns (admission
        // refused). Give any errant spawn a beat to appear, then assert none did
        // for the NEW worker.
        await Task.Delay(200);
        Assert.DoesNotContain(captured.Events, e => e.Evt == "exec.spawn");
    }

    [Fact]
    public async Task Reconcile_LeavesCodedHandlersUntouched()
    {
        // Built via BuildDefaultRegistry (coded EchoHandler on UserPromptSubmit
        // + a oneshot exec entry). Dropping the exec entry must leave the coded
        // handler registered — coded handlers are frozen, never reconciled.
        var path = Path.Combine(_tmp.Path, "coded.json");
        File.WriteAllText(path, HandlersJson(new
        {
            name = "one", command = "/bin/true", events = new[] { "UserPromptSubmit" }, mode = "oneshot",
        }));
        var dispatcher = new Dispatcher(HookRun.BuildDefaultRegistry(path), TimeSpan.FromSeconds(2));
        try
        {
            var upsBefore = dispatcher.Snapshot().Where(h => h.EventType == "UserPromptSubmit").ToList();
            Assert.Contains(upsBefore, h => h.Name == "one");
            var codedName = upsBefore.Single(h => h.Name != "one").Name;

            File.Delete(path);
            var summary = dispatcher.Reconcile(HookRun.BuildDefaultRegistry(path));
            Assert.Equal(1, summary.Removed);

            var upsAfter = dispatcher.Snapshot().Where(h => h.EventType == "UserPromptSubmit").Select(h => h.Name).ToList();
            Assert.Contains(codedName, upsAfter);       // coded survives
            Assert.DoesNotContain("one", upsAfter);     // exec removed
        }
        finally { await dispatcher.DisposeHandlersAsync(); }
    }

    [Fact]
    public void ExecFingerprint_IsInjective_NoSeparatorCollisions()
    {
        // The length-prefixed framing must alias NOTHING a naive separator
        // scheme would: two configs that differ only where a separator could be
        // forged must still get distinct fingerprints (else a reload keeps a
        // stale child). Pure — no child spawned.
        static ExecEntry E(string[] args, Dictionary<string, string> env) =>
            new("n", "/bin/sh", args, ["UserPromptSubmit"], ExecMode.Resident, FailMode.Open,
                null, null, env, [], null);
        var empty = new Dictionary<string, string>();

        Assert.NotEqual(HookRun.ExecFingerprint(E(["a", "b"], empty)),
                        HookRun.ExecFingerprint(E(["ab"], empty)));              // args boundary
        Assert.NotEqual(HookRun.ExecFingerprint(E([], new() { ["A"] = "B=C" })),
                        HookRun.ExecFingerprint(E([], new() { ["A=B"] = "C" })));      // env kv boundary (value with '=')
        Assert.NotEqual(HookRun.ExecFingerprint(E(["a"], empty)),
                        HookRun.ExecFingerprint(E([], empty)));                        // arg-count boundary
        Assert.Equal(HookRun.ExecFingerprint(E(["a", "b"], new() { ["K"] = "V" })),
                     HookRun.ExecFingerprint(E(["a", "b"], new() { ["K"] = "V" })));   // identical ⇒ equal
    }

    // ======================================================================
    //  ReloadingHandlers — the stat-gate
    // ======================================================================

    [Fact]
    public async Task StatGate_UnchangedFile_DoesNotReconcile()
    {
        var path = Path.Combine(_tmp.Path, "h.json");
        File.WriteAllText(path, HandlersJson(OneshotJson("x")));
        Touch(path, 1);
        var dispatcher = new Dispatcher(HookRun.BuildDefaultRegistry(path), TimeSpan.FromSeconds(2));
        try
        {
            using var captured = new CapturedLog();
            var reload = new ReloadingHandlers(path, dispatcher);
            reload.MaybeReload();   // stamp identical to the seed
            reload.MaybeReload();
            Assert.DoesNotContain(captured.Events, e => e.Evt == "handlers.reload");
        }
        finally { await dispatcher.DisposeHandlersAsync(); }
    }

    [Fact]
    public async Task StatGate_MtimeChange_Reconciles()
    {
        var path = Path.Combine(_tmp.Path, "h.json");
        File.WriteAllText(path, HandlersJson(OneshotJson("x")));
        Touch(path, 1);
        var dispatcher = new Dispatcher(HookRun.BuildDefaultRegistry(path), TimeSpan.FromSeconds(2));
        try
        {
            using var captured = new CapturedLog();
            var reload = new ReloadingHandlers(path, dispatcher);

            File.WriteAllText(path, HandlersJson(OneshotJson("x"), OneshotJson("y")));   // add an entry
            Touch(path, 2);
            reload.MaybeReload();

            var evt = Assert.Single(captured.Events.ToArray(), e => e.Evt == "handlers.reload");
            Assert.Equal(1, Convert.ToInt32(evt.Fields.Data!["added"]));
            Assert.Contains("y", dispatcher.Snapshot().Select(h => h.Name));
        }
        finally { await dispatcher.DisposeHandlersAsync(); }
    }

    [Fact]
    public async Task StatGate_MalformedEdit_KillsAllResidents_Loud()
    {
        if (ProcessGroup.SetsidPath is null) return;
        var path = Path.Combine(_tmp.Path, "h.json");
        File.WriteAllText(path, HandlersJson(ResidentJson("a", EchoServer, "UserPromptSubmit")));
        Touch(path, 1);
        var dispatcher = new Dispatcher(HookRun.BuildDefaultRegistry(path), TimeSpan.FromSeconds(5));
        try
        {
            var pidA = await FirstPongAsync(dispatcher);
            using var captured = new CapturedLog();
            var reload = new ReloadingHandlers(path, dispatcher);

            File.WriteAllText(path, "{ this is not valid json");   // corrupt it
            Touch(path, 2);
            reload.MaybeReload();

            Assert.Contains(captured.Events, e => e.Evt == "handlers.malformed");
            await PollUntilAsync(() => Task.FromResult(!Alive(pidA)),
                TimeSpan.FromSeconds(8), "malformed edit kills the resident");
        }
        finally { await dispatcher.DisposeHandlersAsync(); }
    }

    // ======================================================================
    //  Supervisor.Remove — the one new F# member
    // ======================================================================

    [Fact]
    public async Task SupervisorRemove_MarksDead_FreesId_Idempotent()
    {
        var clock = new FakeClock();
        var sup = new Supervisor("rm", maxRestarts: 5, TimeSpan.FromSeconds(5), clock.Now);
        var worker = Worker<int, int>.Supervised(sup, "w", () => x => Task.FromResult(x + 1));

        Assert.False(worker.IsDead);
        Assert.Equal(AskStatus.Ok, (await worker.AskClassifiedAsync(1, 1000, 200, "c1")).Status);

        sup.Remove("w");
        Assert.True(worker.IsDead, "Remove marks the handle dead");
        Assert.Equal(AskStatus.Dead, (await worker.AskClassifiedAsync(1, 1000, 200, "c2")).Status);

        sup.Remove("w");   // idempotent — no throw
        // The id is FREE: a fresh worker at the SAME id spawns (this is exactly
        // what a CHANGE reconcile relies on — Remove then re-Spawn at the id).
        var w2 = Worker<int, int>.Supervised(sup, "w", () => x => Task.FromResult(x + 2));
        Assert.False(w2.IsDead);
        Assert.Equal(3, (await w2.AskClassifiedAsync(1, 1000, 200, "c3")).Reply);
    }

    // ======================================================================
    //  Daemon E2E — the stat-gate + reconcile through the real stack
    // ======================================================================

    [Fact]
    public async Task DaemonE2E_HotReload_AddThenRemove_NoChurnNoOrphan()
    {
        // The flagship: a live daemon, edited handlers.json effective on the
        // next hook. Add a resident (the survivor's warm child never moves),
        // then remove it (its child dies, no orphan) — all through the shim
        // wire, with the reconcile driven by the per-dispatch stat-gate.
        if (ProcessGroup.SetsidPath is null) return;
        using var captured = new CapturedLog();
        var path = Path.Combine(_tmp.Path, "handlers.json");
        File.WriteAllText(path, HandlersJson(ResidentJson("a", EchoServer, "UserPromptSubmit")));
        Touch(path, 1);

        using var stop = new CancellationTokenSource();
        var daemon = Task.Run(() => DaemonHost.RunAsync(_tmp.Paths, NoHarnessDir(), stop.Token, handlersPath: path));
        await PollUntilAsync(async () =>
            await CaptainHook.Wire.ShimClient.TryForwardAsync(_tmp.Paths.SocketPath,
                new CaptainHook.Wire.HookRequest("up000000", "session-start", "claude-code", "{}"u8.ToArray()))
                is CaptainHook.Wire.ForwardOutcome.Answered,
            TimeSpan.FromSeconds(15), "daemon up");

        var pidA = await PollPidAsync("user-prompt-submit", "a");
        Assert.True(Alive(pidA));
        Assert.True(File.Exists(RecordPath(pidA)));

        // --- ADD b (resident on Stop), a UNCHANGED ---
        File.WriteAllText(path, HandlersJson(
            ResidentJson("a", EchoServer, "UserPromptSubmit"),
            ResidentJson("b", EchoServer, "Stop")));
        Touch(path, 2);
        var pidB = await PollPidAsync("stop", "b");        // first Stop hook reconciles + warms b
        Assert.NotEqual(pidA, pidB);
        // No churn: a still answers from the SAME warm child.
        Assert.True(TryPid(await ForwardBodyAsync("user-prompt-submit", "achk0001"), out var pidA2));
        Assert.Equal(pidA, pidA2);
        Assert.True(Alive(pidA));
        var reloadAdd = captured.Events.Where(e => e.Evt == "handlers.reload").ToList();
        Assert.Contains(reloadAdd, e => Convert.ToInt32(e.Fields.Data!["added"]) == 1
                                        && Convert.ToInt32(e.Fields.Data!["kept"]) >= 1);

        // --- REMOVE b, a UNCHANGED ---
        File.WriteAllText(path, HandlersJson(ResidentJson("a", EchoServer, "UserPromptSubmit")));
        Touch(path, 3);
        await ForwardBodyAsync("user-prompt-submit", "trig0001");   // trigger the reconcile
        await PollUntilAsync(() => Task.FromResult(!Alive(pidB)),
            TimeSpan.FromSeconds(8), "removed resident b's child dies");
        await PollUntilAsync(() => Task.FromResult(!File.Exists(RecordPath(pidB))),
            TimeSpan.FromSeconds(5), "b's record cleaned at group death");
        Assert.True(Alive(pidA), "removing b must not touch a");
        Assert.Contains(captured.Events, e => e.Evt == "handlers.reload"
            && Convert.ToInt32(e.Fields.Data!["removed"]) == 1);

        // --- drain: a dies with the daemon, no orphan ---
        stop.Cancel();
        Assert.Equal(0, await daemon.WaitAsync(TimeSpan.FromSeconds(15)));
        await PollUntilAsync(() => Task.FromResult(!Alive(pidA)),
            TimeSpan.FromSeconds(5), "survivor a dies at drain");
    }

    // ---- E2E plumbing ------------------------------------------------------

    private async Task<string> ForwardBodyAsync(string evt, string id)
    {
        var o = await CaptainHook.Wire.ShimClient.TryForwardAsync(_tmp.Paths.SocketPath,
            new CaptainHook.Wire.HookRequest(id, evt, "claude-code", "{}"u8.ToArray()));
        return System.Text.Encoding.UTF8.GetString(
            Assert.IsType<CaptainHook.Wire.ForwardOutcome.Answered>(o).StdoutBytes);
    }

    private async Task<int> PollPidAsync(string evt, string idPrefix)
    {
        int pid = 0; var n = 0;
        await PollUntilAsync(async () => TryPid(await ForwardBodyAsync(evt, $"{idPrefix}{n++:D4}"), out pid),
            TimeSpan.FromSeconds(15), $"{evt} answers a pong");
        return pid;
    }

    // ---- JSON builders -----------------------------------------------------

    private static string HandlersJson(params object[] handlers) =>
        JsonSerializer.Serialize(new { version = 1, handlers });

    private static object ResidentJson(string name, string script, params string[] events) =>
        new { name, command = "/bin/sh", args = new[] { "-c", script, "sh" }, events, mode = "resident" };

    private static object OneshotJson(string name) =>
        new { name, command = "/bin/true", events = new[] { "UserPromptSubmit" }, mode = "oneshot" };

    /// Pin the mtime to a distinct, monotonically-increasing instant so each
    /// edit's (mtime,size) stamp differs deterministically — never relying on
    /// wall-clock advancing between fast writes (the HotReloadTests discipline).
    private static void Touch(string path, int seq) =>
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seq));
}
