using System.Text.Json;
using CaptainHook.Mail;

namespace CaptainHook.Tests;

/// ADR-0018 d4 (roadmap item 23, slice `plan-unicast`, and the 2026-08-17
/// amendment to d3 that followed it) — the slice where a `role@instance`
/// envelope stops being carried and starts being DELIVERED.
///
/// Slices 1–4 built an address grammar, a TTL refusal, a named cursor key and a
/// provenance field; none of them routed anything, so unicast mail parsed,
/// landed on the chain, and reached nobody. The whole of this slice is one
/// predicate — a mailbox reads its role's broadcast, plus its own unicast — and
/// the reason it gets its own test file is that every way it can go wrong is
/// SILENT. Mail delivered to the wrong mailbox is not an exception, a nonzero
/// exit, or a trail line; it is a digest that reads slightly wrong to a human
/// who has no idea what they were supposed to see.
///
/// Three claims, and the tests are grouped along them:
///
///   * THE KEY IS THE ADDRESS. `Pending` resolves the mailbox as
///     `role@(--as ?? session)`, and that one string is both where the cursor
///     lives and what the mailbox answers to. A window's default mailbox is
///     therefore reachable at `role@<its session id>` — ephemeral, but real. (As
///     first landed, an unnamed reader was keyed by its session and NOT
///     reachable there; the amendment removed that asymmetry, and the tests in
///     the "the window's own address" block are the ones that flipped.)
///   * THE SECOND COPY. `MailCursors` filters by recipient in two places, and
///     only one of them is on the happy path. `Pending`'s scan decides what may
///     be delivered; `LoadOrAnchor`'s held-entry check decides whether a cursor
///     still describes its own mail. A reader that HOLDS a unicast is the state
///     where a disagreement shows, and it shows as a re-anchor loop, not as a
///     missing feature.
///   * THE LEDGER. A delivery into a named mailbox says which mailbox, in the
///     spelling `mail.cursorAdvance` already uses.
public class MailUnicastRoutingTests
{
    private static readonly MailAddress Unnamed = new("main", null);
    private static readonly MailAddress Named = new("main", "laptop-a");

    private static void Send(MailStoreTempDir tmp, MailEnvelope e) =>
        Assert.IsType<MailAppend.Appended>(tmp.Store().Append(e));

    /// A unicast envelope carries no TTL at all (d5), so the fixture's default
    /// has to be spelled away — `MailStore.Append` re-parses what it writes and
    /// would refuse the line.
    private static MailEnvelope Unicast(string id, string to) =>
        MailFixtures.Envelope(id: id, to: to, ttl: null);

    private static string[] Ids(MailPendingView v) => [.. v.Pending.Select(p => p.Envelope.Id)];

    // ---- the predicate, alone ----------------------------------------------

    /// An address with NO instance reads its role's broadcast and nothing else.
    /// After the amendment that is only ever the sessionless reader — every
    /// window's mailbox has an instance, its session id — so this table is the
    /// pure predicate on a bare role, and nothing more.
    [Theory]
    [InlineData("main", true)]
    [InlineData("main@laptop-a", false)]
    [InlineData("main@s-1", false)]
    [InlineData("reviewer", false)]
    [InlineData("reviewer@main", false)]
    [InlineData("mainx", false)]
    [InlineData("main@", false)]
    [InlineData("@main", false)]
    public void ABareRoleAddress_ReadsItsBroadcastAndNothingElse(string to, bool accepted) =>
        Assert.Equal(accepted, Unnamed.Accepts(to));

    /// A named reader is still a holder of its role — naming a mailbox
    /// subscribes it to a second address, it does not unsubscribe it from the
    /// first. `main@laptop-b` is a sibling's mail and `laptop-a` alone is not an
    /// address at all.
    [Theory]
    [InlineData("main", true)]
    [InlineData("main@laptop-a", true)]
    [InlineData("main@laptop-b", false)]
    [InlineData("main@laptop-a2", false)]
    [InlineData("mai@laptop-a", false)]
    [InlineData("laptop-a", false)]
    [InlineData("main@laptop-a@x", false)]
    [InlineData("reviewer@laptop-a", false)]
    public void NamedMailbox_ReadsItsBroadcastAndItsOwnUnicast(string to, bool accepted) =>
        Assert.Equal(accepted, Named.Accepts(to));

