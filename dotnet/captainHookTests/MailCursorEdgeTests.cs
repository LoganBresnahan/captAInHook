using System.Text;
using CaptainHook.Mail;
using static CaptainHook.Tests.TestUtil;

namespace CaptainHook.Tests;

// ADR-0016 phase 5, the `cursor-edge-adversarial-tests` slice (roadmap item
// 20): the campaign the dogfood gate waits on. Everything here is an
// INTERLEAVING or a hostile store state — two digests racing one mailbox,
// senders racing the drain, the store replaced or truncated between a read
// and its advance, a foreign writer forcing what the write gate refuses —
// driven through the real files and the real locks, never simulated by
// calling methods in a polite order. The invariant under attack is always
// the same pair: mail is delivered EXACTLY ONCE per (role, session) cursor
// (a double-inject is the Stop-loop hazard; a silent drop is worse), and
// every deviation the design accepts (redelivery after cursor loss,
// loss-not-doubling at a crash) happens LOUDLY in the stated direction —
// with ONE pinned exception the skeptic pass forced into words: a cursor
// deleted mid-race at deliveries 0 doubles QUIETLY, because that deletion is
// indistinguishable from first contact (MailCursorDeletionRaceTests states
// the corner and pins the loud variant beside it).

internal static class EdgeFixtures
{
    public static (int Exit, string Out, string Err) RunDigest(
        string mailDir, string stdin, params string[] argv)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = MailDigest.Run(
            argv.Length == 0 ? ["--role", "main"] : argv,
            new StringReader(stdin), stdout, stderr,
            mailDir: mailDir, harnessDir: NoHarnessDir());
        return (exit, stdout.ToString(), stderr.ToString());
    }

    public static System.Text.Json.JsonElement Answer(string stdoutText) =>
        System.Text.Json.JsonDocument.Parse(stdoutText.TrimEnd('\n')).RootElement.Clone();

    public static string? Effect(string stdoutText) =>
        Answer(stdoutText).GetProperty("effect").GetString();

    public static string InjectText(string stdoutText)
    {
        var a = Answer(stdoutText);
        Assert.Equal("inject", a.GetProperty("effect").GetString());
        return a.GetProperty("text").GetString()!;
    }

    /// Occurrences of an envelope id AS A DIGEST HEAD FIELD: the `· id X ·`
    /// spelling ItemBlock pins (so `xo-1` never matches inside `xo-10`),
    /// counted only on HEAD lines — ItemBlock indents every body line with
    /// three spaces and heads never are, so a body that happens to contain
    /// the literal needle cannot fake a delivery (the skeptic pass's find:
    /// the raw substring count could).
    public static int Deliveries(IEnumerable<string> digestTexts, string id)
    {
        var needle = $"· id {id} ·";
        var count = 0;
        foreach (var line in digestTexts.SelectMany(t => t.Split('\n')))
        {
            if (line.StartsWith("   ", StringComparison.Ordinal)) continue;   // body line
            var at = 0;
            while ((at = line.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
            {
                count++;
                at += needle.Length;
            }
        }
        return count;
    }
}

// ---- the head rule has exactly one spelling --------------------------------

public class MailHeadAgreementTests
{
    /// `Advance` re-checks the store's identity through `HeadHash`; `Pending`
    /// records it from its full read. The two MUST agree in every store state,
    /// because a drift either refuses every advance (guard fires on healthy
    /// stores) or never fires (the chain-changed guard silently dead). This is
    /// the tripwire that keeps the one rule from becoming two.
    [Fact]
    public void HeadHash_AgreesWithPendingsHeadRule_InEveryStoreState()
    {
        using var tmp = new MailStoreTempDir();
        var store = tmp.Store();
        var cursors = tmp.Cursors();

        // Absent store.
        Assert.Null(store.HeadHash());
        Assert.Null(cursors.Pending("main", "s-1").Head);

        // Empty file.
        File.WriteAllText(tmp.FilePath, "");
        Assert.Null(store.HeadHash());
        Assert.Null(cursors.Pending("main", "s-1").Head);

        // A bare newline: one COMPLETE empty line — both rules hash the empty
        // span, and they must agree here too (the consecutive-newline edge
        // the store pass traced, met by the head rule).
        File.WriteAllText(tmp.FilePath, "\n");
        Assert.NotNull(store.HeadHash());
        Assert.Equal(store.HeadHash(), cursors.Pending("main", "s-1").Head);

        // Torn-only store: the first append still in flight has no head yet.
        File.WriteAllText(tmp.FilePath, """{"v":1,"id":"m-01","ts":"2026-""");
        Assert.Null(store.HeadHash());
        Assert.Null(cursors.Pending("main", "s-1").Head);

        // A real append terminates the torn line; both rules now name the
        // SAME first complete line.
        MailFixtures.AppendOk(store, MailFixtures.Envelope(id: "m-02"));
        var head = store.HeadHash();
        Assert.NotNull(head);
        Assert.Equal(head, cursors.Pending("main", "s-1").Head);
    }

    /// A first line larger than HeadHash's read chunk — here genuinely past
    /// the Append cap, which only a FOREIGN writer can produce — must hash
    /// WHOLE: the same grow-the-window reasoning TailLink pins for the other
    /// end of the file. (The skeptic pass caught the first cut of this test
    /// using a line Append would happily have written.)
    [Fact]
    public void HeadHash_HashesAChunkSpanningFirstLineWhole()
    {
        using var tmp = new MailStoreTempDir();
        var store = tmp.Store();
        var big = MailStore.Render(
            MailFixtures.Envelope(id: "m-big", body: new string('x', 200_000)), MailStore.Genesis);
        Assert.True(Encoding.UTF8.GetByteCount(big) + 1 > MailStore.MaxLineBytes);
        File.WriteAllText(tmp.FilePath, big + "\n");

        Assert.Equal(MailStore.HashOf(Encoding.UTF8.GetBytes(big)), store.HeadHash());
        Assert.Equal(store.HeadHash(), tmp.Cursors().Pending("main", "s-1").Head);
    }
}

// ---- exactly-once under real races -----------------------------------------

public class MailExactlyOnceRaceTests
{
    /// Two advances from ONE view, genuinely concurrent (two MailCursors over
    /// two MailStore instances, a barrier start, the real per-cursor flock):
    /// exactly one Written, exactly one Failed, every round. The lock + the
    /// staleness guard together are the at-most-once backstop — remove either
    /// and both advances succeed, which is the double-inject.
    [Fact]
    public void TwoConcurrentAdvances_FromOneView_ExactlyOneWins()
    {
        using var tmp = new MailStoreTempDir();
        for (var round = 0; round < 8; round++)
        {
            var role = $"race-{round}";
            var offset = CursorFixtures.AppendTo(tmp, $"m-{round}", to: role);
            var view = tmp.Cursors().Pending(role, "s-1");

            using var barrier = new Barrier(2);
            var results = new MailCursorWrite[2];
            var contenders = Enumerable.Range(0, 2).Select(i => Task.Run(() =>
            {
                var own = new MailCursors(new MailStore(tmp.Dir));   // its own handles, like a second process
                Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(10)));
                results[i] = own.Advance(view, [offset]);
            })).ToArray();
            Assert.True(Task.WaitAll(contenders, TimeSpan.FromSeconds(30)));

            Assert.Equal(1, results.Count(r => r is MailCursorWrite.Written));
            Assert.Equal(1, results.Count(r => r is MailCursorWrite.Failed));
            Assert.Empty(tmp.Cursors().Pending(role, "s-1").Pending);   // delivered once, gone for good
        }
    }

    /// The same race one layer up, through the REAL verb: two full digest
    /// runs for one (role, session) racing from the same store state. Exactly
    /// one answers inject; the other answers noop (its advance was refused —
    /// a legitimate concurrent delivery, not a defect); the ledger claims
    /// exactly one delivery; nothing is pending afterwards.
    [Fact]
    public void TwoConcurrentDigests_ExactlyOneDelivers_TheOtherNoops()
    {
        using var log = new CapturedLog();
        using var tmp = new MailStoreTempDir();
        MailFixtures.AppendOk(tmp.Store(), MailFixtures.Envelope(id: "m-1"));

        using var barrier = new Barrier(2);
        var outs = new string[2];
        var digests = Enumerable.Range(0, 2).Select(i => Task.Run(() =>
        {
            Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(10)));
            var (exit, stdout, _) = EdgeFixtures.RunDigest(
                tmp.Dir, DigestFixtures.Request(dispatchId: $"d-{i}"));
            Assert.Equal(0, exit);
            outs[i] = stdout;
        })).ToArray();
        Assert.True(Task.WaitAll(digests, TimeSpan.FromSeconds(30)));

        var effects = outs.Select(EdgeFixtures.Effect).OrderBy(e => e).ToArray();
        Assert.Equal(new[] { "inject", "noop" }, effects);
        Assert.Single(log.Events, e => e.Evt == "mail.deliver");

        var cursor = MailCursor.TryParse(
            File.ReadAllText(tmp.Cursors().CursorPath("main", "s-1")), out _)!;
        Assert.Equal(1, cursor.Deliveries);
        Assert.Empty(tmp.Cursors().Pending("main", "s-1").Pending);
    }

    /// The drain soak the dogfood gate names: senders appending concurrently
    /// (real flock contention on the store) while two digests race to drain
    /// the same (role, session). At the end every envelope was rendered into
    /// exactly ONE digest — none lost, none doubled — and the chain verifies
    /// clean. This is "exactly-once" as a property of the whole read/write
    /// path, not of any one method.
    [Fact]
    public void SendersRacingTwoDigests_EveryEnvelopeDeliversExactlyOnce()
    {
        using var tmp = new MailStoreTempDir();
        const string role = "drain";
        const int senders = 3, perSender = 20;
        var allIds = (from s in Enumerable.Range(0, senders)
                      from i in Enumerable.Range(0, perSender)
                      select $"xo-{s}-{i}").ToArray();

        var sendersDone = 0;
        var sendTasks = Enumerable.Range(0, senders).Select(s => Task.Run(() =>
        {
            var own = new MailStore(tmp.Dir);
            for (var i = 0; i < perSender; i++)
                MailFixtures.AppendOk(own, MailFixtures.Envelope(
                    id: $"xo-{s}-{i}", to: role, body: "payload", ttl: 50));
            Interlocked.Increment(ref sendersDone);
        })).ToArray();

        var texts = new System.Collections.Concurrent.ConcurrentBag<string>();
        var digestTasks = Enumerable.Range(0, 2).Select(d => Task.Run(() =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();   // monotonic deadline, house invariant 2
            while (sw.ElapsedMilliseconds < 30_000)
            {
                var (exit, stdout, _) = EdgeFixtures.RunDigest(
                    tmp.Dir, DigestFixtures.Request(dispatchId: $"d-{d}"),
                    "--role", role, "--max-chars", "1000000");
                Assert.Equal(0, exit);
                if (EdgeFixtures.Effect(stdout) == "inject")
                {
                    texts.Add(EdgeFixtures.InjectText(stdout));
                    continue;
                }
                // noop with all senders finished: either drained, or the OTHER
                // digest won the advance for what we saw (it recorded it).
                if (Volatile.Read(ref sendersDone) == senders) break;
            }
        })).ToArray();

        Assert.True(Task.WaitAll([.. sendTasks, .. digestTasks], TimeSpan.FromSeconds(60)));

        // Sweep until a genuine noop: both digests can exit on noops whose
        // reads predate the last appends (the skeptic pass traced the hole),
        // so ONE final run only happens to suffice under this fixture's
        // ambient-seam/huge-cap settings — loop-until-drained states the
        // termination condition instead of leaning on them.
        var drained = false;
        for (var sweep = 0; sweep < 10 && !drained; sweep++)
        {
            var (finalExit, finalOut, _) = EdgeFixtures.RunDigest(
                tmp.Dir, DigestFixtures.Request(dispatchId: $"d-final-{sweep}"),
                "--role", role, "--max-chars", "1000000");
            Assert.Equal(0, finalExit);
            if (EdgeFixtures.Effect(finalOut) == "inject") texts.Add(EdgeFixtures.InjectText(finalOut));
            else drained = true;
        }
        Assert.True(drained, "the store did not drain to a noop within 10 sweeps");

        foreach (var id in allIds)
            Assert.Equal(1, EdgeFixtures.Deliveries(texts, id));
        Assert.Empty(tmp.Cursors().Pending(role, "s-1").Pending);
        Assert.Empty(tmp.Store().VerifyChain());
    }
}

