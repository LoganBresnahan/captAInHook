# Roadmap

Living document — what to build and in what order. Decisions get ADRs
(`doc/adr/`), mechanics get flow docs (`doc/flow/`); this file only orders the
work. Check items off in the commit that lands them; reorder freely.

**The product vision this points at:** a runtime for managing custom
hooks/skills for AI agents — browse, one-click install (writes
`~/.claude/settings.json` / `.claude/skills/`), configure, and *watch them
run live*. The framework underneath is what exists today.

## Now

- [x] **1. Converge the C# dispatcher and F# actor layer** — handlers become
  supervised actors: dispatch = `Ask` with the latency budget as the ask
  timeout; fail-open/fail-closed maps onto supervision (fail-open ≈ restart +
  degrade, fail-closed ≈ escalate + deny). The moment the two halves become
  one architecture. Touches `Dispatcher.cs` + `Supervision.fs`; demands a
  flow-doc update and new tests (shipshape will insist).
  Landed as `Worker<'Req,'Reply>` (ADR-0002); escalated-worker fast-fail deferred to the daemon-topology item.
- [x] **2. First live deployment** — wire the echo handler into the real
  `~/.claude/settings.json` (UserPromptSubmit) and watch an actual Claude
  Code session flow through the JSONL trail. Dogfood before features.
  Observed live 2026-07-04: a real session's dispatch in the trail (47.7ms
  end-to-end) with the injection visible in-context.
- [x] **3. Declarative harness registry** — no hardcoded harness-string
  branches: a `HarnessSpec` registry (embedded defaults + validated user
  overrides in `~/.captainHook/harnesses/`) declares each harness's request
  fields, response adapter (a CLOSED coded set — data selects, code
  implements; config never becomes a template language), per-event effect
  capabilities, and install target (the data the management API + GUI will
  consume). Pattern lineage: pharos `config.gleam` (defaults/load/cached
  layering, tool gating) and moby `models/registry.ts` (capability registry
  + validated custom entries). `claude-code` stays the default; a
  `generic-json` adapter proves N>1.
- [ ] **12. Thin AOT `captainShim`** — ADR-0004 decision 7's gate tripped
  (2026-07-06): a PreToolUse-class before-tools hook puts the shim's measured
  ~85ms procBoot+JIT residual on **every tool call** — per-action, on the
  agent's critical path — so the reserved thin-AOT-shim step is scheduled.
  Design resolved in the decision-7 amendment: two new projects
  (`captainHookWire` leaf lib + `captainShim` AOT exe; arrows `captainShim →
  captainHookWire ← captainHook → captainHookActors`); identity math
  unchanged (the native shim is invisible to it by construction — sound, not
  a hole); publish-time wire-stamp skew guard (skew fails safe to collapse);
  one JSONL schema across two emitters, pinned by a golden byte-equality
  test; source-generated wire JSON; delegation fallback to the co-located
  engine. Success bar: warm hook **p50 ≤ 40ms** end-to-end (from 99ms) and
  the per-tool-call tax subjectively gone. Build order: ADR-0004
  § Implementation plan, amendment plan (6 slices → 3 phases; critical path
  wire-lib-extraction → captainshim-aot-artifact → deploy-two-artifacts).
  Tick slices here as they land.
  Slices landed: `wire-lib-extraction` (2026-07-06; pure move — five files
  `git mv`'d into the new leaf lib, wire log seam bound to `Actors.Log` by
  engine + tests, suite green twice, zero behavior change);
  `wire-json-source-gen` (2026-07-06; `WireJson` context for the two frame
  records, `IsAotCompatible` analyzers on — wire lib builds warning-free);
  `wire-jsonl-logger` (2026-07-06; `WireJsonl.Render` = the shim's emitter of
  the trail's one schema, pinned byte-identical to F# `ToJson()` by 17 golden
  cross-emitter tests — unicode/control escaping, durMs rounding, omit-null,
  nested data — plus mirrored path resolution and the O_APPEND appender).
  Phase A complete.

## Next

