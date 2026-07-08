using System.Net;
using System.Net.Http.Headers;
using System.Text;
using CaptainHook.Api;
using CaptainHook.Core;
using CaptainHook.Wire;
using static CaptainHook.Tests.TestUtil;

namespace CaptainHook.Tests;

// sse-trail-tail (ADR-0007 decision 5): the SSE feed is a stat-poll tail of the
// JSONL trail file with BYTE-OFFSET event ids. Three layers, tested in order:
// TrailCursor (the sharp offset/half-line/truncation logic, pure over temp
// files), TrailSubscription (the poll→channel→writer pipeline, injectable
// sink), and GET /api/v1/events over real HTTP. The tailer is schema-blind —
// these tests use arbitrary single-line strings, not trail-schema JSON, exactly
// because the contract is "newline-delimited bytes", nothing more.

internal sealed class TempTrail : IDisposable
{
    public string Path { get; } =
        System.IO.Path.Combine("/tmp", "chk-trail-" + Guid.NewGuid().ToString("N")[..8] + ".jsonl");

    /// Append lines exactly as the emitters do: newline-terminated, one write.
    public void Append(params string[] lines) =>
        File.AppendAllText(Path, string.Concat(lines.Select(l => l + "\n")));

    /// Append raw bytes/text with NO trailing newline — a half-written line.
    public void AppendRaw(string text) => File.AppendAllText(Path, text);

    public void Dispose()
    {
        try { File.Delete(Path); } catch { /* best-effort */ }
    }
}

public class TrailCursorTests
{
    [Fact]
    public void CompleteLines_CarryByteEndOffsets_Utf8Safe()
    {
        using var trail = new TempTrail();
        trail.Append("plain", "hé🪝llo", "third");   // second line is multi-byte UTF-8

        var cursor = new TrailCursor(trail.Path, 0);
        var poll = cursor.Poll();

        Assert.False(poll.Reset);
        Assert.Equal(3, poll.Lines.Count);
        Assert.Equal("plain", poll.Lines[0].Text);
        Assert.Equal("hé🪝llo", poll.Lines[1].Text);

        // Offsets are BYTES after each '\n' — decode-then-count would drift on
        // the multi-byte line; these are computed from UTF-8 lengths.
        var off1 = Encoding.UTF8.GetByteCount("plain") + 1;
        var off2 = off1 + Encoding.UTF8.GetByteCount("hé🪝llo") + 1;
        var off3 = off2 + Encoding.UTF8.GetByteCount("third") + 1;
        Assert.Equal(off1, poll.Lines[0].EndOffset);
        Assert.Equal(off2, poll.Lines[1].EndOffset);
        Assert.Equal(off3, poll.Lines[2].EndOffset);
        Assert.Equal(off3, cursor.Offset);
        Assert.Equal(new FileInfo(trail.Path).Length, cursor.Offset);
    }

    [Fact]
    public void HalfWrittenLine_HeldBack_UntilItsNewlineArrives()
    {
        using var trail = new TempTrail();
        trail.Append("whole");
        trail.AppendRaw("par");   // a concurrent O_APPEND caught mid-line

        var cursor = new TrailCursor(trail.Path, 0);
        var first = cursor.Poll();
        Assert.Single(first.Lines);
        Assert.Equal("whole", first.Lines[0].Text);   // the partial is NOT emitted

        trail.AppendRaw("tial\n");   // the line completes
        var second = cursor.Poll();
        Assert.Single(second.Lines);
        Assert.Equal("partial", second.Lines[0].Text);   // whole, never split
    }

    [Fact]
    public void Resume_FromASavedOffset_NoDuplicate_NoLoss()
    {
        using var trail = new TempTrail();
        trail.Append("a", "b", "c", "d", "e");

        var first = new TrailCursor(trail.Path, 0);
        var lines = first.Poll().Lines;
        var resumeAt = lines[2].EndOffset;   // the client saw a,b,c

        var resumed = new TrailCursor(trail.Path, resumeAt, alignForward: true);
        var rest = resumed.Poll().Lines;
        Assert.Equal(["d", "e"], rest.Select(l => l.Text).ToArray());
        Assert.Equal(lines[3].EndOffset, rest[0].EndOffset);   // same id space
    }

    [Fact]
    public void MidLineResume_SelfHeals_ForwardToTheNextBoundary()
    {
        using var trail = new TempTrail();
        trail.Append("first-line", "second-line");

        // A bogus/stale id landing mid-"first-line": the byte before it is not
        // '\n', so the cursor discards forward — no garbage half-line event.
        var cursor = new TrailCursor(trail.Path, 3, alignForward: true);
        var poll = cursor.Poll();
        Assert.Single(poll.Lines);
        Assert.Equal("second-line", poll.Lines[0].Text);
    }

