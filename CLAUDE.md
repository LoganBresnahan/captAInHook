# captAInHook 🪝

Lifecycle hooks as the composition primitive for AI agents: splice deterministic
*or* LLM-backed subsystems into an agent's loop at guaranteed seams. See
[README.md](README.md) for the pitch, [DESIGN.md](DESIGN.md) for the thesis.

**This file is the always-loaded static layer — invariants + where to look. It is
NOT project state.** What shipped and what's next lives in git log and
[doc/roadmap.md](doc/roadmap.md); rationale in [doc/adr/](doc/adr/); mechanics in
[doc/flow/](doc/flow/). Those are authoritative and current; this file and memory
are as-of-write-time and can drift. When in doubt, read the code.

## Start here

- **`/orient`** — read-only session-start bearing: reconstructs "you are here" from
  commits + roadmap + docs. Run it first in a fresh window.
- **`/shipshape`** — verifies the repo is in order (tests / docs / logging). Run
  before commits. It *proposes* fixes; it does not apply them unless asked.
- **`/deploy`** — ships the current build (BOTH artifacts: native `captainShim` +
  apphost `captainHook`, staged and swapped together) to the live hook
  installation `~/.captainHook/bin` and verifies the daemon warm path. The
  ONLY sanctioned way to touch the live deployment; needs the suite green twice.

## Architecture in one breath

Five projects under `dotnet/`, one language per layer, two `Exe`s:

- `captainHook/` — **C#** engine (`Exe`, JIT). Dispatcher, domain model, harness
  registry, handlers, `Program.cs` entrypoint; daemon + collapsed + dev-shim modes.
- `captainShim/` — **C#** thin hook shim (`Exe`, **Native AOT**). What the deployed
  hook command points at: forward to the warm daemon, or delegate to the
  co-located engine. References the wire lib ONLY. (ADR-0004 decision 7.)
- `captainHookWire/` — **C#** wire contract, a leaf library (BCL-only,
  `IsAotCompatible`): frame codec, content identity, rendezvous, shim client,
  spawner, argv contract, JSONL log schema seam.
- `captainHookActors/` — **F#** actor / supervision runtime. A leaf library.
- `captainHookTests/` — **C#** xunit suite; references all of the above.

**The dependency arrows point one way — never invert them:**
`captainShim → captainHookWire ← captainHook → captainHookActors`.
Both leaves reference *nothing*. The F# lib must never see `HookEvent` /
`Effect` / `IHandler` — those live in the host; rich F# types (DUs,
`AsyncReplyChannel`) stay inside the F# assembly and C# only ever sees plain
methods returning `Task`. The shim must never reference the host or the F#
lib — that absence is exactly what makes it Native-AOT-compile trivially,
and an added reference walks straight into the native image.
(ADR-0001, ADR-0002, ADR-0004 decision 7 amendment.)

## Sacred invariants — breaking one breaks the project

1. **stdout is the hook protocol channel.** In hook mode, *exactly one* effect-JSON
   object may reach stdout — nothing else, ever. Logs, the human trace, errors, an
   unknown `--harness` (→ exit 1) all go to **stderr** or the JSONL file. All
   diagnostics flow through one seam per layer: engine + actors through
   `CaptainHook.Actors.Log`; wire-lib code through `WireLog` (the engine binds it
   to `Actors.Log`, `captainShim` binds it to the JSONL appender — the two
   renderings are pinned byte-identical by golden tests, `WireJsonlTests`). No
   `Console.*` outside the two `Program.cs` files and `Demo/`, no
   `printfn`/`eprintfn` outside `Logging.fs`. (doc/flow/hook-dispatch.md.)
2. **Monotonic clock for control-flow timing.** Interval / restart-window / deadline
   math uses an injectable monotonic source (`Stopwatch`, `Environment.TickCount64`)
   — never `DateTime.UtcNow` for subtraction or comparison. Wall clock is
   display/timestamps only. Tests use `FakeClock` + Stopwatch deadlines and
   time-bounded async asserts (`PollUntilAsync`/`WaitAsync`), never real sleeps.
   (doc/flow/actor-supervision.md.)
3. **Zero new runtime dependencies.** Host, F# lib, wire lib, and shim are BCL +
   `FSharp.Core` only; only `captainHookTests` carries `PackageReference`s.
   (`PublishAot` is a property, not a package; the clang toolchain it needs at
   publish is a build-host fact — doc/platform.md.) A new runtime dependency —
   or a new project / runtime / changed contract — needs an ADR.
4. **The dependency arrows** (above): shim → wire ← host → actors, one way,
   no back-references, both leaves reference nothing.

## Build / test / run

**No `.sln`, no `global.json`, no Makefile** — a bare `dotnet build`/`test` at the
repo root fails (MSB1003). Always target a project file:

```sh
dotnet build dotnet/captainHook/captainHook.csproj            # builds host + F# lib transitively
dotnet test  dotnet/captainHookTests/captainHookTests.csproj  # xunit suite
dotnet test  dotnet/captainHookTests/captainHookTests.csproj --filter "FullyQualifiedName~<Name>"
printf '{"prompt":"hi"}' | dotnet run --project dotnet/captainHook -- hook user-prompt-submit
dotnet run --project dotnet/captainHook -- actors-demo        # drive the F# actor layer directly
```