// ---- cursor deletion meets the race window ---------------------------------

public class MailCursorDeletionRaceTests
{
    /// The skeptic pass's find, pinned as the STATED cost rather than left
    /// implicit: d13 makes the cursor deletable anytime, and a deletion
    /// landing between two racing digests' reads and advances lets BOTH
    /// advance — the second sees no cursor, and no-cursor at deliveries 0 is
    /// byte-for-byte indistinguishable from first contact, so no guard can
    /// refuse it and nothing can be loud about it. This is the one corner
    /// where the deletion cost is a QUIET double; accepting it here, with the
    /// interleaving spelled out, is what keeps someone from "fixing" it later
    /// with a guard that would also refuse every legitimate first contact.
    [Fact]
    public void ACursorDeletedMidRace_AtDeliveriesZero_DoublesQuietly_TheStatedCost()
    {
        using var tmp = new MailStoreTempDir();
        var offset = CursorFixtures.AppendTo(tmp, "m-1");
        var sideA = new MailCursors(new MailStore(tmp.Dir));
        var sideB = new MailCursors(new MailStore(tmp.Dir));
        var viewA = sideA.Pending("main", "s-1");
        var viewB = sideB.Pending("main", "s-1");   // both fresh: deliveries 0

        CursorFixtures.AdvanceOk(sideA, viewA, offset);      // A delivered m-1
        File.Delete(sideA.CursorPath("main", "s-1"));        // d13: deletable anytime

        CursorFixtures.AdvanceOk(sideB, viewB, offset);      // B delivers m-1 AGAIN — accepted
        Assert.Empty(sideB.Pending("main", "s-1").Pending);
    }