    [Fact]
    public void Truncation_Resets_ThenReadsTheNewFileFromZero()
    {
        using var trail = new TempTrail();
        trail.Append("old-1", "old-2", "old-3");
        var cursor = new TrailCursor(trail.Path, 0);
        Assert.Equal(3, cursor.Poll().Lines.Count);

        File.WriteAllText(trail.Path, "fresh\n");   // rotation: shorter file
        var reset = cursor.Poll();
        Assert.True(reset.Reset);
        Assert.Empty(reset.Lines);
        Assert.Equal(0, cursor.Offset);

        var after = cursor.Poll();
        Assert.Single(after.Lines);
        Assert.Equal("fresh", after.Lines[0].Text);
        Assert.Equal(Encoding.UTF8.GetByteCount("fresh") + 1, after.Lines[0].EndOffset);
    }

    [Fact]
    public void TruncateThenRegrowLarger_IsStillDetected_AsAReset()
    {
        // Length-only stat can't see a file REPLACED with something longer —
        // the boundary-byte re-check does: our offset rests just past a '\n';
        // a replaced file has some other byte there (the "aaaa..." filler makes
        // the check deterministic in this test), so the cursor resets instead
        // of emitting garbage from mid-line.
        using var trail = new TempTrail();
        trail.Append("one", "two");
        var cursor = new TrailCursor(trail.Path, 0);
        Assert.Equal(2, cursor.Poll().Lines.Count);

        File.WriteAllText(trail.Path, new string('a', 64) + "\nfresh\n");   // longer than before
        var reset = cursor.Poll();
        Assert.True(reset.Reset, "a regrown replacement must reset, not emit garbage");
        Assert.Equal(0, cursor.Offset);

        var after = cursor.Poll();
        Assert.Equal([new string('a', 64), "fresh"],
            after.Lines.Select(l => l.Text).ToArray());
    }

    [Fact]
    public void AbsentFile_IsQuiet_ThenDelivers_OnceCreated()
    {
        using var trail = new TempTrail();   // never written: no file
        var cursor = new TrailCursor(trail.Path, 0);
        var quiet = cursor.Poll();
        Assert.False(quiet.Reset);
        Assert.Empty(quiet.Lines);

        trail.Append("born");
        Assert.Equal("born", Assert.Single(cursor.Poll().Lines).Text);
    }

    [Fact]
    public void FileVanishing_MidStream_ResetsOnce()
    {
        using var trail = new TempTrail();
        trail.Append("here");
        var cursor = new TrailCursor(trail.Path, 0);
        Assert.Single(cursor.Poll().Lines);

        File.Delete(trail.Path);
        Assert.True(cursor.Poll().Reset);      // removal reported once
        Assert.False(cursor.Poll().Reset);     // then quiet, not a reset storm

        trail.Append("reborn");                // a recreated file: fresh id space
        var after = cursor.Poll();
        Assert.Equal("reborn", Assert.Single(after.Lines).Text);
    }

    [Fact]
    public void BigBacklog_IsDeliveredAcrossPolls_BoundedPerPoll()
    {
        using var trail = new TempTrail();
        var lines = Enumerable.Range(0, 500).Select(i => $"line-{i:D4}-{new string('x', 100)}").ToArray();
        trail.Append(lines);   // ~52KB, several 16KB polls

        var cursor = new TrailCursor(trail.Path, 0);
        var got = new List<string>();
        TrailPoll poll;
        var polls = 0;
        do
        {
            poll = cursor.Poll(maxBytes: 16 * 1024);
            got.AddRange(poll.Lines.Select(l => l.Text));
            polls++;
        } while (poll.More);

        Assert.Equal(lines, got);          // everything, in order, exactly once
        Assert.True(polls > 1, "the backlog must span multiple bounded polls");
    }

    [Fact]
    public void AnOversizedLine_IsSkippedNotWedged_AndCounted()
    {
        // A single line longer than the read window must not stall the feed
        // forever (the adversarial-verify find: no '\n' in a full view used to
        // mean zero progress, silently, while heartbeats kept flowing). It is
        // skipped — discarded across polls like an alignment — counted once,
        // and everything behind it still flows.
        using var trail = new TempTrail();
        trail.Append("before", new string('x', 5000), "after");

        var cursor = new TrailCursor(trail.Path, 0);
        var got = new List<string>();
        var skipped = 0;
        TrailPoll poll;
        var polls = 0;
        do
        {
            poll = cursor.Poll(maxBytes: 1024);   // window << the monster line
            got.AddRange(poll.Lines.Select(l => l.Text));
            skipped += poll.Skipped;
            Assert.True(++polls < 100, "the oversized line must never wedge the cursor");
        } while (poll.More);

        Assert.Equal(["before", "after"], got);   // the feed survived the monster
        Assert.Equal(1, skipped);                 // and said so, exactly once
        Assert.Equal(new FileInfo(trail.Path).Length, cursor.Offset);
    }

