# ADR-0020 — Release archives, the versioned install layout, and consent-first hook wiring

**Status:** Accepted *(2026-10-19; owner direction relayed by the run
coordinator for the cross-app release run — geex, pharos and captAInHook ship
the same install convention. captAInHook's first public release is v0.1.0.)*
**Date:** 2026-10-19
**Builds on:** [ADR-0012](0012-distribution-and-platform-targets.md) (Linux +
macOS, single-file, no host runtime — this ADR is how that build reaches a
user), [ADR-0004](0004-daemon-topology.md) (content identity names the
socket; the shim's skew guard; on-demand spawn + idle exit),
[ADR-0014](0014-bosun-spawn-wrapper.md) (bosun as the co-located spawn rung,
pinned by tag + sha256), [ADR-0003](0003-declarative-harness-registry.md)
(the harness spec's `install` block — data this ADR finally reads),
[ADR-0010](0010-exec-handlers.md) (handlers.json commands are literal paths;
a relative one means relative to the child's cwd).
**Evidence:** probe (a), 2026-10-19 — a sandboxed install of the real
artifacts behind directory and file symlinks; recorded in
[doc/platform.md](../platform.md) § Install layout.

## Context

Until now the only way captAInHook reached a machine was `/deploy`: the
maintainer's own checkout publishing into `~/.captainHook/bin`, swapped in
place with one `bin.prev` for rollback. Nothing was built in CI, nothing was
downloadable, and the hook wiring in `~/.claude/settings.json` was edited by
hand. Two apps the maintainer develops alongside (geex, pharos) move to
per-platform release archives and one curl installer convention at the same
time, so the three install the same way.

The constraints captAInHook brings to that convention:

- **Co-location is the contract.** The shim finds the engine, the content
  identity, the skew-guard DLL, `ui/` and `bosun` through
  `AppContext.BaseDirectory`. An install that reaches the binaries through a
  link must still hand them their REAL directory.
- **Identity, not paths, retires a daemon.** A new version's DLLs compute a
  new identity, so its first hook rendezvouses on a fresh socket; the old
  daemon is never handed new code. Doctor reaps a daemon only when the binary
  at its OWN recorded path computes a different identity (or is gone).
- **The shim and its DLLs must come from one build.** The shim carries the
  wire assembly's MVID compiled in and refuses the socket when the
  co-located `captainHookWire.dll` differs. Native AOT also cannot cross
  operating systems.

Probe (a) settled the first constraint empirically on Linux: launched through
a directory symlink, a file symlink, or `$PATH`, both the Native AOT shim and
the single-file engine report the real versioned folder as
`AppContext.BaseDirectory` and `Environment.ProcessPath`; the daemon's
pidfile records the real path; bosun resolves from the versioned folder. It
also showed that doctor, run after a version switch, correctly leaves the
previous version's daemon alone (its folder still computes its identity), and
that deleting a version folder under a running daemon is safe — the daemon
keeps running from the unlinked inodes and the next doctor drains it as
`superseded — path now <gone>`.

## Decision

1. **One archive per platform, built natively, all artifacts from one job.**
   Release assets are `captainhook-<version>-<os>-<arch>.tar.gz` (os
   `linux|darwin`, arch `x64|arm64`) plus `SHA256SUMS` and `install.sh`. One
   archive holds one top-level directory with: `captainShim` (Native AOT),
   `captainHook` (single-file self-contained), the four loose app DLLs
   identity and the skew guard read, `ui/`, `bosun` (fetched by the pinned
   tag and verified against digests kept IN THE REPO — `release/bosun.sha256`
   — never "latest", never built here), `payloads/` (the shipped example
   payloads), `LICENSE`, `NOTICE`, `VERSION`. `release/package.sh` builds it;
   CI runs it once per target on a runner of that OS and architecture, so the
   shim and DLLs of an archive always share a build. The release version is
   stamped with `-p:Version=<v>` — which also makes distinct versions
   distinct identities. Windows is not a target (ADR-0012 d1).

2. **The versioned layout, switched by FILE symlinks.**
   ```
   ${CAPTAINHOOK_INSTALL_DIR:-${XDG_DATA_HOME:-~/.local/share}/captainhook}/
     versions/<v>/            one unpacked archive per installed version
     payloads/<file>          FILE symlink → versions/<v>/payloads/<file>
     previous                 the version that was current before the last switch
   ${CAPTAINHOOK_BIN_DIR:-~/.local/bin}/
     captainShim, captainHook FILE symlinks → versions/<v>/…
   ```
   A switch is `ln -s <new> <tmp> && mv -f <tmp> <link>` per link — rename(2)
   over a file symlink, identical under GNU and BSD `mv`. There is
   deliberately NO directory symlink: replacing one atomically needs `mv -T`
   (GNU only), and BSD `mv` follows a symlink-to-directory destination. Config
   and runtime data stay where they are (`~/.captainHook/`, the rendezvous
   dir) — an upgrade touches binaries only.

3. **`install.sh` — POSIX sh, no sudo, fails closed, edits no config.** It
   resolves the version (latest, or `--version X`), downloads the archive and
   `SHA256SUMS`, verifies the digest BEFORE unpacking, unpacks into a
   temporary sibling and renames it into `versions/<v>`, switches the links,
   records `previous`, prunes every version older than the previous one, and
   runs the new engine's `doctor`. Re-running for the installed version does
   nothing unless `--force`. `--archive <path>` installs a local archive (the
   same code path `/deploy` and CI use); `--rollback` points the links back at
   `previous` (and again to return). SemVer build metadata in a version
   (`0.1.0-dev+<sha>`) names the folder but is not stamped into the
   assemblies, so a dev build of unchanged code keeps its identity. It never edits shell rc files,
   `settings.json`, or `handlers.json`; it prints a PATH hint and the
   `install-hooks` next step instead. Retention is current + previous: the
   previous version's daemon is not reaped — no hook reaches it after the
   switch, so it idle-exits within its window — and an older version's
   daemon, whose folder the prune deleted, is drained by that doctor run.
   **No lifecycle code changes:** ADR-0004's identity rule already does the
   right thing under this layout.

4. **`captainHook install-hooks` — consent-first wiring, from the spec.** It
   reads the harness spec's `install` block (ADR-0003; until now surfaced
   read-only) in a closed vocabulary — `hooksPath` `hooks.{event}`,
   placeholders `{captainShim}`, `{event}`, `{event-kebab}`, anything else
   refused. **The default run writes nothing**: it prints the exact unified
   diff it would make. `--apply` writes it after a byte-for-byte timestamped
   backup, atomically (temp + rename, mode kept, a symlinked settings file
   written through to its target). It owns only entries whose program is
   `captainShim`/`captainHook` followed by `hook <event>` (and the old
   `dotnet …captainHook.dll` form); other tools' hooks are carried through
   untouched and in order. It converges: an existing install is repointed in
   place and keeps its chosen events; a first install wires every declared
   event; `--events` chooses explicitly; `--remove` takes captAInHook out. The
   hook command targets the STABLE link (`$CAPTAINHOOK_BIN_DIR/captainShim`),
   so no upgrade ever needs to run it again.

5. **`captainHook version`** prints the stamped version, the content
   identity, `AppContext.BaseDirectory`, and the runtime identifier — so an
   installer smoke test ASSERTS that the link resolved to the real folder
   instead of believing it.

6. **Shipped payloads get stable paths without a template language.**
   handlers.json keeps literal absolute commands (ADR-0010 unchanged — a
   relative command keeps meaning relative to the child's cwd). The installer
   maintains `payloads/` as a real directory of per-file symlinks into the
   current version, so `…/captainhook/payloads/deploy-guard.sh` survives every
   upgrade. Payloads stay FLAT: a payload that finds siblings through
   `$(dirname "$0")` sees the links directory, which works only because every
   shipped file has a link there. A link whose payload a new version no
   longer ships is pruned, and install.sh warns if `handlers.json` still
   names it.

7. **CI proves the artifact, not just the source.** `ci.yml` builds and tests
   on every push. `release.yml` on `v*` tags builds each target natively,
   smoke-tests the archive it built (install from the local archive into a
   temp HOME: cold hook, warm hook, zero `shim.wireSkew`, `spawner=bosun`,
   `version`'s base under `versions/`, doctor, cold-hook latency reported),
   and only then publishes. Every publishing step is gated on a `v*` tag ref,
   so a branch run or a manual dispatch is a dry run. A post-release workflow
   installs the PUBLISHED release from its public URL on fresh runners.

## Consequences

- One install path for dev and release: `/deploy` becomes "package a local
  archive, `install.sh --archive` it" — the live tree moves from
  `~/.captainHook/bin` to the versioned layout at the coordinated cutover.
- Two daemons can be alive for up to one idle window after an upgrade (the
  previous version's, unreachable, and the new one). Doctor reports both
  healthy; that is correct, not a fault.
- The archive is ~75–80 MB per target, dominated by the bundled runtime.
- **N1 · macOS is proven by CI, not by hardware.** The symlink →
  BaseDirectory behaviour, the AOT shim's ad-hoc signature surviving the
  archive round trip, and the whole ADR-0012 N2 list are exercised by the
  macos runner's smoke job; nothing in this run touches a physical Mac
  directly.
- **N2 · install-hooks normalizes layout.** It re-serializes the settings
  file as 2-space JSON (Claude Code's own shape, non-ASCII kept verbatim). A
  file in another layout gets re-indented wholesale — the verb says so, and
  the diff shows every line before anything is written.

## Rejected alternatives

| Alternative | Why not |
|---|---|
| A `current → versions/<v>` directory symlink | No portable atomic replace: `mv -T` is GNU-only, BSD `mv` moves INTO the linked directory. Per-file links need only rename(2). |
| Resolve a relative handlers.json command against the engine's directory | Changes ADR-0010's contract (relative = relative to the child's cwd) for a problem a links directory solves with no engine change. |
| A `{payloads}` placeholder in handlers.json | Config would become a template language (CLAUDE.md house rule). |
| Teach doctor to reap "alive but not the current version" | The previous version's daemon is harmless and idle-exits; reaping it buys nothing and widens doctor's kill rule. A display-only label is the most it earns. |
| Building bosun in CI | It is a separate project with its own release; a pinned, checksum-verified fetch keeps deploy bytes a pure function of this repo. |
| Edit settings.json from install.sh | Silent config edits are exactly what the consent-first verb exists to replace. |

## Ground truth

| what | where |
|---|---|
| archive builder | `release/package.sh` |
| installer | `release/install.sh` |
| bosun pin | `release/bosun.sha256` (tag + digests) |
| install-hooks | `dotnet/captainHook/Install/InstallHooksVerb.cs` (+ `UnifiedDiff.cs`); `InstallHooksTests` |
| version | `dotnet/captainHook/Install/VersionVerb.cs` |
| CI | `.github/workflows/{ci,release,post-release}.yml`, `release/smoke.sh` |
| mechanics | [doc/flow/install.md](../flow/install.md) |
