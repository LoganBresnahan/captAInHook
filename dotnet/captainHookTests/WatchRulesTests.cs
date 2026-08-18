using System.Text.Json;
using CaptainHook.Core;
using CaptainHook.Mail;

namespace CaptainHook.Tests;

/// ADR-0017 decision 7 (roadmap item 22, slice `watch-rules`) — the watcher's
/// rule document and its strict parser.
///
/// The document's whole job is CONSENT for a channel that spends the owner's
/// tokens and takes a turn on their behalf (N1), so the property these tests
/// exist to pin is the one that has no exceptions: **absent and malformed mean
/// exactly the same thing — no robot nudges.** `dispatch.json` has a real
/// fail-open direction to weigh; this file has none, and a future edit that
/// grew one (a default rule, a keep-last-good, a partial accept) would be
/// caught here.
///
/// The rest is the strict-parse table `DispatchPolicy` and `HarnessSpec`
/// already established: every violation collected in one pass, all-or-nothing
/// accept, never a throw on bad data.
public class WatchRulesParseTests
{
    private static WatchRules? Parse(string json, out IReadOnlyList<string> errors)
    {
        using var doc = JsonDocument.Parse(json);
        return WatchRules.TryParse(doc.RootElement, out errors);
    }

    private static WatchRules ParseValid(string json)
    {
        var r = Parse(json, out var errors);
        Assert.True(r is not null, $"expected valid, got: {string.Join("; ", errors)}");
        return r!;
    }

    private static IReadOnlyList<string> Rejects(string json)
    {
        var r = Parse(json, out var errors);
        Assert.Null(r);
        Assert.NotEmpty(errors);
        return errors;
    }

    /// The exact document from ADR-0017 decision 7 — the contract this parser
    /// exists to accept.
    [Fact]
    public void AdrExampleFile_Parses()
    {
        var r = ParseValid("""
            { "version": 1,
              "rules": [
                { "role": "reviewer",
                  "when":   { "priority": ">=urgent", "quietFor": "10min", "noLiveSession": true },
                  "budget": { "perEnvelope": 1, "perRoleHour": 4 } } ] }
            """);

        var rule = Assert.Single(r.Rules);
        Assert.Equal("reviewer", rule.Role);
        Assert.Equal(new WatchPriority(MailPriority.Urgent, AtLeast: true), rule.When.Priority);
        Assert.Equal(600_000, rule.When.QuietForMs);
        Assert.True(rule.When.NoLiveSession);
        Assert.Equal(new WatchBudget(1, 4), rule.Budget);
    }

    /// `noLiveSession` defaults TRUE (d7's mixed-role rule): a role a human also
    /// holds must not be woken by a robot while that human is sitting there, and
    /// the safe reading of an omitted field is the one that does less.
    [Fact]
    public void NoLiveSession_DefaultsTrue()
    {
        var r = ParseValid("""
            { "version": 1, "rules": [
              { "role": "reviewer", "when": { "quietFor": "5min" },
                "budget": { "perEnvelope": 1, "perRoleHour": 1 } } ] }
            """);
        Assert.True(Assert.Single(r.Rules).When.NoLiveSession);
        Assert.Null(Assert.Single(r.Rules).When.Priority);
    }

    /// An empty rule list is legal and means nothing is nudged — the position an
    /// operator reaches for when switching the channel off without deleting
    /// their rules. `rules` may also be omitted entirely.
    [Theory]
    [InlineData("""{ "version": 1, "rules": [] }""")]
    [InlineData("""{ "version": 1 }""")]
    public void AnEmptyOrAbsentRuleList_IsValidAndNudgesNothing(string json)
        => Assert.Empty(ParseValid(json).Rules);

