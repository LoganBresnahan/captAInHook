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
   Presence (`SessionPresence`: cursor files ∪ recent dispatches) says whether
   anyone is home. Consequence: for a human-held role the robot channel does
   not exist — `mail-nudge` is never dispatched, and the count *is* the nudge.

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
| 1 | `watch-rules` — `~/.captainHook/watch.json` strict parser mirroring `DispatchPolicy` (every violation collected; invalid ⇒ warn + zero robot nudges; absent ⇒ zero) | watcher | opus or sonnet | low | Absent and malformed both ⇒ zero nudges — no fail-open asymmetry to guard; parser table tests. |
| 1 | `mail-nudge-event` — the internal `HookEvent` member (origin daemon, harness `internal`, effects logged+ignored), its dispatch entry, embedded `internal` spec, trail rows | watcher | opus | medium | **verify:** the N3 origin audit — presence must NOT count a nudge dispatch, `internal` must never reach a stdout-serialize path, a policy denial is logged not answered. Must be complete before `watcher-actor` dispatches for real, or the presence-fed brain feeds itself. |
| 2 | `role-kind-inference` — human-held / robot-servable / mixed from registrations (REUSE `mail-status`'s digest→roles lookup, never fork it) + the role→any-live-session join over `ApiReadModel.Presence` | watcher | opus | medium | Pure classification + fixtures; the "human-held ⇒ never a robot" consequence is enforced by the brain, not here. |
| 2 | `thread-aware-delivery` — `MailDigest.Plan` learns the session: an answer is urgent-class for the asker's cursor, ambient elsewhere; the Stop reconcile block says "answered by …" for ONE more turn; late answers tagged | thread | opus | **high** | **verify:** the one-more-turn state vs the existing Stop-loop guard (N3 of 0016) and the cursor advance — a naive flag re-blocks Stop forever or is dropped by the delivered-offsets advance. |
| 2 | `mail-ask-wait` — `mail ask --to <role> [--wait <s>]`: append, then bounded monotonic poll for an answer with `inReplyTo`; prints it or `unanswered` and WHICH bound fired | thread | opus | medium | **verify:** monotonic deadline vs the harness's tool timeout (N4) — must fail closed at the shorter, honestly; FakeClock tests, no sleeps. |
| 3 | `watcher-brain` — pure `(pending, presence, roleKind, monotonicNow, nudgeState, rules) → nudges` + `mail watch --once` (verification only, not a schedule) | watcher | **fable** | **high** | **verify:** the arm/disarm/re-arm protocol (`quietFor` as a re-checked deadline; a second envelope while armed re-arms, never double-arms); how persisted state re-derives deadlines across a restart WITHOUT wall-clock control flow (invariant 2); `perRoleHour` sliding window and `perEnvelope` as the token-bill bound; human-held ⇒ never a robot nudge. Golden-tested off the mail reducer fixtures. Its signature fixes the nudgeState shape — do not let phase 4 redefine it. |
| 4 | `nudge-state-and-trail` — `mail/nudges.jsonl` (exactly the brain's state; deadlines re-derived on load; torn tail ⇒ reanchor) + the `mail.nudge` trail row in both emitters (WireJsonl goldens) | watcher | opus | medium | FakeClock round-trip / torn-tail / restart tests. Land SERIALLY with `thread-fields` — both regenerate shared goldens; `CAPTAINHOOK_SCHEMA_UPDATE=1` once each, never in one uncommitted tree. |
| 4 | `turn-claude-payload` — `examples/payloads/turn-claude.sh` (exec-wire envelope on stdin ⇒ `claude -p "<digest>"` in the role's workspace, fresh session) + handlers.json entry + a stub-claude test pinning argv | watcher | opus | medium | Deterministic argv pin. **Resolve the reentrancy question here:** the driven claude's own `UserPromptSubmit` MUST fire (d6 wants a real cursor), so ADR-0010 N7's `--setting-sources ""` guard cannot be copied verbatim — the structural bound is the nudge budget + `dispatch.json`. |
| 5 | `watcher-actor` — the in-daemon supervised actor: fed by the daemon's own trail tail (`mail.append` / `mail.cursorAdvance`), arms deadlines through the actor layer, persists then dispatches `MailNudge`; never defers idle-exit; honors due deadlines on start | watcher | **fable** | **high** | **verify:** deadline firing with no timers and no wall clock; the self-feeding trail loop (its own `mail.nudge` / dispatch rows must not re-trigger it); persist-vs-dispatch ordering ⇒ neither a double nor a lost nudge across a crash; idle-exit N2. Any `Task.Delay` / `DateTime.UtcNow` creeping in here is the most likely regression. |
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
