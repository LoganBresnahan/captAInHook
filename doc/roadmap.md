# Roadmap

Living document — what to build and in what order. Decisions get ADRs
(`doc/adr/`), mechanics get flow docs (`doc/flow/`); this file only orders the
work. Check items off in the commit that lands them; reorder freely.

**The product vision this points at:** a runtime for managing custom
hooks/skills for AI agents — browse, one-click install (writes
`~/.claude/settings.json` / `.claude/skills/`), configure, and *watch them
run live*. The framework underneath is what exists today.

## Now

- [x] **19. GUI overhaul — sidebar views, template-gallery authoring, the
  screenshot loop** — the GUI works but has a visible defect (handlers table
  overflows its card), a broken one-page layout (trace buried, dead space),
  a cramped authoring surface, and a raw-JSON policy editor. Per **ADR-0015**
  (accepted 2026-08-11): sidebar + one-view-at-a-time over the untouched
  island architecture (a store `view` slice, no router; Trace lands first);
  a real token system, polished-terminal direction, both themes; authoring =
  a client-side **template gallery** single-sourced from `examples/payloads/`
  via Vite `?raw` — the script-write API verb explicitly REFUSED (ADR-0011's
  trigger stays unfired); policy rule builder + raw toggle with the
  round-trip guard; and the **screenshot-driven agentic loop as a committed
  deliverable** (extracted daemon sandbox → `preview.mjs`/`snap.mjs` + the
  `ui-loop` skill) — landed FIRST, since it is the eyes for the rest.
  Build order: ADR-0015 § Implementation plan (8 slices; the risk slice is
  `tokens-and-sidebar`, whose nav change churns all 14 e2e specs in one
  atomic commit). API surface frozen throughout. Tick slices here as they
  land.
  Slices landed: `status-harness-polish` (2026-08-11; slice 7 — the two read
  views were flat in the same way: everything at one weight, so nothing was the
  point. **Status gains a hierarchy rather than more tiles.** Identity and pid
  become a text STRIP — a content hash is read and compared against a deploy,
  not a metric that moves, and tile chrome made seven equal boxes whose seventh
  wrapped alone onto its own row; the tiles now carry only what CHANGES, with
  `served` leading at a new `--fs-2xl` and in flight / background / open streams
  beside it. Every tile owns a NOTE saying what the number means now (`idle`,
  `none pending`) and, for supervision, what it COSTS — an escalated worker
  reads "asks fail fast — see Handlers" (ADR-0004 d5), because a count does not
  tell an operator what it bought them. A toned value ships a glyph as well as
  the color (color is never the only channel carrying a state), and the grid
  stretches so a row's notes share a baseline. **Harnesses becomes a real
  MATRIX**: the chips read `Stop (0)` — a count, with the verbs themselves
  hidden in a hover title — which answered a question nobody has; events are now
  rows, declared verbs are columns, and a cell says yes or no. Columns are
  DERIVED from the spec (`verbColumns`), never hardcoded, per ADR-0003's
  declare-in-data rule: a fixed list would render a future verb as a silent
  blank, the harness permitting what the matrix denies. Permitted cells take the
  ACCENT, deliberately not the reserved ok/warn/bad palette — permission is a
  capability, not a health state, and borrowing health colors here would cheapen
  them one view over; every cell's yes/no also exists as `.sr-only` text, and an
  event declaring nothing gets one spanning "no loop effects" cell rather than an
  all-blank row that reads as missing data. `verbsLabel`/`effectLandsOn` are
  SHARED with the gallery per the ADR rather than re-spelled, which surfaced a
  real inconsistency in the old inline code: it collapsed three cases into two,
  claiming "no loop effects" for an event the registry never declared —
  the same false accusation `effectLandsOn` already refuses to make. Now
  declared / declared-empty / not-declared are three sentences.
  Three design misses the snap read caught and fixed IN-slice: ragged tile
  heights (`align-items: start` let each size to content), a lead tile spanning
  two columns so the number floated in dead space, and an over-long note that
  wrapped and made its tile the tallest. 98 units (from 94) + 28 e2e (from 27);
  all four views snapped in both themes and read.)
  `policy-rule-builder` **COMPLETE — build + skeptic pass**
  (built 2026-08-12 on opus, tests first per the plan; the ADR's one
  adversarial-verify slice, its independent `fable` skeptic pass run
  2026-08-11 — builder ⇄ JSON attacked both directions, the raw-lock guard,
  and the 412 path. **Two real finds, both fixed in-pass.** (1) A DAEMON
  contract breach the round-trip guard could never see: `JsonDocument` defers
  string unescaping, so a lone-surrogate escape (`"\ud800"`) in a criterion
  parses as a valid document and throws `InvalidOperationException` at
  `GetString` — not `JsonException` at `Parse` — escaping
  `PolicyResolution.Resolve`'s documented never-throws contract on the
  dispatch hot path, and reaching `ApiPolicyWriter.Write` as an opaque 500
  instead of a 422; fixed with `TryReadString` at every string-read site in
  `DispatchPolicy` and pinned at all three layers (TryParse violation, Resolve
  ⇒ Malformed, Write ⇒ Invalid). The same deferred-unescape pattern in
  `Harness.cs`/`ExecHandlersFile.cs`/`ExecWire.cs` is recorded in scratch as a
  follow-up sweep. (2) The raw → Rules switch SILENTLY DISCARDED raw edits:
  it reverted to the pre-toggle rows and Save then wrote those — draft-level
  silent destruction wearing an intentional-looking write; the switch now
  ADOPTS the raw draft into rows, or refuses with a
  `data-draft-unrepresentable` notice leaving the text exactly as typed.
  Tightened in-pass: the client criterion check now mirrors the daemon
  exactly (whitespace-only and lone-surrogate criteria lock to raw —
  `IsNullOrWhiteSpace` + a `\p{Surrogate}` well-formedness probe), and the
  gate assumption "the builder only ever sees daemon-accepted text" was
  EMPIRICALLY verified: version `1.0`/`1e0` (which `JSON.parse` collapses to
  1) are malformed daemon-side, now pinned. Survived attack unbroken: the
  round-trip meaning guard, duplicate-key reasoning, `__proto__`/`constructor`
  keys (locked), first-save-create with null etag, and 412 `current: null`
  (file deleted ⇒ retry is a deliberate unprotected create, now unit-pinned).
  Coverage the pass added: the 412 path driven END TO END for the first time —
  real daemon, real content-hash ETag: conflicting disk edit ⇒ mismatch with
  nothing written, retry with the adopted tag overwrites deliberately — plus
  e2e pins for raw-edits-carry-into-builder and the refused switch. Units 94
  (from 91), e2e 27 (from 24), dotnet 648 green twice; policy view snapped
  both themes and read.) The hazard is silent
  destruction — a builder that drops a field it did not understand leaves the
  page looking right while the daemon enforces something the user never wrote,
  invisible to every screenshot and every green e2e — so representability is
  decided by ROUND TRIP rather than by a checklist: `parsePolicyRows` builds
  rows, re-serializes, and compares the MEANING of the result against the input
  (`default` absent ⇒ allow and `rules` absent ⇒ none are the only
  normalizations, and they are the dialect's own). Anything that fails —
  including a shape nobody enumerated — returns null and the island LOCKS to
  raw with a notice. A malformed file never reaches the builder at all: the
  daemon could not read it, which is exactly when a lossy rewrite would be most
  destructive; that gate is also why duplicate JSON keys need no client
  detector (`JSON.parse` collapses them, but such a file is malformed
  server-side). The user's own spelling survives — `user-prompt-submit` stays
  kebab in the row and in the file, since the daemon canonicalizes at parse and
  the GUI has no business rewriting text behind a user's back. UI: default
  decision + numbered, reorderable rows (first-match-wins makes order
  load-bearing, so it is visible and editable), a Raw JSON toggle that shows
  EXACTLY what Save would write (the rows are the document — no second
  serializer), and the daemon left as the only validator of legality (its 422
  renders as before). 29 property/round-trip tests written BEFORE the
  implementation (91 units total, from 62), including a 300-case deterministic
  generator for rows → JSON → rows identity and a 15-case raw-lock table; e2e
  24 (from 20) with four new builder pins — compose-and-save, reorder, a
  hand-written policy loading into rows and saving back byte-equivalent, and
  the raw-lock refusing to touch a malformed file. One snap-read fix: a blanket
  `[data-island="policy"] button` rule from when the island had exactly one
  button was painting the mode toggle and every row action as a primary call to
  action. dotnet 642 green twice.)
  `template-gallery` (2026-08-11; d3 — authoring without a
  script-writing verb. **ADR-0011's provenance trigger stays UNFIRED**: the
  daemon gains no ability to write an executable; the gallery shows the whole
  script, says where to save it, and pre-fills the install form, leaving the
  verbatim confirm as the gate. Four new GENERIC starters in
  `examples/payloads/` — one per verb, written to be copied: `starter-inject`
  (note file + the session's git branch, read from the ENVELOPE's cwd — the
  first cut ran git in the daemon's own directory, which the smoke test
  caught), `starter-decide` (a gate that can deny, with the fail-mode choice
  spelled out), `starter-side-effect` (documents that `background` is NOT an
  exec verb — that is an in-process handler's word to the engine; the exec
  equivalent is work + `noop`), and `starter-llm` (the DESIGN.md thesis in 60
  lines: a second model spliced into the loop, carrying ADR-0010 N7's
  `--setting-sources ""` reentrancy guard and a degrade-to-noop path). All four
  **smoke-run through a real daemon** per the plan — Decide/Noop/Noop/Inject on
  the wire, the side-effect's stderr in the trail, and a stub `claude` that
  EXITS NONZERO if the reentrancy guard is missing, so the guard is proven
  passed rather than merely present; the degrade path re-run with an empty
  PATH. Script text is single-sourced via Vite `?raw` from
  `templateScripts.ts` — split from `templates.ts` because `node --test` (zero
  deps, by design) cannot resolve a query suffix — and a unit test reads each
  template's file off DISK asserting it exists, is `#!/bin/sh`, and is
  executable, which is the only thing that would catch a template pointing at a
  moved file. The save path is derived from what the daemon reports, in order:
  the handlers.json directory (its real runtime home, sandbox included), then
  the shim's deploy home, then NOTHING — a wrong absolute path is worse than an
  empty field, since the daemon would accept it and the handler would silently
  never run (the e2e found this: no shim is staged in the fixture, so the
  shim-only derivation produced no path at all). Gallery + form now surface the
  per-event effect verbs from `HarnessesDto.events` — data the client always
  fetched and never showed — so "why did my decide do nothing on Stop?" is
  answered on screen (Stop: "no loop effects"). 62 units (48 → 62) + 20 e2e
  green; dotnet 642 green twice.)
  `handlers-view` (2026-08-11; d6's split. `SupervisionPanel` →
  `HandlersPanel` (`git mv`, island `data-island="supervision"` →
  `"handlers"`, so `gotoView` loses its last special case): the registered
  table + the handlers.json editor now own a full-width view under two
  headings, and the daemon-wide SUMMARY — registrations / escalated / restarts
  / resident children — moves to Status, where the other health numbers live.
  `restarts` is derived rather than reported: a worker's generation starts at 1,
  so restarts survived is Σ(generation − 1) across registrations (ADR-0002's
  model), reading 0 on a healthy daemon. **N4's modal accessibility debt paid**:
  the verbatim confirm — ADR-0011's trust surface, the screen where a user
  consents to running a process as themselves — gains a hand-rolled focus trap
  (no dep), Esc-to-cancel, and focus RESTORE to the opener; pinned by an e2e
  that tabs 12 times asserting focus never leaves the dialog, shift-tabs off the
  first element, then Escapes and asserts the opener is focused and nothing was
  written. **`readinessTimeoutMs` is settable at last**: `ExecEntry` already
  carried it (so a hand-written value round-tripped through an edit) but the
  form could not set it — now a labelled field, shown in the verbatim confirm
  for resident entries, pinned end-to-end form → confirm → file (typed as a
  NUMBER) → back into the edit form. Two snap-read fixes in-slice: the consent
  button was visually identical to Cancel on a trust surface (now primary), and
  the 11-field form was one tall column taller than the viewport — the ADR's
  "cramped authoring surface" — now a responsive 2–3 column grid with the wide
  inputs spanning. Extraction diff reviewed before commit per the plan: pure
  rename + island rename + section headings, no logic moved. 19 e2e (16 → 19)
  + 48 units green; dotnet 642 green twice.)
  `trace-landing` (2026-08-11; the landing view stops being a
  document and becomes a FRAME: shell `100vh`, the view region a definite-height
  flex column, the `createRoot` mount divs `display: contents` so they leave the
  box tree entirely (four empty divs would otherwise share the height and break
  the flex chain), and the trace card takes the rest with its `<ol>` scrolling
  inside itself — which is why the filter and stream badge are always on screen
  with no `position: sticky` anywhere. Rows became a CSS grid with every cell
  ALWAYS rendered (empty when the field is absent), so a line without `durMs`
  no longer slides every later column left. Perf per the ADR's rejected
  alternative (no virtualization dep): `React.memo` on the row + a stable
  useState-setter callback, plus `content-visibility: auto` with an intrinsic
  size. **Measured, not asserted** — `web/scripts/perf.mjs` (`npm run perf`,
  committed as the standing evidence) drives a REAL seeded daemon to
  TRACE_CAP=2000: zero long tasks while 200 lines stream in at the cap, filter
  keystroke → filtered render p50 33ms, and the heaviest operation — clearing
  the filter, re-rendering every row — 88ms WITH content-visibility vs 135ms
  without. Append→visible is 200ms and the script says plainly that this is the
  SERVER's trail stat-poll beat (TrailTail.cs), not rendering. Two measurement
  traps found and documented in the harness: a rAF scroll loop reports ~16.7ms
  whatever the work (capped) and scrolling a container invalidates no layout, so
  both scroll probes read "fine" even when nothing is; and the first cut of the
  harness reported a NEGATIVE duration — the wall clock again — so every timing
  is `performance.now()`. Also fixed a real defect in slice 1's loop that using
  it exposed: `--no-build` skipped STAGING as well as compiling, so the
  documented `npm run dev` + `snap --no-build` loop silently screenshotted the
  previous build; `buildAndStage` split into `build()` + `stageUi()` and both
  scripts always stage. 16 e2e + 48 units green; dotnet 642 green twice.)
  `tokens-and-sidebar` (2026-08-11; the risk slice, one atomic
  commit. A store `view` slice + `VIEWS`/`VIEW_LABELS` is the WHOLE of
  navigation — no router (ADR-0015 d1): the console rail writes `view`, every
  screen island returns null unless `view` names it, and the gate sits AFTER
  each island's hooks so a hidden panel keeps polling and returns current
  rather than blank. Rendering null does not unmount, so the policy editor's
  unsaved draft and pending If-Match tag survive a view trip — the thing a
  router would have thrown away. Token system in `styles.css` (color / type /
  space / radius / depth, light + dark both first-class via
  `prefers-color-scheme`, no toggle; the console rail is deliberately dark in
  BOTH themes, which is what gives the light theme its hierarchy). e2e churn
  landed as the ADR predicted: ONE `gotoView` helper in `fixtures.ts` that
  every spec navigates through, plus the new `nav.spec.ts` — exactly-one-view
  and the regression that matters, **the SSE stream surviving a view switch**
  (lines appended while Trace is off-screen are all there on return, in order,
  and the same stream keeps feeding). 14 specs → 16, green first try after the
  restructure. Two design misses the snap read caught and fixed IN-slice: the
  Policy island floated uncarded while every other view was carded, and — the
  real one — a bookmark visit (`/ui` with no token) showed a BLANK region beside
  a nav that did nothing, with the only instruction buried in 11px rail text;
  the rail now states the session tersely and a `SessionNotice` island fills the
  region with the actionable `captainHook ui` copy (`shell.spec` asserts it
  where a visitor actually looks). The handlers-table overflow the ADR opens
  with died here as a side effect — full-width views gave it room. Snapped all
  5 views × 2 themes + both no-session states and read before commit; frontend
  units 48 green; dotnet suite 642 green twice.)
  `screenshot-loop` (2026-08-11; the eyes, landed first. The
  e2e fixture's daemon sandbox — spawn, env isolation, api.json readiness,
  drain-by-PID, reclaim — extracted verbatim to `web/e2e/daemon.ts` and now
  shared by THREE consumers: the Playwright fixture (`fixtures.ts` is just the
  binding), `scripts/preview.mjs` (ONE persistent seeded daemon printing its
  `/ui#t=` URL, with `trail`/`hook`/`burst`/`url`/`quit` on stdin), and
  `scripts/snap.mjs` (every view × light/dark → gitignored `web/.screens/`).
  What the tests see and what the camera sees therefore cannot drift. View
  discovery is dynamic — `[data-nav]` items if present, else the one-page
  shell as view `all` — so the script starts finding views the moment slice 2
  lands, unchanged. `scripts/seed.mjs` fills the sandbox (and ONLY the
  sandbox — payload scripts are written into it, never pointed at
  `examples/payloads/`, which read/write the operator's home) with three
  handlers, a policy exercising every criterion shape, a varied trail, and two
  real fired hooks. The first baseline capture paid for itself twice: the
  seeded payloads' answers were caught violating the exec wire grammar
  (`inject` takes `text`, `decide` takes `verdict`), and the trace screenshot
  came back EMPTY — an SSE subscription anchors at the END of the trail
  (ADR-0007 d5), so the seed had to split into `seedFiles` (before start) and
  `seedTrail` (after the page is live, per context). The `ui-loop` skill
  documents the loop and what to look for in a snap. Extraction contract met:
  the 14 existing specs stay green UNTOUCHED (one random per-run flake, proven
  pre-existing by running the un-extracted fixture on the same machine —
  a transient then still blamed on daemon warm — **run down and fixed the same
  day**: it was never the daemon. Sampling `CLOCK_REALTIME` against the
  monotonic clock caught **WSL2 stepping the wall clock +86.4s while monotonic
  advanced 101ms**, then back; strace of a "stalled" daemon showed an 86.30s
  gap with zero syscalls and every thread parked, and one probe measured a
  NEGATIVE start-to-ready. The fixture's readiness deadline was `Date.now()`-
  based, so a forward step expired 40s against a healthy daemon — the harness
  violating house invariant 2 while the engine honors it everywhere
  (`DateTime.UtcNow` survives only in log timestamps and the opt-in
  `ColdStartProbe` boot delta). Deadline now `performance.now()`; e2e green
  three runs straight with zero flakes, from 1 flake in every run before.
  Recorded in doc/platform.md § Wall-clock steps, with the misdiagnosis
  corrected in `playwright.config.ts` and the GUI flow doc).
  Baselines captured in both themes and read: they show exactly the defects
  the ADR opens with — the handlers table clipped at its card edge, a dead
  left column, and the live trace 3400px down the page. dotnet suite 642 green
  twice.)
  `docs-capstone` (2026-08-11; slice 8, the close. ADR-0015's Ground truth
  back-filled decision→code, ADR-0008 given the amendment notes it lacked —
  its header, its d1 screen table, and its d7 loop now say what ADR-0015
  changed, so a reader landing on the older ADR is not silently misled (only
  one inline table row had mentioned it). The verify was MECHANICAL rather
  than a re-read: a throwaway checker parsed every Ground truth table in the
  flow doc + both ADRs and asserted each named file exists and each backticked
  symbol appears in that row's files — 140 file refs and 123 symbols across
  the three, with the sole flag being ADR-0008's deliberately historical
  `SupervisionPanel.tsx`, which its own row already annotates as renamed. The
  weak spots of substring matching (`build`, `stageUi`, `VIEWS`) were then
  hand-checked. **Item 19 complete: all 8 slices landed.**)
  **Item complete 2026-08-11** — five sidebar views over the untouched island
  architecture, a token system in both themes, the trace holding 2000 rows
  with measured evidence instead of a virtualization dep, authoring by
  template gallery with ADR-0011's script-write trigger deliberately UNFIRED,
  a policy rule builder whose representability is decided by round trip and
  which survived an independent skeptic pass, and — landed first, because it
  is the eyes for the rest — the screenshot loop as a committed deliverable.
  Final tally: 98 frontend units, 28 e2e, dotnet 648 green twice; all five
  views snapped in both themes as the visual record. NOT deployed yet: the
  live `~/.captainHook/bin` still runs the pre-overhaul build, so `/deploy`
  is the next GUI-facing action whenever the maintainer wants the new shell
  on their own daemon.

- [x] **18. Own the spawn seam: bosun** — the exec-handler kill discipline stops
  renting `setsid(1)` from the host's package set. Per **ADR-0014** (accepted
  2026-08-11, out of the ADR-0013 language analysis — which measured the Go
  rewrite's premise and collapsed to "the only cross-OS defect a native
  component uniquely fixes is the spawn seam"; the owner withdrew that ADR's
  Windows decision the same day, so targets stay Linux + macOS + containers):
  `bosun` — ~130 lines of Zig in its own MIT repo — becomes the spawn prefix,
  ending the stock-macOS `pgroup=false` baseline (ADR-0012 N2's ⚠) and the
  dependency on the host having util-linux at all.
  Slices landed: `bosun-resolution-seam` (2026-08-11; d1/d2 — `ProcessGroup`
  grows a `SpawnPrefix` record and a `Resolve(baseDir, PATH)` that picks
  co-located bosun → PATH setsid → degrade, replacing the bare setsid probe;
  the ARGV differs per rung (bosun requires a literal `--` before the command,
  setsid takes it bare) so `BuildPsi` assembles from the prefix and takes an
  optional prefix for test injection — the deploy rung is never the rung a
  test tree resolves; the winner rides every `exec.spawn` as
  `spawner=bosun|setsid|none` in BOTH modes, the degrade keeping its existing
  `pgroup=false`. The 22 `SetsidPath is null` guards became rung-explicit.
  11 tests (`SpawnPrefixTests`): rung order — co-located bosun beats an
  available setsid, which is the whole decision — non-executable
  fall-through, PATH scan order, both-absent degrade-not-throw, null inputs,
  live-prefix self-consistency, per-rung argv literals, and the `--` contract
  driven through a REAL in-place exec against a wrapper that enforces it
  (pid == pgid asserted), so a dropped terminator fails in the suite rather
  than on a live deploy. Suite 626 → 637 green twice.)
  `deploy-fetch-and-stage` (2026-08-11; d3/d4 — bosun is the FOURTH deploy
  artifact, staged and swapped with the other three and rolled back by the
  same `bin.prev`. `/deploy` § 1a fetches the PINNED release asset (tag +
  target) and verifies it against the published `SHA256SUMS` before staging —
  never "latest", because an unpinned binary would change deploy bytes behind
  unchanged source and build determinism cannot tolerate that; native ⇒ no
  MVID ⇒ invisible to content identity by the `captainShim` rule, so a
  bosun-only swap doesn't roll the socket identity and takes effect on the
  next spawn. Verification treats a missing co-located bosun as a STAGING
  DEFECT (the first rung must win on a live deploy) and reads `spawner=bosun`
  off the trail. Pin: **bosun v0.1.0**, cut 2026-08-11 from its own CI (zig
  test → smoke suite → four cross-compiled targets → `SHA256SUMS`); the four
  digests are recorded IN the deploy skill, not merely fetched beside the
  binary, so the pin means *these bytes* rather than "whatever that tag serves
  now". Fetch + verify + run driven for real: checksum OK, 28 KB static ELF.
  **Decisive drive** (2026-08-11): the real v0.1.0 binary staged co-located
  with the dev engine, then a collapsed hook through the REAL resolver and
  dispatcher — trail `spawner=bosun`, no `pgroup=false`, and the payload
  reported `pid=125115 pgid=125115`, i.e. exec-in-place with its own group.)
  `docs` (2026-08-11; platform.md § Process groups — the fork→exec window no
  managed runtime exposes, exec-in-place pid identity, bosun's macOS
  `setsid()`, the ⚠ retired and the per-OS summary gaining a process-groups
  row; `doc/flow/exec-payloads.md` § The spawn prefix (rung ladder + argv +
  why a wrapper at all); ADR-0010 d6 amendment note; README's
  `brew install util-linux` nice-to-have replaced by the staged-deploy story
  ADR-0014 rejected it for.)
  **Deployed 2026-08-11 and dogfooding live**: `/deploy` staged all four
  artifacts (the fourth for the first time) and swapped them together — pin
  verified OK, zero `shim.wireSkew`, warm hook 15–19ms, `/ui` serving, doctor
  leaving one healthy daemon on the new identity. The rung is in force on the
  maintainer's OWN hooks: every `exec.spawn` from real tool calls carries
  `spawner=bosun` with no `pgroup=false`, in BOTH modes (oneshot
  `deploy-guard`, resident `doc-pointer`), and the live resident child sits at
  pid 164980 == pgid 164980 — exec-in-place, its own group, so a `kill(-pgid)`
  would take any grandchild it spawns. d5 (`--pdeathsig`) stays DEFERRED —
  it fires on the parent's forking THREAD exiting, and the engine spawns from
  pooled threads, so it would kill healthy payloads; revisit when a dedicated
  long-lived spawn thread exists or orphan pressure exceeds `doctor`.

- [x] **17. Dogfood findings: queued-ask fast-fail + payload reentrancy** —
  the first LLM-backed payload (orient-brief, field report
  `doc/dogfood/2026-07-21-llm-payload-and-a-find.md`) surfaced two engine
  truths when its `claude -p` child transitively fired the handler's own
  event. Landed 2026-07-27:
  (a) **a worker restart strands queued asks** — an ask enqueued in a mailbox
  the supervisor then supersedes is never dequeued (F#'s `MailboxProcessor`
  can't be drained), so the classified ask burned the FULL budget before
  guessing `backlogged` (observed: a 20s SessionStart stall for a dispatch
  whose script never spawned). Fix as built: each `ActorRef` instance carries
  an **abort** `TaskCompletionSource` that `Swap`/`MarkDead` completes on
  supersession; `current`+`epoch` fold into ONE atomically-swapped `Instance`
  record so the ask reads a consistent (mailbox, epoch, abort) triple; the
  classified ask races reply / abort / window and returns a new sixth status
  **`Abandoned`** (fast-fail, uncounted, distinct trail classification
  `abandoned`). Genuine backlog — instance still alive — is unchanged, keyed
  apart by `abortedTask.IsCompleted`. Pinned by `WorkerAbandonedAskTests`
  (incident-shaped fast-fail + a backlog-stays-backlogged split guard); the
  dispatcher-level `ClassificationTests` backlog case was revealed to have
  ALWAYS been an abandonment (d1's cancel-restart strands d2) and now asserts
  `abandoned`. Recorded as the **ADR-0004 d5 amendment** (2026-07-27), flow
  doc `doc/flow/actor-supervision.md` (six-status table + the swap section).
  (b) **payload reentrancy self-blocks** — engine-side detection isn't
  feasible (the re-entering `claude` mints its own dispatchId; stripped env
  means no depth marker survives the socket), so it's a documented
  payload-authoring constraint: *a payload must not transitively fire its own
  event* — `--setting-sources ""` is the pattern. Recorded as **ADR-0010 N7**
  and in the exec-payloads flow doc; `orient-brief.sh` carries the guard.
  Suite 626 green twice.

- [x] **16. Distribution & macOS** — ship a runtime-free single binary and add
  the second target. Per **ADR-0012** (accepted 2026-07-20): target Linux +
  macOS (Windows-native out of scope, WSL2 is the path); the engine goes
  self-contained single-file so no host .NET runtime is needed (the shim is
  already Native AOT); port the `/proc` identity sites so the safety guards
  hold on macOS — the kill path (`setsid`+`kill(-pgid)`) is already POSIX;
  all containerization deferred.
  Landed 2026-07-21 as one commit (the ADR's three steps):
  **single-file publish** — `/deploy` publishes
  `--self-contained -p:PublishSingleFile=true`; the csproj's
  `KeepAppAssembliesLooseForIdentity` target keeps the FOUR app assemblies
  loose beside the ~74MB bundle because ContentIdentity hashes the deploy
  dir's *.dll MVIDs and the shim's skew guard reads captainHookWire.dll (a
  fully-bundled publish would throw identity and read permanent skew —
  probed both, plus the excluded-ENTRY-assembly layout running a cold hook +
  daemon spawn + warm answer end-to-end); determinism re-verified (clean
  publish ×2 + empty-commit leg ⇒ loose DLLs byte-identical).
  **`/proc` port** — no third-party lib and no hand-rolled sysctl: the BCL's
  `Process` IS the cross-platform seam (sysctl/`proc_pidinfo` underneath on
  macOS). `ChildRecords.ProcStartTime` keeps RAW `/proc` field-22 ticks on
  Linux (BCL `StartTime` jitters there — equality would misread a live
  child as pid-reused; platform.md records the asymmetry) and uses BCL
  ticks on the macOS branch where they are stable; liveness checks moved
  from `/proc` existence to POSIX `kill(pid,0)` (both OSes, one path);
  `Doctor.DefaultIsOurs` gets a `MainModule.FileName` macOS branch (weaker
  than Linux's argv check — no `--daemon` proof without a KERN_PROCARGS2
  P/Invoke; recorded as the ADR-0012 N3 residue to tighten on a real Mac).
  platform.md: macOS → committed target, Windows → out of scope, § Single-file
  distribution added. ADR-0012 N2 stands: macOS is code-complete but
  UNPROVEN until a real Mac run exercises the sysctl branch. Suite 624
  green twice.

- [x] **1. Converge the C# dispatcher and F# actor layer** — handlers become
  supervised actors: dispatch = `Ask` with the latency budget as the ask
  timeout; fail-open/fail-closed maps onto supervision (fail-open ≈ restart +
  degrade, fail-closed ≈ escalate + deny). The moment the two halves become
  one architecture. Touches `Dispatcher.cs` + `Supervision.fs`; demands a
  flow-doc update and new tests (shipshape will insist).
  Landed as `Worker<'Req,'Reply>` (ADR-0002); escalated-worker fast-fail deferred to the daemon-topology item.
- [x] **2. First live deployment** — wire the echo handler into the real
  `~/.claude/settings.json` (UserPromptSubmit) and watch an actual Claude
  Code session flow through the JSONL trail. Dogfood before features.
  Observed live 2026-07-04: a real session's dispatch in the trail (47.7ms
  end-to-end) with the injection visible in-context.
- [x] **3. Declarative harness registry** — no hardcoded harness-string
  branches: a `HarnessSpec` registry (embedded defaults + validated user
  overrides in `~/.captainHook/harnesses/`) declares each harness's request
  fields, response adapter (a CLOSED coded set — data selects, code
  implements; config never becomes a template language), per-event effect
  capabilities, and install target (the data the management API + GUI will
  consume). Pattern lineage: pharos `config.gleam` (defaults/load/cached
  layering, tool gating) and moby `models/registry.ts` (capability registry
  + validated custom entries). `claude-code` stays the default; a
  `generic-json` adapter proves N>1.
- [x] **12. Thin AOT `captainShim`** — ADR-0004 decision 7's gate tripped
  (2026-07-06): a PreToolUse-class before-tools hook puts the shim's measured
  ~85ms procBoot+JIT residual on **every tool call** — per-action, on the
  agent's critical path — so the reserved thin-AOT-shim step is scheduled.
  Design resolved in the decision-7 amendment: two new projects
  (`captainHookWire` leaf lib + `captainShim` AOT exe; arrows `captainShim →
  captainHookWire ← captainHook → captainHookActors`); identity math
  unchanged (the native shim is invisible to it by construction — sound, not
  a hole); publish-time wire-stamp skew guard (skew fails safe to collapse);
  one JSONL schema across two emitters, pinned by a golden byte-equality
  test; source-generated wire JSON; delegation fallback to the co-located
  engine. Success bar: warm hook **p50 ≤ 40ms** end-to-end (from 99ms) and
  the per-tool-call tax subjectively gone. Build order: ADR-0004
  § Implementation plan, amendment plan (6 slices → 3 phases; critical path
  wire-lib-extraction → captainshim-aot-artifact → deploy-two-artifacts).
  Tick slices here as they land.
- [x] **14. Dispatch policy — captAInHook's own front door** — the product's
  native policy story, layer 1 of 3 (2026-07-06 reframe: policy governs what
  WE bring, not a second copy of harness permissions). A user-editable
  policy file decides whether an arriving hook gets *worked*: per-event /
  per-handler enable-disable, per-project or per-session scoping, and a
  global pause — the hook is always *answered* (the harness blocks on
  stdout), but policy short-circuits dispatch to an immediate Noop.
  Daemon-side by rule — the shim stays policy-free (aot-boundary rule 1).
  Design recorded in **ADR-0006** (2026-07-06): one strict JSON file
  (`~/.captainHook/dispatch.json`, house policy dialect), decisions
  `allow|deny` only, rules AND event/handler/project(prefix)/session,
  first match wins; absent ⇒ allow all, malformed ⇒ Noop-everything
  loudly; one evaluator covering both the daemon serve loop AND the
  collapsed pipeline; hot reload, no last-good; **no pause mechanism** —
  `default: deny` already says it (API convenience later if friction is
  real). This is the same data item 5's API manages and item 6's GUI
  edits: file → API → GUI.
  Build order: ADR-0006 § Implementation plan (10 slices → 6 phases;
  critical path dispatch-policy-file → rule-matcher →
  event-level-deny-shortcircuit → absent-allow-malformed-noop →
  evaluator-both-paths → policy-hot-reload; adversarial verify on exactly
  three slices; no ultracode). Tick slices here as they land.
  Slices landed: `dispatch-policy-file` (2026-07-06; the `DispatchPolicy`
  model + strict parser under `Core/`, on `HarnessSpec.TryParse`'s precedent
  — collect-every-violation, all-or-nothing, never throw on bad DATA —
  tightened so unknown fields, an unknown/missing `version`, `ask`, and
  criteria-less rules are all MALFORMED per ADR-0006 decision 1;
  `ResolvePath` injectable-path idiom + the `CAPTAINHOOK_DISPATCH_FILE`
  override; 24 parse tests; no matcher and no tri-state yet — those wrap it
  in phases 2/4); `handler-level-exclusion` (2026-07-06; an optional
  order-preserving excluded-names filter on `DispatchAsync`, pre-fan-out so
  an excluded fail-closed handler contributes no deny, snapshot registry +
  supervised Worker left untouched — filtered never restarted; default-null
  path byte-identical, dead until wired; smoke-tested). Both land as one
  session (disjoint code); Phase 1 complete.
  `rule-matcher` (2026-07-06; `DispatchPolicy.Evaluate` → `PolicyOutcome`
  {Work, ExcludedHandlers} — two questions from one rule list: handler-less
  rules decide event-level work/short-circuit (first-match-wins, else
  default), handler-named rules decide per-handler exclusion
  (first-match-per-handler, allow shields a later deny); the sharp edge —
  project path-prefix — is separator-boundary-aware so `/repo` never matches
  `/repo2`, trims trailing separators, matches cwd==project and strict
  subdirs, literal prefix / no realpath; still no callers, wired in phase 5)
  and `exclusion-ordering-failmode-pins` (2026-07-06; the N3 adversarial
  pass, test-only: an excluded fail-closed gate contributes no deny among
  survivors, a middle exclusion leaves registration-order merge intact, and
  the sharp one — an excluded handler's supervised Worker is never
  restarted, its stateful counter continuing across the skip; plus
  exclude-all⇒Noop and exclude-unregistered⇒harmless edges). Suite 218 green
  twice. Phase 2 complete.
  `event-level-deny-shortcircuit` (2026-07-06; first wire-touching slice —
  a `DispatchPolicy?` on `HookRun.CollapsedAsync`, evaluated after
  `ParseEvent` and BEFORE the dispatcher is built: `Work==false` answers a
  valid Noop through the shared `DeniedStdout` gate+serialize tail, no worker
  asked / no budget spent / no background drain. Byte-identity to an
  uneventful hook holds by construction — the Noop rides the identical tail a
  worked dispatch's Noop takes — and is both unit-pinned and driven: a real
  collapsed run under a deny policy emits exactly `{}`, == the uneventful
  baseline, ≠ the echo, with the skip visible only on stderr. Default null =
  today's behavior, live CLI byte-unchanged; the daemon site + resolver wiring
  are phase 5, reusing `DeniedStdout` so the two paths can't drift). Suite 223
  green twice. Phase 3 complete.
  `absent-allow-malformed-noop` (2026-07-06; the file tri-state
  `PolicyResolution.Resolve` — Absent (no file ⇒ allow all, the zero-config
  default) / Malformed (present but unreadable or unparseable ⇒ deny all
  loudly, carrying the error; a directory or dangling symlink is Malformed
  not Absent — ambiguous I/O fails toward quiet, never toward silent grant;
  no keep-last-good) / Loaded (valid ⇒ evaluate); `Evaluate` maps each case
  so the two wire sites consume it uniformly. **Adversarial verify (3-skeptic
  fan-out) earned its keep**: confirmed Resolve never throws + the case
  mapping is sound, but caught a real SILENT GRANT — a rule event spelled
  kebab (`user-prompt-submit`, the project's first-class spelling) parsed
  valid yet never matched the canonical dispatch, turning a deny into a grant;
  fixed by canonicalizing the event criterion at parse (Harness.Canon) +
  case-insensitive event match, plus duplicate-JSON-field rejection (strict
  never-guess). Resolver still unwired — phase 5). Suite 240 green twice.
  Phase 4 complete.
  `evaluator-both-paths` (2026-07-06; **the go-live slice**. One shared
  `HookRun.PolicyGateFor` — resolve+evaluate → a `PolicyGate` that either
  short-circuits to the byte-identical `DeniedStdout` Noop or proceeds with
  the handler exclusions — called at the IDENTICAL seam (after ParseEvent,
  before the dispatcher) in BOTH `HookRun.CollapsedAsync` and
  `DaemonHost.DispatchOneAsync`; `policyPath` threaded RunAsync→serve→dispatch,
  Program.cs feeds all three prod entry points (daemon, shim-fallback,
  collapsed) from the same `DispatchPolicy.ResolvePath()`. Resolved
  per-dispatch (content edit effective next hook; phase 6 adds the stat-gate).
  Adversarial verify — no-drift: a cross-path test drives the same policy
  file+event through the real daemon (over ShimClient) and the collapsed
  pipeline and asserts byte-identical answers; a 2-skeptic fan-out confirmed
  no decision drift and no ungoverned dispatch route (all three shim routes +
  both prod pipelines gated); driven live through the real CLI — deny⇒`{}`,
  undenied event echoes, malformed⇒`{}`+loud stderr. Load-bearing order
  honored: landed only after phase 4's absent⇒allow default). Suite 245 green
  twice. Phase 5 complete.
  `policy-hot-reload` + `skip-trail-visibility` + `default-deny-pause-pin`
  (2026-07-06, the tail. `ReloadingPolicy` — the daemon's per-dispatch
  (mtime,size) stat-gate over Resolve: poison-AND-advance (a broken edit
  denies all AND advances the stamp — no keep-last-good, no re-parse storm),
  unchanged file returns the same instance, collapsed path resolves once.
  Trail: `policy.skip`/`.exclude`/`.malformed`/`.reload` emitted in the one
  shared gate (no trail drift), happy path silent. default:deny Noops every
  hook — decision 7's pause, pinned. **Adversarial verify (skeptic on the
  reload edge)**: confirmed poison-advance/no-storm/recover all hold, and
  caught a NEW fail-open — an absent⇒`mkdir` at the path stamped identically
  to absent so the gate stayed allow-all when Resolve says Malformed; fixed
  by giving Stamp Resolve's directory-first precedence, regression-pinned.
  Two other flagged risks (mtime/size stamp collision; unlocked two-field
  swap) are pre-existing properties `ReloadingHarnessRegistry` already
  accepts — not introduced here. Flow doc: doc/flow/dispatch-policy.md).
  Suite green twice. **Item 14 complete — dispatch policy is live on both
  paths.** Deployed 2026-07-06 and verified on the live daemon: deny-
  SessionStart hot-reloaded in (denied ⇒ `{}` while UserPromptSubmit still
  echoed), file removed ⇒ SessionStart echoes again — hot reload both
  directions with `policy.reload`/`policy.skip` in the trail; clean allow-all
  state (no `dispatch.json`) restored. Dogfooding live.
- [ ] **13. PreToolUse policy gate** — *demoted to a secondary payload*
  (2026-07-06): tool-call gating overlaps harness-native permissions; its
  differentiated value (dynamic decisions, portability, central
  distribution) matures with items 5/10. Design stays recorded in
  **ADR-0005** (status: deferred) for when the payload is wanted — likely
  alongside item 9's other handlers, after item 6.
  Slices landed: `wire-lib-extraction` (2026-07-06; pure move — five files
  `git mv`'d into the new leaf lib, wire log seam bound to `Actors.Log` by
  engine + tests, suite green twice, zero behavior change);
  `wire-json-source-gen` (2026-07-06; `WireJson` context for the two frame
  records, `IsAotCompatible` analyzers on — wire lib builds warning-free);
  `wire-jsonl-logger` (2026-07-06; `WireJsonl.Render` = the shim's emitter of
  the trail's one schema, pinned byte-identical to F# `ToJson()` by 17 golden
  cross-emitter tests — unicode/control escaping, durMs rounding, omit-null,
  nested data — plus mirrored path resolution and the O_APPEND appender).
  Phase A complete.
  `captainshim-aot-artifact` (2026-07-06; 3.8MB native ELF, wire-lib-only
  reference graph; ShimMain tested in IL form through injected streams —
  warm relay, delegation verbatim, at-most-once held, mode refusal; staged
  co-located deploy measured live: **16ms/hook warm native vs 139ms JIT**
  20-run avg against the same daemon — 8.7×, success bar ≤40ms beaten;
  ~11ms of the 16 is the forward span, native procBoot ≈5ms; the sun_path
  overflow path exercised by accident and delegated exactly as designed;
  AOT toolchain + no-MVID facts recorded in platform.md);
  `wire-skew-guard` (2026-07-06; zero build machinery — Native AOT preserves
  `Module.ModuleVersionId`, probed AOT≡IL, so the shim compares what it IS
  against what the directory advertises; mismatch or missing DLL ⇒ never
  touch the socket, delegate, `shim.wireSkew` in the trail; pinned by IL
  tests in both skew directions + a live-socket never-accepted assert, and
  verified in the native artifact — including catching a REAL skew created
  mid-verification by copying one artifact without the other, which
  delegated and answered the hook exactly as designed). Phase B complete.
  `deploy-two-artifacts` (2026-07-06; /deploy reworked to stage-both +
  swap-together with `bin.prev` kept for one-swap rollback; live cutover
  verified — cold delegated+spawned, warm `shim.answered`, zero
  `shim.wireSkew`, superseded daemon doctor-drained; PreToolUse wired into
  settings.json per the gate's own trigger, dispatching `{}`/exit-0 with
  zero handlers until item 9's policy gate; **live warm hook 16ms vs 143ms
  pre-cutover** — 9×, the amendment's ≤40ms bar beaten on the real path).
  **Item complete: the amendment plan is fully landed; dogfooding live on
  the native shim.**

## Next

- [x] **4. Daemon topology** — long-lived `captaind` + thin per-event shim
  (DESIGN.md's split). ⚠ Fires ADR-0001's revisit trigger: re-evaluate
  Akka.NET vs the hand-rolled layer *before* building on either → an ADR.
  Design recorded in ADR-0004 (verdict: stay hand-rolled; carry-ins
  answered) — the gate is discharged, this item is now implementation.
  Build order: ADR-0004 § Implementation plan (14 slices → 6 phases;
  critical path content-identity → lock-bind → serve-loop → drain →
  idle-exit). Tick progress here as slices land.
  Slices landed: `three-mode-dispatch`, `frame-protocol`,
  `content-identity-versioned-socket`, `timeout-fault-classification`
  (2026-07-05) — Phase 1 complete; `lock-bind-rendezvous`,
  `shim-forward-or-fallback`, `detached-daemon-spawn` (2026-07-05) —
  Phase 2 complete; `daemon-serve-loop` (2026-07-05; dispatchId adoption
  verified end-to-end); `at-most-once-fallback-guard` (2026-07-05; chaos
  audit: 30 hooks under random daemon SIGKILL — zero double dispatches) —
  Phase 3 complete; `sigterm-drain` (2026-07-06; real SIGTERM landed
  mid-dispatch — in-flight hook still answered, drained in 62ms);
  `harness-hot-reload` (2026-07-06; in-place edit — same inode, dir mtime
  unmoved — served on the next hook through a live daemon); `doctor-reaper`
  (2026-07-06; swept 3 stale identities live while sparing the healthy
  deployed daemon from a dev-tree run) — Phase 4 complete;
  `mandatory-idle-exit` (2026-07-06; live daemon survived refreshing hooks,
  self-reaped 92ms past its window, respawned on the next hook) — Phase 5
  complete; `concurrency-audit-and-soak` (2026-07-06; 200 concurrent
  dispatches in-suite — seq values a perfect 1..200 permutation, background
  queue exact, escalation mid-load survived; 180 live hooks against the
  deployed daemon — 100% warm, zero double dispatches, p50 99ms round-trip
  / 13.4ms daemon-side, RSS asymptoting not leaking) — Phase 6 complete.
  **ADR-0004's implementation plan is fully landed**; dogfooding live.
  Carry-out for the AOT-shim gate (ADR-0004 decision 7): the measured warm
  residual is ~85ms of shim procBoot+JIT per hook — **gate tripped
  2026-07-06 → item 12**.
  ⚠ Until `mandatory-idle-exit` lands, a spawned daemon lives until killed —
  SIGTERM now drains gracefully; kill -9 stays safe.
  Dogfooding: `/deploy` ships the apphost build to the live hooks and
  verifies the warm path (mechanics: doc/flow/live-deployment.md).
  **Carry-ins from ADR-0002 — DISCHARGED** by the
  `timeout-fault-classification` slice (ADR-0004 decision 5): (a) a wedged,
  token-ignoring handler is abandoned-and-respawned and counts toward
  escalation; (b) asks against an escalated worker fail fast (~0ms); (c)
  honored cancellations restart without counting — changed deliberately.
  Pinned by ClassificationTests.cs; mechanics in
  doc/flow/actor-supervision.md.
- [x] **5. Management API** — HTTP + SSE on the daemon: inventory of
  installed hooks/skills, install/uninstall/enable/disable operations, and a
  live event stream sourced from the structured log pipeline (dispatchId
  correlation = per-dispatch traces for free). **After item 14** — the API's
  write surface IS item 14's policy/registry data (file → API → GUI), and
  the event stream wants real dispatch traces. The ADR it fires is fired:
  design recorded in **ADR-0007** (2026-07-07) — BCL `HttpListener`
  loopback-only in daemon mode (no new project; the zero-new-deps answer —
  Kestrel and even the ASP.NET FrameworkReference rejected); fixed default
  port 4665 + `api.json` discovery file, drain-start port handoff across
  identity cutover (the port is a singleton the versioned socket never
  was); SSE over a stat-poll tail of the JSONL trail file (both emitters'
  halves — an in-process tee would miss the shim and collapsed dispatches),
  byte-offset event ids for lossless reconnect, bounded-channel drop-oldest
  + gap marker per subscriber; writes v1 = `PUT /policy` only, validated by
  the same strict parser and atomic-renamed so hot reload makes API writes
  ≡ hand edits (install ops deferred to ride with items 6+10, ADR-0006 N1);
  per-daemon bearer token (0600 `api.json`) + Origin checks on every
  request; idle-exit answered — requests reset the clock, an open SSE
  stream defers exit (current lock-holder only), the API never spawns a
  daemon. ADR-0004's "management API lands" trigger examined and declined:
  the hook path keeps one UDS connection per dispatch.
  Build order: ADR-0007 § Implementation plan (2026-07-07; 13 slices → 7
  phases; critical path api-listener-host → port-config-and-cutover →
  api-json-discovery → auth-token-origin → put-policy-write →
  docs-flow-platform; adversarial verify on 6 slices — the port handoff,
  auth, both SSE slices, idle-defer, and the atomic policy write; no
  ultracode). Tick slices here as they land.
  Phase 7 requirement (`docs-flow-platform`): the flow doc MUST carry a
  ground-truth table of `TrailCursor`'s edge behaviors — oversized-line skip
  (lines ≥ `maxBytes` are dropped, not delivered, and surfaced as a gap;
  the 128KiB read window is a hard limit), truncation-reset, and
  alignment-self-heal — so the tailer's sharp edges have a discoverable home,
  not just the Phase-5 close-out prose below + test names.
  Slices landed: `api-listener-host` (2026-07-07; Phase 1 — the loopback
  `HttpListener` management-API host in a new `Api/` area (`ApiHost` +
  reflection-STJ `ApiJson`), accept-and-hand-off loop that serves requests
  concurrently, `/api/v1` router skeleton that 404s every unwired route as
  JSON, started after `BindWhenWarm` beside the UDS serve loop via a new
  `DaemonHost.RunAsync(apiPort:)` seam and stopped at drain start — off in
  production until port-config wires Program.cs; the shim never sees it,
  aot-boundary rule 1 intact; zero new deps (HttpListener is BCL). 5 tests,
  suite 267 green twice).
  `port-config-and-cutover` (2026-07-07; Phase 2 — the API goes LIVE in
  production: Program.cs resolves `ApiHost.ResolvePort` (default 4665, env
  `CAPTAINHOOK_API_PORT`, 0 disables, malformed falls to default) into the
  daemon; N1's singleton-port handoff lands as `ApiHost.StartRetrying` —
  one sync bind attempt, fast 100ms→1s backoff spanning the incumbent's
  drain deadline, one `api.bindBlocked` warn past it, then a 5s cadence
  that never gives up until Stop, so a deploy-superseded incumbent that
  lingers to idle-exit still hands the port over; the incumbent's release
  stays at drain start (the Phase-1 `api?.Stop()` seam, now also halting
  in-flight retries via a gate double-check so a draining daemon never
  re-grabs the port); bind failure is never fatal and hooks serve
  throughout. Platform facts probed and recorded (doc/platform.md § Loopback
  TCP): no co-bind cross- or in-process, TIME_WAIT does not block a
  .NET→.NET rebind (the .NET Unix PAL sets SO_REUSEADDR on every TCP bind;
  Linux honors it pairwise, so a non-.NET prior occupant can cost ~60s —
  absorbed by the slow retry), loopback binds unprivileged. 18 new tests
  incl. a two-real-daemons cutover proof (successor binds while the
  incumbent still drains a straggler) and a TIME_WAIT rebind pin; suite 285
  green twice; adversarially verified per the plan — the verify pass then
  hardened Stop/Dispose (a concurrent-Dispose ODE), made the trail's
  stopped→listening cutover order deterministic, silenced a misleading
  post-stop warn, and corrected the platform-fact attribution above).
  `api-json-discovery` (2026-07-07; Phase 3a — the credential file: a
  version-partitioned 0600 `captaind-<id>.api.json` (port, token, pid,
  identity) beside socket/lock/pid (`RendezvousPaths.ApiJsonPath` +
  `ApiDiscovery` read/write). `ApiHost` mints a 256-bit hex bearer token —
  the SOLE credential source — and publishes/removes the file UNDER the
  same gate that flips `_listening`, so "file exists ⟺ we hold the port"
  holds against a racing Stop and a retrying host never advertises a port
  it doesn't own. Version-partitioned so a draining incumbent never deletes
  its successor's file; `doctor` reaps a stale one once the lock proves the
  owner dead. No gate yet — the token is published, nothing is checked. 8
  tests; suite 292 green twice).
  `auth-token-origin` (2026-07-07; Phase 3b — the credential gate on the
  WHOLE TCP surface, before the router, so even the unwired 404 is
  unreachable without the token. `ApiAuthGate` (a pure, directly unit-tested
  internal seam) checks, in order: Host = the exact loopback authority
  (rebind), Origin present ⇒ must be ours / absent ⇒ allowed so curl works
  (CSRF), bearer token constant-time compared via `FixedTimeEquals` (authn);
  401 carries `WWW-Authenticate: Bearer`. The token is the api.json one, the
  sole credential. Platform-composed: managed `HttpListener` prefix-matches
  on Host, so a foreign Host 404s at the listener BEFORE the gate (rebind
  defense's first layer, recorded in platform.md); the API answers 127.0.0.1
  only (localhost would need a second prefix — deferred). The engine csproj
  gains one `InternalsVisibleTo` so the security logic is tested directly,
  not only through HttpListener's quirks. 22 auth tests (15 pure-gate + 7
  HTTP) plus every prior HTTP test updated to present the token; suite green
  twice; adversarially verified per the plan. Endpoints (Phase 4) inherit
  the gate for free.
  Phase 4 read endpoints — `get-status` + `get-policy-read` + `get-harnesses`
  + `get-handlers` (2026-07-07; the parallel antichain, landed as one batch —
  read-only, no adversarial verify). `GET /api/v1/{status,policy,harnesses,
  handlers}` render from an `ApiReadModel` over the SAME live resolvers,
  registry, and dispatcher the dispatch path runs, so the API view cannot
  drift (ADR-0007 d3). `/status`: identity, pid, monotonic uptime, and live
  serve counters (a new `ServeStats` replaces DaemonHost's bare `active`
  local, adding a lifetime `served` count). `/policy`: the resolved tri-state
  (absent/malformed+error/loaded+parsed doc) plus the raw file and a
  content-hash **ETag** (header + body — the token `put-policy-write`'s
  If-Match will consume). `/harnesses`: the registry projection (specs,
  adapters, request mapping, per-event capabilities). `/handlers`: every
  registration with fail mode + live supervision state — carried by the one
  new bit of plumbing, plain-data `Worker.Generation`/`IsDead` F# accessors
  (int/bool cross the boundary; the DUs stay inside) behind a
  `Dispatcher.Snapshot()`. All four inherit the Phase-3 auth gate (401
  without the token) and 404 an unknown route; a pure listener with no read
  model 404s them all. 8 endpoint tests over real Core objects + the
  daemon-integration `/status` at 200; suite green twice.
  `sse-trail-tail` (2026-07-08; Phase 5, first slice — the live stream:
  `GET /api/v1/events` is SSE over a stat-poll tail of the JSONL trail file
  (decision 5) — the file, not an in-process tee, so both emitters' halves
  and collapsed dispatches all flow. `TrailCursor` owns the sharp edges: only
  complete lines are ever emitted (bytes past the last `\n` re-read next
  poll, so a concurrent O_APPEND can never surface half-written), event id =
  byte offset after the line (`Last-Event-ID` resumes with zero dup/loss —
  byte-split before UTF-8 decode, so multi-byte content can't skew ids; a
  mid-line resume offset self-heals forward to the next boundary rather than
  emitting garbage), truncation/replacement resets the id space with an
  explicit `reset` event, an absent file is quiet-not-error. The tailer is
  SCHEMA-BLIND — ships opaque newline-delimited lines, parses nothing — so
  N4's third-consumer coupling shrinks to "newline-delimited". Per-subscriber
  `TrailSubscription` (poll task → channel → writer with comment heartbeats —
  the heartbeat doubles as the dead-client probe); streams run on the ApiHost
  stop token, so drain-start `Stop()` now terminates open streams (the
  Phase-2 stub, cashed) and `OpenStreams` tracks them (finally-decremented;
  the idle-defer slice reads it next). Channel is unbounded THIS slice —
  `sse-backpressure` bounds it. Auth-gated like every route; browser
  EventSource can't send the bearer header, so item 6's GUI uses
  fetch-streaming (noted in code). 19 tests incl. byte-offset ids over real
  HTTP, exact resume, live-end default, Stop teardown, heartbeat dead-client
  release, and the Phase-1 debt cashed: an open stream while other requests
  answer. Suite green twice; adversarial verify per plan — the resume/id math
  survived attack (probed via a standalone compile of the real TrailCursor);
  the pass then fixed, in-phase: a line longer than the read window no longer
  wedges every cursor forever (it is SKIPPED across polls and surfaced as an
  honest gap — the verify's one correctness-threatening find), a live cursor
  now detects truncate-then-REGROW via a boundary-byte re-check (offsets rest
  just past '\n'; a replaced file fails the check 255/256), a truncation
  racing inside one poll yields quietly instead of killing the subscription,
  the ApiHost stop-CTS is never disposed (a Stop∥Dispose race could swallow
  the only Cancel SSE writers ride), align-consumption polls report More
  correctly, and a drain-racing /events OCE is a routine end, not
  handlerError noise. It also surfaced a PRE-EXISTING emitter defect: .NET's
  File.AppendAllText does NOT open O_APPEND (strace-proved) — shim+daemon
  can clobber concurrent trail appends; recorded in platform.md + scratch as
  a wire-lib follow-up, reader unaffected.)
  `sse-backpressure` (2026-07-08; Phase 5 — a slow consumer gets drop-oldest
  plus an explicit gap marker with the EXACT dropped count, never a growing
  daemon, never a silent hole, never a disconnect (decision 5 / ADR-0004 d6).
  The per-subscriber channel is bounded (`SseOptions.Capacity`, default 256);
  eviction is by hand — `BoundedChannelFullMode.DropOldest` discards silently
  and could never carry the count — and the count plus the truncation-reset
  both travel OUT OF BAND (Interlocked fields the writer checks before each
  dequeue), which is what makes the gap and the reset structurally
  un-droppable: they are never in the buffer that drops. "Slow" means no
  room within one poll-beat of grace — a burst append bigger than capacity
  with a healthy consumer must not drop on a scheduler race (found by the
  first cut's own test); once pressured, evictions run at full speed until a
  first-try write succeeds. A reset clears the buffer and supersedes any
  pending gap (counting lines of a replaced file would lie). A gap carries
  no id, so a reconnect resumes from the last line id and RECOVERS the
  dropped region from the file. Deterministic stalled-sink tests: exact
  drop counts, reset-supersedes, fast-consumer-full-fidelity.)
  `idle-exit-defer` (2026-07-08; Phase 5, ADR-0004's open question cashed as
  decision 7: any API request resets the idle clock (an `onRequest` stamp
  callback into DaemonHost's `lastActive`, fired before the gate — even a
  401 proves interaction) and an open SSE subscription defers idle-exit —
  `ApiHost.OpenStreams` (finally-decremented) joins `active` and
  `BackgroundPending` in the idle watchdog's activity check, riding the same
  bookkeeping the background queue uses. CURRENT-LOCK-HOLDER-ONLY by
  construction: drain-start `Stop()` terminates every stream, so a
  superseded daemon is never pinned by a forgotten tab. `/status` now
  reports `openStreams`. FakeClock daemon tests: stream-defers/close-
  releases (a full fresh window after release), request-refreshes-the-
  window, drain-never-pinned-by-a-stream.)
  Phase-5 adversarial verifies, closed out (2026-07-08): backpressure's
  exact-count and un-droppable-marker contracts SURVIVED attack (200k-item
  probe: delivered+evicted=enqueued, exactly once; reset ordering airtight);
  touch-ups landed (skip-gaps may surface up to `capacity` lines before
  their chronological hole — positional only, count exact, recovery
  unharmed, now documented+pinned; every eviction counted regardless of
  type; FastConsumer made structurally flake-proof). The idle-defer verify
  CONFIRMED two real gaps, both fixed: (1) the immortal-daemon loop —
  decision 7's "current-lock-holder-only" was an effect of drain, not a
  mechanism, so a forgotten tab could pin a superseded daemon on the
  singleton port forever; the daemon now re-fingerprints its own deploy dir
  on quiet ticks and drains itself on mismatch (`daemon.superseded` —
  ADR-0007 d7 amendment, giving decision 2's "superseded" clause its
  missing code). (2) Probe-proven: managed `HttpListener.Stop()/Close()`
  BLOCK on Linux behind a write wedged on a zero-window subscriber — a
  synchronous teardown made one stalled client an unkillable daemon that
  never released the version lock (every same-identity hook collapsing
  forever); teardown now runs bounded-background (the port frees the
  instant Stop begins, so the handoff is unharmed), recorded in
  platform.md. Both pinned: supersession-reaps-despite-a-forgotten-tab,
  Stop-bounded-under-a-wedged-writer.
  `put-policy-write` (2026-07-08; Phase 6, the last critical-path slice — the
  ONE write verb: `PUT /api/v1/policy`, the API as EDITOR OF THE FILE, not
  owner of state. `ApiPolicyWriter` validates the body with the daemon's OWN
  strict `DispatchPolicy.TryParse` (refuse to write what the daemon would
  refuse to load), honors `If-Match` when supplied (the content-hash ETag
  Phase 4 built), and installs ATOMICALLY — temp+rename in the TARGET'S OWN
  directory, the `GetTempFileName()`+`Move` cross-device trap sidestepped — so
  `ReloadingPolicy`'s stat-gate makes it effective on the next dispatch exactly
  as a hand-edit does. A closed `PolicyWriteOutcome` DU maps 1:1 to HTTP:
  200+ETag / 422 violations / 412 If-Match mismatch / 413 over-cap / 500 I/O;
  the write inherits the Phase-3 auth gate, null policy path ⇒ 404. Wired
  through DaemonHost beside the read model (same `policyPath`). 20 tests incl.
  a concurrent-reader ATOMICITY probe (a hook stat-gating the file mid-write
  never sees a torn/absent state — the exact hazard a non-atomic write would
  flash) and an END-TO-END in-daemon proof (a live PUT of `default:deny`
  short-circuits the next real UDS hook to a Noop — the ADR's mandatory
  hot-path verify, now a committed guard). Suite 358 → 378 green twice.
  Adversarially verified: the two named sharp edges (cross-device atomicity,
  ETag round-trip) SURVIVED; fixes landed for a BOM asymmetry (the daemon's
  loader strips a leading BOM, so the writer must too — else a spurious 422 on
  content the daemon would load, and a broken round-trip), a drain-race
  `api.handlerError` (OCE now → 503, mirroring the SSE swallow), and a
  non-exhaustive outcome switch. One MODERATE routed to follow-up, not
  scope-crept: the `(mtime,size)` stat-gate can miss a same-length change on a
  COARSE-mtime FS — probe-confirmed unreachable on ext4/APFS/NTFS, a
  pre-existing ADR-0006 property; the write API just makes it programmatically
  reachable (scratch.md + platform.md).)
  `docs-flow-platform` (2026-07-08; Phase 7, the capstone — terminal by
  construction: `doc/flow/management-api.md`, the management-API flow doc
  (request-lifecycle + cutover ASCII diagrams, why-prose for discovery/auth/
  lifecycle/SSE/write, GUI notes, and a ground-truth table naming every
  endpoint, symbol, log event, and test class), including the mandated
  `TrailCursor` edge-behavior table (oversized-line skip / truncation-reset /
  alignment-self-heal / truncate-regrow). platform.md's `HttpListener`-on-Unix
  quirks (N5) recorded as met — the § Loopback TCP section already consolidates
  co-bind, SO_REUSEADDR/TIME_WAIT-pairwise, teardown-blocks-behind-a-wedged-
  write, and Host-prefix-match; now cross-linked to the flow doc. No adversarial
  verify — shipshape only. **ADR-0007 complete: all 7 phases, all 13 slices
  landed; item 5 checked.**)
  Install operations carry item 10's
  trust model with them. The fleet/enterprise shape (one org, many
  employees) is local-data-plane + central-control-plane: per-machine
  daemons exactly as today, with policy distribution / config / telemetry
  aggregation as the centralized layer this API eventually fronts —
  never a shared remote daemon on the hot path (ADR-0004's transport
  revisit trigger stands).
- [x] **6. GUI v1: browser UI** — localhost web app served by the daemon.
  Catalog + one-click install, live dispatch traces, supervision view
  (restarts/escalations as they happen). Web-first per the GUI direction
  below; on WSL2 this is the *best* UX, not a fallback. Lands WITH a
  Playwright harness (Microsoft.Playwright, same xunit suite): the DOM +
  accessibility tree is the agent-legible surface — semantic locators and
  auto-waiting beat TUI screen-scraping for the agentic dev loop.
  Design recorded in **ADR-0008** (accepted 2026-07-09): observability-first
  v1 — five screens (live trace / supervision / policy editor / harnesses /
  status) over the EXISTING API, no new data endpoints, no control verbs
  (catalog + one-click install defer to item 10's trust model + the install
  ops); a separate `web/` React+Vite project served same-origin from a disk
  `ui/` dir (the third /deploy artifact; Node dev-only, built assets
  committed); token handed off via URL *fragment* by a new `captainHook ui`
  verb; islands + one Zustand store, TS types generated from the C# DTOs,
  full-reload dev loop (HMR reserved). Playwright E2E lives in `web/`
  driving the daemon's own `/ui` — supersedes the
  Microsoft.Playwright-in-xunit leaning above. Trail growth split out to
  **ADR-0009**: the opaque-cursor resume contract is honored by this GUI's
  SSE client now; rotation/backfill deferred until growth bites.
  Build order: ADR-0008 § Implementation plan (2026-07-09; 13 slices → 7
  phases; critical path web-scaffold → dto-schema-codegen → zustand-store →
  sse-fetch-client → read-panels-islands → playwright-e2e →
  flow-doc-management-gui; adversarial verify on exactly 3 slices — the /ui
  route's traversal guard + gate split, the SSE fetch client's
  resume/reset/gap + dead-credential logic, and the policy editor's ETag
  lifecycle; no ultracode). Tick slices here as they land.
  Slices landed: `ui-static-route` + `inert-shell-tests` (2026-07-09; one
  commit — the `GET /ui[/*]` static route in `Api/`, the daemon's ONE
  bearer-exempt surface, pinned the instant it opens. `ApiAuthGate` split into
  `EvaluateShell` (Host+Origin only) so /ui and /api/v1/* can't drift on
  transport policy; exemption bearer-ONLY and scoped to exactly /ui[/...]
  (`IsUiPath` on the router's own AbsolutePath). Pure `ResolveUiFile` traversal
  guard — rooted/NUL/escape/dir/missing ⇒ null, separator-appended prefix so a
  sibling ui2/ can't pass, trailing-slash-root trimmed. 41 tests: guard proven
  against files that REALLY exist outside ui/, traversal probed over a raw
  socket, inert contract (byte-identical authed/unauthed, token never served).
  **Adversarially verified** — no escape under any encoded/mixed form
  (HttpListener collapses dot-segments pre-gate ⇒ /ui/../api/v1/status meets the
  full 401); trailing-slash hardening applied, symlinks-inside-ui trusted per
  d2). `web-scaffold` (2026-07-09; the `web/` React19+Vite6+Zustand5 project,
  Vite base '/ui/', outDir → the committed `ui/`; Node dev-only, built assets
  committed; drove the real built ui/ through the live daemon route
  end-to-end). Phase 1 complete.
  `dto-schema-codegen` + `token-handoff-bootstrap` (2026-07-09, one
  frontend-plumbing session per the plan's batching note. Codegen:
  `ApiSchema.Export` (BCL `JsonSchemaExporter`; camelCase wire casing,
  NRT-honest nullability, strict numbers) → checked-in
  `web/schema/api.schema.json` pinned by `ApiSchemaTests` — the drift detector;
  `CAPTAINHOOK_SCHEMA_UPDATE=1` regenerates — → `gen-types.mjs` derives
  `src/api.gen.ts` in every npm build; DTO change ⇒ regenerate both, same
  commit. Handoff: `web/src/auth.ts` — #t= fragment → sessionStorage →
  immediate replaceState scrub → Bearer on every apiFetch; fresh hash beats
  stale stash; 401/403 ⇒ the no-self-heal 'session ended' state. Driven
  end-to-end in headless chromium against a real isolated daemon: fragment
  parsed, hash scrubbed, stash survives reload, /status renders live, fresh
  tab inert). `ui-cli-verb` (2026-07-09; `Mode.Ui` in the wire enum + shim's
  loud refusal, `UiVerb` reads the 0600 api.json — absent ⇒ clear refusal,
  never a spawn — and opens `/ui#t=<token>` via the OS opener; the fragment
  shape is pinned against the shell bootstrap's regex; the token reaches the
  browser and NOWHERE else (stdout/stderr asserted clean); the argv
  /proc-cmdline residual recorded in scratch). `deploy-ui-staging`
  (2026-07-09; /deploy stages the committed ui/ beside the two executables —
  one swap, one bin.prev rollback, no npm at deploy; verification gains the
  same-daemon /ui shell check). Suite 417 green twice. Phase 2 complete.
  `zustand-store` (2026-07-09; the contract slice, its own deliberate pass —
  one provider-less store (`web/src/store.ts`), slices 1:1 with decision 1's
  screens + session/stream; `SseFrame` mirrors the server SSE grammar (opaque
  id per ADR-0009 d2, id-less gaps), `PolicyVerdict` mirrors the closed
  `PolicyWriteOutcome`; `foldTrace` is the one reducer — reset clears and
  supersedes the client truncation count, gaps visible, unparsable lines
  render raw, TRACE_CAP counts evictions; trail stays schema-blind
  (all-optional `TrailLine`). App/main are the first store consumers; reducer
  tested pure via node:test (zero new deps); re-driven in headless chromium.
  Phase 3 complete.
  `sse-fetch-client` + `policy-editor-island` (2026-07-09, the parallel pair,
  both **adversarially verified against live daemons** per the plan. SSE
  client (`web/src/sse.ts`): pure protocol layer under a reconnect loop — all
  five contracts held under attack (opaque-cursor resume zero-dup/zero-loss
  across 8 real TCP cuts; gap-never-advances proven against a real
  57,853-line eviction with all 60k lines recovered; reset re-anchors incl.
  cut-at-the-reset ⇒ genesis replay exactly-once; 401⇒dead-stops vs
  drop/drain⇒retry-resumes; UTF-8 through 1–5-byte chunk slices). The verify
  CAUGHT a real server defect — the from-now anchor raced the first flush,
  silently losing a line appended at client-live; fixed by anchoring the
  subscription BEFORE the retry-hint flush (anchor ≤ headers ≤ live by
  construction) — and two client hardenings landed (reader.cancel in a
  finally so a throwing subscriber can't leak the stream and pin the daemon's
  idle-defer counter; CR-free trail note). Policy editor (`policy.ts` +
  `PolicyPanel.tsx`): the ETag discipline's three pins all held (If-Match on
  every PUT once known — stale tag 412s, file untouched; 200's tag adopted,
  no re-GET; 412's current adopted, draft preserved); verify hardenings: a
  tagless 200 maps to null + GET re-seed, never `\"\"` (the server ignores an
  empty If-Match — probed live, it would write blind); submitPolicy never
  throws; saving flag released in a finally. 24 web unit tests; chromium
  drive incl. save-twice ETag adoption; suite 417 green twice ×2 runs.
  Phase 4 complete.
  `read-panels-islands` (2026-07-09; the four read screens as sibling islands
  over the store. Three fetch-and-render tables (Status — identity + serve
  counters, polled 3s; Supervision — handlers with fail mode / generation /
  dead, polled 4s; Harnesses — the ADR-0003 registry projection, fetched
  once) share one `useApiJson` hook so a 401 flips the whole session to dead
  in one place. Live trace reads the SSE-filled trace slice: dispatchId
  correlation via a stable color-per-id + click-to-filter, a substring
  filter, follow-the-tail scroll, and gap/reset as honest dividers; pure
  display logic (`src/format.ts`) unit-tested. App slimmed to the shell; one
  theme-aware stylesheet. 31 web unit tests; chromium drive of all panels +
  trace ingest/filter, eyeballed light+dark; engine untouched, C# 417 green.
  Phase 5 complete.
  `playwright-e2e` (2026-07-09; a @playwright/test suite in web/ — 10 tests, 5
  files — driving the daemon's own same-origin /ui end to end: the shell's
  bearer-exempt-but-inert boundary, the fragment token handoff (live/scrub/
  reload/bad-token-dead), the read panels over real daemon data, the live
  trace ingesting appended lines with dispatch-chip + text filters, and the
  policy editor's write path incl. the ETag-adoption pin. The reasoning is the
  daemon fixture: a fresh daemon per test, fully isolated from the live
  ~/.captainHook tree (temp XDG_RUNTIME_DIR/log/harness/dispatch), readiness by
  the 0600 api.json (polled, not slept), teardown SIGTERM-by-PID → await exit →
  SIGKILL; globalSetup builds the engine + stages the fresh ui/. The phase's
  named flakiness bit — the daemon's F#-actor warm starved under the browser's
  CPU load (a 58s stall diagnosed) — and was root-caused + fixed three ways
  (await-true-exit so drainers don't pile up, a thread-pool floor, one retry
  for the all-cores-pegged residual; proven green under deliberate 4-core
  saturation). Also driven headed via WSLg. Node/@playwright/test dev-only.
  Phase 6 complete.
  `flow-doc-management-gui` (2026-07-09, the capstone — terminal by
  construction: `doc/flow/management-gui.md` (request-lifecycle ASCII diagram +
  why-prose for islands/store/handoff/`/ui`-route/SSE-client/ETag/codegen/
  deploy/E2E + a ground-truth table naming every file, symbol, and test), and
  ADR-0008's placeholder Ground-truth section back-filled as a decision→code
  index. Docs-only; symbols cross-checked against the code. **ADR-0008
  complete: all 7 phases, all 13 slices landed; item 6 checked.**

## Later

- [ ] **20. The mailbox bus — cross-harness agent communication** — the hub
  reframe: N external agent loops (Claude Code + any hook-bearing harness),
  one daemon as the store-and-forward bus. Mail is written by appending an
  envelope to a durable disk store; it is *delivered* only at seams the
  recipient's harness declares (turn-start inject / mid-turn urgent /
  Stop-block reconcile), with per-(role, session) byte-offset cursors,
  delivery-opportunity TTLs, and cursor-advance-on-inject as the Stop-loop
  guard. Members span deterministic gates (key-redactor), write-only
  observers (edit log), on-demand LLM watchers, and full peers — LLM-ness is
  a payload detail the bus never sees. Zero core: two engine CLI verbs
  (`mail send` / `mail digest`), payloads, and data; swarm activation is a
  dispatch-policy flip, not a boot verb; ADR-0011's consent gate stays
  per-executable. Ask/reply correlation explicitly deferred (envelope
  reserves `inReplyTo`). Provenance/governance designed in, not bolted on:
  delivery is a `mail.deliver` ledger event closing the cross-agent causality
  chain, the mail store is hash-chained (trail chaining deferred with the
  two-emitter lock cost named), policy reloads gain content hashes, and the
  three stores get three lifetimes (cursors ephemeral / trail operational /
  mail archival, all 0600). Design: **ADR-0016** (accepted 2026-08-12).
  Build order: ADR-0016 § Implementation plan (2026-08-12; 11 slices → 6
  phases; critical path mail-envelope-parser → mail-store-chained-append →
  mail-cursor → mail-digest-handler → cursor-edge-adversarial-tests →
  swarm-profile-and-flow-doc; adversarial verify on exactly five slices —
  store, cursor, digest, the adversarial-test campaign, stop-seam; no
  ultracode). Sequencing hazards named in the plan: the on-disk chain format
  is durable (nothing writes real data before phase 2's verify settles it),
  and dogfood lands strictly LAST — no live payloads on the maintainer's
  session until the exactly-once tests and the Stop-loop pin are green.
  First dogfood target: both agents' PostToolUse streams into one edit log
  with stale-view warnings — the payload only the hub position makes
  possible. Tick slices here as they land.
  Slices landed: `mail-envelope-parser` (2026-08-12; phase 1 — d2's envelope as
  a strict parser on the `DispatchPolicy.TryParse` precedent: every violation in
  one pass, all-or-nothing accept, unknown AND duplicate fields malformed, never
  a throw on bad DATA. The failure DIRECTION is the mirror of the policy
  parser's and drove every judgment call — a malformed policy poisons the door
  loudly, a malformed envelope is warned-and-skipped, so too-loose delivers what
  nobody can render and too-tight silently drops real mail. Four calls the
  ADR left to the slice: `session` inside `from` is OPTIONAL (a write-only
  hookless member is a real membership class per d5 — requiring it would make
  the bus's cheapest tier unrepresentable); `priority`/`ttlDeliveries` are
  optional with defaults that fail SAFE (lowest-traffic seam class, bounded TTL
  — a forgotten field can never buy the mid-turn budget nor mean "forever"),
  while an unknown priority is still malformed rather than silently downgraded;
  `ts` is REQUIRED but format-unvalidated (the store is the influence record
  per d13, and nothing may ever parse or compare it — TTL is delivery-counted);
  and `prev` (d11's chain link) is a KNOWN optional field, because the store
  appends it and a strict parser that had never heard of it would read every
  chained line as malformed the moment phase 2 lands — the NAME is reserved,
  the encoding stays phase 2's durable-format decision. `TryParseLine` gives
  the JSONL reader one thing to check: torn final lines, garbage, and two
  values on a line land in `errors` beside schema faults, so one bad line can
  never throw its way out of a digest run. The lone-surrogate guard from the
  ADR-0015 skeptic pass is carried over — JsonDocument defers unescaping, so
  `"\ud800"` parses fine and throws at GetString, which without the guard takes
  down whichever reader is walking the store. 68 units.)
  `policy-content-hash` (2026-08-12; phase 1's batch-along, d12 — the trail saw
  policy EFFECTS (`policy.reload`, `policy.skip`) but never policy CONTENT, so
  "which rules were in force at time T" was unanswerable from the ledger alone.
  `PolicyContent.Of` stamps SHA-256 + byte size onto the `policy.reload` emit
  and onto a NEW `policy.write` from `ApiPolicyWriter`. The whole slice is one
  AGREEMENT question, and three sub-decisions settle it: the stamp is over the
  LOADER's view — the BOM-stripped text, which is what `File.ReadAllText`
  returns and what the writer installs — so a document written through the API
  hashes identically when the daemon reloads it (hashing raw file bytes would
  give one document two identities and make one human edit look like two on the
  ledger); hash+bytes are OMITTED, never zeroed, when there are no bytes to
  name, since hashing `""` would put a real-looking empty document on the
  ledger for a file that was never there; and a schema-MALFORMED file still
  stamps, because it IS in force — as deny-everything (ADR-0006 d4) — and an
  audit reconstructing time T needs to identify it. Stamped from the same read
  that classifies, never a second one, so the hash can't name a document other
  than the one now live. `policy.write` fires on the WRITTEN path only and logs
  under src `policy`, not `api`: a 422/412 changed no rules, the ledger records
  what was in force rather than what was attempted, and one src filter now
  shows a document's whole life. The full 64-hex hash prefix-joins the
  `GET /policy` ETag (same input, different surface). +12 units incl. the
  end-to-end write⇄reload same-hash pin and, from the `/shipshape` pass, the
  three no-bytes/no-emit edges the first cut proved only at the `Resolve`
  layer. **Phase 1 complete.**)
  `mail-store-chained-append` (2026-08-12; phase 2 — d11's durable store:
  flock-serialized chained append + `Read` + `VerifyChain`, built opus
  tests-first (37 pins), **fable skeptic pass run 2026-08-12** — the
  chain-under-concurrency attack AND the format sign-off the plan demanded.
  **Format SIGNED OFF as durable**: genesis = 64 zeros (an absent `prev`
  cannot distinguish "first line" from "head deleted"); `prev` =
  lowercase-hex SHA-256 of the previous line's EXACT bytes excluding its LF
  (the terminator is framing, not content — a torn line hashes by the same
  rule as every other); torn tails TERMINATED never repaired, the terminator
  riding the next append's single write so a crash mid-append reduces to the
  case already handled; and — the `gen` question the sign-off had to answer,
  settled in-pass before any real byte exists — **ROTATION STARTS A NEW
  CHAIN**: every generation is an independent self-verifiable file, a
  cross-file `prev` refused (it would make a file unverifiable in isolation,
  and d13 archives/prunes generations independently), with the honest cost
  stated that deleting a whole archived generation is chain-invisible —
  cross-generation continuity belongs to the cursor's `gen`, never to `prev`.
  **Three real finds, all fixed in-pass.** (1) `Append` could write a line
  its OWN strict parser rejects — an in-process envelope value (ttl 0, an
  out-of-range enum cast, a blank `to`) rendered without complaint and landed
  on disk where every future reader warns-and-skips it: mail silently lost
  behind a successful-looking append, the exact too-loose failure the
  envelope slice's direction-of-failure note warned about. Append now
  validates the EXACT rendered bytes through `TryParseLine` and refuses with
  the violations, nothing written — every line the store writes re-parses
  clean, enforced rather than assumed. (2) `VerifyChain` read an IN-FLIGHT
  append as corruption — readers deliberately never take the lock, so a
  verify racing a concurrent send can catch the tail mid-write(2); the
  unterminated-tail fault now says what it can be (interrupted write or
  append in flight, terminated and chained over by the next append) instead
  of crying tamper, pinned in both directions (suffix present unterminated,
  gone once terminated). (3) The `mail.torn` warn reported the NEW line's
  offset under the name `offset` in a warn about the TORN line; `TailLink`
  now reports the torn line's own start. Survived attack unbroken: the flock
  protocol (`FileShare.None` = LOCK_EX, kernel-released on any death incl.
  SIGKILL; lock file never unlinked per the DaemonRendezvous fresh-inode
  rule; bounded monotonic wait per invariant 2), both crash windows (die
  holding the lock / die mid-write — the single-payload write means every
  partial completion reduces to a torn tail), one-corruption-one-fault (the
  expected link is computed from actual bytes, no cascade),
  sender-supplied-`prev` forge-resistance, whole-line hashing under window
  growth (a 300KB line hashes whole), and the empty-line /
  consecutive-newline edges traced coherent between `TailLink` and `Read`.
  Named carry-in pinned to the `mail-cursor` slice: the store accepts lines
  of ANY length but d4's TrailCursor semantics DROP oversized lines — the
  cursor must size its window above anything `mail send` accepts or the send
  verb must cap the body, else big mail is written durably and never
  delivered. 42 store tests; suite 766 → 771 green twice. **Phase 2
  complete — the on-disk chain format is settled; real data may now be
  written.**)
  `mail-cursor` (2026-08-12; phase 3's hard half — d3/d4/d6/d13's
  per-(role, session) delivery cursor, built fable per the plan's
  hard-reasoning row, **independent skeptic pass run same day** (a fresh
  fable agent with no stake in the design). THE SHAPE, settled beyond the
  ADR's sketch and annotated as-built at d4: a bare offset cannot express
  out-of-file-order delivery — a mid-turn seam delivering urgent past held
  ambient forces a single offset to either lose the held line or double the
  delivered one, the exact "too early loses mail, too late double-injects"
  trap — so the cursor is a read FRONTIER plus `held`, a bounded exception
  list before it (offset + id + seenAt), delivered mail being structurally
  ABSENT rather than flagged; plus `head`, the chain's first-line hash, as
  the chain-native rotation check beside `gen` (phase 2 settled that every
  generation restarts at genesis, so rotation IS a head change; gen stays 1
  until d13's rotation machinery exists). THE TTL CLOCK: `deliveries`
  increments once per Advance — an envelope stamped seenAt at its first
  pass-over expires when deliveries − seenAt + 1 ≥ ttl ("passed over at N
  opportunities", exact at ttl=1 and 2, pinned), reads-without-advances age
  nothing, and no wall clock appears anywhere (asserted on the bytes).
  RE-ANCHOR: absent anchors at 0 (store-and-forward — offline mail reaching
  the next holder of the role IS the feature); malformed / foreign gen /
  changed head / offset past frontier / offset off every line boundary /
  held entry the file contradicts each re-anchor loudly preserving the
  monotonic deliveries counter, the frontier never enters an unterminated
  tail (TrailCursor's half-written-line rule one layer up), and cursor
  filenames percent-encode (role, session) so a hostile role cannot escape
  the mail dir. The phase-2 carry-in CLOSED at the write: `MaxLineBytes`
  (128KiB, TrailCursor's window precedent) enforced in Append. **The
  skeptic's attack earned three real fixes**: (1) the staleness guard — the
  If-Match-shaped deliveries check that refuses a stale view — was an
  unlocked check-then-rename, so two concurrent digests for one (role,
  session) could both pass it and double-inject: Advance now runs the guard
  under a per-cursor flock (the store's own "flock is a CORRECTNESS
  requirement" reasoning, one layer up; `MailStore.TryLock` shared), making
  it the authoritative at-most-once backstop, pinned by a held-lock
  bounded-fail test; (2) a PARSEABLE cursor with duplicate held offsets
  passed every check, double-rendered the envelope in one digest, and made
  Advance THROW through its never-throw contract — duplicate held offsets
  are now malformed (duplicate held IDS stay legal: the store does not dedup
  ids), a held entry addressed to another role re-anchors, and Advance's
  whole body is failure-as-value; (3) `head` was taken from lines[0] even
  when the first line was an unterminated tail, so a store born from an
  in-flight first append would later fire a tamper-flavored "different
  chain" false alarm — head now comes from complete lines only, pinned by a
  torn-only-store test. Stated-in-pass rather than fixed (each a sentence in
  the header + a pin): a re-anchor RESURRECTS expired mail with a fresh TTL
  (the seenAt stamps die with the held list; d13's redelivery cost includes
  it — expiry is a one-way door only while the cursor lives), the TTL unit
  is ADVANCES not seams-where-deliverable (a chatty urgent turn burns held
  ambient TTL — managing that is pinned as phase 4's planner obligation),
  `mail.expire` now lands AFTER the rename so the ledger states a fact, and
  `session_id: ""` normalizes to the sessionless cursor instead of sharing
  its file. 31 cursor tests + 44 store; suite 771 → 804 green twice (one
  unidentified single-test failure in one full run did not recur across two
  subsequent full runs + 15 mail-subset runs — watch for recurrence).
  `mail-send-verb` (2026-08-12; phase 3's cheap half, d7's universal write
  path — `captainHook mail send`: one JSON envelope on stdin, phase 1's
  strict parser, phase 2's chained append, one exit code; anything that can
  run a process can put mail on the bus with no jq and no hand-rolled JSON
  (the ADR's rejected shell-script alternative, inverted into the verb's
  whole reason). `Mode.MailSend` joins the wire lib's argv contract (`mail
  <subverb>`, the subverb riding EventName; the ENGINE judges it — unknown
  subverbs are a loud usage error, `mail digest` reserved for phase 4) and
  the shim's refusal list grows the verb (aot-boundary rule 11's discipline;
  the boundary doc's generic "engine-only modes are refused loudly" needed
  no edit). `ts` IS STAMPED at the verb when absent — d2's "every writer
  goes through mail send, which stamps it"; wall-clock UTC, invariant 2's
  display-timestamp carve-out — and the stamp REBUILDS the object copying
  every property verbatim (unknown fields, duplicates, all of it), so
  stamping can never launder a malformed envelope past the strict parser
  (pinned: a duplicate `to` survives the rebuild and is refused); a sender's
  own `ts` is kept verbatim, and a stamp that cannot be applied degrades to
  the parser's own report rather than ever throwing. MailSend.Run takes
  injected streams (the doctor/ui precedent) so the verb is driven entirely
  in-suite; 9 tests including the end-to-end promise the cursor slice was
  owed — send → store → chain verifies → cursor delivers — plus the argv
  parse, the shim refusal, and the MaxLineBytes refusal surfacing on stderr.
  Driven live through the real CLI against a scratch dir: stamped ts, frozen
  defaults, genesis prev, 0700/0600 modes, garbage rejected loudly with
  nothing written. Suite 804 → 813 green twice. **Phase 3 complete — mail
  has a real write path and every recipient a position in it; next is phase
  4, `mail-digest-handler`, the semantic core.**)
- ~~**7. Desktop shell**~~ — **dropped 2026-07-19** (owner decision): staying
  browser-only. The localhost web GUI is first-class on WSL2 and answers the
  need; no Photino/Tauri wrapper. (This *is* the "staying browser-only" arm
  the planned ADR would have recorded — decided here instead.)
- ~~**8. TUI**~~ — **dropped 2026-07-20** (owner decision): staying
  browser-only for the product's face. The web GUI is first-class on WSL2 and
  answers the admin/observability need; a terminal UI earns nothing the
  browser doesn't already give, and the feedback pyramid never needed it (API
  assertions + Playwright over the web UI carry the agent-dev loop). No TUI.
- [x] **9. Exec handlers — user processes as payloads** *(2026-07-12 reframe;
  was "Real handlers" — retriever/memory as framework code)*. The payload
  surface is the user's own process in any language: one coded `ExecHandler`
  adapts a configured command into the existing dispatch — normalized event
  envelope in on stdin, one strict-parsed Effect out on stdout, supervised
  like any worker. Two modes: `oneshot` (spawn per dispatch; session-edge
  events) and `resident` (daemon-held warm child over lock-step JSONL —
  the pharos-mcp kept-warm pattern; eager spawn at registration, ready-line
  handshake; effectively mandatory for before-tools events, where a cold
  interpreter re-imposes the per-tool-call tax item 12 killed).
  Registration via `~/.captainHook/handlers.json` (strict per-entry parse,
  warn-and-skip entries, stat-gate hot reload, canonicalized events);
  budgets are per-handler and **unbounded by design** — sensible per-event
  defaults, but the user may wait as long as they want (per-handler ask
  windows; the shim's 5s post-delivery clamp is removed — wait until answer
  or EOF; the harness's own timeout recorded as spec data + loud
  registration warn, never auto-edited — ADR-0010 d9);
  child env is a stripped allowlist + explicit `env`/`passEnv` from day one
  (sandboxing itself deferred to item 10's trigger). The original payloads
  survive as proof: retriever + memory land as demo scripts riding the seam;
  the tool-gate stays ADR-0005 (in-process, fail-closed, deferred). New
  engine seam: handler teardown on worker restart/drain (a resident child
  must die with its generation). Design: **ADR-0010** (supersedes the
  item-15 capability-API framing and dissolves item 11's payload half).
  Build order: ADR-0010 § Implementation plan (2026-07-12; 14 slices → 8
  phases; critical path child-wire-contract → exec-handler-adapter →
  handlers-json-registry → child-env-allowlist → resident-child-runtime →
  kill-discipline-teardown → handlers-hot-reload; adversarial verify on 9
  slices; ultracode on exactly one — resident-child-runtime). Tick slices
  here as they land.
  Slices landed: `child-wire-contract` (2026-07-12; `Core/ExecWire.cs` —
  the envelope encoder (deterministic field order, omit-null, payload
  re-written compact, single-line guarantee pinned for resident line
  framing) + the strict answer parser (closed four-shape grammar,
  collect-every-violation, duplicate/unknown/trailing/second-object all
  MALFORMED per the never-guess house rules, `Empty` as its own exit-code-
  blind case, one leading BOM stripped); 38 golden/strictness tests; no
  runtime path reaches the codec until the adapter lands — the golden suite
  IS the slice's verification per the plan).
  `shim-timeout-removal` (2026-07-12; decision 9's shim layer — the 5s
  post-delivery `ResponseTimeout` deleted at the type level (no parameter
  left to configure): past the at-most-once boundary the shim waits for the
  answer or EOF with no timer of its own; the wedged-daemon test's premise
  replaced by a TCS-gated late-answer pin (delivered + observed-still-
  waiting → released → relayed), the EOF fail-safes unchanged and still
  pinned; flow docs rewritten to the harness-backstop doctrine
  (hook-dispatch, live-deployment — including a stale "until doctor lands");
  adversarially verified by a wall-clock drive: a real 6.0s-late answer
  relayed by the IL ShimClient where the old timer abandoned at 5s. Deployed
  shim behavior changes only at the next /deploy — both artifacts swap
  together, so no skew window. Suite 457 green twice. **Phase 1 complete.**
  Deployed 2026-07-12 — live hooks ride the no-post-delivery-timer shim.)
  `per-handler-budget-windows` (2026-07-12; decision 9's dispatcher layer —
  `HandlerSpec.Budget` + `Registry.On` budget overloads; each `RunGuarded`
  gets its OWN CTS/deadline/ask-window sized to its handler's effective
  budget (override may EXCEED the default — a min-clamp under the old shared
  token could only shorten), grace scales with the effective budget
  (explicit ctor grace still wins, the classification-test seam);
  `HandlerContext` gains optional `DispatchId` (the adapter's envelope
  correlation); trail truth: `dispatch.start` keeps the default budgetMs,
  overridden handlers carry their own on `handler.*` lines. **Adversarial
  verify (skeptic) earned its keep**: caught the int-ms ask-window OVERFLOW —
  a ≥24.8-day budget (d9 blesses "no upper bound") went negative and faulted
  every dispatch with a misleading trail; fixed by registration-time
  `CheckBudget` (positive, ≤ ceiling, loud at construction) — plus three
  stale dispatch-wide-budget claims in hook-dispatch.md (fixed same commit).
  Skeptic-confirmed clean: CTS-dispose lifetime not worsened, classification
  travel headroom ≥98ms on every path, no overload mis-binding, no test
  depending on the old wall-time bound. Named carry-in → kill-discipline
  slice (N6): a budget > the daemon's 10s drain deadline can now cut a
  live dispatch at cutover — the drain-vs-long-dispatch decision the ADR
  already defers; consider a loud construction-time warn there. 6 tests.)
  `exec-handler-adapter` (2026-07-12; decisions 1/2/5/6's oneshot core —
  `Handlers/ExecHandler.cs`, the ONE coded handler that makes the closed set
  extensible in user space: spawn per dispatch, envelope out on stdin (then
  EOF), answer = first non-empty stdout line strict-parsed by ExecWire,
  reply-then-linger (effect counts when parsed; an async reaper owns the
  afterlife — drains pipes so a chatty lingerer can't wedge, records
  exec.exit), exit-0-empty ⇒ Noop, nonzero-before-answer / protocol garbage
  ⇒ fail mode (child killed on protocol error), budget cancel ⇒ best-effort
  tree kill + honored-cancel OCE (setpgid rigor deferred to
  kill-discipline). Env allowlist BAKED into this first spawn site
  (`Clear()` + PATH/HOME/USER/SHELL/LANG/LC_*/TZ/TMPDIR — sequencing risk 2
  closed; config env/passEnv arrive with handlers.json); cwd = event cwd
  else runtime home. Trail: exec.spawn/answered/exit/stderr/kill/
  protocolError. **Adversarial verify (skeptic, reproduced live) earned its
  keep**: grandchildren decouple pipe-EOF from child-exit — `sleep 30 &
  exit 0` (the everyday daemon idiom) rode the GRANDCHILD's lifetime,
  burning the budget and counting the decided-Noop case as a WEDGE toward
  escalation (stderr variant) or skipping the kill entirely (stdout
  variant); fixed by racing exit vs answer with BOUNDED post-exit pipe
  joins (PipeGrace 250ms — buffered answers still parsed, pinned by three
  grandchild tests), plus a torn stderr-tail read on the protocol-error
  path (lock + join-before-read). d2 amendment recorded: strictness binds
  through the answer line, post-answer stdout is linger chatter; linger is
  a daemon-mode pattern (collapsed abandons the reaper, late writers get
  SIGPIPE). 18 tests incl. dispatcher-integration fail-open/fail-closed/
  deny-wins-merge. Suite 481 green twice. **Phase 2 complete.** Not yet
  user-reachable — registration is code-only until phase 3's
  handlers.json.)
  `handlers-json-registry` (2026-07-12; decision 4 — ExecHandler goes
  USER-REACHABLE. `Core/ExecHandlersFile.cs`: strict per-entry parser +
  `PolicyResolution`-shaped tri-state (Absent ⇒ zero exec handlers, silent;
  whole-file malformed ⇒ zero + loud `handlers.malformed`, directory/
  unreadable/empty all Malformed-never-Absent; Loaded ⇒ valid entries
  register, invalid entries warn-and-skip with every violation collected —
  the deliberate FILE-strict/ENTRY-lenient split of d4, N2's tension);
  entry fields name/command/args/events/mode/failMode/budgetMs/
  readinessTimeoutMs/env/passEnv/cwd, events canonicalized at parse,
  duplicate names and post-canon duplicate events rejected, budget bounds
  mirror `Registry.CheckBudget` as DATA validation (a bad value skips the
  entry, never throws — probed exactly at the boundary). Registration via
  one shared `HookRun.RegisterExecHandlers` (factory overload — restart ⇒
  fresh handler, the resident-slice shape); `handlersPath` threaded
  policyPath-style through CollapsedAsync + DaemonHost.RunAsync, all three
  Program.cs entry points feed `ExecHandlersFile.ResolvePath()`
  (`CAPTAINHOOK_HANDLERS_FILE` override; null = zero exec = test-safe);
  resident entries parse valid but skip loudly until phase 5;
  `handlers.slowShape` warns oneshot-on-PreToolUse; collapsed re-reads per
  dispatch, daemon reads once at warm-up (hot reload = phase 7).
  **Adversarial verify (skeptic, probed live) earned its keep — the exact
  predicted bug class**: camelCase / upper-kebab / all-lower event
  spellings parsed valid and registered workers that NEVER fired (silent
  dead handlers visible as live in /handlers) — `Canon` only rewrites
  kebab and the runner map was case-sensitive; fixed structurally by a
  case-INSENSITIVE runner map (casing can never split the event space —
  DispatchPolicy's case-insensitive match, registration-side), pinned by a
  parser-routed three-spelling firing test. Also fixed: loudness asymmetry
  — parse-valid env/passEnv/cwd/readinessTimeoutMs were silently inert on
  registered oneshot entries while resident skipped loudly ⇒ new
  `handlers.fieldIgnored` warn until phases 4/5 wire them; CLAUDE.md env
  list gains CAPTAINHOOK_HANDLERS_FILE. Known holes recorded, not fixed:
  slowShape keys on the literal PreToolUse (a user harness's other
  decide-capable events draw no warn — needs spec data, revisit with the
  harness-timeout-hint slice); unknown-event typos register silently dead
  (vocabulary warn deferred; phase 8's expected-vs-registered view is the
  backstop). 46 tests incl. the kebab-file-fires-on-canonical-dispatch
  full loop and a collapsed E2E (child inject in the real hook answer).
  Suite 527 green twice. **Phase 3 complete — user payloads are live on
  the collapsed and daemon paths.**)
  `child-env-allowlist` + `harness-timeout-hint` (2026-07-13, the phase-4
  config batch. Env: ExecHandler consumes the entry's `env`/`passEnv`/`cwd`
  — precedence fixed-allowlist ∪ passEnv-names-from-parent, literal env{}
  applied LAST (explicit beats inherited), a passEnv name the parent lacks
  silently skips; cwd rungs config → event → runtime home, each only if it
  EXISTS (bad cwd never fails a spawn; the resolved choice + any fallback
  ride exec.spawn's data); `handlers.fieldIgnored` shrinks to
  readinessTimeoutMs. Hint: `HarnessSpec.HookTimeoutHint`
  (`hookTimeoutHintMs`, optional, strict-validated; embedded claude-code
  declares 60000 — Claude Code's hook-command default), threaded from the
  DEFAULT spec at both registration sites; a budget past it draws
  `handlers.budgetBeyondHarness` — loud, informational, never auto-synced
  (d9). Verified by the plan's decisive drive, one real collapsed run:
  secret=ABSENT (canary stripped), passed=crossed (named passthrough),
  literal=from-config (explicit add), the warn firing with the entry named,
  and phase 2's per-handler budgetMs visible on handler.ok. 13 new tests
  (canary-absence for passEnv, literal-beats-inherited, cwd precedence +
  bad-cwd fallback, hint parse rules, embedded-spec pin, warn/no-warn).
  Suite 535 green twice. **Phase 4 complete.**)
  `kill-discipline-teardown` (2026-07-12, phase 5's first chunk — the seam
  + mechanics + drain; resident-child-runtime is the second chunk. Teardown
  seam: disposal threads through the Dispatcher's C# factory closure
  (worker id → current instance; swap-on-restart disposes the REPLACED
  instance unless same-reference — the instance-registration reuse contract
  — or still shared by another slot), and escalation — where MarkDead never
  re-runs the factory — disposes the LAST instance via a new additive
  `Supervisor.SubscribeEscalated` (the settable OnEscalated stays the
  host's slot; the N3 orphan hole closed). Both paths fire-and-forget so
  the one-fault-at-a-time supervisor mailbox never waits a kill grace.
  Kill mechanics: .NET's CreateNewProcessGroup is Windows-only
  (PlatformNotSupportedException probed), so spawn goes through setsid(1)
  — exec-in-place, pid preserved, pgid == sid == pid, grandchildren die
  with the group — and TERM→2s grace→KILL lands via libc kill(-pid)
  (`ProcessGroup`); TERM-ignorers draw a second exec.kill how=kill line;
  setsid-absent degrades to tree walk, flagged pgroup=false per spawn.
  ExecHandler tracks every live child, implements IAsyncDisposable
  (teardown kills all, refuses new spawns), and budget/protocol kills send
  TERM synchronously then escalate OFF the dispatch path (OCE rethrows
  immediately). N6 DECIDED: cut, loudly — the drain gains an unconditional
  child phase after in-flight + background (Dispatcher.DisposeHandlersAsync,
  own 6s bound; ADR-0010 N6 amendment), daemon.drainChildren +
  daemon.drainCut trail it, handlers.budgetBeyondDrain pre-warns at
  registration, Doctor grace 12s→18s covers the tail. Three-skeptic
  adversarial round confirmed and fixed: leader-keyed liveness (a dead
  leader's surviving group read "gone" — IsAlive now probes the GROUP),
  drain-outruns-detached-SIGKILL (children stay tracked until their kill
  concludes; evicted instances' disposals registered pending and awaited
  by the drain), duplicate-worker-id corruption (ctor re-probes ids;
  Supervisor.Spawn refuses duplicates), a throwing restart factory
  silently killing the supervisor loop (escalates instead), drain-timeout
  background effects silently starting doomed work (intake closed →
  side.dropped), reaper exit-code lost to teardown Dispose (cached),
  setsid probed for the execute bit. Residue documented, not defended:
  collapsed-mode kills are TERM-only (exit can outrun the detached KILL);
  a gracefully-concluded child's daemonized survivors are its own
  (kill paths take the group, graceful exit releases it — ExecHandler
  header). 12 tests (pgid==pid end-to-end, grandchild dies with group,
  SIGKILL escalation, dead-leader group teardown, drain-awaits-kill,
  restart/escalation/singleton/shared disposal, lingerer teardown, the N6
  daemon E2E asserting the cut still ANSWERS). Suite 547 green twice.)
  `resident-child-runtime` (2026-07-13, phase 5's second chunk, the ADR's
  one ULTRACODE slice — the daemon holds children warm. Wire: `ExecWire`
  gains `{"ready":1}` strict readiness (`TryParseReady`) + the optional
  `dispatchId` echo on every answer (mandatory for resident, extracted into
  `ExecAnswer.Ok`). `ResidentExecHandler`: eager spawn AFTER teardown-seam
  admission (new `IEagerStart` — `TrackSwap` returns admitted?+predecessor
  so a post-drain restart never spawns and a successor sequences behind its
  predecessor's kill), the three-way readiness race (ready line vs
  readinessTimeoutMs vs early exit) transitioning `ChildState`
  Spawning→Ready/Failed exactly once (transition-wins-then-kills), lock-step
  JSONL with the mandatory echo as stale-answer backstop (kill-on-timeout is
  the primary defense — every timeout path replaces the child),
  fail-mode-while-warming (uncounted, loud — the dispatch token never races
  the readiness timer), counted-throw-on-Failed → supervised respawn.
  `ChildRecords` writes `~/.captainHook/children/child-<pid>.json` (pid +
  /proc starttime identity, atomic write, once-per-process sweep) — NOT the
  XDG tmpfs, so a SIGKILLed daemon's orphan stays traceable for phase-8
  doctor. Registration: `residentAllowed` gates resident to the daemon;
  collapsed runs skip fail-open loudly and register a DENY stub for
  fail-closed (a declared gate never silently vanishes — the N2 inverse);
  `handlers.readinessBeyondBudget`/`residentFanout` warns; slowShape stays
  silent for resident-on-before-tools (the recommended shape). Two
  adversarial rounds: a pre-build design panel (10 confirmed / 5 refuted —
  echo made mandatory, fail-mode-while-Spawning, Start-after-admission,
  records-in-home, deny-stub) then a 5-lens impl-verify (9 serious all
  confirmed + fixed: expired-in-queue Backlogged guard returning fail-effect,
  tokened envelope write, cancellable predecessor + pre-spawn disposed check,
  gate-resolve on teardown, live-child EPIPE → protocol error). 24 resident
  tests (readiness strict table, eager-spawn-before-dispatch, warm reuse, all
  three race arms, missing/mismatched/double-answer echo, wedge → uncounted
  respawn, FakeClock escalation with all children dead, records sweep,
  IEagerStart sequencing, the no-respawn-after-teardown cut, daemon E2E: two
  warm hooks one child + drain kills + record gone). Suite 580 green twice.
  **Phase 5 complete — user processes run warm, and no child outlives its
  daemon.**)
  `collapsed-mode-degrade` + `demo-payload-scripts` (2026-07-13, the phase-6
  batch — prove the seam, protect the fallback. DEGRADE: a resident entry
  with no daemon runs the SAME ResidentExecHandler in oneshot-lifecycle
  (spawn→serve-one→die), replacing the interim skip/deny-stub. Composed at
  one seam: BuildDefaultRegistry takes a `collapsedEvent` — a collapsed run
  passes its one dispatched event so only that event's exec handlers
  register (an unrelated event's resident child never spawns for a
  single-shot hook; daemon passes null = warm all), and CollapsedAsync
  awaits DisposeHandlersAsync in a `finally` AFTER the stdout answer so N3's
  no-orphan is STRUCTURAL, not best-effort. A fail-closed gate now genuinely
  runs (real verdict or fail-closed deny on any spawn/ready/protocol
  failure) — strictly stronger than the deleted deny stub, never a silent
  grant. `handlers.residentDegraded` marks the daemon-down path.
  DEMOS: examples/payloads/ — retriever.sh (resident, PreToolUse, greps
  notes and injects) + memory.sh (oneshot, Stop, appends a durable log) +
  handlers.json + README, dependency-free POSIX sh outside the build graph.
  Two adversarial skeptics confirmed the degrade SOUND (every fail-closed
  collapsed outcome → deny, verified live; ordering + no-orphan hold);
  5 LOWs fixed (stale docs ×3, teardown bound 6s→8s, construct-inside-try).
  Tests: registration filter/degrade-log, two collapsed no-orphan E2Es
  (fail-open serves + fail-closed runs-for-real), the demo E2E driving the
  REAL committed scripts through the daemon (retriever injects a seeded
  note, memory persists its log, child dies at drain). Suite 583 green
  twice. **Phase 6 complete — the seam is proven with scripts, the fallback
  degrades without orphaning.** Remaining for item 9: phase 7
  (handlers-hot-reload), phase 8 (doctor-orphans, handlers-observability).)
  `handlers-hot-reload` (2026-07-13, phase 7 — the runtime registry-refresh
  seam the Dispatcher had NO precedent for: it only ever spawned workers in
  its ctor. Stat-gate `ReloadingHandlers` (a literal ReloadingPolicy mirror)
  at the top of DispatchOneAsync drives `Dispatcher.Reconcile(fresh)`, which
  diffs the reloadable (exec) workers by a STABLE id (`AssignIds` —
  (event,name), coded a constant id-seed, exec names file-unique) carrying an
  INJECTIVE config fingerprint (`ExecFingerprint`, length-prefixed):
  unchanged ⇒ KEEP (warm child untouched — no churn), gone ⇒ REMOVE
  (Supervisor.Remove + evict-and-kill), changed ⇒ CHANGE (Remove frees the
  id, re-Spawn rides the existing TrackSwap to evict the old instance and
  thread its kill as the successor's predecessor), new ⇒ ADD. `_runners`
  became a volatile swapped-whole snapshot (lock-free dispatch reads, one
  atomic publish under _reconcileLock). Malformed reload ⇒ zero exec ⇒ every
  resident REMOVE'd (no keep-last-good, handlers.malformed loud); the summary
  rides handlers.reload. `Supervisor.Remove` is the one new F# member
  (MarkDead + drop the child-spec so a late exit never restarts a retired
  worker; actor.remove). Reuses phase 5's kill/drain machinery wholesale — no
  bespoke kill path, so N3's leak surface does not grow with the seam.
  THREE-SKEPTIC adversarial round (orphans / diff-correctness / concurrency)
  — 1 HIGH + 3 MED confirmed and FIXED:
  • HIGH (generation aliasing) — a CHANGE's Remove+Spawn minted a fresh
    handle whose generation reset to 1, aliasing the retired worker's gen 1,
    so the reload's kill of the old child (a mid-dispatch EOF crash) was
    charged to the fresh REPLACEMENT: spurious restart → churn, and under
    repetition → escalation to permanent-DEAD (deny-every-hook for a
    fail-closed gate). FIX: fault signals (ChildExit/ChildWedged) now carry a
    supervisor-global monotonic EPOCH (`ActorRef.Epoch`), never the per-handle
    restart count — a reused id can't alias; Generation stays the read-model
    restart count. Regression test: CHANGE mid-conversation, the replacement's
    Generation stays 1.
  • MED (drain-await gap, confirmed ×2) — all three eviction sites removed
    from _liveInstances and registered into _pendingDisposals in SEPARATE lock
    sections; the drain's single-lock snapshot could fall between and await
    neither, dropping an in-flight TERM→grace→KILL (orphan on exit — the very
    thing _pendingDisposals exists to prevent). FIX: `FireDisposeLocked`
    registers the disposal UNDER the same lock as the removal (atomic); only
    the kill's fast synchronous prefix runs there (the handler's own lock,
    never _teardownLock — no reentrancy).
  • MED (restart-vs-remove) — a fault-loop restart already committed to could
    resurrect a REMOVE'd worker as a straggler (cleaned at drain). FIX:
    start() re-checks handle.IsDead before spawning, collapsing the window.
  • MED (fingerprint collision) — a naive \x1f/\0 separator scheme aliased
    args ["a","b"] with ["a\x1fb"] (and env value-with-'='); FIXED pre-report
    by length-prefix framing (injectivity unit-tested).
  LOW residue, documented not defended: an exec name EQUAL to a coded name
  ("echo"/"latency-probe") gets a #2 id (works, but dispatch.json exclusion
  keys on the shared name) — a name CONTAINING '#' is now rejected at parse
  (the sharp reorder crack); the coded id-seed is re-read from
  CAPTAINHOOK_PROBE per reload but is constant within a daemon (a probe flip
  needs a new daemon → new ctor, never a cross-reload diff); a budget or
  readiness edit re-warms the resident child (safe over-churn — those fields
  must stay in the fingerprint to take effect at all). Tests: 15 (fingerprint
  injectivity, no-churn KEEP, add/remove/change, events-list
  churn-only-affected, malformed-kills-all, post-drain refusal, coded-survive,
  stat-gate ×2, Supervisor.Remove, the CHANGE-mid-dispatch misattribution
  guard, the add-then-remove daemon E2E). Suite 598 green twice. **Phase 7
  complete — edit handlers.json, the next hook obeys, and unchanged warm
  children never flinch.** Remaining for item 9: phase 8 (doctor-orphans,
  handlers-observability).)
  `doctor-orphans` + `handlers-observability` (2026-07-13, phase 8 — the
  read-side closers, both report/projection-only, no adversarial verify per
  the plan. DOCTOR-ORPHANS: `Doctor.SweepOrphans` applies the daemon reap's
  SAME double-guard to a second entity class, `~/.captainHook/children/` —
  child pid ALIVE + `/proc` starttime MATCHES + owning daemon gone (dead pid,
  or a live pid that is not a captainHook `--daemon`) ⇒ ORPHAN, reported via
  `doctor.orphan` and the `doctor` verb's stdout. REPORT-ONLY: doctor never
  signals the child (a misclassification is a wrong trail line, not a killed
  process — why the slice needs no verify); stale records (dead pid /
  starttime drift / corrupt) are swept in passing. The pid-reuse guard makes
  the orphan verdict safe — a recycled child pid has a different starttime, a
  recycled daemon pid fails the cmdline check. OBSERVABILITY: child state
  reaches the API through a new one-way Core seam `IResidentObservable` (child
  state lowercased + pid, plain data) that `ResidentExecHandler` implements
  and `Dispatcher.Snapshot` correlates from `_liveInstances` — the F# Worker
  never learns what a `ChildState` is, the arrow holds; `HandlerDto` gains
  `childState`/`childPid` (null for oneshot/coded). Expected-vs-registered:
  `HandlersDto` gains the handlers.json tri-state (`source`
  absent|malformed|loaded + `error`) and an `expected[]` — every declared
  entry JOINED by name to the live Snapshot, a valid entry `registered:true`,
  a warn-and-skip entry `registered:false` + violations and NEVER a live row
  (the N2 caution, structural). The GUI `SupervisionPanel` grows a child
  column + a handlers.json section (tri-state mirroring `PolicyPanel`);
  DTO→schema→TS regen + committed `ui/` rebuild rode the `dto-schema-codegen`
  chain, e2e-asserted. Tests: `DoctorOrphansTests` (7 — alive-orphan reported
  + record kept as evidence, healthy-owned ignored, non-daemon-owner via the
  default guard, dead/pid-reuse/corrupt swept), `ApiReadEndpointsTests`
  (child-state null on coded, expected-vs-registered join, skipped-not-live),
  a resident child-state `Snapshot` test, the `SupervisionPanel` Playwright
  e2e. Suite 607 green twice. **Phase 8 complete — item 9 DONE: the exec-
  handler seam is built, proven, orphan-safe, live-reconfigurable, and
  observable end to end.**)
- [ ] **15. Handler capability policy (egress)** — layer 3 of the native
  policy story: what may a running handler *reach*. *(Narrowed by ADR-0010,
  2026-07-12: payloads are user processes, so there is no in-process
  capability API to design — the process boundary is the isolation seam.)*
  What remains: the env-allowlist policy ships WITH item 9 (ADR-0010 d5);
  sandboxing (namespaces, seccomp, cgroup caps, network deny) is the
  enforcement half, deferred to item 10's trust-model trigger with ADR-0004
  N2's process isolation as the backstop. The old `HandlerContext`-hands-out-
  capabilities principle survives only for first-party *in-process* handlers,
  if an egress-bearing one ever exists.
- [x] **10. Hook trust model** — installing a hook = installing arbitrary code
  that runs on every prompt. The install UX must show exactly what will
  execute, from where, before touching settings. **Rides WITH items 5–6**
  (the install operations and install UX are its only real surface), not a
  phase of its own. *(Made concrete by ADR-0010: installing = a command line
  + `handlers.json` entry the GUI shows verbatim; this item's trigger also
  gates the sandboxing half of item 15.)*
  Design recorded in **ADR-0011** (accepted 2026-07-19): the same-user
  trust boundary ratified (installer = the machine's own user installing
  things they can read; resilience not prevention for the daemon); consent
  as the v1 threat model (verbatim-and-resolved confirm before any write);
  one new write verb `PUT /api/v1/handlers` on the `PUT /policy` pattern,
  refusing warn-and-skip entries at the write (hand edits stay
  entry-lenient); enable/disable composed from shipped dispatch.json rules;
  **no settings.json auto-edit** (wiring hint shown-not-written, auto-wiring
  waits for observed friction); write-authz ratified as the existing bearer
  token; sandboxing + provenance explicitly un-triggered until code the
  user cannot read arrives (that surface fires a new ADR first).
  Build order: ADR-0011 § Implementation plan (2026-07-19; 10 slices → 5
  phases; critical path handlers-put-endpoint → handlers-etag-ifmatch →
  gui-handlers-editor → gui-verbatim-confirm → docs-update; adversarial
  verify on exactly three slices — the write endpoint, its test pins, and
  the editor's 412 re-merge; no ultracode). Tick slices here as they land.
  Slices landed: `handlers-put-endpoint` + `handlers-install-strictness` +
  `handlers-etag-ifmatch` + `handlers-write-adversarial-tests` +
  `install-template-fix` (2026-07-19, the server seam as one batch —
  `ApiHandlersWriter` on ApiPolicyWriter's exact pattern (strict-parse with
  the daemon's own ExecHandlersFile, RFC 7232 If-Match after validation,
  sibling-temp same-dir rename, BOM parity) with its own closed
  `HandlersWriteOutcome` DU; d3's tightening pinned by CONTRAST — the same
  bytes the write path 422s (any warn-and-skip entry, violations labeled
  per entry) load entry-lenient via Resolve, so the split can't silently
  converge; `GET /handlers` gains raw/etag (HandlersDto + header) and
  `StatusDto` gains the resolved `shimPath` for the wiring hint (decided
  pre-pin per the plan's sequencing note); DaemonHost wires the writer from
  the same handlersPath the read model and ReloadingHandlers share; the
  stale `{dotnet} {captainHookDll}` install template → `{captainShim} hook
  {event-kebab}` with a spec-pin test (N4). 17 tests incl. the
  resolver-interleave atomicity probe (400 writes against the REAL
  ExecHandlersFile.Resolve — never a non-Loaded flash) and the
  both-directions daemon E2E (PUT-install → the NEXT dispatch runs the
  child; PUT-uninstall with If-Match → gone), the plan's two named
  vacuous-pass traps. Adversarially verified (skeptic, 21-case
  writer-vs-loader matrix + ETag round-trip + If-Match grammar + stamp
  collision probes): NOTHING above LOW survived; three LOW
  inherited-by-design edges (in-string UTF-8 refusal direction, 1 MiB cap,
  symlink flattening) recorded in the flow doc, not defended. Suite 624
  green twice.)
  `gui-handlers-editor` + `gui-verbatim-confirm` + `gui-enable-disable` +
  `gui-wiring-hint` (2026-07-19, the GUI batch — phase 8's read-only
  handlers.json section grown into the install surface (`HandlersSection`):
  install/edit/uninstall as whole-file read-modify-write over GET+ETag /
  PUT+If-Match, every write behind the d2 verbatim-and-resolved confirm
  (exact command/args/events/mode/fail/budget/cwd/env/passEnv + the entry
  JSON; "resolved" = the GUI mandates absolute command paths, hand edits
  stay free); a valid-but-unreconciled entry renders "pending (live on the
  next hook)" — honest to the stat-gate, distinct from skipped; the d4
  toggle composes a PREPENDED unconditional handler-deny through the
  existing PUT /policy (enable removes only toggle-shaped denies — off→on
  is IDENTITY on hand policy); the d5 wiring hint renders the install
  template per event ({captainShim} → /status shimPath), shown never
  written. **Adversarial verify (the plan's third mandated pass) —
  the 412 INVERSION HELD under attack** (no lost-update sequence
  constructible: conflict verdict structurally tagless, etag/raw travel as
  one consistent pair, render-time closures can't pair stale raw with fresh
  etag); five ancillary findings all fixed + pinned: render-crash on
  daemon-tolerated config shapes (rules:[null], handlers:[null] — defensive
  parse), badge-vs-daemon divergence (first-match now includes scoped rules
  ⇒ "scoped", never a wrong-direction "disabled"), malformed-policy badge
  (— not on/off), destructive toggle round-trip (hand-written allow now
  kept inert behind the prepended deny), and the two-read tear gate
  (composable requires raw to parse — a GUI write never drops entries it
  couldn't render). e2e: fixtures gain an isolated CAPTAINHOOK_HANDLERS_FILE
  (a latent read-only gap made load-bearing by the write verb) + `fireHook`
  (the engine's shim mode inside the sandbox); 4 Playwright tests incl. the
  full loop (install → confirm → real hook runs the payload → registered)
  and 412-stops-nothing-clobbered. 51 web unit tests; 14 e2e; committed
  ui/ rebuilt.)
  `docs-update` (2026-07-19, the capstone — terminal by construction:
  management-api.md § The writes (both writers, the d3 strictness split,
  the three accepted LOW edges), management-gui.md § The handlers editor
  (the 412 inversion, the toggle compose, the wiring hint), ADR-0011's
  Ground truth back-filled as a decision→code index. **Item 10 complete —
  ADR-0011 fully landed: all 10 slices, 5 phases; installing a payload is
  a consented, verbatim-shown, atomically-written, hot-reloaded act on
  both API and GUI, and the trust boundary is a written document.**)
- ~~**11. N-runtime harness**~~ — **dropped 2026-07-19** (owner decision):
  staying .NET-only for the core. ADR-0010 already made payloads N-language
  by construction — user processes in any language — which satisfies the
  polyglot story without porting the shim/daemon/dispatch core. DESIGN.md's
  comparison thesis is retired as a build goal.

## Parking lot

- **Mobile** — a responsive browser UI over LAN already answers the likely
  need; no app until a real use case demands one.
- **Community registry** — discovery/versioning for shared hooks & skills.
  (Plausible under ADR-0010: a shared hook is a script + manifest, not a
  compiled plugin.)
- **shipshape as a Stop-hook** — the repo verifying itself with the very
  mechanism it demonstrates.
- **Packaging** — single-file publish for the JIT engine (the shim's
  Native AOT half promoted to item 12).

## GUI direction (updated 2026-07-19: browser-only decided)

**One API; the browser UI is THE face** (item 6, shipped). The desktop-shell
face (Photino) was dropped with item 7 — localhost-in-browser is first-class
from WSL2 and already does the job with less machinery. The TUI (item 8)
remains a possible second face against the same API, for product reasons
only — the feedback pyramid stays API assertions → Playwright.

The structured log stream (JSONL + correlation ids) is the GUI's live data
feed — the observability layer was built GUI-ready before the GUI existed.
