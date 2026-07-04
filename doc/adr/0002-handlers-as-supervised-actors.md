# ADR-0002 — Handlers as supervised actors: the dispatcher converges on the actor layer

**Status:** Accepted
**Date:** 2026-07-04

## Context

ADR-0001 left the codebase with two working halves that never touched. The C#
dispatcher ([`Dispatcher.cs`](../../dotnet/captainHook/Core/Dispatcher.cs)) ran
`IHandler`s as plain awaited tasks under a latency budget; the F# actor layer
([`Supervision.fs`](../../dotnet/captainHookActors/Supervision.fs)) supervised
demo actors (`Counter`, `AuditWriter`) that nothing on the dispatch path used.
Fault isolation was therefore only half-delivered: a throwing handler was
converted into its fail-open/fail-closed effect, but nothing restarted it and
nothing reset its state — a stateful handler that corrupted itself stayed
corrupted for the life of the process.

Today that costs little: the hook binary is per-invocation, so every dispatch
starts from a fresh process anyway. But the daemon topology
([the daemon-topology roadmap item](../roadmap.md)) makes handlers long-lived — caches, session
state, audit writers surviving thousands of dispatches — and long-lived
stateful handlers need exactly what the actor layer already provides: private
state behind a mailbox, one message at a time, crash → supervised restart with
fresh state. Building the daemon on unsupervised `Task`s and retrofitting
supervision afterwards would mean converging the two halves later, under
pressure.

One hard constraint shapes any design here: **the dependency arrow points
C# host → F# lib.** The F# assembly can never reference `HookEvent`, `Effect`,
or `IHandler` — those types live in the host that references it.

## Decision

Converge the halves now: **every handler registration becomes one supervised,
generic F# worker actor**, and dispatch becomes an ask.

1. **Generic `Worker<'Req,'Reply>`**
   ([`Worker.fs`](../../dotnet/captainHookActors/Worker.fs)). Generic because
   the dependency arrow forbids anything else: the worker knows nothing about
   hooks — the `(HookEvent, HandlerContext)` tuple and the `Effect` flow
   through it opaquely, and only the delegate the host supplies ever looks
   inside. The `WorkMsg` DU and `AsyncReplyChannel` stay internal to the F#
   assembly (the same facade seam ADR-0001 established with `Counter`).
2. **The registry stores child specs, not live handlers.**
   `HandlerSpec(Name, OnFailure, Factory)` is the OTP child-spec idea ported
   to the registry: restart = run the factory again = genuinely fresh handler
   state. The new overload
   `Registry.On(eventType, name, factory, onFailure)` registers a factory;
   the original `On(eventType, params IHandler[])` overload is preserved and
   wraps each instance in a factory that returns **the same instance** — so
   instance-registered handlers are *not* state-reset by a restart. That is a
   documented contract, fine for stateless handlers, deliberate for anything
   else. Workers are spawned in the `Dispatcher` constructor from a snapshot
   of `Registry.Specs`, one per registration, id `{eventType}/{name}` (`#n`
   suffix on name collisions) — register handlers *before* constructing the
   Dispatcher; later `On` calls are silently not picked up.
3. **Dispatch = ask.** `RunGuarded` asks the worker with the latency budget
   as the ask timeout, keeping `.WaitAsync(budgetCt)` on the dispatch-wide
   token as a backstop for whichever fires first. `OperationCanceledException`
   and `TimeoutException` are one timeout path; both convert to the handler's
   fail-mode effect exactly as before. Merge precedence, background draining,
   the human `Trace`, and every structured log event are unchanged.
4. **Reply-then-crash.** On a handler exception the worker replies
   `Choice2Of2 ex` *first*, then raises. The reply means the asker rethrows
   the original exception (stack preserved via `ExceptionDispatchInfo`)
   immediately — to `RunGuarded` a crash looks exactly like direct invocation
   used to, instead of burning the full ask timeout waiting on a corpse. The
   raise means the crash still escapes the loop, so `MailboxProcessor.Error`
   fires and the supervisor restarts (or escalates) the worker. One failure,
   both audiences informed.

