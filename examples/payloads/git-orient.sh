#!/bin/sh
# git-orient — a ONESHOT captAInHook payload (the live-dogfood starter).
#
# Registered on UserPromptSubmit: ONCE per session it injects a one-line git
# bearing for the prompt's cwd — branch @ short-sha, dirty count — an
# automatic micro-/orient. Every later prompt in the same session answers
# noop through a marker file, so the cost is one spawn + three cheap git
# calls per SESSION, not per prompt.
#
# Session dedup: a marker named for the sessionId, under $XDG_RUNTIME_DIR
# when available (tmpfs — clears itself on logout/reboot) else beside the
# runtime home. XDG_RUNTIME_DIR is NOT on the fixed env allowlist
# (ADR-0010 d5), so the registration passEnvs it — see the entry in
# ~/.captainHook/handlers.json.
#
# Fail-open, single line in, single line out, dispatchId echoed (optional
# for oneshot, harmless and future-proof).

esc()   { printf '%s' "$1" | sed 's/\\/\\\\/g; s/"/\\"/g'; }
field() { printf '%s' "$1" | sed -n 's/.*"'"$2"'":"\([^"]*\)".*/\1/p'; }

IFS= read -r envelope
id=$(field "$envelope" dispatchId)

noop() { printf '{"effect":"noop","dispatchId":"%s"}\n' "$id"; exit 0; }

session=$(field "$envelope" sessionId)
cwd=$(field "$envelope" cwd)
[ -n "$session" ] || noop
[ -n "$cwd" ] && [ -d "$cwd" ] || noop

markdir="${XDG_RUNTIME_DIR:-$HOME/.captainHook}/captainHook/orient"
mark="$markdir/$session"
[ -f "$mark" ] && noop
# Mark BEFORE the git calls: one attempt per session even if cwd isn't a
# repo — this payload must never become a per-prompt tax.
mkdir -p "$markdir" 2>/dev/null && : > "$mark"

branch=$(git -C "$cwd" rev-parse --abbrev-ref HEAD 2>/dev/null) || noop
sha=$(git -C "$cwd" rev-parse --short HEAD 2>/dev/null)
dirty=$(git -C "$cwd" status --porcelain 2>/dev/null | wc -l | tr -d ' ')

printf '{"effect":"inject","text":"git-orient: %s @ %s, %s dirty","dispatchId":"%s"}\n' \
  "$(esc "$branch")" "$(esc "$sha")" "$dirty" "$id"
