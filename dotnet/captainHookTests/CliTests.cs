using System.Text.Json;
using CaptainHook.Core;
using CaptainHook.Wire;

namespace CaptainHook.Tests;

// three-mode-dispatch (ADR-0004 decision 1): mode selection is a pure function
// of argv, and the collapsed pipeline is callable in-process with injected
// streams — so the stdout contract (exactly one effect JSON, nothing else) is
// assertable without spawning a process.

public class InvocationParseTests
{
    [Fact]
    public void HookEvent_DefaultsToShimMode()
    {
        var inv = Invocation.Parse(["hook", "user-prompt-submit"]);
        Assert.Equal(Mode.Shim, inv.Mode);
        Assert.Equal("user-prompt-submit", inv.EventName);
        Assert.Equal("claude-code", inv.HarnessName);   // default harness
    }

    [Fact]
    public void NoDaemonFlag_ForcesCollapsedMode()
    {
        var inv = Invocation.Parse(["hook", "user-prompt-submit", "--no-daemon"]);
        Assert.Equal(Mode.Collapsed, inv.Mode);
        Assert.Equal("user-prompt-submit", inv.EventName);
    }

    [Fact]
    public void DaemonFlag_SelectsDaemonMode()
    {
        Assert.Equal(Mode.Daemon, Invocation.Parse(["--daemon"]).Mode);
    }

    [Fact]
    public void DaemonFlag_WinsOverNoDaemon()
    {
        // A daemon invocation is never a hook dispatch; --no-daemon has nothing
        // to force. Deliberate precedence, not accident-of-ordering.
        Assert.Equal(Mode.Daemon, Invocation.Parse(["--daemon", "--no-daemon"]).Mode);
        Assert.Equal(Mode.Daemon, Invocation.Parse(["--no-daemon", "--daemon"]).Mode);
    }

    [Fact]
    public void HarnessFlag_OverridesDefault()
    {
        var inv = Invocation.Parse(["hook", "session-start", "--harness", "generic-json"]);
        Assert.Equal("generic-json", inv.HarnessName);
    }

    [Fact]
    public void ActorsDemo_IsItsOwnMode()
    {
        Assert.Equal(Mode.ActorsDemo, Invocation.Parse(["actors-demo"]).Mode);
    }

    [Fact]
    public void HookWithoutEvent_LeavesEventNameNull()
    {
        // The payload's own event field then decides (Harness.ParseEvent);
        // parsing must not throw on a trailing bare `hook`.
        Assert.Null(Invocation.Parse(["hook"]).EventName);
        Assert.Null(Invocation.Parse([]).EventName);
    }
}

