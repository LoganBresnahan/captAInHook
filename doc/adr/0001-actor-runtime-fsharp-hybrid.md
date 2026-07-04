# ADR-0001 — Actor layer: F# hybrid (MailboxProcessor + Channels), Akka.NET deferred

**Status:** Accepted
**Date:** 2026-07-03

## Context

captAInHook's .NET core ([`dotnet/captainHook`](../../dotnet/captainHook)) is a
C# dispatcher that fans handlers out under latency budgets with
fail-open/fail-closed policies. Fault isolation is one of the framework's rails
([DESIGN.md](../../DESIGN.md)): a crashing handler must not take down the
dispatch, and long-lived stateful components (caches, audit writers, session
state) want the actor discipline — private state behind a mailbox, one message
at a time, crash → supervised restart.

.NET has no built-in supervision, so we ran three spikes, each implementing the
**identical script**: two counter workers under a one_for_one supervisor, poison
one, verify it restarts with fresh state while its sibling's state survives.

| Spike | LOC (raw / code) | Runtime deps |
| --- | --- | --- |
| [`csharp-actors`](../../dotnet/experiments/csharp-actors) — hand-rolled `Channel<T>` actors | 431 / 232 | 0 |
| [`fsharp-actors`](../../dotnet/experiments/fsharp-actors) — F# `MailboxProcessor` | 280 / 127 | 0 |
| [`akka-actors`](../../dotnet/experiments/akka-actors) — Akka.NET 1.5.70 | 294 / 122 (own code) | 10 transitive packages, incl. Newtonsoft.Json |

What the spikes taught:

- **C#** has no actor primitive. The spike had to build the runtime itself —
  mailbox, consumer loop, crash-to-EXIT conversion
  ([`ActorCell.cs`](../../dotnet/experiments/csharp-actors/ActorCell.cs)) —
  which is why it is ~2x the size of the other two. Messages are `object` +
  `switch`: nothing forces a handler to cover every case.
- **F#**'s runtime primitive *is* an actor. A closed discriminated union is the
  whole message protocol in one place, and the compiler enforces exhaustive
  `match` — add a case and every loop that forgot it fails the build. State
  threads immutably through a recursive loop, so *restart = re-run the factory
  = fresh state* falls out for free. Ask is native via `AsyncReplyChannel`.
- **Akka.NET** wins on supervision ergonomics: a declarative
  `OneForOneStrategy` ([`Supervisor.cs`](../../dotnet/experiments/akka-actors/Supervisor.cs))
  replaces the ~90-line hand-rolled supervisor. But its `ActorSystem` spins up
  dispatcher threads, a scheduler, an event stream, and logging at startup, and
  it wants a long-lived process. captAInHook currently ships a
  **per-invocation hook binary**; the daemon topology in DESIGN.md is a
  possibility, not a commitment. Adopting Akka now would prematurely commit us
  to it.

One misconception to kill: **"F# is slow" is false** — F# and C# compile to the
same IL and run on the same CLR. What *is* slower is `MailboxProcessor` (the
library): roughly an order of magnitude more per-message allocation than
`System.Threading.Channels`, and its mailbox is **unbounded** — no
backpressure, a fast producer just grows the queue. That is a property of one
library, not of the language, and Channels is a BCL library callable from any
.NET language.

## Decision

Adopt a **hybrid F# actor layer**: a new F# assembly,
[`dotnet/captainHookActors`](../../dotnet/captainHookActors), referenced by the
C# host via a single `ProjectReference` line
([`captainHook.csproj`](../../dotnet/captainHook/captainHook.csproj)).

1. **Default path — `MailboxProcessor`** for the 95% of actors where
   ergonomics matter more than throughput: DU protocols, exhaustive match,
   immutable state, native ask.
2. **Hot path — bounded `System.Threading.Channels`** (capacity +
   `FullMode.Wait`) written in F# via the `task` computation expression, for
   actors that need backpressure or throughput.
   [`HotPath.fs`](../../dotnet/captainHookActors/HotPath.fs) proves the shape:
   same assembly, same language, a full producer *awaits a slot* instead of
   growing memory.
