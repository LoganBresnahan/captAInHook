using CaptainHook.Core;
using CaptainHook.Mail;

namespace CaptainHook.Tests;

// ADR-0017 decision 4, slice `watcher-brain` (roadmap item 22, phase 3) — the
// pure brain, driven with values. Every stamp here is a plain number the test
// chooses (the brain's `NowMs` is the injectable monotonic source; nothing
// sleeps and nothing reads a clock), so each protocol claim the plan's verify
// column names is a deterministic assertion:
//
//   * arm / disarm / re-arm — ONE `NextCheckMs`, the minimum over everything
//     armed; a second envelope while armed re-arms (the new minimum wins) and
//     can never double-arm; a cursor advance that clears the condition leaves
//     nothing armed for it.
//   * `quietFor` as a re-checked deadline from first sighting, restarted by a
//     recorded nudge — never from an envelope's wall-clock `ts`.
//   * `perRoleHour` sliding window and `perEnvelope` as the token-bill bound;
//     the strictest bound among the admitting rules applies.
//   * human-held ⇒ never a robot nudge, whatever the rules say.
//   * restart re-derivation as ages, not stamps: time the process was not
//     running is not counted, and a deadline that had fallen is due at once.
public class WatcherBrainTests
{
    private const long Min = 60_000;

    // ---- fixtures ------------------------------------------------------------

    private static PendingMail Mail(string id, long offset, string to = "reviewer",
        MailPriority priority = MailPriority.Urgent, long? seenAt = null) =>
        new(offset, MailFixtures.Envelope(id: id, to: to, priority: priority,
            ttl: to.Contains('@') ? null : 3), seenAt);

    private static WatchedMailbox Box(string role, string? instance, params PendingMail[] pending) =>
        new(new MailAddress(role, instance), pending);

    private static WatchRule Rule(string role = "reviewer", string? priority = ">=urgent",
        int? quietForMs = (int)(10 * Min), bool noLiveSession = true, int perEnvelope = 1, int perRoleHour = 4)
    {
        WatchPriority? p = priority switch
        {
            null => null,
            ">=urgent" => new WatchPriority(MailPriority.Urgent, true),
            "urgent" => new WatchPriority(MailPriority.Urgent, false),
            "ambient" => new WatchPriority(MailPriority.Ambient, false),
            ">=ambient" => new WatchPriority(MailPriority.Ambient, true),
            _ => throw new ArgumentException(priority),
        };
        return new WatchRule(role, new WatchWhen(p, quietForMs, noLiveSession), new WatchBudget(perEnvelope, perRoleHour));
    }

    private static readonly RoleKinds Robot = new(new HashSet<string>(), TurnPayloadInstalled: true);
    private static readonly RoleKinds Mixed = new(new HashSet<string> { "reviewer" }, TurnPayloadInstalled: true);
    private static readonly RoleKinds Human = new(new HashSet<string> { "reviewer" }, TurnPayloadInstalled: false);

    private static WatchInput Input(
        IReadOnlyList<WatchedMailbox> mailboxes, long now,
        IReadOnlyList<WatchRule>? rules = null, RoleKinds? kinds = null,
        NudgeState? state = null, IReadOnlyList<(string, long)>? presence = null) =>
        new(mailboxes, presence ?? [], kinds ?? Robot, rules ?? [Rule()], state ?? NudgeState.Empty, now);

    private static WatchRoleVerdict Only(WatchVerdict v) => Assert.Single(v.Roles);

    // ---- the gates that come before any threshold -----------------------------

    [Fact]
    public void NoRules_MeansNoRoles_NoNudges_NothingArmed()
    {
        var v = WatcherBrain.Evaluate(Input([Box("reviewer", "s-1", Mail("m-1", 0))], now: 1000, rules: []));
        Assert.Empty(v.Roles);
        Assert.Empty(v.Nudges);
        Assert.Null(v.NextCheckMs);
        Assert.Empty(v.State.Envelopes);   // untracked: only rule roles are remembered
    }

    /// d3's consequence, enforced by the brain: a human-held role never gets a
    /// robot nudge, however loud the mail, however old, whatever the rule.
    [Fact]
    public void HumanHeld_NeverNudges_AndArmsNothing_EvenPastEveryThreshold()
    {
        var state = new NudgeState([new WatchedEnvelope("reviewer", "m-1", 0, 0, 0)], []);
        var v = WatcherBrain.Evaluate(Input([Box("reviewer", "s-1", Mail("m-1", 0))],
            now: 60 * Min, kinds: Human, state: state));
        var r = Only(v);
        Assert.Equal(WatchStanding.HumanHeld, r.Standing);
        Assert.Equal(1, r.Unread);
        Assert.Empty(v.Nudges);
        Assert.Null(v.NextCheckMs);
        // …but the envelope IS remembered, so installing a turn payload later
        // does not restart its clock.
        Assert.Single(v.State.Envelopes, e => e.Id == "m-1" && e.FirstSeenMs == 0);
    }

