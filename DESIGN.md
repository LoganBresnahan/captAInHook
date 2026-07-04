# captAInHook — Core Design (v0)

> Lifecycle hooks as the composition primitive: splice deterministic *or*
> intelligent subsystems into an agent's loop at guaranteed seams.

## Thesis

A hook is arbitrary code that runs at a fixed point in an agent's loop. Its
**invocation** is deterministic (it always runs); its **payload** can be
anything from a static HTTP call to a full LLM-backed agent. captAInHook is a
framework for authoring these hooks well — enforcing *when* a subsystem runs,
*within what budget*, and *with what failure mode*.

## Core abstraction

```
lifecycle event  ──►  handler(s)  ──►  bounded effect on the loop
```

- **Unbounded** in what a handler may *do* — any I/O, any service, any agent.
- **Bounded** in how it may *affect the loop* — each event type permits only a
  specific set of effects.

## Topology

```
[agent surface: Claude Code / Agent SDK]
        │  fires hook event  (JSON on stdin)
        ▼
   captain-shim  ── tiny, fast start; forwards event ──►  captaind (daemon)
        ▲                                                    │ dispatch → handlers
        └──────────── returns effect (JSON on stdout) ◄──────┘
```

**Single-binary mode** = shim and daemon collapsed into one process
(cavemem-style). Same contracts either way; only the transport differs
(in-process vs. localhost). The split exists to keep the hot-path shim fast
while the daemon stays warm — and because it *is* the thesis: a thin
deterministic seam calling an arbitrarily intelligent subsystem.

## Lifecycle events & effect contracts

| Event            | Handler receives            | Allowed effects                                   |
| ---------------- | --------------------------- | ------------------------------------------------- |
| SessionStart     | session id, cwd, source     | `Inject(context)`                                 |
| UserPromptSubmit | prompt, session             | `Inject(context)`                                 |
| PreToolUse       | tool name, tool input       | `Decide(allow/deny/ask)`, `ModifyInput`, `Inject` |
| PostToolUse      | tool name, input, result    | `Inject(context)`, `ReplaceOutput`                |
| Stop             | session                     | `SideEffect` only                                 |
| SessionEnd       | session                     | `SideEffect` only                                 |

(Also available from the host: `Notification`, `PreCompact`, `SubagentStop`,
`PermissionRequest`. Confirmed live in this repo's owner's Claude Code config:
SessionStart, UserPromptSubmit, PostToolUse, Stop, SessionEnd.)

## Handler interface (runtime-agnostic)

```
handle(event, ctx) -> Effect

event : { type, sessionId, cwd, payload }     // payload shape varies per event
ctx   : { deadline, config, services, logger, actor? }

Effect (only the subset valid for the event is honored):
  Inject(text)            |  Decide(verdict, reason)
  ModifyInput(patch)      |  ReplaceOutput(text)
  SideEffect(async fn)    |  Noop
```

## The framework's real value = the cross-cutting rails

1. **Deadline / latency budget** — every dispatch races a per-event budget;
   overruns fall back to the fail-mode.
2. **Fail-open vs. fail-closed** — per handler. Retrieval → fail-open (degrade,
   annotate). Authz → fail-closed (deny).
3. **Async side-effects** — fire-and-forget work (audit, memory writes)
   scheduled to outlive the response.
4. **Fan-out & merge** — multiple handlers per event run concurrently; effects
   merged deterministically (`Decide(deny)` wins; `Inject`s concatenate in
   declared order).
5. **Caching** — optional per-handler result cache keyed on payload.
6. **Audit** — every dispatch and effect logged.

## v0 scope — framework core + echo handler

- Implement the dispatch loop, the six event contracts, and the cross-cutting
  rails (deadline, fail-mode, side-effects, fan-out/merge).
- `echo` handler: on `SessionStart` / `UserPromptSubmit`,
  `Inject("captAInHook: <event> seen @ <ts>")`; on `PostToolUse`, log. Proves
  the full round-trip — event in → effect out → visible in the agent's context.

## ADR-000 — Runtime: DEFERRED (build as a comparison harness)

The core above is runtime-agnostic on purpose. captAInHook will be implemented
as **one spec, N runtimes**, to compare concurrency/parallelism models on a real
I/O-bound orchestration workload (fan-out + deadline + fault isolation +
background side-effects).

| Runtime            | Model                                              | Fan-out + deadline                        | Fault isolation                    | Background work              | Shim startup        |
| ------------------ | -------------------------------------------------- | ----------------------------------------- | ---------------------------------- | ---------------------------- | ------------------- |
| BEAM (Elixir/Gleam)| share-nothing actors, preemptive, supervised       | `Task.async_stream` + timeout (idiomatic) | **best** — crash + supervisor restart | `spawn` supervised proc   | heavy → daemon-only |
| Node / TS          | single-thread event loop (libuv)                   | `Promise.allSettled` + `AbortController`  | weak — unhandled throw ≈ process    | needs daemon to outlive call | fast-ish (~50ms)    |
| .NET (C#/F#)       | work-stealing thread pool + async/await + channels | `Task.WhenAll/WhenAny` + `CancellationToken` | manual (`try/catch`, Polly)      | `Channels` consumer + backpressure | Native AOT → fast |

**First spike:** .NET (fills the gap between actors and event loop the owner
already knows). Then re-implement the same core in BEAM and Node to compare.
Workload is I/O-bound, so fan-out ergonomics, fault isolation, and startup
matter more than raw CPU throughput.

**Update (2026-07-03):** the actor-layer half of this deferral is now resolved
by [ADR-0001](doc/adr/0001-actor-runtime-fsharp-hybrid.md): the .NET spike's
actor/supervision layer is an F# hybrid (`MailboxProcessor` default, bounded
Channels hot path, hand-rolled one_for_one; Akka.NET deferred). The dispatcher
core remains C#, and the N-runtime comparison harness above continues as
planned for the Node and BEAM ports.
[ADR-0002](doc/adr/0002-handlers-as-supervised-actors.md) then converged the
two halves: handlers now run as supervised `Worker<'Req,'Reply>` actors and
dispatch is an ask with the latency budget as the timeout.
