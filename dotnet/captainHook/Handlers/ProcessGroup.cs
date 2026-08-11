using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CaptainHook.Handlers;

/// Kill discipline for exec-handler children (ADR-0010 decision 6): a wedged
/// or torn-down child dies as a PROCESS GROUP — SIGTERM (a chance to clean
/// up) → grace → SIGKILL — so grandchildren go with it.
///
/// The group is made at SPAWN. .NET has no setpgid seam on Linux
/// (ProcessStartInfo.CreateNewProcessGroup throws PlatformNotSupported off
/// Windows) — and no managed runtime does, because the calls that matter run
/// INSIDE the child, between fork and exec, a window the spawn API never
/// exposes (P/Invoke would set the attribute on the DAEMON). So the spawn is
/// prefixed with a wrapper that sets the attribute on ITSELF and then execs
/// the payload IN PLACE: Process.Id stays the payload's pid and pgid == sid
/// == pid, and grandchildren inherit the group. kill(-pid) then reaches them
/// even after the leader exits, which the /proc tree walk (Process.Kill
/// entireProcessTree) structurally cannot once they reparent to init.
///
/// WHICH wrapper is three rungs, loud at every step (ADR-0014 d1/d2):
/// co-located `bosun` (the deploy dir — the one we own, present on macOS) →
/// `setsid(1)` from PATH (dev trees and tests; util-linux, so ABSENT on stock
/// macOS — the defect bosun exists to fix) → no prefix at all, a direct exec
/// with tree-walk kills only. The active rung rides every `exec.spawn` line
/// as `spawner=bosun|setsid|none`; the degraded rung additionally carries
/// `pgroup=false`. (doc/platform.md § Process groups; probed 2026-07-12,
/// bosun 2026-08-11.)
internal static class ProcessGroup
{
    [DllImport("libc", SetLastError = true, EntryPoint = "kill")]
    private static extern int sys_kill(int pid, int sig);
    private const int SIGTERM = 15;
    private const int SIGKILL = 9;
    private const int ESRCH = 3;

    /// A resolved spawn prefix: the wrapper executable to run instead of the
    /// payload, plus the argv that must precede the payload's own command
    /// (bosun requires an explicit `--` terminator; setsid takes the command
    /// bare). `Exe` null = the degraded rung — spawn the command directly,
    /// no group, tree-walk kills.
    internal sealed record SpawnPrefix(string Name, string? Exe, IReadOnlyList<string> PreArgs)
    {
        /// Whether a spawn through this prefix yields its own process group —
        /// the bit every kill site keys on.
        internal bool Pgroup => Exe is not null;

        internal static readonly SpawnPrefix None = new("none", null, []);
    }

    /// The rung in force, resolved once per process.
    internal static readonly SpawnPrefix Prefix =
        Resolve(AppContext.BaseDirectory, Environment.GetEnvironmentVariable("PATH"));

