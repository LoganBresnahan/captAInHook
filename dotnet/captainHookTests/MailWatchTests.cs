using System.Text.Json;
using CaptainHook.Mail;

namespace CaptainHook.Tests;

// ADR-0017 decision 4, slice `watcher-brain` — `captainHook mail watch --once`,
// the CLI half. Verification, never a schedule: it evaluates once, prints, logs
// one `watch.verdict` line, and exits. The brain is pinned by
// `WatcherBrainTests`; this file pins the VERB — what it refuses, what it names
// on its report about the inputs it cannot see, and that it writes nothing to
// the store or the cursors.
public class MailWatchTests
{
    private const long Now = 5_000_000;

    // ---- refusals -----------------------------------------------------------------

    [Fact]
    public void WithoutOnce_IsRefused_ItIsNotASchedule()
    {
        using var w = new WatchWorld();
        var (exit, _, err) = w.Run(argv: []);
        Assert.Equal(1, exit);
        Assert.Contains("--once is required", err);
        Assert.Contains("not a schedule", err);
    }

    [Fact]
    public void UnknownArgument_IsRefused()
    {
        using var w = new WatchWorld();
        var (exit, _, err) = w.Run(argv: ["--once", "--every", "5m"]);
        Assert.Equal(1, exit);
        Assert.Contains("unexpected argument '--every'", err);
        Assert.Contains(MailWatch.Usage, err);
    }

    // ---- the inputs, named ---------------------------------------------------------

    [Fact]
    public void AbsentWatchJson_SaysNoRuleCanEverFire_Exit0()
    {
        using var w = new WatchWorld();
        var (exit, lines, _) = w.Run();
        Assert.Equal(0, exit);
        Assert.Contains("watch.json: absent — no rules, so no robot nudge can ever fire", lines);
        Assert.Contains("no rule names a role — nothing to evaluate", lines);
        Assert.Contains("next check: nothing armed", lines);
    }

    [Fact]
    public void MalformedWatchJson_SaysSo_AndWarnsOnTheTrail_Exit0()
    {
        using var w = new WatchWorld();
        using var log = new CapturedLog();
        w.WriteWatchRaw("""{ "version": 1, "rules": [ { "role": "reviewer" } ] }""");
        var (exit, lines, _) = w.Run();
        Assert.Equal(0, exit);
        Assert.Contains(lines, l => l.StartsWith("watch.json: malformed — no robot nudge can fire until it parses"));
        Assert.Contains(log.Events, e => e.Evt == "watch.malformed");
        Assert.Contains(log.Events, e => e.Evt == "watch.verdict");
    }

    [Fact]
    public void AbsentHandlersJson_ReportsEveryRoleUnserved()
    {
        using var w = new WatchWorld();
        w.Rules(Rule("reviewer", quietFor: "0s"));
        w.Send("m-01", "reviewer", MailPriority.Urgent);
        var (_, lines, _) = w.Run();
        Assert.Contains("handlers.json: absent or malformed — nothing registered, every role is unserved", lines);
        Assert.Contains(lines, l => l.StartsWith("reviewer: unserved · 1 unread · no dispatch seen · unserved —"));
        Assert.DoesNotContain(lines, l => l.StartsWith("WOULD NUDGE"));
    }

    // ---- presence: only what the CLI can claim ----------------------------------------

    /// No stdin ⇒ nobody is claimed live, and the report says the CLI cannot see
    /// the daemon's view. With a turn payload and a due rule, the nudge would go.
    [Fact]
    public void NoStdin_ClaimsNoLiveSession_AndSaysWhy()
    {
        using var w = new WatchWorld();
        w.Register(TurnPayload("turn-claude"));
        w.Rules(Rule("reviewer", quietFor: "0s"));
        w.Send("m-01", "reviewer", MailPriority.Urgent);
        var (exit, lines, _) = w.Run(stdin: "");
        Assert.Equal(0, exit);
        Assert.Contains("presence: not visible from the CLI (the daemon's own view) — no session treated as live", lines);
        Assert.Contains("handlers.json: turn payload on mail-nudge: installed; human-held roles: none", lines);
        Assert.Contains("WOULD NUDGE reviewer: m-01", lines);
        Assert.Contains(lines, l => l.StartsWith("  reason: 1 unread past quiet (0s+) · no live session"));
        Assert.Contains("  | [captAInHook mail] 1 message(s) for 'reviewer':", lines);
    }

