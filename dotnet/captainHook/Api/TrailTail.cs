using System.Text;
using System.Threading.Channels;
using CaptainHook.Actors;
using CaptainHook.Core;

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
//   * SSE event id = the POSITION after the line's trailing '\n', so a
//     reconnect's Last-Event-ID resumes exactly: zero duplicate, zero loss.
//     Since ADR-0021 a position is (segment, byte offset) packed into one
//     integer (TrailId); in the trail's first file it is the byte offset;
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
//     error); ROTATION (the daemon's keeper renames the active file to a
//     numbered segment) is FOLLOWED: drain the renamed file, wait one grace
//     for lines that raced the rename, step to the next — no reset (ADR-0021
//     rule 6); a DELETED segment (retention) resets forward, to the next one;
//     TRUNCATION/replacement of the active file in place (length < offset)
//     resets it to offset 0 and surfaces an explicit Reset event; EOF just
//     keeps polling.

/// One poll's outcome: complete lines (each with the byte offset AFTER its
/// '\n' — the SSE id that resumes exactly past it), whether the file was
/// truncated/replaced since the last poll, whether more bytes are already
/// known to be waiting (caller should poll again without delay), and how many
/// oversized lines were skipped (surfaced to the subscriber as a gap).
public sealed record TrailPoll(IReadOnlyList<TrailLine> Lines, bool Reset, bool More, int Skipped = 0);

/// One complete line and its resume id: the TrailId just past its '\n' (in
/// the trail's first file, exactly that byte offset).
public sealed record TrailLine(long EndOffset, string Text);

/// A single subscriber's cursor over the trail — across its rotations
/// (ADR-0021 rule 6). NOT thread-safe — each subscriber owns one and polls it
/// from one task.
///
/// The position is (Seq, Offset): a segment number and a byte offset in that
/// file, published as one id (`TrailId`, rule 5). The file for Seq is the
/// renamed segment `<stem>.<Seq><ext>` if it exists, else the active file when
/// Seq is the active number. Each poll resolves that BY NAME, so a rotation
/// between polls is simply seen: the segment name now exists, the cursor keeps
/// reading it from its offset, and once it has stayed at that segment's end
/// for one grace period (lines that raced the rename land in it within
/// microseconds), it steps to Seq+1 at offset 0 — as many steps as rotations
/// happened, never assuming one (N1). No `reset` for a rotation; a reset only
/// when the cursor's segment was deleted (re-anchored FORWARD, at the next
/// segment that exists), for an id from a segment that does not exist yet, or
/// for the active file truncated or replaced in place (today's rule). The
/// active number is resolved every poll, never cached, and a directory or
/// descriptor failure is a quiet poll, never a throw or a spin.
public sealed class TrailCursor
{
    /// How long a renamed segment must sit at its end before the cursor moves
    /// on: a writer that opened the path just before the rename still lands
    /// its line in the renamed file.
    internal static readonly TimeSpan RenameGrace = TimeSpan.FromSeconds(1);

    private readonly string _path;
    private readonly Func<long> _clock;
    private bool _align;      // offset came from a client: trust it only after proving it sits on a boundary
    private bool _skipping;   // mid-way through discarding a line longer than the read window
    private long? _eofSince;  // monotonic ms when a renamed segment was first seen fully drained

    /// `startId` is a TrailId (a client's Last-Event-ID, or TrailId.End for
    /// "from now"); `clock` is the monotonic source the rename grace is
    /// measured by (invariant 2).
    public TrailCursor(string path, long startId, bool alignForward = false, Func<long>? clock = null)
    {
        _path = path;
        (Seq, Offset) = TrailId.Decode(startId);
        _align = alignForward && Offset > 0;
        _clock = clock ?? (() => Environment.TickCount64);
    }

    /// The segment number the next poll reads from.
    public long Seq { get; private set; }

    /// The byte offset in that segment the next poll reads from. Rests on a
    /// line boundary except transiently while an unaligned resume discards
    /// toward one.
    public long Offset { get; private set; }

    /// The position as one resume id (`TrailId`).
    public long Id => TrailId.Encode(Seq, Offset);

