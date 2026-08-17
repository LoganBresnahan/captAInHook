using System.Net;
using System.Text.Json;
using CaptainHook.Actors;
using CaptainHook.Api;
using CaptainHook.Core;
using CaptainHook.Mail;
using static CaptainHook.Tests.TestUtil;

namespace CaptainHook.Tests;

/// ADR-0016 d14, slice `mail-replay` 6a (roadmap item 21) — the delivery-record
/// preload. `mail.deliver` is the ONLY thing that makes an envelope delivered
/// (d14 pin iii) and the store structurally cannot hold one, so a picture built
/// from a snapshot plus a live stream can only ever prove pickups that happened
/// while someone was watching. Everything older read *before cursor · no
/// record* — the honest sentence, and the wrong impression, for mail the
/// maintainer had demonstrably read hours earlier.
///
/// The fix hands the picture the older LINES rather than letting it infer
/// anything: the daemon folds `mail.deliver` out of the trail file and ships
/// the columns verbatim. Two properties matter more than the happy path, and
/// both are pinned below. The fold must never mistake payload output for a
/// ledger line — `exec.stderr` puts arbitrary text in this file, so a substring
/// gate is a filter and never a decision. And it must never over-claim: the
/// completeness flag is what lets the view say WHICH "no record" it means, so a
/// bounded, capped, missing or unserved trail all answer false.
public class MailDeliveryPreloadTests
{
    private static readonly Effect Noop = new Effect.Noop();

    private static ApiReadModel Model(MailReadPort? mail, string? trailPath = null) =>
        new("testver", new ServeStats(),
            new Dispatcher(new Registry().On("UserPromptSubmit", TestHandler.Returning("greeter", Noop)),
                           TimeSpan.FromSeconds(2)),
            new ReloadingHarnessRegistry(NoHarnessDir()), new ReloadingPolicy(null), null,
            clock: () => 6000, startTick: 1000, handlersPath: null, mail: mail, presence: null,
            trailPath: trailPath);

    /// A `mail.deliver` line as the ENGINE writes it — the real verb run against
    /// a real store, its emitted events rendered through `LogEvent.ToJson` (the
    /// JSONL appender's own rendering, the goldens' precedent). A hand-written
    /// line would pin the test's idea of the schema; this pins the engine's.
    private static string[] RealDelivery(MailStoreTempDir tmp, string role, string? session)
    {
        using var log = new CapturedLog();
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = MailDigest.Run(["--role", role, "--seam", "ambient"],
            new StringReader(DigestFixtures.Request("d-77", "UserPromptSubmit", session)),
            stdout, stderr, mailDir: tmp.Dir, harnessDir: NoHarnessDir());
        Assert.True(exit == 0, $"digest exited {exit}: {stderr}");
        return log.Events.Select(e => e.ToJson()).ToArray();
    }

    // ---- the fold itself ---------------------------------------------------