    /// The distinguishable variant IS loud: a view read from a cursor that
    /// existed (deliveries > 0) advancing over its own vanished lineage
    /// proceeds — refusing would strand the rendered digest for nothing —
    /// but warns `mail.cursorVanished`, because mail that lineage had
    /// consumed may now redeliver to whoever anchors fresh.
    [Fact]
    public void ACursorDeletedAfterDeliveries_AdvancesWithALoudVanishedWarn()
    {
        using var log = new CapturedLog();
        using var tmp = new MailStoreTempDir();
        var cursors = tmp.Cursors();
        var o1 = CursorFixtures.AppendTo(tmp, "m-1");
        CursorFixtures.AdvanceOk(cursors, cursors.Pending("main", "s-1"), o1);

        var o2 = CursorFixtures.AppendTo(tmp, "m-2");
        var view = cursors.Pending("main", "s-1");           // deliveries 1: a lineage exists
        File.Delete(cursors.CursorPath("main", "s-1"));

        CursorFixtures.AdvanceOk(cursors, view, o2);         // proceeds, loudly
        var warn = Assert.Single(log.Events, e => e.Evt == "mail.cursorVanished");
        Assert.Equal("warn", warn.Lvl);
    }
}

// ---- the crash window, in its chosen direction -----------------------------

