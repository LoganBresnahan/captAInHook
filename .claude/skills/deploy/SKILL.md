---
name: deploy
description: Deploy captAInHook to the live hook installation (~/.captainHook/bin) and verify the daemon warm path end-to-end. Publishes the apphost build, checks/fixes the settings.json hook command, fires a test hook, confirms spawn + warm answer in the trail, and reaps daemons of superseded identities. Run after substantive changes when you want your real Claude Code session riding the new build; requires the suite green twice first.
---

# /deploy — ship the current build to the live hooks

Dogfooding runs YOUR real prompts through this code. The safety net is the
architecture (fallback, deadlines, at-most-once), but the deploy itself must
be deliberate: this skill is the one place that touches `~/.captainHook/bin`
and `~/.claude/settings.json`.

**Preconditions (refuse to proceed if unmet):**
1. Working tree clean or the user explicitly okayed deploying dirty state.
2. Ship bar: suite green **twice** (run it; don't trust memory).

## 1. Publish the apphost

```sh
dotnet publish dotnet/captainHook/captainHook.csproj -c Release -o ~/.captainHook/bin
```

The **apphost executable** (`~/.captainHook/bin/captainHook`, no extension) is
mandatory — invoked as `dotnet captainHook.dll`, `Environment.ProcessPath` is
the dotnet muxer and the daemon spawner refuses (`shim.spawnFailed`, by
design; doc/platform.md). Confirm the file exists and is executable.

Record the deployed content identity — it names the socket:

```sh
printf '{}' | ~/.captainHook/bin/captainHook hook user-prompt-submit >/dev/null   # any hook logs it
# or compute directly: the identity of ~/.captainHook/bin per Core/Rendezvous.cs
```

## 2. Wire settings.json (idempotent check)

Every captAInHook hook command in `~/.claude/settings.json` must be exactly:

```
/home/oof/.captainHook/bin/captainHook hook <event>
```

If it still says `…dotnet …captainHook.dll hook <event>`, back the file up,
then fix with a **targeted string replacement** — never rewrite/reformat the
whole file; it holds unrelated config (cavemem hooks etc.).

## 3. Verify the warm path (the actual acceptance test)

```sh
# hook 1: cold — expect collapsed effect on stdout + shim.fallback + shim.spawnDaemon in the trail
printf '{"prompt":"deploy-verify"}' | ~/.captainHook/bin/captainHook hook user-prompt-submit
sleep 1.5
# hook 2: warm — expect shim.answered in the trail, same effect on stdout
printf '{"prompt":"deploy-verify-warm"}' | ~/.captainHook/bin/captainHook hook user-prompt-submit
```

Check `~/.captainHook/logs/captainHook.jsonl` (the default trail): the second
hook must log `shim.answered`. If it logs `shim.fallback` twice, the daemon
didn't come up — read the trail for `daemon.*` events before touching
anything.

## 4. Reap superseded daemons  ⚠ until `mandatory-idle-exit` lands

A redeploy mints a new content identity; the previous identity's daemon idles
forever. For each `captaind-*.pid` in the rendezvous dir (`$XDG_RUNTIME_DIR/
captainHook/`, else `~/.captainHook/`) whose identity is NOT the one just
deployed: verify the pid's `/proc/<pid>/exe` still points at a captainHook
binary (PID-reuse guard), then `kill` it. Never delete `.lock` files.

## 5. Report

```
DEPLOYED — captAInHook @ <sha> → ~/.captainHook/bin  (identity <ver>)
  settings.json   <already correct | fixed from dotnet-dll form>
  cold hook       <ms> collapsed + spawned
  warm hook       <ms> answered by daemon pid <pid>
  reaped          <superseded daemons killed, or none>
  rollback        point settings.json back at the previous command; kill the daemon
```

The old `captainHook.dll` invocation keeps working as a rollback target (it
just never spawns daemons), so rollback = revert the settings.json line.
