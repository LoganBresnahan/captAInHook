using System.Text.Json;
using CaptainHook.Core;

namespace CaptainHook.Tests;

/// ADR-0006 decision 1 — the strict policy parser. Its failure mode is
/// loud-by-design (a malformed file makes the caller Noop everything, phase 4),
/// so these pin exactly which files parse and which are rejected, and that
/// every violation surfaces in one pass (moby-style, like HarnessSpec.TryParse).
public class DispatchPolicyParseTests
{
    private static DispatchPolicy? Parse(string json, out IReadOnlyList<string> errors)
    {
        using var doc = JsonDocument.Parse(json);
        // The policy holds only strings/enums (Crit copies via GetString), so it
        // safely outlives the JsonDocument — no Clone needed, unlike HarnessSpec.
        return DispatchPolicy.TryParse(doc.RootElement, out errors);
    }

    private static DispatchPolicy ParseValid(string json)
    {
        var p = Parse(json, out var errors);
        Assert.True(p is not null, $"expected valid, got: {string.Join("; ", errors)}");
        return p!;
    }

    [Fact]
    public void AdrExampleFile_ParsesWithEveryCriterion()
    {
        // The exact file from ADR-0006 decision 1 — the contract this parser
        // exists to accept.
        var p = ParseValid("""
            {
              "version": 1,
              "default": "allow",
              "rules": [
                { "event": "SessionStart", "decision": "deny" },
                { "handler": "echo", "project": "/home/oof/some-repo", "decision": "deny" },
                { "session": "abc123", "decision": "deny" }
              ]
            }
            """);

        Assert.Equal(PolicyDecision.Allow, p.Default);
        Assert.Equal(3, p.Rules.Count);

        Assert.Equal("SessionStart", p.Rules[0].Event);
        Assert.Equal(PolicyDecision.Deny, p.Rules[0].Decision);

        // Criteria AND within a rule: rule 1 names both handler and project.
        Assert.Equal("echo", p.Rules[1].Handler);
        Assert.Equal("/home/oof/some-repo", p.Rules[1].Project);
        Assert.Null(p.Rules[1].Event);

        Assert.Equal("abc123", p.Rules[2].Session);
    }

    [Fact]
    public void MissingDefault_IsAllow_AndRulesOptional()
    {
        // The minimal valid file: version alone. No default => allow; no rules
        // => none. A zero-rule allow-all policy is meaningful — item 5's API
        // will write exactly this as the "reset to open" state.
        var p = ParseValid("""{ "version": 1 }""");
        Assert.Equal(PolicyDecision.Allow, p.Default);
        Assert.Empty(p.Rules);
    }

    [Fact]
    public void EmptyRulesArray_IsValid()
    {
        Assert.Empty(ParseValid("""{ "version": 1, "rules": [] }""").Rules);
    }

    [Fact]
    public void DefaultDeny_Parses_ThePauseStory()
    {
        // ADR-0006 decision 7: "pause" is just default:deny — no separate
        // mechanism. Pin that it parses; the behavior pin is
        // default-deny-pause-pin (phase 6).
        Assert.Equal(PolicyDecision.Deny, ParseValid("""{ "version": 1, "default": "deny" }""").Default);
    }

    [Theory]
    [InlineData("""{ "default": "allow" }""", "version")]                // missing version
    [InlineData("""{ "version": 2 }""", "version")]                     // wrong version number
    [InlineData("""{ "version": 1.5 }""", "version")]                   // non-integer version
    [InlineData("""{ "version": "1" }""", "version")]                   // version wrong type
    [InlineData("""{ "version": 1, "surprise": true }""", "surprise")]  // unknown top-level field
    [InlineData("""{ "version": 1, "default": "maybe" }""", "default")] // bad default decision
    [InlineData("""{ "version": 1, "rules": {} }""", "rules")]          // rules not an array
    public void MalformedTopLevel_IsRejected_NamingTheFault(string json, string needle)
    {
        var p = Parse(json, out var errors);
        Assert.Null(p);
        Assert.Contains(errors, e => e.Contains(needle));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("\"a string\"")]
    public void NonObjectRoot_IsRejected(string json)
    {
        Assert.Null(Parse(json, out var errors));
        Assert.Single(errors);   // one clear "must be a JSON object", not a cascade
    }

