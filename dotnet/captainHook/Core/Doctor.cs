using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using CaptainHook.Actors;
using CaptainHook.Handlers;
using CaptainHook.Wire;

namespace CaptainHook.Core;

// `captainHook doctor` (ADR-0004 decision 4): reap leftovers. Pidfiles exist
// for CLEANUP only, never discovery, and a recorded pid may name an unrelated
// process by reap time — so every verdict is guarded twice:
//   * PID-reuse guard (pharos ADR-030 lineage): the live process must argue
//     it is ours — /proc/<pid>/cmdline's argv[0] equals the recorded binary
//     path and the args contain --daemon. argv[0] survives the binary being
//     overwritten on disk (unlike /proc/<pid>/exe, which grows " (deleted)").
//   * Lineage by PATH, not by the doctor's own identity: a daemon is
//     SUPERSEDED iff the binary at ITS OWN recorded path now computes a
//     different content identity — that installation moved on. This keeps
//     doctor safe to run from any build: a dev-tree doctor never kills a
//     healthy deployed daemon just for being a different install.
// Reaping is SIGTERM (the daemon drains) → grace → SIGKILL. File cleanup
// rides the rendezvous itself: TryAcquire proves-by-lock that no live daemon
// of that version exists, and Dispose removes socket + pidfile. Lock files
// are never unlinked, doctor included.

public sealed record DoctorVerdict(string Version, string Action, string Detail);

public static class Doctor
{
    [DllImport("libc", SetLastError = true, EntryPoint = "kill")]
    private static extern int sys_kill(int pid, int sig);
    private const int SIGTERM = 15;
    private const int SIGKILL = 9;
    private const int ESRCH = 3;

    /// Liveness via signal 0: 0 = alive; ESRCH = gone; EPERM = alive (not ours
    /// to signal — which the reuse guard will classify anyway).
    private static bool IsAlive(int pid) =>
        sys_kill(pid, 0) == 0 || Marshal.GetLastPInvokeError() != ESRCH;

    private static bool DefaultIsOurs(int pid, PidRecord rec)
    {
        try
        {
            var argv = File.ReadAllText($"/proc/{pid}/cmdline").Split('\0');
            return argv.Length > 0 && argv[0] == rec.BinaryPath && argv.Contains("--daemon");
        }
        catch
        {
            return false;   // no cmdline to read = cannot prove ours = never signal
        }
    }

    /// Sweep the rendezvous dir. `isOurProcess` is the test seam for the
    /// reuse guard; production uses the cmdline check.
    public static async Task<IReadOnlyList<DoctorVerdict>> RunAsync(
        string? runtimeDir = null, TimeSpan? grace = null,
        Func<int, PidRecord, bool>? isOurProcess = null)
    {
        var dir = runtimeDir ?? RendezvousPaths.Resolve(version: "probe").RuntimeDir;
        // Covers the daemon's 10s drain PLUS its child phase (ADR-0010 N6:
        // TERM→2s grace→KILL + reap beat + the 1s cut window run after the
        // drain deadline; worst case ≈ 14.5s) with real headroom — a doctor
        // SIGKILL landing mid-child-phase would abandon the very kills the
        // phase exists to finish.
        var graceSpan = grace ?? TimeSpan.FromSeconds(18);
        var ours = isOurProcess ?? DefaultIsOurs;
        var verdicts = new List<DoctorVerdict>();
        if (!Directory.Exists(dir)) return verdicts;

        foreach (var pidPath in Directory.EnumerateFiles(dir, "captaind-*.pid").OrderBy(f => f, StringComparer.Ordinal))
        {
            var version = Path.GetFileNameWithoutExtension(pidPath)["captaind-".Length..];

            PidRecord? rec = null;
            try { rec = JsonSerializer.Deserialize<PidRecord>(File.ReadAllText(pidPath)); }
            catch { /* corrupt: fall through to cleanup */ }

            if (rec is null)
            {
                verdicts.Add(Cleanup(dir, version, "corrupt-pidfile", "unparseable record removed"));
                continue;
            }

            if (!IsAlive(rec.Pid))
            {
                verdicts.Add(Cleanup(dir, version, "dead", $"pid {rec.Pid} gone; stale files removed"));
                continue;
            }

            if (!ours(rec.Pid, rec))
            {
                // Alive but provably not our daemon: the pid was recycled.
                // Clean the RECORD; never signal a stranger.
                verdicts.Add(Cleanup(dir, version, "pid-reused", $"pid {rec.Pid} is an unrelated process; record removed"));
                continue;
            }

            // Alive and ours: superseded iff the binary at ITS path moved on.
            string? pathIdentity = null;
            try { pathIdentity = ContentIdentity.Compute(Path.GetDirectoryName(rec.BinaryPath)!); }
            catch { /* bin dir gone or empty: nothing at that path anymore — superseded */ }

            if (pathIdentity == version)
            {
                verdicts.Add(new DoctorVerdict(version, "healthy", $"pid {rec.Pid} serving the current build at {rec.BinaryPath}; left alone"));
                continue;
            }

            // SIGTERM (drains) -> grace -> SIGKILL.
            sys_kill(rec.Pid, SIGTERM);
            var sw = Stopwatch.StartNew();
            while (IsAlive(rec.Pid) && sw.Elapsed < graceSpan)
                await Task.Delay(100);
            var howItDied = "drained on SIGTERM";
            if (IsAlive(rec.Pid))
            {
                sys_kill(rec.Pid, SIGKILL);
                howItDied = "SIGKILLed after grace";
                // give the kernel a beat to reap before the lock probe
                for (var i = 0; i < 50 && IsAlive(rec.Pid); i++) await Task.Delay(100);
            }
            verdicts.Add(Cleanup(dir, version, "superseded",
                $"pid {rec.Pid} was running identity {version}, path now {pathIdentity ?? "<gone>"}; {howItDied}"));
        }

        foreach (var v in verdicts)
            Log.Info("doctor", "doctor.verdict", new LogFields
            {
                Msg = v.Detail,
                Data = new Dictionary<string, object> { ["version"] = v.Version, ["action"] = v.Action },
            });
        return verdicts;
    }

