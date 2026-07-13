#!/bin/sh
# retriever — a RESIDENT captAInHook payload (ADR-0010 demo).
#
# Registered on a before-tools event (PreToolUse): the daemon holds this
# process warm and speaks lock-step JSONL to it — one envelope line in on
# stdin, one answer line out on stdout. Here it "retrieves" notes relevant to
# the imminent tool call and INJECTS them as context.
#
# The whole resident contract in ~20 lines of POSIX sh, no dependencies:
#   1. announce readiness with exactly {"ready":1} (the daemon won't dispatch
#      until it sees this — pharos ADR-024's demonstrable-readiness lesson);
#   2. loop: read one envelope, echo its dispatchId back on the answer (the
#      MANDATORY attribution the daemon uses to bind answer→dispatch);
#   3. flush every line (sh's echo is unbuffered, but a buffered runtime MUST
#      flush — a held answer looks like a wedged child).
#
# The environment is STRIPPED to an allowlist (ADR-0010 d5): $HOME is present,
# ambient secrets are not. Notes live next to the runtime home so the demo
# needs no config.
NOTES="${HOME}/.captainHook/demo-notes.txt"

# JSON string-escape: backslash and double-quote only (enough for note text).
esc() { printf '%s' "$1" | sed 's/\\/\\\\/g; s/"/\\"/g'; }

# Pull one string field out of the compact single-line envelope without a JSON
# parser: everything after "field":" up to the next unescaped quote. Fine for
# the demo; a real payload would use its language's JSON library.
field() { printf '%s' "$1" | sed -n 's/.*"'"$2"'":"\([^"]*\)".*/\1/p'; }

printf '{"ready":1}\n'

while IFS= read -r envelope; do
  id=$(field "$envelope" dispatchId)

  # "Retrieval": the first note line mentioning any word already on the line.
  hit=""
  if [ -f "$NOTES" ]; then
    hit=$(grep -m1 -i -f /dev/stdin "$NOTES" 2>/dev/null <<EOF
$(printf '%s\n' "$envelope" | tr -c 'A-Za-z0-9' '\n' | awk 'length>3')
EOF
    )
  fi

  if [ -n "$hit" ]; then
    printf '{"effect":"inject","text":"retrieved: %s","dispatchId":"%s"}\n' "$(esc "$hit")" "$id"
  else
    # Nothing relevant — a clean noop is a first-class answer.
    printf '{"effect":"noop","dispatchId":"%s"}\n' "$id"
  fi
done
