#!/bin/sh
# starter-llm — a STARTER captAInHook payload: an LLM-backed subsystem.
#
# A template, not a demo. This is the shape DESIGN.md's thesis points at: the
# hook seam lets you splice a *model* into the agent's loop, not just a script.
# The payload asks a second, cheap, non-interactive model a narrow question and
# injects its answer.
#
# ⚠ REENTRANCY — the rule that makes or breaks this shape (ADR-0010 N7).
# A payload MUST NOT transitively fire its own event. Spawning `claude` from a
# UserPromptSubmit handler means that child's own UserPromptSubmit hook fires,
# which spawns another payload, which… The engine CANNOT detect this for you:
# the child mints its own dispatchId and the stripped environment carries no
# depth marker across the socket. The guard is `--setting-sources ""` below —
# it starts the child with NO hook configuration at all. Keep it, or run a
# model that has no hooks of its own.
#
# BUDGET: the hook blocks on this. A model call is 100s of ms at best, seconds
# at worst, and the entry's budgetMs cancels it — so pick an event where that
# is affordable (a session edge, or a prompt submit) and set budgetMs
# deliberately. If the model is slow or absent, answering `noop` is CORRECT:
# a degraded turn beats a stalled agent (fail-open is the default for a reason).
#
# Env is stripped to an allowlist. An API-key-based client needs its key passed
# explicitly via the entry's `passEnv` — that is the deliberate, auditable seam
# (nothing ambient leaks in). This starter shells out to the `claude` CLI, which
# uses its own stored credentials, so it needs no key at all.

MODEL_CMD="claude"
TIMEOUT_S=20

IFS= read -r envelope

field() { printf '%s' "$1" | sed -n 's/.*"'"$2"'":"\([^"]*\)".*/\1/p'; }
esc() {
  printf '%s' "$1" | sed 's/\\/\\\\/g; s/"/\\"/g' | awk 'BEGIN{ORS=""} NR>1{print "\\n"} {print}'
}
noop() { printf '{"effect":"noop"}\n'; exit 0; }

# Absent model ⇒ degrade, loudly on stderr, quietly on the wire. A template
# someone installs before installing the CLI must not break their agent.
command -v "$MODEL_CMD" >/dev/null 2>&1 || {
  printf 'starter-llm: %s not on PATH — degrading to noop\n' "$MODEL_CMD" >&2
  noop
}

# ---- the question ---------------------------------------------------------
# EDIT THIS. Keep it NARROW: a model asked "is there anything to flag here?"
# will always find something, and you are paying for the answer on every hook.
# The example asks for a single line, or nothing at all.
prompt="You are a terse assistant inside a hook. Given this agent event, reply
with ONE short line of context that would help, or reply with exactly SKIP if
nothing useful applies. Do not explain.

Event: $(printf '%s' "$envelope" | cut -c1-2000)"
# ---------------------------------------------------------------------------

# --setting-sources "" is the REENTRANCY GUARD (see above). -p is
# non-interactive: one prompt in, one answer out, no session.
answer=$(printf '%s' "$prompt" \
  | timeout "$TIMEOUT_S" "$MODEL_CMD" -p --setting-sources "" 2>/dev/null) || noop

# Trim, and treat the model's own "nothing to add" as nothing to add.
answer=$(printf '%s' "$answer" | sed 's/^[[:space:]]*//; s/[[:space:]]*$//')
[ -n "$answer" ] || noop
case "$answer" in SKIP|skip|SKIP.*) noop ;; esac

printf '{"effect":"inject","text":"%s"}\n' "$(esc "$answer")"
