using System.Diagnostics;
using System.Text.Json;
using CaptainHook.Core;

namespace CaptainHook.Tests;

// doctor-reaper (ADR-0004 decision 4): every verdict is double-guarded —
// PID-reuse (never signal a stranger) and lineage-by-path (superseded means
// THE BINARY AT ITS OWN PATH moved on, so a dev-tree doctor never kills a
// healthy deployed daemon). Kills are real: tests spawn real processes and
// watch real SIGTERM/SIGKILL land. File cleanup rides TryAcquire+Dispose, so
// the kernel's lock — not the pidfile — decides what is safe to remove.

public class DoctorTests
{
    private static void WritePidfile(TempRuntimeDir dir, string version, int pid, string binaryPath)
    {
        Directory.CreateDirectory(dir.Path);
        File.WriteAllText(Path.Combine(dir.Path, $"captaind-{version}.pid"),
            JsonSerializer.Serialize(new PidRecord(pid, binaryPath, DateTimeOffset.UtcNow)));
    }

    /// A bin dir with a real assembly in it, so ContentIdentity.Compute works.
    private static string MakeBinDir(out string identity)
    {
        var dir = Path.Combine("/tmp", "chk-bin-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "captainHook.dll"), Path.Combine(dir, "captainHook.dll"));
        identity = ContentIdentity.Compute(dir);
        return dir;
    }

    private static Process Spawn(string shellCmd)
    {
        var p = Process.Start(new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", shellCmd } })!;
        return p;
    }

    [Fact]
    public async Task DeadPid_StaleFilesRemoved_LockStays()
    {
        using var dir = new TempRuntimeDir();
        // A real pid that is REALLY dead: spawn, exit, then record it.
        var p = Spawn("exit 0");
        p.WaitForExit();
        WritePidfile(dir, "deadver", p.Id, "/nonexistent/captainHook");
        File.WriteAllText(Path.Combine(dir.Path, "captaind-deadver.sock"), "stale");

        var verdicts = await Doctor.RunAsync(dir.Path, TimeSpan.FromSeconds(1));

        var v = Assert.Single(verdicts);
        Assert.Equal("dead", v.Action);
        Assert.False(File.Exists(Path.Combine(dir.Path, "captaind-deadver.pid")));
        Assert.False(File.Exists(Path.Combine(dir.Path, "captaind-deadver.sock")));
        Assert.True(File.Exists(Path.Combine(dir.Path, "captaind-deadver.lock")), "lock files stay, always");
    }

    [Fact]
    public async Task ReusedPid_RecordRemoved_ProcessNeverSignaled()
    {
        using var dir = new TempRuntimeDir();
        using var bystander = Spawn("sleep 30");   // alive, but NOT a captaind
        WritePidfile(dir, "reusedver", bystander.Id, "/some/old/captainHook");

        // Default cmdline guard: argv[0] is /bin/sh, not the recorded binary.
        var verdicts = await Doctor.RunAsync(dir.Path, TimeSpan.FromSeconds(1));

        var v = Assert.Single(verdicts);
        Assert.Equal("pid-reused", v.Action);
        Assert.False(bystander.HasExited, "doctor must NEVER signal a process it cannot prove is ours");
        Assert.False(File.Exists(Path.Combine(dir.Path, "captaind-reusedver.pid")));
        bystander.Kill();
    }

    [Fact]
    public async Task HealthyDaemon_CurrentIdentityAtItsPath_LeftAlone()
    {
        using var dir = new TempRuntimeDir();
        var bin = MakeBinDir(out var identity);
        try
        {
            using var daemonish = Spawn("sleep 30");
            WritePidfile(dir, identity, daemonish.Id, Path.Combine(bin, "captainHook"));

            var verdicts = await Doctor.RunAsync(dir.Path, TimeSpan.FromSeconds(1),
                isOurProcess: (_, _) => true);   // seam: pretend the cmdline matches

            var v = Assert.Single(verdicts);
            Assert.Equal("healthy", v.Action);
            Assert.False(daemonish.HasExited, "a healthy current-identity daemon must be left alone");
            Assert.True(File.Exists(Path.Combine(dir.Path, $"captaind-{identity}.pid")), "healthy record kept");
            daemonish.Kill();
        }
        finally { Directory.Delete(bin, recursive: true); }
    }

    [Fact]
    public async Task SupersededDaemon_SigtermReapsIt_FilesCleaned()
    {
        using var dir = new TempRuntimeDir();
        var bin = MakeBinDir(out _);   // real identity at the path...
        try
        {
            using var old = Spawn("sleep 60");   // dies on SIGTERM (default disposition)
            WritePidfile(dir, "oldident0000", old.Id, Path.Combine(bin, "captainHook"));   // ...but the record claims another

            var verdicts = await Doctor.RunAsync(dir.Path, TimeSpan.FromSeconds(5),
                isOurProcess: (_, _) => true);

            var v = Assert.Single(verdicts);
            Assert.Equal("superseded", v.Action);
            Assert.Contains("drained on SIGTERM", v.Detail);
            Assert.True(old.HasExited, "superseded daemon must be dead");
            Assert.False(File.Exists(Path.Combine(dir.Path, "captaind-oldident0000.pid")));
        }
        finally { Directory.Delete(bin, recursive: true); }
    }

    [Fact]
    public async Task TermIgnoringDaemon_SigkilledAfterGrace()
    {
        using var dir = new TempRuntimeDir();
        var bin = MakeBinDir(out _);
        try
        {
            using var stubborn = Spawn("trap '' TERM; sleep 60");
            await Task.Delay(200);   // let sh install the trap before doctor signals
            WritePidfile(dir, "stubborn0000", stubborn.Id, Path.Combine(bin, "captainHook"));

            var verdicts = await Doctor.RunAsync(dir.Path, grace: TimeSpan.FromMilliseconds(500),
                isOurProcess: (_, _) => true);

            var v = Assert.Single(verdicts);
            Assert.Equal("superseded", v.Action);
            Assert.Contains("SIGKILL", v.Detail);
            Assert.True(stubborn.HasExited, "grace expired: SIGKILL must have landed");
        }
        finally { Directory.Delete(bin, recursive: true); }
    }

    [Fact]
    public async Task CorruptPidfile_Removed()
    {
        using var dir = new TempRuntimeDir();
        Directory.CreateDirectory(dir.Path);
        File.WriteAllText(Path.Combine(dir.Path, "captaind-garbled00000.pid"), "not json at all");

        var verdicts = await Doctor.RunAsync(dir.Path, TimeSpan.FromSeconds(1));

        Assert.Equal("corrupt-pidfile", Assert.Single(verdicts).Action);
        Assert.False(File.Exists(Path.Combine(dir.Path, "captaind-garbled00000.pid")));
    }

    [Fact]
    public async Task LiveDaemonsLockBlocksCleanup_LeftAlone()
    {
        // A live daemon whose pidfile looks dead/corrupt (say, overwritten):
        // the LOCK is the truth — cleanup must refuse rather than unlink a
        // living daemon's socket.
        using var dir = new TempRuntimeDir();
        using var holder = DaemonRendezvous.TryAcquire(RendezvousPaths.Resolve(dir.Path, "heldver00000"))!;
        File.WriteAllText(Path.Combine(dir.Path, "captaind-heldver00000.pid"), "corrupted by cosmic ray");

        var verdicts = await Doctor.RunAsync(dir.Path, TimeSpan.FromSeconds(1));

        Assert.Equal("lock-held", Assert.Single(verdicts).Action);
    }
}