    /// Hook-shaped stdin names the calling window; if IT holds the role's
    /// cursor, the role is live and the nudge is held.
    [Fact]
    public void HookShapedStdin_MakesTheCallingWindowLive()
    {
        using var w = new WatchWorld();
        w.Register(TurnPayload("turn-claude"), Digest("mail-digest-reviewer", "reviewer"));
        w.Rules(Rule("reviewer", quietFor: "0s"));
        w.Send("m-00", "reviewer");
        w.Digest("reviewer", "s-1");                        // s-1 holds a cursor
        w.Send("m-01", "reviewer", MailPriority.Urgent);    // …and has not read this
        var (_, lines, _) = w.Run(stdin: JsonSerializer.Serialize(new { session_id = "s-1", cwd = "/x" }));
        Assert.Contains("presence: only what the CLI can claim — the calling session s-1 is live now; no other window is visible from here", lines);
        Assert.Contains(lines, l => l.StartsWith("reviewer: mixed · 1 unread · freshest dispatch 0s ago · live-session —"));
        Assert.DoesNotContain(lines, l => l.StartsWith("WOULD NUDGE"));
        Assert.Contains("next check: in 10m", lines);      // armed for presence to expire

        // Another window asking: s-1 is not live from s-2's point of view.
        var (_, other, _) = w.Run(stdin: JsonSerializer.Serialize(new { session_id = "s-2" }));
        Assert.Contains("WOULD NUDGE reviewer: m-01", other);
    }

    /// Behind a hook the stdin is an exec-wire envelope; the session rides in it.
    [Fact]
    public void ExecWireStdin_NamesTheSessionToo()
    {
        using var w = new WatchWorld();
        w.Register(TurnPayload("turn-claude"), Digest("mail-digest-reviewer", "reviewer"));
        w.Rules(Rule("reviewer", quietFor: "0s"));
        w.Send("m-00", "reviewer");
        w.Digest("reviewer", "s-7");
        w.Send("m-01", "reviewer", MailPriority.Urgent);
        var (_, lines, _) = w.Run(stdin: DigestFixtures.Request("d-9", "UserPromptSubmit", "s-7"));
        Assert.Contains(lines, l => l.Contains("the calling session s-7 is live now"));
        Assert.Contains(lines, l => l.Contains("· live-session —"));
    }

    // ---- state: none, unless --as-if-quiet -----------------------------------------------

    [Fact]
    public void WithoutState_NothingIsPastAQuietThreshold_AndTheReportSaysHowToSeePastIt()
    {
        using var w = new WatchWorld();
        w.Register(TurnPayload("turn-claude"));
        w.Rules(Rule("reviewer", quietFor: "10min"));
        w.Send("m-01", "reviewer", MailPriority.Urgent);
        var (_, lines, _) = w.Run();
        Assert.Contains(lines, l => l.StartsWith("state: none — every unread envelope is first seen now"));
        Assert.Contains(lines, l => l.Contains("· not-due — 1 unread, none past its quiet threshold"));
        Assert.Contains("next check: in 10m", lines);
        Assert.DoesNotContain(lines, l => l.StartsWith("WOULD NUDGE"));
    }

    [Fact]
    public void AsIfQuiet_CrossesEveryThreshold()
    {
        using var w = new WatchWorld();
        w.Register(TurnPayload("turn-claude"));
        w.Rules(Rule("reviewer", quietFor: "10min"));
        w.Send("m-01", "reviewer", MailPriority.Urgent);
        var (_, lines, _) = w.Run(argv: ["--once", "--as-if-quiet"]);
        Assert.Contains("state: --as-if-quiet — every unread envelope treated as past every quiet threshold", lines);
        Assert.Contains("WOULD NUDGE reviewer: m-01", lines);
        Assert.Contains(lines, l => l.StartsWith("  reason: 1 unread past quiet (596h31m+)"));
    }

    // ---- read-only, and the trail line ------------------------------------------------------

