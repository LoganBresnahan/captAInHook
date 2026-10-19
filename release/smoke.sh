#!/bin/sh
# release/smoke.sh — exercise an INSTALLED captAInHook for real (ADR-0020 d7).
#
#   release/smoke.sh
#
# Run after install.sh, in a THROWAWAY HOME (CI: a fresh runner; locally: a
# sandbox) — it writes ~/.claude/settings.json and ~/.captainHook/handlers.json,
# so it refuses a home that already has either. Reads the same env install.sh does (CAPTAINHOOK_INSTALL_DIR,
# CAPTAINHOOK_BIN_DIR) and needs XDG_RUNTIME_DIR either unset (→ ~/.captainHook)
# or short — Unix socket paths cap at ~104 bytes.
#
# Proves, in order: the links resolve to the real versioned folder; install-hooks
# writes a settings file (a temp one under this HOME); a cold hook spawns the
# daemon; a warm hook is answered by it; a shipped payload runs through bosun
# from its payloads/ link; zero wire skew; doctor sees a healthy daemon; and the
# daemon idle-exits on its own. Prints the cold-hook latency. Exit 0 = pass.
set -eu

INSTALL_DIR=${CAPTAINHOOK_INSTALL_DIR:-${XDG_DATA_HOME:-$HOME/.local/share}/captainhook}
BIN_DIR=${CAPTAINHOOK_BIN_DIR:-$HOME/.local/bin}
SHIM=$BIN_DIR/captainShim
ENGINE=$BIN_DIR/captainHook
TRAIL=$HOME/.captainHook/logs/captainHook.jsonl
RUNDIR=${XDG_RUNTIME_DIR:+$XDG_RUNTIME_DIR/captainHook}
RUNDIR=${RUNDIR:-$HOME/.captainHook}
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
[ ! -e "$RUNDIR" ] || [ -z "$(ls "$RUNDIR"/captaind-*.pid 2>/dev/null)" ] || fail "a daemon is already running in $RUNDIR — use a fresh HOME"
export CAPTAINHOOK_IDLE_MS="$IDLE_MS"

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

has_pid() { ls "$RUNDIR"/captaind-*.pid >/dev/null 2>&1; }
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
pass "doctor: $(printf '%s\n' "$doc" | head -1)"

# 8. idle exit: no traffic for CAPTAINHOOK_IDLE_MS ⇒ the daemon leaves by itself
gone() { ! has_pid && [ "$(count daemon.idleExit)" -ge 1 ]; }
wait_for $(( IDLE_MS / 1000 + 30 )) gone || fail "the daemon did not idle-exit within $((IDLE_MS / 1000 + 30))s"
pass "daemon idle-exited after ${IDLE_MS}ms without traffic"

echo "smoke: PASS  cold=${COLD_MS}ms warm=${WARM_MS}ms  $(uname -s)/$(uname -m)"
