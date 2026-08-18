using System.Text;
using CaptainHook.Mail;

namespace CaptainHook.Core;

// Roadmap item 22 / ADR-0017 decision 4, slice `watcher-brain` — the WATCHER's
// decision, as a pure function:
//
//     (pending, presence, roleKind, monotonicNow, nudgeState, rules) → nudges
//
// Everything in this file is values in, values out. No I/O, no clock, no
// randomness: the caller reads the store, the cursors, the registrations and
// presence, and hands the brain a `WatchInput`; the brain hands back the nudges
// to raise NOW, the state to remember, and ONE deadline. That is what makes it
// golden-testable off the same real store/cursors/digest the reducer's fixtures
// are derived from, and it is what keeps house invariant 2: the only time this
// file ever sees is `NowMs`, a monotonic number the caller supplies.
//
// **The protocol with the actor that will host it (phase 5) is fixed here, in
// three rules, so no later phase can redefine the state's shape (the plan's
// warning) or the arm/disarm/re-arm choreography:**
//
//   1. ONE deadline. `WatchVerdict.NextCheckMs` is the earliest monotonic
//      instant at which re-running the brain could change its answer — the
//      nearest quiet threshold, the nearest presence expiry, the nearest budget
//      window release, whichever comes first — or null when nothing is armed.
//      The actor arms exactly that and REPLACES whatever it held. A second
//      envelope arriving while a deadline is armed therefore re-arms (the brain
//      re-runs on the `mail.append`, and the new minimum wins); it can never
//      double-arm, because there is only ever one number to hold.
//   2. DISARM is not an operation. A cursor advance that clears the condition
//      simply makes the next evaluation return no deadline for that envelope
//      (it leaves the state — see `NudgeState`), and the actor's one armed
//      deadline, if it fires, finds nothing due and gets a new (or null) one.
//   3. `NudgeState.Record` is the caller's, and it is called AFTER the caller
//      has decided a nudge really happened. The verdict's `State` tracks every
//      unread envelope but charges no budget and resets no quiet clock for the
//      nudges it emits — because a nudge the dispatcher's policy DENIES should
//      not spend a budget the operator's rules refused to let it use
//      (`MailNudgeOutcome.Ran`), and the brain cannot know that. Persist-then-
//      dispatch ordering, and what to do on a denial, is phase 5's; the brain
//      gives it a state that is honest either way.
//
// **What "unread" means here, and why it is the strict reading.** A role may
// have many mailboxes — one per window that ever read it, plus any `--as`
// durable ones (ADR-0018 d3) — and pending belongs to a CURSOR, not to a role
// (the 2026-08-17 field report's lesson). The brain calls an envelope unread
// for a role when it is pending in EVERY mailbox of that role that accepts it.
// One reader having taken delivery is the role having heard it, and the
// watcher's job — "someone is falling behind" — is done for that envelope. The
// alternative (unread if pending ANYWHERE) would wake a robot for mail a human
// read ten minutes ago in a window that then went quiet, and would count a
// dead cursor's held-forever mail (the reaper's shape, 0018 d6) as a reason to
// spend tokens. Fewer nudges is the conservative direction for a channel that
// bills the owner, and it is the direction every other choice in this ADR
// takes. A unicast (`role@instance`) is accepted by one mailbox, so it is
// unread exactly while THAT mailbox holds it. A role with no mailbox at all is
// read sessionless by the gatherer, and everything ever addressed to it is
// unread — which is the truth for a role nobody has ever picked up.
//
// **What "quiet" means.** `quietFor` counts from the moment the brain FIRST
// SAW the envelope unread for the role (`QuietSinceMs`), and again from the
// moment a nudge was recorded — so a second poke (when `perEnvelope` allows
// one) waits the same quiet period the first did, rather than firing on the
// very next evaluation. It does not count from the envelope's `ts`, which is
// a sender's wall clock and never compared with anything (invariant 2), and it
// does not reset when a window merely dispatches — that is presence's job.
//
// **What "live" means.** A session is live when its freshest dispatch is
// within `LiveWithinMs`, which mirrors the canvas's idle→stale boundary
// (`PRESENCE_IDLE_MS` in web/src/mail.ts) so "someone is home" is one answer
// on the picture and in the decision. The comparison itself is
// `RolePresence.AnyLiveSession` — the one line in the codebase that turns an
// age into an answer. A named (`--as`) mailbox never looks live, correctly: a
// durable mailbox is a mailbox, not a window.
//
// **Four limits the skeptic pass named, kept rather than papered over** (each
// errs toward a nudge the operator can see on the trail, or toward none):
//
//   * Presence is attributed to a role THROUGH ITS CURSORS (`RolePresence`), and
//     a cursor exists only once something was delivered. A window of a mixed
//     role that has fired hooks but never yet been handed mail is invisible to
//     `noLiveSession`, and so is one whose only cursor a reaper just removed.
//     Nothing else in the daemon says which role a session holds; the actor
//     (phase 5) is where a cwd→policy→role attribution could be added if the
//     field report shows this biting.
//   * A role whose LAST cursor is reaped is read sessionless from the anchor,
//     so its whole retained broadcast history reads as unread — the same
//     "fresh anchor redelivers everything" that ADR-0018 d6 says of a reap. The
//     digest cap and the budgets bound what a nudge carries; the reaper's own
//     payloads (0018 `reaper-payloads`) are where "dispose, then reap" keeps
//     this from being a surprise.
//   * Quiet accrues only while the daemon runs (see `NudgeState.ToAges`), and
//     the daemon idle-exits. A `quietFor` longer than the idle window can only
//     be reached while OTHER activity keeps the daemon up — ADR-0017 N2's
//     "late" sharpened to "possibly never" for long thresholds; the resident
//     watcher N2 defers is the remedy, when measured.
//   * `perRoleHour` is ONE window per role. Two rules naming one role with
//     different budgets share it, and the strictest bound applies; a generous
//     rule's spend can therefore hold a strict rule's mail until the window
//     frees. Write one budget per role.
//
// **The second rule this file holds: DEAD MAILBOXES** (ADR-0018 d6, slice
// `watcher-dead-mailbox-rule`). The rule above is about a ROLE falling behind,
// and by design it looks away from exactly the case the 2026-08-17 field report
// found four of: a window that died holding mail. Its held-forever pending is
// not "unread for the role" (some live window may have read the same broadcast
// long ago), and no nudge to that role would help anyway — the reader is gone.
// So `DeadMailboxes` is a second pass with a different unit and a different
// recipient: the MAILBOX, reported to the `reaper`, who decides disposition.
// Detection is deterministic and the watcher's; deletion is nobody's automatic
// business (d6, and the rejected alternative beside it) — the brain's whole
// output is one nudge naming the box, and `mail reap` is a member's verb.
// The conditions, the reason a registered `--as` box is never a corpse, and the
// two consequences accepted are on `DeadMailboxes` itself.

