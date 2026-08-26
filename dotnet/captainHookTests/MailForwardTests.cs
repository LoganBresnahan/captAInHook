using CaptainHook.Mail;

namespace CaptainHook.Tests;

/// ADR-0018 d6 (roadmap item 23, slice `reaper-payloads`) — `captainHook mail
/// forward`, the FORWARD half of a disposition.
///
/// The verb exists because the reaper that ships is deterministic (ADR-0017 §
/// Scope amendment) and a shell payload cannot compose an envelope — which is
/// `mail send`'s founding sentence, not a new argument. So the claims here are
/// about what a COPY is:
///
///   * IT IS A NEW ENVELOPE, and the original is untouched. The chain keeps
///     both, `forwardedFrom` is the only link, and it carries the ADDRESS as
///     well as the id because a bare role in `to` cannot say whose mailbox the
///     mail was stranded in.
///   * WHAT RIDES AND WHAT RESTARTS. Kind, topic, priority, body and `replyTo`
///     ride (d4: the forward's sender is the reaper and its reply address is
///     the asker's); `inReplyTo` is dropped and the depth restarts at 0 — the
///     stated cost of a STORED depth; the TTL is the TARGET's, since the
///     parser refuses one on a unicast address.
///   * IT IS IDEMPOTENT ON (source, target). The caller is a payload nobody is
///     watching, and the only alternative to skipping what already went is a
///     nudge that comes back and forwards everything twice.
///   * IT IS BLIND AND IT IS RECORDED. Nothing here reads a body — bodies go
///     ledger to ledger — and one `mail.forward` row says who moved which
///     mailbox's mail and where to.
public class MailForwardTests
{
    private static readonly MailAddress Dead = new("main", "laptop-a");

    private static (int Exit, string Out, string Err) Forward(
        MailStoreTempDir tmp, params string[] argv)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = MailForward.Run(argv, stdout, stderr, tmp.Dir,
            newId: () => "fwd-fixed", nowTs: "2026-08-26T12:00:00Z",
            declaredFrom: MailSenderDeclaration.None);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    /// A mailbox with a cursor, holding what it never read. That is the state
    /// the watcher calls dead: a standing on disk, mail pending against it, and
    /// nobody coming back for it.
    private static void ADeadMailboxHolding(MailStoreTempDir tmp, params MailEnvelope[] envelopes)
    {
        var cursors = tmp.Cursors();
        // First contact leaves the cursor file with nothing delivered — the
        // shape a window that opened, registered and closed again leaves.
        MailFixtures.AppendOk(tmp.Store(), MailFixtures.Envelope(id: "m-seed", to: "other"));
        CursorFixtures.AdvanceOk(cursors, cursors.Pending(Dead, hookSession: null));
        foreach (var e in envelopes) MailFixtures.AppendOk(tmp.Store(), e);
    }

    private static MailEnvelope Parsed(MailStoreTempDir tmp, string id) =>
        tmp.Store().Read().Select(l => l.Envelope).OfType<MailEnvelope>().Single(e => e.Id == id);

    // ---- the copy ----------------------------------------------------------

    /// The whole of what a forward IS, in one assertion set: a second envelope,
    /// the original still there, and the link that says where it came from.
    [Fact]
    public void AStrandedEnvelope_IsCopied_AndTheOriginalStays()
    {
        using var log = new CapturedLog();
        using var tmp = new MailStoreTempDir();
        ADeadMailboxHolding(tmp, MailFixtures.Envelope(
            id: "m-01", to: "main@laptop-a", body: "the stranded body",
            priority: MailPriority.Urgent, replyTo: "asker@window-9", ttl: null));

        var r = Forward(tmp, "main@laptop-a", "--to", "maintainer", "--by", "reaper");

        Assert.Equal(0, r.Exit);
        Assert.Contains("forwarded 1 from main@laptop-a to maintainer", r.Out);

        var copy = Parsed(tmp, "fwd-fixed");
        Assert.Equal("maintainer", copy.To);
        Assert.Equal(new MailForwardedFrom("m-01", "main@laptop-a"), copy.ForwardedFrom);
        // Rides: the reader of a forwarded urgent request must see the same
        // urgency and the same question the dead mailbox was sent.
        Assert.Equal(MailPriority.Urgent, copy.Priority);
        Assert.Equal("the stranded body", copy.Body);
        Assert.Equal("build", copy.Topic);
        Assert.Equal(MailKind.Status, copy.Kind);
        // d4: the answer belongs to whoever asked, not to the reaper.
        Assert.Equal("asker@window-9", copy.ReplyTo);
        // Restarts: a forward is not a reply, and a new chain starts at 0.
        Assert.Null(copy.InReplyTo);
        Assert.Equal(0, copy.Hops);
        // The sender is the forwarder, said in the honest harness for a
        // command line — never the original's sender, which would erase who
        // actually put this envelope on a live lane.
        Assert.Equal(new MailSender("reaper", "cli", null), copy.From);

        // The original is untouched and still addressed where it was.
        var original = Parsed(tmp, "m-01");
        Assert.Equal("main@laptop-a", original.To);
        Assert.Null(original.ForwardedFrom);
    }

