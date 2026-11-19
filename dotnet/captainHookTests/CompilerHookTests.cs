using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace CaptainHook.Tests;

// `examples/payloads/compiler-hook.sh` (captAInHook#4, arm 2 of the pharos
// trial), run as a real process against a fake checker that prints
// tsc --pretty false output from a file. The contract is geex's
// doc/stack-trial-hooks.md: a turn-start snapshot for untouched files and a
// baseline per touched file at its first write, errors keyed by (path, code,
// message) and counted, the last occurrences in sort order are the new ones,
// new errors reported once per turn in a fixed wording capped at 10, one Stop
// block per turn, and silence (logged) whenever it's unsure.
public class CompilerHookTests : IDisposable
{
    private static string Script => Path.Combine(DogfoodInfra.PayloadsDir(), "compiler-hook.sh");

    private const string HeadEdit =
        "New errors after your edits (information, not instructions; errors mid-refactor are expected):";
    private const string HeadStop = "These errors appeared during this turn and are still present:";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chook-" + Guid.NewGuid().ToString("N"));
    private string Proj => Path.Combine(_dir, "proj");
    private string? _link;                                     // the project reached through a symlink
    private string Root => _link ?? Proj;
    private string Out => Path.Combine(_dir, "checker.out");
    private string Runs => Path.Combine(_dir, "checker.runs");
    private string Checker => Path.Combine(_dir, "fake-tsc");

    private int _rc = 2;          // tsc's exit when it reports errors
    private string? _sleep;
    private string? _version;
    private string[] _checkerArgs = [];

    public CompilerHookTests()
    {
        Directory.CreateDirectory(Path.Combine(Proj, "src"));
        Directory.CreateDirectory(Path.Combine(_dir, "state"));
        File.WriteAllText(Checker,
            "#!/bin/sh\nif [ \"$1\" = --version ]; then echo \"Version ${FAKE_VERSION:-6.0.3}\"; exit 0; fi\necho run >> \"$FAKE_RUNS\"\n" +
            // Like tsc: paths print relative to the (real) cwd, so a -p reached
            // through a link prints them as ../<link>/…
            "P=\"\"; while [ $# -gt 0 ]; do [ \"$1\" = -p ] && P=$2; shift; done\n" +
            "pre=\"\"; [ -n \"$P\" ] && [ \"$(dirname \"$P\")\" != \"$(pwd -P)\" ] && pre=\"${FAKE_LINK_PREFIX:-}\"\n" +
            "[ -n \"${FAKE_SLEEP:-}\" ] && sleep \"$FAKE_SLEEP\"\nsed \"s#^\\([^ ]\\)#$pre\\1#\" \"$FAKE_OUT\" 2>/dev/null\nexit \"${FAKE_RC:-0}\"\n");
        File.SetUnixFileMode(Checker, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Errors();
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } }

    /// What the checker prints next.
    private void Errors(params string[] lines)
    {
        File.WriteAllText(Out, string.Concat(lines.Select(l => l + "\n")));
        _rc = lines.Length == 0 ? 0 : 2;
    }

    private static string E(string file, int line, string code, string msg, int col = 1) => $"{file}({line},{col}): error {code}: {msg}";

    private int CheckerRuns() => File.Exists(Runs) ? File.ReadAllLines(Runs).Length : 0;

    private void Touch(string rel) => File.WriteAllText(Path.Combine(Proj, rel), "// exists\n");

    private sealed record Result(JsonElement Answer, string Stderr);

