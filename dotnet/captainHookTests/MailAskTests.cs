using System.Text.Json;
using CaptainHook.Mail;

namespace CaptainHook.Tests;

// ADR-0017 decision 8, slice `mail-ask-wait` (roadmap item 22, phase 2) —
// `captainHook mail ask`, the one synchronous shape on the bus.
//
// Three properties carry the whole slice and each has its family below:
//
//   * WHERE THE ANSWER GOES is never guessed. `replyTo` is spelled or inferred
//     from the caller's own registrations, and ambiguity is a refusal with
//     nothing written — an ask whose answer has nowhere to go is the failure
//     ADR-0018 d4 exists to prevent.
//   * THE DEADLINE IS THE SHORTEST OF THREE and the output says which fired
//     (N4). No test here sleeps: the clock and the sleeper are injected, so a
//     ten-minute cap is exercised in microseconds.
//   * WAITING IS NOT DELIVERING. The poll reads; no cursor moves, so the answer
//     the verb prints is still pending for the asker's own digest.
public class MailAskTests
{
    // ---- arguments -----------------------------------------------------------

    [Fact]
    public void TheRequiredArguments_AreToAndTopic()
    {
        using var w = new AskWorld();
        w.Register();

        var noTo = w.Run(["--topic", "t"]);
        Assert.Equal(1, noTo.Exit);
        Assert.Contains("--to is required", noTo.Err);

        var noTopic = w.Run(["--to", "reviewer"]);
        Assert.Equal(1, noTopic.Exit);
        Assert.Contains("--topic is required", noTopic.Err);

        // Nothing reached the chain on either refusal.
        Assert.False(File.Exists(w.StorePath));
    }

    [Theory]
    [InlineData("--to", "REVIEWER", "must be")]                 // the address grammar does not fold case
    [InlineData("--to", "rev iewer", "must be")]
    [InlineData("--reply-to", "not an address", "must be")]
    [InlineData("--priority", "loud", "--priority must be one of")]
    [InlineData("--wait", "soon", "--wait must be")]
    [InlineData("--wait", "-1", "--wait must be")]
    public void BadArguments_AreRefusedWithTheirReason(string flag, string value, string expected)
    {
        using var w = new AskWorld();
        w.Register();

        var r = w.Run(["--to", "reviewer", "--topic", "t", flag, value]);

        Assert.Equal(1, r.Exit);
        Assert.Contains(expected, r.Err);
        Assert.False(File.Exists(w.StorePath));
    }

    [Fact]
    public void AnUnknownFlag_IsRefusedRatherThanIgnored()
    {
        using var w = new AskWorld();
        w.Register();

        var r = w.Run(["--to", "reviewer", "--topic", "t", "--urgent"]);

        Assert.Equal(1, r.Exit);
        Assert.Contains("unknown argument '--urgent'", r.Err);
    }

    /// The closed set folds case (a casing slip must not silently downgrade an
    /// urgent ask), which is the divergence from the address above: a closed set
    /// can correct a slip, an open universe of mailboxes cannot.
    [Fact]
    public void Priority_MatchesByNameAndFoldsCase()
    {
        using var w = new AskWorld();
        w.Register();

        Assert.Equal(0, w.Run(["--to", "reviewer", "--topic", "t", "--priority", "URGENT"]).Exit);

        Assert.Equal(MailPriority.Urgent, w.Stored().Single().Priority);
    }

    /// The default is the envelope parser's own — deliberately NOT raised. Urgent
    /// buys a mid-turn seam and can trip a `watch.json` rule into spending the
    /// owner's tokens, and this verb must not be a cheaper way to buy that than
    /// `mail send` is.
    [Fact]
    public void PriorityDefaultsToAmbient_SoAnAskIsNotAutomaticallyAnInterruption()
    {
        using var w = new AskWorld();
        w.Register();

        Assert.Equal(0, w.Run(["--to", "reviewer", "--topic", "t"]).Exit);

        Assert.Equal(MailPriority.Ambient, w.Stored().Single().Priority);
    }

    // ---- the envelope --------------------------------------------------------

