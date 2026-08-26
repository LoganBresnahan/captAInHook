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
//
// **A second chain shares this harness** (roadmap item 23 / ADR-0018 phase 5,
// slice `e2e-named-and-dead-instance`): ADDRESSES, from a declared mailbox
// reading its own unicast to the reaper disposing of that mailbox's corpse. It
// is here rather than in a file of its own because it is the same daemon, the
// same sandbox and the same claim — that the pieces are wired to each other —
// with a different payload at the end of it and no model at all. Its own header
// is on the test.
public class WatcherLoopE2ETests : IDisposable
{
    private const string Asker = "maintainer";      // human-held: a digest registration
    private const string Robot = "reviewer";        // robot-servable: no digest, a turn payload
    private const string RequestId = "m-ask-01";
    private const string AnswerId = "m-answer-01";
    private const string AnswerBody = "read it: the deferred unescape is at parser.cs:42";

    // The corpse (ADR-0018 d6): a role whose declared mailbox outlives the
    // window that declared it, and the reaper role the nudge goes to.
    private const string Corpse = "builder";
    private const string Box = "retired-box";
    private const string ReaperRole = "reaper";
    private const string MisaddressedBody = "for the ci-box window and nobody else";
    private const string StrandedUnicastBody = "the artifact hash you wanted, at exactly one address";
    private const string StrandedBroadcastBody = "the nightly went red on main";

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

        var (sent, _, sendErr) = await EngineAsync(["mail", "send"], Request());
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

        // PROVENANCE, all the way through (ADR-0017 N6's amendment, slice
        // `turn-provenance`). The stub wrote a `from` with no `session` — a
        // model cannot know one — and the answer on the bus carries the
        // instance the PAYLOAD minted from the dispatch this very nudge ran
        // under. Nothing in the prompt could have produced it: the payload put
        // it in the environment, the model inherited it, and `mail send`
        // stamped it. This is the assertion that says the join is real.
        Assert.Equal($"turn-{nudge.Fields.DispatchId}", answer.From.Session);
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

    // ---- the corpse ------------------------------------------------------------------

