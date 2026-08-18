using CaptainHook.Core;
using CaptainHook.Mail;

namespace CaptainHook.Tests;

/// ADR-0017 decision 3 (roadmap item 22, slice `role-kind-inference`) — what
/// kind of thing holds a role, and whether anybody is home.
///
/// Two questions kept apart, because the brain (d4) takes them as separate
/// inputs and answering one with the other is how a watcher starts feeding
/// itself: KIND is structural (who COULD serve this role, from what is
/// registered) and PRESENCE is momentary (is anybody here right now).
///
/// The slice's as-built amendment is what most of these pin: the ADR reads as
/// though a turn payload is registered per role, but the dispatcher fans out by
/// EVENT, so a per-role registration would scope nothing and exist only to be
/// read back here — the "declared twice" d3 refuses. The capability is
/// installation-wide; the per-role gate is `watch.json`.
public class RoleKindsTests
{
    private static ExecEntry Entry(string name, IReadOnlyList<string> args, params string[] events) =>
        new(name, "/bin/true", args, events, ExecMode.Oneshot, FailMode.Open,
            Budget: null, ReadinessTimeout: null,
            Env: new Dictionary<string, string>(), PassEnv: [], Cwd: null);

    private static ExecEntry Digest(string role, string? instance = null, string evt = "UserPromptSubmit")
    {
        string[] args = instance is null
            ? ["mail", "digest", "--role", role]
            : ["mail", "digest", "--role", role, "--as", instance];
        return Entry($"digest-{role}-{instance ?? "window"}", args, evt);
    }

    private static ExecEntry TurnPayload(string evt = "mail-nudge") =>
        Entry("turn-claude", [], evt);

    private static RoleKinds Kinds(params ExecEntry[] entries) =>
        RoleKinds.From(new ExecHandlersResolution.Loaded(entries, []));

    // ---- the kinds ---------------------------------------------------------

    /// The four states. `Unserved` is not in d3's list and is the state the
    /// 2026-08-17 dogfood pass found four of on the live bus: mail addressed to
    /// a role no window reads and no robot can be woken for. Naming it is the
    /// difference between "we decided not to nudge" and "nothing here can help".
    [Fact]
    public void TheFourKinds_FallOutOfWhatIsRegistered()
    {
        Assert.Equal(RoleKind.HumanHeld, Kinds(Digest("main")).Of("main"));
        Assert.Equal(RoleKind.Mixed, Kinds(Digest("main"), TurnPayload()).Of("main"));
        Assert.Equal(RoleKind.RobotServable, Kinds(Digest("main"), TurnPayload()).Of("reviewer"));
        Assert.Equal(RoleKind.Unserved, Kinds(Digest("main")).Of("reviewer"));
    }

    /// THE AMENDMENT, pinned. A turn payload is registered ONCE and makes every
    /// role robot-servable, because the dispatcher fans out by event: a per-role
    /// registration would spawn on every nudge whatever role it named, so it
    /// could only ever annotate, never scope. Which roles may actually be woken
    /// is `watch.json`'s question, and it is a different one.
    [Fact]
    public void OneTurnPayload_ServesEveryRole_BecauseFanOutIsByEvent()
    {
        var kinds = Kinds(TurnPayload());

        Assert.True(kinds.TurnPayloadInstalled);
        Assert.Equal(RoleKind.RobotServable, kinds.Of("reviewer"));
        Assert.Equal(RoleKind.RobotServable, kinds.Of("ops"));
        Assert.Equal(RoleKind.RobotServable, kinds.Of("a-role-nobody-has-mentioned"));
    }

    /// d3's consequence, spelled once so no caller re-derives it from the enum
    /// and gets `Mixed` wrong: for a human-held role the robot channel does not
    /// exist, and for a mixed one it does (human first, robot as fallback —
    /// which is what `noLiveSession` decides in a rule).
    [Theory]
    [InlineData(RoleKind.HumanHeld, false)]
    [InlineData(RoleKind.Unserved, false)]
    [InlineData(RoleKind.RobotServable, true)]
    [InlineData(RoleKind.Mixed, true)]
    public void TheRobotChannelExists_ForExactlyTheTwoKindsThatCanBeWoken(RoleKind kind, bool exists)
    {
        var kinds = kind switch
        {
            RoleKind.HumanHeld => Kinds(Digest("r")),
            RoleKind.Unserved => Kinds(),
            RoleKind.RobotServable => Kinds(TurnPayload()),
            _ => Kinds(Digest("r"), TurnPayload()),
        };
        Assert.Equal(exists, kinds.RobotChannelExists("r"));
    }

