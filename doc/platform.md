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

## Runtime directories

| Fact | Detail | What leans on it |
| --- | --- | --- |
| `XDG_RUNTIME_DIR` is Linux/systemd | Set by pam_systemd to `/run/user/<uid>`: short by construction, per-user `0700`, tmpfs, wiped at logout. Present on systemd-enabled WSL2. Essentially never set on macOS or Windows. | The socket/lock/pid directory choice. "Is it set?" is itself the deterministic branch — unset simply means the `~/.captainHook/` fallback, which is short on macOS (`/Users/<name>`) and Windows (`C:\Users\<name>`). |
| tmpfs wipe at logout is acceptable | Lock/pid files are tiny, version-bounded, and meaningful only while a daemon lives; a daemon rarely outlives the login session that spawned it (systemd may kill user processes at logout anyway — `KillUserProcesses`/linger). | ADR-0004 decision 4's cleanup story; `doctor` reaps whatever survives. |

## File locking & process lifecycle

| Fact | Detail | What leans on it |
| --- | --- | --- |
| POSIX exclusive lock released on any death | An `O_EXCL`-style held lock (`FileShare.None` handle) is released by the kernel on process exit, crash included — no cleanup code required for correctness. | ADR-0004 decision 3: kernel-settled spawn races; "lock files are never unlinked." |
| Deleting a held lock file breaks mutual exclusion | A second process can lock a *fresh inode* at the same path while the first still holds the old one. POSIX-specific footgun. | The "no component ever unlinks a lock file, `doctor` included" rule. |
| Windows lock semantics differ | `FileShare.None` gives exclusivity, but the unlink-while-held / fresh-inode dance is POSIX-flavored; Windows would want a named mutex (or rely on its default can't-delete-open-files behavior — which then breaks the *socket* unlink step instead). | ⚠ The rendezvous mechanics (lock + stale-socket unlink + bind) are **not** Windows-portable as designed. Windows-proper support = its own audit + ADR-0004 amendment. Parked: WSL2 is the Windows story today, and it is Linux for these purposes. |
| PIDs are reused | A recorded pid may name an unrelated process by reap time. | `doctor`'s liveness *plus* binary-identity check before SIGTERM (pharos ADR-030 lineage). |
| `/bin/sh` exists; `sh -c 'exec … &'` detaches | POSIX shell as OS facility (not a dependency): the `&` subshell + `exec` + parent-exit reparents the child to init with /dev/null stdio. .NET alone cannot redirect a child to /dev/null or setsid portably. | `DaemonSpawner.Detached` — the shim's spawn-on-fallback (ADR-0004 decision 2). |
| .NET cannot preemptively kill user code | No `Thread.Abort`; a wedged, token-ignoring handler's task can only be abandoned, not destroyed (the honest cost of not being on the BEAM). | ADR-0004 decision 5's abandon-and-respawn + leak accounting; wedges count toward escalation. |

## Per-OS summary

| | Linux / WSL2 | macOS | Windows (native) |
| --- | --- | --- | --- |
| UDS + length cap | ✓ (108) — verified live | ✓ (104) — researched | ✓ 1803+ (~108) — researched |
| `XDG_RUNTIME_DIR` | ✓ set | ✗ → fallback | ✗ → fallback |
| Fallback `~/.captainHook` short enough | ✓ (guard covers container/network homes) | ✓ | ✓ |
| Lock/unlink rendezvous as designed | ✓ | ✓ (POSIX) | ✗ — needs own audit |
| Status | **target** | should-work | parked |