    /// `--as s-1` from window s-1 and no `--as` from window s-1 are the SAME
    /// mailbox — one key, one address, one cursor file. Under the pre-amendment
    /// rule they differed in reachability alone, which is the kind of
    /// distinction nothing on disk can record and every reader has to guess.
    [Fact]
    public void ANameThatEqualsTheSessionId_IsTheWindowsOwnMailbox()
    {
        using var tmp = new MailStoreTempDir();
        Send(tmp, Unicast("u-1", "main@s-1"));

        var cursors = new MailCursors(tmp.Store());
        var viaName = cursors.Pending(new MailAddress("main", "s-1"), "s-1");
        Assert.Equal(["u-1"], Ids(viaName));
        Assert.IsType<MailCursorWrite.Written>(cursors.Advance(viaName, [viaName.Pending[0].Offset]));

        // Read back through the OTHER spelling: same cursor, already consumed.
        Assert.Empty(cursors.Pending("main", "s-1").Pending);
        Assert.Single(Directory.GetFiles(tmp.Dir, "cursor.*.json"));
    }

    // ---- the window's own address --------------------------------------------

    /// THE AMENDMENT'S PIN. A window is reachable at `role@<its session id>` —
    /// its default mailbox, ephemeral but real. As first landed this was
    /// REFUSED, on ADR-0016 d6's ground that a session-keyed mailbox dies with
    /// the window and strands mail; but the reaper (d6 of ADR-0018) exists to
    /// handle stranded mail, and once it did the asymmetry — a mailbox keyed by
    /// a name it could not be reached at — bought nothing and cost every reader
    /// a "which kind of mailbox is this?" it could not answer from disk. Now the
    /// key is the address, full stop.
    [Fact]
    public void AWindow_ReceivesUnicastAddressedToItsOwnSessionId()
    {
        using var tmp = new MailStoreTempDir();
        Send(tmp, MailFixtures.Envelope(id: "b-1", to: "main"));
        Send(tmp, Unicast("u-1", "main@s-1"));
        Send(tmp, Unicast("u-2", "main@s-2"));       // another window's

        var cursors = new MailCursors(tmp.Store());
        var view = cursors.Pending("main", "s-1");

        Assert.Equal(["b-1", "u-1"], Ids(view));
        Assert.Equal(["b-1", "u-2"], Ids(cursors.Pending("main", "s-2")));
    }

    /// The sessionless reader has no instance and therefore no unicast address:
    /// `role@` is not a spelling anyone can write. It reads the broadcast alone,
    /// and that is not a special case — it is what "no instance" means.
    [Fact]
    public void TheSessionlessReader_HasNoUnicastAddress()
    {
        using var tmp = new MailStoreTempDir();
        Send(tmp, MailFixtures.Envelope(id: "b-1", to: "main"));
        Send(tmp, Unicast("u-1", "main@laptop-a"));

        Assert.Equal(["b-1"], Ids(new MailCursors(tmp.Store()).Pending("main", null)));
    }

    // ---- routing -----------------------------------------------------------

    [Fact]
    public void NamedMailbox_ReceivesBothItsBroadcastAndItsUnicast()
    {
        using var tmp = new MailStoreTempDir();
        Send(tmp, MailFixtures.Envelope(id: "b-1", to: "main"));
        Send(tmp, Unicast("u-1", "main@laptop-a"));
        Send(tmp, Unicast("u-2", "main@laptop-b"));
        Send(tmp, MailFixtures.Envelope(id: "x-1", to: "reviewer"));

        Assert.Equal(["b-1", "u-1"], Ids(new MailCursors(tmp.Store()).Pending(Named, "s-1")));
    }

    /// Unicast means ONE mailbox: a sibling holding the same role sees the
    /// broadcast and never the other's mail. This is the failure the address
    /// kind exists to fix — the reviewer's answer that went to every maintainer
    /// window at once.
    [Fact]
    public void UnicastToOneNamedMailbox_IsInvisibleToItsSibling()
    {
        using var tmp = new MailStoreTempDir();
        Send(tmp, MailFixtures.Envelope(id: "b-1", to: "main"));
        Send(tmp, Unicast("u-1", "main@laptop-a"));

        var cursors = new MailCursors(tmp.Store());
        Assert.Equal(["b-1", "u-1"], Ids(cursors.Pending(Named, "s-1")));
        Assert.Equal(["b-1"], Ids(cursors.Pending(new MailAddress("main", "laptop-b"), "s-2")));
        Assert.Equal(["b-1"], Ids(cursors.Pending("main", "s-3")));   // and to a window's own mailbox
    }