/// One mailbox as the watcher sees it: its address — the role, and the instance
/// that IS its cursor key (ADR-0018 d3) — and the envelopes that mailbox has not
/// consumed. A sessionless read (`Instance` null) is the gatherer's stand-in for
/// a role that has no cursor at all.
/// `HasCursor` is whether a cursor FILE stands for this mailbox. False for the
/// gatherer's two stand-ins — the sessionless read of a role nobody has picked
/// up, and an instance the ledger addresses that has never read — and it is the
/// dead-mailbox rule's precondition: `mail reap` removes a cursor, so a mailbox
/// with none has no standing to remove and is not a corpse, whatever is
/// addressed to it. Defaulted true because every mailbox that came from a cursor
/// file is one.
public sealed record WatchedMailbox(
    MailAddress Address, IReadOnlyList<PendingMail> Pending, bool HasCursor = true);

/// The brain's memory of one envelope for one role: when it first went unread,
/// when its quiet clock last (re)started, and how often it has been nudged.
/// All stamps are MONOTONIC milliseconds of the process that wrote them — see
/// `NudgeState.ToAges` for how they cross a restart.
public sealed record WatchedEnvelope(string Role, string Id, long FirstSeenMs, long QuietSinceMs, int Nudged);

/// One nudge that was recorded for a role, for the sliding `perRoleHour` window.
public sealed record RoleNudge(string Role, long AtMs);

/// The same two lists as durations from a moment rather than stamps of one —
/// the ONLY form in which the state may leave the process (phase 4's
/// `nudges.jsonl` writes exactly this). See `NudgeState.ToAges`.
public sealed record WatchedEnvelopeAges(string Role, string Id, long UnreadForMs, long QuietForMs, int Nudged);
public sealed record RoleNudgeAges(string Role, long AgoMs);
public sealed record NudgeStateAges(IReadOnlyList<WatchedEnvelopeAges> Envelopes, IReadOnlyList<RoleNudgeAges> Nudges);

/// What the brain remembers between evaluations. Immutable; every operation
/// returns a new one.
public sealed record NudgeState(IReadOnlyList<WatchedEnvelope> Envelopes, IReadOnlyList<RoleNudge> Nudges)
{
    public static readonly NudgeState Empty = new([], []);

    /// The caller says a nudge happened. Every envelope it named gets its quiet
    /// clock restarted at `nowMs`, and — when `charged` — its `Nudged` count
    /// incremented and one entry added to the role's sliding window. An
    /// uncharged record (a dispatch that policy denied) still restarts the quiet
    /// clock, so a denial the operator can see on the trail (`nudge.denied`)
    /// recurs once per quiet period rather than on every evaluation — with a
    /// `quietFor` of zero those are the same thing, and the actor that owns the
    /// dispatch outcome is where a repeated denial should be charged.
    ///
    /// An envelope the state does not track (the caller recorded a nudge the
    /// brain did not emit) is ignored: the state tracks unread envelopes, and
    /// only the brain decides what is unread.
    /// The envelopes are matched on the nudge's SUBJECT — its role, or the dead
    /// mailbox's address when it has one (`MailNudge.Subject`) — because that is
    /// the key the brain tracked them under. The sliding window entry is the
    /// ROLE's either way: `perRoleHour` bounds what the reaper is woken for, and
    /// a bill is a role's, not a mailbox's.
    public NudgeState Record(MailNudge nudge, long nowMs, bool charged = true)
    {
        var ids = new HashSet<string>(nudge.EnvelopeIds, StringComparer.Ordinal);
        var subject = nudge.Subject;
        var envelopes = Envelopes.Select(e =>
            e.Role == subject && ids.Contains(e.Id)
                ? e with { QuietSinceMs = nowMs, Nudged = charged ? e.Nudged + 1 : e.Nudged }
                : e).ToList();
        IReadOnlyList<RoleNudge> nudges = charged
            ? [.. Nudges, new RoleNudge(nudge.Role, nowMs)]
            : Nudges;
        return new NudgeState(envelopes, nudges);
    }

