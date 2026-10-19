#!/bin/sh
# deploy-guard — a ONESHOT captAInHook payload (live dogfood, Decide lane).
#
# Registered on PreToolUse: watches for shell commands that would MUTATE the
# live deployment — the very tree the installed hook runs from: the versioned
# install (~/.local/share/captainhook/versions, its ~/.local/bin/captainShim
# and captainHook links — ADR-0020) or the legacy ~/.captainHook/bin — and
# answers Decide(ask) so a human confirms; everything else is a fast allow. captAInHook enforcing its own house rule: only /deploy touches
# the live tree — and even /deploy's swap now pauses for a nod.
#
# Registered fail-OPEN deliberately for the first live phase: a bug here
# must degrade to "the hook contributes nothing", never to "every tool call
# in every session denied". Flip the entry to failMode:"closed" once the
# trail shows it behaving.
#
# Matching is a raw-line heuristic, no JSON parser: EVERY tool call is
# inspected, and it asks when a mutation verb AND the live path both appear
# in the envelope. There is deliberately no tool-name filter: a gate keyed on
# one host's name for its shell ("Bash") read another host's ("run_shell") as
# not-my-business and answered allow without looking (ADR-0019). Known gap,
# unchanged by that: the verb list is shell-shaped, so a file-editing tool
# writing into the live tree carries no verb and is allowed. A false "ask" is
# cheap (one extra confirmation); the verdict is never deny, so the guard
# cannot brick a session.

field() { printf '%s' "$1" | sed -n 's/.*"'"$2"'":"\([^"]*\)".*/\1/p'; }

IFS= read -r envelope
id=$(field "$envelope" dispatchId)

allow() { printf '{"effect":"decide","verdict":"allow","dispatchId":"%s"}\n' "$id"; exit 0; }

# The live tree not mentioned → allow.
case $envelope in
  *'.captainHook/bin'*|*'.local/share/captainhook/versions'*|*'.local/bin/captainShim'*|*'.local/bin/captainHook'*) ;;
  *) allow ;;
esac

# Mentioned, but by a non-mutating command (ls, cat, doctor, the shim
# itself…) → allow. Word-bounded verbs so "rm" never matches "format".
printf '%s' "$envelope" | grep -qE '(^|[^A-Za-z0-9_-])(rm|mv|cp|chmod|chown|ln|tee|truncate|rsync|install|unlink|rmdir)([^A-Za-z0-9_-]|$)' \
  || allow

printf '{"effect":"decide","verdict":"ask","reason":"command touches the live captAInHook install — /deploy (install.sh) is the sanctioned path","dispatchId":"%s"}\n' "$id"
