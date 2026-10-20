# Flow: live deployment — a real prompt through the deployed daemon

What exists on this machine when captAInHook is dogfooding: where the
deployed binary lives, how a real Claude Code prompt travels through it, what
the daemon lifecycle looks like operationally, and what a redeploy must do.
The `/deploy` skill *performs* this — by packaging a local release archive and
installing it with `release/install.sh --archive`, the same path a public
release takes ([install.md](install.md), ADR-0020); this doc explains what it
leaves behind.

```
 you type a prompt (or Claude calls a tool) in Claude Code
        │
        ▼
 ~/.claude/settings.json  hook command (per event):
   ~/.local/bin/captainShim hook <event>     (a FILE symlink →
                                              ~/.local/share/captainhook/versions/<v>/captainShim)
        │  payload JSON on stdin — captainShim is the NATIVE AOT
        │  artifact (ADR-0004 decision 7); ~5ms procBoot vs the
        ▼  engine's ~67ms CLR boot; it sees its REAL folder, not the link
 ┌ captainShim (per-event native process) ───────────────────────┐
 │ skew guard: my compiled-in wire MVID ≟ bin's captainHookWire  │
 │   .dll — mismatch = partial deploy → NEVER touch the socket,  │
 │   delegate below (shim.wireSkew in the trail)                 │
 │ mint dispatchId ── resolve socket from content identity of    │
 │ versions/<v>/       +  $XDG_RUNTIME_DIR/captainHook/          │
 │        │                                                      │
 │   try connect ──────────── warm ──► forward frame ──► relay   │
 │        │ cold (no daemon)           response verbatim         │
 │        ▼                                                      │
 │   spawn captaind (detached) + DELEGATE: exec the co-located   │
 │   engine `captainHook hook <event> --no-daemon`, pipe stdin,  │
 │   relay stdout bytes / stderr / exit verbatim                 │
 └────────┼──────────────────────────────────────────────────────┘
          ▼
   captainHook --daemon  (ONE per content identity — the JIT engine)
     fd0/1/2 → /dev/null · cwd / · record = the JSONL trail
     exits by itself after the idle window (CAPTAINHOOK_IDLE_MS,
     default 30min; a non-empty background queue defers it)
```

## The deployed tree

| path | what |
|---|---|
| `~/.local/share/captainhook/versions/<v>/` | one installed build — native `captainShim` (the hook command) + single-file self-contained `captainHook` (daemon/collapsed engine, runtime bundled — ADR-0012) + the four LOOSE app dlls + `ui/` + `bosun` + `payloads/`; the loose dlls' content identity names the socket (the native shim and the bundle carry no readable MVID and are invisible to the hash — by design, ADR-0004 d7 amendment + ADR-0012 d2) |
| `~/.local/bin/{captainShim,captainHook}` | FILE symlinks into the current version — the stable paths settings.json names |
| `~/.local/share/captainhook/previous` | the version kept for one-step rollback (`install.sh --rollback`); anything older is pruned |
| `~/.local/share/captainhook/payloads/` | one file symlink per shipped payload — the stable paths handlers.json names |
| `~/.captainHook/bin/` (+ `bin.prev/`) | the pre-ADR-0020 in-place deploy; retired once no hook command or daemon uses it |
| `~/.captainHook/logs/captainHook.jsonl` | the trail — shim AND daemon write here (same default), one dispatchId per prompt across both halves |
| `~/.captainHook/harnesses/` | user harness overrides — editable live; the daemon stats the dir per dispatch and reloads (`harness.reload`) |
| `$XDG_RUNTIME_DIR/captainHook/captaind-<ver>.{sock,lock,pid}` | rendezvous files, 0600; `<ver>` = content identity of the deployed bin dir |

## Operations

- **Is a daemon running?** Read `captaind-<ver>.pid` (pid, binary path,
  started-at), then check `/proc/<pid>/exe` still points at a captainHook
  binary — pids get reused; never trust the pid alone.
