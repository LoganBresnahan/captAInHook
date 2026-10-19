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
                        + ui/ + payloads/ + LICENSE NOTICE VERSION                    │
                      install.sh --archive <tgz>   into a temp HOME                   │
                      release/smoke.sh             cold · warm · bosun · doctor · idle│
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
        payloads/<f>              → versions/<v>/payloads/<f>   (one per file, flat)
   8. previous ← the version the links pointed at before (when it changed)
   9. prune every versions/* except <v> and previous
  10. versions/<v>/captainHook doctor   drains daemons whose folder step 9 deleted
  11. print PATH hint + the install-hooks next step — NO config is edited

 captainHook install-hooks            dry run: the unified diff it would make
 captainHook install-hooks --apply    backup, then write ~/.claude/settings.json
   hook command = ~/.local/bin/captainShim hook <event>   ← stable across upgrades
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

**Uninstall is two steps, in order.** `captainHook install-hooks --remove
--apply` takes captAInHook out of the harness config (the binary is needed to
do it); `install.sh --uninstall` then removes the links, payload links and
versions, keeping `~/.captainHook/`. It refuses while `settings.json` still runs
the shim link, because every hook would then fail to start.

## Paths

| path | what | default |
| --- | --- | --- |
| `$CAPTAINHOOK_INSTALL_DIR` | versions, payload links, `previous` | `${XDG_DATA_HOME:-~/.local/share}/captainhook` |
| `$CAPTAINHOOK_BIN_DIR` | `captainShim`, `captainHook` links | `~/.local/bin` |
| `$CAPTAINHOOK_RELEASE_URL` | where releases are fetched from (tests point it at `file://`) | `https://github.com/LoganBresnahan/captAInHook/releases` |
| `~/.captainHook/` | config, logs, harness overrides — untouched by install | |
| `$XDG_RUNTIME_DIR/captainHook/` (else `~/.captainHook/`) | rendezvous files, created by the daemon | |

## Ground truth

| what | where |
| --- | --- |
| archive builder | `release/package.sh` |
| installer (install, upgrade, prune, uninstall) | `release/install.sh` |
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
