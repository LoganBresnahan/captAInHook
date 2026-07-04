using System.Text.Json;
using CaptainHook.Core;
using CaptainHook.Handlers;

// captAInHook — v0 single-binary (shim + daemon collapsed into one process).
//
// Invocation mirrors cavemem:  captain hook <event>   with the hook payload as
// JSON on stdin and the response effect as JSON on stdout. That is exactly how
// Claude Code drives a hook, so this wires straight into settings.json — and is
// testable in isolation:
//     printf '{...}' | dotnet captainHook.dll hook user-prompt-submit
//
// Which host we speak to is now DATA, not code: `--harness <name>` selects a
// HarnessSpec (default: claude-code) and the spec drives request parsing, the
// capability gate, and the response wire format. See Core/Harness.cs.

// ---- cold-start probe (opt-in): anchor the stopwatch as early as managed code
//      allows, before any real work. Null unless CAPTAINHOOK_COLDSTART=1. -------
var probe = ColdStartProbe.StartIfEnabled();

// ---- 0. subcommand: `captainHook actors-demo` — drive the F# actor layer ----
if (args.Length > 0 && args[0] == "actors-demo")
{
    await CaptainHook.Demo.ActorsDemo.RunAsync();
    return 0;
}

// ---- 1. args: which lifecycle event fired, and for which harness? -----------
string? eventName = null;
var harnessName = "claude-code";
for (int i = 0; i + 1 < args.Length; i++)
{
    if (args[i] == "hook") { eventName ??= args[++i]; }
    else if (args[i] == "--harness") { harnessName = args[++i]; }
}

// Resolve the harness BEFORE touching stdin/stdout: an unknown name must put
// a clear error on stderr and NOTHING on stdout (the host parses stdout).
HarnessSpec spec;
try { spec = new HarnessRegistry().Get(harnessName); }
catch (InvalidOperationException ex)
{
    await Console.Error.WriteLineAsync($"captAInHook: {ex.Message}");
    return 1;
}
probe?.Resolved();

string raw = await Console.In.ReadToEndAsync();
JsonElement payload;
try { payload = JsonSerializer.Deserialize<JsonElement>(string.IsNullOrWhiteSpace(raw) ? "{}" : raw); }
catch { payload = JsonSerializer.Deserialize<JsonElement>("{}"); }

// The spec knows which payload fields carry the event name / session / cwd;
// the CLI arg (kebab-case) wins over the payload's own field.
var evt = Harness.ParseEvent(spec, eventName, payload);
probe?.Parsed();

// ---- 2. registry: echo everywhere; a latency probe on UserPromptSubmit so we
//         can watch two handlers fan out concurrently under one budget. --------
var registry = new Registry()
    .On("SessionStart", new EchoHandler())
    .On("UserPromptSubmit", new EchoHandler())
    .On("PostToolUse", new EchoHandler());

// Fan-out demo probe: +150ms and a second inject on every UserPromptSubmit.
// Opt-in via env so a LIVE deployment doesn't tax every real prompt with it.
// (Register before the Dispatcher ctor — workers spawn from a registry snapshot.)
if (Environment.GetEnvironmentVariable("CAPTAINHOOK_PROBE") == "1")
    registry.On("UserPromptSubmit", new LatencyProbeHandler(TimeSpan.FromMilliseconds(150)));

// ---- 3. dispatch under a latency budget -------------------------------------
// One short dispatchId per invocation: every structured log line this run emits
// carries it, so a digest can stitch the whole dispatch back together.
var dispatchId = Guid.NewGuid().ToString("N")[..8];
var dispatcher = new Dispatcher(registry, budget: TimeSpan.FromSeconds(2));
probe?.DispatcherBuilt();
var result = await dispatcher.DispatchAsync(evt, dispatchId);
probe?.Dispatched();

// ---- 4. effect -> host (stdout); human trace -> stderr ----------------------
// Gate first (a harness only ever receives effect kinds its spec declared),
// then serialize through the adapter the spec selected.
var final = Harness.ApplyCapabilityGate(spec, evt, result.Merged, dispatchId);
Console.Out.Write(ResponseAdapters.Get(spec.ResponseAdapter).Serialize(evt, final));
await Console.Error.WriteLineAsync(result.Trace.Render());

probe?.Emit(dispatchId);   // -> JSONL/stderr, never stdout; after the effect is written
return 0;