    [Fact]
    public void EmptyLines_AreRealEvents_WithByteTrueOffsets()
    {
        using var trail = new TempTrail();
        trail.AppendRaw("a\n\nb\n");
        var poll = new TrailCursor(trail.Path, 0).Poll();
        Assert.Equal(["a", "", "b"], poll.Lines.Select(l => l.Text).ToArray());
        Assert.Equal(3, poll.Lines[1].EndOffset);   // the empty line's own '\n'
        Assert.Equal(5, poll.Lines[2].EndOffset);
    }

    [Fact]
    public void CrlfLines_AreTolerated_OffsetsStayByteTrue()
    {
        using var trail = new TempTrail();
        trail.AppendRaw("win\r\nposix\n");

        var cursor = new TrailCursor(trail.Path, 0);
        var poll = cursor.Poll();
        Assert.Equal(["win", "posix"], poll.Lines.Select(l => l.Text).ToArray());
        Assert.Equal(5, poll.Lines[0].EndOffset);    // "win\r\n" is 5 bytes
        Assert.Equal(11, poll.Lines[1].EndOffset);
    }
}

public class TrailSubscriptionTests
{
    [Fact]
    public async Task DeliversLines_ThenHeartbeats_WhenTheTrailGoesQuiet()
    {
        using var trail = new TempTrail();
        trail.Append("one", "two");

        var events = new List<SseEvent>();
        var sawHeartbeat = new TaskCompletionSource();
        using var cts = new CancellationTokenSource();

        var sub = new TrailSubscription(trail.Path, lastEventId: 0,
            poll: TimeSpan.FromMilliseconds(25), heartbeat: TimeSpan.FromMilliseconds(120));
        var run = sub.RunAsync((e, _) =>
        {
            lock (events) events.Add(e);
            if (e is SseEvent.Heartbeat) sawHeartbeat.TrySetResult();
            return Task.CompletedTask;
        }, cts.Token);

        await sawHeartbeat.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));   // RunAsync never throws on cancel

        lock (events)
        {
            Assert.Equal(["one", "two"],
                events.OfType<SseEvent.Line>().Select(l => l.Text).ToArray());
            // Lines precede the quiet-trail heartbeat.
            var firstHb = events.FindIndex(e => e is SseEvent.Heartbeat);
            var lastLine = events.FindLastIndex(e => e is SseEvent.Line);
            Assert.True(lastLine < firstHb, "heartbeats only once the trail went quiet");
        }
    }

    [Fact]
    public async Task AWriteFailure_EndsTheSubscription_AndItsPolling()
    {
        using var trail = new TempTrail();
        trail.Append("only");

        var sub = new TrailSubscription(trail.Path, lastEventId: 0,
            poll: TimeSpan.FromMilliseconds(25), heartbeat: TimeSpan.FromSeconds(30));
        // The sink dies on first write — a hung-up client. RunAsync must return
        // (not throw) and end its reader task; a leaked poller would keep the
        // file handle churning until daemon stop.
        var run = sub.RunAsync((_, _) => throw new IOException("client gone"), CancellationToken.None);
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }
}

