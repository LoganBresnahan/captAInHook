using System.Text.Json;
using System.Text.Json.Nodes;
using CaptainHook.Core;
using CaptainHook.Mail;
using static CaptainHook.Tests.TestUtil;

namespace CaptainHook.Tests;

// ADR-0017 decision 4, slice `watcher-brain` — the brain GOLDEN-TESTED off the
// real engine, on `MailReducerGoldenTests`' precedent: every scenario below runs
// the REAL store, the REAL cursors and the REAL digest verb (so mailboxes are
// exactly what a window's read leaves behind), reads them through the same
// gatherer the CLI and the actor use (`MailWatch.ReadMailboxes`), and hands the
// brain values. What lands in `watcher-brain.golden.json` is the brain's whole
// verdict per scenario — nudges with their reason and digest text, the one
// deadline, the per-role standing, the state to keep — so a change to the
// brain, the digest renderer, the cursor arithmetic or the address predicate
// fails HERE unless the fixture is regenerated in the same commit:
//
//   CAPTAINHOOK_SCHEMA_UPDATE=1 dotnet test dotnet/captainHookTests/captainHookTests.csproj --filter WatcherBrainGoldenTests
//
// Determinism: `NowMs` is a number each scenario chooses; presence is a list
// each scenario chooses; the temp dir never appears in the verdict.
public class WatcherBrainGoldenTests
{
    private static string GoldenPath => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "watcher-brain.golden.json"));

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private const long Min = 60_000;

    private sealed record Scenario(string Name, string Doc, Func<World, WatchInput> Build);

    private sealed class World : IDisposable
    {
        public MailStoreTempDir Tmp { get; } = new();
        public MailCursors Cursors { get; }
        public World() => Cursors = Tmp.Cursors();

        public void Append(string id, string to, MailPriority priority = MailPriority.Ambient) =>
            MailFixtures.AppendOk(Tmp.Store(),
                MailFixtures.Envelope(id: id, to: to, priority: priority, ttl: to.Contains('@') ? null : 3));

        /// The real verb, exactly as a registered digest member runs it, so the
        /// cursor moves as it does in life.
        public void Digest(string role, string? session, string? instance = null, string seam = "ambient")
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            string[] argv = instance is null
                ? ["--role", role, "--seam", seam]
                : ["--role", role, "--as", instance, "--seam", seam];
            var exit = MailDigest.Run(argv,
                new StringReader(DigestFixtures.Request("d-1", "UserPromptSubmit", session)),
                stdout, stderr, mailDir: Tmp.Dir, harnessDir: NoHarnessDir());
            Assert.True(exit == 0, $"digest exited {exit}: {stderr}");
        }

        public IReadOnlyList<WatchedMailbox> Mailboxes(params string[] roles) =>
            MailWatch.ReadMailboxes(Cursors, roles);

        public void Dispose() => Tmp.Dispose();
    }

    private static WatchRule Rule(string role, string? priority = ">=urgent", int quietForMs = (int)(10 * Min),
        bool noLiveSession = true, int perEnvelope = 1, int perRoleHour = 4) =>
        new(role,
            new WatchWhen(priority is null ? null : new WatchPriority(MailPriority.Urgent, priority.StartsWith(">=")),
                quietForMs, noLiveSession),
            new WatchBudget(perEnvelope, perRoleHour));

    private static readonly RoleKinds RobotOnly = new(new HashSet<string>(), true);
    private static readonly RoleKinds MixedReviewer = new(new HashSet<string> { "reviewer" }, true);
    private static readonly RoleKinds HumanReviewer = new(new HashSet<string> { "reviewer" }, false);

    /// Every unread envelope seen at `firstSeen`, so thresholds can be crossed
    /// by choosing `now`.
    private static NudgeState SeenAt(IReadOnlyList<WatchedMailbox> boxes, long firstSeen, int nudged = 0) =>
        new(boxes.SelectMany(b => b.Pending.Select(p => (b.Address.Role, p.Envelope.Id))).Distinct()
                 .Select(x => new WatchedEnvelope(x.Role, x.Id, firstSeen, firstSeen, nudged)).ToList(), []);

    private static readonly Scenario[] Scenarios =
    [
        new("robot-role-never-read",
            "A robot-servable role with no cursor at all: read sessionless, everything ever sent is unread; " +
            "the urgent one is due past 10 minutes, the ambient one is not admitted by the rule.",
            w =>
            {
                w.Append("m-01", "reviewer", MailPriority.Urgent);
                w.Append("m-02", "reviewer", MailPriority.Ambient);
                var boxes = w.Mailboxes("reviewer");
                return new WatchInput(boxes, [], RobotOnly, [Rule("reviewer")], SeenAt(boxes, 0), 12 * Min);
            }),

        new("read-by-one-window-is-heard",
            "Two windows hold the role; one has taken delivery through the real digest. Not unread — nothing armed.",
            w =>
            {
                w.Append("m-00", "reviewer");                       // both windows read this: two cursors exist
                w.Digest("reviewer", "s-1");
                w.Digest("reviewer", "s-2");
                w.Append("m-01", "reviewer", MailPriority.Urgent);
                w.Digest("reviewer", "s-1");                        // s-1 reads m-01; s-2 has not
                w.Append("m-02", "reviewer", MailPriority.Urgent);  // unread in both
                var boxes = w.Mailboxes("reviewer");
                return new WatchInput(boxes, [], MixedReviewer, [Rule("reviewer")], SeenAt(boxes, 0), 12 * Min);
            }),

        new("human-held-never",
            "The maintainer's own role: a digest registration and no turn payload. Unread past every threshold, and never a robot.",
            w =>
            {
                w.Append("m-01", "reviewer", MailPriority.Urgent);
                var boxes = w.Mailboxes("reviewer");
                return new WatchInput(boxes, [], HumanReviewer, [Rule("reviewer")], SeenAt(boxes, 0), 60 * Min);
            }),

        new("live-window-holds-mixed",
            "Mixed role, urgent mail due, but the window that holds the cursor dispatched 2 minutes ago: held for presence, armed for its expiry.",
            w =>
            {
                w.Append("m-00", "reviewer");
                w.Digest("reviewer", "s-1");                        // a cursor for s-1…
                w.Append("m-01", "reviewer", MailPriority.Urgent);  // …which has not seen this
                var boxes = w.Mailboxes("reviewer");
                return new WatchInput(boxes, [("s-1", 2 * Min)], MixedReviewer, [Rule("reviewer")], SeenAt(boxes, 0), 30 * Min);
            }),

        new("named-durable-mailbox-unicast",
            "A `--as robot` mailbox holds a unicast nobody else can accept, plus the role's broadcast; a window read the broadcast. Only the unicast is unread.",
            w =>
            {
                w.Append("m-00", "reviewer");
                w.Digest("reviewer", "s-1");                        // a window's cursor
                w.Digest("reviewer", "s-2", instance: "robot");     // the durable box, served from window s-2
                w.Append("m-01", "reviewer", MailPriority.Urgent);
                w.Append("u-01", "reviewer@robot", MailPriority.Urgent);
                w.Digest("reviewer", "s-1");                        // the window reads the broadcast; the box holds both
                var boxes = w.Mailboxes("reviewer");
                return new WatchInput(boxes, [("s-2", 0)], MixedReviewer, [Rule("reviewer")], SeenAt(boxes, 0), 30 * Min);
            }),

        new("budgets-spent",
            "Two urgent envelopes due; one already nudged its perEnvelope, and the role has 4 nudges this hour: exhausted, armed for the window.",
            w =>
            {
                w.Append("m-01", "reviewer", MailPriority.Urgent);
                w.Append("m-02", "reviewer", MailPriority.Urgent);
                var boxes = w.Mailboxes("reviewer");
                var state = new NudgeState(
                    [new("reviewer", "m-01", 0, 0, 1), new("reviewer", "m-02", 0, 0, 0)],
                    [new("reviewer", 5 * Min), new("reviewer", 15 * Min), new("reviewer", 25 * Min), new("reviewer", 35 * Min)]);
                return new WatchInput(boxes, [], RobotOnly, [Rule("reviewer")], state, 40 * Min);
            }),

        new("two-roles-two-rules",
            "Rules for two roles with different thresholds; the verdict holds one deadline, the minimum, and reports each role.",
            w =>
            {
                w.Append("m-01", "reviewer", MailPriority.Urgent);
                w.Append("m-02", "ops", MailPriority.Urgent);
                w.Append("m-03", "ops", MailPriority.Ambient);
                var boxes = w.Mailboxes("reviewer", "ops");
                return new WatchInput(boxes, [], RobotOnly,
                    [Rule("reviewer", quietForMs: (int)(10 * Min)), Rule("ops", priority: null, quietForMs: (int)(3 * Min), perEnvelope: 2)],
                    SeenAt(boxes, 0), 5 * Min);
            }),
    ];

    private static JsonObject Run(Scenario sc)
    {
        using var w = new World();
        var input = sc.Build(w);
        var v = WatcherBrain.Evaluate(input);
        return new JsonObject
        {
            ["name"] = sc.Name,
            ["doc"] = sc.Doc,
            ["nowMs"] = input.NowMs,
            ["verdict"] = JsonNode.Parse(JsonSerializer.Serialize(new
            {
                nudges = v.Nudges,
                nextCheckMs = v.NextCheckMs,
                roles = v.Roles.Select(r => new
                {
                    r.Role, Kind = r.Kind.ToString(), Standing = r.Standing.ToString(),
                    r.Unread, r.Due, r.FreshestDispatchAgeMs, r.NextCheckMs, r.Detail,
                }),
                state = v.State,
            }, Web)),
        };
    }

    private static string Generate() => JsonSerializer.Serialize(new JsonObject
    {
        ["$comment"] = "GENERATED by WatcherBrainGoldenTests (dotnet/captainHookTests) — do not edit. Each scenario is " +
                       "(real store + real digest-moved cursors + chosen presence/state/now) → the brain's verdict. " +
                       "Regenerate: CAPTAINHOOK_SCHEMA_UPDATE=1 dotnet test dotnet/captainHookTests/captainHookTests.csproj --filter WatcherBrainGoldenTests",
        ["scenarios"] = new JsonArray(Scenarios.Select(s => (JsonNode)Run(s)).ToArray()),
    }, Web) + "\n";

    [Fact]
    public void Golden_MatchesTheCheckedInFixture()
    {
        var generated = Generate();
        if (Environment.GetEnvironmentVariable("CAPTAINHOOK_SCHEMA_UPDATE") == "1")
        {
            File.WriteAllText(GoldenPath, generated);
            return;
        }
        Assert.True(File.Exists(GoldenPath), $"golden missing at {GoldenPath} — run with CAPTAINHOOK_SCHEMA_UPDATE=1");
        var onDisk = File.ReadAllText(GoldenPath);
        Assert.True(onDisk == generated,
            "watcher-brain.golden.json is stale — the brain, the digest renderer, the cursor arithmetic or the address " +
            "predicate changed. Regenerate with CAPTAINHOOK_SCHEMA_UPDATE=1 and review the diff.");
    }

    /// The scenarios' load-bearing claims, asserted directly so a regenerated
    /// golden cannot silently pin a wrong picture.
    [Fact]
    public void Scenarios_SayWhatTheirDocsSay()
    {
        var by = new Dictionary<string, WatchVerdict>();
        foreach (var sc in Scenarios)
        {
            using var w = new World();
            by[sc.Name] = WatcherBrain.Evaluate(sc.Build(w));
        }

        var robot = by["robot-role-never-read"];
        Assert.Equal(["m-01"], Assert.Single(robot.Nudges).EnvelopeIds);
        Assert.Equal(2, robot.Roles.Single().Unread);

        var heard = by["read-by-one-window-is-heard"];
        Assert.Equal(1, heard.Roles.Single().Unread);          // m-02 only; m-01 was read by s-1
        Assert.Equal(["m-02"], Assert.Single(heard.Nudges).EnvelopeIds);

        Assert.Equal(WatchStanding.HumanHeld, by["human-held-never"].Roles.Single().Standing);
        Assert.Empty(by["human-held-never"].Nudges);

        var live = by["live-window-holds-mixed"];
        Assert.Equal(WatchStanding.LiveSession, live.Roles.Single().Standing);
        Assert.Equal(30 * Min + 8 * Min + 1, live.NextCheckMs);

        var named = by["named-durable-mailbox-unicast"];
        Assert.Equal(1, named.Roles.Single().Unread);
        Assert.Equal(["u-01"], Assert.Single(named.Nudges).EnvelopeIds);   // s-2 is a window that dispatched, but its cursor is keyed `robot`

        var spent = by["budgets-spent"];
        Assert.Equal(WatchStanding.Exhausted, spent.Roles.Single().Standing);
        Assert.Equal(5 * Min + WatcherBrain.RoleWindowMs, spent.NextCheckMs);

        var two = by["two-roles-two-rules"];
        Assert.Equal(["ops"], two.Nudges.Select(n => n.Role));
        Assert.Equal(["m-02", "m-03"], two.Nudges[0].EnvelopeIds);
        Assert.Equal(5 * Min + 3 * Min, two.NextCheckMs);   // ops re-arms (perEnvelope 2) — before reviewer's 10m
    }
}
