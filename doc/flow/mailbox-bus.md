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
  a hook fires ──► daemon ──► registered `mail digest --role R [--as I] --seam S`
        │
        ├─ MailCursors.Pending(address, hookSession) ── the mailbox's position
        │     keyed role × (I ?? session); reads `R`, plus `R@I` when named
        │     frontier + held[] ── re-anchors LOUDLY on 7 disagreements
        │     TTL: expired = deliveries − seenAt + 1 ≥ ttlDeliveries (broadcast only)
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

**A unicast envelope has no TTL at all** (ADR-0018 d5), and that is a third
state rather than a large number: `ttlDeliveries` is *refused by the parser* on
a `role@instance` address, omitted from the stored line and from `mail.append`,
and null through the DTO, the reducer and the canvas. With one addressee,
*delivered* is a fact rather than a matter of opportunities — so the comparison
above simply does not run, and a held unicast envelope is never spent. That is
not an unbounded leak: a mailbox that never returns is the reaper's problem
(d6), disposed of by judgement with a trail line, never by an arithmetic that
quietly drops mail nobody has read.

Refused rather than accepted-and-ignored, because an ignored field on an
append-only chain is a lie that outlives everyone who could correct it. The
write side needed no new guard: `MailStore.Append` already re-parses the exact
bytes it is about to make durable and fails if the strict parser would reject
them, so an envelope whose ttl contradicts its address (constructible in
process, never from the wire) is refused at the append instead of becoming a
line no future reader can accept.

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

### A mailbox is named by its registration (ADR-0018 d3)

`mail digest --role R --as I` names the mailbox a registration reads. **The
cursor key becomes role × instance**, where instance is `--as` when given and
the hook's session id when not — so an unnamed reader is exactly the reader
ADR-0016 built (one ephemeral cursor per window, `cursor.<role>.<session>.json`
unchanged), and a named one has a durable mailbox that outlives every window
that serves it. Two windows registered under one name **share one cursor**:
first pickup consumes, the other sees nothing. That is the correct meaning of
"one agent" as opposed to "one window", and it needed no new concurrency —
`Advance`'s per-cursor flock already decides who wins.

Both halves of an address obey the one grammar, checked at registration against
the same predicate the envelope parser uses. A `--role` or `--as` that no
sender could address would be a mailbox nothing can ever reach; refusing it at
dispatch is loud, where letting it register is silent forever.

**The split that makes this work: the cursor keys on the instance, the trail
keeps the window.** `sessionId` on every `mail.*` event still answers *who
moved it*; a new `instance` column answers *which mailbox moved*, and is
written **only when the two differ** — so every line a pre-ADR-0018 reader has
seen keeps its exact shape (the reducer's checked-in golden corpus did not have
to be regenerated for this slice, which is the proof). Collapsing them would
cost one or the other: key on the session and a named mailbox stops being
durable; log the instance and two windows sharing a name become
indistinguishable in the trail. The count on a status bar follows the same key
— `mail status` reads `--as` through the same registration parser, because
counting the window's own cursor for a named registration would report a
mailbox nobody reads.

*Known gap, deliberate:* the read-only SNAPSHOT cannot tell an instance-keyed
cursor from a session-keyed one — the file name is just the key, and learning
which keys are names would mean reading `handlers.json` from a port that reads
only the mail dir. The live trail can (that `instance` column), so the picture
is recoverable from the stream; making the snapshot say it belongs to
`canvas-instances` and its sub-lanes. Until then a named mailbox shows up in
the presence list as a session no window is called, with no dispatch age.

### The address grammar (ADR-0018 d2)

`to` used to accept any non-blank string. It now parses as **`role`** or
**`role@instance`**, each half matching `[a-z0-9][a-z0-9-]*`, at most one `@`,
both halves non-empty — anything else is a parse violation and the envelope is
refused. `MailAddress` (`Mail/MailAddress.cs`) is the whole decision; the
envelope parser calls it on `to` and on nothing else, since `to` is the only
routing key and `from.agent` is a free-form provenance label nobody addresses.