3. **Hand-rolled one_for_one supervision**
   ([`Supervision.fs`](../../dotnet/captainHookActors/Supervision.fs)):
   children are factories (the OTP child-spec idea); a crash surfaces through
   the `MailboxProcessor.Error` event, is reified as an EXIT message posted to
   the supervisor's *own* mailbox (so crashes are handled one at a time), and
   restart means re-running the factory — fresh state and a new mailbox by
   construction. A sliding restart-intensity window (`maxRestarts` within
   `window`) stops crash loops; blowing the budget escalates to the host via
   an `OnEscalated` callback.
4. **`ActorRef` swap indirection**: callers hold a stable `ActorRef<'Msg>`;
   the supervisor swaps the live mailbox underneath on every restart. This
   fixes the classic gotcha where callers keep posting into a dead mailbox
   after a crash.
5. **Facade seam** ([`Counter.fs`](../../dotnet/captainHookActors/Counter.fs)):
   DUs, `AsyncReplyChannel`, and F# funcs stay internal to the F# assembly;
   C# sees plain methods and `Task`s. Rich types inside, boring .NET surface
   at the boundary. [`ActorsDemo.cs`](../../dotnet/captainHook/Demo/ActorsDemo.cs)
   drives both paths from the C# host.

**Akka.NET is deferred, not rejected.** The spike stays in the tree as a
ready-made re-evaluation point (see Revisit triggers).

Zero new runtime dependencies: BCL + FSharp.Core only.

## Consequences

### Positive

- **Zero dependencies** — nothing to audit, upgrade, or trim for a hook binary
  that must start fast.
- **Full ownership** — the supervision code is ~90 lines we wrote and
  understand end to end, not a framework we configure.
- **Per-language strengths** — F# where modeling wins (protocols, state,
  supervision), Channels where mechanics win (bounding, throughput), C# where
  the dispatcher already lives.
- **Learning value** — the layer doubles as a working comparison of three
  actor implementations on one workload, in the spirit of the ADR-000 harness.

### Negative

- **We own supervision correctness.** Intensity-window pruning, escalation
  edge cases, crash-during-restart — bugs here are ours to find and fix.
- **`MailboxProcessor` is unbounded.** A misbehaving producer can grow a
  mailbox without limit. Mitigation: any actor with a fast or untrusted
  producer uses the bounded Channels shape instead.
- **Two languages in one solution.** F# compiles files strictly in order
  (dependencies first, maintained by hand in the `.fsproj`), and contributors
  need reading fluency in both.
- **No rest_for_one, no watch/DeathWatch, no routers, no remoting.** We built
  exactly one_for_one; anything richer is a trigger below.

## Alternatives considered

| Option | Own code | Deps | Supervision | Backpressure | Why not (now) |
| --- | --- | --- | --- | --- | --- |
| [Hand-rolled C# Channels](../../dotnet/experiments/csharp-actors) | 431 / 232 | 0 | Hand-rolled (~120 lines) | Yes (Channels native) | ~2x the code: C# must build the actor runtime itself; `object` + `switch` messages, no exhaustiveness |
| [F# `MailboxProcessor`](../../dotnet/experiments/fsharp-actors) | 280 / 127 | 0 | Hand-rolled (~90 lines) | **No** (unbounded mailbox) | Adopted for the default path — but not alone: no bounding, higher per-message allocation |
| [Akka.NET 1.5.70](../../dotnet/experiments/akka-actors) | 294 / 122 | 10 pkgs incl. Newtonsoft.Json | Declarative `OneForOneStrategy` (best) | Configurable | Heavy `ActorSystem` startup; wants a long-lived process — prematurely commits the per-invocation binary to the daemon topology |
| Pure C# everywhere (promote `csharp-actors` into the core) | ~232+ | 0 | Hand-rolled | Yes | Keeps one language but pays the full runtime-building tax on every actor and gives up DU protocols; the hybrid gets the same Channels wins from F# |

The hybrid takes the two cheap options and splits them by strength; the
expensive option stays on the shelf, one directory away.

## Revisit triggers

Re-evaluate Akka.NET (the [`akka-actors`](../../dotnet/experiments/akka-actors)
spike is the ready-made starting point) if any of these fire:

- We **commit to the persistent `captaind` daemon topology** — Akka's startup
  cost amortizes and its long-lived-process assumption becomes true.
- We need **rest_for_one, watch/DeathWatch, routers, or clustering** —
  rebuilding those by hand is where hand-rolling stops paying.
- **Supervision bugs** are discovered in our hand-rolled code — a correctness
  tax we said we would pay; if the bill is real, buy the framework.
- **Throughput demands exceed even the Channels escape hatch.**
