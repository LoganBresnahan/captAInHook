#!/bin/sh
# orient-brief — an LLM-BACKED oneshot captAInHook payload (dogfood phase 4).
#
# The other half of the thesis: the invocation is deterministic, the payload
# is an arbitrarily intelligent subsystem. On SessionStart it compiles repo
# activity — git log, the roadmap's Now section, the session-pulse ledger
# (a payload feeding on another payload's output) — asks a small model for a
# three-line "since last time" brief, and injects it. Real multi-second
# work under a real budget: overrun ⇒ the engine TERMs the group and the
# session starts without the brief (fail-open); git-orient's instant
# one-liner is still there either way — layered degradation.
#
# Recursion is the hazard: our own `claude -p` subprocess starts a session,
# which fires SessionStart, which dispatches back into THIS handler's
# serialized worker — the inner ask queues behind the outer dispatch that is
# waiting on it, a self-block only budget timeouts unwind (found live,
# 2026-07-21 field report). Primary guard: the inner session runs with
# --setting-sources "" so it loads NO settings and fires NO hooks (also ~4x
# faster). Backstop: a lock file noops an inner dispatch that somehow still
# arrives — filesystem-based because the daemon strips env and no sentinel
# survives the socket boundary. Plus a 30min cache: repeat session starts
# inject instantly, the LLM is consulted at most twice an hour.
#
# CLAUDE_BIN and CAPT_REPO arrive via the entry's env{} config.

esc_ml() { printf '%s' "$1" | sed 's/\\/\\\\/g; s/"/\\"/g' | awk 'NR>1{printf "\\n"} {printf "%s", $0}'; }
field()  { printf '%s' "$1" | sed -n 's/.*"'"$2"'":"\([^"]*\)".*/\1/p'; }

IFS= read -r envelope
id=$(field "$envelope" dispatchId)

noop() { printf '{"effect":"noop","dispatchId":"%s"}\n' "$id"; exit 0; }

[ -n "$CLAUDE_BIN" ] && [ -x "$CLAUDE_BIN" ] || noop
REPO="${CAPT_REPO:-$HOME/captAInHook}"

DIR="${XDG_RUNTIME_DIR:-$HOME/.captainHook}/captainHook/orient-brief"
mkdir -p "$DIR" 2>/dev/null || noop
BRIEF="$DIR/brief.txt"
LOCK="$DIR/lock"

# Inside our own LLM call's inner session? (lock fresher than 2min) → noop.
[ -n "$(find "$DIR" -name lock -mmin -2 2>/dev/null)" ] && noop

# Cache fresh? Inject it without spending a model call.
if [ -f "$BRIEF" ] && [ -n "$(find "$DIR" -name brief.txt -mmin -30 2>/dev/null)" ]; then
  printf '{"effect":"inject","text":"orient-brief (cached):\\n%s","dispatchId":"%s"}\n' \
    "$(esc_ml "$(cat "$BRIEF")")" "$id"
  exit 0
fi

input="RECENT COMMITS:
$(git -C "$REPO" log --oneline -6 2>/dev/null)

ROADMAP NOW:
$(sed -n '/^## Now/,/^## /p' "$REPO/doc/roadmap.md" 2>/dev/null | head -12)

RECENT SESSION LEDGER (ts, session, cwd, head, dirty):
$(tail -5 "$HOME/.captainHook/logs/session-pulse.jsonl" 2>/dev/null)"

: > "$LOCK"
trap 'rm -f "$LOCK"' EXIT TERM INT
out=$(printf '%s' "$input" | "$CLAUDE_BIN" -p \
  "From this repo activity, write a 'since last time' brief for the developer opening a new session: at most 3 short lines — current focus, most recent movement, likely next step. Plain text only, no markdown, no preamble." \
  --model haiku --setting-sources "" 2>/dev/null)
rm -f "$LOCK"

[ -n "$out" ] || noop
printf '%s' "$out" > "$BRIEF"

printf '{"effect":"inject","text":"orient-brief:\\n%s","dispatchId":"%s"}\n' \
  "$(esc_ml "$out")" "$id"
