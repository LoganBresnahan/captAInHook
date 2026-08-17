using System.Text.Json;
using CaptainHook.Mail;

namespace CaptainHook.Tests;

/// ADR-0017 d2, slice `mail-status` (roadmap item 22) — the human channel:
/// always on, ruleless, and the one slice of that ADR that pays off by itself.
///
/// Two claims carry the slice, and the tests are split along them. The LINE is
/// a display contract — what a human reads off a status bar at a glance — so it
/// is pinned as goldens on a pure function, including the two silences (nothing
/// pending, nothing readable), because a status bar that says `📬 0` has made
/// noise out of nothing. The ROLE SET is the interesting half: it comes from
/// the policy evaluation the dispatcher already performs, so a window is told
/// about exactly the mail some digest of its own would hand it — never a second
/// declaration of which window is which, and never a count for mail that policy
/// will keep away from this window forever.
///
/// And the whole verb reads: it is wired into a surface that renders on a
/// human's cadence, so a drive proves it creates no directory, no cursor, and
/// no trail line.
public class MailStatusTests
{
    // ---- the line, as goldens ----------------------------------------------

    /// The display contract. `held` mail counts (it is undelivered, which is the
    /// state the line exists to surface) and `expired` mail does not (it is
    /// spent — the next advance drops it, and pointing a human at mail no digest
    /// will ever hand over is the one way this line can lie).
    [Theory]
    [InlineData(1, 0, 0, false, "📬 1")]
    [InlineData(3, 2, 0, false, "📬 3 · 2 urgent")]
    [InlineData(3, 3, 0, false, "📬 3 · 3 urgent")]
    [InlineData(2, 1, 5, false, "📬 2 · 1 urgent")]          // five expired, uncounted
    [InlineData(1, 0, 0, true, "📬 maintainer 1")]
    [InlineData(4, 1, 0, true, "📬 maintainer 4 · 1 urgent")]
    [InlineData(0, 0, 3, false, null)]                        // only spent mail: nothing to say
    [InlineData(0, 0, 0, true, null)]
    public void Line_IsTheStatusBarContract(int pending, int urgent, int expired, bool qualify, string? expected)
    {
        var view = View("maintainer", "s-1", pending, urgent, expired);
        Assert.Equal(expected, MailStatus.Line("maintainer", view, qualify));
    }

    // ---- the role set, from the policy the dispatcher already applies -------

    /// The happy path end to end: a real store, a real registration, a real
    /// policy, and one line on stdout for the caller's own cursor.
    [Fact]
    public void Status_CountsThisWindowsMail_ForTheRoleItsDigestReads()
    {
        using var w = new StatusWorld();
        w.Register(Digest("mail-digest-maintainer", "maintainer"));
        w.Send("m-01", "maintainer");
        w.Send("m-02", "maintainer", MailPriority.Urgent);
        w.Send("other-role-mail", "reviewer");

        Assert.Equal(["📬 2 · 1 urgent"], w.Run(session: "s-1"));
    }

    /// The count is the CALLER's, not the role's. Two windows read one role;
    /// one of them has already picked its mail up, and the bar in that window
    /// must go quiet while the other still says two.
    [Fact]
    public void Status_IsPerCursor_SoOneWindowGoingQuietLeavesTheOtherLoud()
    {
        using var w = new StatusWorld();
        w.Register(Digest("mail-digest-maintainer", "maintainer"));
        w.Send("m-01", "maintainer");
        w.Send("m-02", "maintainer", MailPriority.Urgent);

        Assert.Equal(["📬 2 · 1 urgent"], w.Run(session: "s-1"));
        Assert.Equal(["📬 2 · 1 urgent"], w.Run(session: "s-2"));

        w.Digest("maintainer", "s-1");                       // s-1 reads its mail

        Assert.Empty(w.Run(session: "s-1"));
        Assert.Equal(["📬 2 · 1 urgent"], w.Run(session: "s-2"));
    }

    /// A caller with no session is the SESSIONLESS reader — a real cursor with a
    /// real count (ADR-0016 d4), not a guess about which window is asking. So a
    /// status call with no stdin still answers, and answers about that cursor.
    [Fact]
    public void Status_WithNoSession_ReadsTheSessionlessCursor()
    {
        using var w = new StatusWorld();
        w.Register(Digest("mail-digest-maintainer", "maintainer"));
        w.Send("m-01", "maintainer");

        Assert.Equal(["📬 1"], w.Run(stdin: ""));
        w.Digest("maintainer", session: null);
        Assert.Empty(w.Run(stdin: ""));
    }