    /// **How the state crosses a restart without a wall clock (invariant 2).**
    /// A monotonic stamp is meaningless in another process — the epoch is the
    /// boot, or the process, and neither survives — so the state never leaves
    /// this process as stamps. It leaves as DURATIONS measured from the moment
    /// it was written (`ToAges(now)`), and comes back as stamps re-derived from
    /// the moment it was read (`FromAges(ages, now)`). The arithmetic is two
    /// subtractions on one clock each; nothing is compared across clocks and
    /// `DateTime` never appears.
    ///
    /// The consequence is stated rather than hidden: TIME THE DAEMON WAS NOT
    /// RUNNING IS NOT COUNTED. An envelope that had been quiet six of its ten
    /// minutes when the daemon idle-exited is quiet six minutes when it comes
    /// back, and is due four minutes after start — not at start. That is the
    /// honest reading of "quiet": the watcher watched it be quiet for six
    /// minutes; it did not watch the gap. A deadline that HAD fallen before the
    /// exit (quiet ≥ threshold) is due at once on the next start, which is
    /// what N2 promises. Budget windows stretch across the gap by the same
    /// rule — fewer nudges, again the conservative direction.
    public NudgeStateAges ToAges(long nowMs) => new(
        Envelopes.Select(e => new WatchedEnvelopeAges(
            e.Role, e.Id, Math.Max(0, nowMs - e.FirstSeenMs), Math.Max(0, nowMs - e.QuietSinceMs), e.Nudged)).ToList(),
        Nudges.Select(n => new RoleNudgeAges(n.Role, Math.Max(0, nowMs - n.AtMs))).ToList());

    public static NudgeState FromAges(NudgeStateAges ages, long nowMs) => new(
        ages.Envelopes.Select(e => new WatchedEnvelope(
            e.Role, e.Id, nowMs - Math.Max(0, e.UnreadForMs), nowMs - Math.Max(0, e.QuietForMs), Math.Max(0, e.Nudged))).ToList(),
        ages.Nudges.Select(n => new RoleNudge(n.Role, nowMs - Math.Max(0, n.AgoMs))).ToList());
}

/// Everything one evaluation reads. `Presence` is `SessionPresence.Recent()`'s
/// shape (session, age) so the daemon hands its own view over unchanged; the
/// CLI's `--once` hands over what it can honestly claim (the calling window,
/// age 0) and says so.
public sealed record WatchInput(
    IReadOnlyList<WatchedMailbox> Mailboxes,
    IReadOnlyList<(string Session, long AgeMs)> Presence,
    RoleKinds Kinds,
    IReadOnlyList<WatchRule> Rules,
    NudgeState State,
    long NowMs);

/// Where one rule-bearing role stands after an evaluation — the closed set of
/// reasons the brain did or did not nudge it, so `--once` and a trail line can
/// name it and a test can assert on it.
public enum WatchStanding
{
    /// Nothing unread for the role.
    NoMail,
    /// The role is human-held: the robot channel does not exist for it (d3), the
    /// count is the nudge. Never a robot, whatever the rules say.
    HumanHeld,
    /// Nobody reads it and nothing can be woken for it (no turn payload).
    Unserved,
    /// Unread mail, none of it past its threshold yet (or none matching a
    /// rule's priority) — a deadline is armed for the nearest threshold.
    NotDue,
    /// Due mail, but a session for the role is live and the rule says
    /// `noLiveSession` — armed for when presence expires.
    LiveSession,
    /// Due mail, but the budget is spent: `perEnvelope` (armed for nothing —
    /// only being read changes it) or `perRoleHour` (armed for the window).
    Exhausted,
    /// A nudge is emitted for the role.
    Nudge,
}

/// One role's line of the verdict, for display and for the trail.
public sealed record WatchRoleVerdict(
    string Role,
    RoleKind Kind,
    WatchStanding Standing,
    int Unread,
    int Due,
    long? FreshestDispatchAgeMs,
    long? NextCheckMs,
    string Detail);

/// One dead-mailbox candidate's line of the verdict (ADR-0018 d6). `Address` is
/// the mailbox the reaper would be woken about; `Role` is whose role it is —
/// never the `reaper` role, which is who the nudge GOES to.
public sealed record WatchDeadMailbox(
    string Address,
    string Role,
    WatchStanding Standing,
    int Stranded,
    long? FreshestDispatchAgeMs,
    long? NextCheckMs,
    string Detail);

/// The whole answer: nudges to raise now, the state to keep (unread envelopes
/// tracked; nudges NOT yet recorded — see the file comment), the one deadline,
/// a line per rule-bearing role, and a line per dead-mailbox candidate.
public sealed record WatchVerdict(
    IReadOnlyList<MailNudge> Nudges,
    NudgeState State,
    long? NextCheckMs,
    IReadOnlyList<WatchRoleVerdict> Roles,
    IReadOnlyList<WatchDeadMailbox> Dead);

public static class WatcherBrain
{
    /// A session whose freshest dispatch is this recent is "home". The same
    /// number as the canvas's idle→stale boundary (`PRESENCE_IDLE_MS`,
    /// web/src/mail.ts) so the picture and the decision agree about who is here
    /// — to within the boundary millisecond, which is `RolePresence`'s `<=`
    /// against the canvas's `<`; the comparison lives there, not here.
    public const long LiveWithinMs = 10 * 60_000;

