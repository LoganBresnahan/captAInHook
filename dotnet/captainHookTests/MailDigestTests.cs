using System.Text;
using System.Text.Json;
using CaptainHook.Core;
using CaptainHook.Handlers;
using CaptainHook.Mail;
using CaptainHook.Wire;
using static CaptainHook.Tests.TestUtil;

namespace CaptainHook.Tests;

// ADR-0016 decision 5/7/10 — `captainHook mail digest`, the read path and the
// bus's semantic core (roadmap item 20, phase 4). Three layers, tested in
// order of purity: the PLANNER (priority × seam class × declared verbs →
// deliver|hold|degrade, downward only), the RENDERER (deterministic, bounded,
// provenance on every line — pinned as golden text), and the VERB (exec-wire
// in, exec-wire out, cursor advanced BEFORE the answer is emitted). The
// direction of failure under test throughout: mail must never be advanced
// past without having been rendered into the effect (lost silently), and
// never rendered without the advance (double-injected — the Stop-loop
// hazard).

static class DigestFixtures
{
    public static MailEnvelope Env(
        string id, MailPriority priority = MailPriority.Ambient, string body = "opaque prose",
        MailKind kind = MailKind.Status, string topic = "build", int ttl = 3,
        string agent = "intent-watcher", string to = "main") =>
        new(id, "2026-08-12T19:00:00Z", new MailSender(agent, "claude-code", "s-77"),
            to, kind, topic, priority, InReplyTo: null, ttl, body, Prev: null);

    public static PendingMail Pending(long offset, MailEnvelope e, long? seenAt = null) =>
        new(offset, e, seenAt);

    public static MailPendingView View(
        IReadOnlyList<PendingMail> pending,
        IReadOnlyList<PendingMail>? expired = null,
        long deliveries = 0, string role = "main") =>
        new(role, "s-1", Gen: 1, Head: null, Frontier: 0, deliveries,
            LastDeliveredId: null, Reanchored: false, ReanchorReason: null,
            pending, expired ?? [], SkippedMalformed: 0);

    /// One exec-wire envelope line, exactly as ExecWire.Envelope writes it.
    public static string Request(
        string dispatchId = "d-1", string type = "UserPromptSubmit", string? sessionId = "s-1") =>
        ExecWire.Envelope(
            new HookEvent(type, sessionId, Cwd: null, JsonDocument.Parse("{}").RootElement),
            dispatchId);
}

/// The pure planner: d5's matrix, driven cell by cell. Degradation is
/// DOWNWARD only, and "nothing deliverable" must always mean Vehicle.None —
/// the verb layer reads that as "do not advance".
public class MailDigestPlannerTests
{
    private static readonly string[] Inject = ["inject"];
    private static readonly string[] Decide = ["decide"];
    private static readonly string[] Both = ["decide", "inject"];

    private static IReadOnlyList<PendingMail> OneOfEach() =>
    [
        DigestFixtures.Pending(0, DigestFixtures.Env("m-amb", MailPriority.Ambient)),
        DigestFixtures.Pending(100, DigestFixtures.Env("m-rec", MailPriority.Reconcile)),
        DigestFixtures.Pending(200, DigestFixtures.Env("m-urg", MailPriority.Urgent)),
    ];

    /// An ambient-class seam delivers EVERYTHING — the cursor's planner
    /// obligation: once this seam advances, every held envelope ages, so
    /// holding at an advancing seam only burns TTL.
    [Fact]
    public void AmbientSeam_DeliversAllPriorities_OrderedByPriorityThenOffset()
    {
        var plan = MailDigest.Plan(OneOfEach(), MailSeam.Ambient, Inject);

        Assert.Equal(MailVehicle.Inject, plan.Vehicle);
        Assert.Equal(["m-urg", "m-rec", "m-amb"], plan.Deliver.Select(p => p.Envelope.Id));
        Assert.Equal(0, plan.HeldByPlan);
    }

    /// The mid-turn class fires on every tool call: ONLY urgent mail
    /// qualifies (d5's strict-budget discipline), and everything else is
    /// counted held.
    [Fact]
    public void UrgentSeam_DeliversOnlyUrgent_CountsTheRestHeld()
    {
        var plan = MailDigest.Plan(OneOfEach(), MailSeam.Urgent, Inject);

        Assert.Equal(MailVehicle.Inject, plan.Vehicle);
        Assert.Equal(["m-urg"], plan.Deliver.Select(p => p.Envelope.Id));
        Assert.Equal(2, plan.HeldByPlan);
    }

    /// A quiet mid-turn seam — nothing urgent pending — must plan NOTHING:
    /// Vehicle.None so the verb never advances, and held mail ages by zero.
    [Fact]
    public void UrgentSeam_NothingUrgent_PlansNone()
    {
        var pending = new[]
        {
            DigestFixtures.Pending(0, DigestFixtures.Env("m-amb", MailPriority.Ambient)),
        };

        var plan = MailDigest.Plan(pending, MailSeam.Urgent, Inject);

        Assert.Equal(MailVehicle.None, plan.Vehicle);
        Assert.Empty(plan.Deliver);
    }

    /// Turn end delivers everything — and INJECT is preferred even here:
    /// mail must never escalate itself into a deny when a plain inject can
    /// carry it (the skeptic pass's finding 3 — a `--seam reconcile` typo on
    /// a decide+inject mid-turn event must not deny a tool call).
    [Fact]
    public void ReconcileSeam_DeliversAll_InjectPreferredOverDecide()
    {
        var plan = MailDigest.Plan(OneOfEach(), MailSeam.Reconcile, Both);

        Assert.Equal(MailVehicle.Inject, plan.Vehicle);
        Assert.Equal(3, plan.Deliver.Count);
    }

    /// The block shape is reserved for events whose ONLY loop verb is decide
    /// (Stop's shape once phase 5 declares it): the reason carries the digest.
    [Fact]
    public void ReconcileSeam_DecideOnly_UsesTheBlock()
    {
        var plan = MailDigest.Plan(OneOfEach(), MailSeam.Reconcile, Decide);
        Assert.Equal(MailVehicle.Decide, plan.Vehicle);
    }

