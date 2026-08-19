using System.Diagnostics;
using CaptainHook.Mail;

namespace CaptainHook.Tests;

// ADR-0017 decision 6, slice `turn-claude-payload` (roadmap item 22, phase 4) —
// the SHIPPED `examples/payloads/turn-claude.sh`, run as a real process against
// a real store, with a stub `claude` standing in for the woken agent.
//
// The payload is what a robot nudge wakes, so it is the one place where three
// separate claims meet, and none of them can be checked by reading the file:
//
//   1. THE REENTRANCY GUARD IS PASSED, not merely typed. The stub `claude`
//      EXITS NONZERO when `--setting-sources ""` is missing (MailDogfoodTests'
//      pattern, for the same reason: a payload that spawns the agent it hooks
//      is a regress the engine cannot detect, so the only available proof is a
//      child that refuses). The mutation test below strips the flag from a copy
//      and shows the stub really does refuse.
//   2. THE SECOND GUARD — the payload runs on `MailNudge` and nothing else.
//      `MailNudge` is internal, so a turn can never fire the event that woke it;
//      that stays true only while the payload is registered on `mail-nudge`, and
//      the refusal is what keeps a misregistration from being silent.
//   3. THE PICKUP IS THE ROLE'S SESSIONLESS MAILBOX. This is the answer to the
//      corpse question the brain review left open (see
//      `WatcherDeadMailboxTests.TheTurnPayloadsSessionlessMailbox_…`): the
//      cursor a turn leaves has NO instance, so no number of turns can grow a
//      dead-mailbox candidate. Here we check the file it actually writes.
//
// …plus the ORDER that makes the whole thing safe to fail: every cheap refusal
// happens BEFORE the pickup, so nothing is ever delivered to a turn that could
// not have read it.
public class TurnPayloadTests : IDisposable
{
    private readonly TempRuntimeDir _tmp = new();
    private readonly string _home, _mailDir, _workspace, _stubDir, _argvLog, _promptLog;

    public TurnPayloadTests()
    {
        Directory.CreateDirectory(_tmp.Path);
        _home = Path.Combine(_tmp.Path, "home");
        _mailDir = Path.Combine(_home, ".captainHook", "mail");
        _workspace = Path.Combine(_tmp.Path, "repo");
        _stubDir = Path.Combine(_tmp.Path, "stubbin");
        _argvLog = Path.Combine(_tmp.Path, "argv.txt");
        _promptLog = Path.Combine(_tmp.Path, "prompt.txt");
        Directory.CreateDirectory(_mailDir);
        Directory.CreateDirectory(_workspace);
        Directory.CreateDirectory(_stubDir);
    }

    public void Dispose() => _tmp.Dispose();

    private static string Shipped => Path.Combine(DogfoodInfra.PayloadsDir(), "turn-claude.sh");

    /// The stand-in for the woken agent. It ENFORCES the guard — exit 1 when
    /// `--setting-sources ""` is absent — and records its argv and the prompt it
    /// was handed, which is how the argv pin below is a pin rather than a grep.
    private void WriteStubClaude()
    {
        var stub = Path.Combine(_stubDir, "claude");
        File.WriteAllText(stub, $"""
            #!/bin/sh
            guard=no
            prev=""
            for a in "$@"; do
              if [ "$prev" = "--setting-sources" ] && [ -z "$a" ]; then guard=yes; fi
              prev="$a"
            done
            printf '%s\n' "$@" > {_argvLog}
            if [ "$guard" != yes ]; then
              echo "stub-claude: REENTRANCY GUARD MISSING (--setting-sources \"\")" >&2
              exit 1
            fi
            cat > {_promptLog}
            echo "stub-claude: the turn ran"
            """.Replace("\r\n", "\n"));
        File.SetUnixFileMode(stub,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private void Send(string id, string to = "reviewer", MailPriority priority = MailPriority.Urgent) =>
        MailFixtures.AppendOk(new MailStore(_mailDir),
            MailFixtures.Envelope(id: id, to: to, priority: priority, ttl: 3));

    private static string Nudge(string role = "reviewer", string dispatchId = "d-nudge") =>
        // The trailing braces are concatenated rather than written inside the
        // raw literal, which cannot end in three of them.
        $$"""{"v":1,"dispatchId":"{{dispatchId}}","event":{"type":"MailNudge","payload":{"hook_event_name":"MailNudge","role":"{{role}}","reason":"1 unread past quiet (12m+) · no live session","digest":"[captAInHook mail] 1 message(s)","replyHow":"Answer on the bus, not on stdout.","envelopeIds":["m-01"]"""
        + "}}}";

    private async Task<(int Exit, string Stdout, string Stderr)> RunAsync(
        string envelope, string? script = null, bool withStubOnPath = true, bool withWorkspace = true)
    {
        var psi = new ProcessStartInfo(script ?? Shipped)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // The stub FIRST, so `command -v claude` can never reach a real one on
        // the developer's machine; without it, nothing named claude is on PATH.
        psi.Environment["PATH"] = (withStubOnPath ? _stubDir + ":" : "") + "/usr/bin:/bin";
        psi.Environment["HOME"] = _home;
        psi.Environment["CAPTAINHOOK_BIN"] = DogfoodInfra.EngineBin();
        psi.Environment["CAPTAINHOOK_MAIL_DIR"] = _mailDir;
        psi.Environment["CAPTAINHOOK_LOG"] = Path.Combine(_tmp.Path, "trail.jsonl");
        psi.Environment["CAPTAINHOOK_LOG_STDERR"] = "off";
        psi.Environment["DOTNET_ROOT"] = DogfoodInfra.DotnetRoot();
        if (withWorkspace) psi.Environment["TURN_WORKSPACE"] = _workspace;

        using var p = Process.Start(psi)!;
        await p.StandardInput.WriteLineAsync(envelope);
        p.StandardInput.Close();
        var stdout = await p.StandardOutput.ReadToEndAsync();
        var stderr = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(90)).Token);
        return (p.ExitCode, stdout, stderr);
    }

