# Flow: actor supervision — crash to restart (or escalation)

How the F# actor layer keeps state-holding workers alive: spawn from a
factory, crash reified as a message, restart under a monotonic intensity
window, escalation when restarting stops making sense.

```
 C# host                                     F# actor layer
─────────                                   ────────────────────────────────
 Counter.Supervised(sup, id)
        │ facade: DUs & AsyncReplyChannel stay F#-internal
        ▼
 Supervisor.Spawn(id, factory) ───────────── actor.spawn (log)
        │ start():
        │   child = factory()                fresh state + new mailbox
        │   child.Error.Add ─────────────┐   the crash-notification wire
        │   handle.Swap(child)           │
        ▼                                │
   ActorRef (stable handle)              │
        │                                │
   Post │ Ask (default 2s timeout)       │
        ▼                                │
 ┌─ MailboxProcessor ─────────────┐      │
 │ let rec loop state =           │      │
 │   Receive() → match msg with   │  one message at a time,
 │   …    → loop state'           │  run-to-completion
 │   Boom → THROW ✗               │      │
 └────────────────────────────────┘      │
            body throws → agent dies silently
                                         │ .Error fires
                                         ▼
        agent.Post(ChildExit(id, gen, err))     agent.Post(ChildWedged(id, gen, corr))
                                         │  faults are MESSAGES in the   │ from the ask layer:
                                         ▼  supervisor's own mailbox     ▼ received, never answered
 ┌─ Supervisor loop (handles one fault at a time) ────────────────────────┐
 │ stale generation or dead child? ──► actor.staleExit (debug), ignore    │
 │ err is OperationCanceledException (budget token honored)?              │
 │   ──► restart, NOT COUNTED (actor.restart kind=cancelled counted=false)│
 │       — timeout is not fault (ADR-0004 d5)                             │
 │ else crash / wedge — COUNTED:                                          │
 │   now = clock()            ← MONOTONIC ms (TickCount64 by default,     │
 │   prune attempts > windowMs  injectable — tests advance a fake clock)  │
 │   attempts = now :: recent                                             │
 │   ≤ maxRestarts ──► restart(): factory → fresh state → handle.Swap     │
 │                     actor.restart (warn)     callers never notice      │
 │   > maxRestarts ──► actor.escalate (error) + MarkDead + OnEscalated    │
 │                     child stays DEAD; classified asks fail FAST (Dead),│
 │                     legacy Asks fault w/ Timeout                       │
 └────────────────────────────────────────────────────────────────────────┘
```

## The facade seam

C# never sees a discriminated union, an `AsyncReplyChannel`, or an F#
function type. `Counter` exposes plain methods returning `Task`s; the rich
protocol lives inside the F# assembly. Every consumer keeps this boundary:
rich types inside, boring .NET surface at the edge.

## The ActorRef swap

Restart creates a *new* `MailboxProcessor` — any caller holding the old one
would post into a dead mailbox forever. `ActorRef` is the stable indirection:
the supervisor calls `Swap` on every (re)start, so the same C# reference
transparently routes to the live instance. Pinned by
`SameCounterReference_StillWorksAfterRestart`.

## Crash as a message

A `MailboxProcessor` whose body throws dies *silently* — the exception
surfaces only on its `.Error` event. The supervisor subscribes at spawn and
converts the event into a `ChildExit` message posted to its **own** mailbox
(the Node/BEAM idiom: failure is ordinary data). Because the supervisor is
itself an actor, crashes are handled strictly one at a time — no concurrent
restart races by construction.

## Restart intensity on a monotonic clock

The sliding window (`maxRestarts` within `window`) distinguishes a transient
blip (restart, quietly heal) from a persistent fault (escalate: log
`actor.escalate`, fire `OnEscalated`, mark the handle dead, stop restarting —
classified asks against a dead child return `AskStatus.Dead` immediately;
the legacy `Ask` faults with `TimeoutException` instead of hanging).

Window math runs on an **injectable monotonic clock** (`Environment.TickCount64`
default), never `DateTime.UtcNow`: wall time steps under NTP corrections and
dual-boot RTC skew, which would silently stretch or shrink the window. Tests
inject a `FakeClock` and advance it explicitly — "6 seconds pass" is one line,
not a real sleep. Pinned by `SlidingWindow_PrunesAgedAttempts_NoFalseEscalation`.

## Timeout is not fault — the classified ask (ADR-0004 decision 5)

What counts toward that window is **classified**, not everything that goes
wrong. `Worker.AskClassifiedAsync(req, budget, grace, correlationId)` waits
budget + grace — the grace exists so a token-honoring handler's cancellation
reply, which *leaves* the handler at the budget, lands **inside** the ask
window instead of racing the deadline — and resolves to one of five statuses:

