# ADR-0013 — Windows becomes a target; the Go rewrite is deferred again, on new evidence

**Status:** Proposed; **decision 1 (Windows-native) WITHDRAWN by the owner
2026-08-11** — targets revert to Linux + macOS per ADR-0012, with "runs in a
Linux container" as the Windows-adjacent story (a container is Linux; WSL2 and
devcontainers are the path). The language analysis (Findings 1–6) stands and
remains the record for why the Go rewrite stays deferred; the Windows design
work routed to "ADR-0014" below was mooted, and that number was reused —
[ADR-0014](0014-bosun-spawn-wrapper.md) instead lands the one cross-OS fix the
analysis isolated as requiring native code: the spawn seam.
*(Original 2026-07-27 framing: raised by the owner: "convert this dotnet
application into Go … Go is better suited for the app's needs considering we want
it to run on Linux, Mac, and Windows." The Windows half was accepted at that
time. The Go half is **recommended against on the stated rationale** — Finding 1
— while its real merits are recorded, costed, and given a revisit trigger. The
complete Go port plan is preserved in
[doc/port/go-port-inventory.md](../port/go-port-inventory.md) § Part 4 if the
owner overrules, which is a legitimate call on merits this ADR also records.)*
**Date:** 2026-07-27
**Amends:** [ADR-0012](0012-distribution-and-platform-targets.md) d1 (Windows out
of scope). Its "Rewrite in Go — deferred, never by portability" row is
**re-affirmed with stronger evidence**, not overturned.
**Evidence:** [doc/port/go-port-inventory.md](../port/go-port-inventory.md).
Everything below was measured on 2026-07-27 (Go 1.26.2, .NET 10), not cited.

## Context

ADR-0012 decided Linux + macOS, Windows out of scope, and deferred a Go rewrite
as justifiable "only by its *own* merits, **never by portability**." The owner has
changed the premise — Windows is wanted — and proposed Go as the answer. The
premise change is real and decision 1 accepts it. The proposed answer does not
survive measurement.

### Finding 1 — Windows is a DESIGN problem, and Go is on balance *worse* at it

The POSIX-specific surface of the shipped code is **~44 lines across 10 files —
0.45% of production code**. Two of those files already carry a non-Linux branch
from ADR-0012 d3. Mechanism by mechanism, all probed:

| # | mechanism | verdict |
|---|---|---|
| 1 | exclusion lock | **Go HARDER.** `FileShare.None` already maps to `CreateFileW` `dwShareMode=0` — mandatory kernel exclusion, *stronger* than POSIX flock, zero code change. Go has **no `flock_windows.go`** (verified in GOROOT) and `LockFileEx` lives in `internal/syscall/windows`, unimportable. |
| 1b | unlink-while-held | **Same — and platform.md overstates the danger.** The socket is unlinked only under the held lock, so it is never open by a live process. And the lock file, held with `dwShareMode=0`, is *undeletable* on Windows — the OS **enforces for free** the invariant `DaemonRendezvous.cs:14-21` maintains by convention on POSIX. |
| 2 | AF_UNIX transport | **Same.** Both speak it on Win10 1803+ (GOROOT: `net/unixsock_posix.go` build tag includes `windows`; `UnixDomainSocketEndPoint` carries no `[UnsupportedOSPlatform]`). |
| 2b | named pipes | **Go MUCH HARDER.** `NamedPipeServerStream`, `PipeSecurity`, `PipeOptions.FirstPipeInstance` and `CurrentUserOnly` are all **present in the shared framework** (probed) — free under invariant 3. Go's stdlib has none; it needs `Microsoft/go-winio`. |
| 3 | process groups | **Go easier on Unix, EQUAL on Windows.** `SysProcAttr{Setpgid:true}` genuinely deletes the `setsid(1)` probe and ends the macOS degrade — but `CreateJobObject` is absent from **both** stdlibs. Adding Windows makes this argument *less* decisive, not more. |
| 3b | TERM→grace→KILL | **Same.** No faithful Windows analogue in either; the graceful half must move onto the wire. Pure design. |
| 4 | pid-reuse identity | **Go REGRESSES — the sharpest reversal.** ADR-0012 d3's existing non-Linux branch (`Process.StartTime`, `MainModule.FileName`) is portable — probed: unsupported only on ios/tvos — so it **already works on Windows unchanged**. Go's stdlib gives nothing on **macOS or Windows**. This is a stated *safety* property. |
| 5 | drain trigger | **Same.** Both sit on `SetConsoleCtrlHandler`, which a detached process never receives. |
| 6 | detached spawn | **Go MEANINGFULLY EASIER — the one unambiguous win.** `SysProcAttr{Setsid:true}` + `os.DevNull` + `Release()` (probed: sid == pid) deletes the `/bin/sh -c 'exec … &'` hop, the zombie reap, and the 14-line muxer guard. But that is *Unix* value ADR-0012 already banks. |
| 7 | file modes / trust root | **Go HARDER, and a real security question in both.** Probed: `File.SetUnixFileMode` and `Directory.CreateDirectory(string, UnixFileMode)` are `[UnsupportedOSPlatform("windows")]`; Go's `syscall.Chmod` on Windows toggles only the read-only bit. .NET has the ACL toolkit; Go does not. |
| 8 | XDG / path caps | **Same.** Zero differentiation. |

