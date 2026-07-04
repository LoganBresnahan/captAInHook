---
name: shipshape
description: Verify captAInHook is shipshape — tests cover the public surface (suite green twice), docs (README/DESIGN/ADRs/flow diagrams) match the code, and logging/tracing conventions hold. Use after substantive changes, before commits, or when asked whether the project is in order.
---

# /shipshape — repo verification pass

Three gates: **Tests**, **Docs**, **Logging**. Check all three even if one
fails early — the deliverable is the full report, not the first failure.
Propose fixes; do **not** apply them unless the user asks.

## 0. Scope the audit

```bash
git status --short && git diff HEAD --stat
```

Uncommitted work is the primary audit surface; spot-check the rest. If the
user asks for a full audit, the scope is the whole repo. `dotnet/experiments/`
is frozen ADR evidence — exempt from all gates.

## 1. Tests gate

The suite must be green **twice in a row** (the flaky bar — timing-sensitive
tests must survive a loaded machine):

```bash
dotnet test dotnet/captainHookTests --nologo -v quiet
dotnet test dotnet/captainHookTests --nologo -v quiet
```

Coverage is judged by **behavior mapping**, not a percentage. Enumerate the
public surface in scope and name the test that pins each behavior:

- C# core: `Registry`, `Dispatcher` (fan-out, budget, fail modes), `Effect`
  merge precedence, `IHandler.OnFailure`, background effects, handlers.
- F# actors: `Supervisor` (restart, intensity window, escalation), `ActorRef`
  (swap survives restart, ask timeout), `Counter` facade, `AuditWriter`
  (drain, bounded/backpressure), `Log` (sink swap, event shape).

A new or changed public behavior with no test naming it = **gap**.

Timing rules (both are pinned by existing tests — keep them true):
- Interval math uses monotonic time (injectable clock, `Stopwatch`,
  `TickCount64`) — never wall-clock arithmetic. Check hits from
  `grep -rn "DateTime.UtcNow" dotnet --include=*.cs --include=*.fs`:
  timestamps/display are fine; subtraction or comparison for control flow is
  a violation.
- Async assertions are time-bounded (`PollUntilAsync`, `WaitAsync`) — a test
  must be unable to hang; no bare sleeps as synchronization.

## 2. Docs gate

**ADRs** (`doc/adr/NNNN-slug.md`, Nygard style: Context / Decision /
Consequences / Alternatives / Revisit triggers). Any **decision** in scope —
new dependency, new project or runtime, changed contract or architecture,
pattern adopted or rejected — needs an ADR, or an update marking an existing
one superseded. Implementation detail is not a decision.

**Flow docs** (`doc/flow/*.md`) — every major pipeline gets one, with all
three parts:
1. an ASCII **diagram** of the flow,
2. supporting **prose** explaining each stage and *why* it is shaped that way,
3. a **Ground truth** section listing the files, symbols, log events, and
   tests the doc depicts.

Drift check, per flow doc:

```bash
git log -1 --format='%ct %h' -- doc/flow/<doc>.md      # when the doc last changed
git log -1 --format='%ct %h' -- <its ground-truth files>
```

If any ground-truth source is newer than the doc, read that source's diff
since the doc's commit and either confirm the doc still matches or name the
exact stale box/arrow. Also grep that every symbol and log event named in
Ground truth still exists in the code.

**README.md / DESIGN.md**: status claims, layout, and links still true.

## 3. Logging gate

All diagnostics flow through `CaptainHook.Actors.Log`. Allowed console
escapes — everything else is a violation:
- `Logging.fs` itself (it *is* the sink),
- `Program.cs`: the stdout effect write + stderr `Trace.Render()`,
- `Demo/` (demo mode is a console app on purpose).

```bash
grep -rn "printfn\|eprintfn" dotnet/captainHookActors --include=*.fs   # expect: Logging.fs only
grep -rn "Console\." dotnet/captainHook --include=*.cs                 # expect: Program.cs, Demo/ only
```

Conventions for any new events:
- `evt` is dot-namespaced noun.verb: `dispatch.start`, `handler.ok`,
  `actor.restart`, `audit.drain`.
- Level semantics: per-message chatter = `debug`; lifecycle = `info`;
  restart = `warn`; escalation / handler failure = `error`.
- Correlation: dispatch-path events carry `dispatchId`/`sessionId`/`hookEvent`;
  actor events carry `actorId`; durations come from `Log.Span` (`durMs`).
- Hot paths emit **summary** events (one per drain), never per-item.

**stdout purity** (the sacred contract — hook mode's stdout is the protocol
channel):

```bash
dotnet build dotnet/captainHook --nologo -v quiet
printf '{"hook_event_name":"UserPromptSubmit","session_id":"s1"}' | \
  dotnet dotnet/captainHook/bin/Debug/net10.0/captainHook.dll hook user-prompt-submit
```

stdout must be **exactly one JSON object**; every other byte belongs on
stderr or in the JSONL file.

## 4. Report

```
SHIPSHAPE REPORT
  tests    ✓|✗   25/25 twice · gaps: <behavior lacking a test, or none>
  docs     ✓|✗   ADRs current · flow drift: <doc: stale element, or none>
  logging  ✓|✗   violations: <file:line, or none>
```

For every ✗, list the concrete fix (file:line, what to change). All green →
the ship is shipshape. 🪝
