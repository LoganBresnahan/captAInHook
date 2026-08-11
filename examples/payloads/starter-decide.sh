#!/bin/sh
# starter-decide — a STARTER captAInHook payload: the `decide` verb.
#
# A template, not a demo: it is deliberately the smallest useful shape of "a
# gate that can say no", meant to be copied and edited. The GUI's template
# gallery (ADR-0015 d3) ships this file's text verbatim.
#
# Registered on a before-tools event (PreToolUse), a `decide` payload is the
# only kind that can STOP the agent: it answers
#   {"effect":"decide","verdict":"allow|deny|ask","reason":"…"}
# and a `deny` blocks the tool call, with the reason shown to the agent.
#
# Which events accept `decide` is harness DATA, not a guess — the GUI shows the
# allowed verbs per event beside each checkbox. On claude-code today, only
# PreToolUse does.
#
# ⚠ FAIL MODE. Register a gate with "failMode":"closed" only if you mean it:
# closed means a crash or a timeout in THIS script denies the tool call. Open
# (the default) means a broken gate lets everything through. Both are defensible
# — choose deliberately, because the accident is silent either way.
#
# Env is stripped to an allowlist; $HOME crosses.

# One envelope, then EOF (oneshot: stdin closes after the single request).
IFS= read -r envelope

# Pull one field out of the compact single-line envelope without a JSON parser.
# Good enough for a starter; use `jq` (or any real parser) once the rule needs
# nesting — the payload may be any language, this one is just dependency-free.
field() { printf '%s' "$1" | sed -n 's/.*"'"$2"'":"\([^"]*\)".*/\1/p'; }

# JSON string-escape for the reason we hand back (backslash + quote only).
esc() { printf '%s' "$1" | sed 's/\\/\\\\/g; s/"/\\"/g'; }

tool=$(field "$envelope" tool_name)
[ -n "$tool" ] || tool=$(field "$envelope" toolName)

# ---- the rule -------------------------------------------------------------
# EDIT THIS. Everything above is contract; this is your policy. The example:
# refuse a destructive shell command, allow everything else.
verdict="allow"
reason=""
case "$envelope" in
  *"rm -rf /"*|*"mkfs"*|*"dd if="*)
    verdict="deny"
    reason="starter-decide: refused a destructive command pattern"
    ;;
esac
# ---------------------------------------------------------------------------

# Anything the payload wants to say to a human goes to STDERR — it lands in the
# trail and is never parsed as protocol. Exactly one JSON line goes to stdout.
[ "$verdict" = "deny" ] && printf 'starter-decide: denying %s\n' "${tool:-a tool call}" >&2

if [ -n "$reason" ]; then
  printf '{"effect":"decide","verdict":"%s","reason":"%s"}\n' "$verdict" "$(esc "$reason")"
else
  printf '{"effect":"decide","verdict":"%s"}\n' "$verdict"
fi
