#!/bin/sh
# memory — a ONESHOT captAInHook payload (ADR-0010 demo).
#
# Registered on a session edge (Stop): the daemon (or a collapsed fallback)
# spawns this process ONCE per event, hands it a single envelope on stdin,
# reads one answer line, and lets it exit. Right for rare, edge events where a
# warm process would just idle — the spawn cost is paid once, not per tool
# call.
#
# Its VALUE is the side effect (appending to a memory log), not the effect it
# returns: it answers a plain {"effect":"noop"} — Stop permits no loop effects
# anyway — and the durable write is the point. A oneshot need not echo the
# dispatchId (there is no second conversation to attribute), but doing so is
# harmless and future-proof.
#
# Env is stripped to the allowlist; $HOME crosses, so the log lives beside the
# runtime home with zero configuration.
LOG="${HOME}/.captainHook/demo-memory.log"

field() { printf '%s' "$1" | sed -n 's/.*"'"$2"'":"\([^"]*\)".*/\1/p'; }

# One envelope, then EOF (oneshot: stdin closes after the single request).
IFS= read -r envelope

session=$(field "$envelope" sessionId)
[ -n "$session" ] || session="unknown"

mkdir -p "$(dirname "$LOG")"
# A wall-clock stamp is fine here — this is a human-read log, not control-flow
# timing (captAInHook's own timing is monotonic; a payload's is its own call).
printf '%s\tsession=%s\tstop\n' "$(date -u '+%Y-%m-%dT%H:%M:%SZ')" "$session" >> "$LOG"

printf '{"effect":"noop"}\n'
