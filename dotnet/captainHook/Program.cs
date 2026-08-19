using CaptainHook.Api;
using CaptainHook.Core;
using CaptainHook.Wire;

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

// Wire-layer diagnostics flow into Actors.Log — bound before anything that
// could emit (ADR-0004 decision 7 amendment; the AOT shim binds differently).
WireLogBridge.Bind();

var inv = Invocation.Parse(args);
switch (inv.Mode)
{
    case Mode.ActorsDemo:
        await CaptainHook.Demo.ActorsDemo.RunAsync();
        return 0;

    case Mode.Daemon:
    {
        // captaind: warm once, serve over the socket, drain on SIGTERM or
        // after the mandatory idle window. See Core/DaemonHost.cs.
        // CAPTAINHOOK_IDLE_MS is daemon-start configuration (ADR-0004 d1).
        TimeSpan? idle = null;
        if (long.TryParse(Environment.GetEnvironmentVariable("CAPTAINHOOK_IDLE_MS"), out var ms) && ms > 0)
            idle = TimeSpan.FromMilliseconds(ms);
        // Dispatch policy: the daemon reads the resolved path per dispatch
        // (ADR-0006 decision 5). The file is optional — absent => allow all.
        // Management API port (ADR-0007 decision 2): 4665 unless
        // CAPTAINHOOK_API_PORT moves it; 0 disables. Resolution is silent
        // like IDLE_MS above — malformed falls back to the default.
        // SSE heartbeat cadence (CAPTAINHOOK_SSE_HEARTBEAT_MS): a test knob so the
        // GUI's stall watchdog can be pinned end-to-end in seconds; unset ⇒ the
        // tail's own default, and the trail path stays the one resolver everyone
        // shares (DaemonHost pins snapshot and stream to the SAME file).
        var heartbeat = ApiHost.ResolveHeartbeat(Environment.GetEnvironmentVariable("CAPTAINHOOK_SSE_HEARTBEAT_MS"));
        return await DaemonHost.RunAsync(idleWindow: idle, policyPath: DispatchPolicy.ResolvePath(),
            apiPort: ApiHost.ResolvePort(Environment.GetEnvironmentVariable("CAPTAINHOOK_API_PORT")),
            handlersPath: ExecHandlersFile.ResolvePath(),
            watchPath: WatchRules.ResolvePath(),
            sse: heartbeat is null ? null : new SseOptions(WireJsonl.DefaultLogPath(), Heartbeat: heartbeat));
    }

    case Mode.Ui:
        // Human command (ADR-0008 decision 3): open the GUI with the bearer
        // token in the URL fragment. Stdout is fine here — not hook mode.
        return await UiVerb.RunAsync(Console.Out, Console.Error);

    case Mode.MailSend:
        // The mail verbs (ADR-0016 decision 7). `send` is a human/CLI verb —
        // stdout is free for its confirmation line. `digest` is the READ
        // path: an exec-wire handler command whose stdout IS its answer to
        // the engine (registered in handlers.json; flags after the subverb
        // are its registration args, invisible to Invocation on purpose).
        // `status` (ADR-0017 d2) is the third shape: a human DISPLAY verb whose
        // stdout is a status bar's line, read-only and ruleless — it interrupts
        // nothing, so it needs no consent surface of its own. `reap`
        // (ADR-0018 d6) is a WRITE verb over standing rather than mail: it
        // removes one mailbox's cursor and says so on the trail, and it reads
        // no stdin because a reap is performed on an address, not by a window.
        // `watch --once` (ADR-0017 d4) runs the watcher's brain once and PRINTS
        // its verdict — verification of the rules, never a schedule; the
        // in-daemon actor is what runs the brain for real.
        return inv.EventName switch
        {
            "digest" => CaptainHook.Mail.MailDigest.Run(args[2..], Console.In, Console.Out, Console.Error),
            "status" => CaptainHook.Mail.MailStatus.Run(args[2..], Console.In, Console.Out, Console.Error),
            "reap" => CaptainHook.Mail.MailReap.Run(args[2..], Console.Out, Console.Error),
            "watch" => CaptainHook.Mail.MailWatch.Run(args[2..], Console.In, Console.Out, Console.Error),
            _ => CaptainHook.Mail.MailSend.Run(inv.EventName, Console.In, Console.Out, Console.Error),
        };

    case Mode.Doctor:
    {
        // Human command, not hook mode: the report goes to stdout on purpose.
        var verdicts = await Doctor.RunAsync();
        // doctor-orphans (ADR-0010 phase 8): a second entity class — resident
        // children that outlived their daemon. Report-only; never signals them.
        var orphans = Doctor.SweepOrphans();
        if (verdicts.Count == 0 && orphans.Count == 0)
            Console.WriteLine("doctor: nothing to reap — rendezvous dir clean, no orphaned children");
        foreach (var v in verdicts)
            Console.WriteLine($"doctor: captaind-{v.Version}: {v.Action} — {v.Detail}");
        foreach (var o in orphans)
            Console.WriteLine($"doctor: {o.Version}: {o.Action} — {o.Detail}");
        return 0;
    }

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
            Console.Out, Console.Error, probe, dispatchId: dispatchId,
            policyPath: DispatchPolicy.ResolvePath(), handlersPath: ExecHandlersFile.ResolvePath());
    }

    default: // Mode.Collapsed (--no-daemon)
        return await HookRun.CollapsedAsync(inv, Console.In, Console.Out, Console.Error, probe,
            policyPath: DispatchPolicy.ResolvePath(), handlersPath: ExecHandlersFile.ResolvePath());
}
