# Platform envelope — OS facts the design leans on

Environmental constraints, not decisions: things the kernel or OS imposes that
are true regardless of what we choose. ADRs record what we *chose* under these
facts (and cite them); flow docs record what the *code* does; this file is the
re-audit checklist when a fact changes or a new OS becomes a target. Each entry
names what leans on it, so nothing here is trivia.

Verification status: **Linux/WSL2 is the lived-in target** — facts there are
exercised by the test suite or observed live. macOS and Windows entries are
researched, not yet exercised; treat them as design inputs, and promote them to
verified the day a real run touches them.

## Unix domain sockets

| Fact | Detail | What leans on it |
| --- | --- | --- |
| `sun_path` caps at ~108 bytes | Linux 108 incl. NUL; macOS 104; Windows AF_UNIX also ~108. Hit live 2026-07-05: a scratchpad-length socket path aborted with `ArgumentOutOfRangeException` before bind. | ADR-0004 decision 3 (as amended 2026-07-05): runtime files under `$XDG_RUNTIME_DIR/captainHook/` when set, else `~/.captainHook/`, plus a clear-error guard at resolve time — `Core/Rendezvous.cs`. |
| Path choice must be a pure function of env | The shim is per-invocation with no memory and no side channel to the daemon; both sides must *compute* the identical path. Probe-until-fits or any stateful negotiation is unsound by construction. | The whole filesystem rendezvous (ADR-0004 decisions 2–3). |
| AF_UNIX exists on Windows 10 1803+ | .NET's `UnixDomainSocketEndPoint` supports it; same length cap. Named pipes are the Windows-native alternative if AF_UNIX gaps appear. | Keeps the wire design portable in principle; see the Windows caveat below for what is *not* portable. |
| `connect()` succeeding ≠ peer healthy | A listening socket's backlog accepts connects even if the accept loop is wedged or draining. | ADR-0004 decision 2's shim-side deadline spanning connect + request + response. |

## Loopback TCP & managed `HttpListener` (management API)

The management API (ADR-0007) rides the *managed* `HttpListener` — on Unix
there is no http.sys; the BCL ships its own socket-level implementation, "the
least-loved corner of the BCL" (ADR-0007 N5). Bind-boundary facts probed
2026-07-07 (Linux/WSL2, .NET 10) for the singleton-port cutover:

| Fact | Detail | What leans on it |
| --- | --- | --- |
| An active listener blocks a second bind — no co-bind | Cross-process: `HttpListenerException` "Address already in use" (kernel EADDRINUSE; SO_REUSEPORT is not set). In-process: `HttpListenerException` "conflicts with an existing registration" from the managed prefix table, before any syscall. | ADR-0007 d2's port-is-a-singleton premise: a successor *cannot* steal, only wait for the incumbent's drain-start release. `ApiHost` retry loop catches `HttpListenerException`; pinned by `ApiRetryBindTests.StartRetrying_NeverStealsAnActivelyHeldPort`. |
| TIME_WAIT residue does NOT block a **.NET→.NET** rebind | Served-and-closed connections leave `127.0.0.1:<port>` TIME_WAIT entries (60s on Linux). The mechanism is NOT `HttpListener` (its managed listener sets no socket options — verified in dotnet/runtime source): the **.NET Unix PAL sets SO_REUSEADDR unconditionally on every TCP bind** (`SystemNative_Bind`, pal_networking.c). And Linux only honors it pairwise (kernel-probed 2026-07-07): a bind over server-close TIME_WAIT needs reuse on BOTH the new socket AND the listener that created the residue; client-close residue never blocks. Both daemons are .NET ⇒ both halves hold ⇒ rebind in ms (probed: 17ms through live worst-case residue). | Handoff latency = release→bind, not release→2·MSL — but only across .NET incumbents. A **non-.NET** prior occupant of the port that closed server-side can block the bind up to ~60s; the retry loop's slow cadence absorbs exactly that (warn, never fatal). Pinned by `ApiRetryBindTests.Rebind_ThroughTimeWaitResidue_IsImmediate` — which guards the **PAL default**: if it ever reds, hunt in Socket/PAL, not `HttpListener`. |
| Loopback bind is unprivileged | No urlacl/reservation step on Unix (that is http.sys ceremony); any user binds ≥1024. Ports <1024 still need root — the failure surfaces as the same bind exception, i.e. warn + slow retry, never fatal. | The API existing with zero install-time OS setup (ADR-0007 d1); `ApiHost.ResolvePort` accepting any 1..65535 without a privilege check. |
| The managed listener PREFIX-MATCHES on the Host header | With prefix `http://127.0.0.1:<port>/`, only a request whose `Host` is exactly `127.0.0.1:<port>` reaches user code. A foreign Host (a DNS-rebind page naming `attacker.example`) or even `localhost:<port>` gets a **404 from the listener itself** before any handler; an empty Host → 400. Probed 2026-07-07. And `HttpListenerRequest.UserHostName` returns the matched PREFIX authority for dispatched requests, not the raw client Host. | ADR-0007 decision 6's rebind defense: the listener refuses a foreign Host first, so `ApiAuthGate`'s own Host→403 check is portable defense-in-depth (unit-tested directly, since real HTTP can't reach it here); and the API answers `127.0.0.1` only — supporting a `localhost` origin would need a second prefix (deferred). `ApiAuthHttpTests.ForeignHost_RefusedByTheListener` pins the 404-before-gate behavior. |

## Runtime directories

| Fact | Detail | What leans on it |
| --- | --- | --- |
| `XDG_RUNTIME_DIR` is Linux/systemd | Set by pam_systemd to `/run/user/<uid>`: short by construction, per-user `0700`, tmpfs, wiped at logout. Present on systemd-enabled WSL2. Essentially never set on macOS or Windows. | The socket/lock/pid directory choice. "Is it set?" is itself the deterministic branch — unset simply means the `~/.captainHook/` fallback, which is short on macOS (`/Users/<name>`) and Windows (`C:\Users\<name>`). |
| tmpfs wipe at logout is acceptable | Lock/pid files are tiny, version-bounded, and meaningful only while a daemon lives; a daemon rarely outlives the login session that spawned it (systemd may kill user processes at logout anyway — `KillUserProcesses`/linger). | ADR-0004 decision 4's cleanup story; `doctor` reaps whatever survives. |
| `CreateDirectory(dir, mode)` can't retighten a pre-existing dir; secrets need create-time modes | The `UnixFileMode` arg applies only when the dir/file is CREATED — on a pre-existing one it is silently ignored. And `WireJsonl.Append` creates `~/.captainHook` (via `logs/`) at the **umask default (0755)** on the first hook, typically before any daemon. So the runtime dir is only reliably private once `DaemonRendezvous.TryAcquire` **explicitly** `SetUnixFileMode`s it to 0700, and the `api.json` token is created 0600 **at birth** (`FileStream` `UnixCreateMode`, never write-then-chmod) so it is never world-readable even in a window — the trust root both ADR-0004 d3 and ADR-0007 d6 assert. Probed live 2026-07-07. | The 0700-dir + 0600-token trust root; `LockBindTests.Acquire_Tightens_APreExisting0755RuntimeDir`, `ApiDiscoveryTests.Write_Is0600AtBirth`. **Residual (pre-existing, ADR-0004 logging):** the JSONL trail file is created 0644 and, in the window before a daemon tightens the dir (or in pure-collapsed/no-daemon runs), sits in a 0755 dir — a co-located user could read it. The token is unaffected (always 0600); closing the trail window means the shim/`WireJsonl` creating the dir 0700 + the trail file 0600, tracked as a logging-layer follow-up. |

## File locking & process lifecycle

| Fact | Detail | What leans on it |
| --- | --- | --- |
| POSIX exclusive lock released on any death | An `O_EXCL`-style held lock (`FileShare.None` handle) is released by the kernel on process exit, crash included — no cleanup code required for correctness. | ADR-0004 decision 3: kernel-settled spawn races; "lock files are never unlinked." |
| Deleting a held lock file breaks mutual exclusion | A second process can lock a *fresh inode* at the same path while the first still holds the old one. POSIX-specific footgun. | The "no component ever unlinks a lock file, `doctor` included" rule. |
| Windows lock semantics differ | `FileShare.None` gives exclusivity, but the unlink-while-held / fresh-inode dance is POSIX-flavored; Windows would want a named mutex (or rely on its default can't-delete-open-files behavior — which then breaks the *socket* unlink step instead). | ⚠ The rendezvous mechanics (lock + stale-socket unlink + bind) are **not** Windows-portable as designed. Windows-proper support = its own audit + ADR-0004 amendment. Parked: WSL2 is the Windows story today, and it is Linux for these purposes. |
| PIDs are reused | A recorded pid may name an unrelated process by reap time. | `doctor`'s liveness *plus* binary-identity check before SIGTERM (pharos ADR-030 lineage). |
| `/bin/sh` exists; `sh -c 'exec … &'` detaches | POSIX shell as OS facility (not a dependency): the `&` subshell + `exec` + parent-exit reparents the child to init with /dev/null stdio. .NET alone cannot redirect a child to /dev/null or setsid portably. | `DaemonSpawner.Detached` — the shim's spawn-on-fallback (ADR-0004 decision 2). |
| `dotnet foo.dll` makes `Environment.ProcessPath` the MUXER | Under framework-dependent `dotnet <dll>` invocation, ProcessPath is `/…/dotnet`, not the app — a self-respawn would exec `dotnet --daemon` (a CLI error). Only the apphost executable yields the app's own path. | `DaemonSpawner`'s muxer guard (refuses + `shim.spawnFailed`); the `/deploy` skill's apphost requirement (doc/flow/live-deployment.md). |
| .NET cannot preemptively kill user code | No `Thread.Abort`; a wedged, token-ignoring handler's task can only be abandoned, not destroyed (the honest cost of not being on the BEAM). | ADR-0004 decision 5's abandon-and-respawn + leak accounting; wedges count toward escalation. |

## Native AOT (captainShim)

| Fact | Detail | What leans on it |
| --- | --- | --- |
| AOT publish needs a platform linker toolchain | `PublishAot` drives the link through `clang` (+ `objcopy` for symbol strip) on Linux; absent toolchain fails the *publish*, not the build. A **build-host** constraint, not a runtime dependency — the csproj carries no PackageReference. Verified 2026-07-06 (clang 18, Ubuntu/WSL2). | The `captainshim-aot-artifact` slice; `/deploy`'s two-artifact publish (ADR-0004 decision 7 amendment). |
| AOT output is per-RID | One native image per `-r <rid>` (`linux-x64` here); no portable fallback inside the artifact. | `/deploy` publishes for the host RID; other-OS support rides the per-OS summary below. |
| A native image has no MVID | The ELF/Mach-O binary carries no managed metadata; `PEReader` throws `BadImageFormatException`, which `ContentIdentity.Compute` already skips. | The decision-7 amendment's identity story: the hash stays over the deploy dir's managed DLLs; the shim is invisible to it *by this fact*, and co-location + the wire-stamp guard carry the rest. |
| `AppContext.BaseDirectory` / `Environment.ProcessPath` work under AOT | Both resolve to the native executable's directory/path. | `ShimMain.DefaultEnginePath` (co-located engine), the shim's identity hash over its own directory. |
| `Process.GetCurrentProcess().StartTime` is coarse | /proc-derived; jitter of several ms observed on WSL2 — fine for the gated `shim.boot` trail line, not for benchmarking. | The `CAPTAINHOOK_COLDSTART` probe's shim half; real measurement is wall-clock over batched runs. |

## Build determinism (content identity leans on all of these)

| Fact | Detail | What leans on it |
| --- | --- | --- |
| The SDK bakes git HEAD into builds by default | `InitializeSourceControlInformation` queries git on every build; the sha lands in `AssemblyInformationalVersion` and (C#) the implicit-SourceLink document feeding the PDB id — so identical source recompiles to fresh MVIDs behind ANY commit, docs-only included. Probed with empty commits 2026-07-06. Opt-out at the root: `EnableSourceControlManagerQueries=false`. | ADR-0004 d3's "identity differs ⟺ behavior may differ"; without the opt-out every commit churns a daemon and costs a spurious cold hook. |
| fsc reference assemblies are nondeterministic | The F# compiler's `ref/` assembly gets fresh bytes per compile from identical input (Roslyn's are deterministic), poisoning the deterministic hash of every C# assembly that references the F# lib. Probed by hashing obj trees across clean publishes 2026-07-06. Opt-out: `ProduceReferenceAssembly=false` on the fsproj. | Same — `captainHook.dll`'s MVID (the biggest identity input) was rolling on every publish. |
| Verified end state | Clean `dotnet publish` ×2 at one HEAD + ×1 behind an empty commit → all shipped MVIDs identical. Re-run this probe if the SDK majors or a project is added. | The no-op-republish-keeps-the-warm-daemon property (doc/flow/live-deployment.md). |

## Per-OS summary

| | Linux / WSL2 | macOS | Windows (native) |
| --- | --- | --- | --- |
| UDS + length cap | ✓ (108) — verified live | ✓ (104) — researched | ✓ 1803+ (~108) — researched |
| `XDG_RUNTIME_DIR` | ✓ set | ✗ → fallback | ✗ → fallback |
| Fallback `~/.captainHook` short enough | ✓ (guard covers container/network homes) | ✓ | ✓ |
| Lock/unlink rendezvous as designed | ✓ | ✓ (POSIX) | ✗ — needs own audit |
| Status | **target** | should-work | parked |
