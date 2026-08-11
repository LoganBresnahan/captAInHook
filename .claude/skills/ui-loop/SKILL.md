---
name: ui-loop
description: The screenshot-driven loop for captAInHook GUI work — start a seeded, isolated preview daemon, rebuild the bundle on edit, capture every view in light and dark, and READ the pictures before calling a slice done. Use whenever you change anything under web/ (layout, tokens, panels, styles) or want to see what the GUI currently looks like.
---

# /ui-loop — build, look, iterate

GUI work fails in a way tests cannot catch: the specs stay green while the page
looks wrong. This loop gives an agent eyes (ADR-0015 decision 5). Everything
runs against a **sandboxed daemon** — its own runtime dir, trail, policy,
handlers, and payload scripts under a temp tree — so nothing here can touch the
operator's live `~/.captainHook` (CLAUDE.md's pollution warning).

Why not a Vite dev server: the daemon's Origin gate 403s a second origin by
design (ADR-0007), so the GUI is always served same-origin from the daemon's own
`/ui`. The loop is `vite build --watch` + reload, now with a camera.

## The loop

```sh
cd web
npm run snap                 # build → seeded daemon → every view × light/dark → web/.screens/
```

Then **Read the PNGs** in `web/.screens/`. That is the point of the skill — a
snap nobody looks at buys nothing.

While iterating on a change:

```sh
cd web
npm run dev                  # terminal A: vite build --watch → ../ui/
npm run snap -- --no-build   # terminal B: re-shoot against the watched bundle
```

`--no-build` skips the dotnet + npm build (a few seconds each); drop it after
touching C# or when in doubt.

### Options

| flag | effect |
| --- | --- |
| `--no-build` | skip engine + frontend build; use what's already staged |
| `--tag <name>` | prefix filenames (`--tag before` → `before-trace-dark.png`) |
| `--views a,b` | only these views (default: every `[data-nav]`, or `all` pre-sidebar) |
| `--themes light` | only these color schemes (default `light,dark`) |
| `--out <dir>` | write elsewhere (default `web/.screens/`, gitignored) |
| `--keep` | don't wipe the output dir first — how a before/after pair survives |

A before/after pair for a redesign:

```sh
npm run snap -- --tag before
# …make the change…
npm run snap -- --no-build --keep --tag after
```

## Driving it by hand

```sh
cd web
npm run ui:preview           # one persistent seeded daemon; prints the /ui#t= URL
```

Open the printed URL (the `#t=` fragment is the one-time token, scrubbed on
load), then type commands on its stdin:

| command | effect |
| --- | --- |
| `trail` | append the varied synthetic trail — **run this after the tab is open**; the SSE stream anchors at the end of the file, so lines written before the tab connected never reach it |
| `hook <event>` | fire one REAL hook through the daemon (`user-prompt-submit`, `pre-tool-use`, …) |
| `burst [n]` | append n trail lines — the perf/scroll pressure test |
| `url` | reprint the handoff URL |
| `quit` / Ctrl-C | drain the daemon, reclaim the sandbox |

## What the sandbox is seeded with

`web/scripts/seed.mjs`: three handlers (two oneshot, one resident with a
readiness protocol, one fail-closed), a policy with rules of every criterion
shape, a trail carrying every level and component the trace renders, and two
real fired hooks. Deliberately varied — a UI that only looks good on tidy data
is not done. Add to the seed rather than hand-editing a sandbox file, so the
next run shows the same thing.

## Reading a snap

Look for, in order: **overflow** (content escaping its container — the defect
ADR-0015 opens with), **hierarchy** (is the thing the page is *for* the thing
you see first?), **dead space**, **alignment** of columns and baselines, and
**dark mode** specifically (contrast, borders that vanish, colors that only
work on white). A design miss found in the snap is fixed inside the same slice
— the loop exists so ugliness never survives a commit.

## Ground truth

| what | where |
| --- | --- |
| sandbox lifecycle (spawn, isolation, readiness, drain) | `web/e2e/daemon.ts` — shared by the Playwright fixture, preview, and snap |
| seed data | `web/scripts/seed.mjs` |
| persistent preview | `web/scripts/preview.mjs` (`npm run ui:preview`) |
| screenshots | `web/scripts/snap.mjs` (`npm run snap`) → `web/.screens/` (gitignored) |
| e2e suite this shares its daemon with | `web/e2e/*.spec.ts` (`npm run e2e`) |
| decision record | [doc/adr/0015-gui-overhaul.md](../../../doc/adr/0015-gui-overhaul.md) d5 |
| GUI mechanics | [doc/flow/management-gui.md](../../../doc/flow/management-gui.md) |