    /// Order is PRESERVED. Which rule wins for a role is `watcher-brain`'s
    /// (first-match-wins, like the policy matcher), and it can only be that if
    /// the parse does not sort, dedupe, or collapse two rules naming one role.
    [Fact]
    public void TwoRulesForOneRole_AreKeptInOrder()
    {
        var r = ParseValid("""
            { "version": 1, "rules": [
              { "role": "reviewer", "when": { "priority": "urgent" },
                "budget": { "perEnvelope": 1, "perRoleHour": 9 } },
              { "role": "reviewer", "when": { "quietFor": "1h" },
                "budget": { "perEnvelope": 2, "perRoleHour": 3 } } ] }
            """);
        Assert.Equal(2, r.Rules.Count);
        Assert.Equal(9, r.Rules[0].Budget.PerRoleHour);
        Assert.Equal(3, r.Rules[1].Budget.PerRoleHour);
    }

    // ---- durations ---------------------------------------------------------

    [Theory]
    [InlineData("\"250ms\"", 250)]
    [InlineData("\"30s\"", 30_000)]
    [InlineData("\"10min\"", 600_000)]
    [InlineData("\"2h\"", 7_200_000)]
    [InlineData("\"0s\"", 0)]           // the explicit "the moment it lands"
    public void ADuration_ParsesToMilliseconds(string quietFor, int expected)
    {
        var r = ParseValid($$"""
            { "version": 1, "rules": [
              { "role": "r", "when": { "quietFor": {{quietFor}} },
                "budget": { "perEnvelope": 1, "perRoleHour": 1 } } ] }
            """);
        Assert.Equal(expected, Assert.Single(r.Rules).When.QuietForMs);
    }

    /// The unit set is CLOSED and a bare number is refused: `600` is ambiguous
    /// between seconds and milliseconds by a factor of a thousand, and guessing
    /// wrong is the difference between a nudge in ten minutes and a nudge in one
    /// second. A huge value is a refusal rather than an overflow into a small or
    /// negative deadline.
    [Theory]
    [InlineData("\"600\"")]             // no unit
    [InlineData("600")]                 // not even a string
    [InlineData("\"min\"")]             // no number
    [InlineData("\"1.5h\"")]            // no fractions — write 90min
    [InlineData("\"-5min\"")]
    [InlineData("\"10 min\"")]
    [InlineData("\"10MIN\"")]           // units are not a closed set to case-fold against
    [InlineData("\"10sec\"")]
    [InlineData("\"10d\"")]
    [InlineData("\"99999999999999h\"")]
    [InlineData("\"\"")]
    public void ABadDuration_IsRefused(string quietFor)
    {
        var errors = Rejects($$"""
            { "version": 1, "rules": [
              { "role": "r", "when": { "quietFor": {{quietFor}} },
                "budget": { "perEnvelope": 1, "perRoleHour": 1 } } ] }
            """);
        Assert.Contains(errors, e => e.Contains("quietFor"));
    }

    // ---- priorities --------------------------------------------------------

    /// A priority is a CLOSED set, so a casing slip is CORRECTED against it —
    /// the same fold `MailEnvelope` applies to `kind`/`priority` on the way in,
    /// and the deliberate divergence from an address, which names an open
    /// universe of mailboxes and has nothing to correct against (ADR-0018 d2).
    [Theory]
    [InlineData("\"urgent\"", MailPriority.Urgent, false)]
    [InlineData("\">=urgent\"", MailPriority.Urgent, true)]
    [InlineData("\"AMBIENT\"", MailPriority.Ambient, false)]
    [InlineData("\">=Reconcile\"", MailPriority.Reconcile, true)]
    public void APriority_ParsesWithItsAtLeastPrefix(string spelling, MailPriority expected, bool atLeast)
    {
        var r = ParseValid($$"""
            { "version": 1, "rules": [
              { "role": "r", "when": { "priority": {{spelling}} },
                "budget": { "perEnvelope": 1, "perRoleHour": 1 } } ] }
            """);
        Assert.Equal(new WatchPriority(expected, atLeast), Assert.Single(r.Rules).When.Priority);
    }

