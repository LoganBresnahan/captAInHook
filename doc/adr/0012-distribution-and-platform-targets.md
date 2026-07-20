# ADR-0012 — Distribution & platform targets: Linux + macOS, single-file, no host runtime

**Status:** Accepted *(2026-07-20; owner decision in a design discussion the
same date. The direction is decided; the macOS implementation work below is
scheduled, not yet done — Linux/WSL2 remains the only lived-in target until it
lands.)*
**Date:** 2026-07-20

## Context

captAInHook ships today as a **framework-dependent** .NET build: the deployed
`captainHook` engine is a 78KB apphost whose `runtimeconfig.json` names
`Microsoft.NETCore.App 10.0.0` — it requires a .NET 10 runtime installed on the
host. Only `captainShim` ships self-sufficiently (3.8MB Native AOT, no runtime).
So the engine is not self-shipping, and the project is pinned to Linux/WSL2 in
two distinct ways worth separating:

1. **Runtime dependency** — the engine needs .NET installed. Orthogonal to OS.
2. **OS-specific code** — the payload-lifecycle and rendezvous plumbing.

The OS-specific surface, inventoried 2026-07-20 (platform.md is the full table):

| surface | Linux | macOS | Windows |
|---|---|---|---|
| kill discipline: `setsid` + `kill(-pgid)` (`ProcessGroup`) | ✓ | ✓ POSIX | ✗ |
| child/daemon identity: `/proc/<pid>/{stat,cmdline}` (`ChildRecords`, `Doctor`) | ✓ | **✗ needs `sysctl`** | ✗ |
| lock + unlink-while-held rendezvous (`DaemonRendezvous`) | ✓ flock | ✓ POSIX | **✗ can't delete open files** |
| UDS + `sun_path` cap, `PosixSignal` drain, loopback API | ✓ | ✓ POSIX | ≈ |
| .NET runtime | needs it | needs it | needs it |

The reading: **macOS is ~90% POSIX-portable already** — its only real gap is the
`/proc`-based identity code. **Windows is a genuine port** — the flock+unlink
rendezvous fundamentally doesn't work (Windows can't delete open files), plus no
`/proc`, plus different signal semantics. WSL2 is, and has been, the Windows
story.

Adjacent decisions this consolidates: items 7, 8, 11 dropped (2026-07-19/20 —
browser-only, .NET-core-only, no TUI); ADR-0011 shipped the trust model;
ADR-0010 made payloads user processes.

## Decision

1. **Target Linux and macOS. Windows-native is out of scope** (WSL2 is the
   Windows path). This retires platform.md's "parked" Windows column from a
   someday-maybe to a non-goal, and lets every design lean on POSIX without a
   Windows-portability tax.

2. **Ship self-contained, single-file — no host runtime.** The engine moves to
   `SelfContained=true` + `PublishSingleFile=true`: the .NET runtime is bundled
   into the executable (self-extracting on first run — the Burrito-equivalent),
   so a user needs nothing preinstalled. `captainShim` stays Native AOT (already
   runtime-free). `/deploy` stages the single-file engine + the AOT shim + the
   committed `ui/`, unchanged in shape. Bigger artifact, zero host dependency.

3. **Port the `/proc` identity code to a cross-platform seam for macOS.** Two
   sites read `/proc`: `ChildRecords.ProcStartTime` (`/proc/<pid>/stat` field 22)
   and `Doctor` (`/proc/<pid>/cmdline` for the daemon pid-reuse guard). Both get
   a macOS branch via `sysctl(KERN_PROC)` (process start time + argv) behind the
   existing injectable-path idiom, so the pid-reuse guard — the safety property,
   not a nicety — holds on both OSes. The kill path (`setsid` + `kill(-pgid)`)
   already works on macOS unchanged; `UiVerb`'s `/proc/version` WSL probe is
   cosmetic and degrades to "not WSL". This is the whole macOS port: ~2 files.

4. **Defer all containerization.** Neither flavor ships now:
   - *Daemon-in-a-container* (portability) — unnecessary once (2) + (3) make the
     daemon run natively on both targets. It was only ever the Windows escape
     hatch, and Windows is out of scope.
   - *Payload-in-a-container* (item 15 sandboxing) — stays parked with item 15,
     un-triggered until untrusted/third-party payloads exist (ADR-0011 d7).

## Rejected / deferred alternatives

