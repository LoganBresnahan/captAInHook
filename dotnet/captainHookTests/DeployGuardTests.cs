using System.Diagnostics;

namespace CaptainHook.Tests;

// ADR-0019 (Rejected in favour of this) — the SHIPPED
// `examples/payloads/deploy-guard.sh`, run as a real process. The guard used
// to open with a `"tool_name":"Bash"` gate, so a second harness whose shell is
// `run_shell` (moby, 2026-08-26) was answered `allow` without being looked at:
// a guard that could not see, reading as a guard that saw. The fix is that the
// guard has no tool-name gate at all — what it inspects is the call's content
// (the live path + a mutation verb), whatever the host calls the tool. These
// pin that a host's tool name can neither open nor close the gate.
public class DeployGuardTests
{
    private static string Shipped => Path.Combine(DogfoodInfra.PayloadsDir(), "deploy-guard.sh");

    private static string Envelope(string toolName, string command) =>
        $$"""{"v":1,"dispatchId":"d-1","event":{"type":"PreToolUse","payload":{"hook_event_name":"PreToolUse","tool_name":"{{toolName}}","tool_input":{"command":"{{command}}"""
        + "\"}}}}";

    private static async Task<string> RunAsync(string envelope)
    {
        var psi = new ProcessStartInfo(Shipped)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.Environment["PATH"] = "/usr/bin:/bin";
        using var p = Process.Start(psi)!;
        await p.StandardInput.WriteLineAsync(envelope);
        p.StandardInput.Close();
        var stdout = await p.StandardOutput.ReadToEndAsync();
        await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);
        Assert.Equal(0, p.ExitCode);
        return stdout.Trim();
    }

    [Theory]
    [InlineData("Bash")]          // Claude Code's shell
    [InlineData("run_shell")]     // moby's — the one that walked through
    [InlineData("some_new_tool")] // a host nobody has met yet
    public async Task AMutationOfTheLiveTree_Asks_WhateverTheHostCallsTheTool(string toolName)
    {
        var verdict = await RunAsync(Envelope(toolName, "rm -rf ~/.captainHook/bin/ui"));

        Assert.Contains("\"verdict\":\"ask\"", verdict);
        Assert.Contains("\"dispatchId\":\"d-1\"", verdict);
    }

    [Theory]
    [InlineData("run_shell", "ls ~/.captainHook/bin")]   // the live tree, read-only
    [InlineData("run_shell", "rm -rf /tmp/scratch")]     // a mutation, elsewhere
    [InlineData("Bash", "git status")]                   // neither
    public async Task AnythingElse_IsAllowed(string toolName, string command)
    {
        var verdict = await RunAsync(Envelope(toolName, command));

        Assert.Contains("\"verdict\":\"allow\"", verdict);
    }
}
