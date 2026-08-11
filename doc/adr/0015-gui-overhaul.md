# ADR-0015 — GUI overhaul: sidebar views, template-gallery authoring, and the screenshot-driven loop

**Status:** Accepted 2026-08-11 (owner direction the same day: authoring =
template gallery only, polished-terminal aesthetic, sidebar + views, policy
rule builder).
**Date:** 2026-08-11
**Amends:** [ADR-0008](0008-gui-v1-browser-ui.md) decision 1 (the five-screen
one-page presentation; the island *architecture* is untouched) and its d7 dev
loop (a screenshot harness joins the full-reload loop).
**Explicitly does NOT fire:** ADR-0011's provenance/sandboxing trigger — see
decision 3.

## Context

The GUI shipped observability-first (item 6) and works, but a headless drive
of the live `/ui` this session (screenshots, light + dark) showed it has both
defects and a UX ceiling:

- The handlers table **overflows its card** — columns clipped mid-word, the
  table spilling under the neighboring card. The auto-fit masonry leaves a
  viewport-scale dead region; the live trace — the product's flagship — is
  the last thing on the page.
- The whole authoring surface (install/edit/uninstall + verbatim confirm) is
  crammed into one ≥260px grid cell inside the supervision card. The form is
  structured and correct; it has no room and no starting help — a user facing
  it must already know what a payload script looks like.
- The dispatch-policy editor is a raw JSON textarea.
- The visual system is 9 color tokens and one font stack; every spacing,
  radius, and size is ad hoc per rule.

Separately, the owner wants this work done **agentically**: the agent drives
Playwright, screenshots its own output, and iterates on what it sees — which
requires tooling that does not exist yet (the e2e fixture tears its daemon
down per test; there is no persistent seeded preview).

## Decisions

1. **Sidebar + views, no router.** A persistent left nav (Trace / Handlers /
   Policy / Harnesses / Status); one view at a time; **Trace is the landing
   view**. Navigation is a `view` slice in the existing Zustand store — the
   six-island architecture (ADR-0008 d3) stays; each island renders `null`
   unless its view is active. No routing dependency (zero-new-deps culture);
   the SSE stream keeps feeding the store from outside React, so a hidden
   view loses nothing. URL/hash persistence of the active view is deferred —
   the `#t=` token scrub owns the fragment.

2. **A real token system, polished-terminal direction.** Type scale (mono for
   data, a UI face for chrome), spacing scale, radii, and semantic colors —
   light and dark both first-class, still via `prefers-color-scheme` (no
   toggle; the OS owns the preference). Dense-but-deliberate. The Playwright
   test hooks (`data-island`, `data-editor`, `data-field`, `data-verdict`,
   `data-enabled-state`) are load-bearing and survive the redesign.

3. **Authoring = a client-side template gallery; the script-write verb is
   REFUSED.** The gallery is curated starter payloads that pre-fill the
   existing install form and show the starter script with a copy button and
   save-to-disk instructions. The API stays reads + `PUT /policy` +
   `PUT /handlers`: a daemon verb that writes executable script files would
   be a new trust surface — exactly the "code the user cannot read" shape
   ADR-0011 deferred behind a future ADR — and the gallery makes it
   unnecessary for v1. The user saves the script; the GUI installs the entry;
   the verbatim confirm stays the gate.
   - Template scripts are **single-sourced from `examples/payloads/`** via
     Vite `?raw` imports (build-time inlining, no runtime fetch, no gen
     step); `web/src/templates.ts` carries the curated metadata. The gallery
     curates the *generic* demos (retriever, memory) plus **new generic
     starters added to `examples/payloads/`** (a `Decide` guard, an `Inject`
     context-puller, a `Background` side-effect logger, an LLM-backed
     skeleton); the maintainer's dogfood payloads are not templates.
   - The gallery and install form surface **per-event allowed effect verbs
     from `HarnessesDto.events`** — data the client already fetches and
     nothing displays today.

4. **Policy becomes a structured rule builder with a raw-JSON toggle.** Rows
   (event / handler / project / session / decision) compose to the exact
   strict JSON the textarea produces; the daemon's parser remains the only
   validator (422 violations render as today). **Round-trip guard:** a loaded
   policy containing anything the builder cannot represent locks the editor
   to raw mode with a notice — a hand-written policy is never lossily
   rewritten. ETag/If-Match flow unchanged.

