# Example payloads

Worked payloads that prove the exec-handler seam (ADR-0010): the engine runs
*your* process as the payload and maps its stdout onto the effect set. All are
dependency-free POSIX `sh` — the point is that a payload is any language, not
framework code.

**Demos** — the two that show the lifecycle shapes end to end:

| script | mode | event | shows |
| --- | --- | --- | --- |
| [`retriever.sh`](retriever.sh) | `resident` | `PreToolUse` | the daemon holds it warm; lock-step JSONL; `{"ready":1}` handshake; `inject` |
| [`memory.sh`](memory.sh) | `oneshot` | `Stop` | spawn-per-event; a durable side effect; `noop` answer |

**Starters** — one per verb, written to be COPIED and edited. Each is the
smallest useful shape of its idea, with the reasoning in the header and one
clearly-marked section to change:

| script | verb | event | the idea |
| --- | --- | --- | --- |
| [`starter-inject.sh`](starter-inject.sh) | `inject` | `UserPromptSubmit` | put a note file + one live fact (the session's git branch, read from the envelope's `cwd`) in front of the model |
| [`starter-decide.sh`](starter-decide.sh) | `decide` | `PreToolUse` | a gate that can DENY a tool call, with a reason — and the fail-mode choice that comes with it |
| [`starter-side-effect.sh`](starter-side-effect.sh) | `noop` | `Stop` | do work, change nothing (`background` is not an exec verb — see the header) |
| [`starter-llm.sh`](starter-llm.sh) | `inject` | `UserPromptSubmit` | splice a *model* into the loop, with the reentrancy guard and a degrade-to-noop path |

**Bus members** — two starters that are not about a VERB but about a POSITION:
what it looks like to join the mailbox bus (ADR-0016), where the interesting
thing is not the effect on your own loop (both answer `noop`) but what reaches
*another agent*:

| script | class | event | the idea |
| --- | --- | --- | --- |
| [`starter-mail-observer.sh`](starter-mail-observer.sh) | write-only member | `PostToolUse` | stream this agent's reads and edits onto the bus; escalate to `urgent` when the edit hits a file the PEER is holding — the stale-view warning no single agent can compute |
| [`starter-mail-watcher.sh`](starter-mail-watcher.sh) | on-demand LLM member | `Stop` | a deterministic gate decides whether a turn is worth waking a model over; past it, a second model writes the handoff note. Carries the `--setting-sources ""` reentrancy guard |
| [`turn-claude.sh`](turn-claude.sh) | woken member | `mail-nudge` | what a robot NUDGE wakes (ADR-0017 d6): the daemon's watcher decides a role is falling behind, and this opens one fresh `claude -p` turn to deal with it. Carries BOTH guards — `--setting-sources ""`, and a refusal to run on any event but the internal `MailNudge` |

⚠ **A turn payload alone wakes nobody.** Installing `turn-claude.sh` makes the
robot channel *exist* (it is what `RoleKinds` calls a turn payload, and it is
installation-wide); the per-role consent is a `~/.captainHook/watch.json` rule,
and without one no nudge is ever raised:

```json
{ "version": 1,
  "rules": [
    { "role": "reviewer",
      "when":   { "priority": ">=urgent", "quietFor": "10min", "noLiveSession": true },
      "budget": { "perEnvelope": 1, "perRoleHour": 4 } } ] }
```

Run `captainHook mail watch --once` to see what those rules would do before they
do it — it is dry, and `--as-if-quiet` shows past every threshold.

⚠ **Two agents need two ROLES, and registration alone cannot give them that** —
`handlers.json` is global, so both members run in both windows. Dispatch policy
is what scopes a member to an agent (ADR-0016's "swarm activation is a
dispatch-policy flip"): handler-named rules AND'd with a `project` path-prefix.
The rule shape is in `starter-mail-observer.sh`'s header, and the pairing is
driven end-to-end in `MailSwarmDaemonSmokeTests`.

⚠ **The four VERB starters are also the GUI's template gallery** (ADR-0015 d3;
the two bus members above are deliberately NOT gallery templates — the gallery
is one-per-verb): `web/`
inlines their text at build time via Vite `?raw`, so **editing a starter script
changes the shipped GUI on the next `npm run build`**. That is the intended
coupling — one copy of each script, never a drifting duplicate — but it means a
change here is a change to what the GUI hands users. The GUI never writes these
files: it shows the script, tells you where to save it, and installs the entry
behind the verbatim confirm. The maintainer's dogfood payloads (`git-orient`,
`deploy-guard`, `session-pulse`, `doc-pointer`, `orient-brief`) are deliberately
NOT templates — they encode one person's workflow.

