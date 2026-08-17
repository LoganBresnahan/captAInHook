# ADR-0016 — The mailbox bus: cross-harness agent communication over lifecycle hooks

**Status:** Accepted *(2026-08-12; owner accept, same day as drafting from the
owner's design sessions of 2026-08-11/12. Nothing here is implemented yet —
build order below, decomposed via `/adr-plan`.)* *(Amended 2026-08-15 —
decision 14, the observation surface: a read-only mail endpoint + the Mail
canvas; plan addendum below. All 11 original slices had landed by then.)*
*(Amended 2026-08-17 — decision 9's revisit trigger fired on the first real
exchange; the watcher, runner shape and ask/reply are
[ADR-0017](0017-watcher-nudge-and-ask.md). Same session, the owner's verdict
on the addendum's optional slice 6 `mail-replay`: **build**, in two parts —
a delivery-record preload first (fold `mail.deliver` history so cards read
✓ for pickups that predate the page), the scrub bar second.)*
**Date:** 2026-08-12
**Builds on:** [ADR-0003](0003-declarative-harness-registry.md) (the harness
spec as capability data), [ADR-0007](0007-management-api.md) (TrailCursor /
stat-poll tail prior art), [ADR-0010](0010-exec-handlers.md) (payloads as user
processes; N7 reentrancy), [ADR-0011](0011-hook-trust-model.md) (consent
boundary — deliberately NOT relaxed here).
**Evidence:** [doc/dogfood/2026-07-21-llm-payload-and-a-find.md](../dogfood/2026-07-21-llm-payload-and-a-find.md)
(the first LLM-backed payload riding the inject seam, live).

## Context

The product sentence this ADR serves: **if a harness has lifecycle hooks, its
hooks are a communication surface.** The goal is heterogeneous — multiple
harnesses (Claude Code today; any hook-bearing CLI tomorrow) running different
models, each doing what it is capable of, coordinating through the one daemon
they already share. captAInHook stops being a per-agent hook runtime and
becomes the **hub**: N external agent loops, one bus.

The topology, and why it needs almost nothing new:

```
                          you
                           │ prompts
                           ▼
  ┌─ agent A: claude (main) ────────────┐      ┌─ agent B: other harness ──┐
  │  prompt ─ think ─ act ─ act ─ stop  │      │  (own loop, own hooks,    │
  └────┬────────────┬────────────┬──────┘      │   own HarnessSpec)        │
       │ UserPrompt │ Post/Pre   │ Stop        └──────────────┬────────────┘
       │ Submit     │ ToolUse    │                            │ its events
       ▼            ▼            ▼                            ▼
     captainShim (per-event) ──────────────▶ captaind ◀────── shim/adapter
                                            (the hub)
                                  ┌────────────┴─────────────┐
                                  │ dispatcher + policy      │
                                  │  mail-digest handler ────┼─▶ Inject / Decide
                                  │  write-log observer      │   (the READ path)
                                  │  watcher spawn ──────────┼─▶ Effect.Background
                                  └───────┬──────────────────┘
                                          │ append
                                          ▼
                          ~/.captainHook/mail/   (durable — survives
                          mail.jsonl + cursors    idle-exit and deploy)
```

Everything load-bearing already exists:

- **The read seam** is the `Inject`/`Decide` effect at events the harness
  declares (DESIGN.md; live since roadmap item 2).
- **The capability card** is the `HarnessSpec` — per-event effect verbs
  declared in data (ADR-0003). It already answers "what can this harness's
  loop accept, and where," which is precisely the question a delivery planner
  asks. Adding a harness to the bus is dropping one JSON spec.
- **Members** are exec-handler payloads (ADR-0010): any language, oneshot or
  resident, supervised, env-stripped. The daemon's lifecycle already
  auto-starts residents on first hook and reaps them at idle-exit.
- **Scoping** is dispatch policy (ADR-0006): per-project, per-event,
  per-handler rules — a "swarm profile" activation surface that already ships.
- **Durable append + cursor tailing** is trail machinery (O_APPEND appender,
  `TrailCursor`'s stat-poll/oversize/truncation semantics — ADR-0007).

Two properties of the substrate shape every decision below:

1. **Delivery is opportunistic.** Hook-based delivery can only surface mail
   when the *recipient's own loop* fires an event — an idle agent is
   unreachable, and an agent deep in a long turn is reachable only at
   per-tool-call seams that tax the critical path. Mail is therefore an
   awareness channel, not an interrupt, and never a request/reply transport.
2. **The daemon is deliberately ephemeral** (idle-exit, identity cutover on
   deploy). Any mail state held in daemon memory is lost state. The store must
   be disk from day one.

And one framing correction that keeps the design honest: **membership is a
spectrum, and LLM-ness is a payload detail.** The most valuable early members
are deterministic — an API-key redactor is a PreToolUse gate that may never
read mail; a write-log observer turns both agents' PostToolUse streams into a
shared edit log. The bus must not know or care what backs a member:

```
 gate      deterministic, no mail        key-redactor: PreToolUse rewrite
 observer  write-only, deterministic     write-log: PostToolUse → edit log
 watcher   write-only, LLM on-demand     intent-watcher: Background spawn
 peer      read/write, full agent        a second harness, a human's shell
```

## Decision

1. **Store-and-forward over durable files; delivery rides existing hook
   seams; never push.** A message is *written* by appending an envelope to the
   store; it is *delivered* only when the recipient's loop walks through a
   seam its harness declares and the mail-digest handler surfaces it there as
   an ordinary effect. No component ever interrupts an agent. Consequence
   embraced, not fought: latency is bounded by the recipient's own hook
   cadence.

2. **Envelope contract v1** — one JSON object per line in the store:

   ```json
   { "v": 1, "id": "m-…", "ts": "…",
     "from": { "agent": "intent-watcher", "harness": "claude-code", "session": "s-…" },
     "to": "main",
     "kind": "status | request | answer | alert",
     "topic": "…",
     "priority": "ambient | reconcile | urgent",
     "inReplyTo": null,
     "ttlDeliveries": 3,
     "body": "opaque prose" }
   ```

   House rules apply: **strict parse, never guess** (`HarnessSpec.TryParse` /
   `DispatchPolicy` precedent) — unknown fields, unknown `kind`/`priority`,
   missing `v` ⇒ the envelope is malformed ⇒ warned-and-skipped, never
   delivered, never fatal. `body` is opaque prose the protocol never grows
   into (the ADR-0003 lesson: data selects among coded behavior; config —
   here, mail — never becomes a template language). `ts` is display-only.
   `inReplyTo` is *reserved, unread* in v1 (decision 9).

   *As built (2026-08-12, slice `mail-envelope-parser`) — what the block above
   leaves unmarked.* The sketch shows every field present; the parser settles
   which of them a sender may omit, and the direction of each call is the
   envelope's failure mode (warned-and-skipped, so too tight silently drops
   real mail while too loose delivers what nobody can render):
   * **Required:** `v` (the number 1), `id`, `ts`, `from.agent`,
     `from.harness`, `to`, `kind`, `topic`, `body`. `body` may be the empty
     string — a message whose whole content is its topic is terse, not
     invalid — but every other required string must be non-blank.
   * **`from.session` is OPTIONAL.** A write-only member (hookless harness,
     cron-shaped observer — decision 5) has no session to name, and requiring
     one would make the bus's cheapest membership tier unrepresentable.
   * **`priority` and `ttlDeliveries` are OPTIONAL, with defaults that fail
     safe:** `ambient` and `3`. A forgotten field can then never buy the
     mid-turn budget nor mean "forever". An *unknown* priority stays malformed
     rather than being silently downgraded — a sender asking for a seam class
     we do not have is a bug worth seeing. `ttlDeliveries` must be an integer
     ≥ 1: zero is a message that can never be delivered, i.e. a typo.
   * **`ts` is required, its FORMAT unvalidated.** The store is the
     inter-agent influence record (decision 13) and an undated line weakens
     it; `mail send` always stamps it, so requiring it costs nothing. Nothing
     may ever parse or compare it — TTL is delivery-counted (decision 3) and
     the wall clock stays display-only (house invariant 2).
   * **`prev` is a KNOWN field of the line**, absent when sent and written by
     the store (decision 11). A strict parser that had never heard of it would
     read every chained line as malformed the moment chaining lands. The slice
     reserves the NAME only; the encoding — genesis convention, hex form, what
     exactly is hashed — remains phase 2's durable-format decision.