    /// The TTL is the target's, both ways: a role address takes the default
    /// countdown, and a unicast target takes none — the parser refuses one
    /// there (d5), so a copy that kept the original's would be a forward that
    /// can only fail.
    [Theory]
    [InlineData("maintainer", MailEnvelope.DefaultTtlDeliveries)]
    [InlineData("maintainer@laptop-b", null)]
    public void TheTtl_IsTheTargetAddresss(string target, int? expected)
    {
        using var log = new CapturedLog();
        using var tmp = new MailStoreTempDir();
        ADeadMailboxHolding(tmp, MailFixtures.Envelope(id: "m-01", to: "main@laptop-a", ttl: null));

        Assert.Equal(0, Forward(tmp, "main@laptop-a", "--to", target, "--by", "reaper").Exit);

        Assert.Equal(expected, Parsed(tmp, "fwd-fixed").TtlDeliveries);
    }

    /// Broadcast mail the dead box was holding is forwarded too: `to: main` was
    /// pending FOR THIS MAILBOX, and a live sibling having read it says nothing
    /// about the copy this box never got.
    [Fact]
    public void EverythingPending_IsForwarded_InLedgerOrder()
    {
        using var log = new CapturedLog();
        using var tmp = new MailStoreTempDir();
        ADeadMailboxHolding(tmp,
            MailFixtures.Envelope(id: "m-01", to: "main"),
            MailFixtures.Envelope(id: "m-02", to: "main@laptop-a", ttl: null));

        var ids = new Queue<string>(["fwd-1", "fwd-2"]);
        var stdout = new StringWriter();
        var exit = MailForward.Run(["main@laptop-a", "--to", "maintainer", "--by", "reaper"],
            stdout, new StringWriter(), tmp.Dir, newId: ids.Dequeue,
            declaredFrom: MailSenderDeclaration.None);

        Assert.Equal(0, exit);
        Assert.Equal(["m-01", "m-02"],
            new[] { "fwd-1", "fwd-2" }.Select(id => Parsed(tmp, id).ForwardedFrom!.Id));
    }

    /// EXPIRED mail is not forwarded — `mail.reap`'s rule for `pendingIds`, for
    /// the same reason: those envelopes are spent, no digest was ever going to
    /// hand them over again, and moving them would resurrect at a live address
    /// what died at a dead one.
    [Fact]
    public void ExpiredMail_IsNotForwarded()
    {
        using var log = new CapturedLog();
        using var tmp = new MailStoreTempDir();
        var cursors = tmp.Cursors();
        var offset = CursorFixtures.AppendTo(tmp, "m-old", to: "main", ttl: 1);
        // One opportunity taken and passed over: at ttl 1 the envelope is spent.
        CursorFixtures.AdvanceOk(cursors, cursors.Pending(Dead, hookSession: null));
        Assert.Contains(offset, cursors.Pending(Dead, hookSession: null).Expired.Select(p => p.Offset));

        var r = Forward(tmp, "main@laptop-a", "--to", "maintainer", "--by", "reaper");

        Assert.Equal(0, r.Exit);
        Assert.Contains("nothing to forward", r.Out);
        Assert.DoesNotContain("fwd-", tmp.Text());
    }

    // ---- idempotence -------------------------------------------------------

    /// Run it twice and the live lane gets one copy. The payload only reaps
    /// when the forward succeeded, so a half-finished run WILL be retried — by
    /// the next nudge if not by the operator — and doubling somebody's mail on
    /// every retry is the failure this skip exists to prevent.
    [Fact]
    public void Rerunning_SkipsWhatItAlreadyForwarded()
    {
        using var log = new CapturedLog();
        using var tmp = new MailStoreTempDir();
        ADeadMailboxHolding(tmp, MailFixtures.Envelope(id: "m-01", to: "main@laptop-a", ttl: null));
        Assert.Equal(0, Forward(tmp, "main@laptop-a", "--to", "maintainer", "--by", "reaper").Exit);

        var again = Forward(tmp, "main@laptop-a", "--to", "maintainer", "--by", "reaper");

        Assert.Equal(0, again.Exit);
        Assert.Contains("forwarded 0", again.Out);
        Assert.Contains("1 already forwarded, skipped", again.Out);
        Assert.Single(tmp.Store().Read().Select(l => l.Envelope).OfType<MailEnvelope>(),
            e => e.ForwardedFrom is not null);
    }

