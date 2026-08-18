using CaptainHook.Mail;

namespace CaptainHook.Tests;

/// ADR-0018 d6 (roadmap item 23, slice `reap-verb`) — `captainHook mail reap`,
/// the one sanctioned way a mailbox's STANDING ends.
///
/// The power is not new: ADR-0016 d13 says a cursor file is deletable at any
/// moment, and `mail.cursorVanished` is what a digest emits when it trips over
/// one that went. What this verb adds is the RECORD — who reaped which mailbox
/// and what it was still holding — and the LOCK, so the removal cannot
/// interleave with an advance. Both are what the tests below are about; the
/// deletion itself is one line.
///
/// Three claims:
///
///   * THE LOCK. The delete holds `cursor….json.lock` (the same flock
///     `Advance` takes) and never unlinks it. Unlinking a held lock file is the
///     classic race — the next caller creates a fresh inode and takes a lock
///     that excludes nobody — and here the party it would fail to exclude is a
///     running digest.
///   * THE RECORD. `mail.reap` names the mailbox in the trail's existing
///     spelling (`role` + `instance`, written when named), lists what was
///     stranded, and names the reaper when one is given. It is written only
///     when something was actually removed.
///   * THE COST, STATED. A reaped mailbox reads as first contact and its mail
///     comes back. That is d13's already-published price for deletion; this
///     verb does not change it, it explains it.
public class MailReapTests
{
    private static readonly MailAddress Sessionless = new("main", null);
    private static readonly MailAddress Named = new("main", "laptop-a");

    private static (int Exit, string Out, string Err) Reap(
        MailStoreTempDir tmp, params string[] argv)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = MailReap.Run(argv, stdout, stderr, tmp.Dir);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    /// A mailbox with a cursor and one delivery behind it — the state a live
    /// reader leaves and a dead one leaves behind.
    private static string GiveItACursor(MailStoreTempDir tmp, MailAddress box, string envelopeId)
    {
        var cursors = tmp.Cursors();
        var offset = CursorFixtures.AppendTo(tmp, envelopeId);
        CursorFixtures.AdvanceOk(cursors, cursors.Pending(box, "s-1"), offset);
        var path = cursors.CursorPath(box.Role, box.Instance);
        Assert.True(File.Exists(path));
        return path;
    }

    // ---- the reap itself ---------------------------------------------------

    /// The standing goes, the mail stays. The store's bytes are compared before
    /// and after: a disposal verb that touched the append-only chain would be
    /// destroying exactly the history d6 promises survives.
    [Fact]
    public void AReap_RemovesTheCursorAndLeavesTheLedgerByteIdentical()
    {
        using var log = new CapturedLog();
        using var tmp = new MailStoreTempDir();
        var path = GiveItACursor(tmp, Named, "m-01");
        var before = tmp.Bytes();

        var r = Reap(tmp, "main@laptop-a");

        Assert.Equal(0, r.Exit);
        Assert.False(File.Exists(path));
        Assert.Equal(before, tmp.Bytes());
        Assert.Contains("reaped main@laptop-a", r.Out);
        Assert.Empty(r.Err);
    }

    /// The cost d13 already published, said out loud here because this verb is
    /// where a human chooses to pay it: the reaped mailbox anchors fresh and
    /// mail it had consumed is pending again. Quietly — at deliveries 0 a
    /// deletion is indistinguishable from first contact, which is why the
    /// `mail.reap` line is the only thing that can explain the duplicate.
    [Fact]
    public void AReapedMailbox_ReadsAsFirstContactAndItsMailComesBack()
    {
        using var log = new CapturedLog();
        using var tmp = new MailStoreTempDir();
        GiveItACursor(tmp, Named, "m-01");
        Assert.Empty(tmp.Cursors().Pending(Named, "s-1").Pending);   // consumed

        Assert.Equal(0, Reap(tmp, "main@laptop-a").Exit);

        var after = tmp.Cursors().Pending(Named, "s-1");
        Assert.Equal(["m-01"], after.Pending.Select(p => p.Envelope.Id));
        Assert.Equal(0, after.Deliveries);
        Assert.False(after.Reanchored);   // first contact, not a distrusted state
    }

    /// Reaping the same mailbox twice is success, not a failure: a reaper that
    /// retried, or raced another one, got the outcome it asked for. Nothing is
    /// created on the way past — in particular no lock file for a mailbox that
    /// does not exist, which is what checking existence BEFORE the lock buys.
    [Fact]
    public void ReapingAMailboxThatIsNotThere_SucceedsAndCreatesNothing()
    {
        using var log = new CapturedLog();
        using var tmp = new MailStoreTempDir();
        var before = Directory.GetFiles(tmp.Dir).OrderBy(f => f).ToArray();

        var r = Reap(tmp, "main@ghost");

        Assert.Equal(0, r.Exit);
        Assert.Contains("nothing to reap", r.Out);
        Assert.Equal(before, Directory.GetFiles(tmp.Dir).OrderBy(f => f).ToArray());
        Assert.DoesNotContain(log.Events, e => e.Evt == "mail.reap");
    }