Introducing the separator *is* introducing the grammar: `@` can only mean
"instance follows" if it can mean nothing else. Refused-not-guessed is the same
direction the rest of this parser points — a misrouted envelope is silent, and
silence is what the bus is built against, whereas a refusal is loud while a
human can still fix the typo.

Two choices are worth knowing because they diverge from neighbours in this
file. Lowercase is **pinned, not folded**, unlike `kind`/`priority`: those are
closed sets a parser can correct a casing slip against, whereas an address
names an open universe of mailboxes — and folding here while
`MailCursors.CursorPath`'s percent-encoder keeps `Ops` and `ops` as two cursor
files would be one concept with two implementations (ADR-0016 N8). The
alphanumeric test is **ASCII by hand**, not `char.IsLetterOrDigit`, because a
Unicode-aware check admits `mаintainer` with a Cyrillic а — a mailbox that
renders identically to a real one and receives none of its mail.

The grammar landed first and alone because its risk is permanence: the ledger is
append-only, so what parses today is what it holds forever. It shipped with
nothing routing on it — a `role@instance` envelope parsed, was carried verbatim,
and was addressed to nobody — and the slices below closed that in order:
`unicast-refuses-ttl` (above), `instance-registration` (naming a mailbox), and
`plan-unicast` (the predicate, next).

### Who reads what (ADR-0018 d4)

An address is a routing key; a cursor is a position. The two are related but not
the same, and the predicate is the address's:

```
   registration:  mail digest --role main [--as laptop-a]
                        │              │
                        │              └── names the MAILBOX (durable)
                        ▼
   MailAddress ── Accepts(to) ─────────────────────────────────┐
        │                                                      │
        │   unnamed  →  to == "main"                            │
        │   named    →  to == "main"  ||  to == "main@laptop-a" │
        │                                                      │
        └── called from BOTH recipient filters: ───────────────┘
              MailCursors.Pending's ledger scan  (what may be delivered)
              LoadOrAnchor's held-entry check    (what a cursor may still hold)
```

Naming a mailbox *adds* an address; it does not remove one. A named
registration is still a holder of its role and still takes the role's broadcast
— unicast is the second thing it answers to, not a replacement for the first.

Two properties carry the whole slice, and both are about silence:

**An unnamed reader does not match `role@<its own session id>`.** Session ids
are grammar-legal instance names, so that is a real address a sender can spell.
Matching it would make windows addressable — the model ADR-0016 d6 rejected and
this ADR re-rejected, since a mailbox keyed to a window dies with the window
(four of six cursors on the live lane are that failure). The session id is the
cursor *key*'s fallback, never a name.

**One predicate, two call sites, and only one of them is on the happy path.**
`Pending`'s scan decides what a digest may deliver; `LoadOrAnchor`'s held-entry
check decides whether a cursor's held list still describes mail this mailbox
reads. A named reader that *holds* an undelivered unicast is the state where a
disagreement surfaces, and it does not surface as a missing feature: the scan
accepts the envelope, the digest holds it, and the next read declares the held
entry addressed to someone else — a `store`-cause re-anchor that resets the
cursor to 0, drops every held entry with it, redelivers what was already read,
and blames the store for a disagreement between two lines of our own code. Then
does it again on every read after that. Calling one `MailAddress.Accepts` from
both sites makes that unrepresentable rather than merely tested for.

Delivery into a named mailbox says so on the ledger: `mail.deliver` carries an
`instance` column beside `role`, in the same spelling and under the same
write-only-when-named rule as `mail.cursorAdvance` (§ *instance registration*).
For a unicast envelope that column is the entire fact — `role` names a lane a
dozen mailboxes may hang off, and `sessionId` names a window that will be gone
tomorrow.

**One thing this deliberately does not do.** The read-only observation surface
(`MailReadPort`) reads every cursor as *unnamed*, so unicast mail shows there as
pending for nobody. A cursor file's name is just its key, and this surface reads
only the mail directory — it cannot tell an `--as` name from a session id, and
so cannot know whether that mailbox is entitled to `role@key`. Between
under-claiming and guessing, a read-only picture under-claims: guessing the
other way would paint every window as addressable by its session id, which is
the routing model d4 refuses. The live trail *can* tell (that `instance`
column), so the picture is recoverable from the stream; saying it in the
snapshot is `canvas-instances`' to design.

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