    /// …but the skip is keyed on the TARGET as well as the id. Forwarding one
    /// dead box's mail to two roles is a legitimate thing an operator may do in
    /// two runs, and matching the id alone would make the second run a silent
    /// no-op.
    [Fact]
    public void ADifferentTarget_IsNotAlreadyForwarded()
    {
        using var log = new CapturedLog();
        using var tmp = new MailStoreTempDir();
        ADeadMailboxHolding(tmp, MailFixtures.Envelope(id: "m-01", to: "main@laptop-a", ttl: null));
        Assert.Equal(0, Forward(tmp, "main@laptop-a", "--to", "maintainer", "--by", "reaper").Exit);

        var stdout = new StringWriter();
        var exit = MailForward.Run(["main@laptop-a", "--to", "reviewer", "--by", "reaper"],
            stdout, new StringWriter(), tmp.Dir, newId: () => "fwd-second",
            declaredFrom: MailSenderDeclaration.None);

        Assert.Equal(0, exit);
        Assert.Contains("forwarded 1", stdout.ToString());
        Assert.Equal("reviewer", Parsed(tmp, "fwd-second").To);
    }

    // ---- nothing to do is not a failure ------------------------------------

    /// A mailbox with no cursor has no standing and holds nothing; an empty one
    /// holds nothing either. Both are exit 0, because the payload's next step
    /// is to reap and a "failure" here would stop it disposing of a box that
    /// really is empty.
    [Theory]
    [InlineData(false, "no mailbox")]
    [InlineData(true, "nothing pending")]
    public void NothingToMove_IsSuccessWithAReason(bool giveItACursor, string why)
    {
        using var log = new CapturedLog();
        using var tmp = new MailStoreTempDir();
        if (giveItACursor) ADeadMailboxHolding(tmp);

        var r = Forward(tmp, "main@laptop-a", "--to", "maintainer", "--by", "reaper");

        Assert.Equal(0, r.Exit);
        Assert.Contains(why, r.Out);
        Assert.Empty(r.Err);
        // Nothing happened, so nothing is on the trail: a row per empty forward
        // would be a record of an event that did not occur.
        Assert.DoesNotContain(log.Events, e => e.Evt == "mail.forward");
    }

    // ---- who is doing it ---------------------------------------------------

    /// Somebody has to be named. This verb puts another mailbox's mail on a
    /// live lane, and an envelope from a sender nobody can name is the exact
    /// provenance defect ADR-0017 N6 was written to end.
    [Fact]
    public void WithNoSenderAtAll_ItRefuses_AndWritesNothing()
    {
        using var log = new CapturedLog();
        using var tmp = new MailStoreTempDir();
        ADeadMailboxHolding(tmp, MailFixtures.Envelope(id: "m-01", to: "main@laptop-a", ttl: null));
        var before = tmp.Bytes();

        var r = Forward(tmp, "main@laptop-a", "--to", "maintainer");

        Assert.Equal(1, r.Exit);
        Assert.Contains("say who is forwarding", r.Err);
        Assert.Equal(before, tmp.Bytes());
    }

    /// A spawning payload's declaration outranks `--by`, and a `--by` that
    /// disagrees is REFUSED rather than corrected — `mail send`'s rule, because
    /// one process must not have two provenances.
    [Fact]
    public void ADeclaration_OutranksBy_AndADisagreeingByIsRefused()
    {
        using var log = new CapturedLog();
        using var tmp = new MailStoreTempDir();
        ADeadMailboxHolding(tmp, MailFixtures.Envelope(id: "m-01", to: "main@laptop-a", ttl: null));
        var declared = new MailSenderDeclaration("reaper", "claude-code", "turn-d7");

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var ok = MailForward.Run(["main@laptop-a", "--to", "maintainer"], stdout, stderr, tmp.Dir,
            newId: () => "fwd-declared", declaredFrom: declared);

        Assert.Equal(0, ok);
        Assert.Equal(new MailSender("reaper", "claude-code", "turn-d7"), Parsed(tmp, "fwd-declared").From);

        var before = tmp.Bytes();
        var refused = MailForward.Run(["main@laptop-a", "--to", "reviewer", "--by", "somebody-else"],
            new StringWriter(), stderr, tmp.Dir, newId: () => "fwd-nope", declaredFrom: declared);

        Assert.Equal(1, refused);
        Assert.Contains("cannot name itself somebody else", stderr.ToString());
        Assert.Equal(before, tmp.Bytes());
    }

