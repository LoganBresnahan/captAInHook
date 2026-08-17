using System.Text.Json;
using CaptainHook.Mail;

namespace CaptainHook.Tests;

/// ADR-0018 d4 (roadmap item 23, slice `plan-unicast`) — the slice where a
/// `role@instance` envelope stops being carried and starts being DELIVERED.
///
/// Slices 1–4 built an address grammar, a TTL refusal, a named cursor key and a
/// provenance field; none of them routed anything, so unicast mail parsed,
/// landed on the chain, and reached nobody. The whole of this slice is one
/// predicate — a registration reads its role's broadcast, plus its own unicast
/// when it is NAMED — and the reason it gets its own test file is that every
/// way it can go wrong is SILENT. Mail delivered to the wrong mailbox is not an
/// exception, a nonzero exit, or a trail line; it is a digest that reads
/// slightly wrong to a human who has no idea what they were supposed to see.
///
/// Three claims, and the tests are grouped along them:
///
///   * THE REFUSAL. An unnamed reader must not match `role@<its session id>`.
///     Session ids are grammar-legal instance names, so this is a real address
///     someone could write, and matching it would make windows addressable —
///     the model ADR-0016 d6 rejected and this ADR re-rejected, since a mailbox
///     keyed to a window dies with the window.
///   * THE SECOND COPY. `MailCursors` filters by recipient in two places, and
///     only one of them is on the happy path. `Pending`'s scan decides what may
///     be delivered; `LoadOrAnchor`'s held-entry check decides whether a cursor
///     still describes its own mail. A named reader that HOLDS a unicast is the
///     state where a disagreement shows, and it shows as a re-anchor loop, not
///     as a missing feature.
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

    /// An unnamed reader is EXACTLY the reader ADR-0016 built: its role's
    /// broadcast and nothing else. The `main@s-1` row is the whole point — see
    /// the refusal test below for why it is not merely unnecessary.
    [Theory]
    [InlineData("main", true)]
    [InlineData("main@laptop-a", false)]
    [InlineData("main@s-1", false)]
    [InlineData("reviewer", false)]
    [InlineData("reviewer@main", false)]
    [InlineData("mainx", false)]
    [InlineData("main@", false)]
    [InlineData("@main", false)]
    public void UnnamedMailbox_ReadsItsBroadcastAndNothingElse(string to, bool accepted) =>
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

    /// A registration whose `--as` HAPPENS to equal its window's session id is
    /// still a named mailbox. Named-ness is carried from the registration, never
    /// inferred from the cursor key differing from the session — the inference
    /// is right for the trail (where equal means "nothing extra to say") and
    /// wrong here, where it would silently un-route a real mailbox's mail.
    [Fact]
    public void ANameThatEqualsTheSessionId_IsStillANamedMailbox()
    {
        using var tmp = new MailStoreTempDir();
        Send(tmp, Unicast("u-1", "main@s-1"));

        var cursors = new MailCursors(tmp.Store());
        Assert.Equal(["u-1"], Ids(cursors.Pending(new MailAddress("main", "s-1"), "s-1")));
    }

    // ---- the refusal --------------------------------------------------------

    /// THE PIN. A session id is the cursor key's FALLBACK (d3), never a name a
    /// sender may spell — and it is grammar-legal, so `main@s-1` is an address
    /// someone can really write. If an unnamed window answered to it, every
    /// window would be addressable by its session id and mail would be routed
    /// to mailboxes that die with the window, which is precisely the model
    /// ADR-0016 d6 rejected and whose failure is on the live ledger (four dead
    /// cursors holding mail forever).
    [Fact]
    public void UnnamedReader_DoesNotReceiveUnicastAddressedToItsOwnSessionId()
    {
        using var tmp = new MailStoreTempDir();
        Send(tmp, MailFixtures.Envelope(id: "b-1", to: "main"));
        Send(tmp, Unicast("u-1", "main@s-1"));

        var cursors = new MailCursors(tmp.Store());
        var view = cursors.Pending("main", "s-1");

        Assert.Equal(["b-1"], Ids(view));
        // And it is not merely unrendered: the frontier consumes it like any
        // other mailbox's mail, so it does not come back on the next read.
        Assert.IsType<MailCursorWrite.Written>(
            cursors.Advance(view, [view.Pending[0].Offset]));
        Assert.Empty(cursors.Pending("main", "s-1").Pending);
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
        Assert.Equal(["b-1"], Ids(cursors.Pending("main", "s-3")));   // and to an unnamed holder
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

    /// End to end through the real verb: an unnamed window whose session id is
    /// spelled out as an instance gets nothing, answers noop, and does not
    /// advance — the refusal is not a filter in front of a delivery, it is the
    /// absence of one.
    [Fact]
    public void UnnamedDigest_NeverDeliversUnicastMail()
    {
        using var tmp = new MailStoreTempDir();
        Send(tmp, Unicast("u-1", "main@s-1"));
        Send(tmp, Unicast("u-2", "main@laptop-a"));

        var run = Digest(tmp.Dir, "s-1", ["--role", "main"]);

        Assert.Equal(0, run.Exit);
        Assert.DoesNotContain("u-1", run.Out);
        Assert.DoesNotContain("u-2", run.Out);
        Assert.Contains("\"effect\":\"noop\"", run.Out.Replace(" ", ""));
        Assert.False(File.Exists(new MailCursors(tmp.Store()).CursorPath("main", "s-1")));
    }

    // ---- the observation surface's honest gap -------------------------------

    /// The read-only snapshot UNDER-CLAIMS, deliberately (d4 as built). A cursor
    /// file's name is just its key, so this surface cannot tell an `--as` name
    /// from a session id and cannot know whether the mailbox is entitled to
    /// `role@key` — and between under-claiming and guessing, a read-only picture
    /// under-claims. Guessing the other way would paint every window as
    /// addressable by its session id, which is the routing model d4 refuses.
    ///
    /// Pinned so the gap stays a decision: the live trail carries the `instance`
    /// column, so the picture is recoverable from the stream, and saying it in
    /// the snapshot is `canvas-instances`' to design.
    [Fact]
    public void TheReadOnlySnapshot_ShowsUnicastMailAsPendingForNobody()
    {
        using var tmp = new MailStoreTempDir();
        Send(tmp, MailFixtures.Envelope(id: "b-1", to: "main"));
        Send(tmp, Unicast("u-1", "main@laptop-a"));

        // The mailbox exists on disk under exactly the name a sender addresses.
        var cursors = new MailCursors(tmp.Store());
        var view = cursors.Pending(Named, "s-1");
        Assert.IsType<MailCursorWrite.Written>(cursors.Advance(view, [view.Pending[0].Offset]));

        var port = MailReadPort.Over(tmp.Dir);
        Assert.Equal([("main", "laptop-a")], port.Cursors());
        Assert.Equal(["b-1"], Ids(port.Pending("main", "laptop-a")));
    }
}
