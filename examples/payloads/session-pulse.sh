#!/bin/sh
# session-pulse — a ONESHOT captAInHook payload (live dogfood, Background lane).
#
# Registered on Stop: fires at every turn end, appends ONE JSONL line — when,
# which session, where, at what commit — to a durable ledger beside the
# runtime home. Its VALUE is the side effect (the cross-session activity
# pulse); the answer is a plain noop, as Stop's effect contract demands.
#
# The wall-clock stamp is fine here: a human-read ledger, not control-flow
# timing. Tolerates everything — no cwd, not a git repo, git absent — the
# line still lands with what's known.

esc()   { printf '%s' "$1" | sed 's/\\/\\\\/g; s/"/\\"/g'; }
field() { printf '%s' "$1" | sed -n 's/.*"'"$2"'":"\([^"]*\)".*/\1/p'; }

IFS= read -r envelope
id=$(field "$envelope" dispatchId)
session=$(field "$envelope" sessionId)
cwd=$(field "$envelope" cwd)
[ -n "$session" ] || session="unknown"

head=""; dirty=0
if [ -n "$cwd" ] && [ -d "$cwd" ]; then
  head=$(git -C "$cwd" rev-parse --short HEAD 2>/dev/null) || head=""
  [ -n "$head" ] && dirty=$(git -C "$cwd" status --porcelain 2>/dev/null | wc -l | tr -d ' ')
fi

LOG="${HOME}/.captainHook/logs/session-pulse.jsonl"
mkdir -p "$(dirname "$LOG")"
printf '{"ts":"%s","session":"%s","cwd":"%s","head":"%s","dirty":%s}\n' \
  "$(date -u '+%Y-%m-%dT%H:%M:%SZ')" "$(esc "$session")" "$(esc "$cwd")" \
  "$(esc "$head")" "${dirty:-0}" >> "$LOG"

printf '{"effect":"noop","dispatchId":"%s"}\n' "$id"
