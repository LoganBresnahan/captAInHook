using System.Text;
using CaptainHook.Core;
using CaptainHook.Wire;

namespace CaptainHook.Tests;

// frame-protocol (ADR-0004 decision 3): 4-byte LE length prefix + UTF-8 JSON,
// payload bytes verbatim across the wire. The golden-bytes tests pin the wire
// format itself — a framing change must break a test, not a live shim/daemon
// pair mid-upgrade (content-identity gives each build its own socket, but the
// codec's shape is still a contract worth pinning explicitly).

/// Delivers at most one byte per ReadAsync call — a socket's worst legal
/// behavior — so any reader that doesn't loop across short reads fails here.
internal sealed class TrickleStream(byte[] data) : Stream
{
    private int _pos;
    public override async ValueTask<int> ReadAsync(Memory<byte> buf, CancellationToken ct = default)
    {
        await Task.Yield();   // force the async path
        if (_pos >= data.Length || buf.Length == 0) return 0;
        buf.Span[0] = data[_pos++];
        return 1;
    }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => data.Length;
    public override long Position { get => _pos; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] b, int o, int c) => ReadAsync(b.AsMemory(o, c)).AsTask().GetAwaiter().GetResult();
    public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
    public override void SetLength(long v) => throw new NotSupportedException();
    public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
}

public class FrameLayerTests
{
    private static async Task<byte[]> FramedAsync(byte[] payload)
    {
        using var ms = new MemoryStream();
        await Frame.WriteAsync(ms, payload);
        return ms.ToArray();
    }

    [Fact]
    public async Task GoldenBytes_LengthPrefixIsLittleEndian()
    {
        // "abc" -> 03 00 00 00 61 62 63. Pinned so the wire format can never
        // drift silently (a big-endian slip would still round-trip in-process).
        var wire = await FramedAsync("abc"u8.ToArray());
        Assert.Equal(new byte[] { 0x03, 0x00, 0x00, 0x00, 0x61, 0x62, 0x63 }, wire);
    }

    [Fact]
    public async Task RoundTrip_ArbitraryBytes_ByteIdentical()
    {
        // Invalid UTF-8, NULs, high bytes — the frame layer moves bytes, not text.
        var payload = new byte[] { 0xFF, 0x00, 0xC3, 0x28, 0x80, 0x0A };
        using var ms = new MemoryStream(await FramedAsync(payload));
        Assert.Equal(payload, await Frame.ReadAsync(ms));
    }

    [Fact]
    public async Task RoundTrip_EmptyPayload()
    {
        using var ms = new MemoryStream(await FramedAsync([]));
        var got = await Frame.ReadAsync(ms);
        Assert.NotNull(got);
        Assert.Empty(got);
    }

    [Fact]
    public async Task MultipleFrames_ReadSequentially_ThenCleanEofIsNull()
    {
        using var ms = new MemoryStream();
        await Frame.WriteAsync(ms, "one"u8.ToArray());
        await Frame.WriteAsync(ms, "two"u8.ToArray());
        ms.Position = 0;

        Assert.Equal("one"u8.ToArray(), await Frame.ReadAsync(ms));
        Assert.Equal("two"u8.ToArray(), await Frame.ReadAsync(ms));
        Assert.Null(await Frame.ReadAsync(ms));   // EOF at a frame boundary = peer done
    }

    [Fact]
    public async Task ShortReads_ReaderLoopsUntilFull()
    {
        var payload = Encoding.UTF8.GetBytes("short-read torture payload");
        var wire = await FramedAsync(payload);
        Assert.Equal(payload, await Frame.ReadAsync(new TrickleStream(wire)));
    }

    [Fact]
    public async Task TruncatedHeader_Throws()
    {
        using var ms = new MemoryStream([0x03, 0x00]);   // 2 of 4 header bytes
        await Assert.ThrowsAsync<EndOfStreamException>(() => Frame.ReadAsync(ms));
    }

    [Fact]
    public async Task TruncatedPayload_Throws_NeverSilentlyShort()
    {
        var wire = (await FramedAsync("abcdef"u8.ToArray()))[..^2];   // lose 2 payload bytes
        using var ms = new MemoryStream(wire);
        await Assert.ThrowsAsync<EndOfStreamException>(() => Frame.ReadAsync(ms));
    }