public class HookRunCollapsedTests
{
    private static async Task<(int Exit, string Stdout, string Stderr)> RunAsync(
        Invocation inv, string stdinJson)
    {
        // Point the registry's override layer at a nonexistent dir so the user's
        // real ~/.captainHook is never involved (embedded defaults only).
        var harnessDir = Path.Combine(Path.GetTempPath(), "captainhook-no-such-dir-" + Guid.NewGuid().ToString("N"));
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await HookRun.CollapsedAsync(
            inv, new StringReader(stdinJson), stdout, stderr, harnessDir: harnessDir);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public async Task Dispatch_WritesExactlyOneJsonObjectToStdout()
    {
        var (exit, outText, errText) = await RunAsync(
            new Invocation(Mode.Collapsed, "user-prompt-submit", "claude-code"),
            """{"prompt":"hi"}""");

        Assert.Equal(0, exit);
        // Sacred invariant: stdout is the protocol channel — one parseable JSON
        // object and nothing else. The human trace goes to stderr.
        var doc = JsonDocument.Parse(outText);   // throws on trailing garbage
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        Assert.NotEqual("", errText.Trim());     // trace rendered on stderr
    }

    [Fact]
    public async Task UnknownHarness_Exit1_StdoutStaysEmpty()
    {
        var (exit, outText, errText) = await RunAsync(
            new Invocation(Mode.Collapsed, "user-prompt-submit", "no-such-harness"),
            """{"prompt":"hi"}""");

        Assert.Equal(1, exit);
        Assert.Equal("", outText);                       // NOTHING on stdout
        Assert.Contains("unknown harness", errText);     // clear error on stderr
    }

    [Fact]
    public async Task MalformedStdin_StillYieldsOneEffect()
    {
        var (exit, outText, _) = await RunAsync(
            new Invocation(Mode.Collapsed, "user-prompt-submit", "claude-code"),
            "not json at all");

        Assert.Equal(0, exit);   // fail-open: garbage payload degrades to {}
        Assert.Equal(JsonValueKind.Object, JsonDocument.Parse(outText).RootElement.ValueKind);
    }
}

/// ADR-0006 Phase 3/5 (event-level-deny-shortcircuit, wired) — driven through
/// the real collapsed pipeline with injected streams AND a real policy FILE (so
/// PolicyResolution.Resolve runs end-to-end). The invariant-1 property: a
/// policy-denied hook's STDOUT is byte-indistinguishable from an uneventful
/// hook's, dispatch is skipped, and the daemon site answers identically (the
/// no-drift cross-check lives in DaemonPolicyTests).
public class HookRunPolicyDenyTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "captainhook-collapsed-policy-" + Guid.NewGuid().ToString("N"));

    public HookRunPolicyDenyTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private static Invocation Ups => new(Mode.Collapsed, "user-prompt-submit", "claude-code");

    /// Write a policy file and return its path (null content => no file at all).
    private string? PolicyFile(string? json)
    {
        if (json is null) return null;
        var path = Path.Combine(_dir, "dispatch-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        File.WriteAllText(path, json);
        return path;
    }

    private async Task<(int Exit, string Stdout, string Stderr)> RunAsync(
        Invocation inv, string stdinJson, string? policyPath = null)
    {
        var harnessDir = Path.Combine(Path.GetTempPath(), "captainhook-no-such-dir-" + Guid.NewGuid().ToString("N"));
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await HookRun.CollapsedAsync(
            inv, new StringReader(stdinJson), stdout, stderr, harnessDir: harnessDir, policyPath: policyPath);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private const string DenyUpsJson =
        """{ "version": 1, "rules": [ { "event": "UserPromptSubmit", "decision": "deny" } ] }""";

    [Fact]
    public async Task EventLevelDeny_StdoutIsByteIdenticalToUneventfulHook()
    {
        // Baseline — a genuinely uneventful hook: Stop has no registered handler,
        // so the dispatch merges to Noop and emits the bare wire form.
        var uneventful = await RunAsync(new Invocation(Mode.Collapsed, "stop", "claude-code"), "{}");
        Assert.Equal("{}", uneventful.Stdout);

        // Control — WITHOUT policy, UserPromptSubmit echoes: it is NOT inert.
        var worked = await RunAsync(Ups, """{"prompt":"hi"}""");
        Assert.Contains("additionalContext", worked.Stdout);

        // The SAME UserPromptSubmit under an event-level deny emits bytes a
        // harness cannot distinguish from the uneventful hook (invariant 1).
        var denied = await RunAsync(Ups, """{"prompt":"hi"}""", policyPath: PolicyFile(DenyUpsJson));
        Assert.Equal(0, denied.Exit);
        Assert.Equal("{}", denied.Stdout);                  // the bare Noop form
        Assert.Equal(uneventful.Stdout, denied.Stdout);     // == an uneventful hook
        Assert.NotEqual(worked.Stdout, denied.Stdout);      // and the deny is what silenced it
    }

    [Fact]
    public async Task EventLevelDeny_SkipsDispatchEntirely_NoWorkerAsked()
    {
        using var captured = new CapturedLog();

        await RunAsync(Ups, """{"prompt":"hi"}""", policyPath: PolicyFile(DenyUpsJson));

        // The dispatcher always logs dispatch.start when it runs (Dispatcher.cs).
        // Its ABSENCE proves the short-circuit returned before building the
        // dispatcher — no worker asked, no budget spent.
        Assert.DoesNotContain(captured.Events, e => e.Evt == "dispatch.start");
    }

    [Fact]
    public async Task EventLevelDeny_TraceGoesToStderr_NotStdout()
    {
        // Decision 3: the harness can tell a skip from an uneventful hook ONLY by
        // our trail — which rides stderr, never stdout.
        var denied = await RunAsync(Ups, """{"prompt":"hi"}""", policyPath: PolicyFile(DenyUpsJson));
        Assert.Equal("{}", denied.Stdout);
        Assert.Contains("policy", denied.Stderr);
    }

    [Fact]
    public async Task MalformedPolicyFile_DeniesEveryHook_Loudly()
    {
        // ADR-0006 decision 4: a present-but-unparseable file Noops every hook —
        // "{}" on stdout, and the fault named loudly on the trail (stderr).
        var denied = await RunAsync(Ups, """{"prompt":"hi"}""", policyPath: PolicyFile("{ this is not json"));
        Assert.Equal(0, denied.Exit);
        Assert.Equal("{}", denied.Stdout);
        Assert.Contains("MALFORMED", denied.Stderr);
    }

    [Fact]
    public async Task HandlerExclusion_DropsHandler_ButStillDispatches()
    {
        using var captured = new CapturedLog();
        // Excluding the only UPS handler (echo) yields the same "{}" as an
        // event-deny — but via the DISPATCH path, not the short-circuit: the
        // dispatcher IS built (dispatch.start present), echo is just filtered.
        var excludeEcho = """{ "version": 1, "rules": [ { "handler": "echo", "decision": "deny" } ] }""";
        var (exit, stdout, _) = await RunAsync(Ups, """{"prompt":"hi"}""", policyPath: PolicyFile(excludeEcho));

        Assert.Equal(0, exit);
        Assert.Equal("{}", stdout);   // echo dropped => Noop
        Assert.Contains(captured.Events, e => e.Evt == "dispatch.start");   // but it DID dispatch
    }

    [Fact]
    public async Task AllowPolicyFile_DoesNotShortCircuit_DispatchRunsNormally()
    {
        // A version-only file (default allow, no rules) leaves the pipeline
        // untouched — the echo still fires.
        var (exit, stdout, _) = await RunAsync(Ups, """{"prompt":"hi"}""", policyPath: PolicyFile("""{ "version": 1 }"""));
        Assert.Equal(0, exit);
        Assert.Contains("additionalContext", stdout);
    }

    [Fact]
    public async Task AbsentPolicyFile_IsTodaysBehavior()
    {
        // A path to a file that does not exist => Absent => allow all.
        var missing = Path.Combine(_dir, "no-such-dispatch.json");
        var (exit, stdout, _) = await RunAsync(Ups, """{"prompt":"hi"}""", policyPath: missing);
        Assert.Equal(0, exit);
        Assert.Contains("additionalContext", stdout);
    }

    [Fact]
    public async Task NullPolicyPath_IsTodaysBehavior()
    {
        // No policy path at all (the pre-slice default) => allow all.
        var (exit, stdout, _) = await RunAsync(Ups, """{"prompt":"hi"}""");
        Assert.Equal(0, exit);
        Assert.Contains("additionalContext", stdout);
    }
}
