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
                agent.Post(ChildExit(id, err, restart))
                                         │  the crash is a MESSAGE in the
                                         ▼  supervisor's own mailbox
 ┌─ Supervisor loop (handles one crash at a time) ────────────────────────┐
 │ now = clock()              ← MONOTONIC ms (TickCount64 by default,     │
 │ prune attempts > windowMs    injectable — tests advance a fake clock)  │
 │ attempts = now :: recent                                               │
 │                                                                        │
 │ ≤ maxRestarts ──► restart(): factory → fresh state → handle.Swap       │
 │                   actor.restart (warn)     callers never notice        │
 │ > maxRestarts ──► actor.escalate (error) + OnEscalated(id, err)        │
 │                   child stays DEAD; future Asks fault w/ Timeout       │
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
`actor.escalate`, fire `OnEscalated`, stop restarting — the child stays dead
and asks against it fault with `TimeoutException` instead of hanging).

Window math runs on an **injectable monotonic clock** (`Environment.TickCount64`
default), never `DateTime.UtcNow`: wall time steps under NTP corrections and
dual-boot RTC skew, which would silently stretch or shrink the window. Tests
inject a `FakeClock` and advance it explicitly — "6 seconds pass" is one line,
not a real sleep. Pinned by `SlidingWindow_PrunesAgedAttempts_NoFalseEscalation`.

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
| `ActorRef` (Post/Ask/Swap), `SupMsg`, `Supervisor` (+ clock ctor) | `dotnet/captainHookActors/Supervision.fs` |
| `CounterMsg` DU, worker loop, `Counter` facade | `dotnet/captainHookActors/Counter.fs` |
| `AuditWriter` bounded actor | `dotnet/captainHookActors/HotPath.fs` |
| log events | `actor.spawn`, `actor.restart`, `actor.escalate`, `counter.increment/boom`, `audit.drain` |
| pinned by | `dotnet/captainHookTests/ActorTests.cs`, `HotPathTests.cs` |
| decision record | `doc/adr/0001-actor-runtime-fsharp-hybrid.md` |
