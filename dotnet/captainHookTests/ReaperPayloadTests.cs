using System.Diagnostics;
using CaptainHook.Mail;

namespace CaptainHook.Tests;

// ADR-0018 d6, slice `reaper-payloads` (roadmap item 23, phase 4) — the SHIPPED
// `examples/payloads/reaper-deterministic.sh`, run as a real process.
//
// The reaper is the one member that acts on somebody ELSE'S mailbox, so what
// has to be pinned is not a rendering but a sequence of decisions:
//
//   1. THE VERBS IT RUNS, in order, per disposition. `forward-all-to-role`
//      forwards and only then reaps; `drop-all` reaps and never forwards;
//      `hold` runs nothing at all. A stub `captainHook` records the argv,
//      which is what makes this a pin rather than a reading of the script.
//   2. THE ORDER IS THE SAFETY PROPERTY. A forward that fails must NOT be
//      followed by a reap: the standing is the only reason the watcher will
//      ever look at that mailbox again, and removing it after failing to pass
//      the mail on is how mail disappears for good.
//   3. NO DISPOSITION, NO DISPOSAL. An operator says how mail is disposed of.
//      A payload that was merely installed decides nothing — the same shape as
//      ADR-0017's "a turn payload alone wakes nobody".
//   4. THE EVENT GUARD, and the ADDRESS guard: only `MailNudge`, and only a
//      nudge that names a mailbox (a role nudge names none, and this payload
//      has nothing to say about one).
//
// …and then the real thing end to end, against the REAL engine and a real
// store: a dead mailbox with stranded mail is forwarded to a live role and
// reaped, and what is on the ledger afterwards is the copy, the original, and
// no cursor.
public class ReaperPayloadTests : IDisposable
{
    private readonly TempRuntimeDir _tmp = new();
    private readonly string _home, _mailDir, _stubDir, _argvLog;

    public ReaperPayloadTests()
    {
        Directory.CreateDirectory(_tmp.Path);
        _home = Path.Combine(_tmp.Path, "home");
        _mailDir = Path.Combine(_home, ".captainHook", "mail");
        _stubDir = Path.Combine(_tmp.Path, "stubbin");
        _argvLog = Path.Combine(_tmp.Path, "argv.txt");
        Directory.CreateDirectory(_mailDir);
        Directory.CreateDirectory(_stubDir);
    }

    public void Dispose() => _tmp.Dispose();

    private static string Shipped => Path.Combine(DogfoodInfra.PayloadsDir(), "reaper-deterministic.sh");

