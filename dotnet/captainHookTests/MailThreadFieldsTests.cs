using System.Text;
using System.Text.Json;
using CaptainHook.Api;
using CaptainHook.Core;
using CaptainHook.Mail;
using CaptainHook.Wire;
using static CaptainHook.Tests.TestUtil;

namespace CaptainHook.Tests;

// ADR-0017 decision 8, slice `thread-fields` (roadmap item 22, phase 1, thread
// lane) — `inReplyTo` becomes READ, and a thread carries a STORED depth.
//
// The stored-vs-derived question was the slice's blocking decision and it was
// answered on the bus itself (2026-08-19, `doc/dogfood/2026-08-19-the-robot-
// channel.md`): a DERIVED count walks `inReplyTo` back to the head, and
// rotation (ADR-0016 d13) starts a new chain per generation, so the walk
// routinely hits a link naming an id this store no longer holds — whereupon the
// walker either treats the dangling link as the head and silently RESETS the
// count on the longest-lived threads, or refuses a legitimate reply for a
// reason no reader can see. "A bound whose failure direction is
// unbounded-and-silent is not a bound."
//
// `AReplyWhoseParentHasRotatedAway_StillCountsItsHops` is the test that
// decision named: every derived implementation fails it.
public class MailThreadFieldsTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static (int Exit, string Out, string Err) Send(
        string envelope, string dir, int? maxHops = null)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = MailSend.Run("send", new StringReader(envelope), stdout, stderr,
            dir, nowTs: "2026-08-19T12:00:00Z", maxHops: maxHops,
            declaredFrom: MailSenderDeclaration.None);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    /// One envelope as a sender would write it: no `hops` unless a test is
    /// making a point of stating one.
    private static string Envelope(
        string id, string? inReplyTo = null, string kind = "status", string? hops = null,
        string to = "main") =>
        $$"""
        {"v":1,"id":"{{id}}","ts":"2026-08-19T12:00:00Z","from":{"agent":"a","harness":"claude-code"},
         "to":"{{to}}","kind":"{{kind}}","topic":"t","body":"b"
         {{(inReplyTo is null ? "" : $",\"inReplyTo\":\"{inReplyTo}\"")}}
         {{(hops is null ? "" : $",\"hops\":{hops}")}}}
        """.Replace("\n", "");

    // ---- the parse rules -----------------------------------------------------

    /// Optional, `>= 0`, default 0 — and REFUSED without a link, which is the
    /// `ttlDeliveries`-on-a-unicast shape: a field that does not apply is
    /// refused rather than accepted-and-ignored, because an ignored field on an
    /// append-only chain is a lie that outlives everyone who could correct it.
    [Theory]
    [InlineData("\"hops\":2,\"inReplyTo\":\"m-01\"", true, 2)]
    [InlineData("\"hops\":0,\"inReplyTo\":\"m-01\"", true, 0)]
    [InlineData("\"inReplyTo\":\"m-01\"", true, 0)]          // absent ⇒ 0
    [InlineData("", true, 0)]                                // no thread at all ⇒ 0
    [InlineData("\"hops\":1", false, 0)]                     // a depth with no link
    [InlineData("\"hops\":0", false, 0)]                     // …even 0: it does not apply
    [InlineData("\"hops\":-1,\"inReplyTo\":\"m-01\"", false, 0)]
    [InlineData("\"hops\":\"2\",\"inReplyTo\":\"m-01\"", false, 0)]
    [InlineData("\"hops\":2.5,\"inReplyTo\":\"m-01\"", false, 0)]
    [InlineData("\"hops\":null,\"inReplyTo\":\"m-01\"", false, 0)]
    public void Hops_ParsesAsAnOptionalNonNegativeIntThatNeedsALink(
        string extra, bool ok, int expected)
    {
        var line =
            "{\"v\":1,\"id\":\"m-02\",\"ts\":\"t\",\"from\":{\"agent\":\"a\",\"harness\":\"h\"},"
            + "\"to\":\"main\",\"kind\":\"answer\",\"topic\":\"t\",\"body\":\"b\""
            + (extra.Length == 0 ? "" : "," + extra) + "}";

        var e = MailEnvelope.TryParseLine(line, out var errors);

        if (!ok)
        {
            Assert.Null(e);
            Assert.Contains(errors, x => x.Contains("hops"));
            return;
        }
        Assert.NotNull(e);
        Assert.Equal(expected, e.Hops);
    }

    /// The refusal SAYS why, in the terms a sender can act on: a head is at
    /// depth 0 by construction, and only a link advances the count.
    [Fact]
    public void HopsWithoutALink_RefusesWithTheReason()
    {
        MailEnvelope.TryParseLine(
            """{"v":1,"id":"m-1","ts":"t","from":{"agent":"a","harness":"h"},"to":"main","kind":"status","topic":"t","body":"b","hops":3}""",
            out var errors);

        Assert.Contains(errors, e => e.Contains("'hops' is not allowed without 'inReplyTo'"));
    }

    // ---- the line ------------------------------------------------------------

    /// The durable line, field for field: `hops` sits beside the link it counts
    /// and is written WHENEVER THE LINK EXISTS — including at 0, which is a
    /// real state (a reply whose parent has rotated away) and not an absence.
    [Fact]
    public void Render_PutsHopsBesideTheLink_AndRoundTrips()
    {
        var reply = MailFixtures.Envelope(id: "m-02", inReplyTo: "m-01", hops: 2);

        var line = MailStore.Render(reply, MailStore.Genesis);

        Assert.Contains("""
            "inReplyTo":"m-01","hops":2,
            """.Trim(), line);
        var back = MailEnvelope.TryParseLine(line, out var errors);
        Assert.True(back is not null, string.Join("; ", errors));
        Assert.Equal(2, back!.Hops);
        Assert.Equal("m-01", back.InReplyTo);
    }

    /// A non-reply writes NOTHING — the field is absent on every line already on
    /// somebody's disk, and this slice must not change one byte of them.
    [Fact]
    public void Render_WritesNoHops_WhenThereIsNoLink()
    {
        Assert.DoesNotContain("hops", MailStore.Render(MailFixtures.Envelope(), MailStore.Genesis));
    }

    /// A depth with no link cannot be silently DROPPED at render: it is
    /// written, the strict parser refuses it, and `Append`'s re-parse guard
    /// turns an impossible envelope into a loud failure rather than a line that
    /// reads back as a fresh thread.
    [Fact]
    public void AnImpossibleEnvelope_FailsLoudly_RatherThanLosingItsDepth()
    {
        using var tmp = new MailStoreTempDir();

        var result = tmp.Store().Append(MailFixtures.Envelope(inReplyTo: null, hops: 4));

        var failed = Assert.IsType<MailAppend.Failed>(result);
        Assert.Contains("hops", failed.Error);
        Assert.Empty(tmp.Bytes());
    }

    // ---- the trail -----------------------------------------------------------

    /// The live picture has to be able to close a request the moment its answer
    /// lands, so the correlation rides the trail beside the depth — clamped by
    /// the same spelling the id uses, since it IS an id.
    [Fact]
    public void Append_CarriesTheThreadColumns()
    {
        using var log = new CapturedLog();
        using var tmp = new MailStoreTempDir();

        MailFixtures.AppendOk(tmp.Store(), MailFixtures.Envelope(id: "m-01"));
        MailFixtures.AppendOk(tmp.Store(), MailFixtures.Envelope(id: "m-02", inReplyTo: "m-01", hops: 1));

        var rows = log.Events.Where(e => e.Evt == "mail.append")
            .Select(e => JsonDocument.Parse(e.ToJson()).RootElement.GetProperty("data").Clone())
            .ToList();

        // The head says nothing about a thread it is not in — absent-means-omit.
        Assert.False(rows[0].TryGetProperty("inReplyTo", out _));
        Assert.False(rows[0].TryGetProperty("hops", out _));

        Assert.Equal("m-01", rows[1].GetProperty("inReplyTo").GetString());
        Assert.Equal(1, rows[1].GetProperty("hops").GetInt32());
    }

    [Fact]
    public void Append_ClampsTheLinkLikeEveryOtherSenderControlledId()
    {
        using var log = new CapturedLog();
        using var tmp = new MailStoreTempDir();

        var long1 = new string('p', 5_000);
        MailFixtures.AppendOk(tmp.Store(), MailFixtures.Envelope(inReplyTo: long1, hops: 1));

        var data = JsonDocument.Parse(
            Assert.Single(log.Events, e => e.Evt == "mail.append").ToJson())
            .RootElement.GetProperty("data");
        Assert.Equal(MailEnvelope.ClampField(long1), data.GetProperty("inReplyTo").GetString());
    }

    // ---- `mail send`: the depth comes from the LEDGER -------------------------

    /// The ordinary exchange: a reply to a resident parent is stamped at
    /// `parent + 1`, and the count keeps climbing down the thread.
    [Fact]
    public void AReply_IsStampedFromItsResidentParent()
    {
        using var tmp = new MailStoreTempDir();

        Assert.Equal(0, Send(Envelope("m-01"), tmp.Dir).Exit);
        Assert.Equal(0, Send(Envelope("m-02", inReplyTo: "m-01", kind: "answer"), tmp.Dir).Exit);
        Assert.Equal(0, Send(Envelope("m-03", inReplyTo: "m-02"), tmp.Dir).Exit);

        var stored = tmp.Store().Read().Select(l => l.Envelope!).ToList();
        Assert.Equal([0, 1, 2], stored.Select(e => e.Hops));
    }

    /// A model must not be able to name its own thread position. A stated depth
    /// that disagrees with the resident parent is REFUSED rather than silently
    /// corrected — correcting it would hide the attempt on a chain nobody can
    /// amend — and the message says what the right number is.
    [Fact]
    public void AStatedDepthThatDisagreesWithTheLedger_IsRefused()
    {
        using var tmp = new MailStoreTempDir();
        Assert.Equal(0, Send(Envelope("m-01"), tmp.Dir).Exit);
        Assert.Equal(0, Send(Envelope("m-02", inReplyTo: "m-01"), tmp.Dir).Exit);

        // m-02 is at depth 1, so a reply to it is at 2 — claiming 0 is the
        // laundering attempt, and it buys nothing.
        var (exit, _, err) = Send(Envelope("m-03", inReplyTo: "m-02", hops: "0"), tmp.Dir);

        Assert.Equal(1, exit);
        Assert.Contains("'hops' says 0", err);
        Assert.Contains("depth 1", err);
        Assert.Equal(2, tmp.Lines().Length);   // nothing written
    }

    /// A stated depth that AGREES is fine — a sender that computed the same
    /// number the ledger did is not wrong, it is redundant.
    [Fact]
    public void AStatedDepthThatAgrees_IsAccepted()
    {
        using var tmp = new MailStoreTempDir();
        Assert.Equal(0, Send(Envelope("m-01"), tmp.Dir).Exit);

        Assert.Equal(0, Send(Envelope("m-02", inReplyTo: "m-01", hops: "1"), tmp.Dir).Exit);
        Assert.Equal(1, tmp.Store().Read()[1].Envelope!.Hops);
    }

    /// d8's ping-pong bound: the depth that would REACH the budget is refused,
    /// nothing is written, and the refusal says what to do instead. The head
    /// occupies the thread's first slot, so a budget of 3 is head + two replies.
    [Fact]
    public void AThreadThatReachesTheBudget_IsRefused()
    {
        using var tmp = new MailStoreTempDir();

        Assert.Equal(0, Send(Envelope("m-01"), tmp.Dir, maxHops: 3).Exit);
        Assert.Equal(0, Send(Envelope("m-02", inReplyTo: "m-01"), tmp.Dir, maxHops: 3).Exit);
        Assert.Equal(0, Send(Envelope("m-03", inReplyTo: "m-02"), tmp.Dir, maxHops: 3).Exit);

        var (exit, _, err) = Send(Envelope("m-04", inReplyTo: "m-03"), tmp.Dir, maxHops: 3);

        Assert.Equal(1, exit);
        Assert.Contains("the thread is full", err);
        Assert.Contains("depth 3", err);
        Assert.Equal(3, tmp.Lines().Length);
        Assert.Empty(tmp.Store().VerifyChain());
    }

    /// The bound is a POLICY, not a format rule: the line the budget refuses is
    /// perfectly parseable, so lowering the budget can never make an archive
    /// unreadable.
    [Fact]
    public void TheBudgetRefusalIsAPolicy_NotAParseError()
    {
        var deep =
            """{"v":1,"id":"m-99","ts":"t","from":{"agent":"a","harness":"h"},"to":"main","kind":"answer","topic":"t","body":"b","inReplyTo":"m-98","hops":99}""";

        var e = MailEnvelope.TryParseLine(deep, out var errors);

        Assert.True(e is not null, string.Join("; ", errors));
        Assert.Equal(99, e!.Hops);
    }

    /// THE ONE TEST the deciding answer named. A store holding ONLY the reply —
    /// its parent rotated away, its `inReplyTo` naming an id nothing here holds
    /// — must still count its hops, and every DERIVED implementation fails part
    /// (c): a walk that cannot find the head has to guess, and guessing "this is
    /// the head" lifts the bound precisely on the threads that caused the
    /// rotation.
    [Fact]
    public void AReplyWhoseParentHasRotatedAway_StillCountsItsHops()
    {
        using var tmp = new MailStoreTempDir();

        // (a) the stored depth round-trips verbatim through render + parse…
        var orphan = MailFixtures.Envelope(id: "m-02", inReplyTo: "m-gone", hops: 2);
        MailFixtures.AppendOk(tmp.Store(), orphan);
        var stored = Assert.Single(tmp.Store().Read()).Envelope!;
        Assert.Equal(2, stored.Hops);
        Assert.Equal("m-gone", stored.InReplyTo);

        // (b) …and a dangling link is NOT tampering. There is deliberately no
        // hop fault kind: a rotated-away parent must never read as an edit.
        Assert.Empty(tmp.Store().VerifyChain());

        // (c) a further reply computes 3 and is refused at a budget of 3 — the
        // count did not restart just because the head is gone.
        var (exit, _, err) = Send(Envelope("m-03", inReplyTo: "m-02"), tmp.Dir, maxHops: 3);
        Assert.Equal(1, exit);
        Assert.Contains("depth 3", err);

        // (d) a FORWARD of it carries no link and therefore depth 0 — the
        // stated cost of a stored count, and the reason it is written down
        // rather than discovered later. It needs a mailbox to actually die, so
        // it is not a cycle two agents can drive.
        var forward =
            """{"v":1,"id":"m-04","ts":"t","from":{"agent":"reaper","harness":"claude-code"},"to":"main","kind":"status","topic":"t","body":"b","forwardedFrom":{"id":"m-02","address":"reviewer@s1"}}""";
        Assert.Equal(0, Send(forward, tmp.Dir).Exit);
        var forwarded = tmp.Store().Read()[1].Envelope!;
        Assert.Equal(0, forwarded.Hops);
        Assert.Null(forwarded.InReplyTo);
    }

    /// A reply to an id this store never held is the same case as a rotated-away
    /// parent, and it must not be refused: nothing on this machine knows the
    /// count, and killing the reply would be a refusal no reader could explain.
    [Fact]
    public void AReplyToAnUnknownId_IsAcceptedAtDepthZero()
    {
        using var tmp = new MailStoreTempDir();

        Assert.Equal(0, Send(Envelope("m-01", inReplyTo: "never-here"), tmp.Dir).Exit);
        Assert.Equal(0, Assert.Single(tmp.Store().Read()).Envelope!.Hops);
    }

    // ---- the digest ----------------------------------------------------------

    /// d8: "the digest says `hop n/N`". Rendered as the envelope's ORDINAL in
    /// the thread (the head is 1/N) so that N/N reads as full — the reader
    /// deciding whether to answer is usually a model, and it must be able to see
    /// that one more reply is all there is.
    [Fact]
    public void TheDigestHead_NamesTheThreadAndTheDepth()
    {
        using var tmp = new MailStoreTempDir();
        MailFixtures.AppendOk(tmp.Store(), MailFixtures.Envelope(id: "m-01"));
        MailFixtures.AppendOk(tmp.Store(), MailFixtures.Envelope(id: "m-02", inReplyTo: "m-01", hops: 2));

        var view = new MailCursors(tmp.Store()).Pending("main", "s-1");
        var plan = MailDigest.Plan(view.Pending, MailSeam.Ambient, ["inject"]);
        var render = MailDigest.Render(view, plan, maxChars: 4096);

        Assert.Contains("answers m-01", render.Text);
        Assert.Contains($"hop 3/{MailEnvelope.HopBudget}", render.Text);
        // The head of a thread says nothing about hops — it is not in one yet.
        var headLine = render.Text.Split('\n').First(l => l.Contains("id m-01"));
        Assert.DoesNotContain("hop ", headLine);
    }

    // ---- the read model ------------------------------------------------------

    /// The closed-by index: an `answer` whose `inReplyTo` names a request closes
    /// it. Computed over the WHOLE ledger rather than the `?since=` window,
    /// because a request and its answer routinely sit on opposite sides of that
    /// window — a client joining only what it can see would draw an open thread
    /// that was answered before the page loaded.
    [Fact]
    public void TheReadModel_IndexesWhoClosedWhat()
    {
        using var tmp = new MailStoreTempDir();
        MailFixtures.AppendOk(tmp.Store(), MailFixtures.Envelope(id: "req-1"));
        MailFixtures.AppendOk(tmp.Store(), MailFixtures.Envelope(id: "unanswered"));
        var answer = MailFixtures.Envelope(id: "ans-1", inReplyTo: "req-1", hops: 1) with { Kind = MailKind.Answer };
        MailFixtures.AppendOk(tmp.Store(), answer);
        // A second answer is a LATE answer — a thing to tag, not a second
        // closure — so the FIRST one keeps the row.
        var late = MailFixtures.Envelope(id: "ans-2", inReplyTo: "req-1", hops: 1) with { Kind = MailKind.Answer };
        MailFixtures.AppendOk(tmp.Store(), late);
        // A STATUS that quotes a request is a comment on it, not a reply to it.
        MailFixtures.AppendOk(tmp.Store(), MailFixtures.Envelope(id: "note", inReplyTo: "unanswered", hops: 1));

        // The window starts past the request, which is the whole point: the
        // client cannot make this join from what it is handed.
        var mail = Model(tmp).Mail(since: tmp.Store().Read()[2].Offset)!;

        Assert.Equal("ans-1", mail.ClosedBy["req-1"]);
        Assert.False(mail.ClosedBy.ContainsKey("unanswered"));
        Assert.Equal(3, mail.Lines.Count);   // the request is outside the window…
        Assert.DoesNotContain(mail.Lines, l => l.Envelope?.Id == "req-1");

        // …and the depth rides every envelope the snapshot serves.
        Assert.Equal(1, mail.Lines.Single(l => l.Envelope?.Id == "ans-1").Envelope!.Hops);
        Assert.Equal(1, mail.Lines.Single(l => l.Envelope?.Id == "note").Envelope!.Hops);
    }

    /// The wire spelling, once: `hops` on the envelope and `closedBy` on the
    /// snapshot, in the camelCase the schema describes.
    [Fact]
    public void TheThreadFieldsAreOnTheWire()
    {
        using var tmp = new MailStoreTempDir();
        MailFixtures.AppendOk(tmp.Store(), MailFixtures.Envelope(id: "req-1"));
        var answer = MailFixtures.Envelope(id: "ans-1", inReplyTo: "req-1", hops: 1) with { Kind = MailKind.Answer };
        MailFixtures.AppendOk(tmp.Store(), answer);

        var json = JsonSerializer.Serialize(Model(tmp).Mail(0)!, Web);

        Assert.Contains("\"hops\":1", json);
        Assert.Contains("\"closedBy\":{\"req-1\":\"ans-1\"}", json);
    }

    private static ApiReadModel Model(MailStoreTempDir tmp) =>
        new("thread-fields", new ServeStats(),
            new Dispatcher(new Registry().On("UserPromptSubmit",
                TestHandler.Returning("greeter", new Effect.Noop())), TimeSpan.FromSeconds(2)),
            new ReloadingHarnessRegistry(NoHarnessDir()), new ReloadingPolicy(null), null,
            clock: () => 6000, startTick: 1000, handlersPath: null,
            mail: MailReadPort.Over(tmp.Dir), presence: null);
}
