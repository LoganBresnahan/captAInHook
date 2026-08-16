using System.Net;
using System.Reflection;
using System.Text.Json;
using CaptainHook.Api;
using CaptainHook.Core;
using CaptainHook.Mail;
using static CaptainHook.Tests.TestUtil;

namespace CaptainHook.Tests;

/// ADR-0016 decision 14, slice `mail-read-endpoint` (roadmap item 21, phase 1)
/// — the bus becomes OBSERVABLE, and observation must not become delivery.
///
/// Two kinds of test live here, and the second kind is the point. The first
/// pins the snapshot's content: the chain's status, the ledger from `?since=`,
/// each cursor's pending view, and the inferred presence behind them, each
/// read from the same engine calls the digest reads so the picture cannot
/// describe a mailbox the delivery path does not have. The second pins the
/// ABSENCE of a write path three ways, per d14 — no append/advance handle in
/// the projection's declared graph, no `Api/` source naming the writable
/// types, and no non-GET method answering under /api/v1/mail.
public class MailApiTests
{
    private static readonly Effect Noop = new Effect.Noop();

    private static ApiReadModel Model(
        MailReadPort? mail, SessionPresence? presence = null, string? trailPath = null) =>
        new("testver", new ServeStats(),
            new Dispatcher(new Registry().On("UserPromptSubmit", TestHandler.Returning("greeter", Noop)),
                           TimeSpan.FromSeconds(2)),
            new ReloadingHarnessRegistry(NoHarnessDir()), new ReloadingPolicy(null), null,
            clock: () => 6000, startTick: 1000, handlersPath: null, mail: mail, presence: presence,
            trailPath: trailPath);

