using System.Text.Json;
using CaptainHook.Core;
using CaptainHook.Wire;

namespace CaptainHook.Tests;

// `~` in handlers.json (ADR-0010, 2026-10-19 amendment): a leading `~/` or a
// bare `~` in a handler's command, args and cwd is the user's home, expanded
// at load so one file serves two machines whose homes differ. Nothing else is
// expanded: no `$VAR`, no `~user`, nothing mid-string — config never becomes a
// template language.
public class HandlerHomeTests
{
    [Theory]
    [InlineData("~", "/home/u")]
    [InlineData("~/", "/home/u/")]
    [InlineData("~/bin/gate.sh", "/home/u/bin/gate.sh")]
    [InlineData("/abs/~/x", "/abs/~/x")]        // not leading
    [InlineData("x~/y", "x~/y")]                // not leading
    [InlineData("~user/bin", "~user/bin")]      // no ~user
    [InlineData("~x", "~x")]
    [InlineData("$HOME/bin", "$HOME/bin")]      // no variables
    [InlineData("--flag=~/x", "--flag=~/x")]    // mid-string in an arg
    [InlineData("relative/x", "relative/x")]
    public void Expand_OnlyALeadingTildeSlashOrABareTilde(string written, string expected) =>
        Assert.Equal(expected, HomePath.Expand(written, home: "/home/u"));

    [Fact]
    public void Expand_AnUnresolvableHome_LeavesThePathAsWritten() =>
        Assert.Equal("~/x", HomePath.Expand("~/x", home: ""));

    [Fact]
    public void Expand_DefaultsToTheEnginesOwnHome() =>
        Assert.Equal(Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "x"), HomePath.Expand("~/x"));

    [Fact]
    public void TheLoader_ExpandsCommandArgsAndCwd_AndNothingElse()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        using var doc = JsonDocument.Parse("""
            {"version":1,"handlers":[{"name":"gate","command":"~/dotfiles/gate.sh",
              "args":["~/notes.md","--x=~/y","~","plain"],"events":["PreToolUse"],
              "env":{"NOTES":"~/notes.md"},"cwd":"~"}]}
            """);

        Assert.True(ExecHandlersFile.TryParse(doc.RootElement, out var entries, out var skipped, out _));
        Assert.Empty(skipped);
        var e = Assert.Single(entries);
        Assert.Equal(Path.Join(home, "dotfiles/gate.sh"), e.Command);
        Assert.Equal(new[] { Path.Join(home, "notes.md"), "--x=~/y", home, "plain" }, e.Args);
        Assert.Equal(home, e.Cwd);
        Assert.Equal("~/notes.md", e.Env["NOTES"]);   // env is not a path field
    }

    /// End to end, through a real spawn: a `~/` command runs, and the trail's
    /// exec.spawn names the EXPANDED path. A `~/` command that does not exist
    /// fails exactly as an absolute missing path does, naming the expanded
    /// path. HOME points at a temp dir so nothing lands in the real one.
    [Fact]
    public async Task ATildeCommand_Spawns_AndTheTrailShowsTheExpandedPath()
    {
        var home = Path.Combine(Path.GetTempPath(), "chk-tilde-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(home);
        var savedHome = Environment.GetEnvironmentVariable("HOME");
        try
        {
            Environment.SetEnvironmentVariable("HOME", home);
            var script = Path.Combine(home, "payload.sh");
            File.WriteAllText(script, "#!/bin/sh\nIFS= read -r _\nprintf '{\"effect\":\"inject\",\"text\":\"tilde ok\"}\\n'\n");
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var handlers = Path.Combine(home, "handlers.json");
            File.WriteAllText(handlers, """
                {"version":1,"handlers":[
                  {"name":"tilde","command":"~/payload.sh","events":["SessionStart"]},
                  {"name":"missing","command":"~/missing.sh","events":["SessionStart"]}]}
                """);

            using var log = new CapturedLog();
            var stdout = new StringWriter();
            var exit = await HookRun.CollapsedAsync(new Invocation(Mode.Collapsed, "session-start", "claude-code"),
                new StringReader("{}"), stdout, new StringWriter(),
                harnessDir: Path.Combine(home, "no-harnesses"), handlersPath: handlers);

            Assert.Equal(0, exit);
            Assert.Contains("tilde ok", stdout.ToString());
            var events = log.Events.ToArray();
            Assert.Contains(events, x => x.Evt == "exec.spawn" && Equals(x.Fields.Data!["command"], script));
            var lines = events.Select(x => x.ToJson()).ToArray();
            Assert.Contains(lines, l => l.Contains(Path.Combine(home, "missing.sh")));
            Assert.DoesNotContain(lines, l => l.Contains("~/"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOME", savedHome);
            try { Directory.Delete(home, recursive: true); } catch { }
        }
    }
}
