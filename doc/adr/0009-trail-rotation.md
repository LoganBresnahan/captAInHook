# ADR-0009 — Trail growth: lock the resume-cursor contract now, defer segmented archive

**Status:** Accepted
**Date:** 2026-07-09

## Context

ADR-0007 d5 made the JSONL trail the live-stream source: SSE tails it, **the event
id is a byte offset in the file**, `Last-Event-ID` resumes, `Last-Event-ID: 0`
replays from the start. The writer is append-only (`Logging.fs`'s
`File.AppendAllText`) and `TrailTail` notes rotation is "rare and manual" —
nothing bounds the file.

ADR-0008 changed the trail's status from mostly-a-log to a first-class, long-lived
*read* surface for the GUI, and surfaced two coupled facts (0008 d5): the file
grows without bound, and bounded *storage* + bounded *history* collide with the
same invariant — the resume id is an **absolute byte offset**, so any rotation that
shifts offsets breaks the 0007 d5 resume contract.

**Owner decision (2026-07-09): get the GUI working first; defer the
memory-bounding.** So this ADR does the *minimum that protects the future* — it
locks the id contract now and records the segmented-archive design for later,
building no storage machinery this phase.

Two system facts constrain the deferred design: the trail has **two writers in two
processes** (daemon via `Actors.Log` / `Logging.fs`, shim via `WireJsonl`), both
appending to `~/.captainHook/logs/captainHook.jsonl`; and the trail is
**continuous across daemon cutover** (successors append to the same path), a
property worth preserving.

## Decision

Split into what is done *now* and what is designed-but-deferred. **The "now" is one
thing: the id contract.**

### Done now (in force this phase)

1. **No storage change — the single append-only trail stands.** ADR-0008's GUI
   streams from the file exactly as it exists today; this is the least-risk path
   and the trail is already battle-tested. Rotation, archive, and capping are all
   deferred (below), built when the file's growth actually bites — not
   speculatively.

2. **Lock the SSE resume-id contract as an opaque, resumable, may-`reset` cursor.**
   ADR-0007 d5 characterizes the id as "byte offset in the file." That is an
   **implementation fact of the single-file present, not a contract a client may
   lean on.** Restated contract, effective now:
   - the id is an **opaque monotonic cursor**; a client stores the last one it saw
     and sends it back to resume;
   - the server may answer *any* resume with a **`reset`** (re-anchor to 0 /
     earliest reachable) when that cursor is no longer reachable;
   - `Last-Event-ID: 0` means "from the earliest still-reachable point" — genesis
     today, the earliest retained segment later.

   For today's single file the cursor *equals* the byte offset, so **this is zero
   code change** — it purely locks the *meaning*, so that when segmentation lands
   the id can silently become a cross-segment global offset without breaking a
   single client. ADR-0008's fetch-streaming client is written to this contract
   from day one: treat the id as opaque, tolerate `reset` (0008 d4).

### Designed, deferred to the "trail-bounding" phase (built when growth bites)

