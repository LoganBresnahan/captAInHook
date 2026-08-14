# Dogfood report — 2026-08-14 — the first members on the bus

The last slice of roadmap item 20 before the docs capstone: the mailbox bus
gets its first two **members**, and the whole path — a payload writing mail, a
daemon storing it, another agent's seam delivering it — runs on the deployed
build rather than in a sandbox. The engine work all landed in phases 1–5; this
is the first time anything *used* it the way a user would.

Deliberately last, per ADR-0016's implementation plan: no live payloads on the
maintainer's real session until the exactly-once tests and the Stop-loop pin
were green. They were (suite 904 at HEAD `e134ec0`), so the gate opened.

## What went live

Two committed starters, both answering `noop` — a bus member's value is what
reaches *another agent*, never an effect on its own loop:

- **`starter-mail-observer.sh`** — write-only member (ADR-0016 d5's cheapest
  class), `resident` on PostToolUse. Streams this agent's reads and edits onto
  the bus, addressed to the PEER's role, and escalates to `urgent` when the
  edited path appears in the peer's view. That escalation is the payload only
  the hub position makes possible: neither agent can compute it alone.
- **`starter-mail-watcher.sh`** — on-demand LLM member, `oneshot` on Stop. A
  deterministic gate (my edits ∩ the peer's reads) decides whether the turn is
  worth waking a model over; only past it does a second model write the handoff
  note. Carries the `--setting-sources ""` reentrancy guard (ADR-0010 N7).

## Verified live, on the deployed stack

Registered into the real `~/.captainHook/handlers.json` (hot-reloaded, no daemon
restart) with a `PostToolUse` entry added to the real `settings.json`, driven
partly by this session's own tool calls and partly by hand-fired hooks through
the deployed `captainShim`. Everything below is the live tree, not a fixture.

| what | evidence |
| --- | --- |
| write path | a real PostToolUse Edit produced a chained envelope: `id=edit-db82477d`, `to=peer`, `priority=urgent`, `topic="stale view: MailStore.cs"`, genesis `prev` of 64 zeros, store 0700 / lines 0600 |
| escalation | the same edit against a path *nobody* held stayed `ambient`/`status` — the bus stays useful without becoming a mid-turn interrupt for every write |
| read path | `mail send` from a **hookless** peer (`from.harness=generic-json`, no session — d5's write-only class, "anything that can run a process can send mail"), then a real `user-prompt-submit` through the shim delivered it: `[captAInHook mail] 1 message(s) for 'captainhook'` with full provenance, merged after the echo handler's inject |
| exactly-once | the same seam fired again delivered nothing — echo only |
| ledger | `mail.deliver` with `renderHash=d3c59f45…`, `bytesInjected=258`, `vehicle=inject`, `envelopeIds=["peer-001"]`, `role=captainhook` |
| cursor | `cursor.captainhook.live-dogfood.json` on disk, per (role, session) |

**Latency.** The observer costs **3.9–5.0ms** per tool call (8 samples, real
dispatches from session `593af288`), inside a 10.4ms total PostToolUse dispatch
and a 14–18ms end-to-end shim round trip. The resident child's first dispatch
paid 100.3ms for the cold spawn — the latency doctrine holding exactly as
written: this shape is `resident` precisely because a `oneshot` would pay that
on every tool call. The `oneshot` digest cost 159.8ms cold on a turn edge,
where it is affordable.

## Three findings

**1. Two agents cannot get two roles from registration alone.** Found while
designing the swarm test, and it is the sharpest thing this slice surfaced.
`handlers.json` is GLOBAL and `--role` is a static string in an entry, so both
members run in both agents' windows and every observer reports the same role —
which would make the whole bus one agent talking to itself. The mechanism that
fixes it already exists and the ADR already names it, as a slogan rather than a
requirement: *"swarm activation is a dispatch-policy flip, not a boot verb."*
Concretely, per-project scoping is handler-named policy rules AND'd with a
`project` path-prefix:

```json
{"handler":"mail-observer-alpha","project":"/home/you/beta-repo","decision":"deny"}
```

An excluded handler is filtered BEFORE fan-out — never asked, never restarted —
so the wrong-role member costs nothing in the window it does not belong to.
This is now driven end-to-end (`MailSwarmDaemonSmokeTests`) rather than
described, and recorded in both starters' headers and the payloads README. The
alternative — one shared role for everyone — is worse and was rejected here: a
member would receive its own traffic back, and nothing in the digest filters by
sender.

**2. A `settings.json` hook edit takes effect mid-session, without a restart.**
I assumed the opposite and planned around it: the first check after adding the
PostToolUse entry showed zero dispatches, which looked like confirmation. It
was just an ordering artifact — the check ran before the next tool call. The
trail then showed **9 real PostToolUse dispatches** from this live session,
including the observer recording a file this very session had read. Worth
knowing for any future dogfood: a hook registration is live as soon as it is
written, so the blast radius of editing the real settings file is immediate.

**3. WSL2 stepped the wall clock again, live, mid-dogfood.** Timing two
back-to-back commands with `date +%s%N` reported **−89,120ms** — a negative
duration, from an ~89-second forward step and its return. The engine's own
`durMs` for the same dispatch read 159.8ms and was correct throughout, because
control-flow timing is monotonic by invariant 2. This is a straight
re-confirmation of `doc/platform.md` § Wall-clock steps (first found running
down the e2e flake in item 19's `screenshot-loop` slice), and a reminder that
the harness *around* the engine is where this bites: my measurement script was
the thing that violated the invariant, exactly as the Playwright fixture once
did.

## What was NOT dogfooded, and why

- **A second real agent loop.** The named first target — both agents' PostToolUse
  streaming into one edit log — is proven end-to-end in the suite with two
  sessions, two projects, two roles and real spawned payloads, but the live half
  ran with one real agent (this session) and one synthetic hookless peer. A
  genuine second Claude Code window is the maintainer's to open; nothing in the
  design is waiting on it.
- **A Stop-seam digest left armed.** Stop now declares `decide`, the harness has
  no loop cap of its own, and our cursor advance is the only guard against a
  livelock. Arming a blocking turn-end seam on the maintainer's own session
  while they are away is not a risk worth taking for a demo; the reconcile seam
  is pinned by the daemon smoke instead (`Daemon_StopSeam_BlocksWithTheTopLevel
  Shape_ThenTerminates`).

## State of the live tree

**Reverted to exactly as found.** `handlers.json` and `settings.json` restored
from backups (both backups removed), and the dogfood's `mail/` store and
`observer-views/` deleted. The JSONL trail keeps its record, being append-only.
Nothing from this exercise is armed on the maintainer's session; the ready-made
registration lives in `examples/payloads/handlers.json` for whenever they want
it.

## Tests this produced

`MailDogfoodTests.cs`, 5 tests, suite 904 → 909 green twice:

- **the reentrancy guard, PROVEN** — a stub `claude` that EXITS NONZERO when
  `--setting-sources ""` is absent from its argv (the plan's new test pattern).
  The shipped watcher passes it and the model's words reach the bus; a copy with
  the guard stripped gets refused and degrades to the ungarnished handoff. The
  mutation is what gives the first test meaning: if it ever passes with the
  model's words present, the stub has stopped enforcing.
- **the on-demand gate** — no overlap ⇒ the model is never spawned and no mail
  is written at all.
- **the swarm, end to end** — two sessions, two projects, policy-scoped roles,
  real resident observers: the peer hears the stale-view alert at its next turn
  start with provenance naming the sender, the SENDER hears nothing, the second
  seam is clean, and the chain still verifies after live traffic.
- **the unescalated case** — an edit nobody is holding is ambient, not urgent.

The swarm test was mutation-checked rather than merely watched to pass: making
the observer address its own role instead of the peer's fails the
sender-hears-nothing assertion.
