#!/bin/sh
# release/smoke.sh — exercise an INSTALLED captAInHook for real (ADR-0020 d7).
#
#   release/smoke.sh
#
# Run after install.sh, in a THROWAWAY HOME (CI: a fresh runner; locally: a
# sandbox) — it writes ~/.claude/settings.json and ~/.captainHook/handlers.json,
# so it refuses a home that already has either. Reads the same env install.sh does (CAPTAINHOOK_INSTALL_DIR,
# CAPTAINHOOK_BIN_DIR) and XDG_RUNTIME_DIR (unset → ~/.captainHook).
#
# Proves, in order: the links resolve to the real versioned folder; install-hooks
# writes a settings file (a temp one under this HOME); a cold hook spawns the
# daemon; a warm hook is answered by it; a shipped payload runs through bosun
# from its payloads/ link; zero wire skew; doctor sees a healthy daemon; the
# daemon idle-exits on its own; and, under an XDG_RUNTIME_DIR too long for a
# socket, a daemon still comes up — in the short per-uid dir
# /tmp/captainhook-<uid> (ADR-0004 d3); a SIGKILLed daemon's leftover children
# (an EOF-deaf resident, a forked grandchild, an in-flight oneshot) die when the
# next daemon starts (ADR-0010); `captainHook self prune` keeps the
# current version and removes a stale one; and `captainHook self uninstall`
# refuses while the hooks are wired, then removes the install once they are
# not — so the smoke ENDS uninstalled. Prints the hook latencies. Exit 0 = pass.
set -eu

INSTALL_DIR=${CAPTAINHOOK_INSTALL_DIR:-${XDG_DATA_HOME:-$HOME/.local/share}/captainhook}
BIN_DIR=${CAPTAINHOOK_BIN_DIR:-$HOME/.local/bin}
SHIM=$BIN_DIR/captainShim
ENGINE=$BIN_DIR/captainHook
TRAIL=$HOME/.captainHook/logs/captainHook.jsonl
IDLE_MS=${SMOKE_IDLE_MS:-6000}

pass() { printf 'smoke: ok    %s\n' "$*"; }
fail() { printf 'smoke: FAIL  %s\n' "$*" >&2; [ -f "$TRAIL" ] && { echo "--- trail tail" >&2; tail -20 "$TRAIL" >&2; }; exit 1; }
# Monotonic where perl has it (invariant 2); a bare Mac's system perl may not
# implement clock_gettime, and a reported latency survives the fallback. The
# system perl first: a version-manager shim on PATH (asdf, …) can break under
# the throwaway HOME. Never fails — whole seconds as the last resort.
now_ms() {
  for p in /usr/bin/perl perl; do
    # shellcheck disable=SC2016  # perl's $t, not the shell's
    v=$("$p" -MTime::HiRes -e 'my $t = eval { Time::HiRes::clock_gettime(Time::HiRes::CLOCK_MONOTONIC()) } // Time::HiRes::time(); printf "%d\n", $t * 1000' 2>/dev/null) \
      && { echo "$v"; return; }
  done
  echo $(( $(date +%s) * 1000 ))
}
count() { [ -f "$TRAIL" ] || { echo 0; return; }; grep -c "\"evt\":\"$1\"" "$TRAIL" || true; }
wait_for() {  # wait_for <seconds> <command...>
  t=$1; shift
  end=$(( $(now_ms) + t * 1000 ))
  while [ "$(now_ms)" -lt "$end" ]; do "$@" && return 0; sleep 0.2; done
  return 1
}

# This script WRITES $HOME/.claude/settings.json and $HOME/.captainHook/
# handlers.json. Under a real home that would replace the user's own — so a
# home that already has either is refused, before anything is written.
for f in "$HOME/.claude/settings.json" "$HOME/.captainHook/handlers.json"; do
  if [ -e "$f" ]; then
    printf 'smoke: refusing to run: %s exists — this HOME is not a throwaway one (run it under a fresh, empty HOME)\n' "$f" >&2
    exit 2
  fi
