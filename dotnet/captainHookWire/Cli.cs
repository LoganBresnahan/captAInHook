namespace CaptainHook.Wire;

// ADR-0004 decision 1: one binary, three modes. Which mode a process runs in is
// decided ONCE, here, from argv — never inferred mid-flight — so the seam the
// daemon slices build on (shim forwards, daemon serves, collapsed dispatches
// in-process) is explicit and unit-testable without spawning a process.

/// The three ADR-0004 modes, plus the pre-existing actors demo subcommand.
public enum Mode
{
    /// `hook <event>` — forward to a warm daemon over the socket; falls back to
    /// collapsed on connect failure. (Forwarding lands in shim-forward-or-fallback;
    /// until then shim mode dispatches in-process, indistinguishable from collapsed.)
    Shim,
    /// `hook <event> --no-daemon` — dispatch in-process exactly like today's
    /// single-shot binary (CI, one-off runs, and the shim's automatic fallback).
    Collapsed,
    /// `--daemon` — build registry/dispatcher/workers once, serve over the socket.
    Daemon,
    /// `doctor` — reap leftover daemons/files with the PID-reuse guard.
    Doctor,
    /// `actors-demo` — drive the F# actor layer directly.
    ActorsDemo,
}

/// One parsed invocation: the mode plus the hook-dispatch arguments that shim
/// and collapsed modes share. Daemon/ActorsDemo ignore EventName/HarnessName.
public sealed record Invocation(Mode Mode, string? EventName, string HarnessName)
{
    public static Invocation Parse(string[] args)
    {
        if (args.Length > 0 && args[0] == "actors-demo")
            return new(Mode.ActorsDemo, null, "claude-code");
        if (args.Length > 0 && args[0] == "doctor")
            return new(Mode.Doctor, null, "claude-code");

        string? eventName = null;
        var harnessName = "claude-code";
        bool daemon = false, noDaemon = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "hook" && i + 1 < args.Length) eventName ??= args[++i];
            else if (args[i] == "--harness" && i + 1 < args.Length) harnessName = args[++i];
            else if (args[i] == "--daemon") daemon = true;
            else if (args[i] == "--no-daemon") noDaemon = true;
        }

        // --daemon wins over --no-daemon: a daemon invocation is never a hook
        // dispatch, so the collapsed flag has nothing to force.
        var mode = daemon ? Mode.Daemon : noDaemon ? Mode.Collapsed : Mode.Shim;
        return new(mode, eventName, harnessName);
    }
}
