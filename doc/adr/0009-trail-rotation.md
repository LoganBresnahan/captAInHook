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
