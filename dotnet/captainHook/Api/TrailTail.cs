using System.Text;
using System.Threading.Channels;
using CaptainHook.Actors;

namespace CaptainHook.Api;

// sse-trail-tail (ADR-0007 decision 5): the live stream is a TAIL OF THE JSONL
// TRAIL FILE — never an in-process tee, which would see only daemon-side lines
// and miss the shim half and every collapsed dispatch. The file is where the
// one-schema/two-emitters design (ADR-0004 d7) already converges both halves;
// one reader gets the whole story.
//
// Mechanics pinned by the ADR:
//   * portable STAT-POLL, not inotify (Linux-only) — hook rates make polling
//     cheap, and the poll interval is the only latency added;
//   * SSE event id = BYTE OFFSET after the line's trailing '\n', so a
//     reconnect's Last-Event-ID resumes exactly: zero duplicate, zero loss;
//   * the tailer is SCHEMA-BLIND: it ships complete lines as opaque strings and
//     never parses them, so the trail's third consumer (N4) adds no schema
//     coupling beyond "newline-delimited" — the golden-pinned emitters stay the
//     only schema authorities.
//
// The three sharp edges, and where each is handled:
//   * HALF-WRITTEN LINES — the shim O_APPENDs concurrently, so the reader may
//     see a line's head before its tail. TrailCursor emits only through the
//     LAST '\n' in view; bytes after it are re-read next poll (the cursor's
//     offset only ever rests on a line boundary).
//   * RESUME EXACTNESS — offsets are bytes, lines are byte-split on 0x0A before
//     UTF-8 decode (a UTF-8 continuation byte can never be 0x0A, so multi-byte
//     content cannot confuse the split). A resume offset that lands mid-line
//     (a stale id against a truncated-then-regrown file, or a tampered value)
//     is detected — the byte before a legitimate offset is always '\n' — and
//     self-heals by discarding forward to the next boundary, so a bogus id can
//     never emit garbage half-lines.
//   * FILE LIFECYCLE — not-yet-created reads as "nothing yet" (never an
//     error); TRUNCATION/replacement (length < offset) resets the cursor to 0
//     and surfaces an explicit Reset event (id: 0) so the client knows the id
//     space restarted; EOF just keeps polling.

/// One poll's outcome: complete lines (each with the byte offset AFTER its
/// '\n' — the SSE id that resumes exactly past it), whether the file was
/// truncated/replaced since the last poll, and whether more bytes are already
/// known to be waiting (caller should poll again without delay).
public sealed record TrailPoll(IReadOnlyList<TrailLine> Lines, bool Reset, bool More);

public sealed record TrailLine(long EndOffset, string Text);

/// A single subscriber's cursor over the trail file. NOT thread-safe — each
/// subscriber owns one and polls it from one task.
public sealed class TrailCursor
{
    private readonly string _path;
    private bool _align;   // offset came from a client: trust it only after proving it sits on a boundary

    public TrailCursor(string path, long startOffset, bool alignForward = false)
    {
        _path = path;
        Offset = Math.Max(0, startOffset);
        _align = alignForward && Offset > 0;
    }

    /// The byte offset the next poll reads from. Rests on a line boundary
    /// except transiently while an unaligned resume discards toward one.
    public long Offset { get; private set; }

    /// Read whatever complete lines are available, up to `maxBytes` of them —
    /// a huge backlog is delivered across repeated polls (More=true) so one
    /// poll can never balloon memory or starve cancellation.
    public TrailPoll Poll(int maxBytes = 128 * 1024)
    {
        FileStream fs;
        try
        {
            // ReadWrite|Delete share: the emitters keep appending (and a rotation
            // may unlink) while we read — the reader must never block a writer.
            fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (IOException)   // FileNotFound/DirectoryNotFound: nothing to read (yet)
        {
            if (Offset == 0) return new TrailPoll([], Reset: false, More: false);
            // The file we were mid-way through vanished: a rotation/removal.
            // Reset once; a recreated file starts a fresh id space at 0.
            Offset = 0;
            _align = false;
            return new TrailPoll([], Reset: true, More: false);
        }

        using (fs)
        {
            var len = fs.Length;
            if (len < Offset)
            {
                // Truncated or replaced with something shorter: every id we ever
                // issued is invalid. Restart the id space; report it explicitly.
                Offset = 0;
                _align = false;
                return new TrailPoll([], Reset: true, More: len > 0);
            }
            if (len == Offset) return new TrailPoll([], Reset: false, More: false);

            var take = (int)Math.Min(maxBytes, len - Offset);
            var buf = new byte[take];
            fs.Seek(Offset, SeekOrigin.Begin);
            fs.ReadExactly(buf, 0, take);

            var start = 0;
            if (_align)
            {
                // A client-supplied offset is legitimate iff it sits just past a
                // '\n' (that is the only kind of id we issue). Prove it by the
                // preceding byte; otherwise discard forward to the next boundary
                // — the discarded bytes are the tail of a line whose head we
                // cannot have, and nothing is emitted for them.
                fs.Seek(Offset - 1, SeekOrigin.Begin);
                if (fs.ReadByte() == '\n') _align = false;
                else
                {
                    var nl = Array.IndexOf(buf, (byte)'\n', 0, take);
                    if (nl < 0)
                    {
                        // No boundary in view yet: swallow the chunk and keep
                        // discarding next poll (offset moves through non-boundary
                        // positions here, but nothing is emitted until aligned).
                        Offset += take;
                        return new TrailPoll([], Reset: false, More: Offset < len);
                    }
                    start = nl + 1;
                    _align = false;
                }
            }

            // Emit only through the LAST '\n' in view: bytes past it are a line
            // still being written (or cut by maxBytes) and will be re-read whole.
            var lastNl = Array.LastIndexOf(buf, (byte)'\n', take - 1, take - start);
            if (lastNl < start)
            {
                Offset += start;   // consumed only alignment discard, if any
                return new TrailPoll([], Reset: false, More: false);
            }

            var lines = new List<TrailLine>();
            var lineStart = start;
            for (var i = start; i <= lastNl; i++)
            {
                if (buf[i] != '\n') continue;
                var lineLen = i - lineStart;
                if (lineLen > 0 && buf[i - 1] == '\r') lineLen--;   // tolerate CRLF trails
                lines.Add(new TrailLine(
                    EndOffset: Offset + i + 1,
                    Text: Encoding.UTF8.GetString(buf, lineStart, lineLen)));
                lineStart = i + 1;
            }
            Offset += lastNl + 1;
            return new TrailPoll(lines, Reset: false, More: Offset < len);
        }
    }
}

/// The daemon-start configuration for GET /api/v1/events: which file to tail
/// (production: WireJsonl.DefaultLogPath — the same trail both emitters append)
/// and the poll/heartbeat cadences (test seams; production defaults).
public sealed record SseOptions(string TrailPath, TimeSpan? Poll = null, TimeSpan? Heartbeat = null);

/// What the SSE writer emits. ApiHost adapts these to text/event-stream frames
/// on the HTTP response; tests inject their own sink and assert on the events
/// themselves.
public abstract record SseEvent
{
    private SseEvent() { }
    /// One trail line; Id is the byte offset after it (the Last-Event-ID token).
    public sealed record Line(long Id, string Text) : SseEvent;
    /// The file was truncated/replaced: the id space restarted at 0.
    public sealed record Reset : SseEvent;
    /// Keep-alive comment — also the dead-client probe: writing it fails on a
    /// closed connection, ending the subscription (and later its idle-defer).
    public sealed record Heartbeat : SseEvent;
}

/// One SSE subscriber's pipeline: a reader task stat-polls the cursor and
/// queues events; the writer loop (RunAsync) drains them to the sink, emitting
/// a heartbeat when the trail is quiet. The channel between them is UNBOUNDED
/// in this slice — the sse-backpressure slice bounds it with drop-oldest plus
/// an explicit gap marker (ADR-0007 decision 5 / ADR-0004 d6).
public sealed class TrailSubscription
{
    private readonly TrailCursor _cursor;
    private readonly TimeSpan _poll;
    private readonly TimeSpan _heartbeat;

