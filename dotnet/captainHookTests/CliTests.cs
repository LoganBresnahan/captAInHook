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
