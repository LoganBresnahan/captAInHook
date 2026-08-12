using System.Text;
using CaptainHook.Mail;

namespace CaptainHook.Tests;

/// ADR-0016 d3/d4/d6/d13 — the per-(role, session) delivery cursor (roadmap
/// item 20, phase 3). What must hold, in the roadmap's own words:
/// advance-on-inject is simultaneously the AT-MOST-ONCE guarantee and the
/// STOP-LOOP GUARD — too early loses mail, too late double-injects;
/// `deliveries` is the sole TTL clock; `gen` (and here, the chain head)
/// survives rotation; deletion re-anchors.
internal static class CursorFixtures
{
    public static MailCursors Cursors(this MailStoreTempDir tmp) => new(tmp.Store());

    public static long AppendTo(MailStoreTempDir tmp, string id, string to = "main",
        MailPriority priority = MailPriority.Ambient, int ttl = 3)
        => MailFixtures.AppendOk(tmp.Store(),
            MailFixtures.Envelope(id: id, to: to, priority: priority, ttl: ttl)).Offset;

    public static MailCursor AdvanceOk(MailCursors cursors, MailPendingView view, params long[] delivered)
    {
        var r = cursors.Advance(view, delivered);
        Assert.True(r is MailCursorWrite.Written, (r as MailCursorWrite.Failed)?.Error);
        return ((MailCursorWrite.Written)r).Cursor;
    }
}

// ---- anchoring and the pending walk ----------------------------------------

public class MailCursorAnchorTests
{
    /// First contact anchors at 0: STORE-AND-FORWARD is the point — mail sent
    /// to a role while nobody held it must reach the next session that does
    /// (the rejected in-daemon store's whole rationale, d4).
    [Fact]
    public void AFreshCursor_SeesAllRetainedMailForItsRole()
    {
        using var tmp = new MailStoreTempDir();
        CursorFixtures.AppendTo(tmp, "m-01");
        CursorFixtures.AppendTo(tmp, "m-02");

        var view = tmp.Cursors().Pending("main", "s-1");

        Assert.False(view.Reanchored);   // an anchor, not a re-anchor: nothing was lost
        Assert.Equal(new[] { "m-01", "m-02" }, view.Pending.Select(p => p.Envelope.Id).ToArray());
        Assert.All(view.Pending, p => Assert.Null(p.SeenAt));   // fresh: no TTL accrued
        Assert.Equal(0, view.Deliveries);
        Assert.Empty(view.Expired);
    }

    [Fact]
    public void AnotherRolesMail_IsInvisible()
    {
        using var tmp = new MailStoreTempDir();
        CursorFixtures.AppendTo(tmp, "m-01", to: "main");
        CursorFixtures.AppendTo(tmp, "m-02", to: "reviewer");
        CursorFixtures.AppendTo(tmp, "m-03", to: "main");

        var view = tmp.Cursors().Pending("main", "s-1");

        Assert.Equal(new[] { "m-01", "m-03" }, view.Pending.Select(p => p.Envelope.Id).ToArray());
        Assert.Equal(0, view.SkippedMalformed);   // other roles' mail is not "skipped" — it was never ours
    }

    /// d6 pinned: each live session holding the role has its OWN cursor and
    /// receives its own delivery — one session consuming mail must not consume
    /// it for the other.
    [Fact]
    public void TwoSessionsOfOneRole_HaveIndependentCursors()
    {
        using var tmp = new MailStoreTempDir();
        var cursors = tmp.Cursors();
        var o1 = CursorFixtures.AppendTo(tmp, "m-01");

        CursorFixtures.AdvanceOk(cursors, cursors.Pending("main", "s-1"), o1);

        Assert.Empty(cursors.Pending("main", "s-1").Pending);
        Assert.Single(cursors.Pending("main", "s-2").Pending);   // s-2 still gets its delivery
    }