    /// `lastEventId` is the client's resume point (Last-Event-ID); null means
    /// "from now" — the file's current end, so a fresh subscriber sees only new
    /// events. Alignment is forced for client-supplied offsets, trusted for our
    /// own end-of-file stat.
    public TrailSubscription(string path, long? lastEventId, TimeSpan? poll = null, TimeSpan? heartbeat = null)
    {
        var start = lastEventId ?? CurrentLength(path);
        _cursor = new TrailCursor(path, start, alignForward: lastEventId is > 0);
        _poll = poll ?? TimeSpan.FromMilliseconds(200);
        _heartbeat = heartbeat ?? TimeSpan.FromSeconds(15);
    }

    private static long CurrentLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }

    /// Pump events to `write` until cancellation or a write failure (a closed
    /// client). Never throws for either — both are normal subscription ends.
    public async Task RunAsync(Func<SseEvent, CancellationToken, Task> write, CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<SseEvent>();
        // The writer's exit — for ANY reason, a hung-up client included — must
        // end the reader task too, or a dead subscription would keep stat-
        // polling until daemon stop. One linked source covers both exits.
        using var sub = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var subCt = sub.Token;

        var reader = Task.Run(async () =>
        {
            try
            {
                while (!subCt.IsCancellationRequested)
                {
                    TrailPoll poll;
                    do
                    {
                        poll = _cursor.Poll();
                        if (poll.Reset) channel.Writer.TryWrite(new SseEvent.Reset());
                        foreach (var line in poll.Lines)
                            channel.Writer.TryWrite(new SseEvent.Line(line.EndOffset, line.Text));
                    } while (poll.More && !subCt.IsCancellationRequested);
                    await Task.Delay(_poll, subCt);
                }
            }
            catch (OperationCanceledException) { /* subscription ended */ }
            catch (Exception ex)
            {
                // A broken tail must end the stream visibly, never spin silently.
                Log.Warn("api", "api.tailError", new LogFields { Msg = ex.Message });
            }
            finally { channel.Writer.TryComplete(); }
        }, CancellationToken.None);

        try
        {
            while (!subCt.IsCancellationRequested)
            {
                if (channel.Reader.TryRead(out var item))
                {
                    await write(item, subCt);
                    continue;
                }
                // Quiet trail: wait for data OR the heartbeat interval. NEVER
                // WhenAny over ReadAsync — an abandoned ReadAsync still consumes
                // an item and would silently eat an event; WaitToReadAsync
                // consumes nothing and is safe to abandon on timeout.
                using var hb = CancellationTokenSource.CreateLinkedTokenSource(subCt);
                hb.CancelAfter(_heartbeat);
                try
                {
                    if (!await channel.Reader.WaitToReadAsync(hb.Token)) break;   // reader task ended
                }
                catch (OperationCanceledException) when (!subCt.IsCancellationRequested)
                {
                    await write(new SseEvent.Heartbeat(), subCt);
                }
            }
        }
        catch (OperationCanceledException) { /* stop/drain: normal end */ }
        catch (Exception)
        {
            // The client hung up (write failed): the subscription is over. The
            // socket error itself is noise, not signal — no log.
        }
        finally
        {
            sub.Cancel();    // writer gone ⇒ reader must go too
            await reader;    // never leak the poll task
        }
    }
}
