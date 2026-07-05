using System.Net.Sockets;
using System.Text;
using CaptainHook.Core;

namespace CaptainHook.Tests;

// shim-forward-or-fallback (ADR-0004 decisions 2–3): the at-most-once boundary
// as types. NotDelivered — and ONLY NotDelivered — permits collapsed fallback;
// everything at or past the fully-written request frame is Answered or
// FailedAfterDelivery. Servers below are in-process UDS listeners playing one
// daemon personality each; timeouts are explicit and short.

public class ShimClientTests
{
    private static HookRequest Req() =>
        new("test1234", "user-prompt-submit", "claude-code", "{\"prompt\":\"hi\"}"u8.ToArray());

    /// A one-connection daemon stand-in: accepts once, runs `serve`, closes.
    private static async Task<(Task Server, string SocketPath, TempRuntimeDir Dir)> ServeOnceAsync(
        Func<Socket, Task> serve)
    {
        var dir = new TempRuntimeDir();
        Directory.CreateDirectory(dir.Path);
        var path = dir.Paths.SocketPath;
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen(1);
        var server = Task.Run(async () =>
        {
            using var l = listener;
            using var conn = await l.AcceptAsync();
            await serve(conn);
        });
        return (server, path, dir);
    }

    [Fact]
    public async Task WarmPath_Answered_RelaysBytesVerbatim()
    {
        var stdout = Encoding.UTF8.GetBytes("""{"hookSpecificOutput":{"x":"café ✓"}}""");
        var (server, path, dir) = await ServeOnceAsync(async conn =>
        {
            await using var s = new NetworkStream(conn);
            var req = HookRequest.Decode((await Frame.ReadAsync(s))!);
            Assert.Equal("test1234", req.DispatchId);              // daemon sees the shim's id
            Assert.Equal("{\"prompt\":\"hi\"}"u8.ToArray(), req.StdinBytes);
            await Frame.WriteAsync(s, new HookResponse(0, stdout, "trace line").Encode());
        });
        using var _ = dir;

        var outcome = await ShimClient.TryForwardAsync(path, Req());

        var a = Assert.IsType<ForwardOutcome.Answered>(outcome);
        Assert.Equal(0, a.ExitCode);
        Assert.Equal(stdout, a.StdoutBytes);          // byte-identical relay
        Assert.Equal("trace line", a.StderrText);
        await server.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task NoDaemon_ConnectFails_NotDelivered_FallbackPermitted()
    {
        using var dir = new TempRuntimeDir();   // nothing listening, socket file absent
        var outcome = await ShimClient.TryForwardAsync(dir.Paths.SocketPath, Req());

        var nd = Assert.IsType<ForwardOutcome.NotDelivered>(outcome);
        Assert.Contains("connect", nd.Reason);   // the everyday cold case
    }

    [Fact]
    public async Task StaleSocketFile_NobodyListening_NotDelivered()
    {
        // A dead daemon's leftover socket: connect gets ECONNREFUSED — provably
        // no delivery, fallback permitted.
        using var dir = new TempRuntimeDir();
        Directory.CreateDirectory(dir.Path);
        File.WriteAllText(dir.Paths.SocketPath, "corpse");

        var outcome = await ShimClient.TryForwardAsync(dir.Paths.SocketPath, Req());
        Assert.IsType<ForwardOutcome.NotDelivered>(outcome);
    }

    [Fact]
    public async Task AcceptsButNeverAnswers_DeadlineFires_FailedAfterDelivery_NeverFallback()
    {
        // The wedged-daemon case decision 2 exists for: connect succeeds, the
        // request is fully written, then silence. The request MAY be running —
        // Background effects and all — so this is a FAILED dispatch, and the
        // shim-side deadline bounds the wait instead of hanging the agent host.
        var silent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (server, path, dir) = await ServeOnceAsync(async conn =>
        {
            await using var s = new NetworkStream(conn);
            await Frame.ReadAsync(s);        // consume the request…
            await silent.Task;               // …then never answer
        });
        using var _ = dir;

        var outcome = await ShimClient.TryForwardAsync(path, Req(),
            responseTimeout: TimeSpan.FromMilliseconds(300));

        var f = Assert.IsType<ForwardOutcome.FailedAfterDelivery>(outcome);
        Assert.Contains("deadline", f.Reason);
        silent.TrySetResult();               // release the server task
        await server.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task DaemonDiesAfterReadingRequest_EofOnResponse_FailedAfterDelivery()
    {
        // Accept, read the request, drop the connection: delivery happened
        // (or cannot be ruled out) — at-most-once forbids the retry.
        var (server, path, dir) = await ServeOnceAsync(async conn =>
        {
            await using var s = new NetworkStream(conn);
            await Frame.ReadAsync(s);
            conn.Shutdown(SocketShutdown.Both);   // clean close, no response frame
        });
        using var _ = dir;

        var outcome = await ShimClient.TryForwardAsync(path, Req());

        var f = Assert.IsType<ForwardOutcome.FailedAfterDelivery>(outcome);
        Assert.Contains("before answering", f.Reason);
        await server.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task MalformedResponseFrame_FailedAfterDelivery()
    {
        // The daemon answered garbage: the dispatch ran, the relay failed.
        // Still no fallback — the effects daemon-side are already real.
        var (server, path, dir) = await ServeOnceAsync(async conn =>
        {
            await using var s = new NetworkStream(conn);
            await Frame.ReadAsync(s);
            await Frame.WriteAsync(s, "not a HookResponse"u8.ToArray());
        });
        using var _ = dir;

        var outcome = await ShimClient.TryForwardAsync(path, Req());
        Assert.IsType<ForwardOutcome.FailedAfterDelivery>(outcome);
        await server.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task NonZeroExit_ZeroStdout_RelaysExactly()
    {
        // The unknown-harness shape: daemon decides exit 1 with zero stdout
        // bytes; the shim relays that verdict without embellishment.
        var (server, path, dir) = await ServeOnceAsync(async conn =>
        {
            await using var s = new NetworkStream(conn);
            await Frame.ReadAsync(s);
            await Frame.WriteAsync(s, new HookResponse(1, [], "captAInHook: unknown harness 'nope'").Encode());
        });
        using var _ = dir;

        var outcome = await ShimClient.TryForwardAsync(path, Req());
        var a = Assert.IsType<ForwardOutcome.Answered>(outcome);
        Assert.Equal(1, a.ExitCode);
        Assert.Empty(a.StdoutBytes);
        await server.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
