using CaptainHook.Core;
using CaptainHook.Mail;

namespace CaptainHook.Tests;

// ADR-0018 decision 6, slice `watcher-dead-mailbox-rule` (roadmap item 23,
// phase 3) — the brain's SECOND rule, driven with values like its first.
//
// The rule the brain already had is about a role falling behind, and it looks
// away from a dead cursor's held-forever mail on purpose (that is the strict
// reading of "unread", and the file comment says why). This is the pass that
// looks at it: the unit is one MAILBOX, the recipient is the `reaper`, and the
// output is a nudge that names the box — detection only, because disposition is
// judgement and belongs to a member (d6, and the "automatic reaping" rejected
// beside it).
//
// What the tests below pin, claim by claim:
//
//   * the four conditions — an instance mailbox WITH a cursor, not one an
//     operator registered, holding mail, its own session not live;
//   * consent is the REAPER's rule, because the reaper's tokens are what a nudge
//     spends; no rule ⇒ nothing, exactly as everywhere else in `watch.json`;
//   * envelopes are tracked under the ADDRESS, so two dead boxes holding one
//     broadcast keep separate quiet clocks and separate `perEnvelope` budgets,
//     while `perRoleHour` stays ONE window on the reaper — including for nudges
//     decided in the same evaluation, which are not in the state yet;
//   * `NudgeState.Record` follows the same key (`MailNudge.Subject`);
//   * the dead lane feeds the one `NextCheckMs` like everything else.
public class WatcherDeadMailboxTests
{
    private const long Min = 60_000;

    // ---- fixtures ------------------------------------------------------------

    private static PendingMail Mail(string id, long offset, string to = "reviewer",
        MailPriority priority = MailPriority.Urgent) =>
        new(offset, MailFixtures.Envelope(id: id, to: to, priority: priority,
            ttl: to.Contains('@') ? null : 3), null);

    private static WatchedMailbox Box(string role, string? instance, params PendingMail[] pending) =>
        new(new MailAddress(role, instance), pending);

    /// The gatherer's two stand-ins: a mailbox with no cursor file behind it.
    private static WatchedMailbox NoCursor(string role, string? instance, params PendingMail[] pending) =>
        new(new MailAddress(role, instance), pending, HasCursor: false);

    private static WatchRule Rule(string role = WatcherBrain.ReaperRole, string? priority = ">=urgent",
        int? quietForMs = (int)(10 * Min), int perEnvelope = 1, int perRoleHour = 4)
    {
        WatchPriority? p = priority switch
        {
            null => null,
            ">=urgent" => new WatchPriority(MailPriority.Urgent, true),
            "urgent" => new WatchPriority(MailPriority.Urgent, false),
            "ambient" => new WatchPriority(MailPriority.Ambient, false),
            _ => throw new ArgumentException(priority),
        };
        return new WatchRule(role, new WatchWhen(p, quietForMs, NoLiveSession: true), new WatchBudget(perEnvelope, perRoleHour));
    }

    private static readonly RoleKinds Robot = new(new HashSet<string>(), TurnPayloadInstalled: true);

    private static WatchInput Input(
        IReadOnlyList<WatchedMailbox> mailboxes, long now,
        IReadOnlyList<WatchRule>? rules = null, RoleKinds? kinds = null,
        NudgeState? state = null, IReadOnlyList<(string, long)>? presence = null) =>
        new(mailboxes, presence ?? [], kinds ?? Robot, rules ?? [Rule()], state ?? NudgeState.Empty, now);

    /// Every envelope in every box, first seen (and quiet since) `at`, keyed by
    /// the box's ADDRESS — the dead lane's subject.
    private static NudgeState SeenAt(IReadOnlyList<WatchedMailbox> boxes, long at, int nudged = 0) =>
        new(boxes.SelectMany(b => b.Pending.Select(p =>
            new WatchedEnvelope(b.Address.ToString(), p.Envelope.Id, at, at, nudged))).ToList(), []);

    // ---- consent -------------------------------------------------------------