    [Fact]
    public void AnEmptyOrAbsentStore_YieldsAnEmptyView()
    {
        using var tmp = new MailStoreTempDir();

        var view = tmp.Cursors().Pending("main", "s-1");

        Assert.Empty(view.Pending);
        Assert.Equal(0, view.Frontier);
        Assert.Null(view.Head);
    }

    /// A malformed line among pending is stepped over and COUNTED — the
    /// envelope's warn-and-skip direction, made visible so a digest can say
    /// "and 1 unreadable line" instead of silently narrowing the store.
    [Fact]
    public void AMalformedLine_IsSkippedAndCounted()
    {
        using var tmp = new MailStoreTempDir();
        CursorFixtures.AppendTo(tmp, "m-01");
        File.AppendAllText(tmp.FilePath, "not json at all\n");
        CursorFixtures.AppendTo(tmp, "m-02");

        var view = tmp.Cursors().Pending("main", "s-1");

        Assert.Equal(new[] { "m-01", "m-02" }, view.Pending.Select(p => p.Envelope.Id).ToArray());
        Assert.Equal(1, view.SkippedMalformed);
    }

    /// The frontier never enters an unterminated tail (TrailCursor's
    /// half-written-line rule): an append in flight is invisible this read —
    /// and fully delivered the next, from the SAME frontier.
    [Fact]
    public void AnUnterminatedTail_IsNotConsumedByTheFrontier()
    {
        using var tmp = new MailStoreTempDir();
        var cursors = tmp.Cursors();
        var o1 = CursorFixtures.AppendTo(tmp, "m-01");
        var torn = """{"v":1,"id":"m-02","ts":"2026-""";
        File.AppendAllText(tmp.FilePath, torn);

        var view = cursors.Pending("main", "s-1");
        Assert.Single(view.Pending);
        CursorFixtures.AdvanceOk(cursors, view, o1);

        // The "in-flight" append completes (here: terminated by a real append
        // whose write also terminates the torn bytes into a malformed line).
        CursorFixtures.AppendTo(tmp, "m-03");

        var next = cursors.Pending("main", "s-1");
        Assert.False(next.Reanchored);
        Assert.Equal("m-03", Assert.Single(next.Pending).Envelope.Id);
        Assert.Equal(1, next.SkippedMalformed);   // the torn line, now terminated garbage
    }

    /// Skeptic find pinned: a store that is ONLY a torn line (the very first
    /// append in flight) has no head yet — hashing the partial bytes would
    /// persist a head that "changes" when the append completes, firing a
    /// tamper-flavored "different chain" false alarm on a healthy store.
    [Fact]
    public void AStoreThatIsOnlyATornLine_HasNoHeadAndNoFalseAlarmLater()
    {
        using var tmp = new MailStoreTempDir();
        var cursors = tmp.Cursors();
        File.WriteAllText(tmp.FilePath, """{"v":1,"id":"m-01","ts":"2026-""");   // no terminator

        var view = cursors.Pending("main", "s-1");
        Assert.Null(view.Head);
        Assert.Equal(0, view.Frontier);
        Assert.Empty(view.Pending);
        CursorFixtures.AdvanceOk(cursors, view);   // persists the null head

        CursorFixtures.AppendTo(tmp, "m-02");      // terminates the torn line, appends real mail

        var next = cursors.Pending("main", "s-1");
        Assert.False(next.Reanchored);             // no "different chain" accusation
        Assert.Equal("m-02", Assert.Single(next.Pending).Envelope.Id);
        Assert.Equal(1, next.SkippedMalformed);    // the terminated torn line
    }
}

// ---- advance-on-inject: at-most-once and the Stop-loop guard ---------------

