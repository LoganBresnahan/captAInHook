#!/bin/sh
# deploy-guard — asks before an agent's shell command modifies captAInHook's
# installed files. Never denies. Fail-open.
#
# A ONESHOT payload on PreToolUse. When a tool call names an installed path
# AND a mutating verb, it answers Decide(ask), so you confirm before the
# command runs; every other call is a fast allow. The installed files are:
#   ~/.local/share/captainhook/versions/   the versioned install (ADR-0020)
#   ~/.local/bin/captainShim, captainHook, captainhook   its links
#   ~/.captainHook/bin/                    the pre-0.1.0 layout
#
# Shipped as an example: it does nothing until an entry in handlers.json
# names it, e.g.
#   {"name":"deploy-guard","command":"~/.local/share/captainhook/payloads/deploy-guard.sh",
#    "events":["PreToolUse"],"mode":"oneshot","failMode":"open"}
#
# Never denies: the strongest answer is "ask", so a wrong match costs one
# extra confirmation and can never block a session. Fail-open: register it
# with failMode "open", so a bug or a crash here means the hook contributes
# nothing, never that every tool call is refused.
#
# Matching is a raw-line heuristic, no JSON parser. EVERY tool call is
# inspected, whatever the host calls its shell tool: a filter on one host's
# name ("Bash") once read another host's ("run_shell") as not-a-shell and
# allowed it unexamined (ADR-0019). Known gap: the verb list is shell-shaped,
# so a file-editing tool writing into the install carries no verb and is
# allowed.

field() { printf '%s' "$1" | sed -n 's/.*"'"$2"'":"\([^"]*\)".*/\1/p'; }

IFS= read -r envelope
id=$(field "$envelope" dispatchId)

allow() { printf '{"effect":"decide","verdict":"allow","dispatchId":"%s"}\n' "$id"; exit 0; }

# The live tree not mentioned → allow.
case $envelope in
  *'.captainHook/bin'*|*'.local/share/captainhook/versions'*|*'.local/bin/captainShim'*|*'.local/bin/captainHook'*|*'.local/bin/captainhook'*) ;;
  *) allow ;;
esac

# Mentioned, but by a non-mutating command (ls, cat, doctor, the shim
# itself…) → allow. Word-bounded verbs so "rm" never matches "format".
printf '%s' "$envelope" | grep -qE '(^|[^A-Za-z0-9_-])(rm|mv|cp|chmod|chown|ln|tee|truncate|rsync|install|unlink|rmdir)([^A-Za-z0-9_-]|$)' \
  || allow

printf '{"effect":"decide","verdict":"ask","reason":"this command modifies captAInHook'"'"'s installed files — use `captainhook self update` to change the installed version","dispatchId":"%s"}\n' "$id"