done
if [ ! -x "$SHIM" ] || [ ! -x "$ENGINE" ]; then fail "no installed captainShim/captainHook under $BIN_DIR"; fi
export CAPTAINHOOK_IDLE_MS="$IDLE_MS"
# Where the daemon meets its shims: ASK the engine — the choice (preferred dir,
# or the short per-uid one when a socket would not fit) is the wire lib's, and
# a script that recomputed it would drift from it.
rundir() { "$ENGINE" version | sed -n 's/^rendezvous  *//p' | sed 's/ (short dir:.*//; s/ REFUSED .*//'; }
RUNDIR=$(rundir)
[ -n "$RUNDIR" ] || fail "captainHook version names no rendezvous dir"
id=$("$ENGINE" version | sed -n 's/^identity *//p')
[ ! -e "$RUNDIR/captaind-$id.pid" ] || fail "a daemon of this build is already running in $RUNDIR — stop it, or use a fresh HOME"

# 1. the link resolved to the REAL versioned folder (AppContext.BaseDirectory)
base=$("$ENGINE" version | sed -n 's/^base *//p')
real_install=$(cd "$INSTALL_DIR" && pwd -P)
case $base in
  "$real_install"/versions/*/) pass "engine base is the versioned folder: $base" ;;
  *) fail "engine base '$base' is not under $real_install/versions/ — symlink resolution broke co-location" ;;
esac
"$ENGINE" version | sed 's/^/        /'

# 2. install-hooks into a temp settings file: dry run writes nothing, --apply writes
SETTINGS=$HOME/.claude/settings.json
mkdir -p "$HOME/.claude"
printf '{\n  "theme": "dark"\n}\n' > "$SETTINGS"
"$ENGINE" install-hooks --settings "$SETTINGS" >/dev/null
grep -q captainShim "$SETTINGS" && fail "install-hooks dry run modified the settings file"
"$ENGINE" install-hooks --settings "$SETTINGS" --apply >/dev/null
grep -q "\"$SHIM hook pre-tool-use\"" "$SETTINGS" || fail "install-hooks --apply did not wire $SHIM"
ls "$SETTINGS".bak-captainhook-* >/dev/null 2>&1 || fail "install-hooks --apply made no backup"
"$ENGINE" install-hooks --settings "$SETTINGS" | grep -q 'up to date' || fail "install-hooks is not idempotent"
pass "install-hooks: dry run, apply + backup, idempotent"

# A shipped payload, through its stable payloads/ link (ADR-0020 d6).
mkdir -p "$HOME/.captainHook"
cat > "$HOME/.captainHook/handlers.json" <<EOF
{"version":1,"handlers":[{"name":"deploy-guard","command":"$INSTALL_DIR/payloads/deploy-guard.sh","events":["pre-tool-use"],"mode":"oneshot","failMode":"open","budgetMs":3000}]}
EOF

# 3. cold hook: the shim finds no daemon, spawns one, and delegates
t0=$(now_ms)
out=$(printf '{"prompt":"smoke-cold","session_id":"smoke"}' | "$SHIM" hook user-prompt-submit 2>/dev/null)
t1=$(now_ms)
COLD_MS=$((t1 - t0))
case $out in *'"hookEventName":"UserPromptSubmit"'*) ;; *) fail "cold hook printed no effect: '$out'" ;; esac
[ "$(count shim.spawnDaemon)" -ge 1 ] || fail "cold hook did not spawn a daemon"
pass "cold hook answered in ${COLD_MS}ms (fallback + spawnDaemon)"

has_pid() { [ -e "$RUNDIR/captaind-$id.pid" ]; }
wait_for 30 has_pid || fail "the spawned daemon never wrote its pidfile in $RUNDIR"

# 4. warm hook: answered by the daemon over the socket
answered() {
  printf '{"prompt":"smoke-warm","session_id":"smoke"}' | "$SHIM" hook user-prompt-submit >/dev/null 2>&1
  [ "$(count shim.answered)" -ge 1 ]
}
wait_for 30 answered || fail "no warm hook was answered by the daemon (shim.answered)"
t0=$(now_ms)
printf '{"prompt":"smoke-warm-2","session_id":"smoke"}' | "$SHIM" hook user-prompt-submit >/dev/null 2>&1
WARM_MS=$(( $(now_ms) - t0 ))
pass "warm hook answered by the daemon in ${WARM_MS}ms"

# 5. a shipped payload runs through bosun, in its own process group
printf '{"tool_name":"Bash","tool_input":{"command":"true"},"session_id":"smoke"}' | "$SHIM" hook pre-tool-use >/dev/null 2>&1
spawned() { grep '"evt":"exec.spawn"' "$TRAIL" 2>/dev/null | grep -q deploy-guard; }
wait_for 10 spawned || fail "the deploy-guard payload never spawned"
line=$(grep '"evt":"exec.spawn"' "$TRAIL" | grep deploy-guard | tail -1)
case $line in *'"spawner":"bosun"'*) ;; *) fail "payload not spawned by bosun: $line" ;; esac
case $line in *'"pgroup":false'*) fail "payload spawned without a process group: $line" ;; esac
grep '"evt":"exec.answered"' "$TRAIL" | grep -q deploy-guard || fail "deploy-guard did not answer"
pass "shipped payload ran via payloads/ link, spawner=bosun, own process group"

# 6. the two artifacts moved together
[ "$(count shim.wireSkew)" -eq 0 ] || fail "shim.wireSkew in the trail — shim and DLLs are from different builds"
pass "zero shim.wireSkew"

# 7. doctor sees the daemon as healthy and reaps nothing
doc=$("$ENGINE" doctor)
printf '%s\n' "$doc" | grep -q 'healthy' || fail "doctor did not report a healthy daemon: $doc"
pass "$(printf '%s\n' "$doc" | grep healthy | head -1)"

# 8. idle exit: no traffic for CAPTAINHOOK_IDLE_MS ⇒ the daemon leaves by itself
gone() { ! has_pid && [ "$(count daemon.idleExit)" -ge 1 ]; }
wait_for $(( IDLE_MS / 1000 + 30 )) gone || fail "the daemon did not idle-exit within $((IDLE_MS / 1000 + 30))s"
pass "daemon idle-exited after ${IDLE_MS}ms without traffic"

# 9. a runtime dir too long for a socket still gets a daemon: the short dir
# (roadmap item 26). Everything resolves it alike — version, the shim, the
# daemon, doctor — so each one is asked.
LONG=$HOME/xdg-runtime-dir-long-enough-that-a-socket-path-under-it-overflows-sun-path-on-every-platform-we-ship
SHORT=/tmp/captainhook-$(id -u)
if [ -e "$SHORT/captaind-$id.pid" ]; then fail "a daemon of this build already runs in $SHORT — stop it first"; fi
v=$(XDG_RUNTIME_DIR=$LONG "$ENGINE" version | sed -n 's/^rendezvous *//p')
case $v in "$SHORT (short dir:"*) ;; *) fail "version does not name the short dir under a long XDG_RUNTIME_DIR: '$v'" ;; esac
spawns=$(count shim.spawnDaemon); answers=$(count shim.answered)
t0=$(now_ms)
out=$(printf '{"prompt":"smoke-long","session_id":"smoke"}' | XDG_RUNTIME_DIR=$LONG "$SHIM" hook user-prompt-submit 2>/dev/null)
LONG_COLD_MS=$(( $(now_ms) - t0 ))
case $out in *'"hookEventName":"UserPromptSubmit"'*) ;; *) fail "long-path cold hook printed no effect: '$out'" ;; esac
[ "$(count shim.spawnDaemon)" -gt "$spawns" ] || fail "long-path cold hook did not spawn a daemon"
short_pid() { [ -e "$SHORT/captaind-$id.pid" ]; }
wait_for 30 short_pid || fail "no daemon pidfile appeared in $SHORT"
[ -n "$(find "$SHORT" -maxdepth 0 -type d -perm 700)" ] || fail "$SHORT is not a 0700 directory"
long_answered() {
  printf '{"prompt":"smoke-long-warm","session_id":"smoke"}' | XDG_RUNTIME_DIR=$LONG "$SHIM" hook user-prompt-submit >/dev/null 2>&1
  [ "$(count shim.answered)" -gt "$answers" ]
}
wait_for 30 long_answered || fail "no warm hook was answered by the short-dir daemon"
doc=$(XDG_RUNTIME_DIR=$LONG "$ENGINE" doctor)
printf '%s\n' "$doc" | head -1 | grep -qF "rendezvous dir $SHORT (short dir:" || fail "doctor does not name the short dir: $doc"
printf '%s\n' "$doc" | grep -q "captaind-$id: healthy" || fail "doctor did not find the short-dir daemon healthy: $doc"
short_gone() { ! short_pid; }
wait_for $(( IDLE_MS / 1000 + 30 )) short_gone || fail "the short-dir daemon did not idle-exit"
pass "long XDG_RUNTIME_DIR: daemon in $SHORT (0700), cold ${LONG_COLD_MS}ms, warm answered, doctor names it, idle-exited"