**Scorecard: Go is meaningfully easier on 1 of 8, marginally on 1, the same on 3,
and meaningfully harder on 3.** The four things Windows actually costs — a
rendezvous substrate, a kill discipline, a graceful-stop mechanism, and a
trust-root decision — are **ADR-shaped design work, required identically in
either language.**

### Finding 2 — the cross-compilation argument is 254 lines wide

Cross-publishing the **real** projects from this Linux box:

| artifact | win-x64 | osx-arm64 |
|---|---|---|
| `captainHook` engine (self-contained, single-file) | **OK — 71 MB PE32+** | **OK — 77 MB Mach-O arm64** |
| `captainShim` (`PublishAot`) | FAIL — *"Cross-OS native compilation is not supported"* | FAIL — linker |

The engine already ships to all three OSes from one Linux host. Only the
**254-LOC shim** needs a per-OS build host. Go's cross-compile win buys artifact
*size* (~10 MB vs 71–77 MB) and removes one build host for one small binary.

**And the maintainer already operates the pattern that removes even that** — see
Finding 6. `pharos-mcp`'s release workflow builds on five native runners
(`ubuntu-latest`, `ubuntu-24.04-arm`, `macos-15-intel`, `macos-latest`,
`windows-latest`) and publishes per-platform npm packages. A per-OS build host for
a 254-LOC shim is a solved, already-owned problem, not a reason to change language.

### Finding 3 — the Go merits that DO survive, and they are cleanup merits

- **One binary.** An engine-shaped Go binary (net/http, `embed.FS` with 460 KB of
  UI assets, 32 routes, 9.8 MB) boots in **1911 µs** — indistinguishable from the
  3.7 MB Native AOT shim (**1919 µs**). Linking the whole engine costs 0.2 ms.
  That collapses ADR-0004 d7's second artifact back into d1's original "one
  binary, three modes," deleting `captainShim`, `SkewGuard`, the skew-guard
  *mechanism*, the MVID identity machinery, the delegation fallback, and
  **`aot-boundary.md`'s twelve standing rules**.
- **`SysProcAttr{Setpgid:true}`** ends the stock-macOS `pgroup=false` degrade
  ADR-0012 accepted as the macOS baseline — a shipped deficiency, fixed.
- **`testing/synctest`** retires `FakeClock`/`PollUntilAsync`; **`go test -race`**
  turns `SoakTests`' *inferred* lost-update property into a checked one.
