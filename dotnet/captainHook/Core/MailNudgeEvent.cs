using System.Text.Json;
using CaptainHook.Actors;

namespace CaptainHook.Core;

// Roadmap item 22 / ADR-0017 decision 5, slice `mail-nudge-event` — the ROBOT
// NUDGE as an ordinary hook event.
//
// The whole decision is a refusal to build anything: waking a member is not a
// new spawner, a new policy language or a new consent surface, it is one more
// EVENT through the dispatcher the shim already uses. `handlers.json` registers
// turn payloads on `events: ["mail-nudge"]`; `dispatch.json` is the consent;
// bosun, budgets, the kill discipline, the exec-wire envelope and the trail
// (`dispatch.start → exec.spawn → exec.exit`) all apply unchanged. This file is
// the entry point that raises it, and it is small on purpose.
//
// **Four ways an internal event is not a hook, each handled deliberately (N3):**
//
//   1. NO SHIM AND NO STDOUT. Nobody is waiting for an answer on a pipe. The
//      `internal` harness spec declares the `none` response adapter, and this
//      path returns before serialization ever comes up; the answer to a nudge
//      travels back on the bus, as mail.
//   2. NO EFFECTS. `internal.json` declares `MailNudge` with `"effects": []`,
//      so a payload that returns an inject is downgraded to Noop by the
//      capability gate that has always done this — logged and ignored, in the
//      existing language, rather than by a new rule written here.
//   3. NO PRESENCE. A nudge carries no session, and this path never stamps one.
//      Presence feeds the watcher's own "is anybody live?" question (d4), so a
//      dispatch that counted as presence would let the watcher's action answer
//      the watcher's question — a loop with no bottom.
//   4. A DENIAL IS LOGGED, NOT ANSWERED. `dispatch.json` denying a nudge means
//      the nudge does not happen; there is no byte-identical Noop to write
//      because there is no stdout. `HookRun.DecidePolicy` is the shared
//      decision (and the one emitter of the policy trail lines); only callers
//      that own a stdout go on to build one.

/// What the brain's budgets stood at when it decided this nudge — as NUMBERS,
/// because the trail row `nudge-state-and-trail` writes must not be a sentence
/// a reader parses to learn what a poke cost (`mail.nudge`'s `budget`), and
/// because the reason's prose and the row must come from ONE arithmetic.
///
/// `Envelope`/`PerEnvelope` is the per-envelope×subject bound — how many times
/// the most-nudged envelope in this nudge will have been named, out of what the
/// governing rule allows. `RoleHour`/`PerRoleHour` is the role's sliding window,
/// counting this nudge and any decided earlier in the same evaluation. Both are
/// the values the nudge WOULD spend; a nudge policy denies spends neither
/// (`MailNudgeOutcome.Ran`), and no `mail.nudge` row is written for it.
public sealed record MailNudgeBudget(int Envelope, int PerEnvelope, int RoleHour, int PerRoleHour)
{
    /// The one rendering of these four numbers as prose. The brain's `Reason`
    /// sentence uses it, so the sentence and the row can never disagree.
    public string Clause => $"budget envelope {Envelope}/{PerEnvelope} · role {RoleHour}/{PerRoleHour} this hour";
}

/// One nudge, as ADR-0017 d5 spells it. `Digest` is rendered by the caller and
/// deterministic — the watcher's brain is pure (d4), so the text a payload is
/// woken with is a value, never something this path composes from the store.
///
/// `Workspace` is where the woken turn should run, and it doubles as the
/// dispatch's `cwd`: that is what makes `dispatch.json`'s `project` criterion
/// work on nudges, so an operator can consent to robot turns per repository
/// with the rules they already have.
public sealed record MailNudge(
    string Role,
    IReadOnlyList<string> EnvelopeIds,
    string Reason,
    string Digest,
    string ReplyHow,
    string? Workspace = null,
    string? Address = null,
    MailNudgeBudget? Budget = null)
{
    /// What the nudge is ABOUT, when that is not simply the role it is sent to.
    /// Set only by the dead-mailbox rule (ADR-0018 d6): the nudge goes to the
    /// `reaper` role, but its subject is somebody else's stranded mailbox, and
    /// a reaper that was handed only "reaper" would not know which box to tend.
    ///
    /// **It is also the key the brain tracks the named envelopes under**
    /// (`Subject`), which is what keeps two dead mailboxes of the SAME role from
    /// sharing one quiet clock and one `perEnvelope` budget for a broadcast they
    /// both hold. The role is still what a `perRoleHour` window counts, because
    /// that budget is the reaper's bill.
    public string Subject => Address ?? Role;

    /// The payload a turn payload reads off its exec-wire stdin. Snake_case
    /// `hook_event_name` because the `internal` spec reads the SAME request
    /// fields every harness spec does — the ingest path is not special-cased
    /// for internal events, it is configured like any other.
    public string ToPayloadJson()
    {
        using var buf = new MemoryStream();
        using (var w = new Utf8JsonWriter(buf))
        {
            w.WriteStartObject();
            w.WriteString("hook_event_name", MailNudgeEvent.EventType);
            w.WriteString("role", Role);
            w.WriteString("reason", Reason);
            w.WriteString("digest", Digest);
            w.WriteString("replyHow", ReplyHow);
            if (Workspace is not null) w.WriteString("workspace", Workspace);
            if (Address is not null) w.WriteString("address", Address);
            w.WriteStartArray("envelopeIds");
            foreach (var id in EnvelopeIds) w.WriteStringValue(id);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buf.ToArray());
    }
}

