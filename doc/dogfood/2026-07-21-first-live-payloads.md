# Dogfood report — 2026-07-21 — first live exec payloads (phases 1–3)

The exec-handler machinery (ADR-0010) had only ever served tests and demo
scripts. Today four real payloads went live on the maintainer's own hooks,
registered through the ADR-0011 API write path, on the freshly deployed
single-file build (identity `24642e962306`, commit `3612b92`).

## What went live

| payload | event | mode | failMode | what it does |
|---|---|---|---|---|
| `git-orient.sh` | SessionStart | oneshot | open | injects a one-line git bearing (branch @ sha, dirty count) once per session (sessionId marker on tmpfs) |
| `deploy-guard.sh` | PreToolUse | oneshot | open | `Decide(ask)` when a Bash command pairs a mutation verb with `~/.captainHook/bin` — the only-/deploy-touches-the-live-tree rule, enforced by the tree's own hook |
| `session-pulse.sh` | Stop | oneshot | open | appends `{ts, session, cwd, head, dirty}` to `~/.captainHook/logs/session-pulse.jsonl` per turn end |
| `doc-pointer.sh` | PreToolUse | **resident** | open | warm child; indexes flow-doc ground-truth paths at boot, injects a keep-docs-in-sync reminder when an Edit/Write targets an indexed file, deduped per (session, file) in process memory |

Wiring: `session-start` and `stop` added to `~/.claude/settings.json`
(captainShim blocks appended beside cavemem's; backup taken). git-orient
moved from UserPromptSubmit to SessionStart in the same change — kills its
per-prompt spawn entirely.

## Evidence

- **Registration rode the API, both shapes**: first PUT (absent file, no
  If-Match), second PUT (If-Match `"834ae128…"` → 200, new etag) — the
  composed read-modify-write from ADR-0011 d5, live.
- **Reconcile**: `handlers.reload added=3 removed=1 kept=1` — the event-list
  change on git-orient is remove+add by design (event is part of worker id);
  deploy-guard kept its identity untouched.
- **Resident lifecycle**: `exec.ready` 14.6ms after spawn (index built
  before the handshake), child record `child-457386.json` with raw-/proc
  start-time proof, `childState=ready` in the supervision read model,
  `pgid==sid==pid` (setsid group — clean kill target).
- **Warm state is real state**: the same Edit dispatched twice injected
  once, then answered noop from the child's in-memory dedup — cross-dispatch
  memory only resident mode can hold.
- **Latency on real traffic**: deploy-guard ~3–4ms, doc-pointer ~6–8ms
  warm, git-orient ~11ms once per session, pulse ~14ms per turn end — all
  concurrent under fan-out, so PreToolUse costs max(4, 8), not the sum.
  Budgets are 1000–1500ms; headroom is ~100×.
- **Self-referential proof**: the session doing this work watched its own
  tool calls flow through deploy-guard (allow, ~4ms) in the trail before
  the synthetic tests even ran, and its follow-up prompt received
  git-orient's inject through the real hook path.

## Observations / friction

- The PUT response body is the fresh GET-shaped read model incl. etag —
  convenient for composed flows (no second GET needed). Worth keeping.
- `expected[]`'s `registered:false → true` flip across a fired hook is the
  honest per-dispatch-reconcile story the GUI's "pending" state tells; it
  reads exactly right from curl too.
- The demo `echo` coded handler still rides SessionStart / UserPromptSubmit
  / PostToolUse and now shares SessionStart with git-orient. Harmless, but
  the injected `captAInHook: <event> seen @ <ts>` line is now noise beside
  real payloads — candidate for retirement or a probe-style opt-in.
- Raw-line sed/grep envelope parsing (house demo style) is fine for these
  payloads but is a known cliff (escaped quotes in values); a real payload
  in a JSON-capable language wouldn't have it.

## Watchlist

- deploy-guard is **fail-open** for the proving phase; flip to
  `failMode:"closed"` after a quiet week in the trail (it answers `ask`,
  never `deny`, so the worst false positive is one confirmation).
- `session-pulse.jsonl` grows per turn end — same unbounded-trail posture
  as ADR-0009; revisit when size bites.
- doc-pointer's index builds once per child lifetime — flow-doc edits after
  spawn aren't indexed until the child recycles (daemon restart, eviction,
  or a handlers.json fingerprint change). Acceptable staleness; noting it.
- Watch for `exec.notReady` / restart events on doc-pointer under real
  concurrent PreToolUse traffic.

## Next

- Soak. Then flip deploy-guard to fail-closed.
- Possible phase 4: an LLM-backed payload (the other half of the thesis) —
  a resident child that calls a model, exercising real multi-second budgets.