## The observation surface (d14): watching without touching

Item 20 built a bus nothing could show. The Mail view is the picture, and its
whole design problem is that a *watcher* must never become a *reader* — the
cursor is the delivery contract, and a GUI that advanced one would consume the
operator's mail by looking at it.

```
  ~/.captainHook/mail/                    logs/captainHook.jsonl
   mail.jsonl   cursor.<role>.<sess>.json      (the trail)
        │              │                            │
        └──────┬───────┘                            │
               ▼                                    │
        MailReadPort  ── five method-group delegates: Read / VerifyChain /
               │          HeadHash / List / Pending.  No Append. No Advance.
               ▼                                    │
        ApiReadModel.Mail(since) ◄──────────────────┤ TrailEventId (stamped FIRST)
               │                                    │ mail.deliver preload (6a)
               ▼                                    │
        GET /api/v1/mail?since=  ──┐                │
                                   │                │
   browser                         ▼                ▼
        seedMail(dto) ──────► MailState ◄──── reduceMail(state, line)
               │                  ▲                 ▲
               │                  │            SSE  │ Last-Event-ID: <TrailEventId>
               │                  │        /api/v1/events (its OWN subscription)
               ▼                  │
        buildScene(state) ────────┘        resnapshot ⇒ re-seed + re-anchor
               │
               ▼
        the canvas: ledger spine · role lanes · session cursors · zoom tiers
```

**Observation is not delivery**, pinned three ways rather than promised. The read
model holds a `MailReadPort` — method-group delegates over a store and cursors it
creates privately — so there is no handle to append or advance and no interface a
cast could re-open; a reflection walk over the read model's declared graph
asserts it, and because captured closures hold what reflection cannot see, a
SOURCE pin asserts no file under `Api/` even NAMES the writable types or verbs.
No non-GET method answers under `/mail`, driven as a route-table theory that also
asserts the store's bytes are unchanged and no cursor file was created: *asking
cannot create a mailbox.* And `delivered` comes from a `mail.deliver` ledger line
and nowhere else — never inferred from a cursor being past an envelope.

**The picture is an interpolator, never a second store** (N8). A snapshot is
authoritative and every re-seed REPLACES reduced state; between snapshots the
reducer folds trail events under three rules — APPLY what an event states, DERIVE
what the ledger proves, FLAG what neither gives. When it cannot honestly say
where a cursor is, it says so and asks for a snapshot instead of guessing; that
is the only defence against the one failure no screenshot catches, a false
picture that looks fine.

**Snapshot and stream are joined by a stamp, not by luck.** Subscribing after the
snapshot loses the window between them; subscribing before it replays. So the
snapshot carries `trailEventId` — the trail's end, read BEFORE the store, so the
residual window can only duplicate (idempotent) and never lose — and the client
opens its subscription exactly there. Mail runs its OWN subscription rather than
filtering the trace's, because filtering by id would mean interpreting an opaque
token (ADR-0009 d2) and because the trace's buffer is capped, where dropping the
oldest line is right for a log and silent corruption for a reduced picture.

**What the trail can prove reaches back further than the page.** A live stream
starts *now*, so through slice 5 every pickup older than the tab read *before
cursor · no record*. Slice 6a folds the trail's own `mail.deliver` lines into the
snapshot (`MailDto.deliveries`), and `deliveriesComplete` says whether the fold
saw the whole file — which is what lets the detail card distinguish "nobody read
it" from "further back than I can see". The rule did not move; only the reach.

**Deliberately not built:** a scrub bar (slice 6b, declined 2026-08-17 — the
trail is queryable JSONL and reading it answers the debugging case; ADR-0016's
addendum records the reasoning and the revisit trigger), and sending from the
GUI, which is a consent decision of its own and is not in this ADR.

## The human channel: `mail status` (ADR-0017 d2)

