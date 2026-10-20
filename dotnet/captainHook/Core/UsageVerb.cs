namespace CaptainHook.Core;

// `captainHook help` / `--help` / `-h` / `<verb> --help`, and the answer to an
// unknown command (0.1.4). Before this, any argv that was not a verb fell
// through to the hook path and dispatched a fake UserPromptSubmit through the
// echo handler (a cold daemon spawn included), so `captainhook --help` printed
// hook JSON. Now only `hook <event>` and `--daemon` dispatch.
//
// Unknown command: usage on stderr and exit 1, not 2. The engine is also a
// valid hook command (`…/captainHook hook <event>`), and to Claude Code a
// hook's exit 2 is a BLOCKING error: one typo in a hook command would block
// every prompt or tool call. Exit 1 is a visible, non-blocking error (and
// what git answers to an unknown command). A bare `captainHook` with no
// arguments is the same mistake: there is no "dev shim reads stdin" default
// any more. Human verb: stdout on purpose when usage is asked for.
public static class UsageVerb
{
    internal const string Text = """
        usage: captainHook <command> [arguments]

        Hooks (what a harness's hook command runs):
          hook <event> [--harness NAME] [--no-daemon]
                          dispatch one hook: payload JSON on stdin, effect JSON on stdout
          --daemon        run the warm daemon (the first hook starts one for you)

        Install:
          version         what is installed, and from where
          install-hooks [--apply] [--remove] [--events a,b] [--settings PATH] [--shim PATH] [--harness NAME]
                          wire the hooks into ~/.claude/settings.json (a dry run without --apply)
          self update [--version V] | rollback | prune | uninstall
                          manage this install with the installer it came with

        Use:
          history [PATTERN] [--session ID] [--since 2d|3h|DATE] [--cwd DIR] [--harness NAME]
                  [-n|--limit N] [--full] [--json]
                          search the shell commands your agents ran
          doctor          check daemons and orphaned children; reap what is stale
          ui              open the management GUI in your browser
          mail <send|ask|digest|status|watch|reap|forward> ...
                          the mailbox bus between agents

          help, --help, -h
                          this text (also: <command> --help)

        """;

    /// Usage asked for: stdout, exit 0.
    public static int Help(TextWriter stdout)
    {
        stdout.Write(Text);
        return 0;
    }

    /// No command, or one nobody knows: say so, usage on stderr, exit 1.
    public static int BadCommand(string? word, TextWriter stderr)
    {
        stderr.WriteLine(word is null
            ? "captainHook: no command given"
            : $"captainHook: unknown command '{word}'");
        stderr.WriteLine();
        stderr.Write(Text);
        return 1;
    }
}
