# ADR-0004 — Daemon topology: warm captaind behind a thin shim, one binary

**Status:** Accepted
**Date:** 2026-07-04

## Context

captAInHook ships today as a per-invocation binary: every hook event pays full
process construction — CLR + JIT startup, assembly load, harness registry,
`Dispatcher` construction spawning supervised workers — before a single
handler runs. The first live deployment measured 47.7ms for just the
**in-process dispatch span** of that cold process — 29.5ms of it the echo
handler's first ask, dominated by first-execution JIT a warm process
amortizes — and the host-observed cost is strictly larger: CLR startup,
assembly load, and `Dispatcher` construction all precede the span,
unmeasured. [DESIGN.md](../../DESIGN.md) has named the answer since v0: a
thin `captain-shim` forwarding events to a long-lived, warm `captaind`.

Committing to that topology **fires ADR-0001's first revisit trigger** —
commit to the persistent daemon, re-evaluate Akka.NET — so this ADR re-runs
that evaluation *before* building on either layer. It also owes deliberate
answers to [ADR-0002](0002-handlers-as-supervised-actors.md)'s three
carry-ins, as the roadmap pins them (a/b/c) on the daemon-topology item —
latent in single-shot mode but live in a daemon: (a) a
handler that ignores its cancellation token wedges its worker silently, (b)
asks against an escalated (dead) worker burn the full budget, (c) budget
timeouts of token-honoring handlers count as crashes toward escalation.

One structural fact shapes the shim↔daemon design: the shim is
**per-invocation with no memory between calls**. Any state it needs —
"is a daemon running? which one? is it ready?" — must rendezvous through the
filesystem, and any race between two shims must be settled by the kernel,
not by an in-process coordinator.

## Decision

Build the daemon topology as **one binary, three modes**, with a versioned
Unix-domain-socket rendezvous, mandatory idle-exit, and a reworked
timeout/fault classification in supervision. Stay on the hand-rolled actor
layer (the Akka re-evaluation below).

1. **One binary, three modes.** No separate daemon artifact — the same
   `captainHook` executable, mode chosen by invocation:
   - *daemon* — `captainHook --daemon`: build the registry, dispatcher, and
     supervised workers **once**, then serve dispatches over the socket.
     Environment (`CAPTAINHOOK_*`) is read at daemon start and becomes
     daemon-start configuration, no longer per-hook — a probe or log-path
     change now needs a restart (`--replace` / `doctor`, decision 3) or,
     eventually, a management-API push. Harness specs are the deliberate
     exception: to preserve ADR-0003's edit-a-spec-effective-next-hook
     contract, the daemon `stat`s the `~/.captainHook/harnesses/` override
     directory per dispatch and reloads on change (one syscall; the embedded
     defaults are fixed at build).
   - *shim* — the existing `hook <event>` invocation, unchanged from the
     host's point of view: connect to the socket, forward a framed request
     (dispatchId, event name, harness name, raw stdin bytes), relay the
     framed response, exit. The **shim mints the dispatchId** and the daemon
     adopts it for every log line of that dispatch — one id stitches the
     shim half (connect, fallback, spawn decisions) and the daemon half
     (and any collapsed retry, which reuses it) into one story in the trail. The response carries the effect's **stdout bytes verbatim** — the
     sacred contract crosses the socket byte-identically — plus the human
     trace for the shim's stderr and an exit code (an unknown `--harness`
     still exits 1 with zero stdout bytes, now decided daemon-side).
   - *collapsed* — dispatch in-process exactly as today; forced with
     `--no-daemon` (CI, one-off runs), and the automatic fallback below.

   These are three modes of ONE JIT binary in v1. Decision 7's AOT step
   later peels the shim off into its own thin, dependency-free `captainShim`
   artifact (Native AOT), leaving `captainHook` as the JIT engine behind the
   daemon and collapsed modes — at which point "one binary" becomes two.
