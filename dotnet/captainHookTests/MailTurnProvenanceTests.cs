using CaptainHook.Mail;

namespace CaptainHook.Tests;

// ADR-0017 N6's amendment, slice `turn-provenance` (roadmap item 22, phase 8) —
// the rule a declared sender imposes on the two write verbs.
//
// The FIELD failure this fixes: a woken turn's model composed its own `from`,
// so the first real robot answer on the live bus said `{agent: "claude",
// harness: "claude-code", session: "reviewer"}` — a model name in the field
// that means the member, and a role name in the field that means a window. The
// fix is that a spawning payload declares the identity before the model exists
// and this is where the declaration is enforced.
//
// **The environment READ is deliberately not tested in-process.**
// `Environment.SetEnvironmentVariable` is global to the test host, xunit runs
// classes in parallel, and `mail send` is called by several suites — a test
// that set `CAPTAINHOOK_MAIL_FROM_AGENT` here could change what an unrelated
// assertion sees. So every case below INJECTS the declaration, and that the
// environment is what carries it to a real turn is proven where it actually
// happens: `TurnPayloadTests.AnAnsweringTurn_…` runs the shipped payload as a
// process and `WatcherLoopE2ETests` runs the whole loop.
public class MailTurnProvenanceTests
{
    private const string Turn = "turn-d-nudge";

    private static readonly MailSenderDeclaration Declared =
        new(Agent: "reviewer", Harness: "claude-code", Session: Turn);

    private static string Envelope(
        string agent = "reviewer", string harness = "claude-code", string? session = null,
        string id = "m-answer", string? inReplyTo = null) =>
        $$"""
        {"v":1,"id":"{{id}}","ts":"2026-08-26T12:00:00Z",
         "from":{"agent":"{{agent}}","harness":"{{harness}}"{{(session is null ? "" : $",\"session\":\"{session}\"")}}},
         "to":"maintainer","kind":"status","topic":"t","body":"b"
         {{(inReplyTo is null ? "" : $",\"inReplyTo\":\"{inReplyTo}\"")}}}
        """.Replace("\n", "");

