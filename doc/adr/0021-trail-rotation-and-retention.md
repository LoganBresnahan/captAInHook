# ADR-0021 — Trail rotation and a retention cap: build ADR-0009's segments, bounded at ~30 days

**Status:** Accepted (2026-10-19) after review: R1 (a durable segment
high-water mark) and notes N1–N3 folded in. All three slices built; slice 3
(rotation on) awaits the owner before merge.
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

1. **Segments, named as ADR-0009 d3 designed, numbered from a durable
   high-water mark.** The active file stays `captainHook.jsonl`. Rotation
   renames it to `captainHook.<seq>.jsonl` and the next append creates a fresh
   active file. `<seq>` comes from `logs/.trail-seq`, the last segment number
   ever used, never from the segments that happen to exist: retention (rule 3)
   can delete the newest renamed segment on a quiet machine, and a number
   derived from survivors would then go backwards, moving ids already issued
   and reusing a name a cursor remembers. A rotation computes
   `seq = max(.trail-seq, highest existing segment) + 1`, renames the active
   file, then writes `seq` to `.trail-seq` (temp file + rename), all under the
   rotate lock (rule 2). A crash between the rename and the write is healed by
   the `max` on the next rotation; a lost `.trail-seq` is healed the same way
   as long as one segment survives, and otherwise restarts at 1 only when no
   segment and no cursor-visible history remain. The active file's own
   number is `max(.trail-seq, highest existing segment) + 1`. Every writer
   keeps its per-call `O_APPEND` open (ADR-0004 N3), so a line racing the
   rename lands whole in one file or the other, never lost and never split.
   No writer holds the file open, which is what makes rename-rotation safe.
   *(Slice 1 finding: "never lost" was not yet true. Both writers created a
   missing file and reopened it exactly once, and a rotation landing between
   the create and the reopen dropped the line: 1–3 lines in 20,000 across
   ~50 racing rotations. Both now loop the create-and-reopen (up to 8 turns,
   each on a fresh file). The race test drives both emitters and was
   mutation-checked: one retry fails it in about 5 runs in 12, the loop in 0
   of 45.)*

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
   not finished (ADR-0009 d8's guard). If that attempt fails too (the
   start-time migration already logged "retried next start"), **pruning is
   skipped until `shell-history.0.jsonl` exists**, with a `trail.pruneHeld`
   warning at most once a day; rotation still proceeds, since it deletes
   nothing. It never reads or touches
   `shell-history*.jsonl` otherwise: command history is kept forever, and is
   not the trail. **The keeper must not assume the trail is free of
   `tool.shell` lines once the copy exists:** a 0.1.3 daemon draining during
   the deploy handoff (or a rolled-back 0.1.3) can still write one after it.
   Those lines are lost to history by ADR-0009 d8's accepted edge, and pruning
   them later loses nothing that history ever had.

5. **The SSE id becomes `(seq − 1) × 2^40 + localOffset` (`TrailId`).** ADR-0009 d4 designed
   a cumulative byte count across segments. That needs every earlier
   segment's final size, and rule 1's race makes that size unfinal: a line
   that lands in a just-renamed segment shifts every later base, so ids
   already handed out would silently move. An encoded `(segment, offset)`
   pair is immune to that and still a single monotonic integer, so d2's
   contract holds with no client change:
   - the active file's segment number is one more than the high-water mark
     (rule 1), so a line keeps its id when its file is renamed;
   - 2^40 bytes (1 TiB) per segment and up to 2^13 segments stay below 2^53,
     so the id survives any client that parses it as a JSON number. Because
     `.trail-seq` only grows, the segment count is the number of rotations
     ever, not the number kept: about 250 years at 16 MiB and today's rate,
     or about 16 if `CAPTAINHOOK_TRAIL_SEGMENT_MB=1` were left on for real.
     Past 2^13 the keeper refuses to rotate and logs `trail.seqExhausted` once
     a day; the trail then grows unbounded, but every id stays valid. The GUI
     does not: it keeps the id as a string and echoes it verbatim
     (`web/src/sse.ts`, `mailStream.ts`);
   - the first file is segment 1, so `seq − 1` makes its ids exactly today's
     byte offsets: an id issued before the upgrade means what it meant, and
     resumes into what is now `captainHook.1.jsonl` after the first rotation.
     *(As built in slice 2: the draft said `seq × 2^40` with "segment 0" for
     today's ids, but the first rotation names the original file segment 1;
     `seq − 1` makes the two agree with no special case.)*

