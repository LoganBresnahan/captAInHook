using System.Text.Json;
using CaptainHook.Core;

namespace CaptainHook.Tests;

/// A throwaway harness-override directory. Each test writes the spec files it
/// needs, points a HarnessRegistry at the directory via its explicit-dir ctor
/// (so the CAPTAINHOOK_HARNESS_DIR env and the user's real ~/.captainHook are
/// never involved), and the whole thing vanishes on Dispose.
internal sealed class TempHarnessDir : IDisposable
{
    public string Path { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "captainhook-harness-tests-" + Guid.NewGuid().ToString("N"));

    public TempHarnessDir() => Directory.CreateDirectory(Path);

    public TempHarnessDir Write(string fileName, string json)
    {
        File.WriteAllText(System.IO.Path.Combine(Path, fileName), json);
        return this;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { /* best-effort cleanup */ }
    }
}

internal static class HarnessTestUtil
{
    /// Parse a spec from a JSON literal, asserting it is valid — the shortcut
    /// for tests whose subject is the gate or the parser, not TryParse itself.
    public static HarnessSpec Spec(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var spec = HarnessSpec.TryParse(doc.RootElement, out var errors);
        Assert.True(spec is not null, $"test spec unexpectedly invalid: {string.Join("; ", errors)}");
        return spec!;
    }

    /// A registry whose override layer is guaranteed empty: point it at a path
    /// that does not exist, so only the embedded defaults load.
    public static HarnessRegistry EmbeddedOnlyRegistry() =>
        new(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "captainhook-no-such-dir-" + Guid.NewGuid().ToString("N")));

    /// A minimal but complete valid spec body, parameterized by name/adapter —
    /// what a user override file looks like.
    public static string MinimalSpecJson(string name, string adapter = "generic-json") =>
        $$"""{ "name": "{{name}}", "response": { "adapter": "{{adapter}}" } }""";
}

public class HarnessRegistryTests
{
    [Fact]
    public void EmbeddedClaudeCodeSpec_LoadsWithExpectedShape()
    {
        var spec = HarnessTestUtil.EmbeddedOnlyRegistry().Get("claude-code");

        // The embedded default IS the live deployment's contract: Claude field
        // names, the byte-compatible adapter, and the per-event capability map.
        Assert.Equal("claude-code", spec.Name);
        Assert.Equal("claude-hook-json", spec.ResponseAdapter);
        Assert.Equal("hook_event_name", spec.Request.EventNameField);
        Assert.Equal("session_id", spec.Request.SessionIdField);
        Assert.Equal("cwd", spec.Request.CwdField);
        Assert.Equal(["inject"], spec.Events["UserPromptSubmit"]);
        Assert.Empty(spec.Events["Stop"]);   // Stop accepts nothing — gate fodder
    }

