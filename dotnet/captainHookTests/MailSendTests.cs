using System.Text;
using CaptainHook.Mail;
using CaptainHook.Wire;

namespace CaptainHook.Tests;

/// ADR-0016 decision 7 — `captainHook mail send`, the universal write path
/// (roadmap item 20, phase 3's cheap half). The verb is the strict parser +
/// the chained store behind one exit code; these tests drive it end to end
/// through injected streams, the `doctor`/`ui` precedent.
public class MailSendTests
{
    private const string GoodEnvelope =
        """{"v":1,"id":"m-01","ts":"2026-08-12T19:00:00Z","from":{"agent":"a","harness":"claude-code"},"to":"main","kind":"status","topic":"t","body":"hello"}""";

    private static (int Exit, string Out, string Err) Run(string stdin, string? subverb = "send", string? dir = null, string? nowTs = null)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = MailSend.Run(subverb, new StringReader(stdin), stdout, stderr, dir, nowTs);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    /// The happy path, end to end: one envelope on stdin lands as one chained
    /// line the cursor can deliver — the real write path the cursor tests
    /// were promised.
    [Fact]
    public void AValidEnvelope_IsAppendedChainedAndPending()
    {
        using var tmp = new MailStoreTempDir();

        var (exit, output, _) = Run(GoodEnvelope, dir: tmp.Dir);

        Assert.Equal(0, exit);
        Assert.Contains("m-01", output);
        Assert.Contains("offset 0", output);
        Assert.Empty(tmp.Store().VerifyChain());

        var view = new MailCursors(tmp.Store()).Pending("main", "s-1");
        Assert.Equal("m-01", Assert.Single(view.Pending).Envelope.Id);
    }

    /// d2: "every writer goes through `mail send`, which stamps it" — an
    /// envelope without `ts` gets one; an envelope WITH `ts` keeps its own
    /// verbatim (the store never rewrites what a sender said).
    [Fact]
    public void AMissingTs_IsStamped_APresentTsIsKept()
    {
        using var tmp = new MailStoreTempDir();
        var noTs = GoodEnvelope.Replace("""
            "ts":"2026-08-12T19:00:00Z",
            """.Trim(), "");

        Assert.Equal(0, Run(noTs, dir: tmp.Dir, nowTs: "2026-08-12T21:00:00Z").Exit);
        Assert.Equal("2026-08-12T21:00:00Z", tmp.Store().Read()[0].Envelope!.Ts);

        Assert.Equal(0, Run(GoodEnvelope.Replace("m-01", "m-02"), dir: tmp.Dir, nowTs: "2026-08-12T21:00:00Z").Exit);
        Assert.Equal("2026-08-12T19:00:00Z", tmp.Store().Read()[1].Envelope!.Ts);
    }

    /// Stamping must never launder a malformed envelope: the rebuild copies
    /// every property verbatim — the duplicate `to` survives into the strict
    /// parse and the envelope is REFUSED, not quietly normalized.
    [Fact]
    public void StampingPreservesTheViolationsTheSenderWrote()
    {
        using var tmp = new MailStoreTempDir();
        var dupTo = GoodEnvelope
            .Replace("""
                "ts":"2026-08-12T19:00:00Z",
                """.Trim(), "")
            .Replace("""
                "to":"main"
                """.Trim(), """
                "to":"main","to":"other"
                """.Trim());

        var (exit, _, err) = Run(dupTo, dir: tmp.Dir);

        Assert.Equal(1, exit);
        Assert.Contains("duplicate field 'to'", err);
        Assert.False(File.Exists(tmp.FilePath));   // rejected means nothing written
    }

    [Fact]
    public void AMalformedEnvelope_ListsEveryViolationAndWritesNothing()
    {
        using var tmp = new MailStoreTempDir();

        var (exit, _, err) = Run("""{"v":1,"id":"m-01","surprise":true}""", dir: tmp.Dir);

        Assert.Equal(1, exit);
        Assert.Contains("nothing written", err);
        Assert.Contains("surprise", err);
        Assert.Contains("'to'", err);
        Assert.False(File.Exists(tmp.FilePath));
    }

    [Fact]
    public void NonJsonStdin_IsRejectedNotThrown()
    {
        using var tmp = new MailStoreTempDir();

        Assert.Equal(1, Run("not json", dir: tmp.Dir).Exit);
        Assert.Equal(1, Run("", dir: tmp.Dir).Exit);
        Assert.False(File.Exists(tmp.FilePath));
    }

    /// Only `send` exists today (`mail digest` is phase 4): anything else is
    /// a loud usage error, never a guess.
    [Fact]
    public void AnUnknownOrMissingSubverb_IsAUsageError()
    {
        using var tmp = new MailStoreTempDir();

        var bogus = Run(GoodEnvelope, subverb: "digest", dir: tmp.Dir);
        Assert.Equal(1, bogus.Exit);
        Assert.Contains("unknown subverb 'digest'", bogus.Err);

        var missing = Run(GoodEnvelope, subverb: null, dir: tmp.Dir);
        Assert.Equal(1, missing.Exit);
        Assert.Contains("usage", missing.Err);
        Assert.False(File.Exists(tmp.FilePath));
    }

    /// The store's own refusals surface through the exit code — here the
    /// MaxLineBytes cap: written durably but undeliverable is not a state the
    /// verb may create.
    [Fact]
    public void AStoreRefusal_SurfacesOnStderr()
    {
        using var tmp = new MailStoreTempDir();
        var huge = GoodEnvelope.Replace("hello", new string('x', MailStore.MaxLineBytes));

        var (exit, _, err) = Run(huge, dir: tmp.Dir);

        Assert.Equal(1, exit);
        Assert.Contains("caps a line", err);
    }

    // ---- the argv contract -------------------------------------------------

    /// `mail <subverb>` parses to MailSend with the subverb riding EventName;
    /// the engine, not the parser, judges the subverb.
    [Fact]
    public void Invocation_ParsesTheMailVerb()
    {
        Assert.Equal(new Invocation(Mode.MailSend, "send", "claude-code"),
            Invocation.Parse(["mail", "send"]));
        Assert.Equal(new Invocation(Mode.MailSend, "bogus", "claude-code"),
            Invocation.Parse(["mail", "bogus"]));
        Assert.Equal(new Invocation(Mode.MailSend, null, "claude-code"),
            Invocation.Parse(["mail"]));
    }

    /// The shim is the hook command, nothing else: `mail` is refused loudly,
    /// pointing at the engine (aot-boundary rule 11's refusal discipline).
    [Fact]
    public async Task TheShim_RefusesTheMailVerb()
    {
        var stderr = new StringWriter();
        var exit = await CaptainHook.Shim.ShimMain.RunAsync(
            ["mail", "send"], new MemoryStream(), new MemoryStream(), stderr);

        Assert.Equal(1, exit);
        Assert.Contains("mail", stderr.ToString());
        Assert.Contains("captainHook", stderr.ToString());
    }
}