    private static (int Exit, string Err, MailStore Store) Send(
        MailStoreTempDir tmp, string envelope, MailSenderDeclaration? declared = null)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = MailSend.Run("send", new StringReader(envelope), stdout, stderr,
            tmp.Dir, nowTs: "2026-08-26T12:00:00Z",
            declaredFrom: declared ?? Declared);
        return (exit, stderr.ToString(), tmp.Store());
    }

    private static MailEnvelope Only(MailStore store) =>
        store.Read().Single(l => l.Envelope is not null).Envelope!;

    // ---- the three rules -----------------------------------------------------

    /// The case the whole slice exists for: the model wrote no `session`,
    /// because it cannot know one, and the declaration stamps the instance the
    /// payload minted. The `ts` rule, applied to provenance.
    [Fact]
    public void AnAbsentSession_IsStamped_WithTheInstanceThePayloadMinted()
    {
        using var tmp = new MailStoreTempDir();

        var (exit, err, store) = Send(tmp, Envelope());

        Assert.True(exit == 0, err);
        var e = Only(store);
        Assert.Equal("reviewer", e.From.Agent);
        Assert.Equal("claude-code", e.From.Harness);
        Assert.Equal(Turn, e.From.Session);
    }

    /// An envelope that already says exactly what the declaration says is a
    /// no-op — the ordinary path for a payload that told its model what to
    /// write, which is what `turn-claude.sh`'s prompt does.
    [Fact]
    public void AStatedSenderThatAgrees_Stands()
    {
        using var tmp = new MailStoreTempDir();

        var (exit, err, store) = Send(tmp, Envelope(session: Turn));

        Assert.True(exit == 0, err);
        Assert.Equal(new MailSender("reviewer", "claude-code", Turn), Only(store).From);
    }

    /// The refusal, and the reason it is a refusal rather than a correction:
    /// quietly rewriting the claim would hide the attempt on a chain nobody can
    /// amend. Nothing is written, and the message names what to write instead.
    [Theory]
    [InlineData("claude", "claude-code", null, "from.agent", "reviewer")]
    [InlineData("reviewer", "codex", null, "from.harness", "claude-code")]
    [InlineData("reviewer", "claude-code", "reviewer", "from.session", Turn)]
    public void AStatedSenderThatDisagrees_IsRefusedAndNothingIsWritten(
        string agent, string harness, string? session, string field, string declared)
    {
        using var tmp = new MailStoreTempDir();

        var (exit, err, store) = Send(tmp, Envelope(agent, harness, session));

        Assert.Equal(1, exit);
        Assert.Contains($"'{field}'", err);
        Assert.Contains(declared, err);
        Assert.Contains("cannot name itself somebody else", err);
        Assert.Empty(store.Read());
    }

    /// Every violation at once, the parser's all-or-nothing shape: a sender
    /// that got all three wrong is told all three times, and still writes
    /// nothing.
    [Fact]
    public void EveryDisagreementIsReported_Together()
    {
        using var tmp = new MailStoreTempDir();

        var (exit, err, store) = Send(tmp, Envelope("claude", "codex", "s-1"));

        Assert.Equal(1, exit);
        Assert.Contains("'from.agent'", err);
        Assert.Contains("'from.harness'", err);
        Assert.Contains("'from.session'", err);
        Assert.Empty(store.Read());
    }

    /// A PARTIAL declaration touches only what it names. A payload that knows
    /// the role but has no dispatch to stamp an instance from declares two
    /// fields, and the third stays whatever the sender said — a write-only
    /// member here, rather than an invented window.
    [Fact]
    public void APartialDeclaration_LeavesTheRestAlone()
    {
        var roleOnly = new MailSenderDeclaration(Agent: "reviewer", Harness: null, Session: null);

        using var lying = new MailStoreTempDir();
        var (exit, err, _) = Send(lying, Envelope(agent: "claude", harness: "codex"), roleOnly);

        Assert.Equal(1, exit);                        // the one declared field still bites
        Assert.Contains("'from.agent'", err);
        Assert.DoesNotContain("'from.harness'", err); // …and the undeclared ones do not

        using var honest = new MailStoreTempDir();
        var (exit2, err2, store2) = Send(honest, Envelope(harness: "codex"), roleOnly);

        Assert.True(exit2 == 0, err2);
        Assert.Equal(new MailSender("reviewer", "codex", null), Only(store2).From);
    }

    /// NOT DECLARED IS NOT AN ERROR — every human at a CLI, and every test in
    /// every other suite. With no declaration the verb behaves exactly as it
    /// did before this slice: the sender names itself and is believed.
    [Fact]
    public void WithNoDeclaration_TheSenderNamesItselfAndIsBelieved()
    {
        using var tmp = new MailStoreTempDir();

        var (exit, err, store) = Send(tmp, Envelope("claude", "claude-code", "whatever"),
            MailSenderDeclaration.None);

        Assert.True(exit == 0, err);
        Assert.Equal(new MailSender("claude", "claude-code", "whatever"), Only(store).From);
    }

    /// A blank declaration is NOT a declaration, whatever route it arrives by:
    /// the normalization is in the constructor rather than at the environment
    /// read, so `export CAPTAINHOOK_MAIL_FROM_AGENT=` cannot produce a member
    /// named "".
    [Fact]
    public void ABlankDeclaredValue_IsNoDeclarationAtAll()
    {
        Assert.False(new MailSenderDeclaration("", "   ", null).Declares);
        Assert.Equal(MailSenderDeclaration.None, new MailSenderDeclaration("", "   ", "\t"));
        Assert.True(new MailSenderDeclaration(null, null, "turn-x").Declares);

        // A blank stamps nothing and refuses nothing — it is simply not there.
        using var tmp = new MailStoreTempDir();
        var (exit, err, store) = Send(tmp, Envelope("claude", "codex"),
            new MailSenderDeclaration("", "", ""));

        Assert.True(exit == 0, err);
        Assert.Equal(new MailSender("claude", "codex", null), Only(store).From);
    }

    /// ORDER: the sender check runs before the thread lookup, so a member that
    /// is not who it says it is never costs a store read — and the message it
    /// gets is about WHO it is, not about a thread it was never going to join.
    [Fact]
    public void TheSenderRefusal_ComesBeforeTheThreadRules()
    {
        using var tmp = new MailStoreTempDir();
        var parent = Send(tmp, Envelope(id: "m-parent"));
        Assert.Equal(0, parent.Exit);

        // A reply that lies about its agent AND states an impossible depth: the
        // provenance refusal is the whole message.
        var (exit, err, _) = Send(tmp,
            Envelope(agent: "claude", id: "m-reply", inReplyTo: "m-parent")
                .Replace("\"body\":\"b\"", "\"body\":\"b\",\"hops\":9"));

        Assert.Equal(1, exit);
        Assert.Contains("'from.agent'", err);
        Assert.DoesNotContain("hops", err);
    }

    // `mail ask` composes its own envelope rather than parsing one, so the
    // declaration has to reach it too — a member that says one thing on `send`
    // and another on `ask` would be two provenances for one process. Those two
    // cases live in `MailAskTests` (`…TakesTheDeclarationOverItsOwnDerivation`,
    // `…RefusesAnExplicitAgentThatDisagrees…`), next to the fixture that builds
    // the registrations and policy that verb reads.
}