| alternative | disposition |
|---|---|
| **Keep framework-dependent** | Rejected — "install .NET 10 first" is hostile to distribution and the reason the project felt runtime-tied. Single-file removes it for a bigger artifact, an easy trade. |
| **Native AOT the engine** | Deferred — same runtime-free benefit as single-file but a real fight with the engine's reflection surface (STJ DTO serialization). Single-file gets 90% of the value at ~0% of the risk. Revisit only if artifact size or cold-start bites. |
| **Daemon-in-a-container as the universal model** | Rejected as universal — on Linux (and POSIX macOS) it is *pure cost*: the agent runs on the host, so payloads would inhabit the container's world (its tools/filesystem/env, not the user's — cutting against ADR-0010's "user's own process"), the event `cwd` needs mounting, and the shim's co-located-engine fallback breaks. It buys portability we get natively. Kept only in spirit as the Windows escape hatch, and Windows is out of scope. |
| **Payloads each in their own container (item 15)** | Deferred, not rejected — this is the *right* sandboxing shape when it's needed, AND the easier lifecycle: `docker run`/`docker stop` (TERM→grace→SIGKILL via `--stop-timeout`) replaces `setsid`+`kill(-pgid)`, the container ID replaces `/proc` starttime identity (no pid-reuse guard), and teardown reaps grandchildren for free — the "runtime owns the lifecycle" property the BEAM gives pharos. If item 15's trigger fires, this is the path; the process-plumbing simplification is a bonus, not the driver. |
| **Rewrite the core in BEAM** | Rejected — BEAM's headline win is free supervision/monitoring/process-management (pharos proves it), but captAInHook's supervision layer is already designed, adversarially tested, and shipped (F#). Free-what-you-already-own is negative value, paid in a whole runtime + a shim/engine language boundary + Burrito packaging (and BEAM can't give a fast native shim, so it *still* wants a Go/Rust shim in front — two languages). |
| **Rewrite the core in Go** | Deferred, justified only by its *own* merits, never by portability (which is nearly free from here — decision 2+3). Real merits if pursued: true static binaries + trivial cross-compile (better shipping than .NET single-file or Burrito); `SysProcAttr{Setpgid:true}` is exactly the fork/exec seam .NET lacks, deleting the `setsid`-wrapper hack and the whole `Pgrouped` degrade path; on a Linux+macOS (both Unix) target the process code is ONE implementation, no `/proc`-for-kill. But it re-ports a supervision design already solved, and resurrects the DESIGN.md comparison thesis retired with item 11 (2026-07-19). If ever done: all-Go (one language, shared wire types), start with the shim (largest payoff, smallest blast radius). Not for portability alone. |

## Consequences

### Positive

- captAInHook becomes a genuine single-binary-per-OS tool on its two targets —
  the pharos-style "just a binary" property, minus a VM (Native AOT shim) and
  minus a runtime install (single-file engine).
- The Windows-portability tax is gone from every future design: POSIX is the
  floor, and platform.md stops carrying a speculative Windows column.
- The macOS port is bounded and small (~2 files, `sysctl`), because the hard
  parts (kill, rendezvous, sockets, signals) are already POSIX.

### Negative

- **N1 · Bigger artifact.** Single-file self-contained carries the runtime —
  tens of MB vs the 78KB apphost. Acceptable for a runtime-free install; the
  shim stays tiny.
- **N2 · macOS is unproven until the port lands + is exercised.** Decision 3 is
  scheduled work, not verified; platform.md's macOS entries stay "researched"
  until a real run flips them. Linux/WSL2 remains the only lived-in target
  meanwhile.
- **N3 · `sysctl` is a second identity implementation to keep honest.** The
  pid-reuse guard's correctness now has two code paths; a cross-platform test
  (or a documented macOS smoke) must cover the `sysctl` branch, or it rots.

## Implementation plan

*Small; not decomposed via `/adr-plan`. Rough order:*
1. `PublishSingleFile`+`SelfContained` on the engine csproj (+ build-determinism
   re-verify per CLAUDE.md's shipped-project rule); confirm `/deploy` stages the
   single-file form.
2. `ChildRecords.ProcStartTime` + `Doctor` argv/starttime: add a `sysctl`
   (`KERN_PROC`) branch behind an OS check; cross-platform-guard the `/proc`
   reads. Pin with a test that exercises the active OS's path.
3. platform.md: flip Windows → out-of-scope, macOS status → target (pending the
   run); note the single-file distribution form.

## Ground truth

*Back-filled when the port lands.*