public class MailCrashWindowTests
{
    /// A digest that advanced and then died before its answer reached stdout
    /// (the crash window d4 chose): the mail is LOST from delivery, never
    /// doubled — and lost VISIBLY, not silently: the body stays durable in the
    /// store, the trail carries the cursor advance, and the ledger — which may
    /// under-claim but never claim falsely — has no `mail.deliver` for it.
    [Fact]
    public void AdvanceThenCrashBeforeEmit_LosesTheDigestVisibly_NeverDoublesIt()
    {
        using var log = new CapturedLog();
        using var tmp = new MailStoreTempDir();
        var cursors = tmp.Cursors();
        var offset = CursorFixtures.AppendTo(tmp, "m-1");

        // The "crashed digest": advance succeeds, no effect is ever emitted.
        CursorFixtures.AdvanceOk(cursors, cursors.Pending("main", "s-1"), offset);

        // The next seam must NOT re-deliver (that is the double-inject).
        var (exit, stdout, _) = EdgeFixtures.RunDigest(tmp.Dir, DigestFixtures.Request());
        Assert.Equal(0, exit);
        Assert.Equal("noop", EdgeFixtures.Effect(stdout));

        Assert.Contains(tmp.Store().Read(), l => l.Envelope?.Id == "m-1");      // durable: the loss is inspectable
        Assert.Contains(log.Events, e => e.Evt == "mail.cursorAdvance");        // the trail says the cursor moved
        Assert.DoesNotContain(log.Events, e => e.Evt == "mail.deliver");        // the ledger never claimed a delivery
    }
}