    [Fact]
    public void AnAsk_IsARequestWithAReturnAddressAndTheBodyFromStdin()
    {
        using var w = new AskWorld();
        w.Register(role: "maintainer");

        var r = w.Run(["--to", "reviewer", "--topic", "hops: store or derive?"], stdin: "which one, and why?\n");

        Assert.Equal(0, r.Exit);
        var e = w.Stored().Single();
        Assert.Equal(MailKind.Request, e.Kind);
        Assert.Equal("reviewer", e.To);
        Assert.Equal("maintainer", e.ReplyTo);       // inferred from this workspace's own registration
        Assert.Equal("hops: store or derive?", e.Topic);
        Assert.Equal("which one, and why?", e.Body);  // the trailing newline is framing, not content
        Assert.Null(e.InReplyTo);                     // an ask is the HEAD of its thread…
        Assert.Equal(0, e.Hops);                      // …so it is at depth 0
        Assert.Equal(MailEnvelope.DefaultTtlDeliveries, e.TtlDeliveries);
        Assert.Empty(new MailStore(w.MailDir).VerifyChain());

        // The id is printed, because correlating a later answer by hand needs it.
        Assert.Contains(e.Id, r.Out);
        Assert.Contains("answer to maintainer", r.Out);
    }

    /// Provenance is LEARNED, never invented: the asker is the mailbox it wants
    /// the answer in, and the harness is what its own digest registration
    /// declares.
    [Fact]
    public void Provenance_ComesFromTheCallersOwnRegistration()
    {
        using var w = new AskWorld();
        w.Register(role: "maintainer", harness: "claude-code");

        Assert.Equal(0, w.Run(["--to", "reviewer", "--topic", "t"]).Exit);

        var from = w.Stored().Single().From;
        Assert.Equal("maintainer", from.Agent);
        Assert.Equal("claude-code", from.Harness);
        // A CLI invocation has no session, and saying so is the write-only
        // member's own shape (ADR-0016 d5) rather than a fudged window name.
        Assert.Null(from.Session);
    }

    /// With no registration to learn from, `cli` is the honest label: this
    /// envelope really was written by a command line.
    [Fact]
    public void WithNoRegistration_TheHarnessIsCli_AndTheReturnAddressMustBeSpelled()
    {
        using var w = new AskWorld();
        w.Policy();   // a policy, but nothing registered

        var refused = w.Run(["--to", "reviewer", "--topic", "t"]);
        Assert.Equal(1, refused.Exit);
        Assert.Contains("nowhere to go", refused.Err);
        Assert.False(File.Exists(w.StorePath));

        Assert.Equal(0, w.Run(["--to", "reviewer", "--topic", "t", "--reply-to", "maintainer@laptop"]).Exit);
        var e = w.Stored().Single();
        Assert.Equal("cli", e.From.Harness);
        Assert.Equal("maintainer@laptop", e.ReplyTo);
    }

    /// Two readable mailboxes and no `--reply-to` is AMBIGUOUS, and the verb
    /// refuses naming both rather than picking. A wrong return address misroutes
    /// answers silently, which is the one failure mode worth a refusal.
    [Fact]
    public void TwoReadableMailboxes_RefuseRatherThanPick()
    {
        using var w = new AskWorld();
        w.Register(name: "mail-a", role: "maintainer");
        w.Register(name: "mail-b", role: "builder", append: true);

        var r = w.Run(["--to", "reviewer", "--topic", "t"]);

        Assert.Equal(1, r.Exit);
        Assert.Contains("builder", r.Err);
        Assert.Contains("maintainer", r.Err);
        Assert.Contains("--reply-to", r.Err);
        Assert.False(File.Exists(w.StorePath));

        // …and spelling it resolves the ambiguity without touching the file.
        Assert.Equal(0, w.Run(["--to", "reviewer", "--topic", "t", "--reply-to", "builder"]).Exit);
        Assert.Equal("builder", w.Stored().Single().ReplyTo);
    }