    /// Matched by NAME, never `Enum.TryParse`, which also accepts "2", comma
    /// lists and padded spellings — none of them wire spellings we advertise.
    [Theory]
    [InlineData("\"2\"")]
    [InlineData("\"urgent,ambient\"")]
    [InlineData("\" urgent\"")]
    [InlineData("\">urgent\"")]
    [InlineData("\">=\"")]
    [InlineData("\"loud\"")]
    [InlineData("true")]
    public void ABadPriority_IsRefused(string spelling)
    {
        var errors = Rejects($$"""
            { "version": 1, "rules": [
              { "role": "r", "when": { "priority": {{spelling}} },
                "budget": { "perEnvelope": 1, "perRoleHour": 1 } } ] }
            """);
        Assert.Contains(errors, e => e.Contains("priority"));
    }

    // ---- the required halves -----------------------------------------------

    /// A `when` naming only `noLiveSession` states NO threshold: the field
    /// defaults to true, so the rule it describes wakes a model the instant mail
    /// lands. Somebody who wants that writes `"quietFor": "0s"` and can be held
    /// to having meant it.
    [Fact]
    public void AWhenWithNoThreshold_IsRefused()
    {
        var errors = Rejects("""
            { "version": 1, "rules": [
              { "role": "r", "when": { "noLiveSession": true },
                "budget": { "perEnvelope": 1, "perRoleHour": 1 } } ] }
            """);
        Assert.Contains(errors, e => e.Contains("at least one of priority/quietFor"));
    }

    /// No budget defaults, in either direction: every candidate default is a
    /// number of model calls this code would be choosing to spend without being
    /// told (N1). A rule states its bound or it is not a rule.
    [Theory]
    [InlineData("""{ "role": "r", "when": { "quietFor": "1min" } }""", "'budget' is required")]
    [InlineData("""{ "role": "r", "when": { "quietFor": "1min" }, "budget": { "perEnvelope": 1 } }""", "'perRoleHour' is required")]
    [InlineData("""{ "role": "r", "when": { "quietFor": "1min" }, "budget": { "perRoleHour": 1 } }""", "'perEnvelope' is required")]
    [InlineData("""{ "role": "r", "budget": { "perEnvelope": 1, "perRoleHour": 1 } }""", "'when' is required")]
    [InlineData("""{ "when": { "quietFor": "1min" }, "budget": { "perEnvelope": 1, "perRoleHour": 1 } }""", "'role' is required")]
    public void AHalfWrittenRule_IsRefused(string rule, string expected)
    {
        var errors = Rejects($$"""{ "version": 1, "rules": [ {{rule}} ] }""");
        Assert.Contains(errors, e => e.Contains(expected));
    }

