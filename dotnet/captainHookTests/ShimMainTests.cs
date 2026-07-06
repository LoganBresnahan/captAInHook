using System.Net.Sockets;
using System.Text;
using CaptainHook.Shim;
using CaptainHook.Wire;

namespace CaptainHook.Tests;

// captainshim-aot-artifact (ADR-0004 decision 7 amendment): ShimMain is the
// AOT shim's whole program, tested here in IL form through its injected
// streams — same seam, same bytes; the native publish only changes the
// compiler. The delegation fallback is probed with a fake "engine" script so
// the verbatim-relay rule (stdout bytes, stderr text, exit code) and the
// at-most-once boundary (FailedAfterDelivery NEVER delegates) are pinned.

public class ShimMainTests
{
    private static async Task<(int Exit, byte[] Stdout, string Stderr)> RunAsync(
        string[] args, byte[] stdin, string? engine = null, string? socket = null)
    {
        using var stdout = new MemoryStream();
        var stderr = new StringWriter();
        var exit = await ShimMain.RunAsync(args, new MemoryStream(stdin), stdout, stderr,
            enginePath: engine, socketPathOverride: socket);
        return (exit, stdout.ToArray(), stderr.ToString());
    }

    /// A fake engine: a shell script that proves what reached it — echoes its
    /// argv and stdin into stdout with a marker, writes a stderr line, exits 3.
    private static string FakeEngine(string dir)
    {
        var path = Path.Combine(dir, "captainHook");
        File.WriteAllText(path,
            "#!/bin/sh\n" +
            "printf 'argv=[%s] stdin=[%s]' \"$*\" \"$(cat)\"\n" +
            "echo 'engine stderr line' >&2\n" +
            "exit 3\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "chk-shimmain-" + Guid.NewGuid().ToString("N")[..8]);
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { /* best-effort */ } }
    }

    [Fact]
    public async Task WarmPath_RelaysAnswerVerbatim_NeverTouchesTheEngine()
    {
        using var dir = new TempDir();
        var socket = Path.Combine(dir.Path, "test.sock");
        var effect = Encoding.UTF8.GetBytes("""{"hookSpecificOutput":{"x":"café ✓"}}""");

        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(socket));
        listener.Listen(1);
        var server = Task.Run(async () =>
        {
            using var l = listener;
            using var conn = await l.AcceptAsync();
            await using var s = new NetworkStream(conn);
            var req = HookRequest.Decode((await Frame.ReadAsync(s))!);
            Assert.Equal("user-prompt-submit", req.EventName);
            await Frame.WriteAsync(s, new HookResponse(0, effect, "trace").Encode());
        });

        // enginePath points at nothing on purpose: a warm answer must never
        // need (or touch) the engine.
        var (exit, stdout, stderr) = await RunAsync(
            ["hook", "user-prompt-submit"], "{\"prompt\":\"hi\"}"u8.ToArray(),
            engine: Path.Combine(dir.Path, "no-such-engine"), socket: socket);

        Assert.Equal(0, exit);
        Assert.Equal(effect, stdout);          // byte-identical relay
        Assert.Contains("trace", stderr);
        await server.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task NoDaemon_DelegatesToEngine_VerbatimRelay_AndAppendsNoDaemonFlag()
    {
        using var dir = new TempDir();
        var engine = FakeEngine(dir.Path);
        var socket = Path.Combine(dir.Path, "nobody-home.sock");   // connect will fail → NotDelivered

        var (exit, stdout, stderr) = await RunAsync(
            ["hook", "user-prompt-submit", "--harness", "claude-code"], "payload-bytes"u8.ToArray(),
            engine: engine, socket: socket);

        Assert.Equal(3, exit);                                     // the engine's exit code, verbatim
        var text = Encoding.UTF8.GetString(stdout);
        Assert.Contains("argv=[hook user-prompt-submit --harness claude-code --no-daemon]", text);
        Assert.Contains("stdin=[payload-bytes]", text);            // original bytes reached the engine
        Assert.Contains("engine stderr line", stderr);
    }

    [Fact]
    public async Task ExplicitNoDaemon_SkipsForwarding_DelegatesWithoutDuplicatingFlag()
    {
        using var dir = new TempDir();
        var engine = FakeEngine(dir.Path);

        // No socketPathOverride: --no-daemon must never even resolve a socket.
        var (exit, stdout, _) = await RunAsync(
            ["hook", "post-tool-use", "--no-daemon"], [], engine: engine);

        Assert.Equal(3, exit);
        Assert.Contains("argv=[hook post-tool-use --no-daemon]", Encoding.UTF8.GetString(stdout));
    }

    [Fact]
    public async Task FailedAfterDelivery_ExitsOne_ZeroStdout_NeverDelegates()
    {
        using var dir = new TempDir();
        var engine = FakeEngine(dir.Path);   // would write bytes if (wrongly) delegated to
        var socket = Path.Combine(dir.Path, "test.sock");

        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(socket));
        listener.Listen(1);
        var server = Task.Run(async () =>
        {
            using var l = listener;
            using var conn = await l.AcceptAsync();
            await using var s = new NetworkStream(conn);
            await Frame.ReadAsync(s);                     // delivery happened…
            conn.Shutdown(SocketShutdown.Both);           // …then no answer
        });

        var (exit, stdout, stderr) = await RunAsync(
            ["hook", "user-prompt-submit"], "{}"u8.ToArray(), engine: engine, socket: socket);

        Assert.Equal(1, exit);
        Assert.Empty(stdout);                             // fake engine untouched — at-most-once held
        Assert.Contains("failed after delivery", stderr);
        await server.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task EngineModes_RefusedLoudly()
    {
        foreach (var args in new[] { new[] { "--daemon" }, new[] { "doctor" }, new[] { "actors-demo" } })
        {
            var (exit, stdout, stderr) = await RunAsync(args, []);
            Assert.Equal(1, exit);
            Assert.Empty(stdout);
            Assert.Contains("run captainHook", stderr);
        }
    }

    [Fact]
    public async Task NoDaemon_NoEngine_ExitsOne_ZeroStdout_SaysDeploy()
    {
        using var dir = new TempDir();
        var (exit, stdout, stderr) = await RunAsync(
            ["hook", "user-prompt-submit"], "{}"u8.ToArray(),
            engine: Path.Combine(dir.Path, "captainHook"),          // absent
            socket: Path.Combine(dir.Path, "nobody-home.sock"));   // and no daemon

        Assert.Equal(1, exit);
        Assert.Empty(stdout);
        Assert.Contains("deploy captainHook next to captainShim", stderr);
    }
}