5. **The screenshot-driven dev loop is a committed deliverable, not
   scaffolding.** The e2e fixture's daemon-sandbox logic (spawn, env
   isolation, api.json readiness, teardown) is extracted into a shared
   module consumed by three things: the Playwright fixture (unchanged
   behavior), `web/scripts/preview.mjs` (ONE persistent sandboxed daemon,
   seeded with handlers + policy + a varied trail, prints the `#t=` URL,
   cleans up on SIGINT), and `web/scripts/snap.mjs` (screenshots every view ×
   light/dark into a gitignored `web/.screens/`). A `.claude/skills/ui-loop`
   skill documents the loop: build → preview → edit → rebuild → snap → the
   agent READS the screenshots → iterate. The sandbox never touches the live
   `~/.captainHook` tree.

6. **The supervision card splits.** The handlers table + editor becomes the
   Handlers view (full width — the overflow defect dies with the cramped
   card); the daemon/supervision summary joins the Status view. Five views
   total; ADR-0008 d1's screen enumeration is amended accordingly.

## Rejected alternatives

| alternative | disposition |
|---|---|
| **Script-writing API verb** (`PUT /payloads/...`) | Refused for v1 — a new executable-write trust surface that fires ADR-0011's deferred trigger for a convenience the gallery provides. Revisit only with the catalog/registry work, behind its own ADR. |
| **A router dependency** (react-router etc.) | A store field does the whole job over the island architecture; a router buys URL persistence we deliberately defer. |
| **CSS framework / Tailwind** | 170 lines of CSS grow into a token system; a framework is a runtime-ish dep and a wholesale rewrite for no capability. |
| **Generated template pipeline** (templates from a gen step) | Metadata is hand-curated by nature; Vite `?raw` already single-sources the script text at build time. |
| **Virtualized trace list** (react-window etc.) | `React.memo` rows + `content-visibility: auto` handle TRACE_CAP=2000; a virtualization dep is the heavier tool. Measured via the preview daemon before acceptance. |
| **Vite dev server / HMR for the loop** | The daemon's Origin gate 403s a second origin by design (ADR-0007); the loop stays build-watch + full reload against the daemon's own `/ui`, now with screenshot eyes. |

## Consequences

- **N1 · e2e churn.** 14 specs assume every panel visible at once; navigation
  changes reachability. Mitigated: one shared `gotoView` helper, Trace as
  default landing (trace/session specs barely move), and the structure + spec
  updates land in ONE slice, atomically.
- **N2 · Committed-`ui/` noise.** Every slice rebuilds the hashed bundle.
  One `npm run build` + commit per slice, never mid-slice.
- **N3 · `?raw` imports cross `web/`'s boundary** into `examples/payloads/` —
  a build-time coupling the examples README must note (editing a template
  script changes the shipped GUI on next build).
- **N4 · Modal accessibility debt is paid in passing** — the verbatim-confirm
  dialog gains a focus trap, Esc-close, and focus restore while its file is
  open anyway (hand-rolled, no dep).
- **P1 · The loop outlives the project phase** — preview + snap become the
  standing way any session (human or agent) eyeballs GUI work before commit.

## Implementation plan

Eight slices, ordered; the loop lands first because it is the eyes for
everything after. Each slice verifies: web unit tests + e2e green, dotnet
suite green twice before commit, and a **screenshot read** (affected views ×
light/dark) before the slice is called done.

*Decomposed in-session by a Plan agent with this session's screenshots and
exploration in context; `/adr-plan` deliberately not re-run — the ordering is
near-linear (1 → 2 → {3,4,5,7} antichain → 6 anywhere after 2 → 8 terminal)
and effort is ranked below. Its one missing output is adopted here instead:*
**adversarial verify on exactly ONE slice — `policy-rule-builder`** *(a
round-trip bug silently rewrites a hand-written dispatch.json while every
screenshot looks fine — the only silent-destruction hazard in the plan; the
skeptic attacks builder ⇄ JSON in both directions plus the raw-lock guard).
Everything else fails visibly in e2e or the snap. The slice-1 fixture
extraction's guard is structural: all 14 existing specs must stay green
untouched. No ultracode.*

1. `screenshot-loop` — extract `e2e/daemon.ts` from `fixtures.ts` (fixture
   behavior identical, 14 specs untouched-green); `preview.mjs` (persistent
   seeded sandbox) + `snap.mjs` (views × themes → `web/.screens/`,
   gitignored); the `ui-loop` skill; baseline "before" screenshots. **M**
2. `tokens-and-sidebar` — the token system; shell restructure
   (`index.html`, `App.tsx` sidebar with `data-nav`, store `view` slice);
   per-island view gating; `gotoView` e2e helper + spec updates; the
   SSE-survives-view-switch regression test. **L — the risk slice, one
   atomic commit.**
