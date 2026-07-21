#!/bin/sh
# doc-pointer — a RESIDENT captAInHook payload (live dogfood, phase 3).
#
# Registered on PreToolUse: the daemon holds this warm. At startup it builds
# an index of the repo's flow-doc ground truth — every backticked
# dotnet/… or web/… path in doc/flow/*.md, mapped to the doc that cites it —
# THEN announces {"ready":1} (index-before-ready is the demonstrable-
# readiness lesson: the daemon never dispatches to a child still warming).
#
# Per dispatch: when an Edit/Write is about to touch an indexed file, inject
# a one-line reminder that a flow doc depicts that file — the docs-must-
# match-code discipline, enforced by the machinery it documents. Announced
# once per (session, file): the dedup lives in process memory, which is
# exactly what resident mode buys — state that survives across dispatches
# and dies with the child (fresh-state doctrine: a restart rebuilds both
# index and dedup from scratch).
#
# CAPT_REPO arrives via the entry's env{} config (ADR-0010 d5's explicit-env
# lane) — the stripped allowlist doesn't know where the repo lives.

field() { printf '%s' "$1" | sed -n 's/.*"'"$2"'":"\([^"]*\)".*/\1/p'; }
esc()   { printf '%s' "$1" | sed 's/\\/\\\\/g; s/"/\\"/g'; }

REPO="${CAPT_REPO:-$HOME/captAInHook}"

# Index: "relpath|docname" lines. Built once, held in memory.
index=$(for f in "$REPO"/doc/flow/*.md; do
  [ -f "$f" ] || continue
  d=${f##*/}
  grep -o '`[^`]*`' "$f" 2>/dev/null | tr -d '`' \
    | grep -E '^(dotnet|web)/[^ ]+\.[A-Za-z]+$' \
    | while IFS= read -r p; do printf '%s|%s\n' "$p" "$d"; done
done | sort -u)

seen=""

printf '{"ready":1}\n'

while IFS= read -r envelope; do
  id=$(field "$envelope" dispatchId)

  # Only writes matter; reads don't drift docs.
  case $envelope in
    *'"tool_name":"Edit"'*|*'"tool_name":"Write"'*|*'"tool_name":"NotebookEdit"'*) ;;
    *) printf '{"effect":"noop","dispatchId":"%s"}\n' "$id"; continue ;;
  esac

  path=$(field "$envelope" file_path)
  rel=${path#"$REPO"/}
  doc=""
  [ -n "$rel" ] && [ "$rel" != "$path" ] && \
    doc=$(printf '%s\n' "$index" | awk -F'|' -v p="$rel" '$1==p{print $2; exit}')

  if [ -n "$doc" ]; then
    session=$(field "$envelope" sessionId)
    key="|$session:$rel|"
    case $seen in
      *"$key"*) printf '{"effect":"noop","dispatchId":"%s"}\n' "$id" ;;
      *)
        seen="$seen$key"
        printf '{"effect":"inject","text":"doc-pointer: %s is ground truth for doc/flow/%s — keep them in sync","dispatchId":"%s"}\n' \
          "$(esc "$rel")" "$(esc "$doc")" "$id"
        ;;
    esac
  else
    printf '{"effect":"noop","dispatchId":"%s"}\n' "$id"
  fi
done
