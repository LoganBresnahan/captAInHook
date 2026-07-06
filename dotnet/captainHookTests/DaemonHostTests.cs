using System.Text;
using System.Text.Json;
using CaptainHook.Core;
using CaptainHook.Wire;
using static CaptainHook.Tests.TestUtil;

namespace CaptainHook.Tests;

// daemon-serve-loop (ADR-0004 decisions 1–3): warm once, serve many. These
// tests run a REAL DaemonHost on throwaway rendezvous paths and speak to it
// through the REAL ShimClient — the exact wire production uses.

public class DaemonHostTests : IAsyncLifetime
{
    private readonly TempRuntimeDir _dir = new();
    private readonly CancellationTokenSource _stop = new();
    private Task<int>? _daemon;

    private string Sock => _dir.Paths.SocketPath;

    /// Nonexistent harness dir: embedded defaults only, user's real
    /// ~/.captainHook never touched.
    private static string NoHarnessDir => Path.Combine("/tmp", "chk-none-" + Guid.NewGuid().ToString("N")[..8]);

    public async Task InitializeAsync()
    {
        _daemon = Task.Run(() => DaemonHost.RunAsync(_dir.Paths, NoHarnessDir, _stop.Token));
        // Listening ⟺ ready: poll connect until the daemon is up (bounded).
        await PollUntilAsync(async () =>
        {
            var probe = await ShimClient.TryForwardAsync(Sock,
                new HookRequest("probe000", "session-start", "claude-code", "{}"u8.ToArray()));
            return probe is ForwardOutcome.Answered;
        }, TimeSpan.FromSeconds(15), "daemon starts listening");
    }

    public async Task DisposeAsync()
    {
        _stop.Cancel();
        if (_daemon is not null)
            Assert.Equal(0, await _daemon.WaitAsync(TimeSpan.FromSeconds(10)));
        _dir.Dispose();
    }

    [Fact]
    public async Task WarmDispatch_AnswersWithTheEffect_UnderTheShimsDispatchId()
    {
        var outcome = await ShimClient.TryForwardAsync(Sock, new HookRequest(
            "cafe0001", "user-prompt-submit", "claude-code",
            Encoding.UTF8.GetBytes("""{"prompt":"hello daemon"}""")));

        var a = Assert.IsType<ForwardOutcome.Answered>(outcome);
        Assert.Equal(0, a.ExitCode);
        // The effect is the claude-code echo inject, produced daemon-side.
        using var doc = JsonDocument.Parse(a.StdoutBytes);
        Assert.Equal("UserPromptSubmit",
            doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("hookEventName").GetString());
        Assert.NotEqual("", a.StderrText.Trim());   // human trace relayed
    }

    [Fact]
    public async Task ManyDispatches_OneWarmDaemon_ServesThemAll()
    {
        // The daemon's whole point: construction paid once, N dispatches served.
        for (var i = 0; i < 5; i++)
        {
            var outcome = await ShimClient.TryForwardAsync(Sock, new HookRequest(
                $"many000{i}", "user-prompt-submit", "claude-code", "{}"u8.ToArray()));
            Assert.IsType<ForwardOutcome.Answered>(outcome);
        }
    }

    [Fact]
    public async Task UnknownHarness_DecidedDaemonSide_Exit1_ZeroStdoutBytes()
    {
        var outcome = await ShimClient.TryForwardAsync(Sock, new HookRequest(
            "nohar001", "user-prompt-submit", "no-such-harness", "{}"u8.ToArray()));

        var a = Assert.IsType<ForwardOutcome.Answered>(outcome);
        Assert.Equal(1, a.ExitCode);
        Assert.Empty(a.StdoutBytes);
        Assert.Contains("unknown harness", a.StderrText);
    }

    [Fact]
    public async Task MalformedRequestFrame_FailsThatConnection_DaemonKeepsServing()
    {
        // A garbage frame gets an exit-1 response (or a dropped connection) —
        // and the NEXT request must be served normally: one connection's
        // failure is never the daemon's.
        using (var sock = new System.Net.Sockets.Socket(
            System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream,
            System.Net.Sockets.ProtocolType.Unspecified))
        {
            await sock.ConnectAsync(new System.Net.Sockets.UnixDomainSocketEndPoint(Sock));
            await using var s = new System.Net.Sockets.NetworkStream(sock);
            await Frame.WriteAsync(s, "utter garbage {{{"u8.ToArray());
            var reply = HookResponse.Decode((await Frame.ReadAsync(s))!);
            Assert.Equal(1, reply.ExitCode);
            Assert.Empty(reply.StdoutBytes);
        }

        var after = await ShimClient.TryForwardAsync(Sock, new HookRequest(
            "after001", "user-prompt-submit", "claude-code", "{}"u8.ToArray()));
        Assert.IsType<ForwardOutcome.Answered>(after);
    }
}

public class DaemonRaceTests
{
    [Fact]
    public async Task SecondDaemon_LosesTheRace_Exits0()
    {
        using var dir = new TempRuntimeDir();
        using var stop = new CancellationTokenSource();
        var first = Task.Run(() => DaemonHost.RunAsync(dir.Paths, "/tmp/chk-none", stop.Token));
        await PollUntilAsync(
            () => Task.FromResult(File.Exists(dir.Paths.SocketPath)),
            TimeSpan.FromSeconds(15), "first daemon binds");

        // The loser must exit 0 promptly — some other daemon won, that IS success.
        var second = await DaemonHost.RunAsync(dir.Paths, "/tmp/chk-none", CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, second);

        stop.Cancel();
        Assert.Equal(0, await first.WaitAsync(TimeSpan.FromSeconds(10)));
    }
}