    /// The consent surface is the one already on disk. A handler-named deny for
    /// this window's project means no digest of this window's will ever hand
    /// over this role's mail — so its bar says nothing about it, while the other
    /// project's window is told.
    [Fact]
    public void Status_SaysNothingAboutARoleThisWindowsPolicyDenies()
    {
        using var w = new StatusWorld();
        w.Register(Digest("mail-digest-maintainer", "maintainer"));
        w.Send("m-01", "maintainer");
        w.Policy(new { handler = "mail-digest-maintainer", project = "/work/beta", decision = "deny" });

        Assert.Empty(w.Run(session: "s-1", cwd: "/work/beta/sub"));
        Assert.Equal(["📬 1"], w.Run(session: "s-1", cwd: "/work/alpha"));
    }

    /// An EVENT-level deny stops the dispatch whole, so it stops the count too:
    /// `Work: false` is the dispatcher declining to fan out at all.
    [Fact]
    public void Status_SaysNothingWhenTheEventItselfIsDenied()
    {
        using var w = new StatusWorld();
        w.Register(Digest("mail-digest-maintainer", "maintainer"));
        w.Send("m-01", "maintainer");
        w.Policy(new { @event = "user-prompt-submit", session = "s-quiet", decision = "deny" });

        Assert.Empty(w.Run(session: "s-quiet"));
        Assert.Equal(["📬 1"], w.Run(session: "s-loud"));
    }

    /// Registered on two events, denied on one: the mail still arrives at the
    /// other seam, so the bar still says so. "Reachable by any of its events" is
    /// the honest reading of a multi-event registration.
    [Fact]
    public void Status_CountsARoleStillReachableAtItsOtherSeam()
    {
        using var w = new StatusWorld();
        w.Register(Digest("mail-digest-maintainer", "maintainer", "user-prompt-submit", "stop"));
        w.Send("m-01", "maintainer");
        w.Policy(new { handler = "mail-digest-maintainer", @event = "user-prompt-submit", decision = "deny" });

        Assert.Equal(["📬 1"], w.Run(session: "s-1"));
    }

    /// Two roles in one window is the swarm case, and `📬 2 · 1 urgent` twice
    /// would be unreadable — so and only so, the role is named. Sorted, so the
    /// bar does not reorder itself between renders.
    [Fact]
    public void Status_NamesTheRoleOnlyWhenTheWindowReadsMoreThanOne()
    {
        using var w = new StatusWorld();
        w.Register(Digest("mail-digest-maintainer", "maintainer"), Digest("mail-digest-reviewer", "reviewer"));
        w.Send("m-01", "reviewer", MailPriority.Urgent);
        w.Send("m-02", "maintainer");
        w.Send("m-03", "maintainer");

        Assert.Equal(["📬 maintainer 2", "📬 reviewer 1 · 1 urgent"], w.Run(session: "s-1"));
    }

    /// One role, two registrations (the normal ambient + urgent shape) is ONE
    /// cursor and therefore one line — and not the two-role naming, which would
    /// otherwise switch on a detail of how the seams were registered.
    [Fact]
    public void Status_CollapsesTwoSeamsOfOneRoleIntoOneLine()
    {
        using var w = new StatusWorld();
        w.Register(
            Digest("mail-digest-ambient", "maintainer"),
            Digest("mail-digest-urgent", "maintainer", "user-prompt-submit"));
        w.Send("m-01", "maintainer");

        Assert.Equal(["📬 1"], w.Run(session: "s-1"));
    }

    /// Recognition is the real parser's: a `mail digest` registration the verb
    /// itself would refuse (no `--role`) names no role, so it cannot contribute
    /// a count for mail nothing will ever deliver.
    [Fact]
    public void Status_IgnoresARegistrationTheDigestVerbWouldRefuse()
    {
        using var w = new StatusWorld();
        w.Register(
            new { name = "broken-digest", command = "/bin/true", args = new[] { "mail", "digest", "--seam", "ambient" },
                  events = new[] { "user-prompt-submit" }, mode = "oneshot", failMode = "open" },
            new { name = "not-mail-at-all", command = "/bin/true", args = new[] { "--role", "maintainer" },
                  events = new[] { "user-prompt-submit" }, mode = "oneshot", failMode = "open" });
        w.Send("m-01", "maintainer");

        Assert.Empty(w.Run(session: "s-1"));
    }