    private static async Task<JsonElement> MailJson(ApiHost api, string query = "")
    {
        var (status, body) = await ApiGetAsync(api.Port, api.Token, "/api/v1/mail" + query);
        Assert.Equal(HttpStatusCode.OK, status);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    // ---- the snapshot ------------------------------------------------------

    /// The whole shape in one drive: chain status, every line with its parsed
    /// envelope, and the cursor's pending view. The envelope fields are the
    /// canvas's provenance (d10) and the body is included — this endpoint IS
    /// the archival store's reader, for the operator's own authenticated GUI.
    [Fact]
    public async Task Mail_Snapshot_ReportsChainLinesAndCursors()
    {
        using var tmp = new MailStoreTempDir();
        var first = CursorFixtures.AppendTo(tmp, "m-01");
        CursorFixtures.AppendTo(tmp, "m-02", priority: MailPriority.Urgent);
        CursorFixtures.AppendTo(tmp, "m-03", to: "other");

        // A cursor exists only because something was DELIVERED to that role —
        // written here through the real writer, which the API cannot reach.
        var cursors = tmp.Cursors();
        CursorFixtures.AdvanceOk(cursors, cursors.Pending("main", "s-1"), first);

        using var api = ApiHost.Start(FreeTcpPort(), readModel: Model(MailReadPort.Over(tmp.Dir)));
        var m = await MailJson(api);

        Assert.Equal(tmp.Dir, m.GetProperty("dir").GetString());
        var chain = m.GetProperty("chain");
        Assert.True(chain.GetProperty("ok").GetBoolean());
        Assert.Equal(3, chain.GetProperty("lines").GetInt32());
        Assert.Equal(1, chain.GetProperty("gen").GetInt32());
        Assert.Empty(chain.GetProperty("faults").EnumerateArray());
        Assert.Equal(tmp.Store().HeadHash(), chain.GetProperty("head").GetString());
        // d13's owner-only discipline, SHOWN rather than asserted in prose.
        Assert.Equal("700", chain.GetProperty("dirMode").GetString());
        Assert.Equal("600", chain.GetProperty("fileMode").GetString());

        var lines = m.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal(3, lines.Count);
        Assert.Equal(0, lines[0].GetProperty("offset").GetInt64());
        Assert.True(lines[0].GetProperty("terminated").GetBoolean());
        Assert.Empty(lines[0].GetProperty("errors").EnumerateArray());

        var e = lines[1].GetProperty("envelope");
        Assert.Equal("m-02", e.GetProperty("id").GetString());
        Assert.Equal("main", e.GetProperty("to").GetString());
        Assert.Equal("urgent", e.GetProperty("priority").GetString());   // wire spelling, camelCased enum
        Assert.Equal("status", e.GetProperty("kind").GetString());
        Assert.Equal("build", e.GetProperty("topic").GetString());
        Assert.Equal(3, e.GetProperty("ttlDeliveries").GetInt32());
        Assert.Equal("opaque prose", e.GetProperty("body").GetString());
        Assert.Equal("intent-watcher", e.GetProperty("from").GetProperty("agent").GetString());
        Assert.Equal("s-77", e.GetProperty("from").GetProperty("session").GetString());
        Assert.NotNull(e.GetProperty("prev").GetString());               // the chain link the near tier draws

        var cursor = Assert.Single(m.GetProperty("cursors").EnumerateArray().ToList());
        Assert.Equal("main", cursor.GetProperty("role").GetString());
        Assert.Equal("s-1", cursor.GetProperty("session").GetString());
        Assert.Equal(1, cursor.GetProperty("deliveries").GetInt64());
        Assert.Equal("m-01", cursor.GetProperty("lastDeliveredId").GetString());
        Assert.False(cursor.GetProperty("reanchored").GetBoolean());
        // m-01 was delivered — behind the frontier, absent from `pending`,
        // which is what makes redelivery impossible rather than merely
        // avoided; m-03 belongs to another role and is invisible here; m-02
        // was passed over by that same opportunity, so it is HELD, stamped
        // with the delivery it was seen at.
        var pending = Assert.Single(cursor.GetProperty("pending").EnumerateArray().ToList());
        Assert.Equal("m-02", pending.GetProperty("id").GetString());
        Assert.Equal(1, pending.GetProperty("seenAt").GetInt64());
        Assert.Equal(1, pending.GetProperty("opportunities").GetInt64());   // deliveries − seenAt + 1
        Assert.Equal(3, pending.GetProperty("ttlDeliveries").GetInt32());
        Assert.Empty(cursor.GetProperty("expired").EnumerateArray());
    }

    /// A malformed line is REPORTED, not dropped: a viewer walking offsets must
    /// see the bytes it steps over (the store's own warn-and-skip, made
    /// visible), and the chain fault says the link could not be checked.
    [Fact]
    public async Task Mail_MalformedLine_IsReportedWithItsErrors()
    {
        using var tmp = new MailStoreTempDir();
        CursorFixtures.AppendTo(tmp, "m-01");
        File.AppendAllText(tmp.FilePath, "not json at all\n");

        using var api = ApiHost.Start(FreeTcpPort(), readModel: Model(MailReadPort.Over(tmp.Dir)));
        var m = await MailJson(api);

        var lines = m.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal(2, lines.Count);
        Assert.Equal(JsonValueKind.Null, lines[1].GetProperty("envelope").ValueKind);
        Assert.NotEmpty(lines[1].GetProperty("errors").EnumerateArray());
        var chain = m.GetProperty("chain");
        Assert.False(chain.GetProperty("ok").GetBoolean());
        var fault = Assert.Single(chain.GetProperty("faults").EnumerateArray().ToList());
        Assert.Equal("unreadable", fault.GetProperty("kind").GetString());
    }

    /// The torn tail: an append in flight is VISIBLE (a line with
    /// `terminated: false`) but the frontier stops before it, exactly as
    /// MailCursors.Pending sees it. A frontier inside those bytes would draw
    /// mail that does not exist yet.
    [Fact]
    public async Task Mail_TornTail_IsShownButNeverBehindTheFrontier()
    {
        using var tmp = new MailStoreTempDir();
        CursorFixtures.AppendTo(tmp, "m-01");
        var completeLength = new FileInfo(tmp.FilePath).Length;
        File.AppendAllText(tmp.FilePath, """{"v":1,"id":"m-02","ts":"2026""");   // no terminator

        using var api = ApiHost.Start(FreeTcpPort(), readModel: Model(MailReadPort.Over(tmp.Dir)));
        var m = await MailJson(api);

        var lines = m.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal(2, lines.Count);
        Assert.False(lines[1].GetProperty("terminated").GetBoolean());
        Assert.Equal(completeLength, m.GetProperty("frontier").GetInt64());
        Assert.Equal(completeLength, lines[1].GetProperty("offset").GetInt64());
        // The torn bytes are not a chain identity: head is still the first
        // COMPLETE line, and a cursor read of the same store agrees.
        Assert.Equal(tmp.Cursors().Pending("main", "s-1").Frontier, m.GetProperty("frontier").GetInt64());
    }

    // ---- ?since= -----------------------------------------------------------

    /// `since` is a byte offset into the same address space the cursor's
    /// frontier uses — the resumable half of the snapshot. Absent means 0
    /// (the whole retained store), which is what a fresh view asks for.
    [Fact]
    public async Task Mail_Since_ReturnsOnlyLinesAtOrAfterTheOffset()
    {
        using var tmp = new MailStoreTempDir();
        CursorFixtures.AppendTo(tmp, "m-01");
        var second = CursorFixtures.AppendTo(tmp, "m-02");
        CursorFixtures.AppendTo(tmp, "m-03");

        using var api = ApiHost.Start(FreeTcpPort(), readModel: Model(MailReadPort.Over(tmp.Dir)));

        var all = await MailJson(api);
        Assert.Equal(3, all.GetProperty("lines").GetArrayLength());
        Assert.Equal(0, all.GetProperty("since").GetInt64());
        Assert.True(all.GetProperty("sinceAligned").GetBoolean());

        var tail = await MailJson(api, $"?since={second}");
        Assert.Equal(new[] { "m-02", "m-03" },
            tail.GetProperty("lines").EnumerateArray()
                .Select(l => l.GetProperty("envelope").GetProperty("id").GetString()).ToArray());
        Assert.Equal(second, tail.GetProperty("since").GetInt64());
        Assert.True(tail.GetProperty("sinceAligned").GetBoolean());

        // Caught up: the frontier itself is a legal, aligned, empty answer —
        // a poller at the head asks this on every tick.
        var caughtUp = await MailJson(api, $"?since={all.GetProperty("frontier").GetInt64()}");
        Assert.Empty(caughtUp.GetProperty("lines").EnumerateArray());
        Assert.True(caughtUp.GetProperty("sinceAligned").GetBoolean());
    }

    /// An offset resting on no line boundary means the client's idea of where
    /// it had read is STALE (a truncation, a replaced chain). It is answered
    /// honestly — `sinceAligned: false` — rather than by splicing a fresh tail
    /// onto a prefix that no longer exists.
    [Fact]
    public async Task Mail_SinceOffBoundary_SaysSoRatherThanGuessing()
    {
        using var tmp = new MailStoreTempDir();
        CursorFixtures.AppendTo(tmp, "m-01");

        using var api = ApiHost.Start(FreeTcpPort(), readModel: Model(MailReadPort.Over(tmp.Dir)));

        var mid = await MailJson(api, "?since=7");
        Assert.False(mid.GetProperty("sinceAligned").GetBoolean());

        var past = await MailJson(api, "?since=999999");
        Assert.False(past.GetProperty("sinceAligned").GetBoolean());
        Assert.Empty(past.GetProperty("lines").EnumerateArray());
    }

    /// A bad `since` is a client bug and is REFUSED. Defaulting it silently
    /// would hand a reducer a full store that looks exactly like a legitimate
    /// resnapshot — the one failure a screenshot cannot catch.
    [Theory]
    [InlineData("?since=abc")]
    [InlineData("?since=-1")]
    [InlineData("?since=")]
    [InlineData("?since=1.5")]
    public async Task Mail_InvalidSince_Is400(string query)
    {
        using var tmp = new MailStoreTempDir();
        using var api = ApiHost.Start(FreeTcpPort(), readModel: Model(MailReadPort.Over(tmp.Dir)));

        var (status, body) = await ApiGetAsync(api.Port, api.Token, "/api/v1/mail" + query);
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("invalid_since", body);
    }

    [Fact]
    public void ParseSince_AbsentIsZero_UnknownParamsIgnored()
    {
        Assert.Equal(0, ApiHost.ParseSince(null));
        Assert.Equal(0, ApiHost.ParseSince(""));
        Assert.Equal(0, ApiHost.ParseSince("?other=3"));
        Assert.Equal(42, ApiHost.ParseSince("?since=42"));
        Assert.Equal(42, ApiHost.ParseSince("?other=3&since=42"));
        Assert.Null(ApiHost.ParseSince("?since=nope"));
    }

    // ---- presence, inferred ------------------------------------------------

    /// Presence is cursor files ∪ recently-dispatched sessions, and each half
    /// is honest about what it knows: a cursor-only session has no dispatch
    /// age (quiet, or the daemon restarted), a dispatch-only session holds no
    /// role yet (nothing has ever been delivered to it).
    [Fact]
    public async Task Mail_Presence_UnionsCursorFilesAndRecentDispatches()
    {
        using var tmp = new MailStoreTempDir();
        CursorFixtures.AppendTo(tmp, "m-01");
        CursorFixtures.AppendTo(tmp, "m-02", to: "reviewer");
        var cursors = tmp.Cursors();
        CursorFixtures.AdvanceOk(cursors, cursors.Pending("main", "s-cursor"));
        CursorFixtures.AdvanceOk(cursors, cursors.Pending("reviewer", "s-cursor"));

        var tick = 10_000L;
        var presence = new SessionPresence(() => tick);
        presence.Seen("s-dispatch");
        tick += 250;

        using var api = ApiHost.Start(FreeTcpPort(),
            readModel: Model(MailReadPort.Over(tmp.Dir), presence));
        var list = (await MailJson(api)).GetProperty("presence").EnumerateArray().ToList();

        Assert.Equal(2, list.Count);
        var dispatched = list.Single(p => p.GetProperty("session").GetString() == "s-dispatch");
        Assert.Equal(250, dispatched.GetProperty("lastDispatchAgeMs").GetInt64());
        Assert.Empty(dispatched.GetProperty("roles").EnumerateArray());

        var quiet = list.Single(p => p.GetProperty("session").GetString() == "s-cursor");
        Assert.Equal(JsonValueKind.Null, quiet.GetProperty("lastDispatchAgeMs").ValueKind);
        Assert.Equal(new[] { "main", "reviewer" },
            quiet.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).ToArray());
    }

