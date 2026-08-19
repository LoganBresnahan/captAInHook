# Dogfood report — 2026-08-19 — the robot channel wakes a turn

Roadmap item 22 / ADR-0017. The day the robot half of the mailbox bus stopped
being a test fixture: `watch.json` written, `turn-claude` registered, and one
deliberate request that woke a real `claude -p` on the maintainer's own machine
and got a real answer back on the bus.

Everything below is the live tree — the deployed build at `934f0cb` (identity
`9b5be974c5e9`, daemon pid 818408), the real trail, the real
`~/.captainHook/mail/mail.jsonl`. Append-only: the second half (a day of normal
work — idle-exit, presence, recurrence) is **not yet observed** and is marked as
such rather than guessed.

## The configuration, as installed

Three files, and the channel does not exist until all three say yes (d7).

| where | what |
|---|---|
| `handlers.json` | `turn-claude` on `["mail-nudge"]`, `budgetMs` 600000, `TURN_WORKSPACE=/home/oof/captAInHook`, `TURN_ALLOWED_TOOLS` = the payload default (Read/Grep/Glob + `mail send`) |
| `watch.json` | one rule: `reviewer`, `>=urgent`, `quietFor 10min`, `noLiveSession true`, budget `perEnvelope 1` / `perRoleHour 2` |
| `dispatch.json` | untouched — `default: allow`, so the nudge is admitted; see finding 2 for why a `project` rule was deliberately NOT added |

`mail watch --once` before anything was sent read the installation correctly:
`turn payload on mail-nudge: installed; human-held roles: maintainer, reviewer`
→ **`reviewer: mixed`**. That is the shape a real installation has, and it is
worth stating plainly: because the robot capability is installation-wide (d3 as
amended), a role that any window reads is `mixed`, never `robot-servable`. The
rule's `noLiveSession` is therefore the *only* thing between a live human
reviewer window and a robot turn — the presence question is on the critical
path, not beside it.

## The chain, end to end

One `kind: request`, `priority: urgent` envelope to `reviewer`, sent by
`mail send` from a window that does not hold the role (this repo; the reviewer
digest is `dispatch.json`-scoped to `/home/oof/profile`).

| ts | event | detail |
|---|---|---|
| 16:14:52.385 | `mail.append` | `m-dogfood-hops-01` → reviewer, urgent, 1146 B |
| 16:14:52.871 | `watch.evaluate` | `Trail` · roles 1 · mailboxes 1 · **armed `nextCheckInMs: 600000`** · `stateSaved` |
| 16:24:53.144 | `mail.nudge` | `1 unread past quiet (10m+) · no live session · budget envelope 1/1 · role 1/2 this hour` |
| 16:24:53.148 | `dispatch.start` → `exec.spawn` | `turn-claude` pid 826304, `spawner: bosun` |
| 16:24:53.293 | `mail.deliver` | role reviewer, **no `sessionId`**, `hookEvent: UserPromptSubmit`, dispatchId = the nudge's |
| 16:28:40 | `mail.append` | `m-dogfood-hops-01-answer` → maintainer, `inReplyTo` set, 2524 B body |
| 16:28:52.654 | `exec.exit` | code 0, `durMs: 239495` |
| 16:28:45 | read | the answer arrived in the maintainer window's next `UserPromptSubmit` digest |

**Measurements**

- **Nudge latency: 600.76s** from append to `dispatch.start`, against a 600s
  threshold — 760ms of overhead, which is the 1s trail-tail poll and nothing
  else. The deadline is one held number re-checked per tick, and it shows.
- **Turn wall time: 239.5s** (~4 min) — 40% of the 600000ms handler budget,
  well inside the payload's own `timeout 600`. Budget never approached.
- **Ask → answer readable: 13m53s**, of which 10min was the threshold the
  operator chose. The channel's latency is the rule's latency.
- **Zero** `watch.evaluateFailed`, `watch.evaluateStalled`, `watch.stateTorn`,
  `watch.stateReanchor`, `watch.dispatchFailed`, `nudge.denied`.
- **The loop stopped by itself.** The turn's own `mail.cursorAdvance` and the
  answer's `mail.append` each re-triggered an evaluation (by design — the gate
  filters which rows, it does not claim none do); both returned `0 nudges` and
  `nextCheckInMs: none`. One nudge was the whole bill.

The answer itself was work, not a demo: it decided the thread lane's blocking
question (store `hops`, because a derived walk hits a rotated-away `inReplyTo`
and silently resets the count — "a bound whose failure direction is
unbounded-and-silent is not a bound"), named the stored option's own cost (a
reaper forward launders the count), and specified one test. It is the input to
`thread-fields`.

## Findings