# 10. a SIGKILLed daemon's leftovers die when the next daemon starts
# (ADR-0010, 2026-10-19 amendment). Measured on v0.1.1: a resident that
# ignores stdin EOF, a resident's forked grandchild and a oneshot mid-run all
# outlived a SIGKILLed daemon, and nothing ever killed them. Now the next
# daemon's boot reap does — from the resident records and the dead daemon's
# oneshot ledger.
P=$HOME/smoke-payloads
mkdir -p "$P"
# A resident's loop: answer each envelope with a noop echoing its dispatchId.
# shellcheck disable=SC2016  # the payload's $e and $id, expanded when it runs
LOOP='while IFS= read -r e; do id=$(printf "%s" "$e" | sed -n "s/.*\"dispatchId\":\"\([^\"]*\)\".*/\1/p"); printf "{\"effect\":\"noop\",\"dispatchId\":\"%s\"}\n" "$id"; done'
cat > "$P/deaf.sh" <<EOF
#!/bin/sh
# ignores stdin EOF: when its daemon's pipe closes, it sleeps on
echo \$\$ > "$P/deaf.pid"
printf '{"ready":1}\n'
$LOOP
exec sleep 100000
EOF
cat > "$P/forker.sh" <<EOF
#!/bin/sh
# forks a long-lived grandchild into its own process group
sleep 100000 </dev/null >/dev/null 2>&1 &
echo \$! > "$P/grandchild.pid"
printf '{"ready":1}\n'
$LOOP
EOF
cat > "$P/slow.sh" <<EOF
#!/bin/sh
# a oneshot still running when the kill lands
echo \$\$ > "$P/slow.pid"
cat >/dev/null
exec sleep 600
EOF
chmod +x "$P/deaf.sh" "$P/forker.sh" "$P/slow.sh"
HANDLERS=$HOME/.captainHook/handlers.json
cp "$HANDLERS" "$HANDLERS.smoke-saved"
cat > "$HANDLERS" <<EOF
{"version":1,"handlers":[
 {"name":"deaf","command":"$P/deaf.sh","events":["user-prompt-submit"],"mode":"resident"},
 {"name":"forker","command":"$P/forker.sh","events":["user-prompt-submit"],"mode":"resident"},
 {"name":"slow","command":"$P/slow.sh","events":["stop"],"mode":"oneshot","budgetMs":30000}
]}
EOF
alive() { s=$(ps -o stat= -p "$1" 2>/dev/null) || return 1; case $s in *Z*|'') return 1 ;; esac; }
dpid() { sed 's/.*"Pid":\([0-9]*\).*/\1/' "$RUNDIR/captaind-$id.pid" 2>/dev/null; }
printf '{"session_id":"smoke"}' | "$SHIM" hook session-end >/dev/null 2>&1   # cold: spawns a daemon
residents_up() { [ -s "$P/deaf.pid" ] && [ -s "$P/grandchild.pid" ] && has_pid; }
wait_for 30 residents_up || fail "the orphan-test residents never started"
printf '{"session_id":"smoke"}' | "$SHIM" hook stop >/dev/null 2>&1 &   # in flight when the kill lands
STOP_HOOK=$!
wait_for 15 test -s "$P/slow.pid" || fail "the slow oneshot never started"
D=$(dpid); DEAF=$(cat "$P/deaf.pid"); GRAND=$(cat "$P/grandchild.pid"); SLOW=$(cat "$P/slow.pid")
kill -9 "$D"
wait "$STOP_HOOK" 2>/dev/null || true
dead() { ! alive "$D"; }
wait_for 10 dead || fail "the daemon survived SIGKILL"
if ! { alive "$DEAF" && alive "$GRAND" && alive "$SLOW"; }; then
  fail "precondition: not every orphan outlived the SIGKILLed daemon (deaf=$DEAF grandchild=$GRAND slow=$SLOW)"