- **`net/http`** deletes platform.md's entire managed-`HttpListener` section.
- **`cmd.WaitDelay` is `ExecHandler.PipeGrace` in stdlib** (probed: `WaitDelay=0`
  rides a backgrounded grandchild 5001 ms; `250ms` returns in 251 ms).
- **`os.OpenFile(…O_APPEND…)`** fixes the trail-clobbering bug platform.md
  records against `File.AppendAllText`.

Every one is *internal cleanup*. None is a capability the product lacks.

### Finding 4 — what the rewrite risks, demonstrated rather than asserted

11.8k LOC / **521 test methods**, overwhelmingly hard-won correctness. This was
tested, not assumed: the item-17a classified ask — the most recently hardened code
in the repo — was ported to ~40 lines of Go and **three targeted tests passed**.

**The port was silently wrong.** Go's `select` has no priority (probed: both-ready
⇒ 5080/10000 chose the deadline), so a handler answering exactly on time is
classified `Wedged` **400/400 times** — and wedges *count toward escalation*, so a
healthy worker is restarted and eventually escalated to permanently DEAD. A second
latent crash sat in the same 40 lines: `close()` of an already-closed channel
panics where `TrySetResult` is idempotent, and double-abort is reachable.

The value in those 521 tests is that each encodes a bug found *adversarially,
after the code looked right*. A Go suite written by the same pass that writes the
Go code transcribes assertions without their reasons. The wedge bug is the
existence proof.

### Finding 5 — the AOT control experiment: the .NET escape hatch is narrower than hoped

The cheapest experiment that could kill this whole ADR is "Native-AOT the C#
engine" — if it works, the one-binary collapse and most of Finding 3's deletion
ledger arrive without a rewrite. **It was run.** Result, in order:

1. **It compiles and links** → a **9.2 MB native binary**, essentially the same
   size as the Go equivalent (9.8 MB). ADR-0012's "a real fight" is not a wall.
2. **It crashes on the first dispatch**: `Reflection-based serialization has been
   disabled for this application`, at `HookRun.CollapsedAsync`. The reflection
   surface is real — but it is **compiler-enumerated**: ~16 `JsonSerializer` call
   sites across 10 files (`HookRun.cs:288-289`, `Doctor.cs:96,183`,
   `ApiDiscovery.cs:34,50`, `DaemonRendezvous.cs:78`, `ChildRecords.cs:63,143`,
   `DaemonHost.cs:496-497`, `Harness.cs:325,341`, `ApiJson.cs:20`,
   `Logging.fs:56`, `ApiSchema.cs:40`). Most take a source-gen context. Four are
   genuinely dynamic and need restructuring: the trail's `Dictionary<string,object>`
   data bag, `ApiJson`'s polymorphic writer, the harness adapters' `object`
   serialization, and `JsonSchemaExporter` (dev-time only — could move out).
