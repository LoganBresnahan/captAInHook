using System.Diagnostics;
using System.Text.Json;
using CaptainHook.Core;

namespace CaptainHook.Tests;

// The SHIPPED `examples/payloads/agent-shell-history.sh`, run as a real
// process against a temp history file. The envelopes are built by the real
// encoder (`ExecWire.Envelope`), so `event.harness` is exactly what the engine
// writes. What these pin:
//   - a shell call is recognised by CONTENT (`tool_input.command`), whatever
//     the host calls the tool, and nothing else is recorded;
//   - one line per command, newlines flattened to a visible marker (never
//     `;`), JSON escapes decoded, tagged `# agent:<harness>/<session8>`;
//   - it appends and never truncates, and it never speaks on stdout (Noop).
public class AgentShellHistoryTests : IDisposable
{
    private readonly TempRuntimeDir _tmp = new();
    private readonly string _history;

    public AgentShellHistoryTests()
    {
        Directory.CreateDirectory(_tmp.Path);
        _history = Path.Combine(_tmp.Path, "history");
    }

    public void Dispose() => _tmp.Dispose();

    private static string Shipped => Path.Combine(DogfoodInfra.PayloadsDir(), "agent-shell-history.sh");

    private static string Envelope(string payloadJson, string? harness = "claude-code",
        string? session = "56544d6f-d8fb-40a5-96ca-d268b27e5bf3")
    {
        using var doc = JsonDocument.Parse(payloadJson);
        return ExecWire.Envelope(
            new HookEvent("PostToolUse", session, "/w", doc.RootElement.Clone(), harness), "d-1");
    }

    private async Task<string> RunAsync(string envelope)
    {
        var psi = new ProcessStartInfo(Shipped)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.Environment["PATH"] = "/usr/bin:/bin";
        psi.Environment["HOME"] = _tmp.Path;
        psi.Environment["AGENT_HISTORY_FILE"] = _history;
        using var p = Process.Start(psi)!;
        await p.StandardInput.WriteLineAsync(envelope);
        p.StandardInput.Close();
        var stdout = await p.StandardOutput.ReadToEndAsync();
        await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);
        Assert.Equal(0, p.ExitCode);
        return stdout;
    }

    private string[] Lines() =>
        File.Exists(_history) ? File.ReadAllLines(_history) : [];

    [Theory]
    [InlineData("Bash")]
    [InlineData("run_shell")]
    public async Task AShellCommand_IsOneTaggedLine_WhateverTheToolIsCalled(string toolName)
    {
        var stdout = await RunAsync(Envelope(
            $$$"""{"tool_name":"{{{toolName}}}","tool_input":{"command":"git status"}}"""));

        Assert.Equal("", stdout);
        Assert.Equal(["git status  # agent:claude-code/56544d6f"], Lines());
    }

    [Fact]
    public async Task AMultiLineCommand_IsFlattenedWithAVisibleMarker_NeverASemicolon_AndDecoded()
    {
        await RunAsync(Envelope(
            """{"tool_name":"Bash","tool_input":{"command":"cat <<'EOF' > x\nhi \"there\" \\ é\nEOF\n"}}"""));

        Assert.Equal(["cat <<'EOF' > x ⏎ hi \"there\" \\ é ⏎ EOF  # agent:claude-code/56544d6f"], Lines());
    }

    [Theory]
    [InlineData("""{"tool_name":"Edit","tool_input":{"file_path":"/w/a.cs","old_string":"a","new_string":"b"}}""")]
    [InlineData("""{"tool_name":"Read","tool_input":{"file_path":"/w/a.cs"}}""")]
    [InlineData("""{"tool_name":"Bash","tool_input":{"command":"   \n  "}}""")]
    [InlineData("""{"tool_name":"Weird","tool_input":{"command":{"not":"a string"}}}""")]
    [InlineData("""{"prompt":"hi"}""")]
    public async Task AnythingButANonEmptyCommandString_RecordsNothing(string payload)
    {
        var stdout = await RunAsync(Envelope(payload));

        Assert.Equal("", stdout);
        Assert.False(File.Exists(_history));
    }

    [Fact]
    public async Task ItAppends_NeverTruncates_AndTagsAnUnknownHarnessAndSessionHonestly()
    {
        File.WriteAllText(_history, "ls -la\n");

        await RunAsync(Envelope("""{"tool_input":{"command":"make"}}""", harness: null, session: null));

        Assert.Equal(["ls -la", "make  # agent:unknown/nosessio"], Lines());
    }
}
