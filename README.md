# captAInHook 🪝

*Lifecycle hooks as the composition primitive for agents.*

A framework for splicing deterministic **or** LLM-backed subsystems into an AI
agent's loop at guaranteed seams — turning *"the model might call the tool"*
into *"the framework always runs the right subsystem, within a latency budget,
with a defined failure mode."*

Born from a simple observation: agents underuse the tools you give them — they
fall back to training-prior habits (grep instead of your language server,
answering from memory instead of your search API). Hooks run as **code**, on
every turn, regardless of what the model decides. That makes them the place you
put guarantees.

## Status

v0 on the .NET runtime:

- **Hook dispatch core (C#)** — registry → concurrent fan-out under a latency
  budget → fail-open/fail-closed → deterministic effect merge. Wire-compatible
  with Claude Code hooks (stdin JSON in, one effect JSON on stdout).
- **Actor/supervision layer (F#)** — MailboxProcessor actors under a
  hand-rolled one_for_one supervisor (restart intensity on an injectable
  monotonic clock, escalation), plus a bounded-Channels hot-path actor.
  Decision record: [ADR-0001](doc/adr/0001-actor-runtime-fsharp-hybrid.md).
- **Structured logging** — one JSONL event stream with dispatch/actor
  correlation (`~/.captainHook/logs/`), human one-liners on stderr, stdout
  kept pure for the hook protocol.
- **Tests** — 25 xunit tests; the bar is green twice in a row.

Maps of the system live in [doc/flow/](doc/flow/); decisions in
[doc/adr/](doc/adr/). The `/shipshape` skill verifies tests, docs, and
logging are in order.

The core remains a **one-spec / N-runtime harness**: the same contracts are
planned in Node (event loop) and BEAM (actors) to compare concurrency
philosophies on one real workload. See [DESIGN.md](DESIGN.md).