/// What a nudge dispatch did. `Ran` is false when policy denied it — the one
/// outcome the watcher must count differently, since a denied nudge should not
/// spend a budget the operator's rules already refused to let it use.
public sealed record MailNudgeOutcome(bool Ran, string DispatchId, string EffectKind, string? DenialTrace);

public static class MailNudgeEvent
{
    /// The canonical event name. `handlers.json` registers it kebab —
    /// `"events": ["mail-nudge"]` — and `Harness.Canon` maps the two together,
    /// exactly as for every shipped event.
    public const string EventType = "MailNudge";

    /// The harness a nudge arrives on. Not a real agent host: it is the
    /// declaration that this event has no wire format and no effects, kept in
    /// data where every other harness capability lives (ADR-0003).
    public const string HarnessName = "internal";

    /// Raise a nudge through the ordinary dispatcher.
    ///
    /// `dispatcher` is the daemon's own — the same fan-out, the same supervised
    /// workers, the same hot reload. `spec` is the `internal` harness; passing
    /// it in rather than resolving it here keeps this testable and keeps the
    /// registry's reload contract in the caller's hands.
    ///
    /// The dispatch id is MINTED here when the caller has none: a nudge has no
    /// shim to mint one (ADR-0004 d2's usual source), and without an id the
    /// `dispatch.start → exec.spawn → exec.exit` rows for a woken turn would
    /// join to nothing.
    public static async Task<MailNudgeOutcome> DispatchAsync(
        MailNudge nudge, Dispatcher dispatcher, HarnessSpec spec, PolicyResolution policy,
        string? dispatchId = null)
    {
        var id = dispatchId ?? Guid.NewGuid().ToString("N")[..8];

        JsonElement payload;
        using var doc = JsonDocument.Parse(nudge.ToPayloadJson());
        payload = doc.RootElement.Clone();

        // Through the spec's own request fields, like every other ingest: the
        // event name is passed explicitly (the CLI arg's role), the session is
        // absent by construction, and `workspace` lands in `Cwd` so the policy's
        // `project` criterion sees it.
        var evt = Harness.ParseEvent(spec, EventType, payload);

        // NO `presence.Seen(...)` here, and the omission is load-bearing rather
        // than an oversight (N3). The daemon's hook path stamps presence before
        // the policy gate because a session being denied is still a session
        // that is here; a nudge is not a session at all, and counting it would
        // feed the watcher's own action back into the "is anybody live?"
        // question its next decision reads.
        var ruling = HookRun.DecidePolicy(policy, evt, id);
        if (!ruling.Work)
        {
            // Logged, not answered. The policy lines are already on the trail
            // (DecidePolicy is their one emitter); this says what became of the
            // nudge, which is the fact a watcher's budget accounting needs.
            Log.Info("nudge", "nudge.denied", Fields(nudge, id, new Dictionary<string, object>
            {
                ["trace"] = ruling.TraceLine!,
            }));
            return new MailNudgeOutcome(Ran: false, id, "noop", ruling.TraceLine);
        }

        var result = await dispatcher.DispatchAsync(evt, id, ruling.Excluded);

        // The capability gate is what makes "effects are logged and ignored"
        // true, and it is the SHIPPED mechanism rather than a rule written
        // here: `internal.json` declares `MailNudge` with no effects, so
        // anything a payload returns warns `harness.effectUnsupported` and
        // becomes Noop. The kind is reported below so the caller can see what
        // was thrown away without reading two lines.
        var kind = Harness.KindOf(result.Merged);
        var final = Harness.ApplyCapabilityGate(spec, evt, result.Merged, id);

        // …and nothing is serialized. An internal event has no stdout; `final`
        // exists to prove the gate ran and to be asserted on, not to be written.
        Log.Info("nudge", "nudge.dispatch", Fields(nudge, id, new Dictionary<string, object>
        {
            ["effect"] = kind,
            ["gated"] = Harness.KindOf(final),
        }));

        return new MailNudgeOutcome(Ran: true, id, kind, null);
    }

    /// One shape for both rows. `SessionId` is deliberately absent: a nudge
    /// belongs to a ROLE, and stamping the trail's session column with anything
    /// here would put a window on the choreography that was never involved.
    private static LogFields Fields(MailNudge nudge, string dispatchId, Dictionary<string, object> extra)
    {
        var data = new Dictionary<string, object>
        {
            ["role"] = nudge.Role,
            ["reason"] = nudge.Reason,
            ["envelopeIds"] = nudge.EnvelopeIds.ToList(),
        };
        if (nudge.Workspace is not null) data["workspace"] = nudge.Workspace;
        if (nudge.Address is not null) data["address"] = nudge.Address;
        foreach (var (k, v) in extra) data[k] = v;

        return new LogFields { DispatchId = dispatchId, HookEvent = EventType, Data = data };
    }
}