    // Roadmap item 23 / ADR-0018 phase 5, slice `e2e-named-and-dead-instance` —
    // ADDRESSES, end to end, through the same stub harness.
    //
    // Every fact item 23 added has unit tests and a golden. What nothing ran in
    // one line is the part that only exists when a real daemon decides: a named
    // mailbox reading its own unicast, that mailbox's registration going away,
    // and the daemon's watcher finding the corpse and dispatching the SHIPPED
    // reaper payload at it — which forwards the stranded mail onto a live lane
    // and then reaps the standing, in that order.
    //
    //   mail send ──► a named box reads it (`mail digest --as`) ──► the registration goes
    //                                                                       │
    //   the maintainer's next prompt ◄── mail forward + mail reap ◄── reaper-deterministic.sh
    //          (the rescue, delivered)      (the copies, forwardedFrom)     ▲
    //                                                                mail.nudge {address}
    //                                                              (the watcher, alone)
    //
    // **Nothing here is a model.** The turn payload of the test above is not
    // even registered: a deterministic reaper is a shell script reading an
    // operator's rule (ADR-0018 d6 as narrowed 2026-08-20), so the only thing
    // this exercise spends is process startup.
    //
    // **Three pins the chain is built around, all of them silent failures.**
    //   * An UNNAMED reader must never match `role@instance` (`plan-unicast`'s
    //     predicate). Misrouting is invisible: the mail simply arrives in the
    //     wrong window, or in none.
    //   * A REGISTERED `--as` mailbox is never a corpse (d6 condition ii); a
    //     mailbox is one only once its registration is gone. Get that backwards
    //     and every declared box is reaped the night its window shuts.
    //   * The SESSIONLESS reader is never a corpse either (d6 condition i, and
    //     risk (a′) of the plan) — that cursor is exactly what a woken turn
    //     leaves behind, one per turn, and the rule cannot be allowed to read a
    //     robot's own pickups as dead windows. It is anchored here holding the
    //     same broadcast the corpse holds, so INSTANCE-NESS is the only thing
    //     that separates the two boxes.
    [Fact]
    public async Task ANamedMailboxReadsItsOwnUnicast_AndWhenItDies_TheReaperForwardsAndReapsIt()
    {
        if (!ProcessGroup.Prefix.Pgroup) return;   // xunit 2.x: no dynamic skip

        var clock = new FakeClock();
        using var log = new CapturedLog();
        using var stop = new CancellationTokenSource();
        var handlersPath = WriteReaperHandlers(withNamedDigest: true);
        var daemon = Task.Run(() => DaemonHost.RunAsync(
            _rv.Paths, NoHarnessDir(), stop.Token,
            drainDeadline: TimeSpan.FromSeconds(10), idleWindow: TimeSpan.FromMinutes(10),
            clock: clock.Now, handlersPath: handlersPath, mailDir: _mailDir,
            watchPath: WriteReaperRules(), watchPoll: TimeSpan.FromMilliseconds(50),
            sse: new SseOptions(_trail, Poll: TimeSpan.FromMilliseconds(50))));
        await PollUntilAsync(async () =>
            await ShimClient.TryForwardAsync(_rv.Paths.SocketPath,
                new HookRequest("warmup0", "session-start", "claude-code", "{}"u8.ToArray()))
                is ForwardOutcome.Answered, TimeSpan.FromSeconds(20), "daemon up");

        // --- a named mailbox reads its own unicast --------------------------
        // `builder@retired-box` is a mailbox an operator DECLARED (`--as`), so
        // it is standing that outlives any window — and this envelope can be
        // read by nothing else on the bus.
        await SendAsync("m-box-01", $"{Corpse}@{Box}", "the ci log you asked for");
        var read = await PromptAsync("dead-p0");

        // The delivery is the trail's, not the prompt's: a `mail.deliver` naming
        // the INSTANCE is what says the named box read it, and the columns are
        // the ones `canvas-instances` taught the row to carry.
        await PollUntilAsync(async () => (await TrailRowsAsync("mail.deliver")).Any(
                r => Data(r, "instance") == Box), TimeSpan.FromSeconds(30),
            "the declared mailbox read its own unicast");
        var pickup = Assert.Single(await TrailRowsAsync("mail.deliver"), r => Data(r, "instance") == Box);
        Assert.Equal(Corpse, pickup.GetProperty("data").GetProperty("role").GetString());
        Assert.True(File.Exists(Path.Combine(_mailDir, $"cursor.{Corpse}.{Box}.json")));

        // --- …and an unnamed reader does NOT ---------------------------------
        // The asker's own window is `maintainer` with no `--as`, so a unicast to
        // `maintainer@ci-box` is not its mail, however much of the address
        // matches. This is `plan-unicast`'s predicate at the live seam, and the
        // failure it guards is silent — nothing errors, the mail just lands in
        // the wrong window.
        await SendAsync("m-mis-01", $"{Asker}@ci-box", MisaddressedBody);
        Assert.DoesNotContain(MisaddressedBody, read);
        Assert.DoesNotContain(MisaddressedBody, await PromptAsync("dead-p1"));

        // --- the sessionless reader: the shape a turn leaves ------------------
        // Anchored the way `turn-claude.sh` anchors it — `mail digest --role`
        // with no `--as` and no session, delivering real mail, which is the only
        // thing that writes a cursor (a digest with nothing to hand over hands
        // nothing over and moves nothing). What it leaves is the file a woken
        // turn leaves, one per turn, so what follows can prove the dead-mailbox
        // rule separates the two boxes by instance-ness and by nothing else.
        await SendAsync("m-cast-01", Corpse, StrandedBroadcastBody);
        var (dig, digOut, digErr) = await EngineAsync(
            ["mail", "digest", "--role", Corpse, "--harness", "claude-code", "--seam", "ambient"],
            """{"v":1,"dispatchId":"d0000001","event":{"type":"UserPromptSubmit","payload":{}}}""");
        Assert.True(dig == 0, digErr);
        Assert.Contains("\"effect\":\"inject\"", digOut);
        Assert.True(File.Exists(Path.Combine(_mailDir, $"cursor.{Corpse}..json")));

        // …and the same broadcast is still waiting in the declared box, which is
        // what a `ttlDeliveries` of more than one MEANS: one reader taking it is
        // not every reader having taken it.
        await SendAsync("m-box-02", $"{Corpse}@{Box}", StrandedUnicastBody);

        // The last of the stranded mail, and it lands in BOTH boxes — which is
        // what gives the sessionless reader pending mail of its own for the rule
        // to decline to call a corpse. **Everything the box is holding must be
        // holdable BEFORE the registration goes**: the watcher is fed by a trail
        // TAIL, so the row for an append the test has already awaited may only
        // reach it after the deregistration, and an evaluation carried by that
        // late row would find a complete corpse holding incomplete mail — the
        // reaper would forward what it saw, reap, and this envelope would arrive
        // at a mailbox with no standing left. Deliver the corpse whole.
        await SendAsync("m-cast-02", Corpse, "and one more nobody is reading");

        // Nothing is a corpse yet: a DECLARED mailbox holding mail is a mailbox
        // waiting, which is the whole of d6's condition (ii).
        Assert.DoesNotContain(log.Events, e => e.Evt == "mail.nudge");

        // The window that declared it is gone: its registration leaves
        // `handlers.json`, and the watcher re-reads the file it stat-gates.
        WriteReaperHandlers(withNamedDigest: false);

        // One append to evaluate on — the daemon's watcher is fed by the trail,
        // so a rule that became true while nothing was being written waits for
        // the next row (`MailWatcher`'s feed, not a timer). It is addressed to a
        // role with no cursor and no rule, so it is a POKE and nothing else:
        // whichever evaluation it carries, the corpse it finds is the one the
        // lines above finished building.
        await SendAsync("m-poke-01", "nobody", "a row for the tail to carry");

        // --- the watcher finds the corpse, alone -----------------------------
        await PollUntilAsync(() => Task.FromResult(log.Events.Any(e => e.Evt == "mail.nudge")),
            TimeSpan.FromSeconds(30), "the watcher raised a dead-mailbox nudge");

        // --- the reaper tends it: forward, THEN reap -------------------------
        await PollUntilAsync(async () => (await TrailRowsAsync("mail.reap")).Count > 0,
            TimeSpan.FromSeconds(60), "the shipped reaper payload reaped the corpse");

        var forwards = await TrailRowsAsync("mail.forward");
        var forwarded = forwards.SelectMany(r => Ids(r, "forwardedIds")).ToHashSet();
        Assert.Equal(["m-box-02", "m-cast-01", "m-cast-02"], forwarded.Order());
        var moved = Assert.Single(forwards, r => Ids(r, "forwardedIds").Count > 0);
        Assert.Equal(Corpse, moved.GetProperty("data").GetProperty("role").GetString());
        Assert.Equal(Box, Data(moved, "instance"));
        Assert.Equal(Asker, Data(moved, "to"));
        Assert.Equal(ReaperRole, Data(moved, "by"));       // …and the ledger says who moved it

        var reap = Assert.Single(await TrailRowsAsync("mail.reap"));
        Assert.Equal(Corpse, reap.GetProperty("data").GetProperty("role").GetString());
        Assert.Equal(Box, Data(reap, "instance"));
        Assert.Equal(ReaperRole, Data(reap, "by"));
        Assert.False(File.Exists(Path.Combine(_mailDir, $"cursor.{Corpse}.{Box}.json")));

        // --- what a forward IS ----------------------------------------------
        // A NEW envelope per stranded one, carrying `forwardedFrom` as the only
        // link — and the originals stay exactly where they were, addressed to a
        // mailbox that no longer has standing, still on the append-only chain.
        var store = new MailStore(_mailDir);
        var envelopes = store.Read().Select(l => l.Envelope).OfType<MailEnvelope>().ToList();
        var copies = envelopes.Where(e => e.ForwardedFrom is not null).ToList();
        Assert.Equal(3, copies.Count);
        Assert.All(copies, c => Assert.Equal(Asker, c.To));
        Assert.All(copies, c => Assert.Equal($"{Corpse}@{Box}", c.ForwardedFrom!.Address));
        Assert.All(copies, c => Assert.Equal(ReaperRole, c.From.Agent));
        Assert.Equal($"{Corpse}@{Box}", envelopes.Single(e => e.Id == "m-box-02").To);

        // --- the rescue reaches a live human ---------------------------------
        // The copies are addressed to a role somebody is sitting in, so the
        // asker's very next prompt carries mail that was stranded in a mailbox
        // it could never have read — including the UNICAST, which by
        // construction was readable at exactly one address in the world.
        var rescued = await PromptAsync("dead-p2");
        Assert.Contains(StrandedUnicastBody, rescued);
        Assert.Contains(StrandedBroadcastBody, rescued);

        // --- and nothing else was ever a corpse ------------------------------
        // Every nudge raised named the one box: not the sessionless reader
        // holding the same broadcast (risk (a′) — that cursor is what a woken
        // turn leaves, one per turn), and not `maintainer@ci-box`, which the
        // ledger addresses but no cursor stands for (condition (i): a reap
        // removes standing, and a box with none has none to remove).
        Assert.All(log.Events.Where(e => e.Evt == "mail.nudge"),
            e => Assert.Equal($"{Corpse}@{Box}", e.Fields.Data?["address"]));
        Assert.True(File.Exists(Path.Combine(_mailDir, $"cursor.{Corpse}..json")));
        Assert.Empty(store.VerifyChain());   // the chain still verifies after a rescue

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

    /// The registrations for the corpse chain. Two shapes of one file, because
    /// the whole point is that the second replaces the first: `withNamedDigest`
    /// is a window that DECLARED `builder@retired-box`, and its absence is that
    /// window gone — which is the only difference between a mailbox waiting and
    /// a mailbox stranded (d6 condition ii). The reaper stays registered
    /// throughout; no turn payload is here at all, so nothing in this test can
    /// wake a model.
    private string WriteReaperHandlers(bool withNamedDigest)
    {
        var entries = new List<object>
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
                name = ReaperRole,
                command = Path.Combine(DogfoodInfra.PayloadsDir(), "reaper-deterministic.sh"),
                args = Array.Empty<string>(),
                events = new[] { "mail-nudge" },
                mode = "oneshot",
                failMode = "open",
                budgetMs = 60000,
                env = ReaperEnv(),
            },
        };
        if (withNamedDigest)
            entries.Insert(1, new
            {
                name = $"mail-digest-{Corpse}-{Box}",
                command = DogfoodInfra.EngineBin(),
                args = new[] { "mail", "digest", "--role", Corpse, "--as", Box, "--seam", "ambient" },
                events = new[] { "UserPromptSubmit" },
                mode = "oneshot",
                failMode = "open",
                budgetMs = 30000,
                env = ChildEnv(),
            });