    [Fact]
    public void UnknownDecisionString_IsRejected_IncludingAsk()
    {
        // The decision set is allow|deny only — smaller than Verdict. "ask"
        // makes no sense at an unwatched door (ADR-0006 decision 1).
        var p = Parse("""{ "version": 1, "rules": [ { "event": "Stop", "decision": "ask" } ] }""", out var errors);
        Assert.Null(p);
        Assert.Contains(errors, e => e.Contains("decision") && e.Contains("ask"));
    }

    [Fact]
    public void MissingDecision_IsRejected()
    {
        var p = Parse("""{ "version": 1, "rules": [ { "event": "Stop" } ] }""", out var errors);
        Assert.Null(p);
        Assert.Contains(errors, e => e.Contains("decision"));
    }

    [Fact]
    public void CriteriaLessRule_IsRejected()
    {
        // This session's decision: a rule with a decision but NO criteria
        // matches everything and is almost always a typo. `default` is the
        // sanctioned way to set the baseline.
        var p = Parse("""{ "version": 1, "rules": [ { "decision": "deny" } ] }""", out var errors);
        Assert.Null(p);
        Assert.Contains(errors, e => e.Contains("at least one"));
    }

    [Fact]
    public void UnknownRuleField_IsRejected()
    {
        // A typo'd 'decison' is caught as an unknown field (and 'decision' is
        // then also missing) — either way, loudly rejected, never guessed.
        var p = Parse("""{ "version": 1, "rules": [ { "event": "Stop", "decison": "deny" } ] }""", out var errors);
        Assert.Null(p);
        Assert.Contains(errors, e => e.Contains("decison"));
    }

    [Theory]
    [InlineData("""{ "version": 1, "rules": [ { "event": "", "decision": "deny" } ] }""")]      // empty
    [InlineData("""{ "version": 1, "rules": [ { "handler": "  ", "decision": "deny" } ] }""")]  // whitespace
    [InlineData("""{ "version": 1, "rules": [ { "session": 7, "decision": "deny" } ] }""")]     // wrong type
    public void BadCriterionValue_IsRejected(string json)
    {
        var p = Parse(json, out var errors);
        Assert.Null(p);
        Assert.Contains(errors, e => e.Contains("non-empty string"));
    }

    [Fact]
    public void RuleNotAnObject_IsRejected()
    {
        var p = Parse("""{ "version": 1, "rules": [ "deny everything" ] }""", out var errors);
        Assert.Null(p);
        Assert.Contains(errors, e => e.Contains("rules[0]"));
    }

    [Fact]
    public void EveryViolationReportedInOnePass()
    {
        // moby-style: a missing version + an unknown top-level field + a rule
        // that is BOTH criteria-less AND has a bad decision all surface
        // together, not one-at-a-time.
        var p = Parse("""
            {
              "mystery": 1,
              "rules": [ { "decision": "maybe" } ]
            }
            """, out var errors);
        Assert.Null(p);
        Assert.Contains(errors, e => e.Contains("version"));      // missing
        Assert.Contains(errors, e => e.Contains("mystery"));      // unknown top-level
        Assert.Contains(errors, e => e.Contains("at least one")); // criteria-less rule
        Assert.Contains(errors, e => e.Contains("maybe"));        // bad decision
        Assert.True(errors.Count >= 4, $"expected >=4 violations, got {errors.Count}");
    }

    [Fact]
    public void ResolvePath_ExplicitOverride_Wins()
    {
        // The injectable-path idiom (mirrors HarnessRegistry.ResolveDir). Only
        // the explicit branch is pinned — the env/default branches touch
        // process-global state and the real ~/.captainHook tree.
        Assert.Equal("/custom/dispatch.json", DispatchPolicy.ResolvePath("/custom/dispatch.json"));
    }
}