2. **Connect-or-fallback: no hook ever waits for warmup — or for silence.**
   The shim tries the socket; on connect failure it does **not** wait — it
   dispatches in-process (collapsed) for *this* event and spawns a detached
   daemon for the *next* one. (Once the shim is decision 7's AOT artifact,
   which carries no dispatcher, this in-process collapse becomes a
   *delegation* — exec the JIT engine in collapsed mode and relay — same
   effect, one process hop.) The warm path also carries a short shim-side
   deadline spanning connect + request + response (a small multiple of the
   warm round-trip): a daemon that accepts but never answers — a wedged
   accept loop, a mid-drain listener — is treated exactly like a transport
   failure instead of hanging the agent host on a UDS backlog that
   `connect()` happily enters. The first hook after boot costs what every
   hook costs today; every subsequent hook rides the warm path. The spawned
   daemon detaches fully: it inherits nothing from the shim's streams (its
   record is the JSONL file; daemon mode defaults the stderr pretty sink
   off), it chdirs out of the shim's working directory rather than pinning
   an arbitrary project dir for its lifetime, and the log path is
   daemon-start configuration like the rest of `CAPTAINHOOK_*` — shim and
   daemon default to the same file so the trail stays in one place.
3. **Rendezvous: a versioned socket, lock-holder binds, listening ⟺ ready.**
   - Socket at `~/.captainHook/captaind-<ver>.sock` (0600), `<ver>` a short
     hash of the binary's **content identity**: the ModuleVersionIds of all
     application assemblies in the app directory. Deliberately *not* the
     informational version — an uncommitted dev rebuild keeps the same
     version string while changing behavior (SourceRevisionId is the HEAD
     sha; dirty state is invisible to it), which would leave a warm daemon
     silently serving stale code whose own traffic keeps resetting its idle
     clock. Content identity makes any rebuild — committed or dirty, host
     or F#-lib assembly — rendezvous on a **fresh socket by construction**:
     shim/daemon version mismatch is unrepresentable, no handshake or compat
     logic exists to get wrong. Superseded daemons stop receiving
     connections and idle-exit; `captainHook --daemon --replace` is the
     explicit escape hatch (tell the incumbent to drain, take over) so no
     workflow ever depends on waiting one out.
   - Spawn races are kernel-settled: daemon startup takes an exclusive lock
     on `captaind-<ver>.lock` (held for its lifetime, released by the OS on
     any death); the holder unlinks any stale socket and binds; a starter
     that cannot take the lock exits 0 — some other daemon won. Shims never
     lock and never unlink anything, and **lock files are never unlinked by
     any component, `doctor` included**: deleting a held lock file lets a
     second daemon lock a fresh inode at the same path and silently break
     mutual exclusion. The files are tiny and version-bounded; they stay.
     The pidfile is written immediately after lock acquisition — *before*
     warmup — and the idle clock runs from process start, so a daemon that
     wedges before ever binding is still reapable by idle-exit and `doctor`.
   - The daemon binds **only after it is fully warm**, so connect-success
     *is* the readiness signal and the hot path needs no probe. This inverts
     pharos-mcp's active readiness probe (its ADR-024), which is necessary
     there because pharos does not control the language server's readiness;
     we control both ends, so "listening ⟺ ready" is ours to guarantee.
   - Framing: 4-byte little-endian length prefix + UTF-8 JSON per frame; one
     connection per dispatch in v1. The collapsed fallback fires **only on
     failures that provably precede delivery** — connect failure, the
     shim-side deadline, an error while *writing* the request frame. A
     failure after the request is fully written is a failed dispatch, not a
     retry: the daemon may already have run non-idempotent `Background`
     effects and computed a verdict, and re-dispatching would run them
     twice. This deliberately narrows pharos's evict-and-retry-once, which
     is safe there only because LSP requests are idempotent reads; hook
     dispatches are not. Dispatch stays **at-most-once** by construction.
