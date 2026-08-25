using System.Text.Json;
using CaptainHook.Mail;
using static CaptainHook.Tests.TestUtil;

namespace CaptainHook.Tests;

// ADR-0017 decision 8, slice `thread-aware-delivery` (roadmap item 22, phase 2,
// thread lane) — the ASKER-SIDE reading of a thread.
//
// `thread-fields` rendered what an envelope says about itself. This slice adds
// what the LEDGER says about the reader: the answer to a question I asked is
// urgent-class in my mailbox whatever its sender declared, and an answer whose
// thread was already closed is marked as the second opinion it is.
//
// THE HAZARD THE PLAN NAMED, and why these tests are shaped as they are: the
// ADR row asked for a "one more turn" memory in the Stop block, verified
// against ADR-0016 N3's Stop-loop guard and the delivered-offsets advance. That
// memory is WITHDRAWN (see the ADR's as-built prose) and everything here is
// re-derived from the append-only ledger on every read, so the two failure
// modes it named cannot arise: nothing this file computes can make a seam
// deliver when the cursor says there is nothing pending (N3 rests entirely on
// cursor-advance), and nothing survives an advance that should not.
// `TheClassChangesSalienceNeverConsumption` is the test that pins the second
// half — an answer promoted to a mid-turn seam is delivered exactly once, like
// any other envelope.
public class MailThreadAwareDeliveryTests
{
    private const string Ts = "2026-08-20T12:00:00Z";

    /// One envelope as a member would write it. A unicast carries no TTL
    /// (ADR-0018 d5), so the helper drops it rather than making every call site
    /// remember.
    private static MailEnvelope Env(
        string id, MailKind kind, string to, string? inReplyTo = null, string? replyTo = null,
        MailPriority priority = MailPriority.Ambient, string agent = "reviewer") =>
        new(id, Ts, new MailSender(agent, "claude-code", "s-9"),
            to, kind, "topic", priority, inReplyTo, Hops: 0, replyTo,
            ForwardedFrom: null, to.Contains('@') ? null : 3, "body", Prev: null);

    private static void Append(MailStoreTempDir tmp, params MailEnvelope[] envelopes)
    {
        foreach (var e in envelopes)
            Assert.IsType<MailAppend.Appended>(tmp.Store().Append(e));
    }

    private static MailThreads Threads(MailStoreTempDir tmp, string role, string? instance) =>
        MailThreads.Of(new MailAddress(role, instance), tmp.Store().Read());

    // ---- the index: whose request was it? ------------------------------------

    /// The join is on `replyTo`, the one field that names a MAILBOX — not on
    /// `from`, which names an agent and a harness and, for a `mail ask` caller,
    /// no window at all.
    [Fact]
    public void AskedByMe_IsTheRequestsWhoseReplyAddressThisMailboxAnswersTo()
    {
        using var tmp = new MailStoreTempDir();
        Append(tmp,
            Env("m-mine", MailKind.Request, to: "ops", replyTo: "main@w1"),
            Env("m-theirs", MailKind.Request, to: "ops", replyTo: "main@w2"),
            Env("m-none", MailKind.Request, to: "ops"));

        var threads = Threads(tmp, "main", "w1");

        Assert.Equal(["m-mine"], threads.AskedByMe.Order());
    }

    /// A BARE-ROLE reply address is accepted by every mailbox of the role, and
    /// that is not a bug to route around — it is exactly as precise as a
    /// broadcast return address can be, and `MailStatus.ReadableMailboxes`
    /// hands `mail ask` a bare role for every UNNAMED window. Two reviewer
    /// windows both read the answer as theirs; naming the mailbox
    /// (`mail digest --as`) is what makes the reading exact, which is
    /// ADR-0018's thesis and the reason this cost is stated rather than hidden.
    [Fact]
    public void ABroadcastReplyAddress_IsEveryHoldersRequest()
    {
        using var tmp = new MailStoreTempDir();
        Append(tmp, Env("m-1", MailKind.Request, to: "ops", replyTo: "main"));

        Assert.Contains("m-1", Threads(tmp, "main", "w1").AskedByMe);
        Assert.Contains("m-1", Threads(tmp, "main", "w2").AskedByMe);
        Assert.DoesNotContain("m-1", Threads(tmp, "other", "w1").AskedByMe);
    }

