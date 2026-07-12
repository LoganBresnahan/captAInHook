# ADR-0010 — Exec handlers: user processes as payloads

**Status:** Accepted
**Date:** 2026-07-12

## Context

Roadmap item 9 ("Real handlers") planned the payloads — retriever, memory — as
framework code: C# handlers inside the engine, which in turn forced item 15's
`HandlerContext` capability API (how does in-process user logic reach the
world?) and collided with the zero-new-runtime-deps invariant (retrieval
infrastructure in BCL-only C#).

**Owner reframe (2026-07-12): captAInHook is, at its core, a process manager
that responds to lifecycle hooks and gates work under policy.** DESIGN.md's
thesis already says the payload half is *"unbounded in what a handler may do —
any I/O, any service, any agent"* and bounded only in how it affects the loop.
The honest completion of that thesis is that the unbounded half belongs to the
**user's own process, in any language** — a Ruby script, a Node service, a
shell one-liner — and the framework's job is the bounded half: *when* it runs,
*within what budget*, *with what failure mode*, and *which Effect it may
express*.

What the framework offers over a raw `settings.json` hook command (the
differentiation, stated once): N handlers per event with deterministic merge
semantics (first deny wins, inject concat), per-handler budgets with
fail-open/fail-closed, supervised restart and escalation, per-handler policy
on/off without touching harness config (ADR-0006), hot reload, a correlated
JSONL trail with a live GUI (ADR-0007/0008), the harness-normalized event
shape (write a handler once, ride any harness a `HarnessSpec` describes —
ADR-0003), and — the thing raw hooks structurally cannot do — **kept-warm
resident payload processes** behind a 16ms native shim.

The latency lesson is already paid for: ADR-0004 decision 7 tripped its gate
because ~85ms of procBoot+JIT *per tool call* was intolerable, and item 12
spent two projects getting the hook path to 16ms warm. A cold interpreter per
dispatch re-imposes that tax at 5–100× (Ruby ~80ms, Python ~50ms, Node ~70ms,
a Rails boot is seconds). Before-tools events fire on *every tool call*,
serially, on the agent's critical path — so payload processes must be able to
stay **warm**. Managing warm child processes is a solved problem in-house:
**pharos-mcp** (`~/pharos-mcp`) is exactly this manager for LSP servers, and
its lessons are imported wholesale (§ Pattern lineage).

## Decision

1. **One new coded handler, `ExecHandler`; everything else stays closed.**
   The `Effect` set does not grow. The `IHandler` surface does not open to
   plugins. `ExecHandler` is a single engine-coded adapter that runs a
   *configured command* as the payload: normalized event in, one strict-parsed
   Effect out, supervised like any other worker. In-process C# handlers remain
   for first-party, latency-critical, fail-closed payloads — the ADR-0005 tool
   gate stays in-process and stays deferred, unchanged. House pattern intact:
   data (the handler config) selects; code (the adapter) enforces.

2. **The child wire contract is the hook protocol turned inward.** captAInHook
   receives a hook and answers an effect; downward it fires the same shape at
   the child and receives an effect back — a hook multiplexer, dogfooding its
   own protocol on both faces.
   - **stdin**: one JSON envelope —
     `{"v":1,"dispatchId":"…","event":{"type":"…","sessionId":…,"cwd":…,"payload":{…}}}`
     — the engine's *normalized* event, not the raw harness JSON.
   - **stdout**: the answer is the **first complete JSON object**, from a
     CLOSED grammar mirroring the effect set:
     `{"effect":"inject","text":…}` · `{"effect":"decide","verdict":"allow|deny|ask","reason":…}`
     · `{"effect":"replace","text":…}` · `{"effect":"noop"}`.
     Parsing is strict, `Frame.Decode` house rules: unknown `effect`, unknown
     fields, trailing garbage, or a second object ⇒ handler failure ⇒ the
     handler's fail mode. **Exit 0 with empty stdout ⇒ `Noop`** (decided, not
     guessed — the "nothing to say" case every hook ecosystem has).
   - **`Effect.Background` does not cross the wire** (it's a `Func` — it
     can't). Fire-and-forget = answer fast, keep working: a oneshot child may
     **reply-then-linger** — the answer counts when parsed; the child's
     remaining lifetime is its own, reaped asynchronously, exit code recorded
     in the trail, the dispatch never blocked on child exit. (The Worker's
     reply-then-crash semantics, extended to processes.)
   - **stderr** is the child's diagnostic channel → captured to the trail
     (`exec.stderr`, truncated), never stdout. A nonzero exit *before* an
     answer ⇒ failure ⇒ fail mode.

3. **Two execution modes, declared in config: `oneshot` and `resident`.**
   - **`oneshot`**: spawn per dispatch. Right for session-edge events
     (SessionStart/Stop/SessionEnd, UserPromptSubmit at a stretch).
   - **`resident`**: the daemon holds the child **warm** — spawned once,
     spoken to over lock-step JSONL on stdin/stdout (one envelope line in,
     one answer line out; the answer MUST echo `dispatchId`, and a mismatch is
     a protocol failure ⇒ kill + restart + fail mode). Mandatory in practice
     for before-tools events. Children are spawned **eagerly at registration**
     (daemon start / config reload), not lazily on first dispatch — spawn cost
     lives off the hot path entirely; pharos's M14 regression (slow spawns
     serializing the pool) is designed out rather than mitigated. A resident
     child signals **readiness** by emitting one `{"ready":1}` line within
     `readinessTimeoutMs`; until ready (or once failed), dispatches to it take
     the handler's fail mode with a loud trail line — pharos ADR-024's lesson:
     readiness must be demonstrable before callers are released.
   - **Collapsed mode degrades `resident` to `oneshot` semantics** (spawn,
     serve the one dispatch, terminate at drain). A collapsed run must never
     orphan a resident child. Cross-dispatch child state is therefore a
     daemon-only property — same contract as handler state under restart.