- [x] **4. Daemon topology** — long-lived `captaind` + thin per-event shim
  (DESIGN.md's split). ⚠ Fires ADR-0001's revisit trigger: re-evaluate
  Akka.NET vs the hand-rolled layer *before* building on either → an ADR.
  Design recorded in ADR-0004 (verdict: stay hand-rolled; carry-ins
  answered) — the gate is discharged, this item is now implementation.
  Build order: ADR-0004 § Implementation plan (14 slices → 6 phases;
  critical path content-identity → lock-bind → serve-loop → drain →
  idle-exit). Tick progress here as slices land.
  Slices landed: `three-mode-dispatch`, `frame-protocol`,
  `content-identity-versioned-socket`, `timeout-fault-classification`
  (2026-07-05) — Phase 1 complete; `lock-bind-rendezvous`,
  `shim-forward-or-fallback`, `detached-daemon-spawn` (2026-07-05) —
  Phase 2 complete; `daemon-serve-loop` (2026-07-05; dispatchId adoption
  verified end-to-end); `at-most-once-fallback-guard` (2026-07-05; chaos
  audit: 30 hooks under random daemon SIGKILL — zero double dispatches) —
  Phase 3 complete; `sigterm-drain` (2026-07-06; real SIGTERM landed
  mid-dispatch — in-flight hook still answered, drained in 62ms);
  `harness-hot-reload` (2026-07-06; in-place edit — same inode, dir mtime
  unmoved — served on the next hook through a live daemon); `doctor-reaper`
  (2026-07-06; swept 3 stale identities live while sparing the healthy
  deployed daemon from a dev-tree run) — Phase 4 complete;
  `mandatory-idle-exit` (2026-07-06; live daemon survived refreshing hooks,
  self-reaped 92ms past its window, respawned on the next hook) — Phase 5
  complete; `concurrency-audit-and-soak` (2026-07-06; 200 concurrent
  dispatches in-suite — seq values a perfect 1..200 permutation, background
  queue exact, escalation mid-load survived; 180 live hooks against the
  deployed daemon — 100% warm, zero double dispatches, p50 99ms round-trip
  / 13.4ms daemon-side, RSS asymptoting not leaking) — Phase 6 complete.
  **ADR-0004's implementation plan is fully landed**; dogfooding live.
  Carry-out for the AOT-shim gate (ADR-0004 decision 7): the measured warm
  residual is ~85ms of shim procBoot+JIT per hook — **gate tripped
  2026-07-06 → item 12**.
  ⚠ Until `mandatory-idle-exit` lands, a spawned daemon lives until killed —
  SIGTERM now drains gracefully; kill -9 stays safe.
  Dogfooding: `/deploy` ships the apphost build to the live hooks and
  verifies the warm path (mechanics: doc/flow/live-deployment.md).
  **Carry-ins from ADR-0002 — DISCHARGED** by the
  `timeout-fault-classification` slice (ADR-0004 decision 5): (a) a wedged,
  token-ignoring handler is abandoned-and-respawned and counts toward
  escalation; (b) asks against an escalated worker fail fast (~0ms); (c)
  honored cancellations restart without counting — changed deliberately.
  Pinned by ClassificationTests.cs; mechanics in
  doc/flow/actor-supervision.md.
- [ ] **5. Management API** — HTTP + WebSocket on the daemon: inventory of
  installed hooks/skills, install/uninstall/enable/disable operations, and a
  live event stream sourced from the structured log pipeline (dispatchId
  correlation = per-dispatch traces for free).
- [ ] **6. GUI v1: browser UI** — localhost web app served by the daemon.
  Catalog + one-click install, live dispatch traces, supervision view
  (restarts/escalations as they happen). Web-first per the GUI direction
  below; on WSL2 this is the *best* UX, not a fallback. Lands WITH a
  Playwright harness (Microsoft.Playwright, same xunit suite): the DOM +
  accessibility tree is the agent-legible surface — semantic locators and
  auto-waiting beat TUI screen-scraping for the agentic dev loop.

## Later

- [ ] **7. Desktop shell** — wrap the same web assets in Photino (native
  window, .NET runtime in-process) once the browser UI proves the workflows.
  ADR to record Photino vs Tauri vs staying browser-only.
- [ ] **8. TUI** — geex-style terminal UI against the same API, for product
  reasons (SSH-side admin, terminal-native users) — not as the agent's
  feedback instrument: the feedback pyramid is API assertions (bulk) →
  Playwright over the web UI (visual) → TUI capture only to test the TUI.
- [ ] **9. Real handlers** — the payloads the framework exists for: retriever
  (forced-RAG on UserPromptSubmit), policy gate (PreToolUse write approval),
  memory (SessionStart/Stop, cavemem-shaped).
- [ ] **10. Hook trust model** — installing a hook = installing arbitrary code
  that runs on every prompt. The install UX must show exactly what will
  execute, from where, before touching settings.
- [ ] **11. N-runtime harness** — port the core spec to Node and BEAM
  (DESIGN.md's comparison thesis — still the point of the exercise).

## Parking lot

- **Mobile** — a responsive browser UI over LAN already answers the likely
  need; no app until a real use case demands one.
- **Community registry** — discovery/versioning for shared hooks & skills.
- **shipshape as a Stop-hook** — the repo verifying itself with the very
  mechanism it demonstrates.
- **Packaging** — single-file publish for the JIT engine (the shim's
  Native AOT half promoted to item 12).

## GUI direction (current leaning — becomes an ADR when work starts)

**One API, three faces, in this order:**

1. **Browser UI first.** The runtime becomes a daemon anyway (item 4); serving
   localhost HTML/JS reuses web skills we already have, needs zero packaging,
   and is first-class from WSL2 (Windows browser → localhost), where desktop
   GUI apps under WSLg are second-class.
2. **Photino when a desktop feel is wanted.** Same web assets in a native
   window with the .NET runtime *in-process* — UI to actors is a method call.
   Tauri's host is Rust, which would force the .NET runtime into a sidecar
   process behind an IPC boundary — at which point plain localhost-in-browser
   already does the same job with less machinery. Tauri is right when the
   core is Rust; ours is .NET.
3. **TUI as the dev-loop instrument** (item 8), driving the same API.

The structured log stream (JSONL + correlation ids) is the GUI's live data
feed — the observability layer was built GUI-ready before the GUI existed.
