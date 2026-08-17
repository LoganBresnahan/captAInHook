namespace CaptainHook.Mail;

// Roadmap item 23 / ADR-0018 decisions 1-2, slice 1 — the ADDRESS GRAMMAR, and
// nothing else. This slice lands ALONE and ahead of everything that fans out
// from it, because its risk is PERMANENCE rather than slip-through: what parses
// here is what the append-only ledger will hold forever, and a grammar loosened
// after mail is on the chain cannot be tightened again without orphaning lines
// somebody already wrote.
//
// `to` is either a ROLE — `maintainer`, every instance holding it, ADR-0016's
// broadcast, unchanged — or a ROLE@INSTANCE — `maintainer@laptop-a`, exactly
// one mailbox, unicast (d1). Both are routing keys over the one chain; the
// store learns nothing new. **This file decides what an address IS, and — from
// slice `plan-unicast` (d4) — who reads one**: `Accepts` is the recipient
// predicate, added here rather than in `MailCursors` because it is a fact about
// addresses and because both of that file's predicate sites must call the same
// one. TTL's refusal on unicast is `unicast-refuses-ttl`'s (d5) and naming an
// instance at registration is `instance-registration`'s (d3); neither is here.
//
// Why a grammar at all, when `to` accepted anything through all of item 20:
// `@` can only mean "instance follows" if it can mean nothing else, so
// introducing the separator IS introducing the role grammar (d2). Refused,
// never guessed — a misrouted envelope is silent, and silence is the failure
// mode this whole subsystem is built against; a refused one is loud at the one
// moment a human can still fix the typo.
//
// Lowercase is pinned rather than case-folded. Folding would make `Ops` and
// `ops` one mailbox here while `MailCursors.CursorPath` — whose percent-encoder
// passes both cases through untouched — keeps them as two cursor files, which
// is the two-implementations-of-one-concept hazard (ADR-0016 N8) wearing an
// address for a hat. Refusing the uppercase spelling outright leaves exactly
// one way to write a mailbox's name. (Note the deliberate divergence from
// `kind` / `priority`, which ARE case-insensitive: a casing slip there picks a
// wrong member of a CLOSED set the parser can enumerate and correct against,
// whereas an address names an open universe of mailboxes and has nothing to
// correct against.)
//
// Pure — no I/O, no clock, no allocation on the reject path beyond the caller's
// message.