    [Fact]
    public void SessionPresence_IsBounded_AndEvictsTheOldest()
    {
        var tick = 0L;
        var presence = new SessionPresence(() => tick, capacity: 3);
        foreach (var s in new[] { "a", "b", "c" }) { presence.Seen(s); tick += 10; }
        presence.Seen("a");                 // refresh: "b" is now the oldest
        tick += 10;
        presence.Seen("d");                 // over capacity: evict "b"

        Assert.Equal(new[] { "d", "a", "c" },
            presence.Recent().Select(x => x.Session).ToArray());   // freshest first
        presence.Seen(null);
        presence.Seen("");
        Assert.Equal(3, presence.Recent().Count);   // a nameless session has no presence
    }

    // ---- observation is not delivery: the three pins -----------------------

    /// PIN (ii), the route table: nothing under /api/v1/mail answers a
    /// non-GET, and a mutating attempt leaves the store and the cursors
    /// untouched. A "mark read" button would have to invent an endpoint.
    [Theory]
    [InlineData("PUT", "/api/v1/mail")]
    [InlineData("POST", "/api/v1/mail")]
    [InlineData("DELETE", "/api/v1/mail")]
    [InlineData("PATCH", "/api/v1/mail")]
    [InlineData("GET", "/api/v1/mail/advance")]
    [InlineData("POST", "/api/v1/mail/advance")]
    [InlineData("GET", "/api/v1/mail/main")]
    public async Task Mail_NonGetAndSubPaths_404_AndWriteNothing(string method, string path)
    {
        using var tmp = new MailStoreTempDir();
        CursorFixtures.AppendTo(tmp, "m-01");
        var before = tmp.Bytes();

        using var api = ApiHost.Start(FreeTcpPort(), readModel: Model(MailReadPort.Over(tmp.Dir)));

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var req = new HttpRequestMessage(new HttpMethod(method), $"http://127.0.0.1:{api.Port}{path}")
        {
            Content = new StringContent("{}"),
        };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", api.Token);
        var resp = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Equal(before, tmp.Bytes());
        Assert.Empty(Directory.GetFiles(tmp.Dir, "cursor.*"));   // no mailbox was created by asking
    }

