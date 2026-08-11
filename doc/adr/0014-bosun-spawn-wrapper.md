# ADR-0014 — Own the spawn seam: bosun replaces setsid(1)

**Status:** Accepted *(2026-08-11; owner decision after the ADR-0013 language
analysis collapsed to "the only cross-OS defect a native layer uniquely fixes is
the spawn seam.")* — **implemented 2026-08-11**: all three planned slices
landed against bosun **v0.1.0** (roadmap item 18). The seam, the pin, and the
docs are in; d5 (`--pdeathsig`) remains deferred as decided. What is NOT yet
done is the live cutover — `/deploy` must run for the first rung to be in force
on the maintainer's own hooks; until then a dev tree still resolves `setsid(1)`
by design.
**Date:** 2026-08-11
**Amends:** [ADR-0010](0010-exec-handlers.md) decision 6 (the kill discipline's
spawn half) and [ADR-0012](0012-distribution-and-platform-targets.md) N2's
accepted macOS baseline.
**Companion repo:** <https://github.com/LoganBresnahan/bosun> (MIT, Zig).
**Evidence:** [doc/port/go-port-inventory.md](../port/go-port-inventory.md)
§ Probes; [ADR-0013](0013-tri-platform-and-the-language-question.md) Findings 3–6.

## Context

The exec-handler kill discipline (ADR-0010 d6) requires every payload to be
spawned as its own **process-group leader**, so `kill(-pgid)` reaches
grandchildren that reparent to init — which the `/proc` tree walk structurally
cannot (platform.md § Process groups). .NET exposes no `setpgid`/`setsid` seam
around its fork/exec (`ProcessStartInfo.CreateNewProcessGroup` throws off
Windows), so `ProcessGroup` probes PATH for the `setsid(1)` **utility** and
prefixes every spawn with it.

Two defects follow from renting that wrapper instead of owning it:

1. **Stock macOS ships no `setsid(1)`** (it is util-linux), so on the second
   committed target every exec spawn runs `pgroup=false` — tree-walk kills
   only, the reparented-grandchild hole open. ADR-0012 accepted this as the
   macOS baseline (N2); platform.md documents it as a standing ⚠.
2. The degrade is decided by a **PATH probe at engine start**
   (`ProcessGroup.Probe()`), so the capability silently depends on the host's
   package set — including minimal container images.

The ADR-0013 investigation established the general fact underneath: the calls
that matter (`setsid(2)`, `prctl(PR_SET_PDEATHSIG)`) must run **inside the
child, between fork and exec** — a window no managed runtime (.NET, BEAM, JVM)
exposes, and one FFI cannot reach (P/Invoke affects the *calling* process).
The minimal ownable form is not a library but a **wrapper executable** that
sets attributes on itself and then execs the payload in place, preserving the
pid. ADR-0013's Windows drop (its decision 1 reversed by the owner, 2026-08-11
direction) leaves this as the *only* cross-OS defect a native component
uniquely fixes.

That wrapper now exists: **bosun** — ~130 lines of Zig, `setsid()` by syscall
(present on macOS; only the util-linux *binary* is missing there),
`--pdeathsig <SIG>` via `PR_SET_PDEATHSIG` on Linux with a `--parent` guard
closing the fork-to-prctl race, loud refusal (exit 125) where a capability is
unsupported, `env(1)` exit-code conventions. Verified against a real kernel:
group kill takes a reparented grandchild; the kernel reaps the child on parent
SIGKILL with no reaper involved; musl-static Linux binaries (28 KB, run in
`FROM scratch`) and libSystem macOS binaries, all four targets cross-compiled
from one Linux host; smoke suite green under bash and dash. A prospective
second consumer exists: pharos-mcp's ADR-030 deferred exactly this shim
("a NIF + zig shim is too much surface") — bosun is that shim as an
executable, no NIF required.

## Decision

1. **`bosun` becomes the spawn prefix for exec-handler payloads**, replacing
   `setsid(1)`. The contract is unchanged where it matters: bosun execs the
   payload **in place**, so `Process.Id` remains the payload's pid and
   pgid == sid == pid — `ProcessGroup.Term`/`TermThenKillAsync`, liveness
   probes, and every existing test keep their semantics.

