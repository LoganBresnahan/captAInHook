using System.Diagnostics;
using System.Text.Json;
using CaptainHook.Core;
using CaptainHook.Handlers;

namespace CaptainHook.Tests;

// doctor-orphans (ADR-0010 phase 8): report resident children that OUTLIVED
// their daemon — the same double-guard as the daemon reap (pid + /proc
// starttime identity, live-owner check), REPORT-ONLY (doctor never signals the
// child). Real processes: a live `sleep` stands in for a leaked resident child.
public class DoctorOrphansTests : IDisposable
{
    private readonly string _dir = Path.Combine("/tmp", "chk-orph-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<Process> _procs = new();

    public DoctorOrphansTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var p in _procs) { try { p.Kill(true); } catch { /* best-effort */ } }
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private Process LiveProc()
    {
        var p = Process.Start(new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", "sleep 30" } })!;
        _procs.Add(p);
        return p;
    }

    private void WriteRecord(int pid, long startTime, int daemonPid, string entry = "gate", string evt = "PreToolUse")
    {
        var rec = new ChildRecords.Record(pid, startTime, "/bin/sh", entry, evt, "resident", daemonPid,
                                          DateTimeOffset.UtcNow);
        File.WriteAllText(Path.Combine(_dir, $"child-{pid}.json"), JsonSerializer.Serialize(rec));
    }

    private string RecPath(int pid) => Path.Combine(_dir, $"child-{pid}.json");

    [Fact]
    public void AliveChild_DeadDaemon_ReportedAsOrphan_RecordKept()
    {
        var child = LiveProc();
        WriteRecord(child.Id, ChildRecords.ProcStartTime(child.Id), daemonPid: 999_999);

        var orphans = Doctor.SweepOrphans(_dir, isDaemonAlive: _ => false);

        var v = Assert.Single(orphans);
        Assert.Equal("orphan", v.Action);
        Assert.Equal($"child-{child.Id}", v.Version);
        Assert.Contains("gate", v.Detail);
        Assert.True(File.Exists(RecPath(child.Id)), "the orphan record stays — it IS the evidence, report-only");
    }

    [Fact]
    public void AliveChild_LiveDaemon_NotOrphan_RecordUntouched()
    {
        var child = LiveProc();
        WriteRecord(child.Id, ChildRecords.ProcStartTime(child.Id), daemonPid: Environment.ProcessId);

        var orphans = Doctor.SweepOrphans(_dir, isDaemonAlive: _ => true);   // owner alive ⇒ healthy
        Assert.Empty(orphans);
        Assert.True(File.Exists(RecPath(child.Id)));
    }

    [Fact]
    public void DefaultOwnerGuard_NonDaemonOwnerPid_IsOrphan()
    {
        // The real IsCaptainHookDaemon guard: a live owner pid whose cmdline is
        // NOT a captainHook --daemon (here a plain sleep, standing in for a
        // recycled daemon pid) counts as "owner gone" ⇒ the child is an orphan.
        // Proves pid-reuse of the DAEMON pid can't mask a genuine orphan.
        var child = LiveProc();
        var fakeOwner = LiveProc();
        WriteRecord(child.Id, ChildRecords.ProcStartTime(child.Id), daemonPid: fakeOwner.Id);

        var orphans = Doctor.SweepOrphans(_dir);   // DEFAULT owner check
        Assert.Equal($"child-{child.Id}", Assert.Single(orphans).Version);
    }

    [Fact]
    public void DeadChild_SweptNotReported()
    {
        var p = Process.Start(new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", "exit 0" } })!;
        p.WaitForExit();
        WriteRecord(p.Id, 12345, daemonPid: 999_999);

        var orphans = Doctor.SweepOrphans(_dir, isDaemonAlive: _ => false);
        Assert.Empty(orphans);   // a dead child is stale, never an orphan
        Assert.False(File.Exists(RecPath(p.Id)), "stale record swept in passing");
    }

    [Fact]
    public void PidReused_StarttimeDrift_SweptNotReported()
    {
        // A LIVE pid whose recorded starttime does NOT match: the pid was
        // recycled onto a different process. It must NEVER be reported as an
        // orphan — the guard that keeps doctor from naming an innocent stranger.
        var child = LiveProc();
        WriteRecord(child.Id, ChildRecords.ProcStartTime(child.Id) + 1, daemonPid: 999_999);

        var orphans = Doctor.SweepOrphans(_dir, isDaemonAlive: _ => false);
        Assert.Empty(orphans);
        Assert.False(File.Exists(RecPath(child.Id)), "pid-reuse (starttime drift) record swept");
    }

    [Fact]
    public void CorruptRecord_SweptNotReported()
    {
        File.WriteAllText(Path.Combine(_dir, "child-424242.json"), "{ not valid json");
        var orphans = Doctor.SweepOrphans(_dir, isDaemonAlive: _ => false);
        Assert.Empty(orphans);
        Assert.False(File.Exists(Path.Combine(_dir, "child-424242.json")), "unparseable record swept, never reported");
    }

    [Fact]
    public void AbsentChildrenDir_NoVerdicts()
    {
        Assert.Empty(Doctor.SweepOrphans(Path.Combine(_dir, "does-not-exist"), isDaemonAlive: _ => false));
    }
}