All projects target `net10.0` (.NET 10 SDK). The shim's native form exists only
at publish (`dotnet publish dotnet/captainShim/captainShim.csproj -c Release -r
linux-x64`); tests exercise `ShimMain` in IL form through its injected streams.
**Ship bar: the suite is green twice in a row** (the flaky guard).
`CAPTAINHOOK_PROBE=1` opts a demo second handler into UserPromptSubmit; off by
default so live prompts aren't taxed.

## Conventions when you touch code

**Handlers** (`Core/Model.cs`, `Handlers/`): a handler affects the loop *only* by
returning one `Effect` from the closed set (`Inject`/`Decide`/`Replace`/`Background`/
`Noop`) — never by side-effecting the host. Register *all* handlers before
constructing the `Dispatcher` (it snapshots the registry). Stateful handlers use the
factory `On(...)` overload so a supervised restart yields fresh state. Honor `ctx.Ct`
on every await (the latency budget cancels through it). Do fire-and-forget work via
`Effect.Background`. Default is fail-open; choose `FailMode.Closed` only for
authz/policy gates. Registration order is load-bearing. (ADR-0002.)

**Harnesses** (`Core/Harness.cs`, `harnesses/*.json`): add a harness by dropping a
JSON `HarnessSpec` — an embedded default in `harnesses/*.json` (ships *inside* the
assembly via `GetManifestResourceStream`) or a user override in
`~/.captainHook/harnesses/` — **not** a class. House pattern: *declare capabilities
in data, provide lookup in code.* Data selects among a CLOSED coded adapter set;
config never becomes a template language. A genuinely new wire format = one
`IResponseAdapter` class + its name in `Known`. An invalid embedded spec is a build
defect (throws); an invalid user override is warned-and-skipped, never fatal.
(ADR-0003.)

**F# compile order:** files in `captainHookActors.fsproj` compile in the strict
order of the `<Compile>` list — dependencies first; `Logging.fs` stays first so both
layers share the `Log` surface. Adding a file means hand-inserting it at the right
spot.

**`dotnet/experiments/`** (akka / csharp / fsharp-actors) is **frozen ADR-0001
evidence** — outside the build graph, exempt from every gate. Don't build, test,
reference, or "fix" it; its wall-clock supervisor is the anti-pattern the production
lib replaced.

## Docs discipline

- **Decisions → ADR** (`doc/adr/NNNN-slug.md`, Nygard-style). A new dependency,
  project, runtime, changed contract/architecture, or a pattern adopted/rejected
  needs an ADR (or an update marking one superseded). Implementation detail is not a
  decision.
- **Mechanics → flow doc** (`doc/flow/*.md`): ASCII diagram + why-prose + a "Ground
  truth" table of files/symbols/events/tests. Flow docs must match code.
- **What's next → [doc/roadmap.md](doc/roadmap.md)** only (Now / Next / Later). Check
  the box in the commit that lands the item.
- **OS facts → [doc/platform.md](doc/platform.md)**: constraints the environment
  imposes (socket path caps, lock semantics, per-OS availability), each tied to what
  leans on it. The lane rule: the environment *imposes* → platform.md; we *chose* →
  ADR; the code *does* → flow doc.
- **[doc/scratch.md](doc/scratch.md)** is an informal idea list — non-authoritative;
  don't cite it as a plan.

## Commits

`doc:` prefix for docs-only changes; imperative `<what>: <why>` for code (e.g.
"Converge dispatcher and actor layer: handlers run as supervised actors"). The commit
that lands a roadmap item checks its box in the *same* commit and cites the item +
ADR in the body. End every commit with a `Co-Authored-By: Claude <model>` trailer
(the model string is per-session).

## Names & paths — four spellings coexist, don't typo them

- repo / dir: `captAInHook`
- namespaces: `CaptainHook.Core` / `.Handlers` / `.Demo` (C# engine), `CaptainHook.Wire`
  (wire lib), `CaptainHook.Shim` (AOT shim), `CaptainHook.Actors` (F#)
- env vars: `CAPTAINHOOK_LOG`, `CAPTAINHOOK_LOG_STDERR`, `CAPTAINHOOK_HARNESS_DIR`, `CAPTAINHOOK_PROBE`, `CAPTAINHOOK_COLDSTART`, `CAPTAINHOOK_IDLE_MS`
- runtime home: `~/.captainHook/` — JSONL logs in `logs/`, user harness overrides in `harnesses/`; daemon rendezvous files (socket/lock/pid) in `$XDG_RUNTIME_DIR/captainHook/` when set, else here

⚠ `~/.captainHook/` is the **same tree the live-deployed hook uses.** A dev run or a
test that writes the real sinks pollutes your actual logs — tests swap the `Log` sink
(never call `Log.ResetSink()` mid-suite) and pass an explicit harness dir.