2. **Resolution order, loud at every step:** co-located `bosun` (the deploy
   dir, `AppContext.BaseDirectory` — same co-location rule as the engine
   fallback) → `setsid(1)` from PATH (today's behavior, kept for dev trees and
   tests) → `pgroup=false` degrade (unchanged, still flagged per-spawn). The
   trail's spawn line grows a `spawner=bosun|setsid|none` field so the active
   rung is observable, and `/deploy`'s verification treats a missing co-located
   bosun as a **staging defect** — on a live deploy the first rung must win.

3. **bosun ships as the fourth deploy artifact**, staged and swapped with the
   other three. It is native ⇒ carries no MVID ⇒ **invisible to the
   content-identity hash by the same documented rule as `captainShim`**
   (platform.md § Native AOT); co-location carries version coherence. Note the
   benign consequence: a bosun-only swap does not roll the socket identity, and
   takes effect on the *next spawn* (bosun is re-exec'd per spawn, never held
   by the warm daemon) — the same effective-next-use shape as a harness-spec
   edit.

4. **Distribution: pinned release fetch, never "latest."** `/deploy` (and CI,
   when it exists) downloads the tagged release asset for the host target from
   the bosun repo and verifies it against the published `SHA256SUMS` before
   staging. The pin (tag + checksum) is recorded in the deploy skill. Rationale
   against invariant 3: bosun is first-party code, not a runtime
   `PackageReference`; the network touch is deploy-time only; and an unpinned
   fetch would let deploy bytes change behind an unchanged source tree, which
   the build-determinism invariant cannot tolerate.

