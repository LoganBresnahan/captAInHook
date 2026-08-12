using System.Text.Json;
using CaptainHook.Mail;

namespace CaptainHook.Tests;

/// ADR-0016 decision 2 — the strict envelope parser (roadmap item 20, phase 1).
/// The failure mode is the mirror of the policy parser's: a malformed envelope
/// is warned-and-skipped, so a rule that is too LOOSE delivers a message nobody
/// can render, and one that is too TIGHT silently drops real mail. These pin
/// exactly which lines parse and which are rejected, that every violation
/// surfaces in one pass (moby-style, like DispatchPolicy/HarnessSpec), and that
/// bad DATA is never a throw.
public class MailEnvelopeParseTests
{
    /// The ADR-0016 d2 envelope verbatim — the contract this parser exists to
    /// accept. Written once here so a drift in the ADR's shape breaks one test,
    /// not twenty.
    private const string AdrEnvelope = """
        { "v": 1, "id": "m-01", "ts": "2026-08-12T19:00:00Z",
          "from": { "agent": "intent-watcher", "harness": "claude-code", "session": "s-77" },
          "to": "main",
          "kind": "status",
          "topic": "build",
          "priority": "ambient",
          "inReplyTo": null,
          "ttlDeliveries": 3,
          "body": "opaque prose" }
        """;

    private static MailEnvelope? Parse(string json, out IReadOnlyList<string> errors)
    {
        using var doc = JsonDocument.Parse(json);
        // The envelope holds only strings/enums/ints (every field is copied out
        // during the parse), so it safely outlives the JsonDocument.
        return MailEnvelope.TryParse(doc.RootElement, out errors);
    }

    private static MailEnvelope ParseValid(string json)
    {
        var e = Parse(json, out var errors);
        Assert.True(e is not null, $"expected valid, got: {string.Join("; ", errors)}");
        return e!;
    }

    private static IReadOnlyList<string> ParseInvalid(string json)
    {
        var e = Parse(json, out var errors);
        Assert.Null(e);
        Assert.NotEmpty(errors);
        return errors;
    }

    // ---- what a valid envelope IS ------------------------------------------

    [Fact]
    public void AdrEnvelope_ParsesWithEveryField()
    {
        var e = ParseValid(AdrEnvelope);

        Assert.Equal("m-01", e.Id);
        Assert.Equal("2026-08-12T19:00:00Z", e.Ts);
        Assert.Equal("intent-watcher", e.From.Agent);
        Assert.Equal("claude-code", e.From.Harness);
        Assert.Equal("s-77", e.From.Session);
        Assert.Equal("main", e.To);
        Assert.Equal(MailKind.Status, e.Kind);
        Assert.Equal("build", e.Topic);
        Assert.Equal(MailPriority.Ambient, e.Priority);
        Assert.Equal(3, e.TtlDeliveries);
        Assert.Equal("opaque prose", e.Body);
        // `inReplyTo: null` is the ADR's own spelling — explicitly nothing, not a
        // violation — and stays UNREAD in v1 (d9).
        Assert.Null(e.InReplyTo);
        Assert.Null(e.Prev);   // the store writes it, not the sender
    }

    [Fact]
    public void MinimalEnvelope_DefaultsAmbientAndDefaultTtl()
    {
        // Everything the sender may omit, omitted. The defaults are the safe
        // direction: the lowest-traffic seam class (d5) and a bounded TTL (d3) —
        // a forgotten `priority` can never buy the mid-turn budget, and a
        // forgotten `ttlDeliveries` can never mean "forever".
        var e = ParseValid("""
            { "v": 1, "id": "m-02", "ts": "t",
              "from": { "agent": "a", "harness": "h" },
              "to": "main", "kind": "alert", "topic": "x", "body": "y" }
            """);

        Assert.Equal(MailPriority.Ambient, e.Priority);
        Assert.Equal(MailEnvelope.DefaultTtlDeliveries, e.TtlDeliveries);
        Assert.Equal(3, e.TtlDeliveries);
        Assert.Null(e.InReplyTo);
    }

    [Fact]
    public void SessionlessSender_IsValid()
    {
        // A write-only member (hookless harness / cron-shaped observer, d5) has
        // no session to name. Requiring one would make the bus's cheapest
        // membership class unrepresentable.
        var e = ParseValid("""
            { "v": 1, "id": "m-03", "ts": "t",
              "from": { "agent": "edit-log", "harness": "none" },
              "to": "main", "kind": "status", "topic": "x", "body": "" }
            """);

        Assert.Null(e.From.Session);
        Assert.Equal("", e.Body);   // empty body is legitimate: the topic carries it
    }

