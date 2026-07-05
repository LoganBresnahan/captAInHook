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
        // Lands with the daemon-serve-loop slice (ADR-0004 § Implementation plan).
        // Until then: clear error on stderr, nothing on stdout, exit 1.
        await Console.Error.WriteLineAsync(
            "captAInHook: --daemon is not implemented yet (ADR-0004); run `hook <event>` instead.");
        return 1;

    case Mode.Shim:
        // Socket forwarding lands with shim-forward-or-fallback; until then the
        // shim's fallback IS the whole shim: dispatch in-process.
        return await HookRun.CollapsedAsync(inv, Console.In, Console.Out, Console.Error, probe);

    default: // Mode.Collapsed (--no-daemon)
        return await HookRun.CollapsedAsync(inv, Console.In, Console.Out, Console.Error, probe);
}