    /// No rule for the reaper is the operator saying no — the same direction
    /// `watch.json` takes everywhere (d7): absence means nothing happens, and a
    /// rule for the DEAD role is not a rule for the reaper.
    [Fact]
    public void NoReaperRule_MeansNoDeadMailboxAtAll()
    {
        var box = Box("reviewer", "s-1", Mail("m-1", 0));
        var v = WatcherBrain.Evaluate(Input([box], now: 60 * Min,
            rules: [Rule(role: "reviewer")], state: SeenAt([box], 0)));

        Assert.Empty(v.Dead);
        Assert.Empty(v.Nudges);   // and the role rule found nothing unread it could act on
    }

    /// The reaper's channel has to exist at all — d3's gate, asked of the role
    /// the nudge would GO to. Named rather than passed over silently: a
    /// human-held reaper's window would learn nothing, since a dead-mailbox
    /// nudge puts no mail in the reaper's own box.
    [Theory]
    [InlineData(true, false, WatchStanding.HumanHeld)]    // a digest registration, no turn payload
    [InlineData(false, false, WatchStanding.Unserved)]    // nothing registered at all
    public void ReaperWithoutARobotChannel_ReportsButNeverNudges(bool human, bool payload, WatchStanding expected)
    {
        var kinds = new RoleKinds(
            human ? new HashSet<string> { WatcherBrain.ReaperRole } : [], TurnPayloadInstalled: payload);
        var box = Box("reviewer", "s-1", Mail("m-1", 0));
        var v = WatcherBrain.Evaluate(Input([box], now: 60 * Min, kinds: kinds, state: SeenAt([box], 0)));

        Assert.Equal(expected, Assert.Single(v.Dead).Standing);
        Assert.Empty(v.Nudges);
        Assert.Null(v.NextCheckMs);
    }

    // ---- the four conditions --------------------------------------------------

    [Fact]
    public void DeadMailbox_NudgesTheReaper_NamingTheBoxAndItsMail()
    {
        var box = Box("reviewer", "s-1", Mail("m-1", 0), Mail("m-2", 120));
        var v = WatcherBrain.Evaluate(Input([box], now: 60 * Min, state: SeenAt([box], 0)));

        var nudge = Assert.Single(v.Nudges);
        Assert.Equal(WatcherBrain.ReaperRole, nudge.Role);          // it goes to the reaper…
        Assert.Equal("reviewer@s-1", nudge.Address);                // …about somebody else's box
        Assert.Equal("reviewer@s-1", nudge.Subject);                // and that is the tracking key
        Assert.Equal(["m-1", "m-2"], nudge.EnvelopeIds);
        Assert.Equal(WatcherBrain.ReaperHow, nudge.ReplyHow);       // dispose, don't reply
        Assert.Contains("for 'reviewer@s-1'", nudge.Digest);        // the digest names whose mail it is
        Assert.Contains("dead-mailbox reviewer@s-1", nudge.Reason);

        var row = Assert.Single(v.Dead);
        Assert.Equal(WatchStanding.Nudge, row.Standing);
        Assert.Equal("reviewer", row.Role);                         // the box's role, never the reaper's
        Assert.Equal(2, row.Stranded);
    }

    /// A stand-in the gatherer invents is not a mailbox anybody can reap: the
    /// sessionless read of a role with no cursor, and an instance the LEDGER
    /// addresses that has never read. `mail reap` removes a cursor file; where
    /// there is none there is no standing to remove.
    [Fact]
    public void MailboxesWithoutACursorFile_AreNeverCandidates()
    {
        var sessionless = NoCursor("reviewer", null, Mail("m-1", 0));
        var addressedOnly = NoCursor("reviewer", "robot", Mail("u-1", 1, to: "reviewer@robot"));
        var v = WatcherBrain.Evaluate(Input([sessionless, addressedOnly], now: 60 * Min,
            state: SeenAt([sessionless, addressedOnly], 0)));

        Assert.Empty(v.Dead);
        Assert.Empty(v.Nudges);
    }