    /// d5: "a harness declaring only turn-start inject gets exactly that".
    [Fact]
    public void ReconcileSeam_InjectOnly_DegradesToInject()
    {
        var plan = MailDigest.Plan(OneOfEach(), MailSeam.Reconcile, Inject);
        Assert.Equal(MailVehicle.Inject, plan.Vehicle);
    }

    /// Never upward: ambient/urgent content rides inject and NOTHING else. A
    /// seam whose event declares only decide delivers nothing — mail must not
    /// escalate itself into a deny to get read.
    [Theory]
    [InlineData(MailSeam.Ambient)]
    [InlineData(MailSeam.Urgent)]
    public void InjectlessSeam_NeverDeliversViaDecide(MailSeam seam)
    {
        var plan = MailDigest.Plan(OneOfEach(), seam, Decide);
        Assert.Equal(MailVehicle.None, plan.Vehicle);
        Assert.Empty(plan.Deliver);
    }

    /// An event the spec declares with NO effects (claude-code's Stop today)
    /// and an event the spec does not declare at all both deliver nothing.
    /// The capability gate would let an inject through PERMISSIVELY on an
    /// undeclared event — but the gate noops after the cursor has advanced,
    /// and that direction is mail lost silently, so the planner is stricter.
    [Theory]
    [InlineData(true)]    // declared with no effects (claude-code's Stop today)
    [InlineData(false)]   // not declared at all
    public void UndeclaredOrEffectlessEvent_PlansNone(bool declaredEmpty)
    {
        var plan = MailDigest.Plan(OneOfEach(), MailSeam.Ambient, declaredEmpty ? [] : null);
        Assert.Equal(MailVehicle.None, plan.Vehicle);
    }

    /// Ties within a priority class read in ARRIVAL order (offset ascending):
    /// held items sit before the frontier by construction, so
    /// closest-to-expiry surfaces first with no extra rule.
    [Fact]
    public void WithinAPriority_ArrivalOrderWins()
    {
        var pending = new[]
        {
            DigestFixtures.Pending(300, DigestFixtures.Env("m-new", MailPriority.Ambient)),
            DigestFixtures.Pending(10, DigestFixtures.Env("m-old", MailPriority.Ambient, ttl: 3), seenAt: 1),
        };

        var plan = MailDigest.Plan(pending, MailSeam.Ambient, Inject);

        Assert.Equal(["m-old", "m-new"], plan.Deliver.Select(p => p.Envelope.Id));
    }
}

/// The renderer: deterministic bounded text, pinned as GOLDEN — the digest is
/// what a recipient model actually reads, so its exact shape is contract, not
/// cosmetics. Provenance on every item (d10); age in delivery opportunities,
/// never a wall clock (d3); whole-item cap with the one anti-deadlock
/// exception (d5: truncate, never summarize).
public class MailDigestRenderTests
{
    [Fact]
    public void GoldenDigest_TwoItems_ProvenanceOrderAndAge()
    {
        var pending = new[]
        {
            DigestFixtures.Pending(0, DigestFixtures.Env(
                "m-1", MailPriority.Urgent, "both agents are editing deploy.sh",
                MailKind.Alert, "deploy-conflict", agent: "write-log")),
            DigestFixtures.Pending(120, DigestFixtures.Env(
                "m-2", MailPriority.Ambient, "suite green twice"), seenAt: 2),
        };
        var view = DigestFixtures.View(pending, deliveries: 3);
        var plan = MailDigest.Plan(pending, MailSeam.Ambient, ["inject"]);

        var render = MailDigest.Render(view, plan, maxChars: 4096);

        Assert.Equal(
            "[captAInHook mail] 2 message(s) for 'main':\n"
            + "\n"
            + "1. from write-log (claude-code) · alert/urgent · id m-1 · topic: deploy-conflict · new\n"
            + "   both agents are editing deploy.sh\n"
            + "\n"
            + "2. from intent-watcher (claude-code) · status/ambient · id m-2 · topic: build · waited 2 opportunities\n"
            + "   suite green twice",
            render.Text);
        Assert.Equal(2, render.Delivered.Count);
        Assert.Equal(0, render.HeldByCap);
    }

    [Fact]
    public void EmptyBody_RendersTheProvenanceLineAlone()
    {
        var pending = new[] { DigestFixtures.Pending(0, DigestFixtures.Env("m-1", body: "")) };
        var plan = MailDigest.Plan(pending, MailSeam.Ambient, ["inject"]);

        var render = MailDigest.Render(DigestFixtures.View(pending), plan, 4096);

        Assert.EndsWith("id m-1 · topic: build · new", render.Text);
    }

    /// WHOLE-ITEM cap: an item the cap excludes is NOT delivered — it stays
    /// pending for the next seam (converging FIFO), and the trailer says so.
    [Fact]
    public void Cap_DropsWholeItems_TheTailStaysUndelivered()
    {
        var pending = new[]
        {
            DigestFixtures.Pending(0, DigestFixtures.Env("m-1", body: new string('a', 100))),
            DigestFixtures.Pending(200, DigestFixtures.Env("m-2", body: new string('b', 100))),
            DigestFixtures.Pending(400, DigestFixtures.Env("m-3", body: new string('c', 100))),
        };
        var plan = MailDigest.Plan(pending, MailSeam.Ambient, ["inject"]);

        var render = MailDigest.Render(DigestFixtures.View(pending), plan, maxChars: 200);

        Assert.Equal(["m-1"], render.Delivered.Select(p => p.Envelope.Id));
        Assert.Equal(2, render.HeldByCap);
        Assert.Contains("(+2 more message(s) pending, held for a later delivery opportunity)", render.Text);
        Assert.DoesNotContain("bbbb", render.Text);
    }

