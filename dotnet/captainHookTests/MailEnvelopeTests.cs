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

    // ---- the address grammar (ADR-0018 d2) ---------------------------------

    [Theory]
    [InlineData("main")]
    [InlineData("maintainer")]
    [InlineData("reviewer")]
    [InlineData("scribe")]
    [InlineData("auditor")]
    [InlineData("other")]
    [InlineData("intent-watcher")]
    [InlineData("s1")]
    public void LegacyRoles_StillParse(string role)
    {
        // The compatibility pin, listing the roles actually on the maintainer's
        // ledger and in this suite's fixtures. Introducing a grammar for a field
        // that accepted ANYTHING through all of item 20 is the one way this
        // slice could silently orphan mail already on the chain, so the corpus
        // is named rather than asserted about in the abstract.
        Assert.Equal(role, ParseValid(With($"\"to\": \"{role}\"")).To);
    }

    [Theory]
    [InlineData("maintainer@laptop-a")]
    [InlineData("reviewer@ci")]
    [InlineData("a@b")]
    [InlineData("9@9")]
    public void UnicastAddress_Parses(string to)
    {
        // Accepted and carried VERBATIM — and with NO ttl, which is why this
        // spells its own envelope instead of reusing the ADR-0016 one: that
        // envelope carries `ttlDeliveries: 3`, and ADR-0018 d5 refuses a ttl on
        // a unicast address. Routing on the instance is still `plan-unicast`'s.
        var e = ParseValid($$"""
            { "v": 1, "id": "m-04", "ts": "t",
              "from": { "agent": "a", "harness": "h" },
              "to": "{{to}}", "kind": "status", "topic": "x", "body": "b" }
            """);

        Assert.Equal(to, e.To);
        Assert.Null(e.TtlDeliveries);   // d5: no ttl, and no DEFAULTED ttl either
        Assert.False(e.HasTtl);
    }

    [Theory]
    [InlineData("Main")]              // uppercase: pinned lowercase, not folded
    [InlineData("MAINTAINER")]
    [InlineData("-lead")]             // must OPEN with alphanumeric
    [InlineData("_lead")]
    [InlineData("team.a")]            // '.' is not in the grammar
    [InlineData("team a")]
    [InlineData("team/a")]
    [InlineData("team_a")]
    [InlineData("mаintainer")]        // Cyrillic 'а' — renders as the real role
    [InlineData("@instance")]         // empty role half
    [InlineData("role@")]             // empty instance half
    [InlineData("@")]
    [InlineData("a@b@c")]             // two readings, both plausible => refuse
    [InlineData("a@B")]               // the grammar binds BOTH halves
    [InlineData("A@b")]
    [InlineData("a@b.c")]
    public void UngrammaticalAddress_IsRefused(string to)
    {
        // Refused, never guessed (d2): a misrouted envelope is silent, and a
        // silent failure is what this whole subsystem is built against. The
        // homoglyph case is the reason the grammar is ASCII by hand rather than
        // char.IsLetterOrDigit — a Unicode-aware check admits a mailbox that
        // renders identically to a real one and receives none of its mail.
        Assert.Contains(ParseInvalid(With($"\"to\": \"{to}\"")), e => e.Contains("'to' must be"));
    }

    [Fact]
    public void BlankAddress_IsRefusedAsBlank_NotAsUngrammatical()
    {
        // Required runs first, so an empty `to` keeps its old message. One
        // violation, one error — the grammar must not double-report a field
        // that never named anything to begin with.
        var errors = ParseInvalid(With("\"to\": \"\""));
        Assert.Single(errors);
        Assert.Contains(errors, e => e.Contains("non-empty string"));
    }

    [Fact]
    public void AddressError_TeachesTheGrammar_AndQuotesWhatItGot()
    {
        // These land in the trail and on `mail send`'s stderr at the one moment
        // a human can still fix the typo, so the message carries the rule.
        var errors = ParseInvalid(With("\"to\": \"Ops\""));
        Assert.Contains(errors, e => e.Contains("[a-z0-9][a-z0-9-]*"));
        Assert.Contains(errors, e => e.Contains("got \"Ops\""));
    }

    // ---- unicast has no TTL (ADR-0018 d5) ----------------------------------

    [Theory]
    [InlineData("1")]
    [InlineData("3")]
    [InlineData("99")]
    public void Ttl_OnUnicastAddress_IsRefused(string value)
    {
        // Refused, not ignored. An accepted-and-ignored field is a lie in a
        // record nobody can amend: a reader six months from now would see a
        // countdown on an envelope that never counted.
        var errors = ParseInvalid($$"""
            { "v": 1, "id": "m", "ts": "t", "from": { "agent": "a", "harness": "h" },
              "to": "reviewer@ci", "kind": "status", "topic": "x",
              "ttlDeliveries": {{value}}, "body": "b" }
            """);

        Assert.Contains(errors, e => e.Contains("'ttlDeliveries' is not allowed on the unicast address"));
        // The message names the way out, because the sender's intent is legible:
        // they wanted delivery opportunities, which the bare role still gives.
        Assert.Contains(errors, e => e.Contains("send it to the bare role"));
    }

    [Fact]
    public void Ttl_OnUnicastAddress_IsOneViolation_NotTwo()
    {
        // `ttlDeliveries: 0` on a unicast address breaks two rules on paper —
        // it is < 1 AND it is on a unicast address. The envelope has ONE thing
        // wrong with it, and the address is the reason; reporting both would
        // send the sender to fix a bound that does not apply to them.
        var errors = ParseInvalid("""
            { "v": 1, "id": "m", "ts": "t", "from": { "agent": "a", "harness": "h" },
              "to": "reviewer@ci", "kind": "status", "topic": "x",
              "ttlDeliveries": 0, "body": "b" }
            """);

        Assert.Single(errors);
        Assert.Contains("not allowed on the unicast address", errors[0]);
    }

    [Fact]
    public void UngrammaticalAddress_DoesNotAlsoAccuseTheTtl()
    {
        // A `to` that fails the grammar is not unicast and not broadcast — it is
        // nothing. The ttl rule must not be guessed from a name we refused to
        // read, or one typo becomes two errors pointing in different directions.
        var errors = ParseInvalid("""
            { "v": 1, "id": "m", "ts": "t", "from": { "agent": "a", "harness": "h" },
              "to": "Reviewer@CI", "kind": "status", "topic": "x",
              "ttlDeliveries": 3, "body": "b" }
            """);

        Assert.Single(errors);
        Assert.Contains("'to' must be", errors[0]);
    }

    [Fact]
    public void BroadcastAddress_StillDefaultsAndStillBounds()
    {
        // The other half of d5: nothing about a ROLE-addressed envelope moved.
        Assert.Equal(3, ParseValid(With("\"to\": \"reviewer\"")).TtlDeliveries);
        Assert.Equal(7, ParseValid(With("\"ttlDeliveries\": 7")).TtlDeliveries);
        Assert.Contains(ParseInvalid(With("\"ttlDeliveries\": 0")), e => e.Contains("integer >= 1"));
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

/// ADR-0018 decisions 1-2 (roadmap item 23, slice 1) — the address VALUE, as
/// distinct from the envelope field that carries it. The parser tests above pin
/// what reaches the ledger; these pin what the later slices will route on:
/// `plan-unicast` reads `Instance`, `unicast-refuses-ttl` reads `IsUnicast`,
/// and `instance-registration` reuses `IsRole` for `--as`.
public class MailAddressTests
{
    private static MailAddress Parse(string s)
    {
        Assert.True(MailAddress.TryParse(s, out var a), $"expected '{s}' to parse");
        return a;
    }

    [Fact]
    public void BareRole_IsBroadcast()
    {
        // A role-only address is not a special case bolted onto unicast — it is
        // exactly what the bus did before this slice (ADR-0016 broadcast), and
        // `Instance is null` is the whole of that difference.
        var a = Parse("maintainer");
        Assert.Equal("maintainer", a.Role);
        Assert.Null(a.Instance);
        Assert.False(a.IsUnicast);
    }

    [Fact]
    public void RoleAtInstance_SplitsOnTheOneSeparator()
    {
        var a = Parse("maintainer@laptop-a");
        Assert.Equal("maintainer", a.Role);
        Assert.Equal("laptop-a", a.Instance);
        Assert.True(a.IsUnicast);
    }

    [Theory]
    [InlineData("maintainer")]
    [InlineData("maintainer@laptop-a")]
    public void ToString_RoundTripsTheAcceptedSpelling(string s)
    {
        // The ledger holds the address as the sender wrote it. A rendering that
        // differed by so much as a separator would be a SECOND spelling of one
        // mailbox — ADR-0016 N8's hazard, at the address layer.
        Assert.Equal(s, Parse(s).ToString());
        Assert.Equal(s, MailAddress.TryParse(s, out var a) ? a.ToString() : null);
    }

    [Theory]
    [InlineData("a")]
    [InlineData("0")]
    [InlineData("a-b-c")]
    [InlineData("a-")]        // a trailing hyphen is legal: the ADR's grammar verbatim
    [InlineData("laptop2")]
    public void IsRole_Accepts(string s) => Assert.True(MailAddress.IsRole(s));

    [Theory]
    [InlineData("")]
    [InlineData("-a")]
    [InlineData("A")]
    [InlineData("a b")]
    [InlineData("a.b")]
    [InlineData("a@b")]       // the separator is never part of a HALF
    [InlineData("é")]
    public void IsRole_Refuses(string s) => Assert.False(MailAddress.IsRole(s));

    [Fact]
    public void FailedParse_YieldsDefault_NeverAPartialAddress()
    {
        // A half-populated address would be a mailbox nobody named. `false` and
        // a default are the only two things a caller may see.
        Assert.False(MailAddress.TryParse("a@b@c", out var a));
        Assert.Equal(default, a);
        Assert.Null(a.Instance);
    }
}