3. **TTL counts delivery opportunities, not wall time.** `ttlDeliveries: N`
   means "drop after being passed over at N seams for this recipient." A
   wall-clock TTL rots while the recipient idles overnight and is the spirit
   of house invariant 2 (wall clock is display-only) violated at the design
   level. Each recipient cursor carries a monotonic `deliveries` counter that
   TTLs are measured against.

4. **The store and the cursor.** `~/.captainHook/mail/mail.jsonl`, O_APPEND,
   single-line envelopes (the trail's framing discipline). Per-(address,
   session) cursor files beside it:

   ```json
   { "v": 1, "gen": 4, "offset": 18211, "lastDeliveredId": "m-…", "deliveries": 112 }
   ```

   `offset` is a byte offset (TrailCursor semantics verbatim: oversized-line
   skip, truncation-reset, alignment-self-heal); `gen` is the mail file's
   rotation generation so an ADR-0009-style rotation never strands a cursor.
   Cursors are written by **atomic rename at the moment of injection** —
   *cursor-advance-on-inject*, delivered means "was rendered into an effect,"
   not "was acted on." This is the loop-termination guard for decision 5's
   Stop seam: a reconcile turn re-blocks only on genuinely new inbound.

   *As built (2026-08-12, `mail-cursor` slice): the cursor grew beyond this
   sketch. A bare offset cannot express out-of-file-order delivery (urgent
   delivered past held ambient) without losing the held line or doubling the
   delivered one, so `offset` is the read FRONTIER and a `held` exception
   list (offset + id + seenAt) carries passed-over mail before it; `head`
   (the chain's first-line hash) rides beside `gen` as the chain-native
   rotation check; the advance runs under a per-cursor flock with a
   deliveries-counter staleness guard. Details in the roadmap slice entry.*

   *As built (2026-08-13, `cursor-edge-adversarial-tests`): the campaign
   found the staleness guard's cross-chain blind spot — a stale view from a
   replaced chain sailed past it (different head ⇒ "the disk cursor vouches
   for nothing") and clobbered the cursor a fresher digest had written for
   the NEW chain, whose delivered mail then re-pended on the next read's
   re-anchor: a constructible double-inject, and a legitimate race once
   d13's rotation replaces chains for real. `Advance` therefore re-reads the
   store's identity under its lock (`MailStore.HeadHash`, the same
   first-complete-line rule `Pending` records, agreement pinned) and refuses
   a view whose chain is gone. Guard refusals are first-class on the trail
   (`mail.cursorRefuse`, info — usually a concurrent delivery winning);
   a view advancing over its own deleted lineage warns
   (`mail.cursorVanished`). One corner stated rather than guarded: a cursor
   deleted mid-race at deliveries 0 doubles QUIETLY — indistinguishable from
   first contact, pinned as d13's accepted deletion cost.*

5. **Three seam classes; `priority` names the class the sender requests; the
   planner degrades to what the recipient's harness declares.**

   | class | seam | traffic | discipline |
   |---|---|---|---|
   | `ambient` | turn start — `UserPromptSubmit` / `SessionStart` inject | awareness, status, findings | the default; bounded digest, deterministic rendering (priority then recency), hard token cap — overflow truncates, it does not summarize |
   | `urgent` | mid-turn — `PostToolUse`-class inject | conflict/stale-view warnings only | fires on every tool call, so: strict budget, watermark airtight, most mail must never qualify |
   | `reconcile` | turn end — `Stop` answered `block` + reason | "you have unopened mail" | the agent finishes its task first, then takes one explicit reconcile cycle; next Stop passes clean by decision 4's cursor rule |

   Degradation is downward and data-driven: `urgent` to a harness with no
   mid-turn verb becomes `ambient`; a harness declaring only turn-start inject
   gets exactly that; a hookless harness is write-only membership. The planner
   is a pure function `(envelope.priority, event being dispatched, recipient's
   HarnessSpec verbs) → deliver | hold | degrade` — no per-harness code paths.
   Enabling the Stop seam for claude-code is a **data edit** (the embedded
   spec currently declares `"Stop": { "effects": [] }`); if the
   `claude-hook-json` adapter's rendering of `Decide` is not event-appropriate
   for Stop's block shape, that is coded-adapter work inside ADR-0003's closed
   set, not config.

   *As built (2026-08-12, slice `mail-digest-handler`) — what the table above
   leaves unmarked.* **The seam CLASS is registration data**: "is PostToolUse
   a mid-turn seam?" is a loop-position fact no HarnessSpec field carries and
   no event name answers without hardcoding one harness's vocabulary — and d7
   already says registration is configuration — so the digest registration
   declares it (`--seam ambient|urgent|reconcile`, one `handlers.json` entry
   per seam class) and the planner stays pure over (priority, seam class,
   verbs). The matrix as built: ambient- and reconcile-class seams deliver
   ALL priorities (the cursor's obligation — once a seam advances, everything
   held ages, so holding at an advancing seam only burns TTL); an
   urgent-class seam delivers urgent only, and when nothing urgent is pending
   it answers noop WITHOUT advancing, so quiet mid-turn seams age nothing.
   The vehicle degrades downward only, and `inject` is preferred at EVERY
   seam class: mail never escalates itself into a deny to get read, so the
   reconcile class reaches for `decide` (verdict `deny`, the digest as the
   reason) only when inject is absent — Stop's shape once phase 5 declares
   it — and a `--seam reconcile` typo on a decide+inject mid-turn event
   degrades to a plain inject instead of denying the user's tool call (a
   skeptic-pass find). An event the spec does not declare — or declares
   with no effects — delivers nothing and never advances: the capability
   gate is permissive there, but it noops AFTER the advance, and that
   direction is mail lost silently. The "hard token cap" is a CHARACTER
   cap (deterministic; tokens are model-specific), per-seam-class defaults
   4096 / 1024 (urgent), `--max-chars` to override, whole-item granularity —
   with one anti-deadlock exception: a first item too big to ever fit is
   delivered truncated with an explicit marker, because held-forever is mail
   lost and the full text stays durable in the store. Truncation cuts the
   BODY only: the provenance head always renders whole, its
   sender-controlled fields display-clamped, and the expired parenthetical
   names a count plus at most three clamped ids — so the cap can be exceeded
   only by a bounded constant, and no rendering path can consume mail while
   erasing the id needed to look it up (two more skeptic finds). The
   provenance line carries the envelope **id** before the topic (the join
   key back to the store, and the handle d9's future ask/reply would
   quote). `--resident` speaks ADR-0010
   d3's lock-step protocol, because an urgent-class registration fires per
   tool call — a cold JIT start per dispatch is the tax ADR-0004 d7 killed.
   Stated hazard, not engineered around: the digest's answer still crosses
   the dispatcher merge and the capability gate after the cursor has
   advanced, so a co-registered deny/replace handler on the same event can
   eat a delivered digest — registration guidance is to give the digest's
   seam events to itself.

   *As built (2026-08-13, slice `stop-reconcile-seam`) — the Stop seam turned
   on.* The data edit landed as `"Stop": { "effects": ["decide"] }` — decide
   and ONLY decide, which is what makes the block the non-escalating vehicle
   here rather than a second choice, since the vehicle rule prefers inject
   wherever inject exists. **The conditional above fired**: the adapter's
   `Decide` rendering is NOT event-appropriate for Stop, so the coded-adapter
   branch this decision reserved was needed. Concretely — read off the shipped
   host's own schemas rather than its published docs, which describe a
   different (nested) shape — `hookSpecificOutput` is a union keyed on
   `hookEventName` with **no `Stop` member at all**, so the `permissionDecision`
   shape every other event takes fails the union parse and the block is
   dropped with no error: the "ships silently" hazard, made concrete.
   `ClaudeHookJsonAdapter.DecidesAtTopLevel` renders Stop (and `SubagentStop`,
   the same contract, declared decide-only in the spec exactly like Stop so an
   inject there flattens at the gate instead of shipping the unparseable
   nested shape) as the top-level `{"decision":"block","reason":…}` pair,
   with `ask` — a word the top-level vocabulary lacks; a third word fails the
   host's schema parse and the whole decision is discarded — degrading to
   noop on the existing never-send-what-it-cannot-represent rule. The full contract, the harness
   version it was read from, and the re-probe command live in
   doc/platform.md § The Stop block shape.

   Building it surfaced a **defect the seam could not work around**:
   `Harness.Canon` short-circuited on names without a hyphen, so a
   single-word event stayed lowercase — and the install template writes
   `hook {event-kebab}`, making `stop` exactly what arrives live. That name
   matched no spec declaration, so every capability lookup missed into the
   permissive undeclared path, the digest saw no declared verbs and noop'd
   forever, and any echoed `hookEventName` would have been a word the host
   rejects outright. Harmless for as long as no single-word event was wired
   up; the whole seam the moment one is. A single word is now just a
   one-segment kebab and takes the same rule. Fallout from that fix, found by
   the same pass and closed with it: a spec's event KEYS were stored raw into
   a case-sensitive map, so an override declaring `"stop"` would now miss
   every lookup and fall to the permissive undeclared path — a deliberately
   restrictive declaration flipping OPEN, the one direction a capability gate
   must never fail. Spec keys canonicalize at load like registrations already
   did, and two spellings of one event is malformed rather than a merge.

   The phase-4 hazard above gets SHARPER here and is still not engineered
   around: `Merge` takes the FIRST deny in registration order, so a
   deny-answering handler registered ahead of the digest on Stop wins and the
   digest's reason — the rendered mail — is discarded. The cursor has already
   advanced and `mail.deliver` has already been written with a `renderHash`
   attesting to what was shown, so that mail is skipped permanently AND the
   ledger claims a delivery that never reached the model — d10's "may
   under-claim, never claim falsely" broken from the outside. Registration
   guidance is unchanged and now load-bearing rather than advisory: **give the
   digest its seam events to itself.** The fix, if a second Stop handler ever
   becomes legitimate, is for `Merge` to concatenate deny reasons rather than
   take the first.

