# captAInHook 🪝

*Lifecycle hooks as the composition primitive for agents.*

> **Building in public — feature-complete, pre-release.** The whole stack
> works and is dogfooded live on the maintainer's own sessions: dispatch
> engine, warm daemon + native shim, dispatch policy, exec-handler payloads
> (your own scripts, any language), management API, and a browser GUI with
> install UX. Validated on Linux/WSL2; macOS is a committed target, not yet
> exercised on real hardware. See [Status](#status) and
> [doc/roadmap.md](doc/roadmap.md).

A framework for splicing deterministic **or** LLM-backed subsystems into an AI
agent's loop at guaranteed seams — turning *"the model might call the tool"*
into *"the framework always runs the right subsystem, within a latency budget,
with a defined failure mode."*

Born from a simple observation: agents underuse the tools you give them — they
fall back to training-prior habits (grep instead of your language server,
answering from memory instead of your search API). Hooks run as **code**, on
every turn, regardless of what the model decides. That makes them the place you
put guarantees.

## Where it sits in the loop

An agent is a back-and-forth between a **host** (Claude Code, the Agent SDK) and
an **LLM**. captAInHook splices in at the **lifecycle seams** of that loop — the
fixed points where control passes back and forth — and runs your code there
whether or not the model would have asked for it.

```
   you ──prompt──►  agent host  ◄──context / tokens──►  LLM
                 (Claude Code / SDK)
                        │
                        │  at each lifecycle SEAM the host fires a hook —
                        │  code the model can't route around
                        ▼
     ═══ captAInHook ══════════════════════════════════════════
        dispatch at the seam:
          fan out handlers ─ under a latency budget ─
          fail-open / fail-closed ─ supervised (crash → restart)
          └─► merge to ONE Effect
     ═══════════════════════════════════════════════════════════
                        │
                        ▼   Inject · Decide · Replace · Background · Noop
              spliced back into the agent's loop
```

The seams span one turn — each is a place to run a guarantee:

```
● SessionStart (Inject) → ● UserPromptSubmit (Inject) → ⟨LLM⟩ → ● PreToolUse → (tool) → ● PostToolUse → ⟨LLM⟩ → ● Stop (Background) → ● SessionEnd (Background)
```

| seam | what a hook does there |
| --- | --- |
| `SessionStart` / `UserPromptSubmit` (Inject) | inject context before the model sees the prompt |
| `PreToolUse` | gate the call — allow / deny / ask, or modify its input |
| `PostToolUse` | annotate or replace the tool's result |
| `Stop` / `SessionEnd` (Background) | fire-and-forget: audit, memory writes |

Two things make a hook different from a tool the model *might* call:

- **The invocation is guaranteed.** The host fires the seam; the model can't skip
  it. That turns *"the model might call the tool"* into *"the framework always runs
  the subsystem"* — which is why hooks are where guarantees live.
- **Unbounded in what it does, bounded in how it touches the loop.** A handler may
  do *anything* — any I/O, any service, a full LLM-backed agent — but it affects the
  loop only by returning **one `Effect`** from a closed set, gated per event.

It isn't sandboxed, and that's deliberate: the safety comes from **rails around the
code** rather than walling the code off — a latency budget, a fail-open/closed
policy, and a supervised actor per handler. See [DESIGN.md](DESIGN.md) for the full
thesis and the per-event effect contracts.

## Install

Targets **Linux** (incl. WSL2 — the lived-in platform) and **macOS**
(code-complete, awaiting real-hardware validation). No runtime dependencies:
the engine ships self-contained (bundled .NET runtime) and the shim is a
native binary. GitHub Releases with prebuilt artifacts are the intended
channel; until the first release is tagged, build from source:

```sh
# prereqs (build-time only): .NET 10 SDK + clang (links the native shim)
git clone https://github.com/LoganBresnahan/captAInHook && cd captAInHook

RID=linux-x64        # macOS: osx-arm64 (Apple silicon) or osx-x64
dotnet publish dotnet/captainHook/captainHook.csproj -c Release -r $RID \
  --self-contained -p:PublishSingleFile=true -o ~/.captainHook/bin
dotnet publish dotnet/captainShim/captainShim.csproj -c Release -r $RID -o /tmp/shim \
  && cp /tmp/shim/captainShim ~/.captainHook/bin/
cp -r ui ~/.captainHook/bin/ui        # the committed GUI assets
```

Then wire the hook commands into `~/.claude/settings.json` (Claude Code's
hooks config) — each event you want worked points at the shim:

```
~/.captainHook/bin/captainShim hook user-prompt-submit
~/.captainHook/bin/captainShim hook pre-tool-use
```

From there: `~/.captainHook/bin/captainHook ui` opens the GUI (live traces,
supervision, policy editor, and handler install); payloads register in
`~/.captainHook/handlers.json` — by hand or through the GUI — and
`examples/payloads/` has working scripts to start from (a resident
retriever, a memory logger, and the two payloads dogfooded live on the
maintainer's own hooks: a git-bearing injector and a deploy guard).

**Nice-to-haves:**

- **macOS: `brew install util-linux`** (provides `setsid`). Without it,
  payload kill discipline degrades from process-group kills to a tree walk —
  everything still gets killed except a payload's *re-parented* background
  children (a script that does `something &` then exits). Flagged loudly per
  spawn (`pgroup=false` in the trail) either way.
- **A `$XDG_RUNTIME_DIR`** (standard on systemd Linux): rendezvous files land
  on per-user tmpfs. Absent (macOS default), they fall back to
  `~/.captainHook/` — fine, just not RAM-backed.

## Status

**Feature-complete, pre-release.** Everything below works today and runs live
on the maintainer's own Claude Code sessions. Linux/WSL2 is validated by the
test suite and live use; macOS support is implemented per
[ADR-0012](doc/adr/0012-distribution-and-platform-targets.md) but not yet
exercised on real hardware.

### Works today (dogfooded live)

- **Hook dispatch core (C#)** — registry → concurrent fan-out under a latency
  budget → fail-open/fail-closed → deterministic effect merge; each handler
  runs inside a supervised F# worker actor
  ([ADR-0002](doc/adr/0002-handlers-as-supervised-actors.md)). Harness-agnostic
  via declarative harness specs — `claude-code` is the built-in default (stdin
  JSON in, one effect JSON on stdout)
  ([ADR-0003](doc/adr/0003-declarative-harness-registry.md)).
- **Actor/supervision layer (F#)** — MailboxProcessor actors under a
  hand-rolled one_for_one supervisor (restart intensity on an injectable
  monotonic clock, escalation), plus a bounded-Channels hot-path actor.
  Decision record: [ADR-0001](doc/adr/0001-actor-runtime-fsharp-hybrid.md).
- **Warm daemon + native shim** — a long-lived daemon serves hooks over a
  versioned Unix socket; the deployed hook command is a Native-AOT shim
  (~16ms per warm hook vs ~140ms cold JIT), with at-most-once dispatch,
  graceful drain, idle self-exit, and a wire-skew guard so mismatched
  artifacts fail safe to a collapsed in-process run
  ([ADR-0004](doc/adr/0004-daemon-topology.md)).
- **Dispatch policy** — a user-editable `~/.captainHook/dispatch.json` decides
  per-event / per-handler / per-project whether an arriving hook gets worked
  (the hook is always *answered*); hot-reloaded, malformed ⇒ deny-all loudly
  ([ADR-0006](doc/adr/0006-dispatch-policy.md)).
- **Management API** — loopback HTTP + SSE on the daemon (bearer-token auth):
  status, handlers, harnesses, policy read/write, and a live event stream
  tailing the trail with lossless resume
  ([ADR-0007](doc/adr/0007-management-api.md)).
- **Browser GUI** — served by the daemon at `/ui`, opened via `captainHook ui`:
  live dispatch traces, supervision view, policy editor, harness registry —
  observability-first, driven end-to-end by Playwright
  ([ADR-0008](doc/adr/0008-management-gui.md)).
- **Exec handlers — your processes as payloads** — register any command in
  `~/.captainHook/handlers.json` (any language; strict JSON wire on
  stdin/stdout): `oneshot` spawn-per-dispatch or `resident` daemon-held warm
  children with readiness handshake, per-handler budgets, a stripped env
  allowlist, group-kill discipline, hot reload, and orphan detection.
  Working retriever/memory demo scripts in `examples/payloads/`
  ([ADR-0010](doc/adr/0010-exec-handlers.md)).
- **Install UX + trust model** — the GUI installs/edits/uninstalls handlers
  behind a verbatim-confirm panel (the exact command, args, env, and budget
  shown before anything is written), with enable/disable toggles composed
  from dispatch policy; same-user trust boundary recorded in
  [ADR-0011](doc/adr/0011-hook-trust-model.md).
- **Runtime-free distribution** — the engine ships single-file
  self-contained (no .NET install needed) and the shim is Native AOT;
  Linux + macOS ([ADR-0012](doc/adr/0012-distribution-and-platform-targets.md)).
- **Structured logging** — one JSONL event stream with dispatch/actor
  correlation (`~/.captainHook/logs/`), human one-liners on stderr, stdout
  kept pure for the hook protocol.
- **Tests** — xunit suite (620+, green twice in a row as the ship bar) plus
  web unit tests and Playwright E2E; the `/shipshape` skill verifies
  coverage, docs, and logging conventions.

### Deliberately not built / deferred

- **Trail rotation** — the JSONL trail grows unbounded (design recorded in
  [ADR-0009](doc/adr/0009-trail-rotation.md), deferred until growth bites).
- **Sandboxing untrusted payloads + a community registry** — parked until
  third-party code distribution exists; the recorded shape is per-payload
  containers ([ADR-0011](doc/adr/0011-hook-trust-model.md) d7,
  `doc/scratch.md`).
- **Desktop shell / TUI / Windows-native / other-runtime ports** — decided
  out, not merely postponed: browser-only, Linux+macOS, .NET core with
  any-language *payloads* carrying the polyglot story
  ([ADR-0012](doc/adr/0012-distribution-and-platform-targets.md)).

Maps of the system live in [doc/flow/](doc/flow/); decisions in
[doc/adr/](doc/adr/); direction in [doc/roadmap.md](doc/roadmap.md). The
`/shipshape` skill verifies tests, docs, and logging are in order. The
design thesis — why hooks, why these rails — is [DESIGN.md](DESIGN.md).

## License

[Apache-2.0](LICENSE).
