using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CaptainHook.Wire;

// ADR-0004 decision 3 (framing): 4-byte little-endian length prefix + UTF-8
// JSON per frame; one connection per dispatch. This file is the codec BOTH
// sides speak — the shim writes a HookRequest and reads a HookResponse; the
// daemon does the reverse. It is a pure Stream codec: no sockets, no dispatch,
// no policy (a malformed frame throws; what that means — fail this connection,
// fall back, exit — is the caller's decision, per mode).
//
// ── The at-most-once boundary lives HERE, at WriteAsync's completion. ──
// The daemon dispatches a request only after reading a COMPLETE frame (the
// length prefix says when), so any failure raised BEFORE WriteAsync returns —
// connect refused, a mid-write break, the shim-side deadline cancelling the
// write — provably precedes delivery: an incomplete frame is never dispatched,
// and the shim may safely fall back to collapsed dispatch. Once WriteAsync has
// returned, the daemon may already be running non-idempotent Background
// effects: any later failure is a FAILED dispatch, never a retry. Callers
// (shim-forward-or-fallback, at-most-once-fallback-guard) branch on exactly
// this point; do not blur it.

/// The length-prefixed frame layer: payload bytes in, payload bytes out.
public static class Frame
{
    /// Ceiling on a single frame's payload. Generous for hook payloads (large
    /// pasted prompts included) while keeping a corrupt or hostile length
    /// prefix from allocating gigabytes before JSON parsing ever runs.
    public const int MaxPayloadBytes = 64 * 1024 * 1024;

    /// Write one frame (header + payload) and flush. Returning = the frame is
    /// fully handed to the transport — the at-most-once boundary above.
    ///
    /// `committed` fires the instant the LAST payload byte is accepted by the
    /// transport, BEFORE the flush: past that point the frame may be
    /// delivered, so a failure thrown out of the remainder of this method
    /// (flush, a cancellation check on the way out) must be classified
    /// after-delivery. Without this marker a deadline landing between
    /// final-byte and return would misclassify as not-delivered and permit a
    /// fallback that double-runs the dispatch (at-most-once-fallback-guard).
    public static async Task WriteAsync(Stream stream, ReadOnlyMemory<byte> payload,
        CancellationToken ct = default, Action? committed = null)
    {
        if (payload.Length > MaxPayloadBytes)
            throw new InvalidDataException($"frame payload {payload.Length} bytes exceeds max {MaxPayloadBytes}");
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)payload.Length);
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(payload, ct);
        committed?.Invoke();
        await stream.FlushAsync(ct);
    }

    /// Read one frame's payload. Null on a clean EOF at a frame boundary (the
    /// peer closed between frames — the normal end of a one-dispatch
    /// connection); EndOfStreamException on EOF mid-frame (truncation is a
    /// transport failure, never silently an empty payload); InvalidDataException
    /// on a length the codec refuses.
    public static async Task<byte[]?> ReadAsync(Stream stream, CancellationToken ct = default)
    {
        var header = new byte[4];
        var got = await FillAsync(stream, header, ct);
        if (got == 0) return null;
        if (got < header.Length) throw new EndOfStreamException("truncated frame header");

        var len = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (len > MaxPayloadBytes)
            throw new InvalidDataException($"frame length {len} exceeds max {MaxPayloadBytes}");

        var payload = new byte[(int)len];
        if (await FillAsync(stream, payload, ct) < payload.Length)
            throw new EndOfStreamException("truncated frame payload");
        return payload;
    }

    /// Fill the buffer from the stream, looping across short reads (sockets
    /// deliver what they have, not what you asked for). Returns bytes filled —
    /// short only if EOF arrived first.
    private static async Task<int> FillAsync(Stream stream, Memory<byte> buf, CancellationToken ct)
    {
        var filled = 0;
        while (filled < buf.Length)
        {
            var n = await stream.ReadAsync(buf[filled..], ct);
            if (n == 0) break;
            filled += n;
        }
        return filled;
    }
}

/// What the shim forwards (ADR-0004 decision 1): the dispatchId it minted, the
/// CLI event/harness args, and the raw stdin bytes — VERBATIM, as base64 (JSON
/// strings can't carry arbitrary bytes; byte[] round-trips base64 losslessly),
/// so the daemon parses exactly the bytes the host wrote.
public sealed record HookRequest(
    [property: JsonPropertyName("dispatchId")] string DispatchId,
    [property: JsonPropertyName("event")] string? EventName,
    [property: JsonPropertyName("harness")] string HarnessName,
    [property: JsonPropertyName("stdin")] byte[] StdinBytes)
{
    public byte[] Encode() => JsonSerializer.SerializeToUtf8Bytes(this, WireJson.Default.HookRequest);

    /// Decode a request payload. Throws InvalidDataException on anything less
    /// than a well-formed, field-complete request — the codec never guesses.
    public static HookRequest Decode(byte[] payload)
    {
        HookRequest? req;
        try { req = JsonSerializer.Deserialize(payload, WireJson.Default.HookRequest); }
        catch (JsonException ex) { throw new InvalidDataException($"malformed request frame: {ex.Message}", ex); }
        if (req is null || req.DispatchId is null || req.HarnessName is null || req.StdinBytes is null)
            throw new InvalidDataException("malformed request frame: missing required field");
        return req;
    }
}

/// What the daemon answers with: the effect's stdout bytes VERBATIM (the sacred
/// contract crosses the socket byte-identically — base64, same reason as
/// stdin), the human trace for the shim's stderr, and the exit code (an unknown
/// --harness exits 1 with zero stdout bytes, decided daemon-side).
public sealed record HookResponse(
    [property: JsonPropertyName("exit")] int ExitCode,
    [property: JsonPropertyName("stdout")] byte[] StdoutBytes,
    [property: JsonPropertyName("stderr")] string StderrText)
{
    public byte[] Encode() => JsonSerializer.SerializeToUtf8Bytes(this, WireJson.Default.HookResponse);

    public static HookResponse Decode(byte[] payload)
    {
        HookResponse? res;
        try { res = JsonSerializer.Deserialize(payload, WireJson.Default.HookResponse); }
        catch (JsonException ex) { throw new InvalidDataException($"malformed response frame: {ex.Message}", ex); }
        if (res is null || res.StdoutBytes is null || res.StderrText is null)
            throw new InvalidDataException("malformed response frame: missing required field");
        return res;
    }
}

/// Source-generated serialization for the two wire records (ADR-0004 decision
/// 7 amendment): the serializer code is emitted at compile time, so the AOT
/// shim carries no runtime reflection — and both artifacts serialize through
/// THIS generated code, so the frame JSON cannot fork between them. The wire
/// contract is exactly this list; adding a serializable type here is adding
/// to the protocol.
[JsonSerializable(typeof(HookRequest))]
[JsonSerializable(typeof(HookResponse))]
internal sealed partial class WireJson : JsonSerializerContext { }