The default supervisor is `Supervisor("dispatcher", maxRestarts: 3, window:
5s)`; the new optional constructor parameter
`Dispatcher(Registry, TimeSpan, Supervisor?)` is the test seam — inject a
supervisor built on a fake clock and restart-window math becomes
deterministic. The only breaking API change is the removal of
`Registry.For(eventType)`; the Dispatcher was its sole caller, replaced by the
internal `Registry.Specs`.

Zero new runtime dependencies, as before: BCL + FSharp.Core only.

## Consequences

### Positive

- **Crash containment with state reset.** A factory-registered handler that
  throws yields its fail-mode effect to the dispatch *and* comes back
  restarted with fresh state (`actor.restart`) — corrupted handler state can
  no longer outlive the crash that revealed it.
- **Per-worker serialization.** A `MailboxProcessor` handles one message at a
  time, so a thread-unsafe stateful handler is safe by construction even
  under overlapping dispatches. Irrelevant in the single-shot binary;
  load-bearing the day the daemon dispatches concurrently.
- **Circuit breaking for free.** The restart-intensity window turns a
  persistently crashing handler into an escalated (dead) worker
  (`actor.escalate`), and every subsequent dispatch degrades to that
  handler's fail-mode effect instead of re-running broken code.
- **The daemon-ready shape.** The daemon-topology roadmap item no longer requires
  re-architecting the dispatch path — the workers are already long-lived-
  capable, and the supervision layer is now on the critical path where its
  bugs surface in the dispatch test suite rather than in a demo.

### Negative

- **Architecture more than behavior, today.** In the per-invocation binary
  the process dies after one dispatch, so restart-with-fresh-state is mostly
  latent value until the daemon lands.
- **Instance-registered handlers keep state across restarts** — by the
  documented contract above. Only the factory overload buys the reset.
- **An escalated worker burns the full ask timeout on every subsequent
  dispatch** before surfacing `TimeoutException` → its fail-mode effect. A
  fast-fail-on-dead-worker optimization is a known follow-up, deferred to the
  daemon work (the daemon-topology roadmap item), where a long-lived process can actually
  accumulate escalated workers.
- **Budget timeouts of token-honoring handlers count as crashes.** A handler
  that honors `ctx.Ct` throws `OperationCanceledException` *inside* the
  worker, which (reply-then-crash) restarts it — so repeated timeouts can
  escalate a merely-slow handler. Conversely, a handler that *ignores* its
  token and hangs times out the ask without crashing the worker: the actor
  stays stuck on that message and later asks queue behind it. Both are
  acceptable in single-shot mode; the daemon revisit must address them
  (cancel-on-timeout, bounded mailbox).
- **`MailboxProcessor` per-message allocation** (~an order of magnitude above
  Channels, per ADR-0001) is irrelevant at hook rates — a dispatch is a
  handful of messages — and ADR-0001's bounded-Channels escape hatch remains
  available if a genuinely hot path ever appears.

## Alternatives considered

| Option | Why not (now) |
| --- | --- |
| Move the domain types (`HookEvent`, `Effect`) into the F# assembly so workers are typed natively | Large churn across the host, handlers, and tests; C# ergonomics of DU-heavy domain types are worse than records; the generic worker delivers the same supervision without moving anything |
| Keep plain `Task` handlers (status quo) | No supervision, no state reset, no daemon story — the two halves stay disconnected forever, and the daemon work inherits the convergence under pressure |
| Bounded-Channels worker instead of `MailboxProcessor` | Viable, and stays viable — deferred per ADR-0001's hybrid split: MailboxProcessor ergonomics (DU protocol, native ask) win at hook rates; switch the worker's internals when a hot path demands backpressure or throughput |

## Revisit triggers

- **The daemon lands (the daemon-topology roadmap item):** add fast-fail on escalated/dead
  workers, decide worker lifecycle (draining, idle shutdown), and revisit
  cancel-on-timeout for token-ignoring handlers and the timeout-counts-as-
  crash policy for token-honoring ones.
- **Handler throughput demands** exceed `MailboxProcessor`: swap the worker's
  internals for the bounded Channels shape behind the same `Worker` facade.