// ---- the store changes between the read and the advance --------------------

public class MailChainChangedTests
{
    /// Rotation-mid-read, the simple half: the store is REPLACED (a different
    /// chain at the same path — exactly what d13's rotation will do) between a
    /// view's read and its advance. The advance must refuse — the chain the
    /// view describes no longer exists — and the new chain then delivers
    /// normally, once.
    [Fact]
    public void StoreReplacedMidRead_AdvanceRefuses_TheNewChainDeliversOnce()
    {
        using var log = new CapturedLog();
        using var tmp = new MailStoreTempDir();
        var cursors = tmp.Cursors();
        var oldOffset = CursorFixtures.AppendTo(tmp, "m-old");
        var staleView = cursors.Pending("main", "s-1");

        File.Delete(tmp.FilePath);                             // rotation stand-in:
        var newOffset = CursorFixtures.AppendTo(tmp, "m-new"); // new file, new chain

        var refused = Assert.IsType<MailCursorWrite.Failed>(cursors.Advance(staleView, [oldOffset]));
        Assert.Contains("chain changed", refused.Error);
        Assert.False(File.Exists(cursors.CursorPath("main", "s-1")));   // nothing written
        // The refusal is first-class on the trail, not just a stderr line the
        // digest happens to print (the skeptic pass's loudness finding).
        Assert.Single(log.Events, e => e.Evt == "mail.cursorRefuse");

        var fresh = cursors.Pending("main", "s-1");
        Assert.Equal("m-new", Assert.Single(fresh.Pending).Envelope.Id);
        CursorFixtures.AdvanceOk(cursors, fresh, newOffset);
        Assert.Empty(cursors.Pending("main", "s-1").Pending);
    }