    /// `perRoleHour` is an hour, sliding: a nudge counts against the role until
    /// this much monotonic time has passed since it was recorded.
    public const long RoleWindowMs = 60 * 60_000;

    /// The role a dead mailbox is reported to (ADR-0018 d6). Detection is the
    /// watcher's and it is deterministic; DISPOSITION is judgement and belongs to
    /// a member, so the brain's whole output here is one nudge naming the box.
    /// The name is fixed rather than configurable: it is the role an operator
    /// writes a `watch.json` rule for and registers a turn payload against, and a
    /// second spelling of it would be a fact declared twice.
    public const string ReaperRole = "reaper";

    /// The instruction a woken turn is handed for answering. Constant on
    /// purpose — the payload is `claude -p "<digest>"` and the reply path is the
    /// bus's own verb, the same for every nudge.
    public const string ReplyHow =
        "Answer on the bus, not on stdout: pipe one JSON envelope to `captainHook mail send` — "
        + "\"kind\": \"answer\", \"inReplyTo\": the id you are answering, \"to\": the `reply to` address "
        + "in that message's head (else the sender's role).";

    /// What a woken reaper is asked to do instead — d6's three dispositions and
    /// the verb that removes the standing. A `mail-nudge` payload therefore needs
    /// no branch on which kind of nudge it got: the instruction is always in the
    /// same field, and `address` says which mailbox it is about.
    public const string ReaperHow =
        "This mailbox is dead: the window that held it is gone and the mail above is stranded there. "
        + "For each message decide FORWARD (pipe a NEW envelope to `captainHook mail send` addressed to a live "
        + "role or role@instance, carrying \"forwardedFrom\": {\"id\": <the original id>, \"address\": <this mailbox>}), "
        + "DROP (leave it — the ledger keeps it either way), or HOLD (do nothing at all and say so). "
        + "Then remove the standing with `captainHook mail reap <role@instance>` unless you held it.";

