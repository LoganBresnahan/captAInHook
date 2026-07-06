using System.Diagnostics;

namespace CaptainHook.Wire;

// ADR-0004 decision 2: on a NotDelivered fallback the shim spawns a daemon for
// the NEXT hook — this hook already collapsed; no hook ever waits for warmup.
// The spawn must detach COMPLETELY:
//   * no inherited streams — the agent host parses the shim's stdout and waits
//     for EOF; a daemon holding that fd would hang the host until the daemon
//     exits (idle-exit later, i.e. minutes). stdin/stdout/stderr all point at
//     /dev/null; the daemon's record is the JSONL file.
//   * no inherited cwd — a daemon pinned to whatever project directory the
//     shim happened to run in would hold that mount/dir for its lifetime; it
//     starts at /.
//   * env DOES flow through: CAPTAINHOOK_* read at daemon start is exactly
//     "daemon-start configuration" — shim and daemon default to the same
//     JSONL file so the trail stays in one place.
//
// Mechanism: /bin/sh -c 'exec "$0" --daemon </dev/null >/dev/null 2>&1 &' —
// the & backgrounds a subshell, exec replaces it with the daemon, sh exits
// immediately and the daemon reparents to init. POSIX-only, deliberately: the
// rendezvous it feeds is a Unix socket (doc/platform.md); /bin/sh is an OS
// facility, not a dependency.
public static class DaemonSpawner
{
    /// Fire-and-forget: spawn this same binary in daemon mode (one binary,
    /// three modes). Failures are logged, never thrown — a hook must never be
    /// lost to a spawn problem; the worst case is the next hook collapsing too.
    /// `exeOverride` is the test seam; production takes ProcessPath.
    public static void SpawnDaemonForNextHook(string? dispatchId, string? exeOverride = null)
    {
        var exe = exeOverride ?? Environment.ProcessPath;
        if (exe is null)
        {
            WireLog.Warn("shim", "shim.spawnFailed", new WireLogFields
            {
                DispatchId = dispatchId, Msg = "no ProcessPath — cannot locate the engine binary",
            });
            return;
        }
        // Invoked as `dotnet captainHook.dll …`, ProcessPath is the dotnet
        // MUXER, not this app — spawning `dotnet --daemon` is a CLI error and
        // dogfooding silently degrades to collapsed-forever (doc/platform.md).
        // Deploy the apphost executable and point the hook command at it.
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            WireLog.Warn("shim", "shim.spawnFailed", new WireLogFields
            {
                DispatchId = dispatchId,
                Msg = $"ProcessPath is the dotnet muxer ({exe}) — run via the apphost executable so the daemon can be spawned",
            });
            return;
        }
        try
        {
            Detached(exe, "--daemon");
            WireLog.Info("shim", "shim.spawnDaemon", new WireLogFields
            {
                DispatchId = dispatchId,
                Data = new Dictionary<string, object> { ["binary"] = exe },
            });
        }
        catch (Exception ex)
        {
            WireLog.Warn("shim", "shim.spawnFailed", new WireLogFields { DispatchId = dispatchId, Msg = ex.Message });
        }
    }

    /// Start `executable args...` fully detached: /dev/null stdio, cwd at /,
    /// reparented to init (the intermediate sh exits at once), environment
    /// inherited. Public for the spawn tests; production callers use
    /// SpawnDaemonForNextHook.
    public static void Detached(string executable, params string[] args)
    {
        // $0 = the executable, "$@" = its args — no shell-quoting of user
        // paths ever happens; sh receives them as positional parameters.
        var psi = new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false,
            WorkingDirectory = "/",
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("exec \"$0\" \"$@\" </dev/null >/dev/null 2>&1 &");
        psi.ArgumentList.Add(executable);
        foreach (var a in args) psi.ArgumentList.Add(a);

        // sh exits immediately; wait for it so no zombie lingers if the shim
        // lives on (collapsed dispatch runs next). Bounded: sh's own exit is
        // instant, the daemon is already disowned behind it.
        using var sh = Process.Start(psi)
            ?? throw new InvalidOperationException("/bin/sh did not start");
        sh.WaitForExit(2000);
    }
}
