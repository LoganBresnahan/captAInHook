using CaptainHook.Core;
using static CaptainHook.Tests.TestUtil;

namespace CaptainHook.Tests;

// detached-daemon-spawn (ADR-0004 decision 2): the spawned process must share
// NOTHING with the spawner but its environment — /dev/null stdio (the agent
// host waits for the shim's stdout EOF; an inherited fd would hang it), cwd
// at /, reparented away from the spawner. Probed with a real spawn that
// reports its own world into a file.

public class SpawnTests
{
    [Fact]
    public async Task Detached_DevNullStdio_RootCwd_ReparentedAwayFromUs()
    {
        var report = Path.Combine("/tmp", "chk-spawn-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            // The spawned probe writes its world: where fds 0/1/2 point, its
            // cwd, its parent pid, and what it reads from stdin (must be EOF
            // -> empty, proving stdin is /dev/null, not our pipe). Read the
            // fd table via /proc/$$/fd — $(…) subshells see their OWN fd1 (the
            // substitution pipe), which is how the first cut of this probe
            // fooled itself.
            DaemonSpawner.Detached("/bin/sh", "-c",
                "printf 'fd0=%s fd1=%s fd2=%s cwd=%s ppid=%s stdin=[%s]' " +
                "\"$(readlink /proc/$$/fd/0)\" \"$(readlink /proc/$$/fd/1)\" \"$(readlink /proc/$$/fd/2)\" " +
                $"\"$PWD\" \"$PPID\" \"$(cat)\" > {report}.tmp && mv {report}.tmp {report}");

            string? world = null;
            await PollUntilAsync(() =>
            {
                if (File.Exists(report)) { world = File.ReadAllText(report); return Task.FromResult(true); }
                return Task.FromResult(false);
            }, TimeSpan.FromSeconds(10), "spawned probe reported its world");

            Assert.Contains("fd0=/dev/null", world);
            Assert.Contains("fd1=/dev/null", world);
            Assert.Contains("fd2=/dev/null", world);
            Assert.Contains("cwd=/ ", world);
            Assert.Contains("stdin=[]", world);   // immediate EOF, nothing shared

            // Reparented: the intermediate sh exited, so the probe's parent is
            // init/a subreaper — never this test process.
            var ppid = int.Parse(System.Text.RegularExpressions.Regex.Match(world!, @"ppid=(\d+)").Groups[1].Value);
            Assert.NotEqual(Environment.ProcessId, ppid);
        }
        finally
        {
            try { File.Delete(report); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void MuxerInvocation_RefusesToSpawn_AndSaysWhy()
    {
        // `dotnet captainHook.dll` makes ProcessPath the dotnet muxer;
        // spawning `dotnet --daemon` would fail silently forever. The guard
        // must refuse loudly in the trail instead.
        using var log = new CapturedLog();
        DaemonSpawner.SpawnDaemonForNextHook("test0001", exeOverride: "/home/user/.dotnet/dotnet");

        var ev = Assert.Single(log.Events, e => e.Evt == "shim.spawnFailed");
        Assert.Contains("apphost", ev.Fields.Msg);
    }

    [Fact]
    public void Detached_ReturnsPromptly_NeverWaitsOnTheDaemon()
    {
        // The shim must not stall behind its own spawn: the intermediate sh
        // exits at once even though the spawned process sleeps for 30s.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        DaemonSpawner.Detached("/bin/sh", "-c", "sleep 30");
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2),
            $"Detached took {sw.Elapsed.TotalMilliseconds:F0}ms — it must return when sh exits, not when the daemon does");
    }
}