    [Fact]
    public void UnknownName_Throws_NamingItAndListingKnownHarnesses()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => HarnessTestUtil.EmbeddedOnlyRegistry().Get("nope"));

        // The message must be actionable: say what was asked for AND what exists
        // (this exact text lands on stderr for a typo'd --harness flag).
        Assert.Contains("'nope'", ex.Message);
        Assert.Contains("claude-code", ex.Message);
    }

    [Fact]
    public void SameNameValidOverride_ReplacesEmbeddedDefaultWholesale()
    {
        using var dir = new TempHarnessDir()
            .Write("claude-code.json", HarnessTestUtil.MinimalSpecJson("claude-code", "generic-json"));

        var spec = new HarnessRegistry(dir.Path).Get("claude-code");

        // v1 semantics: replace, not merge — the override's adapter wins and the
        // embedded events map is GONE (the override declared none).
        Assert.Equal("generic-json", spec.ResponseAdapter);
        Assert.Empty(spec.Events);
    }

    [Fact]
    public void NewNameSpec_AddsHarness_AlongsideEmbeddedDefaults()
    {
        using var dir = new TempHarnessDir()
            .Write("synthetic.json", HarnessTestUtil.MinimalSpecJson("synthetic"));

        var registry = new HarnessRegistry(dir.Path);

        Assert.Equal("synthetic", registry.Get("synthetic").Name);
        // Adding never removes: both harnesses are known (ordinal-sorted).
        Assert.Equal(["claude-code", "synthetic"], registry.Known);
    }

    [Fact]
    public void InvalidOverride_WarnsSpecInvalid_AndEmbeddedDefaultSurvives()
    {
        using var captured = new CapturedLog();
        // Two failure shapes in one directory: a spec with violations (bad
        // adapter, no name) and a file that isn't JSON at all.
        using var dir = new TempHarnessDir()
            .Write("a-bad-spec.json", """{ "response": { "adapter": "carrier-pigeon" } }""")
            .Write("b-not-json.json", "this is not json {");

        var spec = new HarnessRegistry(dir.Path).Get("claude-code");   // triggers lazy Load

        // A broken override must never crash the live hook or displace the
        // default — it is warned about and skipped.
        Assert.Equal("claude-hook-json", spec.ResponseAdapter);
        var warns = captured.Events.Where(e => e.Evt == "harness.specInvalid").ToArray();
        Assert.Equal(2, warns.Length);
        Assert.All(warns, w => Assert.Equal("warn", w.Lvl));
        // Each warn names the offending file so the user can go fix it.
        Assert.Contains(warns, w => ((string)w.Fields.Data!["file"]).EndsWith("a-bad-spec.json"));
        Assert.Contains(warns, w => ((string)w.Fields.Data!["file"]).EndsWith("b-not-json.json"));
    }

    [Fact]
    public void TryParse_CollectsOneErrorPerViolation_InsteadOfThrowing()
    {
        // Missing name + unknown adapter + unknown effect kind = three distinct
        // violations, all reported in one pass (moby-style validation).
        using var doc = JsonDocument.Parse(
            """{ "response": { "adapter": "smoke-signals" }, "events": { "Stop": { "effects": ["explode"] } } }""");

        var spec = HarnessSpec.TryParse(doc.RootElement, out var errors);

        Assert.Null(spec);
        Assert.Equal(3, errors.Count);
        Assert.Contains(errors, e => e.Contains("'name'"));
        Assert.Contains(errors, e => e.Contains("'response.adapter'") && e.Contains("smoke-signals"));
        Assert.Contains(errors, e => e.Contains("events.Stop") && e.Contains("explode"));
    }
}

/// GOLDEN STRINGS. These pin the exact bytes each adapter emits — the
/// claude-hook-json strings are what the LIVE settings.json deployment parses,
/// so any diff here is a breaking change to production. Do not "fix" a failing
/// assertion by editing the expectation without checking the host contract.
public class ResponseAdapterGoldenTests
{
    private static string Claude(string eventType, Effect eff) =>
        ResponseAdapters.Get("claude-hook-json").Serialize(TestUtil.Ev(eventType), eff);

    private static string Generic(string eventType, Effect eff) =>
        ResponseAdapters.Get("generic-json").Serialize(TestUtil.Ev(eventType), eff);

    [Fact]
    public void ClaudeHookJson_FiveEffectShapes_ExactBytes()
    {
        Assert.Equal(
            """{"hookSpecificOutput":{"hookEventName":"UserPromptSubmit","additionalContext":"hi there"}}""",
            Claude("UserPromptSubmit", new Effect.Inject("hi there")));

        Assert.Equal(
            """{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"deny","permissionDecisionReason":"nope"}}""",
            Claude("PreToolUse", new Effect.Decide(Verdict.Deny, "nope")));

        // A null reason serializes as an EMPTY STRING, not null/absent.
        Assert.Equal(
            """{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"ask","permissionDecisionReason":""}}""",
            Claude("PreToolUse", new Effect.Decide(Verdict.Ask, null)));

        Assert.Equal(
            """{"hookSpecificOutput":{"hookEventName":"PostToolUse","replaceOutput":"swapped"}}""",
            Claude("PostToolUse", new Effect.Replace("swapped")));

        // Noop is the bare two-character object — the most common live output.
        Assert.Equal("{}", Claude("Stop", new Effect.Noop()));
    }