    /// Zero is refused rather than read as "never": a rule that can never fire
    /// is written by deleting it, and a 0 here is far more often a half-finished
    /// edit than an intention.
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("\"4\"")]
    public void ABudgetCounterBelowOne_OrNotAWholeNumber_IsRefused(string value)
    {
        var errors = Rejects($$"""
            { "version": 1, "rules": [
              { "role": "r", "when": { "quietFor": "1min" },
                "budget": { "perEnvelope": {{value}}, "perRoleHour": 1 } } ] }
            """);
        Assert.Contains(errors, e => e.Contains("perEnvelope"));
    }

    // ---- the role ----------------------------------------------------------

    /// The role goes through `MailAddress.IsRole` — the envelope parser's own
    /// predicate, never a second spelling of the grammar. A rule naming a role
    /// no sender could address can never fire, silently, forever.
    [Theory]
    [InlineData("\"Reviewer\"")]        // uppercase is pinned out of the grammar
    [InlineData("\"-lead\"")]
    [InlineData("\"a b\"")]
    [InlineData("\"\"")]
    [InlineData("4")]
    public void AnUngrammaticalRole_IsRefused(string role)
    {
        var errors = Rejects($$"""
            { "version": 1, "rules": [
              { "role": {{role}}, "when": { "quietFor": "1min" },
                "budget": { "perEnvelope": 1, "perRoleHour": 1 } } ] }
            """);
        Assert.Contains(errors, e => e.Contains("role"));
    }

    /// An INSTANCE address is refused, and this refusal is a judgement rather
    /// than a grammar check: a rule decides whether a ROLE may be woken, while
    /// the mailbox a nudge names is an instance the watcher found. Refusing an
    /// unbuilt spelling is the reversible direction — no document anybody has
    /// written becomes invalid if per-instance rules are allowed later.
    [Fact]
    public void APerInstanceRule_IsRefusedForNow_WithTheReason()
    {
        var errors = Rejects("""
            { "version": 1, "rules": [
              { "role": "reviewer@laptop-a", "when": { "quietFor": "1min" },
                "budget": { "perEnvelope": 1, "perRoleHour": 1 } } ] }
            """);
        Assert.Contains(errors, e => e.Contains("bare role, not an address"));
    }

    // ---- the strict walk ---------------------------------------------------

    [Theory]
    [InlineData("""{ "rules": [] }""", "'version' is required")]
    [InlineData("""{ "version": 2, "rules": [] }""", "'version' must be 1")]
    [InlineData("""{ "version": 1, "default": "allow" }""", "unknown field 'default'")]
    [InlineData("""{ "version": 1, "rules": {} }""", "'rules' must be an array")]
    [InlineData("""{ "version": 1, "rules": [ 4 ] }""", "rules[0] must be a JSON object")]
    [InlineData("""{ "version": 1, "rules": [ { "role": "r", "when": { "quietFor": "1min", "unless": 1 }, "budget": { "perEnvelope": 1, "perRoleHour": 1 } } ] }""", "unknown field 'unless'")]
    [InlineData("""{ "version": 1, "rules": [ { "role": "r", "when": 4, "budget": { "perEnvelope": 1, "perRoleHour": 1 } } ] }""", "when must be a JSON object")]
    public void TheStrictWalk_RefusesWhatItCannotReadExactly(string json, string expected)
        => Assert.Contains(Rejects(json), e => e.Contains(expected));

    /// A non-object document, and a repeated field. `version` twice is
    /// ambiguous and System.Text.Json would silently keep one.
    [Fact]
    public void ADuplicateField_IsRefusedRatherThanSilentlyResolved()
        => Assert.Contains(Rejects("""{ "version": 1, "version": 1 }"""),
                           e => e.Contains("duplicate field 'version'"));

    [Fact]
    public void ANonObjectDocument_IsRefused()
        => Assert.Contains(Rejects("[]"), e => e.Contains("must be a JSON object"));

    /// EVERY violation in one pass (moby-style), so an operator fixes their file
    /// once rather than learning it one error per edit.
    [Fact]
    public void EveryViolation_SurfacesInOnePass()
    {
        var errors = Rejects("""
            { "version": 3, "extra": true, "rules": [
              { "role": "Reviewer", "when": { "quietFor": "soon" },
                "budget": { "perEnvelope": 0, "perRoleHour": 0 } } ] }
            """);
        Assert.Contains(errors, e => e.Contains("'version' must be 1"));
        Assert.Contains(errors, e => e.Contains("unknown field 'extra'"));
        Assert.Contains(errors, e => e.Contains("role"));
        Assert.Contains(errors, e => e.Contains("quietFor"));
        Assert.Contains(errors, e => e.Contains("perEnvelope"));
        Assert.Contains(errors, e => e.Contains("perRoleHour"));
    }

    /// A lone-surrogate escape parses as a syntactically fine document and
    /// throws at `GetString`, not at `Parse` — the guard that keeps
    /// never-throw-on-DATA true, and the exact bug the policy skeptic pass found
    /// in the twin of this parser.
    [Fact]
    public void AnUnreadableString_IsAViolationRatherThanAThrow()
        => Assert.Contains(Rejects("""
            { "version": 1, "rules": [
              { "role": "\ud800", "when": { "quietFor": "1min" },
                "budget": { "perEnvelope": 1, "perRoleHour": 1 } } ] }
            """), e => e.Contains("role"));
}

