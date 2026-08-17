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
// store learns nothing new. **This file only decides what an address IS.**
// Routing on it is `plan-unicast`'s (d4), TTL's refusal on unicast is
// `unicast-refuses-ttl`'s (d5), and naming an instance at registration is
// `instance-registration`'s (d3) — none of them are here, and until they land
// a `role@instance` envelope parses and is simply addressed to nobody.
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

    /// Parse an address, or fail. At most one `@`; both halves non-empty and
    /// role-valid; anything else is a refusal.
    ///
    /// A second `@` is a refusal rather than a split-on-first or split-on-last:
    /// `a@b@c` has two readings, both plausible, and picking either is guessing
    /// which mailbox a human meant.
    public static bool TryParse(string s, out MailAddress address)
    {
        address = default;

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
        "a role or role@instance, each half matching [a-z0-9][a-z0-9-]* (lowercase, at most one '@')";
}
