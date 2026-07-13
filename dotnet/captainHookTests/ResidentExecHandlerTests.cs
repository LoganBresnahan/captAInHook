using System.Text.Json;
using CaptainHook.Core;
using CaptainHook.Handlers;
using static CaptainHook.Tests.TestUtil;

namespace CaptainHook.Tests;

// resident-child-runtime (ADR-0010 d3, the ultracode slice): eager spawn
// after teardown-seam admission, the three-way readiness race, lock-step
// JSONL with the MANDATORY dispatchId echo, fail-mode-while-warming,
// counted-respawn on Failed, uncounted honored-cancel on mid-conversation
// overrun, group-death record cleanup, and the no-respawn-after-teardown
// guarantee. Real /bin/sh children throughout — the state machine under test
// IS process lifecycle; every wait is a bounded PollUntilAsync/WaitAsync.

public class ResidentExecHandlerTests : IDisposable
{
    private readonly TempRuntimeDir _tmp = new();

    public ResidentExecHandlerTests()
    {
        Directory.CreateDirectory(_tmp.Path);
        // The suite must never write the LIVE ~/.captainHook tree.
        ChildRecords.OverrideDir = Path.Combine(_tmp.Path, "children");
    }

    public void Dispose()
    {
        ChildRecords.OverrideDir = null;
        _tmp.Dispose();
    }

    private static HookEvent Ev(string type = "UserPromptSubmit") => new(
        type, "s-res", Cwd: null, JsonDocument.Parse("""{"prompt":"hi"}""").RootElement.Clone());

    /// The canonical well-behaved resident payload: handshake, then a
    /// lock-step echo server that answers every envelope with its own pid and the
    /// MANDATORY dispatchId echo (extracted with shell parameter expansion).
    private const string EchoServer =
        """
        echo '{"ready":1}'
        while read l; do
          id="${l#*\"dispatchId\":\"}"; id="${id%%\"*}"
          printf '{"effect":"inject","text":"pong %s","dispatchId":"%s"}\n' "$$" "$id"
        done
        """;