    /// The cursor key is the INSTANCE, so a named mailbox's unicast mail is
    /// consumed once and stays consumed — however many windows serve the name.
    [Fact]
    public void ADeliveredUnicast_IsBehindTheFrontierForEveryWindowOfThatMailbox()
    {
        using var tmp = new MailStoreTempDir();
        Send(tmp, Unicast("u-1", "main@laptop-a"));

        var cursors = new MailCursors(tmp.Store());
        var view = cursors.Pending(Named, "s-1");
        Assert.IsType<MailCursorWrite.Written>(cursors.Advance(view, [view.Pending[0].Offset]));

        Assert.Empty(cursors.Pending(Named, "s-2").Pending);
    }

    // ---- the second predicate copy ------------------------------------------

    /// THE ONE A HAPPY-PATH PASS MISSES. `LoadOrAnchor` verifies every held
    /// entry against the file, and its recipient check is a second site the
    /// first drive never reaches: to reach it, a named reader has to HOLD a
    /// unicast envelope and then read again.
    ///
    /// With the two sites disagreeing, this is not a feature that half-works.
    /// The scan accepts the envelope, the digest holds it, and the very next
    /// read declares the held entry addressed to someone else — a `Store`-cause
    /// re-anchor that resets the cursor to 0, drops every held entry with it,
    /// and redelivers everything the mailbox already read, loudly blaming the
    /// store for a disagreement between two lines of our own code. It would
    /// then do it again on every subsequent read.
    [Fact]
    public void AHeldUnicast_SurvivesTheNextRead_WithoutReanchoring()
    {
        using var tmp = new MailStoreTempDir();
        Send(tmp, MailFixtures.Envelope(id: "b-1", to: "main"));
        Send(tmp, Unicast("u-1", "main@laptop-a"));

        var cursors = new MailCursors(tmp.Store());
        var first = cursors.Pending(Named, "s-1");
        Assert.Equal(["b-1", "u-1"], Ids(first));

        // Deliver the broadcast, HOLD the unicast — the state the second
        // predicate site is the only thing standing in front of.
        var unicastOffset = first.Pending[1].Offset;
        Assert.IsType<MailCursorWrite.Written>(
            cursors.Advance(first, [first.Pending[0].Offset]));

        var second = cursors.Pending(Named, "s-1");
        Assert.False(second.Reanchored);
        Assert.Null(second.ReanchorReason);
        var held = Assert.Single(second.Pending);
        Assert.Equal("u-1", held.Envelope.Id);
        Assert.Equal(unicastOffset, held.Offset);
        Assert.Equal(1, held.SeenAt);                     // stamped at the opportunity that passed it over
        Assert.Empty(second.Expired);
    }

    /// A held unicast is never SPENT (d5): with one addressee, "delivered" is a
    /// fact rather than a matter of opportunities, so the arithmetic that ages
    /// broadcast mail has nothing to say about it. The bound is the reaper's
    /// judgement (d6), not a countdown that quietly drops unread mail.
    ///
    /// Reachable for the first time in this slice: before it, a unicast
    /// envelope could never become held, because it was never pending.
    [Fact]
    public void AHeldUnicast_NeverExpires_HoweverManyOpportunitiesPass()
    {
        using var tmp = new MailStoreTempDir();
        Send(tmp, Unicast("u-1", "main@laptop-a"));

        var cursors = new MailCursors(tmp.Store());
        for (var i = 0; i < 12; i++)   // four times the ttl any broadcast mail gets
        {
            var view = cursors.Pending(Named, "s-1");
            Assert.Empty(view.Expired);
            Assert.Equal(["u-1"], Ids(view));
            Assert.False(view.Reanchored);
            Assert.IsType<MailCursorWrite.Written>(cursors.Advance(view, []));
        }
    }