    private async Task<Result> RunAsync(string type, string payload, string session = "s1", int budgetMs = 5000)
    {
        var psi = new ProcessStartInfo("sh")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in new[] { Script, "--root", Root, "--budget-ms", budgetMs.ToString(), "--", Checker }.Concat(_checkerArgs)) psi.ArgumentList.Add(a);
        psi.Environment["FAKE_LINK_PREFIX"] = "../link/";
        psi.Environment["PATH"] = TestPath.Minimal;
        psi.Environment["HOME"] = _dir;
        psi.Environment["CAPTAINHOOK_COMPILER_HOOK_STATE"] = Path.Combine(_dir, "state");
        psi.Environment["FAKE_OUT"] = Out;
        psi.Environment["FAKE_RUNS"] = Runs;
        psi.Environment["FAKE_RC"] = _rc.ToString();
        if (_sleep is not null) psi.Environment["FAKE_SLEEP"] = _sleep;
        if (_version is not null) psi.Environment["FAKE_VERSION"] = _version;
        using var p = Process.Start(psi)!;
        var envelope = "{\"v\":1,\"dispatchId\":\"d-" + Guid.NewGuid().ToString("N") + "\",\"event\":{\"type\":\"" + type +
            "\",\"harness\":\"claude-code\",\"sessionId\":\"" + session + "\",\"cwd\":" + JsonSerializer.Serialize(Root) +
            ",\"payload\":" + payload + "}}";
        await p.StandardInput.WriteLineAsync(envelope);
        p.StandardInput.Close();
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);
        var lines = (await stdout).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);                                   // exactly one answer, nothing else
        using var doc = JsonDocument.Parse(lines[0]);
        Assert.StartsWith("d-", doc.RootElement.GetProperty("dispatchId").GetString());
        return new Result(doc.RootElement.Clone(), await stderr);
    }

    private string Tool(string tool, string rel) =>
        "{\"tool_name\":\"" + tool + "\",\"tool_input\":{\"file_path\":" + JsonSerializer.Serialize(Path.Combine(Root, rel)) + "}}";

    private Task<Result> Prompt(string session = "s1") => RunAsync("UserPromptSubmit", """{"prompt":"go"}""", session);
    private Task<Result> Pre(string rel = "src/a.ts", string tool = "Edit") => RunAsync("PreToolUse", Tool(tool, rel));
    private Task<Result> Post(string rel = "src/a.ts", string tool = "Edit", int budgetMs = 5000) =>
        RunAsync("PostToolUse", Tool(tool, rel), budgetMs: budgetMs);
    private Task<Result> Stop(bool active = false) =>
        RunAsync("Stop", $$"""{"stop_hook_active":{{(active ? "true" : "false")}}}""");

    private static string Effect(Result r) => r.Answer.GetProperty("effect").GetString()!;
    private static string Text(Result r) => r.Answer.GetProperty("text").GetString()!;

    /// The turn starts with these errors on disk; then a write to `rel` begins.
    private async Task BeginTurn(string rel, params string[] baseline)
    {
        Touch(rel);
        Errors(baseline);
        Assert.Equal("noop", Effect(await Prompt()));
        Assert.Equal("noop", Effect(await Pre(rel)));
    }

    private const string Old = "Cannot find name 'old'.";
    private const string NotNumber = "Type 'string' is not assignable to type 'number'.";

    [Fact]
    public async Task ANewError_IsReportedOnce_InTheFixedWording()
    {
        await BeginTurn("src/a.ts", E("src/a.ts", 3, "TS2304", Old));
        Errors(E("src/a.ts", 3, "TS2304", Old), E("src/b.ts", 1, "TS2322", NotNumber, col: 7));

        var first = await Post();
        Assert.Equal("inject", Effect(first));
        Assert.Equal(HeadEdit + "\nsrc/b.ts:1:7 TS2322 " + NotNumber, Text(first));
        Assert.Contains("compiler-hook checked event=PostToolUse errors=2 new=1 fresh=1", first.Stderr);

        Assert.Equal("noop", Effect(await Post(tool: "Write")));   // already reported this turn
    }

    [Fact]
    public async Task AnErrorWhoseLineMoved_IsNotNew()
    {
        await BeginTurn("src/a.ts", E("src/a.ts", 3, "TS2304", Old));
        Errors(E("src/a.ts", 41, "TS2304", Old));
        Assert.Equal("noop", Effect(await Post()));
    }

    [Fact]
    public async Task ASecondIdenticalError_IsNew_AndItIsTheLastOneInSortOrder()
    {
        await BeginTurn("src/a.ts", E("src/a.ts", 9, "TS2304", "Cannot find name 'x'."));
        Errors(E("src/a.ts", 3, "TS2304", "Cannot find name 'x'."), E("src/a.ts", 9, "TS2304", "Cannot find name 'x'."));
        Assert.Equal(HeadEdit + "\nsrc/a.ts:9:1 TS2304 Cannot find name 'x'.", Text(await Post()));
    }

    [Fact]
    public async Task AContinuationLine_JoinsTheMessageWithOneSpace()
    {
        await BeginTurn("src/a.ts");
        Errors(E("src/a.ts", 2, "TS2322", "Type '{ a: string; }' is not assignable to type 'T'."),
               "  Types of property 'a' are incompatible.",
               "    Type 'string' is not assignable to type 'number'.");
        Assert.Equal(HeadEdit + "\nsrc/a.ts:2:1 TS2322 Type '{ a: string; }' is not assignable to type 'T'. " +
                     "Types of property 'a' are incompatible. Type 'string' is not assignable to type 'number'.",
            Text(await Post()));
    }

    [Fact]
    public async Task ErrorsWithNoFile_AreSkipped_AndEveryFileTscReportsIsInScope()
    {
        // tsc reports only the files in its program, so a file it names is in the
        // program, inside the root or not (an imported ../ file, a checked .d.ts).
        await BeginTurn("src/a.ts");
        Errors(E("../lib/x.ts", 1, "TS2304", Old), "error TS5083: Cannot read file 'tsconfig.base.json'.",
               "error TS6053: File 'src/gone.ts' not found.", "error TS2318: Cannot find global type 'Array'.",
               "tsconfig.json(1,40): error TS5069: Option 'emitDeclarationOnly' cannot be specified without option 'declaration'.",
               E("src/a.ts", 1, "TS2339", "Property 'q' does not exist on type 'T'."));
        Assert.Equal(HeadEdit + "\n../lib/x.ts:1:1 TS2304 " + Old + "\nsrc/a.ts:1:1 TS2339 Property 'q' does not exist on type 'T'.", Text(await Post()));
    }

    [Fact]
    public async Task Lines_AreSortedByPathBytewise_ThenLineColumnCodeAndMessage()
    {
        await BeginTurn("src/a.ts");
        Errors(E("src/b.ts", 1, "TS2304", "b"), E("src/a.ts", 2, "TS2304", "z"), E("src/a.ts", 2, "TS2300", "z"),
               E("src/a.ts", 1, "TS2304", "y", col: 9), E("src/a.ts", 1, "TS2304", "x", col: 9), E("src/B.ts", 1, "TS2304", "B"));
        Assert.Equal(new[] { HeadEdit, "src/B.ts:1:1 TS2304 B", "src/a.ts:1:9 TS2304 x", "src/a.ts:1:9 TS2304 y",
                             "src/a.ts:2:1 TS2300 z", "src/a.ts:2:1 TS2304 z", "src/b.ts:1:1 TS2304 b" },
            Text(await Post()).Split('\n'));
    }

    [Fact]
    public async Task PastTen_TheRestIsCounted_AndShowsUpOnTheNextEdit()
    {
        await BeginTurn("src/a.ts");
        Errors(Enumerable.Range(1, 12).Select(i => E("src/a.ts", i, "TS2304", $"Cannot find name 'v{i:00}'.")).ToArray());

        var lines = Text(await Post()).Split('\n');
        Assert.Equal(12, lines.Length);                          // heading + 10 errors + the count
        Assert.Equal("…and 2 more.", lines[^1]);
        Assert.Equal("src/a.ts:1:1 TS2304 Cannot find name 'v01'.", lines[1]);

        Assert.Equal(new[] { HeadEdit, "src/a.ts:11:1 TS2304 Cannot find name 'v11'.", "src/a.ts:12:1 TS2304 Cannot find name 'v12'." },
            Text(await Post()).Split('\n'));
    }

    [Fact]
    public async Task ExactlyTen_HasNoCountLine()
    {
        await BeginTurn("src/a.ts");
        Errors(Enumerable.Range(1, 10).Select(i => E("src/a.ts", i, "TS2304", $"Cannot find name 'v{i:00}'.")).ToArray());
        var lines = Text(await Post()).Split('\n');
        Assert.Equal(11, lines.Length);
        Assert.DoesNotContain(lines, l => l.StartsWith("…and"));
    }

    [Fact]
    public async Task ATouchedFile_IsJudgedAgainstItsErrorsJustBeforeItsFirstWrite()
    {
        await BeginTurn("src/a.ts");
        Errors(E("src/b.ts", 2, "TS2345", "Argument of type 'number' is not assignable to parameter of type 'string'."));
        Assert.Equal("inject", Effect(await Post()));            // a's edit broke b, untouched: against the turn start

        Touch("src/b.ts");
        Assert.Equal("noop", Effect(await Pre("src/b.ts")));     // b's first write: its baseline now holds that error
        Errors(E("src/b.ts", 2, "TS2345", "Argument of type 'number' is not assignable to parameter of type 'string'."),
               E("src/b.ts", 5, "TS2339", "Property 'q' does not exist on type 'T'."));
        Assert.Equal(HeadEdit + "\nsrc/b.ts:5:1 TS2339 Property 'q' does not exist on type 'T'.", Text(await Post("src/b.ts")));
    }

    [Fact]
    public async Task AFileCreatedThisTurn_HasAnEmptyBaseline()
    {
        await BeginTurn("src/a.ts");
        Assert.Equal("noop", Effect(await Pre("src/new.ts", "Write")));   // doesn't exist yet
        Errors(E("src/new.ts", 1, "TS2322", NotNumber), E("src/new.ts", 2, "TS2322", NotNumber));
        Assert.Equal(3, Text(await Post("src/new.ts", "Write")).Split('\n').Length);
    }

    [Theory]
    [InlineData("Read", false)]
    [InlineData("Grep", false)]
    [InlineData("Bash", true)]
    [InlineData("Task", true)]
    [InlineData("Agent", true)]                                   // Claude Code 2.1.328's name for the subagent tool
    public async Task TheLastCheck_IsReusedAsABaseline_UntilAWriterBashOrTaskCallStarts(string between, bool stale)
    {
        await BeginTurn("src/a.ts");
        Errors(E("src/a.ts", 1, "TS2339", "Property 'q' does not exist on type 'T'."));
        await Post();
        var runs = CheckerRuns();

        Touch("src/b.ts");
        await Pre("src/b.ts");                                    // its own bump discounted: reused
        Assert.Equal(runs, CheckerRuns());
        await Post("src/b.ts");
        runs = CheckerRuns();

        await RunAsync("PreToolUse", "{\"tool_name\":\"" + between + "\",\"tool_input\":{}}");
        Touch("src/c.ts");
        await Pre("src/c.ts");
        Assert.Equal(stale ? runs + 1 : runs, CheckerRuns());
    }

    [Fact]
    public async Task ACheckTakenAtAWritersPreToolUse_IsNeverReused()
    {
        await BeginTurn("src/a.ts");
        await RunAsync("PreToolUse", """{"tool_name":"Bash","tool_input":{"command":"true"}}""");
        Touch("src/b.ts");
        await Pre("src/b.ts");                                    // a fresh baseline check, before b's write
        File.WriteAllText(Out, "");
        _rc = 3;
        await Post("src/b.ts");                                   // the check after the write fails
        var runs = CheckerRuns();
        Errors();
        Assert.Equal("noop", Effect(await Stop()));               // so Stop checks afresh
        Assert.Equal(runs + 1, CheckerRuns());
    }

    [Fact]
    public async Task ABackgroundBashCall_MakesEveryCheckFreshUntilTheTurnEnds()
    {
        await BeginTurn("src/a.ts");
        Errors(E("src/a.ts", 1, "TS2339", "Property 'q' does not exist on type 'T'."));
        await Post();
        await RunAsync("PreToolUse", """{"tool_name":"Bash","tool_input":{"command":"npm run watch","run_in_background":true}}""");
        await Post();
        var runs = CheckerRuns();
        await Stop();                                             // nothing started since that check, but still fresh
        Assert.Equal(runs + 1, CheckerRuns());
    }

    [Fact]
    public async Task ABashCall_NeverWaitsOnARunningCheck()
    {
        await BeginTurn("src/a.ts");
        var lockDir = Path.Combine(_dir, "state", "s1", "lock");
        Directory.CreateDirectory(lockDir);                       // a check holds the lock (a live pid)
        File.WriteAllText(Path.Combine(lockDir, "pid"), Environment.ProcessId.ToString());
        var sw = Stopwatch.StartNew();
        var r = await RunAsync("PreToolUse", """{"tool_name":"Bash","tool_input":{"command":"ls"}}""", budgetMs: 10000);
        Assert.Equal("noop", Effect(r));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"took {sw.Elapsed}");
        Directory.Delete(lockDir, recursive: true);
    }

    [Fact]
    public async Task AFilesReportedCounts_ResetWhenItsFirstWriteBaselineIsTaken()
    {
        const string X = "Cannot find name 'x'.";
        await BeginTurn("src/a.ts");
        Errors(E("src/b.ts", 2, "TS2304", X));
        Assert.Equal("inject", Effect(await Post()));            // b untouched: one new against the turn start

        Touch("src/b.ts");
        await Pre("src/b.ts");                                    // b's baseline now holds that one; its counts reset
        Errors(E("src/b.ts", 2, "TS2304", X), E("src/b.ts", 8, "TS2304", X));
        Assert.Equal(HeadEdit + "\nsrc/b.ts:8:1 TS2304 " + X, Text(await Post("src/b.ts")));
    }

    [Fact]
    public async Task Stop_BlocksOnceWithTheNewErrors_ThenLetsTheTurnEnd()
    {
        await BeginTurn("src/a.ts");
        Errors(E("src/b.ts", 4, "TS2322", NotNumber));
        await Post();

        var stop = await Stop();
        Assert.Equal("decide", Effect(stop));
        Assert.Equal("deny", stop.Answer.GetProperty("verdict").GetString());   // the adapter renders it as decision:block
        Assert.Equal(HeadStop + "\nsrc/b.ts:4:1 TS2322 " + NotNumber, stop.Answer.GetProperty("reason").GetString());

        Assert.Equal("noop", Effect(await Stop(active: true)));                  // the loop guard
    }

    [Fact]
    public async Task Stop_WithStopHookActive_EndsTheTurn_EvenWithChangedErrors()
    {
        await BeginTurn("src/a.ts");
        Errors(E("src/b.ts", 4, "TS2322", NotNumber));
        await Post();
        Assert.Equal("decide", Effect(await Stop()));
        Errors(E("src/c.ts", 1, "TS2339", "Property 'q' does not exist on type 'T'."));
        await Post();
        Assert.Equal("noop", Effect(await Stop(active: true)));
    }

    [Fact]
    public async Task Stop_WithStopHookActive_EndsTheTurn_EvenWhenThisHookNeverBlocked()
    {
        await BeginTurn("src/a.ts");                             // another hook blocked first, say
        Errors(E("src/b.ts", 4, "TS2322", NotNumber));
        await Post();
        Assert.Equal("noop", Effect(await Stop(active: true)));
    }

    [Fact]
    public async Task AWrittenFileOutsideTheRoot_IsTrackedAsTouched_ByItsRelativePath()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "shared"));
        File.WriteAllText(Path.Combine(_dir, "shared", "x.ts"), "// exists\n");
        await BeginTurn("src/a.ts");
        Errors(E("../shared/x.ts", 1, "TS2304", "Cannot find name 'y'."));
        Assert.Equal("inject", Effect(await Post()));            // untouched: new against the turn start
        Assert.Equal("noop", Effect(await RunAsync("PreToolUse", Tool("Edit", "../shared/x.ts"))));
        Errors(E("../shared/x.ts", 1, "TS2304", "Cannot find name 'y'."), E("../shared/x.ts", 3, "TS2339", "Property 'q' does not exist on type 'T'."));
        Assert.Equal(HeadEdit + "\n../shared/x.ts:3:1 TS2339 Property 'q' does not exist on type 'T'.", Text(await RunAsync("PostToolUse", Tool("Edit", "../shared/x.ts"))));
    }

    /// The project reached through a symlink, as macOS's /var → /private/var does.
    private void LinkTheRoot()
    {
        _link = Path.Combine(_dir, "link");
        File.CreateSymbolicLink(_link, Proj);
    }

    private void WriteFile(string rel)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(Proj, rel))!);
        File.WriteAllText(Path.Combine(Proj, rel), "// written\n");
    }

    [Fact]
    public async Task ANewFileInANewDirectory_UnderALinkedRoot_IsReported()
    {
        // geex-pharos's case, shared with arm 3's pharos-hook.sh.
        LinkTheRoot();
        await Prompt();
        Assert.Equal("noop", Effect(await Pre("deep/er/new.ts", "Write")));   // neither deep/ nor the file exist
        WriteFile("deep/er/new.ts");
        Errors(E("deep/er/new.ts", 1, "TS2322", NotNumber));
        var post = await Post("deep/er/new.ts", "Write");
        Assert.Equal(HeadEdit + "\ndeep/er/new.ts:1:1 TS2322 " + NotNumber, Text(post));
        Assert.Equal("decide", Effect(await Stop()));
    }

    [Fact]
    public async Task ANewFilesOwnBaseline_UnderALinkedRoot_IsKeyedLikeTscsOutput()
    {
        // Arm 2 judges an untouched file by the turn start, so the case above
        // passes even with a mis-keyed baseline. With the turn start unknown,
        // the file's own (empty) baseline is the only thing that can make it
        // known: keyed by an unresolved path, the file would be skipped.
        LinkTheRoot();
        File.WriteAllText(Out, "");
        _rc = 3;                                                  // the turn start fails
        await Prompt();
        Errors();
        await Pre("deep/er/new.ts", "Write");
        WriteFile("deep/er/new.ts");
        Errors(E("deep/er/new.ts", 1, "TS2322", NotNumber));
        var post = await Post("deep/er/new.ts", "Write");
        Assert.True(post.Answer.GetProperty("effect").GetString() == "inject", post.Stderr);
        Assert.Equal(HeadEdit + "\ndeep/er/new.ts:1:1 TS2322 " + NotNumber, Text(post));
        Assert.DoesNotContain("no-baseline", post.Stderr);
    }

    [Fact]
    public async Task ALinkedTsconfigPath_IsResolved_SoPathsPrintRelativeToIt()
    {
        // verifier-two's finding: `-p <link>/tsconfig.json` made tsc print
        // ../link/src/a.ts, off the line format, and touched files' own
        // baselines never matched tsc's keys.
        LinkTheRoot();
        _checkerArgs = ["-p", Path.Combine(_link!, "tsconfig.json")];
        File.WriteAllText(Path.Combine(Proj, "tsconfig.json"), "{}\n");
        Touch("src/a.ts");
        Errors(E("src/a.ts", 3, "TS2304", Old));
        await Prompt();
        await Pre();
        Errors(E("src/a.ts", 3, "TS2304", Old), E("src/a.ts", 5, "TS2322", NotNumber));
        var post = await Post();
        Assert.True(post.Answer.GetProperty("effect").GetString() == "inject", post.Stderr);
        Assert.Equal(HeadEdit + "\nsrc/a.ts:5:1 TS2322 " + NotNumber, Text(post));
    }

    [Fact]
    public async Task WithoutADaemon_TheFirstWriteRunsTheCheckerOnce()
    {
        Touch("src/a.ts");
        Errors(E("src/a.ts", 3, "TS2304", Old));
        Assert.Equal("noop", Effect(await Pre()));               // no UserPromptSubmit linger ran
        Assert.Equal(1, CheckerRuns());
    }

    [Fact]
    public async Task Stop_WithNoNewErrors_SaysNothing()
    {
        await BeginTurn("src/a.ts", E("src/a.ts", 3, "TS2304", Old));
        await Post();
        Assert.Equal("noop", Effect(await Stop()));
    }

    [Fact]
    public async Task AReadOnlyTurn_StopsOnTheTurnStartCheck_WithoutRunningTheChecker()
    {
        Touch("src/a.ts");
        Errors(E("src/a.ts", 3, "TS2304", Old));
        await Prompt();
        var runs = CheckerRuns();
        await RunAsync("PreToolUse", """{"tool_name":"Read","tool_input":{}}""");
        Assert.Equal("noop", Effect(await Stop()));
        Assert.Equal(runs, CheckerRuns());
    }

    [Fact]
    public async Task ABashOnlyTurn_ThatBreaksTheProject_IsBlockedAtStop()
    {
        Touch("src/a.ts");
        await Prompt();
        await RunAsync("PreToolUse", """{"tool_name":"Bash","tool_input":{"command":"sed -i s/1/x/ src/a.ts"}}""");
        Errors(E("src/a.ts", 1, "TS2322", NotNumber));
        Assert.Equal("decide", Effect(await Stop()));
    }

    [Fact]
    public async Task ASyntaxError_MakesACheckIncomplete_SoItIsNeverABaseline()
    {
        // tsc hides every type error while any file has a syntax error.
        const string Y1 = "Type 'number' is not assignable to type 'string'.";
        Touch("src/x.ts");
        await BeginTurn("src/x.ts", E("src/y.ts", 1, "TS2322", Y1));
        Errors(E("src/x.ts", 1, "TS1005", "')' expected."));      // only the syntax error shows
        var post = await Post("src/x.ts");
        Assert.Equal(HeadEdit + "\nsrc/x.ts:1:1 TS1005 ')' expected.", Text(post));   // still reported
        Assert.Contains("trial.unsure reason=incomplete event=PostToolUse", post.Stderr);

        Touch("src/y.ts");
        var pre = await Pre("src/y.ts");                           // y's baseline would come from that run
        Assert.Contains("trial.unsure reason=incomplete event=baseline", pre.Stderr);
        Errors(E("src/y.ts", 1, "TS2322", Y1), E("src/y.ts", 2, "TS2322", NotNumber));   // x fixed: y's old errors return
        Assert.Equal("noop", Effect(await Post("src/y.ts")));      // y's baseline is unknown, so silence
        Assert.Equal("noop", Effect(await Stop()));
    }

    [Fact]
    public async Task AnIncompleteTurnStart_IsUnknownForUntouchedFiles()
    {
        Touch("src/a.ts");
        Errors(E("src/x.ts", 1, "TS1005", "')' expected."));
        var prompt = await Prompt();
        Assert.Equal("noop", Effect(prompt));
        Errors();
        Assert.Equal("noop", Effect(await Pre()));
        Errors(E("src/y.ts", 1, "TS2322", NotNumber));
        var post = await Post();
        Assert.Equal("noop", Effect(post));
        Assert.Contains("trial.unsure reason=no-baseline event=PostToolUse", post.Stderr);
    }

    [Theory]
    [InlineData("src/x.tsx", "TS17008", "JSX element 'div' has no corresponding closing tag.")]
    [InlineData("src/x.tsx", "TS2657", "JSX expressions must have one parent element.")]
    [InlineData("src/x.js", "TS8010", "Type annotations can only be used in TypeScript files.")]
    public async Task ASyntacticErrorCodedAbove2000_MakesACheckIncomplete(string file, string code, string msg)
    {
        // The verifier's `gatetsx`: tsc's syntactic set reaches well past TS1xxx.
        const string Y1 = "Type 'number' is not assignable to type 'string'.";
        Touch(file);
        await BeginTurn(file, E("src/y.ts", 1, "TS2322", Y1));
        Errors(E(file, 1, code, msg));                              // it alone shows
        var post = await Post(file);
        Assert.Contains($"{file}:1:1 {code} {msg}", Text(post));     // still reported
        Assert.Contains("trial.unsure reason=incomplete event=PostToolUse", post.Stderr);

        Touch("src/y.ts");
        Assert.Contains("trial.unsure reason=incomplete event=baseline", (await Pre("src/y.ts")).Stderr);
        Errors(E("src/y.ts", 1, "TS2322", Y1), E("src/y.ts", 2, "TS2322", NotNumber));   // fixed: y's old errors return
        Assert.Equal("noop", Effect(await Post("src/y.ts")));
        Assert.Equal("noop", Effect(await Stop()));
    }

    [Fact]
    public async Task ATscVersionWithNoSyntacticList_MakesEveryCheckUnsure()
    {
        _version = "9.9.9";
        Touch("src/a.ts");
        Errors(E("src/a.ts", 3, "TS2304", Old));
        await Prompt();
        Errors(E("src/a.ts", 3, "TS2304", Old), E("src/b.ts", 1, "TS2322", NotNumber));
        await Pre();
        var post = await Post();
        Assert.Equal("noop", Effect(post));
        Assert.Contains("trial.unsure reason=failed event=PostToolUse\ncompiler-hook detail=no-syntactic-list-for-tsc-9.9.9", post.Stderr);
    }

    [Fact]
    public async Task AnUnusedLocal_IsAnOrdinaryError_NotAnIncompleteCheck()
    {
        const string Unused = "'tmp' is declared but its value is never read.";
        await BeginTurn("src/a.ts", E("src/b.ts", 3, "TS6133", Unused));   // the turn start is complete
        Errors(E("src/b.ts", 3, "TS6133", Unused), E("src/a.ts", 5, "TS6133", Unused));
        var post = await Post();
        Assert.Equal(HeadEdit + "\nsrc/a.ts:5:1 TS6133 " + Unused, Text(post));
        Assert.DoesNotContain("incomplete", post.Stderr);
    }

    [Fact]
    public async Task ATs1xxxBesideATypeError_ProvesTheTypePassRan_SoTheCheckIsComplete()
    {
        // TS1484 (a type-only import under verbatimModuleSyntax) is a semantic
        // error: a TS2322 beside it shows tsc's type pass ran.
        await BeginTurn("src/a.ts");
        Errors(E("src/a.ts", 1, "TS1484", "'T' is a type and must be imported using a type-only import."),
               E("src/a.ts", 4, "TS2322", NotNumber));
        var post = await Post();
        Assert.DoesNotContain("incomplete", post.Stderr);
        Touch("src/b.ts");
        var pre = await Pre("src/b.ts");                           // reuses it: complete, so kept
        Assert.DoesNotContain("incomplete", pre.Stderr);
    }

    [Fact]
    public async Task AnOptionsError_MakesACheckIncomplete()
    {
        await BeginTurn("src/a.ts");
        Errors("tsconfig.json(1,40): error TS5069: Option 'emitDeclarationOnly' cannot be specified without option 'declaration'.");
        var post = await Post();
        Assert.Equal("noop", Effect(post));                        // nothing in scope to report
        Assert.Contains("trial.unsure reason=incomplete event=PostToolUse", post.Stderr);
    }

    [Fact]
    public async Task ANewTurn_StartsFresh()
    {
        await BeginTurn("src/a.ts");
        Errors(E("src/b.ts", 4, "TS2322", NotNumber));
        Assert.Equal("inject", Effect(await Post()));
        Assert.Equal("decide", Effect(await Stop()));
        Assert.Equal("noop", Effect(await Stop(active: true)));

        Assert.Equal("noop", Effect(await Prompt()));            // the error left behind is now the turn start
        Assert.Equal("noop", Effect(await Pre()));
        Assert.Equal("noop", Effect(await Post()));
    }

    [Fact]
    public async Task WithoutAPromptHook_TheFirstWriteTakesTheTurnStart()
    {
        Touch("src/a.ts");
        Errors(E("src/a.ts", 3, "TS2304", Old));
        Assert.Equal("noop", Effect(await Pre()));
        Errors(E("src/a.ts", 3, "TS2304", Old), E("src/d.ts", 1, "TS2339", "Property 'q' does not exist on type 'T'."));
        Assert.Contains("src/d.ts:1:1 TS2339", Text(await Post()));
    }

    [Fact]
    public async Task AnUnknownTurnStart_SilencesUntouchedFiles_ButNotTheTouchedOne()
    {
        Touch("src/a.ts");
        File.WriteAllText(Out, "");
        _rc = 3;                                                // nonzero, nothing printed
        Assert.Equal("noop", Effect(await Prompt()));

        Errors(E("src/a.ts", 1, "TS2304", Old));
        Assert.Equal("noop", Effect(await Pre()));              // a's own baseline: known
        Errors(E("src/a.ts", 1, "TS2304", Old), E("src/a.ts", 7, "TS2339", "Property 'q' does not exist on type 'T'."), E("src/b.ts", 4, "TS2322", NotNumber));
        var post = await Post();
        Assert.Equal(HeadEdit + "\nsrc/a.ts:7:1 TS2339 Property 'q' does not exist on type 'T'.", Text(post));
        Assert.Contains("trial.unsure reason=no-baseline event=PostToolUse\ncompiler-hook detail=files=src/b.ts reason=no-baseline event=PostToolUse", post.Stderr);
    }

    [Fact]
    public async Task ABaselineThatFails_IsLogged_AndItsFileGetsNoReports()
    {
        await BeginTurn("src/a.ts");
        Touch("src/b.ts");
        await RunAsync("PreToolUse", """{"tool_name":"Bash","tool_input":{"command":"true"}}""");   // forces a fresh run
        File.WriteAllText(Out, "");
        _rc = 3;
        var pre = await Pre("src/b.ts");
        Assert.Contains("trial.unsure reason=failed event=baseline\ncompiler-hook detail=exit=3", pre.Stderr);

        Errors(E("src/b.ts", 4, "TS2322", NotNumber));
        Assert.Equal("noop", Effect(await Post("src/b.ts")));
    }

    [Fact]
    public async Task OutputItCannotRead_IsUnparsed_AndSilent()
    {
        await BeginTurn("src/a.ts");
        File.WriteAllText(Out, "Segmentation fault (core dumped)\n");
        _rc = 139;
        var r = await Post();
        Assert.Equal("noop", Effect(r));
        Assert.Contains("trial.unsure reason=unparsed event=PostToolUse\ncompiler-hook detail=exit=139", r.Stderr);
    }

    [Fact]
    public async Task ACheckPastItsBudget_SaysNothing_AndLogsIt()
    {
        await BeginTurn("src/a.ts");
        Errors(E("src/b.ts", 4, "TS2322", NotNumber));
        _sleep = "20";
        var sw = Stopwatch.StartNew();
        var r = await Post(budgetMs: 2000);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"took {sw.Elapsed}");
        Assert.Equal("noop", Effect(r));
        Assert.Contains("trial.unsure reason=failed event=PostToolUse\ncompiler-hook detail=timeout", r.Stderr);
    }

    [Theory]
    [InlineData("Bash")]
    [InlineData("Read")]
    [InlineData("mcp__pharos__apply_workspace_edit")]
    public async Task OtherTools_AreNotChecked(string tool)
    {
        await BeginTurn("src/a.ts");
        Errors(E("src/b.ts", 4, "TS2322", NotNumber));
        var r = await Post(tool: tool);
        Assert.Equal("noop", Effect(r));
        Assert.DoesNotContain("checked", r.Stderr);
    }

    [Fact]
    public async Task Sessions_KeepSeparateTurns()
    {
        await BeginTurn("src/a.ts");
        Errors(E("src/b.ts", 4, "TS2322", NotNumber));
        Assert.Equal("noop", Effect(await Prompt(session: "s2")));   // s2's turn start already has the error
        Assert.Equal("inject", Effect(await Post()));               // s1 still sees it as new
    }
}