6. **Addresses are stable roles, not session ids.** `to: "main"` names a role;
   the store maps role → live session(s) at delivery time (each live session
   holding the role has its own cursor and receives its own delivery).
   Sessions are ephemeral; a mailbox that addressed them would strand mail on
   every restart.

7. **Mailbox tooling ships as engine CLI verbs; the dispatcher never learns
   about mail.** `captainHook mail send` (append one validated envelope;
   universal write path — anything that can run a process can speak) and
   `captainHook mail digest` (the read path: an exec-wire handler command —
   registered in `handlers.json` with the co-located engine binary as the
   command — that reads store + cursor for the event on stdin and answers
   `inject`/`decide`/`noop` on stdout). Rationale: the envelope's strict
   parser, the cursor's atomicity, and the TTL arithmetic belong in tested C#
   with zero new dependencies (invariant 3), not in a shell script with a jq
   dependency; and putting them behind `mail` verbs keeps the dispatch path
   byte-identical for everyone who never registers the handler. Core model,
   `Dispatcher`, wire contract, shim: untouched. Which events the digest
   handler is registered on in `handlers.json` **is** the deployment's
   delivery capability — registration is configuration.

8. **A swarm profile is a data bundle; activation is a policy flip.** Members
   stay permanently *registered* (`handlers.json`); whether they run for a
   given project/session is `dispatch.json`'s job (ADR-0006 per-project
   scoping — already live). "Jump into a session and the swarm boots" is the
   existing daemon lifecycle: first hook → shim spawns daemon → daemon spawns
   resident members, idle-exit reaps them, the durable store loses nothing.
   Defaults: LLM members are **on-demand** (`Effect.Background` /
   oneshot — a resident headless model burns tokens while the human thinks);
   resident is reserved for members needing warm state or sub-second reaction,
   and residents are daemon-scoped (they serve all sessions on the hub).
   **ADR-0011's consent boundary is deliberately not relaxed:** a profile
   bundles registrations and policy rules — data — but installing a member's
   *executable* still goes through the verbatim-consent gate, per payload. A
   one-click swarm that silently writes scripts is exactly the trigger that
   ADR refuses to fire.
   *Note (2026-08-12) — named modes are this decision's future convenience,
   not new machinery.* "Swarm mode ⇄ simple mode" is entirely a policy
   question: registrations say what exists, `dispatch.json` says what runs,
   and hot reload already makes a policy swap take effect on the next hook.
   A mode is therefore a NAMED, saved policy document (e.g.
   `~/.captainHook/modes/<name>.json`) plus a switch gesture (`captainHook
   mode <name>` or a GUI picker) that writes the active `dispatch.json`
   through the existing validated atomic path. A mode swaps policy ONLY,
   never registrations — consent stays per-executable. Deferred on the
   ADR-0006 precedent (the declined pause mechanism: the primitive exists,
   convenience lands when friction is real); note that per-project scoping
   already expresses "swarm here, simple everywhere else" with no mode
   concept at all, so modes earn their keep only for same-project switching.

9. **Ask/reply correlation is OUT of v1.** Request/reply over opportunistic
   delivery answers questions late or never; an agent that must *act on* an
   answer needs a synchronous tool (daemon-hosted; MCP-shaped), which is its
   own design with its own pending-view and timeout semantics. `inReplyTo`
   reserves the field so v1 envelopes survive that future without a version
   bump. Revisit trigger: dogfooding the write-log/conflict payload produces a
   real case where an agent needed to *wait* on another agent, not merely hear
   from it.

   *Revisit trigger FIRED 2026-08-16:* the bus's first real exchange (a
   two-window review of commit 1ee7218) had the maintainer send a request it
   could do nothing but wait on, until a human typed in the other window.
   Ask/reply, the watcher and the runner shape are
   [ADR-0017](0017-watcher-nudge-and-ask.md), which reads `inReplyTo`
   (decision 8 there), keeps addresses as roles, and resolves N5 below by
   thread-aware delivery preference. This decision stands for v1 as built.

