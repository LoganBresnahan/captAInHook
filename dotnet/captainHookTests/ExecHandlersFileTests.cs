using System.Text.Json;
using CaptainHook.Core;
using CaptainHook.Wire;
using static CaptainHook.Tests.TestUtil;

namespace CaptainHook.Tests;

// handlers-json-registry (ADR-0010 d4): FILE-level strict like dispatch.json
// (malformed ⇒ zero exec handlers, loudly), ENTRY-level warn-and-skip like
// harness overrides. The sharp edge this suite exists for is the
// silent-grant classification: an entry written in the project's first-class
// kebab spelling must REGISTER AND FIRE on the canonical event — the exact
// bug class the dispatch-policy adversarial verify caught (DispatchPolicy
// canonicalizes for the same reason).

public class ExecHandlersFileTests
{
    private static (IReadOnlyList<ExecEntry> Entries, IReadOnlyList<SkippedEntry> Skipped, bool Ok, IReadOnlyList<string> Errors)
        Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var ok = ExecHandlersFile.TryParse(doc.RootElement, out var entries, out var skipped, out var errors);
        return (entries, skipped, ok, errors);
    }

    private static string Wrap(string entriesJson) =>
        $$"""{"version":1,"handlers":[{{entriesJson}}]}""";

    // ---- entries: valid shapes -------------------------------------------

    [Fact]
    public void FullEntry_EveryFieldParsed()
    {
        var (entries, skipped, ok, _) = Parse(Wrap("""
            {"name":"gate","command":"/usr/bin/python3","args":["gate.py","--strict"],
             "events":["PreToolUse"],"mode":"resident","failMode":"closed",
             "budgetMs":8000,"readinessTimeoutMs":3000,
             "env":{"CORPUS":"/data"},"passEnv":["OPENAI_API_KEY"],"cwd":"/srv/gate"}
            """));

        Assert.True(ok);
        Assert.Empty(skipped);
        var e = Assert.Single(entries);
        Assert.Equal("gate", e.Name);
        Assert.Equal("/usr/bin/python3", e.Command);
        Assert.Equal(["gate.py", "--strict"], e.Args);
        Assert.Equal(["PreToolUse"], e.Events);
        Assert.Equal(ExecMode.Resident, e.Mode);
        Assert.Equal(FailMode.Closed, e.OnFailure);
        Assert.Equal(TimeSpan.FromSeconds(8), e.Budget);
        Assert.Equal(TimeSpan.FromSeconds(3), e.ReadinessTimeout);
        Assert.Equal("/data", e.Env["CORPUS"]);
        Assert.Equal(["OPENAI_API_KEY"], e.PassEnv);
        Assert.Equal("/srv/gate", e.Cwd);
    }

    [Fact]
    public void MinimalEntry_DefaultsApplied()
    {
        var (entries, _, ok, _) = Parse(Wrap(
            """{"name":"min","command":"/bin/true","events":["Stop"]}"""));

        Assert.True(ok);
        var e = Assert.Single(entries);
        Assert.Equal(ExecMode.Oneshot, e.Mode);
        Assert.Equal(FailMode.Open, e.OnFailure);
        Assert.Empty(e.Args);
        Assert.Null(e.Budget);
        Assert.Null(e.Cwd);
        Assert.Empty(e.Env);
        Assert.Empty(e.PassEnv);
    }

    [Fact]
    public void KebabEvents_CanonicalizedAtParse_TheSilentGrantPin()
    {
        var (entries, _, ok, _) = Parse(Wrap(
            """{"name":"k","command":"/bin/true","events":["user-prompt-submit","pre-tool-use"]}"""));

        Assert.True(ok);
        Assert.Equal(["UserPromptSubmit", "PreToolUse"], Assert.Single(entries).Events);
    }

    [Fact]
    public void DuplicateEventAfterCanonicalization_EntrySkipped()
    {
        // "pre-tool-use" and "PreToolUse" in one entry would double-register.
        var (entries, skipped, ok, _) = Parse(Wrap(
            """{"name":"d","command":"/bin/true","events":["pre-tool-use","PreToolUse"]}"""));

        Assert.True(ok);
        Assert.Empty(entries);
        Assert.Contains(Assert.Single(skipped).Violations, v => v.Contains("duplicate event"));
    }

    // ---- entries: warn-and-skip, siblings survive ------------------------

    [Theory]
    [InlineData("""{"command":"/bin/true","events":["Stop"]}""", "'name' is required")]
    [InlineData("""{"name":"x","events":["Stop"]}""", "'command' is required")]
    [InlineData("""{"name":"x","command":"/bin/true"}""", "'events' is required")]
    [InlineData("""{"name":"x","command":"/bin/true","events":[]}""", "'events' must not be empty")]
    [InlineData("""{"name":"x","command":"/bin/true","events":["Stop"],"mode":"warm"}""", "'mode' must be")]
    [InlineData("""{"name":"x","command":"/bin/true","events":["Stop"],"failMode":"shut"}""", "'failMode' must be")]
    [InlineData("""{"name":"x","command":"/bin/true","events":["Stop"],"budgetMs":0}""", "must be positive")]
    [InlineData("""{"name":"x","command":"/bin/true","events":["Stop"],"budgetMs":-5}""", "must be positive")]
    [InlineData("""{"name":"x","command":"/bin/true","events":["Stop"],"budgetMs":9999999999999}""", "must be positive and at most")]
    [InlineData("""{"name":"x","command":"/bin/true","events":["Stop"],"budgetMs":"2s"}""", "integer millisecond")]
    [InlineData("""{"name":"x","command":"/bin/true","events":["Stop"],"surprise":1}""", "unknown field 'surprise'")]
    [InlineData("""{"name":"x","command":"/bin/true","events":["Stop"],"name":"y"}""", "duplicate field 'name'")]
    [InlineData("""{"name":"x","command":"/bin/true","events":["Stop"],"args":[1]}""", "'args' entries must be strings")]
    [InlineData("""{"name":"x","command":"/bin/true","events":["Stop"],"env":{"A":1}}""", "must be a string")]
    [InlineData("""{"name":"","command":"/bin/true","events":["Stop"]}""", "'name' is required")]
    public void InvalidEntry_SkippedWithReason(string entry, string expectedViolation)
    {
        var (entries, skipped, ok, _) = Parse(Wrap(entry));

        Assert.True(ok);   // entry problems never fail the FILE
        Assert.Empty(entries);
        Assert.Contains(Assert.Single(skipped).Violations, v => v.Contains(expectedViolation));
    }

    [Fact]
    public void InvalidEntry_SiblingsStillRegister_EveryViolationCollected()
    {
        var (entries, skipped, ok, _) = Parse($$"""
            {"version":1,"handlers":[
              {"name":"good-one","command":"/bin/true","events":["Stop"]},
              {"command":"/bin/false","events":[],"mode":"warm"},
              {"name":"good-two","command":"/bin/true","events":["SessionStart"]}
            ]}
            """);

        Assert.True(ok);
        Assert.Equal(["good-one", "good-two"], entries.Select(e => e.Name));
        var s = Assert.Single(skipped);
        Assert.True(s.Violations.Count >= 3,
            $"expected name+events+mode violations together, got: {string.Join(" | ", s.Violations)}");
    }

    [Fact]
    public void DuplicateName_LaterEntrySkipped()
    {
        // dispatch.json exclusion keys on names — ambiguity is rejected.
        var (entries, skipped, _, _) = Parse($$"""
            {"version":1,"handlers":[
              {"name":"twin","command":"/bin/true","events":["Stop"]},
              {"name":"twin","command":"/bin/false","events":["Stop"]}
            ]}
            """);

        Assert.Single(entries);
        Assert.Contains(Assert.Single(skipped).Violations, v => v.Contains("duplicate handler name"));
    }

    // ---- file level: strict like dispatch.json ---------------------------

    [Theory]
    [InlineData("""[]""")]
    [InlineData("""{"handlers":[]}""")]                       // missing version
    [InlineData("""{"version":2,"handlers":[]}""")]
    [InlineData("""{"version":1,"handlers":{}}""")]
    [InlineData("""{"version":1}""")]                         // missing handlers
    [InlineData("""{"version":1,"handlers":[],"extra":1}""")]
    public void FileLevelViolation_Malformed(string json)
    {
        var (_, _, ok, errors) = Parse(json);
        Assert.False(ok);
        Assert.NotEmpty(errors);
    }

    // ---- Resolve: the tri-state ------------------------------------------

    [Fact]
    public void Resolve_NoFile_Absent()
    {
        var path = Path.Combine(Path.GetTempPath(), "no-such-" + Guid.NewGuid().ToString("N"), "handlers.json");
        Assert.IsType<ExecHandlersResolution.Absent>(ExecHandlersFile.Resolve(path));
    }

    [Fact]
    public void Resolve_Directory_MalformedNotAbsent()
    {
        var dir = Directory.CreateTempSubdirectory("handlers-as-dir-");
        try
        {
            var m = Assert.IsType<ExecHandlersResolution.Malformed>(ExecHandlersFile.Resolve(dir.FullName));
            Assert.Contains("directory", m.Error);
        }
        finally { dir.Delete(); }
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("""{"version":1,"handlers":[],"x":1}""")]
    public async Task Resolve_BadContent_Malformed(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), "handlers-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(path, content);
        try
        {
            Assert.IsType<ExecHandlersResolution.Malformed>(ExecHandlersFile.Resolve(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Resolve_Valid_Loaded_BomTolerated()
    {
        var path = Path.Combine(Path.GetTempPath(), "handlers-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(path,
            "﻿" + """{"version":1,"handlers":[{"name":"b","command":"/bin/true","events":["Stop"]}]}""");
        try
        {
            var l = Assert.IsType<ExecHandlersResolution.Loaded>(ExecHandlersFile.Resolve(path));
            Assert.Equal("b", Assert.Single(l.Entries).Name);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ResolvePath_ExplicitBeatsEnvBeatsDefault()
    {
        Assert.Equal("/x/y.json", ExecHandlersFile.ResolvePath("/x/y.json"));
        Assert.EndsWith(Path.Combine(".captainHook", "handlers.json"), ExecHandlersFile.ResolvePath());
    }
}

public class ExecRegistrationTests
{
    private static ExecHandlersResolution.Loaded Loaded(params ExecEntry[] entries) =>
        new(entries, []);

    private static ExecEntry Entry(string name, string script, string[] events,
                                   ExecMode mode = ExecMode.Oneshot, TimeSpan? budget = null) =>
        new(name, "/bin/sh", ["-c", script], events, mode, FailMode.Open, budget, null,
            new Dictionary<string, string>(), [], null);

    [Fact]
    public async Task LoadedEntry_RegistersAndFires_EndToEnd()
    {
        var registry = new Registry();
        HookRun.RegisterExecHandlers(registry, Loaded(
            Entry("child", """read l; printf '{"effect":"inject","text":"child says hi"}\n'""",
                ["UserPromptSubmit"])));
        var dispatcher = new Dispatcher(registry, TimeSpan.FromSeconds(5));

        var r = await dispatcher.DispatchAsync(Ev());
        Assert.Equal("child says hi", Assert.IsType<Effect.Inject>(r.Merged).Text);
    }

    [Fact]
    public async Task KebabWrittenFile_FiresOnCanonicalDispatch_TheFullSilentGrantLoop()
    {
        // The end-to-end version of the pin: a file written in kebab (the
        // project's first-class spelling) must actually FIRE when the
        // canonical event dispatches — parse-level canon alone isn't the
        // proof; this loop is.
        var path = Path.Combine(Path.GetTempPath(), "handlers-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(path, """
            {"version":1,"handlers":[
              {"name":"kebab-child","command":"/bin/sh",
               "args":["-c","read l; printf '{\"effect\":\"inject\",\"text\":\"kebab fired\"}\n'"],
               "events":["user-prompt-submit"]}
            ]}
            """);
        try
        {
            var registry = new Registry();
            HookRun.RegisterExecHandlers(registry, ExecHandlersFile.Resolve(path));
            var dispatcher = new Dispatcher(registry, TimeSpan.FromSeconds(5));

            var r = await dispatcher.DispatchAsync(Ev("UserPromptSubmit"));
            Assert.Equal("kebab fired", Assert.IsType<Effect.Inject>(r.Merged).Text);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("userPromptSubmit")]      // camelCase
    [InlineData("USER-PROMPT-SUBMIT")]    // upper-kebab (Canon yields USERPROMPTSUBMIT)
    [InlineData("userpromptsubmit")]      // all-lower
    public async Task AnyCasingVariant_StillFires_NoSilentDeadWorker(string spelling)
    {
        // The adversarial-verify HIGH: these spellings parsed valid,
        // registered a worker, and NEVER fired (case-sensitive runner map).
        // The event space must not split on casing. Deliberately routed
        // through the PARSER — canonicalization lives there, and this is the
        // path a real file takes.
        using var doc = JsonDocument.Parse($$"""
            {"version":1,"handlers":[
              {"name":"cased","command":"/bin/sh",
               "args":["-c","read l; printf '{\"effect\":\"inject\",\"text\":\"fired anyway\"}\n'"],
               "events":["{{spelling}}"]}
            ]}
            """);
        Assert.True(ExecHandlersFile.TryParse(doc.RootElement, out var entries, out _, out _));
        var registry = new Registry();
        HookRun.RegisterExecHandlers(registry, new ExecHandlersResolution.Loaded(entries, []));
        var dispatcher = new Dispatcher(registry, TimeSpan.FromSeconds(5));

        var r = await dispatcher.DispatchAsync(Ev("UserPromptSubmit"));
        Assert.Equal("fired anyway", Assert.IsType<Effect.Inject>(r.Merged).Text);
    }

    [Fact]
    public void InertFields_OnRegisteredOneshot_WarnedLoudly()
    {
        // Loudness symmetry (adversarial-verify MED): resident gets a loud
        // skip; parse-valid-but-unenforced fields must be loud too.
        using var captured = new CapturedLog();
        var registry = new Registry();
        HookRun.RegisterExecHandlers(registry, Loaded(
            new ExecEntry("with-env", "/bin/true", [], ["Stop"], ExecMode.Oneshot, FailMode.Open,
                null, null, new Dictionary<string, string> { ["K"] = "v" }, ["PATH2"], "/tmp")));

        var warn = Assert.Single(captured.Events.ToArray(), e => e.Evt == "handlers.fieldIgnored");
        Assert.Contains("env", warn.Fields.Msg);
        Assert.Contains("cwd", warn.Fields.Msg);
    }

    [Fact]
    public async Task RegistrationOrder_CodedFirst_ThenFileOrder()
    {
        var registry = new Registry()
            .On("UserPromptSubmit", TestHandler.Returning("coded", new Effect.Inject("A")));
        HookRun.RegisterExecHandlers(registry, Loaded(
            Entry("exec-b", """printf '{"effect":"inject","text":"B"}\n'""", ["UserPromptSubmit"]),
            Entry("exec-c", """printf '{"effect":"inject","text":"C"}\n'""", ["UserPromptSubmit"])));
        var dispatcher = new Dispatcher(registry, TimeSpan.FromSeconds(5));

        var r = await dispatcher.DispatchAsync(Ev());
        Assert.Equal("A\nB\nC", Assert.IsType<Effect.Inject>(r.Merged).Text);
    }

    [Fact]
    public async Task BudgetMs_FlowsIntoThePerHandlerWindow()
    {
        // Entry budget 3s under a 200ms dispatcher default: the child needs
        // ~500ms — only the per-handler window (phase 2) lets it answer.
        var registry = new Registry();
        HookRun.RegisterExecHandlers(registry, Loaded(
            Entry("slow-child", """sleep 0.5; printf '{"effect":"inject","text":"slow but allowed"}\n'""",
                ["UserPromptSubmit"], budget: TimeSpan.FromSeconds(3))));
        var dispatcher = new Dispatcher(registry, TimeSpan.FromMilliseconds(200));

        var r = await dispatcher.DispatchAsync(Ev());
        Assert.Equal("slow but allowed", Assert.IsType<Effect.Inject>(r.Merged).Text);
    }

    [Fact]
    public async Task ResidentEntry_ParsesValid_SkippedLoudly_NotRegistered()
    {
        using var captured = new CapturedLog();
        var registry = new Registry();
        HookRun.RegisterExecHandlers(registry, Loaded(
            Entry("warm-one", "true", ["UserPromptSubmit"], mode: ExecMode.Resident)));
        var dispatcher = new Dispatcher(registry, TimeSpan.FromSeconds(2));

        var r = await dispatcher.DispatchAsync(Ev());
        Assert.IsType<Effect.Noop>(r.Merged);   // nothing registered
        var skip = Assert.Single(captured.Events.ToArray(), e => e.Evt == "handlers.entrySkipped");
        Assert.Contains("resident", skip.Fields.Msg);
    }

    [Fact]
    public void MalformedResolution_RegistersNothing_Loudly()
    {
        using var captured = new CapturedLog();
        var registry = new Registry();
        HookRun.RegisterExecHandlers(registry,
            new ExecHandlersResolution.Malformed("boom: not a handlers file"));

        Assert.Empty(new Dispatcher(registry, TimeSpan.FromSeconds(2)).Snapshot());
        var m = Assert.Single(captured.Events.ToArray(), e => e.Evt == "handlers.malformed");
        Assert.Contains("boom", m.Fields.Msg);
    }

    [Fact]
    public void OneshotOnPreToolUse_DrawsTheSlowShapeWarn()
    {
        using var captured = new CapturedLog();
        var registry = new Registry();
        HookRun.RegisterExecHandlers(registry, Loaded(
            Entry("cold-gate", "true", ["PreToolUse"])));

        var warn = Assert.Single(captured.Events.ToArray(), e => e.Evt == "handlers.slowShape");
        Assert.Contains("resident", warn.Fields.Msg);
        Assert.Single(new Dispatcher(registry, TimeSpan.FromSeconds(2)).Snapshot());   // still registers
    }

    [Fact]
    public async Task CollapsedAsync_WithHandlersPath_ChildInjectReachesStdout()
    {
        // The go-live proof for the collapsed wire site: a real handlers.json
        // + a real collapsed run ⇒ the child's inject is IN the hook answer.
        var path = Path.Combine(Path.GetTempPath(), "handlers-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(path, """
            {"version":1,"handlers":[
              {"name":"e2e-child","command":"/bin/sh",
               "args":["-c","read l; printf '{\"effect\":\"inject\",\"text\":\"exec handler live\"}\n'"],
               "events":["UserPromptSubmit"]}
            ]}
            """);
        var harnessDir = Path.Combine(Path.GetTempPath(), "captainhook-no-such-dir-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            var exit = await HookRun.CollapsedAsync(
                new Invocation(Mode.Collapsed, "user-prompt-submit", "claude-code"),
                new StringReader("""{"prompt":"hi"}"""), stdout, stderr,
                harnessDir: harnessDir, handlersPath: path);

            Assert.Equal(0, exit);
            Assert.Contains("exec handler live", stdout.ToString());
            Assert.Contains("captAInHook: UserPromptSubmit seen", stdout.ToString());   // coded echo rides along
        }
        finally { File.Delete(path); }
    }

    private static HookEvent Ev(string type = "UserPromptSubmit") => TestUtil.Ev(type);
}
