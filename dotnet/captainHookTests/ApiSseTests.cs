using System.Net;
using System.Net.Http.Headers;
using System.Text;
using CaptainHook.Api;
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

// GET /api/v1/events over real HTTP: bearer-gated, byte-offset ids on the wire,
// Last-Event-ID resume, live tail semantics, and stream teardown.
public class ApiSseHttpTests
{
    private static SseOptions FastSse(TempTrail trail, int heartbeatMs = 60_000) =>
        new(trail.Path, Poll: TimeSpan.FromMilliseconds(30), Heartbeat: TimeSpan.FromMilliseconds(heartbeatMs));

    /// A minimal SSE reader over HttpClient's streaming response.
    private sealed class SseClient : IAsyncDisposable
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
