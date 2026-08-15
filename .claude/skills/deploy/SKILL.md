---
name: deploy
description: Deploy captAInHook to the live hook installation (~/.captainHook/bin) and verify the daemon warm path end-to-end. Stages ALL FOUR artifacts (native captainShim + single-file captainHook engine + the committed ui/ GUI assets + the pinned bosun spawn wrapper) and swaps them in together, checks/fixes the settings.json hook commands, fires a test hook, confirms spawn + warm answer + no wire skew + the bosun spawn rung in the trail, checks the GUI shell serves, and reaps daemons of superseded identities. Run after substantive changes when you want your real Claude Code session riding the new build; requires the suite green twice first.
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

## 1. Stage the artifacts, swap together (ADR-0004 d7 amendment: N6; ADR-0008 d2; ADR-0014 d3)

The deployment is FOUR artifacts that move as one. The two managed executables
are wire-coupled — a partial copy of THOSE is the skew the guard exists for.
The `ui/` dir and `bosun` are NOT wire-coupled (neither carries an MVID, so
both are invisible to content identity — bosun by the same rule as the native
shim, doc/platform.md § Native AOT) but they stage and swap in the same motion
so a deploy is one atomic thing and `bin.prev` rolls all four back together.

### 1a. Fetch bosun — pinned tag, verified checksum (ADR-0014 d4)

`bosun` is the spawn prefix for every exec-handler payload: it execs the
payload in place as a session leader, so `kill(-pgid)` reaches reparented
grandchildren. It replaces the `setsid(1)` PATH probe, which stock macOS does
not satisfy. **Never fetch "latest"** — an unpinned binary would change deploy
bytes behind unchanged source, which build determinism cannot tolerate.

**THE PIN — bump deliberately, tag and digests together.** The digests live
HERE, in the repo, not only in the release: verifying a download against a
manifest fetched from the same release proves only that the download
completed. This is the record that says *these exact bytes*.

```
bosun v0.1.0   (released 2026-08-11, https://github.com/LoganBresnahan/bosun)
  f0076e2c9039e5348b6ad9052fbc7fe27b53a1040e8f0c17a40dfb90f9a668f8  bosun-aarch64-linux
  4617e989c5befba9487362ccb9be6fa7a58fddb6d51c830c06c10bd1ad230320  bosun-aarch64-macos
  b8d5b70f6f9df5cfa0b35baf0145fe776d161f52f22149fd6838a99a178bfa1f  bosun-x86_64-linux
  fe726bee7f0c12d43894144ac2904914c3ef7720bb2a3f9f3fb6fbc3f3065a04  bosun-x86_64-macos
```

```sh
BOSUN_TAG=v0.1.0
BOSUN_TARGET=x86_64-linux            # this host; also aarch64-linux, {x86_64,aarch64}-macos
BOSUN_SHA=b8d5b70f6f9df5cfa0b35baf0145fe776d161f52f22149fd6838a99a178bfa1f
BOSUN_REPO=https://github.com/LoganBresnahan/bosun
BOSUN_DL=/tmp/bosun-$BOSUN_TAG

mkdir -p $BOSUN_DL && cd $BOSUN_DL
curl -fsSLO $BOSUN_REPO/releases/download/$BOSUN_TAG/bosun-$BOSUN_TARGET
echo "$BOSUN_SHA  bosun-$BOSUN_TARGET" | sha256sum -c -    # MUST print OK
cd -
```

A failed checksum aborts the deploy — do not stage an unverified binary; a
mismatch means either the pin is stale (someone re-cut the tag) or the bytes
are not the ones this repo was tested against. The download is cached per tag,
so a re-deploy at the same pin re-verifies local bytes with no network round
trip (delete `$BOSUN_DL` to force a refetch).

To bump: read the new release's `SHA256SUMS`, replace the block above wholesale
in the same commit that states why, and re-run the deploy verification.

### 1b. Stage into a sibling dir, then swap

```sh
STAGE=~/.captainHook/bin.new
rm -rf $STAGE
# Single-file self-contained (ADR-0012): the runtime rides INSIDE the exe (no
# host .NET needed); the four app assemblies stay LOOSE beside it (the csproj's
# KeepAppAssembliesLooseForIdentity target) because ContentIdentity hashes the
# deploy dir's *.dll MVIDs and the shim's skew guard reads captainHookWire.dll
# — a fully-bundled publish would break both.
dotnet publish dotnet/captainHook/captainHook.csproj -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o $STAGE
dotnet publish dotnet/captainShim/captainShim.csproj -c Release -r linux-x64 -o /tmp/shim-stage
cp /tmp/shim-stage/captainShim $STAGE/
cp -r ui $STAGE/ui        # the COMMITTED GUI assets (web/ builds them; repo root ui/)
install -m 0755 $BOSUN_DL/bosun-$BOSUN_TARGET $STAGE/bosun   # the VERIFIED wrapper, named bare
# swap: keep exactly one previous build for rollback
rm -rf ~/.captainHook/bin.prev
[ -d ~/.captainHook/bin ] && mv ~/.captainHook/bin ~/.captainHook/bin.prev
mv $STAGE ~/.captainHook/bin
```

Both executables must exist and be executable: `~/.captainHook/bin/captainShim`
(native — the hook command) and `~/.captainHook/bin/captainHook` (single-file
self-contained engine, ~74MB; never `dotnet captainHook.dll`, doc/platform.md).
The four loose app DLLs must ALSO be present (`captainHook.dll`,
`captainHookWire.dll`, `captainHookActors.dll`, `FSharp.Core.dll`) — identity
and the skew guard read them; their absence means the exclusion target didn't
run and the shim will read permanent skew.
`~/.captainHook/bin/bosun` must exist and be executable — the engine resolves
its spawn prefix by co-location (`AppContext.BaseDirectory`), so **on a live
deploy the bosun rung must win**. Its absence is a STAGING DEFECT, not a
degrade to accept: the engine silently falls back to `setsid(1)` from PATH
(fine on this Linux box, absent on macOS) and the trail says `spawner=setsid`.
Cheap proof it is the right binary for this host:

