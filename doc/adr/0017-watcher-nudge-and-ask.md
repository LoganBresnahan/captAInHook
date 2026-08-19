# ADR-0017 — The watcher: a human nudge that is always on, a robot nudge that sometimes fires, and ask/reply over the bus

**Status:** Proposed *(2026-08-17; drafted from the owner's design session of
2026-08-16 — the day the bus carried its first real exchange, a two-window
review of commit 1ee7218 — and the brainstorm that followed. Nothing here is
implemented. Build order via `/adr-plan`, below.)*
**Date:** 2026-08-17
**Builds on:** [ADR-0016](0016-mailbox-bus.md) (the bus; this ADR is the
answer to its decision 9's revisit trigger and resolves its N5/N6),
[ADR-0006](0006-dispatch-policy.md) (policy as data — the consent surface
reused here), [ADR-0010](0010-exec-handlers.md) (payloads as user processes —
the turn payloads), [ADR-0003](0003-declarative-harness-registry.md)
(capabilities in data, closed adapters in code — the deferred `turn`
capability), [ADR-0011](0011-hook-trust-model.md) (the consent boundary,
again NOT relaxed).
**Evidence:** the exchange of 2026-08-16 (`review-1ee7218-request` →
`review-1ee7218-reply` → `review-1ee7218-addressed`, in the maintainer's live
`mail.jsonl`; written up in `doc/dogfood/`).

## Context

ADR-0016 shipped a bus that is deliberately **passive**: mail is written to a
ledger and delivered only when the recipient's own loop walks through a hook
seam. It said, in decision 9, that request/reply was *out of v1* and named the
trigger to revisit: *an agent needs to **wait** on another agent, not merely
hear.* And it rejected push ("no harness offers an interrupt surface; the
human's agent keeps single-threaded control of its own loop").

On 2026-08-16 the trigger fired in the plainest possible way. The maintainer
window sent a review request to a `reviewer` role held by a second window;
the request sat on the ledger, visible on the Mail canvas as a card with no
cursor near it, until a **human typed a keystroke** in the other window. The
review that came back was excellent (three real bugs), and the whole loop
was legible on the canvas — but the loop had a human in every seat, and the
sender could do nothing but wait for a keystroke it could not cause.

Stepping back to fundamentals showed *why*, and what is and is not latent:

```
     idle ──▶ prompt ──▶ turn ──▶ turn ──▶ stop ──┐
      ▲          │          │        │        │    │
   nothing   UserPrompt   PreTool  PostTool  Stop  │   ← every seam is the harness
   fires     Submit       Use      Use       (may  │     pausing to ask "may I go on,
   here      (inject)     (decide) (inject)  block)│     and with what?"
```

The human answers exactly one of those questions (the prompt); hooks answer
the rest. captAInHook is therefore *already* an agent-shapes-agent substrate;
the bus made the shaper remote and asynchronous. **Continuation** of a running
agent by another agent's mail is latent today (Stop-`block` + `mail digest
--seam reconcile`, built 2026-08-13). **Initiation** — reaching an agent that
is idle at its prompt — is impossible from inside a harness, because an idle
harness fires no seam. That is the constraint everything below respects.

Two more facts from the same day shape the design. First, the human is the
right *target* of most nudges: a window's mailbox count in the corner of the
window would have made the whole demo obvious, and it never enters the loop.
Second, the daemon already knows everything a watcher needs — the pending set
per cursor (`MailCursors.Pending`), presence (`SessionPresence`), and the
ledger's own events as trail lines (`mail.append`, `mail.cursorAdvance`) —
so a watcher is a *rule with a trigger attached to state that exists*, not a
new subsystem.

## Decision

1. **Three kinds of thing on the bus, kept distinct.** A **digest** carries
   mail *into* an actor, only when that actor's seam opens (ADR-0016). A
   **runner** *is* the actor for a role — a headless loop that does the work.
   A **watcher** carries no mail and does no work: it looks at ledger +
   cursors + presence, decides "someone is falling behind", and pokes.
   This ADR builds the watcher and the smallest honest runner shape; it does
   not build a swarm orchestrator.

2. **The human channel is always on and has no rules.** A new read-only verb,
   `captainHook mail status`, prints one line per role the caller may read —
   `📬 2 · 1 urgent` — for wiring into a harness's passive display (Claude
   Code's `statusLine`; the Mail canvas already shows it; other harnesses,
   whatever passive surface they offer). "Which roles may this window read?"
   is answered by the *existing* policy evaluator: the `mail digest` handlers
   that would survive `dispatch.json` for this cwd/session ⇒ their `--role`s.
   No new configuration names a window's role twice. The count is a read of
   cursor files; it never enters the loop; it needs no rule because it
   interrupts nothing.

3. **Roles have a kind, inferred from what is registered — never declared
   twice.** A role served only by `mail digest` handlers is **human-held**. A
   role with a turn payload registered on the `mail-nudge` event (d5) is
   **robot-servable**. Both ⇒ **mixed**: human first, robot as fallback.
   *(**Amended 2026-08-18** at `role-kind-inference`: the turn-payload half is
   INSTALLATION-WIDE, not per-role — the dispatcher fans out by event, so a
   per-role registration would scope nothing and could exist only to be read
   back by the inference, which is the "declared twice" this decision refuses.
   The per-role gate is the watch rule (d7). See the Ground truth row.)*
   Presence (`SessionPresence`: cursor files ∪ recent dispatches) says whether
   anyone is home. Consequence: for a human-held role the robot channel does
   not exist — `mail-nudge` is never dispatched, and the count *is* the nudge.
   *(Read with the amendment: since the robot half is installation-wide,
   "human-held" means "a digest is registered and NO turn payload is installed
   anywhere" — the moment one is, every digest role is `Mixed` and the only
   thing standing between it and a robot turn is the absence of a `watch.json`
   rule for it. `WatcherBrainTests.HumanHeld_NeverNudges…` proves exactly the
   no-payload case, nothing stronger; the per-role "no robot here" is d7's.)*

4. **The watcher is in-daemon, event-driven, and pure — no cron, no timers.**
   It runs as a supervised actor fed by the daemon's own trail tail: rules are
   evaluated on `mail.append` and `mail.cursorAdvance`, nothing else. A rule
   that reads as "unread for 10 min" is a **deadline re-check**, not a timer
   that fires: at append the watcher arms one monotonic deadline (the actor
   layer's restart-window primitive) and re-evaluates once when it passes; a
   cursor advance that clears the condition disarms it. The brain is a pure
   function `(pending, presence, roleKind, monotonicNow, nudgeState, rules)
   → nudges`, golden-tested against the same fixtures the reducer uses. Nudge
   state (last-nudged per envelope×role, budgets, armed deadlines) persists in
   `~/.captainHook/mail/nudges.jsonl` so it survives idle-exit; the watcher
   does **not** defer idle-exit — a deadline that falls while the daemon
   sleeps is honored on the next start. `mail watch --once` runs the same
   brain from the CLI for verification only; it is not a schedule.
   *(**As built 2026-08-18**, slice `watcher-brain`: the brain returns ONE
   deadline per verdict — the actor arms exactly that — and its state is not
   "armed deadlines" but the facts they are re-derived from (first-seen and
   quiet-since stamps, nudge counts, the role's sliding window); it crosses a
   restart as durations, so time the daemon was not running is not counted —
   a deadline that had fallen is due at once, one that had not resumes where it
   left off. "Golden-tested off the reducer's fixtures" is as-built the same
   REAL store, cursors and digest verb the reducer's golden derives from, with
   the brain's own checked-in golden. See the Ground truth row.)*

5. **The robot nudge is an ordinary hook event, not a new spawner.** The
   watcher dispatches a synthetic event **`MailNudge`** — envelope
   `{role, envelopeIds, reason, digest (rendered, deterministic), replyHow,
   workspace}` — through the **same dispatcher** the shim uses. `HookEvent`'s
   closed set grows one *internal* member: origin is the daemon (no shim, no
   stdout protocol), the harness id is `internal`, and every effect a payload
   returns is logged and ignored — the answer comes back on the bus, never
   through the effect. Everything shipped applies unchanged: `handlers.json`
   registers turn payloads on `events: ["mail-nudge"]`; **`dispatch.json` is
   the consent** (which payloads may run, for which project); bosun, budgets,
   the kill discipline, capability policy (roadmap 15) when it lands, and the
   trail (`dispatch.start → exec.spawn → exec.exit`). Zero new spawner code,
   zero new policy language, one new event kind.

6. **Opening a turn is per-harness and lives in payloads first.** The engine
   knows no harness CLI. v1 ships one exec payload per harness under
   `examples/payloads/turn-<harness>.sh` (`turn-claude.sh` first: `claude -p
   "<digest>"` in the role's workspace), consuming the exec-wire envelope on
   stdin. Two invariants make this harness-agnostic: **the bus is the memory,
   not the session** — a woken turn is a *fresh* session every time; never
   `--resume` into a session a human may be sitting in — and **identity comes
   from the harness if it fires hooks, else from the payload**: a driven
   `claude -p` fires its own `UserPromptSubmit` with its own `session_id`, so
   its pickup is a real cursor and a real `mail.deliver`; a hookless harness's
   payload stamps `from.session = "runner:<role>:<n>"`. Promotion to a
   `HarnessSpec` **`turn` capability** (`{"adapter": "cli-prompt-arg" |
   "cli-prompt-stdin" | "http", …}` — data selecting among a closed adapter
   set, ADR-0003's move) is deferred with a trigger: three harnesses' turn
   payloads that differ only in argv shape.

7. **Rules are data, exist only for the robot channel, and are the consent
   for the loud channel.** `~/.captainHook/watch.json`, `dispatch.json`'s
   idiom (strict parse, every violation collected; invalid ⇒ warn + no robot
   nudges, never fatal; absent ⇒ no robot nudges ever):

   ```json
   { "version": 1,
     "rules": [
       { "role": "reviewer",
         "when":   { "priority": ">=urgent", "quietFor": "10min", "noLiveSession": true },
         "budget": { "perEnvelope": 1, "perRoleHour": 4 } } ] }
   ```

   `noLiveSession` defaults true (the mixed-role rule). `quietFor` is a
   monotonic deadline (d4). Budgets are counters — the ping-pong bound and the
   token-bill bound in one place — and the digest *says* the budget state.

8. **Ask/reply: `inReplyTo` becomes read; addresses stay roles.** An `answer`
   whose `inReplyTo` names a request **closes** it. ~~Delivery becomes
   thread-aware without becoming session-addressed: the answer is still `to`
   a role, but the digest prefers the *asking session's* cursor and treats it
   as urgent-class there, ambient for any other session in the role~~ —
   ***simplified 2026-08-17 by [ADR-0018](0018-instance-addressing.md) d4 as
   built (`answer-by-address`), before this ADR's phase 2 built it:*** the
   answer is addressed `to` the asker's mailbox — the request's `replyTo`, or
   since 0018's d3 amendment simply `role@<asking session>` — and reaches that
   one cursor by ROUTING, so no delivery preference exists and `inReplyTo` is
   correlation only. What survives of this decision unchanged: an answer to
   the asker's own mailbox is a unicast, and `thread-aware-delivery` should
   treat it urgent-class there (it is the thing the asker is waiting on) — the
   class is now a fact about the address, not a preference over cursors.
   ADR-0016 N5 (fan-out is wrong for ask/reply) is closed by an address, not a
   heuristic. `mail.append` provenance grows `inReplyTo`. A new verb **`captainHook mail
   ask --to <role> [--wait <s>]`** appends the request and, with `--wait`,
   blocks up to the deadline polling the store for a matching answer, then
   prints it or `unanswered` — the asker *chooses* to yield its turn (a bounded
   tool call), which is the only synchronous shape ADR-0016 d9 was willing to
   admit ("daemon-hosted; MCP-shaped" reads as "a verb the agent runs"). Every
   thread carries a **hop budget** (`hops` incremented per `inReplyTo` link;
   refused past N; the digest says `hop n/N`) so two well-meaning agents cannot
   ping-pong on the owner's bill.

9. **How an answer reaches the session that asked — every state has a seam
   except the human's own.** Blocked in `--wait` ⇒ the tool call returns it.
   Mid-turn ⇒ the urgent seam (PostToolUse) injects it. At Stop ⇒ the
   reconcile seam blocks with "your request was answered by …" for one more
   turn. Idle human window ⇒ the count and the canvas; nothing enters the
   loop (ADR-0016's rejection of push, reaffirmed). A finished runner ⇒ the
   watcher wakes it again — the answer is just new mail for its role.

10. **Provenance: nudges are trail lines, never envelopes.** `mail.nudge
    {role, sessionId?, envelopeIds, channel, reason, budget}` — mail-about-mail
    would recurse, and the trail is where "I poked them and they still haven't
    read it" belongs. The Mail canvas draws a nudge as a mark on the lane
    (2026-08-16's finding, generalised: the picture must show what the system
    *did*, not only what it holds). Threads draw as a link request→answer
    across lanes; a request without an answer is an **awaiting** card.

11. **Bounds, all counters or monotonic deadlines (house invariant 2):** nudge
    budgets per envelope and per role-hour; hop budget per thread; `--wait`
    per ask; `quietFor` as re-checked deadlines. Nothing here reads the wall
    clock for control flow.

## Rejected alternatives

| alternative | disposition |
|---|---|
| **A cron / scheduled watcher** | Rejected — timed execution points are the wrong primitive: rules are true *because the ledger changed*, and "quiet for N" is a deadline re-check, not a schedule. Event-driven off the daemon's own trail tail; deadlines via the actor layer. `mail watch --once` exists for verification, not scheduling. |
| **`--resume` a human's live session to deliver** | Rejected — two processes on one transcript, and it pushes into a loop a human is sitting in. Runners are fresh sessions; the bus is the memory. |
| **Push into a human window (toast that injects, tty writes)** | Rejected again (ADR-0016). The human channel *sits beside* the loop (statusline, canvas); it never enters it. |
| **An LLM watcher by default** | Rejected — the decision "is this stuck?" is a threshold over facts the daemon holds; a model call on every append is ADR-0016 N6's measured cost with no measured benefit. Opt-in later, as a payload, if a judgment-shaped rule ever proves necessary. |
| **A new spawner / orchestrator for runners** | Rejected — `MailNudge` through the ordinary dispatcher reuses registration, budgets, bosun, policy, capability policy and the trail. A second spawn path is a second kill discipline to get wrong. |
| **Session-id addressing for replies** | Rejected (ADR-0016) — still roles. Thread-aware *delivery preference* via `inReplyTo` gets the answer to the asker without stranding it if that session is gone. |
| **A `turn` HarnessSpec capability from day one** | Deferred — one harness's argv is not a pattern. Payload first; promote at three (d6's trigger). |
| **A fourth seam class `drive`** | Deferred — considered as a way to distinguish "read your mail" from "do this now" in the reconcile block; not needed for the watcher or ask/reply. Reopen with the `turn` capability if runners need policy defaults distinct from reconcile. |

## Consequences

### Positive

- The demo's missing piece — *"who has mail, right now, in the corner of my
  eye"* — costs one read of cursor files and no ADR-level machinery; it ships
  first and stands alone.
- The robot channel is *four lines of data* end to end (`statusLine` →
  `watch.json` → `handlers.json` → `dispatch.json`); take any one away and the
  robot does not move; the human count still shows.
- Nothing new can reach an agent's loop that could not before: `MailNudge`
  spawns *processes*, and a process reaches an agent only through the seams
  ADR-0016 already governs. The consent boundary (ADR-0011) is unchanged.
- Everything is on the ledger or the trail: who nudged whom, which turn woke,
  which session answered — the canvas draws it, and `VerifyChain` covers the
  mail half.
- Ask/reply arrives without a transport: `--wait` is a bounded read.

### Negative

- **N1 · A woken turn is a model call on the owner's bill** with no human in
  front of it. Budgets in `watch.json` and consent in `dispatch.json` are the
  throttles; a misconfigured rule is money. The digest must say the budget
  state so a runaway is visible from any window.
- **N2 · Idle daemon = late nudges.** The watcher does not defer idle-exit; a
  role can sit unserved until the next hook anywhere restarts the daemon.
  Honest "sometimes"; a resident watcher that keeps the daemon alive is a
  measured-cost decision for later.
- **N3 · The internal event bends the model a little.** `HookEvent` gains a
  member with no shim and no harness; every place that switches on origin
  must treat `internal` deliberately (policy, trail, GUI filters).
- **N4 · `--wait` is a blocked tool call.** A harness with a hard tool
  timeout shorter than the wait truncates it; the verb must fail closed to
  `unanswered` at the *shorter* of its own deadline and the harness's, and
  say which.
- **N5 · Two humans, two windows, one role, one ask.** Thread-preference
  resolves the *answer* to the asker; it does not stop two humans both
  answering. First answer closes; later ones are tagged `late` on the canvas.
- **N6 · A hookless harness's runner cannot have a real cursor.** Its
  `from.session` is a payload-stamped name, so its "delivered" is by
  construction, not by a `mail.deliver` line; the canvas must show the
  difference (`vehicle: payload`).

## Implementation plan

*Decomposed via `/adr-plan` 2026-08-17: **14 slices → 7 phases, two lanes.**
Critical path `mail-status → role-kind-inference → watcher-brain →
nudge-state-and-trail → watcher-actor → e2e-stub-runner-loop → docs`. After
phase 1 the **watcher lane** (that path) and the **thread lane**
(`thread-fields → {thread-aware-delivery, mail-ask-wait} → thread-canvas`)
run side by side. Adversarial verify on five slices — the ones where a
plausible single pass ships a bug that spends the owner's tokens or wedges a
loop; no ultracode anywhere (each slice is one coherent function / actor /
verb; the parallelism is between slices, not inside one). Model names are
session aliases; effort is the session setting.*

**`mail-status` ships first and stands alone — deploy and dogfood it before
anything else lands.** It is the one slice that pays off by itself.

| # | slice | lane | model | effort | verify |
|---|---|---|---|---|---|
| 1 ✅ | `mail-status` **(landed 2026-08-17)** — `captainHook mail status` (stdin session/cwd → `DispatchPolicy.Evaluate` over the registered `mail digest` handlers ⇒ roles ⇒ `MailCursors.Pending` counts ⇒ one `📬 n · m urgent` line per role); statusLine wiring documented | watcher | opus | medium | goldens on the line + the policy-derived role set; absent handlers.json / no digest / denied / zero-pending. Read-only, wrong count is cosmetic — no adversarial pass. `/shipshape`. |
| 1 | `thread-fields` — `inReplyTo` READ (parse, `mail.append` provenance, read model's id→closed-by index), the hop counter and refusal at N; regenerate the reducer golden | thread | opus | medium | **Decide stored-vs-derived `hops` FIRST** — stored enters the hashed line and `VerifyChain`; reversing later is a format migration. Envelope/send/chain tests; goldens gate. |
| 1 ✅ | `watch-rules` **(landed 2026-08-18)** — `~/.captainHook/watch.json` strict parser mirroring `DispatchPolicy` (every violation collected; invalid ⇒ warn + zero robot nudges; absent ⇒ zero) | watcher | opus or sonnet | low | Absent and malformed both ⇒ zero nudges — no fail-open asymmetry to guard; parser table tests. |
| 1 ✅ | `mail-nudge-event` **(landed 2026-08-18)** — the internal `HookEvent` member (origin daemon, harness `internal`, effects logged+ignored), its dispatch entry, embedded `internal` spec, trail rows | watcher | opus | medium | **verify:** the N3 origin audit — presence must NOT count a nudge dispatch, `internal` must never reach a stdout-serialize path, a policy denial is logged not answered. Must be complete before `watcher-actor` dispatches for real, or the presence-fed brain feeds itself. |
| 2 ✅ | `role-kind-inference` **(landed 2026-08-18)** — human-held / robot-servable / mixed from registrations (REUSE `mail-status`'s digest→roles lookup, never fork it) + the role→any-live-session join over `ApiReadModel.Presence` | watcher | opus | medium | Pure classification + fixtures; the "human-held ⇒ never a robot" consequence is enforced by the brain, not here. |
| 2 | `thread-aware-delivery` — `MailDigest.Plan` learns the session: an answer is urgent-class for the asker's cursor, ambient elsewhere; the Stop reconcile block says "answered by …" for ONE more turn; late answers tagged | thread | opus | **high** | **verify:** the one-more-turn state vs the existing Stop-loop guard (N3 of 0016) and the cursor advance — a naive flag re-blocks Stop forever or is dropped by the delivered-offsets advance. |
| 2 | `mail-ask-wait` — `mail ask --to <role> [--wait <s>]`: append, then bounded monotonic poll for an answer with `inReplyTo`; prints it or `unanswered` and WHICH bound fired | thread | opus | medium | **verify:** monotonic deadline vs the harness's tool timeout (N4) — must fail closed at the shorter, honestly; FakeClock tests, no sleeps. |
| 3 ✅ | `watcher-brain` **(landed 2026-08-18)** — pure `(pending, presence, roleKind, monotonicNow, nudgeState, rules) → nudges` + `mail watch --once` (verification only, not a schedule) | watcher | **fable** | **high** | **verify:** the arm/disarm/re-arm protocol (`quietFor` as a re-checked deadline; a second envelope while armed re-arms, never double-arms); how persisted state re-derives deadlines across a restart WITHOUT wall-clock control flow (invariant 2); `perRoleHour` sliding window and `perEnvelope` as the token-bill bound; human-held ⇒ never a robot nudge. Golden-tested off the mail reducer fixtures. Its signature fixes the nudgeState shape — do not let phase 4 redefine it. **As built:** ONE `NextCheckMs` per verdict (the actor arms exactly that; double-arm is structurally impossible); unread = pending in EVERY accepting mailbox of the role; state crosses a restart as AGES (`ToAges`/`FromAges`) — the gap is not counted; `Record` is the caller's so a denied nudge spends nothing; `--once` is dry, and `--as-if-quiet` is how an operator sees past a threshold before `nudges.jsonl` exists. Skeptic pass run (fable): two real findings fixed — a nudge names and charges only what its capped digest carries, and the gatherer reads cursorless unicast mailboxes off the ledger — plus four limits recorded in the file header rather than papered over. Ground truth row below. |
| 4 ✅ | `nudge-state-and-trail` **(landed 2026-08-18)** — `mail/nudges.jsonl` (exactly the brain's state; deadlines re-derived on load; torn tail ⇒ reanchor) + the `mail.nudge` trail row in both emitters (WireJsonl goldens) | watcher | opus | medium | FakeClock round-trip / torn-tail / restart tests. Land SERIALLY with `thread-fields` — both regenerate shared goldens; `CAPTAINHOOK_SCHEMA_UPDATE=1` once each, never in one uncommitted tree. **As built:** the file is APPEND-ONLY and unlocked, and the reader's torn-line tolerance IS the concurrency story — a crashed or interleaved append can only leave a line that does not parse, losing the tail costs one save and losing every line re-anchors, both *fewer nudges, later*; compaction past `CompactAtBytes` keeps it bounded through a rename. `mail.nudge` is written ONLY when the dispatch ran, by the same `NudgeStore.Record` call that charges the budgets, so a poke on the picture and a spent budget cannot disagree; d10's `channel` and `sessionId` columns are deliberately absent and `budget` is numbers rather than the sentence in `reason` (see the Ground truth rows). `mail watch --once` now READS the state and still writes none. Only `thread-fields` regenerated goldens alongside — it has not started, so the serial constraint was satisfied by there being nothing to serialise against. |
| 4 ✅ | `turn-claude-payload` **(landed 2026-08-18)** — `examples/payloads/turn-claude.sh` (exec-wire envelope on stdin ⇒ `claude -p` in the role's workspace, fresh session) + handlers.json entry + a stub-claude test pinning argv | watcher | opus | medium | Deterministic argv pin. **Both open questions resolved, and the reentrancy one in the OPPOSITE direction to the plan's expectation.** The plan assumed the driven claude's own `UserPromptSubmit` must fire, so N7's guard could not be copied verbatim. As built the guard IS kept verbatim (`--setting-sources ""`) and **the payload does the pickup itself** — d6's own second branch ("identity comes from the harness if it fires hooks, ELSE from the payload"), taken for claude too. That single choice answers the corpse question as well, and structurally rather than by declaration: with no hooks the turn leaves no session cursor at all, and the payload's `captainHook mail digest --role <role>` (no `--as`, no session) reads the role's SESSIONLESS mailbox — which has no INSTANCE, and ADR-0018 d6's rule only ever considers instance mailboxes. So no number of turns can grow a candidate, there is nothing to register, and nothing for a human window in the same cwd to inherit. The `--as`-a-registered-mailbox route the review pass proposed was rejected on the way: scoping a `mail digest --as turn-<role>` registration to the turn alone requires either a second workspace path or a permanently-denied registration that exists only to be read back — a fact declared where it can drift, which d3 refuses. SECOND GUARD, new: the payload refuses any event but `MailNudge`, since the internal event is the only reason a turn can never fire what woke it, and that stays true only while the registration does. Costs recorded rather than hidden (Ground truth row): the pickup's `mail.deliver` carries `hookEvent: UserPromptSubmit` — the seam the payload is about to open — with no `sessionId` and the nudge's `dispatchId`; a hookless turn gets no mid-turn mail, reading once at its start; and `--setting-sources ""` takes the operator's PERMISSIONS with their hooks, so `--allowedTools` ships with the reply path allowed and the rest the operator's call. `MailNudge.Workspace` stays null and the workspace comes from the entry env, refused rather than guessed. |
| 5 | `watcher-actor` — the in-daemon supervised actor: fed by the daemon's own trail tail (`mail.append` / `mail.cursorAdvance`), arms deadlines through the actor layer, persists then dispatches `MailNudge`; never defers idle-exit; honors due deadlines on start | watcher | **fable** | **high** | **verify:** deadline firing with no timers and no wall clock; the self-feeding trail loop (its own `mail.nudge` / dispatch rows must not re-trigger it); persist-vs-dispatch ordering ⇒ neither a double nor a lost nudge across a crash; idle-exit N2. Any `Task.Delay` / `DateTime.UtcNow` creeping in here is the most likely regression. **Cost note** (brain review, 2026-08-18): `MailWatch.ReadMailboxes` as built does one full `MailStore.Read()` for the ledger's addresses plus one `MailCursors.Pending` (another full read) PER mailbox, and a `reaper` rule widens the sweep to every role with a cursor file — O(cursors × store) per evaluation, i.e. per `mail.append`/`mail.cursorAdvance` in the daemon. Fine at the live bus's size; the actor should read the store once per evaluation and derive every mailbox's pending from that one read (or the gatherer should grow that shape) BEFORE this lands on a busy bus, not after. |
| 5 | `thread-canvas` — awaiting cards, request→answer links across lanes, `late` tag, nudge marks on the lane from `mail.nudge`, `vehicle: payload` drawn differently (N6) | thread | opus | medium | `/ui-loop`: golden/skeptic fold harness + READ the screenshots, both themes; runs in parallel with the actor. |
| 6 | `e2e-stub-runner-loop` — the whole chain, zero tokens: append → rule → `MailNudge` → stub harness payload (fires `captainShim hook user-prompt-submit` with its own session id, answers with `mail send kind:answer inReplyTo`) → asker's `--wait` returns → canvas shows thread + nudge, both engines | both | opus | medium | A wrong test fails loudly. This is the flaky-guard exposure point: generous timeouts, sandbox env only (never `~/.captainHook`), expect suite-green-twice to cost a retry cycle. |
| 7 | `docs-and-ground-truth` — flow doc § *The watcher* (diagram from this ADR's context, ground-truth rows), statusLine wiring, `watch.json` + `mail.nudge` schema, this ADR's Ground truth + Status flip, roadmap 22 tick, a dated dogfood note | — | opus | low | `/shipshape`; every symbol named exists. Ground-truth rows and roadmap ticks accrue in EACH landing commit — this phase is a sweep, not a rewrite. |

**Phases:** (1) the four leaves in one parallel sweep — disjoint files
(`MailSend`/`Program.cs`; `MailEnvelope`/`MailStore`/read model;
`WatchRules.cs`; `Model.cs` + the embedded `internal` spec) → (2)
`role-kind-inference` on the tail of the `mail-status` commit if the lookup
is still warm; `thread-aware-delivery` ‖ `mail-ask-wait` on the thread lane →
(3) `watcher-brain` alone, hard, verified before anything consumes it → (4)
`nudge-state-and-trail` ‖ `turn-claude-payload` → (5) `watcher-actor` ‖
`thread-canvas` (different skills: the canvas is `/ui-loop`) → (6) the
zero-token e2e → (7) the docs sweep.

**Sequencing risks named by the plan:** (1) three shape contracts cross the
lanes — the `WatchRule` record (rules → brain), the nudgeState shape (brain →
persistence), the `mail.nudge` row + `vehicle: payload` (persistence/payload →
canvas): pin each in the earlier commit. (2) `thread-fields`' hops
stored-vs-derived enters the hashed line if stored — decide before the thread
lane builds on it. (3) golden-regeneration collisions between `thread-fields`
and `nudge-state-and-trail`: serial, one regen each. (4) `mail-nudge-event`'s
origin audit before the actor dispatches. (5) the turn payload's reentrancy
bound is structural (budget + policy), not `--setting-sources ""`. (6)
invariant 2 across the whole watcher lane — brain, loader and actor all
FakeClock/`PollUntilAsync`-testable. (7) e2e is where the flaky guard bites.

Standing rules as in ADR-0016: mail and watcher tests point at explicit temp
dirs, never the live `~/.captainHook/`; ship bar suite green twice;
`/shipshape` before commits; the live installation touched only via `/deploy`.

## Ground truth

*(rows accrue as slices land)*

| decision | lives in |
|---|---|
| d3 — roles have a kind, inferred from what is registered (**amended: the robot half is installation-wide**) | `RoleKind` (`Unserved`/`HumanHeld`/`RobotServable`/`Mixed`), `RoleKinds` (`From`, `Of`, `RobotChannelExists`, `HumanHeld`, `TurnPayloadInstalled`) and `RolePresence` (`FreshestDispatchAgeMs`, `AnyLiveSession`) in `dotnet/captainHook/Core/RoleKinds.cs` — all PURE: values in, values out, no I/O and no clock, so the brain's fixtures can drive it. **The amendment, and why.** As written, d3 reads as though a turn payload is registered per role. `Dispatcher.DispatchAsync` looks up runners by `e.Type`, so every handler on `mail-nudge` runs on every nudge whatever role it names: two per-role registrations would BOTH spawn on each nudge and one would exit immediately having read a role off the envelope that is not its own — a process spawn per role per nudge, for nothing. A per-role registration could therefore only annotate, never scope, and would exist solely to be read back here, which is exactly the second declaration d3 refuses. So the CAPABILITY is installation-wide (`TurnPayloadInstalled` — is any turn payload registered on `mail-nudge` at all, with the event name canonicalized first, since a registration writes kebab and the host spells Pascal and a raw comparison would find nothing, silently, forever) and the per-role CONSENT stays in `watch.json` (d7). Kind and rules remain independent brain inputs, and an operator has two honest ways to say "no robot here": install no turn payload, or write no rule. **`Unserved` is added to d3's three** — nobody reads it and nothing can be woken for it — because it is the state the 2026-08-17 dogfood pass found four of on the live bus, and "we decided not to nudge" and "nothing here can help" are different facts. **The digest lookup is not forked:** `MailStatus`'s private `MailboxOf` was LIFTED onto `MailDigest` (the verb whose registrations these are) and both callers use it — recognition through the real argument parser, ADR-0016 d13's rule. **Presence returns an AGE, not a boolean:** "live" needs a threshold, and every number about elapsed time in this subsystem belongs with the brain that owns `quietFor` and the monotonic deadlines (d4, house invariant 2); `AnyLiveSession` exists so a caller WITH a threshold has one place to compare. A named `--as` mailbox never looks live, correctly — since ADR-0018 d3 a cursor's key is its instance, and a durable mailbox is a mailbox, not a window. **Known limit, not a choice:** unlike `mail status` this applies no `dispatch.json` filter, because there is no asking window and "would ANY dispatch anywhere be allowed?" is unanswerable without enumerating every cwd; a role whose digest is denied everywhere still reads human-held, which yields FEWER robot nudges — the conservative direction for a channel that spends the owner's tokens. Tests: `RoleKindsTests.cs` (18) |
| d4 — the watcher is in-daemon, event-driven and pure; the brain (slice `watcher-brain`) | `WatcherBrain.Evaluate(WatchInput) → WatchVerdict` in `dotnet/captainHook/Core/WatcherBrain.cs`, with the state it fixes for phase 4 — `NudgeState` (`Envelopes: WatchedEnvelope{Subject, Id, FirstSeenMs, QuietSinceMs, Nudged}` — `Subject` is the key the brain tracked it under, a role for the role rule or a `role@instance` address for ADR-0018 d6's dead-mailbox rule, i.e. `MailNudge.Subject`; renamed from `Role` in the 2026-08-18 review pass BEFORE phase 4 persists it, since `"role": "maintainer@abc"` in `nudges.jsonl` would have been a lie frozen into a file format — `Nudges: RoleNudge{Role, AtMs}`), `NudgeState.Record(nudge, nowMs, charged)`, and `ToAges`/`FromAges` (`NudgeStateAges`) — plus `WatchedMailbox`, `WatchInput`, `WatchStanding` (the closed set of per-role outcomes) and `WatchRoleVerdict`. PURE: the only time it sees is `NowMs`. **The protocol with the actor, fixed here:** (i) ONE deadline — `WatchVerdict.NextCheckMs` is the minimum over the nearest quiet threshold, the nearest presence expiry (`now + (LiveWithinMs − age) + 1`), the nearest `perRoleHour` window release, and the projected re-arm of the nudges it emits; the actor arms exactly that and replaces what it held, so a second envelope re-arms and can never double-arm; (ii) disarm is not an operation — an envelope that leaves every mailbox leaves the state, and the next evaluation simply arms nothing for it; (iii) `Record` is the CALLER's, after it knows the dispatch ran: the verdict tracks every unread envelope but charges no budget and resets no quiet clock for its own nudges, because a nudge `dispatch.json` denies must not spend a budget the operator refused (`MailNudgeOutcome.Ran`); an uncharged record still restarts quiet, so a denial recurs once per period, not per evaluation. **Unread is the strict reading:** pending in EVERY mailbox of the role that `Accepts` it (one reader taking delivery is the role having heard it; a dead cursor's held-forever mail is the reaper's shape, not a reason to spend tokens); a unicast is judged by its one mailbox; a role with no cursor is read sessionless by the gatherer and everything ever sent is unread. **Quiet** counts from first sighting (`QuietSinceMs`), never from the envelope's wall-clock `ts`, and restarts on `Record`, so a second poke waits the full period. **Live** = freshest dispatch age ≤ `LiveWithinMs` (10 min, mirroring the canvas's `PRESENCE_IDLE_MS`), through `RolePresence.AnyLiveSession` — the one comparison; a `--as` mailbox never looks live. Per envelope the FIRST rule whose priority admits it governs threshold and budget (so an urgent-fast and an ambient-slow rule coexist); `perRoleHour` is the strictest among the admitting rules; an envelope no rule admits is unread but ungoverned — not due, not armed. **Restart without a wall clock:** the state leaves the process as DURATIONS from the moment written and returns as stamps re-derived from the moment read; time the daemon was not running is not counted (six of ten minutes quiet at exit ⇒ due four minutes after start), a deadline that had fallen is due at once (N2), and budget windows stretch across the gap — every consequence in the conservative direction. `MailNudge.Digest` is the real renderer (`MailDigest.Render`) over the due envelopes as a sessionless view with `SeenAt` dropped (every item `new`), so the text is a function of the envelopes alone — and since the renderer caps at whole items, a nudge names and charges ONLY the envelopes it carries; the tail stays due and un-nudged and the verdict arms `now` so the next evaluation carries it (the skeptic pass's first finding: as first written the cap silently spent `perEnvelope` on mail no turn ever saw); `Reason` is a deterministic sentence (`"2 unread past quiet (12m+) · no live session · budget envelope 1/1 · role 1/4 this hour"`); `ReplyHow` is a constant; `Workspace` is null until something names one (`watch.json` has no such field). **`mail watch --once`** (`Mail/MailWatch.cs`, routed from `Program.cs`) is verification: dry, prints the inputs it CANNOT see and how it treated them (presence = the calling session on stdin at age 0 or nobody; state = none, every envelope first seen now, or `--as-if-quiet`), the per-role standing, the nudges it WOULD raise with reason and digest, and the one deadline; refuses to run without `--once`; writes no cursor; logs one `watch.verdict` line. **Where the report goes depends on who asked** (review pass, 2026-08-18): on a terminal (hook-shaped JSON or empty stdin) it is stdout; behind a hook — an exec-wire envelope on stdin — stdout is the ANSWER channel the engine reads one JSON line from and kills the child on anything else, so the report goes to stderr, the trail line is written first, and stdout carries exactly `mail digest`'s `{"effect":"noop","dispatchId"}` (`MailDigest.Noop`, one spelling). As first built the report went to stdout in both shapes, which behind a hook would have failed every dispatch with `exec.protocolError` and lost the `watch.verdict` line to the kill — the one record the verb exists to leave. `MailWatch.ReadMailboxes` is the one gatherer for the CLI and the actor alike: one `WatchedMailbox` per cursor file keyed by the file's instance, the sessionless read for a role with no cursor, AND one for every `role@instance` the ledger addresses that has no cursor yet (a `--as` mailbox not yet fired, an answer to a reaped window) — the skeptic's second finding, a unicast nobody could see; its broadcast history reads pending from the anchor and the intersection rule keeps that honest. **Five limits, kept and written down in the file header** (four from the skeptic pass, the fifth from the 2026-08-18 review): presence reaches a role only through its cursors (a mixed role's window that has never been handed mail is invisible to `noLiveSession`); a role whose last cursor is reaped is read sessionless and its retained history reads unread; quiet accrues only while the daemon runs, so a `quietFor` longer than the idle window is reachable only while other activity keeps the daemon up (N2 sharpened — the resident watcher is the remedy); `perRoleHour` is one window per role shared by every rule naming it, strictest bound wins; and a robot turn's own ephemeral cursor is a future dead-mailbox candidate the brain cannot exempt — resolved on the `turn-claude-payload` row, not here. Tests: `WatcherBrainTests.cs` (57), `WatcherBrainGoldenTests.cs` (2, over the checked-in `dotnet/captainHookTests/watcher-brain.golden.json` — real store + real digest-moved cursors, regenerated with `CAPTAINHOOK_SCHEMA_UPDATE=1`), `MailWatchTests.cs` (20 — including the exec-wire shape: stderr report, one noop line, trail line first) |
| d4 (the memory half) — the state persists so it survives idle-exit (slice `nudge-state-and-trail`) | `NudgeStore` (`Load`, `Save`, `Record`, `Render`, `TryParseLine`) in `dotnet/captainHook/Core/NudgeStore.cs`, over `~/.captainHook/mail/nudges.jsonl` — beside the ledger and the cursors, so one sandbox redirect moves the whole subsystem. **What is stored is exactly `NudgeStateAges`** — the brain's own shape, written as DURATIONS and read back as stamps re-derived from the moment of reading, which is the only form in which a monotonic number may cross a process (invariant 2). **APPEND-ONLY JSONL, and no lock.** There is one writer by design (the daemon's watcher; `mail watch --once` is dry), and what a lock would otherwise buy is bought by the READER instead: each save appends one line holding the whole state, a read takes the LAST line that parses, and a line that does not parse is skipped. A crashed or interleaved append can therefore only ever cost the TAIL — the state one save older, whose ages under-count elapsed quiet — and a file where nothing parses is a REANCHOR to `NudgeState.Empty`, every clock restarted. Both directions are fewer nudges, later; both leave a line (`watch.stateTorn`, `watch.stateReanchor`), as does a save that could not land (`watch.stateUnwritable` — never a throw, because a watcher that cannot remember merely forgets while one that dies stops watching). Past `CompactAtBytes` (256 KiB) a save rewrites the file to its single current line through a sibling temp + same-dir rename (`MailCursors.WriteAtomic`'s idiom, 0600 at creation), so it cannot grow without bound and a compaction that dies leaves the appended file intact. **Strictness is per LINE, never per file:** an unknown `v`, an unknown member, a wrong type or the deferred-unescape trap (a lone surrogate that parses fine and throws at `GetString`) rejects that line alone — so an older daemon reading a newer one's state re-anchors once, which costs exactly one quiet period. `MailWatch.Run` reads it and writes none: a dry verb that saved would leave the daemon a memory nothing in the daemon made, and `--as-if-quiet` now moves the REMEMBERED clocks (and the hour window with them) while keeping `Nudged`, since a `perEnvelope` count is not a fact about time and a preview must not promise a nudge the budget would refuse. Tests: `NudgeStoreTests.cs` (29), four `MailWatchTests` cases |
| d10 — a nudge is a TRAIL LINE, never an envelope (slice `nudge-state-and-trail`) | `NudgeStore.Record(state, nudge, outcome, nowMs)` — the one spelling of "a nudge really happened", because the row and the budget charge must not be able to disagree; `Record` stays the CALLER's by d4's rule 3, and this is what a caller calls. **A DENIED nudge writes no row at all**: nobody was woken, so a `mail.nudge` on the role's lane would put a poke on the picture that never happened — its record is `nudge.denied` (d5), and the state still takes it uncharged, so the refusal recurs once per quiet period rather than once per evaluation. **Three of d10's prose columns are as-built decisions.** NO `channel`: the human channel is a pull — a count `mail status` reads off the cursors — so it emits nothing and every line that can exist here is a robot nudge; a column with one possible value is a fact stated where it can drift instead of derived, and a second channel that leaves a record is when it earns its place (`mail.reap`'s precedent for the same reasoning). NO `sessionId`: a nudge belongs to a ROLE and carries no window, exactly as `nudge.dispatch` already has it; `dispatchId` is what joins the row to the woken turn's `dispatch.start → exec.spawn → exec.exit`. `budget` is NUMBERS (`MailNudgeBudget{envelope, perEnvelope, roleHour, perRoleHour}`) rather than the sentence in `reason`, because a reader must never parse prose to learn what a poke cost — and `MailNudgeBudget.Clause` is the ONE rendering the brain's `reason` uses, so the two cannot drift. `address` rides only a dead-mailbox nudge (ADR-0018 d6), where `role` is the reaper's own lane and says nothing about whose mail is stranded. The Mail canvas does not draw it yet: `mail.nudge` folds as the forward-compat `unknown-event` note until `thread-canvas`. Tests: `WireJsonlTests.MailNudge_IdenticalBytes_CarriesTheBudgetAsNumbers` + `MailNudge_ForADeadMailbox_NamesTheBox` (the cross-emitter goldens), eight `NudgeStoreTests` cases |
| d6 — opening a turn is per-harness and lives in payloads first (slice `turn-claude-payload`) | `examples/payloads/turn-claude.sh`, registered on `"events": ["mail-nudge"]` (`examples/payloads/handlers.json`). Dependency-free POSIX `sh`, like every other payload; the engine still knows no harness CLI. **Two guards, and the first one is kept verbatim.** (1) `--setting-sources ""` — ADR-0010 N7's guard, NOT weakened as this ADR's plan expected. (2) The payload REFUSES any event but `MailNudge`: the internal event is the whole reason a turn cannot fire what woke it, and that holds only while the registration does, so a misregistration on `Stop` (the classic regress) is refused loudly instead of silently rebuilding it. **Because guard 1 is kept, the driven turn fires no hooks and picks up nothing — so the PAYLOAD picks up**, which is d6's own second branch ("identity comes from the harness if it fires hooks, else from the payload") taken for claude as well. `captainHook mail digest --role <role>` with NO `--as` and NO session reads the role's SESSIONLESS mailbox: a real cursor and a real `mail.deliver`, so the ledger says the robot read it, the canvas draws it delivered and the watcher stops re-nudging — and that mailbox has no INSTANCE, so ADR-0018 d6's dead-mailbox rule (which only ever considers instance mailboxes) can never see it. **The corpse question is therefore answered structurally**: no number of turns grows a candidate, nothing is registered, nothing is inherited by a human window in the same cwd, and every turn of a role shares ONE durable mailbox whose per-cursor lock makes the pickup first-come. Pinned in the brain (`WatcherDeadMailboxTests.TheTurnPayloadsSessionlessMailbox_IsNeverADeadMailboxCandidate`, with its instance-mailbox contrast) and in the file the payload actually writes (`TurnPayloadTests.ManyTurns_LeaveExactlyOneMailbox`). **Order is the safety property:** every cheap refusal — wrong event, no role, no workspace, no model on PATH — happens BEFORE the pickup, because a pickup is destructive; a turn that dies after it has lost that digest rather than doubled it, which is `mail digest`'s own chosen direction (ADR-0016 d4). A pickup that finds nothing (a window read it between the decision and the spawn) spends no turn at all. **Three costs, stated:** the pickup's `mail.deliver` carries `hookEvent: UserPromptSubmit` — the seam a turn's first prompt IS, and what makes the digest render as an inject at all, since the `internal` harness declares no effects — recognizable by its absent `sessionId` and the nudge's `dispatchId`; a hookless turn reads its mail ONCE, at its start, with no ambient or urgent seam mid-turn; and `--setting-sources ""` removes the operator's permission settings along with their hooks, so `--allowedTools` ships allowing the reply path (`Bash(<bin> mail send:*)`) plus read-only search and widens through `TURN_ALLOWED_TOOLS`. `MailNudge.Workspace` stays null (nothing in `watch.json` names one), so the workspace is the entry's `TURN_WORKSPACE` and an unset one REFUSES — the daemon's own working directory is not a workspace anybody chose. The digest text goes to the model as the raw JSON lines, nudge and delivery both, so `replyHow`/`ReaperHow` stay spelled once in `WatcherBrain` instead of being copied into every harness's payload (`starter-llm.sh`'s precedent). Tests: `TurnPayloadTests.cs` (10, the shipped script run as a real process with a stub `claude` that exits nonzero when the guard is missing — plus the mutation that proves the stub enforces), two `WatcherDeadMailboxTests` cases |
| d7 — rules are data, exist only for the robot channel, and are its consent (slice `watch-rules`) | `WatchRules` (`TryParse`, `ResolvePath`) + `WatchRule`/`WatchWhen`/`WatchBudget`/`WatchPriority` and the file tri-state `WatchResolution` (`Absent`/`Malformed`/`Loaded`, `Resolve`, `Effective`) in `dotnet/captainHook/Core/WatchRules.cs`, beside `DispatchPolicy` whose idiom it copies (strict walk, every violation in one pass, all-or-nothing accept, never throws on bad DATA, the injectable path — here `CAPTAINHOOK_WATCH_FILE`, else `~/.captainHook/watch.json`). **What DIFFERS from its twin, and is the reason it is a separate document:** the direction of the default. `dispatch.json` absent means allow everything; `watch.json` absent means ZERO robot nudges and malformed means the same thing plus a warn — `Effective()` is where that is stated once, so there is no fail-open asymmetry for a reader to reason about. Hence NO `default` field: a baseline of "nudge" would be a document whose absence and presence differ in the direction that costs money. **As-built decisions this slice had to make.** `when` is REQUIRED and must name at least one of `priority`/`quietFor` — `noLiveSession` defaults true and states no threshold on its own, so a `when` holding only it describes a rule that wakes a model the instant mail lands; somebody who wants that writes `"quietFor": "0s"`, which is legal, and can be held to having meant it. `budget` is REQUIRED with both counters, each ≥ 1, because every candidate default is a number of model calls this code would spend without being told (N1). A duration is `<whole number><unit>` over the CLOSED set ms/s/min/h, yielding MILLISECONDS (one side of a monotonic subtraction, house invariant 2) — no fractions, no bare numbers (`600` is ambiguous by a factor of a thousand and guessing wrong is ten minutes vs. one second), no negatives, and an overflowing value is refused rather than wrapped. A priority is `urgent` or `>=urgent`, matched by NAME (never `Enum.TryParse`, which takes "2" and comma lists) and case-INSENSITIVELY, the deliberate divergence from an address (ADR-0018 d2): a closed set can correct a casing slip, an open universe of mailboxes cannot. `role` goes through `MailAddress.IsRole` — the envelope parser's own predicate — and a `role@instance` address is REFUSED for now, a judgement rather than a grammar check: a rule decides whether a ROLE may be woken while the mailbox a nudge names is an instance the watcher FOUND, and refusing an unbuilt spelling is the reversible direction. Rule ORDER is preserved and duplicates are kept; which rule wins is `watcher-brain`'s. Tests: `WatchRulesTests.cs` (68 — the ADR's own document, the duration and priority tables, the required halves, the strict walk, one-pass violations, the lone-surrogate guard, and the tri-state including the absent-≡-malformed theory) |
| d5 — the robot nudge is an ordinary hook event, not a new spawner (slice `mail-nudge-event`) | `MailNudge` (the envelope + `ToPayloadJson`), `MailNudgeEvent` (`EventType` = `MailNudge`, `HarnessName` = `internal`, `DispatchAsync`) and `MailNudgeOutcome` in `dotnet/captainHook/Core/MailNudgeEvent.cs`; the embedded spec `dotnet/captainHook/harnesses/internal.json`. The slice BUILDS almost nothing, which is the decision: `handlers.json` registers turn payloads on `"events": ["mail-nudge"]`, `dispatch.json` is the consent, and bosun / budgets / the kill discipline / the exec-wire envelope / the `dispatch.start → exec.spawn → exec.exit` trail all apply unchanged. **N3's four ways an internal event is not a hook, each as-built.** (i) NO STDOUT: the closed adapter set gains `none` (`NoWireAdapter`) so the spec can state the absence of a wire format in data rather than borrow a real adapter and leave a serializer one refactor from the sacred channel; reaching it writes nothing and warns `harness.noWireSerialize`. `HarnessSpec.AnswersHooks` (keyed on the ADAPTER, so a second internal harness inherits it) makes BOTH hook wire sites refuse `--harness internal` the way they refuse an unknown name — clear stderr, zero stdout bytes — which is the reachable version of "internal never reaches a stdout-serialize path", since nothing in the nudge path serializes but two words are easy to type. (ii) NO EFFECTS: `internal.json` declares `MailNudge` with `"effects": []`, so the SHIPPED capability gate downgrades whatever a payload returns and warns `harness.effectUnsupported` — log-and-ignore expressed in the existing language rather than as a new rule. (iii) NO PRESENCE: a nudge carries no session, so the daemon's `presence.Seen(evt.SessionId)` stamp has nothing to record even in principle, and this path never calls it; a dispatch that counted as presence would let the watcher's own action answer the watcher's "is anybody live?" question. (iv) A DENIAL IS LOGGED, NOT ANSWERED: `HookRun.PolicyGateFor` was split into `HookRun.DecidePolicy` (the ruling + the one emitter of `policy.skip`/`policy.malformed`/`policy.exclude`, returning `PolicyRuling`) and the stdout half, because the gate's short-circuit IS a serialized Noop — the one thing an internal event must never produce — and copying the three trail lines into the nudge path would put the consent surface's record in two places. The nudge path adds `nudge.denied` / `nudge.dispatch` (src `nudge`, no `sessionId`, dispatchId minted here since there is no shim); `mail.nudge` proper is `nudge-state-and-trail`'s. `workspace` is the ingest's `cwd`, so `dispatch.json`'s `project` criterion scopes robot turns per repository. Consequence accepted: the GUI's Harnesses panel now lists `internal` (one event, no verbs), which is honest and useful to somebody writing a turn payload. Tests: `MailNudgeEventTests.cs` (11) |
| d2 — the human channel, always on and ruleless | `MailStatus` (`Run`, `Line`) in `dotnet/captainHook/Mail/MailStatus.cs`, routed from `Program.cs`'s `mail` switch; `MailStatusTests.cs` (30). **As-built**: the role is NAMED in the line only when the window may read more than one (`📬 maintainer 2 · 1 urgent`), since the ADR's bare `📬 2 · 1 urgent` cannot say which of two roles it means; a role with two seam registrations is still one cursor and one line. Silence is a state — no readable role, nothing pending, absent or malformed `handlers.json` all print nothing at exit 0. Recognition of a digest registration is `MailDigest.TryParseArgs` itself, never a second spelling. Wiring + the uncached whole-store read it costs: [doc/flow/mailbox-bus.md](../flow/mailbox-bus.md) § The human channel |

## Revisit triggers

- Three harnesses' turn payloads differing only in argv ⇒ promote the `turn`
  capability into `HarnessSpec` (d6).
- A rule that cannot be written as a threshold over (pending, presence, kind,
  clock) ⇒ consider an opt-in LLM watcher payload, cost-measured (N6 of 0016).
- Runners needing policy defaults distinct from reconcile ⇒ the `drive` seam
  class.
- A role left unserved across daemon idle-exits often enough to matter ⇒ a
  resident watcher (N2).
