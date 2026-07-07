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

// ADR-0006 Phase 5 (evaluator-both-paths) — dispatch policy on the DAEMON path,
// and the adversarial no-drift check: the same policy file + event must produce
// byte-identical answers on the daemon and collapsed paths (both route through
// the one shared HookRun.PolicyGateFor). A real daemon reading a real policy
// file per dispatch, driven over the real ShimClient wire.
public class DaemonPolicyTests : IAsyncLifetime
{
    private readonly TempRuntimeDir _dir = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly string _policyDir =
        Path.Combine(Path.GetTempPath(), "captainhook-daemon-policy-" + Guid.NewGuid().ToString("N"));
    private Task<int>? _daemon;
    private string _policyPath = "";

    private string Sock => _dir.Paths.SocketPath;
    private static string NoHarnessDir => Path.Combine("/tmp", "chk-none-" + Guid.NewGuid().ToString("N")[..8]);

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_policyDir);
        _policyPath = Path.Combine(_policyDir, "dispatch.json");
        // Deny UserPromptSubmit at the event level; leave everything else alone.
        File.WriteAllText(_policyPath,
            """{ "version": 1, "rules": [ { "event": "UserPromptSubmit", "decision": "deny" } ] }""");

        _daemon = Task.Run(() => DaemonHost.RunAsync(_dir.Paths, NoHarnessDir, _stop.Token, policyPath: _policyPath));
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
        try { Directory.Delete(_policyDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task EventLevelDeny_DaemonAnswersByteIdenticalNoop_AndCollapsedAgrees()
    {
        // Daemon path: the denied UserPromptSubmit answers the bare Noop form,
        // byte-identical to an uneventful hook (invariant 1), produced daemon-side.
        var outcome = await ShimClient.TryForwardAsync(Sock, new HookRequest(
            "deny0001", "user-prompt-submit", "claude-code", Encoding.UTF8.GetBytes("""{"prompt":"hi"}""")));
        var a = Assert.IsType<ForwardOutcome.Answered>(outcome);
        Assert.Equal(0, a.ExitCode);
        Assert.Equal("{}", Encoding.UTF8.GetString(a.StdoutBytes));

        // NO-DRIFT: the collapsed path, given the SAME file and event, must
        // answer byte-identically — both sites route through HookRun.PolicyGateFor.
        using var so = new StringWriter();
        using var se = new StringWriter();
        await HookRun.CollapsedAsync(
            new Invocation(Mode.Collapsed, "user-prompt-submit", "claude-code"),
            new StringReader("""{"prompt":"hi"}"""), so, se,
            harnessDir: NoHarnessDir, policyPath: _policyPath);
        Assert.Equal(Encoding.UTF8.GetString(a.StdoutBytes), so.ToString());
    }

    [Fact]
    public async Task UndeniedEvent_StillEchoes_TheDenyIsEventScoped()
    {
        // SessionStart is not denied by this policy, so the daemon still echoes —
        // proving the deny is a per-event decision, not a blanket daemon-off.
        var outcome = await ShimClient.TryForwardAsync(Sock, new HookRequest(
            "allow001", "session-start", "claude-code", "{}"u8.ToArray()));
        var a = Assert.IsType<ForwardOutcome.Answered>(outcome);
        Assert.Contains("additionalContext", Encoding.UTF8.GetString(a.StdoutBytes));
    }
}