6. **The tailer follows a rotation without a `reset`.** `TrailCursor` keeps
   its stat-poll and reopen-per-poll model. When `captainHook.<its seq>.jsonl`
   appears, the file it was reading has been renamed: it drains that segment
   from its local offset to the end, waits one grace period (1 s) and drains
   again for lines that raced the rename, then moves to the next segment at
   offset 0. Several rotations can happen between two polls (a burst, or the
   smoke's tiny segments), so the cursor walks `seq, seq+1, …` up to the
   active file, draining each, and never assumes a single step. Only a cursor whose segment has been deleted gets a `reset`
   (d2), re-anchored **forward**, at the next segment that exists (or the
   active file).
   Truncation of the active file without a rename (a person running
   `: > captainHook.jsonl`) keeps today's `reset`. `MailWatcher` uses the
   same cursor and inherits all of this.
   *(Slice 2's adversarial verify broke the first draft five ways, each now
   fixed and pinned by a test in `TrailRotationCursorTests`:
   A. re-anchoring a deleted segment at the OLDEST one turned a gap in the
   numbers (a kept future-mtime segment, a stubborn or hand-deleted one) into
   an endless reset-and-replay loop; it re-anchors forward now.
   B. a rename landing between the cursor's "does the segment name exist?"
   and "what is the active number?" read as a deleted segment and reset; it
   re-checks the name first.
   C. a cached "this is the active file" outlived a rename the cursor never
   saw (renamed and deleted between two polls) and read the next file under
   the old number, losing lines silently later; the active number is now
   resolved every poll and re-checked after the open.
   D. out of descriptors (or an unreadable logs dir), the cursor spun at
   More=true or threw out of MailWatcher's pump; those are quiet polls now.
   E. (older than rotation) a "from now" start landing mid-line read as a
   replaced file and replayed the whole active file; every subscription and
   the watcher now align forward.
   Accepted, not fixed: each renamed segment costs one grace (1 s) to walk,
   even when later ones exist; at one rotation a minute it never shows.)*

7. **The other readers.** `TrailEventId` becomes the id of the active file's
   current end (rule 5's encoding), so the GUI's snapshot-then-stream join
   stays exact. `MailDeliveryFold` reads its last 4 MiB across the newest
   segments as needed, so a rotation mid-window does not empty the Mail
   view. `MigrateLegacy` already reads `<stem>.<seq>.jsonl` ascending, then
   the active file.

8. **Deferred again:** `GET /events?tail=N` (ADR-0009 d5). It is a read
   convenience, independent of bounding the disk, and lands best after rule
   6 has proven itself. ADR-0007 d5 is amended, as ADR-0009's hand-off
   requires, from "byte offset in the file" to rule 5's encoding, in the
   change that turns rotation on (slice 3): that is when its wording becomes
   false. Until then every id is still a byte offset in the one file.

## Consequences

- The trail is bounded: about 30 days, roughly 45 MB at today's rate, plus
  one segment of slack.
- **The upgrade day deletes nothing.** On the first daemon start the
  migration runs first (ADR-0009 d8), then the first tick renames the whole
  existing trail (90 MB on the maintainer's machine) into segment 1 in one
  rotation. A rename keeps the file's mtime, which is its last append —
  moments ago — so the first prune keeps it, and it ages out 30 days later.
  Measured in a sandbox with the release build and the defaults on a
  106 MB synthetic trail: 6 `tool.shell` lines copied, one `trail.rotate`
  (106,090,502 bytes → `captainHook.1.jsonl`), no `trail.prune`, `history`
  showing the copied commands, and no second rotation on the next hook. (A
  machine whose trail was last written more than 30 days ago loses it at
  that first tick: that is the policy, working.) A machine that wants the old behaviour sets
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

1. `TrailKeeper`: rotation + the lock + `.trail-seq` + retention + the d8
   guard, shipped with the cursor unchanged but rotation **off by default**,
   so no window exists where the stream resets on every rotation. Tests: race
   the rename with concurrent O_APPEND writers (no line lost or split); prune
   by mtime; future mtime kept; the lock serializes two keepers; retention 0
   keeps all; **prune every segment, rotate again, and the numbers keep
   rising** (R1); a failed migration holds pruning but not rotation.
2. Rule 5 + rule 6 in `TrailCursor` (and so `MailWatcher`): segment-aware
   resume, rename-follow with the grace drain, `reset` only for a deleted
   segment. Adversarial verify. Mutation-checked tests: rotate mid-stream
   exactly once, resume across a boundary, a line racing the rename is
   delivered once, several rotations between two polls each drained in order.
3. Rule 7's readers; the ADR-0007 d5 amendment and the management-api flow
   doc; rotation turned **on** (30 days, 16 MiB); CLAUDE.md's env-var list;
   a release smoke step that forces a tiny segment size and proves rotation,
   a resumed stream across it, and pruning.

## Ground truth

| what | where |
|---|---|
| keeper (rotate, lock, `.trail-seq`, prune, d8 guard) | `Core/TrailKeeper.cs`; `TrailKeeperTests` |
| configuration (`TrailKeeperOptions.FromEnv`: retention days, segment MiB, tick) | `Core/TrailKeeper.cs`; passed by `Program.cs`; started by `DaemonHost` (`trailKeeper`: waits for the migration, first tick at start, then per tick until the drain) |
| the segment model readers share (`Segments`, `HighWater`, `ActiveSeq`, `SegmentPath`) | `Core/TrailKeeper.cs`; `ShellTrail.Files` reads through it |
| writers' create-and-reopen loop (rule 1's finding) | `captainHookWire/PosixTrail.cs` (`OpenAttempts`), `captainHookActors/Logging.fs` (`PosixTrail.append`) |
| segment-aware cursor (slice 2): resolve by name each poll, re-resolve when a rotation races the open, drain + `RenameGrace` + step, walk many rotations, reset only for a deleted or future segment | `Api/TrailTail.cs` (`TrailCursor`: `Seq`, `Offset`, `Id`); `TrailRotationCursorTests` (mutation-checked: no grace, no grace marker, ids without seq, jump-to-active, never follow a rename — each fails a test) |
| the id | `TrailId` (`Encode`, `Decode`, `End`) in `Core/TrailKeeper.cs`; used by `TrailSubscription`, `MailWatcher`, `ApiReadModel` (`trailEventId`) |
| readers | `Api/ApiReadModel.cs` (`trailEventId` = `TrailId.End`), `Api/MailDeliveryFold.cs` (the 4 MiB window across the newest segments; `Fold_ReadsAcrossARotation`), `Core/MailWatcher.cs` (a `TrailCursor`, aligned), `Core/ShellTrail.cs` (`Files`, the migration) |
| release smoke | `release/smoke.sh` step 11: a 4 KiB segment size and a 200 ms tick rotate the trail twice, `.trail-seq` kept, a segment aged past the retention pruned and only it, command history untouched |
| the ADR-0007 d5 hand-off | discharged in slice 3: ADR-0007 d5 amended, `doc/flow/management-api.md` (id paragraph, `TrailCursor` edge table) |
| events | `trail.rotate`, `trail.rotateError`, `trail.prune`, `trail.pruneHeld`, `trail.seqExhausted`, `trail.tickError` (src `trail`) |
| high-water mark | `logs/.trail-seq` (written under `logs/.rotate.lock`) |
| env | `CAPTAINHOOK_TRAIL_RETENTION_DAYS`, `CAPTAINHOOK_TRAIL_SEGMENT_MB` |
