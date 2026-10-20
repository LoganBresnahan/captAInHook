using System.Text.Json;
using CaptainHook.Actors;

namespace CaptainHook.Handlers;

/// Per-child pid/identity records for RESIDENT children (ADR-0010 resident
/// slice) — written so doctor-orphans (phase 8) is later pure read-side. An
/// orphan is a child that escaped every kill path (daemon SIGKILL is the
/// canonical door), so the record must OUTLIVE the daemon and the login
/// session: always `~/.captainHook/children/` — deliberately NOT the
/// XDG_RUNTIME_DIR tmpfs the rendezvous files use, which is wiped at session
/// end, i.e. exactly when the forensic record is needed (design-panel find).
/// Identity proof is /proc starttime + pid, the doctor pid-reuse pattern:
/// argv[0] doesn't survive `python3 foo.py` shapes, starttime does.
///
/// Everything here is best-effort: a record failure never fails a spawn, a
/// delete failure never fails a kill — the record is evidence, not lifecycle.
/// Oneshot children get no record of their own (two file ops on the dispatch
/// critical path — the item-12 tax); a daemon's oneshots go in its
/// append-only SpawnLedger instead, which the next daemon's OrphanReaper reads.
///
/// Liveness is the GROUP's, never the leader's alone (ADR-0010, 2026-10-19
/// amendment): a leader that exited can leave members alive in its process
/// group — a forked grandchild, the measured case — and a record is stale
/// only once the whole group is empty. `Classify` is the one verdict the
/// sweep, doctor and the boot reap all read.
internal static class ChildRecords
{
    /// Test seam (the harness-dir idiom): the suite must never write the
    /// LIVE ~/.captainHook tree. Null in production.
    internal static string? OverrideDir;