    /// THE regression this campaign exists for (the find that added the
    /// chain-changed guard): a stale view from chain A must not CLOBBER the
    /// cursor a fresher digest already wrote for chain B. Pre-guard, the
    /// stale advance sailed past the staleness check (different head ⇒ "the
    /// disk cursor vouches for nothing"), overwrote B's cursor with A's
    /// state, and the next read re-anchored on the head mismatch — putting
    /// B's ALREADY-DELIVERED mail back into pending: a double-inject
    /// requiring no tampering, just a replacement between read and advance.
    [Fact]
    public void AStaleChainAView_CannotClobberChainBsCursor_NoDoubleInject()
    {
        using var tmp = new MailStoreTempDir();
        var staleSide = new MailCursors(new MailStore(tmp.Dir));
        var aOffset = CursorFixtures.AppendTo(tmp, "a-1");
        var staleView = staleSide.Pending("main", "s-1");      // chain A, never advanced

        File.Delete(tmp.FilePath);
        var bOffset = CursorFixtures.AppendTo(tmp, "b-1");     // chain B replaces it

        // A fresher digest reads chain B and delivers its mail.
        var freshSide = new MailCursors(new MailStore(tmp.Dir));
        CursorFixtures.AdvanceOk(freshSide, freshSide.Pending("main", "s-1"), bOffset);

        // The stale chain-A advance must be refused BY THE GUARD — asserting
        // the reason keeps this from staying green on some incidental future
        // failure while the guard itself rots (the skeptic pass's point).
        var refused = Assert.IsType<MailCursorWrite.Failed>(staleSide.Advance(staleView, [aOffset]));
        Assert.Contains("chain changed", refused.Error);

        // b-1 was delivered once and stays delivered: no re-anchor, nothing
        // pending, the cursor still describes chain B.
        var after = freshSide.Pending("main", "s-1");
        Assert.False(after.Reanchored);
        Assert.Empty(after.Pending);
        Assert.Equal(1, after.Deliveries);
    }

    /// The guard's honest boundary, pinned so nobody later believes it does
    /// more than it does: truncation that PRESERVES the first line is
    /// chain-invisible (phase 2's own statement) and passes the head check —
    /// it is the truncation-reset re-anchor's job, which catches it on the
    /// NEXT read, loudly, with redelivery as the stated cost.
    [Fact]
    public void SameHeadTruncationMidRead_IsCaughtByTheTruncationReset_NotTheGuard()
    {
        using var tmp = new MailStoreTempDir();
        var cursors = tmp.Cursors();
        var o1 = CursorFixtures.AppendTo(tmp, "m-1");
        var o2 = CursorFixtures.AppendTo(tmp, "m-2");
        var view = cursors.Pending("main", "s-1");

        File.WriteAllText(tmp.FilePath, tmp.Lines()[0] + "\n");   // same head, shorter file

        // Head unchanged ⇒ the advance proceeds (the guard claims nothing here).
        CursorFixtures.AdvanceOk(cursors, view, o1, o2);

        var after = cursors.Pending("main", "s-1");
        Assert.True(after.Reanchored);
        Assert.Contains("truncated", after.ReanchorReason);
        Assert.Equal("m-1", Assert.Single(after.Pending).Envelope.Id);   // redelivery: loud, the safe direction
    }
}

// ---- a foreign writer forces what the write gate refuses -------------------