    /// The verb reads the store and every cursor and writes NONE of them: no
    /// cursor file appears for a role read sessionless, no existing cursor moves.
    [Fact]
    public void Once_WritesNoCursor_AndMovesNone()
    {
        using var w = new WatchWorld();
        w.Register(TurnPayload("turn-claude"), Digest("mail-digest-reviewer", "reviewer"));
        w.Rules(Rule("reviewer", quietFor: "0s"), Rule("ops", quietFor: "0s"));
        w.Send("m-00", "reviewer");
        w.Digest("reviewer", "s-1");
        w.Send("m-01", "reviewer", MailPriority.Urgent);
        w.Send("m-02", "ops", MailPriority.Urgent);
        var before = File.ReadAllText(w.CursorPath("reviewer", "s-1"));
        var files = Directory.GetFiles(w.MailDir).Order().ToArray();

        w.Run(argv: ["--once", "--as-if-quiet"]);

        Assert.Equal(before, File.ReadAllText(w.CursorPath("reviewer", "s-1")));
        Assert.Equal(files, Directory.GetFiles(w.MailDir).Order().ToArray());   // no cursor for `ops`, no lock, nothing
    }

    [Fact]
    public void Once_LogsOneVerdictLine_WithTheRolesAndTheNudges()
    {
        using var w = new WatchWorld();
        using var log = new CapturedLog();
        w.Register(TurnPayload("turn-claude"));
        w.Rules(Rule("reviewer", quietFor: "0s"));
        w.Send("m-01", "reviewer", MailPriority.Urgent);
        w.Run(stdin: JsonSerializer.Serialize(new { session_id = "s-3" }));

        var e = Assert.Single(log.Events, e => e.Evt == "watch.verdict");
        Assert.Equal("watch", e.Src);
        Assert.Equal("s-3", e.Fields.SessionId);
        var nudges = Assert.IsAssignableFrom<System.Collections.IEnumerable>(e.Fields.Data!["nudges"]).Cast<object>().ToList();
        Assert.Single(nudges);
        Assert.Equal(false, e.Fields.Data["asIfQuiet"]);
        Assert.Equal(0L, e.Fields.Data["nextCheckInMs"]);   // quietFor 0: the projected re-arm is now
    }

    /// The whole set of mailboxes for a role, exactly as the brain sees them: a
    /// role with cursors yields one per cursor keyed by the file's instance; a
    /// role with none yields the sessionless read.
    [Fact]
    public void ReadMailboxes_OnePerCursor_ElseSessionless()
    {
        using var w = new WatchWorld();
        w.Send("m-00", "reviewer");
        w.Digest("reviewer", "s-1");
        w.Digest("reviewer", "s-2", instance: "robot");
        w.Send("m-01", "reviewer", MailPriority.Urgent);
        w.Send("m-02", "ops");

        var boxes = MailWatch.ReadMailboxes(new MailCursors(new MailStore(w.MailDir)), ["reviewer", "ops"]);
        Assert.Equal(["reviewer@robot", "reviewer@s-1", "ops"], boxes.Select(b => b.Address.ToString()));
        Assert.All(boxes.Take(2), b => Assert.Equal(["m-01"], b.Pending.Select(p => p.Envelope.Id)));
        Assert.Equal(["m-02"], boxes[2].Pending.Select(p => p.Envelope.Id));
    }

    /// A unicast to a mailbox that has no cursor file — a `--as` registration
    /// that has not fired yet, an answer to a reaped window — is still watched:
    /// the ledger names the mailbox, and the gatherer reads it as the fresh
    /// mailbox it is. Its broadcast history is not unread (the window read it).
    [Fact]
    public void ReadMailboxes_SeesAUnicastToAMailboxWithNoCursor()
    {
        using var w = new WatchWorld();
        w.Send("m-00", "reviewer");
        w.Digest("reviewer", "s-1");
        w.Send("u-01", "reviewer@robot", MailPriority.Urgent);   // nobody has ever read as `robot`
        w.Send("o-01", "ops@nobody", MailPriority.Urgent);       // ops has no cursor at all

        var boxes = MailWatch.ReadMailboxes(new MailCursors(new MailStore(w.MailDir)), ["reviewer", "ops"]);
        Assert.Equal(["reviewer@s-1", "reviewer@robot", "ops", "ops@nobody"], boxes.Select(b => b.Address.ToString()));
        Assert.Equal(["m-00", "u-01"], boxes[1].Pending.Select(p => p.Envelope.Id));   // fresh: from the anchor
        Assert.Equal(["o-01"], boxes[3].Pending.Select(p => p.Envelope.Id));

        w.Register(TurnPayload("turn-claude"));
        w.Rules(Rule("reviewer", quietFor: "0s"), Rule("ops", quietFor: "0s"));
        var (_, lines, _) = w.Run();
        Assert.Contains("WOULD NUDGE reviewer: u-01", lines);   // m-00 is not unread: s-1 read it
        Assert.Contains("WOULD NUDGE ops: o-01", lines);
    }

