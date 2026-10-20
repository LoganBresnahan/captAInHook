# Flow: release → install → upgrade → uninstall

How a captAInHook release reaches a machine and how it is replaced, decided in
[ADR-0020](../adr/0020-release-archives-and-install.md). The same `install.sh`
serves a public release and a locally built archive (`--archive`, which is
what `/deploy` and CI use), so there is one install path.

```
 git tag v<x.y.z> ─► release.yml: one NATIVE job per target ─────────────────────────┐
                      linux-x64 · linux-arm64 · darwin-arm64 · darwin-x64             │
                      release/package.sh <v> <rid>                                    │
                        dotnet publish engine (single-file) + shim (AOT), ONE build   │
                        + bosun (pinned tag, sha256 from release/bosun.sha256)        │
                        + ui/ + payloads/ + install.sh + LICENSE NOTICE VERSION       │
                      install.sh --archive <tgz>   into a temp HOME                   │
                      release/smoke.sh   cold · warm · bosun · doctor · idle ·        │
                                         long runtime dir · orphan reap ·             │
                                         self prune/uninstall                         │
                      ─► only if every target passed: publish                         │
                         captainhook-<v>-<os>-<arch>.tar.gz × N, SHA256SUMS, install.sh
                                                                                      │
 curl …/releases/latest/download/install.sh | sh ◄────────────────────────────────────┘
   1. platform: uname → linux|darwin, x64|arm64 (Rosetta shell ⇒ arm64)
   2. SHA256SUMS (latest or --version) ─► the asset name carries the version
   3. already current + not --force ─► "nothing to do", exit 0
   4. download archive ─► sha256 ≟ SHA256SUMS ─► MISMATCH: stop, nothing touched
   5. unpack into versions/.tmp-<v>-$$ ─► rename to versions/<v>
   6. versions/<v>/captainHook version   (runs here? else stop, links unchanged)
   7. switch FILE symlinks (ln -s new tmp && mv -f tmp link):
        ~/.local/bin/captainShim  → versions/<v>/captainShim
        ~/.local/bin/captainHook  → versions/<v>/captainHook
        ~/.local/bin/captainhook  → versions/<v>/captainHook   (case-sensitive FS only:
                                    skipped when it already -ef captainHook — APFS)
        payloads/<f>              → versions/<v>/payloads/<f>   (one per file, flat)
   8. previous ← the version the links pointed at before (when it changed)
   9. prune: "kept <v> (current)", "kept <p> (previous)", "removed <x>"
  10. versions/<v>/captainHook doctor   drains daemons whose folder step 9 deleted
  11. print PATH hint + the install-hooks next step — NO config is edited

 captainHook install-hooks            dry run: the unified diff it would make
 captainHook install-hooks --apply    backup, then write ~/.claude/settings.json
   hook command = ~/.local/bin/captainShim hook <event>   ← stable across upgrades

 captainHook self rollback|prune|uninstall  ─► sh -s -- --<verb>  < versions/<v>/install.sh
 captainHook self update [--version X]      ─► fetch latest (or vX's) install.sh ─► sh -s -- [--version X]
   env: CAPTAINHOOK_INSTALL_DIR ← the engine's real folder's grandparent
        CAPTAINHOOK_BIN_DIR     ← the launching link's dir (else the PATH entry linking here)
   no versions/ around the engine (dev tree, dotnet run) ─► print the curl one-liner, exit 1
```

## Why it is shaped like this

**The links are files, never a directory.** The obvious layout is one
`current → versions/<v>` link, but replacing a directory symlink atomically
needs `mv -T`, which BSD `mv` lacks — and without it both `mv`s move the new
link *inside* the old target. Per-file links need only rename(2), which every
`mv -f` does. The cost is that payloads must stay flat (a payload that finds a
sibling through `$(dirname "$0")` sees the links directory, which has a link
for every shipped file and nothing else).

**Links never reach the binaries' sense of where they live.** The shim finds
the engine, the identity, the skew-guard DLL, `ui/` and `bosun` beside itself
through `AppContext.BaseDirectory`. Probe (a) showed both executable kinds
report the link's target folder, so `~/.local/bin/captainShim` behaves exactly
like `versions/<v>/captainShim`. `captainHook version` prints that base, and the
smoke test asserts it on every target — on macOS this is the check that stands
in for hardware.

**An upgrade needs no lifecycle code.** The new version's DLLs compute a new
content identity (the release version is stamped into them), so the first hook
after the switch finds no daemon on the new socket, spawns one, and delegates
(ADR-0004). The previous version's daemon is left alone — doctor's rule is
"the binary at the daemon's own path moved on", and the previous folder is
still there — but no hook reaches it any more, so it idle-exits within its
window. Doctor labels it "another installed build" so two healthy lines don't
look like a fault. A version older than that is pruned; deleting a folder
under a running daemon is safe (it runs on from the unlinked inodes), and the
doctor run straight after drains it as `superseded — path now <gone>`.