public class MailCursorAdvanceTests
{
    /// The STOP-LOOP GUARD in one test: once advanced over, delivered mail is
    /// pending for NO subsequent read — a reconcile turn re-blocks only on
    /// genuinely new inbound.
    [Fact]
    public void DeliveredMail_IsNeverPendingAgain()
    {
        using var tmp = new MailStoreTempDir();
        var cursors = tmp.Cursors();
        var o1 = CursorFixtures.AppendTo(tmp, "m-01");
        var o2 = CursorFixtures.AppendTo(tmp, "m-02");

        var cursor = CursorFixtures.AdvanceOk(cursors, cursors.Pending("main", "s-1"), o1, o2);

        Assert.Empty(cursors.Pending("main", "s-1").Pending);
        Assert.Equal("m-02", cursor.LastDeliveredId);
        Assert.Equal(1, cursor.Deliveries);

        var o3 = CursorFixtures.AppendTo(tmp, "m-03");
        Assert.Equal(o3, Assert.Single(cursors.Pending("main", "s-1").Pending).Offset);
    }

    /// THE test the frontier+held shape exists for: deliver out of file order
    /// (the mid-turn seam delivering urgent past held ambient). A bare offset
    /// must either lose the held line or double the delivered one; this must
    /// do neither.
    [Fact]
    public void DeliveringPastHeldMail_LosesNothingAndDoublesNothing()
    {
        using var tmp = new MailStoreTempDir();
        var cursors = tmp.Cursors();
        var oAmbient = CursorFixtures.AppendTo(tmp, "m-amb", priority: MailPriority.Ambient);
        var oUrgent = CursorFixtures.AppendTo(tmp, "m-urg", priority: MailPriority.Urgent);

        // Mid-turn: deliver the urgent line only; the ambient one is passed over.
        CursorFixtures.AdvanceOk(cursors, cursors.Pending("main", "s-1"), oUrgent);

        var view = cursors.Pending("main", "s-1");
        var held = Assert.Single(view.Pending);
        Assert.Equal("m-amb", held.Envelope.Id);        // too early would have lost this
        Assert.Equal(oAmbient, held.Offset);
        Assert.Equal(1, held.SeenAt);                    // stamped at the opportunity that passed it
        Assert.DoesNotContain(view.Pending, p => p.Envelope.Id == "m-urg");   // too late would double this

        // Turn start: the held ambient line delivers; nothing remains.
        CursorFixtures.AdvanceOk(cursors, view, oAmbient);
        Assert.Empty(cursors.Pending("main", "s-1").Pending);
    }

    /// Advancing over an offset that is not pending is refused outright — it
    /// is either a double (already behind the frontier) or an invention, and
    /// both are the corruption the cursor exists to prevent.
    [Fact]
    public void Advance_RefusesAnOffsetThatIsNotPending()
    {
        using var tmp = new MailStoreTempDir();
        var cursors = tmp.Cursors();
        var o1 = CursorFixtures.AppendTo(tmp, "m-01");

        var view = cursors.Pending("main", "s-1");
        CursorFixtures.AdvanceOk(cursors, view, o1);

        // Replaying the SAME view after its mail was consumed: refuse.
        var replay = cursors.Advance(view, [o1]);
        Assert.IsType<MailCursorWrite.Failed>(replay);

        var fresh = cursors.Advance(cursors.Pending("main", "s-1"), [999_999L]);
        var failed = Assert.IsType<MailCursorWrite.Failed>(fresh);
        Assert.Contains("not pending", failed.Error);
    }

    /// Mail arriving BETWEEN the read and the advance survives: the frontier
    /// comes from the view, never from the file's current length, so bytes
    /// this view never saw stay ahead of the cursor.
    [Fact]
    public void MailArrivingAfterTheRead_IsNotConsumedByTheAdvance()
    {
        using var tmp = new MailStoreTempDir();
        var cursors = tmp.Cursors();
        var o1 = CursorFixtures.AppendTo(tmp, "m-01");
        var view = cursors.Pending("main", "s-1");

        CursorFixtures.AppendTo(tmp, "m-02");            // lands after the read
        CursorFixtures.AdvanceOk(cursors, view, o1);

        Assert.Equal("m-02", Assert.Single(cursors.Pending("main", "s-1").Pending).Envelope.Id);
    }