    /// Read whatever complete lines are available, up to `maxBytes` of them —
    /// a huge backlog is delivered across repeated polls (More=true) so one
    /// poll can never balloon memory or starve cancellation.
    public TrailPoll Poll(int maxBytes = 128 * 1024)
    {
        // Bounded walk: each turn either returns or steps Seq forward, and
        // there are never more renamed segments than rotations.
        for (var turn = 0; turn < 1024; turn++)
        {
            var segment = TrailKeeper.SegmentPath(_path, Seq);
            bool renamed;
            long active;
            try
            {
                renamed = File.Exists(segment);
                // Resolved EVERY poll, never cached: a cached "this is the
                // active file" outlives a rename the cursor never saw (renamed
                // and then deleted between two polls) and reads the next file
                // under the old number — silently, at a stale offset.
                active = renamed ? 0 : TrailKeeper.ActiveSeq(_path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The logs dir is unreadable, gone, or out of descriptors:
                // quiet, like an absent file — never a throw, never a spin.
                return new TrailPoll([], Reset: false, More: false);
            }

            if (!renamed && Seq != active)
            {
                // A rename may have landed between the two checks above: if
                // the segment name exists now, this is a rotation, not a loss.
                if (File.Exists(segment)) continue;
                if (Seq > active) return ResetTo(active);   // an id from a segment that never existed
                // Our segment is gone (retention, or deleted by hand): the
                // honest answer is a reset, re-anchored FORWARD at the next
                // segment that exists — never back at the oldest, which a gap
                // in the numbers would turn into an endless replay loop.
                long next;
                try { next = TrailKeeper.Segments(_path).Select(x => x.Seq).Where(x => x > Seq).DefaultIfEmpty(active).Min(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return new TrailPoll([], Reset: false, More: false);
                }
                return ResetTo(next);
            }

            FileStream fs;
            try
            {
                // ReadWrite|Delete share: the emitters keep appending (and the
                // keeper may rename or delete) while we read — the reader must
                // never block a writer.
                fs = new FileStream(renamed ? segment : _path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                if (renamed) continue;                  // deleted under us: resolve again
                if (File.Exists(segment)) continue;     // renamed under us: follow it
                if (Offset == 0) return new TrailPoll([], Reset: false, More: false);   // nothing yet
                // The active file vanished with no rename: removed by hand.
                // Reset once; a recreated file starts at offset 0.
                return ResetCursor(more: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unreadable, or out of descriptors: quiet, try next beat.
                return new TrailPoll([], Reset: false, More: false);
            }

            TrailPoll poll;
            using (fs)
            {
                // Opened the active name, but a rotation may have raced the
                // open and this handle may be the NEXT file: if the segment
                // name exists now, or the active number moved on, drop it and
                // resolve again by name.
                if (!renamed && (File.Exists(segment) || SafeActiveSeq() != Seq)) continue;
                try
                {
                    poll = ReadFrom(fs, maxBytes);
                }
                catch (Exception ex) when (ex is EndOfStreamException or IOException)
                {
                    // The file shrank/vanished BETWEEN the length stat and the
                    // read (a truncation racing this poll). Quietly yield; the
                    // next poll sees len < Offset and resets — one beat of
                    // latency, never a dead subscription.
                    return new TrailPoll([], Reset: false, More: false);
                }
            }

            if (poll.Reset || poll.Lines.Count > 0 || poll.More || poll.Skipped > 0 || !renamed)
            {
                if (poll.Lines.Count > 0 || poll.More) _eofSince = null;
                return poll;
            }

            // A renamed segment, fully drained (a torn last line that will
            // never complete is abandoned with it). Wait out the grace for
            // lines that raced the rename, then step to the next segment.
            var now = _clock();
            if (_eofSince is not { } since)
            {
                _eofSince = now;
                return new TrailPoll([], Reset: false, More: false);
            }
            if (TimeSpan.FromMilliseconds(now - since) < RenameGrace)
                return new TrailPoll([], Reset: false, More: false);
            Seq++;
            Offset = 0;
            _align = false;
            _skipping = false;
            _eofSince = null;
        }
        // Only persistent races reach here; yield a beat rather than spin.
        return new TrailPoll([], Reset: false, More: false);
    }

    private long SafeActiveSeq()
    {
        try { return TrailKeeper.ActiveSeq(_path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Seq; }
    }

    private TrailPoll ResetTo(long seq)
    {
        Seq = seq;
        return ResetCursor(more: true);
    }

    private TrailPoll ResetCursor(bool more)
    {
        Offset = 0;
        _align = false;
        _skipping = false;
        _eofSince = null;
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
                    EndOffset: TrailId.Encode(Seq, Offset + i + 1),
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
        var start = lastEventId ?? TrailId.End(path);
        Anchor = start;
        // Always aligned: a client id is proven against the '\n' before it, and
        // a "from now" end can land inside a line still being written (a long
        // one spans page-wise size updates) — unaligned, the boundary check
        // would read that as a replaced file and replay it from offset 0.
        _cursor = new TrailCursor(path, start, alignForward: true);
        _poll = poll ?? TimeSpan.FromMilliseconds(200);
        _heartbeat = heartbeat ?? TimeSpan.FromSeconds(15);
        _capacity = Math.Max(1, capacity ?? 256);
    }

    /// Where this subscription starts reading — the client's own Last-Event-ID
    /// echoed, or the file's end for a from-now connect. ApiHost ships it in the
    /// hello record (`retry:` + `id:`) so the client holds a resume cursor from
    /// its very first byte: before 2026-08-16 a stream that wedged before any
    /// line arrived had no cursor at all, and its reconnect re-anchored "from
    /// now" — silently skipping everything written during the stall (found by
    /// the reviewer session on the bus, review-1ee7218-reply).
    public long Anchor { get; }

    /// Evictions not yet surfaced as a gap marker — test observability.
    internal long DroppedSoFar => Volatile.Read(ref _dropped);

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
