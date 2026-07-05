using System.Net.Sockets;
using CaptainHook.Core;

namespace CaptainHook.Tests;

// at-most-once-fallback-guard (ADR-0004 decision 3): a dispatch runs AT MOST
// once, whatever fails wherever. The load-bearing line is the commit marker —
// `committed` fires the instant the last payload byte is accepted by the
// transport, and classification branches on the FLAG, not on which call threw:
// a deadline landing after the final byte (on the flush, on the way out of the
// write) is FailedAfterDelivery, never a fallback that double-runs the hook.

/// A stream that misbehaves on cue: throws on flush, or on the Nth write.
internal sealed class SabotageStream : Stream
{
    public bool ThrowOnFlush;
    public int ThrowOnWriteNumber = -1;   // 1-based; -1 = never
    public int Writes;
    public readonly MemoryStream Sink = new();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buf, CancellationToken ct = default)
    {
        await Task.Yield();
        if (++Writes == ThrowOnWriteNumber) throw new IOException("sabotaged write");
        Sink.Write(buf.Span);
    }
    public override Task FlushAsync(CancellationToken ct) =>
        ThrowOnFlush ? throw new OperationCanceledException("deadline landed on the flush") : Task.CompletedTask;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => Sink.Length;
    public override long Position { get => Sink.Position; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
    public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
    public override void SetLength(long v) => throw new NotSupportedException();
    public override void Write(byte[] b, int o, int c) => WriteAsync(b.AsMemory(o, c)).AsTask().GetAwaiter().GetResult();
}

public class CommitBoundaryTests
{
    [Fact]
    public async Task Committed_Fires_EvenWhenTheFlushThrows()
    {
        // The hole this slice plugs: every byte is in the kernel, then the
        // deadline lands on the flush. The marker must already have fired —
        // that is what stops the caller from classifying NotDelivered.
        var stream = new SabotageStream { ThrowOnFlush = true };
        var committed = false;

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => Frame.WriteAsync(stream, "payload"u8.ToArray(), committed: () => committed = true));

        Assert.True(committed, "committed must fire before the flush can throw");
        Assert.Equal(4 + 7, stream.Sink.Length);   // header + payload fully written
    }

    [Fact]
    public async Task Committed_DoesNotFire_WhenThePayloadWriteThrows()
    {
        // Failure mid-payload: the frame is incomplete, the daemon can never
        // dispatch it — the marker must stay unfired so fallback is permitted.
        var stream = new SabotageStream { ThrowOnWriteNumber = 2 };   // header ok, payload throws
        var committed = false;

        await Assert.ThrowsAsync<IOException>(
            () => Frame.WriteAsync(stream, "payload"u8.ToArray(), committed: () => committed = true));

        Assert.False(committed, "an incomplete frame must never read as committed");
    }
}

public class AtMostOnceIntegrationTests
{
    private static HookRequest BigReq(int stdinBytes) =>
        new("blocked1", "user-prompt-submit", "claude-code", new byte[stdinBytes]);

    [Fact]
    public async Task DeadlineMidWrite_NotDelivered_AndTheDaemonSideDispatchesNothing()
    {
        // THE money test: the shim's write stalls (server reads nothing, socket
        // buffers fill), the pre-delivery deadline fires -> NotDelivered
        // (fallback permitted) — and the server, reading everything that DID
        // arrive, finds a truncated frame and dispatches nothing. One dispatch
        // total (the fallback's), zero daemon-side.
        using var dir = new TempRuntimeDir();
        Directory.CreateDirectory(dir.Path);
        var path = dir.Paths.SocketPath;
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen(1);

        var serverSaw = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            using var conn = await listener.AcceptAsync();
            await Task.Delay(600);   // read NOTHING until after the shim's deadline
            await using var s = new NetworkStream(conn);
            try
            {
                var payload = await Frame.ReadAsync(s);
                serverSaw.TrySetResult(payload is null ? "clean-eof" : "COMPLETE FRAME — WOULD DISPATCH");
            }
            catch (EndOfStreamException) { serverSaw.TrySetResult("truncated — nothing dispatched"); }
        });

        // 8 MiB of stdin: no UDS buffer swallows that; the write must stall.
        var outcome = await ShimClient.TryForwardAsync(path, BigReq(8 * 1024 * 1024),
            preDeliveryTimeout: TimeSpan.FromMilliseconds(300));

        var nd = Assert.IsType<ForwardOutcome.NotDelivered>(outcome);
        Assert.Contains("request write", nd.Reason);

        var saw = await serverSaw.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("truncated", saw);
        await server.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task EveryOutcomeClass_YieldsAtMostOneDispatch_CountedInTheTrail()
    {
        // Program's shim logic in miniature, against a REAL DaemonHost, with
        // the trail as witness: for each scenario, dispatch.start events
        // carrying that dispatchId must number EXACTLY one (warm: the
        // daemon's; cold: the fallback's) — never two.
        using var log = new CapturedLog();
        using var dir = new TempRuntimeDir();
        using var stop = new CancellationTokenSource();
        var noHarness = "/tmp/chk-none-" + Guid.NewGuid().ToString("N")[..8];
        var daemon = Task.Run(() => DaemonHost.RunAsync(dir.Paths, noHarness, stop.Token));
        await TestUtil.PollUntilAsync(async () =>
            await ShimClient.TryForwardAsync(dir.Paths.SocketPath,
                new HookRequest("warmup00", "session-start", "claude-code", "{}"u8.ToArray()))
                is ForwardOutcome.Answered,
            TimeSpan.FromSeconds(15), "daemon up");

        async Task<int> RunShimLogicAsync(string dispatchId, string socketPath)
        {
            // Mirror Program.cs: forward; collapse ONLY on NotDelivered.
            var outcome = await ShimClient.TryForwardAsync(socketPath,
                new HookRequest(dispatchId, "user-prompt-submit", "claude-code", "{}"u8.ToArray()));
            switch (outcome)
            {
                case ForwardOutcome.Answered a: return a.ExitCode;
                case ForwardOutcome.FailedAfterDelivery: return 1;
                default:
                    await HookRun.CollapsedAsync(
                        new Invocation(Mode.Collapsed, "user-prompt-submit", "claude-code"),
                        new StringReader("{}"), new StringWriter(), new StringWriter(),
                        harnessDir: noHarness, dispatchId: dispatchId);
                    return 0;
            }
        }

        int Dispatches(string id) => log.Events.Count(e =>
            e.Evt == "dispatch.start" && e.Fields.DispatchId == id);

        // warm: daemon dispatches, no fallback
        Assert.Equal(0, await RunShimLogicAsync("warm0001", dir.Paths.SocketPath));
        Assert.Equal(1, Dispatches("warm0001"));

        // cold: nothing listening at a fresh path -> fallback dispatches
        using var coldDir = new TempRuntimeDir();
        Assert.Equal(0, await RunShimLogicAsync("cold0001", coldDir.Paths.SocketPath));
        Assert.Equal(1, Dispatches("cold0001"));

        stop.Cancel();
        Assert.Equal(0, await daemon.WaitAsync(TimeSpan.FromSeconds(10)));
    }
}