    /// Every state of the world that means "nothing to say" says nothing, on
    /// stdout AND in the exit code: a display command that failed loudly on an
    /// absent config would put an error where a human expects a count.
    [Theory]
    [InlineData("absent")]
    [InlineData("malformed")]
    [InlineData("no-digest")]
    [InlineData("no-mail")]
    public void Status_IsSilentAndSuccessful_WhenThereIsNothingToSay(string world)
    {
        using var w = new StatusWorld();
        switch (world)
        {
            case "absent": break;                                  // no handlers.json at all
            case "malformed": w.WriteHandlersRaw("{ not json"); break;
            case "no-digest":
                w.Register(new { name = "guard", command = "/bin/true", events = new[] { "pre-tool-use" },
                                 mode = "oneshot", failMode = "open" });
                break;
            case "no-mail": w.Register(Digest("mail-digest-maintainer", "maintainer")); break;
        }
        if (world != "no-mail") w.Send("m-01", "maintainer");

        var (exit, lines, err) = w.RunFull(session: "s-1");
        Assert.Equal(0, exit);
        Assert.Empty(lines);
        Assert.Equal("", err);
    }

    /// Malformed stdin is an unknown caller, not a broken bar. It still answers
    /// — about the sessionless cursor, the question it was actually able to ask.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ not json")]
    [InlineData("[1,2,3]")]
    [InlineData("""{"session_id":42,"cwd":null}""")]
    [InlineData("""{"session_id":"\ud800"}""")]
    public void Status_WithUnreadableStdin_StillAnswers(string stdin)
    {
        using var w = new StatusWorld();
        w.Register(Digest("mail-digest-maintainer", "maintainer"));
        w.Send("m-01", "maintainer");

        var (exit, lines, err) = w.RunFull(stdin: stdin);
        Assert.Equal(0, exit);
        Assert.Equal(["📬 1"], lines);
        Assert.Equal("", err);
    }

    /// `workspace.current_dir` is honored beside `cwd` — Claude Code's status
    /// payload carries both, and a policy scoped by project must see the one
    /// that is there.
    [Fact]
    public void Status_ReadsTheWorkspaceDir_WhenCwdIsAbsent()
    {
        using var w = new StatusWorld();
        w.Register(Digest("mail-digest-maintainer", "maintainer"));
        w.Send("m-01", "maintainer");
        w.Policy(new { handler = "mail-digest-maintainer", project = "/work/beta", decision = "deny" });

        Assert.Empty(w.Run(stdin: """{"session_id":"s-1","workspace":{"current_dir":"/work/beta/sub"}}"""));
        Assert.Equal(["📬 1"], w.Run(stdin: """{"session_id":"s-1","workspace":{"current_dir":"/work/alpha"}}"""));
    }

    /// Arguments are the ONE thing it refuses: a typo'd flag is a wiring mistake
    /// the human can fix, and reporting it as "no mail" would hide it forever.
    [Fact]
    public void Status_RefusesUnexpectedArguments_AndSaysNothingOnStdout()
    {
        using var w = new StatusWorld();
        w.Register(Digest("mail-digest-maintainer", "maintainer"));
        w.Send("m-01", "maintainer");

        var (exit, lines, err) = w.RunFull(argv: ["--role", "maintainer"]);
        Assert.Equal(1, exit);
        Assert.Empty(lines);
        Assert.Contains("unexpected argument '--role'", err);
        Assert.Contains("usage:", err);
    }

    /// It reads, and only reads. Driven rather than asserted: a status call
    /// against a bus that does not exist yet creates no directory, and one
    /// against a real bus leaves the store's bytes and the cursor set exactly
    /// as they were — the property that lets a status bar call this every render.
    [Fact]
    public void Status_CreatesNothing_AndChangesNothing()
    {
        using var w = new StatusWorld();
        w.Register(Digest("mail-digest-maintainer", "maintainer"));

        var absent = Path.Combine(w.Home, "no-bus-here");
        Assert.Empty(w.Run(session: "s-1", mailDir: absent));
        Assert.False(Directory.Exists(absent), "a status call created the mail directory");

        w.Send("m-01", "maintainer");
        w.Digest("maintainer", "s-1");                       // a cursor file now exists
        var bytes = File.ReadAllBytes(w.StorePath);
        var files = Directory.GetFileSystemEntries(w.MailDir).OrderBy(x => x).ToArray();

        for (var i = 0; i < 3; i++) w.Run(session: "s-2");   // a second window, repeatedly

        Assert.Equal(bytes, File.ReadAllBytes(w.StorePath));
        Assert.Equal(files, Directory.GetFileSystemEntries(w.MailDir).OrderBy(x => x).ToArray());
    }

