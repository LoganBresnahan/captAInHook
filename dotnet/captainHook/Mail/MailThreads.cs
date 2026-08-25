namespace CaptainHook.Mail;

// Roadmap item 22 / ADR-0017 d8, slice `thread-aware-delivery` — the ASKER-SIDE
// reading of a thread, and the only file that has one.
//
// `thread-fields` made a thread READABLE: `inReplyTo` names the parent, `hops`
// counts the depth, and the digest head renders both. Every one of those facts
// is about the envelope in front of you. This file adds the two facts that are
// about YOU — the mailbox doing the reading — and neither can be answered from
// an envelope alone:
//
//   * IS THIS THE ANSWER I ASKED FOR? An answer names its parent; whether that
//     parent was MY request is a join back onto the ledger.
//   * IS THIS ANSWER LATE? A request is closed by the FIRST answer that names
//     it; a second one is a fact about the thread, and again only the ledger
//     knows.
//
// WHY THE JOIN IS ON `replyTo` AND NOT ON `from` (ADR-0018 d4). A request's
// return address is the one field that names a MAILBOX; `from` names an agent,
// a harness and (sometimes) a window, and `mail ask` leaves `from.session`
// absent on purpose — a write-only caller has no window to claim. So "my
// request" means "a request whose reply address this mailbox answers to", and
// the predicate is `MailAddress.Accepts` — the SAME one `MailCursors.Pending`
// routes with, never a second reading of what an address covers.
//
// That predicate carries one honest imprecision, stated rather than engineered
// around: an UNNAMED window's readable mailbox is its bare role (`MailStatus.
// ReadableMailboxes`), so `mail ask` from such a window writes `replyTo:
// reviewer` and every reviewer mailbox accepts it. Two reviewer windows both
// read that answer as theirs — which is *true of the role* and as precise as a
// broadcast return address can be. Naming the mailbox (`mail digest --as`)
// makes the return address a unicast and the reading exact; that is ADR-0018's
// whole thesis, and this is one more place it pays.
//
// A request with NO `replyTo` cannot be joined at all (the answerer falls back
// to the sender's role, d4), so its answer reads as ordinary mail — today's
// behaviour, which is the direction every degradation here takes.
//
// PURE, and over the WHOLE ledger. No clock, no cursor, no I/O: the caller
// hands in the lines it has already read (the digest reads the store once and
// derives both the view and this from that one read — `MailCursors.Pending`'s
// three-argument overload exists for exactly this reason). "Whole ledger"
// rather than "the pending window" because a request and its answer routinely
// sit on opposite sides of any window a reader happens to hold — the same
// argument `ApiReadModel` made for building its index over every line, which
// is why that index now lives HERE and that file calls this one (ADR-0016 N8:
// two implementations of one concept is the hazard, and "which answer closed
// this request" was about to be the second one).
//
// NOT here, and deliberately: nothing that remembers. Every fact this file
// states is re-derived from the append-only ledger on each read, so there is no
// state to migrate, to reconcile, or to leak across a restart — and, the reason
// that matters at this seam, nothing whose staleness could make a Stop block
// render when the cursor says there is nothing to deliver (ADR-0016 N3 rests
// entirely on cursor-advance; see the slice's note in ADR-0017).