    /// Skeptic find (this slice's verify pass): the staleness guard is a
    /// read-then-write, so Advance runs it under a per-cursor flock — the
    /// same discipline the store's append documents as "a CORRECTNESS
    /// requirement, not a nicety". A held lock fails the advance bounded and
    /// loud, with nothing written; released, delivery proceeds.
    [Fact]
    public void AHeldCursorLock_FailsTheAdvanceBoundedAndWritesNothing()
    {
        using var tmp = new MailStoreTempDir();
        var cursors = tmp.Cursors();
        var o1 = CursorFixtures.AppendTo(tmp, "m-01");
        var view = cursors.Pending("main", "s-1");
        var lockPath = cursors.CursorPath("main", "s-1") + ".lock";

        using (var _ = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = cursors.Advance(view, [o1], lockWaitMs: 250);
            sw.Stop();

            var failed = Assert.IsType<MailCursorWrite.Failed>(result);
            Assert.Contains("lock", failed.Error, StringComparison.OrdinalIgnoreCase);
            Assert.InRange(sw.ElapsedMilliseconds, 200, 5_000);
            Assert.False(File.Exists(cursors.CursorPath("main", "s-1")));
        }

        CursorFixtures.AdvanceOk(cursors, view, o1);   // released: back to normal
    }

    /// The cursor file is 0600 (d13) and written by atomic rename — no
    /// partially written cursor is ever observable at the path.
    [Fact]
    public void TheCursorFile_IsPrivateAndParsesBack()
    {
        using var tmp = new MailStoreTempDir();
        var cursors = tmp.Cursors();
        var o1 = CursorFixtures.AppendTo(tmp, "m-01");

        var written = CursorFixtures.AdvanceOk(cursors, cursors.Pending("main", "s-1"), o1);
        var path = cursors.CursorPath("main", "s-1");

        Assert.True(File.Exists(path));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        var reparsed = MailCursor.TryParse(File.ReadAllText(path), out var errors);
        Assert.Empty(errors);
        Assert.Equal(written.Render(), reparsed!.Render());   // structural: Held is a list
    }
}

// ---- the TTL clock ---------------------------------------------------------

public class MailCursorTtlTests
{
    /// d3, exactly: ttlDeliveries N = "drop after being passed over at N
    /// seams". ttl=2 mail survives one pass-over, is offered once more, and
    /// after its second pass-over is EXPIRED — reported once, then dropped by
    /// the next advance.
    [Fact]
    public void MailExpires_AfterItsTtlInPassedOverOpportunities()
    {
        using var tmp = new MailStoreTempDir();
        var cursors = tmp.Cursors();
        CursorFixtures.AppendTo(tmp, "m-01", ttl: 2);

        // Opportunity 1: passed over (nothing delivered).
        CursorFixtures.AdvanceOk(cursors, cursors.Pending("main", "s-1"));
        var afterOne = cursors.Pending("main", "s-1");
        Assert.Equal("m-01", Assert.Single(afterOne.Pending).Envelope.Id);   // survived: 1 < 2
        Assert.Empty(afterOne.Expired);

        // Opportunity 2: passed over again — its second and last.
        CursorFixtures.AdvanceOk(cursors, afterOne);
        var afterTwo = cursors.Pending("main", "s-1");
        Assert.Empty(afterTwo.Pending);
        Assert.Equal("m-01", Assert.Single(afterTwo.Expired).Envelope.Id);   // spent: reported once

        // The advance that logs mail.expire also drops it for good.
        CursorFixtures.AdvanceOk(cursors, afterTwo);
        var final = cursors.Pending("main", "s-1");
        Assert.Empty(final.Pending);
        Assert.Empty(final.Expired);
    }

    /// ttl=1 is "first opportunity or never": one pass-over and it is spent.
    [Fact]
    public void TtlOne_ExpiresOnTheFirstPassOver()
    {
        using var tmp = new MailStoreTempDir();
        var cursors = tmp.Cursors();
        CursorFixtures.AppendTo(tmp, "m-01", ttl: 1);

        CursorFixtures.AdvanceOk(cursors, cursors.Pending("main", "s-1"));

        var view = cursors.Pending("main", "s-1");
        Assert.Empty(view.Pending);
        Assert.Equal("m-01", Assert.Single(view.Expired).Envelope.Id);
    }