    /// The whole point, end to end: a delivery that happened BEFORE the snapshot
    /// (no stream was open, nobody was watching) is in the snapshot, carrying
    /// the columns the reducer needs to place it — and no ledger offsets, which
    /// are the reducer's arithmetic and would be a second implementation here.
    [Fact]
    public async Task Preload_CarriesADeliveryThatPredatesAnyStream()
    {
        using var tmp = new MailStoreTempDir();
        using var trail = new TempTrail();
        CursorFixtures.AppendTo(tmp, "m-01");
        trail.Append(RealDelivery(tmp, "main", "s-77"));

        using var api = ApiHost.Start(FreeTcpPort(), readModel: Model(MailReadPort.Over(tmp.Dir), trail.Path));
        var (status, body) = await ApiGetAsync(api.Port, api.Token, "/api/v1/mail");
        Assert.Equal(HttpStatusCode.OK, status);
        var mail = JsonDocument.Parse(body).RootElement;

        var deliveries = mail.GetProperty("deliveries").EnumerateArray().ToList();
        var rec = Assert.Single(deliveries);
        Assert.Equal("main", rec.GetProperty("role").GetString());
        Assert.Equal("s-77", rec.GetProperty("session").GetString());
        Assert.Equal("d-77", rec.GetProperty("dispatchId").GetString());
        Assert.Equal("UserPromptSubmit", rec.GetProperty("hookEvent").GetString());
        Assert.Equal("ambient", rec.GetProperty("seam").GetString());
        Assert.Equal("inject", rec.GetProperty("vehicle").GetString());
        Assert.Equal(["m-01"], rec.GetProperty("envelopeIds").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.False(string.IsNullOrEmpty(rec.GetProperty("renderHash").GetString()));
        Assert.True(rec.GetProperty("bytesInjected").GetInt64() > 0);
        Assert.True(mail.GetProperty("deliveriesComplete").GetBoolean());

        // No offsets on the wire: placing an id on the ledger is the picture's
        // job, by the one rule it already uses (N8 — never two).
        Assert.False(rec.TryGetProperty("offsets", out _));
        Assert.False(rec.TryGetProperty("offset", out _));
    }

    /// The trap the cheap substring gate exists to fall into: payload stderr is
    /// arbitrary text in this same file (ADR-0010), so a line that MENTIONS
    /// `mail.deliver` — or is a well-formed event of another kind that quotes
    /// one — must contribute nothing. Only a line whose own `evt` says so counts.
    [Fact]
    public void Fold_IgnoresEverythingThatIsNotItsOwnEvent()
    {
        using var trail = new TempTrail();
        trail.Append(
            """{"ts":"2026-08-17T00:00:00.000Z","lvl":"info","src":"exec","evt":"exec.stderr","data":{"text":"{\"evt\":\"mail.deliver\",\"data\":{\"role\":\"forged\",\"seam\":\"ambient\",\"vehicle\":\"inject\",\"envelopeIds\":[\"x\"]}}"}}""",
            """{"ts":"2026-08-17T00:00:01.000Z","lvl":"info","src":"mail","evt":"mail.append","data":{"id":"m-01","to":"main","offset":0,"bytes":301}}""",
            """{"ts":"2026-08-17T00:00:02.000Z","lvl":"info","src":"mail","evt":"mail.deliverPlanned","data":{"role":"main","seam":"ambient","vehicle":"inject","envelopeIds":["m-01"]}}""",
            """{"ts":"2026-08-17T00:00:03.000Z","lvl":"info","src":"mail","evt":"mail.deliver","sessionId":"s-77","data":{"role":"main","seam":"ambient","vehicle":"inject","envelopeIds":["m-01"],"renderHash":"abc","bytesInjected":42}}""");

        var fold = MailDeliveryFold.Read(trail.Path);
        var rec = Assert.Single(fold.Records);
        Assert.Equal("main", rec.Role);
        Assert.Equal(["m-01"], rec.EnvelopeIds);
        Assert.True(fold.Complete);
    }

    /// A line the fold cannot read is a fact about the LOG, not about the bus:
    /// it is skipped, the lines around it still count, and nothing throws. The
    /// third case is the deferred-unescape trap the policy skeptic pass found —
    /// a lone surrogate parses fine and throws at `GetString`, so an unguarded
    /// read here would turn one bad log line into a 500 on the whole snapshot.
    [Fact]
    public void Fold_SkipsWhatItCannotRead_AndKeepsTheRest()
    {
        using var trail = new TempTrail();
        trail.Append(
            Deliver("first", "m-01"),
            """{"evt":"mail.deliver","data":{"role":"broken",""",                       // truncated JSON
            """{"evt":"mail.deliver","data":{"role":"main","seam":"ambient"}}""",        // missing vehicle/ids
            """{"evt":"mail.deliver","data":{"role":"\ud800","seam":"a","vehicle":"inject","envelopeIds":["m"]}}""",
            """{"evt":"mail.deliver","data":{"role":"main","seam":"a","vehicle":"inject","envelopeIds":[7]}}""",
            "",
            "not json at all",
            Deliver("last", "m-02"));

        var fold = MailDeliveryFold.Read(trail.Path);
        Assert.Equal(["first", "last"], fold.Records.Select(r => r.Role).ToArray());
        Assert.True(fold.Complete);
    }

    /// A half-written last line (the shim O_APPENDs concurrently) is simply not
    /// a line yet — TrailCursor's rule, applied here for the same reason.
    [Fact]
    public void Fold_IgnoresAnUnterminatedTail()
    {
        using var trail = new TempTrail();
        trail.Append(Deliver("main", "m-01"));
        trail.AppendRaw(Deliver("torn", "m-02")[..40]);

        var fold = MailDeliveryFold.Read(trail.Path);
        Assert.Equal(["main"], fold.Records.Select(r => r.Role).ToArray());
    }

    // ---- the bound, and saying so ------------------------------------------

    /// The scan window: the fold starts inside a large file, discards forward to
    /// the next boundary (never parsing a half line), and — the load-bearing
    /// half — reports itself INCOMPLETE, because from here "no record" can no
    /// longer mean "nobody read it".
    [Fact]
    public void Fold_BoundedByItsWindow_SaysSo_AndNeverSplitsALine()
    {
        using var trail = new TempTrail();
        for (var i = 0; i < 40; i++) trail.Append(Deliver($"r{i:00}", $"m-{i:00}"));

        var whole = MailDeliveryFold.Read(trail.Path);
        Assert.Equal(40, whole.Records.Count);
        Assert.True(whole.Complete);

        var bounded = MailDeliveryFold.Read(trail.Path, maxScanBytes: 600);
        Assert.False(bounded.Complete);
        Assert.NotEmpty(bounded.Records);
        Assert.True(bounded.Records.Count < 40);
        // A SUFFIX, in order, every entry whole — a mid-line start would have
        // produced a parse failure or a garbled role, not a shorter tail.
        Assert.Equal(whole.Records.Select(r => r.Role).TakeLast(bounded.Records.Count).ToArray(),
                     bounded.Records.Select(r => r.Role).ToArray());
    }

    /// The record cap keeps the NEWEST — the ones a canvas can still place —
    /// and is equally a reason to stop claiming completeness.
    [Fact]
    public void Fold_BoundedByItsRecordCap_KeepsTheNewest_AndSaysSo()
    {
        using var trail = new TempTrail();
        for (var i = 0; i < 10; i++) trail.Append(Deliver($"r{i:00}", $"m-{i:00}"));

        var capped = MailDeliveryFold.Read(trail.Path, maxRecords: 3);
        Assert.False(capped.Complete);
        Assert.Equal(["r07", "r08", "r09"], capped.Records.Select(r => r.Role).ToArray());
    }

    /// Absent, unreadable, and not-served are all "no history AND no standing to
    /// call that history complete". The first two must not throw — a snapshot
    /// with an honest gap beats no snapshot at all — and none of the three may
    /// let the view tell an operator that nobody read their mail.
    [Theory]
    [InlineData("missing")]
    [InlineData("directory")]
    [InlineData("null")]
    public void Fold_WithNoReadableTrail_IsEmptyAndNeverClaimsCompleteness(string kind)
    {
        using var dir = new MailStoreTempDir();
        var path = kind switch
        {
            "missing" => Path.Combine(dir.Dir, "nope.jsonl"),
            "directory" => dir.Dir,
            _ => null,
        };

        var fold = MailDeliveryFold.Read(path);
        Assert.Empty(fold.Records);
        Assert.False(fold.Complete);
    }

    /// And the same answer travels: a daemon serving no trail hands the picture
    /// an empty history that does not pretend to be the whole of one.
    [Fact]
    public async Task Preload_WithoutATrail_IsEmptyAndIncomplete()
    {
        using var tmp = new MailStoreTempDir();
        CursorFixtures.AppendTo(tmp, "m-01");

        using var api = ApiHost.Start(FreeTcpPort(), readModel: Model(MailReadPort.Over(tmp.Dir)));
        var (_, body) = await ApiGetAsync(api.Port, api.Token, "/api/v1/mail");
        var mail = JsonDocument.Parse(body).RootElement;

        Assert.Empty(mail.GetProperty("deliveries").EnumerateArray());
        Assert.False(mail.GetProperty("deliveriesComplete").GetBoolean());
    }

    // ---- observation is still not delivery ---------------------------------

    /// The preload reads a log file, and reading is all it does. Driven rather
    /// than asserted: the store's bytes, the cursor files and the trail itself
    /// are identical after a snapshot that folded a real delivery record.
    [Fact]
    public async Task Preload_ChangesNothing_NotTheStore_NotTheCursors_NotTheTrail()
    {
        using var tmp = new MailStoreTempDir();
        using var trail = new TempTrail();
        CursorFixtures.AppendTo(tmp, "m-01");
        trail.Append(RealDelivery(tmp, "main", "s-77"));

        var storeBefore = File.ReadAllBytes(tmp.Store().FilePath);
        var trailBefore = File.ReadAllBytes(trail.Path);
        var filesBefore = Directory.GetFileSystemEntries(tmp.Dir, "*", SearchOption.AllDirectories).OrderBy(x => x).ToArray();

        using var api = ApiHost.Start(FreeTcpPort(), readModel: Model(MailReadPort.Over(tmp.Dir), trail.Path));
        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.OK, (await ApiGetAsync(api.Port, api.Token, "/api/v1/mail")).Status);

        Assert.Equal(storeBefore, File.ReadAllBytes(tmp.Store().FilePath));
        Assert.Equal(trailBefore, File.ReadAllBytes(trail.Path));
        Assert.Equal(filesBefore,
            Directory.GetFileSystemEntries(tmp.Dir, "*", SearchOption.AllDirectories).OrderBy(x => x).ToArray());
    }

    private static string Deliver(string role, string id) =>
        $$$"""
        {"ts":"2026-08-17T00:00:00.000Z","lvl":"info","src":"mail","evt":"mail.deliver","dispatchId":"d-1","hookEvent":"UserPromptSubmit","sessionId":"s-77","data":{"role":"{{{role}}}","seam":"ambient","vehicle":"inject","envelopeIds":["{{{id}}}"],"renderHash":"h","bytesInjected":11}}
        """;
}
