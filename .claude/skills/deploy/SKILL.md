---
name: deploy
description: Deploy captAInHook to the live hook installation (~/.captainHook/bin) and verify the daemon warm path end-to-end. Stages BOTH artifacts (native captainShim + apphost captainHook engine) and swaps them in together, checks/fixes the settings.json hook commands, fires a test hook, confirms spawn + warm answer + no wire skew in the trail, and reaps daemons of superseded identities. Run after substantive changes when you want your real Claude Code session riding the new build; requires the suite green twice first.
---

# /deploy — ship the current build to the live hooks

Dogfooding runs YOUR real prompts through this code. The safety net is the
architecture (fallback, deadlines, at-most-once, the wire-stamp skew guard),
but the deploy itself must be deliberate: this skill is the one place that
touches `~/.captainHook/bin` and `~/.claude/settings.json`.

**Preconditions (refuse to proceed if unmet):**
1. Working tree clean or the user explicitly okayed deploying dirty state.
2. Ship bar: suite green **twice** (run it; don't trust memory).
3. Native AOT toolchain present (`clang --version`) — doc/platform.md.

## 1. Stage BOTH artifacts, swap together (ADR-0004 d7 amendment: N6)

The deployment is TWO artifacts that must move as one — a partial copy is the
skew the guard exists for. Stage into a sibling dir, then swap:

```sh
STAGE=~/.captainHook/bin.new
rm -rf $STAGE
dotnet publish dotnet/captainHook/captainHook.csproj -c Release -o $STAGE
dotnet publish dotnet/captainShim/captainShim.csproj -c Release -r linux-x64 -o /tmp/shim-stage
cp /tmp/shim-stage/captainShim $STAGE/
# swap: keep exactly one previous build for rollback
rm -rf ~/.captainHook/bin.prev
[ -d ~/.captainHook/bin ] && mv ~/.captainHook/bin ~/.captainHook/bin.prev
mv $STAGE ~/.captainHook/bin
```

Both executables must exist and be executable: `~/.captainHook/bin/captainShim`
(native — the hook command) and `~/.captainHook/bin/captainHook` (apphost — the
daemon/collapsed engine; never `dotnet captainHook.dll`, doc/platform.md).

## 2. Wire settings.json (idempotent check)

Every captAInHook hook command in `~/.claude/settings.json` must be exactly:

```
/home/oof/.captainHook/bin/captainShim hook <event>
```

If it still names `…/bin/captainHook` (or the ancient `dotnet …captainHook.dll`
form), back the file up, then fix with a **targeted string replacement** —
never rewrite/reformat the whole file; it holds unrelated config (cavemem
hooks etc.).

## 3. Verify the warm path (the actual acceptance test)

```sh
# hook 1: cold — expect effect on stdout + shim.fallback + shim.spawnDaemon + shim.delegated in the trail
printf '{"prompt":"deploy-verify"}' | ~/.captainHook/bin/captainShim hook user-prompt-submit
sleep 1.5
# hook 2: warm — expect shim.answered in the trail, same effect on stdout
printf '{"prompt":"deploy-verify-warm"}' | ~/.captainHook/bin/captainShim hook user-prompt-submit
```

Check `~/.captainHook/logs/captainHook.jsonl` (the default trail): the second
hook must log `shim.answered`, and the deploy window must contain **zero
`shim.wireSkew` events** — a skew line means the two artifacts didn't move
together; redo step 1 whole. If it logs `shim.fallback` twice, the daemon
didn't come up — read the trail for `daemon.*` events before touching
anything.

## 4. Reap superseded daemons

```sh
~/.captainHook/bin/captainHook doctor
```

Doctor is double-guarded (PID-reuse via cmdline; superseded = the binary at
the daemon's OWN path moved on), so it reaps the pre-redeploy daemon
(SIGTERM → drain → grace → SIGKILL), sweeps stale sockets/pidfiles, and
leaves healthy daemons and every `.lock` file alone. Safe to run any time.

## 5. Report

```
DEPLOYED — captAInHook @ <sha> → ~/.captainHook/bin  (identity <ver>)
  artifacts       captainShim <bytes> (native) + captainHook engine, swapped together
  settings.json   <already correct | fixed from engine/dll form>
  cold hook       <ms> delegated + spawned
  warm hook       <ms> answered by daemon pid <pid>
  skew guard      clean (zero shim.wireSkew in the deploy window)
  reaped          <superseded daemons killed, or none>
  rollback        mv bin → bin.bad, mv bin.prev → bin  (same hook command)
```

Rollback is one swap: `bin.prev` is the previous whole build, and the hook
command path never changes. Pointing settings.json at
`…/bin/captainHook hook <event>` also stays valid — the engine keeps its full
shim mode for exactly this.