4. **Lifecycle: drain, idle-exit, and a doctor.**
   - On SIGTERM: close the listener, drain to a deadline — **in-flight
     dispatches *and* queued or running `Background` effects**, which by
     design outlive their responses in a daemon — then unlink the socket,
     remove the pidfile, exit 0.
   - **Idle-exit is mandatory**, on the monotonic clock: no dispatch for a
     configurable window (default deliberately generous) → graceful exit —
     and a non-empty background queue defers it. The last hook of a session
     scheduling a memory write is precisely when the idle window starts
     ticking; background completion, not just dispatch arrival, holds the
     daemon open.
     pharos can run its pool warm-forever because a parent session bounds
     its lifetime; captaind has **no bounding parent**, so without idle-exit
     a superseded or forgotten daemon lives until reboot. Idle-exit is also
     the version-cutover reaper: old-socket daemons starve and remove
     themselves.
   - A pidfile (`captaind-<ver>.pid`: pid, binary path, started-at) exists
     for **cleanup only**, not discovery. `captainHook doctor` reaps
     leftovers: liveness check *plus* verifying the process matches the
     recorded binary before SIGTERM → grace → SIGKILL — the PID-reuse guard
     from pharos's ADR-030.
5. **Timeout is not fault — the ADR-0002 carry-in verdicts.** Supervision
   currently conflates "budget expired" with "handler crashed"; the daemon
   pulls them apart into three classified outcomes. Two mechanical changes
   make the classification observable at all — today *no component sees both
   signals* (the supervisor hears only crashes; `RunGuarded` deliberately
   collapses cancellation and ask-timeout into one catch, and with ask
   timeout == budget the honoring handler's reply always races the timeout):
   the **ask timeout becomes budget + a small grace**, so a token-honoring
   handler's cancellation reply lands *inside* the ask window and a true
   no-reply timeout is unambiguous; and the dispatcher gains a narrow
   channel to report ask outcomes to the supervisor, which owns the
   counting.
   - **Honored cancellation does not count (carry-in c: change it).** A
     worker crash whose exception is the budget token's
     `OperationCanceledException` still restarts the worker (its mailbox
     died) but does **not** count toward the restart-intensity window. A
     correct-but-slow handler is never escalated by the fault breaker;
     chronic slowness stays visible through `handler.timeout` warns —
     observability, not a breaker, in v1.
   - **Wedge → abandon-and-respawn, and it counts (carry-in a).** A wedge
     is an ask whose message was **received but never answered** within
     budget + grace: the worker marks receipt, so a wedge is distinguishable
     from an ask still *queued* behind a busy sibling dispatch. That queued
     case is backlog, not a defect — it degrades to its fail-mode effect
     and does **not** count against the worker (sustained backlog is the
     head-of-line evidence the router trigger in decision 6 waits for). On
     a true wedge the supervisor abandons the worker — re-runs the factory,
     swaps the `ActorRef` — and the stuck computation is **leaked**: .NET
     cannot preemptively kill user code mid-flight (the honest cost of not
     being on the BEAM). Because each wedge leaks a stuck task, wedges
     **do** count toward the window: a chronic wedger escalates and stops
     being respawned.
   - **Dead workers fast-fail (carry-in b).** Escalation marks the
     `ActorRef` dead; an ask against it returns immediately and `RunGuarded`
     converts to the handler's fail-mode effect in ~0ms, instead of burning
     the full budget per dispatch while dead workers accumulate.

   Concurrent dispatch also becomes real in a daemon: ADR-0002's per-worker
   serialization turns load-bearing — already pinned in-process by
   `ConcurrentDispatches_SerializePerWorker` — and the `Dispatcher`, which
   has only ever run one dispatch per process *in production*, must be
   audited for its dispatch-scoped machinery (budget token source, trace,
   background drain) under sustained concurrency.
6. **Actor layer: stay hand-rolled — the ADR-0001 re-evaluation.** Scored
   against what the daemon actually needs:

   | Daemon need | Hand-rolled layer | Akka.NET |
   | --- | --- | --- |
   | Long-lived supervised workers, restart intensity | Have (ADR-0001/0002) | Have |
   | Per-handler serialization under concurrent dispatch | Have (mailbox) | Have |
   | Fast-fail on dead worker | ~15-line flag (5.b above) | DeathWatch |
   | Kill a wedged, token-ignoring handler | Abandon-and-respawn — custom | **Also custom** — no kill-on-slow built in |
   | Timeout ≠ crash classification | Custom (5 above) | Also custom |
   | Live lifecycle feed for the management API / GUI | Already sourced from the JSONL log pipeline | EventStream (unneeded — feed is decoupled) |
   | Worker pools / routers under head-of-line blocking | Would hand-roll | **Declarative — the one real pull** |
   | Runtime dependencies | 0 | 10 packages, incl. Newtonsoft.Json |

   Every must-have is either built or a small addition to ~90 lines we own;
   the carry-in fixes — the actual hard part — are custom code under either
   runtime. The one genuine Akka pull is routers, which matters only if
   concurrent load shows head-of-line blocking on a busy handler's serial
   mailbox — an empirical question the daemon itself will answer, and the
   narrowed revisit trigger below. ADR-0001's first trigger is hereby
   discharged; its remaining triggers stay live.

   Within the layer, the ADR-0001 split sharpens: `MailboxProcessor` stays
   the worker default (native ask, DU protocol; hook rates never grow its
   unbounded mailbox), and bounded Channels is reserved for genuinely hot or
   streaming paths — the anticipated first being the management API's event
   fan-out, where a slow WebSocket subscriber must meet backpressure rather
   than grow the daemon. A bounded mailbox is explicitly **rejected as the
   wedge fix**: a wedged worker that rejects new work is still a dead
   handler; abandon-and-respawn is the fix regardless of mailbox type.

   Head-of-line blocking — the router pull — is narrower than it looks, and
   the .NET thread pool does **not** address it: per-worker serialization is
   deliberate (private state, one message at a time), so more threads cannot
   unblock a queue that is serial by construction. It bites only a handler
   that is **stateful *and* slow *and* concurrently hit**, and the fix splits
   on state. A **stateless** handler needs no mailbox at all — the actor
   exists solely to serialize state access — so it runs directly on the
   thread pool under a `SemaphoreSlim` concurrency cap and head-of-line
   blocking never arises (zero deps; here the thread pool *is* the answer).
   A **stateful** handler that becomes the bottleneck needs *sharded*
   serialization — route by key so same-key messages still serialize while
   different keys run in parallel — for which the zero-dep tool is a
   mailbox-per-shard over Channels behind the same `Worker` facade, and the
   bought tool is Akka.NET's `ConsistentHashingPool` (the one genuine Akka
   pull above; `System.Threading.Tasks.Dataflow`'s `ActionBlock` is the
   closest ready-made shape but is a package and tell-only). The router
   question is deferred to *observed* evidence not from caution but because
   only a specific stateful handler under real concurrent load reveals which
   shape it needs — and either shape is an internals swap behind
   `Worker<'Req,'Reply>`, not an API change.