/// The file tri-state, and the one property this document has that its twin
/// does not: two of the three cases mean the same thing.
public class WatchResolutionTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "captainhook-watch-" + Guid.NewGuid().ToString("N"));

    public WatchResolutionTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* best-effort */ } }

    private string Write(string name, string text)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, text);
        return p;
    }

    private const string Valid = """
        { "version": 1, "rules": [
          { "role": "reviewer", "when": { "quietFor": "10min" },
            "budget": { "perEnvelope": 1, "perRoleHour": 4 } } ] }
        """;

    [Fact]
    public void AValidFile_Loads()
    {
        var r = WatchResolution.Resolve(Write("watch.json", Valid));
        Assert.IsType<WatchResolution.Loaded>(r);
        Assert.Equal("reviewer", Assert.Single(r.Effective()).Role);
    }

    /// THE PROPERTY. Absent and malformed are the same answer — no rules, no
    /// nudges — so there is no fail-open direction in this document for a reader
    /// to have to reason about. Every ambiguous case lands in Malformed, and
    /// Malformed is not a weaker Absent.
    [Theory]
    [InlineData(null)]                                   // absent
    [InlineData("")]                                     // present and empty
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("""{ "version": 1, "rules": [ { "role": "r" } ] }""")]   // schema-invalid
    public void AbsentAndMalformed_AreTheSameAnswer_NoRules(string? content)
    {
        var path = content is null
            ? Path.Combine(_dir, "does-not-exist.json")
            : Write("watch.json", content);

        using var log = new CapturedLog();
        Assert.Empty(WatchResolution.Resolve(path).Effective());
    }

    /// The one thing malformed adds: it says so. An operator whose typo
    /// cancelled the channel would otherwise wait forever for nudges that were
    /// never going to come, with nothing anywhere saying why.
    [Fact]
    public void AMalformedFile_WarnsOnTheTrail_AndAnAbsentOneIsSilent()
    {
        using var log = new CapturedLog();

        Assert.Empty(WatchResolution.Resolve(Write("watch.json", "{ oops")).Effective());
        var warn = Assert.Single(log.Events, e => e.Evt == "watch.malformed");
        Assert.Equal("warn", warn.Lvl);
        Assert.Contains("not valid JSON", (string)warn.Fields.Data!["error"]);

        Assert.Empty(WatchResolution.Resolve(Path.Combine(_dir, "gone.json")).Effective());
        Assert.Single(log.Events, e => e.Evt == "watch.malformed");   // still one: absent is silent
    }

    /// A directory at the path is Malformed rather than Absent. The OUTCOME is
    /// identical — the difference is that one of the two tells the operator
    /// their path points at the wrong kind of thing.
    [Fact]
    public void ADirectoryAtThePath_IsMalformedNotAbsent()
    {
        var path = Path.Combine(_dir, "watch.json");
        Directory.CreateDirectory(path);
        var r = WatchResolution.Resolve(path);
        Assert.Contains("is a directory", Assert.IsType<WatchResolution.Malformed>(r).Error);
    }

    /// The env var is the sandbox seam: a test or the e2e's stub harness points
    /// the whole watcher at its own document without ever touching the
    /// operator's live `~/.captainHook`.
    [Fact]
    public void ResolvePath_PrefersTheExplicitPath_ThenTheEnvVar()
    {
        var previous = Environment.GetEnvironmentVariable("CAPTAINHOOK_WATCH_FILE");
        try
        {
            Environment.SetEnvironmentVariable("CAPTAINHOOK_WATCH_FILE", "/tmp/from-env.json");
            Assert.Equal("/tmp/explicit.json", WatchRules.ResolvePath("/tmp/explicit.json"));
            Assert.Equal("/tmp/from-env.json", WatchRules.ResolvePath());

            Environment.SetEnvironmentVariable("CAPTAINHOOK_WATCH_FILE", null);
            Assert.EndsWith(Path.Combine(".captainHook", "watch.json"), WatchRules.ResolvePath());
        }
        finally { Environment.SetEnvironmentVariable("CAPTAINHOOK_WATCH_FILE", previous); }
    }
}
