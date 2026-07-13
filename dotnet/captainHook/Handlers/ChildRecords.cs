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
/// Oneshot children are deliberately NOT recorded (two file ops on the
/// dispatch critical path — the item-12 tax; short-lived anyway; phase-8
/// revisit).
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

    internal sealed record Record(
        int Pid, long StartTime, string Command, string Entry, string Event,
        string Mode, int DaemonPid, DateTimeOffset SpawnedAt);

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
                                 "resident", Environment.ProcessId, DateTimeOffset.UtcNow);
            // Atomic write (temp + rename): a deploy handoff legitimately
            // overlaps two daemons, and the incumbent's sweep must never
            // read — and delete-as-unparseable — a successor's half-written
            // record.
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(rec));
            try { File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
            catch (Exception) { }
            File.Move(tmp, path, overwrite: true);
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

    /// Delete at CONFIRMED GROUP DEATH (never mere leader exit — the
    /// kill-discipline lesson): idempotent, best-effort.
    internal static void Delete(string? path)
    {
        if (path is null) return;
        try { File.Delete(path); } catch (Exception) { /* evidence cleanup is best-effort */ }
    }

    /// /proc/<pid>/stat field 22 (starttime, clock ticks since boot) — the
    /// pid-reuse identity proof. 0 when unreadable (record still useful).
    internal static long ProcStartTime(int pid)
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

    /// Once per process: sweep records whose pid is gone or whose starttime
    /// no longer matches (pid reused) — pre-phase-8 hygiene so the dir cannot
    /// grow unboundedly across daemon crashes. A LIVE record that still
    /// matches is left alone even if its daemon died: that is precisely the
    /// orphan evidence doctor-orphans will want.
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
                    if (rec is null || !Directory.Exists($"/proc/{rec.Pid}")
                        || (rec.StartTime != 0 && ProcStartTime(rec.Pid) != rec.StartTime))
                        File.Delete(f);
                }
                catch (Exception) { File.Delete(f); }   // unparseable = no evidence value
            }
        }
        catch (Exception) { /* sweep is hygiene, never load-bearing */ }
    }
}
