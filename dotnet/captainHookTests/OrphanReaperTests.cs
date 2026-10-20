using System.Diagnostics;
using System.Text.Json;
using CaptainHook.Core;
using CaptainHook.Handlers;
using static CaptainHook.Tests.TestUtil;

namespace CaptainHook.Tests;

// The boot-time orphan reap, the group-aware sweep and the oneshot spawn
// ledger (ADR-0010, 2026-10-19 amendment). Measured on Linux and macOS
// against v0.1.1: after a SIGKILLed daemon, a resident that ignores stdin EOF,
// a resident's forked grandchild and an in-flight oneshot all survived, and
// nothing ever killed them. The decision table is pinned with fake probes;
// the kills, with real process groups — made by perl's setpgrp, because the
// spawn prefix a test tree resolves (setsid) does not exist on macOS CI.
public class OrphanReaperTests : IDisposable
{
    private readonly string _dir = Path.Combine("/tmp", "chk-reap-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<Process> _procs = new();
    private readonly List<int> _groups = new();

    public OrphanReaperTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var g in _groups) KillGroup(g);
        foreach (var p in _procs) { try { p.Kill(true); } catch { /* best-effort */ } p.Dispose(); }
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private static Task Until(Func<bool> cond, TimeSpan timeout, string what) =>
        PollUntilAsync(() => Task.FromResult(cond()), timeout, what);

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "kill")]
    private static extern int sys_kill(int pid, int sig);
    private static void KillGroup(int pgid) => sys_kill(-pgid, 9);
    private static bool GroupAlive(int pgid) => sys_kill(-pgid, 0) == 0;

    private static ChildRecords.Record Rec(int pid, long start, int daemonPid = 999_999, long daemonStart = 0,
                                           string mode = "resident", string entry = "e") =>
        new(pid, start, "/x", entry, "UserPromptSubmit", mode, daemonPid, DateTimeOffset.UnixEpoch, daemonStart);

    private string Write(ChildRecords.Record r)
    {
        var path = Path.Combine(_dir, $"child-{r.Pid}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(r));
        return path;
    }

    /// A real process group led by a fresh process (pgid == its pid), with
    /// one more member forked into it. `leaderDies` SIGKILLs the leader alone,
    /// leaving the member — the forker-grandchild shape.
    private async Task<(int Leader, int Member, long LeaderStart)> StartGroupAsync(bool leaderDies)
    {
        var pids = Path.Combine(_dir, "g-" + Guid.NewGuid().ToString("N")[..6]);
        var perl = File.Exists("/usr/bin/perl") ? "/usr/bin/perl" : "perl";
        var psi = new ProcessStartInfo(perl) { UseShellExecute = false };
        foreach (var a in new[] { "-e", "setpgrp(0,0); exec @ARGV or die", "/bin/sh", "-c",
                                  $"sleep 300 </dev/null >/dev/null 2>&1 & echo $! > '{pids}.m'; echo $$ > '{pids}.l'; exec sleep 300" })
            psi.ArgumentList.Add(a);
        var proc = Process.Start(psi)!;
        _procs.Add(proc);
        _groups.Add(proc.Id);
        await Until(() => File.Exists(pids + ".m") && File.Exists(pids + ".l")
                                   && File.ReadAllText(pids + ".m").Trim().Length > 0,
                             TimeSpan.FromSeconds(10), "group up");
        var leader = int.Parse(File.ReadAllText(pids + ".l").Trim());
        var member = int.Parse(File.ReadAllText(pids + ".m").Trim());
        Assert.Equal(proc.Id, leader);
        Assert.True(GroupAlive(leader), "the leader leads its own group");
        var start = ChildRecords.ProcStartTime(leader);
        if (leaderDies)
        {
            proc.Kill();   // the leader alone: the member stays in its group
            await proc.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await Until(() => sys_kill(leader, 0) != 0, TimeSpan.FromSeconds(10), "leader reaped");
            Assert.True(GroupAlive(leader), "the member keeps the group alive");
        }
        return (leader, member, start);
    }

    // ---- the decision table -------------------------------------------------

    private static ChildRecords.Probes Fake(bool pidAlive, bool groupAlive = false, long start = 100) =>
        new(_ => pidAlive, _ => groupAlive, _ => start);

    [Theory]
    [InlineData(false, false, 100, 100, "Gone")]
    [InlineData(false, true, 100, 100, "GroupSurvives")]
    [InlineData(true, true, 100, 100, "Alive")]
    [InlineData(true, true, 100, 555, "Reused")]
    [InlineData(true, false, 0, 555, "Unproven")]
    public void Classify_TheFiveStates(bool pidAlive, bool groupAlive, long recorded, long now, string want) =>
        Assert.Equal(want, ChildRecords.Classify(42, recorded, Fake(pidAlive, groupAlive, now)).ToString());

    [Fact]
    public void Stale_IsOnlyAnEmptyGroupOrAReusedPid()
    {
        // A dead leader whose group lives is NOT stale: that record is the
        // only evidence of the grandchild (v0.1.1 swept it).
        Assert.True(ChildRecords.IsStale(ChildRecords.ChildState.Gone));
        Assert.True(ChildRecords.IsStale(ChildRecords.ChildState.Reused));
        Assert.False(ChildRecords.IsStale(ChildRecords.ChildState.GroupSurvives));
        Assert.False(ChildRecords.IsStale(ChildRecords.ChildState.Alive));
        Assert.False(ChildRecords.IsStale(ChildRecords.ChildState.Unproven));
    }

    [Fact]
    public void OwnerAlive_ProvesTheOwnerByStartTime()
    {
        Assert.True(ChildRecords.OwnerAlive(Rec(1, 1, daemonPid: 7, daemonStart: 100), Fake(true, start: 100)));
        Assert.False(ChildRecords.OwnerAlive(Rec(1, 1, daemonPid: 7, daemonStart: 100), Fake(true, start: 101)),
            "a recycled daemon pid is not the owner");
        Assert.False(ChildRecords.OwnerAlive(Rec(1, 1, daemonPid: 7, daemonStart: 100), Fake(false)));
        // A pre-0.1.2 record (no owner start time) naming THIS pid: a boot reap
        // runs before its daemon spawns, so it can only be a dead predecessor's.
        Assert.False(ChildRecords.OwnerAlive(Rec(1, 1, daemonPid: Environment.ProcessId), Fake(true)));
    }

    [Fact]
    public void Plan_DeadOwner_TargetsTheLiveAndTheSurvivingGroups_DeletesTheStale()
    {
        var alive = Write(Rec(10, 100));
        var survives = Write(Rec(11, 100));
        var reused = Write(Rec(12, 100));
        var gone = Write(Rec(13, 100));
        var unproven = Write(Rec(14, 0));
        File.WriteAllText(Path.Combine(_dir, "child-99.json"), "{ torn");
        var probes = new ChildRecords.Probes(
            pid => pid is 10 or 12 or 14,
            pid => pid is 11,
            pid => pid == 12 ? 555 : 100);

        var targets = OrphanReaper.Plan(_dir, probes, ownerAlive: _ => false);

        Assert.Equal(new[] { (10, OrphanReaper.Why.Alive), (11, OrphanReaper.Why.GroupSurvives) },
                     targets.Select(t => (t.Rec.Pid, t.Why)).ToArray());
        Assert.True(File.Exists(alive) && File.Exists(survives), "targets keep their records until killed");
        Assert.False(File.Exists(reused), "pid reused: the record goes, nobody is signaled");
        Assert.False(File.Exists(gone));
        Assert.True(File.Exists(unproven), "no start time, no proof: kept for doctor, never a target");
        Assert.False(File.Exists(Path.Combine(_dir, "child-99.json")));
    }

    [Fact]
    public void Plan_LiveOwner_TargetsNothing()
    {
        // Another daemon's children (a deploy handoff overlaps two), or a
        // collapsed run's degraded residents: their owner kills its own.
        var path = Write(Rec(10, 100));
        Assert.Empty(OrphanReaper.Plan(_dir, Fake(true, start: 100), ownerAlive: _ => true));
        Assert.True(File.Exists(path));
    }

    // ---- the oneshot ledger -------------------------------------------------

    private static void Ledger(string path, params string[] lines) =>
        File.WriteAllLines(path, lines);

    private static string Spawn(int pid, long start, int daemonPid = 999_999, long daemonStart = 0) =>
        JsonSerializer.Serialize(Rec(pid, start, daemonPid, daemonStart, mode: "oneshot", entry: "slow"));

    [Fact]
    public void InFlight_IsTheSpawnsWithoutADone_IncludingAReusedPid_AndSkipsATornLine()
    {
        var path = Path.Combine(_dir, "daemon-7.spawns");
        Ledger(path,
            Spawn(20, 1), "{\"Done\":20}",       // concluded
            Spawn(21, 1),                          // in flight
            Spawn(20, 9),                          // pid 20 again, later: in flight
            "{\"Pid\":22,\"Start");               // torn by the SIGKILL
        var inFlight = SpawnLedger.InFlight(path).OrderBy(r => r.Pid).ToArray();
        Assert.Equal(new[] { (20, 9L), (21, 1L) }, inFlight.Select(r => (r.Pid, r.StartTime)).ToArray());
    }

    [Fact]
    public void Plan_ADeadDaemonsLedger_BecomesRecords_ThenGoes()
    {
        var path = Path.Combine(_dir, "daemon-7.spawns");
        Ledger(path, Spawn(30, 100), Spawn(31, 100), "{\"Done\":31}", Spawn(32, 100));
        // 30 still running; 31 concluded; 32 ran and is gone.
        var probes = new ChildRecords.Probes(pid => pid == 30, _ => false, _ => 100);

        var targets = OrphanReaper.Plan(_dir, probes, ownerAlive: _ => false);

        var t = Assert.Single(targets);
        Assert.Equal((30, "oneshot", "slow"), (t.Rec.Pid, t.Rec.Mode, t.Rec.Entry));
        Assert.True(File.Exists(Path.Combine(_dir, "child-30.json")),
            "adopted as a record: a second hard death mid-reap still leaves it findable");
        Assert.False(File.Exists(path), "the dead daemon's ledger is consumed");
    }

    [Fact]
    public void Plan_ALiveDaemonsLedger_IsLeftAlone()
    {
        var path = Path.Combine(_dir, "daemon-7.spawns");
        Ledger(path, Spawn(30, 100));
        Assert.Empty(OrphanReaper.Plan(_dir, new(_ => true, _ => false, _ => 100), ownerAlive: _ => true));
        Assert.True(File.Exists(path));
        Assert.False(File.Exists(Path.Combine(_dir, "child-30.json")));
    }

    [Fact]
    public void Ledger_WritesSpawnAndDone_ClosesOnOrderlyExit_KeepsOnUnprovenOne()
    {
        var ledger = SpawnLedger.Open(_dir)!;
        Assert.Equal(SpawnLedger.PathFor(_dir, Environment.ProcessId), ledger.Path);
        ledger.Spawned(Environment.ProcessId, "/bin/true", "slow", "Stop");
        ledger.Spawned(1, "/bin/true", "other", "Stop");
        ledger.Done(1);

        // Every line is on disk at once (unbuffered), readable while open.
        var r = Assert.Single(SpawnLedger.InFlight(ledger.Path));
        Assert.Equal((Environment.ProcessId, "oneshot", "slow", Environment.ProcessId),
                     (r.Pid, r.Mode, r.Entry, r.DaemonPid));
        Assert.Equal(ChildRecords.ProcStartTime(Environment.ProcessId), r.StartTime);
        Assert.Equal(ChildRecords.SelfStartTime, r.DaemonStartTime);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(ledger.Path));

        ledger.Close(delete: false);   // the drain could not prove its children dead
        Assert.True(File.Exists(ledger.Path), "kept for the next boot reap");
        ledger.Done(Environment.ProcessId);   // after close: a no-op, never a throw

        var again = SpawnLedger.Open(_dir)!;  // same pid: truncates the predecessor's
        Assert.Empty(SpawnLedger.InFlight(again.Path));
        again.Dispose();
        Assert.False(File.Exists(again.Path), "an orderly exit removes it");
    }

    [Fact]
    public async Task ExecHandler_LedgersEachOneshot_InFlightUntilTheDispatchLetsGo()
    {
        var ledger = SpawnLedger.Open(_dir)!;
        SpawnLedger.Current = ledger;
        try
        {
            var ev = new HookEvent("Stop", "s-ledger", null, JsonDocument.Parse("{}").RootElement.Clone());
            var h = new ExecHandler("slow", "/bin/sh", ["-c", "cat >/dev/null; sleep 300"]);
            using var cts = new CancellationTokenSource();
            var run = h.HandleAsync(ev, new HandlerContext(DateTimeOffset.UtcNow.AddMinutes(1), cts.Token, "ld123456"));

            await Until(() => SpawnLedger.InFlight(ledger.Path).Count == 1, TimeSpan.FromSeconds(10), "spawn line");
            var r = SpawnLedger.InFlight(ledger.Path)[0];
            Assert.Equal(("slow", "Stop", "oneshot"), (r.Entry, r.Event, r.Mode));

            cts.Cancel();   // the budget: the dispatch kills it and lets go
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            await Until(() => SpawnLedger.InFlight(ledger.Path).Count == 0, TimeSpan.FromSeconds(10), "done line");
            await h.DisposeAsync();
        }
        finally
        {
            SpawnLedger.Current = null;
            ledger.Dispose();
        }
    }

    // ---- real processes -----------------------------------------------------

    [Fact]
    public async Task Reap_KillsALiveOrphanGroup_AndADeadLeadersSurvivingGroup()
    {
        var live = await StartGroupAsync(leaderDies: false);    // the deaf resident
        var forker = await StartGroupAsync(leaderDies: true);   // the forker's grandchild
        Write(Rec(live.Leader, live.LeaderStart));
        Write(Rec(forker.Leader, forker.LeaderStart));

        var targets = OrphanReaper.Plan(_dir, ownerAlive: _ => false);
        Assert.Equal(2, targets.Count);
        await OrphanReaper.KillAsync(targets, grace: TimeSpan.FromSeconds(1)).WaitAsync(TimeSpan.FromSeconds(15));

        await Until(() => !GroupAlive(live.Leader) && !GroupAlive(forker.Leader),
                             TimeSpan.FromSeconds(10), "both groups dead");
        Assert.False(Alive(live.Member) || Alive(forker.Member), "the members went with their groups");
        Assert.Empty(Directory.EnumerateFiles(_dir, "child-*.json"));
    }

    [Fact]
    public async Task Sweep_KeepsADeadLeadersRecord_WhileItsGroupLives()
    {
        // The v0.1.1 hole: the per-process sweep keyed on the leader alone and
        // deleted this record — the grandchild's only evidence.
        var forker = await StartGroupAsync(leaderDies: true);
        ChildRecords.OverrideDir = _dir;
        try
        {
            var path = Write(Rec(forker.Leader, forker.LeaderStart));
            ChildRecords.SweepForTests();
            Assert.True(File.Exists(path), "group alive: the record stays");

            KillGroup(forker.Leader);
            await Until(() => !GroupAlive(forker.Leader), TimeSpan.FromSeconds(10), "group dead");
            ChildRecords.SweepForTests();
            Assert.False(File.Exists(path), "group empty: now it is stale");
        }
        finally { ChildRecords.OverrideDir = null; }
    }

    [Fact]
    public async Task Reap_APidReusedRecord_SignalsNobody()
    {
        // The record says pid P started at another time: P is a stranger now.
        var stranger = await StartGroupAsync(leaderDies: false);
        var path = Write(Rec(stranger.Leader, stranger.LeaderStart + 1));

        Assert.Empty(OrphanReaper.Plan(_dir, ownerAlive: _ => false));
        Assert.False(File.Exists(path));
        Assert.True(GroupAlive(stranger.Leader) && Alive(stranger.Member), "never signaled");
    }

    [Fact]
    public async Task Doctor_ReportsTheSurvivingGroup_AndTheLedgersOneshot_ButSignalsNothing()
    {
        var forker = await StartGroupAsync(leaderDies: true);
        var slow = await StartGroupAsync(leaderDies: false);
        Write(Rec(forker.Leader, forker.LeaderStart, entry: "forker"));
        var ledger = Path.Combine(_dir, "daemon-999999.spawns");
        Ledger(ledger, JsonSerializer.Serialize(Rec(slow.Leader, slow.LeaderStart, mode: "oneshot", entry: "slow")));

        var verdicts = Doctor.SweepOrphans(_dir, isDaemonAlive: _ => false);

        Assert.Equal(2, verdicts.Count);
        var g = Assert.Single(verdicts, v => v.Version == $"child-{forker.Leader}");
        Assert.Contains("process group outlived its daemon", g.Detail);
        Assert.Contains($"kill -TERM -{forker.Leader}", g.Detail);
        var o = Assert.Single(verdicts, v => v.Version == $"oneshot-{slow.Leader}");
        Assert.Contains("oneshot 'slow'", o.Detail);
        Assert.True(GroupAlive(forker.Leader) && GroupAlive(slow.Leader), "doctor is report-only");
        Assert.True(File.Exists(ledger), "doctor never consumes a ledger — the boot reap does");
    }

    [Fact]
    public async Task DaemonStart_ReapsAPredecessorsOrphans_AndKeepsItsOwnLedger_UntilAnOrderlyExit()
    {
        // The wiring, end to end in-process: a daemon given a children dir
        // reaps what a dead daemon left (a record AND a ledger entry), opens
        // its own ledger, and removes it at drain.
        var orphan = await StartGroupAsync(leaderDies: false);
        var oneshot = await StartGroupAsync(leaderDies: true);
        Write(Rec(orphan.Leader, orphan.LeaderStart, entry: "deaf"));
        Ledger(Path.Combine(_dir, "daemon-999999.spawns"),
               JsonSerializer.Serialize(Rec(oneshot.Leader, oneshot.LeaderStart, mode: "oneshot", entry: "slow")));

        using var rt = new TempRuntimeDir();
        using var stop = new CancellationTokenSource();
        var daemon = Task.Run(() => DaemonHost.RunAsync(rt.Paths, NoHarnessDir(), stop.Token,
            new Registry(), drainDeadline: TimeSpan.FromSeconds(2), idleWindow: TimeSpan.FromMinutes(5),
            childrenDir: _dir));
        var own = SpawnLedger.PathFor(_dir, Environment.ProcessId);
        try
        {
            await Until(() => !GroupAlive(orphan.Leader) && !GroupAlive(oneshot.Leader),
                                 TimeSpan.FromSeconds(15), "the boot reap killed both");
            await Until(() => !Directory.EnumerateFiles(_dir, "child-*.json").Any(),
                                 TimeSpan.FromSeconds(5), "their records went");
            Assert.False(File.Exists(Path.Combine(_dir, "daemon-999999.spawns")));
            Assert.True(File.Exists(own), "the live daemon's own ledger");
        }
        finally
        {
            stop.Cancel();
            Assert.Equal(0, await daemon.WaitAsync(TimeSpan.FromSeconds(20)));
        }
        Assert.False(File.Exists(own), "an orderly exit removes its ledger");
    }
}
