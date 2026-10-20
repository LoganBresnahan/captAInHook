using CaptainHook.Api;
using CaptainHook.Core;
using CaptainHook.Wire;
using static CaptainHook.Tests.TestUtil;

namespace CaptainHook.Tests;

// ADR-0021 slice 2: TrailCursor follows the keeper's rotations. A rotation is
// never a reset: the cursor drains the renamed segment, waits one grace for
// lines that raced the rename, and steps on — through any number of
// rotations between two polls. Every line is delivered exactly once, ids rise
// strictly, and an id issued before a rotation still resumes after it. Each
// test here was mutation-checked against the cursor (see the commit).
public class TrailRotationCursorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chk-rotcursor-" + Guid.NewGuid().ToString("N")[..8]);
    private string Active => Path.Combine(_dir, "captainHook.jsonl");
    private long _now;

    public TrailRotationCursorTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private TrailCursor Cursor(long startId = 0, bool align = false) => new(Active, startId, align, clock: () => _now);
    private void Write(params string[] lines) { foreach (var l in lines) WireJsonl.Append(Active, l); }

    /// What the keeper does, without its size gate: rename the active file to
    /// the next segment and advance the mark.
    private void Rotate() => Assert.NotNull(new TrailKeeper(Active, 1, retention: null).Tick().Rotated);

    private void PassGrace() => _now += (long)TrailCursor.RenameGrace.TotalMilliseconds + 1;

    /// Poll until nothing more comes, passing the grace whenever the cursor
    /// sits at a renamed segment's end.
    private List<TrailLine> Drain(TrailCursor c, List<SseEventKind>? resets = null)
    {
        var got = new List<TrailLine>();
        for (var i = 0; i < 200; i++)
        {
            var p = c.Poll();
            if (p.Reset) resets?.Add(SseEventKind.Reset);
            got.AddRange(p.Lines);
            if (p.Lines.Count == 0 && !p.More && !p.Reset)
            {
                var before = c.Id;
                PassGrace();
                var q = c.Poll();
                if (q.Reset) resets?.Add(SseEventKind.Reset);
                got.AddRange(q.Lines);
                if (q.Lines.Count == 0 && !q.More && !q.Reset && c.Id == before) break;
            }
        }
        return got;
    }

    public enum SseEventKind { Reset }

    private static void ExactlyOnceInOrder(IEnumerable<TrailLine> got, params string[] expected)
    {
        var list = got.ToList();
        Assert.Equal(expected, list.Select(l => l.Text).ToArray());
        for (var i = 1; i < list.Count; i++)
            Assert.True(list[i].EndOffset > list[i - 1].EndOffset, $"ids must rise strictly: {list[i - 1].EndOffset} then {list[i].EndOffset}");
    }

    [Fact]
    public void TheFirstFilesIds_AreItsByteOffsets()
    {
        Write("a", "bb");
        Assert.Equal(new FileInfo(Active).Length, TrailId.End(Active));
        Assert.Equal(new[] { 2L, 5L }, Cursor().Poll().Lines.Select(l => l.EndOffset).ToArray());
        Assert.Equal((1L, 7L), TrailId.Decode(7));
        Assert.Equal((3L, 9L), TrailId.Decode(TrailId.Encode(3, 9)));
    }

    [Fact]
    public void ARotationMidStream_IsFollowed_NoReset_EveryLineOnce()
    {
        Write("one", "two");
        var c = Cursor();
        ExactlyOnceInOrder(c.Poll().Lines, "one", "two");

        Write("three");
        Rotate();
        Write("four", "five");

        var resets = new List<SseEventKind>();
        ExactlyOnceInOrder(Drain(c, resets), "three", "four", "five");
        Assert.Empty(resets);
        Assert.Equal(2, c.Seq);
    }

    /// A writer that opened the path just before the rename lands its line in
    /// the renamed segment. The cursor must not leave that segment until the
    /// grace has passed, and must deliver the late line exactly once.
    [Fact]
    public void ALineRacingTheRename_IsDeliveredOnce_BeforeTheCursorMovesOn()
    {
        Write("before");
        var c = Cursor();
        ExactlyOnceInOrder(c.Poll().Lines, "before");
        Rotate();
        Write("after-1");                          // the new active file

        Assert.Empty(c.Poll().Lines);              // segment 1 drained; grace starts
        Assert.Equal(1, c.Seq);
        _now += 100;
        Assert.Empty(c.Poll().Lines);              // still inside the grace: it must NOT move on yet
        Assert.Equal(1, c.Seq);
        File.AppendAllText(TrailKeeper.SegmentPath(Active, 1), "late\n");   // raced the rename
        _now += 100;                                // well inside the grace
        ExactlyOnceInOrder(c.Poll().Lines, "late");
        Assert.Equal(1, c.Seq);

        ExactlyOnceInOrder(Drain(c), "after-1");
    }

    /// N1: several rotations between two polls — each segment drained in order.
    [Fact]
    public void SeveralRotationsBetweenPolls_AreWalkedInOrder()
    {
        Write("s1-a");
        var c = Cursor();
        ExactlyOnceInOrder(c.Poll().Lines, "s1-a");

        Write("s1-b"); Rotate();
        Write("s2-a"); Rotate();
        Write("s3-a", "s3-b"); Rotate();
        Write("s4-a");

        var resets = new List<SseEventKind>();
        ExactlyOnceInOrder(Drain(c, resets), "s1-b", "s2-a", "s3-a", "s3-b", "s4-a");
        Assert.Empty(resets);
        Assert.Equal(4, c.Seq);
    }

    /// An id issued before a rotation (a client's Last-Event-ID) resumes after
    /// it: from where it was in what is now segment 1, then onward.
    [Fact]
    public void AnIdIssuedBeforeARotation_ResumesAcrossIt()
    {
        Write("a", "b", "c");
        var first = Cursor().Poll().Lines;
        var resumeAfterB = first[1].EndOffset;
        Rotate();
        Write("d");

        var resumed = Cursor(resumeAfterB, align: true);
        ExactlyOnceInOrder(Drain(resumed), "c", "d");
    }

    /// TrailId.End names the current end across rotations, so a "from now"
    /// reader sees only what comes next.
    [Fact]
    public void FromNow_AfterRotations_SeesOnlyNewLines()
    {
        Write("old-1"); Rotate();
        Write("old-2");
        var c = Cursor(TrailId.End(Active));
        Write("new");
        ExactlyOnceInOrder(Drain(c), "new");
    }

    /// Retention deleted the cursor's segment: the one honest answer is a
    /// reset, re-anchored at the oldest segment left.
    [Fact]
    public void ADeletedSegment_Resets_ToTheOldestLeft()
    {
        Write("s1"); Rotate();
        Write("s2"); Rotate();
        Write("s3");
        var c = Cursor(TrailId.Encode(1, 0));
        File.Delete(TrailKeeper.SegmentPath(Active, 1));

        var p = c.Poll();
        Assert.True(p.Reset);
        Assert.Equal(2, c.Seq);
        ExactlyOnceInOrder(Drain(c), "s2", "s3");
    }

    [Fact]
    public void AnIdFromASegmentThatNeverExisted_Resets_ToTheActiveFile()
    {
        Write("x");
        var c = Cursor(TrailId.Encode(9, 0));
        Assert.True(c.Poll().Reset);
        Assert.Equal(1, c.Seq);
        ExactlyOnceInOrder(Drain(c), "x");
    }

    /// Truncation of the active file in place is still today's reset.
    [Fact]
    public void TruncatingTheActiveFileInPlace_StillResets()
    {
        Write("aaaa", "bbbb");
        var c = Cursor();
        c.Poll();
        File.WriteAllText(Active, "");
        Assert.True(c.Poll().Reset);
        Assert.Equal(0, c.Offset);
    }

    /// End to end through the SSE subscription, on the real clock: a rotation
    /// mid-stream is followed with no Reset event, every line once.
    [Fact]
    public async Task TheSubscription_FollowsARotation_WithoutAReset()
    {
        Write("pre");
        var sub = new TrailSubscription(Active, lastEventId: 0, poll: TimeSpan.FromMilliseconds(20),
            heartbeat: TimeSpan.FromSeconds(30));
        var events = new System.Collections.Concurrent.ConcurrentQueue<SseEvent>();
        using var cts = new CancellationTokenSource();
        var run = sub.RunAsync((e, _) => { events.Enqueue(e); return Task.CompletedTask; }, cts.Token);
        try
        {
            await PollUntilAsync(() => Task.FromResult(events.OfType<SseEvent.Line>().Any()), TimeSpan.FromSeconds(10), "first line");
            Rotate();
            Write("post-1", "post-2");
            await PollUntilAsync(() => Task.FromResult(events.OfType<SseEvent.Line>().Count() >= 3), TimeSpan.FromSeconds(10), "lines after the rotation");
        }
        finally
        {
            cts.Cancel();
            await run;
        }
        Assert.DoesNotContain(events, e => e is SseEvent.Reset);
        var lines = events.OfType<SseEvent.Line>().ToList();
        Assert.Equal(new[] { "pre", "post-1", "post-2" }, lines.Select(l => l.Text).ToArray());
        Assert.True(lines[2].Id > lines[1].Id && lines[1].Id > lines[0].Id);
    }

    // ---- the adversarial verify's findings (slice 2), each pinned --------------

    /// A: a gap in the segment numbers (a kept future-mtime segment, a stubborn
    /// or hand-deleted one) must not trap the cursor in reset-and-replay.
    [Fact]
    public void AGapInTheSegments_DoesNotTrapTheCursor()
    {
        Write("s1"); Rotate(); Write("s2"); Rotate(); Write("s3"); Rotate(); Write("s4");
        File.Delete(TrailKeeper.SegmentPath(Active, 2));
        var c = Cursor(TrailId.Encode(1, 0));
        var got = new List<TrailLine>();
        var resets = 0;
        for (var i = 0; i < 40; i++)
        {
            var p = c.Poll();
            if (p.Reset) resets++;
            got.AddRange(p.Lines);
            if (!p.More) PassGrace();
        }
        Assert.Equal(new[] { "s1", "s3", "s4" }, got.Select(l => l.Text).ToArray());
        Assert.Equal(1, resets);   // one honest reset for the missing segment 2, then onward
    }

    /// C: a segment renamed AND deleted between two polls (the cursor never saw
    /// the rename) must not be read under the old number, and nothing after it
    /// may be lost silently.
    [Fact]
    public void ASegmentDeletedBeforeTheCursorSawTheRename_LosesNothingSilently()
    {
        Write("a-0123456789", "b-0123456789");
        var c = Cursor();
        c.Poll();
        Rotate();
        File.Delete(TrailKeeper.SegmentPath(Active, 1));
        Write("c");
        Assert.All(Drain(c), l => Assert.Equal(2L, TrailId.Decode(l.EndOffset).Seq));
        Write("d-0123456789", "e-0123456789"); Rotate(); Write("f"); Write("g");
        Assert.Equal(new[] { "d-0123456789", "e-0123456789", "f", "g" }, Drain(c).Select(l => l.Text).ToArray());
    }

    /// B: a rotation racing a fresh cursor's first resolve is a rotation, not
    /// a deleted segment: no reset.
    [Fact]
    public async Task ARotationRacingAFreshCursor_NeverResets()
    {
        var resets = 0;
        using var stop = new CancellationTokenSource();
        var keeper = new TrailKeeper(Active, 1, retention: null);
        var rotator = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested) { Write("x"); keeper.Tick(); }
        });
        try
        {
            for (var i = 0; i < 300; i++)
            {
                Write("y");
                var c = new TrailCursor(Active, TrailId.End(Active), alignForward: true, clock: () => _now);
                for (var k = 0; k < 3; k++) if (c.Poll().Reset) resets++;
            }
        }
        finally
        {
            stop.Cancel();
            await rotator;
        }
        Assert.True(TrailKeeper.HighWater(Active) > 20, "the rotator must actually have raced the cursors");
        Assert.Equal(0, resets);
    }

    /// D: an unreadable logs dir (like EMFILE: the dir cannot be listed) is a
    /// quiet poll — no throw, no More=true spin.
    [Fact]
    public void AnUnreadableLogsDir_IsAQuietPoll()
    {
        Write("a"); Rotate(); Write("b");
        var c = Cursor(TrailId.Encode(1, 0));
        File.SetUnixFileMode(_dir, UnixFileMode.None);
        try
        {
            var p = c.Poll();
            Assert.False(p.More);
            Assert.Empty(p.Lines);
        }
        finally { File.SetUnixFileMode(_dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    }

    /// E: a "from now" subscriber whose start lands inside a line still being
    /// written must not read it as a replaced file and replay the whole
    /// active file: it skips that line and delivers only what follows.
    [Fact]
    public async Task FromNow_AnchoredMidLine_SkipsThatLine_AndReplaysNothing()
    {
        Write("old-1", "old-2");
        File.AppendAllText(Active, "{\"partial");          // a writer mid-line
        var sub = new TrailSubscription(Active, lastEventId: null, poll: TimeSpan.FromMilliseconds(20),
            heartbeat: TimeSpan.FromSeconds(30));
        File.AppendAllText(Active, "\"}\n");                // …it finishes
        Write("new");
        var events = new System.Collections.Concurrent.ConcurrentQueue<SseEvent>();
        using var cts = new CancellationTokenSource();
        var run = sub.RunAsync((e, _) => { events.Enqueue(e); return Task.CompletedTask; }, cts.Token);
        try
        {
            // A replay would start from offset 0, so "old-1" would arrive
            // before "new": waiting for "new" is enough to see one.
            await PollUntilAsync(() => Task.FromResult(events.OfType<SseEvent.Line>().Any(l => l.Text == "new")),
                TimeSpan.FromSeconds(10), "the new line");
        }
        finally
        {
            cts.Cancel();
            await run;
        }
        Assert.DoesNotContain(events, e => e is SseEvent.Reset);
        Assert.Equal(new[] { "new" }, events.OfType<SseEvent.Line>().Select(l => l.Text).ToArray());
    }
}