    /// The decision. Pure and deterministic: equal inputs give equal verdicts.
    public static WatchVerdict Evaluate(WatchInput input)
    {
        var now = input.NowMs;

        // Rules grouped by role, first appearance first, order within a role
        // preserved: `WatchRules` keeps document order so first-match-wins
        // needs no second read of the file.
        var rulesByRole = new Dictionary<string, List<WatchRule>>(StringComparer.Ordinal);
        var roleOrder = new List<string>();
        foreach (var rule in input.Rules)
        {
            if (!rulesByRole.TryGetValue(rule.Role, out var list))
            {
                rulesByRole[rule.Role] = list = [];
                roleOrder.Add(rule.Role);
            }
            list.Add(rule);
        }

        // First entry wins on a duplicated (role, id): the state will one day
        // come off a file (phase 4), and a doubled line must not take the
        // brain down.
        var prior = new Dictionary<(string Role, string Id), WatchedEnvelope>();
        foreach (var e in input.State.Envelopes) prior.TryAdd((e.Role, e.Id), e);
        var tracked = new List<WatchedEnvelope>();
        var nudges = new List<MailNudge>();
        var reports = new List<WatchRoleVerdict>();
        long? nextCheck = null;

        // The sliding window, pruned. Entries at or past the window are spent
        // and forgotten; the ones kept are exactly the ones a `perRoleHour`
        // count reads.
        var window = input.State.Nudges.Where(n => now - n.AtMs < RoleWindowMs).ToList();

        // Nudges this evaluation has already emitted, per role. They are not in
        // `window` — the caller records them, not the brain (rule 3) — but a
        // second nudge decided in the same pass must still count against the
        // first one's `perRoleHour`, or one evaluation could hand the reaper
        // every dead mailbox at once whatever the budget says.
        var emitted = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var role in roleOrder)
        {
            var rules = rulesByRole[role];
            var mailboxes = input.Mailboxes.Where(m => m.Address.Role == role).ToList();
            var unread = Unread(mailboxes);
            var kind = input.Kinds.Of(role);

            // Track every unread envelope, whatever the kind and whatever the
            // rule says about it: quiet accrues from the first sighting, and a
            // role whose registrations change (a turn payload installed, a
            // rule's threshold lowered) should not have its clocks restart
            // because the brain declined to remember.
            var entries = new List<(WatchedEnvelope Entry, PendingMail Mail)>();
            foreach (var mail in unread)
            {
                var id = mail.Envelope.Id;
                var entry = prior.TryGetValue((role, id), out var have)
                    ? have
                    : new WatchedEnvelope(role, id, now, now, 0);
                entries.Add((entry, mail));
                tracked.Add(entry);
            }

            if (unread.Count == 0)
            {
                reports.Add(new WatchRoleVerdict(role, kind, WatchStanding.NoMail, 0, 0, null, null, "nothing unread"));
                continue;
            }

            var freshest = RolePresence.FreshestDispatchAgeMs(
                role, mailboxes.Select(m => (m.Address.Role, m.Address.Instance)).ToList(), input.Presence);

            if (!input.Kinds.RobotChannelExists(role))
            {
                var (standing, why) = kind == RoleKind.HumanHeld
                    ? (WatchStanding.HumanHeld, "human-held — the count is the nudge; never a robot")
                    : (WatchStanding.Unserved, "unserved — no window reads it and no turn payload is installed");
                reports.Add(new WatchRoleVerdict(role, kind, standing, unread.Count, 0, freshest, null, why));
                continue;
            }

            // Per envelope: the first rule whose priority filter admits it
            // decides its threshold and its budget. No admitting rule ⇒ the
            // envelope is unread but ungoverned — not due, not armed.
            // `perEnvelope` is per envelope×role and nothing but being read
            // changes it, so a spent envelope is neither due nor armed — whatever
            // its quiet clock says.
            var triage = TriageEntries(entries, rules, now);
            var due = triage.Due;
            var roleNext = triage.NextCheckMs;
            var notDue = triage.NotDue;
            var spent = triage.Spent;

            if (due.Count == 0)
            {
                if (notDue == 0 && spent > 0)
                {
                    Report(WatchStanding.Exhausted, 0,
                        $"{spent} unread, all past their perEnvelope budget — only being read changes that");
                    continue;
                }
                var detail = notDue == 0
                    ? "unread, but no rule admits its priority"
                    : $"{notDue} unread, none past its quiet threshold"
                      + (spent > 0 ? $" · {spent} past their perEnvelope budget" : "");
                Report(WatchStanding.NotDue, 0, detail);
                continue;
            }

            // Presence, per rule: only rules that say `noLiveSession` care.
            var live = RolePresence.AnyLiveSession(
                role, mailboxes.Select(m => (m.Address.Role, m.Address.Instance)).ToList(), input.Presence,
                TimeSpan.FromMilliseconds(LiveWithinMs));
            var afterPresence = new List<(WatchedEnvelope Entry, PendingMail Mail, WatchRule Rule)>();
            var heldByPresence = 0;
            foreach (var d in due)
            {
                if (d.Rule.When.NoLiveSession && live) heldByPresence++;
                else afterPresence.Add(d);
            }
            if (heldByPresence > 0 && freshest is { } age)
                // The instant the freshest session stops being live: `AnyLiveSession`
                // is `age <= within`, so one millisecond past equality.
                roleNext = Min(roleNext, now + (LiveWithinMs - age) + 1);
            if (afterPresence.Count == 0)
            {
                Report(WatchStanding.LiveSession, due.Count,
                    $"{due.Count} due, but a session is live ({Dur(freshest ?? 0)} ago) and the rule says noLiveSession");
                continue;
            }

            // `perRoleHour` is the role's sliding window — ONE window per role,
            // shared by every rule that names the role (a budget is the role's
            // bill, not a rule's); the strictest bound among the rules that
            // admitted the due mail applies, and it arms for the moment enough
            // in-window nudges age out to free one slot.
            var inWindow = window.Where(n => n.Role == role).OrderBy(n => n.AtMs).ToList();
            var perRoleHour = afterPresence.Min(d => d.Rule.Budget.PerRoleHour);
            if (inWindow.Count >= perRoleHour)
            {
                var release = WindowRelease(inWindow.Select(n => n.AtMs).ToList(), perRoleHour);
                roleNext = Min(roleNext, release);
                Report(WatchStanding.Exhausted, due.Count,
                    $"{afterPresence.Count} due, but perRoleHour {perRoleHour} is spent — window frees in {Dur(release - now)}");
                continue;
            }

            // Nudge. Envelopes in ledger order; the digest rendered by the real
            // renderer (one spelling of "here is your mail"), and the reason
            // deterministic so the trail row and the golden agree byte for byte.
            //
            // The digest is CAPPED at whole items, and a nudge names and charges
            // ONLY what it carries: an envelope the cap held back was never
            // shown to anybody, so it stays due — un-nudged, its quiet clock
            // untouched — and the verdict arms `now` so the next evaluation
            // carries the remainder (inside the role's window, like any nudge).
            var candidates = afterPresence.OrderBy(d => d.Mail.Offset).ToList();
            var (digest, rendered) = RenderDigest(role, candidates.Select(d => d.Mail).ToList());
            var chosen = candidates.Take(rendered).ToList();
            var heldByCap = candidates.Count - rendered;
            var ids = chosen.Select(d => d.Mail.Envelope.Id).ToList();
            var perEnvelope = chosen.Min(d => d.Rule.Budget.PerEnvelope);
            var envelopeUse = Math.Min(chosen.Max(d => d.Entry.Nudged) + 1, perEnvelope);
            var quietMin = chosen.Min(d => now - d.Entry.QuietSinceMs);
            var reason =
                $"{ids.Count} unread past quiet ({Dur(quietMin)}+)"
                + (chosen.Any(d => d.Rule.When.NoLiveSession) ? " · no live session" : " · live session allowed")
                + $" · budget envelope {envelopeUse}/{perEnvelope} · role {inWindow.Count + 1}/{perRoleHour} this hour"
                + (heldByCap > 0 ? $" · {heldByCap} more due, held for the next nudge by the digest cap" : "")
                + (spent > 0 ? $" · {spent} more unread but spent" : "")
                + (heldByPresence > 0 ? $" · {heldByPresence} more due but held for a live session" : "");
            nudges.Add(new MailNudge(role, ids, reason, digest, ReplyHow));
            emitted[role] = emitted.GetValueOrDefault(role) + 1;

            // Projected re-arm: when the caller records this nudge — charged or
            // not — each named envelope's quiet clock restarts now, so it is due
            // again after its threshold. Armed unconditionally rather than only
            // when budget remains: an uncharged record (a denied dispatch) keeps
            // the budget and IS due again then, and a charged one that spent
            // its last poke wakes once into `Exhausted`, which is cheap. With
            // `quietFor: 0` this is `now`, and a repeatedly denied nudge would
            // re-run per evaluation — that pairing is the operator's rule
            // meeting their own policy, visible as `nudge.denied` on the trail,
            // and the actor that owns the dispatch outcome is where a repeated
            // denial gets charged.
            foreach (var d in chosen)
                roleNext = Min(roleNext, now + (d.Rule.When.QuietForMs ?? 0));
            if (heldByCap > 0) roleNext = Min(roleNext, now);

            Report(WatchStanding.Nudge, due.Count, reason);

            void Report(WatchStanding standing, int dueCount, string detail)
            {
                reports.Add(new WatchRoleVerdict(role, kind, standing, unread.Count, dueCount, freshest, roleNext, detail));
                if (roleNext is { } rn) nextCheck = Min(nextCheck, rn);
            }
        }

