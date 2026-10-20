using System.Text.Json;
using CaptainHook.Actors;

namespace CaptainHook.Handlers;

/// The boot-time reap (ADR-0010, 2026-10-19 amendment): a starting daemon
/// kills what its dead predecessors left behind. Every orderly path already
/// kills children as groups; a SIGKILLed daemon runs none of them, and the
/// measurement (Linux and macOS, v0.1.1) found three survivors nothing ever
/// killed: a resident that ignores stdin EOF, a resident's forked grandchild
/// (its leader exited, its group lived on), and a oneshot mid-run.
///
/// Two phases, split on purpose:
///   * PLAN — synchronous, before the daemon spawns anything. Reads every
///     `child-*.json` record and every other daemon's `daemon-*.spawns`
///     ledger; deletes what is stale; turns a dead daemon's still-running
///     oneshots into ordinary records (so a second hard death mid-reap leaves
///     them findable, and doctor sees them); returns the kill targets.
///     Running before the first spawn means a record naming this pid can only
///     be a dead predecessor's.
///   * KILL — in the background, so a 2 s grace never delays the bind:
///     TERM the group → 2 s → KILL, one `daemon.reapOrphan` line per target,
///     then the record goes (only if it is still the same record — the pid
///     may already be a new child of ours).
///
/// The decisions, per record whose owner is NOT a live engine:
///   leader alive, start time matches   ⇒ kill (the group when it leads one)
///   leader dead, its group still alive ⇒ kill the group
///   leader alive, start time differs   ⇒ pid reused: delete the record only
///   leader dead, group empty           ⇒ stale: delete
///   no recorded start time             ⇒ unprovable: keep, never signal
/// A live owner — another daemon (a deploy handoff overlaps two), or a
/// collapsed run — keeps every record it owns untouched.
internal static class OrphanReaper
{
    internal static readonly TimeSpan KillGrace = TimeSpan.FromSeconds(2);

    internal enum Why { Alive, GroupSurvives }

    internal sealed record Target(ChildRecords.Record Rec, string RecordPath, Why Why);

    /// Plan and start the reap; the returned task completes when every kill
    /// has concluded (the drain awaits it, bounded).
    internal static Task RunAsync(string dir)
    {
        IReadOnlyList<Target> targets;
        try { targets = Plan(dir); }
        catch (Exception ex)
        {
            Log.Warn("daemon", "daemon.reapError", new LogFields { Msg = $"orphan reap skipped: {ex.Message}" });
            return Task.CompletedTask;
        }
        return targets.Count == 0 ? Task.CompletedTask : Task.Run(() => KillAsync(targets));
    }

    internal static IReadOnlyList<Target> Plan(
        string dir, ChildRecords.Probes? probes = null, Func<ChildRecords.Record, bool>? ownerAlive = null)
    {
        var p = probes ?? ChildRecords.Probes.Real;
        var owner = ownerAlive ?? (r => ChildRecords.OwnerAlive(r, p));
        if (!Directory.Exists(dir)) return [];

        // Ledgers first: a dead daemon's in-flight oneshots become records,
        // so the record pass below plans them with everything else.
        foreach (var ledger in Directory.EnumerateFiles(dir, "daemon-*.spawns"))
        {
            try
            {
                var inFlight = SpawnLedger.InFlight(ledger);
                // Every line names the same owner; an empty ledger has only
                // its file name to go on (and an empty one is no loss).
                var ownerRec = inFlight.FirstOrDefault();
                if (ownerRec is not null ? owner(ownerRec) : LiveLedgerOwner(ledger, p)) continue;
                foreach (var rec in inFlight)
                {
                    if (ChildRecords.IsStale(ChildRecords.Classify(rec.Pid, rec.StartTime, p))) continue;
                    var path = Path.Combine(dir, $"child-{rec.Pid}.json");
                    if (!File.Exists(path)) ChildRecords.WriteAtomic(path, rec);
                }
                File.Delete(ledger);
            }
            catch (Exception) { /* unreadable ledger: leave it for doctor and the next boot */ }
        }

        var targets = new List<Target>();
        foreach (var f in Directory.EnumerateFiles(dir, "child-*.json").OrderBy(x => x, StringComparer.Ordinal))
        {
            ChildRecords.Record? rec = null;
            try { rec = JsonSerializer.Deserialize<ChildRecords.Record>(File.ReadAllText(f)); }
            catch (Exception) { /* corrupt: deleted below */ }
            if (rec is null) { TryDelete(f); continue; }

            var state = ChildRecords.Classify(rec.Pid, rec.StartTime, p);
            if (ChildRecords.IsStale(state)) { TryDelete(f); continue; }
            if (owner(rec)) continue;
            if (state == ChildRecords.ChildState.Alive) targets.Add(new Target(rec, f, Why.Alive));
            else if (state == ChildRecords.ChildState.GroupSurvives) targets.Add(new Target(rec, f, Why.GroupSurvives));
            // Unproven: kept for doctor to report; no proof, no signal.
        }
        return targets;
    }

    private static bool LiveLedgerOwner(string ledger, ChildRecords.Probes p) =>
        SpawnLedger.OwnerPid(ledger) is int pid && pid != Environment.ProcessId
        && p.PidAlive(pid) && ChildRecords.IsCaptainHookProcess(pid);

    internal static async Task KillAsync(IReadOnlyList<Target> targets, TimeSpan? grace = null)
    {
        await Task.WhenAll(targets.Select(async t =>
        {
            var pid = t.Rec.Pid;
            // A live leader is signaled as a group only when it leads one
            // (every spawn prefix but the degraded `none` rung makes it so);
            // a dead leader's survivors are, by construction, its group.
            var pgroup = t.Why == Why.GroupSurvives || ChildRecords.Probes.Real.GroupAlive(pid);
            string how;
            try { how = await ProcessGroup.TermThenKillAsync(pid, pgroup, proc: null, grace ?? KillGrace); }
            catch (Exception ex) { how = "error: " + ex.Message; }
            Log.Warn("daemon", "daemon.reapOrphan", new LogFields
            {
                Msg = t.Why == Why.Alive
                    ? $"{t.Rec.Mode} '{t.Rec.Entry}' (pid {pid}) outlived its daemon (pid {t.Rec.DaemonPid}) — killed"
                    : $"{t.Rec.Mode} '{t.Rec.Entry}' (pid {pid}) exited, but its process group outlived its daemon (pid {t.Rec.DaemonPid}) — killed",
                Data = new Dictionary<string, object>
                {
                    ["pid"] = pid, ["entry"] = t.Rec.Entry, ["event"] = t.Rec.Event, ["mode"] = t.Rec.Mode,
                    ["daemonPid"] = t.Rec.DaemonPid, ["pgroup"] = pgroup,
                    ["why"] = t.Why == Why.Alive ? "alive" : "groupSurvives", ["how"] = how,
                },
            });
            DeleteIfSame(t.RecordPath, t.Rec);
        }));
    }

    /// Delete the record only if it still describes the process we killed:
    /// once that pid is free, a resident of THIS daemon may take it and write
    /// its own record at the same name.
    private static void DeleteIfSame(string path, ChildRecords.Record rec)
    {
        try
        {
            var now = JsonSerializer.Deserialize<ChildRecords.Record>(File.ReadAllText(path));
            if (now is not null && now.Pid == rec.Pid && now.StartTime == rec.StartTime && now.DaemonPid == rec.DaemonPid)
                File.Delete(path);
        }
        catch (Exception) { /* gone already, or unreadable: the next sweep decides */ }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception) { /* hygiene is best-effort */ }
    }
}
