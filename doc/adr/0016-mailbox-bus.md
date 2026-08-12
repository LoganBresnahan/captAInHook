# ADR-0016 — The mailbox bus: cross-harness agent communication over lifecycle hooks

**Status:** Accepted *(2026-08-12; owner accept, same day as drafting from the
owner's design sessions of 2026-08-11/12. Nothing here is implemented yet —
build order below, decomposed via `/adr-plan`.)*
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
    default. All three are `0600` like `api.json`. And a boundary stated
    plainly instead of engineered around: **the bus is a recorded medium by
    design** — there is no "never-record" envelope flag (rejected below), so
    the rule for members is *don't put secrets in mail*; secret-scrubbing
    belongs to gate members (the key-redactor) at the tool seam, not to holes
    in the ledger.

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
  future ask/reply — recorded so decision 9's design remembers it.
- **N6 · Watcher token cost.** Every LLM watcher is a model call on somebody's
  bill; on-demand default + policy scoping are the throttles, and each watcher
  carries ADR-0010 N7's reentrancy guard (`--setting-sources ""` pattern) so a
  watcher's own model call never re-enters the bus.

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

## Ground truth *(prospective — to be back-filled decision→code as slices land)*

| decision | will live in |
|---|---|
| d2/d3/d4 — envelope, TTL, store, cursor | `dotnet/captainHook/Mail/` (model + strict parser + store + cursor); tests beside the `DispatchPolicy` parse-table precedent |
| d5 — planner + seam mapping | the `mail digest` command's planner; delivery capability = `handlers.json` registration × `HarnessSpec.events` verbs |
| d5 — Stop seam data | `dotnet/captainHook/harnesses/claude-code.json` (`Stop.effects`), adapter set per ADR-0003 if needed |
| d7 — CLI verbs | `Program.cs` verb routing → `Mail/`; exec-wire answers per ADR-0010's closed grammar |
| d8 — profile/activation | `~/.captainHook/handlers.json` + `dispatch.json` (no new surface) |
| d10 — provenance rendering + `mail.deliver` | the digest renderer + its golden tests; the deliver event beside the trail's one schema (both-emitter rules do not apply — only the engine's digest path emits it) |
| d11 — hash chain + flock append | `Mail/` store appender + a chain-verify helper; tamper/truncation tests |
| d12 — policy content hash | the shared policy gate's `policy.reload` emit + `ApiPolicyWriter` |
| d13 — lifetimes + perms | `Mail/` store creation (0600, `gen` rotation); retention prose in the flow doc |
| mechanics | `doc/flow/mailbox-bus.md` |