3. **And it hits a second, deeper blocker that is architectural, not mechanical.**
   Before the JSON crash it printed:
   `rendezvous unavailable … no managed assemblies found — cannot compute a
   content identity`. **`ContentIdentity.Compute` throws on an AOT engine**, because
   a native image has no MVID (platform.md § Native AOT records this fact; ADR-0004
   d3's entire identity scheme depends on managed assemblies existing on disk).

Finding 5(3) is the important one, and it cuts *toward* Go: **the .NET route to
one binary also requires replacing the identity substrate.** That is the same
design work the Go port needs, so Finding 3's deletion ledger is not cheaply
reachable in .NET. The AOT escape hatch narrows a real gap; it does not close it.

*(One measurement could not be isolated: the AOT engine's process boot. The engine
has no work-free code path — an unknown verb falls through to the full collapsed
pipeline — and the binary aborts before completing it. The 167 ms figure quoted
for `captainHook` throughout is therefore **full collapsed dispatch, not boot**.)*

### Finding 6 — `pharos-mcp` is the maintainer's own tri-platform precedent, and it splits cleanly in two

`~/pharos-mcp` is a BEAM (Elixir/Gleam) MCP server that manages long-lived LSP
child processes and ships to Linux, macOS, and Windows. It is the nearest prior
art, it is the same maintainer, and it is worth reading precisely — because it is
tri-platform in **distribution** and *not* in **process lifecycle**.

**Distribution — solved, and directly reusable.** `.github/workflows/release.yml`
builds on five native runners (`ubuntu-latest`, `ubuntu-24.04-arm`,
`macos-15-intel`, `macos-latest`, `windows-latest`) and publishes per-platform npm
packages (`@pharos-mcp/win-x64`, …) that a post-install script resolves. The
CHANGELOG shows real Windows engineering here — Burrito cache-path work for
`%LOCALAPPDATA%` vs `%APPDATA%`. **This answers ADR-0013's open question 4**: the
per-OS build host that Native AOT needs is a pattern the maintainer already owns
and operates. It is not a reason to change language.

**Process lifecycle — deliberately NOT solved, and that is the lesson.**
`pharos-mcp` ADR-030 (`doc/adr/030-process-lifecycle-hardening.md` in the sibling repo) reached
"works on three OSes" by **shrinking the problem**, and says so explicitly:

- *"No single-instance enforcement … Any future global lock is the wrong shape and
  rejected here."* — pharos has **no rendezvous at all**; it is multi-instance by
  design (one per MCP client). So it offers captAInHook's hardest Windows
  question no answer, because it declined to ask it.
- *"No auto-reaper at boot … requires platform-divergent process introspection
  code (`/proc/<pid>/stat`, `ps`, `wmic`), runs without explicit user intent, and
  carries nonzero risk of false-positive kills under PID-reuse."* — captAInHook's
  `doctor` is exactly the auto-reaper pharos judged too risky to run unattended
  across three platforms. A user-confirmed `pharos cleanup` CLI replaced it.
- *"No Windows JobObject in v1.0"* — an explicit non-goal, deferred to v1.1. The
  precise primitive captAInHook's kill discipline needs is the one pharos punted.
- PID-reuse is guarded by **process name**, not start time — weaker than
  `ChildRecords`' field-22 ticks, but uniform across OSes.

**And the shipped code is POSIX-only anyway.** ADR-030 decision 3 promises
`tasklist` for the Windows branch; `src/pharos_instance_track_ffi.erl` implements
`is_pid_alive` as `os:cmd("kill -0 …")`, `process_comm` as `/proc/<pid>/comm` with
a `ps -o comm=` fallback, and `signal_pid` as `os:cmd("kill -…")`. **There is no
Windows branch.** On Windows every one of those silently returns
false/empty/error. CI (`.github/workflows/ci.yml`) runs tests on `ubuntu-latest`
only — Windows is *built* and *shipped*, never *tested*.

Three things follow, and they all support the decision below:

1. **It is not evidence for Go.** pharos is on the BEAM — a *heavier* runtime than
   .NET — and reaches three OSes through per-OS packaging, exactly the route
   available to .NET today. If anything it is a counterexample to "you need Go to
   be tri-platform."
2. **It confirms Finding 1's shape from an independent codebase.** A second
   project by the same author, on a different runtime, hit the same wall and
   resolved it by *design* (drop the lock, drop the auto-reap, weaken the identity
   guard, ask the user before killing) rather than by language.
3. **It is a live warning about ADR-0013's own risk.** pharos shipped an ADR
   promising a Windows branch that was never written, on a target its CI never
   exercises. That is the precise failure mode decision 7 (CI first) exists to
   prevent, and it argues for making the Windows CI leg a *gate*, not a follow-up.

## Decision

1. **Windows becomes a first-class target: Linux, macOS, Windows.** Supersedes
   ADR-0012 d1. WSL2 stops being "the Windows story."

2. **Do the Windows port in .NET.** Finding 1 puts the delta at ~44 lines across
   10 files plus language-neutral design work; Finding 2 removes the distribution
   argument. Rewriting ~9.7k LOC of production code and ~11.8k LOC of tests to
   change 10 files is a two-order-of-magnitude amplification of the problem.

3. **The Go rewrite is deferred again — on its own merits this time, not
   dismissed.** ADR-0012's "never by portability" survives the premise change
   intact, strengthened. **Revisit trigger:** if the two-artifact deploy, the AOT
   boundary's twelve rules, or the `HttpListener` scar tissue ever *block* a
   feature rather than merely annoy; or if a second maintainer joins; or if the
   roadmap goes quiet enough to absorb Finding 4.

4. **The four Windows design questions get their own ADR — ADR-0014.** They are the
   real work and are language-neutral: (a) the **rendezvous substrate** — named
   pipes with `FirstPipeInstance` vs AF_UNIX + a `CreateFileW` lock; note .NET
   named pipes are *not* a free cross-platform answer (probed on Linux: backing
   file 0754, no create-time mode seam, no crash-reclaim), so a per-OS substrate is
   required either way; (b) the **kill discipline** — job objects with
   `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`, graceful half moved onto the wire,
   mutating an ADR-0010 contract; (c) the **drain trigger**; (d) the **trust root**
   when file modes are meaningless.

   **ADR-0014 must open by reading `pharos-mcp` ADR-030** (Finding 6). It is the
   same author solving the same problem on a different runtime, and its most useful
   contribution is the *cheaper design menu* it chose — each option a real
   candidate for captAInHook, each a deliberate weakening:
   - **name-based PID-reuse guard** instead of start-time ticks — uniform across
     all three OSes, weaker than `ChildRecords`' field-22, and it removes N5's
     macOS problem entirely;
   - **user-confirmed reaping** (`pharos cleanup`) instead of an automatic
     `doctor` — pharos rejected the auto-reaper *specifically* because it needs
     platform-divergent introspection and can false-positive under PID reuse;
   - **drop the global lock** — unavailable to captAInHook (the warm-daemon
     rendezvous *is* the architecture), but worth stating as rejected-with-reason
     rather than never-considered.

   Read it also as a **cautionary tale**: ADR-030 decision 3 promises a `tasklist`
   Windows branch that was never implemented, in a project whose CI is Linux-only.
   An ADR is not a platform.

5. **Fix the Windows hard-crash first, as a standalone slice.**
   `DaemonRendezvous.cs:53` calls `Directory.CreateDirectory(paths.RuntimeDir,
   dirMode)` **not inside a try**, and that overload is
   `[UnsupportedOSPlatform("windows")]`. Daemon startup throws
   `PlatformNotSupportedException` on Windows today. Cheap, isolated, and it makes
   every later slice testable.

6. **Probe the one unresolved security unknown on real Windows before calling
   Windows supported:** does Windows enforce the AF_UNIX socket file's DACL at
   `connect()`? If not, AF_UNIX is unsuitable on security grounds and named pipes
   become mandatory — which decides 4(a).

7. **Establish CI before any of it.** There is **no `.github`**. Every gate below
   ("green on Linux *and* Windows") reads an instrument that does not exist. A
   3-OS matrix running the **existing C# suite** is a prerequisite, not port work —
   and it is what makes decision 3's revisit cheap if it ever fires.

## Rejected / deferred alternatives

| alternative | disposition |
|---|---|
| **Rewrite in Go for portability** | **Rejected on the evidence** — Finding 1's scorecard and Finding 2. This is the proposal as stated. |
| **Rewrite in Go on its own merits** | **Deferred with a trigger** (d3). Finding 3's merits are real and Finding 5 shows the .NET alternative to them is not cheap. Finding 4's cost remains dominant for a solo maintainer with four payloads running live on their own hooks. Full plan preserved in the inventory doc. |
| **Native-AOT the .NET engine** | **Deferred, and now better understood** — Finding 5. Compiles to 9.2 MB; blocked by ~16 enumerated JSON sites *and* by `ContentIdentity` throwing on a native image. The second blocker means this is not a shortcut to Finding 3; it is a redesign with a different name. Revisit alongside ADR-0014, since both need a new identity substrate. |
| **Strangler: Go shim first, C# daemon** | **Rejected — the seam is real but the payoff is illusory.** The crossing was *built*: ~130 LOC of Go (`debug/pe` + an ECMA-335 metadata walk) computed the live deploy dir's identity as `5b6dc0cc8b5a`, matching the running socket exactly, in 0.32 ms. But `SkewGuard` compares `typeof(Frame).Module.ModuleVersionId` — a Go shim links no assembly, so the comparison has **no left-hand side**, and the shim is deliberately outside the identity hash (`aot-boundary` rule 6). A Go-shim-only change would roll no socket and trip no guard, making ADR-0004 d3's "version mismatch is unrepresentable" representable. And it buys nothing: the shim is already at parity and d4 would retire it. |
| **Go for Windows only; .NET elsewhere** | Rejected. Two implementations of five stable contracts across three OSes, one maintainer, no CI — and the maintainer dogfoods on Linux, so the divergent Windows half is the one nobody runs. `WireJsonlTests` exists because two emitters in *one* language already drifted. |
| **Keep Windows out of scope (WSL2)** | Still defensible and worth naming: `UiVerb.LauncherCommand` already shells to `powershell.exe Start-Process` on WSL, so the GUI opens in the real Windows browser today; and the payload corpus is POSIX shell, so Windows-native currently buys a *worse* product on Windows. Rejected because the owner decided otherwise — their call. |
| **Containerize the daemon for Windows** | Stays deferred per ADR-0012 d4. |

## Consequences

### Positive

- Windows is reached for ~44 lines plus four design decisions that would have been
  needed anyway, instead of a full rewrite.
- Every hard-won correctness property in the 521-test suite is preserved by not
  touching it, and the dogfood loop — which found the best bugs — never stops. You
  cannot dogfood a half-ported daemon.
- The BCL's Windows toolkit (named pipes, `PipeSecurity`, `FirstPipeInstance`,
  `CurrentUserOnly`, `Process.StartTime`/`MainModule` on foreign pids) is free
  under invariant 3; none has a Go stdlib equivalent.