4. **Registration is a file: `~/.captainHook/handlers.json`** — the fourth
   user-facing contract (after harness overrides, the hook protocol, and
   `dispatch.json`). Fields per entry: `name`, `command`, `args[]`,
   `events[]` (canonicalized at parse — ADR-0006's kebab silent-grant lesson),
   `mode`, `failMode`, `budgetMs`, `readinessTimeoutMs` (resident),
   `env{}`/`passEnv[]` (decision 5), `cwd` (default: the event's `cwd` when
   present, else the runtime home — the payload usually wants the project).
   Semantics follow the established tri-state + strictness split:
   - **Absent ⇒ zero exec handlers** — today's behavior, zero cost to users
     who never asked (ADR-0005 d2's rhyme).
   - **Invalid entry ⇒ skipped loudly, valid siblings register** (ADR-0003's
     warn-and-skip precedent — registration config, not authorization).
   - **Whole file malformed ⇒ zero exec handlers + a loud per-dispatch trail
     line** (`handlers.malformed`), stateless, no keep-last-good.
   - **Hot reload** by per-dispatch stat-gate (the `ReloadingPolicy` /
     `ReloadingHarnessRegistry` pattern): an edit is effective next hook; a
     changed/removed resident entry drains its old child and (re)spawns under
     a bumped generation; a malformed reload poisons-AND-advances to
     zero-exec-handlers, loudly.
   - `dispatch.json` (ADR-0006) governs exec handlers **by name, for free** —
     per-event/per-handler policy needs no new mechanism.

5. **Child environment is a stripped allowlist, from day one.** Children do
   NOT inherit the daemon's environment. Default allowlist: `PATH`, `HOME`,
   `USER`, `SHELL`, `LANG`, `LC_*`, `TZ`, `TMPDIR` — plus the entry's explicit
   `env` map (literal adds) and `passEnv` list (named passthroughs from the
   daemon env). Nothing else crosses. Rationale: the daemon's environment can
   carry agent credentials and API keys; resident children are long-lived; and
   item 10's community handlers must never inherit ambient secrets a user
   didn't name. **Sandboxing (namespaces, seccomp, cgroup caps, network deny)
   is deliberately NOT built now** — it is the enforcement half of item 15,
   deferred to item 10's trust-model trigger (first third-party/community
   handler support), with ADR-0004 N2's process-isolation note as the
   backstop. When built, the OS mechanics land in platform.md (the lane rule:
   the environment imposes → platform.md).

6. **Supervision maps onto the existing machinery; one engine seam is new.**
   - **Per-handler budgets, unbounded by design (decision 9).** `budgetMs`
     may *exceed* the dispatcher default — the Dispatcher's single budget
     becomes the *default* handler budget, and each handler's ask window is
     its own effective budget (+ grace). A dispatch completes when every
     participating handler has answered or hit its *own* window. (A small
     Dispatcher change: today one `budgetCts` spans the dispatch; a
     min-clamp inside `ExecHandler` could only shorten, never extend.)
   - A child that overruns or ignores the deadline is **wedged**: kill
     (SIGTERM → grace → SIGKILL, process-group so grandchildren die too) +
     respawn + count toward escalation — ADR-0004 d5's classification, with
     "abandon" upgraded to "kill" because a process, unlike a task, *can* be
     killed.
   - Crash / pipe EOF ⇒ worker failure ⇒ supervised restart; the factory
     `On(...)` idiom yields a fresh handler instance and therefore a fresh
     child. **New engine need:** the worker restart path must *dispose* the
     replaced handler instance (kill its child) — `IHandler` has no teardown
     seam today; `ExecHandler`'s child must not outlive its generation. Same
     at daemon drain: resident children are terminated in the drain sequence,
     and idle-exit implies children die with the daemon (respawned eagerly on
     the next daemon spawn).
   - Escalated workers fast-fail per fail mode, unchanged.

7. **Latency doctrine is loud guidance, not a hard gate.** Config may put any
   mode on any event; a `oneshot` entry registered on a before-tools
   (decide-capable) event draws a loud registration warning in the trail
   (`handlers.slowShape`) naming the measured cost. The capability gate and
   budgets make it survivable; the trail makes it visible; the user stays
   sovereign.

8. **Observability is named up front** so the GUI shows payloads running
   (the reason item 9 waited for item 6): `exec.spawn`, `exec.ready`,
   `exec.answered`, `exec.stderr`, `exec.exit`, `exec.kill`,
   `exec.protocolError` (src `exec`); `handlers.reload`, `handlers.malformed`,
   `handlers.entrySkipped`, `handlers.slowShape` (src `handlers`). The
   `/api/v1/handlers` read model and the GUI supervision panel grow
   **expected-vs-registered** (config entries vs live registrations) and the
   child state (`Spawning | Ready | Failed` — pharos's `LspState` shape).

9. **Budgets are the user's; the harness's patience is the harness's.**
   The user may wait as long as they want: `budgetMs` has a sensible
   per-event-class default (tight for before-tools, looser for session
   edges) and **no upper bound**. Three layers had to line up:
   - **Daemon-side**: per-handler ask windows (decision 6) — the only place
     a budget is *enforced*.
   - **Shim-side**: the shim's post-delivery `ResponseTimeout` (5s today) is
     **removed** — after the at-most-once delivery commit, the shim waits for
     the answer or socket EOF, with no timer of its own. The shim stays
     policy-free (aot-boundary rule 1): it needs no knowledge of budgets,
     because the daemon *always* answers within the participating handlers'
     bounded windows, and a daemon crash closes the socket (EOF ⇒ fail per
     fail-mode norms, trailed). A wedged daemon is backstopped by the harness
     timeout, not by a shim guess. The 250ms pre-delivery timeout (rendezvous
     fail-fast → collapsed fallback) stands.
   - **Harness-side — not ours to control.** The harness kills the hook
     command at *its* timeout (Claude Code: 60s default, per-command
     override in `settings.json`). We do NOT auto-edit harness config at
     runtime: the harness's timeout contract can change arbitrarily, and
     `handlers.json` hot-reloads while `settings.json` edits happen only at
     install — auto-sync would drift. Instead the `HarnessSpec` MAY carry an
     informational `hookTimeoutHint`, and registration warns loudly
     (`handlers.budgetBeyondHarness`) when a handler's budget exceeds the
     harness's known patience. If the harness abandons the shim mid-wait,
     the daemon still completes the dispatch — the work happens, the
     undeliverable answer is trailed; only the *effect* is lost, and the
     trail says exactly that.

**Not decided here** (each a trigger, not scope creep): streaming or
multi-effect answers; a child→daemon reverse channel (pharos's
server-request handlers — the obvious future for LLM-backed children);
per-handler parallelism >1 (the worker mailbox serializes per handler today,
exactly like a pharos proc; cross-handler parallelism is already free);
sandbox technology choice (decision 5's deferral); a shareable
handler-manifest format (the community-registry parking-lot item, now
plausible as script + manifest); eager-vs-lazy spawn as a config knob
(eager is implied by `resident`, never applies to `oneshot` — the mode field
carries it); a **pre-warmed oneshot pool** (the zygote/pre-fork pattern:
N standby children each awaiting one envelope on stdin — warm-start latency
for plain read-stdin-once scripts without the resident protocol; sharp edges:
pre-event boot means precomputed state can be stale, pool sizing, more
kill-discipline surface — trigger: a real oneshot payload whose boot cost
hurts on an event too rare to justify resident); **install-time harness-timeout
stamping** (the install block already writes the hook command; writing the
harness's per-command timeout to match a handler's declared budget is
plausible *at install only* — item 10 territory, and only ever as an explicit
user-approved write, per decision 9's no-auto-sync rule); Windows/macOS
child-process specifics; children that speak MCP.

## Consequences

### Positive

- **Item 9 shrinks from "build retrieval infrastructure" to "build one seam,
  prove it with scripts."** Retriever and memory land as demo scripts in the
  repo (any language), not engine code. The tool gate stays ADR-0005.
- **Item 15 mostly dissolves as an API problem.** There is no in-process user
  code to hand capabilities to; the process boundary is the isolation seam,
  and what remains of item 15 is decision 5's env policy now + sandboxing at
  item 10's trigger.
- The zero-new-runtime-deps invariant stops fighting the payloads, forever.
- **Item 11 halves**: payloads are N-language by construction; only the core
  spec (shim/daemon/dispatch) remains as the comparison thesis.
- **Item 10 gets concrete**: installing a hook = installing a command line +
  config entry the GUI can show verbatim before touching anything.
- The GUI's value proposition sharpens: watch *your own* processes being
  spawned, warmed, budgeted, restarted, and merged, live.

### Negative

- **N1 · A fourth contract to keep stable.** The child envelope/answer
  grammar is user-facing wire; it gets a `v` field from day one and the same
  golden-test discipline as the frame codec.
- **N2 · Warn-and-skip registration can un-gate a fail-closed exec entry.**
  A typo'd entry that *would have been* a deny-gate is skipped loudly rather
  than failing the world closed — the deliberate opposite of ADR-0005 d3
  (that file *is* authorization; this file is registration). Mitigations:
  per-dispatch loud trail, expected-vs-registered in API/GUI. Revisit trigger:
  if a real user is burned, add a per-file `strict: true` knob that refuses
  the whole file on any invalid entry.
- **N3 · Resident children are a new leak class.** The daemon now owns OS
  processes that must die at drain, at idle-exit, at worker restart, and at
  collapsed-mode exit — the kill discipline (decision 6) and its tests are
  load-bearing, and `doctor` learns to report orphans.
- **N4 · The latency doctrine is advisory.** A user CAN put a Rails oneshot
  on PreToolUse and suffer; decision 7 makes it loud, not impossible.
- **N5 · Collapsed-mode degrade changes resident semantics.** Cross-dispatch
  child state exists only under a daemon — acceptable (identical to handler
  state under restart), documented.
- **N6 · Long budgets hold real resources.** An in-flight long dispatch
  defers idle-exit (`active > 0`), head-of-lines *that handler's* worker
  (the per-worker mailbox serializes — a 10-minute ask queues the next
  event's ask to the same handler behind it; inherent to the
  one-worker-per-handler model, now reachable), and interacts with the drain
  deadline — a deploy/cutover drain that fires mid-long-dispatch must either
  wait out the remaining budget or cut it loudly; decided at implementation,
  named here.

## Pattern lineage — pharos-mcp (`~/pharos-mcp`)

The in-house prior art: a kept-warm process manager for LSP servers. Direct
imports:

| pharos-mcp | here |
| --- | --- |
| `lsp/pool.gleam` kept-warm cache `(lang, ws, server) → Proc`; cold-start paid once per key per session | resident children keyed by handler entry; spawn cost paid once per daemon lifetime |
| M14: spawns run off-actor with an inflight waitlist so slow cold-starts never serialize the pool | designed out entirely — eager spawn at registration keeps spawning off the dispatch path |
| ADR-013 "structure-by-supervision, communication-by-monitoring": monitor → DOWN → evict → respawn transparently | worker restart + fresh-child-per-generation; kill-on-replace |
| ADR-024 readiness: `Spawning → Probing → Ready / Failed`, demonstrable answer before waiters release; per-server init budgets | `{"ready":1}` line within `readinessTimeoutMs`; warming/failed ⇒ fail mode, loud; per-entry budgets |
| `lsp/proc.gleam`: one actor owns one child's byte stream; mailbox serializes; cross-child parallelism free | one Worker owns one child; identical concurrency model |
| layering `port → framing → lifecycle → proc` | `Process/pipes → JSONL lines → envelope/answer → ExecHandler` (line-framed, not Content-Length — house JSONL precedent) |
| `port.gleam` `BinaryNotFound` (ADR-018): bare command resolved on PATH with a clean error | same nicety at registration validation |
| typed exit errors (`PortClosed(exit_status)`, `Timeout`, `ActorCallPanic`) → evict-and-retry at the tool layer | typed child faults → ADR-0004 d5 classification (cancelled / wedged / backlogged) |

Not imported: Content-Length framing (LSP's requirement, not ours),
full-env child inheritance (pharos trusts its LSP binaries; decision 5
deliberately does not), lazy spawn-on-first-use (the M14 lesson argues for
eager).

## Ground truth

Backfilled when the implementation lands (ADR-0008 precedent): files,
symbols, trail events, and tests get their table here; mechanics get
`doc/flow/exec-handlers.md`.
