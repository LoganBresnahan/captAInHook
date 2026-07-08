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
/// truncated/replaced since the last poll, whether more bytes are already
/// known to be waiting (caller should poll again without delay), and how many
/// oversized lines were skipped (surfaced to the subscriber as a gap).
public sealed record TrailPoll(IReadOnlyList<TrailLine> Lines, bool Reset, bool More, int Skipped = 0);

public sealed record TrailLine(long EndOffset, string Text);

/// A single subscriber's cursor over the trail file. NOT thread-safe — each
/// subscriber owns one and polls it from one task.
public sealed class TrailCursor
{
    private readonly string _path;
    private bool _align;      // offset came from a client: trust it only after proving it sits on a boundary
    private bool _skipping;   // mid-way through discarding a line longer than the read window

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
            return ResetCursor(more: false);
        }
        catch (UnauthorizedAccessException)   // unreadable: quiet, like absent
        {
            return new TrailPoll([], Reset: false, More: false);
        }

        using (fs)
        {
            try
            {
                return ReadFrom(fs, maxBytes);
            }
            catch (Exception ex) when (ex is EndOfStreamException or IOException)
            {
                // The file shrank/vanished BETWEEN the length stat and the read
                // (a truncation racing this poll). Quietly yield; the next poll
                // sees len < Offset and resets — one beat of latency, never a
                // dead subscription.
                return new TrailPoll([], Reset: false, More: false);
            }
        }
    }

    private TrailPoll ResetCursor(bool more)
    {
        Offset = 0;
        _align = false;
        _skipping = false;
        return new TrailPoll([], Reset: true, More: more);
    }

    private TrailPoll ReadFrom(FileStream fs, int maxBytes)
    {
        {
            var len = fs.Length;
            if (len < Offset)
            {
                // Truncated or replaced with something shorter: every id we ever
                // issued is invalid. Restart the id space; report it explicitly.
                return ResetCursor(more: len > 0);
            }
            if (len == Offset) return new TrailPoll([], Reset: false, More: false);

            if (!_align && !_skipping && Offset > 0)
            {
                // Truncate-then-REGROW blind spot: a file replaced with content
                // at least as long as our offset passes the length check, and a
                // mid-line resume would emit garbage. Our own offsets always
                // rest just past a '\n', so re-verify that boundary byte every
                // poll — it can never false-fire on an untouched file, and a
                // replaced file fails it 255/256 times: same answer as a
                // truncation, reset the id space. (The residual 1/256
                // coincidence is accepted; trail rotation is rare and manual.
                // Skipped while mid-discard: those offsets are deliberately
                // mid-line.)
                fs.Seek(Offset - 1, SeekOrigin.Begin);
                if (fs.ReadByte() != '\n')
                    return ResetCursor(more: len > 0);
            }

            var take = (int)Math.Min(maxBytes, len - Offset);
            var buf = new byte[take];
            fs.Seek(Offset, SeekOrigin.Begin);
            fs.ReadExactly(buf, 0, take);

            if (_skipping)
            {
                // Mid-way through an oversized line: keep discarding until its
                // '\n', then count ONE skipped line (the subscriber surfaces it
                // as a gap). Progress every poll — a monster line can never
                // wedge the feed.
                var nl = Array.IndexOf(buf, (byte)'\n', 0, take);
                if (nl < 0)
                {
                    Offset += take;
                    return new TrailPoll([], Reset: false, More: Offset < len);
                }
                Offset += nl + 1;
                _skipping = false;
                return new TrailPoll([], Reset: false, More: Offset < len, Skipped: 1);
            }

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
                if (take == maxBytes)
                {
                    // No newline in a FULL window: the line is longer than the
                    // window itself. Delivering would need line-sized memory and
                    // a line-sized SSE frame; instead SKIP it — discard forward
                    // (like alignment) to its '\n' and surface one gap. The
                    // trail file still holds the line; only the live feed caps.
                    // Without this, one monster line would wedge every cursor
                    // forever while heartbeats kept the stream looking healthy.
                    _skipping = true;
                    Offset += take;
                    return new TrailPoll([], Reset: false, More: Offset < len);
                }
                // A partial line still being written: hold back, re-read whole
                // next poll. More only if alignment consumed bytes (progress) —
                // More without progress would spin the poll loop hot.
                Offset += start;
                return new TrailPoll([], Reset: false, More: start > 0 && Offset < len);
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
/// (production: WireJsonl.DefaultLogPath — the same trail both emitters append),
/// the poll/heartbeat cadences, and the per-subscriber buffer capacity
/// (sse-backpressure) — all test seams with production defaults.
public sealed record SseOptions(
    string TrailPath, TimeSpan? Poll = null, TimeSpan? Heartbeat = null, int? Capacity = null);

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
    /// `Dropped` lines were evicted for a slow consumer (sse-backpressure) —
    /// degraded honestly, never silently. Carries no id, so a reconnect resumes
    /// from the last LINE id and can actually recover the gap from the file.
    public sealed record Gap(long Dropped) : SseEvent;
    /// Keep-alive comment — also the dead-client probe: writing it fails on a
    /// closed connection, ending the subscription (and later its idle-defer).
    public sealed record Heartbeat : SseEvent;
}

/// One SSE subscriber's pipeline: a reader task stat-polls the cursor and
/// queues lines; the writer loop (RunAsync) drains them to the sink, emitting
/// a heartbeat when the trail is quiet.
///
/// Backpressure (sse-backpressure; ADR-0007 decision 5 / ADR-0004 d6): the
/// channel is BOUNDED, so a slow consumer can never grow the daemon — the
/// per-subscriber ceiling is capacity × the read window (a buffered line can
/// be up to maxBytes) plus one in-flight poll batch; hard-bounded, ~32MiB at
/// worst-case defaults, typically KBs — and never stalls the tail. A full buffer
/// gets ONE poll-beat of grace (a burst must not drop on a scheduler race);
/// past it the OLDEST line is evicted and counted — by hand, because
/// BoundedChannelFullMode.DropOldest discards silently and could never carry
/// the count. The count and the
/// truncation-reset signal both travel OUT OF BAND (Interlocked fields the
/// writer checks before each dequeue), which is what makes the gap marker and
/// the reset structurally un-droppable: they are never IN the buffer that
/// drops. A gap carries no id, so a reconnecting client resumes from its last
/// line id and recovers the dropped region from the file itself.
public sealed class TrailSubscription
{
    private readonly TrailCursor _cursor;
    private readonly TimeSpan _poll;
    private readonly TimeSpan _heartbeat;
    private readonly int _capacity;
    private long _dropped;        // lines evicted since the last gap marker
    private int _resetPending;    // a truncation-reset awaiting emission
    private bool _pressured;      // in a full-buffer episode (reader-task-local)

    /// `lastEventId` is the client's resume point (Last-Event-ID); null means
    /// "from now" — the file's current end, so a fresh subscriber sees only new
    /// events. Alignment is forced for client-supplied offsets, trusted for our
    /// own end-of-file stat.
    public TrailSubscription(
        string path, long? lastEventId,
        TimeSpan? poll = null, TimeSpan? heartbeat = null, int? capacity = null)
    {
        var start = lastEventId ?? CurrentLength(path);
        _cursor = new TrailCursor(path, start, alignForward: lastEventId is > 0);
        _poll = poll ?? TimeSpan.FromMilliseconds(200);
        _heartbeat = heartbeat ?? TimeSpan.FromSeconds(15);
        _capacity = Math.Max(1, capacity ?? 256);
    }

    /// Evictions not yet surfaced as a gap marker — test observability.
    internal long DroppedSoFar => Volatile.Read(ref _dropped);

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
        // Bounded: the slow-consumer memory ceiling. FullMode.Wait only shapes
        // blocking WriteAsync, which is never used — TryWrite returns false on
        // full and Enqueue evicts by hand so the drop is COUNTED. The channel
        // carries lines only; gaps and resets ride the out-of-band fields.
        var channel = Channel.CreateBounded<SseEvent>(new BoundedChannelOptions(_capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = true,     // the reader task is the only producer
            SingleReader = false,    // the writer loop dequeues; Enqueue's eviction also reads
        });
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
                        if (poll.Reset)
                        {
                            // The id space restarted: buffered pre-reset lines are
                            // from the replaced file — superseded by the reset
                            // itself (which also supersedes any pending gap; a
                            // "dropped N" straddling a reset would count lines of
                            // a file that no longer exists).
                            while (channel.Reader.TryRead(out _)) { }
                            Interlocked.Exchange(ref _dropped, 0);
                            Volatile.Write(ref _resetPending, 1);
                        }
                        // An oversized line the cursor skipped is a gap the
                        // subscriber must hear about — same counter, same
                        // un-droppable out-of-band path as slow-consumer drops.
                        if (poll.Skipped > 0) Interlocked.Add(ref _dropped, poll.Skipped);
                        foreach (var line in poll.Lines)
                            await EnqueueAsync(channel, new SseEvent.Line(line.EndOffset, line.Text), subCt);
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
                // Out-of-band signals FIRST, so neither can be starved by a full
                // buffer. For EVICTION drops the hole sits exactly here (they
                // remove the oldest buffered line, so everything still buffered
                // is newer); a SKIP gap (oversized line at the poll frontier) may
                // surface up to `capacity` lines early — the count stays exact
                // and reconnect-recovery from the file is unharmed, so the
                // imprecision is positional only. On a quiet trail a marker
                // waits for the next line or the heartbeat to wake this loop —
                // marker latency is heartbeat-bounded, never dropped.
                if (Interlocked.Exchange(ref _resetPending, 0) == 1)
                {
                    await write(new SseEvent.Reset(), subCt);
                    continue;
                }
                var dropped = Interlocked.Exchange(ref _dropped, 0);
                if (dropped > 0)
                {
                    await write(new SseEvent.Gap(dropped), subCt);
                    continue;
                }
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

    /// Buffer a line; when full, evict-and-count the oldest — the manual
    /// drop-oldest that BoundedChannelFullMode.DropOldest cannot be (it
    /// discards silently; the count IS the honesty).
    ///
    /// "Slow" means the consumer doesn't make room within one pipeline beat —
    /// NOT that it lost a scheduler race: a burst append larger than capacity
    /// with a perfectly healthy consumer must not drop, so the FIRST overflow
    /// of an episode waits one poll-beat for space (WriteAsync under a grace
    /// token) before declaring pressure. Once pressured, evictions run at full
    /// speed — a dead consumer never throttles the reader to grace-per-line —
    /// until a first-try write succeeds again, which ends the episode. The
    /// eviction TryRead can race the writer loop's dequeue — losing that race
    /// just means the line was DELIVERED, so only what this evicts is counted.
    private async ValueTask EnqueueAsync(Channel<SseEvent> channel, SseEvent.Line line, CancellationToken subCt)
    {
        if (channel.Writer.TryWrite(line))
        {
            _pressured = false;
            return;
        }
        if (!_pressured)
        {
            using var grace = CancellationTokenSource.CreateLinkedTokenSource(subCt);
            grace.CancelAfter(_poll);
            try
            {
                await channel.Writer.WriteAsync(line, grace.Token);
                return;
            }
            catch (OperationCanceledException) when (!subCt.IsCancellationRequested)
            {
                _pressured = true;   // no room in a full beat: the consumer is genuinely slow
            }
        }
        while (!channel.Writer.TryWrite(line))
        {
            // Count EVERY eviction: the channel carries only lines today, and if
            // that ever changes, an uncounted silent removal is the worse bug.
            if (channel.Reader.TryRead(out _))
                Interlocked.Increment(ref _dropped);
        }
    }
}
