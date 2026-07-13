using System.Text;
using System.Text.Json;
using CaptainHook.Core;
using CaptainHook.Handlers;
using CaptainHook.Wire;
using static CaptainHook.Tests.TestUtil;

namespace CaptainHook.Tests;

// demo-payload-scripts (ADR-0010 phase 6): the acceptance proof that the whole
// exec-handler seam runs REAL committed payload scripts end-to-end through the
// daemon — a live resident child on a before-tools event (retriever/PreToolUse,
// inject) and a oneshot on a session edge (memory/Stop, durable side effect).
// The scripts are the committed examples/payloads/*.sh, run verbatim; only
// $HOME is redirected (via the entry's env override) so the demo's files land
// in a temp dir, never the live ~/.captainHook tree.

public class DemoPayloadTests : IDisposable
{
    private readonly TempRuntimeDir _tmp = new();
    private readonly string _home;
    private readonly string _payloadsDir;

    public DemoPayloadTests()
    {
        Directory.CreateDirectory(_tmp.Path);
        _home = Path.Combine(_tmp.Path, "home");
        Directory.CreateDirectory(Path.Combine(_home, ".captainHook"));
        _payloadsDir = FindPayloadsDir();
        ChildRecords.OverrideDir = Path.Combine(_tmp.Path, "children");
    }

    public void Dispose()
    {
        ChildRecords.OverrideDir = null;
        _tmp.Dispose();
    }

    /// Walk up from the test assembly to the committed examples/payloads/ dir —
    /// the E2E runs the REAL scripts, not a copy.
    private static string FindPayloadsDir()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var cand = Path.Combine(dir, "examples", "payloads");
            if (File.Exists(Path.Combine(cand, "retriever.sh"))) return cand;
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        throw new DirectoryNotFoundException("examples/payloads not found above the test assembly");
    }

    private string WriteHandlers()
    {
        // env.HOME redirects the child's home to the temp dir — the demos write
        // demo-notes.txt / demo-memory.log under $HOME/.captainHook, so this
        // keeps the live tree untouched while running the scripts verbatim.
        var handlers = new
        {
            version = 1,
            handlers = new object[]
            {
                new
                {
                    name = "retriever",
                    command = Path.Combine(_payloadsDir, "retriever.sh"),
                    events = new[] { "PreToolUse" },
                    mode = "resident",
                    failMode = "open",
                    budgetMs = 2000,
                    readinessTimeoutMs = 5000,
                    env = new Dictionary<string, string> { ["HOME"] = _home },
                },
                new
                {
                    name = "memory",
                    command = Path.Combine(_payloadsDir, "memory.sh"),
                    events = new[] { "Stop" },
                    mode = "oneshot",
                    failMode = "open",
                    budgetMs = 3000,
                    env = new Dictionary<string, string> { ["HOME"] = _home },
                },
            },
        };
        var path = Path.Combine(_tmp.Path, "handlers.json");
        File.WriteAllText(path, JsonSerializer.Serialize(handlers));
        return path;
    }

    private static string Body(ForwardOutcome o) =>
        Encoding.UTF8.GetString(Assert.IsType<ForwardOutcome.Answered>(o).StdoutBytes);

    [Fact]
    public async Task Daemon_RunsBothDemoPayloads_RetrieverInjects_MemoryPersists()
    {
        if (ProcessGroup.SetsidPath is null) return;   // xunit 2.x: no dynamic skip

        // Seed a note the retriever can find, keyed on a word the PreToolUse
        // envelope will carry.
        await File.WriteAllTextAsync(
            Path.Combine(_home, ".captainHook", "demo-notes.txt"),
            "rotate the payments credentials before deploying\n");

        var handlersPath = WriteHandlers();
        using var stop = new CancellationTokenSource();
        var daemon = Task.Run(() => DaemonHost.RunAsync(
            _tmp.Paths, NoHarnessDir(), stop.Token, handlersPath: handlersPath));
        await PollUntilAsync(async () =>
            await ShimClient.TryForwardAsync(_tmp.Paths.SocketPath,
                new HookRequest("warmup00", "session-start", "claude-code", "{}"u8.ToArray()))
                is ForwardOutcome.Answered,
            TimeSpan.FromSeconds(15), "daemon up");

        // --- resident retriever on a before-tools event -------------------
        // The PreToolUse payload mentions "payments" → the retriever greps its
        // notes and injects the matching line. Poll: the warm child may still
        // be reaching {"ready":1} on the first hook.
        var prePayload = """{"tool_name":"bash","tool_input":{"command":"deploy payments"}}""";
        string preBody = "";
        await PollUntilAsync(async () =>
        {
            preBody = Body(await ShimClient.TryForwardAsync(_tmp.Paths.SocketPath,
                new HookRequest("pretool01", "pre-tool-use", "claude-code",
                    Encoding.UTF8.GetBytes(prePayload))));
            return preBody.Contains("retrieved", StringComparison.Ordinal);
        }, TimeSpan.FromSeconds(10), "retriever injects a retrieved note");
        Assert.Contains("payments", preBody);   // the seeded note crossed into the inject

        // --- oneshot memory on a session edge -----------------------------
        var stopOutcome = await ShimClient.TryForwardAsync(_tmp.Paths.SocketPath,
            new HookRequest("stop0001", "stop", "claude-code",
                Encoding.UTF8.GetBytes("""{"session_id":"s-demo"}""")));
        JsonDocument.Parse(Body(stopOutcome));   // one JSON object — invariant 1

        var memoryLog = Path.Combine(_home, ".captainHook", "demo-memory.log");
        await PollUntilAsync(() => Task.FromResult(File.Exists(memoryLog)),
            TimeSpan.FromSeconds(5), "memory oneshot wrote its log");
        var logged = await File.ReadAllTextAsync(memoryLog);
        Assert.Contains("session=s-demo", logged);   // the durable side effect landed

        // --- drain: the warm resident child dies with the daemon ----------
        stop.Cancel();
        Assert.Equal(0, await daemon.WaitAsync(TimeSpan.FromSeconds(15)));
    }
}
