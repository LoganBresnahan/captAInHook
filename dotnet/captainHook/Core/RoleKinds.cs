using CaptainHook.Mail;

namespace CaptainHook.Core;

// Roadmap item 22 / ADR-0017 decision 3, slice `role-kind-inference` — what
// KIND of thing holds a role, and whether anybody is home.
//
// Two questions, kept apart on purpose, because the brain (d4) takes them as
// separate inputs and answering one with the other is how a watcher starts
// feeding itself:
//
//   * KIND is structural — who COULD serve this role, from what is registered.
//     It changes when an operator edits `handlers.json`, and not otherwise.
//   * PRESENCE is momentary — is anybody actually here right now. It changes
//     every time a window fires a hook.
//
// **The as-built amendment to d3.** The ADR says "a role with a turn payload
// registered on the `mail-nudge` event is robot-servable", which reads as a
// per-role registration. The dispatcher makes that spelling meaningless: fan-out
// is by EVENT, so every handler on `mail-nudge` runs on every nudge whatever
// role it names. Two per-role registrations would both spawn on every nudge and
// one would exit immediately having read a role off the envelope that is not
// its own — a process spawn per role per nudge, for nothing. A per-role
// registration would therefore exist ONLY to be read back by this file, which is
// the "declared twice" that d3 exists to refuse.
//
// So the capability is INSTALLATION-WIDE — is any turn payload registered on
// `mail-nudge` at all — and the per-role gate stays where d7 put it, in
// `watch.json`. Kind and rules stay independent brain inputs, and an operator
// has two honest ways to say "no robot here": install no turn payload, or write
// no rule for the role.
//
// Everything here is PURE: values in, values out, no I/O and no clock. The
// caller resolves `handlers.json`, lists the cursor files and reads presence;
// this file only joins them, which is what makes the whole thing fixture-
// testable the way the brain will need.

/// Who can serve a role. `Unserved` is not in d3's list and is the state the
/// 2026-08-17 dogfood pass found four of on the live bus: mail addressed to a
/// role no window reads and no robot can be woken for. Naming it is the
/// difference between "we decided not to nudge" and "nothing here can help".
public enum RoleKind
{
    /// Nobody reads it and nothing can be woken for it. Mail piles up.
    Unserved,

    /// One or more `mail digest` registrations — a window reads this role. The
    /// robot channel does not exist for it (d3): no nudge is ever dispatched,
    /// and the count IS the nudge.
    HumanHeld,

    /// A turn payload is installed and no window reads this role: a robot is
    /// the only thing that can answer.
    RobotServable,

    /// Both. Human first, robot as fallback — which is `noLiveSession`'s whole
    /// job in a watch rule (d7), and why the default there is true.
    Mixed,
}

/// The structural half, computed once from `handlers.json`.
///
/// `HumanHeld` is the set of ROLES (not addresses): a role read by two
/// registrations — the ambient seam and the urgent one, the normal shape — is
/// one entry, and a role read under two different `--as` names is still one
/// role, because the question here is "does any window read this" and every
/// instance of a role is a window that does.
public sealed record RoleKinds(IReadOnlySet<string> HumanHeld, bool TurnPayloadInstalled)
{
    private static readonly IReadOnlySet<string> NoRoles = new HashSet<string>(StringComparer.Ordinal);

    /// Every NAMED mailbox an operator registered (`mail digest --as <name>`),
    /// as `role@instance` strings. Not a kind and not presence — it is the third
    /// structural fact `handlers.json` holds, and the dead-mailbox rule
    /// (ADR-0018 d6) is what needs it: a registered durable mailbox is STANDING
    /// an operator declared, so mail waiting in one is waiting, not stranded,
    /// however long its window has been shut. Init-only rather than positional
    /// so the two questions d3 keeps apart stay the record's shape.
    public IReadOnlySet<string> RegisteredMailboxes { get; init; } = NoRoles;

    /// Nothing registered: every role is `Unserved`. This is also what a
    /// MALFORMED `handlers.json` yields, and deliberately so — a malformed file
    /// registers NOTHING (ADR-0010 d4), so there is no turn payload to run and
    /// no digest to read, and reporting anything else would describe a system
    /// that is not there.
    public static readonly RoleKinds None = new(NoRoles, false);

