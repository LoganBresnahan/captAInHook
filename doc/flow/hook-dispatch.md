# Flow: hook dispatch — from agent event to merged effect

How one lifecycle event travels through the C# core: in on stdin, fanned out
to handlers under a budget, merged deterministically, out on stdout — with a
structured JSONL trail the whole way.

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
│ Registry.For(event) ──► [ handler₁ … handlerₙ ]                           │
│        │                                                                  │
│        ▼                                        structured log (JSONL)    │
│ Dispatcher.DispatchAsync(evt, dispatchId)       ── dispatch.start         │
│   CancellationTokenSource(budget)                                         │
│   Task.WhenAll ── FAN-OUT: all handlers run concurrently                  │
│    ┌───────────────┼────────────────┐                                     │
│    ▼               ▼                ▼                                     │
│ RunGuarded      RunGuarded       RunGuarded                               │
│    │ ok            │ timeout        │ throws                              │
│    │ Effect        │ Fail(h):       │ Fail(h):        ── handler.ok       │
│    │               │  open → Noop   │  open → Noop    ── handler.timeout  │
│    │               │  closed → Deny │  closed → Deny  ── handler.error    │
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
wall-time tracks the *slowest* handler, not the sum. One
`CancellationTokenSource(budget)` covers the whole dispatch, and
`RunGuarded` enforces it with `.WaitAsync(budgetCt)` — the budget bites even
for handlers that ignore their cancellation token.

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
| `Dispatcher.DispatchAsync`, `RunGuarded`, `Merge`, `Trace` | `dotnet/captainHook/Core/Dispatcher.cs` |
| `HookEvent`, `Effect`, `IHandler`, `FailMode` | `dotnet/captainHook/Core/Model.cs` |
| `EchoHandler`, `LatencyProbeHandler` | `dotnet/captainHook/Handlers/Handlers.cs` |
| log events | `dispatch.start`, `handler.ok/timeout/error`, `side.ok/error`, `dispatch.done` |
| pinned by | `dotnet/captainHookTests/DispatcherTests.cs`, `LoggingTests.cs` |
