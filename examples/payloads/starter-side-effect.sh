#!/bin/sh
# starter-side-effect — a STARTER captAInHook payload: do work, change nothing.
#
# A template, not a demo: the shape for hooks whose VALUE is the side effect —
# logging, metrics, a notification, kicking off a build — and which must not
# touch the agent's loop at all.
#
# ⚠ The `background` effect is NOT available to exec payloads. `Effect.Background`
# is an in-process handler's way of telling the ENGINE to run work off the
# critical path; the exec answer grammar is `inject` / `decide` / `replace` /
# `noop` only (ADR-0010). An exec payload's equivalent is exactly this file: do
# the work, then answer {"effect":"noop"}.
#
# Which is also why the event choice matters. Stop and SessionEnd permit NO loop
# effects at all (the GUI shows this per event) — they exist for precisely this
# kind of hook, and a side-effect payload belongs there rather than on a
# per-tool-call event where its spawn cost is charged to every action.
#
# LATENCY: the hook BLOCKS until this exits. Keep it fast, or detach the slow
# part yourself and answer immediately — the commented line below shows how.
LOG="${HOME}/.captainHook/demo-side-effect.log"

# One envelope, then EOF (oneshot).
IFS= read -r envelope

field() { printf '%s' "$1" | sed -n 's/.*"'"$2"'":"\([^"]*\)".*/\1/p'; }
session=$(field "$envelope" sessionId)
event=$(field "$envelope" type)

# ---- the side effect ------------------------------------------------------
# EDIT THIS. The example appends one line. A wall clock is fine for a
# human-read log; captAInHook's own control-flow timing is monotonic, but a
# payload's stamp is its own business.
mkdir -p "$(dirname "$LOG")"
printf '%s\tevent=%s\tsession=%s\n' \
  "$(date -u '+%Y-%m-%dT%H:%M:%SZ')" "${event:-?}" "${session:-unknown}" >> "$LOG"

# Slow work? Detach it so the hook is not waiting on you. The daemon kills the
# payload's whole PROCESS GROUP when it reaps (bosun gives it one — ADR-0014),
# so a detached child dies with its parent unless you daemonize deliberately:
#   ( sleep 30; do_slow_thing ) >/dev/null 2>&1 &
# ---------------------------------------------------------------------------

# Diagnostics go to stderr (captured to the trail); stdout carries exactly one
# JSON line and nothing else.
printf 'starter-side-effect: logged %s\n' "${event:-event}" >&2

printf '{"effect":"noop"}\n'
