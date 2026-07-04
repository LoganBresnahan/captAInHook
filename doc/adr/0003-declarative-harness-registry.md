# ADR-0003 — Declarative harness registry: harness specifics become data, adapters stay code

**Status:** Accepted
**Date:** 2026-07-04

## Context

A *harness* is the agent host driving us — Claude Code today, anything
speaking JSON-over-stdio tomorrow. Until now, everything Claude-specific
lived as code in [`Program.cs`](../../dotnet/captainHook/Program.cs): the
request field names (`hook_event_name`, `session_id`, `cwd`), the
kebab→Pascal event canon, and a private `ClaudeCode` class that serialized
our `Effect` into Claude's `hookSpecificOutput` wire format. Worse, the
*install* knowledge — which config file to edit, what command line to write
into it — lived entirely out of repo, in the head of whoever wired the live
deployment.

That was fine while Claude Code was the only host. But the management-API and
GUI [roadmap](../roadmap.md) items will build inventory, install, and
uninstall operations *on top of* whatever encodes this knowledge — and if
they are built against hardcoded Claude paths, those paths fossilize into the
product's API surface permanently. The product vision is harness-plural:
hooks as the composition primitive for *agents*, not for one agent host.

One constraint shapes everything: the **live deployment**.
`~/.claude/settings.json` runs this binary on every real user prompt, with no
new flags — that invocation must keep producing byte-identical stdout.

## Decision

Introduce a **declarative harness registry**
([`Core/Harness.cs`](../../dotnet/captainHook/Core/Harness.cs)): each harness
is described by a JSON `HarnessSpec`, and the spec — not code — drives
request parsing, effect capability gating, and response serialization. The
principle throughout is *declare capabilities in data, provide lookup in
code*: data selects among coded behaviors; it never becomes a program.

1. **`HarnessSpec` — one harness, fully described.** `name` (the registry
   key), `request` field names (defaulting to the Claude names, so a minimal
   spec omits the block), `response.adapter` (selects a coded adapter by
   name), per-event allowed effect kinds, and an `install` block.
   `HarnessSpec.TryParse` validates moby-style: collect *every* violation as
   a clear error string, accept all-or-nothing, never throw on bad data.
2. **`HarnessRegistry` — embedded defaults + validated user overrides.**
   Layer 1: specs shipped inside the assembly
   ([`harnesses/claude-code.json`](../../dotnet/captainHook/harnesses/claude-code.json),
   compiled in via `<EmbeddedResource>`); an invalid embedded spec is a build
   defect and fails loudly. Layer 2: `*.json` files from
   `CAPTAINHOOK_HARNESS_DIR` (else `~/.captainHook/harnesses`) — a valid file
   whose `name` matches an embedded spec **replaces it wholesale**, a new
   name adds a harness, and an invalid file logs `harness.specInvalid` and is
   skipped: a bad override must never crash the live hook. Loaded once,
   `Lazy`-cached.
3. **A CLOSED, coded adapter set.** `ResponseAdapters` maps names to
   `IResponseAdapter` implementations: `claude-hook-json` (the old
   `ClaudeCode` serializer moved verbatim, protecting byte-identical output)
   and `generic-json` (a neutral envelope proving a second harness needs zero
   `Program.cs` changes). Specs pick *which* adapter; code defines *what* it
   emits. Config never becomes a template language.
4. **Per-event capability gate, warn + downgrade.** After `Merge`, the single
   merged effect passes `Harness.ApplyCapabilityGate`: an effect kind the
   spec never declared for that event logs `harness.effectUnsupported` and
   downgrades to `Noop` — never send a harness something it cannot represent.
   An event *absent* from the spec is permissively allowed with a
   `harness.eventUndeclared` debug line, so new events cannot silently eat
   effects. `Noop` always passes (it is the downgrade target — gating it
   would be circular), and `Background` never survives `Merge`, so it never
   reaches the gate.
5. **Install target as opaque data.** The `install` block (config file, hooks
   path, command-line template) rides through as a raw `JsonElement` — v1
   deliberately does not model it. It exists so the future management API
   reads install knowledge as *data* instead of encoding per-harness install
   code.