    [Theory]
    [InlineData("status", MailKind.Status)]
    [InlineData("request", MailKind.Request)]
    [InlineData("answer", MailKind.Answer)]
    [InlineData("alert", MailKind.Alert)]
    [InlineData("ALERT", MailKind.Alert)]    // casing slip must not drop mail
    public void EveryKind_AndCasing(string wire, MailKind expected) =>
        Assert.Equal(expected, ParseValid(With($"\"kind\": \"{wire}\"")).Kind);

    [Theory]
    [InlineData("ambient", MailPriority.Ambient)]
    [InlineData("reconcile", MailPriority.Reconcile)]
    [InlineData("urgent", MailPriority.Urgent)]
    [InlineData("Urgent", MailPriority.Urgent)]
    public void EveryPriority_AndCasing(string wire, MailPriority expected) =>
        Assert.Equal(expected, ParseValid(With($"\"priority\": \"{wire}\"")).Priority);

    [Fact]
    public void InReplyTo_IsPreservedButUnread()
    {
        // Reserved by d9 so v1 envelopes survive a future ask/reply design
        // without a version bump: the parser carries it, nothing branches on it.
        Assert.Equal("m-01", ParseValid(With("\"inReplyTo\": \"m-01\"")).InReplyTo);
    }

    [Fact]
    public void StoredLine_CarryingPrev_IsNotMalformed()
    {
        // The forward-compat pin for phase 2: the STORE appends `prev` (d11), so
        // a stored line has a field the sender never wrote. If `prev` were an
        // unknown field, every chained line would read as malformed the moment
        // chaining lands. Phase 1 reserves the NAME only — the encoding is
        // phase 2's durable-format decision.
        Assert.Equal("abc123", ParseValid(With("\"prev\": \"abc123\"")).Prev);
    }

    // ---- what is MALFORMED -------------------------------------------------

    [Theory]
    [InlineData("[]")]
    [InlineData("\"a string\"")]
    [InlineData("7")]
    [InlineData("null")]
    public void NonObject_IsMalformed(string json) =>
        Assert.Contains(ParseInvalid(json), e => e.Contains("must be a JSON object"));

    [Theory]
    [InlineData("""{ "id": "m", "ts": "t", "from": { "agent": "a", "harness": "h" }, "to": "main", "kind": "status", "topic": "x", "body": "b" }""")]  // missing
    [InlineData("""{ "v": 2, "id": "m", "ts": "t", "from": { "agent": "a", "harness": "h" }, "to": "main", "kind": "status", "topic": "x", "body": "b" }""")]     // wrong version
    [InlineData("""{ "v": "1", "id": "m", "ts": "t", "from": { "agent": "a", "harness": "h" }, "to": "main", "kind": "status", "topic": "x", "body": "b" }""")]   // string, not number
    public void Version_MustBeTheNumberOne(string json) =>
        Assert.Contains(ParseInvalid(json), e => e.Contains("'v'"));

    [Fact]
    public void UnknownTopLevelField_IsMalformed()
    {
        // Strict never-guess (the DispatchPolicy tightening): an unrecognized key
        // is a typo or a newer dialect, and guessing which is how a `priorty`
        // silently becomes ambient forever.
        Assert.Contains(ParseInvalid(With("\"urgency\": \"urgent\"")), e => e.Contains("unknown field 'urgency'"));
    }

    [Fact]
    public void UnknownSenderField_IsMalformed_AndNamesItsPath() =>
        Assert.Contains(
            ParseInvalid("""
                { "v": 1, "id": "m", "ts": "t",
                  "from": { "agent": "a", "harness": "h", "pid": 9 },
                  "to": "main", "kind": "status", "topic": "x", "body": "b" }
                """),
            e => e.Contains("unknown field 'from.pid'"));

    [Fact]
    public void DuplicateField_IsMalformed()
    {
        // System.Text.Json would silently keep ONE of the two — an envelope that
        // quietly changes recipient is exactly the ambiguity strict-never-guess
        // refuses. (Not expressible via `With`: two `to` keys.)
        var errors = ParseInvalid("""
            { "v": 1, "id": "m", "ts": "t", "from": { "agent": "a", "harness": "h" },
              "to": "main", "to": "auditor", "kind": "status", "topic": "x", "body": "b" }
            """);
        Assert.Contains(errors, e => e.Contains("duplicate field 'to'"));
    }

