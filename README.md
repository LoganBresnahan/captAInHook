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

Early. v0 = **framework core + an `echo` handler**.

Runtime is deliberately undecided: captAInHook is being built as a
**one-spec / N-runtime harness** to compare three concurrency philosophies on
the same real workload — BEAM (actors) · Node (event loop) · .NET (work-stealing
thread pool). See [DESIGN.md](DESIGN.md).