    private string[] Cursors() =>
        Directory.Exists(_mailDir)
            ? Directory.GetFiles(_mailDir, "cursor.*.json").Select(Path.GetFileName).Order().ToArray()!
            : [];

    // ---- the happy path, and the mailbox it reads as --------------------------------

    /// The whole chain: a nudge in, the guard passed, the mail delivered into
    /// the ROLE'S SESSIONLESS mailbox, the prompt carrying both JSON lines the
    /// turn needs, and exactly one noop on the wire.
    [Fact]
    public async Task ANudge_PassesTheGuard_DeliversToTheSessionlessMailbox_AndAnswersOneNoop()
    {
        WriteStubClaude();
        Send("m-01");

        var (exit, stdout, _) = await RunAsync(Nudge());

        Assert.Equal(0, exit);
        Assert.Equal("{\"effect\":\"noop\"}\n", stdout.Replace("\r\n", "\n"));

        // THE claim of this slice: the cursor a turn leaves has no instance.
        // `cursor.<role>..json` is the sessionless key — an empty instance
        // segment, which `MailCursors.TryParseCursorFileName` reads back as
        // null and the dead-mailbox rule therefore never considers.
        Assert.Equal(["cursor.reviewer..json"], Cursors());
        Assert.Null(Assert.Single(MailCursors.List(_mailDir)).Session);

        // …and the model really was handed the mail, not just the nudge.
        var prompt = File.ReadAllText(_promptLog);
        Assert.Contains("\"type\":\"MailNudge\"", prompt);          // the nudge, verbatim
        Assert.Contains("\"effect\":\"inject\"", prompt);           // the delivery, verbatim
        Assert.Contains("m-01", prompt);
        Assert.Contains("replyHow", prompt);                        // spelled once, in the engine
    }

    /// The argv pin. `-p` is what makes it one prompt in and one answer out;
    /// `--setting-sources` + the empty string is the reentrancy guard, and the
    /// two are asserted as adjacent argv entries rather than as a substring of
    /// the file, so a reordering that breaks the flag is caught.
    [Fact]
    public async Task TheTurnIsSpawnedWithPrintAndTheReentrancyGuard()
    {
        WriteStubClaude();
        Send("m-01");
        await RunAsync(Nudge());

        var argv = File.ReadAllLines(_argvLog);
        Assert.Equal(["-p", "--setting-sources", "", "--allowedTools"], argv[..4]);

        // `--setting-sources ""` takes the operator's PERMISSIONS away with
        // their hooks, so the allowlist is what keeps a turn able to do the one
        // thing the channel is for: answer on the bus.
        Assert.Contains("mail send", argv[4]);
        Assert.Equal(5, argv.Length);
    }