    [Theory]
    [InlineData("id")]
    [InlineData("ts")]
    [InlineData("to")]
    [InlineData("topic")]
    public void RequiredStrings_MissingEmptyOrWrongType(string field)
    {
        Assert.Contains(ParseInvalid(Without(field)), e => e.Contains($"'{field}'"));
        Assert.Contains(ParseInvalid(With($"\"{field}\": \"\"")), e => e.Contains($"'{field}'"));
        Assert.Contains(ParseInvalid(With($"\"{field}\": \"   \"")), e => e.Contains($"'{field}'"));
        Assert.Contains(ParseInvalid(With($"\"{field}\": 7")), e => e.Contains($"'{field}'"));
        Assert.Contains(ParseInvalid(With($"\"{field}\": null")), e => e.Contains($"'{field}'"));
    }

    [Theory]
    [InlineData("""{ "v": 1, "id": "m", "ts": "t", "to": "main", "kind": "status", "topic": "x", "body": "b" }""")]                              // no from
    [InlineData("""{ "v": 1, "id": "m", "ts": "t", "from": "intent-watcher", "to": "main", "kind": "status", "topic": "x", "body": "b" }""")]    // not an object
    [InlineData("""{ "v": 1, "id": "m", "ts": "t", "from": { "harness": "h" }, "to": "main", "kind": "status", "topic": "x", "body": "b" }""")]  // no agent
    [InlineData("""{ "v": 1, "id": "m", "ts": "t", "from": { "agent": "a" }, "to": "main", "kind": "status", "topic": "x", "body": "b" }""")]    // no harness
    [InlineData("""{ "v": 1, "id": "m", "ts": "t", "from": { "agent": "a", "harness": "h", "session": "" }, "to": "main", "kind": "status", "topic": "x", "body": "b" }""")]  // blank session
    public void Sender_MustNameAgentAndHarness(string json) =>
        Assert.Contains(ParseInvalid(json), e => e.Contains("from"));

    [Theory]
    [InlineData("\"kind\": \"gossip\"")]
    [InlineData("\"kind\": \"\"")]
    [InlineData("\"kind\": 1")]
    [InlineData("\"kind\": null")]
    public void UnknownKind_IsMalformed(string field) =>
        Assert.Contains(ParseInvalid(With(field)), e => e.Contains("'kind'"));

    [Fact]
    public void MissingKind_IsMalformed() =>
        Assert.Contains(ParseInvalid(Without("kind")), e => e.Contains("'kind' is required"));

    [Theory]
    [InlineData("\"priority\": \"immediate\"")]
    [InlineData("\"priority\": \"\"")]
    [InlineData("\"priority\": 2")]
    [InlineData("\"priority\": null")]
    public void UnknownPriority_IsMalformed(string field)
    {
        // An unknown priority must NOT quietly fall back to the default: a sender
        // asking for a seam class we do not have is a bug in the sender, and
        // silently downgrading it hides that at exactly the moment it matters.
        Assert.Contains(ParseInvalid(With(field)), e => e.Contains("'priority'"));
    }