    [Fact]
    public void Unserved_NeverNudges()
    {
        var kinds = new RoleKinds(new HashSet<string>(), TurnPayloadInstalled: false);
        var v = WatcherBrain.Evaluate(Input([Box("reviewer", null, Mail("m-1", 0))], now: 60 * Min, kinds: kinds));
        Assert.Equal(WatchStanding.Unserved, Only(v).Standing);
        Assert.Empty(v.Nudges);
        Assert.Null(v.NextCheckMs);
    }

    [Fact]
    public void NoMail_SaysSo_AndTracksNothing()
    {
        var v = WatcherBrain.Evaluate(Input([Box("reviewer", "s-1")], now: 1000));
        Assert.Equal(WatchStanding.NoMail, Only(v).Standing);
        Assert.Null(v.NextCheckMs);
        Assert.Empty(v.State.Envelopes);
    }

    // ---- quiet: a deadline re-checked, from first sighting ---------------------

    [Fact]
    public void FirstSighting_ArmsTheQuietDeadline_AndNudgesWhenItPasses()
    {
        var box = Box("reviewer", "s-1", Mail("m-1", 0));

        var at0 = WatcherBrain.Evaluate(Input([box], now: 1000));
        Assert.Equal(WatchStanding.NotDue, Only(at0).Standing);
        Assert.Empty(at0.Nudges);
        Assert.Equal(1000 + 10 * Min, at0.NextCheckMs);
        var tracked = Assert.Single(at0.State.Envelopes);
        Assert.Equal((1000L, 1000L, 0), (tracked.FirstSeenMs, tracked.QuietSinceMs, tracked.Nudged));

        // One millisecond short: still not due, still the same deadline.
        var early = WatcherBrain.Evaluate(Input([box], now: 1000 + 10 * Min - 1, state: at0.State));
        Assert.Empty(early.Nudges);
        Assert.Equal(1000 + 10 * Min, early.NextCheckMs);

        // At the deadline: due, one nudge, the envelope named.
        var due = WatcherBrain.Evaluate(Input([box], now: 1000 + 10 * Min, state: at0.State));
        var n = Assert.Single(due.Nudges);
        Assert.Equal("reviewer", n.Role);
        Assert.Equal(["m-1"], n.EnvelopeIds);
        Assert.Equal(WatchStanding.Nudge, Only(due).Standing);
        Assert.Equal(WatcherBrain.ReplyHow, n.ReplyHow);
        Assert.Null(n.Workspace);
    }

    /// The envelope's `ts` is a sender's wall clock and never read: an envelope
    /// stamped years ago is first seen NOW and waits its full quiet period.
    [Fact]
    public void Quiet_CountsFromFirstSighting_NeverFromTheEnvelopesWallClockTs()
    {
        var old = new PendingMail(0, MailFixtures.Envelope(id: "m-old", to: "reviewer",
            priority: MailPriority.Urgent) with { Ts = "1999-01-01T00:00:00Z" }, null);
        var v = WatcherBrain.Evaluate(Input([Box("reviewer", "s-1", old)], now: 5_000_000));
        Assert.Equal(WatchStanding.NotDue, Only(v).Standing);
        Assert.Equal(5_000_000 + 10 * Min, v.NextCheckMs);
    }

    [Fact]
    public void QuietForZero_IsDueTheMomentItLands()
    {
        var v = WatcherBrain.Evaluate(Input([Box("reviewer", "s-1", Mail("m-1", 0))],
            now: 7, rules: [Rule(quietForMs: 0)]));
        Assert.Single(v.Nudges);
    }

    // ---- arm / disarm / re-arm ---------------------------------------------------

    /// A second envelope while armed: the verdict still holds ONE deadline, and
    /// it is the earlier one. When that fires, the next verdict's deadline is
    /// the second envelope's — re-armed, never double-armed.
    [Fact]
    public void SecondEnvelopeWhileArmed_ReArmsToTheMinimum_NeverDoubleArms()
    {
        var first = WatcherBrain.Evaluate(Input([Box("reviewer", "s-1", Mail("m-1", 0))], now: 0));
        Assert.Equal(10 * Min, first.NextCheckMs);

        // m-2 lands at 4 min: the deadline stays m-1's (6 min away), not m-2's.
        var box2 = Box("reviewer", "s-1", Mail("m-1", 0), Mail("m-2", 300));
        var second = WatcherBrain.Evaluate(Input([box2], now: 4 * Min, state: first.State));
        Assert.Equal(10 * Min, second.NextCheckMs);
        Assert.Equal(2, second.State.Envelopes.Count);
        Assert.Equal(4 * Min, second.State.Envelopes.Single(e => e.Id == "m-2").QuietSinceMs);

        // m-1 fires at 10 min; m-2 is still 4 min short and stays behind.
        var fired = WatcherBrain.Evaluate(Input([box2], now: 10 * Min, state: second.State));
        var n = Assert.Single(fired.Nudges);
        Assert.Equal(["m-1"], n.EnvelopeIds);
        Assert.DoesNotContain("more due", n.Reason, StringComparison.Ordinal);   // m-2 is not due, just unread
        Assert.Equal(14 * Min, fired.NextCheckMs);                                 // re-armed to m-2's threshold
    }