    /// `deliveries` is the SOLE clock: with no opportunities, mail accrues no
    /// staleness — however many times it is merely LOOKED at. The read is not
    /// the opportunity; the advance is.
    [Fact]
    public void ReadingWithoutAdvancing_AgesNothing()
    {
        using var tmp = new MailStoreTempDir();
        var cursors = tmp.Cursors();
        CursorFixtures.AppendTo(tmp, "m-01", ttl: 1);

        for (var i = 0; i < 5; i++)
            Assert.Single(cursors.Pending("main", "s-1").Pending);   // still fresh every time

        Assert.Empty(cursors.Pending("main", "s-1").Expired);
    }

    /// An expired envelope cannot be delivered — it is not in Pending, and
    /// Advance refuses offsets that are not pending. Expiry is a one-way door
    /// WHILE THE CURSOR LIVES; a re-anchor resurrects it (the pin below).
    [Fact]
    public void ExpiredMail_CannotBeDelivered()
    {
        using var tmp = new MailStoreTempDir();
        var cursors = tmp.Cursors();
        var o1 = CursorFixtures.AppendTo(tmp, "m-01", ttl: 1);
        CursorFixtures.AdvanceOk(cursors, cursors.Pending("main", "s-1"));

        var view = cursors.Pending("main", "s-1");
        Assert.Single(view.Expired);

        Assert.IsType<MailCursorWrite.Failed>(cursors.Advance(view, [o1]));
    }

    /// Skeptic find (this slice's verify pass), pinned so the choice is
    /// deliberate: a re-anchor RESURRECTS expired mail with a fresh TTL — the
    /// seenAt stamps die with the held list even though the deliveries clock
    /// survives. d13's redelivery cost includes it; the direction is the
    /// accepted one (redeliver, never silently lose).
    [Fact]
    public void AReanchor_ResurrectsExpiredMail_TheStatedCost()
    {
        using var tmp = new MailStoreTempDir();
        var cursors = tmp.Cursors();
        CursorFixtures.AppendTo(tmp, "m-01", ttl: 1);
        CursorFixtures.AdvanceOk(cursors, cursors.Pending("main", "s-1"));   // passed over: spent

        Assert.Single(cursors.Pending("main", "s-1").Expired);
        File.Delete(cursors.CursorPath("main", "s-1"));

        var view = cursors.Pending("main", "s-1");
        var back = Assert.Single(view.Pending);
        Assert.Equal("m-01", back.Envelope.Id);
        Assert.Null(back.SeenAt);            // fresh again: the TTL restarted
        Assert.Empty(view.Expired);
    }

    /// The wall clock appears NOWHERE in the cursor file — house invariant 2
    /// at the delivery layer, asserted on the bytes.
    [Fact]
    public void TheCursorFile_CarriesNoTimestamp()
    {
        using var tmp = new MailStoreTempDir();
        var cursors = tmp.Cursors();
        var o1 = CursorFixtures.AppendTo(tmp, "m-01");
        CursorFixtures.AdvanceOk(cursors, cursors.Pending("main", "s-1"), o1);

        var text = File.ReadAllText(cursors.CursorPath("main", "s-1"));
        Assert.DoesNotContain("ts", text.Replace("\"lastDeliveredId\"", ""));
        Assert.DoesNotContain("time", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("date", text, StringComparison.OrdinalIgnoreCase);
    }
}

// ---- re-anchor semantics ---------------------------------------------------

public class MailCursorReanchorTests
{
    private static (MailStoreTempDir Tmp, MailCursors Cursors, string Path) DeliveredOne(
        string id = "m-01")
    {
        var tmp = new MailStoreTempDir();
        var cursors = tmp.Cursors();
        var o = CursorFixtures.AppendTo(tmp, id);
        CursorFixtures.AdvanceOk(cursors, cursors.Pending("main", "s-1"), o);
        return (tmp, cursors, cursors.CursorPath("main", "s-1"));
    }