    [Theory]
    [InlineData("0")]        // a message that can never be delivered is a typo
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("\"3\"")]
    [InlineData("null")]
    [InlineData("99999999999999")]   // beyond Int32 — not an integer count
    public void BadTtl_IsMalformed(string value) =>
        Assert.Contains(ParseInvalid(With($"\"ttlDeliveries\": {value}")), e => e.Contains("'ttlDeliveries'"));

    [Theory]
    [InlineData("\"body\": 7")]
    [InlineData("\"body\": null")]
    [InlineData("\"body\": { \"text\": \"hi\" }")]
    public void BadBody_IsMalformed(string field) =>
        Assert.Contains(ParseInvalid(With(field)), e => e.Contains("'body'"));

    [Fact]
    public void MissingBody_IsMalformed() =>
        Assert.Contains(ParseInvalid(Without("body")), e => e.Contains("'body' is required"));

    [Theory]
    [InlineData("\"inReplyTo\": \"\"")]
    [InlineData("\"inReplyTo\": 7")]
    [InlineData("\"prev\": \"\"")]
    [InlineData("\"prev\": false")]
    public void BadOptionalString_IsMalformed(string field) =>
        Assert.Contains(ParseInvalid(With(field)), e => e.Contains("or null"));

    // ---- the contract itself: never throw, report everything ----------------

    [Theory]
    [InlineData("\"to\": \"\\ud800\"")]
    [InlineData("\"body\": \"\\ud800\"")]
    [InlineData("\"kind\": \"\\ud800\"")]
    [InlineData("\"inReplyTo\": \"\\ud800\"")]
    public void LoneSurrogate_IsMalformed_NeverThrown(string field)
    {
        // JsonDocument DEFERS unescaping, so this parses as a fine document and
        // only throws InvalidOperationException at GetString. Without the
        // TryReadString guard it escapes the never-throw-on-DATA contract and
        // takes down whichever reader is walking the store — one poisoned line
        // killing a whole digest run. (Same class of find as the ADR-0015
        // slice-6 skeptic pass on DispatchPolicy.)
        Assert.NotEmpty(ParseInvalid(With(field)));
    }

    [Fact]
    public void EveryViolation_IsReportedInOnePass()
    {
        // Moby-style: one edit round-trip should surface every fault, not the
        // first one. Six independent violations, six errors.
        var errors = ParseInvalid("""
            { "v": 9, "id": "", "ts": "t",
              "from": { "agent": "a", "harness": "h" },
              "to": "main", "kind": "gossip", "topic": "x",
              "priority": "immediate", "ttlDeliveries": 0, "body": "b",
              "extra": true }
            """);

        Assert.Contains(errors, e => e.Contains("'v'"));
        Assert.Contains(errors, e => e.Contains("'id'"));
        Assert.Contains(errors, e => e.Contains("'kind'"));
        Assert.Contains(errors, e => e.Contains("'priority'"));
        Assert.Contains(errors, e => e.Contains("'ttlDeliveries'"));
        Assert.Contains(errors, e => e.Contains("unknown field 'extra'"));
    }

    [Fact]
    public void ErrorMessages_QuoteStringsAndRawRenderTypes()
    {
        // The reader logs these into the trail; `got "gossip"` vs `got 7` is the
        // difference between a fixable warning and a puzzle.
        Assert.Contains(ParseInvalid(With("\"kind\": \"gossip\"")), e => e.Contains("got \"gossip\""));
        Assert.Contains(ParseInvalid(With("\"kind\": 7")), e => e.Contains("got 7"));
    }

    // ---- TryParseLine: the JSONL reader's entry point ------------------------

    [Fact]
    public void TryParseLine_ValidLine_Parses()
    {
        var e = MailEnvelope.TryParseLine(Compact(AdrEnvelope), out var errors);
        Assert.True(e is not null, $"expected valid, got: {string.Join("; ", errors)}");
        Assert.Equal("m-01", e!.Id);
        Assert.Empty(errors);
    }

    [Theory]
    [InlineData("")]                          // blank line
    [InlineData("   ")]
    [InlineData("not json at all {")]         // garbage
    [InlineData("{ \"v\": 1, \"id\": \"m\"")] // TORN final line — a crash mid-append
    [InlineData("{} {}")]                     // two values on one line: JSONL framing broken
    public void TryParseLine_BadBytes_AreErrorsNotThrows(string line)
    {
        // The store is append-only and read by every digest run, so a torn or
        // garbage line must land in `errors` exactly like a schema violation:
        // the reader has ONE thing to check, and a bad line can never throw its
        // way out of a dispatch.
        Assert.Null(MailEnvelope.TryParseLine(line, out var errors));
        Assert.NotEmpty(errors);
    }

    // ---- helpers: mutate the ADR envelope, one field at a time --------------

    /// The ADR envelope with `field` (a full `"name": value` pair) replacing any
    /// same-named field, so each case differs from the contract in exactly one
    /// way and nothing else can explain a rejection.
    private static string With(string field)
    {
        var name = field[..field.IndexOf(':')].Trim().Trim('"');
        return "{ " + field + ", " + Body(Without(name)) + " }";
    }

    /// The ADR envelope minus one field.
    private static string Without(string field)
    {
        using var doc = JsonDocument.Parse(AdrEnvelope);
        var kept = doc.RootElement.EnumerateObject()
            .Where(p => p.Name != field)
            .Select(p => $"\"{p.Name}\": {p.Value.GetRawText()}");
        return "{ " + string.Join(", ", kept) + " }";
    }

    private static string Body(string obj) => obj.Trim()[1..^1].Trim();

    private static string Compact(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(doc.RootElement);
    }
}