    /// No bus wired (a pure-listener host, or a daemon with no mail dir
    /// resolvable) ⇒ the route 404s like every other capability-gated read.
    [Fact]
    public async Task Mail_WithoutAPort_404s()
    {
        using var api = ApiHost.Start(FreeTcpPort(), readModel: Model(mail: null));
        var (status, _) = await ApiGetAsync(api.Port, api.Token, "/api/v1/mail");
        Assert.Equal(HttpStatusCode.NotFound, status);
    }

    // ---- the snapshot's place in the stream --------------------------------

    /// The whole point of `trailEventId`, end to end: a client that takes this
    /// snapshot and subscribes at the id it carries sees every event after it
    /// and NOT ONE it already has. Zero loss and zero duplicate are asserted as
    /// the same fact — the frames that arrive are exactly the lines appended
    /// after the snapshot, in order — because either failure is invisible on
    /// screen. A lost `mail.cursorAdvance` leaves an envelope drawn pending
    /// forever; a duplicated one is only survivable because the reducer works
    /// to survive it, and the point of the stamp is that it does not have to.
    [Fact]
    public async Task Mail_TrailEventId_ResumesExactlyWhereTheSnapshotEnds()
    {
        using var tmp = new MailStoreTempDir();
        using var trail = new TempTrail();
        CursorFixtures.AppendTo(tmp, "m-01");

        // The world before the picture: events the snapshot already accounts
        // for, and which the client must therefore never be shown again.
        trail.Append("""{"ev":"mail.append","id":"m-01"}""", """{"ev":"exec.spawn"}""");

        using var api = ApiHost.Start(FreeTcpPort(),
            readModel: Model(MailReadPort.Over(tmp.Dir), trailPath: trail.Path),
            sse: new SseOptions(trail.Path, Poll: TimeSpan.FromMilliseconds(30),
                                Heartbeat: TimeSpan.FromMinutes(10)));

        var mail = await MailJson(api);
        var stamp = mail.GetProperty("trailEventId").GetInt64();
        Assert.Equal(new FileInfo(trail.Path).Length, stamp);

        // The choreography the picture must not miss, appended in the window
        // where a subscribe-after-snapshot client would have lost it.
        trail.Append("""{"ev":"mail.cursorAdvance","role":"main"}""",
                     """{"ev":"mail.deliver","id":"m-01"}""");

        await using var client = new SseClient();
        Assert.Equal(HttpStatusCode.OK, await client.OpenAsync(api.Port, api.Token, lastEventId: stamp));

        var first = await client.ReadFrameAsync(TimeSpan.FromSeconds(10));
        var second = await client.ReadFrameAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("""{"ev":"mail.cursorAdvance","role":"main"}""", first?.Data);
        Assert.Equal("""{"ev":"mail.deliver","id":"m-01"}""", second?.Data);

        // And nothing behind the stamp: the two pre-snapshot lines never come.
        trail.Append("""{"ev":"sentinel"}""");
        var third = await client.ReadFrameAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("""{"ev":"sentinel"}""", third?.Data);
    }

