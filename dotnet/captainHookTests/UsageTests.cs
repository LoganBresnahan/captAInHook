using CaptainHook.Core;
using CaptainHook.Shim;
using CaptainHook.Wire;

namespace CaptainHook.Tests;

// help / --help / -h / <verb> --help print usage; an unknown word or a bare
// invocation is a usage error that NEVER dispatches (0.1.4). Before this,
// `captainhook --help` fell through to a fake UserPromptSubmit through the
// echo handler, and `help` even cold-spawned a daemon.
public class UsageTests
{
    [Theory]
    [InlineData("help")]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("history", "--help")]
    [InlineData("self", "-h")]
    [InlineData("install-hooks", "--help")]
    [InlineData("hook", "--help")]
    [InlineData("help", "history")]
    public void HelpRequests_AreHelp(params string[] args) =>
        Assert.Equal(Mode.Help, Invocation.Parse(args).Mode);

    [Theory]
    [InlineData]                          // bare: there is no dev-shim default any more
    [InlineData("hepl")]
    [InlineData("--verbose")]
    [InlineData("user-prompt-submit")]    // an event without `hook`
    [InlineData("--harness", "claude-code")]
    [InlineData("--no-daemon")]
    public void EverythingElse_IsABadCommand_NeverADispatch(params string[] args)
    {
        var inv = Invocation.Parse(args);
        Assert.Equal(Mode.BadCommand, inv.Mode);
        Assert.Equal(args.Length > 0 ? args[0] : null, inv.EventName);
    }

    [Theory]
    [InlineData(Mode.Shim, "hook", "pre-tool-use")]
    [InlineData(Mode.Collapsed, "--no-daemon", "hook", "stop")]
    [InlineData(Mode.Shim, "--harness", "claude-code", "hook", "stop")]
    [InlineData(Mode.Shim, "hook")]       // the payload's own event field decides
    [InlineData(Mode.Daemon, "--daemon")]
    [InlineData(Mode.History, "history", "git")]
    [InlineData(Mode.Version, "--version")]
    public void HooksAndVerbs_StillParse(Mode expected, params string[] args) =>
        Assert.Equal(expected, Invocation.Parse(args).Mode);

    [Fact]
    public void Help_PrintsEveryVerb_OnStdout_ExitZero()
    {
        var o = new StringWriter();
        Assert.Equal(0, UsageVerb.Help(o));
        var text = o.ToString();
        Assert.StartsWith("usage: captainHook", text);
        foreach (var verb in new[] { "hook <event>", "--daemon", "version", "install-hooks", "self update", "history", "doctor", "ui", "mail", "--help" })
            Assert.Contains(verb, text);
    }

    [Fact]
    public void BadCommand_NamesTheWord_UsageOnStderr_ExitOne()
    {
        var e = new StringWriter();
        Assert.Equal(1, UsageVerb.BadCommand("hepl", e));
        Assert.StartsWith("captainHook: unknown command 'hepl'", e.ToString());
        Assert.Contains("usage: captainHook", e.ToString());

        var bare = new StringWriter();
        Assert.Equal(1, UsageVerb.BadCommand(null, bare));
        Assert.StartsWith("captainHook: no command given", bare.ToString());
    }

    [Fact]
    public async Task TheShim_AnswersHelp_AndRefusesAnUnknownWord_WithoutDispatching()
    {
        var o = new MemoryStream();
        Assert.Equal(0, await ShimMain.RunAsync(["--help"], new MemoryStream(), o, new StringWriter()));
        Assert.Contains("usage: captainShim hook <event>", System.Text.Encoding.UTF8.GetString(o.ToArray()));

        var stdout = new MemoryStream();
        var stderr = new StringWriter();
        // Bare, as a misconfigured hook command would run it: no dispatch,
        // nothing on stdout (the hook protocol channel), exit 1 (non-blocking).
        Assert.Equal(1, await ShimMain.RunAsync([], new MemoryStream("{}"u8.ToArray()), stdout, stderr));
        Assert.Empty(stdout.ToArray());
        Assert.Contains("usage: captainShim", stderr.ToString());
    }
}

// The echo demo handler is opt-in since 0.1.4 (CAPTAINHOOK_ECHO=1): by
// default no prompt gets a "seen @" line and no tool call an audit.echo.
public class EchoOptInTests
{
    [Fact]
    public void ByDefault_TheRegistryHasNoEcho()
    {
        var saved = Environment.GetEnvironmentVariable("CAPTAINHOOK_ECHO");
        try
        {
            Environment.SetEnvironmentVariable("CAPTAINHOOK_ECHO", null);
            Assert.DoesNotContain(HookRun.BuildDefaultRegistry().Specs, s => s.Spec.Name == "echo");
        }
        finally { Environment.SetEnvironmentVariable("CAPTAINHOOK_ECHO", saved); }
    }

    [Fact]
    public void OptedIn_EchoRegistersOnItsThreeEvents()
    {
        using var echo = new EchoOn();
        Assert.Equal(new[] { "PostToolUse", "SessionStart", "UserPromptSubmit" },
            HookRun.BuildDefaultRegistry().Specs.Where(s => s.Spec.Name == "echo").Select(s => s.EventType).OrderBy(x => x).ToArray());
    }

    [Fact]
    public async Task ByDefault_AUserPromptSubmitAnswersTheBareNoop()
    {
        var saved = Environment.GetEnvironmentVariable("CAPTAINHOOK_ECHO");
        try
        {
            Environment.SetEnvironmentVariable("CAPTAINHOOK_ECHO", null);
            var stdout = new StringWriter();
            var exit = await HookRun.CollapsedAsync(new Invocation(Mode.Collapsed, "user-prompt-submit", "claude-code"),
                new StringReader("""{"prompt":"hi"}"""), stdout, new StringWriter(),
                harnessDir: Path.Combine(Path.GetTempPath(), "captainhook-no-harness-overrides"));
            Assert.Equal(0, exit);
            Assert.Equal("{}", stdout.ToString());
        }
        finally { Environment.SetEnvironmentVariable("CAPTAINHOOK_ECHO", saved); }
    }
}
