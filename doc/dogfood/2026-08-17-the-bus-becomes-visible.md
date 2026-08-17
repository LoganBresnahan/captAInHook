# Dogfood report — 2026-08-17 — the bus becomes visible (and the count comes to you)

Roadmap item 21 slice 7's field report. Two days of the Mail view on the
maintainer's real bus: the first exchange that did actual work (2026-08-16), the
defect that watching it exposed, and the day the picture stopped lying about
history. `captainHook mail status` (ADR-0017 d2, item 22 slice 1) deployed the
same afternoon and is included here, because the two channels were finally
observable side by side.

Everything below is the live tree — `~/.captainHook/mail/mail.jsonl`, the real
trail, the deployed build at `4c6fdf6` — not a fixture.

## What the bus actually carried

13 envelopes across three roles: `maintainer` 9, `reviewer` 2, `scribe` 2.
First line 2026-08-16T04:47:29Z, last 2026-08-17T16:18:32Z, chain intact, store
0700 / lines 0600, frontier 10071 B.

The three that mattered are the **review exchange** — the first time the bus
carried work rather than a demo:

| ts | to | id | kind/priority |
|---|---|---|---|
| 2026-08-16T16:04:50Z | reviewer | `review-1ee7218-request` | request/urgent |
| 2026-08-16T16:10:47Z | maintainer | `review-1ee7218-reply` | answer/urgent |
| 2026-08-16T16:16:04Z | reviewer | `review-1ee7218-addressed` | status/ambient |

A second window holding `reviewer` (a second `mail digest` registration,
cwd-scoped by `dispatch.json` — ADR-0016 d8 as built) reviewed commit `1ee7218`
and returned six findings, three of them real bugs in the SSE client: lost
frames on the stall path when the cursor was still null, a watchdog that armed
only after headers (so a pre-header hang — the exact Firefox symptom — had no
timer at all), and a starvation test against a lifetime frame count that could
only ever fire on the first connect. All three fixed in `d6083f9`. **The round
trip cost three envelopes and no human relay.**

## The defect watching it exposed, and the fix

For that entire day, every envelope behind a cursor read *before cursor · no
delivery record in this picture*. That sentence was honest — `delivered` comes
from a `mail.deliver` ledger line and nowhere else (ADR-0016 d14 pin iii), and a
live stream only starts *now* — but it was also the wrong impression about mail
that had demonstrably been read hours earlier. Graduated into slice 6a
(`6f67534`): the daemon folds `mail.deliver` out of its own trail into the
snapshot.

Live proof, from the deployed daemon this evening:

```
GET /api/v1/mail →  lines: 13   cursors: 7   frontier: 10071
                    chain ok: true   modes: 700/600
                    deliveries: 23   deliveriesComplete: true
```

23 delivery records, spanning **2026-08-16T04:57:07Z → 2026-08-17T17:22:16Z** —
about 37 hours of history, naming 75 envelope pickups — folded into a page that
had streamed nothing. `deliveriesComplete: true` because the whole trail fitted
inside the 4 MiB scan window, so "no record" on this bus currently means what it
says.

## The count, and where it has nowhere to go

`mail status` deployed and wired into `~/.claude/settings.json` as a
`statusLine`. It works: a terminal Claude Code session shows `📬 9 · 4 urgent`
under the input box, and the number drains as that window prompts.

**It does not render in the VS Code extension** — the maintainer's primary
window. That panel's chrome ends at the input box; there is no status line for a
command to fill. This is the "harness with no passive display" gap named in
ADR-0017's own consequences, and it turns out the first harness to hit it is the
one the maintainer uses most. The human channel is not wrong, it is *unplaced*:
in that window the surfaces that work are the digest (into the model's context,
at a seam) and the Mail view in `/ui`, which is harness-independent.

Cost of the other channel, measured over the same period: the ambient digest
handler runs on **every** `UserPromptSubmit` at median **75.0 ms** (n=92, min
52.5, max 420.6) — inside its 2000 ms budget with room, but it is a per-prompt
tax that the status line, which enters no loop at all, does not levy.