// sse-backpressure (ADR-0007 decision 5 / ADR-0004 d6): a slow consumer gets
// drop-oldest plus an explicit gap marker carrying the exact dropped count —
// never a growing daemon, never a silent hole, never a disconnect. Driven at
// the subscription level with a stallable sink: TCP buffering would make an
// HTTP-level version nondeterministic; here every eviction is exact.
public class SseBackpressureTests
{
    [Fact]
    public async Task SlowConsumer_DropsOldest_ThenGetsOneGapWithTheExactCount()
    {
        using var trail = new TempTrail();
        trail.Append("L0");

        var stall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new List<SseEvent>();
        using var cts = new CancellationTokenSource();
        var sub = new TrailSubscription(trail.Path, lastEventId: 0,
            poll: TimeSpan.FromMilliseconds(20), heartbeat: TimeSpan.FromSeconds(30), capacity: 4);

        var run = sub.RunAsync(async (e, _) =>
        {
            var stallNow = false;
            lock (events)
            {
                events.Add(e);
                stallNow = events.Count == 1;   // take L0, then stall — the slow client
            }
            if (stallNow) await stall.Task;
        }, cts.Token);

        // While the writer is stalled holding L0, nine more lines arrive. The
        // reader free-runs: capacity 4 buffers L1..L4, then L5..L9 each evict
        // the oldest — exactly five drops (L1..L5), buffer left with L6..L9.
        await PollUntilAsync(() =>
        {
            lock (events) { if (events.Count == 0) return Task.FromResult(false); }
            trail.Append("L1", "L2", "L3", "L4", "L5", "L6", "L7", "L8", "L9");
            return Task.FromResult(true);
        }, TimeSpan.FromSeconds(5), "writer took L0");
        await PollUntilAsync(() => Task.FromResult(sub.DroppedSoFar == 5),
            TimeSpan.FromSeconds(10), "five evictions counted");

        stall.TrySetResult();   // the client wakes up
        await PollUntilAsync(() =>
        {
            lock (events) return Task.FromResult(events.OfType<SseEvent.Line>().Count() == 5);
        }, TimeSpan.FromSeconds(10), "buffered lines delivered after the gap");
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        lock (events)
        {
            var meaningful = events.Where(e => e is not SseEvent.Heartbeat).ToList();
            // The story a degraded consumer sees: L0, an HONEST gap of exactly 5,
            // then the newest lines — never a silent hole.
            Assert.Equal("L0", Assert.IsType<SseEvent.Line>(meaningful[0]).Text);
            Assert.Equal(5, Assert.IsType<SseEvent.Gap>(meaningful[1]).Dropped);
            Assert.Equal(["L6", "L7", "L8", "L9"],
                meaningful.Skip(2).Cast<SseEvent.Line>().Select(l => l.Text).ToArray());
            Assert.Equal(0, sub.DroppedSoFar);   // the counter was consumed by the gap
        }
    }

    [Fact]
    public async Task Reset_ClearsTheBuffer_AndSupersedesAnyPendingGap()
    {
        using var trail = new TempTrail();
        trail.Append("old-0");

        var stall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new List<SseEvent>();
        using var cts = new CancellationTokenSource();
        var sub = new TrailSubscription(trail.Path, lastEventId: 0,
            poll: TimeSpan.FromMilliseconds(20), heartbeat: TimeSpan.FromSeconds(30), capacity: 2);

        var run = sub.RunAsync(async (e, _) =>
        {
            var stallNow = false;
            lock (events)
            {
                events.Add(e);
                stallNow = events.Count == 1;
            }
            if (stallNow) await stall.Task;
        }, cts.Token);

        // Writer stalled on old-0; old-1..old-4 overflow capacity 2 (drops
        // accumulate); then the file is REPLACED — pre-reset buffer and the
        // pending gap are both superseded by the reset itself.
        await PollUntilAsync(() =>
        {
            lock (events) { if (events.Count == 0) return Task.FromResult(false); }
            trail.Append("old-1", "old-2", "old-3", "old-4");
            return Task.FromResult(true);
        }, TimeSpan.FromSeconds(5), "writer took old-0");
        await PollUntilAsync(() => Task.FromResult(sub.DroppedSoFar >= 1),
            TimeSpan.FromSeconds(10), "pressure built");

        File.WriteAllText(trail.Path, "new-0\n");   // rotation: id space restarts
        await PollUntilAsync(() => Task.FromResult(sub.DroppedSoFar == 0),
            TimeSpan.FromSeconds(10), "reset superseded the pending gap");

        stall.TrySetResult();
        await PollUntilAsync(() =>
        {
            lock (events) return Task.FromResult(events.Any(e => e is SseEvent.Line { Text: "new-0" }));
        }, TimeSpan.FromSeconds(10), "post-reset line delivered");
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        lock (events)
        {
            var meaningful = events.Where(e => e is not SseEvent.Heartbeat).ToList();
            Assert.Equal("old-0", Assert.IsType<SseEvent.Line>(meaningful[0]).Text);
            // Reset arrives before anything post-rotation; NO gap marker and no
            // old-1..old-4 — the reset superseded them all.
            Assert.IsType<SseEvent.Reset>(meaningful[1]);
            Assert.DoesNotContain(meaningful, e => e is SseEvent.Gap);
            Assert.DoesNotContain(meaningful, e => e is SseEvent.Line l && l.Text.StartsWith("old-") && l.Text != "old-0");
            var newLine = Assert.IsType<SseEvent.Line>(meaningful[2]);
            Assert.Equal("new-0", newLine.Text);
            Assert.Equal(Encoding.UTF8.GetByteCount("new-0") + 1, newLine.Id);   // fresh id space
        }
    }