**Nothing edits config silently.** install.sh never touches shell rc files,
`~/.claude/settings.json` or `~/.captainHook/`. Hook wiring is the separate
`install-hooks` verb, which prints its diff first and writes only with
`--apply` (after a byte-for-byte backup). Its hook command names the stable
link, so it runs once per machine, not once per upgrade. `handlers.json`
keeps literal absolute commands; shipped payloads are referenced through
`…/captainhook/payloads/<f>`, which every install re-points.

**Rollback is one switch.** `install.sh --rollback` points every link at the
`previous` version and records the one it left as the new `previous`, so a
second `--rollback` returns. Nothing is downloaded or pruned; the version
rolled away from keeps its daemon until it idles out, exactly as after an
upgrade.

**Dev builds name the commit, not the identity.** `/deploy` packages
`<release>-dev+<sha>`: SemVer build metadata names the archive and its folder
but is not stamped into the assemblies, so a docs-only commit produces a new
folder with the SAME content identity, and the warm daemon keeps serving.

**The lower-case alias is for people, and only where it can exist.**
`captainhook` runs the same engine, so the command works however it is typed.
On a case-insensitive file system (APFS's default) that name already resolves
to `captainHook`, and a link written under it would rename-replace the real
entry, so the installer writes it only when the lower-case path is not already
the same file (`-ef`). A `captainhook` that is not ours is kept. Hooks and
settings.json always use the exact names (ADR-0020 d2's 2026-10-19 amendment).

**Uninstall is two steps, in order.** `captainHook install-hooks --remove
--apply` takes captAInHook out of the harness config (the binary is needed to
do it); `captainHook self uninstall` (= `install.sh --uninstall`) then removes
the links (the alias too), payload links and versions, keeping `~/.captainHook/`. It refuses
while `settings.json` still runs the shim link, because every hook would then
fail to start.

**The installer travels with the install.** Every version folder carries the
`install.sh` that laid it out, and `captainHook self` runs it: `rollback`,
`prune` and `uninstall` work offline, against the install the engine really
lives in, while `update` fetches the newest installer (or `--version X`'s) so
an update never runs an old one. The engine only launches: the script goes in
on stdin (`sh -s --`, the `curl | sh` path), exactly as geex's and pharos's
launchers run theirs — the three apps share the verbs, the flags and the
prune lines. A version folder from before 0.1.1 has no `install.sh`; `self`
then says how to install instead, and one `curl … | sh` fixes it.

## Paths

| path | what | default |
| --- | --- | --- |
| `$CAPTAINHOOK_INSTALL_DIR` | versions, payload links, `previous` | `${XDG_DATA_HOME:-~/.local/share}/captainhook` |
| `$CAPTAINHOOK_BIN_DIR` | `captainShim`, `captainHook` links | `~/.local/bin` |
| `$CAPTAINHOOK_RELEASE_URL` | where releases are fetched from (tests point it at `file://`) | `https://github.com/LoganBresnahan/captAInHook/releases` |
| `~/.captainHook/` | config, logs, harness overrides — untouched by install | |
| `$XDG_RUNTIME_DIR/captainHook/` (else `~/.captainHook/`; when either is too long for a socket, `/tmp/captainhook-<uid>`) | rendezvous files, created by the daemon — `captainHook version` prints which | |

## Ground truth

| what | where |
| --- | --- |
| archive builder | `release/package.sh` |
| installer (install, upgrade, rollback, prune, uninstall) | `release/install.sh` — also `versions/<v>/install.sh` in every install |
| the lower-case alias (`link_alias`; removed by `--uninstall` after the real names) | `release/install.sh`; `release/smoke.sh` step 1 (its own link on Linux, no second entry on macOS) and step 12 |
| `captainHook self` | `dotnet/captainHook/Install/SelfVerb.cs`; `SelfVerbTests`; smoke steps 10–11 |
| per-target smoke test | `release/smoke.sh` |
| a toolchain-free sandbox run (install → smoke → uninstall inside one directory; how a bare Mac is checked) | `release/sandbox-smoke.sh` |
| bosun pin | `release/bosun.sha256` |
| hook wiring | `dotnet/captainHook/Install/InstallHooksVerb.cs`, `UnifiedDiff.cs`; `InstallHooksTests`, `UnifiedDiffTests` |
| where the install lives | `dotnet/captainHook/Install/VersionVerb.cs` |
| the "another installed build" label | `Doctor.RunAsync` (`selfIdentity`); `DoctorTests.HealthyDaemon_IsLabelledThisBuildOrAnother_LeftAloneEitherWay` |
| the guard on the live tree | `examples/payloads/deploy-guard.sh`; `DeployGuardTests` |
| CI | `.github/workflows/ci.yml`, `release.yml`, `post-release.yml` |
| decision record | [ADR-0020](../adr/0020-release-archives-and-install.md) |
| OS facts | [doc/platform.md](../platform.md) § Install layout |
