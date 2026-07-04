# Flow: hook dispatch — from agent event to merged effect

How one lifecycle event travels through the C# core: in on stdin, fanned out
to supervised handler workers under a budget, merged deterministically, out on
stdout — with a structured JSONL trail the whole way.

```
 Claude Code / Agent SDK host
        │  fires a lifecycle hook; payload JSON on STDIN
        ▼
┌ captainHook (per-invocation binary) ──────────────────────────────────────┐
│ Program.cs                                                                │
│   parse stdin JSON ─ canon event name (user-prompt-submit →               │
│   UserPromptSubmit) ─ mint dispatchId (8 hex)                             │
│        │                                                                  │
│        ▼                                                                  │
│ ctor: snapshot Registry.Specs ──► spawn one supervised                    │
│       F# Worker actor per registration (id "event/name")  ── actor.spawn  │
│        │                                                                  │
│        ▼                                        structured log (JSONL)    │
│ Dispatcher.DispatchAsync(evt, dispatchId)       ── dispatch.start         │
│   CancellationTokenSource(budget)                                         │
│   Task.WhenAll ── FAN-OUT: all handlers run concurrently                  │
│    ┌───────────────┼────────────────┐                                     │
│    ▼               ▼                ▼                                     │
│ RunGuarded      RunGuarded       RunGuarded     each ASKS its worker      │
│    │ ok            │ timeout        │ throws    (budget = ask timeout)    │
│    │ Effect        │ Fail(h):       │ Fail(h):        ── handler.ok       │
│    │               │  open → Noop   │  open → Noop    ── handler.timeout  │
│    │               │  closed → Deny │  closed → Deny  ── handler.error    │
│    │               │                │ reply-then-crash: the worker also   │
│    │               │                │ crashes → supervisor restarts it    │
│    │               │                │      ── actor.restart / escalate    │
│    └───────────────┴── outcomes ────┘                                     │
│        │ partition                                                        │
│        ├─ Effect.Background ──► Channel ──► drained    ── side.ok/error   │
│        ▼                                                                  │
│   Merge(loop effects):  deny ▸ ask ▸ replace(last) ▸ inject(concat)       │
│        │                                        ── dispatch.done (durMs)  │
└────────┼──────────────────────────────────────────────────────────────────┘
         ▼
 STDOUT  one effect JSON (hookSpecificOutput…)  ← the ONLY stdout bytes
 STDERR  human Trace summary (+ pretty log one-liners)
 FILE    ~/.captainHook/logs/captainHook.jsonl  ← the digestible trail
```

## The I/O contract

Hook mode's **stdout is the protocol channel**: the agent host parses it as
the hook's response, so exactly one JSON object may ever appear there. All
diagnostics are split by audience — humans get the `Trace` summary and pretty
one-liners on stderr; machines get JSONL in the log file. This is why
`Logging.fs` structurally cannot write to stdout.

## Fan-out under a budget

Handlers run concurrently via `Task.WhenAll` on the thread pool, so dispatch
wall-time tracks the *slowest* handler, not the sum. Each `RunGuarded` is an
**ask against the handler's supervised worker**: the latency budget doubles as
the ask timeout, and `.WaitAsync(budgetCt)` on the dispatch-wide
`CancellationTokenSource(budget)` stays as a backstop — the budget bites even
for handlers that ignore their cancellation token. `OperationCanceledException`
(the token fired) and `TimeoutException` (the ask timed out, or the worker is
dead after escalation) land on the same timeout path: no answer within budget.

## Handlers as supervised workers

The `Dispatcher` constructor snapshots `Registry.Specs` and spawns **one
generic `Worker<(HookEvent, HandlerContext), Effect>` per registration**
(id `{event}/{name}`) under a one_for_one supervisor — so register all
handlers *before* constructing the Dispatcher; later `On` calls are not picked
up. The registry stores child specs (name, fail mode, factory): the factory
overload of `On` yields a genuinely fresh handler on every supervised restart,
while the instance overload wraps the same object — no state reset, by
documented contract.

A handler exception inside the worker is **reply-then-crash**: the reply
carries the exception back, so `RunGuarded` rethrows exactly what direct
invocation used to throw — immediately, not after an ask timeout — and
converts it per fail mode; the raise still escapes the loop, so the supervisor
restarts the worker (`actor.restart`). A crash-looping handler blows the
restart window and escalates (`actor.escalate`): the worker stays dead, and
every later ask burns the full budget before degrading to the fail-mode effect
(fast-fail on dead workers is deferred to the daemon work). One nuance: a
handler that *honors* `ctx.Ct` and times out throws inside the worker, so
budget timeouts also count against its restart window. Supervision mechanics:
[actor-supervision.md](actor-supervision.md); decision:
[ADR-0002](../adr/0002-handlers-as-supervised-actors.md).

## Failure policy

Each handler declares `IHandler.OnFailure`. On timeout or exception,
`Fail(h)` converts the failure into an effect instead of an exception:
**fail-open** → `Noop` (degrade quietly — right for retrieval/enrichment),
**fail-closed** → `Decide(Deny)` (refuse loudly — right for authz/policy
gates). A failing handler never poisons its siblings' effects.

## Merge precedence

| precedence | effect | rule | why |
|---|---|---|---|
| 1 | `Decide(deny)` | first deny wins outright | a safety veto must be unoverridable |
| 2 | `Decide(ask)` | next | escalate to a human beats content edits |
| 3 | `Replace` | **last registered wins** | replacements don't compose; deliberate, pinned by test |
| 4 | `Inject` | **all concatenate, registration order** | context contributions do compose |
| 5 | `Noop` | only when nothing else | — |

## Background effects

`Effect.Background` work (audit writes, notifications) is routed onto a
`Channel` and drained after the handler barrier — it contributes nothing to
the merged effect. In today's per-invocation binary the drain completes
before exit; in a future daemon the consumer becomes long-lived and never
blocks the response.

## Ground truth

| what | where |
|---|---|
| stdin parse, event canon, dispatchId, stdout write | `dotnet/captainHook/Program.cs` |
| `Registry` (spec registration), `HandlerSpec`, `Dispatcher` (ctor spawns workers), `RunGuarded`, `Merge`, `Trace` | `dotnet/captainHook/Core/Dispatcher.cs` |
| `Worker<'Req,'Reply>` (ask, reply-then-crash) | `dotnet/captainHookActors/Worker.fs` |
| `HookEvent`, `Effect`, `IHandler`, `FailMode` | `dotnet/captainHook/Core/Model.cs` |
| `EchoHandler`, `LatencyProbeHandler` | `dotnet/captainHook/Handlers/Handlers.cs` |
| log events | `dispatch.start`, `handler.ok/timeout/error`, `side.ok/error`, `dispatch.done` (src `dispatcher`); `actor.spawn/restart/escalate` (src `sup:dispatcher`) |
| pinned by | `dotnet/captainHookTests/DispatcherTests.cs`, `LoggingTests.cs` (every dispatch test now runs handlers through the worker path); `ConvergenceTests.cs` (restart/state-reset, escalation fail modes, reply-then-crash speed, per-worker serialization) |
| decision record | `doc/adr/0002-handlers-as-supervised-actors.md` |
