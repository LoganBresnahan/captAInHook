#!/bin/sh
# starter-mail-observer — a STARTER captAInHook payload: a WRITE-ONLY member of
# the mailbox bus (roadmap item 20 / ADR-0016 d5's cheapest membership class).
#
# A template, not a demo. This is the payload the HUB position makes possible:
# two agent loops, in different windows on different projects, that have no idea
# the other exists — and one daemon between them that turns "A just edited F"
# into something B is TOLD, at a seam B's harness actually has.
#
# The class matters. This member never reads the bus and never touches its own
# agent's loop: it observes one event, appends an envelope, and answers `noop`.
# That is why it needs no cursor, no role of its own to read, and no budget
# beyond a `mail send` — and why breaking it can never break the agent it
# watches. Reading is somebody else's registration (`captainHook mail digest`).
#
# ⚠ WHO IT WRITES TO. Mail is addressed to a ROLE, and a digest reads the role
# it was registered with — so an observer addressing its OWN role would mail its
# agent its own edits. $MAIL_PEER is therefore the OTHER member's role, set per
# registration: with two agents you install this twice, mirrored (alpha→beta,
# beta→alpha). That is one entry per peer, deliberately, rather than a shared
# "everybody" role the sender would have to be filtered back out of.
#
# STALE VIEW — the part that needs the hub. A plain "A edited F" notice is noise
# on every file B has never heard of. The signal is "A edited a file B IS
# HOLDING", and answering that needs both agents' facts in one place. This
# payload keeps the cheapest possible shared fact: each member appends the paths
# its agent READ to a per-role view file beside the runtime home, and an edit is
# escalated to `urgent` only when the peer's view names the same path. Urgent is
# the mid-turn seam class (ADR-0016 d5), so B hears about it on its next tool
# call rather than at the end of a turn spent working from a stale read.
#
# LATENCY: PostToolUse fires on EVERY tool call, so this is `resident` — the
# daemon holds one warm child and speaks the lock-step protocol to it (ADR-0010
# d3). A `oneshot` here would spawn an interpreter per tool call on the agent's
# critical path; the engine warns (`handlers.slowShape`) if you try.
#
# REGISTRATION (one per member; see examples/payloads/handlers.json):
#   {"name":"mail-observer-alpha","command":".../starter-mail-observer.sh",
#    "events":["PostToolUse"],"mode":"resident","failMode":"open",
#    "budgetMs":2000,
#    "env":{"MAIL_ROLE":"alpha","MAIL_PEER":"beta",
#           "CAPTAINHOOK_BIN":"/home/you/.captainHook/bin/captainHook"}}
#
# ⚠ AND THE HALF THAT IS NOT OBVIOUS: handlers.json is GLOBAL, so both agents
# on one machine run both entries and would report the same role. What separates
# them is DISPATCH POLICY, not registration — ADR-0016's "swarm activation is a
# dispatch-policy flip". Register both members, then scope each to its project
# in ~/.captainHook/dispatch.json, whose handler-named rules AND a project
# path-prefix:
#   {"version":1,"default":"allow","rules":[
#     {"handler":"mail-observer-alpha","project":"/home/you/beta-repo","decision":"deny"},
#     {"handler":"mail-observer-beta", "project":"/home/you/alpha-repo","decision":"deny"}]}
# An excluded handler is filtered BEFORE fan-out — never asked, never restarted —
# so the wrong-role member costs nothing in the window it does not belong to.

# The engine binary — this payload's ONE dependency, and it is the same engine
# running it. The child env is a stripped allowlist (ADR-0010 d5), so an
# absolute path in the entry's `env` is the unambiguous way to name it; the
# fallbacks cover a deploy-standard home and a bare name on PATH.
BIN="${CAPTAINHOOK_BIN:-${HOME}/.captainHook/bin/captainHook}"
[ -x "$BIN" ] || BIN=captainHook

VIEWS="${MAIL_VIEWS_DIR:-${HOME}/.captainHook/observer-views}"
VIEW_LINES=200          # per-role tail kept; a view is recent attention, not history

esc() { printf '%s' "$1" | sed 's/\\/\\\\/g; s/"/\\"/g'; }
field() { printf '%s' "$1" | sed -n 's/.*"'"$2"'":"\([^"]*\)".*/\1/p'; }