The bus can now be *watched* (the Mail canvas) and *delivered* (a digest at a
seam), but both require the human to already be looking. `captainHook mail
status` is the third surface — a count on whatever passive display the harness
offers:

```
       stdin: {"session_id": …, "cwd": …}          ← the harness's status payload
                     │
   handlers.json ────┤  registrations whose args MailDigest.TryParseArgs accepts
                     │        ⇒ address              (recognition, not a 2nd parser)
   dispatch.json ────┤  Evaluate(event, cwd, session) per event it is registered on
                     │        ⇒ the mailboxes THIS window may read
   cursor+store ─────┘  MailCursors.Pending(address, session)   ── unicast counted
                     │
                     ▼
              📬 2 · 1 urgent        (one line per role; the role is NAMED only
                                      when the window may read more than one)
```

Three properties, each deliberate. It is **ruleless** — every other channel in
ADR-0017 can spend tokens or take a turn and so gets a consent surface, while a
count on a status bar is read when a human chooses to look. The role is **never
declared twice**: which roles a window reads is already answered by the digests
that survive `dispatch.json` there, so a denied digest means a silent bar rather
than a count for mail this window will never be handed. And **silence is a
state**: no readable role, no pending mail, an absent or malformed
`handlers.json` all print nothing and exit 0, because a display command that
failed loudly would put an error where a human expects a number. Expired mail is
not counted (it is spent); held mail is (it is undelivered).

Wiring it into Claude Code — `~/.claude/settings.json`:

```json
"statusLine": { "type": "command", "command": "/home/you/.captainHook/bin/captainHook mail status" }
```

The harness passes its status payload on stdin and renders what comes back;
`cwd` is read from `cwd` or `workspace.current_dir`, whichever the payload
carries. Any harness with a passive display and a JSON-on-stdin command hook
wires the same way.

Cost, stated rather than discovered: a status bar renders on a human's cadence
and every call re-reads the whole store, which is unbounded until rotation lands
(N4). Nothing is cached and nothing is logged — a trail line per render would
drown the trail it exists to help you read.

## Ground truth

Test counts below are **cases as the runner reports them**, per FILE — not
methods, not `test(` declarations. Reproduce them:

```sh
dotnet test dotnet/captainHookTests/captainHookTests.csproj --no-build --list-tests   # per class; sum the classes in a file
cd web && node --test src/<name>.test.ts                                             # the ℹ tests line
```

Spelling that out because these numbers have drifted three times now, always in
the same way: a method count copied where a case count was meant (the envelope
table read `26` for a file the runner expands to 98), or a number carried
forward from an earlier commit. A count nobody can reproduce in one command is
prose, not ground truth.