    /// `--as-if-quiet` is past EVERY threshold the parser accepts, not a day.
    [Fact]
    public void AsIfQuiet_CrossesTheLongestLegalThreshold()
    {
        using var w = new WatchWorld();
        w.Register(TurnPayload("turn-claude"));
        w.Rules(Rule("reviewer", quietFor: "500h"));
        w.Send("m-01", "reviewer", MailPriority.Urgent);
        var (_, lines, _) = w.Run(argv: ["--once", "--as-if-quiet"]);
        Assert.Contains("WOULD NUDGE reviewer: m-01", lines);
    }

    // ---- fixtures ------------------------------------------------------------------------

    private static object Rule(string role, string priority = ">=urgent", string quietFor = "10min",
        int perEnvelope = 1, int perRoleHour = 4) => new
    {
        role,
        when = new { priority, quietFor },
        budget = new { perEnvelope, perRoleHour },
    };

    private static object Digest(string name, string role) => new
    {
        name,
        command = "/usr/bin/captainHook",
        args = new[] { "mail", "digest", "--role", role, "--seam", "ambient" },
        events = new[] { "user-prompt-submit" },
        mode = "oneshot",
        failMode = "open",
    };

    private static object DigestAs(string name, string role, string instance) => new
    {
        name,
        command = "/usr/bin/captainHook",
        args = new[] { "mail", "digest", "--role", role, "--as", instance, "--seam", "ambient" },
        events = new[] { "user-prompt-submit" },
        mode = "oneshot",
        failMode = "open",
    };

    private static object TurnPayload(string name) => new
    {
        name,
        command = "/usr/bin/turn-claude.sh",
        args = Array.Empty<string>(),
        events = new[] { "mail-nudge" },
        mode = "oneshot",
        failMode = "open",
    };

    // ---- the dead-mailbox rule (ADR-0018 d6) -------------------------------------

    /// The CLI's half of the second rule, and the one claim that is the VERB's
    /// rather than the brain's: a `reaper` rule widens the sweep to every role
    /// with a cursor file. The dead box here belongs to `reviewer`, which has no
    /// rule of its own — gathering only rule roles would make the whole rule
    /// unreachable for exactly the boxes the field report found.
    [Fact]
    public void DeadMailbox_OfARoleWithNoRule_IsFound_AndWouldNudgeTheReaper()
    {
        using var w = new WatchWorld();
        using var log = new CapturedLog();
        w.Register(TurnPayload("turn-claude"));
        w.Rules(Rule("reaper", quietFor: "0s"));
        w.Send("m-00", "reviewer");
        w.Digest("reviewer", "s-1");                       // the window that dies, cursor and all
        w.Send("m-01", "reviewer", MailPriority.Urgent);   // …holding this

        var (exit, lines, _) = w.Run(stdin: "");
        Assert.Equal(0, exit);
        Assert.Contains(lines, l => l.StartsWith("reviewer@s-1: dead-mailbox candidate · 1 stranded · no dispatch seen · nudge —"));
        Assert.Contains(lines, l => l.StartsWith("WOULD NUDGE reaper about reviewer@s-1: m-01"));
        Assert.Contains(lines, l => l.Contains("reason: dead-mailbox reviewer@s-1 · 1 stranded past quiet"));

        var verdict = Assert.Single(log.Events, e => e.Evt == "watch.verdict");
        Assert.Contains("reviewer@s-1", verdict.ToJson());
    }

    /// …and without a reaper rule the sweep does not widen: the operator wrote
    /// no consent for the reaper, so its lane does not exist and neither does
    /// the reading of somebody else's mailbox.
    [Fact]
    public void WithoutAReaperRule_NoOtherRolesMailboxIsEvenRead()
    {
        using var w = new WatchWorld();
        w.Register(TurnPayload("turn-claude"));
        w.Rules(Rule("ops", quietFor: "0s"));
        w.Send("m-00", "reviewer");
        w.Digest("reviewer", "s-1");
        w.Send("m-01", "reviewer", MailPriority.Urgent);

        var (_, lines, _) = w.Run(stdin: "");
        Assert.DoesNotContain(lines, l => l.Contains("dead-mailbox"));
        Assert.DoesNotContain(lines, l => l.StartsWith("reviewer"));
        Assert.DoesNotContain(lines, l => l.StartsWith("WOULD NUDGE"));
    }