    /// d13, verbatim: cursors are deletable anytime — a deleted cursor just
    /// re-anchors. The cost is redelivery of consumed mail, and that is the
    /// SAFE direction: the alternative to redelivering is guessing a
    /// frontier, and a guessed frontier loses mail silently.
    [Fact]
    public void ADeletedCursor_ReanchorsAndRedelivers()
    {
        var (tmp, cursors, path) = DeliveredOne();
        using var _ = tmp;
        File.Delete(path);

        var view = cursors.Pending("main", "s-1");

        Assert.False(view.Reanchored);   // absence is a fresh ANCHOR, not an anomaly
        Assert.Equal("m-01", Assert.Single(view.Pending).Envelope.Id);
        Assert.Equal(0, view.Deliveries);
    }

    /// Malformed cursor bytes re-anchor LOUDLY — and never throw. The TTL
    /// clock restarts too: unreadable bytes vouch for nothing.
    [Fact]
    public void AMalformedCursor_ReanchorsLoudly()
    {
        var (tmp, cursors, path) = DeliveredOne();
        using var _ = tmp;
        File.WriteAllText(path, "{ definitely not a cursor");

        var view = cursors.Pending("main", "s-1");

        Assert.True(view.Reanchored);
        Assert.Contains("malformed", view.ReanchorReason);
        Assert.Single(view.Pending);
        Assert.Equal(0, view.Deliveries);
    }

    /// An unknown field in a cursor is malformed (strict never-guess — the
    /// same rule as every other file this project parses).
    [Fact]
    public void AnUnknownCursorField_IsMalformed()
    {
        var parsed = MailCursor.TryParse(
            """{"v":1,"gen":1,"offset":0,"deliveries":0,"surprise":true}""", out var errors);

        Assert.Null(parsed);
        Assert.Contains(errors, e => e.Contains("surprise"));
    }

    /// The store REPLACED under the cursor (a different chain at the same
    /// path — which is also what a rotation looks like): head mismatch,
    /// re-anchor, and the monotonic deliveries counter SURVIVES — the clock
    /// outlives the position.
    [Fact]
    public void AReplacedChain_ReanchorsPreservingTheDeliveriesCounter()
    {
        var (tmp, cursors, _) = DeliveredOne();
        using var _1 = tmp;

        File.Delete(tmp.FilePath);                       // rotation stand-in:
        CursorFixtures.AppendTo(tmp, "m-new");           // new file, new chain

        var view = cursors.Pending("main", "s-1");

        Assert.True(view.Reanchored);
        Assert.Contains("chain", view.ReanchorReason);
        Assert.Equal("m-new", Assert.Single(view.Pending).Envelope.Id);
        Assert.Equal(1, view.Deliveries);                // preserved, not reset
    }

    /// Truncation-reset: an offset past the frontier means the store shrank —
    /// every position this cursor knew is invalid. (Truncating back to
    /// exactly the frontier is deliberately NOT a re-anchor: it is
    /// indistinguishable from "no new mail", phase 2's honest statement about
    /// tail truncation — only a frontier the file cannot reach proves loss.)
    [Fact]
    public void ATruncatedStore_Reanchors()
    {
        using var tmp = new MailStoreTempDir();
        var cursors = tmp.Cursors();
        var o1 = CursorFixtures.AppendTo(tmp, "m-01");
        var o2 = CursorFixtures.AppendTo(tmp, "m-02");
        CursorFixtures.AdvanceOk(cursors, cursors.Pending("main", "s-1"), o1, o2);

        // Truncate back to the first line only — BELOW the frontier, same
        // head, so only the truncation check can catch it.
        var first = tmp.Lines()[0];
        File.WriteAllText(tmp.FilePath, first + "\n");

        var view = cursors.Pending("main", "s-1");

        Assert.True(view.Reanchored);
        Assert.Contains("truncated", view.ReanchorReason);
        Assert.Equal("m-01", Assert.Single(view.Pending).Envelope.Id);   // redelivery, the stated cost
    }

