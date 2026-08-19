using System.Buffers;
using System.Text;
using System.Text.Json;

namespace CaptainHook.Mail;

// Roadmap item 20 / ADR-0016 decision 7 — `captainHook mail send`, the
// UNIVERSAL WRITE PATH: anything that can run a process can put mail on the
// bus. One JSON envelope on stdin, validated by phase 1's strict parser,
// appended through phase 2's chained store. The strictness lives in tested C#
// exactly so senders do not need jq or hand-rolled JSON (the ADR's rejected
// shell-script alternative); a sender's whole contract is "write one object,
// read the exit code".
//
// This is a HUMAN/CLI verb like `doctor` and `ui`, not hook mode: stdout is
// free for a confirmation line, and nothing here touches the sacred channel.
//
// `ts` IS STAMPED HERE when the sender omits it (d2: "every writer goes
// through `mail send`, which stamps it") — wall-clock UTC, which is exactly
// house invariant 2's carve-out: a display timestamp, never parsed, never
// compared (TTL is delivery-counted). The stamp REBUILDS the object
// preserving every property as-is — unknown fields, duplicates, all of it —
// so stamping can never launder a malformed envelope past the strict parser.
//
// `hops` IS STAMPED AND CHECKED HERE (ADR-0017 d8, slice `thread-fields`), for
// the same reason `ts` is: this verb is the one path every writer goes through,
// so it is the only place a thread's depth can be established from the ledger
// rather than from what a sender claims. Three rules, in the order they fire:
//
//   * the parent is RESIDENT ⇒ the depth is `parent.hops + 1`, computed here.
//     A sender that stated a different number is REFUSED rather than corrected
//     — a model must not be able to name its own thread position, and silently
//     overwriting a claim would hide the attempt on a chain nobody can amend.
//   * the parent is GONE (rotated out, or an id this store never held) ⇒ the
//     sender's number stands, or 0 when it said nothing. Nothing on this
//     machine knows the count, and refusing the reply would kill a legitimate
//     answer for a reason no reader could see. This is the stated cost of a
//     STORED depth, and it needs the head to have actually rotated away.
//   * the resulting depth reaches `MailEnvelope.HopBudget` ⇒ REFUSED, nothing
//     written. That is d8's ping-pong bound: two well-meaning agents stop
//     spending the owner's tokens at a number rather than at a stack trace.
//
// The lookup is one full store read per REPLY (a first envelope never reads),
// taken before the append lock and therefore racy in exactly one direction: a
// parent appended between the read and the write reads as gone, which loses a
// count and never invents one.