        var path = Path.Combine(_root, "handlers.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { version = 1, handlers = entries }));
        return path;
    }

    /// The disposition an OPERATOR wrote, which is the whole of the reaper's
    /// authority (d6 as settled 2026-08-20): forward everything still pending
    /// to a role somebody is sitting in, then end the standing. There is no
    /// default for this and the payload refuses without it.
    private Dictionary<string, string> ReaperEnv()
    {
        var env = ChildEnv();
        env["REAPER_DISPOSITION"] = "forward-all-to-role";
        env["REAPER_FORWARD_TO"] = Asker;
        return env;
    }

    /// The reaper's own consent (ADR-0017 d7, applied by d6 to the role the
    /// nudge GOES to — never the dead box's role, which is typically human-held
    /// and has no rule at all). `quietFor: 0s` is the same test-not-a-wait the
    /// rule above uses.
    private string WriteReaperRules()
    {
        var rules = new
        {
            version = 1,
            rules = new object[]
            {
                new
                {
                    role = ReaperRole,
                    when = new { priority = ">=urgent", quietFor = "0s" },
                    budget = new { perEnvelope = 1, perRoleHour = 4 },
                },
            },
        };
        var path = Path.Combine(_root, "watch-reaper.json");
        File.WriteAllText(path, JsonSerializer.Serialize(rules));
        return path;
    }