    /// Alignment-self-heal: a cursor offset resting on no line boundary was
    /// written against bytes that are gone. Hand-build the cursor (same head,
    /// offset mid-line) so alignment is the ONLY check that can catch it.
    [Fact]
    public void AMisalignedOffset_Reanchors()
    {
        var (tmp, cursors, path) = DeliveredOne();
        using var owned = tmp;

        var cursor = MailCursor.TryParse(File.ReadAllText(path), out _)!;
        File.WriteAllText(path, (cursor with { Offset = cursor.Offset - 3 }).Render());

        var view = cursors.Pending("main", "s-1");

        Assert.True(view.Reanchored);
        Assert.Contains("boundary", view.ReanchorReason);
    }

    /// A held entry the file contradicts — the line at its offset now carries
    /// a different id — is a changed file, not a substitutable delivery.
    /// (The tampered line is a MIDDLE line: the head hash stays intact, so
    /// only the held-id check can catch it.)
    [Fact]
    public void AHeldEntryTheFileContradicts_Reanchors()
    {
        using var tmp = new MailStoreTempDir();
        var cursors = tmp.Cursors();
        var o0 = CursorFixtures.AppendTo(tmp, "m-00");
        CursorFixtures.AppendTo(tmp, "m-01");
        var o2 = CursorFixtures.AppendTo(tmp, "m-02");
        CursorFixtures.AdvanceOk(cursors, cursors.Pending("main", "s-1"), o0, o2);   // m-01 held

        // Rewrite the SECOND line in place, same length: id m-01 → m-XX.
        var lines = tmp.Lines();
        File.WriteAllText(tmp.FilePath,
            string.Join('\n', lines.Select(l => l.Replace("\"m-01\"", "\"m-XX\""))) + "\n");

        var view = cursors.Pending("main", "s-1");

        Assert.True(view.Reanchored);
        Assert.Contains("no longer matches", view.ReanchorReason);
    }

    /// A gen the store does not report re-anchors, deliveries preserved —
    /// "gen survives rotation" (hand-built: no rotation machinery exists yet
    /// to produce one naturally).
    [Fact]
    public void AForeignGen_ReanchorsPreservingDeliveries()
    {
        var (tmp, cursors, path) = DeliveredOne();
        using var owned = tmp;

        var cursor = MailCursor.TryParse(File.ReadAllText(path), out _)!;
        File.WriteAllText(path, (cursor with { Gen = 4 }).Render());

        var view = cursors.Pending("main", "s-1");

        Assert.True(view.Reanchored);
        Assert.Contains("rotated", view.ReanchorReason);
        Assert.Equal(1, view.Deliveries);
    }

    /// Skeptic find pinned: duplicate held OFFSETS in a parseable cursor are
    /// MALFORMED (one position is one envelope; listing it twice would render
    /// it twice in one digest — a double-inject needing no race), and the
    /// advance from the re-anchored view never throws.
    [Fact]
    public void ADuplicateHeldOffset_IsMalformedAndReanchors()
    {
        using var tmp = new MailStoreTempDir();
        var cursors = tmp.Cursors();
        var o1 = CursorFixtures.AppendTo(tmp, "m-01");
        var o2 = CursorFixtures.AppendTo(tmp, "m-02");
        CursorFixtures.AdvanceOk(cursors, cursors.Pending("main", "s-1"), o2);   // m-01 held

        var path = cursors.CursorPath("main", "s-1");
        var cursor = MailCursor.TryParse(File.ReadAllText(path), out _)!;
        var dup = cursor.Held[0];
        File.WriteAllText(path, (cursor with { Held = new[] { dup, dup } }).Render());

        var view = cursors.Pending("main", "s-1");

        Assert.True(view.Reanchored);
        Assert.Contains("duplicate held offset", view.ReanchorReason);
        Assert.Equal(2, view.Pending.Count);                     // once each, never twice
        Assert.Equal(2, view.Pending.Select(p => p.Offset).Distinct().Count());
        CursorFixtures.AdvanceOk(cursors, view, o1, o2);         // and Advance survives it
    }

