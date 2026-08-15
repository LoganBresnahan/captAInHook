# Flow: the mailbox bus — N agent loops, one daemon, store-and-forward

captAInHook's cross-harness communication layer (ADR-0016, roadmap item 20).
The reframe that makes it possible: the daemon already sits between *every*
agent's loop and *every* one of its seams, so it is the only thing on the
machine positioned to be a **hub**. Mail is written by appending an envelope to
a durable disk store; it is *delivered* only at seams the recipient's harness
actually declares.

**Zero core.** The whole bus is two engine CLI verbs, payloads, and data — no
new project, no daemon boot mode, no new runtime dependency. A member joins by
running a process (`mail send`) and, if it wants to *read*, by being registered
as a handler (`mail digest`). Nothing about "swarm" is a mode the daemon enters.

```
  ANY process, any language, any harness — or none at all
        │  one JSON envelope on stdin
        ▼
  captainHook mail send ──── strict parse (MailEnvelope.TryParse)
        │                     · ts STAMPED here if absent — one clock for the bus
        │                     · a rebuild that copies fields verbatim, so a stamp
        │                       can never launder a malformed envelope past the parse
        ▼
  MailStore.Append ─── flock (FileShare.None) ─── ~/.captainHook/mail/mail.jsonl
        │  · re-parses its OWN rendered bytes and REFUSES what a reader would skip
        │  · prev = SHA-256 of the previous line's bytes (genesis = 64 zeros)
        │  · MaxLineBytes 128KiB — REFUSED above it, never truncated, so no writer
        │    can produce a line a windowed reader would silently drop
        ▼
  ════════════════ the store is store-and-forward: nobody need be listening ═══
        ▼
  a hook fires ──► daemon ──► registered `mail digest --role R --seam S`
        │
        ├─ MailCursors.Pending(role, session) ── the per-(role, session) position
        │     frontier + held[] ── re-anchors LOUDLY on 7 disagreements
        │     TTL: expired = deliveries − seenAt + 1 ≥ ttlDeliveries
        │
        ├─ MailDigest.Plan(pending, seam, verbs)   ── PURE: no I/O, no clock
        │     seam class × priority × the event's DECLARED verbs → deliver | none
        │
        ├─ MailDigest.Render(plan, maxChars)       ── deterministic, golden-pinned
        │     priority rank, then arrival order · whole-item cap · provenance head
        │
        └─ ADVANCE, then EMIT ─────────────────────────────────────────────┐
              MailCursors.Advance under a per-cursor flock                  │
                ├─ guard: view's deliveries == disk's   (staleness)         │
                ├─ guard: store's HeadHash == view's    (chain identity)    │
                ├─ refused → mail.cursorRefuse → answer NOOP, mail pending  │
                └─ Written → mail.deliver on the ledger → answer the effect ┘
                                                             │
                                                             ▼
                                            inject (context) | decide (block)
                                                 the recipient's loop
```

## Two halves, deliberately asymmetric

