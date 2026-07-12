using System.Text.Json;
using CaptainHook.Core;

namespace CaptainHook.Tests;

// child-wire-contract (ADR-0010 decision 2): the fourth user-facing stable
// contract, pinned the way FrameTests/WireJsonlTests pin theirs — golden
// strings for the envelope (field order, omission, single-line), exhaustive
// strictness cases for the answer grammar. The golden suite IS this slice's
// verification (no runtime path reaches the codec until the adapter lands).

public class ExecWireTests
{
    private static JsonElement Payload(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    // ---- envelope: golden bytes -----------------------------------------

    [Fact]
    public void Envelope_GoldenString_FieldOrderPinned()
    {
        var e = new HookEvent("PreToolUse", "sess-1", "/home/oof/proj",
            Payload("""{"tool_name":"Bash","tool_input":{"command":"ls"}}"""));

        var line = ExecWire.Envelope(e, "abc12345");

        Assert.Equal(
            """{"v":1,"dispatchId":"abc12345","event":{"type":"PreToolUse","sessionId":"sess-1","cwd":"/home/oof/proj","payload":{"tool_name":"Bash","tool_input":{"command":"ls"}}}}""",
            line);
    }

    [Fact]
    public void Envelope_AbsentSessionAndCwd_OmittedNotNull()
    {
        var e = new HookEvent("Stop", null, null, Payload("{}"));
        Assert.Equal(
            """{"v":1,"dispatchId":"d1","event":{"type":"Stop","payload":{}}}""",
            ExecWire.Envelope(e, "d1"));
    }

    [Fact]
    public void Envelope_NewlinesAndUnicode_EscapedNeverRaw_SingleLineGuaranteed()
    {
        // The single-line guarantee is what resident mode's line framing
        // stands on: raw \n and \r must never survive into the envelope.
        var e = new HookEvent("UserPromptSubmit", "s", null,
            Payload(JsonSerializer.Serialize(new { prompt = "hi\nthere \"q\" café ✓\r\n" })));

        var line = ExecWire.Envelope(e, "d2");

        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
        Assert.Contains(@"\n", line);              // escaped, not dropped
        Assert.Contains(@"\u00E9", line);          // non-ASCII escaped (é)

        // Round-trip: the child parses the envelope and sees the exact prompt.
        var prompt = JsonDocument.Parse(line).RootElement
            .GetProperty("event").GetProperty("payload").GetProperty("prompt").GetString();
        Assert.Equal("hi\nthere \"q\" café ✓\r\n", prompt);
    }

    [Fact]
    public void Envelope_PrettyPrintedPayload_ReWrittenCompact()
    {
        var e = new HookEvent("Stop", null, null, Payload("{\n  \"a\": 1\n}"));
        Assert.Equal(
            """{"v":1,"dispatchId":"d3","event":{"type":"Stop","payload":{"a":1}}}""",
            ExecWire.Envelope(e, "d3"));
    }

    // ---- answer: the four valid shapes ----------------------------------

    [Fact]
    public void Answer_Inject_Ok()
    {
        var ok = Assert.IsType<ExecAnswer.Ok>(
            ExecWire.ParseAnswer("""{"effect":"inject","text":"context here"}"""));
        var inj = Assert.IsType<Effect.Inject>(ok.Effect);
        Assert.Equal("context here", inj.Text);
    }

    [Fact]
    public void Answer_Replace_Ok()
    {
        var ok = Assert.IsType<ExecAnswer.Ok>(
            ExecWire.ParseAnswer("""{"effect":"replace","text":""}"""));
        Assert.Equal("", Assert.IsType<Effect.Replace>(ok.Effect).Text);
    }

    [Fact]
    public void Answer_Noop_Ok()
    {
        var ok = Assert.IsType<ExecAnswer.Ok>(ExecWire.ParseAnswer("""{"effect":"noop"}"""));
        Assert.IsType<Effect.Noop>(ok.Effect);
    }

    [Theory]
    [InlineData("allow", Verdict.Allow)]
    [InlineData("deny", Verdict.Deny)]
    [InlineData("ask", Verdict.Ask)]
    public void Answer_Decide_AllVerdicts(string wire, Verdict expected)
    {
        var ok = Assert.IsType<ExecAnswer.Ok>(ExecWire.ParseAnswer(
            $$"""{"effect":"decide","verdict":"{{wire}}","reason":"why"}"""));
        var dec = Assert.IsType<Effect.Decide>(ok.Effect);
        Assert.Equal(expected, dec.Verdict);
        Assert.Equal("why", dec.Reason);
    }

    [Fact]
    public void Answer_Decide_ReasonOptional_AbsentIsNull()
    {
        var ok = Assert.IsType<ExecAnswer.Ok>(
            ExecWire.ParseAnswer("""{"effect":"decide","verdict":"deny"}"""));
        Assert.Null(Assert.IsType<Effect.Decide>(ok.Effect).Reason);
    }

    [Fact]
    public void Answer_LeadingBomAndWhitespace_StrippedAndParsed()
    {
        var ok = Assert.IsType<ExecAnswer.Ok>(
            ExecWire.ParseAnswer("\uFEFF  {\"effect\":\"noop\"}  \n"));
        Assert.IsType<Effect.Noop>(ok.Effect);
    }

    // ---- answer: Empty is its own case ----------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   \n\t")]
    [InlineData("\uFEFF")]
    public void Answer_EmptyOrWhitespace_IsEmptyNotError(string stdout) =>
        Assert.IsType<ExecAnswer.Empty>(ExecWire.ParseAnswer(stdout));

    // ---- answer: strictness — never guess --------------------------------

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{"effect":"noop"}{"effect":"noop"}""")]     // second object
    [InlineData("""{"effect":"noop"} trailing""")]            // trailing garbage
    [InlineData("""{"effect":"noop",}""")]                    // trailing comma
    public void Answer_BadBytes_Malformed(string stdout)
    {
        var m = Assert.IsType<ExecAnswer.Malformed>(ExecWire.ParseAnswer(stdout));
        Assert.Contains(m.Violations, v => v.Contains("invalid JSON"));
    }

    [Theory]
    [InlineData("""["effect","noop"]""")]
    [InlineData(""" "noop" """)]
    [InlineData("42")]
    [InlineData("null")]
    public void Answer_NonObjectRoot_Malformed(string stdout)
    {
        var m = Assert.IsType<ExecAnswer.Malformed>(ExecWire.ParseAnswer(stdout));
        Assert.Contains("answer must be a JSON object", m.Violations);
    }

    [Fact]
    public void Answer_BackgroundDoesNotCrossTheWire()
    {
        // ADR-0010 d2: fire-and-forget is reply-then-linger, not a wire kind.
        var m = Assert.IsType<ExecAnswer.Malformed>(
            ExecWire.ParseAnswer("""{"effect":"background"}"""));
        Assert.Contains(m.Violations, v => v.Contains("unknown effect 'background'"));
    }

    [Theory]
    [InlineData("""{"text":"orphan"}""", "'effect' is required")]
    [InlineData("""{"effect":42}""", "'effect' must be a string")]
    [InlineData("""{"effect":"veto"}""", "unknown effect 'veto'")]
    [InlineData("""{"effect":"noop","extra":1}""", "unknown field 'extra'")]
    [InlineData("""{"effect":"inject"}""", "'text' is required")]
    [InlineData("""{"effect":"inject","text":42}""", "'text' is required")]
    [InlineData("""{"effect":"decide"}""", "'verdict' is required")]
    [InlineData("""{"effect":"decide","verdict":"Deny"}""", "must be \"allow\", \"deny\", or \"ask\"")]
    [InlineData("""{"effect":"decide","verdict":"block"}""", "must be \"allow\", \"deny\", or \"ask\"")]
    [InlineData("""{"effect":"decide","verdict":"deny","reason":null}""", "'reason' must be a string")]
    [InlineData("""{"effect":"decide","verdict":"deny","text":"x"}""", "unknown field 'text'")]
    public void Answer_StrictViolations_Malformed(string stdout, string expectedViolation)
    {
        var m = Assert.IsType<ExecAnswer.Malformed>(ExecWire.ParseAnswer(stdout));
        Assert.Contains(m.Violations, v => v.Contains(expectedViolation));
    }

    [Fact]
    public void Answer_DuplicateField_Malformed()
    {
        var m = Assert.IsType<ExecAnswer.Malformed>(
            ExecWire.ParseAnswer("""{"effect":"noop","effect":"noop"}"""));
        Assert.Contains(m.Violations, v => v.Contains("duplicate field 'effect'"));
    }

    [Fact]
    public void Answer_EveryViolationCollected_NotJustTheFirst()
    {
        var m = Assert.IsType<ExecAnswer.Malformed>(ExecWire.ParseAnswer(
            """{"effect":"decide","verdict":"nope","bogus":1}"""));
        Assert.Contains(m.Violations, v => v.Contains("unknown field 'bogus'"));
        Assert.Contains(m.Violations, v => v.Contains("must be \"allow\", \"deny\", or \"ask\""));
        Assert.True(m.Violations.Count >= 2);
    }

    // ---- the loop: envelope out, answer back ------------------------------

    [Fact]
    public void RoundTrip_EnvelopeIsValidChildInput_AnswerIsValidEffect()
    {
        // What a real child does: parse the envelope, answer from the grammar.
        var e = new HookEvent("PreToolUse", "s1", "/p",
            Payload("""{"tool_name":"Bash","tool_input":{"command":"rm -rf /"}}"""));
        var envelope = ExecWire.Envelope(e, "rt1");

        var seen = JsonDocument.Parse(envelope).RootElement;
        Assert.Equal(1, seen.GetProperty("v").GetInt32());
        Assert.Equal("rt1", seen.GetProperty("dispatchId").GetString());
        var tool = seen.GetProperty("event").GetProperty("payload").GetProperty("tool_name").GetString();

        var answer = ExecWire.ParseAnswer(
            $$"""{"effect":"decide","verdict":"deny","reason":"{{tool}} looks destructive"}""");
        var dec = Assert.IsType<Effect.Decide>(Assert.IsType<ExecAnswer.Ok>(answer).Effect);
        Assert.Equal(Verdict.Deny, dec.Verdict);
        Assert.Equal("Bash looks destructive", dec.Reason);
    }
}