    internal static string Dir => OverrideDir ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".captainHook", "children");

    private static int _swept;

    /// Test seam: the sweep runs once per process by design, so a direct
    /// unit test must be able to re-arm and invoke it against a seeded
    /// OverrideDir.
    internal static void SweepForTests()
    {
        Volatile.Write(ref _swept, 0);
        SweepOnce();
    }

    /// `DaemonStartTime` is the owning daemon's own start-time identity (0 in
    /// records written before 0.1.2): with it, "is the owner alive" is the
    /// same pid + start-time proof as the child's, not a cmdline guess.
    internal sealed record Record(
        int Pid, long StartTime, string Command, string Entry, string Event,
        string Mode, int DaemonPid, DateTimeOffset SpawnedAt, long DaemonStartTime = 0);

    /// This process's start-time identity — what its records name as owner.
    internal static long SelfStartTime => _selfStart ??= ProcStartTime(Environment.ProcessId);
    private static long? _selfStart;

    /// What a record's pid is now.
    internal enum ChildState
    {
        /// Leader dead and its group empty: nothing left — a stale record.
        Gone,
        /// The pid is alive but started at another time: recycled onto a
        /// stranger. The original group is gone too — the kernel never hands
        /// out a pid still in use as a process-group id — so: stale.
        Reused,
        /// Leader alive, start time proven equal.
        Alive,
        /// Leader alive, but the record has no start time to prove it with
        /// (unreadable at spawn): kept, reported, never signaled.
        Unproven,
        /// Leader dead, yet kill(-pid, 0) finds its group: members (a forked
        /// grandchild) outlived it. Still the ORIGINAL group, by the same
        /// kernel rule — a pgid's number is not reissued while the group lives.
        GroupSurvives,
    }

    /// The probes `Classify` reads, injectable so the decision table is
    /// testable without staging every process shape.
    internal sealed record Probes(Func<int, bool> PidAlive, Func<int, bool> GroupAlive, Func<int, long> StartTime)
    {
        internal static readonly Probes Real = new(
            pid => ProcessGroup.IsAlive(pid, pgroup: false, proc: null),
            pid => ProcessGroup.IsAlive(pid, pgroup: true, proc: null),
            ProcStartTime);
    }

    internal static ChildState Classify(int pid, long recordedStart, Probes? probes = null)
    {
        var p = probes ?? Probes.Real;
        if (!p.PidAlive(pid)) return p.GroupAlive(pid) ? ChildState.GroupSurvives : ChildState.Gone;
        if (recordedStart == 0) return ChildState.Unproven;
        return p.StartTime(pid) == recordedStart ? ChildState.Alive : ChildState.Reused;
    }

    internal static bool IsStale(ChildState s) => s is ChildState.Gone or ChildState.Reused;

    /// Is the record's owning engine still alive? With a recorded owner start
    /// time, exactly the child's proof. Without one (a pre-0.1.2 record), the
    /// old guard: a live captainHook process that is not this one — a boot
    /// reap runs before its daemon spawns anything, so a record naming this
    /// pid is a dead predecessor's that happened to share it.
    internal static bool OwnerAlive(Record rec, Probes? probes = null)
    {
        var p = probes ?? Probes.Real;
        if (!p.PidAlive(rec.DaemonPid)) return false;
        if (rec.DaemonStartTime != 0) return p.StartTime(rec.DaemonPid) == rec.DaemonStartTime;
        return rec.DaemonPid != Environment.ProcessId && IsCaptainHookProcess(rec.DaemonPid);
    }

    /// A live captainHook engine, in ANY mode — a collapsed run owns the
    /// records of the residents it degrades to oneshot lifecycle, and its
    /// children are no orphans while it runs. Linux: argv names captainHook;
    /// macOS: the executable path does (ADR-0012 d3).
    internal static bool IsCaptainHookProcess(int pid)
    {
        if (OperatingSystem.IsLinux())
        {
            try
            {
                return File.ReadAllText($"/proc/{pid}/cmdline").Split('\0')
                    .Any(a => a.Contains("captainHook", StringComparison.Ordinal));
            }
            catch { return false; }   // can't read cmdline: cannot prove a live owner
        }
        try
        {
            using var proc = System.Diagnostics.Process.GetProcessById(pid);
            return (proc.MainModule?.FileName ?? "").Contains("captainHook", StringComparison.Ordinal);
        }
        catch { return false; }
    }

    /// Write the record for a just-spawned resident child. Returns the path
    /// (for the confirmed-death delete) or null when best-effort failed.
    internal static string? Write(int pid, string command, string entry, string evt)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            try { File.SetUnixFileMode(Dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            catch (Exception) { /* permissions best-effort, like the dir itself */ }
            SweepOnce();
            var path = Path.Combine(Dir, $"child-{pid}.json");
            var rec = new Record(pid, ProcStartTime(pid), command, entry, evt,
                                 "resident", Environment.ProcessId, DateTimeOffset.UtcNow, SelfStartTime);
            WriteAtomic(path, rec);
            return path;
        }
        catch (Exception ex)
        {
            Log.Warn("exec", "exec.recordError", new LogFields
            {
                Msg = $"child record write failed: {ex.Message}",
                Data = new Dictionary<string, object> { ["entry"] = entry, ["pid"] = pid },
            });
            return null;
        }
    }

    /// Atomic write (temp + rename): a deploy handoff legitimately overlaps
    /// two daemons, and the incumbent's sweep must never read — and
    /// delete-as-unparseable — a successor's half-written record.
    internal static void WriteAtomic(string path, Record rec)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(rec));
        try { File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        catch (Exception) { }
        File.Move(tmp, path, overwrite: true);
    }

    /// Delete at CONFIRMED GROUP DEATH (never mere leader exit — the
    /// kill-discipline lesson): idempotent, best-effort.
    internal static void Delete(string? path)
    {
        if (path is null) return;
        try { File.Delete(path); } catch (Exception) { /* evidence cleanup is best-effort */ }
    }

    /// The pid-reuse identity proof: a per-process start-time value that is
    /// EXACT and STABLE across reads, compared by equality against the value
    /// recorded at spawn. 0 when unreadable (record still useful).
    ///
    /// Linux reads /proc/<pid>/stat field 22 RAW (clock ticks since boot) —
    /// deliberately NOT the BCL's Process.StartTime, which on Linux derives a
    /// DateTime from boot-time arithmetic and jitters several ms between
    /// calls (doc/platform.md): a jittering value under equality would misread
    /// a LIVE child as pid-reused and sweep its record. macOS has no /proc;
    /// there the BCL is the seam (sysctl/proc_pidinfo underneath, a stored
    /// absolute timestamp — stable across reads, so equality is sound).
    /// The two branches never mix: a record is always compared on the OS that
    /// wrote it (ADR-0012 decision 3).
    internal static long ProcStartTime(int pid)
    {
        if (OperatingSystem.IsLinux())
        {
            try
            {
                var stat = File.ReadAllText($"/proc/{pid}/stat");
                // fields 3.. start after the parenthesized comm (which may itself
                // contain spaces/parens — split after the LAST ')').
                var rest = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
                return long.Parse(rest[19]);   // field 22 overall = index 19 after state
            }
            catch (Exception) { return 0; }
        }
        try
        {
            using var proc = System.Diagnostics.Process.GetProcessById(pid);
            return proc.StartTime.ToUniversalTime().Ticks;
        }
        catch (Exception) { return 0; }   // gone, or start time unreadable
    }

    /// Once per process: sweep records whose group is empty or whose pid was
    /// reused — hygiene so the dir cannot grow unboundedly across daemon
    /// crashes. A LIVE record is left alone even if its daemon died, and so is
    /// one whose leader exited while its group lives on: that is precisely the
    /// orphan evidence the boot reap and doctor read. (Liveness via signal 0 —
    /// POSIX on both targets, no /proc, ADR-0012 d3.)
    private static void SweepOnce()
    {
        if (Interlocked.Exchange(ref _swept, 1) == 1) return;
        try
        {
            foreach (var f in Directory.EnumerateFiles(Dir, "child-*.json"))
            {
                try
                {
                    var rec = JsonSerializer.Deserialize<Record>(File.ReadAllText(f));
                    if (rec is null || IsStale(Classify(rec.Pid, rec.StartTime)))
                        File.Delete(f);
                }
                catch (Exception) { File.Delete(f); }   // unparseable = no evidence value
            }
        }
        catch (Exception) { /* sweep is hygiene, never load-bearing */ }
    }
}
