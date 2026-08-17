# ADR-0018 — Instance addressing: a mailbox per agent, not only per role, and the reaper that tends the dead ones

**Status:** Proposed *(2026-08-17; drafted from the maintainer's scratch note
of the same day, written while watching the Mail canvas show six cursors on
one `maintainer` lane — two of them live windows that both received
everything. Nothing here is implemented. Build order via `/adr-plan`, below.
The reaper's authority scoping (decision 6) is deliberately left open: decide
when the slice is reached.)*
**Date:** 2026-08-17
**Builds on:** [ADR-0016](0016-mailbox-bus.md) (decision 6 — *addresses are
stable roles* — extended, not reversed; the ledger, cursors, digest, canvas),
[ADR-0017](0017-watcher-nudge-and-ask.md) (the watcher, role kind, `MailNudge`,
ask/reply — this ADR precedes its phase 2 and simplifies its d8),
[ADR-0006](0006-dispatch-policy.md) (registration scoping by cwd — how two
windows already get two roles).
**Evidence:** the live `mail/` tree of 2026-08-16/17: `cursor.maintainer.*`
× 6, four of them sessions that will never return, each holding pending mail
forever; the exchange in which a reply meant for *one* window reached every
window wearing its role.

## Context

ADR-0016 d6 chose roles as addresses because sessions are ephemeral: a
mailbox keyed to a session id dies with the window and strands mail on every
restart. That is right, and it produced a bus where `to: maintainer` means
*every instance holding the role* — one cursor per (role, session), each
envelope delivered to each of them, each spending its own TTL. Correct for
"all maintainers, read this."

Two things happened on 2026-08-16 that the model cannot express:

- **"*You*, the instance I am talking to."** The reviewer's answer to the
  maintainer's request went to *every* maintainer session (two live windows
  and four dead cursors); the one that asked had no more claim on it than the
  others. ADR-0017 d8 works around this with a delivery *preference* (the
  asker's cursor gets it urgent-class, others ambient), because there is no
  unicast address to send it to. That is a workaround for a missing address
  kind, not a design.
- **Dead mailboxes.** Cursors are created on first contact and never removed.
  Four of the six maintainer cursors belong to closed sessions; each shows
  every later envelope as *pending* forever, the lane grows monotonically,
  and nothing — not `doctor`, not the watcher — has any standing to touch
  them. The maintainer's instinct on seeing this was not "delete them" but
  "**that's a job for an agent**": something should look at what a dead
  mailbox still holds and decide whether it should be passed on.

What already exists and must not be rebuilt:

- **Cursors are already keyed role × session** (`MailCursors.CursorPath(role,
  session)`, files `cursor.<role>.<session>.json`, names percent-encoded by
  `Enc` so any address character is safe on disk). "A mailbox per reader" is
  half-built; the missing half is the ability to *address* one.
- **`dispatch.json` already scopes which digest runs where** (project prefix,
  session id) — how two windows hold two roles today, and how a named
  instance's registration will be pinned to a place.
- **One ledger, one hash chain, one canvas.** "Two mailboxes" must not mean
  two files: an address is a *routing key over the one chain*, which is what
  keeps `VerifyChain`, the Mail view and the audit story whole.
- **`from` already names the sender** as `{agent, harness, session}`;
  recipients are the half that is not named.
- **`to` is a bare, unvalidated string on a hash-chained ledger.** Whatever
  syntax this ADR chooses is permanent the moment one envelope is written with
  it. That is why this is an ADR and not a slice.

## Decision

1. **Two address kinds, one namespace, one ledger.** `to` is either a
   **role** — `maintainer` — meaning every instance holding it (ADR-0016's
   behaviour, unchanged: **broadcast**), or a **role@instance** —
   `maintainer@laptop-a` — meaning exactly one mailbox (**unicast**). Both
   are routing keys over the single append-only chain; nothing about the store
   changes. Two agents holding one role are two mailboxes, addressable either
   way.

2. **Syntax: `role@instance`, refused rather than guessed.** `@` becomes a
   forbidden character in role names (validated at parse — today `to` accepts
   anything; this decision introduces the role grammar `[a-z0-9][a-z0-9-]*`
   for both halves). At most one `@`; both halves non-empty and role-valid;
   anything else is a parse error and the envelope is refused. A misrouted
   envelope is silent; a refused one is loud. Existing envelopes (no `@`) parse
   exactly as before.

3. **An instance's name is its registration, not its window.** `mail digest
   --role R --as <instance>` names the mailbox a registration reads.
   `dispatch.json` pins that registration to a place, as it already pins
   roles. **Cursor key = role × instance, where instance := `--as` if given,
   else the session id.** Consequences, all deliberate: an *unnamed* reader is
   exactly today's reader (ephemeral, one cursor per window, backward
   compatible); a *named* reader has a durable mailbox that outlives any
   window; two windows registered under the same name **share one cursor** —
   first pickup consumes, the other never sees it — which is the correct
   meaning of "one agent" as opposed to "one window". A named instance's first
   contact anchors like any first contact (`MailCursors` offset 0: it inherits
   the role's retained history — for a durable mailbox that is right, and for
   unicast mail it is moot since nothing unicast predates the name).

4. **Delivery: broadcast to a role, unicast to an instance, and a
   registration reads both.** A registration `--role R --as I` reads
   envelopes `to: R` (as every R-holder does) **and** envelopes `to: R@I`
   (as no one else does). `MailDigest.Plan` learns the second predicate;
   ranking is unchanged. Consequence for ADR-0017: an **answer is addressed
   `to: <asker's role@instance>`** — d8's delivery-preference hack disappears
   and `inReplyTo` becomes pure correlation; N5 of ADR-0016 (fan-out is wrong
   for ask/reply) is closed by an address, not a heuristic. A role-addressed
   answer remains legal (a request from an unnamed reader has only its
   session to be answered to; d3's fallback covers it).

5. **Unicast has no TTL.** With one addressee, *delivered* is a fact and
   *pending* is not a matter of opportunities. `ttlDeliveries` on a
   `role@instance` envelope is **refused by the parser** (not ignored — an
   accepted-and-ignored field is a lie in the ledger). Delivered once, done.
   Pending-forever-if-the-instance-never-returns is decision 6's problem, not
   TTL's.

6. **Dead mailboxes are a role's job — the reaper — never an automatic
   deletion.** Detection is deterministic and stays in the watcher (ADR-0017):
   an instance mailbox with pending mail, no live session, quiet past a rule
   ⇒ `MailNudge {role: reaper, reason: dead-mailbox, address, envelopeIds}`.
   **Disposition is judgment and belongs to a member** — the `reaper` role,
   robot-servable, ADR-0017's first runner: for each held envelope it may
   **forward** (append a *new* envelope to a live address carrying
   `forwardedFrom: {id, address}` — provenance, fresh TTL, the original stays
   on the chain), **drop** (leave it; reap the cursor), or **hold** (not dead
   yet). Then `captainHook mail reap <role@instance>` removes the cursor file
   and writes `mail.reap {address, pendingIds, by}` to the trail. The
   mailbox's *history* remains readable on the ledger; only its standing is
   gone; if the instance ever returns, its next pickup is a fresh first
   contact and the ledger shows why it sees duplicates. A deterministic
   payload (`forward-all-to-role` / `drop-all`) is the zero-token fallback;
   the LLM reaper is the feature. **The reaper reads bodies addressed to
   others** — it is the first cross-mailbox reader on the bus, and its
   authority (registration-scoped `mayReap: [roles]` vs. dispatch-policy
   rules) is **left open by this ADR**; every read it makes of a dead box is a
   trail line regardless.

7. **The canvas draws addresses, not files.** A lane per role, as now; named
   instances as durable sub-lanes with their name; unnamed sessions as today;
   dead (watcher-marked) mailboxes greyed with their pending count, never
   hidden; a forward as a link from the dead lane to the live one; a unicast
   envelope hung on its instance's sub-lane only. `mail status` counts per
   instance where the caller is named.

8. **Bounds and provenance, house style.** `forwardedFrom` and `inReplyTo`
   are the only two envelope-to-envelope references; both are validated
   presence-only (a reference to an unknown id parses — the ledger may have
   rotated). `mail.reap` and every reaper read are trail lines. Nothing here
   reads the wall clock for control flow; "dead" is presence + monotonic
   quiet, from ADR-0017's brain.

## Rejected alternatives

| alternative | disposition |
|---|---|
| **A mailbox file per address** | Rejected — breaks the one-chain audit story, the canvas, and `VerifyChain` for nothing: an address is a routing key over the ledger; cursors already give per-reader state. |
| **Instance = session id (make sessions addressable)** | Rejected (ADR-0016 d6 again) — dies with the window; four of six cursors on the live lane are exactly this failure. Session id remains only the *fallback* name for unnamed readers. |
| **Instance from an env var / cwd** | Rejected — env does not survive shim → daemon → payload cleanly, and cwd collides the moment two windows open one repo. The registration is the one place a name is already scoped to a place. |
| **`role/instance` or typed `agent:` / `role:` prefixes** | Rejected — `/` reads as a path; typed prefixes are heavier and would have to be retrofitted onto every existing `to`. `@` reads as an address and costs one forbidden character. |
| **Role as a work queue (any ONE holder claims)** | Out of scope — claim/lease semantics are a different system; the cursor model deliberately has none. Trigger to revisit: a role whose members must not duplicate work. |
| **TTL on unicast** | Rejected — one addressee makes "delivered" a fact; opportunities-based expiry answers a question unicast does not ask. Refused, not ignored. |
| **Automatic reaping (doctor / watcher deletes dead cursors)** | Rejected — a mailbox is somebody's; deleting standing silently is the one thing a ledger-first design must not do. Detection is automatic; disposition is a role's; reaping is a logged verb. |
| **Reaper as a deterministic rule only** | Not rejected — it is the zero-token fallback (d6). The LLM reaper is the point: "should this be passed on?" is judgment, and it is low-stakes judgment (nothing it does destroys information). |

## Consequences

### Positive

- Ask/reply gets its answer to the asker by *address*; ADR-0017 d8 loses a
  heuristic and gains a line.
- Backward compatible on the wire and on disk: envelopes without `@` and
  readers without `--as` behave exactly as before; the cursor file naming
  already encodes any character.
- The reaper is the ideal first runner for ADR-0017: inputs entirely on the
  ledger, outputs are envelopes and one logged verb, nothing it does is
  irreversible, and it is a role no human wants.
- Dead mailboxes stop being noise and become the canvas's honest picture of
  who left.

### Negative

- **N1 · The syntax is forever.** `@` in `to` is a wire fact from the first
  envelope; the grammar in d2 must be right on day one, and the parser test is
  the pin.
- **N2 · Shared-name windows lose fan-out on purpose.** Two windows under one
  `--as` see each envelope once *between them*. That is the intended meaning
  of "one agent"; it will surprise someone who names two humans the same.
- **N3 · The reaper reads other people's mail.** Bounded only by whatever d6's
  open authority decision becomes; until then, the deterministic fallback is
  the only reaper that should run outside a dogfood session.
- **N4 · A named instance's first contact inherits history.** Correct for a
  durable mailbox, but a newly named long-lived role sees everything retained
  for the role at once — the digest's cap and the watcher's budgets are the
  only bounds.
- **N5 · One more predicate in `Plan`, one more sub-lane in the canvas.** Both
  small; both places where a wrong pass paints a false picture (the reducer's
  golden corpus must grow instance cases).

## Implementation plan

*Decomposed via `/adr-plan` 2026-08-17: **13 slices → 6 phases.** Critical
path `address-grammar → instance-registration → plan-unicast →
reaper-payloads → e2e-named-and-dead-instance → docs`. Adversarial verify on
three slices — the ones whose failure is on the append-only chain or silent
misrouting; no ultracode (every slice is one file cluster). Model names are
session aliases; effort is the session setting.*

**Two wire decisions and one authority decision are cheap to make and
expensive to revisit — write the ADR text first, code second:** the address
grammar (d2), *where the asker's address rides on a request* (grow `from`
with the instance, or a `replyTo` field — `answer-by-address` decides;
permanent on the ledger), and the reaper's authority (d6, left open;
`reaper-payloads` decides and records it here).