/// ADR-0006 decisions 1-2 — the matcher. Two questions from one rule list:
/// handler-LESS rules decide whether the whole dispatch is WORKED (first match
/// wins, else default); handler-NAMED rules decide, per handler, which are
/// EXCLUDED. The one sharp edge is the project path-prefix boundary — its own
/// theory below.
public class DispatchPolicyEvaluateTests
{
    private static PolicyOutcome Eval(string json, string ev, string? cwd = null, string? session = null)
    {
        using var doc = JsonDocument.Parse(json);
        var p = DispatchPolicy.TryParse(doc.RootElement, out var errors);
        Assert.True(p is not null, $"policy invalid: {string.Join("; ", errors)}");
        return p!.Evaluate(ev, cwd, session);
    }

    [Fact]
    public void NoRules_DefaultAllow_WorksEverythingWithNoExclusions()
    {
        var o = Eval("""{ "version": 1 }""", "UserPromptSubmit");
        Assert.True(o.Work);
        Assert.Empty(o.ExcludedHandlers);
    }

    [Fact]
    public void DefaultDeny_NoRules_NoopsEverything()
    {
        // ADR-0006 decision 7: default:deny IS the pause — every dispatch is an
        // event-level deny.
        Assert.False(Eval("""{ "version": 1, "default": "deny" }""", "UserPromptSubmit").Work);
        Assert.False(Eval("""{ "version": 1, "default": "deny" }""", "Stop").Work);
    }

    [Fact]
    public void EventLevelDeny_ShortCircuits_OnlyForThatEvent()
    {
        const string json = """{ "version": 1, "rules": [ { "event": "SessionStart", "decision": "deny" } ] }""";
        Assert.False(Eval(json, "SessionStart").Work);       // denied
        Assert.True(Eval(json, "UserPromptSubmit").Work);    // untouched -> default allow
    }

    [Fact]
    public void EventLevel_FirstMatchWins()
    {
        // Two handler-less rules for the same event: the FIRST decides.
        Assert.False(Eval("""
            { "version": 1, "rules": [
                { "event": "Stop", "decision": "deny" },
                { "event": "Stop", "decision": "allow" } ] }
            """, "Stop").Work);
        Assert.True(Eval("""
            { "version": 1, "rules": [
                { "event": "Stop", "decision": "allow" },
                { "event": "Stop", "decision": "deny" } ] }
            """, "Stop").Work);
    }

    [Fact]
    public void DefaultDeny_WithAllowRule_SelectivelyEnablesOneEvent()
    {
        // "Deny everything except Stop" — a handler-less allow rule overrides
        // the deny baseline for exactly its event.
        const string json = """
            { "version": 1, "default": "deny", "rules": [ { "event": "Stop", "decision": "allow" } ] }
            """;
        Assert.True(Eval(json, "Stop").Work);
        Assert.False(Eval(json, "UserPromptSubmit").Work);
    }

    [Fact]
    public void HandlerDeny_ExcludesThatHandler_DispatchStillWorks()
    {
        var o = Eval("""{ "version": 1, "rules": [ { "handler": "echo", "decision": "deny" } ] }""", "UserPromptSubmit");
        Assert.True(o.Work);
        Assert.Equal(new[] { "echo" }, o.ExcludedHandlers);
    }

    [Fact]
    public void HandlerAllow_ShieldsFromLaterDeny_PerHandlerFirstMatch()
    {
        // rule 0 allows echo on Stop; rule 1 denies echo everywhere. First
        // match per handler wins.
        const string json = """
            { "version": 1, "rules": [
                { "handler": "echo", "event": "Stop", "decision": "allow" },
                { "handler": "echo", "decision": "deny" } ] }
            """;
        Assert.Empty(Eval(json, "Stop").ExcludedHandlers);              // rule 0 shields
        Assert.Equal(new[] { "echo" }, Eval(json, "UserPromptSubmit").ExcludedHandlers);  // rule 1 bites
    }

