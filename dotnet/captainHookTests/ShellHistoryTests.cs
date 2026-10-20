using System.Text;
using System.Text.Json;
using CaptainHook.Actors;
using CaptainHook.Core;
using CaptainHook.Shim;
using CaptainHook.Wire;
using static CaptainHook.Tests.TestUtil;

namespace CaptainHook.Tests;

// `tool.shell` + `captainHook history` (ADR-0009 d7, 2026-10-19): the
// engine records each shell command an agent ran, recognised by content
// (tool_input.command is a string, never the tool's name: ADR-0019), and
// history streams those lines back with filters. Both wire sites record, and
// both record before the policy gate.
public class ShellHistoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chk-history-" + Guid.NewGuid().ToString("N")[..8]);
    private string Trail => Path.Combine(_dir, "captainHook.jsonl");
    private static readonly string NoOverrides = Path.Combine(Path.GetTempPath(), "captainhook-no-harness-overrides");
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    public ShellHistoryTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private static HookEvent Post(string toolName, string commandJson, string type = "PostToolUse") =>
        new(type, "56544d6f-d8fb-40a5-96ca-d268b27e5bf3", "/work/proj",
            JsonDocument.Parse("{\"tool_name\":\"" + toolName + "\",\"tool_input\":{\"command\":" + commandJson + "}}").RootElement,
            "claude-code");

    // ---- recognition ------------------------------------------------------

    [Theory]
    [InlineData("Bash")]          // Claude Code's shell
    [InlineData("run_shell")]     // moby's
    [InlineData("some_new_tool")] // a host nobody has met: content decides
    public void AShellCommandThatRan_IsRecorded_WhateverTheHostCallsTheTool(string toolName)
    {
        using var log = new CapturedLog();
        using var history = new CapturedShellHistory();
        ShellTrail.Record(Post(toolName, "\"git status\""), "d-1");

        var e = Assert.Single(history.Entries);
        Assert.Equal("git status", e.Command);
        Assert.Equal("/work/proj", e.Cwd);
        Assert.Equal("claude-code", e.Harness);
        Assert.Equal(toolName, e.Tool);
        Assert.Equal("d-1", e.DispatchId);
        Assert.Equal("56544d6f-d8fb-40a5-96ca-d268b27e5bf3", e.SessionId);
        // Its own file since 0.1.4 (ADR-0009 d8): nothing of it in the trail.
        Assert.DoesNotContain(log.Events, x => x.Evt == "tool.shell");
    }

    [Fact]
    public void NotAShellCommand_RecordsNothing()
    {
        using var history = new CapturedShellHistory();
        ShellTrail.Record(Post("Bash", "\"git status\"", type: "PreToolUse"), "d");   // not yet run
        ShellTrail.Record(Post("Edit", "[\"not\",\"a string\"]"), "d");                // command not a string
        ShellTrail.Record(Post("Bash", "\"   \""), "d");                               // blank
        ShellTrail.Record(new HookEvent("PostToolUse", null, null,
            JsonDocument.Parse("""{"tool_name":"Read","tool_input":{"file_path":"/x"}}""").RootElement), "d");
        ShellTrail.Record(new HookEvent("PostToolUse", null, null,
            JsonDocument.Parse("[1,2]").RootElement), "d");                                // not even an object
        Assert.Empty(history.Lines);
    }

    [Fact]
    public void AMultiLineCommand_IsRecordedWhole()
    {
        using var history = new CapturedShellHistory();
        ShellTrail.Record(Post("Bash", "\"cat <<'EOF' > x\\nhello\\nEOF\""), "d");
        Assert.Equal("cat <<'EOF' > x\nhello\nEOF", Assert.Single(history.Entries).Command);
    }

    /// The collapsed site records BEFORE the policy gate: an event-level deny
    /// stops the handlers, not the record of what ran.
    [Fact]
    public async Task TheCollapsedRun_Records_EvenWhenPolicyDeniesTheEvent()
    {
        var policy = Path.Combine(_dir, "dispatch.json");
        File.WriteAllText(policy, """{ "version": 1, "rules": [ { "event": "PostToolUse", "decision": "deny" } ] }""");
        using var history = new CapturedShellHistory();
        var stdout = new StringWriter();

        var exit = await HookRun.CollapsedAsync(new Invocation(Mode.Collapsed, "post-tool-use", "claude-code"),
            new StringReader("""{"session_id":"s-1","cwd":"/w","tool_name":"Bash","tool_input":{"command":"ls -la"}}"""),
            stdout, new StringWriter(), harnessDir: NoOverrides, policyPath: policy);

        Assert.Equal(0, exit);
        Assert.Equal("{}", stdout.ToString());   // denied: the bare Noop
        var e = Assert.Single(history.Entries);
        Assert.Equal("ls -la", e.Command);
        Assert.Equal("/w", e.Cwd);
        Assert.Equal("s-1", e.SessionId);
        Assert.NotNull(e.DispatchId);
    }

    /// The daemon site records too, under the shim's dispatch id.
    [Fact]
    public async Task TheDaemon_Records_UnderTheShimsDispatchId()
    {
        using var dir = new TempRuntimeDir();
        using var stop = new CancellationTokenSource();
        using var history = new CapturedShellHistory();
        var daemon = Task.Run(() => DaemonHost.RunAsync(dir.Paths, NoOverrides, stop.Token));
        try
        {
            await PollUntilAsync(async () => await ShimClient.TryForwardAsync(dir.Paths.SocketPath,
                    new HookRequest("probe000", "session-start", "claude-code", "{}"u8.ToArray())) is ForwardOutcome.Answered,
                TimeSpan.FromSeconds(15), "daemon starts listening");

            var outcome = await ShimClient.TryForwardAsync(dir.Paths.SocketPath, new HookRequest("cafe0042", "post-tool-use",
                "claude-code", Encoding.UTF8.GetBytes("""{"session_id":"s-2","cwd":"/w","tool_name":"run_shell","tool_input":{"command":"make test"}}""")));
            Assert.IsType<ForwardOutcome.Answered>(outcome);

            var e = Assert.Single(history.Entries);
            Assert.Equal("make test", e.Command);
            Assert.Equal("cafe0042", e.DispatchId);
        }
        finally
        {
            stop.Cancel();
            Assert.Equal(0, await daemon.WaitAsync(TimeSpan.FromSeconds(10)));
        }
    }

    // ---- reading the trail back -------------------------------------------

    /// A trail line exactly as the real emitter renders it, so a reader test
    /// can never pass against a shape production does not write.
    private static string Line(string command, string ts = "2026-10-19T10:00:00.000Z", string session = "56544d6f-aaaa",
        string cwd = "/work/proj", string harness = "claude-code") =>
        new LogEvent(
            DateTime.Parse(ts, null, System.Globalization.DateTimeStyles.AdjustToUniversal),
            "info", "tool", "tool.shell",
            new LogFields
            {
                DispatchId = "d", SessionId = session, HookEvent = "PostToolUse",
                Data = new Dictionary<string, object> { ["command"] = command, ["cwd"] = cwd, ["harness"] = harness, ["tool"] = "Bash" },
            }).ToJson();

    private void WriteTrail(params string[] lines) => File.WriteAllLines(Trail, lines);

    private (int Exit, string Out, string Err) History(params string[] args) => HistoryAt(null, args);

    private (int Exit, string Out, string Err) HistoryAt(int? width, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var exit = HistoryVerb.Run(args, o, e, width, Trail, Now);
        return (exit, o.ToString(), e.ToString());
    }

    private static string[] Lines(string s) => s.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public void Read_SkipsOtherEvents_AndATornFinalLine()
    {
        WriteTrail(
            """{"ts":"2026-10-19T09:00:00.000Z","lvl":"info","src":"exec","evt":"exec.spawn","data":{"command":"/payload.sh"}}""",
            Line("git status"),
            Line("git log")[..^5]);   // a writer killed mid-append: names the event, does not parse

        var all = ShellTrail.Read(ShellTrail.Files(Trail)).ToList();

        Assert.Equal("git status", Assert.Single(all).Command);
    }

    [Fact]
    public void Files_AreTheNumberedSegmentsInOrder_ThenTheActiveFile()
    {
        foreach (var n in new[] { "captainHook.10.jsonl", "captainHook.2.jsonl", "captainHook.jsonl", "captainHook.old.jsonl", "other.1.jsonl" })
            File.WriteAllText(Path.Combine(_dir, n), "");

        Assert.Equal(new[] { "captainHook.2.jsonl", "captainHook.10.jsonl", "captainHook.jsonl" },
            ShellTrail.Files(Trail).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public void History_ReadsSegmentsBeforeTheActiveFile()
    {
        File.WriteAllLines(Path.Combine(_dir, "captainHook.1.jsonl"), [Line("first", "2026-10-18T10:00:00.000Z")]);
        WriteTrail(Line("second"));

        var (exit, o, _) = History("--json");

        Assert.Equal(0, exit);
        Assert.Equal(new[] { "first", "second" },
            Lines(o).Select(l => JsonDocument.Parse(l).RootElement.GetProperty("command").GetString()).ToArray());
    }

    [Fact]
    public void Pattern_IsACaseInsensitiveSubstringOfTheWholeCommand()
    {
        WriteTrail(Line("git status"), Line("dotnet test"), Line("echo " + new string('x', 300) + " NEEDLE"));

        var (exit, o, _) = HistoryAt(80, "needle");

        Assert.Equal(0, exit);
        var line = Assert.Single(Lines(o));
        Assert.Equal(80, line.Length);   // the display is cut…
        Assert.EndsWith("…", line);      // …but the match ran on the whole command
    }

    [Fact]
    public void Filters_Session_Cwd_Harness_Since_AllHold()
    {
        WriteTrail(
            Line("a", session: "aaaa1111-x", cwd: "/work/proj", ts: "2026-10-19T11:00:00.000Z"),
            Line("b", session: "bbbb2222-x", cwd: "/work/proj/src", ts: "2026-10-19T11:30:00.000Z"),
            Line("c", session: "bbbb2222-x", cwd: "/work/project", ts: "2026-10-19T11:45:00.000Z", harness: "moby"),
            Line("d", session: "bbbb2222-x", cwd: "/work/proj", ts: "2026-10-17T11:45:00.000Z"));

        string[] Commands(params string[] args) =>
            Lines(History(["--json", .. args]).Out).Select(l => JsonDocument.Parse(l).RootElement.GetProperty("command").GetString()!).ToArray();

        Assert.Equal(new[] { "b", "c", "d" }, Commands("--session", "BBBB"));
        Assert.Equal(new[] { "a", "b", "d" }, Commands("--cwd", "/work/proj"));     // by component: not /work/project
        Assert.Equal(new[] { "a", "b", "d" }, Commands("--cwd", "/work/proj/"));
        Assert.Equal(new[] { "c" }, Commands("--harness", "MOBY"));
        Assert.Equal(new[] { "b", "c" }, Commands("--since", "45m"));
        Assert.Equal(new[] { "a", "b", "c" }, Commands("--since", "2026-10-18T00:00:00Z"));
        Assert.Equal(new[] { "b" }, Commands("--session", "bbbb", "--cwd", "/work/proj", "--since", "1d"));
    }

    [Fact]
    public void Limit_KeepsTheMostRecentMatches_OldestFirst()
    {
        WriteTrail(Enumerable.Range(1, 10).Select(i => Line($"cmd {i}")).ToArray());

        var (_, o, _) = History("--limit", "3", "--json");

        Assert.Equal(new[] { "cmd 8", "cmd 9", "cmd 10" },
            Lines(o).Select(l => JsonDocument.Parse(l).RootElement.GetProperty("command").GetString()).ToArray());
    }

    [Fact]
    public void Default_IsOneLinePerCommand_TimeSessionCwdCommand()
    {
        WriteTrail(Line("cat <<'EOF' > x\nhello\nEOF", session: "56544d6f-d8fb", cwd: Home + "/proj"));

        var line = Assert.Single(Lines(History().Out));

        var local = new DateTimeOffset(2026, 10, 19, 10, 0, 0, TimeSpan.Zero).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        Assert.Equal($"{local}  56544d6f  ~/proj  cat <<'EOF' > x ⏎ hello ⏎ EOF", line);
    }

    [Fact]
    public void Default_NotATerminal_CutsNothing()
    {
        var long_ = "echo " + new string('y', 500);
        WriteTrail(Line(long_));

        Assert.EndsWith(long_, Assert.Single(Lines(History().Out)));
    }

    [Fact]
    public void Full_PrintsMultiLineCommandsAsMultiLine_Aligned()
    {
        WriteTrail(Line("cat <<'EOF' > x\nhello\nEOF", session: "56544d6f-d8fb", cwd: "/w"));

        var lines = History("--full").Out.Split('\n');

        Assert.EndsWith("  56544d6f  /w  cat <<'EOF' > x", lines[0]);
        var indent = lines[0].IndexOf("cat", StringComparison.Ordinal);
        Assert.Equal(new string(' ', indent) + "hello", lines[1]);
        Assert.Equal(new string(' ', indent) + "EOF", lines[2]);
    }

    [Fact]
    public void Json_CarriesEveryField_OneObjectPerLine()
    {
        WriteTrail(Line("git status\ngit diff"));

        var o = JsonDocument.Parse(Assert.Single(Lines(History("--json").Out))).RootElement;

        Assert.Equal("git status\ngit diff", o.GetProperty("command").GetString());
        Assert.Equal("2026-10-19T10:00:00.000Z", o.GetProperty("ts").GetString());
        Assert.Equal("56544d6f-aaaa", o.GetProperty("sessionId").GetString());
        Assert.Equal("/work/proj", o.GetProperty("cwd").GetString());
        Assert.Equal("claude-code", o.GetProperty("harness").GetString());
        Assert.Equal("Bash", o.GetProperty("tool").GetString());
    }

    [Fact]
    public void NoMatch_ExitsOne_Quietly()
    {
        WriteTrail(Line("git status"));
        var (exit, o, e) = History("nothing-like-this");
        Assert.Equal(1, exit);
        Assert.Equal("", o);
        Assert.Equal("", e);
    }

    /// What history can see is what the trail holds: a trail written before
    /// recording began says so, instead of looking like an empty search.
    [Fact]
    public void ATrailWithNoRecordedCommands_SaysWhereOlderOnesLive()
    {
        WriteTrail("""{"ts":"2026-10-19T09:00:00.000Z","lvl":"info","src":"exec","evt":"exec.spawn","data":{"command":"/p.sh"}}""");
        var (exit, _, e) = History();
        Assert.Equal(1, exit);
        Assert.Contains("Recording began in 0.1.3", e);
        Assert.Contains("agent-shell-history", e);
    }

    [Fact]
    public void NoTrail_SaysWhere()
    {
        var (exit, _, e) = History();
        Assert.Equal(1, exit);
        Assert.Contains($"no trail at {Trail}", e);
    }

    [Theory]
    [InlineData("--limit", "0")]
    [InlineData("--limit", "x")]
    [InlineData("--since", "yesterday-ish")]
    [InlineData("--session")]
    [InlineData("--bogus")]
    [InlineData("one", "two")]
    public void BadArguments_AreAUsageError(params string[] args)
    {
        WriteTrail(Line("git status"));
        var (exit, _, e) = History(args);
        Assert.Equal(2, exit);
        Assert.Contains("usage: captainHook history", e);
    }

    [Fact]
    public void Cut_NeverSplitsASurrogatePair()
    {
        Assert.Equal("ab…", HistoryVerb.Cut("ab😀cd", 4));
        Assert.Equal("abc", HistoryVerb.Cut("abc", 3));
        Assert.Equal("abc", HistoryVerb.Cut("abc", null));
    }

    [Fact]
    public void History_IsAnEngineVerb_TheShimRefusesIt()
    {
        Assert.Equal(Mode.History, Invocation.Parse(["history", "git"]).Mode);
        var stderr = new StringWriter();
        var exit = ShimMain.RunAsync(["history"], new MemoryStream(), new MemoryStream(), stderr).GetAwaiter().GetResult();
        Assert.Equal(1, exit);
        Assert.Contains("history", stderr.ToString());
    }

    // ---- its own file, kept forever (ADR-0009 d8, 0.1.4) ---------------------

    private string Active => Path.Combine(_dir, "shell-history.jsonl");
    private string Legacy => Path.Combine(_dir, "shell-history.0.jsonl");

    private string[] Commands(params string[] args) =>
        Lines(History(["--json", .. args]).Out).Select(l => JsonDocument.Parse(l).RootElement.GetProperty("command").GetString()!).ToArray();

    [Fact]
    public void TheHistoryFile_SitsBesideTheTrail()
    {
        Assert.Equal(Active, ShellTrail.HistoryPath(Trail));
        Assert.Equal(Legacy, ShellTrail.LegacyPath(Trail));
    }

    /// Before a daemon has migrated them, the 0.1.3 window's lines are read
    /// straight from the trail, then the history file.
    [Fact]
    public void History_ReadsTheTrailsOldLines_ThenTheHistoryFile()
    {
        WriteTrail(Line("from 0.1.3", "2026-10-19T09:00:00.000Z"));
        File.WriteAllLines(Active, [Line("from 0.1.4")]);
        Assert.Equal(new[] { "from 0.1.3", "from 0.1.4" }, Commands());
    }

    /// Once migrated, the trail is not read at all: no duplicates, and the
    /// old lines survive the trail's rotation.
    [Fact]
    public void AfterMigration_TheTrailIsNotRead_AndNothingIsDoubled()
    {
        WriteTrail(
            """{"ts":"2026-10-19T08:00:00.000Z","lvl":"info","src":"exec","evt":"exec.spawn","data":{"command":"/p.sh"}}""",
            Line("from 0.1.3", "2026-10-19T09:00:00.000Z"),
            Line("torn")[..^5]);
        File.WriteAllLines(Active, [Line("from 0.1.4")]);

        Assert.Equal(1, ShellTrail.MigrateLegacy(Trail));
        Assert.Equal(-1, ShellTrail.MigrateLegacy(Trail));   // once
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Legacy));
        Assert.Single(File.ReadAllLines(Legacy));

        File.WriteAllText(Trail, "");   // the trail rotated away: history does not care
        Assert.Equal(new[] { "from 0.1.3", "from 0.1.4" }, Commands());
    }

    [Fact]
    public void Migration_WithNothingToCopy_LeavesAnEmptyFile_SoLaterStartsSkipTheScan()
    {
        WriteTrail("""{"ts":"2026-10-19T08:00:00.000Z","lvl":"info","src":"exec","evt":"exec.spawn"}""");
        Assert.Equal(0, ShellTrail.MigrateLegacy(Trail));
        Assert.Equal("", File.ReadAllText(Legacy));
        Assert.Empty(Directory.GetFiles(_dir, ".shell-history*"));   // no temp left behind
    }

    /// The daemon runs the migration at start, in the background, when it is
    /// handed the trail (production passes the real one).
    [Fact]
    public async Task TheDaemon_MigratesAtStart()
    {
        WriteTrail(Line("from 0.1.3"));
        using var dir = new TempRuntimeDir();
        using var stop = new CancellationTokenSource();
        var daemon = Task.Run(() => DaemonHost.RunAsync(dir.Paths, NoOverrides, stop.Token, legacyTrail: Trail));
        try
        {
            await PollUntilAsync(() => Task.FromResult(File.Exists(Legacy)), TimeSpan.FromSeconds(15), "legacy file written");
            Assert.Contains("from 0.1.3", File.ReadAllText(Legacy));
        }
        finally
        {
            stop.Cancel();
            Assert.Equal(0, await daemon.WaitAsync(TimeSpan.FromSeconds(15)));
        }
    }

    [Fact]
    public void DashN_IsLimit_AsInHh()
    {
        WriteTrail(Enumerable.Range(1, 5).Select(i => Line($"cmd {i}")).ToArray());
        Assert.Equal(new[] { "cmd 4", "cmd 5" }, Commands("-n", "2"));
        Assert.Equal(new[] { "cmd 4", "cmd 5" }, Commands("cmd", "-n", "2"));
        Assert.Equal(2, History("-n").Exit);
    }
}