/// What this mailbox's own threads look like on the ledger: the requests it
/// asked, and which answer closed each request that has one.
///
/// `AskedByMe` holds request ids; `ClosedBy` maps a request id to the id of the
/// FIRST answer naming it. Both are ordinal-keyed — an id is bytes, never a
/// locale-sensitive string.
public sealed record MailThreads(
    IReadOnlySet<string> AskedByMe,
    IReadOnlyDictionary<string, string> ClosedBy)
{
    /// No thread knowledge at all — every reading below answers "no", and the
    /// digest renders exactly what it rendered before this slice.
    ///
    /// This is what a caller that has no ledger in hand passes, and the default
    /// when one is omitted. The omission is SAFE in the one sense that matters
    /// here: its whole cost is that an answer to your own question arrives as
    /// ordinary ambient mail, untagged. Nothing is misrouted, nothing is
    /// delivered that should not be, nothing is consumed — the failure
    /// direction is *less salience*, never lost or misattributed mail.
    public static readonly MailThreads None = new(
        new HashSet<string>(StringComparer.Ordinal),
        new Dictionary<string, string>(StringComparer.Ordinal));

    /// Build the reading for one MAILBOX over one read of the store.
    ///
    /// `mailbox` must be the RESOLVED address — `role@(--as ?? session)`, which
    /// is what `MailPendingView` already carries as `Role`/`Session` — because
    /// the entitlement and the thread reading have to agree about which mailbox
    /// is asking; two resolutions of "who am I" is the same hazard as two
    /// resolutions of "what is pending".
    ///
    /// A TORN TAIL IS NOT A THREAD. Only terminated lines are joined, matching
    /// the frontier rule one layer down: the bytes of an append in flight may
    /// be a request or an answer or neither, and a thread fact derived from
    /// them would change the moment the write completed.
    public static MailThreads Of(MailAddress mailbox, IReadOnlyList<MailLine> lines)
    {
        var asked = new HashSet<string>(StringComparer.Ordinal);
        foreach (var l in lines)
            if (l.Terminated
                && l.Envelope is { Kind: MailKind.Request, ReplyTo: { } back } request
                && mailbox.Accepts(back))
                asked.Add(request.Id);

        return new MailThreads(asked, Closures(lines));
    }

    /// The thread index: request id → the id of the first `answer` that names
    /// it in `inReplyTo`.
    ///
    /// FIRST wins, in ledger order, and only `kind: answer` closes — a `status`
    /// quoting a request is a comment on it, not a reply to it. Nothing here
    /// reads a clock or resolves an id against a mailbox: an envelope is closed
    /// when another envelope on this chain says it answers it, which is a fact
    /// about the ledger and survives every cursor on the machine being deleted.
    ///
    /// Named `Closures` rather than `ClosedBy` only because the record already
    /// carries the built index under that name. Shared with the read model
    /// (`MailDto.ClosedBy` — the browser's copy of this same index), so the
    /// digest and the canvas can never disagree about which answer closed a
    /// thread.
    public static IReadOnlyDictionary<string, string> Closures(IReadOnlyList<MailLine> lines)
    {
        var closed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var l in lines)
            if (l.Terminated && l.Envelope is { Kind: MailKind.Answer, InReplyTo: { } request } answer)
                closed.TryAdd(request, answer.Id);
        return closed;
    }

    /// Does this envelope answer a request THIS mailbox asked?
    ///
    /// The one question that turns an answer from "mail" into "the thing I am
    /// waiting for" — and the whole basis for the delivery class below. Note
    /// what it is NOT: it does not ask whether the envelope is addressed here
    /// (routing already settled that before the digest ever sees it), and it
    /// does not ask who sent it. A sender who forges `inReplyTo` at one of my
    /// requests buys salience it did not earn — which is worth naming and not
    /// worth defending against, since `priority: urgent` is a cheaper way to
    /// buy strictly more, and both stop at the same trust boundary (ADR-0016
    /// N1: every member is already a same-user process).
    public bool AnswersMe(MailEnvelope e) =>
        e is { Kind: MailKind.Answer, InReplyTo: { } id } && AskedByMe.Contains(id);

    /// The id of the answer that got here FIRST, when this envelope is not it —
    /// i.e. this answer is LATE, and the thread it joins was already closed.
    /// Null for the closing answer itself and for anything that is not an
    /// answer.
    ///
    /// Late is not an error and nothing is withheld for it: two agents
    /// answering one question is a normal race on a bus with no locking, and
    /// the second answer may well be the better one. It is rendered so the
    /// reader knows it is reading a second opinion rather than the reply.
    public string? LateAfter(MailEnvelope e) =>
        e is { Kind: MailKind.Answer, InReplyTo: { } id }
            && ClosedBy.TryGetValue(id, out var first)
            && !string.Equals(first, e.Id, StringComparison.Ordinal)
                ? first
                : null;
}