- CI (d7) is durable value regardless of which language wins.
- The Go analysis is not wasted: costed, measured, preserved, with a trigger.

### Negative

- **N1 · The .NET cleanup debt stays.** Two artifacts, twelve AOT-boundary rules,
  the MVID machinery, the `HttpListener` scar tissue, the `setsid(1)` wrapper.
  Finding 3 is a real bill that goes unpaid, and Finding 5 shows paying it in .NET
  is itself a redesign.
- **N2 · The stock-macOS `pgroup=false` degrade stays open.** Options: document
  brew-installed `setsid`, or earn an ADR for a native spawn helper.
- **N3 · Artifacts stay large** — 71–77 MB per OS versus ~10 MB, now ×3 targets.
- **N4 · A per-OS rendezvous substrate is now certain**, so `LockBindTests`' 8
  POSIX-flock facts gain a Windows sibling suite asserting different properties.
- **N5 · ~2,280 LOC of POSIX-shell test fixtures must become a cross-platform
  helper binary.** This cost is Windows's, not Go's — but it is now due.

## Implementation plan

Ship bar per phase: **suite green twice**, plus the named gate.

**Phase 0 — instrument and unblock.** `ci-matrix` (d7 — 3-OS runners, existing C#
suite green twice on Linux and at least the pure-parser subset elsewhere; crib the
matrix from `pharos-mcp`'s `release.yml`, which already runs five native runners);
`windows-startup-crash` (d5); `windows-dacl-probe` (d6, on real Windows — its
answer picks 4(a)). **Gate:** a daemon *starts* on Windows, CI proves it, and the
DACL question has an answer. Per Finding 6, the Windows CI leg is a **gate, not a
follow-up** — pharos shipped a Windows branch that did not exist because nothing
ran it.

**Phase 1 — ADR-0014.** The four design decisions, written before code.
**Gate:** ADR accepted; platform.md's Windows column rewritten from "out of scope"
to a real envelope.

**Phase 2 — rendezvous.** `rendezvous-substrate-seam` (one interface, per-OS
impls), `windows-lock-and-endpoint`, `windows-trust-root`. **Gate:** two real
daemons race on Windows and the kernel settles it; the credential file is not
readable by a second local user.

**Phase 3 — lifecycle.** `windows-kill-discipline` (job objects),
`wire-graceful-stop` (the ADR-0010 contract change), `windows-drain-trigger`,
`process-identity-windows` (mostly free per Finding 1 #4). **Gate:**
`KillDisciplineTests`' properties re-proven on Windows, reparented grandchild
included.

**Phase 4 — test infrastructure.** `cross-platform-test-helper` (N5).
**Gate:** the full suite green on Windows twice.

**Phase 5 — distribution + docs.** `deploy-windows` — and a real choice Finding 2
surfaces: per-OS build hosts for the AOT shim, *or* ship the Windows shim
framework-dependent and accept a slower cold path. platform.md re-audit, flow
docs, ADR-0012 amended. **Gate:** a real Claude Code session on Windows runs on
the deployed build; dogfood field report in `doc/dogfood/`.

## Open questions — the owner's call

1. **Accept this recommendation, or overrule it?** Finding 3's merits are real and
   Finding 5 shows the .NET alternative to them is not cheap — someone could
   reasonably weigh the cleanup above Finding 4's cost. That is a legitimate
   judgment; the full Go plan is ready. This ADR argues the *rationale as stated*
   fails, not that Go is a bad language for this.
2. **Is Windows-native truly required, or is WSL2 sufficient?** The GUI already
   opens the real Windows browser from WSL, and the payload corpus is POSIX shell.
   If WSL2 suffices, decisions 1–7 all fall away.
3. **Does the design accept a weaker Windows security posture** (relying on
   `%USERPROFILE%`'s inherited per-user DACL) or does the trust root need explicit
   ACL code? The api.json token is the management API's entire auth story.
4. ~~**Per-OS build hosts for the AOT shim, or a slower Windows shim?**~~
   **Answered by Finding 6** — per-OS build hosts, cribbing `pharos-mcp`'s
   five-runner release matrix. Already owned, already operated; not a reason to
   change language.
5. **How much of `pharos-mcp` ADR-030's cheaper design menu does captAInHook
   adopt?** Name-based PID-reuse identity instead of start-time ticks (uniform,
   weaker, deletes the macOS problem); user-confirmed reaping instead of an
   automatic `doctor`. Both trade a safety property for tri-platform uniformity.
   Genuinely the owner's call, and it belongs in ADR-0014.

## Ground truth

| item | lives in |
|---|---|
| all measurements | [doc/port/go-port-inventory.md](../port/go-port-inventory.md) (2026-07-27; Go 1.26.2, .NET 10) |
| the full Go port plan, if overruled | same doc, § Part 4 |
| d4 design | ADR-0014 (to be written) |

### Incidental find — a docs defect, true regardless of this ADR

[ADR-0004 § decision 3](0004-daemon-topology.md) states that "a touch-only or
**comment-only** rebuild emits identical bytes and keeps its socket." **Measured
false** on the real `captainHookWire` project: clean rebuild ⇒ identical,
touch-only ⇒ identical, **comment-only ⇒ different bytes**. Roslyn's determinism
tracks *source text*, and a comment is source text. The error is in the safe
direction — a spurious identity change costs one cold hook, never a wrong answer —
but the record should say what is true. (Curiosity for the deferred branch: Go's
build-ID `contentID` *is* comment-insensitive, so the property ADR-0004 claims
would be literally true there.)
