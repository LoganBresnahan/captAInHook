# Flow: live deployment — a real prompt through the deployed daemon

What exists on this machine when captAInHook is dogfooding: where the
deployed binary lives, how a real Claude Code prompt travels through it, what
the daemon lifecycle looks like operationally, and what a redeploy must do.
The `/deploy` skill *performs* this; this doc explains what it leaves behind.

```
 you type a prompt in Claude Code
        │
        ▼
 ~/.claude/settings.json  UserPromptSubmit hook:
   /home/oof/.captainHook/bin/captainHook hook user-prompt-submit
        │  payload JSON on stdin — the APPHOST executable, never
        │  `dotnet captainHook.dll` (ProcessPath must be this app,
        ▼   or the spawner refuses — doc/platform.md)
 ┌ shim (per-prompt process) ────────────────────────────────────┐
 │ mint dispatchId ── resolve socket from content identity of    │
 │ ~/.captainHook/bin  +  $XDG_RUNTIME_DIR/captainHook/          │
 │        │                                                      │
 │   try connect ──────────── warm ──► forward frame ──► relay   │
 │        │ cold (no daemon)           response verbatim         │
 │        ▼                                                      │
 │   spawn captaind (detached) + dispatch in-process (collapsed) │
 └────────┼──────────────────────────────────────────────────────┘
          ▼
   captaind --daemon  (ONE per content identity, warm forever*)
     fd0/1/2 → /dev/null · cwd / · record = the JSONL trail
     *no idle-exit yet: lives until killed — see Operations
```

## The deployed tree

| path | what |
|---|---|
| `~/.captainHook/bin/` | the published build — apphost `captainHook` + dlls; its content identity names the socket |
| `~/.captainHook/logs/captainHook.jsonl` | the trail — shim AND daemon write here (same default), one dispatchId per prompt across both halves |
| `~/.captainHook/harnesses/` | user harness overrides — editable live; the daemon stats the dir per dispatch and reloads (`harness.reload`) |
| `$XDG_RUNTIME_DIR/captainHook/captaind-<ver>.{sock,lock,pid}` | rendezvous files, 0600; `<ver>` = content identity of the deployed bin dir |

## Operations

- **Is a daemon running?** Read `captaind-<ver>.pid` (pid, binary path,
  started-at), then check `/proc/<pid>/exe` still points at a captainHook
  binary — pids get reused; never trust the pid alone.
- **Kill it** — `kill <pid>` (SIGTERM) now drains gracefully: in-flight
  prompts get their responses, queued Background work completes (10s
  deadline), socket and pidfile are removed on the way out
  (`daemon.drainStart` → `daemon.drained` in the trail). `kill -9` remains
  safe too — the kernel releases the lock, the next prompt collapses and
  respawns. Never delete `.lock` files.
- **Sweep leftovers**: `~/.captainHook/bin/captainHook doctor` — dead
  pidfiles/sockets removed, pid-reused records cleaned without signaling the
  stranger, superseded daemons (their binary's path computes a different
  identity now) SIGTERM-drained then SIGKILLed after grace, healthy daemons
  and lock files untouched. Runnable from any build: lineage is judged
  per-path, so a dev-tree doctor never kills a healthy deployed daemon.
- **Watch it live**: `tail -f ~/.captainHook/logs/captainHook.jsonl` — a warm
  prompt is `shim.answered` + daemon-side `dispatch.start/done` under one
  dispatchId; a cold one is `shim.fallback` + `shim.spawnDaemon` + collapsed
  `dispatch.*`.
- **Worst cases, by design**: daemon wedged → the shim's 5s response deadline
  fires once (`shim.deliveryFailed`, exit 1, zero stdout — the prompt
  proceeds without the hook's effect), and the wedged daemon must be killed
  manually until doctor lands. Daemon dead → every prompt collapses (~200ms,
  today's pre-daemon cost) and respawns; nothing breaks.

## Redeploy = new identity

Publishing changed bytes to `~/.captainHook/bin` changes the content
identity, so the next prompt rendezvouses on a **fresh socket by
construction** — the old daemon can never serve stale code. But until
`mandatory-idle-exit` lands, the superseded daemon idles forever: the
`/deploy` skill reaps it (PID-reuse-guarded kill). A no-op republish
(deterministic compilation, unchanged source) keeps the identity and the
warm daemon.

## Rollback

Point the settings.json hook command back at the previous invocation and
kill the current daemon. The old `dotnet …captainHook.dll` form remains a
valid degraded target: it dispatches collapsed on every prompt and never
spawns a daemon (the spawner's muxer guard logs `shim.spawnFailed`).

## Ground truth

| what | where |
|---|---|
| deploy procedure (the executable version of this doc) | `.claude/skills/deploy/SKILL.md` |
| shim decision tree, at-most-once boundary | `dotnet/captainHook/Core/ShimClient.cs`; [hook-dispatch.md](hook-dispatch.md) |
| spawn mechanics + muxer guard | `dotnet/captainHook/Core/DaemonSpawner.cs` |
| serve loop, spec hot-reload | `dotnet/captainHook/Core/DaemonHost.cs` |
| rendezvous paths & identity | `dotnet/captainHook/Core/Rendezvous.cs`, `Core/DaemonRendezvous.cs` |
| pinned by | `SpawnTests.cs` (detachment, muxer guard), `DaemonHostTests.cs`, `AtMostOnceTests.cs` (chaos-verified at-most-once) |
| decision record | `doc/adr/0004-daemon-topology.md` |