    /// A registered `--as` mailbox is standing an operator asked for, so mail
    /// waiting in it is waiting, not stranded. Without this every durable box
    /// would be a corpse the moment its window shut for the night — a named
    /// cursor's key is a name, not a session, so it can never look live.
    [Fact]
    public void RegisteredDurableMailbox_IsNeverDead()
    {
        var box = Box("reviewer", "robot", Mail("u-1", 0, to: "reviewer@robot"));
        var registered = new RoleKinds(new HashSet<string> { "reviewer" }, TurnPayloadInstalled: true)
        {
            RegisteredMailboxes = new HashSet<string> { "reviewer@robot" },
        };

        Assert.Empty(WatcherBrain.Evaluate(Input([box], now: 60 * Min, kinds: registered, state: SeenAt([box], 0))).Dead);

        // …and the very same box is a candidate once nothing registers it.
        Assert.Single(WatcherBrain.Evaluate(Input([box], now: 60 * Min, state: SeenAt([box], 0))).Dead);
    }

    [Fact]
    public void EmptyMailbox_IsNotACandidate()
    {
        Assert.Empty(WatcherBrain.Evaluate(Input([Box("reviewer", "s-1")], now: 60 * Min)).Dead);
    }

    /// The mailbox's own silence is what "dead" means, so the presence check is
    /// unconditional — `noLiveSession: false` cannot buy a nudge for a box whose
    /// window is right there. Armed for the instant that window stops being live.
    [Fact]
    public void LiveMailbox_IsNotDead_AndArmsItsPresenceExpiry()
    {
        var box = Box("reviewer", "s-1", Mail("m-1", 0));
        var rule = Rule() with { When = new WatchWhen(null, (int)(10 * Min), NoLiveSession: false) };
        var v = WatcherBrain.Evaluate(Input([box], now: 60 * Min, rules: [rule],
            state: SeenAt([box], 0), presence: [("s-1", 2 * Min)]));

        var row = Assert.Single(v.Dead);
        Assert.Equal(WatchStanding.LiveSession, row.Standing);
        Assert.Empty(v.Nudges);
        Assert.Equal(60 * Min + 8 * Min + 1, v.NextCheckMs);
    }

    /// Presence reaches a mailbox through ITS OWN key: a live sibling window of
    /// the same role says nothing about the box whose reader is gone.
    [Fact]
    public void ALiveSiblingWindow_DoesNotKeepADeadBoxAlive()
    {
        var dead = Box("reviewer", "s-1", Mail("m-1", 0));
        var alive = Box("reviewer", "s-2");
        var v = WatcherBrain.Evaluate(Input([dead, alive], now: 60 * Min,
            state: SeenAt([dead], 0), presence: [("s-2", 0)]));

        Assert.Equal("reviewer@s-1", Assert.Single(v.Nudges).Address);
    }

    // ---- thresholds and budgets ------------------------------------------------

    [Fact]
    public void NotPastQuiet_ArmsTheThreshold_AndNudgesNothing()
    {
        var box = Box("reviewer", "s-1", Mail("m-1", 0));
        var v = WatcherBrain.Evaluate(Input([box], now: 4 * Min, state: SeenAt([box], 0)));

        Assert.Equal(WatchStanding.NotDue, Assert.Single(v.Dead).Standing);
        Assert.Empty(v.Nudges);
        Assert.Equal(10 * Min, v.NextCheckMs);      // first seen at 0, quietFor 10m
    }

    [Fact]
    public void PriorityNoReaperRuleAdmits_IsStrandedButUngoverned()
    {
        var box = Box("reviewer", "s-1", Mail("m-1", 0, priority: MailPriority.Ambient));
        var v = WatcherBrain.Evaluate(Input([box], now: 60 * Min, state: SeenAt([box], 0)));

        var row = Assert.Single(v.Dead);
        Assert.Equal(WatchStanding.NotDue, row.Standing);
        Assert.Contains("no reaper rule admits", row.Detail);
        Assert.Null(v.NextCheckMs);                 // ungoverned: not due, not armed
    }

    [Fact]
    public void PerEnvelopeSpent_IsExhausted_AndArmsNothing()
    {
        var box = Box("reviewer", "s-1", Mail("m-1", 0));
        var v = WatcherBrain.Evaluate(Input([box], now: 60 * Min, state: SeenAt([box], 0, nudged: 1)));

        Assert.Equal(WatchStanding.Exhausted, Assert.Single(v.Dead).Standing);
        Assert.Empty(v.Nudges);
        Assert.Null(v.NextCheckMs);                 // only being read or reaped changes it
    }