    // ---- fixtures ----------------------------------------------------------

    private static MailPendingView View(string role, string? session, int pending, int urgent, int expired)
    {
        var items = new List<PendingMail>();
        var offset = 0L;
        for (var i = 0; i < pending; i++, offset += 100)
            items.Add(new PendingMail(offset,
                DigestFixtures.Env($"p-{i}", i < urgent ? MailPriority.Urgent : MailPriority.Ambient), null));
        var spent = new List<PendingMail>();
        for (var i = 0; i < expired; i++, offset += 100)
            spent.Add(new PendingMail(offset, DigestFixtures.Env($"e-{i}"), 1));
        return new MailPendingView(role, session, 1, "h0", 0, offset, 1, null, false, null, items, spent, 0);
    }

    private static object Digest(string name, string role, params string[] events) => new
    {
        name,
        command = "/usr/bin/captainHook",
        args = new[] { "mail", "digest", "--role", role, "--seam", "ambient" },
        events = events.Length > 0 ? events : ["user-prompt-submit"],
        mode = "oneshot",
        failMode = "open",
    };

    /// A world with its own HOME-shaped tree: a mail store, a handlers.json and
    /// a dispatch.json, all passed explicitly. Nothing here can reach the
    /// operator's `~/.captainHook` (CLAUDE.md's pollution rule).
    private sealed class StatusWorld : IDisposable
    {
        public string Home { get; } = Path.Combine(Path.GetTempPath(), "chk-status-" + Guid.NewGuid().ToString("N")[..8]);
        public string MailDir => Path.Combine(Home, "mail");
        public string StorePath => Path.Combine(MailDir, "mail.jsonl");
        private string HandlersPath => Path.Combine(Home, "handlers.json");
        private string PolicyPath => Path.Combine(Home, "dispatch.json");

        public StatusWorld() => Directory.CreateDirectory(Home);

        public void Register(params object[] handlers) =>
            File.WriteAllText(HandlersPath, JsonSerializer.Serialize(new { version = 1, handlers }));

        public void WriteHandlersRaw(string text) => File.WriteAllText(HandlersPath, text);

        public void Policy(params object[] rules) =>
            File.WriteAllText(PolicyPath, JsonSerializer.Serialize(new { version = 1, @default = "allow", rules }));

        public void Send(string id, string to, MailPriority priority = MailPriority.Ambient) =>
            MailFixtures.AppendOk(new MailStore(MailDir),
                MailFixtures.Envelope(id: id, to: to, priority: priority));

        /// The real digest verb, so a cursor moves exactly as it does in life.
        public void Digest(string role, string? session)
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exit = MailDigest.Run(["--role", role, "--seam", "ambient"],
                new StringReader(DigestFixtures.Request("d-1", "UserPromptSubmit", session)),
                stdout, stderr, mailDir: MailDir, harnessDir: TestUtil.NoHarnessDir());
            Assert.True(exit == 0, $"digest exited {exit}: {stderr}");
        }

        public string[] Run(string? session = null, string? cwd = null, string? stdin = null, string? mailDir = null)
            => RunFull(session, cwd, stdin, mailDir).Lines;

        public (int Exit, string[] Lines, string Err) RunFull(
            string? session = null, string? cwd = null, string? stdin = null, string? mailDir = null,
            string[]? argv = null)
        {
            var payload = stdin ?? JsonSerializer.Serialize(new { session_id = session, cwd });
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exit = MailStatus.Run(argv ?? [], new StringReader(payload), stdout, stderr,
                mailDir: mailDir ?? MailDir, handlersPath: HandlersPath, policyPath: PolicyPath);
            return (exit, stdout.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries), stderr.ToString());
        }

        public void Dispose()
        {
            try { Directory.Delete(Home, recursive: true); } catch { /* best-effort */ }
        }
    }
}