    /// A unicast ask has no TTL at all (ADR-0018 d5) — one addressee, so
    /// delivered is a fact rather than a matter of opportunities. The store
    /// re-parses what it renders, so this is also proof the verb cannot compose
    /// an envelope the parser would refuse.
    [Fact]
    public void AUnicastAsk_CarriesNoTtl()
    {
        using var w = new AskWorld();
        w.Register(role: "maintainer");

        Assert.Equal(0, w.Run(["--to", "reviewer@ci", "--topic", "t"]).Exit);

        Assert.Null(w.Stored().Single().TtlDeliveries);
        Assert.DoesNotContain("ttlDeliveries", File.ReadAllText(w.StorePath));
    }

    // ---- the wait ------------------------------------------------------------

    [Fact]
    public void WithNoWait_TheVerbAppendsAndReturns()
    {
        using var w = new AskWorld();
        w.Register(role: "maintainer");

        var r = w.Run(["--to", "reviewer", "--topic", "t"]);

        Assert.Equal(0, r.Exit);
        Assert.DoesNotContain("waiting up to", r.Out);
        Assert.DoesNotContain("unanswered", r.Out);
        Assert.Equal(0, w.Naps);   // nothing slept: there was nothing to wait for
    }

    /// The happy path: an answer that lands mid-wait is printed, with the
    /// digest's own head shape so provenance reads the same on every surface.
    [Fact]
    public void AnAnswerThatLandsMidWait_IsPrinted()
    {
        using var w = new AskWorld();
        w.Register(role: "maintainer");
        // The third nap is when the reviewer answers — a fake clock, so this is
        // "1.5 seconds later" without anybody waiting 1.5 seconds.
        w.AnswerOnNap(3, id: "ans-1", to: "maintainer", body: "store it.");

        var r = w.Run(["--to", "reviewer", "--topic", "hops", "--wait", "60", "--id", "ask-1"]);

        Assert.Equal(0, r.Exit);
        Assert.Contains("answer from", r.Out);
        Assert.Contains("id ans-1", r.Out);
        Assert.Contains("hop 2/6", r.Out);        // the ordinal against d8's budget
        Assert.Contains("   store it.", r.Out);   // the body, indented under its head
        Assert.DoesNotContain("unanswered", r.Out);
        Assert.Equal(3, w.Naps);                  // it stopped as soon as the answer was there
    }

    /// WAITING IS NOT DELIVERING: the poll reads the ledger and moves nothing,
    /// so the answer this verb printed is STILL pending for the asker's own
    /// digest. Consuming here would be a write from a read-shaped verb — and if
    /// the wait were interrupted a millisecond later the answer would be gone.
    [Fact]
    public void APrintedAnswer_IsStillPendingForTheAskersOwnDigest()
    {
        using var w = new AskWorld();
        w.Register(role: "maintainer");
        w.AnswerOnNap(1, id: "ans-1", to: "maintainer", body: "yes");

        Assert.Equal(0, w.Run(["--to", "reviewer", "--topic", "t", "--wait", "30", "--id", "ask-1"]).Exit);

        // No cursor file exists at all — the verb created none.
        Assert.Empty(MailCursors.List(w.MailDir));
        var pending = new MailCursors(new MailStore(w.MailDir)).Pending("maintainer", "s-1");
        Assert.Contains(pending.Pending, p => p.Envelope.Id == "ans-1");
    }

    /// Only `kind: answer` closes (d8's rule, the same one the read model's
    /// index uses): a status quoting the request is a comment on it, and a wait
    /// that ended on a comment would report the wrong thing as the answer.
    [Fact]
    public void AStatusQuotingTheRequest_DoesNotEndTheWait()
    {
        using var w = new AskWorld();
        w.Register(role: "maintainer");
        w.ReplyOnNap(1, id: "note-1", to: "maintainer", kind: MailKind.Status, body: "looking");

        var r = w.Run(["--to", "reviewer", "--topic", "t", "--wait", "2", "--id", "ask-1"]);

        Assert.Contains("unanswered after 2s", r.Out);
        Assert.DoesNotContain("note-1", r.Out);
    }

