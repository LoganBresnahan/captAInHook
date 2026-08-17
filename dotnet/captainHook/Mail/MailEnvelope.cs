using System.Text.Json;

namespace CaptainHook.Mail;

// Roadmap item 20 / ADR-0016 decision 2 — the mail ENVELOPE and its STRICT
// parser. One JSON object per line in the mail store; this file is the whole
// contract between anything that writes mail (`captainHook mail send`, phase 3)
// and anything that reads it (the store + digest handler, phases 2/4).
//
// Everything here is PURE — no I/O, no clock, no store. The store's on-disk
// framing (genesis line, `prev` encoding, `gen` rotation) is phase 2's to
// settle and is deliberately NOT decided here; see the `prev` note below.
//
// House precedent, followed deliberately: DispatchPolicy.TryParse's strict walk
// — collect EVERY violation in one pass, all-or-nothing accept, never throw on
// bad DATA, unknown/duplicate fields are malformed (never-guess). The stakes
// differ from the policy file's, and the difference sets the failure direction:
// a malformed policy poisons the door (deny loudly), whereas a malformed
// envelope is warned-and-skipped by its reader — never delivered, never fatal,
// and never able to stop the OTHER envelopes on the line after it (ADR-0016 d2).
//
// `body` is OPAQUE prose the protocol never grows into (the ADR-0003 lesson:
// data selects among coded behavior; mail never becomes a template language) —
// so the parser checks that it is a readable string and looks no further.

/// What an envelope IS, as a closed set (ADR-0016 d2). Unknown kinds are
/// malformed rather than passed through: the digest renderer (phase 4) switches
/// on this, and a kind it has never heard of has no rendering.
public enum MailKind { Status, Request, Answer, Alert }

/// The seam class the SENDER requests (ADR-0016 d5). Not an ordering — the
/// planner degrades a request downward against what the recipient's HarnessSpec
/// actually declares, and that mapping is phase 4's, not this enum's. Members
/// are listed in the ADR's order and nothing may read them as a rank.
public enum MailPriority { Ambient, Reconcile, Urgent }

/// Who sent it. `session` is OPTIONAL: a write-only member (a hookless
/// harness, a cron-shaped observer — ADR-0016 d5) has no session to name, and
/// requiring one would make the bus's cheapest membership class unrepresentable.
/// `agent`/`harness` are required — provenance rendering (d10) always names both.
public sealed record MailSender(string Agent, string Harness, string? Session);