    private static ResidentExecHandler Res(string script, FailMode failMode = FailMode.Open,
                                           TimeSpan? readiness = null, params string[] extra)
    {
        var args = new List<string> { "-c", script, "sh" };
        args.AddRange(extra);
        return new ResidentExecHandler("res-test", "/bin/sh", args, failMode,
                                       readinessTimeout: readiness);
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

    private static int PongPid(Effect effect)
    {
        var text = Assert.IsType<Effect.Inject>(effect).Text;
        Assert.StartsWith("pong ", text);
        return int.Parse(text["pong ".Length..]);
    }

    // ---- the wire additions ------------------------------------------------

    [Theory]
    [InlineData("""{"ready":1}""", true)]
    [InlineData("""  {"ready":1}  """, true)]     // whitespace tolerance, ParseAnswer-equivalent
    [InlineData("\uFEFF{\"ready\":1}", true)]   // one leading BOM
    [InlineData("""{"ready":2}""", false)]         // wrong version
    [InlineData("""{"ready":"1"}""", false)]       // string, not number
    [InlineData("""{"ready":1,"extra":1}""", false)]
    [InlineData("""{"effect":"noop"}""", false)]   // an ANSWER is not the handshake
    [InlineData("""{"ready":1} trailing""", false)]
    [InlineData("not json", false)]
    [InlineData("", false)]
    public void TryParseReady_StrictTable(string line, bool ok) =>
        Assert.Equal(ok, ExecWire.TryParseReady(line));

    [Fact]
    public void ParseAnswer_ExtractsTheEcho()
    {
        var ok = Assert.IsType<ExecAnswer.Ok>(
            ExecWire.ParseAnswer("""{"effect":"noop","dispatchId":"abc123"}"""));
        Assert.Equal("abc123", ok.DispatchId);
        Assert.Null(Assert.IsType<ExecAnswer.Ok>(ExecWire.ParseAnswer("""{"effect":"noop"}""")).DispatchId);
    }

    [Fact]
    public void ParseAnswer_NonStringEcho_Malformed()
    {
        var bad = Assert.IsType<ExecAnswer.Malformed>(
            ExecWire.ParseAnswer("""{"effect":"noop","dispatchId":7}"""));
        Assert.Contains(bad.Violations, v => v.Contains("dispatchId"));
    }

    // ---- eager spawn + warm reuse -------------------------------------------

    [Fact]
    public async Task EagerSpawn_ChildReadyBeforeAnyDispatch_ThenWarmReuse()
    {
        using var captured = new CapturedLog();
        var reg = new Registry().On("UserPromptSubmit", "echo", () => Res(EchoServer));
        var dispatcher = new Dispatcher(reg, TimeSpan.FromSeconds(5));

        // Spawned + ready with ZERO dispatches — the whole point of eager.
        await PollUntilAsync(() => Task.FromResult(captured.Events.Any(e => e.Evt == "exec.ready")),
            TimeSpan.FromSeconds(10), "resident child ready before any dispatch");
        Assert.Contains(captured.Events, e =>
            e.Evt == "exec.spawn" && Equals(e.Fields.Data!["mode"], "resident"));

        // Two dispatches, ONE child: same pid answers both — warm reuse.
        var pid1 = PongPid((await dispatcher.DispatchAsync(Ev(), "resid001")).Merged);
        var pid2 = PongPid((await dispatcher.DispatchAsync(Ev(), "resid002")).Merged);
        Assert.Equal(pid1, pid2);
        Assert.Single(captured.Events.ToArray(), e => e.Evt == "exec.spawn");

        await dispatcher.DisposeHandlersAsync();
        await PollUntilAsync(() => Task.FromResult(!Alive(pid1)),
            TimeSpan.FromSeconds(5), "resident child killed at teardown");
    }

    // ---- the three-way readiness race ----------------------------------------

    [Fact]
    public async Task NotReadyYet_DispatchTakesFailMode_ChildKeepsWarming_Uncounted()
    {
        // The design-panel boot-starvation find, pinned: a dispatch budget
        // expiring while the child warms takes the fail mode LOUDLY — no
        // kill, no restart — and once the child readies, the SAME child
        // (same pid, one spawn) serves the next dispatch.
        using var captured = new CapturedLog();
        var gate = Path.Combine(_tmp.Path, "warmup-gate");
        // Child warms until the gate file appears, then becomes the echo server.
        var script =
            """
            while [ ! -e "$1" ]; do sleep 0.05; done
            """ + "\n" + EchoServer;
        var reg = new Registry().On("UserPromptSubmit", "slowboot",
            () => Res(script, extra: gate), FailMode.Open, TimeSpan.FromMilliseconds(400));
        var dispatcher = new Dispatcher(reg, TimeSpan.FromSeconds(5));

        // Not ready: fail-open Noop + exec.notReady, and NO kill/restart.
        var r1 = await dispatcher.DispatchAsync(Ev(), "warm0001");
        Assert.IsType<Effect.Noop>(r1.Merged);
        Assert.Contains(captured.Events, e => e.Evt == "exec.notReady");
        Assert.DoesNotContain(captured.Events, e => e.Evt == "exec.kill");
        Assert.DoesNotContain(captured.Events, e => e.Evt == "actor.restart");

        File.WriteAllText(gate, "");   // the child finishes warming
        await PollUntilAsync(() => Task.FromResult(captured.Events.Any(e => e.Evt == "exec.ready")),
            TimeSpan.FromSeconds(10), "slow-booting child eventually ready");

        var pid = PongPid((await dispatcher.DispatchAsync(Ev(), "warm0002")).Merged);
        Assert.True(Alive(pid));
        Assert.Single(captured.Events.ToArray(), e => e.Evt == "exec.spawn");   // ONE child throughout

        await dispatcher.DisposeHandlersAsync();
    }

    [Fact]
    public async Task NotReady_FailClosed_Denies()
    {
        using var captured = new CapturedLog();
        var reg = new Registry().On("UserPromptSubmit", "gate",
            () => Res("exec sleep 300", FailMode.Closed), FailMode.Closed, TimeSpan.FromMilliseconds(300));
        var dispatcher = new Dispatcher(reg, TimeSpan.FromSeconds(5));

        var r = await dispatcher.DispatchAsync(Ev(), "deny0001");
        var deny = Assert.IsType<Effect.Decide>(r.Merged);
        Assert.Equal(Verdict.Deny, deny.Verdict);
        Assert.Contains("not ready", deny.Reason);

        await dispatcher.DisposeHandlersAsync();
    }

    [Fact]
    public async Task ReadinessTimeout_Failed_KillsChild_NextDispatchRespawns()
    {
        // Race arm 2: no handshake within readinessTimeoutMs ⇒ Failed + the
        // group dies. The FIRST dispatch that finds Failed throws (counted)
        // ⇒ supervised restart ⇒ a SECOND spawn attempt.
        using var captured = new CapturedLog();
        var reg = new Registry().On("UserPromptSubmit", "mute",
            () => Res("exec sleep 300", readiness: TimeSpan.FromMilliseconds(200)));
        var dispatcher = new Dispatcher(reg, TimeSpan.FromSeconds(5));

        await PollUntilAsync(() => Task.FromResult(captured.Events.Any(e =>
                e.Evt == "exec.protocolError" && e.Fields.Msg!.Contains("readiness timeout"))),
            TimeSpan.FromSeconds(10), "readiness timeout trailed");
        var spawn = captured.Events.First(e => e.Evt == "exec.spawn");
        var pid = (int)spawn.Fields.Data!["pid"];
        await PollUntilAsync(() => Task.FromResult(!Alive(pid)),
            TimeSpan.FromSeconds(10), "mute child killed on readiness timeout");

        // Failed ⇒ counted throw ⇒ restart ⇒ fresh spawn attempt.
        Assert.IsType<Effect.Noop>((await dispatcher.DispatchAsync(Ev(), "resp0001")).Merged);
        Assert.Contains(captured.Events, e => e.Evt == "handler.error");
        await PollUntilAsync(() => Task.FromResult(
                captured.Events.Count(e => e.Evt == "exec.spawn") >= 2),
            TimeSpan.FromSeconds(10), "restart respawned a fresh child");

        await dispatcher.DisposeHandlersAsync();
    }

    [Fact]
    public async Task EarlyExit_Failed_ExitCodeInTheThrow()
    {
        using var captured = new CapturedLog();
        var reg = new Registry().On("UserPromptSubmit", "quitter",
            () => Res("echo doomed >&2; exit 7"));
        var dispatcher = new Dispatcher(reg, TimeSpan.FromSeconds(5));

        // Poll for the stderr line specifically — it is emitted only AFTER
        // the drain buffer is populated, so it also proves 'doomed' has
        // landed before the dispatch reads the tail (no scheduling race).
        await PollUntilAsync(() => Task.FromResult(captured.Events.Any(e =>
                e.Evt == "exec.stderr" && e.Fields.Msg!.Contains("doomed"))),
            TimeSpan.FromSeconds(10), "early exit's stderr drained");
        Assert.Contains(captured.Events, e =>
            e.Evt == "exec.exit" && Equals(e.Fields.Data!["code"], 7));

        Assert.IsType<Effect.Noop>((await dispatcher.DispatchAsync(Ev(), "exit0001")).Merged);
        var err = captured.Events.First(e => e.Evt == "handler.error");
        Assert.Contains("exited 7", err.Fields.Msg);
        Assert.Contains("doomed", err.Fields.Msg);   // stderr tail crossed

        await dispatcher.DisposeHandlersAsync();
    }

    [Fact]
    public async Task AnswerBeforeReady_IsAProtocolFailure()
    {
        using var captured = new CapturedLog();
        var reg = new Registry().On("UserPromptSubmit", "eager-beaver",
            () => Res("""printf '{"effect":"noop"}\n'; exec sleep 300"""));
        var dispatcher = new Dispatcher(reg, TimeSpan.FromSeconds(5));

        await PollUntilAsync(() => Task.FromResult(captured.Events.Any(e =>
                e.Evt == "exec.protocolError" && e.Fields.Msg!.Contains("ready handshake"))),
            TimeSpan.FromSeconds(10), "answer-before-ready rejected");
        var pid = (int)captured.Events.First(e => e.Evt == "exec.spawn").Fields.Data!["pid"];
        await PollUntilAsync(() => Task.FromResult(!Alive(pid)),
            TimeSpan.FromSeconds(10), "out-of-protocol child killed");

        await dispatcher.DisposeHandlersAsync();
    }

    // ---- the lock-step conversation ------------------------------------------

    [Fact]
    public async Task MissingEcho_ProtocolError_KillCountedRespawn()
    {
        // ADR d3: the resident answer MUST echo dispatchId — an answer
        // without it can never be attributed and the child is replaced.
        using var captured = new CapturedLog();
        var reg = new Registry().On("UserPromptSubmit", "no-echo",
            () => Res("""echo '{"ready":1}'; while read l; do printf '{"effect":"noop"}\n'; done"""));
        var dispatcher = new Dispatcher(reg, TimeSpan.FromSeconds(5));

        Assert.IsType<Effect.Noop>((await dispatcher.DispatchAsync(Ev(), "echo0001")).Merged);   // fail-open
        var perr = captured.Events.First(e => e.Evt == "exec.protocolError");
        Assert.Contains("missing the mandatory dispatchId echo", perr.Fields.Msg);
        var pid = (int)captured.Events.First(e => e.Evt == "exec.spawn").Fields.Data!["pid"];
        await PollUntilAsync(() => Task.FromResult(!Alive(pid)),
            TimeSpan.FromSeconds(10), "echo-less child killed");
        await PollUntilAsync(() => Task.FromResult(
                captured.Events.Count(e => e.Evt == "exec.spawn") >= 2),
            TimeSpan.FromSeconds(10), "counted crash respawned");

        await dispatcher.DisposeHandlersAsync();
    }

    [Fact]
    public async Task MismatchedEcho_ProtocolError()
    {
        using var captured = new CapturedLog();
        var reg = new Registry().On("UserPromptSubmit", "wrong-echo",
            () => Res("""echo '{"ready":1}'; while read l; do printf '{"effect":"noop","dispatchId":"stale-id"}\n'; done"""));
        var dispatcher = new Dispatcher(reg, TimeSpan.FromSeconds(5));

        Assert.IsType<Effect.Noop>((await dispatcher.DispatchAsync(Ev(), "mism0001")).Merged);
        var perr = captured.Events.First(e => e.Evt == "exec.protocolError");
        Assert.Contains("echo mismatch", perr.Fields.Msg);
        Assert.Contains("stale-id", perr.Fields.Msg);

        await dispatcher.DisposeHandlersAsync();
    }

    [Fact]
    public async Task WedgedConversation_BudgetKills_UncountedRestart_FreshChildServes()
    {
        // Mid-conversation overrun: the child is killed (every timeout path
        // replaces the child — the PRIMARY stale-answer defense) and the OCE
        // stays an UNCOUNTED honored cancel (ADR-0004 d5 carry-in c). The
        // supervised restart still yields a fresh child that serves.
        using var captured = new CapturedLog();
        var flag = Path.Combine(_tmp.Path, "wedged-once");
        // Gen1 (no flag): handshake, then swallow the envelope and wedge.
        // Gen2+ (flag exists): the well-behaved echo server.
        var script =
            """
            if [ ! -e "$1" ]; then : > "$1"; echo '{"ready":1}'; read l; exec sleep 300; fi
            """ + "\n" + EchoServer;
        var reg = new Registry().On("UserPromptSubmit", "wedger",
            () => Res(script, extra: flag), FailMode.Open, TimeSpan.FromMilliseconds(500));
        var dispatcher = new Dispatcher(reg, TimeSpan.FromSeconds(5));

        await PollUntilAsync(() => Task.FromResult(captured.Events.Any(e => e.Evt == "exec.ready")),
            TimeSpan.FromSeconds(10), "gen1 ready");
        var pid1 = (int)captured.Events.First(e => e.Evt == "exec.spawn").Fields.Data!["pid"];

        var r1 = await dispatcher.DispatchAsync(Ev(), "wedge001");
        Assert.IsType<Effect.Noop>(r1.Merged);   // timeout → fail-open
        Assert.Contains(captured.Events, e => e.Evt == "handler.timeout");
        await PollUntilAsync(() => Task.FromResult(!Alive(pid1)),
            TimeSpan.FromSeconds(10), "wedged child killed");
        // Honored cancel: the restart is UNCOUNTED (kind=cancelled).
        await PollUntilAsync(() => Task.FromResult(captured.Events.Any(e =>
                e.Evt == "actor.restart" && Equals(e.Fields.Data!["counted"], false))),
            TimeSpan.FromSeconds(10), "uncounted honored-cancel restart");

        // Gen2 serves warm.
        Effect.Inject? pong = null;
        await PollUntilAsync(async () =>
        {
            var r = await dispatcher.DispatchAsync(Ev(), "wedge002");
            if (r.Merged is Effect.Inject i) { pong = i; return true; }
            return false;
        }, TimeSpan.FromSeconds(15), "replacement child answers");
        Assert.NotEqual(pid1, PongPid(pong!));

        await dispatcher.DisposeHandlersAsync();
    }

    [Fact]
    public async Task DoubleAnswer_LeftoverLineNeverBindsToTheNextDispatch()
    {
        // The stale-attribution backstop, exercised ACROSS dispatches: a
        // child that answers every envelope TWICE leaves its duplicate
        // buffered; the next conversation reads that leftover first — and
        // the mandatory echo (carrying the PREVIOUS dispatchId) refuses it.
        // A wrong-conversation Deny can never bind to an innocent dispatch.
        using var captured = new CapturedLog();
        var script =
            """
            echo '{"ready":1}'
            while read l; do
              id="${l#*\"dispatchId\":\"}"; id="${id%%\"*}"
              printf '{"effect":"noop","dispatchId":"%s"}\n' "$id"
              printf '{"effect":"decide","verdict":"deny","dispatchId":"%s"}\n' "$id"
            done
            """;
        var reg = new Registry().On("UserPromptSubmit", "chatty", () => Res(script));
        var dispatcher = new Dispatcher(reg, TimeSpan.FromSeconds(5));

        // Dispatch 1 consumes the real answer; the duplicate stays buffered.
        Assert.IsType<Effect.Noop>((await dispatcher.DispatchAsync(Ev(), "dup00001")).Merged);

        // Dispatch 2 reads the leftover (echo=dup00001) — mismatch, protocol
        // error, fail-open Noop. NEVER the stray Deny.
        var r2 = await dispatcher.DispatchAsync(Ev(), "dup00002");
        Assert.IsType<Effect.Noop>(r2.Merged);
        var perr = captured.Events.First(e => e.Evt == "exec.protocolError");
        Assert.Contains("dup00001", perr.Fields.Msg);

        await dispatcher.DisposeHandlersAsync();
    }

    [Fact]
    public async Task ChronicProtocolOffender_Escalates_AllChildrenDead_NoSpawnChurn()
    {
        // Repeated protocol errors are COUNTED — under a frozen FakeClock
        // window the supervisor escalates, the worker fast-fails from then
        // on, and no further children are ever spawned (dead worker = no
        // factory reruns). Every generation's child must be dead.
        using var captured = new CapturedLog();
        var clock = new FakeClock();
        var sup = new CaptainHook.Actors.Supervisor("res-esc", maxRestarts: 1, TimeSpan.FromSeconds(5), clock.Now);
        var escalated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sup.OnEscalated = (_, _) => escalated.TrySetResult();

        var reg = new Registry().On("UserPromptSubmit", "offender",
            () => Res("""echo '{"ready":1}'; while read l; do echo garbage; done"""));
        var dispatcher = new Dispatcher(reg, TimeSpan.FromSeconds(5), sup);

        for (var i = 0; i < 20 && !escalated.Task.IsCompleted; i++)
            await dispatcher.DispatchAsync(Ev(), $"esc{i:D5}");
        await escalated.Task.WaitAsync(TimeSpan.FromSeconds(15));

        // Dead worker: fail-fast Noop, and the spawn count stops moving.
        Assert.IsType<Effect.Noop>((await dispatcher.DispatchAsync(Ev(), "escdead1")).Merged);
        Assert.Contains(captured.Events, e => e.Evt == "handler.dead");
        var spawns = captured.Events.Where(e => e.Evt == "exec.spawn")
            .Select(e => (int)e.Fields.Data!["pid"]).ToArray();
        Assert.IsType<Effect.Noop>((await dispatcher.DispatchAsync(Ev(), "escdead2")).Merged);
        Assert.Equal(spawns.Length, captured.Events.Count(e => e.Evt == "exec.spawn"));

        // No generation's child survives: eviction disposed every replaced
        // instance and the escalation hook disposed the last one.
        foreach (var pid in spawns)
            await PollUntilAsync(() => Task.FromResult(!Alive(pid)),
                TimeSpan.FromSeconds(10), $"generation child {pid} dead after escalation");
    }

    /// Records the admission seam's calls so the eager-start plumbing is
    /// pinned as BEHAVIOR (Start-after-admission, predecessor threading) —
    /// not just exercised incidentally by the process tests.
    private sealed class EagerProbe(string tag, System.Collections.Concurrent.ConcurrentQueue<(string Tag, Task? Pred)> starts,
                                    Func<bool> crash) : IHandler, IEagerStart, IAsyncDisposable
    {
        private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Disposed => _disposed.Task;
        public string Name => "eager";
        public FailMode OnFailure => FailMode.Open;
        public void Start(Task? predecessor) => starts.Enqueue((tag, predecessor));
        public Task<Effect> HandleAsync(HookEvent e, HandlerContext ctx) =>
            crash() ? throw new InvalidOperationException($"{tag} crashed")
                    : Task.FromResult<Effect>(new Effect.Inject(tag));
        public ValueTask DisposeAsync() { _disposed.TrySetResult(); return ValueTask.CompletedTask; }
    }

    [Fact]
    public async Task EagerStart_FirstSpawnGetsNullPredecessor_RestartGetsTheEvictedDisposal()
    {
        // The IEagerStart contract, pinned: first admission passes NO
        // predecessor; a restart's fresh instance receives the EVICTED
        // instance's disposal task — awaiting it means the predecessor's
        // teardown ran to completion before a successor would spawn.
        var starts = new System.Collections.Concurrent.ConcurrentQueue<(string Tag, Task? Pred)>();
        var crash = new[] { true };
        var made = 0;
        EagerProbe? gen1 = null;
        var reg = new Registry().On("UserPromptSubmit", "eager", () =>
        {
            var n = Interlocked.Increment(ref made);
            var probe = new EagerProbe($"gen{n}", starts, () => n == 1 && Volatile.Read(ref crash[0]));
            if (n == 1) gen1 = probe;
            return probe;
        });
        var dispatcher = new Dispatcher(reg, TimeSpan.FromSeconds(2));

        Assert.True(starts.TryDequeue(out var first));
        Assert.Equal("gen1", first.Tag);
        Assert.Null(first.Pred);   // nothing evicted on first admission

        await dispatcher.DispatchAsync(Ev(), "eager001");   // gen1 crashes → restart
        (string Tag, Task? Pred) second = default;
        await PollUntilAsync(() => Task.FromResult(starts.TryDequeue(out second)),
            TimeSpan.FromSeconds(10), "restart re-admitted a fresh instance");
        Assert.Equal("gen2", second.Tag);
        Assert.NotNull(second.Pred);   // the evicted gen1's disposal task
        await second.Pred!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(gen1!.Disposed.IsCompleted, "the predecessor task IS gen1's completed teardown");

        await dispatcher.DisposeHandlersAsync();
    }

    // ---- records + teardown ---------------------------------------------------

    [Fact]
    public async Task ChildRecord_WrittenAtSpawn_GoneAtConfirmedGroupDeath()
    {
        using var captured = new CapturedLog();
        var reg = new Registry().On("UserPromptSubmit", "recorded", () => Res(EchoServer));
        var dispatcher = new Dispatcher(reg, TimeSpan.FromSeconds(5));

        await PollUntilAsync(() => Task.FromResult(captured.Events.Any(e => e.Evt == "exec.ready")),
            TimeSpan.FromSeconds(10), "child ready");
        var pid = (int)captured.Events.First(e => e.Evt == "exec.spawn").Fields.Data!["pid"];

        var recordPath = Path.Combine(ChildRecords.Dir, $"child-{pid}.json");
        Assert.True(File.Exists(recordPath), "record written at spawn");
        var rec = JsonSerializer.Deserialize<ChildRecords.Record>(File.ReadAllText(recordPath))!;
        Assert.Equal(pid, rec.Pid);
        Assert.Equal(Environment.ProcessId, rec.DaemonPid);
        Assert.NotEqual(0, rec.StartTime);   // /proc starttime captured — the pid-reuse proof

        await dispatcher.DisposeHandlersAsync();
        Assert.False(Alive(pid), "child dead when the awaited teardown returns");
        await PollUntilAsync(() => Task.FromResult(!File.Exists(recordPath)),
            TimeSpan.FromSeconds(5), "record deleted at confirmed group death");
    }

    [Fact]
    public async Task TornDownDispatcher_CutDispatchRestart_NeverRespawns()
    {
        // The design-panel orphan find, pinned: a drain cuts a mid-conversation
        // resident child → the dispatch's counted crash restarts the worker →
        // the factory runs post-teardown → admission is REFUSED and no child
        // ever spawns again.
        using var captured = new CapturedLog();
        var reg = new Registry().On("UserPromptSubmit", "cut",
            () => Res("""echo '{"ready":1}'; read l; exec sleep 300"""),
            FailMode.Open, TimeSpan.FromSeconds(30));
        var dispatcher = new Dispatcher(reg, TimeSpan.FromSeconds(5));

        await PollUntilAsync(() => Task.FromResult(captured.Events.Any(e => e.Evt == "exec.ready")),
            TimeSpan.FromSeconds(10), "child ready");
        var inFlight = dispatcher.DispatchAsync(Ev(), "cutme001");

        // Cut it: teardown kills the child mid-conversation; the dispatch
        // resolves into its fail mode.
        await dispatcher.DisposeHandlersAsync();
        Assert.IsType<Effect.Noop>((await inFlight.WaitAsync(TimeSpan.FromSeconds(10))).Merged);

        // The counted restart's factory ran post-teardown: give the fault
        // loop a beat via the trail, then assert NO second spawn happened.
        await PollUntilAsync(() => Task.FromResult(captured.Events.Any(e => e.Evt == "handler.error")),
            TimeSpan.FromSeconds(10), "cut dispatch failed loudly");
        await PollUntilAsync(() => Task.FromResult(captured.Events.Any(e =>
                e.Evt == "handler.teardown" && e.Fields.Msg == "post-drain restart")),
            TimeSpan.FromSeconds(10), "post-teardown restart refused admission");
        Assert.Single(captured.Events.ToArray(), e => e.Evt == "exec.spawn");
    }

    [Fact]
    public void RecordSweep_DropsDeadAndReusedPids_KeepsLiveMatching()
    {
        // Hygiene without a doctor: the once-per-process sweep clears records
        // for gone pids and pid-reuse (starttime drift) while leaving a live,
        // matching record — the very orphan evidence doctor-orphans reads.
        Directory.CreateDirectory(ChildRecords.Dir);
        var self = Environment.ProcessId;
        var selfStart = ChildRecords.ProcStartTime(self);

        void Seed(int pid, long start) => File.WriteAllText(
            Path.Combine(ChildRecords.Dir, $"child-{pid}.json"),
            JsonSerializer.Serialize(new ChildRecords.Record(
                pid, start, "/x", "e", "Ev", "resident", 1, DateTimeOffset.UnixEpoch)));

        Seed(999_999, 12345);             // (a) dead pid — no such process
        Seed(1, selfStart + 777);         // (b) pid 1 is alive but starttime drift = reused
        Seed(self, selfStart);            // (c) live + matching = keep
        File.WriteAllText(Path.Combine(ChildRecords.Dir, "child-nope.json"), "{ truncated");

        ChildRecords.SweepForTests();

        Assert.False(File.Exists(Path.Combine(ChildRecords.Dir, "child-999999.json")), "dead pid swept");
        Assert.False(File.Exists(Path.Combine(ChildRecords.Dir, "child-1.json")), "reused pid (starttime drift) swept");
        Assert.False(File.Exists(Path.Combine(ChildRecords.Dir, "child-nope.json")), "unparseable swept");
        Assert.True(File.Exists(Path.Combine(ChildRecords.Dir, $"child-{self}.json")),
            "live, starttime-matching record kept — the orphan evidence");
    }

    // ---- the daemon E2E ---------------------------------------------------------

    [Fact]
    public async Task DaemonE2E_ResidentServesWarmAcrossHooks_DrainKillsAndCleansRecord()
    {
        // The whole slice through the real stack: a handlers.json resident
        // entry, eagerly spawned at daemon warm-up, answers two REAL hook
        // dispatches from the SAME warm child; the drain kills it and the
        // child record goes with it.
        if (ProcessGroup.SetsidPath is null) return;   // xunit 2.x: no dynamic skip
        using var captured = new CapturedLog();
        var handlersPath = Path.Combine(_tmp.Path, "handlers.json");
        File.WriteAllText(handlersPath, JsonSerializer.Serialize(new
        {
            version = 1,
            handlers = new object[]
            {
                new
                {
                    name = "echo-live",
                    command = "/bin/sh",
                    args = new[] { "-c", EchoServer, "sh" },
                    events = new[] { "UserPromptSubmit" },
                    mode = "resident",
                },
            },
        }));

        using var stop = new CancellationTokenSource();
        var daemon = Task.Run(() => CaptainHook.Core.DaemonHost.RunAsync(
            _tmp.Paths, NoHarnessDir(), stop.Token, handlersPath: handlersPath));
        await PollUntilAsync(async () =>
            await CaptainHook.Wire.ShimClient.TryForwardAsync(_tmp.Paths.SocketPath,
                new CaptainHook.Wire.HookRequest("warmup00", "session-start", "claude-code", "{}"u8.ToArray()))
                is CaptainHook.Wire.ForwardOutcome.Answered,
            TimeSpan.FromSeconds(15), "daemon up");

        static string Body(CaptainHook.Wire.ForwardOutcome o) =>
            System.Text.Encoding.UTF8.GetString(
                Assert.IsType<CaptainHook.Wire.ForwardOutcome.Answered>(o).StdoutBytes);
        static bool TryPid(string body, out int pid)
        {
            pid = 0;
            var at = body.IndexOf("pong ", StringComparison.Ordinal);
            if (at < 0) return false;
            var digits = new string(body[(at + 5)..].TakeWhile(char.IsDigit).ToArray());
            return int.TryParse(digits, out pid);
        }

        // First warm dispatch may race a still-warming eager child (the child
        // takes fail-open Noop until ready); poll it to a real pong so the
        // test never flakes on a slow boot under load.
        var pid1 = 0;
        await PollUntilAsync(async () => TryPid(Body(await CaptainHook.Wire.ShimClient.TryForwardAsync(
                _tmp.Paths.SocketPath,
                new CaptainHook.Wire.HookRequest("warm0001", "user-prompt-submit", "claude-code", "{}"u8.ToArray()))),
                out pid1),
            TimeSpan.FromSeconds(15), "warm child answers the first hook");

        Assert.True(TryPid(Body(await CaptainHook.Wire.ShimClient.TryForwardAsync(_tmp.Paths.SocketPath,
            new CaptainHook.Wire.HookRequest("warm0002", "user-prompt-submit", "claude-code", "{}"u8.ToArray()))),
            out var pid2), "second hook answered warm");
        Assert.Equal(pid1, pid2);   // ONE warm child served both hooks
        Assert.True(Alive(pid1));
        var record = Path.Combine(ChildRecords.Dir, $"child-{pid1}.json");
        Assert.True(File.Exists(record), "child record present while serving");

        stop.Cancel();
        Assert.Equal(0, await daemon.WaitAsync(TimeSpan.FromSeconds(15)));
        await PollUntilAsync(() => Task.FromResult(!Alive(pid1)),
            TimeSpan.FromSeconds(5), "resident child died with the daemon");
        await PollUntilAsync(() => Task.FromResult(!File.Exists(record)),
            TimeSpan.FromSeconds(5), "record cleaned at confirmed group death");
        Assert.Contains(captured.Events, e => e.Evt == "daemon.drainChildren");
    }

    // ---- registration gating ---------------------------------------------------

    private static ExecEntry Resident(string name, string[] events, FailMode fm = FailMode.Open,
                                      TimeSpan? budget = null, TimeSpan? readiness = null) =>
        new(name, "/bin/true", [], events, ExecMode.Resident, fm, budget, readiness,
            new Dictionary<string, string>(), [], null);

    [Fact]
    public void DaemonRegistration_MultiEventEntry_FanoutIsLoud()
    {
        using var captured = new CapturedLog();
        var registry = new Registry();
        HookRun.RegisterExecHandlers(registry, new ExecHandlersResolution.Loaded(
            [Resident("multi", ["UserPromptSubmit", "Stop"])], []));   // no collapsedEvent = daemon

        var fan = Assert.Single(captured.Events.ToArray(), e => e.Evt == "handlers.residentFanout");
        Assert.Contains("2 independent children", fan.Fields.Msg);
        // Both events registered — but do NOT construct a Dispatcher here:
        // that would eagerly spawn /bin/true children for nothing.
        Assert.Equal(2, registry.Specs.Count);
    }

    [Fact]
    public void CollapsedRegistration_FiltersToDispatchedEvent_LogsDegrade_NoFanout()
    {
        // The degrade seam (phase 6): a collapsed run passes its ONE event;
        // a multi-event resident entry registers only THAT event's handler
        // (no spurious spawn for the others), logs residentDegraded, and the
        // daemon-only fanout warn stays silent.
        using var captured = new CapturedLog();
        var registry = new Registry();
        HookRun.RegisterExecHandlers(registry, new ExecHandlersResolution.Loaded(
            [Resident("multi", ["UserPromptSubmit", "Stop"])], []),
            collapsedEvent: "UserPromptSubmit");

        Assert.Equal(1, registry.Specs.Count);   // only the dispatched event
        var degrade = Assert.Single(captured.Events.ToArray(), e => e.Evt == "handlers.residentDegraded");
        Assert.Equal("multi", degrade.Fields.Data!["entry"]);
        Assert.DoesNotContain(captured.Events, e => e.Evt == "handlers.residentFanout");
    }

    [Fact]
    public void CollapsedRegistration_UnrelatedEvent_RegistersNothing()
    {
        // A resident gate on PreToolUse must NOT register (or spawn) for a
        // collapsed UserPromptSubmit hook.
        using var captured = new CapturedLog();
        var registry = new Registry();
        HookRun.RegisterExecHandlers(registry, new ExecHandlersResolution.Loaded(
            [Resident("tool-gate", ["PreToolUse"], FailMode.Closed)], []),
            collapsedEvent: "UserPromptSubmit");

        Assert.Empty(registry.Specs);
        Assert.DoesNotContain(captured.Events, e => e.Evt == "handlers.residentDegraded");
    }

    [Fact]
    public void ReadinessBeyondBudget_WarnsAtRegistration()
    {
        using var captured = new CapturedLog();
        var registry = new Registry();
        HookRun.RegisterExecHandlers(registry, new ExecHandlersResolution.Loaded(
            [
                Resident("slow-warm", ["UserPromptSubmit"],
                    budget: TimeSpan.FromSeconds(2), readiness: TimeSpan.FromSeconds(30)),
                Resident("fast-warm", ["Stop"],
                    budget: TimeSpan.FromSeconds(5), readiness: TimeSpan.FromSeconds(1)),
            ], []));

        var warn = Assert.Single(captured.Events.ToArray(), e => e.Evt == "handlers.readinessBeyondBudget");
        Assert.Equal("slow-warm", warn.Fields.Data!["entry"]);
        Assert.Contains("fail mode until the child readies", warn.Fields.Msg);
    }

    [Fact]
    public void ResidentOnPreToolUse_DrawsNoSlowShapeWarn()
    {
        // slowShape is oneshot guidance; resident-on-before-tools IS the
        // recommended shape and must stay silent.
        using var captured = new CapturedLog();
        var registry = new Registry();
        HookRun.RegisterExecHandlers(registry, new ExecHandlersResolution.Loaded(
            [Resident("gate", ["PreToolUse"])], []));

        Assert.DoesNotContain(captured.Events, e => e.Evt == "handlers.slowShape");
    }

    // ---- collapsed-mode degrade E2E (the no-orphan proof) ----------------------

    private async Task<(int Exit, string Stdout, IReadOnlyList<CaptainHook.Actors.LogEvent> Trail)> CollapsedAsync(
        CapturedLog captured, string handlersJson, string eventName = "user-prompt-submit")
    {
        var handlersPath = Path.Combine(_tmp.Path, "handlers.json");
        File.WriteAllText(handlersPath, handlersJson);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await HookRun.CollapsedAsync(
            new CaptainHook.Wire.Invocation(CaptainHook.Wire.Mode.Collapsed, eventName, "claude-code"),
            new StringReader("""{"prompt":"hi"}"""), stdout, stderr,
            harnessDir: NoHarnessDir(), handlersPath: handlersPath);
        return (exit, stdout.ToString(), captured.Events.ToArray());
    }

    private static string HandlersJson(string name, string script, string[] events, string mode = "resident",
                                       string failMode = "open") =>
        JsonSerializer.Serialize(new
        {
            version = 1,
            handlers = new object[]
            {
                new { name, command = "/bin/sh", args = new[] { "-c", script, "sh" }, events, mode, failMode },
            },
        });

    [Fact]
    public async Task CollapsedE2E_ResidentDegrades_ServesOneDispatch_ChildDiesNoOrphan()
    {
        // The degrade end-to-end: no daemon, a resident echo server runs
        // spawn→serve-one→die. The hook is answered AND the child is dead
        // when CollapsedAsync returns — the whole N3-no-orphan promise.
        if (ProcessGroup.SetsidPath is null) return;   // xunit 2.x: no dynamic skip
        using var captured = new CapturedLog();
        var (exit, stdout, trail) = await CollapsedAsync(captured,
            HandlersJson("memo", EchoServer, ["UserPromptSubmit"]));

        Assert.Equal(0, exit);
        JsonDocument.Parse(stdout);   // exactly one JSON object — invariant 1
        Assert.Contains(trail, e => e.Evt == "handlers.residentDegraded");
        Assert.Contains(trail, e => e.Evt == "exec.answered" && Equals(e.Fields.Data!["mode"], "resident"));

        var pid = (int)trail.First(e => e.Evt == "exec.spawn").Fields.Data!["pid"];
        Assert.False(Alive(pid), "the degraded resident child must be dead when the collapsed hook returns");
    }

    [Fact]
    public async Task CollapsedE2E_FailClosedResidentGate_RunsForReal_NoOrphan()
    {
        // The old interim was a DENY stub; the real degrade RUNS the gate. A
        // fail-closed resident PreToolUse child that readies and allows must
        // produce that verdict (not a blanket stub-deny) — and still die
        // cleanly.
        if (ProcessGroup.SetsidPath is null) return;   // xunit 2.x: no dynamic skip
        using var captured = new CapturedLog();
        var gate =
            """
            echo '{"ready":1}'
            while read l; do
              id="${l#*\"dispatchId\":\"}"; id="${id%%\"*}"
              printf '{"effect":"decide","verdict":"allow","dispatchId":"%s"}\n' "$id"
            done
            """;
        var (exit, stdout, trail) = await CollapsedAsync(captured,
            HandlersJson("tool-gate", gate, ["PreToolUse"], failMode: "closed"),
            eventName: "pre-tool-use");

        Assert.Equal(0, exit);
        Assert.Contains(trail, e => e.Evt == "handlers.residentDegraded");
        // The real gate ran and ALLOWED — proving the degrade executes the
        // user's logic rather than blanket-denying.
        Assert.Contains(trail, e => e.Evt == "exec.answered");
        var pid = (int)trail.First(e => e.Evt == "exec.spawn").Fields.Data!["pid"];
        Assert.False(Alive(pid), "fail-closed resident gate child dead after the collapsed hook");
    }
}