```sh
~/.captainHook/bin/bosun --help >/dev/null && echo "bosun runs"   # exit 0
```

`~/.captainHook/bin/ui/index.html` must exist — the daemon serves `GET /ui`
from this dir (absent ⇒ the GUI 404s; hooks are unaffected). If `web/` sources
changed this session, the committed `ui/` must have been rebuilt in that commit
(`cd web && npm run build`) — deploy never runs npm (Node is dev-only,
ADR-0008); it ships what is committed.

### 1c. Discard a pre-0600 trail (ADR-0016 d13)

Both emitters now create the trail `0600` and `logs/` `0700` — but
`UnixCreateMode` applies **on create only**, and `Directory.CreateDirectory` is
a no-op on a directory that already exists. A tree from before that fix keeps
its umask modes (`0644`/`0755`) forever, so the deploy is where the old one goes.

Deleting rather than `chmod`-ing is deliberate: the trail is operational
telemetry with a days-to-weeks life (d13), the archival store is `mail/` and is
untouched by this, and a fresh file created by the new build is self-evidently
correct where a chmod'ed one only looks it. Both emitters open-write-close per
line and hold no fd, so nothing is holding the old inode.

```sh
# Only when the existing modes are the loose ones — never widen, never surprise
# a user who tightened something themselves.
if [ -d ~/.captainHook/logs ] && [ "$(stat -c %a ~/.captainHook/logs)" != 700 ]; then
  rm -rf ~/.captainHook/logs      # recreated 0700 by the first line the new build writes
fi
```

This drops the JSONL history, including any `session-pulse.jsonl` or other
payload-written logs living beside it — say so in the report rather than
letting it be noticed later. Verify after step 3 has written a line:

```sh
stat -c '%a %n' ~/.captainHook/logs ~/.captainHook/logs/captainHook.jsonl   # want 700, 600
```

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

Then the spawn rung, if a payload is registered (ADR-0014 d2). Fire an event
that `~/.captainHook/handlers.json` actually serves — `pre-tool-use` when a
gate is installed, else `session-start`:

```sh
printf '{"tool_name":"Bash","tool_input":{"command":"true"}}' \
  | ~/.captainHook/bin/captainShim hook pre-tool-use >/dev/null
grep '"evt":"exec.spawn"' ~/.captainHook/logs/captainHook.jsonl | tail -1
```

The last `exec.spawn` must carry `"spawner":"bosun"` and NO `"pgroup":false`.
`spawner=setsid` means the co-located rung lost — step 1b didn't stage, or
staged unreadable; fix the staging (hooks keep working either way, on the
weaker kill discipline). No `exec.spawn` at all just means no payload serves
that event — then step 1's `bosun --help` check is the binding one.

Then the GUI shell (ADR-0008 d2 — the warm daemon from hook 2 is serving):

```sh
# port + token from the daemon's discovery file (0600, version-partitioned)
API=$(ls "${XDG_RUNTIME_DIR:-$HOME/.captainHook}"/captainHook/captaind-*.api.json 2>/dev/null | head -1)
PORT=$(grep -o '"port":[0-9]*' "$API" | cut -d: -f2)
SHELL_HTML=$(curl -sf "http://127.0.0.1:$PORT/ui")               # shell serves, no token needed
grep -q 'id="nav"' <<<"$SHELL_HTML" && echo "shell ok"           # the sidebar shell's first island mount
# and its HASHED assets must serve too — a staged index.html whose assets are
# missing renders a blank page while the shell check alone says "fine".
for a in $(grep -o 'assets/index-[A-Za-z0-9]*\.\(js\|css\)' <<<"$SHELL_HTML"); do
  echo "  $a → $(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:$PORT/ui/$a")"
done
```

A 404 here means the `ui/` dir didn't stage (step 1's `cp -r ui`); hooks are
unaffected either way — fix the staging, don't roll back for this alone.

*(Marker updated 2026-08-11: ADR-0015 slice 2 replaced the single
`<div id="app">` mount with the sidebar shell — rail + view region + one mount
per island — so the old grep reported ABSENT on a perfectly healthy deploy.
Caught on the first post-overhaul deploy. `id="nav"` is the structural marker
now; the per-asset check is new, because "index.html serves" never proved the
bundle beside it did.)*

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
  artifacts       captainShim <bytes> (native) + captainHook engine + ui/ (<n> files) + bosun <tag>, swapped together
  bosun pin       <tag> / <target> — SHA256SUMS verified OK
  settings.json   <already correct | fixed from engine/dll form>
  cold hook       <ms> delegated + spawned
  warm hook       <ms> answered by daemon pid <pid>
  skew guard      clean (zero shim.wireSkew in the deploy window)
  spawn rung      spawner=bosun <| setsid — STAGING DEFECT | no payload fired>
  gui shell       /ui serves <200 | ABSENT — staging missed ui/>
  reaped          <superseded daemons killed, or none>
  rollback        mv bin → bin.bad, mv bin.prev → bin  (same hook command)
```

Rollback is one swap: `bin.prev` is the previous whole build, and the hook
command path never changes. Pointing settings.json at
`…/bin/captainHook hook <event>` also stays valid — the engine keeps its full
shim mode for exactly this.