    /// The registration writes the event KEBAB and the host spells it Pascal.
    /// Comparing raw strings would find nothing, ever, and would find it
    /// silently — every role reading human-held and no nudge ever firing, with
    /// a correctly-registered turn payload sitting right there.
    [Theory]
    [InlineData("mail-nudge")]
    [InlineData("MailNudge")]
    public void TheEventSpelling_IsCanonicalizedBeforeItIsCompared(string evt)
        => Assert.True(Kinds(TurnPayload(evt)).TurnPayloadInstalled);

    [Fact]
    public void AnUnrelatedRegistration_MakesNothingRobotServable()
        => Assert.False(Kinds(Entry("memory", [], "Stop"), Digest("main")).TurnPayloadInstalled);

    // ---- recognition, not a second parser ----------------------------------

    /// A role read by two registrations — the ambient seam and the urgent one,
    /// the normal shape — is ONE human-held role. And a role read under two
    /// `--as` names is still one role: the question is "does any window read
    /// this", and every instance of a role is a window that does.
    [Fact]
    public void SeamsAndInstances_CollapseToOneRole()
    {
        var kinds = Kinds(
            Digest("main", evt: "SessionStart"),
            Digest("main", evt: "UserPromptSubmit"),
            Digest("main", instance: "laptop-a"),
            Digest("main", instance: "laptop-b"));

        Assert.Equal(["main"], kinds.HumanHeld);
    }

    /// The gate is the REAL verb's argument parser, so a registration counted
    /// here is one the dispatcher would actually run. A registration the verb
    /// would refuse contributes no role — which matters because a role inferred
    /// from a registration that can never run is a role reported as read by
    /// nobody.
    [Fact]
    public void ARegistrationTheVerbWouldRefuse_ContributesNoRole()
    {
        string[][] refused =
        [
            ["mail", "digest"],                              // no --role
            ["mail", "digest", "--role", "Main"],            // ungrammatical role
            ["mail", "digest", "--role", "main", "--nope"],  // unknown argument
            ["mail", "digest", "--role", "main", "--as"],    // incomplete
            ["mail", "send"],
            ["mail"],
            [],
        ];
        foreach (var args in refused)
            Assert.Empty(Kinds(Entry("x", args, "UserPromptSubmit")).HumanHeld);
    }

    /// Absent and MALFORMED both mean nothing is registered — so every role is
    /// `Unserved`, which is not a fudge: a malformed `handlers.json` registers
    /// NOTHING (ADR-0010 d4), so there is no turn payload to run and no digest
    /// to read, and reporting anything else would describe a system that is not
    /// there.
    [Fact]
    public void AbsentAndMalformedRegistrations_LeaveEveryRoleUnserved()
    {
        foreach (var resolution in new ExecHandlersResolution[]
                 { new ExecHandlersResolution.Absent(), new ExecHandlersResolution.Malformed("bad") })
        {
            var kinds = RoleKinds.From(resolution);
            Assert.Equal(RoleKind.Unserved, kinds.Of("main"));
            Assert.False(kinds.TurnPayloadInstalled);
        }
    }

    // ---- presence ----------------------------------------------------------

    /// The join: cursor files × recent dispatches, the same two halves the mail
    /// snapshot uses. An AGE rather than a boolean, because "live" needs a
    /// threshold and every number about elapsed time belongs with the brain that
    /// owns `quietFor` — a second one here would be a policy nobody wrote down.
    [Fact]
    public void FreshestDispatchAge_IsTheFreshestWindowHoldingACursorForTheRole()
    {
        (string, string?)[] cursors = [("main", "s-old"), ("main", "s-new"), ("ops", "s-ops")];
        (string, long)[] recent = [("s-old", 90_000), ("s-new", 1_500), ("s-ops", 10)];

        Assert.Equal(1_500, RolePresence.FreshestDispatchAgeMs("main", cursors, recent));
        Assert.Equal(10, RolePresence.FreshestDispatchAgeMs("ops", cursors, recent));
    }

    /// Null is the honest answer for a role whose readers have all gone — a
    /// cursor with no dispatch behind it says "this mailbox was delivered to
    /// once", never "somebody is here". That is exactly the dead-mailbox shape
    /// the reaper exists for, and it is what the dogfood pass watched: four of
    /// seven cursors on the live bus were windows that had ended.
    [Fact]
    public void ACursorWithNoDispatchBehindIt_IsNotPresence()
    {
        (string, string?)[] cursors = [("main", "s-gone")];
        Assert.Null(RolePresence.FreshestDispatchAgeMs("main", cursors, []));
        Assert.Null(RolePresence.FreshestDispatchAgeMs("main", [], [("s-gone", 5)]));
    }