5. **`--pdeathsig` is explicitly DEFERRED for captAInHook.** The capability
   (kernel kills payloads when the daemon dies, however it dies) is attractive
   — it would make `doctor`'s orphan sweep unnecessary on Linux — but pdeathsig
   fires when the parent's forking **thread** exits, not the parent process,
   and the engine spawns from .NET thread-pool threads, which idle out. Using
   it today would spuriously kill healthy payloads. Revisit trigger: a
   dedicated long-lived spawn thread lands, or live orphan pressure exceeds
   what `doctor` absorbs. (`--parent` narrows the startup race only; it cannot
   close the pooled-thread hazard. bosun's README documents this edge.)

## Rejected alternatives

| alternative | disposition |
|---|---|
| **Document `brew install util-linux` as the macOS answer** | Rejected — pushes a per-user install step onto every Mac user for a capability the engine silently loses when they skip it, and buys no path to pdeathsig later. The one-line cost comparison was fair when the wrapper didn't exist; it now does. |
| **Managed PAL only (C# + P/Invoke)** | Rejected *for this defect* — structurally cannot reach the child-side window; P/Invoke sets attributes on the daemon. Remains the right shape for everything else ADR-0013 discussed (lease, identity, `make_private`), none of which needs native code. |
| **Full native PAL library (`liboshal`, ~15 functions)** | Rejected as scope — with Windows out (ADR-0013 d1 reversed to Linux+macOS+containers), the spawn seam is the only capability that *requires* native code. A library, dlopen, and a C ABI for things P/Invoke already does is cost without benefit. bosun is the minimal ownable form. |
| **Vendor bosun's source; build with Zig at deploy time** | Rejected — adds a Zig toolchain requirement to every dev/deploy machine. Prebuilt pinned artifacts keep the toolchain in bosun's CI where it belongs. Vendoring the *binary* into this repo remains the documented fallback if release-fetch availability ever becomes a problem. |
| **Windows parent-side Job Object support in bosun** | Out of scope by shape and by target: `KILL_ON_JOB_CLOSE` must be applied by the parent, which an exec wrapper cannot do; and Windows-native is off the target list. Recorded in bosun's README as a non-goal with the reason. |

## Consequences

### Positive

- **The macOS `pgroup=false` baseline ends.** The kill discipline's spawn half
  becomes one implementation across Linux and macOS, and ADR-0012 N2's ⚠ in
  platform.md § Process groups is retired when this lands.
- The capability stops depending on the host's package set — minimal container
  images included (bosun's Linux form is musl-static; its CI smoke-tests
  inside alpine).
- `BinaryNotFound` reporting improves incidentally: `setsid` masked a missing
  payload as exit 127 with setsid's stderr (`ExecHandler.cs` documents the
  degradation); bosun's 127/126/125 contract is pinned by its own suite.
- A second project (pharos-mcp) can adopt the same artifact for its deferred
  orphan-LSP fix, NIF-free — shared infrastructure instead of a house
  workaround.

### Negative

- **N1 · A fourth artifact and a supply chain.** `/deploy` gains a fetch +
  checksum step and a pin to bump deliberately. Mitigated by decision 4's
  pinning discipline and the vendoring fallback.
- **N2 · A second language in the org.** Zig, in its own repo with its own CI
  (pinned ziglang.org toolchain, sha256-verified). captAInHook's own build
  remains .NET-only.
- **N3 · The dev-tree rung differs from the live rung.** A dev run without a
  staged bosun uses `setsid(1)` — fine on Linux, degraded on a stock Mac.
  Decision 2's trail field keeps the active rung visible; the macOS dev story
  is only fully fixed when running against a staged deploy dir.
- **N4 · macOS remains unproven** until a real Mac run (ADR-0012 N2's larger
  clause stands) — bosun's macOS binaries are cross-compiled and CI-tested on
  GitHub's macOS runners, which is stronger evidence than the engine's own
  macOS branches currently have, but the integrated path awaits a Mac.

## Implementation plan

*Small; not decomposed via `/adr-plan`. Rough order:*

1. `bosun-resolution-seam` — `ProcessGroup`/`ExecHandler`: co-located-bosun →
   PATH-setsid → degrade resolution replacing the bare PATH probe;
   `spawner=` field on the `exec.spawn` trail line; tests for all three rungs
   (the existing 22 `SetsidPath is null` guards collapse into rung-explicit
   cases).
2. `deploy-fetch-and-stage` — the pinned fetch + `SHA256SUMS` verify in
   `/deploy`, staging bosun as the fourth artifact; verification fails the
   deploy if the co-located rung would not win.
3. Docs — platform.md § Process groups (retire the macOS ⚠, add bosun's row),
   `doc/flow/exec-payloads.md` (spawn-prefix mechanics + resolution order),
   ADR-0010 d6 amendment note pointing here.

## Ground truth

| decision | lives in |
|---|---|
| the wrapper itself | <https://github.com/LoganBresnahan/bosun> (src/main.zig; test/smoke.sh is the behavioral contract) |
| d1/d2 — the three-rung seam | `ProcessGroup.SpawnPrefix` + `ProcessGroup.Resolve(baseDir, PATH)` + the process-wide `ProcessGroup.Prefix` (`dotnet/captainHook/Handlers/ProcessGroup.cs`); argv assembled per rung in `ExecHandler.BuildPsi` (bosun's mandatory `--`); `ExecHandler.Pgrouped` reads `Prefix.Pgroup` |
| d2 — trail visibility | `spawner=bosun\|setsid\|none` on `exec.spawn`, emitted by BOTH modes (`ExecHandler.HandleAsync`, `ResidentExecHandler`); the degrade keeps its existing `pgroup=false` |
| d1/d2 pinned by | `SpawnPrefixTests` — rung order (co-located bosun beats an available setsid), non-executable fall-through, PATH scan, both-absent degrade, null inputs, per-rung argv, and the bosun `--` contract driven through a REAL in-place exec (a dropped terminator fails there, not on a live deploy). The 22 former `SetsidPath is null` guards are now rung-explicit (`!ProcessGroup.Prefix.Pgroup`) |
| d3/d4 — fourth artifact + pin | `.claude/skills/deploy/SKILL.md` § 1a (pinned tag + `SHA256SUMS` verify), § 1b (`install -m 0755 … $STAGE/bosun`), § 3 (`spawner=bosun` or staging defect) |
| d5 deferral | this ADR + bosun README § Sharp edges |
| platform facts | `doc/platform.md` § Process groups (the fork→exec window, exec-in-place identity, bosun's macOS `setsid()`, the retired ⚠) |
| mechanics | `doc/flow/exec-payloads.md` § The spawn prefix |