    /// Infer from a resolved registration file.
    ///
    /// A digest is recognized by `MailDigest.MailboxOf` — the real verb's own
    /// argument parser, never a lookalike — so a registration this counts is one
    /// the dispatcher would actually run.
    ///
    /// **Known limit, and it is not a choice.** `mail status` filters
    /// registrations through `dispatch.json` for the asking window's cwd and
    /// session; nothing here does, because there is no window asking. "Would ANY
    /// dispatch anywhere be allowed?" is not answerable without enumerating every
    /// cwd a hook might arrive from. So a role whose digest is denied by policy
    /// everywhere still reads as human-held, and the effect is FEWER robot
    /// nudges — the conservative direction for a channel that spends the owner's
    /// tokens, and the one an operator can see on the canvas.
    public static RoleKinds From(ExecHandlersResolution handlers)
    {
        if (handlers is not ExecHandlersResolution.Loaded loaded) return None;

        var human = new HashSet<string>(StringComparer.Ordinal);
        var named = new HashSet<string>(StringComparer.Ordinal);
        var robot = false;
        foreach (var entry in loaded.Entries)
        {
            if (MailDigest.MailboxOf(entry) is { } box)
            {
                human.Add(box.Role);
                if (box.Instance is not null) named.Add(box.ToString());
            }

            // Canonicalized, because a registration writes the event kebab
            // (`"mail-nudge"`) and the host spells it Pascal — the same
            // canonicalization the registration loader and the harness spec
            // both apply. Comparing raw strings here would find nothing, ever,
            // and would find it silently.
            if (entry.Events.Any(ev => Harness.Canon(ev) == MailNudgeEvent.EventType)) robot = true;
        }
        return new RoleKinds(human, robot) { RegisteredMailboxes = named };
    }

    /// The kind of one role. Asked per role rather than enumerated, because the
    /// caller already knows which roles it cares about — the ones with pending
    /// mail — and a role that exists only in the mail store (nobody registered,
    /// nobody woken) has a real and useful answer here: `Unserved`.
    public RoleKind Of(string role) => (HumanHeld.Contains(role), TurnPayloadInstalled) switch
    {
        (true, true) => RoleKind.Mixed,
        (true, false) => RoleKind.HumanHeld,
        (false, true) => RoleKind.RobotServable,
        (false, false) => RoleKind.Unserved,
    };

    /// Does the robot channel exist for this role at all? The one question the
    /// brain asks before it considers a rule — d3's consequence, spelled once so
    /// no caller re-derives it from the enum and gets `Mixed` wrong.
    public bool RobotChannelExists(string role) => Of(role) is RoleKind.RobotServable or RoleKind.Mixed;

    /// Is this mailbox one an operator declared? Only a named one can be — an
    /// unnamed cursor is keyed by a session id (ADR-0018 d3) and was created by
    /// a delivery, not by a registration.
    public bool IsRegisteredMailbox(MailAddress address) =>
        address.Instance is not null && RegisteredMailboxes.Contains(address.ToString());
}

/// The momentary half: is anybody home for a role.
///
/// Deliberately returns an AGE rather than a boolean. "Live" needs a threshold,
/// and every number about elapsed time in this subsystem belongs with the brain
/// that owns `quietFor` and the monotonic deadlines (d4, house invariant 2) —
/// inventing a second one here would be a second policy nobody wrote down.
public static class RolePresence
{
    /// How long ago the freshest window holding a cursor for `role` last drove a
    /// dispatch, or null if none of them ever has.
    ///
    /// The join is cursor-files × recent-dispatches, the same two halves
    /// `ApiReadModel.Presence` uses (ADR-0016 d14), and both are INFERENCE: a
    /// cursor says "this mailbox was delivered to once", a dispatch age says
    /// "this daemon served a hook of its N ms ago". Neither is a liveness claim,
    /// and null is the honest answer for a role whose readers have all gone —
    /// which is exactly the dead-mailbox shape the reaper exists for.
    ///
    /// **A named mailbox contributes nothing, correctly.** Since ADR-0018 d3 a
    /// cursor's key is its instance — the `--as` name when a registration has
    /// one — so a durable named mailbox's key never matches a session id and
    /// never looks live. That is the right answer rather than a limitation: a
    /// `--as` mailbox is a mailbox, not a window, and nobody is sitting in it.
    public static long? FreshestDispatchAgeMs(
        string role,
        IReadOnlyList<(string Role, string? Session)> cursors,
        IReadOnlyList<(string Session, long AgeMs)> recent)
    {
        var ages = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (session, ageMs) in recent)
            if (!ages.TryGetValue(session, out var have) || ageMs < have) ages[session] = ageMs;

        long? freshest = null;
        foreach (var (r, session) in cursors)
        {
            if (r != role || session is null) continue;
            if (!ages.TryGetValue(session, out var age)) continue;
            if (freshest is null || age < freshest) freshest = age;
        }
        return freshest;
    }

    /// The join as a yes/no, for a caller that HAS a threshold to apply. The
    /// threshold is always the caller's — this is the one line that turns an age
    /// into an answer, so no two callers can disagree about the comparison.
    public static bool AnyLiveSession(
        string role,
        IReadOnlyList<(string Role, string? Session)> cursors,
        IReadOnlyList<(string Session, long AgeMs)> recent,
        TimeSpan within) =>
        FreshestDispatchAgeMs(role, cursors, recent) is { } age && age <= within.TotalMilliseconds;
}
