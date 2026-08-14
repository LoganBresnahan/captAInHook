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

        // Stop takes `decide` and ONLY decide (ADR-0016 d5's reconcile seam):
        // the block is the sole loop verb the host offers at turn end, and its
        // absence of `inject` is what makes the digest's block non-escalating
        // there rather than a second choice. SessionEnd stays the gate fodder.
        Assert.Equal(["decide"], spec.Events["Stop"]);
        Assert.Empty(spec.Events["SessionEnd"]);
    }

    [Fact]
    public void EmbeddedClaudeCodeSpec_InstallTemplate_NamesTheShim()
    {
        // ADR-0011 N4: the install template is load-bearing the moment the GUI
        // renders it as the wiring hint users hand-paste into their live
        // settings.json. It must name the deployed hook command — the native
        // shim (item 12) — never the ancient `{dotnet} {captainHookDll}` form
        // this pin was added to bury.
        var spec = HarnessTestUtil.EmbeddedOnlyRegistry().Get("claude-code");
        var entry = spec.Install.GetProperty("entry");
        Assert.Equal("command", entry.GetProperty("type").GetString());
        Assert.Equal("{captainShim} hook {event-kebab}", entry.GetProperty("command").GetString());
        Assert.Equal("~/.claude/settings.json", spec.Install.GetProperty("configFile").GetString());
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

    /// A spec's event KEYS canonicalize like everything else that names an
    /// event. The direction of failure is what makes this matter: dispatch
    /// canonicalizes its side, so a spec key that does not would declare an
    /// event no dispatch can name — the lookup misses into the PERMISSIVE
    /// undeclared path and a deliberately restrictive declaration flips open,
    /// the one way a capability gate must never fail. (Found by the slice's
    /// skeptic pass, as fallout from Canon learning single words.)
    [Fact]
    public void SpecEventKeys_Canonicalize_SoARestrictiveDeclarationStillBinds()
    {
        using var doc = JsonDocument.Parse("""
            { "name": "quiet", "response": { "adapter": "generic-json" },
              "events": { "stop": { "effects": [] }, "user-prompt-submit": { "effects": ["inject"] } } }
            """);

        var spec = HarnessSpec.TryParse(doc.RootElement, out var errors);
        Assert.Empty(errors);
        Assert.Equal(["Stop", "UserPromptSubmit"], spec!.Events.Keys.Order());

        // ...and it BINDS: the silencer the user wrote actually gates.
        Assert.IsType<Effect.Noop>(Harness.ApplyCapabilityGate(
            spec, TestUtil.Ev("Stop"), new Effect.Decide(Verdict.Deny, "blocked")));
    }

    /// Two spellings of one event is a contradiction, not a merge: the spec
    /// says two different things about the same seam, and last-write-wins
    /// would silently pick one.
    [Fact]
    public void SpecDeclaringOneEventTwice_IsMalformed()
    {
        using var doc = JsonDocument.Parse("""
            { "name": "twofaced", "response": { "adapter": "generic-json" },
              "events": { "Stop": { "effects": ["decide"] }, "stop": { "effects": [] } } }
            """);

        Assert.Null(HarnessSpec.TryParse(doc.RootElement, out var errors));
        Assert.Contains(errors, e => e.Contains("duplicate event declaration") && e.Contains("Stop"));
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

    /// The turn-end exception (ADR-0016 decision 5; roadmap item 20, phase 5).
    /// Stop has NO member in the host's `hookSpecificOutput` union, so a decide
    /// there speaks the TOP-LEVEL `decision`/`reason` pair instead. Getting it
    /// wrong neither throws nor warns — the union parse just fails and the
    /// block evaporates — which is why this is pinned as exact bytes and why
    /// the shape was read off the shipped host's own schemas rather than the
    /// published docs (doc/platform.md § The Stop block shape).
    [Fact]
    public void ClaudeHookJson_StopDecide_IsTheTopLevelBlock()
    {
        Assert.Equal(
            """{"decision":"block","reason":"you have unopened mail"}""",
            Claude("Stop", new Effect.Decide(Verdict.Deny, "you have unopened mail")));

        // The envelope must be ABSENT, not merely different: it is the
        // presence of `hookSpecificOutput` with an unmatched `hookEventName`
        // that fails the parse and swallows the block.
        var block = Claude("Stop", new Effect.Decide(Verdict.Deny, "x"));
        Assert.DoesNotContain("hookSpecificOutput", block);
        Assert.DoesNotContain("permissionDecision", block);

        // A null reason keeps the empty-string convention the nested shape has.
        Assert.Equal(
            """{"decision":"approve","reason":""}""",
            Claude("Stop", new Effect.Decide(Verdict.Allow, null)));

        // `ask` has no word in the top-level vocabulary — `approve|block` is
        // the whole of it, and a third value fails the host's schema parse,
        // discarding the decision — so it degrades to noop rather than
        // shipping something unparseable.
        Assert.Equal("{}", Claude("Stop", new Effect.Decide(Verdict.Ask, "well?")));

        // SubagentStop is the same contract, declared decide-only like Stop.
        Assert.Equal(
            """{"decision":"block","reason":"r"}""",
            Claude("SubagentStop", new Effect.Decide(Verdict.Deny, "r")));
    }

    /// The branch is EVENT-shaped, not decide-shaped: every other event keeps
    /// the nested `permissionDecision` it has always emitted. This is the
    /// guard against a later "simplification" that hoists the top-level shape
    /// everywhere and silently breaks the tool gate.
    [Fact]
    public void ClaudeHookJson_DecideElsewhere_StaysNested()
    {
        foreach (var ev in new[] { "PreToolUse", "UserPromptSubmit" })
        {
            var json = Claude(ev, new Effect.Decide(Verdict.Deny, "nope"));
            Assert.Equal(
                "{\"hookSpecificOutput\":{\"hookEventName\":\"" + ev
                    + "\",\"permissionDecision\":\"deny\",\"permissionDecisionReason\":\"nope\"}}",
                json);
            Assert.DoesNotContain("\"decision\"", json);
        }
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
    // default, whose Stop/SubagentStop events declare decide ONLY and which
    // never declares an event named "SomethingBrandNew".
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

    /// The same rule one layer down, at a granularity the gate cannot see: the
    /// gate reasons about effect KINDS and `decide` is genuinely declared on
    /// Stop, so only the adapter knows that the top-level vocabulary there is
    /// `approve|block` and has no word for `ask`. A third word fails the
    /// host's schema parse and the whole decision is discarded — but it is
    /// still mail (or a gate) that went nowhere, so it is loud.
    [Fact]
    public void UnrepresentableVerdictOnStop_DowngradesToNoop_AndWarns()
    {
        using var captured = new CapturedLog();

        var json = ResponseAdapters.Get("claude-hook-json")
            .Serialize(TestUtil.Ev("Stop"), new Effect.Decide(Verdict.Ask, "well?"));

        Assert.Equal("{}", json);
        var warn = Assert.Single(captured.Events, e => e.Evt == "harness.verdictUnsupported");
        Assert.Equal("warn", warn.Lvl);
        Assert.Equal("harness", warn.Src);
        Assert.Equal("Stop", warn.Fields.HookEvent);
        Assert.Equal("claude-hook-json", warn.Fields.Data!["adapter"]);
        Assert.Equal("ask", warn.Fields.Data!["verdict"]);

        // The representable verdicts stay silent — a warn per block would be
        // noise on the seam's happy path.
        Assert.Single(captured.Events, e => e.Evt == "harness.verdictUnsupported");
        ResponseAdapters.Get("claude-hook-json")
            .Serialize(TestUtil.Ev("Stop"), new Effect.Decide(Verdict.Deny, "mail"));
        Assert.Single(captured.Events, e => e.Evt == "harness.verdictUnsupported");
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

    /// SubagentStop is declared decide-only precisely so this flattens: were
    /// the event undeclared, an Inject would pass the permissive gate and the
    /// adapter would ship a nested `hookSpecificOutput` the host's union has
    /// no member for — the silent turn-end loss, one event over from Stop.
    [Fact]
    public void InjectOnSubagentStop_FlattensToNoop()
    {
        using var captured = new CapturedLog();

        var final = Harness.ApplyCapabilityGate(
            ClaudeCode, TestUtil.Ev("SubagentStop"), new Effect.Inject("mail"), dispatchId: "d1234567");

        Assert.IsType<Effect.Noop>(final);
        var warn = Assert.Single(captured.Events, e => e.Evt == "harness.effectUnsupported");
        Assert.Equal("SubagentStop", warn.Fields.HookEvent);
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

        // A SINGLE-WORD event is a one-segment kebab and canonicalizes the
        // same way. It used to pass through untouched, so `hook stop` — what
        // the install template's {event-kebab} writes into settings.json —
        // produced the event `stop`, which matches no spec declaration: every
        // capability lookup missed into the permissive undeclared path, and
        // the digest saw no verbs to deliver with. Silent while nothing was
        // registered at turn end; the whole seam the moment something is.
        Assert.Equal("Stop", Harness.ParseEvent(spec, "stop", empty.RootElement).Type);
        Assert.Equal("Stop", Harness.Canon("stop"));
        Assert.Equal("Stop", Harness.Canon("Stop"));       // idempotent
        Assert.Equal("SessionEnd", Harness.Canon("session-end"));
        Assert.Equal("", Harness.Canon(""));
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