10. **Every delivery carries provenance — in both directions.** Into the
    context: the rendered digest prefixes each item with sender, harness, and
    age ("from intent-watcher (claude-code), 2 turns ago"). Injected mail is a
    prompt-injection surface *by design* — that is the feature — so the
    renderer is a policy layer, not a passthrough, and the recipient always
    sees who is speaking (N1). Into the trail: delivery is a **first-class
    ledger event** —

    ```
    mail.deliver { dispatchId, seam, recipient: {role, session},
                   envelopeIds: [m-…], renderHash, bytesInjected }
    ```

    — which closes the cross-agent causality chain using only existing join
    keys: envelope (`from`/`id`/`inReplyTo`, durable in the store) →
    `mail.deliver` (envelope ids ↔ dispatchId ↔ recipient session) → the
    recipient's own subsequent hook events (same session). "Why did agent A
    do X" is answerable by reconstructing exactly what A saw. The event
    records envelope **ids + a hash of the rendered digest**, not the digest
    bytes — the bodies are already durable in the mail store, ids keep the
    trail lean, and `renderHash` makes the rendering itself tamper-evident.

    *As built (2026-08-13, `mail-deliver-ledger-event`):* the sketch above
    nests `recipient: {role, session}`, but `sessionId` is a **first-class
    trail column** every existing consumer reads at the top level (the JSONL
    filters, the API stream, the GUI trace) — nesting it would make mail
    delivery the one event invisible to a session filter. Shipped as
    `sessionId`/`dispatchId`/`hookEvent` in the standard fields with
    `role`, `seam`, `vehicle`, `envelopeIds`, `renderHash`, `bytesInjected`
    in `data`: the join keys are unchanged and strictly better connected.
    `vehicle` was added because it is the one fact no other ledger event can
    reconstruct — whether the digest *informed* the loop (inject) or *blocked*
    it (a reconcile-class decide). Two bounds the slice had to state: the
    event fires **only** from the branch where the cursor really advanced (a
    refused advance answers noop and must claim nothing), and it lands
    **after** the answer is written, on the `mail.expire` ordering rule — the
    ledger may under-claim a delivery, never claim one falsely. Envelope ids
    are display-clamped by the same clamp the digest head uses, so one 128KiB
    sender-controlled id cannot bloat a trail line and the id on the ledger
    is character-for-character the id the recipient was shown.

11. **Tamper-evidence: the mail store is hash-chained; trail chaining is a
    named follow-up, not silence.** The threat that matters is new with the
    bus: exec members run *as the user*, so the processes with motive and
    opportunity to rewrite history are exactly the swarm. Nothing stops a
    determined same-user attacker (accepted, unchanged trust boundary) — the
    goal is converting **silent** tampering into **detectable** tampering,
    which also catches accidental truncation/corruption long before malice.
    Mechanism: each appended envelope line carries
    `prev: sha256(previous line)` (BCL only, invariant 3 intact); appends are
    serialized by `flock`, which multi-process `mail send` needs for
    correctness anyway. Chaining the *trail* is deliberately deferred with the
    sharp edge named rather than hidden: the trail has TWO emitters (engine
    `Log` sink + shim `WireJsonl`) whose O_APPEND writes interleave — a chain
    requires read-last-line + append under a lock on the per-hook hot path,
    in both emitters, without breaking the golden byte-equality pin. Revisit
    trigger: the mail chain proves its worth in dogfood, or a real integrity
    incident; decide then with the lock's cost measured, not assumed.

12. **Rule changes are first-class events.** Governance is preventive
    (policy) + detective (trail) + review (GUI); today the trail sees policy
    *effects* (`policy.reload`, `policy.skip`) but not policy *content*.
    Every `policy.reload` and every API `PUT /policy` gains the document's
    content hash + byte size, so "which rules were in force at time T" is
    reconstructable by matching hashes against known documents and any drift
    is visible on the ledger. Full document snapshots into the trail are
    rejected for v1 (trail bloat; the hash suffices while policy documents
    are few and named). D8's future mode flips ride the same event — a mode
    switch is exactly the state change an audit wants recorded.

