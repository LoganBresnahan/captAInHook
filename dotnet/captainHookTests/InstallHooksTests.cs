using System.Text.Json.Nodes;
using CaptainHook.Core;
using CaptainHook.Install;
using CaptainHook.Wire;
using Xunit;

namespace CaptainHook.Tests;

// install-hooks (ADR-0020 d4): consent-first wiring of the harness config.
// Every test drives a settings file in a temp dir — never ~/.claude.
public class InstallHooksTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "chk-installhooks-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _settings;
    private readonly string _shim;
    private static readonly HarnessRegistry Registry =
        new(Path.Combine(Path.GetTempPath(), "chk-installhooks-no-overrides"));
    private static readonly DateTime Fixed = new(2026, 10, 19, 15, 30, 0);

    public InstallHooksTests()
    {
        Directory.CreateDirectory(_dir);
        _settings = Path.Combine(_dir, "settings.json");
        _shim = Path.Combine(_dir, "bin", "captainShim");
        Directory.CreateDirectory(Path.GetDirectoryName(_shim)!);
        File.WriteAllText(_shim, "#!/bin/sh\n");
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ } }

    private (int Exit, string Out, string Err) Run(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var exit = InstallHooksVerb.Run([.. args, "--settings", _settings, "--shim", _shim], o, e,
                                        Registry, now: () => Fixed);
        return (exit, o.ToString(), e.ToString());
    }

    /// Run with HOME = the temp dir, so the shim (`<dir>/bin/captainShim`)
    /// lives under it, as `~/.local/bin/captainShim` does for real.
    private (int Exit, string Out, string Err) RunHome(string home, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var exit = InstallHooksVerb.Run([.. args, "--settings", _settings, "--shim", _shim], o, e,
                                        Registry, now: () => Fixed, home: home);
        return (exit, o.ToString(), e.ToString());
    }

    private static string Hooks(params (string Event, string Command)[] entries) =>
        new JsonObject
        {
            ["hooks"] = new JsonObject(entries.Select(x => new KeyValuePair<string, JsonNode?>(x.Event,
                new JsonArray(new JsonObject { ["hooks"] = new JsonArray(new JsonObject
                {
                    ["type"] = "command", ["command"] = x.Command,
                }) })))),
        }.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true, IndentSize = 2, NewLine = "\n", Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n";

    private string Command(string ev) =>
        JsonNode.Parse(File.ReadAllText(_settings))!["hooks"]![ev]![0]!["hooks"]![0]!["command"]!.GetValue<string>();

    // ---- home-relative spellings (ADR-0020 d4, 2026-10-19 amendment) -------

    /// A settings.json shared by two machines names the shim through the
    /// shell's home: that IS the shim, so nothing is rewritten.
    [Theory]
    [InlineData("\"$HOME/bin/captainShim\" hook user-prompt-submit")]
    [InlineData("$HOME/bin/captainShim hook user-prompt-submit")]
    [InlineData("${HOME}/bin/captainShim hook user-prompt-submit")]
    [InlineData("\"${HOME}/bin/captainShim\" hook user-prompt-submit")]
    [InlineData("~/bin/captainShim hook user-prompt-submit")]
    public void AHomeRelativeShim_IsUpToDate(string command)
    {
        var file = Hooks(("UserPromptSubmit", command));
        File.WriteAllText(_settings, file);

        var (exit, stdout, _) = RunHome(_dir, "--apply");

        Assert.Equal(0, exit);
        Assert.Contains("up to date", stdout);
        Assert.Equal(file, File.ReadAllText(_settings));
        Assert.Empty(Backups());
    }

    /// The other machine: same file, a different home, its own shim under it.
    [Fact]
    public void TheSharedFile_IsUpToDate_OnAMachineWithAnotherHome()
    {
        var file = Hooks(("UserPromptSubmit", "\"$HOME/bin/captainShim\" hook user-prompt-submit"));
        File.WriteAllText(_settings, file);
        var (_, stdout, _) = RunHome(_dir + "/", "--apply");   // a home with a trailing slash is the same home
        Assert.Contains("up to date", stdout);
        Assert.Equal(file, File.ReadAllText(_settings));
    }

    /// Quoting decides what a shell expands: '$HOME/…' and "~/…" are literal
    /// paths, not the shim, so they are repointed (to the absolute path, since
    /// the file has no home-relative spelling to keep).
    [Theory]
    [InlineData("'$HOME/bin/captainShim' hook user-prompt-submit")]
    [InlineData("\"~/bin/captainShim\" hook user-prompt-submit")]
    public void AQuotedSpellingTheShellWouldNotExpand_IsRepointed(string command)
    {
        File.WriteAllText(_settings, Hooks(("UserPromptSubmit", command)));
        RunHome(_dir, "--apply");
        Assert.Equal($"{_shim} hook user-prompt-submit", Command("UserPromptSubmit"));
    }

    /// Repointing an entry keeps the file's spelling: an upgrade on either
    /// machine never bakes one home into the shared file.
    [Theory]
    [InlineData("\"$HOME/.captainHook/bin/captainShim\" hook user-prompt-submit", "\"$HOME/bin/captainShim\" hook user-prompt-submit")]
    [InlineData("${HOME}/.captainHook/bin/captainShim hook user-prompt-submit", "${HOME}/bin/captainShim hook user-prompt-submit")]
    [InlineData("~/.captainHook/bin/captainShim hook user-prompt-submit", "~/bin/captainShim hook user-prompt-submit")]
    public void ARepointedEntry_KeepsTheFilesSpelling(string before, string after)
    {
        File.WriteAllText(_settings, Hooks(("UserPromptSubmit", before)));

        var (exit, stdout, _) = RunHome(_dir, "--apply");

        Assert.Equal(0, exit);
        Assert.Contains("home-relative", stdout);
        Assert.Equal(after, Command("UserPromptSubmit"));
        Assert.DoesNotContain(_dir, File.ReadAllText(_settings));
    }

    [Fact]
    public void ANewEntry_InAHomeRelativeFile_IsWrittenTheSameWay()
    {
        File.WriteAllText(_settings, Hooks(("UserPromptSubmit", "~/bin/captainShim hook user-prompt-submit")));

        RunHome(_dir, "--apply", "--events", "UserPromptSubmit,PreToolUse");

        Assert.Equal("~/bin/captainShim hook pre-tool-use", Command("PreToolUse"));
        Assert.Equal("~/bin/captainShim hook user-prompt-submit", Command("UserPromptSubmit"));
    }

    /// A shim that is not under this home cannot be spelled through it: the
    /// absolute path, as before.
    [Fact]
    public void AShimOutsideTheHome_IsWrittenAbsolute()
    {
        File.WriteAllText(_settings, Hooks(("UserPromptSubmit", "\"$HOME/old/captainShim\" hook user-prompt-submit")));
        RunHome("/nonexistent-home", "--apply");
        Assert.Equal($"{_shim} hook user-prompt-submit", Command("UserPromptSubmit"));
    }

    /// A file with no home-relative entry keeps getting absolute paths.
    [Fact]
    public void AnAbsoluteFile_StaysAbsolute()
    {
        File.WriteAllText(_settings, Hooks(("UserPromptSubmit", "/old/captainShim hook user-prompt-submit")));
        RunHome(_dir, "--apply");
        Assert.Equal($"{_shim} hook user-prompt-submit", Command("UserPromptSubmit"));
    }

    [Theory]
    [InlineData("a b", "\"$HOME/a b/captainShim\"")]          // unsafe chars: double-quoted
    [InlineData("a$b", "\"$HOME/a\\$b/captainShim\"")]        // $ escaped inside the quotes
    public void Program_QuotesAnUnsafePath_AndATildeFallsToHome(string sub, string expected)
    {
        var home = "/h";
        var shim = $"/h/{sub}/captainShim";
        Assert.Equal(expected, ShimSpelling.Program(shim, new ShimSpelling("~", null), home));
        Assert.Equal(expected, ShimSpelling.Program(shim, new ShimSpelling("$HOME", '"'), home));
    }

    private string[] Backups() => Directory.GetFiles(_dir, "settings.json.bak-captainhook-*");

    // The maintainer's real shape: another tool's hooks interleaved with ours,
    // ours pointing at the legacy ~/.captainHook/bin deploy, unrelated keys.
    private const string Legacy = """
        {
          "permissions": {
            "allow": [
              "Bash(git status)"
            ]
          },
          "hooks": {
            "SessionStart": [
              {
                "hooks": [
                  {
                    "type": "command",
                    "command": "/home/u/.captainHook/bin/captainShim hook session-start"
                  }
                ]
              }
            ],
            "PostToolUse": [
              {
                "hooks": [
                  {
                    "type": "command",
                    "command": "node /opt/cavemem/index.js hook run post-tool-use --ide claude-code"
                  }
                ]
              },
              {
                "hooks": [
                  {
                    "type": "command",
                    "command": "/home/u/.captainHook/bin/captainShim hook post-tool-use"
                  }
                ]
              }
            ],
            "Stop": [
              {
                "hooks": [
                  {
                    "type": "command",
                    "command": "node /opt/cavemem/index.js hook run stop --ide claude-code"
                  }
                ]
              }
            ]
          },
          "theme": "dark — café"
        }

        """;

    [Fact]
    public void DryRun_ShowsTheDiff_WritesNothing()
    {
        File.WriteAllText(_settings, Legacy);
        var (exit, stdout, _) = Run();

        Assert.Equal(0, exit);
        Assert.Contains($"-            \"command\": \"/home/u/.captainHook/bin/captainShim hook session-start\"", stdout);
        Assert.Contains($"+            \"command\": \"{_shim} hook session-start\"", stdout);
        Assert.Contains("dry run — nothing written", stdout);
        Assert.Equal(Legacy, File.ReadAllText(_settings));
        Assert.Empty(Backups());
    }

    [Fact]
    public void Apply_RepointsOnlyOurs_KeepsEventsOrderAndEverythingElse()
    {
        File.WriteAllText(_settings, Legacy);
        var (exit, stdout, _) = Run("--apply");

        Assert.Equal(0, exit);
        var after = File.ReadAllText(_settings);
        // Exactly the two command strings changed — same events (an upgrade
        // never widens what was chosen), same position after cavemem's entry,
        // non-ASCII untouched, trailing newline kept.
        var expected = Legacy
            .Replace("/home/u/.captainHook/bin/captainShim hook session-start", $"{_shim} hook session-start")
            .Replace("/home/u/.captainHook/bin/captainShim hook post-tool-use", $"{_shim} hook post-tool-use");
        Assert.Equal(expected, after);
        Assert.Contains("the events you already wire", stdout);
        Assert.Contains("not wired", stdout);

        var backup = Assert.Single(Backups());
        Assert.EndsWith("settings.json.bak-captainhook-20261019-153000", backup);
        Assert.Equal(Legacy, File.ReadAllText(backup));   // byte-for-byte previous state
    }

    [Fact]
    public void SecondRun_IsUpToDate_NoWriteNoBackup()
    {
        File.WriteAllText(_settings, Legacy);
        Assert.Equal(0, Run("--apply").Exit);
        var once = File.ReadAllText(_settings);
        foreach (var b in Backups()) File.Delete(b);

        var (exit, stdout, _) = Run("--apply");
        Assert.Equal(0, exit);
        Assert.Contains("up to date — nothing to change", stdout);
        Assert.Equal(once, File.ReadAllText(_settings));
        Assert.Empty(Backups());
    }

    [Fact]
    public void FirstInstall_NoFile_WiresEveryDeclaredEvent()
    {
        var (exit, stdout, _) = Run("--apply");

        Assert.Equal(0, exit);
        Assert.Contains("(new file)", stdout);
        var root = JsonNode.Parse(File.ReadAllText(_settings))!.AsObject();
        var hooks = root["hooks"]!.AsObject();
        var declared = Registry.Get("claude-code").Events.Keys.ToList();
        Assert.Equal(declared, hooks.Select(kv => kv.Key).ToList());
        Assert.Equal($"{_shim} hook pre-tool-use",
            hooks["PreToolUse"]![0]!["hooks"]![0]!["command"]!.GetValue<string>());
        Assert.Equal("command", hooks["Stop"]![0]!["hooks"]![0]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void ExplicitEvents_AddsAndRemovesOnlyOurs()
    {
        File.WriteAllText(_settings, Legacy);
        Assert.Equal(0, Run("--apply", "--events", "pre-tool-use,Stop").Exit);

        var hooks = JsonNode.Parse(File.ReadAllText(_settings))!["hooks"]!.AsObject();
        Assert.False(hooks.ContainsKey("SessionStart"));            // ours only — group and key went
        Assert.Single(hooks["PostToolUse"]!.AsArray());              // cavemem's group stays
        Assert.Equal(2, hooks["Stop"]!.AsArray().Count);             // cavemem + ours appended
        Assert.Equal($"{_shim} hook stop", hooks["Stop"]![1]!["hooks"]![0]!["command"]!.GetValue<string>());
        Assert.True(hooks.ContainsKey("PreToolUse"));
    }

    [Fact]
    public void Remove_TakesOutOursOnly()
    {
        File.WriteAllText(_settings, Legacy);
        Assert.Equal(0, Run("--remove", "--apply").Exit);

        var after = File.ReadAllText(_settings);
        Assert.DoesNotContain("captainShim", after);
        Assert.Contains("cavemem/index.js hook run post-tool-use", after);
        Assert.Contains("cavemem/index.js hook run stop", after);
        Assert.Contains("\"permissions\"", after);
        Assert.False(JsonNode.Parse(after)!["hooks"]!.AsObject().ContainsKey("SessionStart"));
    }

    [Fact]
    public void InvalidJson_IsRefused_FileUntouched()
    {
        const string broken = "{ \"hooks\": { ";
        File.WriteAllText(_settings, broken);
        var (exit, _, stderr) = Run("--apply");

        Assert.Equal(1, exit);
        Assert.Contains("not valid JSON", stderr);
        Assert.Equal(broken, File.ReadAllText(_settings));
        Assert.Empty(Backups());
    }

    [Fact]
    public void Apply_RefusesAShimThatDoesNotExist()
    {
        File.WriteAllText(_settings, Legacy);
        File.Delete(_shim);
        var (exit, stdout, stderr) = Run("--apply");

        Assert.Equal(1, exit);
        Assert.Contains("does not exist", stdout);
        Assert.Contains("refusing to --apply", stderr);
        Assert.Equal(Legacy, File.ReadAllText(_settings));
    }

    [Fact]
    public void SymlinkedSettings_WritesThroughToTheTarget_LinkSurvives()
    {
        var real = Path.Combine(_dir, "dotfiles-settings.json");
        File.WriteAllText(real, Legacy);
        File.CreateSymbolicLink(_settings, real);

        Assert.Equal(0, Run("--apply").Exit);
        Assert.NotNull(new FileInfo(_settings).LinkTarget);         // still a link
        Assert.Contains($"{_shim} hook post-tool-use", File.ReadAllText(real));
        Assert.Single(Directory.GetFiles(_dir, "dotfiles-settings.json.bak-captainhook-*"));
    }

    [Theory]
    [InlineData("/x/captainShim hook stop", true)]
    [InlineData("'/a b/captainShim' hook stop", true)]
    [InlineData("/home/u/.captainHook/bin/captainHook hook user-prompt-submit", true)]
    [InlineData("dotnet /r/bin/captainHook.dll hook user-prompt-submit", true)]
    [InlineData("node /opt/cavemem/index.js hook run stop", false)]
    [InlineData("/x/captainShim-wrapper hook stop", false)]
    [InlineData("/x/captainHook ui", false)]
    public void Ownership_IsTheProgramName_NotASubstring(string command, bool ours) =>
        Assert.Equal(ours, InstallHooksVerb.IsOurs(new JsonObject { ["command"] = command }));

    [Fact]
    public void ShimPathsWithSpaces_AreQuotedForTheHostShell()
    {
        var spec = InstallSpec.TryFrom(Registry.Get("claude-code"))!;
        Assert.Equal("'/a b/captainShim' hook pre-tool-use", spec.Render("/a b/captainShim", "PreToolUse"));
        Assert.Equal("/a/captainShim hook user-prompt-submit", spec.Render("/a/captainShim", "UserPromptSubmit"));
    }

    [Fact]
    public void UnknownEvent_IsAUsageError()
    {
        var (exit, _, stderr) = Run("--events", "not-an-event");
        Assert.Equal(1, exit);
        Assert.Contains("unknown event", stderr);
    }

    [Fact]
    public void Parse_TheNewVerbs()
    {
        Assert.Equal(Mode.InstallHooks, Invocation.Parse(["install-hooks", "--apply"]).Mode);
        Assert.Equal(Mode.Version, Invocation.Parse(["version"]).Mode);
        Assert.Equal(Mode.Version, Invocation.Parse(["--version"]).Mode);
        Assert.Equal(Mode.Shim, Invocation.Parse(["hook", "version"]).Mode);   // never shadows a hook
    }

    [Fact]
    public void Version_PrintsTheRealBaseDirAndIdentity()
    {
        var o = new StringWriter();
        Assert.Equal(0, VersionVerb.Run(o, new StringWriter(), AppContext.BaseDirectory));
        var text = o.ToString();
        Assert.StartsWith("captainHook ", text);
        Assert.Contains($"base        {AppContext.BaseDirectory}", text);
        Assert.Contains($"identity    {ContentIdentity.Compute(AppContext.BaseDirectory)}", text);
    }
}

public class UnifiedDiffTests
{
    [Fact]
    public void OneChangedLine_IsOneHunkWithContext()
    {
        var a = string.Join('\n', Enumerable.Range(1, 10).Select(i => $"l{i}")) + "\n";
        var b = a.Replace("l5\n", "L5\n");
        Assert.Equal("""
            --- a
            +++ b
            @@ -2,7 +2,7 @@
             l2
             l3
             l4
            -l5
            +L5
             l6
             l7
             l8

            """, UnifiedDiff.Render(a, b, "a", "b"));
    }

    [Fact]
    public void EmptyBefore_IsAllAdditions()
    {
        Assert.Equal("--- a\n+++ b\n@@ -0,0 +1,2 @@\n+x\n+y\n", UnifiedDiff.Render("", "x\ny\n", "a", "b"));
    }
}