/// One mail envelope, v1 (ADR-0016 d2).
///
/// `InReplyTo` is RESERVED AND UNREAD in v1 (d9): the parser accepts and
/// preserves it so today's envelopes survive a future ask/reply design without
/// a version bump, and nothing in v1 may branch on it.
///
/// `Prev` is the hash-chain link (d11). The store — not the sender — writes it,
/// so it is absent on a freshly built envelope and present on a stored line.
/// Phase 1 reserves the FIELD NAME only, so a stored line does not read as
/// malformed the moment phase 2 starts chaining; the encoding (genesis
/// convention, hex form, what is hashed) is phase 2's durable-format decision
/// and is NOT settled here.
public sealed record MailEnvelope(
    string Id,
    string Ts,
    MailSender From,
    string To,
    MailKind Kind,
    string Topic,
    MailPriority Priority,
    string? InReplyTo,
    int? TtlDeliveries,
    string Body,
    string? Prev)
{
    /// Delivery opportunities an envelope survives when the sender does not say
    /// (ADR-0016 d3: TTL counts SEAMS PASSED, never wall time — a wall clock
    /// would rot mail while the recipient idles overnight, which is house
    /// invariant 2 violated at the design level).
    public const int DefaultTtlDeliveries = 3;

    /// `TtlDeliveries` is NULL for exactly one reason: the address is unicast
    /// (ADR-0018 d5). With one addressee, *delivered* is a fact rather than a
    /// matter of opportunities, so there is nothing for a countdown to count —
    /// and pending-forever-if-the-instance-never-returns is the reaper's
    /// problem (d6), not TTL's.
    ///
    /// It is REFUSED at parse rather than accepted-and-ignored, because an
    /// ignored field on an append-only chain is a lie that outlives everyone
    /// who could correct it. Null and "3" are therefore never two spellings of
    /// one state: null means the concept does not apply here.
    public bool HasTtl => TtlDeliveries is not null;

    private static readonly IReadOnlySet<string> KnownFields =
        new HashSet<string>
        {
            "v", "id", "ts", "from", "to", "kind", "topic",
            "priority", "inReplyTo", "ttlDeliveries", "body", "prev",
        };

    private static readonly IReadOnlySet<string> KnownFromFields =
        new HashSet<string> { "agent", "harness", "session" };

    /// Strict parse of one envelope object: the envelope, or null plus one error
    /// per violation. NEVER throws on bad DATA — a JsonException from malformed
    /// BYTES is the caller's to catch (same split as DispatchPolicy.TryParse;
    /// TryParseLine below is that caller for the JSONL store).
    public static MailEnvelope? TryParse(JsonElement root, out IReadOnlyList<string> errors)
    {
        var errs = new List<string>();
        errors = errs;

        if (root.ValueKind != JsonValueKind.Object)
        {
            errs.Add("envelope must be a JSON object");
            return null;
        }

        // Unknown OR duplicate field => malformed. Duplicates matter for the same
        // reason they do in the policy dialect: System.Text.Json would silently
        // keep one of two `to`s, and an envelope that quietly changes recipient
        // is exactly the ambiguity strict-never-guess exists to refuse.
        CheckFields(root, KnownFields, prefix: null, errs);

        // v: required, must be the number 1. A missing version is malformed
        // (ADR-0016 d2) — an unversioned line cannot be safely re-read later.
        if (!root.TryGetProperty("v", out var v))
            errs.Add("'v' is required and must be the number 1");
        else if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var ver) || ver != 1)
            errs.Add($"'v' must be 1 (got {RawText(v)})");

        var id = Required(root, "id", errs);

        // ts is required but its FORMAT is unvalidated (d2: display-only). The
        // store is the inter-agent influence record (d13) — an undated line
        // weakens it and every writer goes through `mail send`, which stamps it —
        // but nothing may parse or compare it: TTL is delivery-counted (d3) and
        // wall clock stays display-only (house invariant 2).
        var ts = Required(root, "ts", errs);

        var from = ParseFrom(root, errs);

        // `to` is the ONE field carrying the ADR-0018 d2 address grammar — a
        // role, or a role@instance. Checked here and nowhere else: this parser
        // is the single choke point every write path passes through (`mail
        // send` parses before it appends; the store serializes a parsed record),
        // so an ungrammatical address cannot reach the chain.
        //
        // Scoped to `to` deliberately. `from.agent` is a free-form provenance
        // label, not a routing key — nothing addresses mail to it — and
        // constraining it would refuse envelopes already on the ledger for a
        // property nothing reads.
        var to = Required(root, "to", errs);
        var unicast = false;
        if (to is not null)
        {
            if (MailAddress.TryParse(to, out var address)) unicast = address.IsUnicast;
            // `to` is already known readable and non-blank here (Required), so it
            // can be quoted directly — no second walk of the document.
            else errs.Add($"'to' must be {MailAddress.GrammarHelp} (got \"{to}\")");
        }

        var topic = Required(root, "topic", errs);

        var kind = MailKind.Status;
        if (!root.TryGetProperty("kind", out var k))
            errs.Add($"'kind' is required (one of: {Names<MailKind>()})");
        else if (!TryEnum(k, out kind))
            errs.Add($"'kind' must be one of: {Names<MailKind>()} (got {RawText(k)})");

        // priority: optional, defaulting to the LOWEST-traffic class. Optional
        // with a default is the house shape (DispatchPolicy's `default` field);
        // defaulting to ambient means a sender who forgets it can never
        // accidentally buy itself the mid-turn budget (d5).
        var priority = MailPriority.Ambient;
        if (root.TryGetProperty("priority", out var p) && !TryEnum(p, out priority))
            errs.Add($"'priority' must be one of: {Names<MailPriority>()} (got {RawText(p)})");

        // ttlDeliveries: optional, >= 1, and MEANINGLESS ON A UNICAST ADDRESS
        // (ADR-0018 d5). Zero or negative is malformed rather than "already
        // expired" — an envelope that can never be delivered is a typo, and
        // silently accepting one loses the message with no diagnosis.
        //
        // On `role@instance` the field is REFUSED, not ignored: there is one
        // addressee, so delivered is a fact and not a matter of opportunities,
        // and a field the reader accepts and disregards is a lie on a chain
        // nobody can amend. The refusal supersedes the >= 1 check — a sender
        // who writes `ttlDeliveries: 0` to a unicast address has one thing
        // wrong with the envelope, not two, and the address is the reason.
        int? ttl = unicast ? null : DefaultTtlDeliveries;
        if (root.TryGetProperty("ttlDeliveries", out var t))
        {
            if (unicast)
                errs.Add(
                    $"'ttlDeliveries' is not allowed on the unicast address \"{to}\" — " +
                    "a role@instance envelope has one recipient, so it is delivered once and " +
                    "never expires (send it to the bare role if you want delivery opportunities)");
            else if (t.ValueKind != JsonValueKind.Number || !t.TryGetInt32(out var n) || n < 1)
                errs.Add($"'ttlDeliveries' must be an integer >= 1 (got {RawText(t)})");
            else
                ttl = n;
        }

        // body: required and READABLE, but otherwise untouched — opaque prose.
        // Empty is allowed on purpose: a `status` whose whole content is its
        // topic is a legitimate (if terse) message, and the protocol has no
        // business deciding how much prose is enough.
        string? body = null;
        if (!root.TryGetProperty("body", out var b))
            errs.Add("'body' is required and must be a string");
        else if (b.ValueKind != JsonValueKind.String || (body = TryReadString(b)) is null)
            errs.Add($"'body' must be a string (got {RawText(b)})");

        var inReplyTo = Optional(root, "inReplyTo", errs);
        var prev = Optional(root, "prev", errs);

        return errs.Count > 0
            ? null
            : new MailEnvelope(id!, ts!, from!, to!, kind, topic!, priority, inReplyTo, ttl, body!, prev);
    }

    /// One STORE LINE: the JSONL reader's entry point. Bad bytes (not JSON, or
    /// more than one value on the line) land in `errors` exactly like a schema
    /// violation, so a reader has one thing to check and a torn or garbage line
    /// can never throw its way out of a digest run.
    public static MailEnvelope? TryParseLine(string line, out IReadOnlyList<string> errors)
    {
        try
        {
            // Default JsonDocumentOptions: trailing content after the object is a
            // JsonException, which is what we want — one envelope per line.
            using var doc = JsonDocument.Parse(line);
            // Every field is copied out as a string/enum/int before returning, so
            // the envelope safely outlives the document (same reasoning as
            // DispatchPolicy's parse — no Clone needed, unlike HarnessSpec).
            return TryParse(doc.RootElement, out errors);
        }
        catch (JsonException ex)
        {
            errors = new[] { $"not valid JSON: {ex.Message}" };
            return null;
        }
    }

    private static MailSender? ParseFrom(JsonElement root, List<string> errs)
    {
        if (!root.TryGetProperty("from", out var f))
        {
            errs.Add("'from' is required and must be an object with 'agent' and 'harness'");
            return null;
        }
        if (f.ValueKind != JsonValueKind.Object)
        {
            errs.Add($"'from' must be a JSON object (got {RawText(f)})");
            return null;
        }

        var before = errs.Count;
        CheckFields(f, KnownFromFields, prefix: "from", errs);
        var agent = Required(f, "agent", errs, prefix: "from");
        var harness = Required(f, "harness", errs, prefix: "from");
        var session = Optional(f, "session", errs, prefix: "from");

        // Materialize only a fully clean sender — a half-parsed one would poison
        // provenance rendering (moot while errs is non-empty, since TryParse then
        // returns null, but it keeps the value coherent regardless).
        return errs.Count == before ? new MailSender(agent!, harness!, session) : null;
    }

    // Unknown OR duplicate members of `obj`, reported one per violation.
    private static void CheckFields(
        JsonElement obj, IReadOnlySet<string> known, string? prefix, List<string> errs)
    {
        var at = prefix is null ? "" : prefix + ".";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var prop in obj.EnumerateObject())
        {
            if (!seen.Add(prop.Name))
                errs.Add($"duplicate field '{at}{prop.Name}'");
            else if (!known.Contains(prop.Name))
                errs.Add($"unknown field '{at}{prop.Name}' (known: {string.Join(", ", known)})");
        }
    }

    /// A required field: must be present, a string, READABLE, and non-empty.
    /// Whitespace-only is rejected for the same reason a blank policy criterion
    /// is — it names nothing, and a blank `to` would address no role at all.
    private static string? Required(
        JsonElement obj, string field, List<string> errs, string? prefix = null)
    {
        var at = prefix is null ? field : prefix + "." + field;
        if (!obj.TryGetProperty(field, out var v))
        {
            errs.Add($"'{at}' is required and must be a non-empty string");
            return null;
        }
        var s = v.ValueKind == JsonValueKind.String ? TryReadString(v) : null;
        if (string.IsNullOrWhiteSpace(s))
        {
            errs.Add($"'{at}' must be a non-empty string (got {RawText(v)})");
            return null;
        }
        return s;
    }

    /// An optional field carrying "a value, or explicitly nothing": absent and
    /// JSON `null` both mean null (the ADR's own envelope spells `"inReplyTo":
    /// null`, so null must not be a violation), and anything present-and-not-null
    /// must be a non-empty readable string.
    private static string? Optional(
        JsonElement obj, string field, List<string> errs, string? prefix = null)
    {
        if (!obj.TryGetProperty(field, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        var s = v.ValueKind == JsonValueKind.String ? TryReadString(v) : null;
        if (string.IsNullOrWhiteSpace(s))
        {
            var at = prefix is null ? field : prefix + "." + field;
            errs.Add($"'{at}' must be a non-empty string or null (got {RawText(v)})");
            return null;
        }
        return s;
    }

    /// The wire spelling of a closed set is its member name, lowercased — the
    /// ADR's `"kind": "status"` / `"priority": "urgent"`. Ordinal-ignore-case so
    /// a casing slip cannot silently drop mail, matching how the policy matcher
    /// treats event names; anything else is malformed.
    private static bool TryEnum<T>(JsonElement e, out T value) where T : struct, Enum
    {
        value = default;
        if (e.ValueKind != JsonValueKind.String) return false;
        var s = TryReadString(e);
        // Enum.TryParse would also accept "0" and comma-separated flag lists —
        // neither is a wire spelling we advertise, so match names explicitly.
        if (s is null) return false;
        foreach (var name in Enum.GetNames<T>())
            if (string.Equals(name, s, StringComparison.OrdinalIgnoreCase))
            {
                value = Enum.Parse<T>(name);
                return true;
            }
        return false;
    }

    private static string Names<T>() where T : struct, Enum =>
        string.Join(", ", Enum.GetNames<T>().Select(n => n.ToLowerInvariant()));

    /// JsonDocument DEFERS string unescaping: a lone-surrogate escape ("\ud800")
    /// parses as a syntactically fine document and then throws
    /// InvalidOperationException at GetString — not JsonException at Parse — so
    /// without this guard it escapes the never-throw-on-DATA contract and takes
    /// down whichever reader is walking the store. Null means "present but
    /// unreadable"; every caller treats that as a violation. (Same guard as
    /// DispatchPolicy.TryReadString, found by the ADR-0015 slice-6 skeptic pass;
    /// consolidating the five copies is the pending sweep in doc/scratch.md.)
    private static string? TryReadString(JsonElement e)
    {
        try { return e.GetString(); }
        catch (InvalidOperationException) { return null; }
    }

    /// Bound for a SENDER-CONTROLLED field on a display or telemetry surface —
    /// an id or a topic may legally run to the store's 128KiB line cap, and
    /// neither a digest head nor a trail line may inherit that. Keeps enough
    /// prefix to look the store line up, never splits a surrogate pair, and is
    /// NEVER applied to a body (the store is the record; a clamped body would
    /// be the store rewriting what a sender said).
    ///
    /// One spelling, two surfaces (the digest head and `mail.append`), so a
    /// clamped id in the trail and a clamped id in a rendered digest are the
    /// same string and join to each other verbatim.
    public const int HeadFieldChars = 120;

    public static string ClampField(string s)
    {
        if (s.Length <= HeadFieldChars) return s;
        var cut = HeadFieldChars;
        if (char.IsHighSurrogate(s[cut - 1])) cut--;
        return s[..cut] + "…";
    }

    /// Quote strings, raw-render everything else — `got "maybe"` for a bad
    /// string but `got 2` / `got true` for a bad type. An unreadable string falls
    /// back to its raw escaped text (GetRawText never unescapes, so it cannot
    /// throw).
    private static string RawText(JsonElement e) =>
        e.ValueKind == JsonValueKind.String && TryReadString(e) is { } s ? $"\"{s}\"" : e.GetRawText();
}