    /// `perRoleHour` is ONE window on the reaper, and it has to bound nudges
    /// decided in the SAME evaluation too — the caller records them, so they are
    /// not in the state yet, and without this one pass could hand the reaper
    /// every dead box at once whatever the budget says.
    [Fact]
    public void PerRoleHour_BoundsTwoDeadBoxesInOnePass_AndArmsTheWindow()
    {
        var a = Box("reviewer", "s-1", Mail("m-1", 0));
        var b = Box("ops", "s-2", Mail("m-2", 1, to: "ops"));
        var v = WatcherBrain.Evaluate(Input([a, b], now: 60 * Min,
            rules: [Rule(perRoleHour: 1)], state: SeenAt([a, b], 0)));

        var nudge = Assert.Single(v.Nudges);
        Assert.Equal("ops@s-2", nudge.Address);     // ordinal by address: ops@s-2 before reviewer@s-1
        Assert.Contains("role 1/1 this hour", nudge.Reason);

        var held = v.Dead.Single(d => d.Address == "reviewer@s-1");
        Assert.Equal(WatchStanding.Exhausted, held.Standing);
        // The one already decided this pass counts at `now`, so THAT box is
        // armed for an hour from now — not from some older entry.
        Assert.Equal(60 * Min + WatcherBrain.RoleWindowMs, held.NextCheckMs);
        // The verdict's one deadline is still the nearest of everything armed:
        // the nudge it just emitted is due again a quiet period from now.
        Assert.Equal(70 * Min, v.NextCheckMs);
    }

    /// The reaper's own mailbox and a dead box share the one window: the role
    /// rule's nudge is counted before the dead pass decides.
    [Fact]
    public void TheReapersOwnMail_AndADeadBox_ShareTheRolesWindow()
    {
        var own = Box(WatcherBrain.ReaperRole, "s-9", Mail("r-1", 0, to: WatcherBrain.ReaperRole));
        var dead = Box("reviewer", "s-1", Mail("m-1", 1));
        var state = new NudgeState(
            [
                new(WatcherBrain.ReaperRole, "r-1", 0, 0, 0),   // the role rule's key
                new("reviewer@s-1", "m-1", 0, 0, 0),            // the dead lane's key
            ], []);
        var v = WatcherBrain.Evaluate(Input([own, dead], now: 60 * Min,
            rules: [Rule(perRoleHour: 1)], state: state));

        var nudge = Assert.Single(v.Nudges);
        Assert.Null(nudge.Address);                             // the ordinary role nudge won the slot
        Assert.Equal(WatchStanding.Exhausted, v.Dead.Single(d => d.Address == "reviewer@s-1").Standing);

        // …and the reaper's OWN mailbox is a mailbox like any other: its window
        // is gone too, so it is a candidate in its own right — first seen now,
        // so not yet due.
        Assert.Equal(WatchStanding.NotDue, v.Dead.Single(d => d.Address == $"{WatcherBrain.ReaperRole}@s-9").Standing);
    }

    // ---- the address is the key ------------------------------------------------

    /// Two dead boxes of one role holding the same broadcast are two corpses,
    /// not one: separate quiet clocks, separate `perEnvelope` budgets, separate
    /// nudges. Keying the state by role would have merged them.
    [Fact]
    public void TwoDeadBoxes_HoldingOneBroadcast_AreTrackedSeparately()
    {
        var a = Box("reviewer", "s-1", Mail("m-1", 0));
        var b = Box("reviewer", "s-2", Mail("m-1", 0));
        var v = WatcherBrain.Evaluate(Input([a, b], now: 60 * Min, state: SeenAt([a, b], 0)));

        Assert.Equal(["reviewer@s-1", "reviewer@s-2"], v.Nudges.Select(n => n.Address));
        Assert.Equal(2, v.State.Envelopes.Count(e => e.Id == "m-1"));

        // …and one of them being spent leaves the other due.
        var spentOne = new NudgeState(
            [new("reviewer@s-1", "m-1", 0, 0, 1), new("reviewer@s-2", "m-1", 0, 0, 0)], []);
        var again = WatcherBrain.Evaluate(Input([a, b], now: 60 * Min, state: spentOne));
        Assert.Equal("reviewer@s-2", Assert.Single(again.Nudges).Address);
    }