    [Fact]
    public void MultipleHandlerDenies_AllExcluded()
    {
        var o = Eval("""
            { "version": 1, "rules": [
                { "handler": "a", "decision": "deny" },
                { "handler": "b", "decision": "deny" } ] }
            """, "UserPromptSubmit");
        Assert.True(o.Work);
        Assert.Equal(new[] { "a", "b" }, o.ExcludedHandlers.OrderBy(h => h, StringComparer.Ordinal));
    }

    [Fact]
    public void EventLevelDeny_ShortCircuits_BeforeHandlerQuestion()
    {
        // A handler allow rule is MOOT when a handler-less rule denies the whole
        // dispatch — Work=false, no exclusions computed.
        var o = Eval("""
            { "version": 1, "rules": [
                { "handler": "echo", "decision": "allow" },
                { "event": "Stop", "decision": "deny" } ] }
            """, "Stop");
        Assert.False(o.Work);
        Assert.Empty(o.ExcludedHandlers);
    }

    [Fact]
    public void SessionCriterion_IsExactMatch()
    {
        const string json = """{ "version": 1, "rules": [ { "session": "abc123", "decision": "deny" } ] }""";
        Assert.False(Eval(json, "UserPromptSubmit", session: "abc123").Work);  // exact hit
        Assert.True(Eval(json, "UserPromptSubmit", session: "abc124").Work);   // no partial
        Assert.True(Eval(json, "UserPromptSubmit", session: null).Work);       // no session, no match
    }

    [Fact]
    public void CriteriaWithinARule_AndTogether()
    {
        // event AND project must BOTH hold for the rule to bite.
        const string json = """
            { "version": 1, "rules": [ { "event": "Stop", "project": "/foo", "decision": "deny" } ] }
            """;
        Assert.False(Eval(json, "Stop", cwd: "/foo").Work);                 // both match
        Assert.True(Eval(json, "Stop", cwd: "/bar").Work);                  // project misses
        Assert.True(Eval(json, "UserPromptSubmit", cwd: "/foo").Work);      // event misses
    }

    [Theory]
    // project, cwd, expected Work — false means the project-deny rule matched.
    [InlineData("/home/oof/some-repo", "/home/oof/some-repo", false)]        // cwd == project
    [InlineData("/home/oof/some-repo", "/home/oof/some-repo/sub/x", false)]  // strict subdirectory
    [InlineData("/home/oof/some-repo", "/home/oof/some-repo2", true)]        // sibling — the classic prefix trap
    [InlineData("/home/oof/some-repo", "/home/oof/some-repoX", true)]        // sibling, no separator boundary
    [InlineData("/home/oof/some-repo", "/home/oof", true)]                   // parent, not contained
    [InlineData("/home/oof/some-repo", "/elsewhere", true)]                  // unrelated
    [InlineData("/home/oof/some-repo/", "/home/oof/some-repo", false)]       // trailing sep on project trimmed
    [InlineData("/home/oof/some-repo", "/home/oof/some-repo/", false)]       // trailing sep on cwd trimmed
    public void ProjectPrefix_BoundaryIsSeparatorAware(string project, string cwd, bool expectedWork)
    {
        var json = $$"""{ "version": 1, "rules": [ { "project": "{{project}}", "decision": "deny" } ] }""";
        Assert.Equal(expectedWork, Eval(json, "UserPromptSubmit", cwd: cwd).Work);
    }

    [Fact]
    public void ProjectRule_WithNoCwd_DoesNotMatch()
    {
        // A project-scoped rule cannot bite an event that carries no cwd.
        var json = """{ "version": 1, "rules": [ { "project": "/home/oof/some-repo", "decision": "deny" } ] }""";
        Assert.True(Eval(json, "UserPromptSubmit", cwd: null).Work);
    }
}