fi
reaps=$(count daemon.reapOrphan)
printf '{"session_id":"smoke"}' | "$SHIM" hook session-end >/dev/null 2>&1   # the next daemon
reaped() { ! alive "$DEAF" && ! alive "$GRAND" && ! alive "$SLOW"; }
wait_for 20 reaped || fail "the next daemon left orphans alive (deaf=$DEAF grandchild=$GRAND slow=$SLOW)"
got=$(( $(count daemon.reapOrphan) - reaps ))
[ "$got" -ge 3 ] || fail "expected 3 daemon.reapOrphan lines, got $got"
doc=$("$ENGINE" doctor)
if printf '%s\n' "$doc" | grep -q ': orphan'; then fail "doctor still reports orphans after the reap: $doc"; fi
mv "$HANDLERS.smoke-saved" "$HANDLERS"
wait_for $(( IDLE_MS / 1000 + 30 )) gone || fail "the reaping daemon did not idle-exit"
pass "SIGKILLed daemon: its EOF-deaf resident, forked grandchild and in-flight oneshot reaped by the next daemon ($got daemon.reapOrphan)"

# 11. self prune: the version folder's own install.sh, run by the engine
# (ADR-0020 d8) — keeps the current version, removes a stale one.
cur=$(basename "$base")
mkdir -p "$INSTALL_DIR/versions/0.0.0-smoke" && echo 0.0.0-smoke > "$INSTALL_DIR/versions/0.0.0-smoke/VERSION"
out=$("$ENGINE" self prune </dev/null 2>&1) || fail "self prune failed: $out"
printf '%s\n' "$out" | grep -q "kept $cur (current)" || fail "self prune did not keep the current version: $out"
printf '%s\n' "$out" | grep -q "removed 0.0.0-smoke" || fail "self prune did not report the stale version: $out"
[ ! -e "$INSTALL_DIR/versions/0.0.0-smoke" ] || fail "self prune left the stale version behind"
[ -x "$INSTALL_DIR/versions/$cur/captainHook" ] || fail "self prune removed the current version"
pass "self prune: kept $cur (current), removed a stale version"

# 12. self uninstall: refused while settings.json still runs the shim, then
# done once install-hooks has taken the hooks out.
if out=$("$ENGINE" self uninstall </dev/null 2>&1); then fail "self uninstall ran while the hooks were wired: $out"; fi
printf '%s\n' "$out" | grep -q 'install-hooks --remove --apply' || fail "self uninstall refused without naming the fix: $out"
"$ENGINE" install-hooks --settings "$SETTINGS" --remove --apply >/dev/null
out=$("$ENGINE" self uninstall </dev/null 2>&1) || fail "self uninstall failed after the hooks were removed: $out"
if [ -e "$SHIM" ] || [ -e "$ENGINE" ] || [ -e "$INSTALL_DIR/versions" ]; then fail "self uninstall left the install behind: $out"; fi
grep -q captainShim "$SETTINGS" && fail "install-hooks --remove left a captainShim hook in $SETTINGS"
pass "self uninstall: refused while hooks were wired, then removed the links and versions"

echo "smoke: PASS  cold=${COLD_MS}ms warm=${WARM_MS}ms  $(uname -s)/$(uname -m)"
