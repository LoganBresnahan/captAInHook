#!/bin/sh
# reaper-deterministic — the REAPER payload (roadmap item 23 / ADR-0018
# decision 6, slice `reaper-payloads`). What a dead-mailbox nudge wakes: the
# daemon's watcher finds an instance mailbox holding mail with nobody left to
# read it, dispatches the internal `mail-nudge` event addressed to the `reaper`
# role, and this payload disposes of the corpse by a rule the OPERATOR wrote.
#
# ⚠ NO MODEL RUNS HERE, AND THAT IS THE DECISION. ADR-0018 originally made the
# LLM reaper "the feature" — a model reading a dead box's mail and judging each
# envelope. That was WITHDRAWN 2026-08-20 (ADR-0017 § Scope amendment): an LLM
# disposing of other members' mail is agent orchestration, which is outside this
# project's scope. What ships is the deterministic fallback, and it turns out to
# answer d6's open authority question by construction — see BLINDNESS below.
#
# ⚠ THIS PAYLOAD IS BLIND. It never reads a body. `captainHook mail forward`
# copies envelopes from the ledger to the ledger inside the engine, and what
# comes back here is a count and a confirmation line. The reaper is the bus's
# first cross-mailbox reader, and the reason that was a hard question was a
# MODEL reading other members' mail to judge it; a payload that moves sealed
# envelopes without opening them is a postal worker, not a reader.
#
# THE DISPOSITION IS DATA, NOT JUDGEMENT (the house pattern: declare capability
# in data, provide behavior in code). One env var in the handlers.json entry:
#
#   REAPER_DISPOSITION=forward-all-to-role   copy everything still pending to
#                                            REAPER_FORWARD_TO, then reap
#   REAPER_DISPOSITION=drop-all              reap; the mail stays on the ledger,
#                                            undelivered, forever readable
#   REAPER_DISPOSITION=hold                  do nothing at all, and say so
#
# THERE IS NO DEFAULT. An unset disposition REFUSES: disposing of somebody
# else's mail because a payload was installed, without the operator ever saying
# how, is exactly the silent consent this project keeps refusing (the same
# reasoning that makes `watch.json` a per-role rule rather than a switch).
#
# REGISTRATION (see examples/payloads/handlers.json):
#   {"name":"reaper","command":".../reaper-deterministic.sh",
#    "events":["mail-nudge"],"mode":"oneshot","failMode":"open","budgetMs":10000,
#    "env":{"REAPER_DISPOSITION":"forward-all-to-role",
#           "REAPER_FORWARD_TO":"maintainer",
#           "CAPTAINHOOK_BIN":"/home/you/.captainHook/bin/captainHook"}}
#
# and a `watch.json` rule for the `reaper` ROLE, which is the consent (ADR-0017
# d7) and also the SCOPE: the watcher decides which mailboxes are dead and worth
# reporting, so a second allow-list here would be a second consent surface that
# can drift out of step with the first (`mail status`' "never configured twice"
# rule, applied to authority).
#
# ⚠ REENTRANCY is not a concern the way it is for a turn payload — nothing here
# spawns an agent — but the EVENT guard is kept for the same reason
# `turn-claude.sh` keeps it: `MailNudge` is internal, so a payload registered on
# a hook event would be reaping mailboxes on somebody's keystrokes.

BIN="${CAPTAINHOOK_BIN:-${HOME}/.captainHook/bin/captainHook}"
[ -x "$BIN" ] || BIN=captainHook

# A nudge changes nothing about the loop it was dispatched from — there is no
# loop; the nudge came from the daemon. `internal.json` declares no effects.
noop() { printf '{"effect":"noop"}\n'; exit 0; }

# Good enough for the three fields this payload reads: a role and two addresses,
# none of which can contain a quote (the address grammar is `[a-z0-9][a-z0-9-]*`
# on both halves, ADR-0018 d2).
field() { printf '%s' "$1" | sed -n 's/.*"'"$2"'":"\([^"]*\)".*/\1/p'; }

IFS= read -r envelope

# ---- guard: one event, and only one ---------------------------------------
case "$envelope" in
  *'"type":"MailNudge"'*) ;;
  *)
    printf 'reaper: refusing — this payload may only be registered on the internal\n' >&2
    printf '        mail-nudge event; on a hook event it would reap mailboxes on a keystroke.\n' >&2
    noop
    ;;
esac

# ---- which corpse ----------------------------------------------------------
# `address` rides ONLY a dead-mailbox nudge (ADR-0018 d6): a role-behind nudge
# names no mailbox, and this payload has nothing to say about one. Testing the
# field rather than the reason string is deliberate — the address is what this
# payload needs, so its absence is exactly the condition to refuse on.
address=$(field "$envelope" address)
[ -n "$address" ] || {
  printf 'reaper: this nudge names no mailbox (it is a role nudge, not a dead-mailbox one) — nothing to tend\n' >&2
  noop
}

role=$(field "$envelope" role)
[ -n "$role" ] || role=reaper

# ---- the disposition, which the operator wrote -----------------------------
case "${REAPER_DISPOSITION:-}" in
  hold)
    printf 'reaper: holding %s — REAPER_DISPOSITION=hold, so its standing and its mail both stay\n' "$address" >&2
    noop
    ;;
  drop-all)
    # Drop is not a delete: the envelopes stay on the append-only ledger and
    # stay readable forever. What ends is the mailbox's STANDING, and the
    # `mail.reap` line is what tells a later reader why.
    "$BIN" mail reap "$address" --by "$role" >&2 || {
      printf 'reaper: could not reap %s — its standing is unchanged\n' "$address" >&2
      noop
    }
    noop
    ;;
  forward-all-to-role)
    [ -n "${REAPER_FORWARD_TO:-}" ] || {
      printf 'reaper: REAPER_DISPOSITION=forward-all-to-role but REAPER_FORWARD_TO names no address\n' >&2
      printf '        — refusing rather than guessing where somebody else%s mail should go\n' "'s" >&2
      noop
    }

    # ORDER IS THE SAFETY PROPERTY, and it is d6's own order: forward, THEN
    # reap. A forward that fails leaves the standing intact, so the mailbox is
    # still a candidate and the next nudge tries again; reaping first would
    # remove the standing and then discover it cannot pass the mail on, and
    # nothing would ever look at that box again. `mail forward` is idempotent
    # on (source, target), so the retry copies only what did not make it.
    "$BIN" mail forward "$address" --to "$REAPER_FORWARD_TO" --by "$role" >&2 || {
      printf 'reaper: forward failed for %s — NOT reaping, so the next nudge can retry\n' "$address" >&2
      noop
    }

    "$BIN" mail reap "$address" --by "$role" >&2 || {
      printf 'reaper: %s was forwarded but not reaped — its mail is safe at %s and the\n' "$address" "$REAPER_FORWARD_TO" >&2
      printf '        next run will skip what it already moved\n' >&2
      noop
    }
    noop
    ;;
  "")
    printf 'reaper: no REAPER_DISPOSITION in this handler entry — refusing to dispose of\n' >&2
    printf '        %s. An operator says how mail is disposed of; a payload does not decide.\n' "$address" >&2
    noop
    ;;
  *)
    printf 'reaper: unknown REAPER_DISPOSITION "%s" — expected forward-all-to-role, drop-all or hold\n' \
      "$REAPER_DISPOSITION" >&2
    noop
    ;;
esac