    /// THE MUTATION that gives the pin its meaning: strip the guard from a copy
    /// and the stub refuses. The payload still answers noop (a nudge has no loop
    /// to fail) and says on stderr that the turn did not run.
    [Fact]
    public async Task GuardStripped_TheStubRefuses_AndThePayloadSaysSo()
    {
        WriteStubClaude();
        Send("m-01");

        var shipped = File.ReadAllText(Shipped);
        Assert.Contains("-p --setting-sources \"\"", shipped);       // the mutation must have a target

        var stripped = Path.Combine(_tmp.Path, "turn-no-guard.sh");
        File.WriteAllText(stripped, shipped.Replace("-p --setting-sources \"\"", "-p"));
        File.SetUnixFileMode(stripped,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var (exit, stdout, stderr) = await RunAsync(Nudge(), script: stripped);

        Assert.Equal(0, exit);
        Assert.Contains("\"effect\":\"noop\"", stdout);
        Assert.Contains("exited nonzero", stderr);
        Assert.DoesNotContain("the turn ran", File.Exists(_promptLog) ? File.ReadAllText(_promptLog) : "");
    }

    // ---- guard 2: one event, and only one -------------------------------------------

    /// Registered on a real hook event, this payload would spawn an agent whose
    /// own hook fires the event that spawned it — ADR-0010 N7's regress. It
    /// refuses, before it reads anything and before it delivers anything.
    [Theory]
    [InlineData("Stop")]
    [InlineData("UserPromptSubmit")]
    [InlineData("PreToolUse")]
    public async Task AnyEventButMailNudge_IsRefused_AndNothingIsDelivered(string type)
    {
        WriteStubClaude();
        Send("m-01");

        var (exit, stdout, stderr) = await RunAsync(
            $$"""{"v":1,"dispatchId":"d-1","event":{"type":"{{type}}","sessionId":"s-1","payload":{"role":"reviewer" """.TrimEnd() + "}}}");

        Assert.Equal(0, exit);
        Assert.Contains("\"effect\":\"noop\"", stdout);
        Assert.Contains("may only be registered on the internal", stderr);
        Assert.Empty(Cursors());                       // nothing was picked up
        Assert.False(File.Exists(_argvLog));           // and no model was spawned
    }

    // ---- the order: nothing is delivered to a turn that cannot read it ---------------

    /// The pickup is destructive — it advances a cursor and writes a
    /// `mail.deliver` — so every cheap refusal comes FIRST. No model on PATH
    /// means the mail is still pending for the next turn, not delivered into a
    /// turn that never happened.
    [Fact]
    public async Task NoModelOnPath_LeavesTheMailPending()
    {
        Send("m-01");

        var (exit, stdout, stderr) = await RunAsync(Nudge(), withStubOnPath: false);

        Assert.Equal(0, exit);
        Assert.Contains("\"effect\":\"noop\"", stdout);
        Assert.Contains("not on PATH", stderr);
        Assert.Empty(Cursors());
    }

    /// A turn has to run somewhere specific, and the daemon's own working
    /// directory is not a workspace anybody chose. Refused, and again before
    /// the pickup.
    [Fact]
    public async Task NoWorkspace_IsRefused_BeforeAnythingIsDelivered()
    {
        WriteStubClaude();
        Send("m-01");

        var (_, stdout, stderr) = await RunAsync(Nudge(), withWorkspace: false);

        Assert.Contains("\"effect\":\"noop\"", stdout);
        Assert.Contains("no workspace for role reviewer", stderr);
        Assert.Empty(Cursors());
    }

    /// The race the watcher cannot avoid: a window reads the mail between the
    /// brain's decision and this spawn. The pickup finds nothing, and the turn
    /// costs nothing — cheap when there is nothing to say is what makes a robot
    /// channel affordable at all.
    [Fact]
    public async Task NothingLeftToDeliver_SpendsNoTurn()
    {
        WriteStubClaude();
        Send("m-01");

        await RunAsync(Nudge());                        // the first turn takes it
        Assert.True(File.Exists(_argvLog));
        File.Delete(_argvLog);

        var (_, stdout, stderr) = await RunAsync(Nudge(dispatchId: "d-again"));

        Assert.Contains("\"effect\":\"noop\"", stdout);
        Assert.Contains("nothing left to deliver", stderr);
        Assert.False(File.Exists(_argvLog));            // no second model call
    }

    /// Every turn of a role shares ONE mailbox, however many turns run. This is
    /// the corpse question's answer stated as a count: the fresh-session-per-turn
    /// shape the brain review named would have left a cursor file per turn.
    [Fact]
    public async Task ManyTurns_LeaveExactlyOneMailbox()
    {
        WriteStubClaude();
        Send("m-01");
        await RunAsync(Nudge());
        Send("m-02");
        await RunAsync(Nudge(dispatchId: "d-2"));
        Send("m-03");
        await RunAsync(Nudge(dispatchId: "d-3"));

        Assert.Equal(["cursor.reviewer..json"], Cursors());
    }
}