    /// `Record` follows the same key, and charges the ROLE's window — a bill is
    /// the reaper's, not a mailbox's.
    [Fact]
    public void Record_RestartsQuietOnTheAddressKey_AndChargesTheReapersWindow()
    {
        var box = Box("reviewer", "s-1", Mail("m-1", 0));
        var v = WatcherBrain.Evaluate(Input([box], now: 60 * Min, state: SeenAt([box], 0)));
        var after = v.State.Record(Assert.Single(v.Nudges), nowMs: 60 * Min);

        var entry = Assert.Single(after.Envelopes);
        Assert.Equal("reviewer@s-1", entry.Role);
        Assert.Equal(60 * Min, entry.QuietSinceMs);          // quiet restarted
        Assert.Equal(1, entry.Nudged);
        Assert.Equal(WatcherBrain.ReaperRole, Assert.Single(after.Nudges).Role);

        // The next evaluation waits the full quiet period again.
        var next = WatcherBrain.Evaluate(Input([box], now: 61 * Min, rules: [Rule(perEnvelope: 2)], state: after));
        Assert.Empty(next.Nudges);
        Assert.Equal(70 * Min, next.NextCheckMs);
    }

    /// An uncharged record (a dispatch `dispatch.json` denied) spends no budget
    /// but still restarts quiet, so a denial recurs once per period — the same
    /// rule the role lane has, on the same key.
    [Fact]
    public void UnchargedRecord_SpendsNothing_ButStillRestartsQuiet()
    {
        var box = Box("reviewer", "s-1", Mail("m-1", 0));
        var v = WatcherBrain.Evaluate(Input([box], now: 60 * Min, state: SeenAt([box], 0)));
        var after = v.State.Record(Assert.Single(v.Nudges), nowMs: 60 * Min, charged: false);

        Assert.Equal(0, Assert.Single(after.Envelopes).Nudged);
        Assert.Empty(after.Nudges);
        Assert.Equal(60 * Min, Assert.Single(after.Envelopes).QuietSinceMs);
    }

    /// The dead lane feeds the ONE deadline like everything else: the minimum
    /// across both rules is what the actor arms.
    [Fact]
    public void TheDeadLane_FeedsTheOneDeadline()
    {
        var roleBox = Box("reviewer", null, Mail("m-1", 0));                 // the role rule's, unread, 12m to go
        var deadBox = Box("ops", "s-2", Mail("m-2", 1, to: "ops"));          // the dead lane's, 3m to go
        var state = new NudgeState(
            [new("reviewer", "m-1", 0, 0, 0), new("ops@s-2", "m-2", 0, 0, 0)], []);
        var v = WatcherBrain.Evaluate(Input([roleBox, deadBox], now: 0,
            rules: [Rule(role: "reviewer", quietForMs: (int)(12 * Min)), Rule(quietForMs: (int)(3 * Min))],
            state: state));

        Assert.Empty(v.Nudges);
        Assert.Equal(3 * Min, v.NextCheckMs);
    }

    /// The state the dead lane keeps is the state it gets back: an envelope that
    /// leaves the mailbox leaves the memory (rule 2 — disarm is not an
    /// operation), and one that stays keeps its first sighting.
    [Fact]
    public void StateRoundTrips_AndForgetsWhatTheBoxNoLongerHolds()
    {
        var box = Box("reviewer", "s-1", Mail("m-1", 0), Mail("m-2", 1));
        var first = WatcherBrain.Evaluate(Input([box], now: 0));
        Assert.Equal(2, first.State.Envelopes.Count);
        Assert.All(first.State.Envelopes, e => Assert.Equal("reviewer@s-1", e.Role));

        var reaped = WatcherBrain.Evaluate(Input([Box("reviewer", "s-1", Mail("m-2", 1))], now: 5 * Min, state: first.State));
        var kept = Assert.Single(reaped.State.Envelopes);
        Assert.Equal("m-2", kept.Id);
        Assert.Equal(0, kept.FirstSeenMs);      // still first seen at 0, not re-sighted at 5m
    }
}