- **Kill it** — `kill <pid>` (SIGTERM) now drains gracefully: in-flight
  prompts get their responses, queued Background work completes (10s
  deadline), exec-handler children are killed in the drain's child phase
  (TERM→2s grace→KILL, whole process groups — ADR-0010 N6; children never
  outlive the daemon), and socket and pidfile are removed on the way out
  (`daemon.drainStart` → `daemon.drained` [+ `daemon.drainChildren` /
  `daemon.drainCut`] in the trail). `kill -9` remains safe too — the kernel
  releases the lock, the next prompt collapses and respawns — though a -9
  skips the child phase: what it leaves (a resident ignoring stdin EOF, a
  forked grandchild, an in-flight oneshot) is killed by the NEXT daemon's
  boot reap (`daemon.reapOrphan`, ADR-0010's 2026-10-19 amendment); until
  one starts, `doctor` reports it.
  Never delete `.lock` files.
- **Sweep leftovers**: `~/.local/bin/captainHook doctor` — dead
  pidfiles/sockets removed, pid-reused records cleaned without signaling the
  stranger, superseded daemons (their binary's path computes a different
  identity now) SIGTERM-drained then SIGKILLed after grace, healthy daemons
  and lock files untouched. Runnable from any build: lineage is judged
  per-path, so a dev-tree doctor never kills a healthy deployed daemon.
- **Watch it live**: `tail -f ~/.captainHook/logs/captainHook.jsonl` — a warm
  event is `shim.answered` + daemon-side `dispatch.start/done` under one
  dispatchId; a cold one is `shim.fallback` + `shim.spawnDaemon` +
  `shim.delegated` wrapping the engine's collapsed `dispatch.*`; a partial
  deploy is `shim.wireSkew` + `shim.delegated` (hook still answers).
- **Worst cases, by design**: daemon crashes mid-dispatch → the shim sees
  EOF (`shim.deliveryFailed`, exit 1, zero stdout — the prompt proceeds
  without the hook's effect; at-most-once forbids the retry). Daemon wedged
  (accepts, never answers, never closes) → the shim has NO post-delivery
  timer of its own (ADR-0010 decision 9 — handler budgets are unbounded, so
  a slow answer is legitimate); the harness's own hook timeout is the
  backstop that kills the shim, and `doctor` sweeps the wedged daemon.
  Daemon dead → every prompt collapses (~200ms, the pre-daemon cost) and
  respawns; nothing breaks.

## Redeploy = new identity

Installing a build with changed bytes switches the links to a folder with a
new content identity, so the next prompt rendezvouses on a **fresh socket by
construction** — the old daemon can never serve stale code. The previous
version's daemon is unreachable and idle-exits within its window on its own
(doctor labels it "another installed build" and leaves it alone); an older
version's daemon, whose folder the install pruned, is drained by the doctor
run the installer ends with. A no-op redeploy (deterministic compilation,
unchanged source — the dev version's `+<sha>` is not stamped into the
assemblies) lands in a new folder with the SAME identity and keeps the warm
daemon.

## Rollback

`sh release/install.sh --rollback` points every link at `previous` (and keeps
the version it left as the new `previous`, so a second `--rollback` returns).
The hook command path is unchanged, the restored dlls compute the old
identity, and the next event rendezvouses accordingly. Pointing the
settings.json command at the engine (`~/.local/bin/captainHook hook <event>`)
also remains valid — the engine keeps its full shim mode for exactly this.

## Ground truth

| what | where |
|---|---|
| deploy procedure (the executable version of this doc) | `.claude/skills/deploy/SKILL.md` → `release/package.sh` + `release/install.sh --archive` |
| install layout, upgrade, rollback | [install.md](install.md); [ADR-0020](../adr/0020-release-archives-and-install.md) |
| the shim program: guard → forward → delegate | `dotnet/captainShim/ShimMain.cs`, `SkewGuard.cs` |
| shim decision tree, at-most-once boundary | `dotnet/captainHookWire/ShimClient.cs`; [hook-dispatch.md](hook-dispatch.md) |
| spawn mechanics + muxer guard | `dotnet/captainHookWire/DaemonSpawner.cs` |
| serve loop, spec hot-reload | `dotnet/captainHook/Core/DaemonHost.cs` |
| rendezvous paths & identity | `dotnet/captainHookWire/Rendezvous.cs`, `dotnet/captainHook/Core/DaemonRendezvous.cs` |
| one trail schema, two emitters | `dotnet/captainHookWire/WireJsonl.cs` ≡ `Logging.fs` `ToJson()` (golden: `WireJsonlTests.cs`) |
| pinned by | `ShimMainTests.cs` (relay, delegation, skew guard, at-most-once), `SpawnTests.cs`, `DaemonHostTests.cs`, `AtMostOnceTests.cs` |
| decision record | `doc/adr/0004-daemon-topology.md` (decision 7 + 2026-07-06 amendment) |