3. `trace-landing` — full-height layout, aligned columns, sticky filter;
   perf: memoized rows + `content-visibility`, measured at cap via the
   preview daemon. **M**
4. `handlers-view` — table + editor extracted to the full-width Handlers
   view; supervision summary → Status; modal focus trap/Esc; the missing
   `readinessTimeoutMs` form field. **L**
5. `template-gallery` — new generic starters in `examples/payloads/`;
   `templates.ts` (`?raw` + metadata); gallery cards with per-event effect
   verbs; pre-fill + copy-script + save instructions; e2e: template →
   pre-filled form → install → row. **M**
6. `policy-rule-builder` — bidirectional rows ⇄ strict JSON in `policy.ts`
   (round-trip property tests first), raw toggle, unrepresentable → raw-lock;
   e2e over the existing save/412 pins. **L**
7. `status-harness-polish` — stat tiles; harness effect-verb matrix (shares
   the gallery's render helper). **S**
8. `docs-capstone` — this ADR's Ground truth back-filled;
   `doc/flow/management-gui.md` updated; ADR-0008 amendment notes; roadmap
   ticks. **S**

### Per-slice model / effort / verify recommendations

*(2026-08-11, owner-requested. Model names are the session aliases; the owner
switches per slice. Rationale in one clause each — the drivers are: does the
slice fail VISIBLY (snap catches it) or SILENTLY (needs a skeptic), and is
the work judgment-heavy (design, big restructure) or mechanical.)*

| # | slice | model | effort | verify |
|---|---|---|---|---|
| 1 | `screenshot-loop` | opus[1m] | medium | Structural: all 14 existing specs green UNTOUCHED (the extraction's whole contract); preview + snap driven once against the current UI; the baseline "before" captures read and kept. |
| 2 | `tokens-and-sidebar` | opus[1m] | **high** | Full e2e (every spec crosses the nav change) + the new SSE-survives-view-switch regression + snap ALL 5 views × 2 themes, read before commit. The risk slice — one atomic commit. |
| 3 | `trace-landing` | opus[1m] | medium | `trace.spec` + a MEASURED perf pass at TRACE_CAP=2000 via the preview daemon (seed cap, fireHook bursts, interaction stays fluid) + snap. |
| 4 | `handlers-view` | opus[1m] | **high** | `handlers.spec` extended (Esc-closes, focus-trap smoke, `readinessTimeoutMs` round-trip through PUT) + snap. Big-file restructure: diff-review the extraction before commit. |
| 5 | `template-gallery` | opus[1m] | medium | Unit: template→form mapping + per-event verb derivation. E2e: pick template → `data-field` pre-fill asserts → install → row. Each NEW starter script smoke-RUN through the preview daemon's fireHook (they are executable examples, not prose). Snap. |
| 6 | `policy-rule-builder` | opus[1m] build; **fable skeptic pass** | **high** | The ADR's one adversarial verify: round-trip property tests FIRST (build→serialize→parse→same rows; unrepresentable→raw-lock), then an independent skeptic attacks builder ⇄ JSON both directions + the raw-lock guard + the 412 path. E2e over the existing save/412 pins. |
| 7 | `status-harness-polish` | sonnet or opus | low | `panels.spec` + snap both themes. Fails visibly; cheapest slice. |
| 8 | `docs-capstone` | opus[1m] | medium | Shipshape-style pass: every symbol/file named in this ADR's Ground truth and the flow doc cross-checked against code; final full snap set as the visual record. |

The `fable` skeptic on slice 6 mirrors the session pattern that produced this
ADR (build on opus, review on fable) — an independent model attacking the one
silent-destruction hazard is worth more than a same-model re-read. If a
slice's snap read reveals a DESIGN miss (not a defect), iterate within the
slice rather than deferring — the loop exists so ugliness never survives a
commit.

## Ground truth

*To be back-filled as slices land, per house convention.*

| decision | lives in |
|---|---|
| d1 view slice + sidebar | pending slice 2 |
| d2 tokens | pending slice 2 (`web/src/styles.css`) |
| d3 gallery + refusal | pending slice 5 (`web/src/templates.ts`) |
| d4 rule builder | pending slice 6 (`web/src/policy.ts`) |
| d5 loop | pending slice 1 (`web/e2e/daemon.ts`, `web/scripts/`, `.claude/skills/ui-loop/`) |
| d6 view split | pending slice 4 |