    // ---- the lock ----------------------------------------------------------

    /// The lock file OUTLIVES the reap. Unlinking it under the flock would let
    /// the next caller create a fresh inode and take a lock that excludes
    /// nobody — and the caller this must never fail to exclude is a digest
    /// mid-advance. The stray file is the price, and it is invisible:
    /// `MailCursors.List` matches `cursor.*.json`, which a `.lock` is not.
    [Fact]
    public void TheLockFile_SurvivesTheReap_AndIsInvisibleToEveryListing()
    {
        using var log = new CapturedLog();
        using var tmp = new MailStoreTempDir();
        var path = GiveItACursor(tmp, Named, "m-01");

        Assert.Equal(0, Reap(tmp, "main@laptop-a").Exit);

        Assert.True(File.Exists(path + ".lock"));
        Assert.Empty(MailCursors.List(tmp.Dir));
    }

    /// An advance in flight wins. Holding the cursor's lock the way `Advance`
    /// does makes the reap refuse — because deleting the file underneath a
    /// running advance would let it write the cursor back a moment later,
    /// leaving a reaped mailbox standing again with nothing on the trail
    /// saying it ever went.
    [Fact]
    public void AReap_RefusesWhileAnotherWriterHoldsTheCursorLock()
    {
        using var log = new CapturedLog();
        using var tmp = new MailStoreTempDir();
        var path = GiveItACursor(tmp, Named, "m-01");

        using var lockHeld = new FileStream(path + ".lock", new FileStreamOptions
        {
            Mode = FileMode.OpenOrCreate,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
        });

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = MailReap.Run(["main@laptop-a"], stdout, stderr, tmp.Dir, lockWaitMs: 50);

        Assert.Equal(1, exit);
        Assert.True(File.Exists(path));                                // standing intact
        Assert.Contains("held by another writer", stderr.ToString());
        Assert.DoesNotContain(log.Events, e => e.Evt == "mail.reap");  // and no false record
    }

    // ---- the record --------------------------------------------------------

    /// `mail.reap` names the mailbox in the columns the trail already uses for
    /// that question (`role` + `instance`, d4's spelling), lists what was
    /// stranded, and names who decided. No `sessionId`: a reap has no window.
    [Fact]
    public void TheTrailRow_NamesTheMailboxWhatItHeldAndWhoReapedIt()
    {
        using var log = new CapturedLog();
        using var tmp = new MailStoreTempDir();
        GiveItACursor(tmp, Named, "m-01");
        CursorFixtures.AppendTo(tmp, "m-02");                          // arrives, never read
        Send(tmp, MailFixtures.Envelope(id: "m-03", to: "main@laptop-a", ttl: null));

        Assert.Equal(0, Reap(tmp, "main@laptop-a", "--by", "reaper@daemon").Exit);

        var row = Assert.Single(log.Events, e => e.Evt == "mail.reap");
        Assert.Equal("info", row.Lvl);
        Assert.Null(row.Fields.SessionId);
        Assert.Equal("main", row.Fields.Data!["role"]);
        Assert.Equal("laptop-a", row.Fields.Data["instance"]);
        Assert.Equal("reaper@daemon", row.Fields.Data["by"]);
        // Broadcast and unicast alike — both were waiting for THIS mailbox, and
        // the unicast one has nobody else it could ever reach.
        Assert.Equal(["m-02", "m-03"], (IEnumerable<string>)row.Fields.Data["pendingIds"]);
    }

    /// A bare role is the sessionless reader's own mailbox (d3 as amended: the
    /// cursor key IS the address), and its row carries a role alone — the same
    /// write-only-when-named rule `mail.cursorAdvance` uses, so a reader of the
    /// old columns learns nothing new. `by` is likewise absent when nobody was
    /// named, which is the honest record of a human at a terminal.
    [Fact]
    public void ABareRole_ReapsTheSessionlessMailbox_AndNamesNoInstanceOrReaper()
    {
        using var log = new CapturedLog();
        using var tmp = new MailStoreTempDir();
        var cursors = tmp.Cursors();
        var offset = CursorFixtures.AppendTo(tmp, "m-01");
        CursorFixtures.AdvanceOk(cursors, cursors.Pending(Sessionless, null), offset);
        var path = cursors.CursorPath("main", null);
        Assert.True(File.Exists(path));

        Assert.Equal(0, Reap(tmp, "main").Exit);

        Assert.False(File.Exists(path));
        var row = Assert.Single(log.Events, e => e.Evt == "mail.reap");
        Assert.DoesNotContain("instance", row.Fields.Data!.Keys);
        Assert.DoesNotContain("by", row.Fields.Data.Keys);
    }