    [Fact]
    public async Task OversizeLength_RefusedBeforeAllocation()
    {
        // A corrupt/hostile prefix claiming 4GiB-1 must throw, not allocate.
        using var ms = new MemoryStream([0xFF, 0xFF, 0xFF, 0xFF]);
        await Assert.ThrowsAsync<InvalidDataException>(() => Frame.ReadAsync(ms));
    }

    [Fact]
    public async Task OversizePayload_RefusedAtWrite()
    {
        using var ms = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(
            () => Frame.WriteAsync(ms, new byte[Frame.MaxPayloadBytes + 1]));
    }
}

public class FrameMessageTests
{
    [Fact]
    public void HookRequest_RoundTrips_StdinBytesVerbatim()
    {
        // The whole point of base64 fields: raw host stdin — including bytes
        // that are not valid UTF-8 — crosses the frame byte-identically.
        var stdin = new byte[] { 0x7B, 0xFF, 0x00, 0xC3, 0x28, 0x7D };
        var req = new HookRequest("a1b2c3d4", "user-prompt-submit", "claude-code", stdin);

        var back = HookRequest.Decode(req.Encode());
        Assert.Equal(req.DispatchId, back.DispatchId);
        Assert.Equal(req.EventName, back.EventName);
        Assert.Equal(req.HarnessName, back.HarnessName);
        Assert.Equal(stdin, back.StdinBytes);
    }

    [Fact]
    public void HookRequest_NullEventName_Survives()
    {
        // `hook` without an event arg: the payload's own field decides daemon-side.
        var back = HookRequest.Decode(new HookRequest("a1b2c3d4", null, "claude-code", []).Encode());
        Assert.Null(back.EventName);
    }

    [Fact]
    public void HookResponse_RoundTrips_StdoutBytesVerbatim()
    {
        // The sacred contract crosses the socket byte-identically: what the
        // daemon's adapter serialized is exactly what the shim writes to stdout.
        var stdout = Encoding.UTF8.GetBytes(
            """{"hookSpecificOutput":{"hookEventName":"UserPromptSubmit","additionalContext":"café ✓"}}""");
        var res = new HookResponse(0, stdout, "trace: 2 handlers, 12ms");

        var back = HookResponse.Decode(res.Encode());
        Assert.Equal(0, back.ExitCode);
        Assert.Equal(stdout, back.StdoutBytes);
        Assert.Equal(res.StderrText, back.StderrText);
    }

    [Fact]
    public void HookResponse_UnknownHarnessShape_Exit1_ZeroStdoutBytes()
    {
        var back = HookResponse.Decode(new HookResponse(1, [], "captAInHook: unknown harness 'nope'").Encode());
        Assert.Equal(1, back.ExitCode);
        Assert.Empty(back.StdoutBytes);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{"event":"x"}""")]                          // missing dispatchId/harness/stdin
    [InlineData("""{"dispatchId":"a","harness":"h"}""")]       // missing stdin
    [InlineData("null")]
    public void MalformedRequest_ThrowsInvalidData_NeverGuesses(string json)
    {
        Assert.Throws<InvalidDataException>(() => HookRequest.Decode(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public async Task FullExchange_OverOneStream_RequestThenResponse()
    {
        // One connection per dispatch, both directions through the same codec —
        // the shape shim-forward-or-fallback will drive over a real socket.
        using var wire = new MemoryStream();
        var req = new HookRequest("deadbeef", "session-start", "claude-code", "{}"u8.ToArray());
        await Frame.WriteAsync(wire, req.Encode());
        var res = new HookResponse(0, "{\"ok\":true}"u8.ToArray(), "trace");
        await Frame.WriteAsync(wire, res.Encode());
        wire.Position = 0;

        var gotReq = HookRequest.Decode((await Frame.ReadAsync(wire))!);
        var gotRes = HookResponse.Decode((await Frame.ReadAsync(wire))!);
        Assert.Equal("deadbeef", gotReq.DispatchId);
        Assert.Equal("{\"ok\":true}"u8.ToArray(), gotRes.StdoutBytes);
    }
}
