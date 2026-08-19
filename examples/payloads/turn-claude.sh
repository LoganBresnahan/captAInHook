#!/bin/sh
# turn-claude — the TURN payload for the `claude` harness (roadmap item 22 /
# ADR-0017 decision 6). This is what a robot nudge WAKES: the daemon's watcher
# decides a role is falling behind on its mail, dispatches the internal
# `mail-nudge` event, and this payload opens one turn to deal with it.
#
# It is registered like any other exec handler — `handlers.json` on
# `"events": ["mail-nudge"]`, `dispatch.json` is the consent, budgets bound it —
# because a nudge is an ordinary event and waking a member needed no new
# spawner (d5). What is special here is only that the child is an AGENT.
#
# ⚠ THE BUS IS THE MEMORY, NOT THE SESSION. A woken turn is a FRESH `claude -p`
# every time; this payload never `--resume`s. A session a human may be sitting
# in is not a thing to drive, and everything a turn needs to know is either in
# the mail it was handed or on the bus it answers to.
#
# ⚠ REENTRANCY — two guards, and neither is optional (ADR-0010 N7).
#
#   1. `--setting-sources ""` starts the child with NO hook configuration, so
#      it cannot fire the operator's hooks at all. The suite proves it is
#      present the only way it can be: a stub `claude` that EXITS NONZERO when
#      the flag is missing, so the guard is proven PASSED, not merely typed.
#      It costs the operator's PERMISSION settings too, which is why
#      `--allowedTools` below is not optional garnish — see TURN_ALLOWED_TOOLS.
#   2. THIS PAYLOAD REFUSES ANY EVENT BUT `MailNudge`. `MailNudge` is internal —
#      only the daemon's own watcher raises it — so a turn can never fire the
#      event that woke it. That is true only for as long as this payload stays
#      registered on `mail-nudge`, and an operator who registers it on `Stop`
#      would rebuild the classic regress (spawn an agent from a Stop hook, its
#      Stop hook spawns another…). The refusal below is what keeps that from
#      being a silent mistake.
#
# Because guard 1 is kept, THE DRIVEN TURN FIRES NO HOOKS OF ITS OWN — so it
# does not pick its own mail up. This payload does the pickup instead, and that
# choice answers two questions at once (see the ADR's `turn-claude-payload` row):
#
#   * THE PICKUP IS REAL. `captainHook mail digest --role <role>` advances a
#     cursor and writes a `mail.deliver` line, so the ledger says the robot read
#     it, the canvas draws it delivered, and the watcher stops re-nudging. The
#     alternative — hand the model the nudge's own digest text and never move a
#     cursor — would leave every envelope a robot read looking unread forever.
#   * IT READS THE ROLE'S SESSIONLESS MAILBOX — no `--as`, no session id. That
#     mailbox has no INSTANCE, and the dead-mailbox rule (ADR-0018 d6) only ever
#     considers instance mailboxes, so a turn can never leave a corpse behind:
#     not one per turn, not one at all. It is also durable — every turn of a role
#     shares it, `Advance`'s per-cursor lock making the pickup first-come — so
#     nothing re-anchors and no mail is re-read.
#
# ⚠ ORDER: the pickup happens BEFORE the model runs, and a turn that then dies
# has lost that digest rather than doubled it — the same direction `mail digest`
# itself chose (ADR-0016 d4). So the cheap refusals (no model, no workspace)
# ALL come first: nothing is delivered until there is something able to read it.
#
# REGISTRATION (see examples/payloads/handlers.json):
#   {"name":"turn-claude","command":".../turn-claude.sh",
#    "events":["mail-nudge"],"mode":"oneshot","failMode":"open","budgetMs":600000,
#    "env":{"TURN_WORKSPACE":"/home/you/code/your-repo",
#           "CAPTAINHOOK_BIN":"/home/you/.captainHook/bin/captainHook",
#           "TURN_ALLOWED_TOOLS":"Read,Grep,Glob,Bash(/home/you/.captainHook/bin/captainHook mail send:*)"}}
#
# and a `watch.json` rule for the role, which is the per-role consent (d7) —
# installing this payload alone wakes nobody.

MODEL_CMD="${TURN_MODEL_CMD:-claude}"
TIMEOUT_S="${TURN_TIMEOUT_S:-600}"

BIN="${CAPTAINHOOK_BIN:-${HOME}/.captainHook/bin/captainHook}"
[ -x "$BIN" ] || BIN=captainHook

# A turn changes NOTHING about the loop it was dispatched from — there is no
# loop; the nudge came from the daemon. `internal.json` declares no effects, so
# anything else here would be downgraded and warned about anyway.
noop() { printf '{"effect":"noop"}\n'; exit 0; }