# A misregistration must be LOUD on stderr and INVISIBLE on the wire: this
# member's whole contract is that it cannot hurt the loop it watches.
if [ -z "$MAIL_ROLE" ] || [ -z "$MAIL_PEER" ]; then
  printf 'mail-observer: MAIL_ROLE and MAIL_PEER must both be set in the entry env — idling\n' >&2
fi

mkdir -p "$VIEWS" 2>/dev/null

# Resident handshake, then one answer per envelope with the dispatchId echoed
# (the daemon binds answer→dispatch across a warm stream).
printf '{"ready":1}\n'

while IFS= read -r envelope; do
  id=$(field "$envelope" dispatchId)
  noop='{"effect":"noop","dispatchId":"'"$id"'"}'

  if [ -z "$MAIL_ROLE" ] || [ -z "$MAIL_PEER" ]; then
    printf '%s\n' "$noop"; continue
  fi

  session=$(field "$envelope" sessionId)
  path=$(field "$envelope" file_path)
  [ -n "$path" ] || { printf '%s\n' "$noop"; continue; }

  # ---- what counts as a read, and what counts as an edit -------------------
  # EDIT THIS for your harness's tool vocabulary. Reads build the view that
  # makes a later edit interesting; writes are what the peer is told about.
  case $envelope in
    *'"tool_name":"Edit"'*|*'"tool_name":"Write"'*|*'"tool_name":"NotebookEdit"'*) act=edit ;;
    *'"tool_name":"Read"'*)                                                       act=read ;;
    *) printf '%s\n' "$noop"; continue ;;
  esac
  # --------------------------------------------------------------------------

  if [ "$act" = read ]; then
    # Record attention. Bounded: a view is what this agent is holding NOW.
    mine="${VIEWS}/${MAIL_ROLE}"
    printf '%s\n' "$path" >> "$mine" 2>/dev/null
    if [ "$(wc -l < "$mine" 2>/dev/null || echo 0)" -gt "$VIEW_LINES" ]; then
      tail -n "$VIEW_LINES" "$mine" > "${mine}.tmp" 2>/dev/null && mv "${mine}.tmp" "$mine"
    fi
    printf '%s\n' "$noop"; continue
  fi

  # An edit. Is the PEER holding this path? That question is the whole reason
  # a bus exists — neither agent can answer it alone.
  stale=no
  if [ -f "${VIEWS}/${MAIL_PEER}" ] && grep -qxF "$path" "${VIEWS}/${MAIL_PEER}" 2>/dev/null; then
    stale=yes
  fi

  # This turn's edits, for whatever else this member's agent runs — see
  # starter-mail-watcher.sh, which reads exactly this file to decide whether a
  # turn is worth waking a model over. Appended before the send so the record
  # exists even if the bus refuses the envelope.
  printf '%s\n' "$path" >> "${VIEWS}/${MAIL_ROLE}.edits" 2>/dev/null

  base=$(basename "$path")
  if [ "$stale" = yes ]; then
    kind=alert; priority=urgent; ttl=2
    topic="stale view: ${base}"
    body="${MAIL_ROLE} just edited ${path}, which you read earlier this session. Re-read it before you act on what you have."
  else
    kind=status; priority=ambient; ttl=3
    topic="edited ${base}"
    body="${MAIL_ROLE} edited ${path}."
  fi

  # `ts` is deliberately ABSENT: `mail send` stamps it (ADR-0016 d2 — every
  # writer goes through the verb, so one clock stamps the whole bus). The verb
  # is also the only validator; a refusal is loud on stderr and changes nothing.
  printf '{"v":1,"id":"edit-%s","from":{"agent":"%s","harness":"claude-code","session":"%s"},"to":"%s","kind":"%s","topic":"%s","priority":"%s","ttlDeliveries":%s,"body":"%s"}\n' \
    "$(esc "$id")" "$(esc "$MAIL_ROLE")" "$(esc "$session")" "$(esc "$MAIL_PEER")" \
    "$kind" "$(esc "$topic")" "$priority" "$ttl" "$(esc "$body")" \
    | "$BIN" mail send >/dev/null 2>&1 \
    || printf 'mail-observer: mail send refused the %s notice for %s\n' "$kind" "$path" >&2

  printf '%s\n' "$noop"
done