    /// A unicast reply address reaches ONE mailbox — the predicate is
    /// `MailAddress.Accepts`, the same one routing uses, so a named mailbox
    /// never claims a sibling's thread.
    [Fact]
    public void AUnicastReplyAddress_IsOneMailboxsRequest()
    {
        using var tmp = new MailStoreTempDir();
        Append(tmp, Env("m-1", MailKind.Request, to: "ops", replyTo: "main@w1"));

        Assert.Contains("m-1", Threads(tmp, "main", "w1").AskedByMe);
        Assert.DoesNotContain("m-1", Threads(tmp, "main", "w2").AskedByMe);
        Assert.DoesNotContain("m-1", Threads(tmp, "main", null).AskedByMe);
    }

    /// Only a REQUEST is an ask. A status or an alert carrying a reply address
    /// is telling you where to send comment, not asking you a question, and
    /// promoting its eventual answer to a mid-turn seam would let any sender
    /// buy the strict-budget class by writing two envelopes.
    [Theory]
    [InlineData(MailKind.Status)]
    [InlineData(MailKind.Alert)]
    [InlineData(MailKind.Answer)]
    public void OnlyARequestIsAnAsk(MailKind kind)
    {
        using var tmp = new MailStoreTempDir();
        Append(tmp, Env("m-1", kind, to: "ops", replyTo: "main@w1",
            inReplyTo: kind == MailKind.Answer ? "m-0" : null));

        Assert.Empty(Threads(tmp, "main", "w1").AskedByMe);
    }

    // ---- the index: which answer closed it? ----------------------------------

    /// FIRST wins, in ledger order, and only `kind: answer` closes — a status
    /// quoting a request is a comment on it.
    [Fact]
    public void Closures_FirstAnswerWins_AndOnlyAnAnswerCloses()
    {
        using var tmp = new MailStoreTempDir();
        Append(tmp,
            Env("m-req", MailKind.Request, to: "ops", replyTo: "main"),
            Env("m-chat", MailKind.Status, to: "main", inReplyTo: "m-req"),
            Env("m-first", MailKind.Answer, to: "main", inReplyTo: "m-req"),
            Env("m-second", MailKind.Answer, to: "main", inReplyTo: "m-req"));

        var closures = MailThreads.Closures(tmp.Store().Read());

        Assert.Equal("m-first", closures["m-req"]);
    }

    /// A TORN TAIL IS NOT A THREAD: the bytes of an append in flight may be
    /// anything, and a thread fact derived from them would change the moment
    /// the write completed. Same rule the frontier uses one layer down.
    [Fact]
    public void AnUnterminatedTail_JoinsNothing()
    {
        using var tmp = new MailStoreTempDir();
        Append(tmp, Env("m-req", MailKind.Request, to: "ops", replyTo: "main"));
        var torn = MailStore.Render(
            Env("m-ans", MailKind.Answer, to: "main", inReplyTo: "m-req"), MailStore.Genesis);
        File.AppendAllText(tmp.FilePath, torn);   // no newline: an append in flight

        var lines = tmp.Store().Read();

        Assert.False(lines[^1].Terminated);
        Assert.Empty(MailThreads.Closures(lines));
        Assert.Contains("m-req", MailThreads.Of(new MailAddress("main", "w1"), lines).AskedByMe);
    }

    /// The empty reading answers "no" to everything, which is what makes it
    /// safe as a default: a caller with no ledger in hand plans and renders
    /// exactly as this file did before the slice.
    [Fact]
    public void None_KnowsNothing()
    {
        var answer = Env("m-a", MailKind.Answer, to: "main", inReplyTo: "m-req");

        Assert.False(MailThreads.None.AnswersMe(answer));
        Assert.Null(MailThreads.None.LateAfter(answer));
    }

    // ---- the class: the recipient's consent outranks the sender's assertion --

    private static readonly string[] Inject = ["inject"];

    private static IReadOnlyList<PendingMail> Mixed() =>
    [
        DigestFixtures.Pending(0, Env("m-amb", MailKind.Status, to: "main")),
        DigestFixtures.Pending(100, Env("m-urg", MailKind.Status, to: "main",
            priority: MailPriority.Urgent)),
        DigestFixtures.Pending(200, Env("m-ans", MailKind.Answer, to: "main@w1",
            inReplyTo: "m-req")),
    ];

    private static MailThreads Asked(params string[] ids) =>
        new(new HashSet<string>(ids, StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal));