    [Fact]
    public async Task AFastConsumer_NeverSeesAGap()
    {
        using var trail = new TempTrail();
        var events = new List<SseEvent>();
        using var cts = new CancellationTokenSource();
        var sub = new TrailSubscription(trail.Path, lastEventId: 0,
            poll: TimeSpan.FromMilliseconds(15), heartbeat: TimeSpan.FromSeconds(30), capacity: 4);

        var run = sub.RunAsync((e, _) =>
        {
            lock (events) events.Add(e);
            return Task.CompletedTask;
        }, cts.Token);

        for (var batch = 0; batch < 10; batch++)
        {
            trail.Append(Enumerable.Range(0, 10).Select(i => $"b{batch}-{i}").ToArray());
            await PollUntilAsync(() =>
            {
                lock (events) return Task.FromResult(events.OfType<SseEvent.Line>().Count() == (batch + 1) * 10);
            }, TimeSpan.FromSeconds(10), $"batch {batch} drained");
        }
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        lock (events)
        {
            Assert.Equal(100, events.OfType<SseEvent.Line>().Count());
            Assert.DoesNotContain(events, e => e is SseEvent.Gap);   // keeping up = full fidelity
        }
    }
}

// idle-exit-defer (ADR-0007 decision 7): an open SSE subscription is an
// attached observer — it defers the mandatory idle-exit exactly as a non-empty
// background queue does — and any API request resets the idle clock. The defer
// holds for the CURRENT lock-holder only: drain-start Stop() terminates every
// stream, so a superseded daemon can never be pinned alive by a forgotten tab.
// FakeClock idiom (IdleExitTests): window math runs on the injected monotonic
// clock, so "did not exit" asserts are deterministic — a frozen clock can never
// satisfy the window. Real time paces only watchdog ticks and SSE heartbeats.
public class SseIdleDeferTests
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(10);   // fake-clock seconds

    private static async Task<Task<int>> StartAsync(
        TempRuntimeDir dir, TempTrail trail, FakeClock clock, int apiPort)
    {
        // Tight REAL-time heartbeat: closing a client on a quiet trail is only
        // discovered by a failed heartbeat write — 80ms keeps the test brisk.
        var sse = new SseOptions(trail.Path,
            Poll: TimeSpan.FromMilliseconds(30), Heartbeat: TimeSpan.FromMilliseconds(80));
        var daemon = Task.Run(() => DaemonHost.RunAsync(dir.Paths, NoHarnessDir(), CancellationToken.None,
            new Registry(), drainDeadline: TimeSpan.FromSeconds(5), idleWindow: Window,
            clock: clock.Now, apiPort: apiPort, sse: sse));
        await PollUntilAsync(async () =>
            await ShimClient.TryForwardAsync(dir.Paths.SocketPath,
                new HookRequest("warmup00", "session-start", "claude-code", "{}"u8.ToArray()))
                is ForwardOutcome.Answered,
            TimeSpan.FromSeconds(15), "daemon up");
        return daemon;
    }

    private static string TokenOf(TempRuntimeDir dir) =>
        ApiDiscovery.TryRead(dir.Paths.ApiJsonPath)!.Token;

    [Fact]
    public async Task AnOpenSseStream_DefersIdleExit_ClosingItReleasesTheDaemon()
    {
        using var dir = new TempRuntimeDir();
        using var trail = new TempTrail();
        var clock = new FakeClock();
        var apiPort = FreeTcpPort();
        var daemon = await StartAsync(dir, trail, clock, apiPort);

        var client = new SseClient();
        Assert.Equal(HttpStatusCode.OK, await client.OpenAsync(apiPort, TokenOf(dir)));

        // THREE windows past — but the open stream is an attached observer:
        // deterministically alive (the watchdog refreshes the stamp while
        // OpenStreams > 0; the frozen clock can never satisfy the window).
        clock.Advance(TimeSpan.FromSeconds(30));
        await Assert.ThrowsAsync<TimeoutException>(
            () => daemon.WaitAsync(TimeSpan.FromMilliseconds(1500)));

        // The tab closes: the heartbeat probe detects the dead client and the
        // defer releases — observable as /status.openStreams → 0. (Polling
        // /status stamps the idle clock, but the FAKE clock is frozen between
        // Advances, so the stamp value never moves — determinism holds.)
        await client.DisposeAsync();
        var token = TokenOf(dir);
        await PollUntilAsync(async () =>
        {
            var (_, body) = await ApiGetAsync(apiPort, token, "/api/v1/status");
            return System.Text.Json.JsonDocument.Parse(body)
                .RootElement.GetProperty("openStreams").GetInt32() == 0;
        }, TimeSpan.FromSeconds(10), "the dead client's defer released");

        // A fresh window after the release: the daemon starves out.
        clock.Advance(TimeSpan.FromSeconds(11));
        Assert.Equal(0, await daemon.WaitAsync(TimeSpan.FromSeconds(15)));
    }

    [Fact]
    public async Task AnApiRequest_RefreshesTheIdleClock()
    {
        using var dir = new TempRuntimeDir();
        using var trail = new TempTrail();
        var clock = new FakeClock();
        var apiPort = FreeTcpPort();
        var daemon = await StartAsync(dir, trail, clock, apiPort);
        var token = TokenOf(dir);

        clock.Advance(TimeSpan.FromSeconds(6));
        var (status, _) = await ApiGetAsync(apiPort, token, "/api/v1/status");   // stamps t=6
        Assert.Equal(HttpStatusCode.OK, status);

        clock.Advance(TimeSpan.FromSeconds(6));   // t=12; idle-for = 6s < 10s
        await Assert.ThrowsAsync<TimeoutException>(
            () => daemon.WaitAsync(TimeSpan.FromMilliseconds(1500)));   // deterministically alive

        clock.Advance(TimeSpan.FromSeconds(5));   // t=17; idle-for = 11s >= 10s
        Assert.Equal(0, await daemon.WaitAsync(TimeSpan.FromSeconds(15)));
    }

    [Fact]
    public async Task Drain_TerminatesTheStream_AndIsNeverPinnedByIt()
    {
        // The current-lock-holder-only edge, end to end: a draining daemon
        // must terminate its subscribers at drain start — never wait on them.
        using var dir = new TempRuntimeDir();
        using var trail = new TempTrail();
        using var stop = new CancellationTokenSource();
        var apiPort = FreeTcpPort();
        var sse = new SseOptions(trail.Path, Poll: TimeSpan.FromMilliseconds(30));
        var daemon = Task.Run(() => DaemonHost.RunAsync(dir.Paths, NoHarnessDir(), stop.Token,
            new Registry(), drainDeadline: TimeSpan.FromSeconds(5), apiPort: apiPort, sse: sse));
        await PollUntilAsync(async () =>
            await ShimClient.TryForwardAsync(dir.Paths.SocketPath,
                new HookRequest("warmup01", "session-start", "claude-code", "{}"u8.ToArray()))
                is ForwardOutcome.Answered,
            TimeSpan.FromSeconds(15), "daemon up");

        await using var client = new SseClient();
        Assert.Equal(HttpStatusCode.OK, await client.OpenAsync(apiPort, TokenOf(dir)));

        stop.Cancel();   // drain: api.Stop() ends the stream at drain START
        Assert.Equal(0, await daemon.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Null(await client.ReadFrameAsync(TimeSpan.FromSeconds(5)));   // the stream was ended
    }
}