    /// A sessionless reader has no window and no presence to infer, and a
    /// dispatch by a session holding a cursor for ANOTHER role says nothing
    /// about this one.
    [Fact]
    public void ASessionlessCursor_AndAnotherRolesWindow_BothContributeNothing()
    {
        (string, string?)[] cursors = [("main", null), ("ops", "s-1")];
        Assert.Null(RolePresence.FreshestDispatchAgeMs("main", cursors, [("s-1", 5)]));
    }

    /// A NAMED mailbox contributes nothing, and that is the right answer rather
    /// than a limitation: since ADR-0018 d3 a cursor's key is its instance, so a
    /// durable `--as` mailbox's key never matches a session id. A `--as` mailbox
    /// is a mailbox, not a window, and nobody is sitting in it.
    [Fact]
    public void ANamedMailbox_NeverLooksLive()
    {
        (string, string?)[] cursors = [("reviewer", "ci-box")];
        Assert.Null(RolePresence.FreshestDispatchAgeMs("reviewer", cursors, [("s-1", 5), ("ci-box-session", 5)]));
    }

    /// The yes/no form exists so no two callers can disagree about the
    /// comparison; the threshold is always the caller's.
    [Fact]
    public void AnyLiveSession_AppliesTheCallersThresholdInclusively()
    {
        (string, string?)[] cursors = [("main", "s-1")];
        (string, long)[] recent = [("s-1", 60_000)];

        Assert.True(RolePresence.AnyLiveSession("main", cursors, recent, TimeSpan.FromMinutes(1)));
        Assert.True(RolePresence.AnyLiveSession("main", cursors, recent, TimeSpan.FromMinutes(5)));
        Assert.False(RolePresence.AnyLiveSession("main", cursors, recent, TimeSpan.FromSeconds(59)));
        Assert.False(RolePresence.AnyLiveSession("nobody", cursors, recent, TimeSpan.FromHours(1)));
    }

    // ---- the shared lookup -------------------------------------------------

    /// `mail status` and this file ask the same question of a registration, and
    /// there is ONE answer: `MailDigest.MailboxOf`, lifted onto the verb when
    /// the second caller appeared. A second copy would be a second answer the
    /// day the argument shape moves.
    [Fact]
    public void TheDigestLookup_IsTheVerbsOwn_AndCarriesTheInstance()
    {
        Assert.Equal(new MailAddress("main", null), MailDigest.MailboxOf(Digest("main")));
        Assert.Equal(new MailAddress("main", "laptop-a"), MailDigest.MailboxOf(Digest("main", "laptop-a")));
        Assert.Null(MailDigest.MailboxOf(Entry("memory", [], "Stop")));
    }

    // ---- registered mailboxes (ADR-0018 d6's precondition) ---------------------

    /// The third structural fact `handlers.json` holds: which NAMED mailboxes an
    /// operator declared. The dead-mailbox rule needs it because a named cursor
    /// can never look live (its key is a name, not a session), so without this
    /// every durable box with mail in it would read as a corpse the moment its
    /// window shut. Recognized through the real argument parser, like the rest.
    [Fact]
    public void RegisteredMailboxes_AreTheNamedDigestRegistrations_Only()
    {
        var kinds = Kinds(Digest("reviewer"), Digest("reviewer", "robot"), Digest("ops", "box"), TurnPayload());

        Assert.Equal(["ops@box", "reviewer@robot"], kinds.RegisteredMailboxes.Order(StringComparer.Ordinal));
        Assert.True(kinds.IsRegisteredMailbox(new MailAddress("reviewer", "robot")));
        Assert.False(kinds.IsRegisteredMailbox(new MailAddress("reviewer", "s-1")));   // a session cursor
        Assert.False(kinds.IsRegisteredMailbox(new MailAddress("reviewer", null)));    // the role itself
    }

    /// Nothing registered — a malformed or absent file — declares no standing
    /// either, which is the same "describe what is there" rule the kinds follow.
    [Fact]
    public void NoRegistrations_MeansNoDeclaredMailboxes()
    {
        Assert.Empty(RoleKinds.None.RegisteredMailboxes);
        Assert.False(RoleKinds.None.IsRegisteredMailbox(new MailAddress("reviewer", "robot")));
    }
}
