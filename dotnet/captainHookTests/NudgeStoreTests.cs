using System.Text;
using System.Text.Json;
using CaptainHook.Core;

namespace CaptainHook.Tests;

// ADR-0017 decision 4, slice `nudge-state-and-trail` — the brain's memory on
// disk and the `mail.nudge` row.
//
// Two claims are load-bearing and everything here is aimed at them:
//
//   1. THE STATE CROSSES A PROCESS AS AGES, NEVER AS STAMPS. A monotonic number
//      means nothing in another process, so what is written is durations from
//      the moment of writing and what is read is stamps re-derived from the
//      moment of reading — and the gap between the two is NOT counted. Every
//      test below picks its own `now`, and no test sleeps or reads a clock.
//   2. A STATE THAT CANNOT BE READ COSTS A QUIET PERIOD AND NOTHING ELSE. A
//      torn tail falls back to the last complete line; a file where nothing
//      parses re-anchors to `Empty`. Both directions are FEWER nudges, and both
//      say so on the trail.
public class NudgeStoreTests
{
    private sealed class Tmp : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "chk-nudge-" + Guid.NewGuid().ToString("N")[..8]);
        public NudgeStore Store => new(Dir);
        public string FilePath => Path.Combine(Dir, NudgeStore.FileName);
        public Tmp() => Directory.CreateDirectory(Dir);
        public string[] Lines() => File.Exists(FilePath)
            ? File.ReadAllLines(FilePath).Where(l => l.Length > 0).ToArray()
            : [];
        public void WriteRaw(string text) => File.WriteAllText(FilePath, text);
        public void Dispose() { try { Directory.Delete(Dir, recursive: true); } catch { /* best-effort */ } }
    }

    private static NudgeState State(long now, params (string Subject, string Id, long UnreadFor, long QuietFor, int Nudged)[] entries) =>
        new(entries.Select(e => new WatchedEnvelope(e.Subject, e.Id, now - e.UnreadFor, now - e.QuietFor, e.Nudged)).ToList(), []);

    // ---- first contact --------------------------------------------------------------

    [Fact]
    public void NoFileAtAll_IsFirstContact_EmptyAndSilent()
    {
        using var t = new Tmp();
        using var log = new CapturedLog();
        Assert.Equal(NudgeState.Empty, t.Store.Load(nowMs: 500));
        Assert.DoesNotContain(log.Events, e => e.Evt.StartsWith("watch.state"));
    }

    [Fact]
    public void AnEmptyFile_IsAlsoFirstContact_NotAReanchor()
    {
        using var t = new Tmp();
        using var log = new CapturedLog();
        t.WriteRaw("");
        Assert.Empty(t.Store.Load(nowMs: 500).Envelopes);
        Assert.DoesNotContain(log.Events, e => e.Evt == "watch.stateReanchor");
    }

    // ---- the round trip, and the gap that is not counted -----------------------------

    /// The whole point of `ToAges`/`FromAges`: an envelope that had been quiet
    /// six minutes when the daemon wrote its state is quiet six minutes when it
    /// reads it back — whatever monotonic epoch the reading process happens to
    /// be on, and however long the daemon was down.
    [Fact]
    public void AcrossARestart_ClocksResumeWhereTheyStopped_AndTheGapIsNotCounted()
    {
        using var t = new Tmp();
        const long wrote = 1_000_000;
        var before = State(wrote, ("reviewer", "m-01", UnreadFor: 12 * 60_000, QuietFor: 6 * 60_000, Nudged: 2));
        Assert.True(t.Store.Save(before, wrote));

        // A different process, a different epoch, and a long absence.
        const long read = 77;
        var after = t.Store.Load(read);
        var e = Assert.Single(after.Envelopes);
        Assert.Equal("reviewer", e.Subject);
        Assert.Equal("m-01", e.Id);
        Assert.Equal(12 * 60_000, read - e.FirstSeenMs);
        Assert.Equal(6 * 60_000, read - e.QuietSinceMs);   // six, not six + the gap
        Assert.Equal(2, e.Nudged);
    }

    [Fact]
    public void TheSlidingWindow_CrossesTheSameWay()
    {
        using var t = new Tmp();
        var before = new NudgeState([], [new RoleNudge("reaper", 900_000 - 20 * 60_000)]);
        t.Store.Save(before, nowMs: 900_000);

        var after = t.Store.Load(nowMs: 4);
        var n = Assert.Single(after.Nudges);
        Assert.Equal("reaper", n.Role);
        Assert.Equal(20 * 60_000, 4 - n.AtMs);
    }

    /// A state written "in the future" (a clock the writer read after the
    /// numbers it stored, or a hand-edited file) cannot make an envelope look
    /// unread for a negative time: `ToAges`/`FromAges` clamp at zero, which is
    /// "first seen now" — the conservative reading.
    [Fact]
    public void NegativeAges_ClampToNow_NeverToTheFuture()
    {
        using var t = new Tmp();
        t.WriteRaw("""{"v":1,"envelopes":[{"subject":"r","id":"m-1","unreadForMs":-5000,"quietForMs":-5000,"nudged":-3}],"nudges":[{"role":"r","agoMs":-1}]}""" + "\n");
        var s = t.Store.Load(nowMs: 1_000);
        var e = Assert.Single(s.Envelopes);
        Assert.Equal(1_000, e.FirstSeenMs);
        Assert.Equal(1_000, e.QuietSinceMs);
        Assert.Equal(0, e.Nudged);
        Assert.Equal(1_000, Assert.Single(s.Nudges).AtMs);
    }

    // ---- the file --------------------------------------------------------------------

    [Fact]
    public void EachSave_AppendsExactlyOneLine()
    {
        using var t = new Tmp();
        var store = t.Store;
        store.Save(State(10, ("r", "m-1", 0, 0, 0)), 10);
        store.Save(State(20, ("r", "m-1", 0, 0, 1)), 20);
        store.Save(State(30, ("r", "m-1", 0, 0, 2)), 30);
        Assert.Equal(3, t.Lines().Length);
        // …and the LAST one is what a reader sees.
        Assert.Equal(2, Assert.Single(t.Store.Load(30).Envelopes).Nudged);
    }

    [Fact]
    public void TheFileIsPrivate_LikeEverythingInThisTree()
    {
        using var t = new Tmp();
        t.Store.Save(NudgeState.Empty, 1);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(t.FilePath));
    }

    /// The file cannot grow forever: past the bound a save rewrites it to the
    /// single current line, through a rename, so what a reader sees never
    /// changes even if the compaction dies.
    [Fact]
    public void PastTheBound_ASaveCompactsToOneLine()
    {
        using var t = new Tmp();
        var store = t.Store;
        // One line big enough that two of them pass the bound.
        var many = new NudgeState(
            Enumerable.Range(0, 3000)
                .Select(i => new WatchedEnvelope("reviewer", $"m-{i:D5}", 0, 0, 1)).ToList(),
            []);
        store.Save(many, 0);
        Assert.True(new FileInfo(t.FilePath).Length < NudgeStore.CompactAtBytes, "one line should not pass the bound");
        Assert.Single(t.Lines());

        store.Save(many, 0);
        Assert.Single(t.Lines());   // appended, then compacted back to one
        Assert.Equal(3000, t.Store.Load(0).Envelopes.Count);
    }

    [Fact]
    public void AnUnwritableTree_WarnsAndReturnsFalse_ItDoesNotThrow()
    {
        using var t = new Tmp();
        using var log = new CapturedLog();
        var blocked = Path.Combine(t.Dir, "blocked");
        File.WriteAllText(blocked, "not a directory");   // so CreateDirectory cannot win
        Assert.False(new NudgeStore(blocked).Save(NudgeState.Empty, 1));
        Assert.Contains(log.Events, e => e.Evt == "watch.stateUnwritable");
    }

    // ---- what an unreadable state costs ----------------------------------------------

    [Fact]
    public void ATornTail_FallsBackToTheLastCompleteLine_AndSaysSo()
    {
        using var t = new Tmp();
        using var log = new CapturedLog();
        t.Store.Save(State(100, ("reviewer", "m-01", 60_000, 60_000, 1)), 100);
        // A crash mid-append, or two writers interleaving one.
        File.AppendAllText(t.FilePath, """{"v":1,"envelopes":[{"subject":"revi""");

        var s = t.Store.Load(nowMs: 100);
        Assert.Equal("m-01", Assert.Single(s.Envelopes).Id);
        var warn = Assert.Single(log.Events, e => e.Evt == "watch.stateTorn");
        Assert.Equal(1, warn.Fields.Data["skipped"]);
        Assert.DoesNotContain(log.Events, e => e.Evt == "watch.stateReanchor");
    }

    [Fact]
    public void WhenNothingParses_ItReanchors_AndSaysWhy()
    {
        using var t = new Tmp();
        using var log = new CapturedLog();
        t.WriteRaw("not json at all\n{\"v\":1,\n");

        Assert.Equal(NudgeState.Empty, t.Store.Load(nowMs: 5));
        var warn = Assert.Single(log.Events, e => e.Evt == "watch.stateReanchor");
        Assert.Equal("malformed", warn.Fields.Data["cause"]);
    }

    [Theory]
    // A version this build does not know: skipped, not guessed at.
    [InlineData("""{"v":2,"envelopes":[],"nudges":[]}""")]
    // A member this build does not know — the same answer, for the same reason.
    [InlineData("""{"v":1,"envelopes":[],"nudges":[],"armed":123}""")]
    [InlineData("""{"v":1,"envelopes":[{"subject":"r","id":"m","unreadForMs":0,"quietForMs":0,"nudged":0,"why":"x"}],"nudges":[]}""")]
    // Halves that must both be present: a line missing one is not a state.
    [InlineData("""{"v":1,"envelopes":[]}""")]
    [InlineData("""{"envelopes":[],"nudges":[]}""")]
    // Wrong types, each rejecting the LINE and nothing else.
    [InlineData("""{"v":1,"envelopes":{},"nudges":[]}""")]
    [InlineData("""{"v":1,"envelopes":[{"subject":"r","id":"m","unreadForMs":"600000","quietForMs":0,"nudged":0}],"nudges":[]}""")]
    [InlineData("""{"v":1,"envelopes":[],"nudges":[{"role":"r"}]}""")]
    [InlineData("[]")]
    // The deferred-unescape trap: this parses fine and throws at GetString.
    [InlineData("""{"v":1,"envelopes":[{"subject":"\udead","id":"m","unreadForMs":0,"quietForMs":0,"nudged":0}],"nudges":[]}""")]
    public void ALineThisBuildCannotRead_IsSkipped_NeverGuessedAt(string line)
    {
        Assert.False(NudgeStore.TryParseLine(line, out _));

        using var t = new Tmp();
        t.WriteRaw(line + "\n");
        Assert.Equal(NudgeState.Empty, t.Store.Load(nowMs: 5));   // and it never throws
    }

    [Fact]
    public void EveryLineThisBuildWrites_ReadsBackAsItself()
    {
        var ages = new NudgeStateAges(
            [new WatchedEnvelopeAges("maintainer@laptop-a", "m-01", 600_000, 60_000, 3)],
            [new RoleNudgeAges("reaper", 900_000)]);
        var line = NudgeStore.Render(ages);
        Assert.EndsWith("\n", line);
        Assert.True(NudgeStore.TryParseLine(line, out var back));
        // Element-wise: a record's list members compare by reference.
        Assert.Equal(ages.Envelopes, back.Envelopes);
        Assert.Equal(ages.Nudges, back.Nudges);
    }

    /// A `role@instance` subject is the reason the field is `subject` and not
    /// `role` (the 2026-08-18 rename): the dead-mailbox rule tracks under an
    /// ADDRESS, and this file format is where that would have been frozen.
    [Fact]
    public void TheSubjectColumn_HoldsAnAddress_NotJustARole()
    {
        using var t = new Tmp();
        t.Store.Save(State(0, ("maintainer@abc123", "m-09", 0, 0, 1)), 0);
        Assert.Contains("\"subject\":\"maintainer@abc123\"", File.ReadAllText(t.FilePath));
        Assert.DoesNotContain("\"role\":\"maintainer@abc123\"", File.ReadAllText(t.FilePath));
    }

    // ---- Record: the row and the charge, together ------------------------------------

    private static MailNudge Nudge(string role = "reviewer", string? address = null, params string[] ids) =>
        new(role, ids.Length == 0 ? ["m-01"] : ids, "1 unread past quiet (12m+) · budget envelope 1/1 · role 1/4 this hour",
            "[captAInHook mail] 1 message(s)", WatcherBrain.ReplyHow, Workspace: null, Address: address,
            Budget: new MailNudgeBudget(1, 1, 1, 4));

    [Fact]
    public void ANudgeThatRan_ChargesTheBudgets_AndLeavesOneMailNudgeRow()
    {
        using var log = new CapturedLog();
        var state = State(0, ("reviewer", "m-01", 0, 12 * 60_000, 0));
        var after = NudgeStore.Record(state, Nudge(), new MailNudgeOutcome(true, "abc12345", "noop", null), nowMs: 1_000);

        var e = Assert.Single(after.Envelopes);
        Assert.Equal(1, e.Nudged);
        Assert.Equal(1_000, e.QuietSinceMs);                     // quiet restarts
        Assert.Equal(("reviewer", 1_000L), (Assert.Single(after.Nudges).Role, Assert.Single(after.Nudges).AtMs));

        var row = Assert.Single(log.Events, x => x.Evt == "mail.nudge");
        Assert.Equal("mail", row.Src);
        Assert.Equal("abc12345", row.Fields.DispatchId);
        Assert.Equal("reviewer", row.Fields.Data["role"]);
    }

    /// A denial wakes nobody, so it puts no poke on the picture — the record of
    /// it is `nudge.denied`, which `MailNudgeEvent` already writes. The state
    /// still takes it, uncharged, so the refusal recurs once per quiet period.
    [Fact]
    public void ANudgePolicyDenied_WritesNoRow_AndSpendsNoBudget()
    {
        using var log = new CapturedLog();
        var state = State(0, ("reviewer", "m-01", 0, 12 * 60_000, 0));
        var after = NudgeStore.Record(state, Nudge(), new MailNudgeOutcome(false, "abc12345", "noop", "denied"), nowMs: 1_000);

        var e = Assert.Single(after.Envelopes);
        Assert.Equal(0, e.Nudged);            // perEnvelope untouched
        Assert.Empty(after.Nudges);           // …and nothing in the role's hour
        Assert.Equal(1_000, e.QuietSinceMs);  // but the quiet clock did restart
        Assert.DoesNotContain(log.Events, x => x.Evt == "mail.nudge");
    }

    /// d10's columns, as built: the budget is NUMBERS (never a sentence a
    /// reader has to parse), there is no `channel` (the human channel emits
    /// nothing, so the column would have one value) and no `sessionId` (a nudge
    /// belongs to a role and carries no window).
    [Fact]
    public void TheRow_CarriesTheBudgetAsNumbers_AndNeitherAChannelNorASession()
    {
        using var log = new CapturedLog();
        NudgeStore.Record(NudgeState.Empty, Nudge(), new MailNudgeOutcome(true, "d-1", "noop", null), 1);

        var row = Assert.Single(log.Events, x => x.Evt == "mail.nudge");
        var budget = Assert.IsType<Dictionary<string, object>>(row.Fields.Data["budget"]);
        Assert.Equal(1, budget["envelope"]);
        Assert.Equal(1, budget["perEnvelope"]);
        Assert.Equal(1, budget["roleHour"]);
        Assert.Equal(4, budget["perRoleHour"]);
        Assert.Null(row.Fields.SessionId);
        Assert.False(row.Fields.Data.ContainsKey("channel"));
        Assert.False(row.Fields.Data.ContainsKey("address"));   // a role nudge is about no mailbox
    }

    [Fact]
    public void ADeadMailboxNudge_NamesTheBoxOnTheRow()
    {
        using var log = new CapturedLog();
        NudgeStore.Record(NudgeState.Empty,
            Nudge(role: WatcherBrain.ReaperRole, address: "maintainer@abc123"),
            new MailNudgeOutcome(true, "d-2", "noop", null), 1);

        var row = Assert.Single(log.Events, x => x.Evt == "mail.nudge");
        Assert.Equal("reaper", row.Fields.Data["role"]);
        Assert.Equal("maintainer@abc123", row.Fields.Data["address"]);
    }

    /// The brain's sentence and the row's numbers are ONE arithmetic: the clause
    /// in `reason` is rendered from the same value the row serializes.
    [Fact]
    public void TheReasonsBudgetClause_IsTheRowsBudget()
    {
        var budget = new MailNudgeBudget(2, 3, 1, 4);
        Assert.Equal("budget envelope 2/3 · role 1/4 this hour", budget.Clause);
    }

    // ---- the state a nudge leaves, read back through the file ------------------------

    /// The full loop the actor will run: decide, record, save, restart, load —
    /// and the envelope is still remembered as nudged once, so `perEnvelope`
    /// survives an idle-exit.
    [Fact]
    public void ARecordedNudge_SurvivesTheFile_SoPerEnvelopeIsNotRefunded()
    {
        using var t = new Tmp();
        using var log = new CapturedLog();
        var state = State(1_000, ("reviewer", "m-01", 12 * 60_000, 12 * 60_000, 0));
        state = NudgeStore.Record(state, Nudge(), new MailNudgeOutcome(true, "d-3", "noop", null), 1_000);
        Assert.True(t.Store.Save(state, 1_000));

        var back = t.Store.Load(nowMs: 6);
        Assert.Equal(1, Assert.Single(back.Envelopes).Nudged);
        Assert.Equal(0, 6 - Assert.Single(back.Nudges).AtMs);   // recorded at the save's moment
    }
}