13. **Three stores, three lifetimes, one permission.** The records have
    different lives and rotation must not be inherited across them:
    **cursors** are pure delivery state (worthless once consumed; deletable
    anytime, a deleted cursor just re-anchors); the **trail** is operational
    telemetry (ADR-0009's rotation, days-to-weeks); the **mail store** is the
    inter-agent influence record — the longest-lived thing on disk, retained
    on a provenance clock, rotated by `gen` with archived generations kept by
    default. All three are `0600` like `api.json`. *(As-built 2026-08-15: true
    of the cursors and the mail store from the start; the **trail** was the one
    store that missed it — neither emitter set `UnixCreateMode`, so it landed at
    the process umask (`0644` live). Closed the same day: both emitters create
    the file 0600 and `logs/` 0700, the directory mode being the half that also
    covers payload-written logs the engine never creates. Create-mode applies on
    CREATE only, so a pre-fix tree is discarded at deploy rather than chmod'ed —
    cheap, given the trail's days-to-weeks lifetime. Pinned per emitter and
    asserted EQUAL by `TrailAppendTests`, since whichever emitter reaches a
    fresh trail first decides the mode.)* And a boundary stated
    plainly instead of engineered around: **the bus is a recorded medium by
    design** — there is no "never-record" envelope flag (rejected below), so
    the rule for members is *don't put secrets in mail*; secret-scrubbing
    belongs to gate members (the key-redactor) at the tool seam, not to holes
    in the ledger.

14. **The bus is observable, and observation is not delivery** *(amendment
    2026-08-15, owner-requested after the first members went live)*. The
    daemon's GUI gains a **Mail** view: a zoomable canvas that draws the whole
    bus live — every mailbox, every session's cursor, every envelope, arriving
    and being picked up — and it is **strictly read-only in a way the
    architecture already guarantees rather than the UI merely promising.**
    Three parts:

    - **A read endpoint, `GET /api/v1/mail`** — the snapshot: chain status
      (`VerifyChain` result, head, `gen`, count, modes), the ledger's lines
      from an optional `?since=<offset>` (offset-resumable, the same shape as
      the cursor's frontier and the SSE `Last-Event-ID`), and one
      `MailPendingView` per cursor file on disk (`MailCursors.Pending` — a
      pure read: it computes frontier / held / expired and writes nothing).
      Presence of a "connected harness" is **inferred**, not tracked: a
      session is present if it holds a cursor or has appeared in recent
      dispatch events, shown with its last-seen; the daemon has no session
      registry and no heartbeat, and this decision does not add one — the
      canvas fades a stale session, it never claims liveness.
    - **The live signal is the trail, unchanged in kind.** `/api/v1/events`
      (ADR-0007) already tails the file both emitters append; `mail.append`,
      `mail.deliver`, `mail.expire`, `mail.cursorAdvance` and the three
      re-anchor events are the entire choreography, and the GUI is one more
      subscriber to a stat-poll tail that has never had a back-channel to an
      emitter — a slow canvas gets a `Gap` and re-snapshots; it cannot slow a
      digest. One enrichment: `mail.append` gains the envelope's `from`
      (agent / harness / session), `kind`, `priority`, `topic` and
      `ttlDeliveries` — provenance for the arrival animation, never the
      `body` (the trail is operational-lifetime and payload-readable; the body
      belongs to the archival store alone). Both trail emitters are pinned by
      golden tests, so this is a schema-seam change and lands as one.
    - **The canvas draws the mechanism, not a mailbox metaphor.** The spine is
      the ledger — one bus line in append order — because on this bus mail
      never moves: an envelope is appended once, and *cursors move past it*.
      Each `to` role hangs off the bus as a lane; each session in that role is
      a cursor sliding along it, tagged with the seam it lands at; held
      envelopes show their TTL countdown, expired ones grey. **Semantic zoom**
      (the zoom level selects the render tier — far: roles + counts + pulse;
      mid: sessions / cursors / frontiers; near: envelope cards with d10's
      provenance, chain link, and the envelope's `mail.deliver` records)
      rather than scaling text into illegibility. Plain SVG with a `viewBox`
      in the store's slice — no canvas/graph library (ADR-0015's zero-new-deps
      culture) — and the model behind it is a **pure reducer**
      `(state, trailLine) → state` seeded from the snapshot, which is what
      makes the choreography unit-testable without a browser and makes
      **replay** (feed the same reducer trail lines from an older offset) a
      scrub bar rather than a feature.

    **Read-only is pinned, not promised**, three ways: (i) `ApiReadModel` is
    handed `MailStore.Read` / `VerifyChain` / `MailCursors.Pending` and
    nothing else — `Append` and `Advance` are not reachable from the API
    graph, so a "mark read" button has nothing to call; (ii) a route-table
    test asserts nothing under `/api/v1/mail` answers a non-GET (the auth-gate
    tests' family); (iii) the reducer never derives "delivered" from anything
    but a `mail.deliver` ledger line — an envelope behind a cursor with no
    ledger record renders as *before cursor · no record* (the trail is
    days-to-weeks and gets discarded; the store is the archival truth), never
    as delivered. Only the digest advances a cursor; the GUI observing a
    mailbox changes nothing on disk, and this sentence is the invariant.
    Sending from the GUI (the operator as a bus member) is deliberately **not
    here** — it is a new write path beside `mail send` and a d5/ADR-0011
    consent question; it needs its own decision if wanted.

    *As-built amendment (2026-08-15, slice `mail-reducer`).* "The trail,
    unchanged in kind" held; "the entire choreography" did not, quite: the
    reducer's first drive found that `mail.cursorAdvance`, `mail.expire`
    and the re-anchor family named a ROLE but not the session whose cursor
    moved — unattributable the moment a role has two sessions, and a canvas
    that guessed would draw a cursor moving that did not. So the cursor
    family gained the trail's first-class `sessionId` column (d10's own
    rule for `mail.deliver`, so one session filter sees the whole
    choreography), the advance carries `deliveredOffsets` beside its count
    and the expire its `offset` beside its id (ids are not unique on this
    bus), the re-anchor carries the `deliveries` it preserves and a
    structured `cause` (`"cursor"`: the file is distrusted, the store
    believed intact; `"store"`: the bytes the cursor described are gone —
    the skeptic pass's find: without it a watcher rebuilt a pending set from
    a ledger that no longer existed), and `mail.append` carries the line's
    `bytes` so a tail reader can track the store's frontier without a
    snapshot. `MailPendingView`/`MailCursorDto`
    gained the cursor's own `offset` (its position; `frontier` is the
    store's end) — a role with no fresh mail would otherwise leave the
    position unknowable. All pinned as golden trail lines (`WireJsonlTests`)
    and by the generated reducer fixture below. The pure reducer is
    `web/src/mail.ts`; N8's mitigation is mechanical — see its ground truth
    row.

    *As-built amendment (2026-08-15, ahead of slice `mail-live-choreography`).*
    The reducer's drive left one question open for slice 5 and it is answered
    here, because the answer is an engine field rather than a client tactic.
    A live view needs both the snapshot and the `mail.*` stream, and the gap
    between acquiring them is either **lost** or **duplicated**. Snapshot-then-
    subscribe loses: a fresh subscription anchors at the trail's current end
    (ADR-0007 d5), so the window's events are simply gone, and a vanished
    `mail.cursorAdvance` leaves an envelope drawn pending forever with nothing
    flagged — precisely the silent-wrong picture d14 exists to prevent.
    Subscribe-then-snapshot cannot lose but replays the window, which the
    reducer survives by design (`deliveries` is a per-cursor sequence number;
    a `mail.deliver` record's identity is its content) with one honest
    exception: a replayed FIRST advance is indistinguishable from a
    deleted-and-restarted cursor lineage, so the reducer flags `uncertain` and
    asks for a re-snapshot — and the window is exactly where a first advance
    replays, making the flag routine rather than exceptional.
    So **`MailDto` carries `TrailEventId`**, the trail's end at the moment the
    snapshot was taken, and the client subscribes with `Last-Event-ID: <it>`.
    The SSE id IS the byte offset after a line, so the resume begins exactly
    where the picture's knowledge ends: zero loss, zero duplicate. Two
    properties make it sound rather than merely convenient. (1) The stamp is
    read **before** the store, so the residual window — two in-process reads,
    not a network round trip — can only ever produce a duplicate, never a
    loss; the direction of the error is a statement's placement, and a source
    pin holds it there. (2) The field is **nullable and never defaulted**: a
    daemon serving no trail has no id space to align to, and the caller must
    fall back to subscribe-then-snapshot, because 0 in this space means "from
    the first byte" and would replay the entire trail as live. Rotation and
    truncation need nothing new — `TrailCursor` already resets and emits
    `Reset` (id 0), which the reducer already re-seeds on. The replay rules
    stay exactly as written; they simply stop being load-bearing on first
    paint and go back to covering what they were written for: reconnects,
    gaps, and a replaced trail.

    *As-built amendment (2026-08-15, slice `mail-live-choreography`).* Two
    shapes the sketch did not have. (1) **The stamp is a STRING and the Mail
    view runs its OWN subscription**, both forced by ADR-0009 d2: the resume id
    is an opaque monotonic cursor a client stores and echoes and never
    interprets, so the tempting design — let the bus ride the trace's already
    open stream and drop frames whose id is at or behind the stamp — is not
    available, because that comparison IS an interpretation and d4 will
    redefine the id as a cross-segment global offset underneath it. Opening a
    second subscription AT the stamp asks the server the same question and
    needs no arithmetic at all. It is independently right: the trace's buffer
    is `TRACE_CAP`-capped, and dropping the oldest line is correct for a log
    and silent corruption for a reduced picture, which cannot know what it was
    never shown. The string typing then makes the opacity structural rather
    than a comment, and removes the one comparison that must never collapse —
    `"0"` (replay everything) and absent (no trail served) on the same side of
    a falsy test. The cost is honest and small: one more attached observer, so
    the stream is started LAZILY on the Mail view's first visit rather than at
    session start. (2) **Resync is a first-class state, not an error path.**
    The reducer already refuses to guess and says so by raising `resnapshot`;
    the driver watches for exactly that, tears the stream down, re-seeds, and
    re-anchors at the NEW stamp — which is why `seedMail` replacing state is
    the right shape rather than a merge, and why the badge has a word for it.
    The animations are CSS keyed on state the reducer already computes
    (`MailGlyph.arrival` from the line's `source`, `MailTrack.motion` from the
    cursor's `lastEventKind`), so the canvas computes no motion of its own and
    the whole choreography vanishes under `prefers-reduced-motion` without
    removing a single fact. One distinction is load-bearing rather than
    decorative: an advance SLIDES and a re-anchor JUMPS, because a cursor only
    ever reads forward and animating a re-anchor as a leftward slide would
    depict a cursor reading backwards.

## Rejected alternatives

| alternative | disposition |
|---|---|
| **Push delivery / interrupting agents** | Rejected — no harness offers an interrupt surface, and faking one (writing to the agent's tty, killing turns) breaks the property that makes passive coordination safe: the human's agent keeps single-threaded control of its own loop. |
| **In-daemon (memory) mail store** | Rejected — the daemon is ephemeral by design (idle-exit, deploy cutover). Memory state is lost state; the disk store makes daemon death free. |
| **Wall-clock TTL** | Rejected — rots while the recipient idles; house invariant 2's spirit. Delivery-opportunity TTL measures staleness in the unit that matters to an agent: its own turns. |
| **Session-id addressing** | Rejected — strands mail on every session restart. Roles are the stable name; sessions are resolved at delivery. |
| **LLM-summarized digest by default** | Rejected — prose-summarizes-prose is a telephone game; each hop loses fidelity, and it puts a model on the delivery path of every turn. Deterministic bounded rendering; truncation on overflow. LLM summarization only as a deliberate future overflow mode, never the default. |
| **Adopting A2A (Agent2Agent) wholesale** | Rejected for v1 — A2A solves discovery and task lifecycle between *services*; this bus coordinates *loops on one machine* through seams they already have. The HarnessSpec is the capability card, grounded in verified hook mechanics rather than self-description. Worth re-reading A2A before any v2 envelope growth. |
| **New core `Effect` verbs / mailbox awareness in the dispatcher** | Rejected — the closed effect set already expresses delivery (`Inject`, `Decide`, `Background`). The thesis test: the bus is payload + data + two CLI verbs; the loop machinery does not change. |
| **Mailbox implemented as shell-script payloads** | Rejected — the strict parser, cursor atomicity, and TTL arithmetic are exactly the code that must be tested in-suite, and a script needs jq (a dependency invariant 3 refuses) or hand-rolled JSON (never-guess violated). Engine CLI verbs, decision 7. |
| **A dedicated swarm boot mechanism / UI "start swarm" verb** | Rejected — the daemon lifecycle already is the boot mechanism, and activation-as-policy reuses a shipped, scoped, hot-reloading surface. A boot verb would be a second way to say what `handlers.json` + `dispatch.json` already say. |
| **A database (SQLite) for mail/audit** | Rejected — invariant 3 (its package), and the architecture is already event-sourced: append-only JSONL is the sole source of truth, cursors are consumer offsets, the GUI/SSE are projections. If a review UI ever needs indexed joins, it gets a **rebuildable projection derived from the ledger**, never a second source of truth. |
| **A "never-record" envelope flag** | Rejected — an unrecorded influence channel defeats the ledger's whole purpose (d13). The bus is a recorded medium by design; secrets are the redactor-gate's job at the tool seam, not the mail layer's. |
| **Full policy-document snapshots in the trail** | Rejected for v1 (d12) — trail bloat for documents that are few, named, and hash-matchable. Revisit if hash-matching ever fails to attribute a rules-in-force question in practice. |

## Consequences

### Positive

- **Heterogeneity becomes a data problem.** A new harness joins the bus by
  dropping a spec; its delivery capability is the verbs it declares; the GUI's
  harness matrix already renders exactly that. No per-CLI code paths.
- **The killer payload becomes buildable:** both agents' `PostToolUse` streams
  into one edit log, stale-view/conflict warnings delivered as `urgent` — the
  thing no single-agent framework can do, produced by the hub position alone.
- **Zero core.** Dispatcher, wire, shim, effect set: untouched. The bus is
  two CLI verbs, one store directory, payloads, and data.
- Deterministic members are first-class; the redactor/gate class needs no
  mailbox at all and still belongs to the swarm.
- Daemon death, idle-exit, and deploy cutover are free — the store and
  cursors are disk.

### Negative

- **N1 · A designed prompt-injection surface.** Everything delivered steers
  the recipient; a buggy or hostile member can steer the human's main agent.
  Mitigations: provenance on every line (decision 10), deterministic rendering
  (no summarizer to socially engineer), policy scoping of who may run at all
  (decision 8), and the ADR-0011 consent gate on every member executable.
  Residual risk accepted within the same-user trust boundary — and named.
- **N2 · `urgent` is not immediate.** Delivery latency is bounded by the
  recipient's hook cadence; an idle recipient hears nothing until it wakes.
  This is decision 1's price, accepted; anything needing a guarantee waits for
  decision 9's future tool.
- **N3 · The Stop seam can loop.** Block-on-unread must terminate;
  cursor-advance-on-inject (decision 4) is the guard and MUST be pinned by a
  test shaped "reconcile turn generates no new inbound ⇒ second Stop passes."

  *As built (2026-08-13, `stop-reconcile-seam`).* Pinned end to end through a
  real daemon, a spawned digest child, and the live adapter. Two things the
  slice's skeptic pass established about the guard's surroundings, neither
  engineered around, both stated so nobody assumes otherwise:

  **The harness offers no second guard.** The host has no loop cap of its own
  — `Stop` and `SubagentStop` share one runner, and `stop_hook_active` is
  merely *passed to* hooks, never enforced. Termination therefore rests
  entirely on our cursor advance, for every handler answering at that seam,
  not only the digest.

  **Which makes fail-closed on Stop a livelock.** `Dispatcher.Fail` answers
  `Decide(Deny)` when a `FailMode.Closed` handler crashes, so such a handler,
  broken at turn end, blocks every Stop forever with no bound. Declaring
  `Stop: ["decide"]` is what put that answer on the wire — before the data
  edit the capability gate flattened it to Noop. Not reachable in any shipped
  configuration (exec handlers default to fail-open, and nothing fail-closed
  registers at turn end), so it is recorded rather than guarded: registration
  guidance is that **fail-closed handlers do not belong on Stop**, and the
  belt-and-braces fix, if the hazard ever becomes real, is to honor
  `stop_hook_active` from the Stop payload — answer noop when it is true and
  nothing new is pending — which costs the ability to deliver mail that
  arrived during a reconcile turn.
- **N4 · The store grows unbounded** until rotation mechanics land (ADR-0009's
  analogue; `gen` in the cursor reserves them, d13 sets the retention policy —
  archived generations kept by default). Acceptable for v1 dogfood volume; not
  for leaving on for months.
- **N7 · The flock'd chained append is a new serialization point** (d11).
  Negligible at mail volume, but it is the reason trail chaining is deferred
  rather than assumed cheap — the same lock on the per-hook trail hot path is
  a measured-cost decision, not a default.
- **N5 · Multi-session fan-out.** A role held by two live sessions receives
  twice (one cursor each). Correct for awareness traffic; would be wrong for
  future ask/reply — recorded so decision 9's design remembers it. *Picked up
  by ADR-0017 d8: an answer prefers the asking session's cursor (urgent-class
  there, ambient elsewhere) without addressing sessions.*
- **N6 · Watcher token cost.** Every LLM watcher is a model call on somebody's
  bill; on-demand default + policy scoping are the throttles, and each watcher
  carries ADR-0010 N7's reentrancy guard (`--setting-sources ""` pattern) so a
  watcher's own model call never re-enters the bus.
- **N8 · Two implementations of "pending".** Decision 14's reducer
  reproduces cursor semantics (frontier / held / TTL-in-deliveries / expired)
  in TypeScript so the canvas can animate between snapshots; the C#
  `MailCursors.Pending` remains the only truth. A divergence would draw a
  mailbox state that does not exist. Mitigation: the reducer's golden
  sequences are derived from the C# tests' fixtures, and every re-snapshot
  (`Gap`, `Reset`, reconnect, view open) replaces the reduced state with the
  daemon's view rather than merging into it — the reducer is an interpolator
  between authoritative reads, not a second store. The trail-lifetime gap
  (delivered-but-unrecorded, d14 pin iii) is the same asymmetry stated
  honestly on screen.

## Implementation plan

*Decomposed via `/adr-plan` 2026-08-12: **11 slices → 6 phases**; critical
path `mail-envelope-parser → mail-store-chained-append → mail-cursor →
mail-digest-handler → cursor-edge-adversarial-tests →
swarm-profile-and-flow-doc`; adversarial verify on exactly five slices
(store, cursor, digest, the adversarial-test campaign itself, stop-seam);
no ultracode — every hard slice is one cohesive invariant, not breadth.*

**Model per slice** (house pattern from item 19's `policy-rule-builder`:
opus builds where precedent + tests make correctness checkable; fable where
a plausible-but-wrong single pass is the failure mode, and always for the
independent skeptic in a verify pass):

| slice | build | verify |
|---|---|---|
| `mail-envelope-parser` | opus (precedent transcription; table tests fail loudly) | — |
| `policy-content-hash` | opus (two log emits, existing hash pattern) | — |
| `mail-store-chained-append` | opus, tests-first | **fable skeptic** — the chain-under-concurrency protocol AND a format sign-off: the on-disk convention is durable, so fable reviews genesis/`prev`/`gen` before anything writes real data |
| `mail-cursor` | **fable** (hard-reasoning: advance-on-inject is two guarantees at once; the TTL clock; re-anchor semantics) | independent skeptic pass |
| `mail-send-verb` | opus (one `Program.cs` mode case) | — |
| `mail-digest-handler` | **fable** (the semantic core: planner matrix + rendering + the cursor-advance seam) | independent skeptic pass |
| `mail-deliver-ledger-event` | opus (emitter on an existing seam) | — |
| `cursor-edge-adversarial-tests` | **fable** (the slice IS adversarial thinking — fixtures that genuinely exercise interleavings, not happy-path lookalikes) | skeptic on the tests themselves |
| `stop-reconcile-seam` | opus (one data edit + one adapter branch) | **fable skeptic** on the wire shape — a wrong Stop block shape ships silently — plus the live-deploy check |
| `first-members-dogfood` | opus (assembly on starter precedent; the stub-`claude` test + live dogfood are the check) | — |
| `swarm-profile-and-flow-doc` | opus (transcription; shipshape catches drift) | — |

**Phases** (tick in the roadmap as slices land):

1. **`mail-envelope-parser`** (medium; the `DispatchPolicy.TryParse`
   precedent transcribed onto the envelope field set — the malformed-vs-valid
   table is the only real thinking: `inReplyTo` present is NOT unknown-field,
   `body` opaque, `ts` unvalidated display) **+ `policy-content-hash`** (low,
   d12; zero deps — SHA-256 + byte size on the `policy.reload` emit
   (`DispatchPolicy.cs` stat-gate) and the API `PUT /policy` event; settle
   Absent/Malformed stamping and raw-vs-BOM-stripped bytes so the two emits
   agree). Two independent commits, one sitting.
2. **`mail-store-chained-append`** (high, verify) — flock-serialized chained
   append. Lands ALONE and verifies hard: genesis-line convention,
   `prev`-hash encoding, torn-final-line/truncation handling, crash between
   read-last-line and append across concurrent senders. ⚠ The on-disk format
   is durable — nothing may write real data to `~/.captainHook/mail/` before
   this phase's verify pass settles it.
3. **`mail-cursor`** (high, verify — advance-on-inject is simultaneously the
   at-most-once guarantee and the Stop-loop guard; too early loses mail, too
   late double-injects; `deliveries` is the sole TTL clock; `gen` survives
   rotation; deletion re-anchors) **+ `mail-send-verb`** (low; one
   `Program.cs` mode case — the cheap batch-along that gives the cursor tests
   a real end-to-end write path).
4. **`mail-digest-handler`** (high, verify) — the semantic core, a phase to
   itself: the pure planner (priority × event × HarnessSpec verbs →
   deliver|hold|degrade, downward only, no per-harness code paths),
   deterministic bounded rendering pinned by golden tests
   (priority-then-recency, hard cap, truncate-never-summarize), exec-wire
   registration, smoke-run through a real daemon.
5. **Hardening + surfaces, in order:** `mail-deliver-ledger-event` (low; emit
   only on non-noop, hash the bytes actually injected — foldable into the
   same session as the tests, whose observability hook it is) →
   `cursor-edge-adversarial-tests` (high, verify the tests themselves —
   duplicate delivery, rotation mid-read, oversized-envelope skip,
   chain-break classification; a fixture that passes while the urgent
   watermark leaks is the failure mode) → `stop-reconcile-seam` (medium,
   verify; the one-line `claude-code.json` data edit PLUS an event-appropriate
   adapter branch — `claude-hook-json` renders `Decide` as PreToolUse
   `permissionDecision`, Stop needs top-level `{"decision":"block"}`, and a
   wrong shape ships silently, so validate against a live deploy, not just
   the suite; the N3 termination pin) → `first-members-dogfood` (medium,
   **deliberately LAST**: no live payloads on the maintainer's real session
   until the exactly-once/watermark tests and the Stop-loop pin are green —
   the write-log observer + on-demand watcher as `examples/payloads/`
   starters, the reentrancy guard PROVEN via a stub-`claude` that fails on a
   missing guard (a new test pattern for the suite), field report in
   `doc/dogfood/`).
6. **`swarm-profile-and-flow-doc`** — the close: profile-as-policy prose,
   `doc/flow/mailbox-bus.md` (diagram + ground-truth incl. cursor edges and
   chain-verify semantics), this ADR's prospective table back-filled
   decision→code, roadmap box checked.

Standing risks the plan names: on-disk format lock-in (phase 2's ⚠); dogfood
strictly after hardening (a broken guard = runaway model calls on the
owner's bill); mail tests point at explicit temp dirs, never the live
`~/.captainHook/` tree; ship bar throughout — suite green twice, `/shipshape`
before commits, live installation touched only via `/deploy`.

### Addendum — decision 14, the observation surface *(2026-08-15)*

*Seven slices → five phases (one optional). Critical path
`mail-read-endpoint → mail-reducer → mail-canvas → mail-live-choreography →
mail-view-docs`; `mail-append-provenance-fields` is parallel to the endpoint.
Adversarial verify on exactly one slice — the reducer — because it is the one
place a plausible-but-wrong pass paints a false picture and no screenshot can
tell; everything else fails VISIBLY under `/ui-loop`. Same drivers as
ADR-0015's table: visible-vs-silent failure, judgment-heavy vs mechanical.
Model names are session aliases; effort is the session effort setting.*

| # | slice | model | effort | verify |
|---|---|---|---|---|
| 1 | `mail-read-endpoint` — `MailDto`s + `GET /api/v1/mail?since=` in `ApiHost`/`ApiReadModel`/`ApiSchema`, `api.gen.ts` regenerated; presence inference from cursor files ∪ recent dispatch sessions; `Pending` per cursor, never `Advance` | opus[1m] | medium | Route-table pin: no non-GET under `/mail`; the read model's constructor takes no append/advance handle (compile-time absence, asserted by a reflection test naming the forbidden symbols); a `since` round-trip test in the `ApiHostTests` family; store read against a torn tail returns the honest frontier. Fails loudly. |
| 2 | `mail-append-provenance-fields` — `from`/`kind`/`priority`/`topic`/`ttlDeliveries` on `mail.append`; NOT `body` | opus[1m] or sonnet | low | Golden trail test extended (both emitters render this event only from the engine, but the field set is schema — pin it); grep-pin that `body` never appears in a `mail.*` emit. |
| 3 | `mail-reducer` — pure TS `(state, line) → state` + snapshot seed; frontier/held/TTL/expired mirrored from `MailCursors.Pending`; presence decay; anomaly surfacing (deliver for an unknown cursor, advance past frontier, reanchor); `Gap`/`Reset` ⇒ resnapshot flag | **fable** | **high** | The one adversarial pass: golden sequences ported from `MailCursorTests`/`MailCursorEdgeTests` fixtures (same inputs ⇒ same pending set as C#), then an independent skeptic attacks reducer ⇄ `Pending` divergence, out-of-order lines, a deliver with no matching append (trail truncated), and the "no record ≠ delivered" rule. `node --test`. |
| 4 | `mail-canvas` — the SVG bus: ledger spine, role lanes, session cursors, envelope glyphs; semantic-zoom tiers; pointer pan / wheel zoom on a `viewBox` in the store slice; sidebar entry `Mail`; both themes | opus[1m] | **high** | `/ui-loop`: seeded preview daemon with a scripted swarm (two roles, two sessions, held + expired envelopes), snap all three zoom tiers × 2 themes and READ them; a `mail.spec` e2e (zoom in on a lane ⇒ envelope cards; the ledger stays legible at far zoom); axe/contrast pass on the glyph palette. Big surface, fails visibly. |
| 5 | `mail-live-choreography` — SSE subscription filtered to `mail.*`, one animation per event kind (drop-on-bus / cursor slide with seam tag / grey-out / reanchor jump), presence fade, resnapshot on `Gap`/`Reset`/reconnect | opus[1m] | medium | e2e through the preview daemon's fireHook: `mail send` then a digest at a seam ⇒ the envelope lights and the cursor passes it, asserted on DOM state not timing; `Gap` injected via the sse-backpressure test seam ⇒ resnapshot observed. Snap mid-animation frames read. |
| 6a | `mail-replay` part a — the delivery-record PRELOAD: the daemon folds `mail.deliver` out of its trail into the snapshot, so a pickup that predates the page reads ✓ instead of *before cursor · no record* **(landed 2026-08-17)** | opus[1m] | low | Placement is the reducer's one rule, shared with the live path; the fold is pinned against an engine-written line and against a payload-stderr forgery; e2e reloads onto a delivery nobody watched. |
| 6 | `mail-replay` *(optional)* — scrub bar: reducer fed from `/api/v1/events?Last-Event-ID=<older>` at variable rate; live resumes at the head | opus[1m] | low | Reducer determinism already pinned by 3; one e2e (scrub back ⇒ pending set matches the golden at that offset). Skippable if the live view alone satisfies the field report. |
| 7 | `mail-view-docs` — flow doc § *The observation surface* (diagram of the bus canvas over the mechanism, ground-truth rows), this ADR's Ground truth rows for d14, ADR-0015 d1 note honored, roadmap tick | opus[1m] | low | `/shipshape`: every symbol named exists; a field report entry from watching the maintainer's real session in `doc/dogfood/`. |

**As-built on 6a (2026-08-17):** the sketch above feeds the reducer from
`/api/v1/events?Last-Event-ID=<older>`, and that is **not available** — ADR-0009
d2 makes the resume id an opaque token a client echoes and never interprets, so
naming an id *older than* the snapshot's stamp is arithmetic the contract
forbids, and the only constant a client may legitimately spell is `"0"`, which
replays the entire trail as live (exactly what `mail-stream-alignment` exists to
stop). So the DAEMON folds: `GET /api/v1/mail` gains `deliveries` — the
`mail.deliver` lines as the trail stated them, columns verbatim and no ledger
offsets, since placing an envelope id is the reducer's arithmetic and a second
implementation of it in C# is N8 again — plus `deliveriesComplete`, the narrow
claim that the whole file was read and nothing trimmed. Pin iii is untouched:
delivered still comes from a ledger line and nowhere else. What changed is how
far back the picture can see one, and it now says how far that is.

**Phases:** (1) slices 1 + 2, one sitting, independent commits → (2) slice 3
alone, verify hard before any pixel is drawn — the picture inherits the
reducer's truth → (3) slice 4 → (4) slice 5 (+ 6 if wanted) → (5) slice 7.
Standing rules: nothing under `/api/v1/mail` writes, ever; the reducer is an
interpolator between authoritative snapshots (N8); mail tests point at
explicit temp dirs; ship bar unchanged.

## Ground truth *(back-filled decision→code 2026-08-15, all 11 slices landed)*

Mechanics live in **[doc/flow/mailbox-bus.md](../flow/mailbox-bus.md)**; this
table is the decision→code index. Where the as-built shape departs from the
sketch above, the departure is named in the decision's own annotation.

| decision | lives in |
|---|---|
| d2 — envelope + strict parser | `MailEnvelope` (`TryParse`/`TryParseLine`, `MailSender`, `MailKind`, `MailPriority`) in `dotnet/captainHook/Mail/MailEnvelope.cs`; `MailEnvelopeTests.cs` (26). `ts` is stamped by the verb, format-unvalidated by design |
| d3 — TTL as delivery opportunities | `MailCursors.Pending` — `deliveries − seenAt + 1 ≥ ttlDeliveries`; no wall clock appears anywhere, asserted on the bytes (`MailCursorTests.cs`) |
| d4 — the cursor | `MailCursor`/`MailHeld`/`MailCursors` in `Mail/MailCursor.cs`. **As-built departure**: a bare offset cannot express out-of-file-order delivery, so the cursor is a read FRONTIER plus a bounded `held` exception list, with `head` as the chain-native rotation check beside `gen` |
| d5 — planner, seam mapping, Stop seam | `MailDigest.Plan`/`VehicleFor` (pure; `MailSeam`, `MailVehicle`) in `dotnet/captainHook/Mail/MailDigest.cs`; seam class is REGISTRATION data (`--seam`), not an event name. Stop's `{"effects":["decide"]}` in `harnesses/claude-code.json` + `DecidesAtTopLevel`/`TopLevelDecision` in `Core/Harness.cs` — the conditional this decision left open FIRED (the nested shape parses as nothing at turn end) |
| d7 — CLI verbs | `Mode.MailSend` on the wire argv contract (`dotnet/captainHookWire/Cli.cs`, `mail <subverb>`), routed in `dotnet/captainHook/Program.cs` to `MailSend.Run` / `MailDigest.Run`; refused by the shim (aot-boundary rule 11) |
| d8 — profile/activation | no new surface, as designed: members in `~/.captainHook/handlers.json`, per-agent scoping in `dispatch.json` via handler×`project` rules (`Core/DispatchPolicy.cs`, pre-fan-out exclusion in `Dispatcher.DispatchAsync`). **As-built sharpening**: this is not merely how a swarm is *activated* — it is the only thing that gives two agents on one machine two ROLES, since `handlers.json` is global and `--role` is static |
| d10 — provenance + `mail.deliver` | `MailDigest.Render`/`ItemBlock` + golden tests; `MailDigest.LogDelivery` from the one advanced-the-cursor branch, after the answer is written. **As-built**: the sketch's nested `recipient: {role, session}` ships as a first-class `sessionId` column (nesting would hide mail delivery from every existing session filter), `role` in data |
| d11 — hash chain + flock append | `MailStore.Append`/`Read`/`VerifyChain`/`HeadHash`/`HashOf`; `Genesis` = 64 zeros, `prev` = SHA-256 of the previous line's bytes excluding its LF, `MaxLineBytes` 128KiB. **Settled in the format sign-off**: rotation starts a NEW chain (no cross-file `prev`); torn tails terminated, never repaired. `MailStoreTests.cs` (43), `MailCursorEdgeTests.cs` (15) |
| d12 — policy content hash | `PolicyContent.Of` stamped onto `policy.reload` and a new `policy.write` (`Core/DispatchPolicy.cs`, `Api/ApiPolicyWriter`) — over the LOADER's view, so an API write and its reload hash identically |
| d14 — reducer + N8's mitigation *(slice 3 of the addendum; the endpoint, canvas and choreography rows land with their slices)* | `web/src/mail.ts` (`seedMail`, `reduceMail`, `projectCursor`, `lineStatus`, `deliveriesFor`, `presenceTier`) — pure, seeded from `MailDto`, `(state, trailLine) → state`; the golden corpus `web/src/mail.golden.json` is GENERATED by `dotnet/captainHookTests/MailReducerGoldenTests.cs` from the real store/cursors/digest (before-snapshot, trail, after-snapshot per scenario; regenerate with `CAPTAINHOOK_SCHEMA_UPDATE=1`, on `ApiSchemaTests`' drift-detector precedent) and replayed by `web/src/mail.test.ts` — same inputs ⇒ same pending set as `MailCursors.Pending`, per cursor, per offset. Adversarial: `web/src/mail.skeptic.test.ts` |
| d13 — lifetimes + perms | store dir 0700 / lines 0600 / cursors 0600 (`MailStore`, `MailCursors`), role+session percent-encoded in cursor filenames; `gen` reserves rotation (N4 open). Retention prose in the flow doc; the cursor-deletion race is a STATED cost, not a defect to guard |
| first members | `examples/payloads/starter-mail-observer.sh` (write-only class), `starter-mail-watcher.sh` (on-demand LLM class, reentrancy guard proven by a stub `claude`); `MailDogfoodTests.cs` (5); field report `doc/dogfood/2026-08-14-first-bus-members.md` |
| mechanics | `doc/flow/mailbox-bus.md` |