    /// A registered `--as` mailbox is standing an operator asked for. Named
    /// through the REAL registration file, because that is where the fact lives
    /// and a lookalike would drift from it.
    [Fact]
    public void RegisteredDurableMailbox_IsNeverACorpse()
    {
        using var w = new WatchWorld();
        w.Register(TurnPayload("turn-claude"), DigestAs("robot-box", "reviewer", "robot"));
        w.Rules(Rule("reaper", quietFor: "0s"));
        w.Digest("reviewer", "s-2", instance: "robot");    // the durable box comes into being
        w.Send("u-01", "reviewer@robot", MailPriority.Urgent);

        var (_, lines, _) = w.Run(stdin: "");
        Assert.DoesNotContain(lines, l => l.Contains("dead-mailbox"));
        Assert.DoesNotContain(lines, l => l.StartsWith("WOULD NUDGE"));
    }

    /// `--as-if-quiet` reaches the dead lane too — it is the rule an operator
    /// most wants to preview, and its memory is keyed by the ADDRESS rather than
    /// the role, so the pretence has to be written under both.
    [Fact]
    public void AsIfQuiet_SeesPastTheDeadMailboxThreshold()
    {
        using var w = new WatchWorld();
        w.Register(TurnPayload("turn-claude"));
        w.Rules(Rule("reaper", quietFor: "10min"));
        w.Send("m-00", "reviewer");
        w.Digest("reviewer", "s-1");
        w.Send("m-01", "reviewer", MailPriority.Urgent);

        var (_, plain, _) = w.Run(stdin: "");
        Assert.Contains(plain, l => l.StartsWith("reviewer@s-1: dead-mailbox candidate") && l.EndsWith("none past its quiet threshold"));
        Assert.DoesNotContain(plain, l => l.StartsWith("WOULD NUDGE"));

        var (_, past, _) = w.Run(argv: ["--once", "--as-if-quiet"], stdin: "");
        Assert.Contains(past, l => l.StartsWith("WOULD NUDGE reaper about reviewer@s-1: m-01"));
    }

    private sealed class WatchWorld : IDisposable
    {
        public string Home { get; } = Path.Combine(Path.GetTempPath(), "chk-watch-" + Guid.NewGuid().ToString("N")[..8]);
        public string MailDir => Path.Combine(Home, "mail");
        private string HandlersPath => Path.Combine(Home, "handlers.json");
        private string WatchPath => Path.Combine(Home, "watch.json");

        public WatchWorld() => Directory.CreateDirectory(Home);

        public void Register(params object[] handlers) =>
            File.WriteAllText(HandlersPath, JsonSerializer.Serialize(new { version = 1, handlers }));

        public void Rules(params object[] rules) =>
            File.WriteAllText(WatchPath, JsonSerializer.Serialize(new { version = 1, rules }));

        public void WriteWatchRaw(string text) => File.WriteAllText(WatchPath, text);

        public void Send(string id, string to, MailPriority priority = MailPriority.Ambient) =>
            MailFixtures.AppendOk(new MailStore(MailDir),
                MailFixtures.Envelope(id: id, to: to, priority: priority, ttl: to.Contains('@') ? null : 3));

        public void Digest(string role, string? session, string? instance = null)
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            string[] argv = instance is null
                ? ["--role", role, "--seam", "ambient"]
                : ["--role", role, "--as", instance, "--seam", "ambient"];
            var exit = MailDigest.Run(argv,
                new StringReader(DigestFixtures.Request("d-1", "UserPromptSubmit", session)),
                stdout, stderr, mailDir: MailDir, harnessDir: TestUtil.NoHarnessDir());
            Assert.True(exit == 0, $"digest exited {exit}: {stderr}");
        }

        public string CursorPath(string role, string? key) =>
            new MailCursors(new MailStore(MailDir)).CursorPath(role, key);

        public (int Exit, string[] Lines, string Err) Run(string[]? argv = null, string? stdin = null)
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exit = MailWatch.Run(argv ?? ["--once"], new StringReader(stdin ?? ""), stdout, stderr,
                mailDir: MailDir, handlersPath: HandlersPath, watchPath: WatchPath, nowMs: Now);
            return (exit, stdout.ToString().Split('\n').Select(l => l.TrimEnd('\r')).ToArray(), stderr.ToString());
        }

        public void Dispose()
        {
            try { Directory.Delete(Home, recursive: true); } catch { /* best-effort */ }
        }
    }
}