    /// The stand-in for the engine: it records the verb line it was called with
    /// and can be told to FAIL on one of them, which is how the order property
    /// is testable at all.
    private string WriteStubBin(string failOn = "")
    {
        var stub = Path.Combine(_stubDir, "captainHook");
        File.WriteAllText(stub, $"""
            #!/bin/sh
            printf '%s\n' "$*" >> {_argvLog}
            case "$2" in
              {(failOn.Length == 0 ? "__never__" : failOn)}) echo "stub: refusing $2" >&2; exit 1 ;;
            esac
            exit 0
            """.Replace("\r\n", "\n"));
        File.SetUnixFileMode(stub,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return stub;
    }

    private string[] Verbs() =>
        File.Exists(_argvLog)
            ? File.ReadAllLines(_argvLog).Where(l => l.Length > 0).ToArray()
            : [];

    /// A dead-mailbox nudge, as `MailNudge.ToPayloadJson` writes one inside the
    /// exec-wire frame: `address` is the corpse, `role` is the reaper's own lane.
    private static string Nudge(string? address = "main@laptop-a") =>
        $$"""{"v":1,"dispatchId":"d-reap","event":{"type":"MailNudge","payload":{"hook_event_name":"MailNudge","role":"reaper","reason":"dead-mailbox {{address}} · 2 stranded past quiet (2h+)","digest":"[captAInHook mail] 2 message(s)","replyHow":"This mailbox is dead.","envelopeIds":["m-01"]"""
        + (address is null ? "" : $$""","address":"{{address}}" """.TrimEnd())
        + "}}}";

    private async Task<(int Exit, string Stdout, string Stderr)> RunAsync(
        string envelope, string bin, params (string Key, string Value)[] env)
    {
        var psi = new ProcessStartInfo(Shipped)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.Environment["PATH"] = TestPath.Minimal;
        psi.Environment["HOME"] = _home;
        psi.Environment["CAPTAINHOOK_BIN"] = bin;
        psi.Environment["CAPTAINHOOK_MAIL_DIR"] = _mailDir;
        psi.Environment["CAPTAINHOOK_LOG"] = Path.Combine(_tmp.Path, "trail.jsonl");
        psi.Environment["CAPTAINHOOK_LOG_STDERR"] = "off";
        psi.Environment["DOTNET_ROOT"] = DogfoodInfra.DotnetRoot();
        foreach (var (k, v) in env) psi.Environment[k] = v;

        using var p = Process.Start(psi)!;
        await p.StandardInput.WriteLineAsync(envelope);
        p.StandardInput.Close();
        var stdout = await p.StandardOutput.ReadToEndAsync();
        var stderr = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(90)).Token);
        return (p.ExitCode, stdout, stderr);
    }

    // ---- the verbs, per disposition ---------------------------------------

    /// d6's order, which is also the ADR's sentence: forward, THEN reap. Both
    /// carry `--by` so the ledger and the trail name who did it.
    [Fact]
    public async Task ForwardAllToRole_ForwardsThenReaps()
    {
        var bin = WriteStubBin();

        var (exit, stdout, _) = await RunAsync(Nudge(), bin,
            ("REAPER_DISPOSITION", "forward-all-to-role"), ("REAPER_FORWARD_TO", "maintainer"));

        Assert.Equal(0, exit);
        Assert.Equal("{\"effect\":\"noop\"}\n", stdout.Replace("\r\n", "\n"));
        Assert.Equal(
            [
                "mail forward main@laptop-a --to maintainer --by reaper",
                "mail reap main@laptop-a --by reaper",
            ],
            Verbs());
    }

    /// THE ORDER IS THE SAFETY PROPERTY. A failed forward leaves the standing
    /// alone, so the mailbox stays a candidate and the next nudge retries;
    /// reaping anyway would remove the one thing that makes anybody look at it
    /// again, after failing to pass its mail on.
    [Fact]
    public async Task AFailedForward_DoesNotReap()
    {
        var bin = WriteStubBin(failOn: "forward");

        var (exit, stdout, stderr) = await RunAsync(Nudge(), bin,
            ("REAPER_DISPOSITION", "forward-all-to-role"), ("REAPER_FORWARD_TO", "maintainer"));

        Assert.Equal(0, exit);   // the payload always answers the wire
        Assert.Equal("{\"effect\":\"noop\"}\n", stdout.Replace("\r\n", "\n"));
        Assert.Contains("NOT reaping", stderr);
        Assert.Equal(["mail forward main@laptop-a --to maintainer --by reaper"], Verbs());
    }

    /// A drop is not a delete: nothing is forwarded, the standing goes, and the
    /// envelopes stay on the append-only ledger where `mail.reap` explains them.
    [Fact]
    public async Task DropAll_ReapsAndNeverForwards()
    {
        var bin = WriteStubBin();

        var (exit, _, _) = await RunAsync(Nudge(), bin, ("REAPER_DISPOSITION", "drop-all"));

        Assert.Equal(0, exit);
        Assert.Equal(["mail reap main@laptop-a --by reaper"], Verbs());
    }

    /// `hold` is a real disposition, not a no-op by omission — d6 lists it —
    /// and the difference from an absent rule is that this one says so.
    [Fact]
    public async Task Hold_RunsNothingAtAll_AndSaysSo()
    {
        var bin = WriteStubBin();

        var (exit, _, stderr) = await RunAsync(Nudge(), bin, ("REAPER_DISPOSITION", "hold"));

        Assert.Equal(0, exit);
        Assert.Empty(Verbs());
        Assert.Contains("holding main@laptop-a", stderr);
    }

    // ---- the refusals ------------------------------------------------------

    /// Installed is not consented. An operator says how mail is disposed of;
    /// a payload that merely exists disposes of nothing.
    [Fact]
    public async Task WithNoDisposition_ItRefuses_AndRunsNothing()
    {
        var bin = WriteStubBin();

        var (exit, stdout, stderr) = await RunAsync(Nudge(), bin);

        Assert.Equal(0, exit);
        Assert.Equal("{\"effect\":\"noop\"}\n", stdout.Replace("\r\n", "\n"));
        Assert.Empty(Verbs());
        Assert.Contains("no REAPER_DISPOSITION", stderr);
    }

    /// Two ways to be told to forward nowhere, and both refuse rather than
    /// guess: an unknown disposition word, and a forward with no target.
    [Theory]
    [InlineData("forward-all-to-role", "", "REAPER_FORWARD_TO names no address")]
    [InlineData("forward-everything", "maintainer", "unknown REAPER_DISPOSITION")]
    public async Task AnUnusableRule_RefusesAndRunsNothing(string disposition, string to, string expected)
    {
        var bin = WriteStubBin();

        var (_, _, stderr) = await RunAsync(Nudge(), bin,
            ("REAPER_DISPOSITION", disposition), ("REAPER_FORWARD_TO", to));

        Assert.Empty(Verbs());
        Assert.Contains(expected, stderr);
    }

    /// The event guard, `turn-claude.sh`'s reasoning applied to a verb that
    /// removes standing: `MailNudge` is internal, so a payload registered on a
    /// hook event would be reaping mailboxes on somebody's keystrokes.
    [Fact]
    public async Task OnAnyOtherEvent_ItRefuses()
    {
        var bin = WriteStubBin();
        var stopEvent =
            """{"v":1,"dispatchId":"d-1","event":{"type":"Stop","payload":{"address":"main@laptop-a"}}}""";

        var (_, stdout, stderr) = await RunAsync(stopEvent, bin,
            ("REAPER_DISPOSITION", "drop-all"));

        Assert.Empty(Verbs());
        Assert.Contains("mail-nudge event", stderr);
        Assert.Equal("{\"effect\":\"noop\"}\n", stdout.Replace("\r\n", "\n"));
    }

    /// A ROLE nudge (the ordinary "this role is falling behind" kind) names no
    /// mailbox — `address` rides only a dead-mailbox nudge — and this payload
    /// has nothing to say about one. Testing the field rather than the reason
    /// string is deliberate: the address is what it needs.
    [Fact]
    public async Task ANudgeWithNoAddress_IsNotItsBusiness()
    {
        var bin = WriteStubBin();

        var (_, _, stderr) = await RunAsync(Nudge(address: null), bin,
            ("REAPER_DISPOSITION", "drop-all"), ("REAPER_FORWARD_TO", "maintainer"));

        Assert.Empty(Verbs());
        Assert.Contains("names no mailbox", stderr);
    }

    // ---- and the whole thing, for real -------------------------------------

    /// No stub: the shipped payload, the REAL engine, a real store. A mailbox
    /// with a cursor and stranded mail is forwarded to a live role and reaped —
    /// and afterwards the copy is on the ledger carrying `forwardedFrom`, the
    /// original is still there addressed where it was, and the cursor is gone.
    [Fact]
    public async Task AgainstTheRealEngine_TheCorpseIsForwardedAndReaped()
    {
        var store = new MailStore(_mailDir);
        var cursors = new MailCursors(store);
        var dead = new MailAddress("main", "laptop-a");
        // A standing with nothing delivered — what a window that opened,
        // registered and closed leaves behind — and then mail for it.
        MailFixtures.AppendOk(store, MailFixtures.Envelope(id: "m-seed", to: "other"));
        CursorFixtures.AdvanceOk(cursors, cursors.Pending(dead, hookSession: null));
        MailFixtures.AppendOk(store, MailFixtures.Envelope(id: "m-01", to: "main@laptop-a", ttl: null));
        var cursorPath = cursors.CursorPath("main", "laptop-a");
        Assert.True(File.Exists(cursorPath));

        var (exit, stdout, stderr) = await RunAsync(Nudge(), DogfoodInfra.EngineBin(),
            ("REAPER_DISPOSITION", "forward-all-to-role"), ("REAPER_FORWARD_TO", "maintainer"));

        Assert.Equal(0, exit);
        Assert.Equal("{\"effect\":\"noop\"}\n", stdout.Replace("\r\n", "\n"));
        Assert.Contains("forwarded 1 from main@laptop-a to maintainer", stderr);
        Assert.Contains("reaped main@laptop-a", stderr);

        var envelopes = store.Read().Select(l => l.Envelope).OfType<MailEnvelope>().ToList();
        var copy = Assert.Single(envelopes, e => e.ForwardedFrom is not null);
        Assert.Equal("maintainer", copy.To);
        Assert.Equal(new MailForwardedFrom("m-01", "main@laptop-a"), copy.ForwardedFrom);
        Assert.Equal("reaper", copy.From.Agent);
        // The original stays exactly where it was, undelivered, on the chain.
        Assert.Equal("main@laptop-a", envelopes.Single(e => e.Id == "m-01").To);
        // …and the standing is gone, which is what stops the watcher nudging
        // about this box forever.
        Assert.False(File.Exists(cursorPath));
    }
}
