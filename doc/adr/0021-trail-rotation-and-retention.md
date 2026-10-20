# ADR-0021 — Trail rotation and a retention cap: build ADR-0009's segments, bounded at ~30 days

**Status:** Proposed (2026-10-19), for review before any code
**Builds on:** [ADR-0009](0009-trail-rotation.md) (the opaque-cursor contract
d2, the segmented-archive design d3–d6, d8's command-history split),
[ADR-0007](0007-management-api.md) d5 (SSE ids), [ADR-0004](0004-daemon-topology.md)
N3 (two O_APPEND writers, one file), [ADR-0016](0016-mailbox-bus.md) d13 (the
trail is days-to-weeks telemetry).
**Supersedes:** ADR-0009 d3's "archived, kept, uncapped" retention, and d4's
cumulative-bytes cursor encoding (the d2 contract it served is unchanged).

## Context

The trail (`~/.captainHook/logs/captainHook.jsonl`) is one append-only file
that nothing bounds: 90 MB on the maintainer's machine after ~65 days, about
1.4 MB/day. ADR-0009 locked the resume-id contract in July (d2: opaque,
monotonic, may answer `reset`) and designed segmentation (d3–d6), deferring
the build "until growth bites". Three things changed:

- **The owner set the policy** (2026-10-19): segment the trail and cap it at
  about 30 days, configurable. ADR-0009 had chosen archive-forever; the owner
  now wants a bounded trail, and moved the one thing that must live forever
  (agent command history) out of it first (ADR-0009 d8, v0.1.4).
- **Readers multiplied.** Four things read the trail today: the SSE tailer
  (`TrailCursor`, the GUI's live stream), `MailWatcher` (its own
  `TrailCursor`), `ApiReadModel` (`TrailEventId` = the trail's length, and
  `MailDeliveryFold`, which scans the last 4 MiB for `mail.deliver` lines),
  and the one-time `ShellTrail.MigrateLegacy`.
- **A bounded window is now acceptable to every reader.** The tailer and the
  watcher read forward from "now"; the delivery fold already reads only the
  last 4 MiB; command history no longer lives here.

## Decision

1. **Segments, named as ADR-0009 d3 designed.** The active file stays
   `captainHook.jsonl`. Rotation renames it to `captainHook.<seq>.jsonl`, where
   `<seq>` is one more than the highest existing segment (1 on the first
   rotation), and the next append creates a fresh active file. Every writer
   keeps its per-call `O_APPEND` open (ADR-0004 N3), so a line racing the
   rename lands whole in one file or the other, never lost and never split.
   No writer holds the file open, which is what makes rename-rotation safe.

2. **Only the daemon rotates; nobody else ever renames or deletes** (ADR-0009
   d6). A `TrailKeeper` in the daemon runs on a monotonic timer (every 60 s,
   invariant 2) and rotates when the active file passes the segment size
   (default 16 MiB). The daemon already holds the version lock, but two
   daemons overlap during a deploy handoff, so the rotation itself is
   serialized by a separate, never-unlinked lock file `logs/.rotate.lock`
   (`FileShare.None`, released by the kernel on any death; ADR-0004 d3's lock
   rule). A daemon that cannot take it skips this tick. **Collapsed-only
   machines never rotate**: the trail grows as it did before 0.1.5 until a
   daemon runs. That is the fail-safe direction ADR-0009's plan named, and a
   machine with no daemon is a machine with almost no trail traffic.

3. **Retention: segments older than the cap are deleted; default 30 days.**
   A segment's age is its mtime (its newest line). A segment whose mtime is
   older than `now - retention` is deleted at the keeper's tick; the active
   file is never deleted. This compares file timestamps with the wall clock,
   which is the one place it is right: the question is how old the persisted
   data is, not how long an interval took. A segment with an mtime in the
   future (a clock step) is kept. Configured by
   `CAPTAINHOOK_TRAIL_RETENTION_DAYS` (read at daemon start like
   `CAPTAINHOOK_IDLE_MS`; `0` means never delete, ADR-0009's archive-forever)
   and `CAPTAINHOOK_TRAIL_SEGMENT_MB` (the rotation size; also the test knob).
   Pruning logs `trail.prune` (segments, bytes); rotation logs `trail.rotate`.

4. **The keeper never deletes the 0.1.3 window's commands.** Before its first
   prune, the keeper ensures `shell-history.0.jsonl` exists, running
   `ShellTrail.MigrateLegacy` synchronously if the daemon-start migration has
   not finished (ADR-0009 d8's guard). It never reads or touches
   `shell-history*.jsonl` otherwise: command history is kept forever, and is
   not the trail. **The keeper must not assume the trail is free of
   `tool.shell` lines once the copy exists:** a 0.1.3 daemon draining during
   the deploy handoff (or a rolled-back 0.1.3) can still write one after it.
   Those lines are lost to history by ADR-0009 d8's accepted edge, and pruning
   them later loses nothing that history ever had.

5. **The SSE id becomes `seq × 2^40 + localOffset`.** ADR-0009 d4 designed
   a cumulative byte count across segments. That needs every earlier
   segment's final size, and rule 1's race makes that size unfinal: a line
   that lands in a just-renamed segment shifts every later base, so ids
   already handed out would silently move. An encoded `(segment, offset)`
   pair is immune to that and still a single monotonic integer, so d2's
   contract holds with no client change:
   - the active file's segment number is one more than the highest renamed
     segment, so a line keeps its id when its file is renamed;
   - 2^40 bytes (1 TiB) per segment and up to 2^13 segments stay below 2^53,
     so the id survives any client that parses it as a JSON number; the GUI
     does not: it keeps the id as a string and echoes it verbatim
     (`web/src/sse.ts`, `mailStream.ts`);
   - today's ids (offsets in a never-rotated file, segment 0) mean exactly
     what they did, so a client resuming across the upgrade continues.

6. **The tailer follows a rotation without a `reset`.** `TrailCursor` keeps
   its stat-poll and reopen-per-poll model. When `captainHook.<its seq>.jsonl`
   appears, the file it was reading has been renamed: it drains that segment
   from its local offset to the end, waits one grace period (1 s) and drains
   again for lines that raced the rename, then moves to the next segment at
   offset 0. Only a cursor whose segment has been deleted gets a `reset`
   (d2: "earliest still-reachable"), re-anchored at the oldest segment.
   Truncation of the active file without a rename (a person running
   `: > captainHook.jsonl`) keeps today's `reset`. `MailWatcher` uses the
   same cursor and inherits all of this.

7. **The other readers.** `TrailEventId` becomes the id of the active file's
   current end (rule 5's encoding), so the GUI's snapshot-then-stream join
   stays exact. `MailDeliveryFold` reads its last 4 MiB across the newest
   segments as needed, so a rotation mid-window does not empty the Mail
   view. `MigrateLegacy` already reads `<stem>.<seq>.jsonl` ascending, then
   the active file.

8. **Deferred again:** `GET /events?tail=N` (ADR-0009 d5). It is a read
   convenience, independent of bounding the disk, and lands best after rule
   6 has proven itself. ADR-0007 d5 is amended in the same change that ships
   rule 6, as ADR-0009's hand-off requires: from "byte offset in the file" to
   rule 5's encoding.

## Consequences

- The trail is bounded: about 30 days, roughly 45 MB at today's rate, plus
  one segment of slack. A machine that wants the old behaviour sets
  `CAPTAINHOOK_TRAIL_RETENTION_DAYS=0`.
- Operational history older than the cap is gone: `exec.stderr`, dispatch
  timings, daemon lifecycles. That is the owner's chosen trade, and the
  maintainer's field reports (`doc/dogfood/`) are where findings that matter
  are written down anyway. `mail/` and `shell-history*.jsonl` are not the
  trail and are unaffected.
- **The hard part is rule 6.** It replaces a fail-safe heuristic (`reset` on
  shrink) with a continuity guarantee, and every race in it fails silently
  (a lost or doubled line behind a healthy heartbeat). It needs the
  adversarial verify ADR-0009's plan named, and rotation tests that are
  mutation-checked so they cannot pass vacuously.
- A collapsed-only machine stays unbounded. Accepted (rule 2).
- Two new env vars, documented in CLAUDE.md's list when the code lands.

## Alternatives considered

- **Cumulative-bytes ids (ADR-0009 d4 as written).** Rejected for rule 5's
  reason: a racing append into a renamed segment moves every later id.
- **Rotate by day instead of size.** Simpler ages, but a burst (a payload
  spewing stderr) makes one enormous file; size keeps segments uniform, and
  mtime already gives each segment its age.
- **Let the shim rotate when it sees a big file.** Rejected (ADR-0009 d6):
  short-lived writers racing to rotate is the double-rotation hazard.
- **gzip old segments instead of deleting.** It saves disk without bounding
  it, and every reader would need to read `.gz`. Not needed once
  command history left the trail; can be added behind the cap later.
- **Keep archive-forever (ADR-0009 d3 unchanged).** The owner chose a cap.

## Build order (for review; slices, not commits)

1. `TrailKeeper`: rotation + the lock + retention + the d8 guard, shipped
   with the cursor unchanged but rotation **off by default**, so no window
   exists where the stream resets on every rotation. Tests: race the rename
   with concurrent O_APPEND writers (no line lost or split); prune by mtime;
   future mtime kept; the lock serializes two keepers; retention 0 keeps all.
2. Rule 5 + rule 6 in `TrailCursor` (and so `MailWatcher`): segment-aware
   resume, rename-follow with the grace drain, `reset` only for a deleted
   segment. Adversarial verify. Mutation-checked tests: rotate mid-stream
   exactly once, resume across a boundary, a line racing the rename is
   delivered once.
3. Rule 7's readers; the ADR-0007 d5 amendment and the management-api flow
   doc; rotation turned **on** (30 days, 16 MiB); CLAUDE.md's env-var list;
   a release smoke step that forces a tiny segment size and proves rotation,
   a resumed stream across it, and pruning.

## Ground truth (to fill when built)

| what | where |
|---|---|
| keeper (rotate, lock, prune, d8 guard) | `Core/TrailKeeper.cs` (new), started by `DaemonHost` |
| segment-aware cursor | `Api/TrailTail.cs` (`TrailCursor`) |
| readers | `Api/ApiReadModel.cs` (`TrailEventId`), `Api/MailDeliveryFold.cs`, `Core/MailWatcher.cs`, `Core/ShellTrail.cs` |
| events | `trail.rotate`, `trail.prune` (src `trail`) |
| env | `CAPTAINHOOK_TRAIL_RETENTION_DAYS`, `CAPTAINHOOK_TRAIL_SEGMENT_MB` |
