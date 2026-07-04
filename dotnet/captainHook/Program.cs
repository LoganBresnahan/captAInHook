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

// ---- 0. subcommand: `captainHook actors-demo` — drive the F# actor layer ----
if (args.Length > 0 && args[0] == "actors-demo")
{
    await CaptainHook.Demo.ActorsDemo.RunAsync();
    return;
}

// ---- 1. which lifecycle event fired? ----------------------------------------
string? eventName = null;
for (int i = 0; i + 1 < args.Length; i++)
    if (args[i] == "hook") { eventName = args[i + 1]; break; }

string raw = await Console.In.ReadToEndAsync();
JsonElement payload;
try { payload = JsonSerializer.Deserialize<JsonElement>(string.IsNullOrWhiteSpace(raw) ? "{}" : raw); }
catch { payload = JsonSerializer.Deserialize<JsonElement>("{}"); }

// The host also carries the name in the JSON (`hook_event_name`); fall back to it.
eventName ??= payload.TryGetProperty("hook_event_name", out var hen) ? hen.GetString() : null;
eventName = Canon(eventName ?? "UserPromptSubmit");

var evt = new HookEvent(
    Type: eventName,
    SessionId: payload.TryGetProperty("session_id", out var sid) ? sid.GetString() : null,
    Cwd: payload.TryGetProperty("cwd", out var cwd) ? cwd.GetString() : null,
    Payload: payload);

// ---- 2. registry: echo everywhere; a latency probe on UserPromptSubmit so we
//         can watch two handlers fan out concurrently under one budget. --------
var registry = new Registry()
    .On("SessionStart", new EchoHandler())
    .On("UserPromptSubmit", new EchoHandler(), new LatencyProbeHandler(TimeSpan.FromMilliseconds(150)))
    .On("PostToolUse", new EchoHandler());

// ---- 3. dispatch under a latency budget -------------------------------------
// One short dispatchId per invocation: every structured log line this run emits
// carries it, so a digest can stitch the whole dispatch back together.
var dispatchId = Guid.NewGuid().ToString("N")[..8];
var result = await new Dispatcher(registry, budget: TimeSpan.FromSeconds(2)).DispatchAsync(evt, dispatchId);

// ---- 4. effect -> host (stdout); human trace -> stderr ----------------------
Console.Out.Write(ClaudeCode.Serialize(evt, result.Merged));
await Console.Error.WriteLineAsync(result.Trace.Render());

// kebab-case (cavemem style) -> PascalCase (host style)
static string Canon(string s) =>
    s.Contains('-')
        ? string.Concat(s.Split('-').Select(p => p.Length == 0 ? p : char.ToUpperInvariant(p[0]) + p[1..]))
        : s;

// Maps our internal Effect onto Claude Code's hook stdout contract.
// NOTE: field names follow the Agent SDK hook docs; verify against current docs
// before relying on them in a live settings.json wire-up.
static class ClaudeCode
{
    public static string Serialize(HookEvent e, Effect eff) => eff switch
    {
        Effect.Inject inj => J(new { hookSpecificOutput = new { hookEventName = e.Type, additionalContext = inj.Text } }),
        Effect.Decide dec => J(new { hookSpecificOutput = new { hookEventName = e.Type, permissionDecision = dec.Verdict.ToString().ToLowerInvariant(), permissionDecisionReason = dec.Reason ?? "" } }),
        Effect.Replace rep => J(new { hookSpecificOutput = new { hookEventName = e.Type, replaceOutput = rep.Text } }),
        _ => "{}",
    };

    static string J(object o) => JsonSerializer.Serialize(o);
}