    [Fact]
    public void GenericJson_FiveEffectShapes_ExactBytes()
    {
        Assert.Equal(
            """{"event":"UserPromptSubmit","effect":{"kind":"inject","text":"hi there"}}""",
            Generic("UserPromptSubmit", new Effect.Inject("hi there")));

        Assert.Equal(
            """{"event":"PreToolUse","effect":{"kind":"decide","verdict":"deny","reason":"nope"}}""",
            Generic("PreToolUse", new Effect.Decide(Verdict.Deny, "nope")));

        Assert.Equal(
            """{"event":"PreToolUse","effect":{"kind":"decide","verdict":"ask","reason":""}}""",
            Generic("PreToolUse", new Effect.Decide(Verdict.Ask, null)));

        Assert.Equal(
            """{"event":"PostToolUse","effect":{"kind":"replace","text":"swapped"}}""",
            Generic("PostToolUse", new Effect.Replace("swapped")));

        // Unlike claude-hook-json, generic-json makes noop EXPLICIT: a generic
        // host shouldn't need "empty object means nothing" folklore.
        Assert.Equal(
            """{"event":"Stop","effect":{"kind":"noop"}}""",
            Generic("Stop", new Effect.Noop()));
    }

    [Fact]
    public void UnknownAdapterName_Throws_ListingKnownAdapters()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ResponseAdapters.Get("morse-code"));
        Assert.Contains("'morse-code'", ex.Message);
        Assert.Contains("claude-hook-json", ex.Message);
        Assert.Contains("generic-json", ex.Message);
    }
}

public class CapabilityGateTests
{
    // The gate always runs against a real spec shape — the embedded claude-code
    // default, whose Stop event declares NO effects and which never declares
    // an event named "SomethingBrandNew".
    private static readonly HarnessSpec ClaudeCode = HarnessTestUtil.EmbeddedOnlyRegistry().Get("claude-code");

    [Fact]
    public void UndeclaredEffectOnDeclaredEvent_DowngradesToNoop_AndWarns()
    {
        using var captured = new CapturedLog();

        var final = Harness.ApplyCapabilityGate(
            ClaudeCode, TestUtil.Ev("Stop"), new Effect.Inject("late thoughts"), dispatchId: "d1234567");

        // Never send a harness an effect kind its spec didn't declare: the
        // inject is swallowed on the wire...
        Assert.IsType<Effect.Noop>(final);
        // ...but LOUDLY — the warn carries harness/event/effect + dispatchId so
        // a digest can explain exactly what was dropped and why.
        var warn = Assert.Single(captured.Events, e => e.Evt == "harness.effectUnsupported");
        Assert.Equal("warn", warn.Lvl);
        Assert.Equal("harness", warn.Src);
        Assert.Equal("d1234567", warn.Fields.DispatchId);
        Assert.Equal("Stop", warn.Fields.HookEvent);
        Assert.Equal("claude-code", warn.Fields.Data!["harness"]);
        Assert.Equal("inject", warn.Fields.Data!["effect"]);
    }

    [Fact]
    public void EventAbsentFromSpec_PassesThroughUngated_WithDebugNotWarn()
    {
        using var captured = new CapturedLog();
        var inject = new Effect.Inject("hello from the future");

        var final = Harness.ApplyCapabilityGate(
            ClaudeCode, TestUtil.Ev("SomethingBrandNew"), inject, dispatchId: "d1234567");

        // Permissive by design: an event the spec never mentions must not
        // silently eat effects — the effect survives untouched...
        Assert.Same(inject, final);
        // ...and the only trace is a debug breadcrumb, never a warn.
        Assert.DoesNotContain(captured.Events, e => e.Lvl == "warn");
        var dbg = Assert.Single(captured.Events, e => e.Evt == "harness.eventUndeclared");
        Assert.Equal("debug", dbg.Lvl);
        Assert.Equal("SomethingBrandNew", dbg.Fields.HookEvent);
    }