## The wire, in one glance

The engine sends one compact envelope line per dispatch on **stdin**:

```json
{"v":1,"dispatchId":"…","event":{"type":"PreToolUse","sessionId":"…","cwd":"…","payload":{…}}}
```

The payload answers with exactly one line on **stdout** from the closed
grammar (`inject` / `decide` / `replace` / `noop`). A **resident** child first
emits `{"ready":1}`, then answers each envelope and **must** echo the
`dispatchId` (so the daemon binds answer→dispatch across a warm stream). A
**oneshot** child reads one envelope, answers once, and exits; the echo is
optional. Everything else the payload writes goes to **stderr** (captured to
the trail, never parsed as protocol).

The child environment is **stripped to an allowlist** (`PATH`, `HOME`,
`USER`, `SHELL`, `LANG`, `LC_*`, `TZ`, `TMPDIR`, plus whatever the entry's
`env`/`passEnv` names) — ambient secrets in the daemon's environment never
leak in. These demos lean only on `$HOME`.

## Install

1. Make them executable (already `chmod +x` in the repo, but after a fresh
   clone): `chmod +x retriever.sh memory.sh`.
2. Copy `handlers.json` to `~/.captainHook/handlers.json` and **edit the two
   `command` paths** to wherever you cloned this repo (they are absolute — the
   engine resolves a bare name on `PATH`, but an absolute path is
   unambiguous).
3. Seed the retriever's notes (optional): put lines in
   `~/.captainHook/demo-notes.txt`. A tool call whose envelope mentions a word
   (>3 chars) from a note gets that note injected.
4. The next hook picks it up — `handlers.json` hot-reloads (a resident entry
   (re)spawns its warm child on the next dispatch); no daemon restart needed.

Watch it work: `tail -f ~/.captainHook/logs/captainHook.jsonl` and look for
`exec.spawn` (mode `resident`), `exec.ready`, `exec.answered`, and — after a
`Stop` — a new line in `~/.captainHook/demo-memory.log`.

## Latency doctrine

`retriever` is `resident` on `PreToolUse` on purpose: a `oneshot` on a
before-tools event spawns an interpreter on **every tool call**, serially, on
the agent's critical path — the engine warns (`handlers.slowShape`) if you try
it. `memory` is `oneshot` on `Stop` because session edges are rare; a warm
process there would just idle. When no daemon is running, a resident entry
**degrades** to oneshot-lifecycle for that one collapsed hook (spawn, serve,
die) — it never orphans a child.

## The mail digest (ADR-0016)

The example `handlers.json` also carries two **mailbox-bus** entries — not
scripts but the engine invoking **itself** as its own payload
(`captainHook mail digest`, decision 7: the strict parser, cursor atomicity,
and TTL arithmetic live in tested C#, not a shell script):

- `mail-digest-ambient` — `oneshot` on the turn-start seams
  (`SessionStart`, `UserPromptSubmit`): delivers everything pending for the
  role as one bounded `inject` digest.
- `mail-digest-urgent` — `resident` on `PostToolUse` with `--seam urgent`:
  mid-turn fires on every tool call, so only `urgent`-priority mail
  qualifies, the budget is a quarter of ambient's, and the process stays
  warm (a cold JIT start per tool call is the tax the AOT shim killed).

**The `--seam` flag is the registration declaring what CLASS of seam these
events are** — registration is configuration (ADR-0016 d5/d7): the planner
degrades against the harness's declared verbs, so a misclassified entry
noops rather than losing mail. Which events you register the digest on IS
your deployment's delivery capability; the Stop/reconcile seam is not wired
for claude-code yet (its spec declares no Stop effects — ADR-0016 phase 5).
Mail lands on the bus from anything that can run a process:
`printf '{...envelope...}' | captainHook mail send`.
