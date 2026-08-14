#!/bin/sh
# starter-mail-watcher — a STARTER captAInHook payload: an ON-DEMAND LLM member
# of the mailbox bus (roadmap item 20 / ADR-0016 d5's third membership class).
#
# A template, not a demo. The bus never sees LLM-ness — it is a payload detail
# (ADR-0016's framing), so this member is a peer like any other: it writes an
# envelope with `mail send`, and the recipient's digest reads it at a seam. What
# makes it different is only that it asks a MODEL what to say.
#
# ⚠ ON-DEMAND is the whole discipline. A model in a hook is 100s of ms at best,
# and a member that wakes one every turn taxes every turn — so a DETERMINISTIC
# gate decides whether the question is even worth asking, and the model runs
# only past it. Here the gate is overlap: did this turn edit anything the peer
# is holding a read of? If not, this exits without spawning anything. Cheap when
# there is nothing to say is what makes it affordable when there is.
#
# ⚠ REENTRANCY — the rule that makes or breaks any model-backed payload
# (ADR-0010 N7). A payload MUST NOT transitively fire its own event. Spawning
# `claude` from a Stop handler means that child's own Stop hook fires, which
# spawns another payload, which… The engine CANNOT detect this: the child mints
# its own dispatchId and the stripped env carries no depth marker across the
# socket. The guard is `--setting-sources ""` below — it starts the child with
# NO hook configuration at all. Keep it, or run a model that has no hooks.
# (The suite proves this one is present: a stub `claude` that EXITS NONZERO when
# the flag is missing, so the guard is proven passed, not merely present.)
#
# PAIRING: reads the view/edit files starter-mail-observer.sh writes — two
# members of one bus sharing a convention. Install the observer first, or point
# EDITS/VIEWS at whatever your own members write.
#
# LATENCY: registered on Stop — a turn edge, where a model call is affordable
# and where NO loop effect is permitted anyway (this member answers `noop`; its
# value is the envelope it puts on the bus, which the peer reads at its own
# seam). budgetMs must exceed the model timeout or the engine cancels it first.
#
# REGISTRATION (see examples/payloads/handlers.json):
#   {"name":"mail-watcher-alpha","command":".../starter-mail-watcher.sh",
#    "events":["Stop"],"mode":"oneshot","failMode":"open","budgetMs":25000,
#    "env":{"MAIL_ROLE":"alpha","MAIL_PEER":"beta",
#           "CAPTAINHOOK_BIN":"/home/you/.captainHook/bin/captainHook"}}
#
# and, as with the observer, a dispatch-policy rule scoping this entry to its
# own project — handlers.json is global, policy is what makes a role an AGENT's
# role (see starter-mail-observer.sh's header for the rule shape).

MODEL_CMD="claude"
TIMEOUT_S=20

BIN="${CAPTAINHOOK_BIN:-${HOME}/.captainHook/bin/captainHook}"
[ -x "$BIN" ] || BIN=captainHook

VIEWS="${MAIL_VIEWS_DIR:-${HOME}/.captainHook/observer-views}"

esc() {
  printf '%s' "$1" | sed 's/\\/\\\\/g; s/"/\\"/g' | awk 'BEGIN{ORS=""} NR>1{print "\\n"} {print}'
}
field() { printf '%s' "$1" | sed -n 's/.*"'"$2"'":"\([^"]*\)".*/\1/p'; }
noop() { printf '{"effect":"noop"}\n'; exit 0; }

IFS= read -r envelope
session=$(field "$envelope" sessionId)

[ -n "$MAIL_ROLE" ] && [ -n "$MAIL_PEER" ] || {
  printf 'mail-watcher: MAIL_ROLE and MAIL_PEER must both be set in the entry env\n' >&2
  noop
}

EDITS="${VIEWS}/${MAIL_ROLE}.edits"
PEER_VIEW="${VIEWS}/${MAIL_PEER}"

# ---- the gate -------------------------------------------------------------
# EDIT THIS. Everything past here costs a model call, so the gate should be
# specific enough that passing it means something. This one: the intersection
# of what I edited and what the peer has read — the question no single agent
# can answer, which is the whole reason to be on a bus.
[ -s "$EDITS" ] && [ -s "$PEER_VIEW" ] || noop
overlap=$(sort -u "$EDITS" 2>/dev/null | grep -xF -f "$PEER_VIEW" 2>/dev/null | head -n 10)
[ -n "$overlap" ] || noop
# ---------------------------------------------------------------------------

# Absent model ⇒ degrade, loudly on stderr and quietly on the wire. But the
# overlap is real whether or not a model is available to describe it, so the
# degrade path still puts a plain envelope on the bus: losing the WARNING
# because the prose was unavailable would be the wrong direction to fail in.
send_mail() {
  # $1 = priority, $2 = topic, $3 = body
  printf '{"v":1,"id":"watch-%s-%s","from":{"agent":"%s","harness":"claude-code","session":"%s"},"to":"%s","kind":"alert","topic":"%s","priority":"%s","ttlDeliveries":2,"body":"%s"}\n' \
    "$(esc "$MAIL_ROLE")" "$$" "$(esc "$MAIL_ROLE")" "$(esc "$session")" "$(esc "$MAIL_PEER")" \
    "$(esc "$2")" "$1" "$(esc "$3")" \
    | "$BIN" mail send >/dev/null 2>&1 \
    || printf 'mail-watcher: mail send refused the handoff envelope\n' >&2
}

plain="I changed files you have read this session:
${overlap}"

command -v "$MODEL_CMD" >/dev/null 2>&1 || {
  printf 'mail-watcher: %s not on PATH — sending the ungarnished handoff\n' "$MODEL_CMD" >&2
  send_mail urgent "handoff from ${MAIL_ROLE}" "$plain"
  noop
}

# ---- the question ---------------------------------------------------------
# EDIT THIS. Keep it NARROW and keep it about the OTHER agent: this text is
# read by a peer model mid-turn, not by a human reviewing a diff.
prompt="You are writing one short note from one AI coding agent to another that
is working in the same repository right now. I just finished a turn in which I
edited these files, which the other agent has already read:

${overlap}

Write at most two sentences telling them what to re-check before they act on
their stale reads. Be concrete and terse. No preamble, no sign-off."
# ---------------------------------------------------------------------------

# --setting-sources "" is the REENTRANCY GUARD (see the header). -p is
# non-interactive: one prompt in, one answer out, no session.
answer=$(printf '%s' "$prompt" \
  | timeout "$TIMEOUT_S" "$MODEL_CMD" -p --setting-sources "" 2>/dev/null)

answer=$(printf '%s' "$answer" | sed 's/^[[:space:]]*//; s/[[:space:]]*$//')
if [ -n "$answer" ]; then
  send_mail urgent "handoff from ${MAIL_ROLE}" "$answer

Files: ${overlap}"
else
  printf 'mail-watcher: model gave nothing — sending the ungarnished handoff\n' >&2
  send_mail urgent "handoff from ${MAIL_ROLE}" "$plain"
fi

# The turn's edits are consumed: this member reports a turn once. The peer's
# view is NOT cleared — that is the peer's own business, and clearing it here
# would let one agent erase another's record of what it is holding.
: > "$EDITS" 2>/dev/null

noop
