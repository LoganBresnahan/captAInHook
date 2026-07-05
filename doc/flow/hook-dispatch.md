# Flow: hook dispatch — from agent event to merged effect

How one lifecycle event travels through the C# core: in on stdin (parsed per
the selected harness spec), fanned out to supervised handler workers under a
budget, merged deterministically, capability-gated, out on stdout in the
harness's wire format — with a structured JSONL trail the whole way.

```
 Agent harness (Claude Code by default; any host described by a HarnessSpec)
        │  fires a lifecycle hook; payload JSON on STDIN
        ▼
┌ captainHook (per-invocation binary) ──────────────────────────────────────┐
│ Program.cs: Invocation.Parse (Cli.cs) picks the mode — shim / collapsed / │
│ --daemon (ADR-0004); shim & collapsed both run HookRun.CollapsedAsync:    │
│   resolve HarnessSpec (--harness <name>, default claude-code)             │
│     embedded defaults ◄─ overridden by ~/.captainHook/harnesses/*.json    │
│     (invalid override skipped, default kept)   ── harness.specInvalid     │
│        │                                                                  │
│        ▼                                                                  │
│   parse stdin JSON per the spec's field names ─ canon event name          │
│   (user-prompt-submit → UserPromptSubmit) ─ mint dispatchId (8 hex)       │
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
│        ▼                                                                  │
│   capability gate: effect kind undeclared for this event in the spec?     │
│   warn + downgrade to Noop                 ── harness.effectUnsupported   │
│        │                                                                  │
│        ▼                                                                  │
│   spec's response adapter (closed set: claude-hook-json / generic-json)   │
└────────┼──────────────────────────────────────────────────────────────────┘
         ▼
 STDOUT  one effect JSON in the harness's wire format  ← the ONLY stdout bytes
 STDERR  human Trace summary (+ pretty log one-liners)
 FILE    ~/.captainHook/logs/captainHook.jsonl  ← the digestible trail
```

## The I/O contract

Hook mode's **stdout is the protocol channel**: the agent host parses it as
the hook's response, so exactly one JSON object may ever appear there. All
diagnostics are split by audience — humans get the `Trace` summary and pretty
one-liners on stderr; machines get JSONL in the log file. This is why
`Logging.fs` structurally cannot write to stdout.

## The harness boundary

Which host we speak to is data, not code
([ADR-0003](../adr/0003-declarative-harness-registry.md)). `--harness <name>`
(default `claude-code`) selects a `HarnessSpec` from the `HarnessRegistry`:
embedded defaults (`harnesses/*.json`, compiled into the assembly) overlaid
by user overrides from `CAPTAINHOOK_HARNESS_DIR` (else
`~/.captainHook/harnesses`) — a valid same-name file replaces the default
wholesale, a new name adds a harness, and an invalid file is skipped with a
`harness.specInvalid` warning, so a bad override can never crash the live
hook. An unknown `--harness` name exits 1 with a stderr message and **zero**
stdout bytes.

The spec drives both ends of the dispatch. At ingress, `Harness.ParseEvent`
reads the event name, session id, and cwd from whichever payload fields the
spec declares (the CLI event arg wins over the payload field). At egress, the
merged effect passes `Harness.ApplyCapabilityGate`: an effect kind the spec
never declared for that event is downgraded to `Noop` with a
`harness.effectUnsupported` warning — never send a harness something it
cannot represent — while an event *absent* from the spec passes permissively
with a `harness.eventUndeclared` debug line. The surviving effect is then
serialized by the adapter the spec names (`claude-hook-json` or
`generic-json`) — a CLOSED, coded set: data selects *which* adapter, code
defines *what* it emits, and config never becomes a template language.

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
| mode selection (`Mode`, `Invocation.Parse`) | `dotnet/captainHook/Core/Cli.cs` |
| harness resolution, stdin read, dispatchId, stdout write (`HookRun.CollapsedAsync`); Console wiring in `Program.cs` | `dotnet/captainHook/Core/HookRun.cs` |
| `HarnessSpec` (+`TryParse`), `HarnessRegistry`, `Harness.ParseEvent`/`Canon`, `Harness.ApplyCapabilityGate`, `IResponseAdapter` + `ResponseAdapters` | `dotnet/captainHook/Core/Harness.cs` |
| default harness spec (embedded resource) | `dotnet/captainHook/harnesses/claude-code.json` |
| `Registry` (spec registration), `HandlerSpec`, `Dispatcher` (ctor spawns workers), `RunGuarded`, `Merge`, `Trace` | `dotnet/captainHook/Core/Dispatcher.cs` |
| `Worker<'Req,'Reply>` (ask, reply-then-crash) | `dotnet/captainHookActors/Worker.fs` |
| `HookEvent`, `Effect`, `IHandler`, `FailMode` | `dotnet/captainHook/Core/Model.cs` |
| `EchoHandler`, `LatencyProbeHandler` | `dotnet/captainHook/Handlers/Handlers.cs` |
| log events | `dispatch.start`, `handler.ok/timeout/error`, `side.ok/error`, `dispatch.done` (src `dispatcher`); `actor.spawn/restart/escalate` (src `sup:dispatcher`); `harness.specInvalid`, `harness.effectUnsupported`, `harness.eventUndeclared` (src `harness`) |
| pinned by | `dotnet/captainHookTests/CliTests.cs` (mode selection, stdout contract in-process); `DispatcherTests.cs`, `LoggingTests.cs` (every dispatch test now runs handlers through the worker path); `ConvergenceTests.cs` (restart/state-reset, escalation fail modes, reply-then-crash speed, per-worker serialization); `HarnessTests.cs` (registry layering + overrides, adapter golden bytes, capability gate, spec-driven parsing) |
| decision record | `doc/adr/0002-handlers-as-supervised-actors.md`; `doc/adr/0003-declarative-harness-registry.md` |