| what | where |
|---|---|
| `MailEnvelope`, `MailSender`, `MailKind`, `MailPriority`, `TryParse`/`TryParseLine` | `dotnet/captainHook/Mail/MailEnvelope.cs` |
| `forwardedFrom` PROVENANCE (ADR-0018 d8, slice 4) — `{id, address}` on the envelope, the second and last envelope-to-envelope reference. The id is validated PRESENCE-ONLY (the ledger rotates; a forward that stopped parsing when its original aged out would destroy the provenance it exists to keep), the ADDRESS is grammar-checked (an address no sender could write names no mailbox that existed). Object-shaped and strictly walked like `from` — a half-read provenance link would name an origin nobody can check. Carries the address BESIDE the id because the id alone cannot say whose mailbox the mail was stranded in: the original's `to` may be a bare role a dozen mailboxes hold | `MailForwardedFrom` + `ParseForwardedFrom` (`Mail/MailEnvelope.cs`); `MailStore.Render` writes it beside `inReplyTo` and omits it when absent; `MailForwardedFromDto` (`Api/ApiDtos.cs`) → `api.schema.json` → `api.gen.ts` → `MailEnvelopeView.forwardedFrom` (`web/src/mail.ts`, snapshot-only like `body`/`ts`). Nothing READS it yet — the reaper (d6) is what writes one. Pinned by six parse tests + two store round trips |
| unicast has NO TTL (ADR-0018 d5, slice 2) — `ttlDeliveries` REFUSED on a `role@instance` address (not ignored), `MailEnvelope.TtlDeliveries` nullable, omitted from the stored line and from `mail.append`, null through DTO → reducer → canvas; expiry simply does not run, so a held unicast is never spent. No new write-side guard was needed: `Append` already re-parses what it renders | `MailEnvelope.TryParse` + `HasTtl`; `MailStore.Render`/`Append`; `MailCursors.Pending`'s expiry guard (`MailCursor.cs`); `MailEnvelopeDto`/`MailPendingDto` (`Api/ApiDtos.cs`); `isExpired` + `onAppend`'s ttl-applies rule (`web/src/mail.ts`); the three renderings in `web/src/MailPanel.tsx` (mark reads `n held`, the card reads `none — unicast`, the standing line says unicast mail does not expire). Pinned by the parse table's d5 block, `MailStoreFormatTests.Render_UnicastLine_ReParsesCleanWithNoTtl` + `Append_RefusesAUnicastEnvelopeCarryingATtl` (the adversarial case: a contradiction constructible in process, refused at the append), and `mail.skeptic.test.ts` § 9 |
| UNICAST ROUTING (ADR-0018 d4, slice 5) — the recipient predicate: a registration reads its role's BROADCAST always, plus its own `role@instance` UNICAST when it is named. `MailAddress.Accepts` is the one spelling and BOTH of `MailCursors`' recipient filters call it — the pending scan (what may be delivered) and `LoadOrAnchor`'s held-entry check (what a cursor may still hold), the second of which no happy-path drive reaches: a named reader HOLDING a unicast is where a disagreement surfaces, as a re-anchor loop that drops held state and redelivers, blaming the store. An UNNAMED reader never matches `role@<its session id>` (that would make windows addressable — ADR-0016 d6's rejected model), and named-ness is CARRIED from the registration, never inferred from key≠session. `mail.deliver` gains the `instance` column beside `role`, same write-only-when-named rule as `mail.cursorAdvance`. The read-only snapshot reads every cursor as unnamed and so UNDER-CLAIMS: unicast shows as pending for nobody there, because a cursor file's name is just its key | `MailAddress.Accepts` (`Mail/MailAddress.cs`); `MailCursors.Pending(MailAddress, hookSession)` + its unnamed 2-arg overload and `LoadOrAnchor` (`Mail/MailCursor.cs`); `MailDigestOptions.Mailbox` + `LogDelivery` (`Mail/MailDigest.cs`); `MailStatus.Run` passes the address whole (`Mail/MailStatus.cs`); `MailReadPort.Over`'s deliberate unnamed call (`Mail/MailReadPort.cs`). Pinned by `MailUnicastRoutingTests` (28 — the two predicate tables, the session-id refusal end to end, sibling invisibility, the held-unicast survival and non-expiry, the ledger column, and the snapshot's gap) + two `MailStatusTests` cases. The reducer does NOT mirror the predicate yet — `canvas-instances` owns that, and the golden corpus needed no regeneration here |
| INSTANCE registration (ADR-0018 d3, slice 3) — `mail digest --as <instance>`; cursor key = role × instance (`--as` ?? session id) so the unnamed path is byte-identical; `--role`/`--as` both grammar-checked at registration; the cursor keys on the instance while the trail keeps the window (`sessionId` = who moved it, a new `instance` column = which mailbox, written only when they differ); `mail status` follows the same key and names a qualified line by its full address | `MailDigestOptions.Instance`/`CursorKey` + `--as` in `MailDigest.TryParseArgs` (`Mail/MailDigest.cs`); `MailPendingView.HookSession`/`Named` and the two `MailCursors.Pending` overloads — the 2-arg one is the SAFE one on purpose, and since d4 it is also the UNNAMED one (`Mail/MailCursor.cs`); `MailStatus.ReadableMailboxes`/`MailboxOf` (`Mail/MailStatus.cs`). Pinned by `MailInstanceRegistrationTests` (13) and four `MailStatusTests` cases; byte-identity proven by the reducer's golden corpus needing no regeneration |
| the ADDRESS grammar (ADR-0018 d2, slice 1) — `to` parses as `role` or `role@instance`, `[a-z0-9][a-z0-9-]*` per half, one `@`, both halves non-empty; refused not guessed; lowercase pinned rather than folded; ASCII by hand (no homoglyph mailboxes); applied to `to` and nothing else. Routing on the instance is `plan-unicast`'s, above | `MailAddress` (`TryParse`, `IsRole`, `Role`, `Instance`, `IsUnicast`, `GrammarHelp`) in `dotnet/captainHook/Mail/MailAddress.cs`, called from `MailEnvelope.TryParse`; `MailAddressTests` (17) + the address block in `MailEnvelopeTests` (30: the legacy-role corpus, unicast accept, 17 refusals, the blank-vs-ungrammatical split, and the message that teaches the grammar) |
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
| the read ENDPOINT (d14, slice 1) — one read-only snapshot: chain status, the ledger from `since`, every cursor's pending view, inferred presence; `since` absent ⇒ 0, off-boundary ⇒ `sinceAligned: false` (never a spliced prefix), malformed ⇒ 400 | `ApiReadModel.Mail` + the `/api/v1/mail` route (`dotnet/captainHook/Api/ApiReadModel.cs`, `Api/ApiHost.cs`); DTOs in `Api/ApiDtos.cs`; the write half unreachable by construction via `MailReadPort` (`dotnet/captainHook/Mail/MailReadPort.cs`); presence from `SessionPresence` ∪ cursor files. `MailApiTests.cs` (31) — reflection walk, `Api/` source pin, non-GET route theory asserting nothing is written |
| the CANVAS (d14, slice 4) — ledger spine, a lane per role, a track per session, marks with the cursor's own arithmetic; semantic zoom in px-per-slot (far < 40 ≤ mid < 132 ≤ near); pan/zoom on ONE axis, every vertical measure a CSS pixel | `web/src/MailPanel.tsx` (`MailPanel`, `Spine`, `Lane`, `Glyph`, `Track`, `LaneHeads`, `Detail`; `data-lane`, `data-glyph`, `data-track`, `data-mark`, `data-tier`, `data-arrival`, `data-motion`) over `web/src/mailCanvas.ts` (`buildScene`, `MailView`); every status is `lineStatus`/`projectCursor`, never the canvas's own. `web/src/mailCanvas.test.ts` (39) against all 16 goldens; `web/e2e/mail.spec.ts` |
| the observation surface's reducer (d14, slice 3) | `web/src/mail.ts` — pure `(state, trailLine) → state` seeded from `MailDto`; golden corpus `web/src/mail.golden.json` GENERATED by `dotnet/captainHookTests/MailReducerGoldenTests.cs` (2), replayed + attacked by `web/src/mail.test.ts` / `mail.skeptic.test.ts` (`npm test`) |
| snapshot ⇄ stream alignment (d14 as-built): `MailDto.TrailEventId` is the trail's end STAMPED BEFORE the store is read, so a client subscribing at `Last-Event-ID: <it>` gets zero loss and zero duplicate. A STRING, because the id is opaque (ADR-0009 d2) and because `"0"` (replay everything) must never collapse into absent under a falsy test; null = no trail served ⇒ the picture is real and frozen | `MailDto` in `dotnet/captainHook/Api/ApiDtos.cs`; `ApiReadModel.Mail`/`TrailLength` (`_trailPath`, bound from `sseOptions.TrailPath` in `Core/DaemonHost.cs`); `MailState.trailEventId` in `web/src/mail.ts`; pinned by `MailApiTests` (resume-exactness end to end, null/"0" contracts, the source pin on read order) |
| the observation surface, LIVE (d14, slice 5) — snapshot → seed → stream at the stamp → resync when the reducer distrusts the picture | `web/src/mailStream.ts` (`runMailStream`, `startMailStream`), started lazily on the Mail view's first visit from `web/src/main.tsx`; folds through `foldMail` in `web/src/store.ts`; a SECOND subscription, never a filter over the trace's — see [management-gui.md](management-gui.md). Pinned by `web/src/mailStream.test.ts` (10) and, end to end against a real daemon, `web/e2e/mail.spec.ts` (arrival, delivery-by-record, no-poll, reset⇒resync) |
| the delivery PRELOAD (d14, slice 6a) — `delivered` still comes from a `mail.deliver` line and nowhere else, but the picture no longer has to have been watching when it landed: the daemon folds those lines out of the trail into `MailDto.deliveries` (columns verbatim; NO ledger offsets, because placing an id is the reducer's arithmetic and a second implementation is N8), and `MailDto.deliveriesComplete` is the narrow claim that the whole file was read and nothing trimmed — false for a scan window, a hit cap, or no trail at all, which is what lets the detail card distinguish "nobody read it" from "further back than I can see" | `MailDeliveryFold`/`MailDeliveryLine`/`MailDeliveryFoldResult` in `dotnet/captainHook/Api/MailDeliveryFold.cs`; `MailDeliveryDto` + `ApiReadModel.Mail` in `Api/ApiDtos.cs`/`Api/ApiReadModel.cs`; `preloadDeliveries`/`resolveDelivery` (ONE placement rule, shared with `onDeliver`) in `web/src/mail.ts`. Pinned by `dotnet/captainHookTests/MailDeliveryPreloadTests.cs` (11 — a real engine-written line, the payload-stderr forgery, the bounds saying so, and a drive proving three snapshots change nothing on disk), 5 in `web/src/mail.test.ts` (preload ≡ live, dedup, unplaceable-is-quiet, no phantom cursors), and `web/e2e/mail.spec.ts` (a pickup nobody watched, delivered on a reloaded page from a snapshot line) |
| the human channel (ADR-0017 d2) — `captainHook mail status`: roles from the `mail digest` registrations that survive `dispatch.json` for this cwd/session, counts from `MailCursors.Pending`, one `📬 n · m urgent` line per role, silent when there is nothing to say | `MailStatus` (`Run`, `Line`) in `dotnet/captainHook/Mail/MailStatus.cs`, routed from `Program.cs`'s `mail` switch; role recognition is `MailDigest.TryParseArgs` itself, policy is `PolicyResolution.Resolve` + `Evaluate` (`Core/DispatchPolicy.cs`), registrations are `ExecHandlersFile.Resolve` (`Core/ExecHandlersFile.cs`). Pinned by `dotnet/captainHookTests/MailStatusTests.cs` (36 — line goldens, per-cursor counts, handler/event/project denials, multi-seam collapse, the refused registration, four silences, unreadable stdin, a drive proving it creates and changes nothing, the four instance cases, and the two unicast ones. The `30` here was measured at the mail-status slice and never re-measured when d3 and d4 added cases) |
| envelope parse table (117, measured — the older `26` counted methods, not the theory cases they expand to) + `MailAddressTests` (17) | `dotnet/captainHookTests/MailEnvelopeTests.cs` |
| store: chain, flock, torn tails, write gate, the unicast + forwarded round trips (51) | `dotnet/captainHookTests/MailStoreTests.cs` |
| cursor: frontier/held, TTL, re-anchor (32) | `dotnet/captainHookTests/MailCursorTests.cs` |
| exactly-once races, chain-changed guard, drain soak (15) | `dotnet/captainHookTests/MailCursorEdgeTests.cs` |
| planner matrix, golden renders, verb, ledger, daemon + Stop smokes, instance registration (83) | `dotnet/captainHookTests/MailDigestTests.cs` |
| `mail send` verb end to end (9) | `dotnet/captainHookTests/MailSendTests.cs` |
| reentrancy guard proven by stub `claude`; two-role swarm smoke (5) | `dotnet/captainHookTests/MailDogfoodTests.cs` |
| field report — first members live | `doc/dogfood/2026-08-14-first-bus-members.md` |
| field report — the bus becomes visible (the Mail view + `mail status` on real traffic; the 6b verdict) | `doc/dogfood/2026-08-17-the-bus-becomes-visible.md` |