    /// The mid-turn seam is the strict-budget one, and until now only the
    /// SENDER could qualify mail for it. An answer to my own request qualifies
    /// on my authority — I spent a turn asking — even at `priority: ambient`,
    /// which is exactly what `mail ask` writes by default.
    [Fact]
    public void UrgentSeam_AdmitsAnAmbientAnswerToMyOwnRequest()
    {
        var plan = MailDigest.Plan(Mixed(), MailSeam.Urgent, Inject, Asked("m-req"));

        Assert.Equal(["m-ans", "m-urg"], plan.Deliver.Select(p => p.Envelope.Id));
        Assert.Equal(1, plan.HeldByPlan);
    }

    /// …and only mine. An answer on somebody else's thread is ordinary mail
    /// here, so a sender cannot reach the strict-budget seam by answering a
    /// question I did not ask.
    [Fact]
    public void UrgentSeam_DoesNotAdmitAnAnswerToSomebodyElsesRequest()
    {
        var plan = MailDigest.Plan(Mixed(), MailSeam.Urgent, Inject, Asked("m-other"));

        Assert.Equal(["m-urg"], plan.Deliver.Select(p => p.Envelope.Id));
    }

    /// My answer sorts above `priority: urgent`: the sender's requested class
    /// is its salience, and the recipient's own request outranks it.
    [Fact]
    public void MyAnswer_SortsAboveDeclaredUrgent()
    {
        var plan = MailDigest.Plan(Mixed(), MailSeam.Ambient, Inject, Asked("m-req"));

        Assert.Equal(["m-ans", "m-urg", "m-amb"], plan.Deliver.Select(p => p.Envelope.Id));
    }

    /// WITHOUT the reading, planning is byte-for-byte what it was before this
    /// slice — the property that let every existing planner test stand
    /// unchanged, and the reason the watcher's nudge digest can keep passing
    /// nothing.
    [Fact]
    public void WithoutTheReading_PlanningIsUnchanged()
    {
        var withNothing = MailDigest.Plan(Mixed(), MailSeam.Urgent, Inject);
        var withNone = MailDigest.Plan(Mixed(), MailSeam.Urgent, Inject, MailThreads.None);

        Assert.Equal(["m-urg"], withNothing.Deliver.Select(p => p.Envelope.Id));
        Assert.Equal(
            withNothing.Deliver.Select(p => p.Envelope.Id),
            withNone.Deliver.Select(p => p.Envelope.Id));
    }

    // ---- the rendering -------------------------------------------------------

    private static string Rendered(MailEnvelope e, MailThreads threads)
    {
        var pending = new[] { DigestFixtures.Pending(0, e) };
        var view = DigestFixtures.View(pending);
        var plan = MailDigest.Plan(pending, MailSeam.Ambient, Inject, threads);
        return MailDigest.Render(view, plan, 4096, threads).Text;
    }

    /// A model handed an answer mid-turn has to be able to see WHY it arrived
    /// now, or the seam reads as noise — so the class it was delivered under is
    /// said in the head, where its eye already is.
    [Fact]
    public void MyAnswer_SaysSoInTheHead()
    {
        var answer = Env("m-ans", MailKind.Answer, to: "main", inReplyTo: "m-req");

        Assert.Contains("answers your request m-req", Rendered(answer, Asked("m-req")));
        Assert.Contains("answers m-req", Rendered(answer, Asked("m-other")));
        Assert.DoesNotContain("your request", Rendered(answer, Asked("m-other")));
    }

    /// Late is a fact about the ledger, not a defect: two agents answering one
    /// question is a normal race on a bus with no locking, and the second
    /// answer may be the better one. Nothing is withheld — it is rendered so
    /// the reader knows it is reading a second opinion, and the FIRST answer is
    /// never marked.
    [Fact]
    public void ALateAnswer_NamesTheOneThatClosedTheThread()
    {
        var threads = new MailThreads(
            new HashSet<string>(["m-req"], StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["m-req"] = "m-first" });

        var first = Env("m-first", MailKind.Answer, to: "main", inReplyTo: "m-req");
        var second = Env("m-second", MailKind.Answer, to: "main", inReplyTo: "m-req");

        Assert.DoesNotContain("late", Rendered(first, threads));
        Assert.Contains("late — already answered by m-first", Rendered(second, threads));
        // …and the two readings compose: it is still my thread.
        Assert.Contains("answers your request m-req", Rendered(second, threads));
    }

    /// An envelope with no thread renders no thread clause at all — the head
    /// stays what `thread-fields` pinned for the ordinary case.
    [Fact]
    public void AnEnvelopeWithNoThread_RendersNoThreadClause()
    {
        var text = Rendered(Env("m-1", MailKind.Status, to: "main"), Asked("m-req"));

        Assert.DoesNotContain("answers", text);
        Assert.DoesNotContain("hop ", text);
    }