        var dead = DeadMailboxes(input, rulesByRole.GetValueOrDefault(ReaperRole) ?? [],
            prior, tracked, nudges, emitted, window, ref nextCheck);

        return new WatchVerdict(nudges, new NudgeState(tracked, window), nextCheck, reports, dead);
    }

    /// **The dead-mailbox rule (ADR-0018 d6).** A mailbox whose reader is gone
    /// still holds mail, and nothing in the rule above will ever notice: the
    /// strict reading of "unread" deliberately does NOT count a dead cursor's
    /// held-forever mail as a reason to wake the role, and the mail is often
    /// already read by some live window of the same role. So this is a second,
    /// separate pass with a different unit — the MAILBOX, not the role's mail —
    /// and a different recipient: the nudge goes to the `reaper`, whose job is
    /// disposition, and names the box in `MailNudge.Address`.
    ///
    /// A mailbox is a candidate when all four of d6's conditions hold:
    ///
    ///   * it is an INSTANCE mailbox with a cursor file (`HasCursor`) — `mail
    ///     reap` removes a cursor, so a box with none has no standing to remove;
    ///     the gatherer's sessionless read and its ledger-addressed stand-ins are
    ///     therefore never candidates, however much is addressed to them;
    ///   * it is not one an operator REGISTERED (`--as`): a declared durable
    ///     mailbox is standing somebody asked for, so mail waiting in it is
    ///     waiting rather than stranded. This is not a nicety — a named box never
    ///     looks live (its key is a name, not a session), so without this every
    ///     `--as` mailbox with pending mail would be a corpse the moment its
    ///     window closed for the night;
    ///   * it holds pending mail;
    ///   * and no session of ITS OWN address is live. That check is
    ///     unconditional here, not `noLiveSession`'s: the mailbox's silence is
    ///     what "dead" means, so a rule that set `noLiveSession: false` cannot
    ///     buy a nudge for a box whose window is right there.
    ///
    /// Everything else is the ordinary machinery, deliberately: the same
    /// `watch.json` rules (the reaper's own — the rule that consents to spending
    /// the reaper's tokens, never the dead role's, which is usually human-held
    /// and has no rule at all), the same triage, the same budgets, the same
    /// digest renderer, the same state. Two consequences are stated rather than
    /// hidden: a reaper window being live does NOT hold a dead-mailbox nudge,
    /// because there is nothing in the reaper's own mailbox for a live window to
    /// see and holding would mean nobody ever tends the box; and the envelopes
    /// are tracked under the ADDRESS (`MailNudge.Subject`), so two dead boxes
    /// holding the same broadcast each get their own quiet clock and their own
    /// `perEnvelope` budget, while `perRoleHour` stays one window on the reaper.
    private static IReadOnlyList<WatchDeadMailbox> DeadMailboxes(
        WatchInput input,
        IReadOnlyList<WatchRule> rules,
        Dictionary<(string Role, string Id), WatchedEnvelope> prior,
        List<WatchedEnvelope> tracked,
        List<MailNudge> nudges,
        Dictionary<string, int> emitted,
        List<RoleNudge> window,
        ref long? nextCheck)
    {
        // No rule for the reaper is the operator saying no, exactly as it is for
        // every other role (d7): absent means zero robot nudges, and there is no
        // second place to look.
        if (rules.Count == 0) return [];

        var now = input.NowMs;
        var reports = new List<WatchDeadMailbox>();
        var candidates = input.Mailboxes
            .Where(m => m.Address.Instance is not null && m.HasCursor && m.Pending.Count > 0)
            .Where(m => !input.Kinds.IsRegisteredMailbox(m.Address))
            .OrderBy(m => m.Address.ToString(), StringComparer.Ordinal)
            .ToList();

        foreach (var box in candidates)
        {
            var address = box.Address.ToString();
            IReadOnlyList<(string Role, string? Session)> cursor = [(box.Address.Role, box.Address.Instance)];
            var freshest = RolePresence.FreshestDispatchAgeMs(box.Address.Role, cursor, input.Presence);
            long? next = null;

            void Report(WatchStanding standing, string detail) =>
                reports.Add(new WatchDeadMailbox(
                    address, box.Address.Role, standing, box.Pending.Count, freshest, next, detail));

            // The reaper channel has to exist at all — d3's gate, asked of the
            // role the nudge would GO to. A human-held reaper is a role whose
            // window would learn nothing (a dead-mailbox nudge puts no mail in
            // the reaper's own box), which is worth naming rather than passing
            // over in silence.
            if (!input.Kinds.RobotChannelExists(ReaperRole))
            {
                Report(input.Kinds.Of(ReaperRole) == RoleKind.HumanHeld ? WatchStanding.HumanHeld : WatchStanding.Unserved,
                    $"a rule names {ReaperRole}, but no turn payload is installed for it — nothing can be woken to tend this box");
                continue;
            }

            if (RolePresence.AnyLiveSession(box.Address.Role, cursor, input.Presence, TimeSpan.FromMilliseconds(LiveWithinMs)))
            {
                next = Min(next, now + (LiveWithinMs - (freshest ?? 0)) + 1);
                nextCheck = Min(nextCheck, next!.Value);
                Report(WatchStanding.LiveSession,
                    $"{box.Pending.Count} pending, but this mailbox's own session dispatched {Dur(freshest ?? 0)} ago — not dead");
                continue;
            }

            var entries = new List<(WatchedEnvelope Entry, PendingMail Mail)>();
            foreach (var mail in box.Pending)
            {
                var entry = prior.TryGetValue((address, mail.Envelope.Id), out var have)
                    ? have
                    : new WatchedEnvelope(address, mail.Envelope.Id, now, now, 0);
                entries.Add((entry, mail));
                tracked.Add(entry);
            }

            var triage = TriageEntries(entries, rules, now);
            next = triage.NextCheckMs;
            if (triage.Due.Count == 0)
            {
                if (triage.NotDue == 0 && triage.Spent > 0)
                    Report(WatchStanding.Exhausted,
                        $"{triage.Spent} stranded, all past their perEnvelope budget — only being read or reaped changes that");
                else
                    Report(WatchStanding.NotDue, triage.NotDue == 0
                        ? "stranded, but no reaper rule admits its priority"
                        : $"{triage.NotDue} stranded, none past its quiet threshold"
                          + (triage.Spent > 0 ? $" · {triage.Spent} past their perEnvelope budget" : ""));
                if (next is { } armed) nextCheck = Min(nextCheck, armed);
                continue;
            }

            var already = emitted.GetValueOrDefault(ReaperRole);
            var inWindow = window.Where(n => n.Role == ReaperRole).OrderBy(n => n.AtMs).Select(n => n.AtMs).ToList();
            var perRoleHour = triage.Due.Min(d => d.Rule.Budget.PerRoleHour);
            var spentSlots = inWindow.Count + already;
            if (spentSlots >= perRoleHour)
            {
                // The nudges this pass already decided count too, at `now`.
                var stamps = inWindow.Concat(Enumerable.Repeat(now, already)).ToList();
                var release = WindowRelease(stamps, perRoleHour);
                next = Min(next, release);
                nextCheck = Min(nextCheck, next!.Value);
                Report(WatchStanding.Exhausted,
                    $"{triage.Due.Count} stranded and due, but the {ReaperRole}'s perRoleHour {perRoleHour} is spent — window frees in {Dur(release - now)}");
                continue;
            }

            var ordered = triage.Due.OrderBy(d => d.Mail.Offset).ToList();
            var (digest, rendered) = RenderDigest(address, ordered.Select(d => d.Mail).ToList());
            var chosen = ordered.Take(rendered).ToList();
            var heldByCap = ordered.Count - rendered;
            var ids = chosen.Select(d => d.Mail.Envelope.Id).ToList();
            var perEnvelope = chosen.Min(d => d.Rule.Budget.PerEnvelope);
            var envelopeUse = Math.Min(chosen.Max(d => d.Entry.Nudged) + 1, perEnvelope);
            var quietMin = chosen.Min(d => now - d.Entry.QuietSinceMs);
            var reason =
                $"dead-mailbox {address} · {ids.Count} stranded past quiet ({Dur(quietMin)}+)"
                + (freshest is { } age ? $" · no dispatch for {Dur(age)}" : " · no dispatch ever seen")
                + $" · budget envelope {envelopeUse}/{perEnvelope} · role {inWindow.Count + already + 1}/{perRoleHour} this hour"
                + (heldByCap > 0 ? $" · {heldByCap} more stranded, held for the next nudge by the digest cap" : "")
                + (triage.Spent > 0 ? $" · {triage.Spent} more stranded but spent" : "");
            nudges.Add(new MailNudge(ReaperRole, ids, reason, digest, ReaperHow, Workspace: null, Address: address));
            emitted[ReaperRole] = already + 1;

            foreach (var d in chosen) next = Min(next, now + (d.Rule.When.QuietForMs ?? 0));
            if (heldByCap > 0) next = Min(next, now);
            if (next is { } armedNext) nextCheck = Min(nextCheck, armedNext);
            Report(WatchStanding.Nudge, reason);
        }

        return reports;
    }

    /// What one pass over a mailbox's entries decided, before presence and
    /// budgets: which are due, how many are waiting on a threshold, how many are
    /// past their `perEnvelope` bound, and the nearest threshold to arm for. The
    /// role rule and the dead-mailbox rule share it so the two can never disagree
    /// about what "due" means.
    private sealed record TriageResult(
        IReadOnlyList<(WatchedEnvelope Entry, PendingMail Mail, WatchRule Rule)> Due,
        int NotDue, int Spent, long? NextCheckMs);

    private static TriageResult TriageEntries(
        IReadOnlyList<(WatchedEnvelope Entry, PendingMail Mail)> entries,
        IReadOnlyList<WatchRule> rules, long now)
    {
        var due = new List<(WatchedEnvelope Entry, PendingMail Mail, WatchRule Rule)>();
        long? next = null;
        var notDue = 0;
        var spent = 0;
        foreach (var (entry, mail) in entries)
        {
            var rule = rules.FirstOrDefault(r => Admits(r.When.Priority, mail.Envelope.Priority));
            if (rule is null) continue;
            if (entry.Nudged >= rule.Budget.PerEnvelope) { spent++; continue; }
            var dueAt = entry.QuietSinceMs + (rule.When.QuietForMs ?? 0);
            if (now >= dueAt) due.Add((entry, mail, rule));
            else { notDue++; next = Min(next, dueAt); }
        }
        return new TriageResult(due, notDue, spent, next);
    }

    /// When a spent sliding window frees one slot: the moment enough of its
    /// oldest entries age out that `perRoleHour` admits one more. `stamps` is
    /// oldest-first.
    private static long WindowRelease(IReadOnlyList<long> stamps, int perRoleHour) =>
        stamps[stamps.Count - perRoleHour] + RoleWindowMs;

    /// The strict reading of "unread for the role": pending in every mailbox of
    /// the role that accepts it. Returned in ledger order, one `PendingMail` per
    /// envelope (the first mailbox's — offsets are ledger positions and agree
    /// across mailboxes; `SeenAt` is a per-cursor fact the digest below does not
    /// use).
    private static IReadOnlyList<PendingMail> Unread(IReadOnlyList<WatchedMailbox> mailboxes)
    {
        if (mailboxes.Count == 0) return [];

        var holders = new Dictionary<string, (PendingMail Mail, int Count)>(StringComparer.Ordinal);
        foreach (var box in mailboxes)
            foreach (var mail in box.Pending)
            {
                var id = mail.Envelope.Id;
                holders[id] = holders.TryGetValue(id, out var have) ? (have.Mail, have.Count + 1) : (mail, 1);
            }

        var unread = new List<PendingMail>();
        foreach (var (mail, count) in holders.Values)
        {
            // Every mailbox that accepts the address must hold it. A mailbox
            // that holds it is counted as accepting even if `Accepts` says
            // otherwise — the cursor is the fact, the predicate is the
            // expectation, and holding it is the stronger claim.
            var accepting = mailboxes.Count(m => m.Address.Accepts(mail.Envelope.To) || Holds(m, mail.Envelope.Id));
            if (count >= accepting) unread.Add(mail);
        }
        return unread.OrderBy(m => m.Offset).ToList();
    }

    private static bool Holds(WatchedMailbox box, string id) => box.Pending.Any(p => p.Envelope.Id == id);

    /// A rule with no priority admits everything; `>=urgent` admits that class
    /// and anything louder (the enum is ordered ambient < reconcile < urgent);
    /// a bare class admits exactly itself.
    internal static bool Admits(WatchPriority? filter, MailPriority priority) =>
        filter is null || (filter.AtLeast ? priority >= filter.Priority : priority == filter.Priority);

    /// The text a woken turn is handed: the real digest renderer over the due
    /// envelopes, so a robot reads the same head fields (id, sender, `reply to`)
    /// a human's window would. The view is the ROLE's, sessionless — a nudge
    /// belongs to a role, and no cursor moves for it (d10: nudges are trail
    /// lines, never deliveries). `SeenAt` is dropped so every item reads `new`
    /// and the text is a function of the envelopes alone.
    /// Returns the text and HOW MANY of `due` (a prefix, in order) it carries —
    /// the renderer's whole-item cap may hold a tail back, and the caller names
    /// and charges only what was rendered.
    private static (string Text, int Rendered) RenderDigest(string role, IReadOnlyList<PendingMail> due)
    {
        var fresh = due.Select(m => m with { SeenAt = null }).ToList();
        var view = new MailPendingView(role, Session: null, HookSession: null, Gen: MailCursors.CurrentGen,
            Head: null, Offset: 0, Frontier: 0, Deliveries: 0, LastDeliveredId: null,
            Reanchored: false, ReanchorReason: null, fresh, Expired: [], SkippedMalformed: 0);
        var render = MailDigest.Render(view, new MailPlan(fresh, MailVehicle.Inject, HeldByPlan: 0),
            MailDigestOptions.DefaultMaxChars);
        return (render.Text, render.Delivered.Count);
    }

    private static long? Min(long? a, long b) => a is null || b < a ? b : a;

    /// A duration for humans, deterministic and unit-suffixed the way
    /// `watch.json` spells them: `1h30m`, `12m`, `45s`, `500ms`, `0s`.
    public static string Dur(long ms)
    {
        if (ms < 0) ms = 0;
        if (ms < 1_000) return ms == 0 ? "0s" : $"{ms}ms";
        var s = ms / 1_000;
        var h = s / 3_600; s %= 3_600;
        var m = s / 60; s %= 60;
        var sb = new StringBuilder();
        if (h > 0) sb.Append(h).Append('h');
        if (m > 0) sb.Append(m).Append('m');
        if (s > 0 && h == 0) sb.Append(s).Append('s');
        return sb.ToString();
    }
}