    /// Report ORPHANED resident children (ADR-0010 phase 8, doctor-orphans):
    /// the SAME double-guarded verdict template as the daemon reap, aimed at a
    /// second entity class — `~/.captainHook/children/child-<pid>.json`. An
    /// orphan is a child that escaped every kill path: its record's pid is
    /// still ALIVE with a MATCHING /proc starttime (the pid-reuse guard — a
    /// recycled pid running something else has a different starttime), yet its
    /// owning daemon is GONE (the canonical door a resident dies through is its
    /// daemon's SIGTERM/SIGKILL; a daemon that was itself SIGKILLed before it
    /// could drain leaves its children behind). REPORT-ONLY: doctor NEVER
    /// signals the child — a misclassification is a wrong trail line, never a
    /// killed live process (why this slice needs no adversarial verify). Stale
    /// records (dead pid / starttime drift / unparseable) are cleaned in
    /// passing — the same hygiene ChildRecords.SweepOnce does at daemon start,
    /// available on demand here. `childrenDir` and `isDaemonAlive` are test
    /// seams (the suite must never read the live children tree, and must be able
    /// to force the owner dead).
    public static IReadOnlyList<DoctorVerdict> SweepOrphans(
        string? childrenDir = null, Func<int, bool>? isDaemonAlive = null)
    {
        var dir = childrenDir ?? ChildRecords.Dir;
        var ownerAlive = isDaemonAlive ?? IsCaptainHookDaemon;
        var verdicts = new List<DoctorVerdict>();
        if (!Directory.Exists(dir)) return verdicts;

        foreach (var f in Directory.EnumerateFiles(dir, "child-*.json").OrderBy(x => x, StringComparer.Ordinal))
        {
            ChildRecords.Record? rec = null;
            try { rec = JsonSerializer.Deserialize<ChildRecords.Record>(File.ReadAllText(f)); }
            catch { /* corrupt: cleaned below, never reported (no evidence value) */ }
            if (rec is null)
            {
                TryDelete(f);
                continue;
            }

            // Child gone, or pid reused (starttime drift): a stale record, not
            // an orphan — sweep it, don't report it.
            if (!Directory.Exists($"/proc/{rec.Pid}")
                || (rec.StartTime != 0 && ChildRecords.ProcStartTime(rec.Pid) != rec.StartTime))
            {
                TryDelete(f);
                continue;
            }

            // Alive + identity-proven. Owned by a LIVE captainHook daemon? Then
            // healthy — that daemon will kill it at drain; not an orphan.
            if (ownerAlive(rec.DaemonPid)) continue;

            // Alive child, dead/foreign owning daemon ⇒ ORPHAN. Report only.
            verdicts.Add(new DoctorVerdict(
                $"child-{rec.Pid}", "orphan",
                $"resident '{rec.Entry}' (event {rec.Event}, pid {rec.Pid}) outlived its daemon " +
                $"(pid {rec.DaemonPid}, gone) — still running; kill the process group or run the payload's own cleanup"));
        }

        foreach (var v in verdicts)
            Log.Warn("doctor", "doctor.orphan", new LogFields
            {
                Msg = v.Detail,
                Data = new Dictionary<string, object> { ["child"] = v.Version, ["action"] = v.Action },
            });
        return verdicts;
    }

    /// Is `pid` a LIVE captainHook daemon (not a reused pid)? The child-owner
    /// guard: alive AND its cmdline names captainHook with --daemon. A dead pid,
    /// or one recycled onto an unrelated process, both mean the owning daemon is
    /// gone — so its live child is an orphan.
    private static bool IsCaptainHookDaemon(int pid)
    {
        if (!IsAlive(pid)) return false;
        try
        {
            var argv = File.ReadAllText($"/proc/{pid}/cmdline").Split('\0');
            return argv.Any(a => a.Contains("captainHook", StringComparison.Ordinal)) && argv.Contains("--daemon");
        }
        catch { return false; }   // can't read cmdline: cannot prove a live owner
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* hygiene is best-effort, like the sweep */ }
    }

    /// Remove a version's socket + pidfile by BECOMING its daemon for an
    /// instant: TryAcquire proves by lock that nothing of this version lives
    /// (the kernel's word, not the pidfile's), and Dispose unlinks socket and
    /// pidfile through the same code every graceful exit uses. The lock file
    /// stays, always.
    private static DoctorVerdict Cleanup(string dir, string version, string action, string detail)
    {
        var paths = RendezvousPaths.Resolve(dir, version);
        using var rv = DaemonRendezvous.TryAcquire(paths);
        if (rv is null)
            return new DoctorVerdict(version, "lock-held",
                $"wanted to clean ({action}: {detail}) but a live daemon holds the lock; left alone");
        // The lock proves this version's daemon is dead, so its management-API
        // discovery file (ADR-0007) is stale too — reap it beside the socket and
        // pidfile `rv.Dispose` removes. Version-partitioned, so never a live
        // successor's file. (Dispose owns socket+pid; api.json is not its
        // concern, so it is unlinked explicitly here.)
        try { File.Delete(paths.ApiJsonPath); } catch { /* best-effort, like the socket */ }
        return new DoctorVerdict(version, action, detail);
    }
}