    /// The fallback contract, and the reason the field is NULLABLE rather than
    /// defaulted: a daemon serving no trail has no id space to align to, and
    /// the client must fall back to subscribe-then-snapshot and fold the
    /// overlap as replay. It must NOT read a missing stamp as 0 — in this id
    /// space 0 is the first byte, so that reading replays the entire trail as
    /// live. Absent means absent.
    [Fact]
    public async Task Mail_TrailEventId_IsNullWhenNoTrailIsServed()
    {
        using var tmp = new MailStoreTempDir();
        CursorFixtures.AppendTo(tmp, "m-01");
        using var api = ApiHost.Start(FreeTcpPort(), readModel: Model(MailReadPort.Over(tmp.Dir)));

        var mail = await MailJson(api);
        Assert.Equal(JsonValueKind.Null, mail.GetProperty("trailEventId").ValueKind);
    }

    /// A trail file that does not exist yet is "nothing yet", never an error —
    /// the same answer `TrailSubscription` gives a subscriber arriving before
    /// the first line is written. Here 0 is the honest stamp rather than a
    /// dangerous default: there are no bytes for it to replay.
    [Fact]
    public async Task Mail_TrailEventId_IsZeroWhenTheTrailDoesNotExistYet()
    {
        using var tmp = new MailStoreTempDir();
        using var trail = new TempTrail();          // path reserved, file never created
        CursorFixtures.AppendTo(tmp, "m-01");
        Assert.False(File.Exists(trail.Path));

        using var api = ApiHost.Start(FreeTcpPort(),
            readModel: Model(MailReadPort.Over(tmp.Dir), trailPath: trail.Path));

        var mail = await MailJson(api);
        Assert.Equal(0, mail.GetProperty("trailEventId").GetInt64());
    }

