---
name: deploy
description: Deploy the current captAInHook build to the live hook installation and verify the daemon warm path end-to-end. Packages a local release archive (native captainShim + single-file captainHook engine + loose DLLs + the committed ui/ + the pinned bosun + the shipped payloads) with release/package.sh and installs it with release/install.sh --archive — the SAME path a public release takes — into ~/.local/share/captainhook/versions/<v> behind the ~/.local/bin links. Then checks the settings.json hook wiring with install-hooks (dry run), fires a cold and a warm hook, confirms no wire skew and the bosun spawn rung in the trail, checks the GUI shell serves, and runs doctor. Run after substantive changes when you want your real Claude Code session riding the new build; requires the suite green twice first.
---

# /deploy — ship the current build to the live hooks

Dogfooding runs YOUR real prompts through this code. The safety net is the
architecture (fallback, deadlines, at-most-once, the wire-stamp skew guard),
but the deploy itself must be deliberate: this skill is the sanctioned way to
change the live install. Since ADR-0020 it is a release in miniature — the
archive a tag would publish, installed by the installer a user runs — so dev
and release share one install path and one set of failure modes.

**Preconditions (refuse to proceed if unmet):**
1. Working tree clean or the user explicitly okayed deploying dirty state.
2. Ship bar: suite green **twice** (run it; don't trust memory).
3. Native AOT toolchain present (`clang --version`) — doc/platform.md.

**Target.** By default the live install: `~/.local/share/captainhook`
(`$CAPTAINHOOK_INSTALL_DIR`) and `~/.local/bin` (`$CAPTAINHOOK_BIN_DIR`). To
REHEARSE without touching the live install, point both variables — and `HOME`,
so the verify steps read a sandbox trail — at a scratch directory, and use a
short `XDG_RUNTIME_DIR` (socket paths cap at ~104 bytes).

## 1. Package the archive (ADR-0020 d1)

```sh
VER="$(cat release/VERSION)-dev+$(git rev-parse --short HEAD)"
ARCHIVE=$(release/package.sh "$VER" linux-x64 dist)    # osx-arm64 / osx-x64 / linux-arm64 elsewhere
echo "$ARCHIVE"
```

`package.sh` publishes the engine and the shim from ONE build (the shim's
skew guard compares the wire MVID it linked against the DLL beside it),
checks the four loose DLLs are present (identity and the guard read them),
fetches **bosun by the pinned tag and verifies it against
`release/bosun.sha256`** — the pin lives in the repo; a mismatch aborts — and
copies the COMMITTED `ui/` (deploy never runs npm; if `web/` changed, the
commit must carry the rebuilt `ui/`).

The `+<sha>` is SemVer build metadata: it names the archive and its folder but
is NOT stamped into the assemblies. A docs-only commit therefore deploys a new
folder with the SAME content identity, and the warm daemon keeps serving.

**Bumping bosun:** read the new release's `SHA256SUMS`, replace
`release/bosun.sha256` (tag line + digests) wholesale in a commit that says
why, and re-run this deploy's verification.

## 2. Install it (ADR-0020 d2–d3)

```sh
sh release/install.sh --archive "$ARCHIVE"
```

Unpack into `versions/<VER>`, prove the new engine runs, switch the two bin
links and the payload links by rename, record `previous`, prune anything
older, run doctor. It edits no config.

- **Same identity** (no code change): the next hook rendezvouses on the same
  socket and the warm daemon answers — no cold start.
- **New identity:** the next hook finds no daemon on the new socket, spawns
  one and delegates. The previous version's daemon is unreachable and
  idle-exits; doctor labels it "another installed build". That is correct.

## 3. Hook wiring (idempotent check)

```sh
~/.local/bin/captainHook install-hooks          # dry run — prints the diff, writes nothing
```

It must say **up to date**: every captAInHook hook command already names
`~/.local/bin/captainShim hook <event>`, which no deploy changes. If it shows
a diff — the first deploy after moving off the legacy `~/.captainHook/bin`, or
a hand edit — show the user the diff and run `install-hooks --apply` (backup +
write) **only with their OK**. Never edit `settings.json` by hand; it holds
unrelated config (cavemem hooks etc.).

## 4. Verify the warm path (the actual acceptance test)

```sh
SHIM=~/.local/bin/captainShim
printf '{"prompt":"deploy-verify"}' | $SHIM hook user-prompt-submit        # cold if the identity is new
sleep 1.5
printf '{"prompt":"deploy-verify-warm"}' | $SHIM hook user-prompt-submit   # warm
```

Check `~/.captainHook/logs/captainHook.jsonl`: the second hook must log
`shim.answered`, and the deploy window must contain **zero `shim.wireSkew`
events** — a skew line means the shim and the DLLs came from different
builds; re-run step 1 whole. If it logs `shim.fallback` twice, the daemon
didn't come up — read the trail for `daemon.*` events before touching
anything.

Then the spawn rung, if a payload is registered (ADR-0014 d2). Fire an event
that `~/.captainHook/handlers.json` serves — `pre-tool-use` when a gate is
installed, else `session-start`:

```sh
printf '{"tool_name":"Bash","tool_input":{"command":"true"}}' | $SHIM hook pre-tool-use >/dev/null
grep '"evt":"exec.spawn"' ~/.captainHook/logs/captainHook.jsonl | tail -1
```

The last `exec.spawn` must carry `"spawner":"bosun"` and NO `"pgroup":false`.
`spawner=setsid` means the archive's bosun is missing or unreadable — a
packaging defect, not a degrade to accept.

Then the GUI shell (ADR-0008 d2 — the warm daemon is serving):

```sh
# the rendezvous dir: $XDG_RUNTIME_DIR/captainHook when set, else ~/.captainHook itself
RUNDIR=${XDG_RUNTIME_DIR:+$XDG_RUNTIME_DIR/captainHook}; RUNDIR=${RUNDIR:-$HOME/.captainHook}
API=$(ls "$RUNDIR"/captaind-$(~/.local/bin/captainHook version | sed -n 's/^identity *//p').api.json)
PORT=$(grep -o '"port":[0-9]*' "$API" | cut -d: -f2)
SHELL_HTML=$(curl -sf "http://127.0.0.1:$PORT/ui")
grep -q 'id="nav"' <<<"$SHELL_HTML" && echo "shell ok"
# the HASHED assets too — a staged index.html with missing assets is a blank
# page. The class MUST include _ and -: Vite's base64url hashes use both.
for a in $(grep -oE 'assets/index-[A-Za-z0-9_-]+\.(js|css)' <<<"$SHELL_HTML" | sort -u); do
  echo "  $a → $(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:$PORT/ui/$a")"
done
```

Several `captaind-*.api.json` can exist right after an identity change (the
previous version's daemon is still up until it idles out) — which is why the
snippet picks the one named for the identity `captainHook version` prints. The
API port is one per machine (4665): a REHEARSAL daemon next to the live one
cannot bind it, so set `CAPTAINHOOK_API_PORT` to a spare port for a rehearsal.

## 5. Doctor

```sh
~/.local/bin/captainHook doctor
```

Expect "serving this build" for the new daemon and, after an identity change,
"serving another installed build" for the previous one — both healthy, both
left alone. A pruned version's daemon was already drained by step 2.

## 6. Report

```
DEPLOYED — captAInHook @ <sha> → ~/.local/share/captainhook/versions/<VER>  (identity <id>)
  archive         <name>.tar.gz (<MB>) — bosun <tag> verified against release/bosun.sha256
  links           ~/.local/bin/{captainShim,captainHook} → versions/<VER>; previous <ver>
  settings.json   up to date | <diff shown, applied with OK | left for the user>
  cold hook       <ms> delegated + spawned  | warm daemon kept (same identity)
  warm hook       <ms> answered by daemon pid <pid>
  skew guard      clean (zero shim.wireSkew in the deploy window)
  spawn rung      spawner=bosun <| setsid — PACKAGING DEFECT | no payload fired>
  gui shell       /ui serves + <n> assets 200 <| ABSENT>
  doctor          <this build pid …; another installed build pid … | nothing else>
  rollback        sh release/install.sh --rollback   (again to return)
```

**Rollback is one switch:** `install.sh --rollback` points every link at
`previous` and keeps the version it left as the new `previous`. The hook
command path never changes, so `settings.json` is untouched either way.

## The legacy tree

Before ADR-0020 the live install was `~/.captainHook/bin` (+ `bin.prev`),
swapped in place. Once `install-hooks` reports no hook command still naming
it and no daemon runs from it (`doctor`), it can be deleted. `deploy-guard`
still guards it until then, alongside the versioned layout.
