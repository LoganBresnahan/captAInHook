#!/bin/sh
# starter-inject — a STARTER captAInHook payload: the `inject` verb.
#
# A template, not a demo: the smallest useful shape of "put something in front
# of the model before it answers". Copy it and change the middle.
#
# Registered on UserPromptSubmit (or SessionStart), an `inject` payload answers
#   {"effect":"inject","text":"…"}
# and that text is added to the agent's context for this turn. It is the verb
# behind every "always remember X" / "here is the current state of Y" hook.
#
# COST DISCIPLINE: this text is spent on every dispatch, forever. Inject facts
# the model cannot derive (live state, machine-specific paths, a policy it must
# honour) — not restatements of what is already in the repo. A payload that
# injects 2KB of boilerplate per prompt is a tax with no payer.
#
# Env is stripped to an allowlist; $HOME crosses, so the notes file needs no
# configuration.
NOTES="${HOME}/.captainHook/context.md"

# One envelope, then EOF (oneshot).
IFS= read -r envelope

# Read a field out of the compact single-line envelope without a JSON parser.
field() { printf '%s' "$1" | sed -n 's/.*"'"$2"'":"\([^"]*\)".*/\1/p'; }

# JSON string-escape: backslash, quote, and newline (inject text is often
# multi-line — an unescaped newline would break the single-line answer).
esc() {
  printf '%s' "$1" | sed 's/\\/\\\\/g; s/"/\\"/g' | awk 'BEGIN{ORS=""} NR>1{print "\\n"} {print}'
}

# ---- what to inject -------------------------------------------------------
# EDIT THIS. The example: the contents of a notes file, plus one live fact the
# model cannot know. Keep it short and keep it CURRENT — that is the whole
# reason to compute it per dispatch instead of writing it in a prompt.
text=""
[ -r "$NOTES" ] && text=$(cat "$NOTES")

# The payload's OWN working directory is not the session's — the entry may pin
# a `cwd`, and a daemon-spawned child inherits the daemon's. The session's
# directory travels in the ENVELOPE, so read it from there and ask git about
# that repo. (Getting this wrong is silent: git answers about the wrong
# directory, or nothing, and the inject just goes quiet.)
branch=""
session_cwd=$(field "$envelope" cwd)
if [ -n "$session_cwd" ] && command -v git >/dev/null 2>&1; then
  branch=$(git -C "$session_cwd" rev-parse --abbrev-ref HEAD 2>/dev/null)
fi
[ -n "$branch" ] && text="${text:+$text
}current git branch: $branch"
# ---------------------------------------------------------------------------

# Nothing to say is a first-class answer: inject an empty string and you have
# spent context on nothing. Say noop instead.
if [ -z "$text" ]; then
  printf '{"effect":"noop"}\n'
else
  printf '{"effect":"inject","text":"%s"}\n' "$(esc "$text")"
fi
