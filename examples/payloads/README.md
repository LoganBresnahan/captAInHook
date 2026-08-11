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

⚠ **These four are also the GUI's template gallery** (ADR-0015 d3): `web/`
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