/// A minimal SSE reader over HttpClient's streaming response — shared by the
/// pure-listener HTTP tests and the real-daemon idle-defer tests.
internal sealed class SseClient : IAsyncDisposable
{
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private HttpResponseMessage? _resp;
    private StreamReader? _reader;

    public async Task<HttpStatusCode> OpenAsync(int port, string token, long? lastEventId = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/api/v1/events");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (lastEventId is { } id) req.Headers.Add("Last-Event-ID", id.ToString());
        _resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead)
            .WaitAsync(TimeSpan.FromSeconds(10));
        if (_resp.StatusCode == HttpStatusCode.OK)
            _reader = new StreamReader(await _resp.Content.ReadAsStreamAsync());
        return _resp.StatusCode;
    }

    public string? ContentType => _resp?.Content.Headers.ContentType?.MediaType;

    /// The next full SSE frame (id/event/data), skipping comment heartbeats.
    /// Null when the stream ends.
    public async Task<(string? Id, string? Event, string? Data)?> ReadFrameAsync(TimeSpan timeout)
    {
        string? id = null, evt = null, data = null;
        var sawField = false;
        while (true)
        {
            string? line;
            try { line = await _reader!.ReadLineAsync().WaitAsync(timeout); }
            catch (IOException) { return null; }        // stream torn down mid-read
            catch (ObjectDisposedException) { return null; }
            if (line is null) return null;              // EOF: stream ended
            if (line.StartsWith(':')) continue;         // comment (heartbeat)
            if (line.Length == 0)
            {
                if (sawField) return (id, evt, data);   // frame boundary
                continue;
            }
            sawField = true;
            if (line.StartsWith("id: ")) id = line[4..];
            else if (line.StartsWith("event: ")) evt = line[7..];
            else if (line.StartsWith("data: ")) data = line[6..];
            else if (line.StartsWith("retry: ")) sawField = false;   // the hello hint, not a frame
        }
    }

    public async ValueTask DisposeAsync()
    {
        _reader?.Dispose();
        _resp?.Dispose();
        _http.Dispose();
        await Task.CompletedTask;
    }
}

// GET /api/v1/events over real HTTP: bearer-gated, byte-offset ids on the wire,
// Last-Event-ID resume, live tail semantics, and stream teardown.
public class ApiSseHttpTests
{
    private static SseOptions FastSse(TempTrail trail, int heartbeatMs = 60_000) =>
        new(trail.Path, Poll: TimeSpan.FromMilliseconds(30), Heartbeat: TimeSpan.FromMilliseconds(heartbeatMs));

