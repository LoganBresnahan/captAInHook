using System.Text.Json;
using CaptainHook.Actors;
using CaptainHook.Core;
using CaptainHook.Mail;
using static CaptainHook.Tests.TestUtil;

namespace CaptainHook.Tests;

// ADR-0017 decision 4, slice `watcher-actor` — the in-daemon watcher: the
// supervised actor that joins the pure brain to a schedule that is not a
// timer. What this file pins, in the order the plan's verify names it:
//
//   * a deadline fires with NO timer and NO wall clock — the pump holds one
//     monotonic number and a FakeClock advance is what makes it due;
//   * the self-feeding loop is closed by the gate: the actor's own rows and a
//     payload's stderr quoting the event names are not triggers; only a real
//     `mail.append` / `mail.cursorAdvance` row is;
//   * persist THEN dispatch: by the time the turn's handler runs, the state
//     on disk already shows the charge — a crash between the two costs one
//     poke, never doubles one; a denial charges nothing but restarts quiet;
//   * idle-exit (N2): a daemon with a watcher and an armed deadline still
//     idle-exits; a turn it woke is activity for as long as it runs;
//   * supervision: a throwing evaluation is restarted, the fresh instance
//     RELOADS the persisted state, and the pump re-evaluates at once;
//   * on start, a deadline that had fallen while the daemon slept is due.
//
// Nothing here sleeps through a threshold: `StepAsync` is driven by hand with
// a FakeClock, and the two pump tests use a tight poll with time-bounded
// asserts (`PollUntilAsync`).
public class MailWatcherTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(2);

    // ---- the deadline: one number, no timer --------------------------------------

    /// A fresh envelope is not due; the brain arms the quiet threshold; the
    /// pump would hold it. Advance the fake clock past it and the next step —
    /// nothing else has changed — raises the nudge. No `Task.Delay`, no
    /// `DateTime`: the deadline fired because the CLOCK said so.
    [Fact]
    public async Task ADeadline_FiresWhenTheMonotonicClockPassesIt_NotOnATimer()
    {
        using var w = new World();
        using var log = new CapturedLog();
        w.Rules(Rule("reviewer", quietFor: "10min"));
        w.Register(TurnPayload("turn-claude"));
        w.Send("m-01", "reviewer", MailPriority.Urgent);
        var seen = new List<HookEvent>();
        await using var watcher = w.Watcher(Inspecting("turn", seen.Add));

        var first = await watcher.StepAsync(WatchTrigger.Start);
        Assert.NotNull(first);
        Assert.Equal(0, first!.Nudges);
        Assert.Equal(w.Clock.Now() + 10 * 60_000, first.NextCheckMs);   // armed for the threshold
        Assert.Empty(seen);

        w.Clock.Advance(TimeSpan.FromMinutes(9));
        var early = await watcher.StepAsync(WatchTrigger.Deadline);   // a spurious re-check: still not due
        Assert.Equal(0, early!.Nudges);

        w.Clock.Advance(TimeSpan.FromMinutes(1));
        var due = await watcher.StepAsync(WatchTrigger.Deadline);
        Assert.Equal(1, due!.Nudges);
        Assert.Equal(1, due.Admitted);
        await w.SettleAsync(watcher);
        var evt = Assert.Single(seen);
        Assert.Equal(MailNudgeEvent.EventType, evt.Type);
        Assert.Equal("reviewer", evt.Payload.GetProperty("role").GetString());
        Assert.Contains(log.Events, e => e.Evt == "mail.nudge");
        Assert.Contains(log.Events, e => e.Evt == "nudge.dispatch");
    }

    /// The pump itself, against a real trail file: append the mail to the
    /// store, then the `mail.append` row to the trail — the pump evaluates on
    /// the row and the nudge goes. Then a `mail.cursorAdvance` row after the
    /// role reads it, and the next evaluation finds nothing unread.
    [Fact]
    public async Task ThePump_EvaluatesOnTrailRows_AndArmsNothingOnceRead()
    {
        using var w = new World();
        using var log = new CapturedLog();
        w.Rules(Rule("reviewer", quietFor: "0s"));
        w.Register(TurnPayload("turn-claude"));
        var seen = 0;
        await using var watcher = w.Watcher(Counting("turn", () => Interlocked.Increment(ref seen)),
            poll: TimeSpan.FromMilliseconds(20));
        watcher.Start();

        w.Send("m-01", "reviewer", MailPriority.Urgent);
        w.Trail("""{"ts":"2026-08-18T00:00:00.000Z","lvl":"info","src":"mail","evt":"mail.append","data":{"id":"m-01","to":"reviewer"}}""");
        await PollUntilAsync(() => Task.FromResult(Volatile.Read(ref seen) == 1),
            TimeSpan.FromSeconds(10), "the pump raised the nudge off the trail row");
        Assert.Contains(log.Events, e => e.Evt == "watch.evaluate" && Str(e, "trigger") == "Trail");

        // The role reads it (a real digest moves a real cursor); the row lands
        // on the trail; the pump re-evaluates and finds the mailbox caught up.
        w.Digest("reviewer", "s-1");
        w.Trail("""{"ts":"2026-08-18T00:00:01.000Z","lvl":"debug","src":"mail","evt":"mail.cursorAdvance","sessionId":"s-1","data":{"role":"reviewer"}}""");
        await PollUntilAsync(() => Task.FromResult(
                log.Events.Count(e => e.Evt == "watch.evaluate" && Str(e, "trigger") == "Trail") >= 2),
            TimeSpan.FromSeconds(10), "the pump re-evaluated on the cursor advance");
        Assert.Null(watcher.Armed);           // nothing unread ⇒ nothing armed
        Assert.Equal(1, Volatile.Read(ref seen));   // and no second poke
    }

    // ---- the gate: the actor's own output cannot re-trigger it ---------------------

    /// The two rows the watcher evaluates on, and everything it must ignore:
    /// its own `watch.*` and `mail.nudge` rows, the woken turn's dispatch rows,
    /// an `exec.stderr` row that QUOTES the event name (a payload's pretty
    /// stderr does exactly this on the live trail), a `msg` mentioning it, and
    /// garbage. The substring is a filter; the parsed `evt` is the decision.
    [Fact]
    public void TheGate_AdmitsOnlyTheTwoEvents_ByParsedEvtNotBySubstring()
    {
        Assert.True(MailWatcher.IsTrigger("""{"evt":"mail.append","data":{}}"""));
        Assert.True(MailWatcher.IsTrigger("""{"ts":"x","evt":"mail.cursorAdvance"}"""));

        Assert.False(MailWatcher.IsTrigger("""{"evt":"mail.nudge","data":{"role":"reviewer"}}"""));
        Assert.False(MailWatcher.IsTrigger("""{"evt":"watch.evaluate","data":{"trigger":"Trail"}}"""));
        Assert.False(MailWatcher.IsTrigger("""{"evt":"nudge.dispatch"}"""));
        Assert.False(MailWatcher.IsTrigger("""{"evt":"dispatch.start","hookEvent":"MailNudge"}"""));
        Assert.False(MailWatcher.IsTrigger("""{"evt":"mail.deliver","data":{"envelopeIds":["a"]}}"""));
        // The live-trail shape: an exec child's stderr, quoting the pretty rendering.
        Assert.False(MailWatcher.IsTrigger("""{"evt":"exec.stderr","msg":"04:30 DEBUG [mail] \"mail.cursorAdvance\" role=maintainer"}"""));
        Assert.False(MailWatcher.IsTrigger("""{"evt":"exec.stderr","data":{"line":"{\"evt\":\"mail.append\"}"}}"""));
        Assert.False(MailWatcher.IsTrigger("""["mail.append"]"""));
        Assert.False(MailWatcher.IsTrigger("""{"evt":"mail.append" """));   // torn
        Assert.False(MailWatcher.IsTrigger(""));
        Assert.False(MailWatcher.IsTrigger("not json at all mail.append"));
    }

    /// The whole loop, end to end through the pump: after a nudge is raised
    /// the trail carries `mail.nudge`, `nudge.dispatch`, `dispatch.*` rows —
    /// none of which wakes the actor again. One evaluation for the row that
    /// mattered, then quiet.
    [Fact]
    public async Task TheActorsOwnRows_DoNotReTriggerIt()
    {
        using var w = new World();
        using var log = new CapturedLog();
        w.Rules(Rule("reviewer", quietFor: "0s"));
        w.Register(TurnPayload("turn-claude"));
        var seen = 0;
        await using var watcher = w.Watcher(Counting("turn", () => Interlocked.Increment(ref seen)),
            poll: TimeSpan.FromMilliseconds(20));
        watcher.Start();

        w.Send("m-01", "reviewer", MailPriority.Urgent);
        w.Trail("""{"evt":"mail.append","data":{"id":"m-01"}}""");
        await PollUntilAsync(() => Task.FromResult(Volatile.Read(ref seen) == 1),
            TimeSpan.FromSeconds(10), "nudge raised");
        await w.SettleAsync(watcher);

        // Everything the actor and the turn wrote, replayed onto the trail the
        // pump tails — the JSONL rendering of the very rows it just emitted.
        foreach (var e in log.Events.Where(e => e.Evt is "mail.nudge" or "nudge.dispatch" or "watch.evaluate"
                                                 || e.Evt.StartsWith("dispatch.")).ToList())
            w.Trail(e.ToJson());
        w.Trail("""{"evt":"exec.stderr","msg":"mail.append id=m-02"}""");

        // Deterministic negative: five poll intervals with the row count frozen.
        var before = log.Events.Count(e => e.Evt == "watch.evaluate");
        await Task.Delay(150);
        Assert.Equal(before, log.Events.Count(e => e.Evt == "watch.evaluate"));
        Assert.Equal(1, Volatile.Read(ref seen));
    }

    // ---- persist THEN dispatch ------------------------------------------------------

    /// By the time the turn's handler RUNS, `nudges.jsonl` already shows the
    /// envelope nudged once and the role's window spent one — the handler
    /// reads the file itself and finds it so. The `mail.nudge` row precedes
    /// `nudge.dispatch`. A crash between the two would leave a charged nudge
    /// and no turn — never a turn and no charge.
    [Fact]
    public async Task TheStateIsOnDisk_BeforeTheTurnRuns()
    {
        using var w = new World();
        using var log = new CapturedLog();
        w.Rules(Rule("reviewer", quietFor: "0s"));
        w.Register(TurnPayload("turn-claude"));
        w.Send("m-01", "reviewer", MailPriority.Urgent);
        NudgeState? atRun = null;
        await using var watcher = w.Watcher(new TestHandler("turn", (_, _) =>
        {
            atRun = new NudgeStore(w.MailDir).Load(w.Clock.Now());
            return Task.FromResult<Effect>(new Effect.Noop());
        }));

        var step = await watcher.StepAsync(WatchTrigger.Start);
        Assert.Equal(1, step!.Admitted);
        Assert.True(step.StateSaved);
        await w.SettleAsync(watcher);

        Assert.NotNull(atRun);
        var e = Assert.Single(atRun!.Envelopes);
        Assert.Equal(("reviewer", "m-01", 1), (e.Subject, e.Id, e.Nudged));
        Assert.Single(atRun.Nudges);

        var evts = log.Events.Select(x => x.Evt).ToList();
        Assert.True(evts.IndexOf("mail.nudge") < evts.IndexOf("nudge.dispatch"), string.Join(",", evts));
        var nudgeRow = Assert.Single(log.Events, x => x.Evt == "mail.nudge");
        var dispatchRow = Assert.Single(log.Events, x => x.Evt == "nudge.dispatch");
        Assert.Equal(nudgeRow.Fields.DispatchId, dispatchRow.Fields.DispatchId);   // one id joins them
    }

    /// A policy denial: no turn, no `mail.nudge`, no charge — but the quiet
    /// clock restarts, so the very next evaluation does not ask again; it is
    /// armed for one quiet period later.
    [Fact]
    public async Task ADeniedNudge_ChargesNothing_AndIsNotRetriedUntilQuietAgain()
    {
        using var w = new World();
        using var log = new CapturedLog();
        w.Rules(Rule("reviewer", quietFor: "5min"));
        w.Register(TurnPayload("turn-claude"));
        w.Send("m-01", "reviewer", MailPriority.Urgent);
        var ran = 0;
        w.Remember(("reviewer", "m-01", QuietForMs: 5 * 60_000, Nudged: 0));   // already past quiet
        await using var fresh = w.Watcher(Counting("turn", () => ran++),
            policy: Policy("""{ "version": 1, "default": "deny" }"""));

        var step = await fresh.StepAsync(WatchTrigger.Start);
        Assert.Equal(1, step!.Nudges);
        Assert.Equal(0, step.Admitted);
        Assert.Equal(w.Clock.Now() + 5 * 60_000, step.NextCheckMs);   // quiet restarted, uncharged
        Assert.Single(log.Events, e => e.Evt == "nudge.denied");
        Assert.DoesNotContain(log.Events, e => e.Evt == "mail.nudge");
        Assert.Equal(0, ran);

        var again = await fresh.StepAsync(WatchTrigger.Deadline);   // spurious: quiet has not re-elapsed
        Assert.Equal(0, again!.Nudges);

        var onDisk = new NudgeStore(w.MailDir).Load(w.Clock.Now());
        Assert.Equal(0, Assert.Single(onDisk.Envelopes).Nudged);
        Assert.Empty(onDisk.Nudges);
    }

    // ---- start: a fallen deadline is due at once -----------------------------------

    /// The state that crossed the restart says the envelope had been quiet
    /// past its threshold when the last daemon exited. The first evaluation
    /// honors it: the nudge goes on `Start`.
    [Fact]
    public async Task OnStart_ADeadlineThatFellWhileTheDaemonSlept_IsDueAtOnce()
    {
        using var w = new World();
        w.Rules(Rule("reviewer", quietFor: "10min"));
        w.Register(TurnPayload("turn-claude"));
        w.Send("m-01", "reviewer", MailPriority.Urgent);
        w.Remember(("reviewer", "m-01", QuietForMs: 11 * 60_000, Nudged: 0));
        var ran = 0;
        await using var watcher = w.Watcher(Counting("turn", () => Interlocked.Increment(ref ran)));

        var step = await watcher.StepAsync(WatchTrigger.Start);
        Assert.Equal(1, step!.Admitted);
        await w.SettleAsync(watcher);
        Assert.Equal(1, Volatile.Read(ref ran));
    }

    // ---- supervision: restart = reload -----------------------------------------------

    /// The evaluation throws once. The supervisor restarts the actor; the
    /// fresh instance starts from this process's last evaluated state (the
    /// first sighting, six minutes ago), so the quiet clock is NOT reset by the
    /// crash — as first written the fresh instance re-read the FILE, whose
    /// last save was that first sighting re-derived as age 0, and the restart
    /// silently pushed the deadline out by a full quiet period; the pump
    /// re-evaluates as `Restart`. Then the deadline fires as it would have.
    [Fact]
    public async Task AThrowingEvaluation_IsRestartedFromThePersistedState()
    {
        using var w = new World();
        using var log = new CapturedLog();
        w.Rules(Rule("reviewer", quietFor: "10min"));
        w.Register(TurnPayload("turn-claude"));
        w.Send("m-01", "reviewer", MailPriority.Urgent);
        var ran = 0;
        await using var watcher = w.Watcher(Counting("turn", () => Interlocked.Increment(ref ran)));

        var first = await watcher.StepAsync(WatchTrigger.Start);   // persists first-seen = now
        Assert.True(first!.StateSaved);
        var gen = watcher.Generation;

        w.Clock.Advance(TimeSpan.FromMinutes(6));
        var blowUp = true;
        watcher.BeforeEvaluate = _ => { if (blowUp) { blowUp = false; throw new InvalidOperationException("boom"); } };
        Assert.Null(await watcher.StepAsync(WatchTrigger.Trail));
        Assert.Contains(log.Events, e => e.Evt == "watch.evaluateFailed");
        await PollUntilAsync(() => Task.FromResult(watcher.Generation > gen), TimeSpan.FromSeconds(5), "restarted");
        Assert.False(watcher.IsDead);

        // The fresh instance: reloaded, so the envelope has been quiet 6 min
        // already — the deadline is 4 min out, not 10.
        var after = await watcher.StepAsync(WatchTrigger.Restart);
        Assert.NotNull(after);
        Assert.Equal(w.Clock.Now() + 4 * 60_000, after!.NextCheckMs);

        w.Clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Equal(1, (await watcher.StepAsync(WatchTrigger.Deadline))!.Admitted);
        await w.SettleAsync(watcher);
        Assert.Equal(1, Volatile.Read(ref ran));
    }

    /// Past the restart window the supervisor escalates: the pump says so
    /// once and stops; the handle is dead; nothing else in the daemon changes.
    [Fact]
    public async Task ChronicCrashes_EscalateToDead_AndTheWatcherSaysSoOnce()
    {
        using var w = new World();
        using var log = new CapturedLog();
        w.Rules(Rule("reviewer", quietFor: "0s"));
        await using var watcher = w.Watcher(TestHandler.Returning("turn", new Effect.Noop()));
        watcher.BeforeEvaluate = _ => throw new InvalidOperationException("always");

        for (var i = 0; i < 6 && !watcher.IsDead; i++)
        {
            Assert.Null(await watcher.StepAsync(WatchTrigger.Trail));
            await Task.Delay(20);   // let the supervisor's fault loop turn
        }
        await PollUntilAsync(() => Task.FromResult(watcher.IsDead), TimeSpan.FromSeconds(5), "escalated");
        Assert.Null(await watcher.StepAsync(WatchTrigger.Trail));
        Assert.Contains(log.Events, e => e.Evt == "watch.dead");
        Assert.Contains(log.Events, e => e.Evt == "actor.escalate");
    }

    // ---- what the watcher does not do -----------------------------------------------

    /// No rules ⇒ no roles ⇒ nothing armed, nothing saved, no read of the
    /// handlers file needed. The pump would tick forever and never touch the
    /// state file. (The daemon-level N2 test is in IdleExitTests.)
    [Fact]
    public async Task NoRules_ArmsNothing_WritesNothing()
    {
        using var w = new World();
        w.Send("m-01", "reviewer", MailPriority.Urgent);
        await using var watcher = w.Watcher(TestHandler.Returning("turn", new Effect.Noop()));
        var step = await watcher.StepAsync(WatchTrigger.Start);
        Assert.Equal(0, step!.Nudges);
        Assert.Null(step.NextCheckMs);
        Assert.False(step.StateSaved);
        Assert.False(File.Exists(Path.Combine(w.MailDir, NudgeStore.FileName)));
    }

    /// The state file is not rewritten when nothing changed: two evaluations
    /// of one quiet bus append one line, not two.
    [Fact]
    public async Task AnUnchangedState_IsNotRewritten()
    {
        using var w = new World();
        w.Rules(Rule("reviewer", quietFor: "10min"));
        w.Register(TurnPayload("turn-claude"));
        w.Send("m-01", "reviewer", MailPriority.Urgent);
        await using var watcher = w.Watcher(TestHandler.Returning("turn", new Effect.Noop()));

        Assert.True((await watcher.StepAsync(WatchTrigger.Start))!.StateSaved);
        w.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.False((await watcher.StepAsync(WatchTrigger.Trail))!.StateSaved);
        Assert.Single(File.ReadAllLines(Path.Combine(w.MailDir, NudgeStore.FileName)).Where(l => l.Length > 0));
    }

    /// The turn payload's registration in `handlers.json` decides role kind
    /// (robot-servable or not); a change to that file is seen on the next
    /// evaluation without a restart — the stat-gate, not a cached parse.
    [Fact]
    public async Task ARegistrationAddedLater_IsSeenOnTheNextEvaluation()
    {
        using var w = new World();
        w.Rules(Rule("reviewer", quietFor: "0s"));
        w.Send("m-01", "reviewer", MailPriority.Urgent);
        var ran = 0;
        await using var watcher = w.Watcher(Counting("turn", () => Interlocked.Increment(ref ran)));

        Assert.Equal(0, (await watcher.StepAsync(WatchTrigger.Start))!.Nudges);   // unserved: no payload
        w.Register(TurnPayload("turn-claude"));
        w.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, (await watcher.StepAsync(WatchTrigger.Trail))!.Admitted);
        await w.SettleAsync(watcher);
        Assert.Equal(1, Volatile.Read(ref ran));
    }

    // ---- fixtures ---------------------------------------------------------------------

    private static string? Str(LogEvent e, string key) =>
        e.Fields.Data is { } d && d.TryGetValue(key, out var v) ? v?.ToString() : null;

    private static object Rule(string role, string priority = ">=urgent", string quietFor = "10min",
        int perEnvelope = 1, int perRoleHour = 4) => new
    {
        role,
        when = new { priority, quietFor },
        budget = new { perEnvelope, perRoleHour },
    };

    private static object TurnPayload(string name) => new
    {
        name,
        command = "/usr/bin/turn-claude.sh",
        args = Array.Empty<string>(),
        events = new[] { "mail-nudge" },
        mode = "oneshot",
        failMode = "open",
    };

    private static TestHandler Counting(string name, Action bump) =>
        new(name, (_, _) => { bump(); return Task.FromResult<Effect>(new Effect.Noop()); });

    private static TestHandler Inspecting(string name, Action<HookEvent> capture) =>
        new(name, (e, _) => { capture(e); return Task.FromResult<Effect>(new Effect.Noop()); });

    private static PolicyResolution Policy(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var policy = DispatchPolicy.TryParse(doc.RootElement, out var errors);
        Assert.True(policy is not null, string.Join("; ", errors));
        return new PolicyResolution.Loaded(policy!);
    }

    /// A sandboxed bus: its own mail dir, watch.json, handlers.json and trail
    /// file, a FakeClock, and a dispatcher whose only handler is the test's
    /// stand-in for the turn payload. Nothing under ~/.captainHook is touched.
    private sealed class World : IDisposable
    {
        public string Home { get; } = Path.Combine(Path.GetTempPath(), "chk-watcher-" + Guid.NewGuid().ToString("N")[..8]);
        public string MailDir => Path.Combine(Home, "mail");
        public string TrailPath => Path.Combine(Home, "trail.jsonl");
        private string HandlersPath => Path.Combine(Home, "handlers.json");
        private string WatchPath => Path.Combine(Home, "watch.json");
        public FakeClock Clock { get; } = new();
        public SessionPresence Presence { get; }

        private static readonly string NoOverrides =
            Path.Combine(Path.GetTempPath(), "captainhook-no-harness-overrides");

        public World()
        {
            Directory.CreateDirectory(Home);
            Presence = new SessionPresence(Clock.Now);
        }

        public void Register(params object[] handlers) =>
            File.WriteAllText(HandlersPath, JsonSerializer.Serialize(new { version = 1, handlers }));

        public void Rules(params object[] rules) =>
            File.WriteAllText(WatchPath, JsonSerializer.Serialize(new { version = 1, rules }));

        public void Send(string id, string to, MailPriority priority = MailPriority.Ambient) =>
            MailFixtures.AppendOk(new MailStore(MailDir),
                MailFixtures.Envelope(id: id, to: to, priority: priority, ttl: to.Contains('@') ? null : 3));

        public void Digest(string role, string? session)
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exit = MailDigest.Run(["--role", role, "--seam", "ambient"],
                new StringReader(DigestFixtures.Request("d-1", "UserPromptSubmit", session)),
                stdout, stderr, mailDir: MailDir, harnessDir: NoHarnessDir());
            Assert.True(exit == 0, $"digest exited {exit}: {stderr}");
        }

        /// Append one row to the trail the pump tails.
        public void Trail(string line) => File.AppendAllText(TrailPath, line + "\n");

        /// The brain's memory as a previous daemon would have left it, written
        /// at the clock's now so the ages re-derive exactly.
        public void Remember(params (string Subject, string Id, long QuietForMs, int Nudged)[] entries) =>
            new NudgeStore(MailDir).Save(
                new NudgeState(
                    entries.Select(e => new WatchedEnvelope(
                        e.Subject, e.Id, Clock.Now() - e.QuietForMs, Clock.Now() - e.QuietForMs, e.Nudged)).ToList(),
                    []),
                Clock.Now());

        public MailWatcher Watcher(IHandler turn, PolicyResolution? policy = null, TimeSpan? poll = null)
        {
            var dispatcher = new Dispatcher(new Registry().On(MailNudgeEvent.EventType, turn), Budget);
            var spec = new HarnessRegistry(NoOverrides).Get(MailNudgeEvent.HarnessName);
            return new MailWatcher(new MailWatcherOptions(
                MailDir, WatchPath, HandlersPath, TrailPath, dispatcher, () => spec,
                () => policy ?? new PolicyResolution.Absent(), Presence, Clock.Now,
                Poll: poll,
                Supervisor: new Supervisor("watcher-test", 3, TimeSpan.FromMinutes(1), Clock.Now)));
        }

        /// Wait for every fire-and-forget dispatch the watcher started.
        public Task SettleAsync(MailWatcher w) =>
            PollUntilAsync(() => Task.FromResult(w.InFlight == 0), TimeSpan.FromSeconds(10), "nudge dispatches settle");

        public void Dispose()
        {
            try { Directory.Delete(Home, recursive: true); } catch { /* best-effort */ }
        }
    }
}
