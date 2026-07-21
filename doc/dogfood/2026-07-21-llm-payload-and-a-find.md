# Dogfood report — 2026-07-21 (later) — the LLM payload, and the find it paid for

Phase 4 of the live-payload dogfood: `orient-brief.sh`, the first LLM-backed
payload — the "arbitrarily intelligent subsystem behind a deterministic
seam" half of the thesis, on real multi-second budgets. It shipped working,
but the valuable output is the engine finding it surfaced on its first live
dispatch. This is what the dogfood loop is *for*.

## What went live

`orient-brief.sh` — SessionStart, oneshot, fail-open, budget 30000ms.
Compiles repo activity (git log, roadmap Now, the session-pulse ledger —
one payload feeding on another's output), asks haiku via `claude -p` for a
≤3-line "since last time" brief, injects it. A 30-min cache makes repeat
session starts instant (~9ms); the model is consulted at most twice an
hour. Layered degradation: any failure → noop, and git-orient's instant
one-liner still lands.

Verified live: cold 16.7s `handler ok` with an *accurate* brief injected
through the full daemon path; cached repeat 9.1ms; fail-open cancel
exercised for real (see below) with the session proceeding on git-orient
alone.

## The find (graduated to roadmap item 17)

First live attempt, budget 20000ms, standalone timing 11.8s. It timed out —
and the trail told a three-dispatch story worth the whole exercise:

```
 outer dispatch ──► worker(SessionStart/orient-brief) ──► claude -p
                        ▲ serialized mailbox                  │ starts an INNER session
                        │                                     ▼
                        └────── inner session-start hook ── queues behind
                                the very dispatch waiting on it: SELF-BLOCK
```

1. **Payload reentrancy self-blocks.** The inner `claude -p` session fired
   SessionStart; that ask queued behind the outer dispatch on the same
   serialized worker. The inner session's boot blocked ~20s on its own hook
   (until its ask timed out `backlogged`), which pushed the outer call past
   its budget — a deadlock-by-serialization that only budget timeouts
   unwind. Standalone testing couldn't see it: the handler wasn't
   registered there, so the recursion had nothing to dispatch into.
2. **A worker restart strands queued asks.** The outer budget cancel
   restarted the worker (`actor.restart, counted=false`, gen 2). A third
   dispatch, enqueued 2ms before the restart, was never answered by the old
   generation's drained mailbox — the dispatcher burned its full 20s before
   classifying `backlogged`, for a script that never even spawned
   (`exec.spawn` absent). Fail-fast on restart-drained asks would turn that
   20s stall into milliseconds. → roadmap item 17(a).
3. **What worked exactly as designed**: TERM at budget → `claude` ignored
   it → SIGKILL after the 2s grace (`exec.kill how=kill`); fail-open merged
   the surviving injects; the session started anyway. The rails held —
   that's the thesis doing its job under a genuinely misbehaving payload.

**Fixes applied to the payload**: `--setting-sources ""` on the inner call
(no settings → no hooks → no recursion, and 4× faster: 2.3s vs 9.4s
standalone), a lock-file backstop with a TERM trap, budget 20s → 30s
(daemon-env CLI boot is slower than a shell's: 16.7s observed vs 11.8s).

## Observations

- The kill discipline's TERM→grace→KILL was not theoretical: a real CLI
  ignored TERM on its first live encounter.
- `handler.timeout` classifications (`cancelled` vs `backlogged`) made the
  three-dispatch story reconstructable from the trail alone — the
  correlation design paid off.
- Editing the payload file needed no re-registration: oneshot spawns read
  fresh bytes per dispatch. Config (budget) changes rode If-Match PUTs.

## Watchlist

- Cold-brief cost on a real session start is ~5–17s once per 30min window.
  If it annoys, shrink by pre-warming (a Stop-event refresher keeping the
  cache always-fresh) rather than cutting the budget.
- The stranded-ask fix (item 17a) should get a regression test shaped
  exactly like this: cancel-restart with an ask enqueued pre-swap.
