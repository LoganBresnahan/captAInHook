using System.Text.Json;
using System.Text.Json.Nodes;
using CaptainHook.Actors;
using CaptainHook.Api;
using CaptainHook.Core;
using CaptainHook.Mail;
using static CaptainHook.Tests.TestUtil;

namespace CaptainHook.Tests;

// ADR-0016 decision 14 / N8, slice `mail-reducer` (roadmap item 21, phase 2)
// — the GOLDEN SEQUENCES the TypeScript reducer must reproduce.
//
// N8 names the hazard: the canvas's reducer is a SECOND implementation of
// "pending" (frontier / held / TTL-in-deliveries / expired), and a divergence
// draws a mailbox state that does not exist. The mitigation the ADR promises
// is that the reducer's golden sequences DERIVE from the C# side rather than
// being written by hand against a reading of it. This file is that
// derivation, made mechanical: every scenario below runs through the REAL
// store, the REAL cursors, and the REAL digest verb, with the trail captured
// between two REAL `GET /api/v1/mail` snapshots. What lands in
// `web/src/mail.golden.json` is (before-snapshot, trail lines, after-snapshot)
// per scenario, and `web/src/mail.test.ts` asserts that seeding the reducer
// from `before` and folding `trail` yields `after` — same inputs, same
// pending set as C#, per cursor, per offset.
//
// The checked-in file is the drift detector, on ApiSchemaTests' precedent: a
// change to any of the engine pieces that alters the trail or the snapshot
// shape fails HERE unless the fixture is regenerated in the same commit —
//   CAPTAINHOOK_SCHEMA_UPDATE=1 dotnet test dotnet/captainHookTests/captainHookTests.csproj --filter MailReducerGoldenTests
//   (cd web && npm test)   # the reducer must still reproduce every scenario
// which is the point: the reducer's truth is re-derived from the engine's
// every time the engine moves, and a reducer that stops agreeing fails the
// web suite rather than the operator's eyes.
//
// Determinism, so the file is stable across regenerations: envelope `ts` is
// the fixtures' fixed stamp; the trail's `ts` is normalized to one instant;
// the temp directory is spelled `<mail>` wherever it appears (the DTO's `dir`,
// the re-anchor family's `path`); nothing else in these paths reads a clock.
public class MailReducerGoldenTests
{
    private static string GoldenPath => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "web", "src", "mail.golden.json"));

    private static readonly DateTime Ts = new(2026, 8, 15, 12, 0, 0, 0, DateTimeKind.Utc);
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // ---- the harness ---------------------------------------------------------

    /// One golden scenario: `Setup` builds the pre-snapshot world (its trail is
    /// discarded), `Act` is what the reducer must interpolate across.
    /// `Exact` says the reducer is expected to reproduce `after` with no
    /// uncertainty and no re-snapshot request; `false` names a scenario where
    /// the honest answer IS a flag, and the TS test asserts the flag instead.
    private sealed record Scenario(
        string Name, string Doc, Action<World> Setup, Action<World> Act,
        bool Exact = true, string[]? ExpectUncertain = null);

    /// The world one scenario runs in: a temp mail dir, the writable cursors
    /// (this side MAY write — it is the engine, not the API), and the read
    /// model the API would serve from.
    private sealed class World : IDisposable
    {
        public MailStoreTempDir Tmp { get; } = new();
        public MailCursors Cursors { get; }
        public ApiReadModel Model { get; }

        /// The `?since=` both snapshots are taken from — 0 unless a scenario's
        /// Setup chooses a line boundary (a partial ledger is a scenario in its
        /// own right).
        public long Since { get; set; }

        public World()
        {
            Cursors = Tmp.Cursors();
            Model = new ApiReadModel("golden", new ServeStats(),
                new Dispatcher(new Registry().On("UserPromptSubmit",
                    TestHandler.Returning("greeter", new Effect.Noop())), TimeSpan.FromSeconds(2)),
                new ReloadingHarnessRegistry(NoHarnessDir()), new ReloadingPolicy(null), null,
                clock: () => 6000, startTick: 1000, handlersPath: null,
                mail: MailReadPort.Over(Tmp.Dir), presence: null);
        }

        public long Append(string id, string to = "main",
            MailPriority priority = MailPriority.Ambient, int ttl = 3, string? session = "s-77") =>
            MailFixtures.AppendOk(Tmp.Store(),
                MailFixtures.Envelope(id: id, to: to, priority: priority, ttl: ttl, session: session)).Offset;

        /// The real verb, exactly as a registered digest member runs it: one
        /// exec-wire request in, one answer out. Asserts the run did not fail
        /// loudly (a bad registration would make the golden a lie).
        public string Digest(string role, string? session, string seam = "ambient",
            string eventType = "UserPromptSubmit", string dispatchId = "d-1")
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exit = MailDigest.Run(["--role", role, "--seam", seam],
                new StringReader(DigestFixtures.Request(dispatchId, eventType, session)),
                stdout, stderr, mailDir: Tmp.Dir, harnessDir: NoHarnessDir());
            Assert.True(exit == 0, $"digest exited {exit}: {stderr}");
            return stdout.ToString();
        }

        public string CursorPath(string role, string? session) => Cursors.CursorPath(role, session);

        public JsonNode Snapshot(long since)
        {
            var dto = Model.Mail(since);
            Assert.NotNull(dto);
            return JsonNode.Parse(Normalize(JsonSerializer.Serialize(dto, Web)))!;
        }

        public string Normalize(string s) => s
            .Replace(JsonEncodedText.Encode(Tmp.Dir).ToString(), "<mail>")
            .Replace(Tmp.Dir, "<mail>");

        public void Dispose() => Tmp.Dispose();
    }

    private static JsonObject Run(Scenario sc)
    {
        using var world = new World();
        using var log = new CapturedLog();

        sc.Setup(world);
        var before = world.Snapshot(world.Since);   // may itself warn (re-anchor at read) — discarded below
        log.Events.Clear();

        sc.Act(world);
        var trail = new JsonArray();
        foreach (var e in log.Events)
        {
            // Every event the act emitted, `ts` pinned to one instant so the
            // file is byte-stable; a reducer never reads `ts` for anything but
            // display, which is also why pinning it costs nothing.
            var line = new LogEvent(Ts, e.Lvl, e.Src, e.Evt, e.Fields).ToJson();
            trail.Add(world.Normalize(line));
        }
        var after = world.Snapshot(world.Since);

        foreach (var s in new[] { before.ToJsonString(), after.ToJsonString() }.Concat(trail.Select(t => t!.ToString())))
            Assert.DoesNotContain("captainhook-mail-", s);   // the temp dir never leaks into the golden

        return new JsonObject
        {
            ["name"] = sc.Name,
            ["doc"] = sc.Doc,
            ["since"] = world.Since,
            ["exact"] = sc.Exact,
            ["expectUncertain"] = new JsonArray((sc.ExpectUncertain ?? []).Select(x => (JsonNode)x!).ToArray()),
            ["before"] = before,
            ["trail"] = trail,
            ["after"] = after,
        };
    }

    // ---- the scenarios -------------------------------------------------------

    private static readonly Scenario[] Scenarios =
    [
        new("appends-only",
            "Three appends across two roles, no cursor anywhere. The reducer's ledger must track the store's frontier from `offset` + `bytes` alone and grow no cursor.",
            Setup: _ => { },
            Act: w =>
            {
                w.Append("m-01");
                w.Append("m-02", to: "reviewer", priority: MailPriority.Urgent);
                w.Append("m-03");
            }),

        new("first-delivery",
            "Two lines for main and one for reviewer, no cursor; then main/s-1's first digest at an ambient seam delivers both of main's. A cursor the reducer never saw appears with deliveries 1 — first contact anchors at 0, so its pending set is reconstructible from the ledger and the held set must come out empty.",
            Setup: w =>
            {
                w.Append("m-01");
                w.Append("m-02", to: "reviewer");
                w.Append("m-03");
            },
            Act: w => w.Digest("main", "s-1")),

        new("hold-then-expire",
            "An ambient envelope with ttl 2 is held across two urgent-seam deliveries (seenAt 1, then opportunities 2 ≥ ttl) and dropped by the third: `mail.expire` names it by offset, then the advance drops it. The before-snapshot already carries the held entry, so seeding held (with its ttl) is exercised, and the expiry arithmetic runs in the reducer against the C#'s.",
            Setup: w =>
            {
                w.Append("amb-1", ttl: 2);
                w.Append("urg-1", priority: MailPriority.Urgent);
                w.Digest("main", "s-1", seam: "urgent", eventType: "PreToolUse", dispatchId: "d-1");   // delivers urg-1, holds amb-1 @1
            },
            Act: w =>
            {
                w.Append("urg-2", priority: MailPriority.Urgent);
                w.Digest("main", "s-1", seam: "urgent", eventType: "PreToolUse", dispatchId: "d-2");   // delivers urg-2, amb-1 still held (opp 1 < 2)
                w.Append("urg-3", priority: MailPriority.Urgent);
                w.Digest("main", "s-1", seam: "urgent", eventType: "PreToolUse", dispatchId: "d-3");   // amb-1 expired (opp 2 ≥ 2): expire + drop
            }),

        new("two-sessions-one-role",
            "Two sessions hold the same role and have INDEPENDENT cursors (d6). s-1 consumed everything before the snapshot; then a new line arrives, s-2 makes first contact and gets all three, and s-1 gets only the new one. The reducer must key cursors by (role, session) — an advance without a session would be unattributable here.",
            Setup: w =>
            {
                w.Append("m-01");
                w.Append("m-02");
                w.Digest("main", "s-1", dispatchId: "d-1");
            },
            Act: w =>
            {
                w.Append("m-03");
                w.Digest("main", "s-2", dispatchId: "d-2");
                w.Digest("main", "s-1", dispatchId: "d-3");
            }),

        new("reanchor-preserves-deliveries",
            "The cursor file's gen is edited to a generation the store does not report (no trail event for the edit — it is a foreign write). The next digest re-anchors LOUDLY at 0 preserving `deliveries`, then advances over everything retained (the stated redelivery cost). The reducer must apply the re-anchor as offset 0 / held empty / the carried counter, rebuild the fresh set from its ledger, and then take the advance as the next in sequence.",
            Setup: w =>
            {
                w.Append("m-01");
                w.Append("m-02");
                w.Digest("main", "s-1", dispatchId: "d-1");
            },
            Act: w =>
            {
                var path = w.CursorPath("main", "s-1");
                var cursor = MailCursor.TryParse(File.ReadAllText(path), out _)!;
                File.WriteAllText(path, (cursor with { Gen = 4 }).Render());
                w.Append("m-03");
                w.Digest("main", "s-1", dispatchId: "d-2");
            }),

        new("torn-tail-terminated",
            "The store ends in an unterminated, unparseable tail (an interrupted write); the cursor sits at the torn line's offset. The next append terminates it — it becomes an ordinary malformed line, stepped over and COUNTED (skippedMalformed) — and lands after it. No digest runs, so `after` shows the fresh envelope pending and the count.",
            Setup: w =>
            {
                w.Append("m-01");
                w.Digest("main", "s-1", dispatchId: "d-1");
                File.AppendAllText(w.Tmp.FilePath, "{\"v\":1,\"id\":\"torn");   // no terminator, no close
            },
            Act: w => w.Append("m-02")),

        new("partial-since",
            "The snapshot is taken from `?since=` the third line, so the reducer's ledger lacks the first two — yet the cursor's held entry (below since) and its fresh item are BOTH in the DTO, with their ttl. An append and a delivery follow. The reducer must not need the missing lines to reproduce the pending set: cursor state is materialized from the DTO, not re-derived from a ledger it may not have.",
            Setup: w =>
            {
                w.Append("amb-1", ttl: 5);
                w.Append("urg-1", priority: MailPriority.Urgent);
                w.Since = w.Append("amb-2", ttl: 5);
                w.Digest("main", "s-1", seam: "urgent", eventType: "PreToolUse", dispatchId: "d-1");   // holds amb-1, amb-2; delivers urg-1
            },
            Act: w =>
            {
                w.Append("m-04");
                w.Digest("main", "s-1", dispatchId: "d-2");   // ambient seam: everything held + fresh delivers
            }),

        new("partial-since-reanchor",
            "Same partial snapshot, but the cursor is then re-anchored (gen edited). Rebuilding the fresh set needs lines from offset 0 that this reducer never had — so the honest answer is a flag: the cursor is marked uncertain and a re-snapshot is requested. Positions (offset, deliveries) still track.",
            Setup: w =>
            {
                w.Append("amb-1", ttl: 5);
                w.Append("urg-1", priority: MailPriority.Urgent);
                w.Since = w.Append("amb-2", ttl: 5);
                w.Digest("main", "s-1", seam: "urgent", eventType: "PreToolUse", dispatchId: "d-1");
            },
            Act: w =>
            {
                var path = w.CursorPath("main", "s-1");
                var cursor = MailCursor.TryParse(File.ReadAllText(path), out _)!;
                File.WriteAllText(path, (cursor with { Gen = 4 }).Render());
                w.Digest("main", "s-1", dispatchId: "d-2");
            },
            Exact: false, ExpectUncertain: ["main/s-1"]),

        new("deleted-cursor-restarts-lineage",
            "A cursor with deliveries 2 is DELETED (d13: deletable anytime, no trail event) and the next digest makes silent first contact: deliveries restart at 1 and everything retained redelivers. The counter going BACKWARDS is the tell; because a first advance always comes from an anchor at 0, the reducer can still reconstruct exactly — it must not mistake this for a stale replay of an old event.",
            Setup: w =>
            {
                w.Append("m-01");
                w.Digest("main", "s-1", dispatchId: "d-1");
                w.Append("m-02");
                w.Digest("main", "s-1", dispatchId: "d-2");
            },
            Act: w =>
            {
                File.Delete(w.CursorPath("main", "s-1"));
                w.Append("m-03");
                w.Digest("main", "s-1", dispatchId: "d-3");
            }),

        new("sessionless-reader",
            "A reader with no session (the sessionless cursor, `cursor.main..json`): its events carry NO sessionId column, and the reducer must map that absence to session null — the same cursor the DTO reports with `session: null` — never to a cursor named \"\" or \"undefined\".",
            Setup: w =>
            {
                w.Append("m-01");
                w.Digest("main", null, dispatchId: "d-1");
                w.Append("m-02");
            },
            Act: w => w.Digest("main", null, dispatchId: "d-2")),

        new("hold-only-advance",
            "An advance that delivers NOTHING (the engine's Advance called directly with no offsets — no `mail.deliver` follows). Every pending item becomes held, stamped with the new deliveries count; the reducer must not wait for a deliver record to move the cursor.",
            Setup: w =>
            {
                w.Append("m-01");
                w.Append("m-02");
            },
            Act: w => CursorFixtures.AdvanceOk(w.Cursors, w.Cursors.Pending("main", "s-1"))),

        new("stale-view-refused",
            "Two advances from ONE view: the second is refused (the staleness guard) with `mail.cursorRefuse`. The reducer must record the refusal and change nothing — the cursor moved exactly once.",
            Setup: w =>
            {
                w.Append("m-01");
                w.Append("m-02");
            },
            Act: w =>
            {
                var view = w.Cursors.Pending("main", "s-1");
                var first = view.Pending[0].Offset;
                CursorFixtures.AdvanceOk(w.Cursors, view, first);
                Assert.IsType<MailCursorWrite.Failed>(w.Cursors.Advance(view, [view.Pending[1].Offset]));
            }),

        new("vanished-lineage-advances-loudly",
            "A view read at deliveries > 0 whose cursor file is deleted before the advance: the advance proceeds with `mail.cursorVanished` and the sequence continues (+1). The reducer keeps the sequence, records the notice, and the deliveries count matches the file the engine wrote.",
            Setup: w =>
            {
                w.Append("m-01");
                w.Digest("main", "s-1", dispatchId: "d-1");
                w.Append("m-02");
            },
            Act: w =>
            {
                var view = w.Cursors.Pending("main", "s-1");
                File.Delete(w.CursorPath("main", "s-1"));
                CursorFixtures.AdvanceOk(w.Cursors, view, view.Pending[0].Offset);
            }),

        new("store-truncated-reanchor",
            "The STORE is truncated under a cursor that sat at its end (an incident, not a cursor edit): the next digest re-anchors with cause `store` — the bytes the cursor described are gone — and delivers what the shortened store retains. The reducer's ledger picture predates the truncation and cannot be trusted; it must NOT rebuild a pending set from lines that no longer exist (the skeptic pass's top find). Honest answer: uncertain + re-snapshot; positions still track.",
            Setup: w =>
            {
                w.Append("m-01");
                w.Append("m-02");
                w.Append("m-03");
                w.Digest("main", "s-1", dispatchId: "d-1");
            },
            Act: w =>
            {
                var lines = File.ReadAllText(w.Tmp.FilePath).Split('\n', StringSplitOptions.RemoveEmptyEntries);
                File.WriteAllText(w.Tmp.FilePath, lines[0] + "\n");   // the store shrinks to its first line
                w.Digest("main", "s-1", dispatchId: "d-2");
            },
            Exact: false, ExpectUncertain: ["main/s-1"]),

        new("reconcile-seam-decides",
            "Reconcile-priority mail at a Stop-class seam rides the `decide` vehicle (d5, the harness's own top-level decision). The reducer reads seam and vehicle off `mail.deliver` for the cursor's tag; nothing about the pending arithmetic changes.",
            Setup: w =>
            {
                w.Append("rec-1", priority: MailPriority.Reconcile);
                w.Append("amb-1");
            },
            Act: w => w.Digest("main", "s-1", seam: "reconcile", eventType: "Stop", dispatchId: "d-1")),
    ];

    // ---- the pin -----------------------------------------------------------------

    /// Generate every scenario and compare against the checked-in file (or
    /// rewrite it under CAPTAINHOOK_SCHEMA_UPDATE=1). Determinism is part of
    /// the assertion: the same scenarios generated twice in one process must
    /// be byte-identical, or the file could never be stable.
    [Fact]
    public void CheckedInGolden_MatchesTheEngine()
    {
        var generated = Generate();
        Assert.Equal(generated, Generate());   // deterministic, or the pin is noise

        if (Environment.GetEnvironmentVariable("CAPTAINHOOK_SCHEMA_UPDATE") == "1")
            File.WriteAllText(GoldenPath, generated);
        Assert.True(File.Exists(GoldenPath), $"missing {GoldenPath} — run with CAPTAINHOOK_SCHEMA_UPDATE=1");
        Assert.Equal(File.ReadAllText(GoldenPath), generated);
    }

    /// The scenarios must actually exercise what they claim: every event kind
    /// the reducer folds appears somewhere in the golden, so a reducer that
    /// handles a kind wrongly cannot pass by never meeting it.
    [Fact]
    public void Golden_CoversEveryMailEventKind()
    {
        var text = Generate();
        foreach (var evt in new[]
        {
            "mail.append", "mail.cursorAdvance", "mail.deliver", "mail.expire",
            "mail.cursorReanchor", "mail.cursorRefuse", "mail.cursorVanished",
        })
            Assert.Contains($"\\\"evt\\\":\\\"{evt}\\\"", text);
    }

    private static string Generate()
    {
        var doc = new JsonObject
        {
            ["$comment"] = "GENERATED by MailReducerGoldenTests (dotnet/captainHookTests) — do not edit. "
                + "Each scenario is (before snapshot, trail lines, after snapshot) produced by the real "
                + "store/cursors/digest; web/src/mail.test.ts replays them through the reducer. Regenerate: "
                + "CAPTAINHOOK_SCHEMA_UPDATE=1 dotnet test dotnet/captainHookTests/captainHookTests.csproj --filter MailReducerGoldenTests",
            ["scenarios"] = new JsonArray(Scenarios.Select(s => (JsonNode)Run(s)).ToArray()),
        };
        return doc.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            // Readable prose in `doc` (an em dash is an em dash, not \u2014);
            // the trail lines inside are already the emitters' own escaping.
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }) + "\n";
    }
}