    // ---- the arguments -----------------------------------------------------

    /// Forwarding a mailbox to itself would append copies straight back into
    /// the box nobody is reading — a loop with a fresh TTL on every lap, and
    /// the one mistake a `REAPER_FORWARD_TO` typo can actually make.
    [Fact]
    public void ForwardingAMailboxToItself_IsRefused()
    {
        using var log = new CapturedLog();
        using var tmp = new MailStoreTempDir();
        ADeadMailboxHolding(tmp, MailFixtures.Envelope(id: "m-01", to: "main@laptop-a", ttl: null));

        var r = Forward(tmp, "main@laptop-a", "--to", "main@laptop-a", "--by", "reaper");

        Assert.Equal(1, r.Exit);
        Assert.Contains("is the mailbox being forwarded", r.Err);
    }

    /// Every address goes through the envelope parser's own gate, and a
    /// forward with nowhere to go is a caller bug rather than a no-op.
    [Theory]
    [InlineData(new[] { "main@laptop-a" }, "--to <address> is required")]
    [InlineData(new[] { "main@laptop-a", "--to" }, "unknown or incomplete argument")]
    [InlineData(new[] { "Main@laptop-a", "--to", "maintainer" }, "is not an address")]
    [InlineData(new[] { "main@laptop-a", "--to", "MAINTAINER" }, "--to 'MAINTAINER' is not an address")]
    [InlineData(new[] { "main@laptop-a", "other@box", "--to", "maintainer" }, "exactly one source address")]
    [InlineData(new[] { "main@laptop-a", "--to", "a", "--to", "b" }, "--to given twice")]
    [InlineData(new[] { "main@laptop-a", "--to", "maintainer", "--by", "NOPE" }, "--by 'NOPE' is not an address")]
    public void BadArguments_AreRefusedWithTheUsage(string[] argv, string expected)
    {
        using var log = new CapturedLog();
        using var tmp = new MailStoreTempDir();

        var r = Forward(tmp, argv);

        Assert.Equal(1, r.Exit);
        Assert.Contains(expected, r.Err);
        Assert.Contains("usage: captainHook mail forward", r.Err);
    }

    // ---- the record --------------------------------------------------------

    /// One row per run, in the trail's existing spelling: `role` + `instance`
    /// for the mailbox (what `mail.deliver`, `mail.cursorAdvance` and
    /// `mail.reap` already use), a single `to` for the target because that is
    /// what it literally became, and the ids — the fact the per-envelope
    /// `mail.append` rows cannot show is that somebody moved another mailbox's
    /// mail.
    [Fact]
    public void TheTrailRow_NamesTheMailbox_TheTarget_AndWhatMoved()
    {
        using var log = new CapturedLog();
        using var tmp = new MailStoreTempDir();
        ADeadMailboxHolding(tmp, MailFixtures.Envelope(id: "m-01", to: "main@laptop-a", ttl: null));

        Assert.Equal(0, Forward(tmp, "main@laptop-a", "--to", "maintainer", "--by", "reaper").Exit);

        var row = Assert.Single(log.Events, e => e.Evt == "mail.forward");
        var data = row.Fields.Data!;
        Assert.Equal("main", data["role"]);
        Assert.Equal("laptop-a", data["instance"]);
        Assert.Equal("maintainer", data["to"]);
        Assert.Equal("reaper", data["by"]);
        Assert.Equal(["m-01"], (IReadOnlyList<string>)data["forwardedIds"]);
        // Absent on a first run: a zero here would be a column about the
        // ordinary case.
        Assert.False(data.ContainsKey("skippedIds"));
    }

    /// A second run's row says what it skipped, so a reader of the trail can
    /// tell "nothing was there" from "it had already been moved".
    [Fact]
    public void ASecondRunsRow_SaysWhatItSkipped()
    {
        using var log = new CapturedLog();
        using var tmp = new MailStoreTempDir();
        ADeadMailboxHolding(tmp, MailFixtures.Envelope(id: "m-01", to: "main@laptop-a", ttl: null));
        Assert.Equal(0, Forward(tmp, "main@laptop-a", "--to", "maintainer", "--by", "reaper").Exit);
        Assert.Equal(0, Forward(tmp, "main@laptop-a", "--to", "maintainer", "--by", "reaper").Exit);

        var row = log.Events.Where(e => e.Evt == "mail.forward").Last();
        Assert.Empty((IReadOnlyList<string>)row.Fields.Data!["forwardedIds"]);
        Assert.Equal(["m-01"], (IReadOnlyList<string>)row.Fields.Data!["skippedIds"]);
    }
}