# Good enough for the two fields this payload reads by hand: a role and a
# workspace path, neither of which can contain a quote. Everything else — the
# digest, the reason, `replyHow` — goes to the model as raw JSON rather than
# through a shell parser that would mangle its escapes.
field() { printf '%s' "$1" | sed -n 's/.*"'"$2"'":"\([^"]*\)".*/\1/p'; }

IFS= read -r envelope

# ---- guard 2: one event, and only one -------------------------------------
case "$envelope" in
  *'"type":"MailNudge"'*) ;;
  *)
    printf 'turn-claude: refusing — this payload may only be registered on the internal\n' >&2
    printf '             mail-nudge event. On any hook event a turn would fire the very\n' >&2
    printf '             hook that spawned it (ADR-0010 N7).\n' >&2
    noop
    ;;
esac

role=$(field "$envelope" role)
[ -n "$role" ] || {
  printf 'turn-claude: the nudge names no role — nothing to read\n' >&2
  noop
}

# The nudge carries `workspace` when the watcher had one to give; otherwise the
# entry's env says where this role's turns run. A turn has to run SOMEWHERE
# specific, and the daemon's own working directory is not a workspace anybody
# chose — so an unset one refuses rather than guesses.
workspace=$(field "$envelope" workspace)
[ -n "$workspace" ] || workspace="$TURN_WORKSPACE"
[ -d "$workspace" ] || {
  printf 'turn-claude: no workspace for role %s — set TURN_WORKSPACE in the handlers.json entry\n' "$role" >&2
  noop
}

command -v "$MODEL_CMD" >/dev/null 2>&1 || {
  printf 'turn-claude: %s not on PATH — nothing is delivered and the mail stays pending\n' "$MODEL_CMD" >&2
  noop
}

# ---- the pickup -----------------------------------------------------------
# The role's SESSIONLESS mailbox (see the header): no `--as`, no sessionId, so
# no instance and therefore never a dead-mailbox candidate. The dispatchId is
# the nudge's own, so the `mail.deliver` line joins to the dispatch that caused
# it; the event type is the seam this payload is about to open — a turn's first
# prompt IS a UserPromptSubmit, and it is what makes the digest render as an
# inject at all (the harness spec declares no effects for an internal event).
dispatch=$(field "$envelope" dispatchId)
delivery=$(printf '{"v":1,"dispatchId":"%s","event":{"type":"UserPromptSubmit","payload":{}}}\n' "$dispatch" \
  | "$BIN" mail digest --role "$role" --harness claude-code --seam ambient)

case "$delivery" in
  *'"effect":"inject"'*) ;;
  *)
    # Nothing was pending by the time this turn started — a window read it
    # between the watcher's decision and this spawn. Cheap when there is
    # nothing to say is what makes a robot channel affordable.
    printf 'turn-claude: nothing left to deliver for %s — not spending a turn\n' "$role" >&2
    noop
    ;;
esac

# ---- the turn -------------------------------------------------------------
# Both JSON lines go to the model verbatim, which is deliberate: `digest`,
# `reason` and `replyHow` are escaped prose, and a shell that unescaped them
# would be a second, worse copy of a decoder — while a model reads them fine.
# It also keeps `replyHow` spelled ONCE, in the engine (WatcherBrain), rather
# than duplicated into every harness's payload.
prompt="You are an AI coding agent, woken by captAInHook because mail addressed
to the role '${role}' had gone unread. You are a fresh session: the bus is your
memory, and everything you know about this task is below.

THE NUDGE (the watcher's own envelope; read \"reason\" for why you were woken,
and \"replyHow\" for exactly how to answer):
${envelope}

THE MAIL (as the bus just delivered it into this turn; read the \"text\" field):
${delivery}

Do what the mail asks. Then answer on the bus exactly as \"replyHow\" says —
that is the only channel anyone will see; nothing you print here is read by
anybody. If there is nothing to answer, say so on the bus and stop."

cd "$workspace" || noop

# WIDEN THIS. `--setting-sources ""` takes the operator's permission settings
# away along with their hooks, so a turn can do NOTHING unless this says so —
# and the one thing it must always be able to do is answer, or the whole channel
# is a model talking to itself. The default is that reply path plus read-only
# search; anything a turn should be able to CHANGE is the operator's call, in
# the entry's env.
ALLOWED="${TURN_ALLOWED_TOOLS:-Read,Grep,Glob,Bash(${BIN} mail send:*)}"

# -p is non-interactive. --setting-sources "" is REENTRANCY GUARD 1 (see the
# header) — keep it. The child's stdout goes to STDERR on purpose: our stdout is
# the exec wire and carries exactly one JSON line, which is the noop below.
printf '%s' "$prompt" \
  | timeout "$TIMEOUT_S" "$MODEL_CMD" -p --setting-sources "" --allowedTools "$ALLOWED" >&2 2>&1 \
  || printf 'turn-claude: the turn exited nonzero or timed out — its mail is already delivered\n' >&2

noop