    /// The anti-deadlock exception: a FIRST item too big to ever fit is
    /// delivered truncated with an explicit marker — held-forever is mail
    /// lost, and the full text stays durable in the store. Only the BODY is
    /// cut: the provenance head (sender, id) survives whole, or the marker's
    /// "full text is durable in the store" names a line nobody can find.
    [Fact]
    public void Cap_SingleOversizedItem_BodyTruncated_ProvenanceWhole()
    {
        var pending = new[]
        {
            DigestFixtures.Pending(0, DigestFixtures.Env("m-big", body: new string('x', 5000))),
        };
        var plan = MailDigest.Plan(pending, MailSeam.Ambient, ["inject"]);

        var render = MailDigest.Render(DigestFixtures.View(pending), plan, maxChars: 500);

        Assert.Equal(["m-big"], render.Delivered.Select(p => p.Envelope.Id));
        Assert.Contains("· id m-big ·", render.Text);
        Assert.Contains("[truncated to fit the delivery budget", render.Text);
        // The item block is bounded by the cap (+ the marker constant); the
        // 5000-char body must not have survived whole.
        Assert.DoesNotContain(new string('x', 501), render.Text);
    }

    /// A sender-controlled TOPIC longer than the whole cap must not eat the
    /// digest (the skeptic pass's finding 1): head fields are display-clamped,
    /// the head renders whole — id, sender, and a body remnant all present.
    [Fact]
    public void Cap_HugeTopic_HeadClampedIdAndBodySurvive()
    {
        var pending = new[]
        {
            DigestFixtures.Pending(0, DigestFixtures.Env(
                "m-1", body: "tiny", topic: new string('t', 5000))),
        };
        var plan = MailDigest.Plan(pending, MailSeam.Ambient, ["inject"]);

        var render = MailDigest.Render(DigestFixtures.View(pending), plan, maxChars: 1024);

        Assert.Contains("· id m-1 ·", render.Text);
        Assert.Contains("tiny", render.Text);              // the body was never the problem
        Assert.DoesNotContain(new string('t', 200), render.Text);   // topic clamped
        Assert.True(render.Text.Length < 2048, $"render ran to {render.Text.Length} chars");
    }

    /// A cap below even the marker cannot erase provenance: the head always
    /// renders whole, so the worst case is head + marker — bounded constant
    /// overage, never a content-free digest.
    [Fact]
    public void Cap_PathologicallySmall_ProvenanceLineStillWhole()
    {
        var pending = new[]
        {
            DigestFixtures.Pending(0, DigestFixtures.Env("m-1", body: "does not fit")),
        };
        var plan = MailDigest.Plan(pending, MailSeam.Ambient, ["inject"]);

        var render = MailDigest.Render(DigestFixtures.View(pending), plan, maxChars: 10);

        Assert.Contains("from intent-watcher (claude-code)", render.Text);
        Assert.Contains("· id m-1 ·", render.Text);
        Assert.Contains("[truncated to fit the delivery budget", render.Text);
        Assert.DoesNotContain("does not fit", render.Text);
    }

    /// The expired parenthetical is OUTSIDE the item cap, so it must be
    /// bounded by construction: a count plus at most three display-clamped
    /// ids — a 50KB sender-controlled id cannot ride expiry into a mid-turn
    /// answer (the skeptic pass's finding 2).
    [Fact]
    public void ExpiredList_IsClampedToACountAndThreeIds()
    {
        var pending = new[] { DigestFixtures.Pending(0, DigestFixtures.Env("m-live")) };
        var expired = Enumerable.Range(1, 5).Select(i =>
            DigestFixtures.Pending(50 + i, DigestFixtures.Env(
                i == 1 ? "m-" + new string('z', 50_000) : $"m-dead{i}", ttl: 1), seenAt: 1))
            .ToList();
        var plan = MailDigest.Plan(pending, MailSeam.Ambient, ["inject"]);

        var render = MailDigest.Render(
            DigestFixtures.View(pending, expired, deliveries: 1), plan, 4096);

        Assert.Contains("(5 expired undelivered", render.Text);
        Assert.Contains(", …)", render.Text);              // more than three: elided
        Assert.DoesNotContain("m-dead4", render.Text);     // only the first three named
        Assert.True(render.Text.Length < 4096 + 1024, $"render ran to {render.Text.Length} chars");
    }

    /// d4: expired mail is reported ONCE, in the digest that accompanies the
    /// advance that drops it — visibility without content.
    [Fact]
    public void ExpiredMail_IsNamedInTheDigest()
    {
        var pending = new[] { DigestFixtures.Pending(0, DigestFixtures.Env("m-live")) };
        var expired = new[]
        {
            DigestFixtures.Pending(50, DigestFixtures.Env("m-dead", ttl: 1), seenAt: 1),
        };
        var plan = MailDigest.Plan(pending, MailSeam.Ambient, ["inject"]);

        var render = MailDigest.Render(
            DigestFixtures.View(pending, expired, deliveries: 1), plan, 4096);

        Assert.Contains("expired undelivered", render.Text);
        Assert.Contains("m-dead", render.Text);
    }
}

/// The exec-wire stdin envelope's strict parse: the only writer is
/// ExecWire.Envelope, so anything it would not write is malformed — never
/// guessed at.
public class MailDigestRequestTests
{
    [Fact]
    public void TheEncodersOwnOutput_Parses()
    {
        var req = MailDigest.TryParseRequest(DigestFixtures.Request(), out var errors);

        Assert.NotNull(req);
        Assert.Empty(errors);
        Assert.Equal(new DigestRequest("d-1", "UserPromptSubmit", "s-1"), req);
    }