7. **Build strategy: one JIT binary now; AOT behind a measurement gate.**
   The daemon's win — setup paid once — is independent of compilation
   strategy, and the collapsed fallback bounds the worst case at today's
   cost. Before any AOT work, instrument the cold-start breakdown — raw
   process start vs. framework construction vs. first-dispatch JIT — with a
   new probe gated like `CAPTAINHOOK_PROBE` (process start-time vs.
   first-managed-code vs. pre/post-`Dispatcher`-construction timestamps; the
   existing trail cannot see anything before its first log line). The AOT
   form, when the shim's residual justifies it, is a **thin AOT shim as its
   own artifact** — not whole-binary AOT. A separate minimal `captainShim`
   (C#, referencing neither the host nor the F# lib — just sockets and bytes)
   Native-AOT-compiles trivially, while the dispatcher, F# actor lib, and
   reflection-based spec parsing stay in the JIT `captainHook` engine (daemon
   + collapsed modes), where warm startup makes AOT moot. Whole-binary AOT is
   rejected: it would force source-generated JSON and F#-AOT-cleanliness for
   a gain the daemon already delivers. One consequence for decision 2 — the
   AOT shim carries no dispatcher, so its no-daemon fallback is a
   **delegation** (exec the JIT engine in collapsed mode and relay its
   output), not an in-process dispatch, which the interim one-binary JIT
   shim still does.

   **Measured (2026-07-04, `CAPTAINHOOK_COLDSTART=1`, `Core/ColdStartProbe.cs`,
   7 steady cold runs).** End-to-end cold start ~216ms, and Release ≈ Debug
   (~216 vs ~205ms) — a run-once process is JIT/startup-bound, so
   optimization level is no lever; only *not running* the managed code
   (daemon) or *not JIT-ing* it (AOT) helps. `procBoot` (CLR + assembly load
   + entry JIT) is ~67ms / **31%**; the managed remainder (harness resolve,
   parse, Dispatcher ctor, first dispatch) is ~149ms / **69%**, nearly all
   first-run JIT the daemon amortizes. So raw process start does **not**
   dominate the total — AOT-the-whole-binary is out, since the daemon
   eliminates the 69% without the F#/reflection-AOT fight — but `procBoot`
   is ~all that is left on the shim's hot path once warm, so AOT stays
   justified for the thin shim as a **second-order** step: daemon first
   (the ~70% win), AOT-the-shim later, gated on whether the residual
   (~40-60ms, lower for a thinner shim that loads less than this full
   binary — needs its own measurement) is felt.

**Pattern lineage.** The lifecycle layer borrows from pharos-mcp's LSP pool —
the sibling project that find-or-spawns long-lived language servers:
connect-or-spawn shape (`src/pharos/lsp/pool.gleam`), readiness as a hard
gate before serving (its ADR-024, inverted here into listening ⟺ ready),
and process-lifecycle hardening — pidfile-for-cleanup, PID-reuse-guarded
reaper, graceful drain (its ADR-030). Equally instructive is what does
**not** transfer: pharos's discovery and spawn-dedup live in the RAM of a
long-lived manager, and its servers die with their parent session. Our
manager (the shim) dies every call and our daemon outlives every parent —
hence the filesystem rendezvous, the kernel-settled lock, the versioned
socket, and the mandatory idle-exit, none of which pharos needed. BEAM gave
pharos in-process supervision for free; at the OS-process boundary pharos
hand-wrote the same plumbing this ADR adopts — no runtime buys it away.

Zero new runtime dependencies, as always: BCL + FSharp.Core only.

## Consequences

### Positive

- **The hook path drops to socket round-trip + handler time.** Framework
  construction is paid once per daemon lifetime instead of per event.
- **Supervision starts earning its keep.** Restarts, escalations, and state
  reset become observable across dispatches instead of dying with the
  process — the supervision view the GUI roadmap item wants to show.
- **The live deployment is protected twice over.** Stdout bytes cross the
  socket verbatim (the golden-wire tests extend to the daemon round-trip),
  and the collapsed fallback means the hook works with no daemon at all.
- **Version skew is unrepresentable.** The socket name carries the binary's
  content identity; any rebuild — release or dirty dev loop — cuts over
  silently and superseded daemons reap themselves.
- **Crash-only by construction.** The lock releases on any death, the next
  lock-holder unlinks stale sockets, `doctor` reaps orphans — no state
  survives a crash that can wedge the next start.

### Negative

- **N1 · Env freezes at daemon start.** `CAPTAINHOOK_PROBE=1` on a single
  hook no longer reaches a warm daemon; per-invocation env knobs move to
  daemon start and eventually the management API (harness-override *files*
  stay hot via decision 1's stat-reload). Live debugging habits change.
- **N2 · Wedge abandonment leaks the stuck task.** Bounded by the wedge breaker
  (a chronic wedger escalates), but a hostile handler still costs leaked
  threads until it does.
- **N3 · Two processes in one trail.** Shim and daemon both emit JSONL; the
  shim-minted `dispatchId` (decision 1) and the shared default log path keep
  a digest able to stitch one dispatch together, but both halves must be
  read.
- **N4 · A second hand-rolled category.** OS-process lifecycle — locks, signals,
  reaping — is fiddly and ours to get right, mitigated by adopting pharos's
  proven ADR-030 contract wholesale rather than designing from scratch.
- **N5 · Collapsed-mode hooks still pay today's full cost** — the first event
  after boot and any pre-delivery-failure retry run at cold-process speed
  (strictly more than the 47.7ms dispatch span), not warm speed.

### Mitigations

Each Negative is a real cost, not a blocker; every one has a remedy — most
deferrable, a few v1:

- **N1 · Env freeze** — harness specs stay hot via decision 1's per-dispatch
  stat-reload; env changes take a restart (`--replace` / `doctor`) until the
  management API can push them, with a whitelisted `CAPTAINHOOK_*` passthrough
  in the request frame as the escape hatch if debug-knob friction proves real.
- **N2 · Leaked wedge tasks** — the wedge breaker caps the leak count; the
  structural fix is process isolation for untrusted or expensive handlers (a
  process takes `SIGKILL`, a thread cannot), converging with the hook-trust
  and real-handler roadmap items — in-process handlers owe a good-cancellation
  contract, anything else runs where the kernel can reclaim it. A daemon past
  a leaked-thread threshold drains and self-restarts (near-free; it is already
  respawnable).
- **N3 · Split trail** — single-writer-per-dispatch (warm: the daemon writes
  and the shim ships its trace over the socket it already holds; collapsed:
  the shim writes locally), plus `O_APPEND` one-syscall line writes
  (POSIX-atomic under `PIPE_BUF`). This also closes a cross-process interleave
  **already latent** whenever two agent sessions log concurrently — the daemon
  only makes it routine.
- **N4 · OS-lifecycle surface** — beyond adopting pharos's hardened ADR-030
  contract wholesale, the lifecycle takes the same injectable seams the actor
  layer has: an injected clock (idle-exit), an injected socket path /
  filesystem (spawn-race, in a temp dir), a drivable lock→bind→listen unit,
  and a concurrency soak test (N shims against cold state → exactly one daemon,
  no double-dispatch, no orphans). Hand-rolled OS plumbing is acceptable only
  because it is made as deterministically testable as the ~90-line actor layer.
- **N5 · Cold collapsed hooks** — accept it: it is the correctness floor.
  SessionStart spawns the daemon and collapse-dispatches itself, so only the
  first hook of a session is cold; a generous idle window keeps it warm across
  gaps; an always-on service install (the install-UX roadmap item) removes even
  the first-hook cost for steady-state users. The dev-loop coldness is
  *correct* — a fresh content-hash after every rebuild guarantees freshly built
  code never talks to a stale daemon, and tests run `--no-daemon` regardless.

## Alternatives considered

| Option | Why not (now) |
| --- | --- |
| Adopt Akka.NET with the daemon | The carry-in fixes are custom code either way; the live feed is already decoupled from actor internals; 10 runtime packages (incl. Newtonsoft.Json) against a zero-dep project whose product *is* its wire bytes. Routers remain the one pull — a narrowed trigger, not a default |
| localhost TCP instead of UDS | Port allocation needs its own discovery; TCP is reachable by any local process where the socket file is filesystem-permissioned (0600); UDS is first-class on Linux/WSL2 and modern Windows |
| Version handshake instead of versioned socket path | Running code must implement compat negotiation correctly under skew; a versioned path makes mismatch structurally impossible and needs zero protocol |
| Bounded mailbox as the wedge fix | Turns "queue grows behind a corpse" into "rejects behind a corpse" — the handler is equally dead; only abandon-and-respawn restores service |
| AOT shim as part of this work | Optimizes before the daemon exists; the breakdown confirmed managed setup (69%), not process start (31%), dominates — so the daemon is the win and the thin AOT `captainShim` (decision 7) is a later second step the daemon *enables*, not part of this work |
| Whole-binary Native AOT (shim + daemon + collapsed as one AOT build) | Forces source-generated JSON and F#-AOT-cleanliness across the dispatcher and actor lib for no gain the warm daemon does not already deliver; the thin `captainShim` gets AOT's fast start by having *neither* F# nor reflection, and the engine stays JIT because it starts once |
| Stay per-invocation (status quo) | Every hook pays cold-process construction plus first-run JIT (the measured dispatch span alone is 47.7ms); supervision value stays latent; the management API and GUI roadmap items need a resident process regardless |

## Revisit triggers

- **Head-of-line blocking observed** — a busy handler's serial mailbox
  delaying concurrent dispatches → the router/pool question: re-open Akka.NET
  (the spike is still one directory away) against a hand-rolled pool behind
  the same `Worker` facade.
- **The shim's residual (`procBoot`) is felt in practice** — the daemon has
  landed and its ~40-60ms per-hook floor matters → build the thin
  `captainShim` AOT artifact (decision 7). Measured at ~31% of cold start;
  second-order behind the daemon, and a *new project* of its own.
- **The management API lands** — persistent or multiplexed connections may
  replace one-connection-per-dispatch; the length-prefixed framing already
  permits it.
- **Supervision bugs bill arrives** (ADR-0001's standing trigger, still
  live) — if our hand-rolled layer's correctness tax turns real, buy the
  framework.
- **A remote or multi-user transport need appears** — UDS-only and
  same-user 0600 stop being sufficient; revisit transport and auth together.

## Implementation plan

Generated by [`/adr-plan`](../../.claude/workflows/adr-plan.js) on 2026-07-05 — a
**stable ranking** of this ADR's work into effort-tagged, dependency-ordered build
phases. It is *derived from* the decisions above, not a decision itself, and changes
only if they do. **Progress (what's landed) is tracked on the [roadmap](../roadmap.md)
daemon item, not here**, so this snapshot never drifts. Tags: `effort/hardness ·
verify?`; ◆ = on the critical path.

**Phase 1 — independent foundations** (no deps; parallelize)
- `three-mode-dispatch` — low/mechanical · no adversarial verify
- `frame-protocol` — med/moderate · **verify** (byte-identical payload — likely base64 / binary section)
- `content-identity-versioned-socket` ◆ — med/moderate · no adversarial verify (deterministic: two builds differ, same build agrees)
- `timeout-fault-classification` — high/hard-reasoning · **verify** — *orthogonal to the socket track (Worker.fs / Supervision.fs / RunGuarded); parallel-track it.* The one slice where an ultracode-style split is debatable — treat as high-effort + verify, not multi-agent.

**Phase 2 — first dependents** (two independent lanes)
- `lock-bind-rendezvous` ◆ — high/hard-reasoning · **verify** — deps: content-identity. *Critical bottleneck — front-load it.*
- `shim-forward-or-fallback` — high/hard-reasoning · **verify** — deps: three-mode, frame
- `detached-daemon-spawn` — med/moderate · **verify** — deps: three-mode

**Phase 3 — warm engine + delivery boundary**
- `daemon-serve-loop` ◆ — med/moderate · no adversarial verify — deps: three-mode, frame, lock-bind. *Build the long-lived Background-effect queue HERE, once (the Dispatcher.cs:129 refactor).*
- `at-most-once-fallback-guard` — high/hard-reasoning · **verify** — deps: shim, frame

**Phase 4 — lifecycle + maintenance** (batch; all hang off the serve loop)
- `sigterm-drain` ◆ — high/hard-reasoning · **verify** — deps: daemon-serve-loop, lock-bind
- `harness-hot-reload` — med/moderate · **verify** — deps: daemon-serve-loop
- `doctor-reaper` — med/moderate · **verify** — deps: content-identity, daemon-serve-loop

**Phase 5 — idle reaper**
- `mandatory-idle-exit` ◆ — high/hard-reasoning · **verify** — deps: daemon-serve-loop, sigterm-drain. *Shares the "non-empty background queue defers exit" bookkeeping with `sigterm-drain` — they must agree.*

**Phase 6 — terminal integration gate**
- `concurrency-audit-and-soak` ◆ — high/hard-reasoning · **verify** — deps: the full daemon. *Run genuinely last; extends the golden-wire tests across the round trip.*

**Critical path:** content-identity → lock-bind-rendezvous → daemon-serve-loop →
sigterm-drain → mandatory-idle-exit → concurrency-audit-and-soak.

**Sequencing traps.** (1) Design `frame-protocol` with the at-most-once boundary (the
frame-write-completion point) in mind, or you retrofit the codec two phases later.
(2) `sigterm-drain` + `mandatory-idle-exit` share the background-queue bookkeeping —
co-design them; divergence silently drops a session's memory-write. (3)
`concurrency-audit-and-soak`'s formal deps clear at phase 3, but its soak is only
meaningful once drain + idle-exit exist. Only `three-mode-dispatch`,
`daemon-serve-loop`, and `content-identity-versioned-socket` skip the adversarial
verify pass; every other slice warrants one. No slice warrants ultracode.