    /// The three-rung resolution, as a pure function of (deploy dir, PATH) so
    /// every rung is directly testable — the live one is a snapshot of the
    /// real process's pair.
    internal static SpawnPrefix Resolve(string? baseDir, string? pathEnv)
    {
        // Rung 1 — the artifact we ship and stage (ADR-0014 d3), found by
        // co-location exactly as the shim finds the engine. On a live deploy
        // this rung must win; /deploy treats its absence as a staging defect.
        if (!string.IsNullOrEmpty(baseDir))
        {
            var bosun = Path.Combine(baseDir, "bosun");
            if (IsExecutable(bosun)) return new SpawnPrefix("bosun", bosun, ["--"]);
        }

        // Rung 2 — the rented wrapper: fine on Linux, absent on stock macOS.
        foreach (var dir in (pathEnv ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var setsid = Path.Combine(dir, "setsid");
            if (IsExecutable(setsid)) return new SpawnPrefix("setsid", setsid, []);
        }

        return SpawnPrefix.None;
    }

    private static bool IsExecutable(string candidate)
    {
        const UnixFileMode anyExec =
            UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
        try
        {
            // Executability, not mere existence: a non-executable candidate
            // must fall to the next rung, not fail EVERY spawn with a
            // Win32Exception blaming the payload.
            return File.Exists(candidate) && (File.GetUnixFileMode(candidate) & anyExec) != 0;
        }
        catch (Exception) { return false; /* unreadable candidate — keep walking */ }
    }

    /// Liveness for the thing the kill targets — the GROUP when `pgroup`,
    /// the leader otherwise (the adversarial-verify leader-keyed-liveness
    /// find: keying on the leader alone read `sleep 300 & exit 0` as "gone"
    /// and never signaled the surviving grandchild). Leader-alive answers
    /// fast via the Process object (zombie-tolerant: the runtime reaps its
    /// own children); leader-dead + pgroup probes the group with signal 0 —
    /// a pgid persists while any member does. Residual: a fully-dead,
    /// fully-reaped group whose pgid the kernel reuses inside the ≤3s kill
    /// window would read alive and could draw a stray signal — that needs a
    /// pid-space wraparound in seconds; accepted, like doctor's pid-reuse
    /// guard accepts its own window.
    internal static bool IsAlive(int pid, bool pgroup, Process? proc)
    {
        if (proc is not null)
        {
            try { if (!proc.HasExited) return true; }
            catch (Exception) { /* disposed ⇒ leader dead; the group may live on */ }
            if (!pgroup) return false;
        }
        var target = pgroup ? -pid : pid;
        return sys_kill(target, 0) == 0 || Marshal.GetLastPInvokeError() != ESRCH;
    }

    /// Send the group SIGTERM alone — synchronously, no waiting. The
    /// dispatch-path kill uses this BEFORE detaching the grace→KILL
    /// escalation: in collapsed mode the process can exit within
    /// milliseconds of the effect reaching stdout, and a TERM that lives
    /// only inside a detached task might never be sent at all.
    internal static void Term(int pid, bool pgroup, Process? proc) =>
        Signal(pid, pgroup, SIGTERM, proc);

    /// TERM → `grace` → KILL, against the group when `pgroup` (the setsid
    /// spawn made pgid == pid), else single pid + /proc tree walk. Returns how
    /// it ended: "gone" (already dead), "term" (drained on SIGTERM), "kill"
    /// (escalated). `termSent` skips the initial signal (the caller already
    /// sent it synchronously via Term). Real-time polling on purpose —
    /// production kill mechanics, not test waiting; callers never block a
    /// dispatch or the supervisor's fault mailbox on this (fire-and-forget
    /// from kill paths; awaited only by the drain's child phase, which
    /// budgets for it).
    internal static async Task<string> TermThenKillAsync(int pid, bool pgroup, Process? proc, TimeSpan grace,
                                                         bool termSent = false)
    {
        if (!IsAlive(pid, pgroup, proc)) return termSent ? "term" : "gone";
        if (!termSent) Signal(pid, pgroup, SIGTERM, proc);
        var sw = Stopwatch.StartNew();
        while (IsAlive(pid, pgroup, proc) && sw.Elapsed < grace)
            await Task.Delay(25);
        if (!IsAlive(pid, pgroup, proc)) return "term";
        Signal(pid, pgroup, SIGKILL, proc);
        // Give the kernel a beat to reap so a caller's follow-up probe sees it
        // gone; bounded — SIGKILL is not refusable, only slow to land.
        for (var i = 0; i < 40 && IsAlive(pid, pgroup, proc); i++)
            await Task.Delay(25);
        return "kill";
    }

    private static void Signal(int pid, bool pgroup, int sig, Process? proc)
    {
        if (pgroup)
        {
            // Group first; if the group is already gone but the pid somehow
            // lives (it re-grouped itself), fall back to the single pid.
            if (sys_kill(-pid, sig) != 0)
                sys_kill(pid, sig);
            return;
        }
        sys_kill(pid, sig);
        // No group to lean on: take the /proc descendant walk too — it misses
        // reparented grandchildren but beats leaving direct children behind.
        try { proc?.Kill(entireProcessTree: true); }
        catch (Exception) { /* already gone — the goal state */ }
    }
}
