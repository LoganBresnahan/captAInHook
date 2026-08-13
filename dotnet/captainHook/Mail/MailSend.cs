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

public static class MailSend
{
    /// Run the verb. `subverb` is argv's word after `mail` (Invocation carries
    /// it in EventName); only "send" exists today — `mail digest` is phase 4.
    /// `nowTs` overrides the stamp for tests; production passes null.
    public static int Run(
        string? subverb, TextReader stdin, TextWriter stdout, TextWriter stderr,
        string? mailDir = null, string? nowTs = null)
    {
        // `digest` is routed to MailDigest by Program.cs before this runs;
        // this usage line still names both so a bare `mail` teaches the verb.
        if (subverb != "send")
        {
            stderr.WriteLine(subverb is null
                ? "captainHook mail: a subverb is required — usage: captainHook mail <send|digest>  (send: one JSON envelope on stdin)"
                : $"captainHook mail: unknown subverb '{subverb}' — usage: captainHook mail <send|digest>  (send: one JSON envelope on stdin)");
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