| # | slice | model | effort | verify |
|---|---|---|---|---|
| 1 | `address-grammar` — the forever pin: `to` parses as `role` or `role@instance`, grammar `[a-z0-9][a-z0-9-]*` on both halves, one `@`, non-empty, `to` only; refused not guessed; legacy envelopes byte-identical | opus | medium | Theory corpus in `MailEnvelopeTests` on the existing rejection pattern; the live ledger + test corpus checked lowercase-legal (they are). Land ALONE before anything fans out. Risk is permanence, not slip-through — no adversarial pass. |
| 2 | `unicast-refuses-ttl` — `ttlDeliveries` refused on `role@instance`; the record's TTL becomes nullable; serializer, `mail.append` trail data, expiry arithmetic (`MailCursor` `deliveries − seenAt + 1 ≥ ttl`), DTO/read model/canvas countdown all learn "none" | opus | medium | **verify:** the failure is on the chain — if `MailStore.Serialize` still writes `ttlDeliveries` for a unicast `to` while the parser refuses it, every future read sees a malformed line. Round-trip on the golden ledger. |
| 2 | `instance-registration` — `mail digest --as <instance>`; cursor key role × instance (`instance := --as ?? sessionId`) via the existing `CursorPath`; the trail keeps the REAL hook session while the cursor keys on the instance | opus | medium | Byte-identical unnamed path; two session ids under one `--as` share one cursor (Advance's per-cursor flock already gives first-pickup-consumes — no new concurrency). The one sharp edge is the cursor-key vs trail-session split; pin it, no adversarial pass. |
| 2 | `forwarded-from-provenance` — `forwardedFrom {id, address}` on the envelope, a clone of the `inReplyTo` path across parser / store / DTO / `api.gen.ts` | opus or sonnet | low–medium | Mechanical; `ApiSchemaTests` drift check; regen `api.gen.ts`. |
| 3 | `plan-unicast` — the recipient predicate becomes "`To == R`, or `To == R@I` when the reader is NAMED" — in BOTH copies (`MailCursors.Pending` and `LoadOrAnchor`'s held-entry check); address on `mail.deliver`; a held unicast never expires | opus | medium | **verify:** misrouting is silent, and the second predicate copy (held-entry → Reanchor) is the one a happy-path pass misses — untouched, every digest holding an undelivered `R@I` re-anchors and drops held state. An unnamed reader must NOT match `R@<sessionId>`. Gates all of phase 4 — do first in the batch. |
| 3 | `reap-verb` — `captainHook mail reap <role@instance>`: cursor removal under the SAME per-cursor flock `Advance` uses; `mail.reap {address, pendingIds, by}` trail row in both emitters | opus | medium | **verify:** the delete must hold `path+'.lock'` via `MailStore.TryLock` and must NOT unlink the lock file (flock-unlink race); `List` already filters `.lock`. |
| 3 | `watcher-dead-mailbox-rule` — the ADR-0017 brain grows `dead-mailbox`: instance cursor + pending > 0 + no live session + quiet past rule ⇒ `MailNudge {role: reaper, reason, address, envelopeIds}` | opus | medium | Golden rows beside the brain's; **depends on ADR-0017 `watcher-brain` + `mail-nudge-event`, which are not in this roster** — see risks. |
| 4 | `answer-by-address` — a request carries the asker's address; an answer goes `to: <asker's role@instance>`; ADR-0017 d8 text simplified to correlation-only | opus | medium | Unit test catches misaddressing. **Must land before ADR-0017 phase 2** or the preference hack gets built and torn out. |
| 4 | `mail-status-per-instance` — the count is per instance when the caller is named (reuse `RoleOf`/`TryParseArgs` to learn `--as`; no second flag) | sonnet | low | Mechanical; fold into the `instance-registration` / `plan-unicast` commit series. |
| 4 | `reaper-payloads` — the deterministic fallback (`forward-all-to-role` / `drop-all`) and the LLM reaper (`claude -p` over the dead box's pending, forward/drop/hold, then `mail reap`); **the authority decision is made and recorded HERE** | opus | medium | Stub-payload tests pin the verbs it runs; the LLM reaper is dogfood-only until authority is decided (N3). |
| 4 | `canvas-instances` — sub-lanes for named instances, dead lanes greyed with pending count, unicast hung on its sub-lane only, forward links dead→live; reducer goldens grow instance cases; `web/src/mail.ts` mirrors `plan-unicast`'s predicate at its FOUR sites | opus | medium | `/ui-loop` both themes; goldens (N5): a wrong mirror paints unicast as pending on sibling tracks. |
| 5 | `e2e-named-and-dead-instance` — extends ADR-0017's stub-harness e2e: a named instance's unicast pickup; a dead one nudged, deterministically forwarded with `forwardedFrom`, reaped with the trail line; canvas shows the link and the greyed lane, both engines | opus | medium | Sandbox mail dir only; injectable low quiet-threshold; `PollUntilAsync`, no sleeps; the flaky-guard exposure point — green twice. |
| 6 | `docs-instance-addressing` — flow doc § the bus grows addresses; this ADR's Ground truth + Status flip; ADR-0017 d8 marked simplified; roadmap 23 tick | opus | low | `/shipshape`; each landing commit ticks and adds rows — this is the sweep. |

**Phases:** (1) `address-grammar` alone → (2) `unicast-refuses-ttl` ‖
`instance-registration` ‖ `forwarded-from-provenance` (disjoint files; the one
shared decision — nullable TTL on the record — fixed before splitting) → (3)
`plan-unicast` first, then `reap-verb` ‖ `watcher-dead-mailbox-rule` → (4)
`answer-by-address` ‖ `mail-status-per-instance` ‖ `reaper-payloads` ‖
`canvas-instances` (the two decisions above written into this ADR before
coding) → (5) the e2e pin → (6) the docs sweep.

**Sequencing risks named by the plan:** (a) `watcher-dead-mailbox-rule` and
the e2e depend on ADR-0017's `watcher-brain` / `mail-nudge-event` /
`e2e-stub-runner-loop`, none landed — the external dependency, not the chain
above, may be the real critical path; phases 1–2 and `answer-by-address` /
`mail-status-per-instance` / `canvas-instances` (minus dead-greying) proceed
regardless. (b) `answer-by-address` before ADR-0017 phase 2. (c) two permanent
wire decisions + one authority decision: ADR text first. (d) `canvas-instances`
must mirror the predicate exactly and grow `mail.golden.json`; regen
`api.gen.ts` after `forwardedFrom`. (e) every slice: sandbox mail dir, swapped
`Log` sink, explicit harness dir — never `~/.captainHook`; green twice before
each commit.

**Interleave with the other two ADRs (the sequence as of 2026-08-17):**
item 21 close (0016) → 0017 phase 1 leaves (`mail-status` ✓, `thread-fields`,
`watch-rules`, `mail-nudge-event`) → **0018 phases 1–2 + `answer-by-address`**
→ 0017 phase 2 onward (thread lane now unicasts) → 0018 phases 3–4 as the
watcher lands → the two e2e pins → docs.

## Ground truth

*(rows accrue in each landing commit; `docs-instance-addressing` is the sweep,
not the first entry.)*

| decision | as built |
|---|---|
| d1/d2 — two address kinds, one namespace; `role` or `role@instance`, refused not guessed | `MailAddress` (`TryParse`, `IsRole`, `Role`, `Instance`, `IsUnicast`, `GrammarHelp`) in `dotnet/captainHook/Mail/MailAddress.cs`, called from `MailEnvelope.TryParse` on `to` — the single choke point every write path crosses (`mail send` parses before it appends; the store serializes a parsed record), so an ungrammatical address cannot reach the chain. **As-built**: the check is scoped to `to` alone — `from.agent` is a free-form provenance label nobody routes on, and constraining it would refuse envelopes already on the ledger for a property nothing reads. Lowercase is PINNED, not folded (unlike `kind`/`priority`, which are closed sets a parser can correct a casing slip against): folding here while `MailCursors.CursorPath`'s percent-encoder keeps `Ops` and `ops` as two cursor files is N8 wearing an address for a hat. The alphanumeric test is ASCII spelled out by hand rather than `char.IsLetterOrDigit`, which is Unicode-aware and would admit `mаintainer` with a Cyrillic а — a mailbox rendering identically to a real one and receiving none of its mail, which is exactly the silent misrouting d2 exists to refuse. A second `@` is refused rather than split-on-first or split-on-last, because `a@b@c` has two plausible readings and picking either is guessing. A trailing `-` is legal: the grammar is the ADR's verbatim and was not "improved" in passing, this being the half of the decision that is permanent. Tests: `MailAddressTests` (17) + the address block in `MailEnvelopeTests` (30), including the named legacy corpus (every role on the maintainer's live ledger and in the fixtures) — the one way this slice could have silently orphaned mail already on the chain. Flow doc: [doc/flow/mailbox-bus.md](../flow/mailbox-bus.md) § The address grammar |

## Revisit triggers

- A role whose members must not duplicate work ⇒ the work-queue kind
  (claim/lease), its own ADR.
- The reaper's authority: registration-scoped vs. policy-scoped — decided at
  `reaper-role`, recorded here.
- A second cross-mailbox reader appears ⇒ generalise the reaper's read
  authority into a capability rather than a special case.