    /// One urgent envelope through the real `mail send`, addressed as given —
    /// a role for a broadcast, `role@instance` for a unicast, which the verb
    /// tells apart itself. No `ttlDeliveries` anywhere: on a unicast the parser
    /// refuses one (d5) and on a role the default is what every other sender
    /// gets.
    private async Task SendAsync(string id, string to, string body)
    {
        var envelope = $$"""
            {"v":1,"id":"{{id}}","from":{"agent":"{{Asker}}","harness":"claude-code","session":"s-asker"},
             "to":"{{to}}","kind":"status","topic":"{{id}}","priority":"urgent","body":"{{body}}"}
            """.Replace("\n", "").Replace("\r", "");
        var (exit, _, stderr) = await EngineAsync(["mail", "send"], envelope);
        Assert.True(exit == 0, stderr);
    }

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
    private async Task<(int Exit, string Stdout, string Stderr)> EngineAsync(string[] args, string stdin)
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
        var stdout = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token);
        return (p.ExitCode, stdout, await stderr);
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

    /// A string column of a trail row's `data` object — where every mail row
    /// puts the mailbox it is about (`role` + `instance`, the write-when-named
    /// pair), never the row's own top level.
    private static string? Data(JsonElement row, string name) =>
        row.TryGetProperty("data", out var d) && d.TryGetProperty(name, out var v) ? v.GetString() : null;

    private static IReadOnlyList<string> Ids(JsonElement row, string name) =>
        row.TryGetProperty("data", out var d) && d.TryGetProperty(name, out var v)
            ? v.EnumerateArray().Select(e => e.GetString()!).ToList()
            : [];

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