6. **CLI: `--harness <name>`, default `claude-code`.** The no-flag invocation
   — the live deployment — resolves the same embedded default and produces
   byte-identical stdout. An unknown name puts a clear message on stderr and
   exits 1 with **zero** stdout bytes, because stdout is the host's protocol
   channel.

**Pattern lineage.** This is the owner's established registry pattern applied
to a third domain. pharos-mcp's `src/pharos/config.gleam` contributes the
config shape: a typed `Config` with `defaults()`/`load()`/`cached()`
layering, `ServerOverride`/`LanguageOverride` records, and `tool_allowed`
gating requests against declared capability. deepseek-moby's
`src/models/registry.ts` contributes the registry shape: a `ModelCapabilities`
record per entry in a built-in `MODEL_REGISTRY`, with
`validateCustomModelEntry`/`registerCustomModels` admitting user entries only
after validation. LSP servers → models → agent harnesses: same pattern,
third domain.

Zero new runtime dependencies, as always: BCL + FSharp.Core only.

## Consequences

### Positive

- **No harness-string branches outside the registry.**
  [`Program.cs`](../../dotnet/captainHook/Program.cs) consults the resolved
  spec for parsing, gating, and serialization; nothing else in the host knows
  which harness is speaking.
- **A second harness is one JSON file** — dropped into the override
  directory or embedded — plus a coded adapter *only if* its wire shape is
  genuinely novel. `generic-json` proves the zero-code path end to end.
- **The live deployment was protected.** The default no-flag path produces
  byte-identical stdout; the Claude serializer moved, it did not change.
- **Install knowledge became data.** What used to live out-of-repo in a
  human's head now ships in the spec, ready for the management API to
  consume.

### Negative

- **Whole-file override only.** v1 has no deep merge: overriding one field of
  `claude-code` means restating the entire spec, which can drift from the
  embedded default across upgrades.
- **Novel wire shapes still require a coded adapter release.** The closed set
  is the point (see Alternatives), but it means a harness with a genuinely
  new response format waits on us.
- **The canonical event vocabulary is Claude-derived.** `SessionStart`,
  `UserPromptSubmit`, and friends *are* Claude's event names; the canon maps
  kebab-case onto them. A second *real* harness must translate its lifecycle
  onto this vocabulary until it forces a genuinely neutral one — a named
  revisit trigger below, deliberately not designed speculatively now.
- **The capability gate is warn + downgrade, not hard-fail.** Deliberate: a
  misdeclared spec should degrade a hook's effect (`Noop` + warning), not
  break a user's agent mid-session. The cost is that misdeclarations only
  surface in the log trail, not as errors.
- **`dispatch.start` does not carry the harness name.** Threading it through
  `DispatchAsync`'s public signature touches existing tests; deferred as a
  known follow-up.

## Alternatives considered

| Option | Why not (now) |
| --- | --- |
| Template-language responses (specs carry an output template the binary renders) | The Turing-complete config trap: templates grow conditionals, loops, and escaping rules, and every bug lands on the protocol channel the host parses. A closed adapter set keeps wire formats as testable code |
| Per-harness code plugins only (no spec, each harness is a class) | ~90% of harness differences are request field names and capability lists — that is data. A plugin per harness pays a code-release cost for every trivial variance and leaves nothing for the management API to introspect |
| Do nothing until harness #2 actually arrives | The management-API and GUI roadmap items would be built against Claude-hardcoded internals in the meantime, fossilizing those paths exactly when they become expensive to unwind |

## Revisit triggers

- **A second real harness lands** — re-derive the canonical event vocabulary
  from two concrete data points instead of one, and decide whether the
  Claude-derived names survive as the neutral core.
- **Install operations get built** (the management-API roadmap item) — the
  opaque `install` block meets reality; validate its schema against actual
  installs and give it a typed model then.
- **A spec needs merging semantics** — the first time someone wants to
  override one field of an embedded spec without restating the file,
  whole-file replacement has hit its limit.
