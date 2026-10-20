namespace CaptainHook.Wire;

// ADR-0004 decision 1: one binary, three modes. Which mode a process runs in is
// decided ONCE, here, from argv — never inferred mid-flight — so the seam the
// daemon slices build on (shim forwards, daemon serves, collapsed dispatches
// in-process) is explicit and unit-testable without spawning a process.

/// The three ADR-0004 modes, plus the pre-existing actors demo subcommand.
public enum Mode
{
    /// `hook <event>` — forward to a warm daemon over the socket; falls back to
    /// collapsed on connect failure. (Forwarding lands in shim-forward-or-fallback;
    /// until then shim mode dispatches in-process, indistinguishable from collapsed.)
    Shim,
    /// `hook <event> --no-daemon` — dispatch in-process exactly like today's
    /// single-shot binary (CI, one-off runs, and the shim's automatic fallback).
    Collapsed,
    /// `--daemon` — build registry/dispatcher/workers once, serve over the socket.
    Daemon,
    /// `doctor` — reap leftover daemons/files with the PID-reuse guard.
    Doctor,
    /// `actors-demo` — drive the F# actor layer directly.
    ActorsDemo,
    /// `ui` — open the management GUI in the default browser with the bearer
    /// token in the URL fragment (ADR-0008 decision 3). Engine-only; the shim
    /// refuses it like every other non-hook verb.
    Ui,
    /// `mail <subverb>` — the mailbox bus CLI (ADR-0016 decision 7): `mail
    /// send` appends one validated envelope to the durable store; `mail
    /// digest` is the read path (an exec-wire handler command). The subverb
    /// rides EventName; the engine validates it (unknown subverbs are a loud
    /// usage error there, not a parse-time guess here). Engine-only; the shim
    /// refuses it.
    MailSend,
    /// `version` / `--version` — what is installed and from where (ADR-0020
    /// d5). Engine-only; the shim refuses it.
    Version,
    /// `install-hooks [flags]` — the consent-first hook wiring verb (ADR-0020
    /// d4): dry-run diff by default, `--apply` to write. Its flags are its
    /// own (the engine hands it args[1..]). Engine-only; the shim refuses it.
    InstallHooks,
    /// `self update [--version X] | rollback | prune | uninstall` — manage the
    /// install this engine runs from with its install.sh (ADR-0020 d8). Its
    /// arguments are its own (the engine hands it args[1..]). Engine-only;
    /// the shim refuses it.
    Self,
    /// `history [pattern] [flags]` — search the shell commands agents ran, as
    /// the trail recorded them (`tool.shell`). Its arguments are its own (the
    /// engine hands it args[1..]). Engine-only; the shim refuses it.
    History,
    /// `help`, `--help`, `-h`, or `<verb> --help|-h` — print the usage to
    /// stdout, exit 0. EventName carries the verb asked about, if any.
    Help,
    /// Anything that is neither a verb nor a hook dispatch — an unknown word,
    /// a stray flag, or no arguments at all. Usage to stderr, exit 1, and
    /// NEVER a dispatch (before 0.1.4 every such argv fell through to a
    /// UserPromptSubmit). EventName carries the offending word (null: none).
    BadCommand,
}

/// One parsed invocation: the mode plus the hook-dispatch arguments that shim
/// and collapsed modes share. Daemon/ActorsDemo ignore EventName/HarnessName.
public sealed record Invocation(Mode Mode, string? EventName, string HarnessName)
{
    /// The engine's verbs: the first word that selects a non-hook mode.
    private static readonly string[] Verbs =
        ["actors-demo", "doctor", "ui", "mail", "version", "--version", "install-hooks", "self", "history"];

    /// The words a hook dispatch (or the daemon) may start with.
    private static readonly string[] HookWords = ["hook", "--harness", "--daemon", "--no-daemon"];

    private static bool IsHelp(string a) => a is "help" or "--help" or "-h";

    public static Invocation Parse(string[] args)
    {
        // Usage, asked for: `help [verb]`, `--help`, `-h`, `<verb> --help`.
        // Checked first, so a help request can never reach a dispatch.
        if (args.Length > 0 && IsHelp(args[0]))
            return new(Mode.Help, args.Length > 1 ? args[1] : null, "claude-code");
        if (args.Length > 1 && (Verbs.Contains(args[0]) || args[0] == "hook") && args[1] is "--help" or "-h")
            return new(Mode.Help, args[0], "claude-code");
        // Nothing, or a word that is neither a verb nor a hook dispatch: a
        // human typo, never a fake event. Only `hook …` / `--daemon` dispatch.
        if (args.Length == 0 || !(Verbs.Contains(args[0]) || HookWords.Contains(args[0])))
            return new(Mode.BadCommand, args.Length > 0 ? args[0] : null, "claude-code");
        if (Array.IndexOf(args, "hook") < 0 && Array.IndexOf(args, "--daemon") < 0 && !Verbs.Contains(args[0]))
            return new(Mode.BadCommand, args[0], "claude-code");

        if (args.Length > 0 && args[0] == "actors-demo")
            return new(Mode.ActorsDemo, null, "claude-code");
        if (args.Length > 0 && args[0] == "doctor")
            return new(Mode.Doctor, null, "claude-code");
        if (args.Length > 0 && args[0] == "ui")
            return new(Mode.Ui, null, "claude-code");
        if (args.Length > 0 && args[0] == "mail")
            return new(Mode.MailSend, args.Length > 1 ? args[1] : null, "claude-code");
        if (args.Length > 0 && args[0] is "version" or "--version")
            return new(Mode.Version, null, "claude-code");
        if (args.Length > 0 && args[0] == "install-hooks")
            return new(Mode.InstallHooks, null, "claude-code");
        if (args.Length > 0 && args[0] == "self")
            return new(Mode.Self, null, "claude-code");
        if (args.Length > 0 && args[0] == "history")
            return new(Mode.History, null, "claude-code");

        string? eventName = null;
        var harnessName = "claude-code";
        bool daemon = false, noDaemon = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "hook" && i + 1 < args.Length) eventName ??= args[++i];
            else if (args[i] == "--harness" && i + 1 < args.Length) harnessName = args[++i];
            else if (args[i] == "--daemon") daemon = true;
            else if (args[i] == "--no-daemon") noDaemon = true;
        }

        // --daemon wins over --no-daemon: a daemon invocation is never a hook
        // dispatch, so the collapsed flag has nothing to force.
        var mode = daemon ? Mode.Daemon : noDaemon ? Mode.Collapsed : Mode.Shim;
        return new(mode, eventName, harnessName);
    }
}
