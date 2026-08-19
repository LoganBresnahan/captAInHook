using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CaptainHook.Api;
using CaptainHook.Core;
using CaptainHook.Handlers;
using CaptainHook.Mail;
using CaptainHook.Wire;
using static CaptainHook.Tests.TestUtil;

namespace CaptainHook.Tests;

// Roadmap item 22 / ADR-0017 phase 6, slice `e2e-stub-runner-loop` — THE WHOLE
// CHAIN, ZERO TOKENS.
//
// Every part of the robot channel has a test. Nothing had run them in one line,
// through real processes, with the daemon's own watcher deciding: a human asks
// a role nobody is sitting in, the watcher notices, a turn is woken, the turn
// reads its mail and answers on the bus, and the asker's next prompt carries
// the answer back. Six subsystems, one assertion each in the unit suites, and
// exactly one place where a wiring mistake between any two of them shows up.
//
//   mail send ──► trail row ──► watcher brain ──► MailNudge ──► exec handler
//   (asker, CLI)   (mail.append)   (watch.json)    (dispatcher)  (turn-claude.sh)
//                                                                     │
//        asker's next UserPromptSubmit ◄── mail send ◄── stub claude ◄┘
//        (mail digest, the inject)         (the answer)   (the pickup, first)
//
// **Zero tokens, and the stub is not a mock.** `claude` on the turn's PATH is a
// shell script; everything else in the picture is the shipped artifact run
// verbatim — the committed `examples/payloads/turn-claude.sh`, the real
// `captainHook mail send` / `mail digest` verbs as child processes, the real
// dispatcher, the real watcher inside a real daemon. The stub does exactly what
// a woken model does and nothing else: it reads the prompt, finds the id of the
// envelope it was handed, and writes ONE answer envelope to `mail send`.
//
// **The plan's shape for this slice is superseded, and by its own phase 4.**
// The ADR's row says the stub payload "fires `captainShim hook
// user-prompt-submit` with its own session id". That premise died when
// `turn-claude-payload` resolved reentrancy the other way: guard 1
// (`--setting-sources ""`) is kept verbatim, so the woken turn fires NO hooks at
// all and the payload does the pickup itself. A turn firing hooks here would be
// testing a shape that ships nowhere. What is exercised instead is what ships:
// the payload's own pickup, out of the role's sessionless mailbox.
//
// **`mail ask --wait` is not in this picture, and its absence is not a gap in
// the loop.** That verb is the thread lane's (`mail-ask-wait`, unlanded); the
// asker here closes the loop the way every human window on the live bus already
// does — the digest handler on its next `UserPromptSubmit`. When `--wait` lands
// it is a second way to read the same answer, not a second loop.
//
// The whole thing runs against a sandbox HOME and a throwaway rendezvous; the
// live ~/.captainHook tree is never touched. The clock is fake, so nothing here
// waits out an idle window, and the only real time spent is process startup.
public class WatcherLoopE2ETests : IDisposable
{
    private const string Asker = "maintainer";      // human-held: a digest registration
    private const string Robot = "reviewer";        // robot-servable: no digest, a turn payload
    private const string RequestId = "m-ask-01";
    private const string AnswerId = "m-answer-01";
    private const string AnswerBody = "read it: the deferred unescape is at parser.cs:42";

    private readonly TempRuntimeDir _rv = new();
    private readonly string _root, _home, _mailDir, _trail, _workspace, _stubDir, _promptLog;

    public WatcherLoopE2ETests()
    {
        _root = Path.Combine(Path.GetTempPath(), "chk-loop-" + Guid.NewGuid().ToString("N")[..8]);
        _home = Path.Combine(_root, "home");
        _mailDir = Path.Combine(_home, ".captainHook", "mail");
        _trail = Path.Combine(_root, "trail.jsonl");
        _workspace = Path.Combine(_root, "repo");
        _stubDir = Path.Combine(_root, "stubbin");
        _promptLog = Path.Combine(_root, "prompt.txt");
        Directory.CreateDirectory(_mailDir);
        Directory.CreateDirectory(_workspace);
        Directory.CreateDirectory(_stubDir);
    }