    /// The cursor advance that clears the condition: the envelope leaves the
    /// mailbox, leaves the state, and nothing stays armed for it.
    [Fact]
    public void Read_DisarmsAndForgets()
    {
        var armed = WatcherBrain.Evaluate(Input([Box("reviewer", "s-1", Mail("m-1", 0))], now: 0));
        Assert.NotNull(armed.NextCheckMs);

        var read = WatcherBrain.Evaluate(Input([Box("reviewer", "s-1")], now: 5 * Min, state: armed.State));
        Assert.Equal(WatchStanding.NoMail, Only(read).Standing);
        Assert.Null(read.NextCheckMs);
        Assert.Empty(read.State.Envelopes);

        // …and if it somehow reappears (a reaped cursor re-anchoring), the
        // clock starts over — no ghost of the old sighting.
        var back = WatcherBrain.Evaluate(Input([Box("reviewer", "s-1", Mail("m-1", 0))], now: 6 * Min, state: read.State));
        Assert.Equal(16 * Min, back.NextCheckMs);
    }

    /// Recording restarts the quiet clock: the second poke waits the full
    /// period again rather than firing on the very next evaluation.
    [Fact]
    public void RecordedNudge_RestartsQuiet_AndTheProjectedReArmIsInTheVerdict()
    {
        var box = Box("reviewer", "s-1", Mail("m-1", 0));
        var rules = new[] { Rule(perEnvelope: 2) };
        var armed = WatcherBrain.Evaluate(Input([box], now: 0, rules: rules));
        var fired = WatcherBrain.Evaluate(Input([box], now: 10 * Min, rules: rules, state: armed.State));
        var n = Assert.Single(fired.Nudges);
        // The verdict projects the re-arm: budget remains, so due again in 10m.
        Assert.Equal(20 * Min, fired.NextCheckMs);

        var recorded = fired.State.Record(n, 10 * Min);
        var e = Assert.Single(recorded.Envelopes);
        Assert.Equal((0L, 10 * Min, 1), (e.FirstSeenMs, e.QuietSinceMs, e.Nudged));
        Assert.Single(recorded.Nudges);

        var soon = WatcherBrain.Evaluate(Input([box], now: 10 * Min + 1, rules: rules, state: recorded));
        Assert.Empty(soon.Nudges);
        Assert.Equal(WatchStanding.NotDue, Only(soon).Standing);
        Assert.Equal(20 * Min, soon.NextCheckMs);

        var again = WatcherBrain.Evaluate(Input([box], now: 20 * Min, rules: rules, state: recorded));
        Assert.Single(again.Nudges);
        Assert.Contains("budget envelope 2/2", again.Nudges[0].Reason, StringComparison.Ordinal);
        // The re-arm is projected whether or not budget remains: the caller may
        // record this one UNCHARGED (denied), in which case it is due again at
        // 30m — and if it is charged, the wake at 30m finds `Exhausted` and arms
        // nothing more.
        Assert.Equal(30 * Min, again.NextCheckMs);
        var lastRecorded = again.State.Record(again.Nudges[0], 20 * Min);
        var after = WatcherBrain.Evaluate(Input([box], now: 30 * Min, rules: rules, state: lastRecorded));
        Assert.Equal(WatchStanding.Exhausted, Only(after).Standing);
        Assert.Null(after.NextCheckMs);
    }

    /// The skeptic's finding: an uncharged record keeps the budget, so the
    /// envelope IS due again after its threshold — the verdict's deadline must
    /// say so even when the charged path would have had nothing left to arm.
    [Fact]
    public void UnchargedRecord_IsDueAgainAtTheProjectedReArm()
    {
        var box = Box("reviewer", "s-1", Mail("m-1", 0));
        var fired = WatcherBrain.Evaluate(Input([box], now: 10 * Min,
            state: new NudgeState([new WatchedEnvelope("reviewer", "m-1", 0, 0, 0)], [])));   // perEnvelope 1
        var n = Assert.Single(fired.Nudges);
        Assert.Equal(20 * Min, fired.NextCheckMs);

        var denied = fired.State.Record(n, 10 * Min, charged: false);
        var at20 = WatcherBrain.Evaluate(Input([box], now: 20 * Min, state: denied));
        Assert.Single(at20.Nudges);
    }

    /// A spent envelope whose quiet clock was restarted arms nothing: nothing
    /// but being read can change its standing, so waking for it is waste.
    [Fact]
    public void SpentEnvelope_ArmsNothing_WhateverItsQuietClockSays()
    {
        var box = Box("reviewer", "s-1", Mail("m-1", 0));
        var state = new NudgeState([new WatchedEnvelope("reviewer", "m-1", 0, 10 * Min, Nudged: 1)], []);
        var v = WatcherBrain.Evaluate(Input([box], now: 12 * Min, state: state));
        Assert.Equal(WatchStanding.Exhausted, Only(v).Standing);
        Assert.Null(v.NextCheckMs);
    }