    /// An expired wait is NOT an error exit: the request was written and is on
    /// the chain, so an agent chaining on `&&` should carry on — and the line
    /// says where the answer will turn up instead.
    [Fact]
    public void AnExpiredWait_SaysWhichBoundFired_AndExitsZero()
    {
        using var w = new AskWorld();
        w.Register(role: "maintainer");

        var r = w.Run(["--to", "reviewer", "--topic", "t", "--wait", "5", "--id", "ask-1"]);

        Assert.Equal(0, r.Exit);
        Assert.Contains("unanswered after 5s (your --wait)", r.Out);
        Assert.Contains("ask-1 is on the bus", r.Out);
        Assert.Contains("maintainer", r.Out);
        // 5s at a 500ms poll: ten naps and not one more.
        Assert.Equal(10, w.Naps);
    }

    /// N4, the whole point: the SHORTEST of three bounds wins and the output
    /// names it, because "unanswered" alone leaves an agent unable to tell
    /// "nobody answered in a minute" from "your harness cut me off".
    [Theory]
    [InlineData(30, null, 30, "your --wait")]
    [InlineData(30, "10", 10, "tool timeout")]
    [InlineData(10, "30", 10, "your --wait")]
    [InlineData(900, null, MailAsk.MaxWaitSeconds, "this verb's cap")]
    [InlineData(900, "1200", MailAsk.MaxWaitSeconds, "this verb's cap")]
    [InlineData(30, "0", 30, "your --wait")]        // a nonsense timeout is ignored…
    [InlineData(30, "soon", 30, "your --wait")]     // …and so is a typo'd one
    public void TheDeadlineIsTheShortestBound_AndTheOutputNamesIt(
        int wait, string? toolTimeout, int expectedSeconds, string expectedReason)
    {
        using var w = new AskWorld();
        w.Register(role: "maintainer");

        var r = w.Run(["--to", "reviewer", "--topic", "t", "--wait", wait.ToString(), "--id", "ask-1"],
            toolTimeout: toolTimeout);

        Assert.Equal(0, r.Exit);
        Assert.Contains($"unanswered after {expectedSeconds}s", r.Out);
        Assert.Contains(expectedReason, r.Out);
        Assert.Contains($"waiting up to {expectedSeconds}s", r.Out);
    }

    /// The deadline is checked FIRST, so `--wait 0` reads exactly once: an
    /// answer that landed between the append and the poll is still found, and
    /// nothing sleeps.
    [Fact]
    public void AZeroWait_ReadsOnceAndSleepsNever()
    {
        using var w = new AskWorld();
        w.Register(role: "maintainer");

        var empty = w.Run(["--to", "reviewer", "--topic", "t", "--wait", "0", "--id", "ask-1"]);
        Assert.Contains("unanswered after 0s", empty.Out);
        Assert.Equal(0, w.Naps);

        // Now one is already on the chain when a second ask waits zero seconds.
        w.Answer("ans-2", to: "maintainer", inReplyTo: "ask-2", body: "already here");
        var found = w.Run(["--to", "reviewer", "--topic", "t", "--wait", "0", "--id", "ask-2"]);
        Assert.Contains("id ans-2", found.Out);
        Assert.Equal(0, w.Naps);
    }

    /// The last nap is the REMAINDER, never a full poll interval past the
    /// deadline: the verb returns when it said it would.
    [Fact]
    public void TheLastNapIsTheRemainder_NotAFullPollPastTheDeadline()
    {
        using var w = new AskWorld();
        w.Register(role: "maintainer");

        w.Run(["--to", "reviewer", "--topic", "t", "--wait", "2", "--id", "ask-1"]);

        Assert.Equal(2000, w.SleptMs);
        Assert.All(w.NapLengths, ms => Assert.True(ms <= MailAsk.PollMs, $"napped {ms}ms"));
    }

    // ---- the bound table, alone ----------------------------------------------

    [Fact]
    public void NoWait_IsNoDeadlineAtAll()
    {
        Assert.Equal((null, MailAskBound.Wait), MailAsk.Deadline(null, "30"));
        Assert.Equal((0, MailAskBound.Wait), MailAsk.Deadline(0, null));
    }