3. **Segmented archive, uncapped.** Numbered segments (`captainHook.<seq>.jsonl`);
   on a size threshold the active file rotates and a fresh one opens. Old segments
   are **archived — kept, not pruned** (owner's call), and **uncapped for now** —
   chunk-and-keep, which bounds *per-file* size and keeps the trail inspectable but
   does *not* yet bound total disk. A total-size ceiling (prune or gzip the oldest
   past it) is a *further* deferral (revisit trigger).

4. **The opaque cursor becomes a concrete global offset.** Cumulative bytes across
   all segments (per-segment base + local offset) — still a single integer, so
   decision 2's contract is honored with no wire change. The tailer keeps a segment
   index and follows rotation deterministically (the `tail -F`-across-rename
   problem, made first-class instead of `TrailTail`'s "rare coincidence").
   Continuity survives rotation and daemon cutover.

5. **`GET /events?tail=N`** — bounded backfill, the GUI's "scroll back" (ADR-0008
   d5's named gap); the read-half of the same phase.

6. **Rotation is daemon-owned; short-lived writers only append.** Only the
   long-lived singleton rotates (avoids double-rotation races); POSIX `rename`
   atomicity + per-call append opens mean a shim append racing a rotation lands in
   one segment or the other, never lost. The collapsed-mode-no-daemon edge is for
   the impl slices.

### The 0007 ↔ 0009 hand-off — so both halves get done at the right time

ADR-0007 d5's "byte offset in the file" wording is **left as-is now** (it
accurately describes the single-file present) but is **superseded in contract by
decision 2**. **When the trail-bounding phase is built, that same change amends
ADR-0007 d5** — its id characterization is rewritten to the opaque-cursor /
global-offset contract and d5 is marked *amended-by-ADR-0009*. This ADR is the
standing reminder that the contract lock (now) and the d5 amendment (at build time)
are the two ends of one decision, and neither is orphaned.

## Consequences

### Positive

- **v1 ships with zero storage risk** — the trail is exactly today's proven single
  file; the GUI needs nothing new here.
- **The id contract is future-proofed.** Segmentation later is non-breaking
  because no client was ever allowed to assume single-file byte offsets — they
  treat the id as an opaque cursor and tolerate `reset`.
- **The bounding phase is a build task, not a re-litigation** — archive-over-prune,
  uncapped, the global-offset scheme, and the 0007 d5 amendment are all decided and
  recorded here.

### Negative

- **The single file grows unbounded until the bounding phase** — deliberately
  accepted; the memory work is out of scope now.
- **`Last-Event-ID: 0` replays the whole (growing) file**, increasingly costly over
  time; the `?tail=N` that fixes it is deferred with the rest.
- **The contract lock has no test teeth yet** — nothing prunes, so nothing
  exercises a `reset`; the guarantee ("client treats the id as opaque, tolerates
  `reset`") is held in review of the ADR-0008 client until segmentation makes it
  testable.

## Alternatives considered

- **Build rotation now, cap later.** Rejected: rotation's only payoff is the
  bounding we're deferring, so it is the hard tailer complexity (global-offset
  resume, rotation-following) for zero benefit this phase — the single file already
  serves the GUI.
- **In-place ring buffer** (overwrite / truncate-from-front). Rejected: it shifts
  or invalidates absolute offsets, shattering the cursor contract — the naive
  "won't it get huge → ring it" instinct, exactly wrong for a resumable stream.
- **Prune instead of archive.** Not chosen (owner prefers keeping history);
  recorded as the retention style for the bounding phase, with prune / gzip-cap as
  the later disk-bounding option.
- **Leave 0007 d5 unqualified, no forward link.** Rejected: the "byte offset"
  wording would silently become a lie after segmentation, and a client could
  hard-code it; the lock + the explicit 0007 ↔ 0009 hand-off prevent that.

## Revisit triggers

- **The single trail file's growth / memory bites** ⇒ build the trail-bounding
  phase (decisions 3–6) *and* amend ADR-0007 d5 (the hand-off).
- **Total disk matters** (archive-uncapped isn't enough) ⇒ add a ceiling: prune or
  gzip the oldest segments past it.
- **Log-role and trail-role retention needs diverge** ⇒ split the diagnostic log
  from the SSE trail into two files with independent retention.

## Implementation plan

Generated by [`/adr-plan`](../../.claude/workflows/adr-plan.js) on 2026-07-09 — a
**stable ranking** of this ADR's work into effort-tagged, dependency-ordered build
phases. Derived from the decisions above, not a decision itself; changes only if
they do. **This is the build order for when the first revisit trigger fires**
(growth bites ⇒ the trail-bounding phase); a roadmap item is created then and
tracks progress — not here. Tags: `effort/hardness · verify?`; ◆ = on the
critical path. 9 slices → 4 build phases (+1 already landed, +1 trigger-gated
further deferral); critical path length 4; adversarial verify on exactly 5
slices; no ultracode.

**Phase 0 — already landed (with ADR-0008)**
- `client-opaque-cursor-conformance` — low/mechanical · no verify — deps: none.
  Decision 2's zero-code-change lock, already honored: `web/src/sse.ts` keeps
  the cursor opaque, echoes it verbatim in `Last-Event-ID`, never advances it
  on a gap, follows a `reset` re-anchor — each pinned by `sse.test.ts`. What
  remains is a standing review gate on future `sse.ts` edits, not a work item;
  its end-to-end `reset` teeth arrive in phase 4.

**Phase 1 — the segmented writer (rotation mechanism, shipped dark)**
- `segmented-writer-rotation` ◆ — med/moderate · **verify (adversarial)** —
  deps: none buildable. Root of the whole graph — every other slice consumes
  the on-disk segment format. ~30 lines under the existing append gate in
  `captainHookActors/Logging.fs` (decision 3): size check, seq scan, atomic
  `File.Move` to `captainHook.<seq>.jsonl`, fresh active file. The hard
  reasoning (rename-vs-concurrent-append) is already done in decision 6 —
  per-call O_APPEND opens + atomic POSIX rename; the verify pass exists
  because the failure modes are silent (lost lines, mis-numbered segments) and
  because the tempting refactor — a held `FileStream` — would invalidate d6's
  race argument and the `WireJsonl` byte-identical premise. Check for it
  explicitly. Build the ownership-gate signature here but **default it to
  never-rotate** (ship dark), so no interim window exists where short-lived
  writers can rotate. The shim's `WireJsonl` path stays byte-for-byte
  unchanged — `WireJsonlTests` goldens stay green untouched.

**Phase 2 — ownership gate + reader index (parallelizable pair, one session)**
- `daemon-owned-rotation-collapsed-edge` — high/hard-reasoning · **verify
  (adversarial)** — deps: segmented-writer-rotation. Decision 6's gate: only
  the daemon singleton rotates (ownership proven by the `DaemonRendezvous`
  flock already held for the process lifetime); the wiring is small but the
  slice carries the punted design decision — what bounds the file when only
  short-lived writers run. Any answer letting a short-lived writer rotate
  reintroduces the forbidden double-rotation race; even correct answers must
  reason about an O_APPEND fd racing the rename (a shim line landing in the
  just-archived segment perturbs per-segment base offsets). Fail-safe
  direction: append-only/unbounded in collapsed mode, or rotate only when the
  singleton lock is takeable; the gate defaults closed.
- `global-offset-cursor-resume` ◆ — high/hard-reasoning · **verify
  (adversarial)** — deps: segmented-writer-rotation. Decision 4: `TrailCursor`
  (`Api/TrailTail.cs`) generalizes from absolute single-file offsets to a
  cumulative global offset — segment index, per-segment base accounting,
  `Last-Event-ID` → segment+local mapping, len<offset disambiguated as
  rotation-vs-truncation via the index, boundary verification in the right
  segment, unreachable-cursor `reset` re-anchoring to earliest-reachable
  rather than genesis-0. A base-offset off-by-one is silent dup/loss behind a
  healthy-looking stream — the exact contract (d2/d4) this exists to honor.
  Disjoint layers (F# gate vs C# cursor): batch into one session, land as
  separate commits per layer.

**Phase 3 — read-path consumers (tailer first if solo)**
- `rotation-following-tailer` ◆ — high/hard-reasoning · **verify
  (adversarial)** — deps: segmented-writer-rotation,
  global-offset-cursor-resume. The ADR's own named hard problem (the reason
  rotation was deferred): replace `TrailCursor`'s vanish/truncate ⇒
  `ResetCursor` heuristic with deterministic rename-follow — distinguish
  rename-rotation from truncate/recreate via file identity under the
  stat-poll model, drain the renamed-away segment to completion, continue
  into the successor **without emitting `reset`**, while two writers race the
  rename and the id stays continuous across rotation and daemon cutover.
  Highest-risk slice: it deletes a fail-safe heuristic in favor of a
  continuity guarantee clients will depend on; every race window is silent
  loss-or-dup behind a healthy heartbeat.
- `tail-n-backfill` — med/moderate · **verify (adversarial)** — deps:
  global-offset-cursor-resume. Decision 5: `?tail=N` on
  `ApiHost.ServeEventsAsync` — parse N, chunked backward-scan for the
  Nth-from-last boundary (via the segment index when N spans a rotation),
  hand the anchor to `TrailSubscription`; `alignForward` already self-heals a
  mid-line anchor. Sharp edges: backward-scan off-by-ones against a
  concurrently-appended file (a half-written trailing line must not count),
  `Last-Event-ID`-beats-`tail` precedence, anchor-timing vs live appends —
  `ServeEventsAsync`'s comments record a live-reproduced bug of exactly this
  class. Shares `TrailSubscription` anchor code with the tailer — solo work
  sequences tailer → tail=N.

**Phase 4 — test teeth + the 0007 hand-off (ride the landing commit)**
- `rotation-resilience-tests` ◆ — med/moderate · shipshape + mutation-check
  (this slice IS verification — don't spend the verify skill on it) — deps:
  all four implementation slices. The trio: rotate-mid-stream exactly-once,
  resume-across-boundary, id-continuity-across-cutover — the first real
  exercise of decision 2's `reset` contract. The trap is vacuous passing (a
  rotation that never straddles a poll with unread bytes goes green while
  "exactly once" ships unpinned): mutation-check each test (break
  continuity, confirm red) and flake-hunt — a flaky race test poisons the
  green-twice ship bar. FakeClock + `PollUntilAsync` house patterns from
  `ApiSseTests.cs`, never real sleeps (invariant 2).
- `adr0007-d5-amendment-flow-doc` — low/mechanical · no verify (shipshape's
  docs-match-code gate is the check) — deps: writer, cursor, tailer. The
  hand-off, discharged: rewrite ADR-0007 d5's id characterization to the
  opaque-cursor/global-offset contract, mark it *amended-by-ADR-0009*; update
  `doc/flow/management-api.md`'s id paragraph, the `TrailCursor` edge table
  (the truncation row's "id space restarts at 0" is the one row whose
  semantics genuinely change), and the ground-truth table. **Must ship in the
  same change that completes rotation-following** — the point where d5's
  "byte offset in the file" wording becomes false.

**Phase 5 — trigger-gated further deferral (do NOT build unless it fires)**
- `archive-ceiling-reaper` — med/moderate · **verify (adversarial)** — deps:
  writer, ownership gate, cursor. Only if the second revisit trigger fires
  (archive-uncapped stops being enough): enumerate segments by seq, sum
  sizes, prune/gzip the oldest past the ceiling, never touch the active file.
  Copy `DaemonHost`'s FakeClock reaper pattern. The one slice that
  irreversibly deletes user data the owner chose to archive — an off-by-one
  in the keep-set or a prune-without-`reset` strands cursors or destroys
  history silently; prune-vs-gzip is a real reachability decision (does
  `Last-Event-ID: 0`'s "earliest still-reachable" include gzipped segments?).
  Until the trigger fires, building this is scope creep.

**Sequencing risks, named:** (1) rotation live before the daemon gate =
cross-process double-rotation race — mitigated by shipping phase 1 dark;
(2) rotation live before the tailer follows it = `reset`-storm on the live
GUI — **do not /deploy a rotation-enabled build before phase 3 lands**, or
keep the threshold effectively infinite; (3) a shim append racing the rename
must land in one segment or the other *and* the segment index must tolerate
it — the collapsed-edge slice's reasoning, don't shortcut it; (4) `Logging.fs`
is F# — any new file is hand-inserted in `<Compile>` order, `Logging.fs`
stays first.

## Ground truth (at acceptance)

**Now:** the id-contract statement *is* this ADR (decision 2), honored by
ADR-0008's fetch-streaming client (id treated as opaque, `reset` tolerated); **no
code or storage change lands with this ADR.** **Deferred homes (the bounding
phase):** the segmented writer + size-check rotation under the append gate in
`captainHookActors/Logging.fs` (daemon-owned; the shim's `WireJsonl` append
unchanged); the segment index + global-offset resume + rotation-following in
`dotnet/captainHook/Api/TrailTail.cs`; `?tail=N` in `ApiHost.ServeEventsAsync`; the
archive reaper in the daemon lifecycle; the **ADR-0007 d5 amendment** + the
global-offset id documented in the SSE/trail flow doc; tests for rotate-mid-stream,
resume-across-boundary, and id-continuity-across-cutover.