    public void Dispose()
    {
        _rv.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    // ---- the chain -------------------------------------------------------------------

    [Fact]
    public async Task AnUnreadRequest_WakesATurn_WhoseAnswerComesBackOnTheAskersNextPrompt()
    {
        if (!ProcessGroup.Prefix.Pgroup) return;   // xunit 2.x: no dynamic skip

        WriteStubClaude();
        var clock = new FakeClock();
        using var log = new CapturedLog();
        using var stop = new CancellationTokenSource();
        var daemon = Task.Run(() => DaemonHost.RunAsync(
            _rv.Paths, NoHarnessDir(), stop.Token,
            drainDeadline: TimeSpan.FromSeconds(10), idleWindow: TimeSpan.FromMinutes(10),
            clock: clock.Now, handlersPath: WriteHandlers(), mailDir: _mailDir,
            watchPath: WriteRules(), watchPoll: TimeSpan.FromMilliseconds(50),
            sse: new SseOptions(_trail, Poll: TimeSpan.FromMilliseconds(50))));
        await PollUntilAsync(async () =>
            await ShimClient.TryForwardAsync(_rv.Paths.SocketPath,
                new HookRequest("warmup0", "session-start", "claude-code", "{}"u8.ToArray()))
                is ForwardOutcome.Answered, TimeSpan.FromSeconds(20), "daemon up");

        // --- the asker's window opens, and asks -----------------------------
        // A real prompt first: it stamps presence for the asking session and
        // gives the maintainer role a live cursor, which is the state the
        // watcher's questions are asked against.
        var opening = await PromptAsync("ask-p0");
        Assert.DoesNotContain("captAInHook mail", opening);          // nothing waiting yet

        var (sent, sendErr) = await EngineAsync(["mail", "send"], Request());
        Assert.True(sent == 0, sendErr);

        // --- the watcher decides, alone -------------------------------------
        // Nothing below pokes the daemon. The only thing that reached it is the
        // `mail.append` row the CLI wrote to the trail it tails.
        await PollUntilAsync(() => Task.FromResult(log.Events.Any(e => e.Evt == "mail.nudge")),
            TimeSpan.FromSeconds(30), "the watcher raised a nudge off the trail row");
        var evalsAtNudge = log.Events.Count(e => e.Evt == "watch.evaluate");

        // --- the turn runs, and answers -------------------------------------
        var store = new MailStore(_mailDir);
        await PollUntilAsync(() => Task.FromResult(store.Read().Any(l => l.Envelope.Id == AnswerId)),
            TimeSpan.FromSeconds(90), "the woken turn answered on the bus");

        var answer = store.Read().Single(l => l.Envelope.Id == AnswerId).Envelope;
        Assert.Equal(Asker, answer.To);                              // back to the asker's role
        Assert.Equal(MailKind.Answer, answer.Kind);
        Assert.Equal(RequestId, answer.InReplyTo);                   // the id came through the nudge
        Assert.Equal(Robot, answer.From.Agent);
        Assert.Contains("parser.cs:42", answer.Body);

        // The pickup was real and it was the role's SESSIONLESS mailbox: the
        // cursor the turn left has no instance, so no turn can ever become a
        // dead-mailbox candidate (ADR-0018 d6).
        Assert.Equal(["cursor.reviewer..json"],
            Directory.GetFiles(_mailDir, "cursor.reviewer*.json").Select(Path.GetFileName).Order());

        // …and the turn was handed the mail itself, not just the nudge's text.
        var prompt = await File.ReadAllTextAsync(_promptLog);
        Assert.Contains("\"type\":\"MailNudge\"", prompt);
        Assert.Contains("\"effect\":\"inject\"", prompt);
        Assert.Contains(RequestId, prompt);

        // --- the asker reads it on its next prompt --------------------------
        var next = await PromptAsync("ask-p1");
        Assert.Contains("captAInHook mail", next);
        Assert.Contains(AnswerBody, next);
        Assert.Contains(Robot, next);                                // provenance names the answerer

        // Exactly once, across two process boundaries and a real cursor file.
        Assert.DoesNotContain(AnswerBody, await PromptAsync("ask-p2"));

        // --- and the loop STOPS ---------------------------------------------
        // The turn's own pickup and its answer are trail rows too, and the
        // watcher DOES re-evaluate on them — the self-feed guard is a filter on
        // what re-triggers, not a claim that nothing does. So wait for an
        // evaluation the turn's OWN rows caused before counting nudges: the same
        // assertion taken the instant the answer lands would pass on a system
        // that pokes again a tick later.
        await PollUntilAsync(
            () => Task.FromResult(log.Events.Count(e => e.Evt == "watch.evaluate") > evalsAtNudge),
            TimeSpan.FromSeconds(30), "the watcher evaluated again, on the turn's own rows");

        // Nothing about those rows is a second reason to spend the owner's
        // tokens: the request is delivered, the answer is addressed to a
        // human-held role with no rule, and the budget is spent either way. One
        // nudge is the whole bill for this exchange (N1).
        var nudge = Assert.Single(log.Events, e => e.Evt == "mail.nudge");
        var start = Assert.Single(log.Events,
            e => e.Evt == "dispatch.start" && e.Fields.DispatchId == nudge.Fields.DispatchId);
        Assert.Contains(log.Events,
            e => e.Evt == "exec.spawn" && e.Fields.DispatchId == start.Fields.DispatchId);

        // THE LEDGER SAYS THE ROBOT READ IT, and says which poke caused it: the
        // turn's `mail.deliver` carries the NUDGE's dispatch id and no session
        // (`hookEvent: UserPromptSubmit` is the seam the payload opened, not a
        // window's). That row is what stops the re-nudging — a delivery nobody
        // could join to its nudge would leave the canvas guessing.
        var pickup = Assert.Single(await TrailRowsAsync("mail.deliver"),
            r => Field(r, "dispatchId") == nudge.Fields.DispatchId);
        Assert.Equal("UserPromptSubmit", Field(pickup, "hookEvent"));
        Assert.Null(Field(pickup, "sessionId"));
        Assert.Equal(Robot, pickup.GetProperty("data").GetProperty("role").GetString());

        Assert.Empty(store.VerifyChain());   // the chain still verifies after live traffic

        stop.Cancel();
        Assert.Equal(0, await daemon.WaitAsync(TimeSpan.FromSeconds(20)));
    }

    // ---- the sandbox ------------------------------------------------------------------

    /// The stand-in for the woken agent, and the ONLY stub in the picture. It
    /// enforces reentrancy guard 1 the way every other payload test does — exit
    /// nonzero when `--setting-sources ""` is missing, so the guard is proven
    /// PASSED rather than typed — then does what a model woken by a nudge does:
    /// reads the prompt, takes the id it was handed, writes one answer envelope
    /// to the real `mail send`.
    private void WriteStubClaude()
    {
        var stub = Path.Combine(_stubDir, "claude");
        File.WriteAllText(stub, $$"""
            #!/bin/sh
            guard=no
            prev=""
            for a in "$@"; do
              if [ "$prev" = "--setting-sources" ] && [ -z "$a" ]; then guard=yes; fi
              prev="$a"
            done
            prompt=$(cat)
            printf '%s' "$prompt" > {{_promptLog}}
            if [ "$guard" != yes ]; then
              echo "stub-claude: REENTRANCY GUARD MISSING (--setting-sources \"\")" >&2
              exit 1
            fi
            id=$(printf '%s' "$prompt" | sed -n 's/.*"envelopeIds":\["\([^"]*\)".*/\1/p' | head -n 1)
            [ -n "$id" ] || { echo "stub-claude: the prompt named no envelope" >&2; exit 1; }
            printf '{"v":1,"id":"{{AnswerId}}","from":{"agent":"{{Robot}}","harness":"claude-code"},"to":"{{Asker}}","kind":"answer","topic":"re: the deferred unescape","priority":"urgent","inReplyTo":"%s","body":"{{AnswerBody}}"}\n' "$id" \
              | "${CAPTAINHOOK_BIN:-captainHook}" mail send >&2 \
              || { echo "stub-claude: mail send refused the answer" >&2; exit 1; }
            echo "stub-claude: answered on the bus"
            """.Replace("\r\n", "\n"));
        File.SetUnixFileMode(stub,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// The two registrations that make the two roles what they are (d3): a
    /// `mail digest` for the asker — which is what "human-held" IS — and the
    /// shipped turn payload, which is the installation-wide robot capability.
    /// `reviewer` deliberately has NO digest: a role a window reads is never
    /// robot-nudged, so registering one here would switch the channel off.
    private string WriteHandlers()
    {
        var handlers = new
        {
            version = 1,
            handlers = new object[]
            {
                new
                {
                    name = $"mail-digest-{Asker}",
                    command = DogfoodInfra.EngineBin(),
                    args = new[] { "mail", "digest", "--role", Asker, "--seam", "ambient" },
                    events = new[] { "UserPromptSubmit" },
                    mode = "oneshot",
                    failMode = "open",
                    budgetMs = 30000,
                    env = ChildEnv(),
                },
                new
                {
                    name = "turn-claude",
                    command = Path.Combine(DogfoodInfra.PayloadsDir(), "turn-claude.sh"),
                    args = Array.Empty<string>(),
                    events = new[] { "mail-nudge" },
                    mode = "oneshot",
                    failMode = "open",
                    budgetMs = 120000,
                    env = ChildEnv(withTurn: true),
                },
            },
        };
        var path = Path.Combine(_root, "handlers.json");
        File.WriteAllText(path, JsonSerializer.Serialize(handlers));
        return path;
    }

    /// The per-role consent (d7). `quietFor: 0s` is what makes this a test
    /// rather than a wait: the threshold is real, it is simply due the moment
    /// the mail lands.
    private string WriteRules()
    {
        var rules = new
        {
            version = 1,
            rules = new object[]
            {
                new
                {
                    role = Robot,
                    when = new { priority = ">=urgent", quietFor = "0s" },
                    budget = new { perEnvelope = 1, perRoleHour = 4 },
                },
            },
        };
        var path = Path.Combine(_root, "watch.json");
        File.WriteAllText(path, JsonSerializer.Serialize(rules));
        return path;
    }

    /// Everything a child needs to live in the sandbox: its own HOME (hence its
    /// own mail dir), the engine binary and its runtime, and the SAME trail the
    /// daemon's watcher tails — which is the whole reason a CLI's `mail.append`
    /// reaches the watcher at all, live or here. `TURN_MODEL_CMD` is left unset
    /// on purpose: the payload's default is `claude`, and the stub is what PATH
    /// finds under that name.
    private Dictionary<string, string> ChildEnv(bool withTurn = false)
    {
        var env = new Dictionary<string, string>
        {
            ["HOME"] = _home,
            ["PATH"] = _stubDir + ":/usr/bin:/bin",
            ["CAPTAINHOOK_BIN"] = DogfoodInfra.EngineBin(),
            ["DOTNET_ROOT"] = DogfoodInfra.DotnetRoot(),
            ["CAPTAINHOOK_LOG"] = _trail,
            ["CAPTAINHOOK_LOG_STDERR"] = "off",
        };
        if (withTurn) env["TURN_WORKSPACE"] = _workspace;
        return env;
    }

    private static string Request() =>
        $$"""
        {"v":1,"id":"{{RequestId}}","from":{"agent":"{{Asker}}","harness":"claude-code","session":"s-asker"},
         "to":"{{Robot}}","kind":"request","topic":"the deferred unescape","priority":"urgent",
         "replyTo":"{{Asker}}","body":"Where does the lone-surrogate throw come from? Answer on the bus."}
        """.Replace("\n", "").Replace("\r", "");

    /// A real hook through the socket, as the asker's window. The digest handler
    /// runs behind it, so what comes back is the inject the operator would read.
    private async Task<string> PromptAsync(string id)
    {
        var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new { session_id = "s-asker", cwd = _workspace }));
        var outcome = await ShimClient.TryForwardAsync(_rv.Paths.SocketPath,
            new HookRequest(id, "user-prompt-submit", "claude-code", payload));
        return Encoding.UTF8.GetString(Assert.IsType<ForwardOutcome.Answered>(outcome).StdoutBytes);
    }

    /// The engine binary as a child process — the asker's `mail send` is a CLI
    /// invocation like anybody else's, not an in-process call.
    private async Task<(int Exit, string Stderr)> EngineAsync(string[] args, string stdin)
    {
        var psi = new ProcessStartInfo(DogfoodInfra.EngineBin())
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var (k, v) in ChildEnv()) psi.Environment[k] = v;

        using var p = Process.Start(psi)!;
        await p.StandardInput.WriteLineAsync(stdin);
        p.StandardInput.Close();
        var stderr = p.StandardError.ReadToEndAsync();
        _ = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token);
        return (p.ExitCode, await stderr);
    }

    /// The trail rows of one kind, as the CHILDREN wrote them. The daemon's own
    /// events go to the in-memory sink; this file is what a CLI and an exec child
    /// put on the record, which is the half a canvas reads.
    private async Task<IReadOnlyList<JsonElement>> TrailRowsAsync(string evt) =>
        (await ReadTrailAsync())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonDocument.Parse(l).RootElement)
            .Where(r => r.GetProperty("evt").GetString() == evt)
            .ToList();

    private static string? Field(JsonElement row, string name) =>
        row.TryGetProperty(name, out var v) ? v.GetString() : null;

    /// The trail as the children wrote it — read shared, since a child may still
    /// hold it open.
    private async Task<string> ReadTrailAsync()
    {
        await using var fs = new FileStream(_trail, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        return await sr.ReadToEndAsync();
    }
}