    [Fact]
    public async Task Events_StreamAppendedLines_WithByteOffsetIds()
    {
        using var trail = new TempTrail();
        using var api = ApiHost.Start(FreeTcpPort(), sse: FastSse(trail));
        await using var client = new SseClient();

        Assert.Equal(HttpStatusCode.OK, await client.OpenAsync(api.Port, api.Token));
        Assert.Equal("text/event-stream", client.ContentType);

        trail.Append("alpha", "bé🪝ta");   // second line multi-byte: ids must be bytes
        var f1 = await client.ReadFrameAsync(TimeSpan.FromSeconds(10));
        var f2 = await client.ReadFrameAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("alpha", f1!.Value.Data);
        var off1 = Encoding.UTF8.GetByteCount("alpha") + 1;
        Assert.Equal(off1.ToString(), f1.Value.Id);
        Assert.Equal("bé🪝ta", f2!.Value.Data);
        Assert.Equal((off1 + Encoding.UTF8.GetByteCount("bé🪝ta") + 1).ToString(), f2.Value.Id);
    }

    [Fact]
    public async Task Events_LastEventId_ResumesExactly_NoDupNoLoss()
    {
        using var trail = new TempTrail();
        trail.Append("a", "b", "c");
        using var api = ApiHost.Start(FreeTcpPort(), sse: FastSse(trail));

        // First connection replays from 0 and remembers b's id.
        string? bId;
        await using (var first = new SseClient())
        {
            await first.OpenAsync(api.Port, api.Token, lastEventId: 0);
            Assert.Equal("a", (await first.ReadFrameAsync(TimeSpan.FromSeconds(10)))!.Value.Data);
            bId = (await first.ReadFrameAsync(TimeSpan.FromSeconds(10)))!.Value.Id;
        }

        // Reconnect exactly past b: c arrives first (no a/b dup, no c loss),
        // then the live tail continues.
        await using var second = new SseClient();
        await second.OpenAsync(api.Port, api.Token, lastEventId: long.Parse(bId!));
        Assert.Equal("c", (await second.ReadFrameAsync(TimeSpan.FromSeconds(10)))!.Value.Data);
        trail.Append("d");
        Assert.Equal("d", (await second.ReadFrameAsync(TimeSpan.FromSeconds(10)))!.Value.Data);
    }

    [Fact]
    public async Task Events_WithoutLastEventId_StartAtTheLiveEnd()
    {
        using var trail = new TempTrail();
        trail.Append("history-1", "history-2");
        using var api = ApiHost.Start(FreeTcpPort(), sse: FastSse(trail));
        await using var client = new SseClient();

        await client.OpenAsync(api.Port, api.Token);   // no resume id: "from now"
        trail.Append("live");
        var frame = await client.ReadFrameAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("live", frame!.Value.Data);       // history never replayed
    }

    [Fact]
    public async Task AnOversizedLine_SurfacesAsAGapFrame_OverHttp()
    {
        // The cursor's skip becomes the subscriber's honest gap, end to end.
        using var trail = new TempTrail();
        using var api = ApiHost.Start(FreeTcpPort(), sse: FastSse(trail));
        await using var client = new SseClient();
        await client.OpenAsync(api.Port, api.Token);

        trail.Append("small", new string('x', 200 * 1024), "tail");   // > the 128KiB window
        Assert.Equal("small", (await client.ReadFrameAsync(TimeSpan.FromSeconds(10)))!.Value.Data);
        var gap = await client.ReadFrameAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("gap", gap!.Value.Event);
        Assert.Equal("""{"dropped":1}""", gap.Value.Data);
        Assert.Equal("tail", (await client.ReadFrameAsync(TimeSpan.FromSeconds(10)))!.Value.Data);
    }

    [Fact]
    public async Task Truncation_SurfacesAsAResetFrame_ThenAFreshIdSpace()
    {
        using var trail = new TempTrail();
        trail.Append("old");
        using var api = ApiHost.Start(FreeTcpPort(), sse: FastSse(trail));
        await using var client = new SseClient();
        await client.OpenAsync(api.Port, api.Token, lastEventId: 0);
        Assert.Equal("old", (await client.ReadFrameAsync(TimeSpan.FromSeconds(10)))!.Value.Data);

        File.WriteAllText(trail.Path, "x\n");   // rotation
        var reset = await client.ReadFrameAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("reset", reset!.Value.Event);
        Assert.Equal("0", reset.Value.Id);      // re-anchors Last-Event-ID to the new space

        var fresh = await client.ReadFrameAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("x", fresh!.Value.Data);
        Assert.Equal("2", fresh.Value.Id);      // "x\n" = 2 bytes: the id space restarted
    }