    /// A held entry naming another role's line (right id, wrong recipient) is
    /// a changed file or a hand edit — re-anchor, never a substituted
    /// delivery into the wrong context.
    [Fact]
    public void AHeldEntryForAnotherRole_Reanchors()
    {
        using var tmp = new MailStoreTempDir();
        var cursors = tmp.Cursors();
        var oR = CursorFixtures.AppendTo(tmp, "m-r", to: "reviewer");
        var o1 = CursorFixtures.AppendTo(tmp, "m-01");
        CursorFixtures.AdvanceOk(cursors, cursors.Pending("main", "s-1"), o1);

        var path = cursors.CursorPath("main", "s-1");
        var cursor = MailCursor.TryParse(File.ReadAllText(path), out _)!;
        File.WriteAllText(path,
            (cursor with { Held = new[] { new MailHeld(oR, "m-r", 1) } }).Render());

        var view = cursors.Pending("main", "s-1");

        Assert.True(view.Reanchored);
        Assert.Contains("addressed to 'reviewer'", view.ReanchorReason);
        Assert.DoesNotContain(view.Pending, p => p.Envelope.Id == "m-r" && p.SeenAt is not null);
    }

    /// Re-anchoring is not a dead end: the next advance writes a valid cursor
    /// and delivery proceeds normally from it.
    [Fact]
    public void AfterAReanchor_DeliveryProceedsNormally()
    {
        var (tmp, cursors, path) = DeliveredOne();
        using var _ = tmp;
        File.WriteAllText(path, "garbage");

        var view = cursors.Pending("main", "s-1");
        Assert.True(view.Reanchored);
        CursorFixtures.AdvanceOk(cursors, view, view.Pending.Single().Offset);

        var after = cursors.Pending("main", "s-1");
        Assert.False(after.Reanchored);
        Assert.Empty(after.Pending);
    }
}

// ---- naming ----------------------------------------------------------------

public class MailCursorNamingTests
{
    /// Role and session are PERCENT-ENCODED into the filename: an arbitrary
    /// role can never escape the mail directory, eat the `.` separators, or
    /// collide with a different role that encodes near it.
    [Fact]
    public void CursorPaths_AreSafeAndCollisionFree()
    {
        using var tmp = new MailStoreTempDir();
        var cursors = tmp.Cursors();

        var traversal = cursors.CursorPath("../../etc/passwd", "s");
        Assert.StartsWith(tmp.Dir, traversal);
        Assert.DoesNotContain("..", Path.GetFileName(traversal));

        Assert.NotEqual(cursors.CursorPath("a.b", "c"), cursors.CursorPath("a", "b.c"));
        Assert.NotEqual(cursors.CursorPath("a%2Eb", "c"), cursors.CursorPath("a.b", "c"));
        Assert.Equal(cursors.CursorPath("main", "s-1"), cursors.CursorPath("main", "s-1"));
    }

    [Fact]
    public void ANullSession_HasItsOwnDistinctCursor()
    {
        using var tmp = new MailStoreTempDir();
        var cursors = tmp.Cursors();

        Assert.NotEqual(cursors.CursorPath("main", null), cursors.CursorPath("main", "s-1"));
        // "" normalizes to null: two spellings of "no session" must not be
        // two cursors sharing one file (skeptic find).
        Assert.Equal(cursors.CursorPath("main", null), cursors.CursorPath("main", ""));
        CursorFixtures.AppendTo(tmp, "m-01");
        var o = cursors.Pending("main", null).Pending.Single().Offset;
        CursorFixtures.AdvanceOk(cursors, cursors.Pending("main", null), o);

        Assert.Empty(cursors.Pending("main", null).Pending);
        Assert.Single(cursors.Pending("main", "s-1").Pending);
    }
}
