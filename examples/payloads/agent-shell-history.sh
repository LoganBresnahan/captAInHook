#!/bin/sh
# agent-shell-history — a ONESHOT captAInHook payload (live dogfood, provenance).
#
# Registered on PostToolUse: every shell command an agent RAN is appended to
# the maintainer's bash history as one line, tagged with who ran it:
#
#   git status  # agent:claude-code/56544d6f
#   cat <<'EOF' > x.txt ⏎ hello ⏎ EOF  # agent:claude-code/56544d6f
#
# so `hh agent` finds every agent command and `hh 56544d6f` one session's.
# Answers nothing on stdout (exit 0 + empty stdout = Noop, ADR-0010 d2): it
# records, it never steers. Register it fail-OPEN; a bug here must cost a
# missing history line, never a tool call.
#
# PostToolUse only, so a command that was denied or interrupted never lands;
# a command that ran and FAILED does (it ran). A shell call is recognised by
# CONTENT, `tool_input.command` being a string, never by the host's tool name
# (`Bash`, moby's `run_shell`…): the ADR-0019 lesson.
#
# FLATTENED, NOT RUNNABLE. Newlines become " ⏎ ", never ";". Joining a
# heredoc with ";" silently changes what it means, so the line is for lookup,
# not for re-running. Bash history is line-oriented and the maintainer's
# .bashrc dedups whole lines at startup, so a multi-line block written
# verbatim would be scattered and mangled.
#
# Interactions with ~/.bashrc, stated rather than fixed:
#   - Startup dedup (`gawk -i inplace '!seen[$0]++'`) keeps the FIRST copy of
#     each whole line. The session tag keeps two sessions' identical commands
#     distinct; the same command twice in ONE session collapses to its first.
#   - That dedup rewrites the file via temp + rename, so a line appended
#     during that window, at a new shell's startup, is lost. The same race
#     already exists between two of your own shells; it is not made worse.
#   - Appends use O_APPEND (`>>`), as `history -a` does, so a line lands whole
#     at the end unless one write is split. Very long commands can be.
#
# SECRETS: an agent that puts a token on a command line now leaves it in
# your history, exactly as you would by typing it. This makes agent
# commands as durable as your own; it does not scrub them.
#
# Needs `jq`: a command string carries JSON escapes (\n, \", \uXXXX) that a
# substring match cannot decode faithfully. Without jq it records nothing and
# says so on stderr (the trail's exec.stderr).
#
# AGENT_HISTORY_FILE overrides the target (tests; a handlers.json `env`).

target=${AGENT_HISTORY_FILE:-$HOME/.bash_eternal_history}

IFS= read -r envelope || [ -n "$envelope" ] || exit 0

if ! command -v jq >/dev/null 2>&1; then
  echo "agent-shell-history: jq not found on PATH; nothing recorded" >&2
  exit 0
fi

line=$(printf '%s\n' "$envelope" | jq -r '
  .event as $e
  | ($e.payload.tool_input.command // null) as $c
  | select($c | type == "string")
  | ($c | gsub("\\A\\s+|\\s+\\z"; "") | gsub("\r\n|\r|\n"; " ⏎ ")) as $flat
  | select($flat != "")
  | "\($flat)  # agent:\($e.harness // "unknown")/\(($e.sessionId // "nosession")[0:8])"
' 2>/dev/null) || {
  echo "agent-shell-history: envelope did not parse; nothing recorded" >&2
  exit 0
}

[ -n "$line" ] || exit 0
printf '%s\n' "$line" >> "$target"