**Writing is universal; reading is a seam.** Anything that can run a process can
put mail on the bus — that is the whole reason `mail send` is a verb rather than
a documented JSON shape a payload hand-rolls (the ADR's rejected shell-script
alternative, inverted into the verb's reason for existing). A member needs no
session, no harness, and no hooks: `from.session` is optional precisely so the
cheapest membership class — a write-only observer, a cron-shaped reporter — is
representable.

Reading is the opposite. Mail cannot be *delivered* whenever it arrives, because
an agent's loop has only the openings its harness declares. So delivery is
pull-shaped and seam-bound: a registered `mail digest` runs at a hook, reads
what is pending for its role, and answers with one effect from ADR-0010's closed
grammar. **The bus never sees LLM-ness** — whether a member is a shell one-liner
or a second model is a payload detail (see `examples/payloads/`
`starter-mail-observer.sh` and `starter-mail-watcher.sh`).

## The seam class is registration data, not an event name

"Is PostToolUse a mid-turn seam?" is a fact about loop *position* that no
`HarnessSpec` field carries and no event NAME answers without hardcoding one
harness's vocabulary. Since d7 already makes registration configuration, the
registration declares it: `--seam ambient|urgent|reconcile`, one `handlers.json`
entry per seam class. The planner then stays a pure function with no
per-harness code path.

| seam class | typical event | delivers | when nothing qualifies |
|---|---|---|---|
| `ambient` | turn start (UserPromptSubmit, SessionStart) | every priority | noop, **no advance** |
| `urgent` | mid-turn, per tool call (PostToolUse) | `urgent` only | noop, **no advance** |
| `reconcile` | turn end (Stop) | every priority | noop, **no advance** |

Two rules do the real work:

- **An advancing seam delivers everything it can.** Once a seam advances, every
  held envelope ages by one — so holding deliverable mail at an advancing seam
  only burns TTL for nothing. This was the cursor slice's pinned obligation on
  the planner, and it is why only the `urgent` class filters.
- **A quiet seam ages nothing.** A mid-turn seam with no urgent mail pending
  plans `Vehicle.None`, and the verb reads that as "do not advance". Three
  consecutive quiet tool calls age a `ttl: 1` envelope by zero.

**Vehicle degrades downward only, and `inject` is preferred at every class.**
`decide` is reserved for events whose *only* loop verb is decide — which is
exactly what makes the Stop block non-escalating. Preferring `decide` would let
a `--seam reconcile` typo on a decide+inject mid-turn event turn a status
message into a **denied tool call**.

An event the spec does not declare, or declares effectless, delivers nothing and
never advances — deliberately **stricter** than the permissive capability gate,
because that gate noops *after* the advance, and that direction is silent mail
loss.

## Why the cursor is not a bare offset

A single integer cannot express out-of-file-order delivery. A mid-turn seam
delivering `urgent` mail that sits *past* held ambient mail forces one offset to
either lose the held line or re-deliver the urgent one — the "too early loses
mail, too late double-injects" trap. So the cursor is a read **frontier** plus
`held`, a bounded exception list of what is still pending *behind* it
(offset + id + seenAt). Delivered mail is structurally **absent** rather than
flagged.

`head` — the chain's first-line hash — rides beside `gen` as the chain-native
rotation check, since every generation restarts at genesis and a rotation is
therefore a head change.

**The TTL clock is `deliveries`, never a wall clock.** It increments once per
`Advance`. An envelope stamped `seenAt` at its first pass-over is spent when
`deliveries − seenAt + 1 ≥ ttlDeliveries` — "passed over at N opportunities".
Reads without advances age nothing. A wall clock would rot mail while a
recipient idles overnight, which is house invariant 2 violated at the design
level.

### Re-anchoring is loud, and there are nine ways in

A cursor that disagrees with the file re-anchors to offset 0 — store-and-forward
means offline mail reaching the next holder of a role is the *feature* — always
emitting `mail.cursorReanchor` and always preserving the monotonic `deliveries`
counter. Every disagreement is one of:

file unreadable · content malformed · foreign `gen` · changed `head` (a
different chain at the same path) · offset past the frontier (truncation) ·
offset resting on no line boundary · a held entry not behind the frontier · a
held entry whose offset/id no longer matches the file · a held entry addressed
to **another role** (never a substitutable delivery).

An absent cursor anchors at 0 silently — that is first contact, not
disagreement. The frontier never enters an unterminated tail.

## Advance before emit — one contract, two guarantees

The verb checks everything that could stop the effect *first*. A failed advance
answers noop with the mail still pending; only a `Written` cursor is followed by
an emitted effect. This single ordering buys both:

1. **At-most-once.** The same seam asked twice delivers exactly once.
2. **The Stop-loop guard.** A reconcile seam that blocks the turn must let the
   *next* Stop through, or block-on-unread is an infinite loop — and the harness
   has no loop cap of its own, so our advance is the only guard.

`Advance` runs under a **per-cursor flock**, which makes it the authoritative
at-most-once backstop rather than a best-effort check: two concurrent digests
for one (role, session) cannot both pass the staleness guard. Under that lock it
re-reads the store's identity too — a view of a chain that is *gone* is refused,
because "a disk cursor on a different chain vouches for nothing" runs exactly
backwards when the **view** is the stale side.

The accepted deviations, each in its stated direction:

| situation | what happens | why this direction |
|---|---|---|
| advance succeeds, process dies before stdout | delivery lost VISIBLY — `mail.cursorAdvance` on the trail, no `mail.deliver` | the ledger under-claims, never claims falsely |
| a guard refuses | `mail.cursorRefuse` (info), answer noop | usually a legitimate concurrent delivery winning the race |
| cursor deleted mid-race at `deliveries` 0 | both racers may deliver | indistinguishable from first contact; a guard here would refuse every genuine first contact (d13's stated deletion cost) |
| chain break *behind* the frontier | one audit fault, delivery continues | refusing to deliver over tampered history turns tamper-evidence into denial of service |

## The chain, and what it does and does not prove

Every line carries `prev`, the lowercase-hex SHA-256 of the previous line's
**exact bytes excluding its LF** (the terminator is framing, not content).
Genesis is 64 zeros — an *absent* `prev` cannot distinguish "first line" from
"head deleted". `VerifyChain` reports four fault kinds: `Genesis`,
`PrevMismatch`, `PrevMissing`, `Unreadable`. The expected link is computed from
actual bytes, so one corruption stays one fault instead of cascading.

**Rotation starts a new chain.** Every generation is an independent,
self-verifiable file; a cross-file `prev` is refused, because it would make a
file unverifiable in isolation and d13 archives generations independently. The
honest cost, stated rather than engineered around: deleting a whole archived
generation is chain-invisible. Cross-generation continuity belongs to the
cursor's `gen`, never to `prev`.

The 128KiB line cap is enforced **at the write** and refuses rather than
truncates (a truncated body would be the store rewriting what a sender said) —
that is phase 2's oversized-line carry-in closed, so no writer can leave mail
durable and undeliverable. The chain still hashes any length it *finds*, and the
cursor's reader is windowless: a foreign oversized line that some other writer
forced in is delivered **truncated with a marker**, never size-skipped, because
skipping would be silent loss.

Readers deliberately never take the lock, so a verify racing a live append can
catch the tail mid-write — an unterminated tail says so (interrupted write *or*
append in flight) rather than crying tamper. Torn tails are **terminated, never
repaired**: the terminator rides the next append's single write, so a crash
mid-append reduces to the case already handled.

## The swarm profile is policy, not a mode

There is no swarm boot verb, and activating the bus is a **dispatch-policy
flip**. This is also the answer to a question registration alone cannot solve:
`handlers.json` is global and `--role` is a static string in an entry, so on one
machine every agent would run every member and report the *same role* — the bus
reduced to one agent talking to itself.

What separates two agents is per-project scoping, using policy criteria that
already exist (handler-named rules AND'd with a `project` path-prefix):

```json
{ "version": 1, "default": "allow", "rules": [
  { "handler": "mail-observer-alpha", "project": "/home/you/beta-repo",  "decision": "deny" },
  { "handler": "mail-digest-alpha",   "project": "/home/you/beta-repo",  "decision": "deny" },
  { "handler": "mail-observer-beta",  "project": "/home/you/alpha-repo", "decision": "deny" },
  { "handler": "mail-digest-beta",    "project": "/home/you/alpha-repo", "decision": "deny" } ] }
```

An excluded handler is filtered **before fan-out** — never asked, never
restarted — so the wrong-role member costs nothing in the window it does not
belong to. A profile is therefore just: members in `handlers.json`, scoping in
`dispatch.json`. Turning the swarm off is editing one file; `default: deny` is
the global pause it already was.

**Mail is addressed to a role, and a digest reads the role it was registered
with**, so members address *peers* rather than a shared "everybody" role —
nothing in the digest filters by sender, so a shared role would hand every
member its own traffic back.

## Provenance, and the ledger's other direction

The digest tells the recipient who is speaking (per-item: sender agent, harness,
kind/priority, envelope id as the store join key, age in delivery
opportunities). `mail.deliver` records the reverse — what the recipient was
actually *shown* — on join keys that already exist: envelope ids ↔ `dispatchId`
↔ `sessionId`, with `renderHash` and `bytesInjected` describing the bytes the
effect really carried. A cap-truncated delivery hashes truncated: the store
proves what was written, the ledger proves what was shown, and comparing the two
is exactly how "A only ever saw part of this" surfaces. `vehicle` is the one
fact nothing else can reconstruct — whether the digest *informed* the loop or
**blocked** it.

It is emitted only from the single branch where the cursor was actually written,
and only after the answer is on stdout.

## Three stores, three lifetimes (d13)

| store | lifetime | mode as built |
|---|---|---|
| cursors (`cursor.<role>.<session>.json`) | ephemeral — delivery position only; deletable anytime (a deleted cursor just re-anchors) | 0600, enforced |
| trail (`logs/captainHook.jsonl`) | operational telemetry, days-to-weeks | 0600 file / 0700 dir, enforced at creation |
| mail (`mail/mail.jsonl`) | **archival** — the inter-agent influence record, the longest-lived thing on disk | 0700 dir / 0600 file, enforced |

All three are owner-only, but the trail got there last: until 2026-08-15 neither
emitter set a create mode, so it landed at the process umask (`0644` on a
default install) while `api.json` — which holds the API bearer token — the mail
store, the cursors, and the rendezvous files were all explicitly locked. It
earns the mode on its **contents**, not its name: `exec.stderr` captures payload
stderr verbatim, so a trail holds whatever an arbitrary user process wrote to
its diagnostics.

The **directory** mode is the load-bearing half, because it is the only one that
covers files the engine never creates — a payload writing its own log beside
ours (`session-pulse.jsonl`, by shell `printf >>`) does so at that payload's
umask, and no engine change can reach it. A `0700` directory keeps it
unreachable anyway.

Two consequences of *how* the mode is set, both deliberate: `UnixCreateMode`
applies on CREATE only, and `Directory.CreateDirectory` is a no-op on an
existing directory — so neither call retightens anything. A tree from before the
fix is **discarded at deploy** (`/deploy` § 1c) rather than chmod'ed under a
user who may have widened it on purpose, which the trail's days-to-weeks
lifetime makes cheap.

The bus is a **recorded medium by design**: there is no "never-record" envelope
flag, so the rule for members is *don't put secrets in mail*. Secret-scrubbing
belongs to gate members at the tool seam, not to holes in the ledger.

Role and session are percent-encoded in cursor filenames, so a hostile role name
cannot escape the mail directory. The mail store grows unbounded until rotation
mechanics land (ADR-0016 N4); `gen` reserves them.

## Ground truth

| what | where |
|---|---|
| `MailEnvelope`, `MailSender`, `MailKind`, `MailPriority`, `TryParse`/`TryParseLine` | `dotnet/captainHook/Mail/MailEnvelope.cs` |
| `MailStore` (`Append`, `Read`, `VerifyChain`, `HeadHash`, `Render`, `HashOf`, `ResolveDir`, `TryLock`), `Genesis`, `MaxLineBytes` | `dotnet/captainHook/Mail/MailStore.cs` |
| `MailAppend` (Appended/Failed), `MailLine`, `MailChainFault`, `MailChainFaultKind` | `dotnet/captainHook/Mail/MailStore.cs` |
| `MailCursor`, `MailHeld`, `MailCursors` (`Pending`, `Advance`, `CursorPath`, `Enc`, `CurrentGen`) | `dotnet/captainHook/Mail/MailCursor.cs` |
| `MailPendingView`, `PendingMail`, `MailCursorWrite` (Written/Failed) | `dotnet/captainHook/Mail/MailCursor.cs` |
| `MailDigest` (`TryParseArgs`, `TryParseRequest`, `Plan`, `Render`, `Run`, `LogDelivery`), `MailSeam`, `MailVehicle`, `MailPlan`, `MailRender`, `MailDigestOptions` | `dotnet/captainHook/Mail/MailDigest.cs` |
| `MailSend.Run` (the universal write path; stamps `ts`) | `dotnet/captainHook/Mail/MailSend.cs` |
| verb routing (`Mode.MailSend`, `mail <subverb>` on the argv contract) | `dotnet/captainHook/Program.cs`; `captainHookWire/` argv contract; refused by the shim |
| `Stop`/`SubagentStop` declaring `decide` + the top-level block shape | `dotnet/captainHook/harnesses/claude-code.json`; `DecidesAtTopLevel`/`TopLevelDecision` in `dotnet/captainHook/Core/Harness.cs` — see [hook-dispatch.md](hook-dispatch.md) and platform.md § The Stop block shape |
| swarm scoping (handler × project rules, pre-fan-out exclusion) | `dotnet/captainHook/Core/DispatchPolicy.cs`; `Dispatcher.DispatchAsync(excludedHandlers)` — see [dispatch-policy.md](dispatch-policy.md) |
| starter members (write-only observer; on-demand LLM watcher) | `examples/payloads/starter-mail-observer.sh`, `starter-mail-watcher.sh`, `examples/payloads/handlers.json` |
| trail events | `mail.append` (+ `bytes`, provenance, never `body`), `mail.torn`, `mail.lockBusy`, `mail.expire` (+ `offset`), `mail.deliver`, `mail.cursorAdvance` (+ `deliveredOffsets`), `mail.cursorReanchor` (+ `cause` cursor|store, `deliveries`), `mail.cursorRefuse`, `mail.cursorVanished` — every cursor-family event carries the `sessionId` column (ADR-0016 d14 as-built: the observation surface's join keys) |
| the observation surface's reducer (d14; the read endpoint and canvas rows land with their slices) | `web/src/mail.ts` — pure `(state, trailLine) → state` seeded from `MailDto`; golden corpus `web/src/mail.golden.json` GENERATED by `dotnet/captainHookTests/MailReducerGoldenTests.cs` (2), replayed + attacked by `web/src/mail.test.ts` / `mail.skeptic.test.ts` (`npm test`) |
| envelope parse table (26) | `dotnet/captainHookTests/MailEnvelopeTests.cs` |
| store: chain, flock, torn tails, write gate (43) | `dotnet/captainHookTests/MailStoreTests.cs` |
| cursor: frontier/held, TTL, re-anchor (32) | `dotnet/captainHookTests/MailCursorTests.cs` |
| exactly-once races, chain-changed guard, drain soak (15) | `dotnet/captainHookTests/MailCursorEdgeTests.cs` |
| planner matrix, golden renders, verb, ledger, daemon + Stop smokes (54) | `dotnet/captainHookTests/MailDigestTests.cs` |
| `mail send` verb end to end (9) | `dotnet/captainHookTests/MailSendTests.cs` |
| reentrancy guard proven by stub `claude`; two-role swarm smoke (5) | `dotnet/captainHookTests/MailDogfoodTests.cs` |
| field report — first members live | `doc/dogfood/2026-08-14-first-bus-members.md` |