**1 · The nudge names one envelope; the pickup delivers the backlog.** The nudge
named `m-dogfood-hops-01`. The `mail.deliver` carried **three** —
`review-1ee7218-request` and `review-1ee7218-addressed` from 2026-08-16 came
with it, because the role's sessionless mailbox was created by this pickup and
anchored behind them. Correct per the digest's own rules, and not what "a turn
was woken for mail this role had not read" leads a reader to expect. The turn
paid tokens to reason about two settled threads, and triaged them correctly on
its own ("the 1ee7218 review thread needs nothing from me: the reply landed and
all findings were taken") — but that was the model being sensible, not the
system being bounded. **A first pickup on a new sessionless mailbox is
unbounded in what it hands a turn.**

**2 · `project`-scoped consent cannot work for nudges today.** The spawn's `cwd`
is `/home/oof/.captainHook` — the daemon's own — because `MailNudge.Workspace`
is null and **`watch.json` has no field that could name a workspace**. ADR-0017
d5 says `Workspace` "doubles as the dispatch's cwd: that is what makes
`dispatch.json`'s `project` criterion work on nudges", and the turn payload's
own header says the nudge carries `workspace` "when the watcher had one to
give". Nothing can give it one. A `project` rule written against
`TURN_WORKSPACE` would match the daemon's cwd instead and mis-scope silently.
This is why `dispatch.json` was left alone here. Either `watch.json` grows a
`workspace` field, or the ADR's claim about project-scoped robot consent needs
withdrawing.

**3 · The answer's provenance is model-authored fiction.** The envelope reads
`from: {agent: "claude", harness: "claude-code", session: "reviewer"}`. Nothing
stamped that — the model wrote it, because `mail send` takes an envelope on
stdin and the turn composes it. N6 anticipated "a payload-stamped name"; as
built it is *model*-stamped, which is weaker: `agent` is not the role, and
`session` holds the string `reviewer`, which is not a session id and joins to
nothing. Provenance on the canvas will read "claude" for every robot answer of
every role. A payload that composed the envelope's `from` itself, or a
`mail send --as-role` that stamped it, would close this.

**4 · The `dispatch.start` row advertises the wrong budget.** It logged
`budgetMs: 2000` (the dispatcher's fan-out default) while the handler actually
ran under its entry's 600000 — visible in `handler.ok`'s `budgetMs: 600000`
239 seconds later. Nothing failed, but a maintainer diagnosing a killed turn
would read the dispatch row and blame the wrong number.

**5 · The old reviewer cursor is now a corpse-in-waiting.** The pickup created
`cursor.reviewer..json` (sessionless, as designed — a turn can never leave a
dead-mailbox candidate). Beside it sits
`cursor.reviewer.84abfc7c-f1f3-4aca-be01-e8ac01a58e92.json` from a window that
closed days ago: an instance mailbox, unregistered, whose session is not live.
That is exactly ADR-0018 d6's dead mailbox, and with no `reaper` payload
installed a `reaper` rule would only log `unserved`. `reaper-payloads` has its
first real subject.

**6 · The bill is not measurable from the trail.** N1 says a woken turn is money.
What the trail records is wall time and exit code; the model's own output is
captured as one `exec.stderr` row, **2049 bytes and truncated mid-sentence**.
There is no token or cost column anywhere, so "what did the robot channel cost
this week" cannot be answered from the record the channel keeps. Wall time is
the only proxy, and it is a bad one.

## Not yet observed (the second half)

Deliberately left open — these need a day of ordinary work, not one exercise:

- **N2 · idle-exit delaying nudges.** Not measurable here: the maintainer
  session was active in the same tree throughout, so its own hooks kept the
  daemon warm (the idle window is 30 min). The first quiet-machine nudge is the
  real datapoint.
- **Presence false negatives.** The brain's own header names the hazard — a
  `mixed` role's window that has fired hooks but never been handed mail is
  invisible to `noLiveSession`. Not yet hit; with `reviewer` being `mixed` and
  the rule's only guard being presence, this is the one most likely to bite.
- **Recurrence and the hourly budget.** `perRoleHour 2` has been spent once.
  Whether a normal day produces a second nudge, and whether the budget is felt
  as a bound or a nuisance, is unmeasured.
- **Whether the next-prompt loop is enough.** 13m53s ask→read here, but the
  operator was watching the trail. Whether `mail ask --wait` is actually wanted
  is a question about impatience under normal work, and one exercise cannot
  answer it.

## What graduates

- Finding 2 → an ADR-0017 amendment or a `watch.json` `workspace` field, before
  anyone documents project-scoped robot consent as working.
- Findings 1 and 3 → `thread-fields` / the turn payload: bound a first pickup,
  and stop provenance being model prose.
- Finding 5 → `reaper-payloads`, which now has a live corpse to tend.
- The turn's answer itself → the `thread-fields` stored-vs-derived decision.
