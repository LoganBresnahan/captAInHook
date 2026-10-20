# Dogfood report — 2026-10-20 — the trail rotates

Roadmap item 32 / [ADR-0021](../adr/0021-trail-rotation-and-retention.md),
slice 3 live. v0.1.5 (`21331a0`, identity `abeef46bb7c2`) went out through the
release path: release run 39429212142 — four builds `smoke: PASS`, publish,
seven post-release smoke runners PASS — then `captainhook self update`
0.1.4 → 0.1.5, keeping 0.1.4 as the rollback and pruning 0.1.3.

Everything below is the live tree, read after the update:
`~/.captainHook/logs/`.

## The first tick

The new daemon ticked its keeper once the shell-history migration had
settled, and the 66-day trail became segment 1 in one rename:

```
2026-10-20T13:23:21.496Z  trail.rotate  rotated 91390280 bytes to captainHook.1.jsonl  {"seq":1}
```

| file | size | lines | note |
|---|---|---|---|
| `captainHook.1.jsonl` | 91 390 280 | 371 024 | first line 2026-08-15T20:30Z; mtime 2026-10-20 13:23Z |
| `captainHook.jsonl` | 33 KB | 150 | opens with the `trail.rotate` line itself |
| `.trail-seq` | `1` | | the last number USED — the active file is seq 2 (`ActiveSeq` = mark + 1) |
| `.rotate.lock` | 0 | | created on first tick, never unlinked |

The `trail.rotate` line is the first line of the NEW active file: the rename
landed before the keeper logged, and the log append created the fresh file —
the writers' create-on-append path doing exactly its job, on its first live
use. An `exec.answered` stamped the same millisecond follows it, whole.

**No prune**, and none was due: segment 1's mtime is its newest line, minutes
old. Zero `trail.prune`, `trail.pruneHeld`, `trail.rotateError`.

## What was not touched

- `shell-history.0.jsonl` — 113 lines, mtime 2026-10-19, unchanged (the
  0.1.3 window's copy from the 0.1.4 migration).
- `shell-history.jsonl` — still growing (142 lines); `captainHook history`
  reads it and shows this session's commands from minutes after the rotation.
- `session-pulse.jsonl` — a payload's file, unmatched by the segment glob.

## The five stray lines

The verifier's five `trail.rotate` lines from 2026-10-20T04:13Z (written into
the live trail during slice 3's adversarial pass, left in place by decision)
are now inside segment 1. They name segments `1`–`3` with 3–28-byte sizes
that never existed here; read them as the fixture they were. They leave with
segment 1.

## Watchlist

- **The first prune**: segment 1 ages past 30 days at 2026-11-19T13:23Z; the
  first tick after that should log one `trail.prune` (≈91 MB) and leave the
  active file and the shell history alone.
- **The second rotation**: the old trail grew ~1.4 MB/day on average, so 16 MiB
  is on the order of two weeks — the first rotation of a file this build
  wrote from byte 0, and the first with a live SSE cursor open across it
  (slice 2's `RenameGrace` path on real traffic).
- `.trail-seq` should read `2` after that, and never go backwards.