    /// The stamp is read BEFORE the store, and that order is the difference
    /// between a duplicate and a loss: an append landing between the two reads
    /// is either in the snapshot AND replayed (recoverable — the reducer drops
    /// it by sequence number) or in NEITHER (unrecoverable, and silent). The
    /// window is two in-process reads wide, so no drive can observe it
    /// deterministically without a real sleep; this is a SOURCE pin, on the
    /// precedent of the `Api/` naming pin above — reflection cannot see
    /// statement order any more than it can see a captured closure. It is
    /// deliberately weak on its own and strong about the one thing that
    /// matters: which line comes first.
    [Fact]
    public void Mail_TrailStamp_IsTakenBeforeTheStoreIsRead()
    {
        var src = File.ReadAllText(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "captainHook", "Api", "ApiReadModel.cs")));
        var body = src[src.IndexOf("public MailDto? Mail(long since)", StringComparison.Ordinal)..];

        var stamp = body.IndexOf("TrailLength(_trailPath)", StringComparison.Ordinal);
        var read = body.IndexOf("_mail.Read()", StringComparison.Ordinal);

        Assert.True(stamp >= 0, "the trail stamp moved or was renamed — this pin must move with it");
        Assert.True(read >= 0, "the store read moved or was renamed — this pin must move with it");
        Assert.True(stamp < read,
            "the trail stamp must precede the store read: taken after, an append in the window is in "
            + "neither the snapshot nor the stream, and no client can recover what it was never told");
    }

    /// PIN (i), the graph: no type the read model DECLARES — constructor
    /// parameters or fields, transitively through the engine's own types —
    /// offers Append or Advance. `MailReadPort` carries method-group delegates,
    /// so the writable objects survive only inside closures reflection cannot
    /// see; that is exactly why the source pin below exists too. Neither test
    /// alone is the claim.
    [Fact]
    public void ReadModel_DeclaresNoMailWriteHandle()
    {
        // The check would be vacuous if the writable types did not actually
        // declare these members — assert the detector detects.
        Assert.NotNull(typeof(MailStore).GetMethod("Append"));
        Assert.NotNull(typeof(MailCursors).GetMethod("Advance"));

        var engine = typeof(MailStore).Assembly;
        var seen = new HashSet<Type>();
        var queue = new Queue<Type>(
            typeof(ApiReadModel).GetConstructors().Single().GetParameters().Select(p => p.ParameterType));
        foreach (var f in typeof(ApiReadModel).GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
            queue.Enqueue(f.FieldType);

        var reached = new List<Type>();
        while (queue.Count > 0)
        {
            var t = queue.Dequeue();
            if (t.IsGenericType) foreach (var arg in t.GetGenericArguments()) queue.Enqueue(arg);
            if (t.Assembly != engine || !seen.Add(t)) continue;
            reached.Add(t);
            foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                queue.Enqueue(f.FieldType);
        }

        Assert.Contains(typeof(MailReadPort), reached);            // the walk really did reach the port
        Assert.DoesNotContain(typeof(MailStore), reached);
        Assert.DoesNotContain(typeof(MailCursors), reached);
        var offenders = reached
            .Where(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                                     | BindingFlags.Instance | BindingFlags.Static)
                         .Any(m => m.Name is "Append" or "Advance"))
            .Select(t => t.FullName)
            .ToList();
        Assert.Empty(offenders);
    }

    /// PIN (i), the source half: nothing under `Api/` NAMES the writable mail
    /// types or those verbs — not in a call, not in a using, not in a
    /// constant. Textual because the closure hides what reflection cannot
    /// prove; the two together are the guarantee. Comments are stripped first,
    /// because the ADR's own reasoning is quoted in them.
    [Fact]
    public void ApiSources_NeverNameTheWritableMailTypes()
    {
        var apiDir = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "captainHook", "Api"));
        var files = Directory.GetFiles(apiDir, "*.cs");
        Assert.NotEmpty(files);   // a moved directory must fail here, not pass silently

        foreach (var file in files)
        {
            var code = string.Join("\n", File.ReadAllLines(file)
                .Select(l => l.IndexOf("//", StringComparison.Ordinal) is var i && i >= 0 ? l[..i] : l));
            foreach (var forbidden in new[] { "MailStore", "MailCursors", ".Append(", ".Advance(" })
                Assert.False(code.Contains(forbidden, StringComparison.Ordinal),
                    $"{Path.GetFileName(file)} names '{forbidden}' — the API must not reach the bus's write half");
        }
    }

    // ---- cursor-file listing (the presence inference's on-disk half) -------

    [Fact]
    public void CursorList_ReportsRolesAndSessions_AndSkipsWhatWeDidNotWrite()
    {
        using var tmp = new MailStoreTempDir();
        CursorFixtures.AppendTo(tmp, "m-01", to: "main");
        var cursors = tmp.Cursors();
        CursorFixtures.AdvanceOk(cursors, cursors.Pending("main", "s-1"));
        CursorFixtures.AdvanceOk(cursors, cursors.Pending("main", null));      // the sessionless reader
        CursorFixtures.AdvanceOk(cursors, cursors.Pending("odd role/../x", "s-2"));

        // Neighbours that must NOT read as cursors: the advance lock files are
        // already there, plus two names no `Enc` could have produced — an
        // unescaped space and a third separator. (A WELL-FORMED name we did
        // not write is indistinguishable from one we did, and is listed: the
        // file IS a cursor by every rule the digest applies to it.)
        File.WriteAllText(Path.Combine(tmp.Dir, "cursor.hand made..json"), "{}");
        File.WriteAllText(Path.Combine(tmp.Dir, "cursor.one.two.three.json"), "{}");

        Assert.Equal(
            new[] { ("main", (string?)null), ("main", "s-1"), ("odd role/../x", "s-2") },
            MailCursors.List(tmp.Dir).ToArray());
    }

    [Fact]
    public void CursorFileName_RoundTripsThroughEncAndDec()
    {
        foreach (var (role, session) in new (string, string?)[]
                 { ("main", "s-1"), ("main", null), ("rôle ✓", "s/../x"), ("a.b", "%41") })
        {
            var name = Path.GetFileName(new MailCursors(new MailStore("/tmp/x")).CursorPath(role, session));
            Assert.Equal((role, session), MailCursors.TryParseCursorFileName(name));
        }

        // Not ours: a lock file, a temp file, a non-canonical encoding, a
        // truncated escape. Each is refused rather than guessed.
        Assert.Null(MailCursors.TryParseCursorFileName("cursor.main..json.lock"));
        Assert.Null(MailCursors.TryParseCursorFileName(".cursor.main..json.abc.tmp"));
        Assert.Null(MailCursors.TryParseCursorFileName("cursor.ma in..json"));
        Assert.Null(MailCursors.TryParseCursorFileName("cursor.%4.json"));
        Assert.Null(MailCursors.TryParseCursorFileName("cursor..s-1.json"));   // a role always has a name
        Assert.Null(MailCursors.Dec("%FF"));                                   // not UTF-8: refused
    }

    [Fact]
    public void MailReadPort_ExposesReadsOnly()
    {
        var members = typeof(MailReadPort)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Select(m => m.Name)
            .ToList();
        Assert.DoesNotContain("Append", members);
        Assert.DoesNotContain("Advance", members);
        Assert.Contains("Read", members);
        Assert.Contains("Pending", members);
        Assert.Contains("VerifyChain", members);
    }

    /// The DAEMON wiring, end to end through a real one: the mail dir seam
    /// reaches the read model, and the presence stamp fires where dispatch
    /// actually happens. A hook arrives over the UDS carrying a session id,
    /// and that session shows up on the bus snapshot as present — with no
    /// registry, no heartbeat, and no cursor of its own yet.
    [Fact]
    public async Task Mail_ThroughARealDaemon_SeesTheStore_AndTheDispatchedSession()
    {
        using var rt = new TempRuntimeDir();
        using var tmp = new MailStoreTempDir();
        CursorFixtures.AppendTo(tmp, "m-01");

        var apiPort = FreeTcpPort();
        using var stop = new CancellationTokenSource();
        var daemon = Task.Run(() => DaemonHost.RunAsync(
            rt.Paths, NoHarnessDir(), stop.Token, apiPort: apiPort, mailDir: tmp.Dir));
        try
        {
            await PollUntilAsync(async () =>
                await CaptainHook.Wire.ShimClient.TryForwardAsync(rt.Paths.SocketPath,
                    new CaptainHook.Wire.HookRequest("probe000", "session-start", "claude-code", "{}"u8.ToArray()))
                    is CaptainHook.Wire.ForwardOutcome.Answered,
                TimeSpan.FromSeconds(15), "daemon starts listening");

            Assert.IsType<CaptainHook.Wire.ForwardOutcome.Answered>(
                await CaptainHook.Wire.ShimClient.TryForwardAsync(rt.Paths.SocketPath,
                    new CaptainHook.Wire.HookRequest("live0001", "user-prompt-submit", "claude-code",
                        """{"session_id":"s-live"}"""u8.ToArray())));

            var token = ApiDiscovery.TryRead(rt.Paths.ApiJsonPath)!.Token;
            var (status, body) = await ApiGetAsync(apiPort, token, "/api/v1/mail");
            Assert.Equal(HttpStatusCode.OK, status);

            var m = JsonDocument.Parse(body).RootElement;
            Assert.Equal(tmp.Dir, m.GetProperty("dir").GetString());
            Assert.Single(m.GetProperty("lines").EnumerateArray());
            var live = Assert.Single(m.GetProperty("presence").EnumerateArray().ToList(),
                p => p.GetProperty("session").GetString() == "s-live");
            Assert.True(live.GetProperty("lastDispatchAgeMs").GetInt64() >= 0);
            Assert.Empty(live.GetProperty("roles").EnumerateArray());   // nothing delivered to it yet
        }
        finally
        {
            stop.Cancel();
            Assert.Equal(0, await daemon.WaitAsync(TimeSpan.FromSeconds(15)));
        }
    }

    /// An absent mail directory is not an error: the bus simply has nothing on
    /// it yet, and asking must not CREATE it (the endpoint is a read).
    [Fact]
    public async Task Mail_AbsentStore_ReadsEmpty_AndCreatesNothing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "captainhook-mail-" + Guid.NewGuid().ToString("N"));
        using var api = ApiHost.Start(FreeTcpPort(), readModel: Model(MailReadPort.Over(dir)));

        var m = await MailJson(api);
        Assert.Empty(m.GetProperty("lines").EnumerateArray());
        Assert.Empty(m.GetProperty("cursors").EnumerateArray());
        Assert.Equal(0, m.GetProperty("frontier").GetInt64());
        Assert.True(m.GetProperty("chain").GetProperty("ok").GetBoolean());
        Assert.Equal(JsonValueKind.Null, m.GetProperty("chain").GetProperty("head").ValueKind);
        Assert.Equal(JsonValueKind.Null, m.GetProperty("chain").GetProperty("dirMode").ValueKind);
        Assert.False(Directory.Exists(dir));
    }
}