## Three readings of the same bus, all correct

Watching the canvas beside a fresh terminal produced an apparent contradiction
worth recording, because the next person will hit it too:

- the **canvas lane header** said `maintainer — 9 mail · 6 pending`
- a brand-new terminal's **status line** said `📬 9 · 4 urgent`
- this VS Code session's status was **blank**

All three are right. Pending is a property of a *cursor*, not a role: the two
oldest sessions are 6 behind, the fresh terminal had no cursor at all (so all 9
were unread for it), and the window that has been reading all day is caught up.
The lane header shows the most-pending reader, which is the honest summary but
is not any particular window's number.

Two things the canvas made visible that no count could:

- **Observation really is not delivery.** The terminal read its count for
  several minutes and created *no cursor* — no sixth track appeared on the lane.
  The track appeared at 16:21:30, on that session's first prompt, when its
  digest actually ran and took all 9 envelopes at once.
- **Dead cursors hold mail forever.** Of seven cursors on the bus, four belong
  to sessions that no longer exist; two of them sit at 6 pending and always
  will, because TTL burns on *delivery opportunities* and nobody is attending
  their seams. Nothing reaps them — `doctor` does not touch mail. This is what
  drove **ADR-0018** (instance addressing + the reaper) the same evening.

## A phantom reader consumed real mail

The trail shows a `mail.deliver` at **2026-08-17T15:25:43Z** for
`sessionId: "s1"` naming **7 envelopes** on `maintainer`. There is no such
session: `s1` is the fake id in `/shipshape`'s stdout-purity probe, which ran the
dev shim **bare** against the live `~/.captainHook`, fired the maintainer's own
registered digest, delivered seven live envelopes to nobody, and left
`cursor.maintainer.s1.json` on the real bus — a phantom track on a real lane.

Removed by hand; the skill was the cause and is fixed in `495bf5c` (the probe
now runs under the five sandbox env vars the e2e fixture uses, driven before
committing: trail inside the sandbox, live mail dir and trail unchanged). Two
lessons worth keeping: a verification step that dispatches through the
operator's own hooks is a *write* to their state no matter how read-only its
intent looks, and the bus made the intrusion visible in a way the trail alone
would not have — the phantom cursor was a track on a lane, at a glance.

3 of the 23 delivery records carry **no session at all** (a hookless caller
lands on the shared sessionless cursor). Harmless today with one such caller;
ADR-0018's problem the moment there are two.

## Verdict on slice 6b (the scrub bar)

The 2026-08-16 verdict was "build (a) then (b)". With (a) shipped, the evidence
for (b) has weakened: every question this pass actually asked — *did that
envelope get read, by whom, at which seam, and what is still waiting* — is
answered by the live view plus the preloaded records. Nothing yet has asked
*"what did the bus look like at 14:30?"*, which is the only question a scrub bar
answers.

ADR-0016's own slice-7 row anticipates exactly this (*"skippable if the live
view alone satisfies the field report"*). **Recommendation: defer 6b** until a
question needs it — the owner's call, recorded here rather than decided here.

## Watchlist

- **No passive surface in the VS Code extension.** The human channel works in
  the TUI and has nowhere to render in the maintainer's main window. Candidate
  answers: `/ui` in a Simple Browser tab (today, free), or a VS Code status-bar
  item (new surface area, new place). Do not let ADR-0017's watcher assume every
  harness has a bar.
- **Dead cursors accumulate** (4 of 7 already) and nothing reaps them →
  ADR-0018.
- **The digest is a per-prompt tax** (median 75 ms) that grows with the store:
  `Pending` re-reads the whole ledger, which is archival and unrotated
  (ADR-0016 N4). `mail status` pays the same cost per status-bar render.
- **A fresh window sees the whole unexpired backlog** on first contact — correct
  per d3/d4, but "9 unread" in a brand-new window feels different from "9
  arrived while you were away", and no surface distinguishes them.
