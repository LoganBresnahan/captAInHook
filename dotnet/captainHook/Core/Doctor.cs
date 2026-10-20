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
        if (OperatingSystem.IsLinux())
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
        // macOS (ADR-0012 d3): no /proc — the BCL's MainModule.FileName
        // (proc_pidpath underneath) proves the executable path. WEAKER than
        // Linux's argv check on purpose-honesty grounds: argv (--daemon) isn't
        // reachable without a KERN_PROCARGS2 P/Invoke we can't exercise off-Mac,
        // so a short-lived collapsed run at the same path could momentarily
        // read as "ours". Documented macOS residue (ADR-0012 N3) — tighten
        // with the argv sysctl when a real Mac run exercises this path.
        try
        {
            using var proc = System.Diagnostics.Process.GetProcessById(pid);
            return proc.MainModule?.FileName == rec.BinaryPath;
        }
        catch
        {
            return false;   // gone or unreadable = cannot prove ours = never signal
        }
    }

    /// Sweep the rendezvous dir. `isOurProcess` is the test seam for the
    /// reuse guard; production uses the cmdline check.
    public static async Task<IReadOnlyList<DoctorVerdict>> RunAsync(
        string? runtimeDir = null, TimeSpan? grace = null,
        Func<int, PidRecord, bool>? isOurProcess = null, string? selfIdentity = null)
    {
        // Display only (ADR-0020 d3): which healthy daemon is THIS build's. After
        // an upgrade the previous version's daemon is still alive and still
        // healthy by the reap rule — nothing reaches it, so it idles out — and
        // saying so keeps two healthy lines from reading as a fault.
        string? self = selfIdentity;
        if (self is null) { try { self = ContentIdentity.Current; } catch { /* dev tree: no loose DLLs */ } }
        // The DIRECTORY only: doctor reads pidfiles, it binds no socket, so the
        // sun_path cap Resolve enforces is not its to trip over. (It used to
        // resolve a probe socket here and crash on a long home path.) The
        // choice is the shim's and the daemon's (ADR-0004 d3, amended
        // 2026-10-19); a short dir that is not proven ours is never read —
        // its pidfiles would steer which processes doctor signals.
        var choice = runtimeDir is null ? RendezvousPaths.ChooseRuntimeDir() : null;
        if (choice?.Refusal is not null) return [];
        var dir = runtimeDir ?? choice!.Dir;
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
                verdicts.Add(new DoctorVerdict(version, "healthy", version == self || self is null
                    ? $"pid {rec.Pid} serving this build at {rec.BinaryPath}; left alone"
                    : $"pid {rec.Pid} serving another installed build at {rec.BinaryPath} (not this one — "
                      + "after an upgrade that is the previous version, unreachable now; it idle-exits on its own); left alone"));
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

    /// Report ORPHANED children (ADR-0010 phase 8, doctor-orphans; group-aware
    /// since the 2026-10-19 amendment): the SAME verdict template as the
    /// daemon reap, aimed at `~/.captainHook/children/` — resident records
    /// `child-<pid>.json`, plus each dead daemon's oneshot ledger
    /// `daemon-<pid>.spawns`. An orphan outlived its owner: its record's pid
    /// is ALIVE with a MATCHING start time (the pid-reuse guard), or its
    /// leader exited while its PROCESS GROUP lives on (a forked grandchild —
    /// the measured case the leader-only check used to sweep as stale,
    /// erasing the only evidence) — and the owning engine is gone.
    /// REPORT-ONLY: doctor NEVER signals; the next daemon start kills what it
    /// reports (OrphanReaper — the same ChildRecords.Classify verdicts).
    /// Stale records (group empty / pid reused / unparseable) are cleaned in
    /// passing, the hygiene ChildRecords.SweepOnce does at daemon start;
    /// ledgers are only read — the next boot reap owns them. `childrenDir`
    /// and `isDaemonAlive` are test seams (the suite must never read the live
    /// children tree, and must be able to force the owner dead).
    internal static IReadOnlyList<DoctorVerdict> SweepOrphans(
        string? childrenDir = null, Func<ChildRecords.Record, bool>? isDaemonAlive = null)
    {
        var dir = childrenDir ?? ChildRecords.Dir;
        var ownerAlive = isDaemonAlive ?? (r => ChildRecords.OwnerAlive(r));
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

            // Group empty, or pid reused: a stale record, not an orphan —
            // sweep it, don't report it. Liveness via signal 0, POSIX on both
            // targets (ADR-0012 d3).
            var state = ChildRecords.Classify(rec.Pid, rec.StartTime);
            if (ChildRecords.IsStale(state))
            {
                TryDelete(f);
                continue;
            }

            // Owned by a LIVE engine? Then healthy — it kills its own.
            if (ownerAlive(rec)) continue;
            verdicts.Add(Orphan($"child-{rec.Pid}", rec, state));
        }

        foreach (var ledger in Directory.EnumerateFiles(dir, "daemon-*.spawns").OrderBy(x => x, StringComparer.Ordinal))
        {
            IReadOnlyList<ChildRecords.Record> inFlight;
            try { inFlight = SpawnLedger.InFlight(ledger); }
            catch { continue; }
            foreach (var rec in inFlight)
            {
                var state = ChildRecords.Classify(rec.Pid, rec.StartTime);
                if (ChildRecords.IsStale(state) || ownerAlive(rec)) continue;
                verdicts.Add(Orphan($"oneshot-{rec.Pid}", rec, state));
            }
        }

        foreach (var v in verdicts)
            Log.Warn("doctor", "doctor.orphan", new LogFields
            {
                Msg = v.Detail,
                Data = new Dictionary<string, object> { ["child"] = v.Version, ["action"] = v.Action },
            });
        return verdicts;
    }

    private static DoctorVerdict Orphan(string id, ChildRecords.Record rec, ChildRecords.ChildState state)
    {
        var what = $"{rec.Mode} '{rec.Entry}' (event {rec.Event}, pid {rec.Pid})";
        var detail = state == ChildRecords.ChildState.GroupSurvives
            ? $"{what} exited, but its process group outlived its daemon (pid {rec.DaemonPid}, gone) — "
              + $"members still running; the next daemon start kills the group, or: kill -TERM -{rec.Pid}"
            : $"{what} outlived its daemon (pid {rec.DaemonPid}, gone) — still running; "
              + "the next daemon start kills it, or kill its process group yourself";
        return new DoctorVerdict(id, "orphan", detail);
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
