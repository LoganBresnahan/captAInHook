# ADR-0018 — Instance addressing: a mailbox per agent, not only per role, and the reaper that tends the dead ones

**Status:** Proposed *(2026-08-17; drafted from the maintainer's scratch note
of the same day, written while watching the Mail canvas show six cursors on
one `maintainer` lane — two of them live windows that both received
everything. Slices 1–4 of 13 have since landed — the address grammar (d2), the
unicast TTL refusal (d5), instance registration (d3), `forwardedFrom`
provenance (d8), slice 5 the routing itself (d4), slice 6
`answer-by-address` — which also carried the evening's one AMENDMENT (d3, below):
every mailbox is addressable, a window's at `role@<session id>`, so the cursor
key IS the address — and slice 7 `reap-verb` (d6's mechanism, not its judgement).
A `role@instance` envelope reaches its one mailbox; a request can carry `replyTo`
and an answer goes there; a dead mailbox's standing can be removed, by hand and
on the record. Nothing yet WRITES a forward, nothing DECIDES a reap, and the
canvas does not draw instances. Status flips at
`docs-instance-addressing`. Build order via
`/adr-plan`, below.
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
   *Amended 2026-08-17 (`answer-by-address`): one length bound —* an address is
   at most `MailAddress.MaxChars` = `MailEnvelope.HeadFieldChars` (120)
   characters, `@` included, refused past it. The grammar as first landed
   bounded the alphabet and not the length; a `replyTo` rendered unclamped in
   the digest head (a clamped return address is worse than none) needed the
   head to stay bounded some other way, and the bound also sits well under the
   platform fact that would otherwise bite first — a mailbox's cursor is a file
   named `cursor.<role>.<instance>.json` under NAME_MAX 255 (doc/platform.md),
   so an address past ~242 characters was already a mailbox whose cursor could
   never be written. Nothing on any real ledger came within a factor of two.

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
   ***Amended 2026-08-17 (`answer-by-address`): the cursor key IS the
   address.*** As first written and first built, an unnamed reader was keyed
   `role × session` but NOT reachable at `role@<session>` — the session id was
   a key's fallback and never a name a sender could spell, on d6-of-ADR-0016's
   ground that a mailbox addressed by session strands mail. That asymmetry
   turned out to cost more than it saved: a cursor file cannot say whether its
   key is a name or a session, so every surface that had to decide what a
   mailbox was entitled to (the read model, `mail status`, the canvas to come)
   was guessing or under-claiming; `answer-by-address` had no way to reach an
   unnamed asker except by role, which is the fan-out this ADR exists to
   remove; and the reaper (d6) is precisely the mechanism for stranded mail, so
   the premise of the refusal was already answered by a later decision. **Now:
   the mailbox a registration reads is `role@(--as ?? session id)`, and that one
   string is both where the cursor lives and what the mailbox answers to.** A
   `--as` mailbox is DURABLE and a window's is EPHEMERAL; the difference is
   lifetime, not reachability. A sessionless reader has no instance and reads
   its role's broadcast alone. Trail lines are unchanged: the `instance` column
   still appears only when mailbox and window differ. ADR-0016 d6 carries the
   matching note.

4. **Delivery: broadcast to a role, unicast to an instance, and a
   registration reads both.** A registration `--role R --as I` reads
   envelopes `to: R` (as every R-holder does) **and** envelopes `to: R@I`
   (as no one else does). `MailDigest.Plan` learns the second predicate;
   ranking is unchanged. Consequence for ADR-0017: an **answer is addressed
   `to: <asker's role@instance>`** — d8's delivery-preference hack disappears
   and `inReplyTo` becomes pure correlation; N5 of ADR-0016 (fan-out is wrong
   for ask/reply) is closed by an address, not a heuristic. A role-addressed
   answer remains legal (a request whose sender named no return address is
   answered to the sender's role, as today).
   ***As built (`answer-by-address`, 2026-08-17): the asker's address rides on
   the request as a top-level `replyTo`*** — an address in `to`'s grammar,
   optional, no default — and NOT as a member of `from`. It is a property of
   the REQUEST rather than of the sender: when the reaper forwards a stranded
   request, the new envelope's sender is the reaper and its reply address is
   the original asker's, two facts one `from` cannot carry (`forwardedFrom`
   exists for the same reason). The digest renders it in the item HEAD, verbatim
   and unclamped, because the reader that has to answer is very often a model
   and the return address it should copy into `to` has to be where its eye
   lands. Unclamped is only safe because the address grammar gained its one
   length bound the same day (d2, below). Since the d3 amendment an unnamed
   asker HAS an address (`role@<its session>`), so `replyTo` is not the only
   way to reach one — it is the way a sender says *"answer this box, not my
   role"*, whichever kind of box it is.

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
| **Instance = session id (make sessions addressable)** | Rejected as first written (ADR-0016 d6 again — dies with the window; four of six cursors on the live lane are exactly this failure), then **half-reversed 2026-08-17** by the d3 amendment: a session id IS a window's default instance and its mailbox IS addressable at `role@<session>`. What stays rejected is *only* session ids — no way to name a durable mailbox — which is what `--as` is for. The four dead cursors are the reaper's to tend either way; being addressable made them honest, not more numerous. |
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
| 1 ✅ | `address-grammar` **(landed 2026-08-17)** — the forever pin: `to` parses as `role` or `role@instance`, grammar `[a-z0-9][a-z0-9-]*` on both halves, one `@`, non-empty, `to` only; refused not guessed; legacy envelopes byte-identical | opus | medium | Theory corpus in `MailEnvelopeTests` on the existing rejection pattern; the live ledger + test corpus checked lowercase-legal (they are). Land ALONE before anything fans out. Risk is permanence, not slip-through — no adversarial pass. |
| 2 ✅ | `unicast-refuses-ttl` **(landed 2026-08-17)** — `ttlDeliveries` refused on `role@instance`; the record's TTL becomes nullable; serializer, `mail.append` trail data, expiry arithmetic (`MailCursor` `deliveries − seenAt + 1 ≥ ttl`), DTO/read model/canvas countdown all learn "none" | opus | medium | **verify:** the failure is on the chain — if `MailStore.Serialize` still writes `ttlDeliveries` for a unicast `to` while the parser refuses it, every future read sees a malformed line. Round-trip on the golden ledger. |
| 2 ✅ | `instance-registration` **(landed 2026-08-17)** — `mail digest --as <instance>`; cursor key role × instance (`instance := --as ?? sessionId`) via the existing `CursorPath`; the trail keeps the REAL hook session while the cursor keys on the instance | opus | medium | Byte-identical unnamed path; two session ids under one `--as` share one cursor (Advance's per-cursor flock already gives first-pickup-consumes — no new concurrency). The one sharp edge is the cursor-key vs trail-session split; pin it, no adversarial pass. |
| 2 ✅ | `forwarded-from-provenance` **(landed 2026-08-17)** — `forwardedFrom {id, address}` on the envelope, a clone of the `inReplyTo` path across parser / store / DTO / `api.gen.ts` | opus or sonnet | low–medium | Mechanical; `ApiSchemaTests` drift check; regen `api.gen.ts`. |
| 3 ✅ | `plan-unicast` **(landed 2026-08-17)** — the recipient predicate becomes "`To == R`, or `To == R@I` when the reader is NAMED" — in BOTH copies (`MailCursors.Pending` and `LoadOrAnchor`'s held-entry check); address on `mail.deliver`; a held unicast never expires | opus | medium | **verify:** misrouting is silent, and the second predicate copy (held-entry → Reanchor) is the one a happy-path pass misses — untouched, every digest holding an undelivered `R@I` re-anchors and drops held state. An unnamed reader must NOT match `R@<sessionId>`. Gates all of phase 4 — do first in the batch. |
| 3 ✅ | `reap-verb` **(landed 2026-08-18)** — `captainHook mail reap <role@instance>`: cursor removal under the SAME per-cursor flock `Advance` uses; `mail.reap {address, pendingIds, by}` trail row in both emitters | opus | medium | **verify:** the delete must hold `path+'.lock'` via `MailStore.TryLock` and must NOT unlink the lock file (flock-unlink race); `List` already filters `.lock`. |
| 3 ✅ | `watcher-dead-mailbox-rule` **(landed 2026-08-18)** — the ADR-0017 brain grows `dead-mailbox`: instance cursor + pending > 0 + no live session + quiet past rule ⇒ `MailNudge {role: reaper, reason, address, envelopeIds}` | opus | medium | Golden rows beside the brain's; **depends on ADR-0017 `watcher-brain` + `mail-nudge-event`** — both landed 2026-08-18, so the external dependency the risks named resolved before this ran. **As built:** consent is the REAPER's rule (its tokens), never the dead role's; a registered `--as` box is standing and never a corpse (without which every durable mailbox would be one, since a named cursor cannot look live); the mailbox's own silence is checked unconditionally rather than through `noLiveSession`; envelopes tracked under the ADDRESS so two dead boxes are two corpses, while `perRoleHour` stays one window on the reaper and counts same-pass nudges; `ReaperHow` spells forward/drop/hold rather than "answer on the bus". Ground truth row below. |
| 4 ✅ | `answer-by-address` **(landed 2026-08-17)** — a request carries the asker's address; an answer goes `to: <asker's role@instance>`; ADR-0017 d8 text simplified to correlation-only | opus | medium | Unit test catches misaddressing. **Must land before ADR-0017 phase 2** or the preference hack gets built and torn out. |
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
`e2e-stub-runner-loop` — the external dependency, not the chain above, may be
the real critical path; phases 1–2 and `answer-by-address` /
`mail-status-per-instance` / `canvas-instances` (minus dead-greying) proceed
regardless. *(As of 2026-08-18: `watcher-brain` and `mail-nudge-event` have
landed and `watcher-dead-mailbox-rule` with them; only the e2e still waits on
0017's `e2e-stub-runner-loop`.)* (a′) **The reaper and the robot's own cursors**
(brain review, 2026-08-18): a turn payload's fresh-session-per-turn leaves an
ephemeral cursor per turn that d6's rule cannot distinguish from a dead human
window — resolved on ADR-0017's `turn-claude-payload` row (read `--as` a
registered durable mailbox); `reaper-payloads` and the e2e must assume that
resolution, and the e2e should assert a driven turn's cursor is never a
`WatchVerdict.Dead` candidate. (b) `answer-by-address` before ADR-0017 phase 2. (c) two permanent
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
| d6 (the DETECTION half; disposition and authority still open) — a dead mailbox is found deterministically and reported, never reaped | `WatcherBrain.DeadMailboxes` (+ `WatchDeadMailbox`, `WatchVerdict.Dead`, `ReaperRole`, `ReaperHow`) in `dotnet/captainHook/Core/WatcherBrain.cs` — a SECOND pass beside the role rule, because the two have different units and different recipients: the role rule asks whether a role is falling behind and deliberately does not count a dead cursor's held-forever mail (its strict "unread" reading), which is exactly the case this finds. Unit = one MAILBOX; recipient = the `reaper`; output = one `MailNudge` whose `Address` names the box (a reaper handed only "reaper" would not know which corpse to tend) and whose `ReplyHow` is `ReaperHow` — forward / drop / hold, then `mail reap` — rather than the bus's answer instruction. **The four conditions, each as-built.** (i) An INSTANCE mailbox with a cursor FILE (`WatchedMailbox.HasCursor`, set false by `MailWatch.ReadMailboxes` for its two stand-ins): `mail reap` removes a cursor, so a box with none has no standing to remove and a unicast addressed to a mailbox that never existed is a different shape, not a corpse. (ii) NOT registered — `RoleKinds.RegisteredMailboxes`/`IsRegisteredMailbox`, collected by `RoleKinds.From` through `MailDigest.MailboxOf` (the real parser, never a lookalike). This is load-bearing rather than a nicety: since d3 a named cursor's key is a NAME, so it can never look live, and without this rule every `--as` mailbox with pending mail would become a corpse the moment its window shut for the night. (iii) Pending mail. (iv) No live session for ITS OWN address, through `RolePresence.AnyLiveSession` — checked UNCONDITIONALLY rather than under the rule's `noLiveSession`, because the mailbox's silence is what *dead* means; a live sibling window of the same role says nothing about it. **Three decisions the slice had to make.** Consent is the REAPER's `watch.json` rule, not the dead role's: the nudge spends the reaper's tokens, and the dead box's role is typically human-held with no rule at all, so a rule there would be consent from the wrong party; no `reaper` rule ⇒ the pass does not exist (d7's direction), and the reaper must also be robot-servable or the box is reported and never nudged (a dead-mailbox nudge puts no mail in the reaper's own mailbox for a window to find). Envelopes are tracked under the ADDRESS (`WatchedEnvelope.Subject` = `MailNudge.Subject` — the address when there is one, else the role, so `NudgeState.Record` follows the same key; the field was renamed from `Role` in the 2026-08-18 review pass, before phase 4 of 0017 persists it, and the dead pass now tracks stranded envelopes BEFORE asking whether a reaper can be woken, matching the role rule, so installing the reaper's turn payload later does not restart every dead box's quiet clock): two dead boxes holding one broadcast are two corpses with their own quiet clocks and `perEnvelope` budgets, where a role key would have merged them; `perRoleHour` stays ONE window on the reaper, and it counts nudges decided in the SAME evaluation (they are not in the state yet — `Record` is the caller's), or one pass could hand the reaper every dead box at once whatever the budget said. A live reaper window does NOT hold a nudge, since nothing is in the reaper's own box to see and holding would mean nobody ever tends the corpse. **The CLI half:** `MailWatch.RolesToWatch` — a `reaper` rule widens the sweep from the rules' roles to every role with a cursor file (ordinal, so two runs agree), because the box the reaper cares about belongs to somebody else; `mail watch --once` prints a `dead-mailbox candidate` line per box and `WOULD NUDGE reaper about <address>`, and `--as-if-quiet` writes its pretence under both keys. Everything else is the ordinary machinery on purpose — the same triage (lifted to `TriageEntries` so the two rules cannot disagree about "due"), the same digest renderer over the box's own view, the same budgets, the same ONE `NextCheckMs`. Tests: `WatcherDeadMailboxTests.cs` (19), two `WatcherBrainGoldenTests` scenarios, four `MailWatchTests` cases, two `RoleKindsTests` cases, two `MailNudgeEventTests` cases (`address` present and absent, payload and trail). **A consequence the review pass named (2026-08-18), not a bug here:** the rule cannot tell a robot turn's own ephemeral cursor from a dead human window — the turn payload's fresh session per turn manufactures one candidate per turn once the next broadcast lands; the exemption is condition (ii), so the payload must read `--as` a registered mailbox (pinned on ADR-0017's `turn-claude-payload` row and risk (a′) above). NOT here: what the reaper DOES (`reaper-payloads`, which also decides its authority) and the canvas's dead lanes (`canvas-instances`) |
| d6 (the MECHANISM half; judgement and authority still open) — `mail reap` removes standing, and says so | `MailReap` (`Run`, `TryParseArgs`, `LogReap`) in `dotnet/captainHook/Mail/MailReap.cs`, routed from `Program.cs`'s `mail` switch: `captainHook mail reap <role@instance|role> [--by <address>]`. **The verb does not judge.** It never asks whether the mailbox is dead — detection is the watcher's (`watcher-dead-mailbox-rule`) and disposition the reaper's (forward / drop / hold, THEN reap), and a deadness test here would be a second one to disagree with the watcher's (N8). **Standing only:** the store is read and never written, so every envelope the mailbox read and every one it did not stays on the chain; a returning instance is a fresh first contact, and because that re-anchor is at `deliveries: 0` it is indistinguishable from real first contact (ADR-0016 d13's already-published cost), which is precisely why the `mail.reap` line is the only thing that can explain the repeat. **The lock:** the delete holds `cursor.<...>.json.lock` — the same per-cursor flock `Advance` takes, so a reap cannot interleave with an advance that would write the cursor back a moment later — and does NOT unlink it, since `flock` lives on the inode and unlinking under it hands the next caller a lock that excludes nobody. The stray lock file is invisible (`MailCursors.List` matches `cursor.*.json`). Existence is checked BEFORE the lock too, so reaping a mailbox that never existed leaves no lock file behind. **The row:** `mail.reap {role, instance?, pendingIds, by?}` — the mailbox spelled in the SAME two columns `mail.deliver` and `mail.cursorAdvance` use with the same write-when-named rule, NOT the joined `address` this ADR's prose named, on `plan-unicast`'s reasoning that two spellings of "which mailbox" on one trail is N8 in a new hat; no `sessionId`, because a reap has no window; `by` is the reaper's address, grammar-checked, and absent when a human ran the verb by hand. Written AFTER the delete: a crash in that window loses the record of a real reap, which reads like the bare deletion d13 already tolerates, where the other order would put a reap that never happened on an append-only chain. **Idempotent:** an already-reaped mailbox is exit 0 and logs nothing; the only exit 1s are a bad argument and a busy lock. A bare role reaps the sessionless reader's own mailbox (d3: the key IS the address). Tests: `MailReapTests` (19) + the `mail.reap` golden in `WireJsonlTests`. The delete is the COMMIT POINT: reporting happens after it, outside the guarded block, so a closed stdout cannot report "cannot reap" about a reap that happened. NOT here: the reducer does not fold `mail.reap` (it lands as the forward-compat `unknown-event` note — the picture cannot place a reap before it models instances at all, `canvas-instances`), and the reaper's payloads and authority are `reaper-payloads`' |
| d4 (as built) — `replyTo`, and the address length bound | `MailEnvelope.ReplyTo` (`string?`), parsed in `TryParse` beside `inReplyTo` through `MailAddress.TryParse` — `to`'s grammar, `to`'s refusal, NOT resolved against anything (a mailbox that does not exist yet is a legitimate return address; d3 anchors it on first contact); written by `MailStore.Render` beside `inReplyTo`, omitted when absent; `MailEnvelopeDto.ReplyTo` → `api.schema.json` → `api.gen.ts` → `MailEnvelopeView.replyTo` (`web/src/mail.ts`, snapshot-only like `inReplyTo`) → the detail card's `reply to` row (`web/src/MailPanel.tsx`, `data-detail-reply-to`). Rendered in the digest item head as `· reply to <address>` by `MailDigest.ItemBlock`, verbatim and unclamped. `MailAddress.MaxChars = MailEnvelope.HeadFieldChars` bounds every address at parse (`TryParse`) and at registration (`MailDigest.TryParseArgs` checks the composed `--role@--as`). Tests: `MailEnvelopeTests` replyTo block (4 methods, 9 cases), `MailAddressTests` two length theories (4 cases), `MailDigestTests.AnAddressPastTheLengthBound_IsRefusedAtRegistration`, `MailStoreFormatTests.Render_WritesReplyTo_BesideInReplyTo_AndOmitsItWhenAbsent`, `MailUnicastRoutingTests.Render_ShowsTheReturnAddress_InTheHead` + `AnAnswerAddressedToTheRequestsReplyTo_ReachesTheAskerAlone` (the plan's misaddressing pin: request carries the asker's address, answer goes there, ONE mailbox has it and three siblings do not). Reducer golden regenerated to 62 pure `"replyTo": null` insertions and nothing else. Nothing FILLS `replyTo` yet — `mail ask` (ADR-0017) is what will, from the caller's own registration the way `mail status` learns it, so a sender never spells its own address by hand |
| d4 — broadcast to a role, unicast to an instance, and a registration reads both | `MailAddress.Accepts(to)` in `dotnet/captainHook/Mail/MailAddress.cs` — the recipient predicate, and the ONE spelling of it. A registration reads `to == Role` always and `to == Role@Instance` when it is named; naming a mailbox ADDS an address rather than replacing one. `MailCursors.Pending` takes the address (`Pending(MailAddress, hookSession)`, cursor key `Instance ?? hookSession`), and the unnamed 2-arg overload stays the safe short call. **The predicate has two call sites and only one is on the happy path** — the pending scan decides what may be delivered, `LoadOrAnchor`'s held-entry check decides what a cursor may still hold, and the second is reached only when a NAMED reader HOLDS a unicast. With the sites disagreeing this is not a half-built feature: the scan accepts, the digest holds, and the next read re-anchors the cursor to 0 (`cause: store`), drops every held entry, redelivers what was already read, and does it again on every read after — so both sites call the one `Accepts`, and the mutation was verified to fail exactly the two tests aimed at it and nothing else. **As-built, three calls — the first REVERSED the same evening.** (i) As first landed, an unnamed reader did NOT match `role@<its own session id>` (the plan's named pin, on ADR-0016 d6's ground); the d3 amendment above reversed it — a window IS reachable at its session — and the pin now reads the other way. (ii) Named-ness is CARRIED from the registration, never inferred from cursor-key ≠ session — the inference is right for the trail (equal means "nothing extra to say") and would silently un-route a registration whose `--as` happened to equal its session id. (iii) `mail.deliver` says which mailbox with an `instance` column beside `role`, the spelling and the write-only-when-named rule `mail.cursorAdvance` already uses, rather than the plan's word "address": two spellings of "which mailbox" on one trail is the second implementation this subsystem keeps refusing to grow (ADR-0016 N8), and for a unicast envelope that column is the whole fact — `role` names a lane a dozen mailboxes hang off. **Known gap as first landed, CLOSED by the d3 amendment:** the read-only `MailReadPort` had to read every cursor as unnamed (it cannot tell a name from a session, and only one was reachable), so unicast showed as pending for nobody; with the key being the address there is nothing to tell apart, and the snapshot reads each cursor file as the mailbox its name spells. `mail status` follows the same address and counts unicast, since nothing broadcasts a unicast envelope to a second window and an uncounted one is mail no human is told about. Tests: `MailUnicastRoutingTests` (31 after the amendment and `answer-by-address`) + two `MailStatusTests` cases; the reducer's golden corpus needed no regeneration for the routing, which is the byte-identity proof for every window's reader |
| d3 — an instance's name is its registration (**amended: the key is the address**) | **As amended 2026-08-17:** `MailCursors.Pending(MailAddress requested, string? hookSession)` resolves the mailbox as `role@(requested.Instance ?? hookSession)` and that resolved address is BOTH the cursor key and the entitlement — the unnamed 2-arg overload passes a bare role and lets the window's session become the instance, so a window is reachable at `role@<session>` (`MailUnicastRoutingTests.AWindow_ReceivesUnicastAddressedToItsOwnSessionId`), a sessionless reader has no instance and reads broadcast alone (`TheSessionlessReader_HasNoUnicastAddress`), and `--as s-1` from window s-1 is the same mailbox as no `--as` from window s-1 (`ANameThatEqualsTheSessionId_IsTheWindowsOwnMailbox`). `MailReadPort.Over` therefore reads every cursor file as the mailbox its name spells and the snapshot's under-claim is gone (`TheReadOnlySnapshot_ShowsUnicastMailPendingForItsMailbox`); `mail status` counts a window's own unicast (`MailStatusTests.UnnamedRegistration_CountsUnicastAddressedToItsSession`). Trail unchanged. As first built: `mail digest --as <instance>`; `MailDigestOptions.Instance` + `CursorKey(sessionId) = Instance ?? sessionId`, keyed through the existing `MailCursors.CursorPath`, so `cursor.<role>.<instance>.json` and the unnamed path is unchanged byte for byte. Both halves of an address are grammar-checked AT REGISTRATION against `MailAddress.IsRole` — the envelope parser's own predicate, never a second spelling — because a `--role`/`--as` no sender could address is a mailbox that reads nothing forever, silently. **The split (this slice's sharp edge):** `MailPendingView` grows `HookSession` beside `Session`, where `Session` IS the cursor key and `HookSession` is the window; every `mail.*` event keeps naming the window in `sessionId`, and a new `instance` column names the mailbox — written ONLY when the two differ, so pre-ADR-0018 lines keep their exact shape. `MailCursors.Pending` is two OVERLOADS rather than one optional parameter: a defaulted `hookSession = null` reads as harmless and silently unlinks the choreography from its window, which the checked-in reducer golden caught, so the short call is the safe one. `mail status` follows the same key (`ReadableMailboxes`/`MailboxOf`) and names a qualified line by its full address — counting the window's cursor for a named registration would report a mailbox nobody reads. **Known gap, left honest:** the read-only snapshot cannot distinguish an instance-keyed cursor from a session-keyed one (the file name is just the key; learning otherwise means reading `handlers.json` from a port that reads only the mail dir), so a named mailbox appears in presence as a session no window is called. The trail CAN tell, so the live picture is recoverable; making the snapshot say it is `canvas-instances`' decision. Tests: `MailInstanceRegistrationTests` (13) + four `MailStatusTests` cases; byte-identity proven by the golden corpus needing no regeneration |
| d8 — `forwardedFrom`, the second envelope-to-envelope reference | `MailForwardedFrom(Id, Address)` + `ParseForwardedFrom` in `dotnet/captainHook/Mail/MailEnvelope.cs`; written beside `inReplyTo` by `MailStore.Render` and omitted when absent; `MailForwardedFromDto` through `api.schema.json` → `api.gen.ts` → `MailEnvelopeView.forwardedFrom`. **As-built:** the id is presence-only as this decision requires, but the ADDRESS is grammar-checked — the two halves are different kinds of reference, and an address no sender could write names no mailbox that ever existed. The record carries the address BESIDE the id because the id alone cannot say whose mailbox the mail was stranded in: the original's `to` may be a bare role a dozen mailboxes hold, and that is exactly the fact a forward exists to preserve. Object-shaped, strictly walked like `from` (unknown/duplicate members malformed, errors named `forwardedFrom.<field>`) — a half-read provenance link would name an origin nobody can check. Nothing reads it yet; `reaper-payloads` is what writes one. The reducer's golden corpus regenerated to 62 pure `"forwardedFrom": null` insertions and nothing else, which is the mechanical-clone proof. Tests: six parse cases + two store round trips |
| d5 — unicast has no TTL, refused not ignored | Parser: `ttlDeliveries` on a `role@instance` address is a violation, and it SUPERSEDES the `>= 1` bound (a `ttlDeliveries: 0` unicast envelope has one thing wrong with it, and the address is the reason). `MailEnvelope.TtlDeliveries` is `int?` with `HasTtl`; null has exactly one meaning — unicast — and is never a second spelling of the default. Write side: `MailStore.Render` OMITS the field (the format's existing spelling of absent, as for `session`/`inReplyTo`; null or 0 would each invent a fact), and `mail.append` omits it too, so the column keeps its type instead of becoming a string `"none"` every consumer must special-case. Expiry: `MailCursors.Pending` guards the comparison, so a held unicast envelope is never spent — bounded by the reaper's judgement (d6), not by arithmetic that drops unread mail. Read side: `MailEnvelopeDto`/`MailPendingDto` nullable → `api.schema.json` → `api.gen.ts` → `web/src/mail.ts` (`isExpired` returns false on null; `onAppend` requires the field for a role address and requires its ABSENCE for a unicast one, refusing anything else as a line the engine could not have written) → three renderings in `MailPanel.tsx` (`n held` on the mark, `none — unicast, delivered once to <addr>` on the card, "unicast mail does not expire" on the standing line). **As-built, and the reason this slice added no write-side validation:** `MailStore.Append` already re-parses the exact bytes it is about to make durable and refuses a line the strict parser would reject, so an envelope whose ttl contradicts its address — constructible in process, impossible from the wire — is refused AT THE APPEND rather than becoming a line no future reader can accept. `Append_RefusesAUnicastEnvelopeCarryingATtl` pins that; `Render_UnicastLine_ReParsesCleanWithNoTtl` pins the round trip the plan's verify note named. Tests: the parse table's d5 block, three store tests, `mail.skeptic.test.ts` § 9 (4), e2e green |
| d1/d2 — two address kinds, one namespace; `role` or `role@instance`, refused not guessed | `MailAddress` (`TryParse`, `IsRole`, `Role`, `Instance`, `IsUnicast`, `GrammarHelp`) in `dotnet/captainHook/Mail/MailAddress.cs`, called from `MailEnvelope.TryParse` on `to` — the single choke point every write path crosses (`mail send` parses before it appends; the store serializes a parsed record), so an ungrammatical address cannot reach the chain. **As-built**: the check is scoped to `to` alone — `from.agent` is a free-form provenance label nobody routes on, and constraining it would refuse envelopes already on the ledger for a property nothing reads. Lowercase is PINNED, not folded (unlike `kind`/`priority`, which are closed sets a parser can correct a casing slip against): folding here while `MailCursors.CursorPath`'s percent-encoder keeps `Ops` and `ops` as two cursor files is N8 wearing an address for a hat. The alphanumeric test is ASCII spelled out by hand rather than `char.IsLetterOrDigit`, which is Unicode-aware and would admit `mаintainer` with a Cyrillic а — a mailbox rendering identically to a real one and receiving none of its mail, which is exactly the silent misrouting d2 exists to refuse. A second `@` is refused rather than split-on-first or split-on-last, because `a@b@c` has two plausible readings and picking either is guessing. A trailing `-` is legal: the grammar is the ADR's verbatim and was not "improved" in passing, this being the half of the decision that is permanent. Tests: `MailAddressTests` (17) + the address block in `MailEnvelopeTests` (30), including the named legacy corpus (every role on the maintainer's live ledger and in the fixtures) — the one way this slice could have silently orphaned mail already on the chain. Flow doc: [doc/flow/mailbox-bus.md](../flow/mailbox-bus.md) § The address grammar |

## Revisit triggers

- A role whose members must not duplicate work ⇒ the work-queue kind
  (claim/lease), its own ADR.
- The reaper's authority: registration-scoped vs. policy-scoped — decided at
  `reaper-role`, recorded here.
- A second cross-mailbox reader appears ⇒ generalise the reaper's read
  authority into a capability rather than a special case.
- A harness whose session ids are not `[a-z0-9-]` (or run past ~100 chars)
  ⇒ that harness's windows are keyed by a session no sender can spell, i.e.
  broadcast-only, silently. Decide then whether the cursor key ENCODES the
  session into the grammar or the harness spec declares a mapping.