    /// A collapsed run without an id writes `dispatchId:""` — legal, echoed
    /// back verbatim. An absent sessionId is the sessionless cursor.
    [Fact]
    public void EmptyDispatchId_AndAbsentSessionId_AreLegal()
    {
        var req = MailDigest.TryParseRequest(
            DigestFixtures.Request(dispatchId: "", sessionId: null), out _);

        Assert.Equal(new DigestRequest("", "UserPromptSubmit", null), req);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"v":2,"dispatchId":"d","event":{"type":"Stop"}}""")]
    [InlineData("""{"v":1,"dispatchId":"d"}""")]
    [InlineData("""{"v":1,"dispatchId":"d","event":{"type":""}}""")]
    [InlineData("""{"v":1,"dispatchId":"d","event":{"type":"Stop"},"extra":1}""")]
    [InlineData("""{"v":1,"dispatchId":"d","dispatchId":"e","event":{"type":"Stop"}}""")]
    public void AnythingTheEncoderWouldNotWrite_IsMalformed(string line)
    {
        Assert.Null(MailDigest.TryParseRequest(line, out var errors));
        Assert.NotEmpty(errors);
    }
}

/// The verb end to end through injected streams (the MailSend precedent):
/// real store, real cursor, real harness registry — and the order that IS the
/// contract: advance before emit, noop means the cursor did not move.
public class MailDigestRunTests
{
    private static (int Exit, string Out, string Err) Run(
        string dir, string stdin, string[]? argv = null, string? harnessDir = null) =>
        RunArgs(dir, stdin, argv ?? ["--role", "main"], harnessDir);

    private static (int Exit, string Out, string Err) RunArgs(
        string dir, string stdin, string[] argv, string? harnessDir = null)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = MailDigest.Run(argv, new StringReader(stdin), stdout, stderr,
            mailDir: dir, harnessDir: harnessDir ?? NoHarnessDir());
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private static void Send(MailStoreTempDir tmp, MailEnvelope e) =>
        Assert.IsType<MailAppend.Appended>(tmp.Store().Append(e));

    private static JsonElement Answer(string stdoutText)
    {
        var line = stdoutText.TrimEnd('\n');
        Assert.DoesNotContain('\n', line);   // one answer, one line
        return JsonDocument.Parse(line).RootElement.Clone();
    }

    // ---- registration errors: loud, nothing on stdout ---------------------

    [Theory]
    [InlineData("")]                                  // --role missing
    [InlineData("--role main --wat")]                 // unknown flag
    [InlineData("--role main --seam soon")]           // not a seam class
    [InlineData("--role main --seam 1")]              // numeric spelling refused
    [InlineData("--role main --seam ambient,urgent")] // Enum.TryParse comma-list refused
    [InlineData("--role main --max-chars 0")]
    [InlineData("--role")]                            // flag without value
    public void BadRegistrationArgs_Exit1_NothingOnStdout(string argvLine)
    {
        using var tmp = new MailStoreTempDir();
        var argv = argvLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var (exit, stdout, stderr) = RunArgs(tmp.Dir, DigestFixtures.Request(), argv);

        Assert.Equal(1, exit);
        Assert.Equal("", stdout);
        Assert.Contains("usage", stderr);
    }

    [Fact]
    public void UnknownHarness_Exit1_NamesTheKnownOnes()
    {
        using var tmp = new MailStoreTempDir();
        var (exit, stdout, stderr) = RunArgs(tmp.Dir, DigestFixtures.Request(),
            ["--role", "main", "--harness", "nope"]);

        Assert.Equal(1, exit);
        Assert.Equal("", stdout);
        Assert.Contains("unknown harness", stderr);
    }

    // ---- stdin failures: noop answer, cursor untouched --------------------

    [Fact]
    public void MalformedStdin_AnswersNoop_NothingAdvances()
    {
        using var tmp = new MailStoreTempDir();
        Send(tmp, DigestFixtures.Env("m-1"));

        var (exit, stdout, stderr) = Run(tmp.Dir, "not an envelope");

        Assert.Equal(1, exit);
        Assert.Equal("noop", Answer(stdout).GetProperty("effect").GetString());
        Assert.Contains("malformed", stderr);
        Assert.Empty(Directory.GetFiles(tmp.Dir, "cursor.*"));   // no advance
    }

    [Fact]
    public void EmptyStdin_IsAUsageError()
    {
        using var tmp = new MailStoreTempDir();
        var (exit, stdout, _) = Run(tmp.Dir, "");

        Assert.Equal(1, exit);
        Assert.Equal("", stdout);
    }

    // ---- the core: deliver-then-never-again -------------------------------

    /// THE pin (d4 one layer up): a digest delivers pending mail as inject
    /// with the dispatchId echoed, the cursor advances, and the SAME seam
    /// asked again answers noop — at-most-once, and the Stop-loop guard's
    /// shape ("a reconcile turn re-blocks only on genuinely new inbound").
    [Fact]
    public void Delivers_AdvancesTheCursor_ThenNoops()
    {
        using var tmp = new MailStoreTempDir();
        Send(tmp, DigestFixtures.Env("m-1", body: "first"));
        Send(tmp, DigestFixtures.Env("m-2", body: "second"));

        var (exit, stdout, _) = Run(tmp.Dir, DigestFixtures.Request(dispatchId: "d-42"));

        Assert.Equal(0, exit);
        var answer = Answer(stdout);
        Assert.Equal("inject", answer.GetProperty("effect").GetString());
        Assert.Equal("d-42", answer.GetProperty("dispatchId").GetString());
        var text = answer.GetProperty("text").GetString()!;
        Assert.Contains("2 message(s) for 'main'", text);
        Assert.Contains("first", text);
        Assert.Contains("second", text);

        // The cursor moved: deliveries counted, frontier past both lines.
        var cursorPath = new MailCursors(tmp.Store()).CursorPath("main", "s-1");
        var cursor = MailCursor.TryParse(File.ReadAllText(cursorPath), out _)!;
        Assert.Equal(1, cursor.Deliveries);
        Assert.Empty(cursor.Held);

        // Same seam again: genuinely new inbound only — there is none.
        var again = Run(tmp.Dir, DigestFixtures.Request(dispatchId: "d-43"));
        Assert.Equal(0, again.Exit);
        Assert.Equal("noop", Answer(again.Out).GetProperty("effect").GetString());
        Assert.Equal("d-43", Answer(again.Out).GetProperty("dispatchId").GetString());
    }

    /// Another role's mail is invisible — and CRUCIALLY, a seam with nothing
    /// to deliver never advances: no cursor file, no TTL burn, no state.
    [Fact]
    public void OtherRolesMail_Invisible_NoAdvance()
    {
        using var tmp = new MailStoreTempDir();
        Send(tmp, DigestFixtures.Env("m-1", to: "other"));

        var (exit, stdout, _) = Run(tmp.Dir, DigestFixtures.Request());

        Assert.Equal(0, exit);
        Assert.Equal("noop", Answer(stdout).GetProperty("effect").GetString());
        Assert.Empty(Directory.GetFiles(tmp.Dir, "cursor.*"));
    }

    // ---- seam classes across runs -----------------------------------------

    /// The mid-turn discipline end to end: an urgent-class seam delivers the
    /// urgent envelope ONLY (PostToolUse declares inject), holds the ambient
    /// one with its seenAt stamp, and the next ambient-class seam delivers it
    /// carrying its age.
    [Fact]
    public void UrgentSeam_DeliversUrgent_HoldsAmbient_AmbientSeamDrainsIt()
    {
        using var tmp = new MailStoreTempDir();
        Send(tmp, DigestFixtures.Env("m-amb", MailPriority.Ambient, body: "ambient news"));
        Send(tmp, DigestFixtures.Env("m-urg", MailPriority.Urgent, body: "stale view!"));

        var mid = RunArgs(tmp.Dir,
            DigestFixtures.Request(type: "PostToolUse"),
            ["--role", "main", "--seam", "urgent"]);

        var midText = Answer(mid.Out).GetProperty("text").GetString()!;
        Assert.Contains("stale view!", midText);
        Assert.DoesNotContain("ambient news", midText);
        Assert.Contains("(+1 more message(s) pending", midText);

        var cursors = new MailCursors(tmp.Store());
        var cursor = MailCursor.TryParse(
            File.ReadAllText(cursors.CursorPath("main", "s-1")), out _)!;
        var held = Assert.Single(cursor.Held);
        Assert.Equal("m-amb", held.Id);
        Assert.Equal(1, held.SeenAt);

        var start = Run(tmp.Dir, DigestFixtures.Request(dispatchId: "d-2"));
        var startText = Answer(start.Out).GetProperty("text").GetString()!;
        Assert.Contains("ambient news", startText);
        Assert.Contains("waited 1 opportunity", startText);
    }

    /// A QUIET mid-turn seam must not age mail: nothing urgent pending ⇒
    /// noop, no cursor write, deliveries stays zero — the planner obligation
    /// ("only advance when writing state worth the opportunity") pinned.
    [Fact]
    public void UrgentSeam_NothingUrgent_NoAdvance_NoTtlBurn()
    {
        using var tmp = new MailStoreTempDir();
        Send(tmp, DigestFixtures.Env("m-amb", MailPriority.Ambient, ttl: 1));

        for (var i = 0; i < 3; i++)
        {
            var (_, stdout, _) = RunArgs(tmp.Dir,
                DigestFixtures.Request(type: "PostToolUse"),
                ["--role", "main", "--seam", "urgent"]);
            Assert.Equal("noop", Answer(stdout).GetProperty("effect").GetString());
        }

        Assert.Empty(Directory.GetFiles(tmp.Dir, "cursor.*"));

        // Three quiet mid-turn seams later, the ttl-1 envelope still delivers.
        var (_, output, _) = Run(tmp.Dir, DigestFixtures.Request());
        Assert.Contains("m-amb",
            Answer(output).GetProperty("text").GetString());
    }

    /// The stated TTL cost, end to end: urgent deliveries ARE advances, so a
    /// held ttl-1 ambient envelope expires after being passed over once —
    /// named in the next delivering digest, dropped by its advance, durable
    /// in the store.
    [Fact]
    public void ChattyUrgentTurn_BurnsHeldAmbientTtl_ExpiryNamedThenDropped()
    {
        using var tmp = new MailStoreTempDir();
        Send(tmp, DigestFixtures.Env("m-amb", MailPriority.Ambient, ttl: 1, body: "doomed"));
        Send(tmp, DigestFixtures.Env("m-u1", MailPriority.Urgent));

        string[] urgentArgs = ["--role", "main", "--seam", "urgent"];
        var first = RunArgs(tmp.Dir, DigestFixtures.Request(type: "PostToolUse"), urgentArgs);
        Assert.Contains("m-u1", Answer(first.Out).GetProperty("text").GetString());

        Send(tmp, DigestFixtures.Env("m-u2", MailPriority.Urgent));
        var second = RunArgs(tmp.Dir, DigestFixtures.Request(type: "PostToolUse"), urgentArgs);
        var text = Answer(second.Out).GetProperty("text").GetString()!;
        Assert.Contains("m-u2", text);
        Assert.Contains("expired undelivered", text);
        Assert.Contains("m-amb", text);
        Assert.DoesNotContain("doomed", text);   // named, not delivered

        // Dropped from delivery state; still durable in the store (d13).
        var next = Run(tmp.Dir, DigestFixtures.Request(dispatchId: "d-9"));
        Assert.Equal("noop", Answer(next.Out).GetProperty("effect").GetString());
        Assert.Contains(tmp.Store().Read(), l => l.Envelope?.Id == "m-amb");
    }

    // ---- the reconcile class ----------------------------------------------

    /// claude-code's Stop declares NO effects today (the phase-5 data edit is
    /// deliberately unshipped): a reconcile-class registration there answers
    /// noop and advances nothing — mail waits for a capable seam.
    [Fact]
    public void ReconcileSeam_OnEffectlessStop_Noops()
    {
        using var tmp = new MailStoreTempDir();
        Send(tmp, DigestFixtures.Env("m-1"));

        var (exit, stdout, _) = RunArgs(tmp.Dir,
            DigestFixtures.Request(type: "Stop"),
            ["--role", "main", "--seam", "reconcile"]);

        Assert.Equal(0, exit);
        Assert.Equal("noop", Answer(stdout).GetProperty("effect").GetString());
        Assert.Empty(Directory.GetFiles(tmp.Dir, "cursor.*"));
    }

    /// With a spec that DOES declare decide on Stop, the reconcile seam
    /// blocks with the digest as the reason — the rendered digest is the
    /// delivery, the cursor advances, and the next Stop passes clean (N3's
    /// termination shape, one layer below the live adapter).
    [Fact]
    public void ReconcileSeam_WithDecideDeclared_BlocksOnceThenPasses()
    {
        using var tmp = new MailStoreTempDir();
        using var harnessDir = new MailStoreTempDir();
        File.WriteAllText(Path.Combine(harnessDir.Dir, "rec-h.json"), """
            { "name": "rec-h", "response": { "adapter": "generic-json" },
              "events": { "Stop": { "effects": ["decide"] } } }
            """);
        Send(tmp, DigestFixtures.Env("m-1", body: "unfinished thread"));

        string[] argv = ["--role", "main", "--harness", "rec-h", "--seam", "reconcile"];
        var (exit, stdout, _) = RunArgs(tmp.Dir,
            DigestFixtures.Request(type: "Stop"), argv, harnessDir.Dir);

        Assert.Equal(0, exit);
        var answer = Answer(stdout);
        Assert.Equal("decide", answer.GetProperty("effect").GetString());
        Assert.Equal("deny", answer.GetProperty("verdict").GetString());
        Assert.Contains("unfinished thread", answer.GetProperty("reason").GetString());

        // The decide answer must satisfy the engine's OWN strict grammar —
        // parsed here through ExecWire.ParseAnswer, not a lenient JsonDocument.
        var wire = Assert.IsType<ExecAnswer.Ok>(ExecWire.ParseAnswer(stdout.TrimEnd('\n')));
        var decide = Assert.IsType<Effect.Decide>(wire.Effect);
        Assert.Equal(Verdict.Deny, decide.Verdict);

        var again = RunArgs(tmp.Dir, DigestFixtures.Request(type: "Stop", dispatchId: "d-2"),
            argv, harnessDir.Dir);
        Assert.Equal("noop", Answer(again.Out).GetProperty("effect").GetString());
    }

    /// The escalation regression (skeptic finding 3): `--seam reconcile`
    /// registered — by typo or otherwise — on PreToolUse, which declares BOTH
    /// decide and inject, must deliver as a plain inject. The decide arm
    /// would render as `permissionDecision: deny` on the user's tool call:
    /// a status message denying a tool is mail escalating itself to get read.
    [Fact]
    public void ReconcileSeam_OnADecideAndInjectEvent_InjectsNeverDenies()
    {
        using var tmp = new MailStoreTempDir();
        Send(tmp, DigestFixtures.Env("m-1", body: "just news"));

        var (exit, stdout, _) = RunArgs(tmp.Dir,
            DigestFixtures.Request(type: "PreToolUse"),
            ["--role", "main", "--seam", "reconcile"]);

        Assert.Equal(0, exit);
        var answer = Answer(stdout);
        Assert.Equal("inject", answer.GetProperty("effect").GetString());
        Assert.Contains("just news", answer.GetProperty("text").GetString());
    }

    [Fact]
    public void AnEventTheSpecNeverDeclares_Noops()
    {
        using var tmp = new MailStoreTempDir();
        Send(tmp, DigestFixtures.Env("m-1"));

        var (_, stdout, _) = Run(tmp.Dir, DigestFixtures.Request(type: "BrandNewEvent"));

        Assert.Equal("noop", Answer(stdout).GetProperty("effect").GetString());
        Assert.Empty(Directory.GetFiles(tmp.Dir, "cursor.*"));
    }

    // ---- advance failure: noop, never a blind inject ----------------------

    /// If the cursor CANNOT advance, the answer must be noop — an inject
    /// whose advance failed would double-deliver on the next seam. Forced
    /// here by a directory squatting on the cursor path (Advance returns
    /// Failed, never throws).
    [Fact]
    public void AdvanceFailure_AnswersNoop_MailStaysPending()
    {
        using var tmp = new MailStoreTempDir();
        Send(tmp, DigestFixtures.Env("m-1"));
        var cursors = new MailCursors(tmp.Store());
        Directory.CreateDirectory(cursors.CursorPath("main", "s-1"));

        var (exit, stdout, stderr) = Run(tmp.Dir, DigestFixtures.Request());

        Assert.Equal(0, exit);
        Assert.Equal("noop", Answer(stdout).GetProperty("effect").GetString());
        Assert.Contains("cursor did not advance", stderr);

        // Nothing was consumed: clear the obstruction and the mail delivers.
        Directory.Delete(cursors.CursorPath("main", "s-1"));
        var retry = Run(tmp.Dir, DigestFixtures.Request(dispatchId: "d-2"));
        Assert.Contains("m-1", Answer(retry.Out).GetProperty("text").GetString());
    }

    // ---- sessions ----------------------------------------------------------

    /// Cursors are per-(role, session): two sessions holding the role each
    /// get their own delivery (ADR-0016 N5's fan-out, correct for awareness).
    [Fact]
    public void TwoSessions_EachGetsItsOwnDelivery()
    {
        using var tmp = new MailStoreTempDir();
        Send(tmp, DigestFixtures.Env("m-1", body: "for everyone"));

        var a = Run(tmp.Dir, DigestFixtures.Request(sessionId: "s-a"));
        var b = Run(tmp.Dir, DigestFixtures.Request(sessionId: "s-b"));

        Assert.Contains("for everyone", Answer(a.Out).GetProperty("text").GetString());
        Assert.Contains("for everyone", Answer(b.Out).GetProperty("text").GetString());
    }

    // ---- resident lock-step ------------------------------------------------

    /// `--resident` speaks ADR-0010 d3's protocol: `{"ready":1}` first, then
    /// one answer line per envelope line — the digest is stateless between
    /// envelopes, so the second envelope sees the first's advance.
    [Fact]
    public void Resident_ReadyThenLockStep_SecondEnvelopeSeesTheAdvance()
    {
        using var tmp = new MailStoreTempDir();
        Send(tmp, DigestFixtures.Env("m-1"));

        var stdin = DigestFixtures.Request(dispatchId: "d-1") + "\n"
                  + DigestFixtures.Request(dispatchId: "d-2") + "\n";
        var (exit, stdout, _) = RunArgs(tmp.Dir, stdin, ["--role", "main", "--resident"]);

        Assert.Equal(0, exit);
        var lines = stdout.TrimEnd('\n').Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.True(ExecWire.TryParseReady(lines[0]));
        var first = JsonDocument.Parse(lines[1]).RootElement;
        Assert.Equal("inject", first.GetProperty("effect").GetString());
        Assert.Equal("d-1", first.GetProperty("dispatchId").GetString());
        var second = JsonDocument.Parse(lines[2]).RootElement;
        Assert.Equal("noop", second.GetProperty("effect").GetString());
        Assert.Equal("d-2", second.GetProperty("dispatchId").GetString());
    }

    /// A bad-but-addressable line (valid JSON the strict parse rejects — the
    /// wire-skew shape) answers a noop that CARRIES the dispatchId echo: the
    /// resident engine kills any un-echoed answer as a protocol error
    /// (skeptic finding 4), so the error reply must be addressed for the
    /// loop's survival to be real end to end.
    [Fact]
    public void Resident_AnAddressableBadLine_NoopCarriesTheEcho_LoopSurvives()
    {
        using var tmp = new MailStoreTempDir();
        Send(tmp, DigestFixtures.Env("m-1"));

        var badButAddressed = """{"v":99,"dispatchId":"d-skew","event":{"type":"Stop"}}""";
        var stdin = badButAddressed + "\n" + DigestFixtures.Request(dispatchId: "d-2") + "\n";
        var (exit, stdout, stderr) = RunArgs(tmp.Dir, stdin, ["--role", "main", "--resident"]);

        Assert.Equal(0, exit);
        var lines = stdout.TrimEnd('\n').Split('\n');
        Assert.Equal(3, lines.Length);
        var noop = JsonDocument.Parse(lines[1]).RootElement;
        Assert.Equal("noop", noop.GetProperty("effect").GetString());
        Assert.Equal("d-skew", noop.GetProperty("dispatchId").GetString());
        Assert.Equal("inject", JsonDocument.Parse(lines[2]).RootElement.GetProperty("effect").GetString());
        Assert.Contains("malformed", stderr);
    }

    /// True garbage has no id to lift: the noop goes out unaddressed, the
    /// digest's loop continues — and the ENGINE will kill the conversation
    /// over the missing echo, which is the honest outcome for a corrupted
    /// wire (a supervised respawn, visible in the trail — not a guess).
    [Fact]
    public void Resident_UnaddressableGarbage_NoopWithoutEcho_LoopStillServes()
    {
        using var tmp = new MailStoreTempDir();
        Send(tmp, DigestFixtures.Env("m-1"));

        var stdin = "garbage\n" + DigestFixtures.Request(dispatchId: "d-2") + "\n";
        var (exit, stdout, stderr) = RunArgs(tmp.Dir, stdin, ["--role", "main", "--resident"]);

        Assert.Equal(0, exit);
        var lines = stdout.TrimEnd('\n').Split('\n');
        Assert.Equal(3, lines.Length);
        var noop = JsonDocument.Parse(lines[1]).RootElement;
        Assert.Equal("noop", noop.GetProperty("effect").GetString());
        Assert.False(noop.TryGetProperty("dispatchId", out _));
        Assert.Equal("inject", JsonDocument.Parse(lines[2]).RootElement.GetProperty("effect").GetString());
        Assert.Contains("malformed", stderr);
    }

    // ---- the hot-reload window ---------------------------------------------

    /// A TextReader that runs an action just before handing over each line —
    /// the seam that lets a test edit the world between two lock-step
    /// envelopes, exactly as a live spec edit lands between two dispatches.
    private sealed class ScriptedReader(Queue<(string Line, Action? Before)> script) : TextReader
    {
        public override string? ReadLine()
        {
            if (script.Count == 0) return null;
            var (line, before) = script.Dequeue();
            before?.Invoke();
            return line;
        }
    }

    /// Skeptic finding 6: a resident whose spec view froze at spawn would
    /// plan an inject the daemon-side capability gate then swallows AFTER the
    /// cursor advanced — silent mail loss. The digest re-resolves the spec
    /// per envelope through the same stamp-gated reload the daemon uses:
    /// remove inject from the event between two envelopes and the second
    /// answers noop with the new mail still pending.
    [Fact]
    public void Resident_SpecEditBetweenEnvelopes_IsHonored_NoMailLost()
    {
        using var tmp = new MailStoreTempDir();
        using var harnessDir = new MailStoreTempDir();
        var specPath = Path.Combine(harnessDir.Dir, "hot-h.json");
        File.WriteAllText(specPath, """
            { "name": "hot-h", "response": { "adapter": "generic-json" },
              "events": { "UserPromptSubmit": { "effects": ["inject"] } } }
            """);
        Send(tmp, DigestFixtures.Env("m-1"));

        var script = new Queue<(string, Action?)>();
        script.Enqueue((DigestFixtures.Request(dispatchId: "d-1"), null));
        script.Enqueue((DigestFixtures.Request(dispatchId: "d-2"), () =>
        {
            // Between the two dispatches: the event loses its inject verb and
            // new mail arrives. (Backdated mtime — same-tick edits are
            // invisible to a stamp; a real edit is never in the same tick.)
            File.WriteAllText(specPath, """
                { "name": "hot-h", "response": { "adapter": "generic-json" },
                  "events": { "UserPromptSubmit": { "effects": [] } } }
                """);
            File.SetLastWriteTimeUtc(specPath, DateTime.UtcNow.AddSeconds(2));
            Send(tmp, DigestFixtures.Env("m-2"));
        }));

        var stdout = new StringWriter();
        var exit = MailDigest.Run(["--role", "main", "--harness", "hot-h", "--resident"],
            new ScriptedReader(script), stdout, new StringWriter(),
            mailDir: tmp.Dir, harnessDir: harnessDir.Dir);

        Assert.Equal(0, exit);
        var lines = stdout.ToString().TrimEnd('\n').Split('\n');
        Assert.Equal("inject", JsonDocument.Parse(lines[1]).RootElement.GetProperty("effect").GetString());
        Assert.Equal("noop", JsonDocument.Parse(lines[2]).RootElement.GetProperty("effect").GetString());

        // m-2 was NOT advanced past: it is still pending for the next seam.
        var view = new MailCursors(tmp.Store()).Pending("main", "s-1");
        Assert.Equal("m-2", Assert.Single(view.Pending).Envelope.Id);
    }

    /// Finding 2 end to end: a 50KB sender-controlled id riding expiry into a
    /// mid-turn urgent answer must come out bounded — the whole answer line,
    /// not just the item blocks.
    [Fact]
    public void UrgentAnswer_WithHugeExpiredId_StaysBounded()
    {
        using var tmp = new MailStoreTempDir();
        Send(tmp, DigestFixtures.Env("m-" + new string('z', 50_000), MailPriority.Ambient, ttl: 1));
        Send(tmp, DigestFixtures.Env("m-u1", MailPriority.Urgent));

        string[] urgentArgs = ["--role", "main", "--seam", "urgent"];
        var first = RunArgs(tmp.Dir, DigestFixtures.Request(type: "PostToolUse"), urgentArgs);
        Assert.Equal("inject", Answer(first.Out).GetProperty("effect").GetString());

        Send(tmp, DigestFixtures.Env("m-u2", MailPriority.Urgent));
        var second = RunArgs(tmp.Dir, DigestFixtures.Request(type: "PostToolUse"), urgentArgs);

        Assert.Contains("expired undelivered", Answer(second.Out).GetProperty("text").GetString());
        Assert.True(second.Out.Length < 4096,
            $"urgent-seam answer ran to {second.Out.Length} bytes — the cap is not hard");
    }
}

/// The smoke the plan demands: the digest as a REGISTERED exec handler,
/// spawned by a real daemon as a real process, answering a real hook — the
/// engine binary invoking itself as its own payload (d7's whole point: the
/// strictness lives in tested C#, and registration is configuration).
public class MailDigestDaemonSmokeTests : IDisposable
{
    private readonly TempRuntimeDir _tmp = new();
    private readonly string _home;

    public MailDigestDaemonSmokeTests()
    {
        Directory.CreateDirectory(_tmp.Path);
        _home = Path.Combine(_tmp.Path, "home");
        Directory.CreateDirectory(Path.Combine(_home, ".captainHook"));
    }

    public void Dispose() => _tmp.Dispose();

    /// The runtime the child apphost must find: this test process is already
    /// running on it, so derive DOTNET_ROOT from CoreLib's location
    /// (…/dotnet/shared/Microsoft.NETCore.App/<ver>/ → three levels up) —
    /// the child's env is a stripped allowlist and a user-local install
    /// (~/.dotnet) is invisible without it.
    private static string DotnetRoot()
    {
        var corelib = typeof(object).Assembly.Location;
        return Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(
            Path.GetDirectoryName(corelib)!)!)!)!;
    }

    private string WriteHandlers()
    {
        var handlers = new
        {
            version = 1,
            handlers = new object[]
            {
                new
                {
                    name = "mail-digest",
                    command = Path.Combine(AppContext.BaseDirectory, "captainHook"),
                    args = new[] { "mail", "digest", "--role", "main" },
                    events = new[] { "UserPromptSubmit" },
                    mode = "oneshot",
                    failMode = "open",
                    budgetMs = 20000,
                    env = new Dictionary<string, string>
                    {
                        ["HOME"] = _home,               // mail store, cursors, logs — all under the sandbox
                        ["DOTNET_ROOT"] = DotnetRoot(),
                    },
                },
            },
        };
        var path = Path.Combine(_tmp.Path, "handlers.json");
        File.WriteAllText(path, JsonSerializer.Serialize(handlers));
        return path;
    }

    private static string Body(ForwardOutcome o) =>
        Encoding.UTF8.GetString(Assert.IsType<ForwardOutcome.Answered>(o).StdoutBytes);

    [Fact]
    public async Task Daemon_SpawnsTheDigest_MailInjectsOnce_ThenCleanNoop()
    {
        if (!ProcessGroup.Prefix.Pgroup) return;   // xunit 2.x: no dynamic skip

        // Mail arrives while nobody is listening — store-and-forward is the
        // point. Seeded through the real store into the CHILD's mail dir
        // (its HOME is the sandbox).
        var mailDir = Path.Combine(_home, ".captainHook", "mail");
        var store = new MailStore(mailDir);
        Assert.IsType<MailAppend.Appended>(
            store.Append(DigestFixtures.Env("m-live", body: "the smoke crossed the wire")));

        var handlersPath = WriteHandlers();
        using var stop = new CancellationTokenSource();
        var daemon = Task.Run(() => DaemonHost.RunAsync(
            _tmp.Paths, NoHarnessDir(), stop.Token, handlersPath: handlersPath));
        await PollUntilAsync(async () =>
            await ShimClient.TryForwardAsync(_tmp.Paths.SocketPath,
                new HookRequest("warmup00", "session-start", "claude-code", "{}"u8.ToArray()))
                is ForwardOutcome.Answered,
            TimeSpan.FromSeconds(15), "daemon up");

        // First prompt: the digest child cold-starts, reads store + cursor,
        // and the mail rides the claude-hook-json inject into the answer —
        // concatenated after the default echo handler's inject by Merge.
        var payload = Encoding.UTF8.GetBytes("""{"session_id":"s-live"}""");
        var first = Body(await ShimClient.TryForwardAsync(_tmp.Paths.SocketPath,
            new HookRequest("prompt01", "user-prompt-submit", "claude-code", payload)));
        Assert.Contains("captAInHook mail", first);
        Assert.Contains("the smoke crossed the wire", first);
        Assert.Contains("additionalContext", first);   // the adapter's inject shape

        // The advance is on disk in the sandbox: one cursor for (main, s-live).
        var cursorPath = new MailCursors(store).CursorPath("main", "s-live");
        Assert.True(File.Exists(cursorPath), "cursor-advance-on-inject must have written the cursor");

        // Second prompt, same session: the digest contributes NOTHING — only
        // the default echo handler's inject remains in the merged answer.
        var second = Body(await ShimClient.TryForwardAsync(_tmp.Paths.SocketPath,
            new HookRequest("prompt02", "user-prompt-submit", "claude-code", payload)));
        Assert.DoesNotContain("captAInHook mail", second);
        Assert.DoesNotContain("smoke crossed", second);

        stop.Cancel();
        Assert.Equal(0, await daemon.WaitAsync(TimeSpan.FromSeconds(15)));
    }
}