    /// The digest is capped at whole items. A nudge names and charges ONLY what
    /// it carries; the tail stays due, un-nudged, and the verdict arms `now` so
    /// the next evaluation carries it.
    [Fact]
    public void DigestCap_NamesOnlyWhatWasRendered_TheRestStaysDue()
    {
        var big = new string('x', 1500);
        var mails = Enumerable.Range(1, 5).Select(i => new PendingMail(i * 2000,
            MailFixtures.Envelope(id: $"m-{i}", to: "reviewer", priority: MailPriority.Urgent, body: big), null)).ToArray();
        var box = Box("reviewer", "s-1", mails);
        var state = new NudgeState(mails.Select(m => new WatchedEnvelope("reviewer", m.Envelope.Id, 0, 0, 0)).ToList(), []);
        var rules = new[] { Rule(quietForMs: 0, perRoleHour: 10) };

        var v = WatcherBrain.Evaluate(Input([box], now: 1000, rules: rules, state: state));
        var n = Assert.Single(v.Nudges);
        Assert.Equal(["m-1", "m-2"], n.EnvelopeIds);                 // 2 × ~1.6KB fit the 4096 cap
        Assert.Contains("· 3 more due, held for the next nudge by the digest cap", n.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("m-3", n.Digest);
        Assert.Equal(1000, v.NextCheckMs);                             // carry the remainder next evaluation

        var recorded = v.State.Record(n, 1000);
        var next = WatcherBrain.Evaluate(Input([box], now: 1000, rules: rules, state: recorded));
        Assert.Equal(["m-3", "m-4"], Assert.Single(next.Nudges).EnvelopeIds);
        Assert.Equal(0, recorded.Envelopes.Single(e => e.Id == "m-3").Nudged);   // never charged
    }

    /// State off a file may carry a doubled line; the first entry wins and the
    /// brain does not throw.
    [Fact]
    public void DuplicateStateEntry_FirstWins_NoThrow()
    {
        var state = new NudgeState(
            [new("reviewer", "m-1", 0, 0, 0), new("reviewer", "m-1", 5 * Min, 5 * Min, 0)], []);
        var v = WatcherBrain.Evaluate(Input([Box("reviewer", "s-1", Mail("m-1", 0))], now: 10 * Min, state: state));
        Assert.Single(v.Nudges);
        Assert.Single(v.State.Envelopes);
    }

    /// More in the window than the bound allows (a lowered `perRoleHour`): the
    /// release is when ENOUGH have aged out to free one slot, not the oldest.
    [Fact]
    public void PerRoleHour_ReleaseFreesASlot_NotJustTheOldest()
    {
        var box = Box("reviewer", "s-1", Mail("m-1", 0));
        var state = new NudgeState([new("reviewer", "m-1", 0, 0, 0)],
            [new("reviewer", 10 * Min), new("reviewer", 20 * Min), new("reviewer", 30 * Min), new("reviewer", 40 * Min), new("reviewer", 45 * Min)]);
        var v = WatcherBrain.Evaluate(Input([box], now: 50 * Min, rules: [Rule(perRoleHour: 2)], state: state));
        Assert.Equal(WatchStanding.Exhausted, Only(v).Standing);
        Assert.Equal(40 * Min + WatcherBrain.RoleWindowMs, v.NextCheckMs);   // 4 must age out: 10,20,30,40
        Assert.Contains("window frees in 50m", Only(v).Detail, StringComparison.Ordinal);
    }

    /// The verdict does NOT charge the nudges it emits (a denied dispatch must
    /// not spend the budget); re-evaluating without recording emits it again.
    [Fact]
    public void Verdict_DoesNotChargeItsOwnNudges_RecordIsTheCallers()
    {
        var box = Box("reviewer", "s-1", Mail("m-1", 0));
        var armed = WatcherBrain.Evaluate(Input([box], now: 0));
        var fired = WatcherBrain.Evaluate(Input([box], now: 10 * Min, state: armed.State));
        Assert.Single(fired.Nudges);
        Assert.Equal(0, fired.State.Envelopes.Single().Nudged);
        Assert.Empty(fired.State.Nudges);

        var twice = WatcherBrain.Evaluate(Input([box], now: 10 * Min + 1, state: fired.State));
        Assert.Single(twice.Nudges);
    }

    /// An UNCHARGED record (policy denied the dispatch): quiet restarts so the
    /// denial recurs once per period, but no budget moves.
    [Fact]
    public void UnchargedRecord_RestartsQuiet_ButSpendsNothing()
    {
        var box = Box("reviewer", "s-1", Mail("m-1", 0));
        var fired = WatcherBrain.Evaluate(Input([box], now: 10 * Min,
            state: new NudgeState([new WatchedEnvelope("reviewer", "m-1", 0, 0, 0)], [])));
        var n = Assert.Single(fired.Nudges);

        var denied = fired.State.Record(n, 10 * Min, charged: false);
        var e = Assert.Single(denied.Envelopes);
        Assert.Equal((10 * Min, 0), (e.QuietSinceMs, e.Nudged));
        Assert.Empty(denied.Nudges);
    }

    [Fact]
    public void Record_IgnoresEnvelopesTheStateDoesNotTrack()
    {
        var s = NudgeState.Empty.Record(new MailNudge("reviewer", ["ghost"], "r", "d", "h"), 5);
        Assert.Empty(s.Envelopes);
        Assert.Single(s.Nudges);   // the role's window entry is real: a nudge did happen
    }

    // ---- presence: noLiveSession, and the deadline it arms ---------------------

    [Fact]
    public void LiveSession_HoldsTheNudge_AndArmsForPresenceExpiry()
    {
        var box = Box("reviewer", "s-1", Mail("m-1", 0));
        var state = new NudgeState([new WatchedEnvelope("reviewer", "m-1", 0, 0, 0)], []);
        // s-1 dispatched 3 minutes ago: live (within 10).
        var v = WatcherBrain.Evaluate(Input([box], now: 30 * Min, state: state, presence: [("s-1", 3 * Min)]));
        var r = Only(v);
        Assert.Equal(WatchStanding.LiveSession, r.Standing);
        Assert.Empty(v.Nudges);
        Assert.Equal(3 * Min, r.FreshestDispatchAgeMs);
        // Live is `age <= 10m`; the moment past that is 7m + 1ms from now.
        Assert.Equal(30 * Min + 7 * Min + 1, v.NextCheckMs);
    }

    [Fact]
    public void StaleSession_IsNotLive_SoTheNudgeGoes()
    {
        var box = Box("reviewer", "s-1", Mail("m-1", 0));
        var state = new NudgeState([new WatchedEnvelope("reviewer", "m-1", 0, 0, 0)], []);
        var v = WatcherBrain.Evaluate(Input([box], now: 30 * Min, state: state, presence: [("s-1", 10 * Min + 1)]));
        Assert.Single(v.Nudges);
        Assert.Contains("no live session", v.Nudges[0].Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ExactlyAtTheLiveBoundary_IsStillLive()
    {
        var box = Box("reviewer", "s-1", Mail("m-1", 0));
        var state = new NudgeState([new WatchedEnvelope("reviewer", "m-1", 0, 0, 0)], []);
        var v = WatcherBrain.Evaluate(Input([box], now: 30 * Min, state: state, presence: [("s-1", 10 * Min)]));
        Assert.Empty(v.Nudges);
        Assert.Equal(30 * Min + 1, v.NextCheckMs);
    }

    [Fact]
    public void NoLiveSessionFalse_NudgesDespiteALiveWindow()
    {
        var box = Box("reviewer", "s-1", Mail("m-1", 0));
        var state = new NudgeState([new WatchedEnvelope("reviewer", "m-1", 0, 0, 0)], []);
        var v = WatcherBrain.Evaluate(Input([box], now: 30 * Min, rules: [Rule(noLiveSession: false)],
            state: state, presence: [("s-1", 0)]));
        Assert.Single(v.Nudges);
        Assert.Contains("live session allowed", v.Nudges[0].Reason, StringComparison.Ordinal);
    }

    /// A `--as` mailbox is a mailbox, not a window (RolePresence's rule): a
    /// role held only by a durable named mailbox never reads as live, however
    /// busy the machine's other sessions are — while a WINDOW of the role that
    /// dispatched does make it live.
    [Fact]
    public void NamedMailbox_NeverLooksLive_ButAWindowOfTheRoleDoes()
    {
        var state = new NudgeState([new WatchedEnvelope("reviewer", "m-1", 0, 0, 0)], []);
        var named = Box("reviewer", "robot", Mail("m-1", 0));
        var busyElsewhere = WatcherBrain.Evaluate(Input([named], now: 30 * Min, kinds: Mixed, state: state,
            presence: [("s-9", 0), ("s-10", 0)]));
        Assert.Single(busyElsewhere.Nudges);
        Assert.Null(Only(busyElsewhere).FreshestDispatchAgeMs);

        var window = Box("reviewer", "s-9", Mail("m-1", 0));
        var held = WatcherBrain.Evaluate(Input([named, window], now: 30 * Min, kinds: Mixed, state: state,
            presence: [("s-9", 0)]));
        Assert.Equal(WatchStanding.LiveSession, Only(held).Standing);
    }

    // ---- unread: the strict reading -----------------------------------------------

    /// Two mailboxes of the role; one has read the envelope. The role has heard
    /// it — not unread, nothing armed, nothing tracked.
    [Fact]
    public void ReadByOneMailbox_IsNotUnreadForTheRole()
    {
        var v = WatcherBrain.Evaluate(Input(
            [Box("reviewer", "s-1", Mail("m-1", 0)), Box("reviewer", "s-2")], now: 0));
        Assert.Equal(WatchStanding.NoMail, Only(v).Standing);
        Assert.Empty(v.State.Envelopes);
    }

    [Fact]
    public void PendingInEveryMailbox_IsUnread()
    {
        var v = WatcherBrain.Evaluate(Input(
            [Box("reviewer", "s-1", Mail("m-1", 0)), Box("reviewer", "s-2", Mail("m-1", 0))], now: 0));
        Assert.Equal(1, Only(v).Unread);
    }

    /// A unicast is accepted by one mailbox: it is unread while THAT one holds
    /// it, whatever the role's other windows have read.
    [Fact]
    public void Unicast_IsJudgedByItsOneMailbox()
    {
        var uni = Mail("u-1", 0, to: "reviewer@robot");
        var v = WatcherBrain.Evaluate(Input(
            [Box("reviewer", "s-1", Mail("m-1", 100)), Box("reviewer", "s-2"), Box("reviewer", "robot", uni, Mail("m-1", 100))],
            now: 0, kinds: Mixed));
        Assert.Equal(1, Only(v).Unread);
        Assert.Equal("u-1", v.State.Envelopes.Single().Id);
    }

    [Fact]
    public void RoleWithNoMailbox_ContributesNothing_TheGathererMustReadItSessionless()
    {
        var v = WatcherBrain.Evaluate(Input([Box("other", "s-1", Mail("m-1", 0, to: "other"))], now: 0));
        Assert.Equal(WatchStanding.NoMail, Only(v).Standing);
    }

    // ---- priority filters and rule order ----------------------------------------------

    [Theory]
    [InlineData(">=urgent", MailPriority.Ambient, false)]
    [InlineData(">=urgent", MailPriority.Reconcile, false)]
    [InlineData(">=urgent", MailPriority.Urgent, true)]
    [InlineData(">=ambient", MailPriority.Ambient, true)]
    [InlineData(">=ambient", MailPriority.Urgent, true)]
    [InlineData("urgent", MailPriority.Reconcile, false)]
    [InlineData("urgent", MailPriority.Urgent, true)]
    [InlineData("ambient", MailPriority.Urgent, false)]
    [InlineData(null, MailPriority.Ambient, true)]
    public void PriorityFilter_AdmitsExactlyTheClassOrAnythingLouder(string? filter, MailPriority p, bool admits)
    {
        var rule = Rule(priority: filter, quietForMs: 0);
        Assert.Equal(admits, WatcherBrain.Admits(rule.When.Priority, p));
        var v = WatcherBrain.Evaluate(Input([Box("reviewer", "s-1", Mail("m-1", 0, priority: p))], now: 0, rules: [rule]));
        Assert.Equal(admits ? 1 : 0, v.Nudges.Count);
        if (!admits)
        {
            Assert.Equal(WatchStanding.NotDue, Only(v).Standing);
            Assert.Null(v.NextCheckMs);   // ungoverned: unread, but nothing to wait for
        }
    }

    /// Two rules for one role: the FIRST whose priority admits an envelope
    /// governs it — so an urgent-fast rule and an ambient-slow one coexist.
    [Fact]
    public void FirstAdmittingRuleWins_PerEnvelope()
    {
        var rules = new[]
        {
            Rule(priority: ">=urgent", quietForMs: (int)(5 * Min)),
            Rule(priority: null, quietForMs: (int)(60 * Min)),
        };
        var box = Box("reviewer", "s-1", Mail("m-u", 0, priority: MailPriority.Urgent), Mail("m-a", 300, priority: MailPriority.Ambient));
        var v = WatcherBrain.Evaluate(Input([box], now: 0, rules: rules));
        Assert.Equal(5 * Min, v.NextCheckMs);

        var at5 = WatcherBrain.Evaluate(Input([box], now: 5 * Min, rules: rules, state: v.State));
        Assert.Equal(["m-u"], Assert.Single(at5.Nudges).EnvelopeIds);
        // The ambient one is governed by the slow rule and still waits; the
        // verdict's deadline is m-u's projected re-arm (5m from now), which is
        // sooner — and once m-u is charged and spent, the ambient one's hour.
        Assert.Equal(10 * Min, at5.NextCheckMs);
        var charged = at5.State.Record(at5.Nudges[0], 5 * Min);
        var at10 = WatcherBrain.Evaluate(Input([box], now: 10 * Min, rules: rules, state: charged));
        Assert.Empty(at10.Nudges);
        Assert.Equal(60 * Min, at10.NextCheckMs);
    }

    // ---- budgets --------------------------------------------------------------------

    [Fact]
    public void PerEnvelope_Exhausted_NudgesNoMore_AndArmsNothing()
    {
        var box = Box("reviewer", "s-1", Mail("m-1", 0));
        var state = new NudgeState([new WatchedEnvelope("reviewer", "m-1", 0, 0, Nudged: 1)], []);
        var v = WatcherBrain.Evaluate(Input([box], now: 60 * Min, state: state));   // perEnvelope 1
        var r = Only(v);
        Assert.Equal(WatchStanding.Exhausted, r.Standing);
        Assert.Empty(v.Nudges);
        Assert.Null(v.NextCheckMs);
        Assert.Contains("perEnvelope", r.Detail, StringComparison.Ordinal);
    }

    /// The sliding window: four nudges in the hour spend `perRoleHour: 4`; the
    /// fifth waits for the OLDEST to age out, and the deadline says when.
    [Fact]
    public void PerRoleHour_IsASlidingWindow_ArmedForTheOldestToAgeOut()
    {
        var box = Box("reviewer", "s-1", Mail("m-5", 0));
        var state = new NudgeState(
            [new WatchedEnvelope("reviewer", "m-5", 0, 0, 0)],
            [new("reviewer", 10 * Min), new("reviewer", 20 * Min), new("reviewer", 30 * Min), new("reviewer", 40 * Min)]);

        var blocked = WatcherBrain.Evaluate(Input([box], now: 50 * Min, state: state));
        Assert.Equal(WatchStanding.Exhausted, Only(blocked).Standing);
        Assert.Empty(blocked.Nudges);
        Assert.Equal(10 * Min + WatcherBrain.RoleWindowMs, blocked.NextCheckMs);
        Assert.Equal(4, blocked.State.Nudges.Count);

        // At 70 min the 10-min entry has aged out: three in window, one free.
        var freed = WatcherBrain.Evaluate(Input([box], now: 70 * Min, state: state));
        var n = Assert.Single(freed.Nudges);
        Assert.Contains("role 4/4 this hour", n.Reason, StringComparison.Ordinal);
        Assert.Equal(3, freed.State.Nudges.Count);   // pruned
    }

    /// Two due envelopes admitted by two rules with different `perRoleHour`:
    /// the strictest bound applies to the role.
    [Fact]
    public void PerRoleHour_TheStrictestAdmittingRuleBounds()
    {
        var rules = new[]
        {
            Rule(priority: ">=urgent", quietForMs: 0, perRoleHour: 1),
            Rule(priority: null, quietForMs: 0, perRoleHour: 10),
        };
        var box = Box("reviewer", "s-1", Mail("m-u", 0, priority: MailPriority.Urgent), Mail("m-a", 300, priority: MailPriority.Ambient));
        var state = new NudgeState([], [new("reviewer", 0)]);   // one already this hour
        var v = WatcherBrain.Evaluate(Input([box], now: 1000, rules: rules, state: state));
        Assert.Equal(WatchStanding.Exhausted, Only(v).Standing);
        Assert.Empty(v.Nudges);
    }

    /// One role's window is another role's nothing.
    [Fact]
    public void PerRoleHour_IsPerRole()
    {
        var rules = new[] { Rule(role: "reviewer", quietForMs: 0, perRoleHour: 1), Rule(role: "ops", quietForMs: 0, perRoleHour: 1) };
        var boxes = new[] { Box("reviewer", "s-1", Mail("m-r", 0)), Box("ops", "s-2", Mail("m-o", 300, to: "ops")) };
        var state = new NudgeState([], [new("reviewer", 0)]);
        var v = WatcherBrain.Evaluate(Input(boxes, now: 1000, rules: rules, state: state));
        Assert.Equal(["ops"], v.Nudges.Select(n => n.Role));
        Assert.Equal(WatchStanding.Exhausted, v.Roles.Single(r => r.Role == "reviewer").Standing);
    }

    // ---- restart: ages, not stamps -----------------------------------------------------

    /// Six of ten minutes quiet, then the process ends. Reloaded into a process
    /// whose clock reads something else entirely, the envelope is still six
    /// minutes quiet, and due four minutes after the reload — the gap is not
    /// counted, and no wall clock was consulted.
    [Fact]
    public void ToAgesFromAges_ReDerivesDeadlines_WithoutCountingTheGap()
    {
        var box = Box("reviewer", "s-1", Mail("m-1", 0));
        var armed = WatcherBrain.Evaluate(Input([box], now: 100_000));
        var later = WatcherBrain.Evaluate(Input([box], now: 100_000 + 6 * Min, state: armed.State));
        Assert.Equal(WatchStanding.NotDue, Only(later).Standing);

        var ages = later.State.ToAges(100_000 + 6 * Min);
        var e = Assert.Single(ages.Envelopes);
        Assert.Equal((6 * Min, 6 * Min), (e.UnreadForMs, e.QuietForMs));

        // A "new process": its monotonic clock reads 5000.
        var reloaded = NudgeState.FromAges(ages, 5000);
        var back = WatcherBrain.Evaluate(Input([box], now: 5000, state: reloaded));
        Assert.Equal(WatchStanding.NotDue, Only(back).Standing);
        Assert.Equal(5000 + 4 * Min, back.NextCheckMs);

        var due = WatcherBrain.Evaluate(Input([box], now: 5000 + 4 * Min, state: reloaded));
        Assert.Single(due.Nudges);
    }

    /// A deadline that HAD fallen before the exit is due at once on the next start.
    [Fact]
    public void ADeadlineThatFellBeforeTheExit_IsDueOnStart()
    {
        var ages = new NudgeStateAges([new("reviewer", "m-1", 12 * Min, 12 * Min, 0)], []);
        var v = WatcherBrain.Evaluate(Input([Box("reviewer", "s-1", Mail("m-1", 0))], now: 42, state: NudgeState.FromAges(ages, 42)));
        Assert.Single(v.Nudges);
    }

    [Fact]
    public void Ages_RoundTrip_ExactlyWhenTheClockDoesNotMove_AndClampNegatives()
    {
        var s = new NudgeState(
            [new("reviewer", "m-1", 10, 20, 2)],
            [new("reviewer", 30)]);
        var back = NudgeState.FromAges(s.ToAges(100), 100);
        Assert.Equal(s.Envelopes, back.Envelopes);
        Assert.Equal(s.Nudges, back.Nudges);

        // A stamp "in the future" (a corrupt file, a clock that stepped) becomes
        // age 0 — never a negative that would push a deadline further out.
        var future = new NudgeState([new("reviewer", "m-1", 500, 500, 0)], [new("reviewer", 500)]);
        var ages = future.ToAges(100);
        Assert.Equal(0, ages.Envelopes[0].QuietForMs);
        Assert.Equal(0, ages.Nudges[0].AgoMs);
        var neg = new NudgeStateAges([new("reviewer", "m-1", -5, -5, -1)], [new("reviewer", -5)]);
        var fromNeg = NudgeState.FromAges(neg, 100);
        Assert.Equal((100L, 100L, 0), (fromNeg.Envelopes[0].FirstSeenMs, fromNeg.Envelopes[0].QuietSinceMs, fromNeg.Envelopes[0].Nudged));
        Assert.Equal(100, fromNeg.Nudges[0].AtMs);
    }

    // ---- the values it emits ---------------------------------------------------------

    [Fact]
    public void Evaluate_IsDeterministic()
    {
        var box = Box("reviewer", "s-1", Mail("m-2", 300), Mail("m-1", 0));
        var state = new NudgeState([new("reviewer", "m-1", 0, 0, 0), new("reviewer", "m-2", 0, 0, 0)], []);
        var a = WatcherBrain.Evaluate(Input([box], now: 30 * Min, state: state));
        var b = WatcherBrain.Evaluate(Input([box], now: 30 * Min, state: state));
        Assert.Equal(a.Nudges.Single().Reason, b.Nudges.Single().Reason);
        Assert.Equal(a.Nudges.Single().Digest, b.Nudges.Single().Digest);
        Assert.Equal(a.Nudges.Single().EnvelopeIds, b.Nudges.Single().EnvelopeIds);
        Assert.Equal(a.NextCheckMs, b.NextCheckMs);
        Assert.Equal(a.State.Envelopes, b.State.Envelopes);
        // Ledger order, whatever order the mailbox listed them in.
        Assert.Equal(["m-1", "m-2"], a.Nudges[0].EnvelopeIds);
    }

    [Fact]
    public void Reason_IsTheDeterministicSentence()
    {
        var box = Box("reviewer", "s-1", Mail("m-1", 0));
        var state = new NudgeState([new("reviewer", "m-1", 0, 0, 0)], []);
        var v = WatcherBrain.Evaluate(Input([box], now: 12 * Min + 30_000, state: state));
        Assert.Equal("1 unread past quiet (12m30s+) · no live session · budget envelope 1/1 · role 1/4 this hour",
            v.Nudges.Single().Reason);
    }

    /// The digest is the real renderer's text over the due envelopes, headed
    /// for the role, every item `new` (no per-cursor `SeenAt` leaks in), so a
    /// robot reads the id and the return address exactly as a window would.
    [Fact]
    public void Digest_IsTheRealRendererOverTheDueEnvelopes()
    {
        var box = Box("reviewer", "s-1", Mail("m-1", 0, seenAt: 3), Mail("m-2", 300));
        var state = new NudgeState([new("reviewer", "m-1", 0, 0, 0), new("reviewer", "m-2", 0, 0, 0)], []);
        var v = WatcherBrain.Evaluate(Input([box], now: 30 * Min, state: state));
        var digest = v.Nudges.Single().Digest;
        Assert.StartsWith("[captAInHook mail] 2 message(s) for 'reviewer':", digest);
        Assert.Contains("1. from intent-watcher (claude-code) · status/urgent · id m-1 · topic: build · new", digest);
        Assert.Contains("2. from intent-watcher (claude-code) · status/urgent · id m-2 · topic: build · new", digest);
        Assert.DoesNotContain("waited", digest);
    }

    [Theory]
    [InlineData(0, "0s")]
    [InlineData(500, "500ms")]
    [InlineData(1_000, "1s")]
    [InlineData(90_000, "1m30s")]
    [InlineData(600_000, "10m")]
    [InlineData(5_400_000, "1h30m")]
    [InlineData(3_600_000, "1h")]
    [InlineData(-7, "0s")]
    public void Dur_RendersLikeWatchJsonSpellsIt(long ms, string expected) =>
        Assert.Equal(expected, WatcherBrain.Dur(ms));

    // ---- state hygiene --------------------------------------------------------------

    /// A rule removed ⇒ its role's memory is dropped; re-adding it starts the
    /// clocks over — the conservative direction.
    [Fact]
    public void RuleRemoved_ForgetsTheRole()
    {
        var box = Box("reviewer", "s-1", Mail("m-1", 0));
        var armed = WatcherBrain.Evaluate(Input([box], now: 0));
        var without = WatcherBrain.Evaluate(Input([box], now: 5 * Min, rules: [], state: armed.State));
        Assert.Empty(without.State.Envelopes);
    }

    /// Several roles: each is judged alone, and the verdict's one deadline is
    /// the minimum across them.
    [Fact]
    public void ManyRoles_OneDeadline_TheMinimum()
    {
        var rules = new[] { Rule(role: "reviewer", quietForMs: (int)(10 * Min)), Rule(role: "ops", quietForMs: (int)(3 * Min)) };
        var boxes = new[] { Box("reviewer", "s-1", Mail("m-r", 0)), Box("ops", "s-2", Mail("m-o", 300, to: "ops")) };
        var v = WatcherBrain.Evaluate(Input(boxes, now: 0, rules: rules));
        Assert.Equal(["reviewer", "ops"], v.Roles.Select(r => r.Role));   // rule order
        Assert.Equal(3 * Min, v.NextCheckMs);
    }
}
