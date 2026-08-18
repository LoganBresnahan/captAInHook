using System.Text.Json;
using CaptainHook.Core;
using CaptainHook.Wire;
using static CaptainHook.Tests.TestUtil;

namespace CaptainHook.Tests;

/// ADR-0017 decision 5 (roadmap item 22, slice `mail-nudge-event`) — the robot
/// nudge as an ordinary hook event.
///
/// The decision is a refusal to build anything: no new spawner, no new policy
/// language, no new consent surface — one more event through the dispatcher the
/// shim already uses. So most of what these tests assert is that the shipped
/// machinery applies UNCHANGED, and the rest is N3's audit: the four ways an
/// internal event is not a hook, each of which fails silently if it is wrong.
///
///   * no stdout — nothing here serializes, and a hook that names the internal
///     harness is refused rather than answered with the empty string;
///   * no effects — the capability gate downgrades, from a declaration in data;
///   * no presence — a nudge carries no session, so the watcher's own action
///     can never answer the watcher's "is anybody live?" question;
///   * a denial is logged, not answered.
public class MailNudgeEventTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(2);

    private static HarnessSpec Internal() =>
        new HarnessRegistry(NoOverrides).Get(MailNudgeEvent.HarnessName);

    /// A directory with no user overrides — the embedded specs alone.
    private static readonly string NoOverrides =
        Path.Combine(Path.GetTempPath(), "captainhook-no-harness-overrides");

    private static MailNudge Nudge(string role = "reviewer", string? workspace = null) =>
        new(role, ["m-01", "m-02"], "quiet", "📬 2 waiting", "mail send kind:answer", workspace);

    private static Dispatcher With(params IHandler[] handlers) =>
        new(new Registry().On(MailNudgeEvent.EventType, handlers), Budget);

    /// A handler that records it ran, and one that captures the event it saw.
    private static TestHandler Counting(string name, Action bump) =>
        new(name, (_, _) => { bump(); return Task.FromResult<Effect>(new Effect.Noop()); });

    private static TestHandler Inspecting(string name, Action<HookEvent> capture) =>
        new(name, (e, _) => { capture(e); return Task.FromResult<Effect>(new Effect.Noop()); });

    // ---- the event exists, and it is ordinary ------------------------------

    /// The embedded `internal` spec declares exactly one event, with NO effects
    /// and no wire format. Everything else in this file follows from these three
    /// facts being in DATA rather than in code (ADR-0003's house pattern).
    [Fact]
    public void TheInternalHarness_DeclaresMailNudgeWithNoEffectsAndNoWireFormat()
    {
        var spec = Internal();
        Assert.Equal("none", spec.ResponseAdapter);
        Assert.False(spec.AnswersHooks);
        Assert.Empty(Assert.Contains(MailNudgeEvent.EventType, (IDictionary<string, IReadOnlyList<string>>)spec.Events));
    }

    /// `handlers.json` registers turn payloads kebab — `"events": ["mail-nudge"]`
    /// — and the canonicalizer maps the two spellings together, exactly as for
    /// every shipped event. A drift here is the silent kind: the registration
    /// parses, the spec parses, and no handler ever runs.
    [Fact]
    public void TheKebabRegistrationSpelling_CanonicalizesToTheEventName()
        => Assert.Equal(MailNudgeEvent.EventType, Harness.Canon("mail-nudge"));

    /// The ordinary path: handlers registered on the event run, and the
    /// dispatch is reported as having run.
    [Fact]
    public async Task ANudge_ReachesHandlersRegisteredOnTheEvent()
    {
        using var log = new CapturedLog();
        var ran = 0;
        var dispatcher = With(Counting("turn", () => ran++));

        var outcome = await MailNudgeEvent.DispatchAsync(
            Nudge(), dispatcher, Internal(), new PolicyResolution.Absent());

        Assert.True(outcome.Ran);
        Assert.Equal(1, ran);
        Assert.NotEmpty(outcome.DispatchId);
    }

    /// The payload a turn payload reads: the nudge's own fields plus the event
    /// name in the spec's request field, so the ingest path is CONFIGURED like
    /// every other harness rather than special-cased for internal events.
    [Fact]
    public async Task ThePayload_CarriesTheNudge_AndTheWorkspaceBecomesTheCwd()
    {
        using var log = new CapturedLog();
        HookEvent? seen = null;
        var dispatcher = With(Inspecting("turn", e => seen = e));

        await MailNudgeEvent.DispatchAsync(
            Nudge(workspace: "/home/you/repo"), dispatcher, Internal(), new PolicyResolution.Absent());

        Assert.NotNull(seen);
        Assert.Equal(MailNudgeEvent.EventType, seen!.Type);
        Assert.Equal("/home/you/repo", seen.Cwd);
        Assert.Equal("reviewer", seen.Payload.GetProperty("role").GetString());
        Assert.Equal("quiet", seen.Payload.GetProperty("reason").GetString());
        Assert.Equal("📬 2 waiting", seen.Payload.GetProperty("digest").GetString());
        Assert.Equal(["m-01", "m-02"],
            seen.Payload.GetProperty("envelopeIds").EnumerateArray().Select(x => x.GetString()!).ToArray());
    }

    /// A dead-mailbox nudge (ADR-0018 d6) rides the same event: the role it goes
    /// TO and the address it is ABOUT are different facts, so both are on the
    /// payload and both are on the trail. A turn payload therefore needs no
    /// branch — `address` present means "tend this box", absent means "your mail".
    [Fact]
    public async Task ADeadMailboxNudge_CarriesTheAddress_OnThePayloadAndTheTrail()
    {
        using var log = new CapturedLog();
        HookEvent? seen = null;
        var nudge = Nudge(role: "reaper") with { Address = "reviewer@s-1" };

        Assert.Equal("reviewer@s-1", nudge.Subject);
        await MailNudgeEvent.DispatchAsync(
            nudge, With(Inspecting("turn", e => seen = e)), Internal(), new PolicyResolution.Absent());

        Assert.Equal("reaper", seen!.Payload.GetProperty("role").GetString());
        Assert.Equal("reviewer@s-1", seen.Payload.GetProperty("address").GetString());

        var row = Assert.Single(log.Events, e => e.Evt == "nudge.dispatch");
        Assert.Equal("reviewer@s-1", Assert.IsType<string>(row.Fields.Data!["address"]));
    }

    /// An ordinary role nudge writes no `address` at all — absent rather than
    /// empty, so a payload can test for it.
    [Fact]
    public async Task AnOrdinaryNudge_HasNoAddressField()
    {
        using var log = new CapturedLog();
        HookEvent? seen = null;
        await MailNudgeEvent.DispatchAsync(
            Nudge(), With(Inspecting("turn", e => seen = e)), Internal(), new PolicyResolution.Absent());

        Assert.False(seen!.Payload.TryGetProperty("address", out _));
        Assert.DoesNotContain("address", Assert.Single(log.Events, e => e.Evt == "nudge.dispatch").Fields.Data!.Keys);
    }

    // ---- N3: no session, no presence ---------------------------------------

    /// A nudge carries NO session, by construction. This is the loop-with-no-
    /// bottom guard: presence is one of the facts the watcher's next decision
    /// reads (d4), so a dispatch that looked like a live session would let the
    /// watcher's own action answer the watcher's own question. The daemon's hook
    /// path stamps presence from exactly this field; a null here is what makes
    /// the omission structural rather than a rule somebody has to remember.
    [Fact]
    public async Task ANudge_NamesNoSession_SoItCanNeverCountAsPresence()
    {
        using var log = new CapturedLog();
        HookEvent? seen = null;
        await MailNudgeEvent.DispatchAsync(
            Nudge(), With(Inspecting("turn", e => seen = e)), Internal(), new PolicyResolution.Absent());

        Assert.Null(seen!.SessionId);
        Assert.All(log.Events, e => Assert.Null(e.Fields.SessionId));
    }

    // ---- N3: effects are logged and ignored --------------------------------

    /// A payload that returns an inject gets it thrown away — by the capability
    /// gate that has always done this, driven by `"effects": []` in the spec.
    /// The trail says what was returned AND what it became, so "ignored" is
    /// visible rather than merely true.
    [Fact]
    public async Task AnEffectFromAPayload_IsLoggedAndIgnored()
    {
        using var log = new CapturedLog();

        var outcome = await MailNudgeEvent.DispatchAsync(
            Nudge(), With(TestHandler.Returning("turn", new Effect.Inject("answer me this"))),
            Internal(), new PolicyResolution.Absent());

        Assert.True(outcome.Ran);
        Assert.Equal("inject", outcome.EffectKind);

        var gated = Assert.Single(log.Events, e => e.Evt == "harness.effectUnsupported");
        Assert.Equal("warn", gated.Lvl);

        var row = Assert.Single(log.Events, e => e.Evt == "nudge.dispatch");
        Assert.Equal("inject", row.Fields.Data!["effect"]);
        Assert.Equal("noop", row.Fields.Data["gated"]);
        Assert.Equal("reviewer", row.Fields.Data["role"]);
        Assert.Equal(outcome.DispatchId, row.Fields.DispatchId);
    }

    // ---- N3: a denial is logged, not answered ------------------------------

    /// `dispatch.json` is the consent, and a denied nudge simply does not
    /// happen. There is no byte-identical Noop to write because there is no
    /// stdout — the policy lines land on the trail from their one emitter, and
    /// `nudge.denied` says what became of the nudge, which is the fact a
    /// budget-keeping watcher needs.
    [Fact]
    public async Task APolicyDeniedNudge_IsLoggedAndNeverDispatched()
    {
        using var log = new CapturedLog();
        var ran = 0;
        var deny = Policy("""{ "version": 1, "default": "deny" }""");

        var outcome = await MailNudgeEvent.DispatchAsync(
            Nudge(), With(Counting("turn", () => ran++)),
            Internal(), deny);

        Assert.False(outcome.Ran);
        Assert.Equal(0, ran);
        Assert.NotNull(outcome.DenialTrace);
        Assert.Single(log.Events, e => e.Evt == "policy.skip");
        Assert.Single(log.Events, e => e.Evt == "nudge.denied");
        Assert.DoesNotContain(log.Events, e => e.Evt == "nudge.dispatch");
    }

    /// A MALFORMED policy denies the robot channel too, loudly — the same
    /// direction `dispatch.json` takes everywhere else, and the same direction
    /// `watch.json` takes for its own document.
    [Fact]
    public async Task AMalformedPolicy_DeniesTheNudgeLoudly()
    {
        using var log = new CapturedLog();
        var outcome = await MailNudgeEvent.DispatchAsync(
            Nudge(), With(TestHandler.Returning("turn", new Effect.Noop())),
            Internal(), new PolicyResolution.Malformed("bad file"));

        Assert.False(outcome.Ran);
        Assert.Single(log.Events, e => e.Evt == "policy.malformed");
        Assert.Single(log.Events, e => e.Evt == "nudge.denied");
    }

    /// The consent surface an operator already has works on nudges: a
    /// `project`-scoped rule matches because the nudge's WORKSPACE is the
    /// dispatch's cwd. Without that wiring, per-repository consent for robot
    /// turns would silently apply to none of them.
    [Fact]
    public async Task AProjectScopedRule_ScopesTheRobotChannelByRepository()
    {
        using var log = new CapturedLog();
        var policy = Policy("""
            { "version": 1, "default": "allow",
              "rules": [ { "event": "MailNudge", "project": "/home/you/secret", "decision": "deny" } ] }
            """);

        Assert.False((await MailNudgeEvent.DispatchAsync(
            Nudge(workspace: "/home/you/secret"), With(), Internal(), policy)).Ran);
        Assert.True((await MailNudgeEvent.DispatchAsync(
            Nudge(workspace: "/home/you/open"), With(), Internal(), policy)).Ran);
    }

    // ---- N3: internal never reaches a stdout-serialize path ----------------

    /// A hook that NAMES the internal harness is refused, not answered. This is
    /// the reachable version of "internal must never reach a stdout-serialize
    /// path": nothing in the nudge path serializes, but `--harness internal` is
    /// two words anybody can type. Refused like an unknown name — a clear line
    /// on stderr and ZERO bytes on stdout, which is invariant 1 either way.
    [Fact]
    public async Task AHookNamingTheInternalHarness_IsRefusedWithNoStdout()
    {
        using var log = new CapturedLog();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await HookRun.CollapsedAsync(
            new Invocation(Mode.Collapsed, "user-prompt-submit", MailNudgeEvent.HarnessName),
            new StringReader("{}"), stdout, stderr, harnessDir: NoOverrides);

        Assert.Equal(1, exit);
        Assert.Empty(stdout.ToString());
        Assert.Contains("is internal — it has no hook wire format", stderr.ToString());
    }

    /// And if a refactor ever DID route one there, the adapter writes nothing
    /// and says so. The closed adapter set's honest member: reaching it is a
    /// bug, so it emits no bytes rather than throwing on a daemon path or
    /// putting something on a stdout that belongs to no hook.
    [Fact]
    public void TheNoneAdapter_WritesNothingAndSaysSo()
    {
        using var log = new CapturedLog();
        var spec = Internal();

        var written = ResponseAdapters.Get(spec.ResponseAdapter)
            .Serialize(Ev(MailNudgeEvent.EventType), new Effect.Inject("should never happen"));

        Assert.Equal("", written);
        Assert.Equal("warn", Assert.Single(log.Events, e => e.Evt == "harness.noWireSerialize").Lvl);
    }

    private static PolicyResolution Policy(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var policy = DispatchPolicy.TryParse(doc.RootElement, out var errors);
        Assert.True(policy is not null, string.Join("; ", errors));
        return new PolicyResolution.Loaded(policy!);
    }
}