public class MailOversizedLineTests
{
    /// `MaxLineBytes` is enforced at Append so no windowed reader ever meets
    /// an oversized line — but a FOREIGN writer can force one in anyway. The
    /// delivery path must not silently skip it (skip is mail lost; the plan's
    /// worst direction): the cursor's read is windowless, the line is pending,
    /// and the digest delivers it TRUNCATED under its own cap with the marker
    /// — provenance whole, cursor advanced, full text durable in the store.
    [Fact]
    public void AForeignOversizedLine_IsDeliveredTruncated_NeverSilentlySkipped()
    {
        using var tmp = new MailStoreTempDir();
        var store = tmp.Store();
        var first = MailFixtures.AppendOk(store, MailFixtures.Envelope(id: "m-first", body: "small"));

        // Chained correctly (prev = the real hash) so SIZE is the only thing
        // foreign about the line — the write gate would refuse it.
        var bigLine = MailStore.Render(
            MailFixtures.Envelope(id: "m-big", body: new string('x', 200_000)),
            MailStore.HashOf(Encoding.UTF8.GetBytes(first.Line)));
        Assert.True(Encoding.UTF8.GetByteCount(bigLine) + 1 > MailStore.MaxLineBytes);
        Assert.IsType<MailAppend.Failed>(store.Append(MailFixtures.Envelope(
            id: "m-big", body: new string('x', 200_000))));           // the gate says no…
        File.AppendAllText(tmp.FilePath, bigLine + "\n");             // …the foreign writer does it anyway

        Assert.Equal(2, tmp.Cursors().Pending("main", "s-1").Pending.Count);   // windowless: never size-skipped

        // Whole-item cap: the small line delivers first, the oversized one
        // stays PENDING (held, not dropped).
        var one = EdgeFixtures.InjectText(EdgeFixtures.RunDigest(tmp.Dir, DigestFixtures.Request()).Out);
        Assert.Contains("id m-first", one);
        Assert.DoesNotContain("id m-big", one);

        // Alone, it takes the single-oversized-item path: truncated body,
        // provenance whole, explicit marker — and the cursor moves past it.
        var two = EdgeFixtures.InjectText(EdgeFixtures.RunDigest(tmp.Dir, DigestFixtures.Request()).Out);
        Assert.Contains("id m-big", two);
        Assert.Contains("[truncated to fit the delivery budget", two);
        Assert.True(two.Length < 6_000);   // the cap held against 200KB of sender data

        Assert.Empty(tmp.Cursors().Pending("main", "s-1").Pending);
        Assert.Empty(store.VerifyChain());   // hashed whole: size never broke the chain
    }
}

// ---- chain breaks meet the delivery path -----------------------------------

public class MailChainBreakDeliveryTests
{
    /// Classification and delivery are SEPARATE verdicts, deliberately: a
    /// break in already-consumed history (a middle line rewritten behind the
    /// frontier, not held) is the AUDIT's find — one PrevMismatch at the
    /// following line, nothing else — while the delivery path, which checks
    /// exactly what delivery depends on (head, boundaries, held ids), keeps
    /// serving new mail. A digest that refused to deliver because history was
    /// tampered would convert detectable tampering into a denial of service
    /// on the bus — the wrong trade within d11's stated boundary.
    [Fact]
    public void ABreakBehindTheFrontier_IsOneAuditFault_DeliveryContinues()
    {
        using var tmp = new MailStoreTempDir();
        var cursors = tmp.Cursors();
        var o1 = CursorFixtures.AppendTo(tmp, "m-1");
        var o2 = CursorFixtures.AppendTo(tmp, "m-2");
        CursorFixtures.AppendTo(tmp, "m-3");
        CursorFixtures.AdvanceOk(cursors, cursors.Pending("main", "s-1"), o1, o2);

        // Rewrite the MIDDLE line in place, same length: behind the frontier,
        // not held, head intact — only the chain can see it.
        File.WriteAllText(tmp.FilePath,
            string.Join('\n', tmp.Lines().Select(l => l.Replace("\"m-2\"", "\"m-Z\""))) + "\n");

        var fault = Assert.Single(tmp.Store().VerifyChain());
        Assert.Equal(MailChainFaultKind.PrevMismatch, fault.Kind);   // found, once, at the right place

        var view = cursors.Pending("main", "s-1");
        Assert.False(view.Reanchored);                               // delivery state is intact
        Assert.Equal("m-3", Assert.Single(view.Pending).Envelope.Id);
        var (exit, stdout, _) = EdgeFixtures.RunDigest(tmp.Dir, DigestFixtures.Request());
        Assert.Equal(0, exit);
        Assert.Contains("id m-3", EdgeFixtures.InjectText(stdout));  // new mail still flows
    }
}

// ---- the expired-only mailbox ----------------------------------------------