    /// EXPIRED mail is not stranded mail. It was already spent — the next
    /// advance drops it — so listing it would report as lost what no digest
    /// was ever going to hand over again, and would send the reaper chasing it.
    [Fact]
    public void ExpiredMail_IsNotCountedAmongWhatTheMailboxWasHolding()
    {
        using var log = new CapturedLog();
        using var tmp = new MailStoreTempDir();
        var cursors = tmp.Cursors();
        CursorFixtures.AppendTo(tmp, "m-old", ttl: 1);                 // one opportunity
        CursorFixtures.AdvanceOk(cursors, cursors.Pending(Named, "s-1"));   // passed over: spent
        CursorFixtures.AppendTo(tmp, "m-new");

        var view = cursors.Pending(Named, "s-1");
        Assert.Equal(["m-old"], view.Expired.Select(p => p.Envelope.Id));

        Assert.Equal(0, Reap(tmp, "main@laptop-a").Exit);

        var row = Assert.Single(log.Events, e => e.Evt == "mail.reap");
        Assert.Equal(["m-new"], (IEnumerable<string>)row.Fields.Data!["pendingIds"]);
    }

    /// The delete is the COMMIT POINT: past it the mailbox is gone, and nothing
    /// downstream may report otherwise. A confirmation line into a closed pipe
    /// (`mail reap … | head -1`) is the reachable case — it must not become
    /// "cannot reap" on stderr, a nonzero exit, or a stack trace about a reap
    /// that actually happened.
    [Fact]
    public void ABrokenStdout_CannotTurnACompletedReapIntoAFailure()
    {
        using var log = new CapturedLog();
        using var tmp = new MailStoreTempDir();
        var path = GiveItACursor(tmp, Named, "m-01");

        var stderr = new StringWriter();
        var exit = MailReap.Run(["main@laptop-a"], new BrokenWriter(), stderr, tmp.Dir);

        Assert.Equal(0, exit);
        Assert.False(File.Exists(path));
        Assert.Empty(stderr.ToString());
        Assert.Single(log.Events, e => e.Evt == "mail.reap");   // and the record still stands
    }

    // ---- the arguments -----------------------------------------------------

    /// The address goes through `MailAddress.TryParse` — the envelope parser's
    /// own gate — so this verb cannot accept a spelling no sender could write,
    /// and the refusal teaches the same grammar every other seam teaches.
    [Theory]
    [InlineData("Main@laptop-a")]     // uppercase is pinned out, not folded
    [InlineData("main@")]             // empty half
    [InlineData("a@b@c")]             // two readings; refused, not guessed
    [InlineData("main laptop")]
    public void AnUngrammaticalAddress_IsRefusedWithTheGrammar(string address)
    {
        using var log = new CapturedLog();
        using var tmp = new MailStoreTempDir();

        var r = Reap(tmp, address);

        Assert.Equal(1, r.Exit);
        Assert.Contains("is not an address", r.Err);
        Assert.Contains("[a-z0-9][a-z0-9-]*", r.Err);
        Assert.Empty(r.Out);
    }

    /// `--by` is grammar-checked for the same reason the address is: a reaper
    /// spelled so no sender could reach it is a name on the ledger nobody can
    /// ask about what it did.
    [Fact]
    public void AnUngrammaticalBy_IsRefused()
    {
        using var log = new CapturedLog();
        using var tmp = new MailStoreTempDir();
        var path = GiveItACursor(tmp, Named, "m-01");

        var r = Reap(tmp, "main@laptop-a", "--by", "The Reaper");

        Assert.Equal(1, r.Exit);
        Assert.Contains("--by 'The Reaper' is not an address", r.Err);
        Assert.True(File.Exists(path));   // refused before anything was removed
    }

    /// Everything else a caller can get wrong, refused before any mailbox is
    /// touched. Two addresses is the one worth naming: reaping the first and
    /// ignoring the second is the silent half of a reaper looping wrong.
    [Theory]
    [InlineData(new string[0], "an address is required")]
    [InlineData(new[] { "main@a", "main@b" }, "exactly one address")]
    [InlineData(new[] { "main@a", "--by" }, "unknown or incomplete argument")]
    [InlineData(new[] { "main@a", "--force" }, "unknown or incomplete argument")]
    [InlineData(new[] { "main@a", "--by", "x", "--by", "y" }, "--by given twice")]
    public void ABadInvocation_IsRefusedAndTeachesTheUsage(string[] argv, string expected)
    {
        using var log = new CapturedLog();
        using var tmp = new MailStoreTempDir();

        var r = Reap(tmp, argv);

        Assert.Equal(1, r.Exit);
        Assert.Contains(expected, r.Err);
        Assert.Contains(MailReap.Usage, r.Err);
        Assert.DoesNotContain(log.Events, e => e.Evt == "mail.reap");
    }

    private static void Send(MailStoreTempDir tmp, MailEnvelope e) =>
        Assert.IsType<MailAppend.Appended>(tmp.Store().Append(e));

    /// A stdout that is gone, the way a closed pipe is gone.
    private sealed class BrokenWriter : StringWriter
    {
        public override void WriteLine(string? value) => throw new IOException("broken pipe");
    }
}
