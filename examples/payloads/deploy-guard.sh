#!/bin/sh
# deploy-guard — a ONESHOT captAInHook payload (live dogfood, Decide lane).
#
# Registered on PreToolUse: watches for shell commands that would MUTATE the
# live deployment (~/.captainHook/bin — the very tree the deployed hook runs
# from) and answers Decide(ask) so a human confirms; everything else is a
# fast allow. captAInHook enforcing its own house rule: only /deploy touches
# the live tree — and even /deploy's swap now pauses for a nod.
#
# Registered fail-OPEN deliberately for the first live phase: a bug here
# must degrade to "the hook contributes nothing", never to "every tool call
# in every session denied". Flip the entry to failMode:"closed" once the
# trail shows it behaving.
#
# Matching is a raw-line heuristic, no JSON parser: only the Bash tool, and
# only when a mutation verb AND the live path both appear in the envelope.
# A false "ask" is cheap (one extra confirmation); the verdict is never
# deny, so the guard cannot brick a session.

field() { printf '%s' "$1" | sed -n 's/.*"'"$2"'":"\([^"]*\)".*/\1/p'; }

IFS= read -r envelope
id=$(field "$envelope" dispatchId)

allow() { printf '{"effect":"decide","verdict":"allow","dispatchId":"%s"}\n' "$id"; exit 0; }

# Not a shell command → none of our business.
case $envelope in *'"tool_name":"Bash"'*) ;; *) allow ;; esac

# The live tree not mentioned → allow.
case $envelope in *'.captainHook/bin'*) ;; *) allow ;; esac

# Mentioned, but by a non-mutating command (ls, cat, doctor, the shim
# itself…) → allow. Word-bounded verbs so "rm" never matches "format".
printf '%s' "$envelope" | grep -qE '(^|[^A-Za-z0-9_-])(rm|mv|cp|chmod|chown|ln|tee|truncate|rsync|install|unlink|rmdir)([^A-Za-z0-9_-]|$)' \
  || allow

printf '{"effect":"decide","verdict":"ask","reason":"command touches the live deployment (~/.captainHook/bin) — /deploy is the sanctioned path","dispatchId":"%s"}\n' "$id"
