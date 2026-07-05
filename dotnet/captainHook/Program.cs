using CaptainHook.Core;

// captAInHook — one binary, three modes (ADR-0004 decision 1): shim, daemon,
// collapsed. Mode is decided once from argv in Invocation.Parse; the dispatch
// pipeline itself lives in Core/HookRun.cs. Program.cs is only the seam between
// the real Console streams and that pipeline — the one place (plus Demo/) that
// may touch Console.*.
//
// Invocation mirrors cavemem:  captain hook <event>   with the hook payload as
// JSON on stdin and the response effect as JSON on stdout. That is exactly how
// Claude Code drives a hook, so this wires straight into settings.json — and is
// testable in isolation:
//     printf '{...}' | dotnet captainHook.dll hook user-prompt-submit
//
// Which host we speak to is DATA, not code: `--harness <name>` selects a
// HarnessSpec (default: claude-code) and the spec drives request parsing, the
// capability gate, and the response wire format. See Core/Harness.cs.

// ---- cold-start probe (opt-in): anchor the stopwatch as early as managed code
//      allows, before any real work. Null unless CAPTAINHOOK_COLDSTART=1. -------
var probe = ColdStartProbe.StartIfEnabled();

var inv = Invocation.Parse(args);
switch (inv.Mode)
{
    case Mode.ActorsDemo:
        await CaptainHook.Demo.ActorsDemo.RunAsync();
        return 0;

    case Mode.Daemon:
        // captaind: warm once, serve over the socket until killed (drain and
        // idle-exit are upcoming slices). See Core/DaemonHost.cs.
        return await DaemonHost.RunAsync();

    case Mode.Shim:
    {
        // The SHIM mints the dispatchId; the daemon adopts it, and a collapsed
        // fallback reuses it — one id stitches the forward attempt, the
        // fallback, and the daemon half into one story (ADR-0004 decision 2).
        var dispatchId = Guid.NewGuid().ToString("N")[..8];

        // Raw stdin bytes, read once: forwarded VERBATIM inside the frame; the
        // collapsed fallback re-reads them as text, exactly as Console.In would.
        using var stdinBuf = new MemoryStream();
        await Console.OpenStandardInput().CopyToAsync(stdinBuf);
        var stdinBytes = stdinBuf.ToArray();

        // Resolve the rendezvous. A failure here (e.g. the sun_path guard on a
        // pathological runtime dir) must never cost the user their hook:
        // collapse instead — the shim without a daemon is today's binary.
        string? socketPath = null;
        try { socketPath = RendezvousPaths.Resolve().SocketPath; }
        catch (Exception ex) { await Console.Error.WriteLineAsync($"captAInHook: rendezvous unavailable, dispatching in-process: {ex.Message}"); }

        if (socketPath is not null)
        {
            var outcome = await ShimClient.TryForwardAsync(socketPath,
                new HookRequest(dispatchId, inv.EventName, inv.HarnessName, stdinBytes));
            switch (outcome)
            {
                case ForwardOutcome.Answered a:
                    // Relay VERBATIM: the effect's stdout bytes are the sacred
                    // channel and cross the socket byte-identically.
                    using (var stdout = Console.OpenStandardOutput())
                        stdout.Write(a.StdoutBytes);
                    await Console.Error.WriteLineAsync(a.StderrText);
                    return a.ExitCode;

                case ForwardOutcome.FailedAfterDelivery f:
                    // The daemon may already be running non-idempotent
                    // Background effects: at-most-once forbids re-dispatching.
                    // Zero stdout bytes, visible error, non-blocking exit 1.
                    await Console.Error.WriteLineAsync($"captAInHook: dispatch {dispatchId} failed after delivery: {f.Reason}");
                    return 1;

                case ForwardOutcome.NotDelivered:
                    // Provably never dispatched: spawn a detached daemon so
                    // the NEXT hook rides the warm path (this one collapses —
                    // no hook ever waits for warmup), then fall through.
                    // Spawn first: its warmup overlaps our handler run.
                    DaemonSpawner.SpawnDaemonForNextHook(dispatchId);
                    break;
            }
        }

        return await HookRun.CollapsedAsync(inv,
            new StringReader(System.Text.Encoding.UTF8.GetString(stdinBytes)),
            Console.Out, Console.Error, probe, dispatchId: dispatchId);
    }

    default: // Mode.Collapsed (--no-daemon)
        return await HookRun.CollapsedAsync(inv, Console.In, Console.Out, Console.Error, probe);
}