public static class MailSend
{
    /// Run the verb. `subverb` is argv's word after `mail` (Invocation carries
    /// it in EventName); this file serves "send" and refuses everything else,
    /// the other verbs having been routed away before it is called.
    /// `nowTs` overrides the stamp for tests; production passes null.
    /// `maxHops` overrides the thread bound for tests; production passes null
    /// and gets `MailEnvelope.HopBudget`.
    public static int Run(
        string? subverb, TextReader stdin, TextWriter stdout, TextWriter stderr,
        string? mailDir = null, string? nowTs = null, int? maxHops = null)
    {
        // `digest`, `status` and `reap` are routed to their own files by
        // Program.cs before this runs; the usage line still names every verb,
        // because this is where a bare or misspelled `mail` lands and it is
        // the only place the whole set is taught.
        if (subverb != "send")
        {
            stderr.WriteLine(subverb is null
                ? "captainHook mail: a subverb is required — usage: captainHook mail <send|ask|digest|status|reap|watch>  (send: one JSON envelope on stdin)"
                : $"captainHook mail: unknown subverb '{subverb}' — usage: captainHook mail <send|ask|digest|status|reap|watch>  (send: one JSON envelope on stdin)");
            return 1;
        }

        var text = stdin.ReadToEnd();
        text = StampTsIfAbsent(text,
            nowTs ?? DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"));

        var envelope = MailEnvelope.TryParseLine(text, out var errors);
        if (envelope is null)
        {
            stderr.WriteLine("captainHook mail send: envelope rejected — nothing written:");
            foreach (var e in errors) stderr.WriteLine($"  {e}");
            return 1;
        }

        var store = new MailStore(MailStore.ResolveDir(mailDir));

        if (envelope.InReplyTo is not null)
        {
            envelope = WithThreadDepth(store, envelope, HopsStated(text), maxHops ?? MailEnvelope.HopBudget, out var refusal);
            if (refusal is not null)
            {
                stderr.WriteLine("captainHook mail send: envelope rejected — nothing written:");
                stderr.WriteLine($"  {refusal}");
                return 1;
            }
        }

        switch (store.Append(envelope))
        {
            case MailAppend.Appended a:
                stdout.WriteLine($"mail: appended {envelope.Id} to '{envelope.To}' at offset {a.Offset}");
                return 0;

            case MailAppend.Failed f:
                stderr.WriteLine($"captainHook mail send: {f.Error}");
                return 1;

            default: throw new InvalidOperationException("MailAppend is a closed set");
        }
    }

    /// The three rules in the header, applied to one reply: the resident
    /// parent's depth wins, a stated depth that disagrees with it is refused,
    /// an absent parent leaves the sender's number (or 0) alone, and a depth
    /// that reaches the budget is refused. `stated` is null when the sender
    /// wrote no `hops` at all — the parser cannot say (an absent field and an
    /// explicit 0 both arrive as 0), which is why it is read off the text.
    private static MailEnvelope WithThreadDepth(
        MailStore store, MailEnvelope e, int? stated, int maxHops, out string? refusal)
    {
        refusal = null;

        // The LAST line carrying the id wins: ids are a sender's to choose and
        // the store never enforces uniqueness, so the most recent bearer is the
        // one a reader answering today was looking at.
        var parent = store.Read()
            .Where(l => l.Terminated && l.Envelope is not null && l.Envelope.Id == e.InReplyTo)
            .Select(l => l.Envelope)
            .LastOrDefault();

        var hops = e.Hops;
        if (parent is not null)
        {
            var expected = parent.Hops + 1;
            if (stated is int claimed && claimed != expected)
            {
                refusal =
                    $"'hops' says {claimed}, but the envelope it answers ('{parent.Id}') is at " +
                    $"depth {parent.Hops} — a reply to it is at {expected}. The store computes " +
                    "this field; omit it and `mail send` stamps the right number.";
                return e;
            }
            hops = expected;
        }
        // else: the parent is not in this store (rotated away, or never here).
        // The sender's number stands — see the header's second rule.

        if (hops >= maxHops)
        {
            refusal =
                $"the thread is full: this reply would be at depth {hops} and one thread carries " +
                $"at most {maxHops} envelopes (ADR-0017 d8's hop budget — the ping-pong bound). " +
                "Start a new thread with its own topic instead of answering again.";
            return e;
        }

        return e with { Hops = hops };
    }

    /// Whether the sender WROTE a `hops` field, read off the raw text because
    /// the parsed envelope cannot say: absent and an explicit 0 are the same
    /// value there, and they mean different things here. Unreadable text is
    /// "did not state it" — the parser has already spoken about such text, and
    /// this must never throw for the same reason `StampTsIfAbsent` must not.
    private static int? HopsStated(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty("hops", out var h)) return null;
            return h.ValueKind == JsonValueKind.Number && h.TryGetInt32(out var n) ? n : null;
        }
        catch (Exception) { return null; }
    }

    /// Append a `ts` to a lone JSON object that lacks one, touching NOTHING
    /// else: every existing property — unknown, duplicate, unreadable — is
    /// copied through verbatim, so the strict parser still sees exactly the
    /// violations the sender wrote. Anything that is not a lone object (bad
    /// JSON, an array, trailing content) is returned unchanged for
    /// TryParseLine to reject with its own message.
    private static string StampTsIfAbsent(string text, string ts)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return text;
            foreach (var p in doc.RootElement.EnumerateObject())
                if (p.Name == "ts") return text;

            var buf = new ArrayBufferWriter<byte>(text.Length + 40);
            using (var w = new Utf8JsonWriter(buf))
            {
                w.WriteStartObject();
                foreach (var p in doc.RootElement.EnumerateObject()) p.WriteTo(w);
                w.WriteString("ts", ts);
                w.WriteEndObject();
            }
            return Encoding.UTF8.GetString(buf.WrittenSpan);
        }
        // JsonException: not a lone object — TryParseLine's to reject.
        // Anything else (e.g. a lone-surrogate escape tripping WriteTo's
        // deferred unescape): stamping is best-effort and must NEVER throw;
        // the unstamped text goes to the parser, which reports what is wrong.
        catch (Exception) { return text; }
    }
}