public class MailExpiredOnlyTests
{
    /// Expiry is bound to the ADVANCE, and a digest only advances when it
    /// delivers — so a mailbox holding ONLY expired mail noops (no advance,
    /// no state change: the quiet-seam rule outranks tidiness) and the spent
    /// envelope lingers, reported on every read, until the next DELIVERING
    /// digest names it once and its advance drops it. Pinned so the lingering
    /// window is a stated choice, not an accident someone later "fixes" by
    /// advancing at quiet seams — which would burn every other envelope's TTL.
    [Fact]
    public void AnExpiredOnlyMailbox_NoopsUntouched_TheNextDeliveryFlushesIt()
    {
        using var tmp = new MailStoreTempDir();
        var cursors = tmp.Cursors();
        CursorFixtures.AppendTo(tmp, "e-1", ttl: 1);
        CursorFixtures.AdvanceOk(cursors, cursors.Pending("main", "s-1"));   // passed over once: spent

        var cursorPath = cursors.CursorPath("main", "s-1");
        var before = File.ReadAllBytes(cursorPath);

        var (exit, stdout, _) = EdgeFixtures.RunDigest(tmp.Dir, DigestFixtures.Request());
        Assert.Equal(0, exit);
        Assert.Equal("noop", EdgeFixtures.Effect(stdout));
        Assert.Equal(before, File.ReadAllBytes(cursorPath));                 // untouched: no advance happened
        Assert.Equal("e-1", Assert.Single(cursors.Pending("main", "s-1").Expired).Envelope.Id);

        // New mail arrives: the delivering digest names the expiry once and
        // its advance flushes the spent envelope for good.
        CursorFixtures.AppendTo(tmp, "m-2");
        var text = EdgeFixtures.InjectText(EdgeFixtures.RunDigest(tmp.Dir, DigestFixtures.Request()).Out);
        Assert.Contains("id m-2", text);
        Assert.Contains("expired undelivered", text);
        Assert.Contains("e-1", text);

        var after = cursors.Pending("main", "s-1");
        Assert.Empty(after.Pending);
        Assert.Empty(after.Expired);
        Assert.Empty(MailCursor.TryParse(File.ReadAllText(cursorPath), out _)!.Held);
    }
}

// ---- seam classes interleaved across one turn ------------------------------

public class MailSeamInterleavingTests
{
    /// The cross-class shape of one full turn, driven through the real verb:
    /// urgent mail delivered MID-turn must be structurally gone by the
    /// turn-end reconcile (a leak here re-injects it into the digest that
    /// blocks the loop — the Stop-loop hazard wearing a different seam), the
    /// held ambient mail delivers there exactly once with its age, and a
    /// second reconcile finds a clean mailbox.
    [Fact]
    public void UrgentDeliveredMidTurn_IsGoneAtReconcile_SecondReconcileClean()
    {
        using var tmp = new MailStoreTempDir();
        var store = tmp.Store();
        MailFixtures.AppendOk(store, MailFixtures.Envelope(id: "amb-1", body: "ambient news"));
        MailFixtures.AppendOk(store, MailFixtures.Envelope(
            id: "urg-1", priority: MailPriority.Urgent, body: "act now"));

        var mid = EdgeFixtures.InjectText(EdgeFixtures.RunDigest(
            tmp.Dir, DigestFixtures.Request(type: "PostToolUse"),
            "--role", "main", "--seam", "urgent").Out);
        Assert.Contains("id urg-1", mid);
        Assert.DoesNotContain("id amb-1", mid);

        var reconcile = EdgeFixtures.InjectText(EdgeFixtures.RunDigest(
            tmp.Dir, DigestFixtures.Request(dispatchId: "d-2"),
            "--role", "main", "--seam", "reconcile").Out);
        Assert.Contains("id amb-1", reconcile);
        Assert.Contains("waited 1 opportunity", reconcile);    // the held stamp survived the urgent advance
        Assert.DoesNotContain("id urg-1", reconcile);          // delivered mail is ABSENT, not filtered

        var again = EdgeFixtures.RunDigest(
            tmp.Dir, DigestFixtures.Request(dispatchId: "d-3"),
            "--role", "main", "--seam", "reconcile");
        Assert.Equal("noop", EdgeFixtures.Effect(again.Out));  // nothing genuinely new: the turn ends quiet
    }
}