    /// The held check still REFUSES what it should. A cursor hand-edited to
    /// hold a sibling's unicast is not this mailbox's mail, and the re-anchor
    /// names the mailbox rather than the bare role — the address is the thing
    /// that decides, so it is the thing the message has to say.
    [Fact]
    public void AHeldEntryNamingAnotherMailboxsUnicast_StillReanchors()
    {
        using var tmp = new MailStoreTempDir();
        Send(tmp, Unicast("u-1", "main@laptop-b"));
        Send(tmp, MailFixtures.Envelope(id: "b-1", to: "main"));

        var cursors = new MailCursors(tmp.Store());
        var lines = tmp.Store().Read();
        var path = cursors.CursorPath("main", "laptop-a");
        File.WriteAllText(path, new MailCursor(
            MailCursors.CurrentGen, lines[0].Hash, Offset: lines[1].Offset,
            LastDeliveredId: null, Deliveries: 1,
            [new MailHeld(lines[0].Offset, "u-1", SeenAt: 1)]).Render());

        var view = cursors.Pending(Named, "s-1");
        Assert.True(view.Reanchored);
        Assert.Contains("main@laptop-a", view.ReanchorReason);
    }

    // ---- the verb, and the ledger -------------------------------------------

    private static (int Exit, string Out, string Err) Digest(string dir, string? session, string[] argv)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = MailDigest.Run(argv, new StringReader(DigestFixtures.Request(sessionId: session)),
            stdout, stderr, mailDir: dir, harnessDir: TestUtil.NoHarnessDir());
        return (exit, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void NamedDigest_DeliversUnicastMail_AndTheDeliveryNamesTheMailbox()
    {
        using var log = new CapturedLog();
        using var tmp = new MailStoreTempDir();
        Send(tmp, Unicast("u-1", "main@laptop-a"));

        var run = Digest(tmp.Dir, "s-77", ["--role", "main", "--as", "laptop-a"]);

        Assert.Equal(0, run.Exit);
        Assert.Contains("u-1", run.Out);

        // WHICH MAILBOX took it. For a unicast envelope this is the entire
        // fact — `role` names a lane a dozen mailboxes may hang off, and
        // `sessionId` names a window that will be gone tomorrow.
        var deliver = Assert.Single(log.Events.Where(e => e.Evt == "mail.deliver"));
        var json = JsonDocument.Parse(deliver.ToJson()).RootElement;
        Assert.Equal("s-77", json.GetProperty("sessionId").GetString());
        Assert.Equal("main", json.GetProperty("data").GetProperty("role").GetString());
        Assert.Equal("laptop-a", json.GetProperty("data").GetProperty("instance").GetString());
    }

    /// Byte-identity for every reader that predates ADR-0018: the column is
    /// written only when it says something `sessionId` does not.
    [Fact]
    public void UnnamedDigest_HasNoInstanceColumnOnItsDeliveryLine()
    {
        using var log = new CapturedLog();
        using var tmp = new MailStoreTempDir();
        Send(tmp, MailFixtures.Envelope(id: "b-1", to: "main"));

        Digest(tmp.Dir, "s-77", ["--role", "main"]);

        var deliver = Assert.Single(log.Events.Where(e => e.Evt == "mail.deliver"));
        var data = JsonDocument.Parse(deliver.ToJson()).RootElement.GetProperty("data");
        Assert.False(data.TryGetProperty("instance", out _));
    }

    /// End to end through the real verb: a window with no `--as` is handed the
    /// unicast addressed to `role@<its session>` and not a sibling's — the
    /// default mailbox is a mailbox.
    [Fact]
    public void AWindowsDigest_DeliversItsOwnUnicast_AndNotASiblings()
    {
        using var log = new CapturedLog();
        using var tmp = new MailStoreTempDir();
        Send(tmp, Unicast("u-1", "main@s-1"));
        Send(tmp, Unicast("u-2", "main@laptop-a"));

        var run = Digest(tmp.Dir, "s-1", ["--role", "main"]);

        Assert.Equal(0, run.Exit);
        Assert.Contains("u-1", run.Out);
        Assert.DoesNotContain("u-2", run.Out);
        // And the trail keeps its pre-ADR-0018 shape for this reader: mailbox
        // and window are one name, so there is nothing extra to say.
        var deliver = Assert.Single(log.Events, e => e.Evt == "mail.deliver");
        Assert.False(JsonDocument.Parse(deliver.ToJson()).RootElement
            .GetProperty("data").TryGetProperty("instance", out _));
    }

    // ---- answer-by-address (ADR-0018 d4, phase 4) --------------------------

    /// The return address is rendered in the digest HEAD, verbatim and
    /// unclamped, because the reader that has to answer is very often a model
    /// and the address it should write into `to` has to be where its eye
    /// lands, spelled exactly. (Unclamped is safe: the grammar bounds an
    /// address at `MailAddress.MaxChars`, so the head stays bounded without a
    /// clamp that would produce a return address nobody can reach.)
    [Fact]
    public void Render_ShowsTheReturnAddress_InTheHead()
    {
        var pending = new[]
        {
            DigestFixtures.Pending(0, DigestFixtures.Env("q-1", kind: MailKind.Request,
                replyTo: "reviewer@laptop-a", body: "which branch?")),
            DigestFixtures.Pending(200, DigestFixtures.Env("s-1")),
        };
        var plan = MailDigest.Plan(pending, MailSeam.Ambient, ["inject"]);
        var render = MailDigest.Render(DigestFixtures.View(pending), plan, maxChars: 4096);

        var lines = render.Text.Split('\n');
        var q = Assert.Single(lines, l => l.Contains("id q-1"));
        Assert.EndsWith("· reply to reviewer@laptop-a", q);
        var st = Assert.Single(lines, l => l.Contains("id s-1"));
        Assert.DoesNotContain("reply to", st);
    }

    /// The plan's "unit test catches misaddressing", end to end through the
    /// real store and cursors: a request carries the asker's address, the
    /// answer goes `to` exactly that address, and it lands in ONE mailbox — the
    /// asker's — not in any sibling holding the asker's role. This is the
    /// exchange the whole ADR was written for (the reviewer's answer that
    /// reached every maintainer window), now addressed rather than preferred.
    [Fact]
    public void AnAnswerAddressedToTheRequestsReplyTo_ReachesTheAskerAlone()
    {
        using var tmp = new MailStoreTempDir();
        var cursors = new MailCursors(tmp.Store());

        // The asker is a maintainer window; its request names its own address.
        var asker = new MailAddress("maintainer", "s-ask");
        Send(tmp, MailFixtures.Envelope(id: "q-1", to: "reviewer", replyTo: asker.ToString())
            with { Kind = MailKind.Request });

        // The reviewer reads the request and can see where to answer.
        var review = cursors.Pending("reviewer", "s-rev");
        var request = Assert.Single(review.Pending).Envelope;
        Assert.Equal("maintainer@s-ask", request.ReplyTo);

        // It answers `to` that address — a unicast, so no ttl (d5).
        Send(tmp, MailFixtures.Envelope(id: "a-1", to: request.ReplyTo!, inReplyTo: "q-1", ttl: null)
            with { Kind = MailKind.Answer });

        // The asker's own mailbox has it; two sibling maintainer windows and a
        // durable maintainer instance do not.
        Assert.Equal(["a-1"], Ids(cursors.Pending("maintainer", "s-ask")));
        Assert.Empty(cursors.Pending("maintainer", "s-other").Pending);
        Assert.Empty(cursors.Pending("maintainer", "s-third").Pending);
        Assert.Empty(cursors.Pending(new MailAddress("maintainer", "laptop-a"), "s-x").Pending);
    }

    // ---- the observation surface -------------------------------------------

    /// The read-only snapshot reads every cursor file as the mailbox its name
    /// spells — which, since the key IS the address, is exactly right for a
    /// durable `--as` mailbox and for a window's ephemeral one alike. Before
    /// the amendment this surface had to UNDER-CLAIM (it could not tell the two
    /// kinds apart and only one was reachable); now there is nothing to tell
    /// apart for routing purposes, and unicast mail shows as pending for the
    /// mailbox it was sent to.
    [Fact]
    public void TheReadOnlySnapshot_ShowsUnicastMailPendingForItsMailbox()
    {
        using var tmp = new MailStoreTempDir();
        Send(tmp, MailFixtures.Envelope(id: "b-1", to: "main"));
        Send(tmp, Unicast("u-1", "main@laptop-a"));
        Send(tmp, Unicast("u-2", "main@s-9"));

        var cursors = new MailCursors(tmp.Store());
        var view = cursors.Pending(Named, "s-1");
        Assert.IsType<MailCursorWrite.Written>(cursors.Advance(view, [view.Pending[0].Offset]));
        var win = cursors.Pending("main", "s-9");
        Assert.IsType<MailCursorWrite.Written>(cursors.Advance(win, []));

        var port = MailReadPort.Over(tmp.Dir);
        Assert.Equal([("main", "laptop-a"), ("main", "s-9")], port.Cursors());
        Assert.Equal(["u-1"], Ids(port.Pending("main", "laptop-a")));
        Assert.Equal(["b-1", "u-2"], Ids(port.Pending("main", "s-9")));
    }
}
