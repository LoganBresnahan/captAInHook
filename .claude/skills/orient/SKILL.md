---
name: orient
description: Take a bearing at the start of a new captAInHook context window. Read the recent commits and the roadmap to reconstruct what shipped and what's next, follow the commit trail into whichever docs it points at, then reconcile that ground truth against remembered state (MEMORY.md, cavemem) and flag any drift. Read-only — it briefs, it does not write memory or change code. Run when opening a fresh session or whenever you've lost the thread of where the project stands.
---

# /orient — take a bearing at session start

A new context window starts blind to *where we are*. This reconstructs it from
the sources that don't auto-load: the commits, the roadmap, and the docs they
point at — then checks that what you already remember still matches reality.

**Read-only.** The deliverable is a briefing, not actions. It does **not** edit
code and does **not** write memory — cavemem and MEMORY.md own writing; orient
only reads and reconciles them. Assumes cwd = the captAInHook repo root (same as
`/shipshape`).

## The context sources — and which lane is orient's

Four sources, two trust classes. The first two are already in your context; the
last two are not — reading and reconciling them is this skill's whole job.

| Source | Holds | Trust | Loaded |
| --- | --- | --- | --- |
| CLAUDE.md | how-to-work rules | authoritative, static | auto |
| MEMORY.md + `memory/*.md` | curated durable facts | *as-of-write-time* — can drift | auto |
| **cavemem** (MCP) | narrative / the *why*, cross-session | *as-of-write-time*, richer | **on-demand** |
| **git + roadmap + docs** | what the code *is* / the plan *says* | authoritative, **current** | **must be read** |

Rules that keep the lanes disjoint (don't duplicate work that's already done):

- **Don't re-summarize CLAUDE.md or MEMORY.md** — they're in context already.
  Add the *delta* (what changed since last session) and the *reconciliation*
  (does memory still match ground truth?).
- **git = what shipped. cavemem = what was discussed/decided.** Complementary,
  not redundant: a commit says *what* changed, never *why*. Reach into cavemem
  only for a *why* the commits don't carry.
- **orient never writes memory.** If a session-start memory recall (cavemem is
  itself SessionStart/Stop-shaped — roadmap item 9) already injected the
  narrative, don't repeat it — reconstruct the *ground truth* it can't.

## 1. What shipped — read the commits

```bash
git log --oneline -12
git status --short
```

Read back only until the arc is coherent — usually the last 5–10 commits, not
all of history. You're answering three things: what's the last known-good state,
what landed most recently, and is there uncommitted WIP on the floor.

## 2. Where we meant to be — the roadmap

Read `doc/roadmap.md` (the **Now** and **Next** sections). Map the shipped
commits onto checked items. The **frontier** = the first unchecked item under
Now, else the first under Next. Note any carry-ins pinned to upcoming items
(e.g. the daemon item's ADR-0002 carry-ins) — those are the traps waiting on the
next task.

## 3. Reconcile ground truth against memory  ← the cavemem step

MEMORY.md is already in your context. For every remembered claim that names a
concrete artifact — a file, symbol, flag, or roadmap item — **verify it against
what git / roadmap / code show now.** Memories reflect what was true when
written; flag any drift explicitly rather than trusting them.

Then, and only then, reach for the *why* the commits don't carry:

```
cavemem search "<topic the commit raised>"   → get_observations(ids)   # targeted
# or replay the last session's decisions:
cavemem list_sessions → timeline(session_id) → get_observations(ids)
```

Query cavemem against a **specific question the commits raised** — never dump
it. cavemem is the narrative across sessions; git is what shipped. Use cavemem
to recover a decision's rationale or a thread that never became a commit — not
to re-establish state the commits already prove.

## 4. Follow the trail into docs (on demand)

Commit messages point at docs — "ADR-0002", "flow", "roadmap". Read the
*specific* referenced doc **only when the next move touches it** (about to start
the daemon item → read ADR-0002's Revisit triggers and the roadmap carry-ins;
touching dispatch → read `doc/flow/hook-dispatch.md`). Same on-demand rule as
cavemem: pulled by a question, not read by default.

## 5. The bearing (report)

```
ORIENT — captAInHook @ <branch> <sha>
  shipped     <recent arc in one line>  · last good: <commit>
  wip         <uncommitted files, or "clean">
  roadmap     frontier → item <N> "<title>"  (done: <range>)
  drift       <remembered-vs-actual mismatch, or "none">
  next move   <the obvious task> — read <the one doc> first
```

Keep it to that shape. The point is a fast "you are here" that lets work resume
in one turn — not a re-run of the project's whole history.