    // ---- end to end through the verb ----------------------------------------

    private static (int Exit, string Out, string Err) Digest(
        string dir, string stdin, params string[] argv)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = MailDigest.Run(argv, new StringReader(stdin), stdout, stderr,
            mailDir: dir, harnessDir: NoHarnessDir());
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private static JsonElement OneAnswer(string stdout)
    {
        var line = stdout.TrimEnd('\n');
        Assert.DoesNotContain('\n', line);
        return JsonDocument.Parse(line).RootElement.Clone();
    }

    /// The whole slice through the real verb, the real store and the real
    /// cursor: a window asks, the answer comes back addressed to it
    /// (ADR-0018 d4), and a MID-TURN seam hands it over though the answer is
    /// `priority: ambient` — the seam that would otherwise have said noop.
    [Fact]
    public void AnAnswerToThisWindowsOwnRequest_ArrivesAtTheMidTurnSeam()
    {
        using var tmp = new MailStoreTempDir();
        Append(tmp,
            // The window `main@s-1` asked, naming itself as the return address.
            Env("m-req", MailKind.Request, to: "ops", replyTo: "main@s-1", agent: "main"),
            // Ordinary ambient mail, which this seam must still leave alone.
            Env("m-chatter", MailKind.Status, to: "main"),
            // The answer, addressed back and declaring nothing urgent.
            Env("m-ans", MailKind.Answer, to: "main@s-1", inReplyTo: "m-req"));

        var (exit, stdout, _) = Digest(tmp.Dir,
            DigestFixtures.Request(type: "PreToolUse", sessionId: "s-1"),
            "--role", "main", "--seam", "urgent");

        Assert.Equal(0, exit);
        var answer = OneAnswer(stdout);
        Assert.Equal("inject", answer.GetProperty("effect").GetString());
        var text = answer.GetProperty("text").GetString()!;
        Assert.Contains("answers your request m-req", text);
        Assert.DoesNotContain("m-chatter", text);   // the seam's budget still holds
    }

    /// THE CLASS CHANGES SALIENCE, NEVER CONSUMPTION. Promotion buys an earlier
    /// seam and a higher rank and nothing else: the answer is delivered exactly
    /// once, the cursor advances past it like any other envelope, and the next
    /// mid-turn seam is quiet again. This is the half of the plan's verify note
    /// that survives the withdrawal of the "one more turn" memory — anything
    /// that made a delivered answer keep arriving would be the Stop-loop
    /// failure wearing a thread for a hat.
    [Fact]
    public void TheClassChangesSalienceNeverConsumption()
    {
        using var tmp = new MailStoreTempDir();
        Append(tmp,
            Env("m-req", MailKind.Request, to: "ops", replyTo: "main@s-1", agent: "main"),
            Env("m-ans", MailKind.Answer, to: "main@s-1", inReplyTo: "m-req"));

        var stdin = DigestFixtures.Request(type: "PreToolUse", sessionId: "s-1");
        var first = Digest(tmp.Dir, stdin, "--role", "main", "--seam", "urgent");
        var second = Digest(tmp.Dir, stdin, "--role", "main", "--seam", "urgent");

        Assert.Contains("m-ans", OneAnswer(first.Out).GetProperty("text").GetString());
        Assert.Equal("noop", OneAnswer(second.Out).GetProperty("effect").GetString());
    }

    /// The control: the same mailbox, the same seam, mail that answers nothing
    /// it asked. Nothing is delivered and — the cursor's own obligation — the
    /// quiet seam ages nothing.
    [Fact]
    public void AnUnaskedAnswer_LeavesTheMidTurnSeamQuiet()
    {
        using var tmp = new MailStoreTempDir();
        Append(tmp,
            Env("m-req", MailKind.Request, to: "ops", replyTo: "other@s-9"),
            Env("m-ans", MailKind.Answer, to: "main", inReplyTo: "m-req"));

        var (exit, stdout, _) = Digest(tmp.Dir,
            DigestFixtures.Request(type: "PreToolUse", sessionId: "s-1"),
            "--role", "main", "--seam", "urgent");

        Assert.Equal(0, exit);
        Assert.Equal("noop", OneAnswer(stdout).GetProperty("effect").GetString());
        Assert.Equal(0, new MailCursors(tmp.Store()).Pending("main", "s-1").Deliveries);
    }
}