    [Fact]
    public async Task TwoSubscribers_AtDifferentPositions_EachGetTheirOwnStream()
    {
        using var trail = new TempTrail();
        trail.Append("h1", "h2");
        using var api = ApiHost.Start(FreeTcpPort(), sse: FastSse(trail));

        await using var replayer = new SseClient();   // full history
        await using var live = new SseClient();       // from now
        await replayer.OpenAsync(api.Port, api.Token, lastEventId: 0);
        await live.OpenAsync(api.Port, api.Token);

        Assert.Equal("h1", (await replayer.ReadFrameAsync(TimeSpan.FromSeconds(10)))!.Value.Data);
        Assert.Equal("h2", (await replayer.ReadFrameAsync(TimeSpan.FromSeconds(10)))!.Value.Data);

        trail.Append("now");
        // Both cursors are independent: the replayer continues, the live one
        // starts exactly here — same content, same ids, no crosstalk.
        var r = await replayer.ReadFrameAsync(TimeSpan.FromSeconds(10));
        var l = await live.ReadFrameAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("now", r!.Value.Data);
        Assert.Equal("now", l!.Value.Data);
        Assert.Equal(r.Value.Id, l.Value.Id);
    }

    [Fact]
    public async Task Events_404_WhenNoTrailIsConfigured()
    {
        using var api = ApiHost.Start(FreeTcpPort());   // no SseOptions
        var (status, _) = await ApiGetAsync(api.Port, api.Token, "/api/v1/events");
        Assert.Equal(HttpStatusCode.NotFound, status);
    }

    [Fact]
    public async Task Events_InheritTheAuthGate()
    {
        using var trail = new TempTrail();
        using var api = ApiHost.Start(FreeTcpPort(), sse: FastSse(trail));
        using var http = new HttpClient();
        var resp = await http.GetAsync($"http://127.0.0.1:{api.Port}/api/v1/events");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);   // no token, no stream
    }

    [Fact]
    public async Task AnOpenStream_DoesNotBlockOtherRequests()
    {
        // The Phase-1 accept-loop rule, finally externally pinned: a LONG-LIVED
        // response is held open while other requests answer. If the accept loop
        // awaited handlers, this would deadlock the API.
        using var trail = new TempTrail();
        using var api = ApiHost.Start(FreeTcpPort(), sse: FastSse(trail));
        await using var stream = new SseClient();
        Assert.Equal(HttpStatusCode.OK, await stream.OpenAsync(api.Port, api.Token));
        await PollUntilAsync(() => Task.FromResult(api.OpenStreams == 1),
            TimeSpan.FromSeconds(5), "stream registered");

        for (var i = 0; i < 3; i++)
        {
            var (status, _) = await ApiGetAsync(api.Port, api.Token, $"/api/v1/probe{i}");
            Assert.Equal(HttpStatusCode.NotFound, status);   // served WHILE the stream is open
        }
    }

    [Fact]
    public async Task Stop_TerminatesOpenStreams_AndZeroesTheCounter()
    {
        using var trail = new TempTrail();
        var api = ApiHost.Start(FreeTcpPort(), sse: FastSse(trail));
        await using var client = new SseClient();
        await client.OpenAsync(api.Port, api.Token);
        await PollUntilAsync(() => Task.FromResult(api.OpenStreams == 1),
            TimeSpan.FromSeconds(5), "stream open");

        api.Stop();   // drain start: subscribers must not pin a draining daemon

        Assert.Null(await client.ReadFrameAsync(TimeSpan.FromSeconds(10)));   // stream ended
        await PollUntilAsync(() => Task.FromResult(api.OpenStreams == 0),
            TimeSpan.FromSeconds(5), "stream counter released");
        api.Dispose();
    }

    [Fact]
    public async Task ClientDisconnect_ReleasesTheStream_ViaTheHeartbeatProbe()
    {
        // A vanished client on a QUIET trail is only discovered when a write
        // fails — the heartbeat is that probe. Tight heartbeat, drop the client,
        // expect the counter released well before any trail traffic.
        using var trail = new TempTrail();
        using var api = ApiHost.Start(FreeTcpPort(), sse: FastSse(trail, heartbeatMs: 80));
        var client = new SseClient();
        await client.OpenAsync(api.Port, api.Token);
        await PollUntilAsync(() => Task.FromResult(api.OpenStreams == 1),
            TimeSpan.FromSeconds(5), "stream open");

        await client.DisposeAsync();   // the tab closed
        await PollUntilAsync(() => Task.FromResult(api.OpenStreams == 0),
            TimeSpan.FromSeconds(10), "dead client detected and released");
    }
}