/// A parsed `to`: a role, plus the instance when the address is unicast.
///
/// `Instance is null` is exactly ADR-0016's behaviour (broadcast to every
/// holder of the role) — so an unnamed address is not a special case bolted on,
/// it is the whole of what the bus did before this slice.
public readonly record struct MailAddress(string Role, string? Instance)
{
    /// Unicast means "one mailbox", which is what d5 leans on to refuse a TTL
    /// (delivered-once is a fact, not a matter of opportunities) and what d6's
    /// reaper needs to name a dead box. Nothing reads it yet.
    public bool IsUnicast => Instance is not null;

    /// Round-trips the spelling the parser accepted — the ledger holds the
    /// address as the sender wrote it, and this must never invent a second one.
    public override string ToString() => Instance is null ? Role : $"{Role}@{Instance}";

    /// The grammar, in one sentence: `[a-z0-9][a-z0-9-]*`, ASCII only.
    ///
    /// ASCII is spelled out digit by digit rather than deferred to
    /// `char.IsLetterOrDigit`, which is Unicode-aware and would quietly admit
    /// `mаintainer` with a Cyrillic а — a mailbox that renders identically to
    /// another one and receives none of its mail. That is precisely the silent
    /// misrouting d2 exists to refuse.
    ///
    /// A trailing `-` is legal. The grammar is copied from the ADR verbatim and
    /// not "improved" in passing: this is the half of the decision that is
    /// permanent, so it says what was decided, not what a parser author would
    /// have preferred.
    public static bool IsRole(string s)
    {
        if (s.Length == 0) return false;
        if (!IsAlnum(s[0])) return false;
        for (var i = 1; i < s.Length; i++)
            if (!IsAlnum(s[i]) && s[i] != '-') return false;
        return true;

        static bool IsAlnum(char c) => c is (>= 'a' and <= 'z') or (>= '0' and <= '9');
    }

    /// Does the mailbox at THIS address read an envelope addressed to `to`?
    /// (d4, slice `plan-unicast` — the recipient predicate, and the ONE
    /// spelling of it.)
    ///
    /// A mailbox reads its role's BROADCAST, as every holder of the role does,
    /// plus the unicast addressed to it alone. Both predicate sites in
    /// `MailCursors` call this, which is not tidiness: the sites disagreeing is
    /// a specific, silent failure. The pending scan decides what a digest may
    /// deliver; `LoadOrAnchor`'s held-entry check decides whether a cursor's own
    /// held list still describes this mailbox's mail. A scan that accepted a
    /// unicast the held check then rejected would hold the envelope, re-anchor
    /// on the next read ("addressed to someone else"), and drop every held
    /// entry with it — mail delivered twice, held state lost, and a loud warn
    /// blaming the store for a predicate's disagreement with itself.
    ///
    /// `Instance is null` reads the broadcast alone. Since the 2026-08-17
    /// amendment to d3 that is only ever the SESSIONLESS reader — a window is
    /// addressable at `role@<its session id>` (its default, ephemeral mailbox)
    /// and `MailCursors.Pending` resolves the window's session in as the
    /// instance before this is called, so the caller never has to know which
    /// kind of mailbox it holds. The old model (windows keyed by session but
    /// NOT reachable there) is what ADR-0016 d6 chose to avoid stranded mail;
    /// the reaper (d6 of ADR-0018) is what handles stranded mail, and once it
    /// existed the asymmetry cost more than it saved.
    public bool Accepts(string to)
    {
        // Broadcast only. Spelled as the first branch because it is the whole
        // of what the bus did before this slice, and it must stay exactly that.
        if (Instance is null) return to == Role;

        if (to == Role) return true;   // a named reader is still a holder of its role

        // `Role + "@" + Instance` without building it: this runs once per
        // ledger line per cursor per read, and the read is on the dispatch hot
        // path. Robust to a `to` the grammar would refuse (a second `@` lands
        // in the instance half and simply fails to match) — the parser is the
        // gate, but a predicate that leans on its caller is a predicate that
        // breaks when someone calls it from somewhere new.
        var at = to.IndexOf('@');
        return at == Role.Length
            && to.AsSpan(0, at).SequenceEqual(Role)
            && to.AsSpan(at + 1).SequenceEqual(Instance);
    }

    /// The one bound the grammar has: an address is at most this many
    /// characters, `@` included (added 2026-08-17 with `answer-by-address`; the
    /// grammar as first landed bounded the alphabet and not the length).
    ///
    /// It is `MailEnvelope.HeadFieldChars` and not a number of its own because
    /// that is the reason it exists: a `replyTo` is rendered in the digest HEAD
    /// — the return address a model copies verbatim — and a head field must be
    /// bounded (the render skeptic's finding 1: a sender-controlled field longer
    /// than the cap eats the digest). Every other head field is display-CLAMPED
    /// to this width; an address cannot be, since a clamped return address is
    /// worse than none (the answer goes to a mailbox that does not exist), so
    /// it is REFUSED at this width instead. The same bound happens to sit well
    /// under the platform fact that would otherwise bite first: a mailbox's
    /// cursor is a FILE named `cursor.<role>.<instance>.json`, and NAME_MAX is
    /// 255 on every filesystem this runs on (doc/platform.md), so an address
    /// past ~242 characters is a mailbox whose cursor can never be written.
    public const int MaxChars = MailEnvelope.HeadFieldChars;

    /// Parse an address, or fail. At most one `@`; both halves non-empty and
    /// role-valid; the whole no longer than `MaxChars`; anything else is a
    /// refusal.
    ///
    /// A second `@` is a refusal rather than a split-on-first or split-on-last:
    /// `a@b@c` has two readings, both plausible, and picking either is guessing
    /// which mailbox a human meant.
    public static bool TryParse(string s, out MailAddress address)
    {
        address = default;
        if (s.Length > MaxChars) return false;

        var at = s.IndexOf('@');
        if (at < 0)
        {
            if (!IsRole(s)) return false;
            address = new MailAddress(s, null);
            return true;
        }

        if (s.IndexOf('@', at + 1) >= 0) return false;   // more than one '@'

        var role = s[..at];
        var instance = s[(at + 1)..];
        if (!IsRole(role) || !IsRole(instance)) return false;

        address = new MailAddress(role, instance);
        return true;
    }

    /// The one wording every refusal uses, so a rejected envelope teaches the
    /// grammar at the seam where it was refused (`mail send`'s stderr today;
    /// a registration's, once `instance-registration` lands).
    public const string GrammarHelp =
        "a role or role@instance, each half matching [a-z0-9][a-z0-9-]* (lowercase, at most one '@', at most 120 characters in all)";
}