    [Fact]
    public void DeclaredEffect_PassesUnchanged_AndNoopAlwaysPasses()
    {
        using var captured = new CapturedLog();

        // Inject on UserPromptSubmit is declared -> identity, no logs.
        var inject = new Effect.Inject("hi");
        Assert.Same(inject, Harness.ApplyCapabilityGate(ClaudeCode, TestUtil.Ev("UserPromptSubmit"), inject));

        // Noop passes even where the event declares effects: [] — it is the
        // downgrade TARGET, so gating it would be circular.
        var noop = new Effect.Noop();
        Assert.Same(noop, Harness.ApplyCapabilityGate(ClaudeCode, TestUtil.Ev("Stop"), noop));

        Assert.Empty(captured.Events);   // the happy path is silent
    }
}

public class RequestParsingTests
{
    [Fact]
    public void ParseEvent_HonorsSpecFieldNames_ForNonClaudePayloadShapes()
    {
        // A synthetic harness whose request JSON uses its OWN field names —
        // exactly the case the request block exists for.
        var spec = HarnessTestUtil.Spec("""
            {
              "name": "synthetic",
              "request": { "eventNameField": "evt", "sessionIdField": "sid", "cwdField": "dir" },
              "response": { "adapter": "generic-json" }
            }
            """);
        using var payload = JsonDocument.Parse("""{"evt":"tool-done","sid":"s9","dir":"/tmp/work"}""");

        var e = Harness.ParseEvent(spec, cliEventName: null, payload.RootElement);

        // The payload's kebab-case event name is canonicalized too — Canon runs
        // on whatever source the name came from, CLI or payload.
        Assert.Equal("ToolDone", e.Type);
        Assert.Equal("s9", e.SessionId);
        Assert.Equal("/tmp/work", e.Cwd);
    }

    [Fact]
    public void ParseEvent_CliKebabName_WinsOverPayload_AndCanonicalizes()
    {
        var spec = HarnessTestUtil.EmbeddedOnlyRegistry().Get("claude-code");
        using var payload = JsonDocument.Parse("""{"hook_event_name":"PostToolUse","session_id":"s1"}""");

        // The CLI arg is authoritative (it's what settings.json wired up) and
        // arrives kebab-case, cavemem style.
        var e = Harness.ParseEvent(spec, "user-prompt-submit", payload.RootElement);
        Assert.Equal("UserPromptSubmit", e.Type);
        Assert.Equal("s1", e.SessionId);

        // With no CLI arg and no payload field at all, the default event is
        // UserPromptSubmit (the one the live deployment fires most).
        using var empty = JsonDocument.Parse("{}");
        Assert.Equal("UserPromptSubmit", Harness.ParseEvent(spec, null, empty.RootElement).Type);
    }
}

public class HarnessEndToEndTests
{
    /// The whole in-process pipeline Program.cs runs — registry -> parse ->
    /// dispatch -> gate -> serialize — without spawning the process. This is
    /// the closest the suite gets to the live hook invocation.
    [Fact]
    public async Task ClaudeCodeDefault_InjectOnUserPromptSubmit_ProducesExactWireBytes()
    {
        var spec = HarnessTestUtil.EmbeddedOnlyRegistry().Get("claude-code");
        using var payload = JsonDocument.Parse("""{"hook_event_name":"UserPromptSubmit","session_id":"s1","cwd":"/home/x"}""");
        var evt = Harness.ParseEvent(spec, "user-prompt-submit", payload.RootElement);

        var registry = new Registry().On("UserPromptSubmit",
            TestHandler.Returning("greeter", new Effect.Inject("ahoy")));
        var result = await new Dispatcher(registry, TimeSpan.FromSeconds(2)).DispatchAsync(evt, "e2e00001");

        var final = Harness.ApplyCapabilityGate(spec, evt, result.Merged, "e2e00001");
        var wire = ResponseAdapters.Get(spec.ResponseAdapter).Serialize(evt, final);

        // Byte-for-byte what the live Claude Code deployment would read.
        Assert.Equal(
            """{"hookSpecificOutput":{"hookEventName":"UserPromptSubmit","additionalContext":"ahoy"}}""",
            wire);
    }
}