| status | what happened | counted? | worker fate |
|---|---|---|---|
| `Ok` | reply arrived | — | lives |
| `Faulted` (OCE) | budget token **honored** | **no** | restart (mailbox died via reply-then-crash) |
| `Faulted` (other) | handler crashed | yes | restart or escalate |
| `Wedged` | **received, never answered** | **yes** | **abandoned**: factory re-runs, handle swaps, stuck task LEAKED |
| `Backlogged` | never received (queued behind a busy sibling) | no | untouched — backlog is load evidence, not a defect |
| `Dead` | already escalated | — | ask fails fast, ~0ms |

The wedge/backlog split rides a **receipt flag** the worker flips the moment
it dequeues the message; wedges reach the supervisor through its narrow
reporting channel (`ReportWedge`), because the supervisor owns *all* counting.
Wedges count precisely because each abandonment leaks a stuck task — .NET
cannot kill user code mid-flight — so a chronic wedger must escalate rather
than leak forever. Every fault signal carries the **generation** of the
instance it belongs to (bumped on each `Swap`): a leaked, abandoned instance
dying minutes later is recognized as stale (`actor.staleExit`) instead of
restarting its healthy replacement.

One observable race, seen live and deliberate: a dispatch fired in the moment
between a wedge report and the respawn posts into the doomed mailbox and
classifies `Backlogged` (physically true — it queued behind the stuck
handler). It degrades identically and merely doesn't count, so a chronic
wedger may take a few extra dispatches to escalate. Classification guides
counting, never correctness.

## The generic Worker — the convergence seam

`Worker<'Req,'Reply>` (`Worker.fs`) is how the C# dispatcher rides this
machinery without the F# assembly ever seeing a domain type — the dependency
arrow points C# host → F# lib, so `HookEvent`/`Effect` can never appear here.
It wraps a caller-supplied `Func<'Req, Task<'Reply>>` in a supervised
`MailboxProcessor`: `Supervised(sup, id, handlerFactory)` treats the factory
as the child spec (a fresh delegate per restart = fresh handler state), and
`AskAsync` rethrows a handler exception with its original stack
(`ExceptionDispatchInfo`), so to the asker it looks exactly like awaiting the
delegate directly. A failure inside the worker is **reply-then-crash**: reply
`Choice2Of2 ex` first (the asker learns immediately instead of burning its ask
timeout), then raise (so `.Error` still fires and the supervisor restarts or
escalates). The dispatcher spawns one worker per handler registration — see
[hook-dispatch.md](hook-dispatch.md) and
[ADR-0002](../adr/0002-handlers-as-supervised-actors.md).

## The two mailbox flavors

| | `MailboxProcessor` (default) | `Channel` (hot path) |
|---|---|---|
| mailbox | **unbounded** — `Post` never waits | **bounded** — `WriteAsync` awaits a slot |
| backpressure | none (watch `CurrentQueueLength`) | `FullMode.Wait` throttles the producer |
| ask | native `AsyncReplyChannel` | hand-rolled (`TaskCompletionSource`) |
| speed | ~10× more per-message allocation | purpose-tuned, low-allocation |
| use for | the 95% — supervised, stateful workers | bursty/high-volume sinks |

`AuditWriter` is the Channels shape in F# (via the `task { }` CE):

```
 producer ──WriteAsync──► [■■■■■■■■] capacity N ──TryRead──► slow consumer
               │ full? the WRITE awaits a slot (no thread blocked)
               ▼
    producer throttled to consumer pace — memory bounded by construction
```

## Ground truth

| what | where |
|---|---|
| `ActorRef` (Post/Ask/Swap/Generation/IsDead), `SupMsg` (ChildExit/ChildWedged), `ChildEntry`, `Supervisor` (+ clock ctor, `ReportWedge`) | `dotnet/captainHookActors/Supervision.fs` |
| `WorkMsg` DU (with receipt flag), `AskStatus`, `AskOutcome`, `Worker<'Req,'Reply>` (Supervised/AskAsync/AskClassifiedAsync, reply-then-crash) | `dotnet/captainHookActors/Worker.fs` |
| `CounterMsg` DU, worker loop, `Counter` facade | `dotnet/captainHookActors/Counter.fs` |
| `AuditWriter` bounded actor | `dotnet/captainHookActors/HotPath.fs` |
| log events | `actor.spawn`, `actor.restart` (data: `kind` = cancelled/crash/wedge, `counted`), `actor.wedge`, `actor.escalate` (data: `kind`), `actor.staleExit`, `counter.increment/boom`, `audit.drain` |
| pinned by | `dotnet/captainHookTests/ActorTests.cs`, `HotPathTests.cs`; the Worker path by `DispatcherTests.cs` and `ConvergenceTests.cs`; classification by `ClassificationTests.cs` |
| decision records | `doc/adr/0001-actor-runtime-fsharp-hybrid.md`, `doc/adr/0002-handlers-as-supervised-actors.md`, `doc/adr/0004-daemon-topology.md` (decision 5) |