    /// A world with its own HOME-shaped tree — a mail store, a handlers.json and
    /// a dispatch.json, all passed explicitly, so nothing here can reach the
    /// operator's `~/.captainHook` (CLAUDE.md's pollution rule). The clock is
    /// fake and the sleeper only advances it: no test in this file waits.
    private sealed class AskWorld : IDisposable
    {
        private readonly List<object> _handlers = [];
        private readonly List<(int Nap, MailEnvelope Envelope)> _scheduled = [];
        private long _nowMs;

        public string Home { get; } = Path.Combine(Path.GetTempPath(), "chk-ask-" + Guid.NewGuid().ToString("N")[..8]);
        public string MailDir => Path.Combine(Home, "mail");
        public string StorePath => Path.Combine(MailDir, "mail.jsonl");
        private string HandlersPath => Path.Combine(Home, "handlers.json");
        private string PolicyPath => Path.Combine(Home, "dispatch.json");

        /// How many times the verb napped, and for how long — the wait's shape
        /// without a millisecond of real time.
        public List<int> NapLengths { get; } = [];
        public int Naps => NapLengths.Count;
        public int SleptMs => NapLengths.Sum();

        public AskWorld() => Directory.CreateDirectory(Home);

        public void Register(
            string name = "mail-digest", string role = "maintainer", string harness = "claude-code",
            bool append = false)
        {
            if (!append) _handlers.Clear();
            _handlers.Add(new
            {
                name,
                command = "/usr/bin/captainHook",
                args = new[] { "mail", "digest", "--role", role, "--harness", harness, "--seam", "ambient" },
                events = new[] { "user-prompt-submit" },
                mode = "oneshot",
                failMode = "open",
            });
            File.WriteAllText(HandlersPath,
                JsonSerializer.Serialize(new { version = 1, handlers = _handlers }));
            Policy();
        }

        public void Policy() =>
            File.WriteAllText(PolicyPath,
                JsonSerializer.Serialize(new { version = 1, @default = "allow", rules = Array.Empty<object>() }));

        /// Put an answer on the chain right now.
        public void Answer(string id, string to, string inReplyTo, string body) =>
            MailFixtures.AppendOk(new MailStore(MailDir), Reply(id, to, inReplyTo, MailKind.Answer, body));

        /// …or on the Nth nap, which is how "the reviewer answered while we were
        /// waiting" is spelled without a thread or a sleep.
        public void AnswerOnNap(int nap, string id, string to, string body, string inReplyTo = "ask-1") =>
            ReplyOnNap(nap, id, to, MailKind.Answer, body, inReplyTo);

        public void ReplyOnNap(
            int nap, string id, string to, MailKind kind, string body, string inReplyTo = "ask-1") =>
            _scheduled.Add((nap, Reply(id, to, inReplyTo, kind, body)));

        private static MailEnvelope Reply(string id, string to, string inReplyTo, MailKind kind, string body) =>
            MailFixtures.Envelope(id: id, to: to, body: body, inReplyTo: inReplyTo, hops: 1) with { Kind = kind };

        public (int Exit, string Out, string Err) Run(
            string[] argv, string? stdin = null, string? toolTimeout = null)
        {
            NapLengths.Clear();
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exit = MailAsk.Run(argv, new StringReader(stdin ?? "the body"), stdout, stderr,
                mailDir: MailDir, handlersPath: HandlersPath, policyPath: PolicyPath, cwd: Home,
                nowMs: () => _nowMs,
                sleep: ms =>
                {
                    NapLengths.Add(ms);
                    _nowMs += ms;
                    foreach (var (nap, envelope) in _scheduled.Where(x => x.Nap == NapLengths.Count))
                        MailFixtures.AppendOk(new MailStore(MailDir), envelope);
                },
                newId: () => "ask-fixed", nowTs: "2026-08-19T12:00:00Z", toolTimeoutS: toolTimeout);
            return (exit, stdout.ToString(), stderr.ToString());
        }

        public IReadOnlyList<MailEnvelope> Stored() =>
            [.. new MailStore(MailDir).Read().Where(l => l.Envelope is not null).Select(l => l.Envelope!)];

        public void Dispose()
        {
            try { Directory.Delete(Home, recursive: true); } catch { /* best-effort */ }
        }
    }
}
