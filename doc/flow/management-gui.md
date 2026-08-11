# Flow: the management GUI — an observability face, served same-origin

The browser UI for captAInHook (ADR-0008, roadmap item 6): a localhost web app
the daemon serves from its own port, so an operator can *watch hooks run* — did
it fire, which handler, what effect, how long, did the actor restart — and edit
the one piece of config the daemon owns, the dispatch policy. It is a **client
of the management API** ([management-api.md](management-api.md)), not part of the
engine: the daemon owns no browser code, only the bytes it serves. Everything
interesting is browser code in its own **`web/`** project (React + Vite +
Zustand); the engine-side blast radius is one static route and one CLI verb.

Two facts from the API shape the whole thing. **The auth gate refuses a foreign
`Origin`**, so a UI served from a *different* origin (a Vite dev server on
`:5173`) is 403'd on every call — **same-origin is forced, not chosen**. And
**`EventSource` cannot set headers**, so the live stream is consumed via `fetch`
streaming with reconnect hand-rolled off the last event id. Node/npm are a
**dev-only build tool** — the built assets are committed; deploying or running
captAInHook needs no Node (invariant 3 intact).

```
 captainHook ui  ── reads 0600 api.json (port+token) → opens the browser at
   (Program.cs)        http://127.0.0.1:<port>/ui#t=<token>     ── the FRAGMENT,
        │                                                          never a query param
        ▼
 ┌─ browser ─────────────────────────────────────────────────────────────────┐
 │  GET /ui           ── the shell: bearer-EXEMPT (a top-level nav carries no  │
 │   (no bearer)         Authorization); INERT bytes; Host+Origin still gate   │
 │        │              (ApiHost.ServeUiAsync → ResolveUiFile guard)          │
 │        ▼                                                                    │
 │  bootstrapToken()  ── hash → sessionStorage → replaceState SCRUB → the      │
 │   (auth.ts)           token is on no URL, in no history, in no server log   │
 │        │                                                                    │
 │        ▼   every data call carries Authorization: Bearer via apiFetch       │
 │  ┌──────────────────────────── one Zustand store ───────────────────────┐  │
 │  │  VIEW · session · stream · status · policy · handlers · harnesses ·   │  │
 │  │  trace                                                                │  │
 │  └───┬─────────────┬──────────────┬───────────────┬───────────────┬──────┘  │
 │   Nav(rail)    Status/Superv/   PolicyPanel    TracePanel       (each an     │
 │   session +    Harnesses        GET→PUT/policy  SSE → foldFrame  island:     │
 │   view writes  useApiJson poll  ETag lifecycle  → trace slice    own root)   │
 │        └── every screen island renders null unless `view` names it ──┘       │
 └────────┼──────────────┼──────────────┼───────────────┼─────────────────────┘
          │  GET /status │ GET/PUT       │ GET /events (fetch stream)
          ▼              ▼ policy        ▼
                    the management API (same 127.0.0.1:<port>)
```

## The frontend is islands over one store, not a prop-threaded tree (d8)

Each screen in the table above mounts as its **own `createRoot`** on a sibling
`<div>` (`main.tsx` is the mount table; `index.html` has the leaf divs) — DOM
siblings with **no shared React parent**. They coordinate *only* through **one
provider-less Zustand store** (`store.ts`) that lives outside every tree: an
island subscribes to the slice it reads (`useStore(s => s.trace)`) and nothing
is prop-threaded across screens. This is React's *grain* (the store is
`useSyncExternalStore` shrink-wrapped) against React's *default*; it kills the
giant `<App>` tree and prop drilling the owner set out to avoid, while landing on
the ecosystem tool rather than a bespoke bus.

**Navigation is one more store field, not a router (ADR-0015 d1).** A persistent
console rail lists the five views (Trace — the landing view — Handlers, Policy,
Harnesses, Status); clicking one writes `view`, and every screen island returns
`null` unless `view` names it, so the region holds exactly one screen. The gate
sits AFTER each island's hooks, so a hidden island keeps polling and comes back
current instead of blank — and, because rendering null does not unmount, the
policy editor's unsaved draft and pending If-Match tag survive a trip to another
view. A router would have bought URL persistence (deliberately deferred — the
`#t=` scrub owns the fragment) at the cost of unmounting exactly that state.
The property that makes this safe is that the SSE client runs OUTSIDE React
(`main.tsx`) and folds into the store regardless of what is rendered: lines that
arrive while Trace is off-screen are all there on return
(`nav.spec.ts` pins both halves). When the session is not live every screen is
null, so a `SessionNotice` island fills the region with the one actionable
thing a credential-less visitor has — the `captainHook ui` launch instruction —
while the rail states the session tersely.

The UI is **state-shaped, not event-shaped**: almost everything rendered is
current state (status/policy/handlers, the accumulating trace). The one genuine
event source is the SSE stream, and it is **folded into the store by exactly one
reducer** (`foldFrame` → `foldTrace`) — one place stream state ever mutates. The
`PUT /policy` verdict lands the same way. The store file *is* the contract every
island and the SSE client build against: `SseFrame` mirrors the server's SSE
grammar, `PolicyVerdict` mirrors the closed `PolicyWriteOutcome`, and the slice
shape maps 1:1 onto the screen table.

## The token handoff — fragment, scrubbed, tab-scoped (d3)

A browser can't read the 0600 `api.json`, so it must be *handed* the token, and
the handoff must leak it into neither a server log nor browser history. The
`captainHook ui` verb (`UiVerb`, collapsed-mode, no daemon of its own) reads
`api.json` and opens the OS browser at `…/ui#t=<token>` — the **fragment**, which
is sent to no server and written to no access log. The opener is WSL-aware
(`UiVerb.LauncherCommand`): on WSL2 a bare `xdg-open` is a silent no-op, so the
launcher detects WSL and hands the URL to the Windows shell's `Start-Process`,
which opens the real default browser (the daemon's `127.0.0.1` is reachable via
WSL2 localhost forwarding). On load, `bootstrapToken()`:

1. reads `location.hash`; 2. stashes the token in `sessionStorage` (survives a
reload, dies with the tab); 3. **immediately** `history.replaceState`s the hash
away, so the credential-bearing URL is never observed or persisted; 4. every
`fetch` thereafter attaches `Authorization: Bearer` (`apiFetch`). A fresh hash
token beats a stale stash (re-running the verb after a cutover must win); neither
present ⇒ the shell sits inert and says how to get a session.

The one residual: the URL rides the opener's argv, briefly world-readable in
`/proc/<pid>/cmdline` to another local user — narrower than a query param (no
log/history/server), inherent to "the CLI opens the browser", recorded as a
hardening candidate (scratch, `UiVerb.DefaultLauncher`).

## The `/ui` route — the one bearer-exempt surface (d2)

`GET /ui[/*]` streams the staged `ui/` dir as opaque static bytes. It is the
API's **single deliberate hole**, and safe because a top-level browser navigation
carries no `Authorization` header (it *cannot* — that is the whole handoff
problem) — so the shell must load unauthenticated, then authenticate itself. The
hole is held to inert bytes by three properties:

- **bearer-ONLY, and only when a UI is staged.** `HandleAsync` routes a `/ui`
  path through `ApiAuthGate.EvaluateShell` (Host + Origin only) instead of the
  full `Evaluate`; the split is one method so `/ui` and `/api/v1/*` can never
  drift on transport policy. A pure API host (no `_uiDir`) keeps `/ui` fully
  gated.
- **scoped to exactly `/ui[/...]`** (`IsUiPath` on the same `AbsolutePath` the
  router reads) — `/uifoo` is not exempt, and the router never sees a UI path, so
  no data route can ride the exemption.
- **traversal-guarded.** `ResolveUiFile` maps a request path INTO `ui/` or
  returns null: rooted paths, NUL, any canonicalized escape, the root itself,
  directories, missing files. The prefix check appends the separator (a sibling
  `ui2/` can't pass) and trims a trailing separator on the root.

Adversarially verified end to end: no encoded/mixed traversal form escapes;
`HttpListener` collapses dot-segments *before* the gate, so `/ui/../api/v1/status`
normalizes to a data route and meets the full 401. Pinned by
`ApiUiRouteHttpTests` (byte-identity of the shell authed vs unauthed, token
never in served bytes, raw-socket traversal), `UiResolveGuardTests` (the guard
against files that really exist outside), `UiShellGateTests` (the shell gate).

## The live stream client — fetch streaming, opaque cursor (d4)

`runEventStream` (`sse.ts`) consumes `GET /events` off a `fetch`
`ReadableStream` because `EventSource` can't send the bearer. A pure protocol
layer (`splitRecords` / `parseRecord` / `recordToFrame`, factored out for unit
tests) sits under a reconnect loop that honors three server-pinned semantics and
two distinct failure modes:

```
 LINE   id: <cursor> ── the cursor is OPAQUE (ADR-0009 d2): stored, echoed in
                        Last-Event-ID, never interpreted
 GAP    (no id)      ── the cursor MUST NOT advance over the hole — that is what
                        lets the reconnect recover the dropped region from the file
 RESET  id: 0        ── re-anchor to the restarted id space; foldTrace CLEARS

 transient  (network drop / draining daemon 503) → backoff (server retry: hint)
                        + resume from the cursor        → stream = "retrying"
 dead       (401/403 = token rotated at a cutover)   → STOP the loop, end the
                        session; the browser cannot re-read api.json (decision 4)
```

The dead-credential answer typically arrives on the reconnect *after* a drop, so
misclassifying it as "another blip" would retry a dead token forever — the check
sits on the response status, before any retry decision. First connect sends no
`Last-Event-ID` ("from now"). Frames fold into the store (`foldFrame`); the
client `reader.cancel()`s in a `finally` so a throwing subscriber can't leak the
TCP stream and pin the daemon's `openStreams` idle-defer counter. Adversarially
verified against a live daemon (8 real TCP cuts zero-dup/zero-loss; a 57,853-line
eviction fully recovered; cut-at-the-reset ⇒ genesis replay exactly-once; the
401-dead vs drop-retry split) — a pass that **caught and fixed a real server
bug**: the from-now anchor raced the first flush, silently losing a line appended
at client-live (`ServeEventsAsync` now anchors before the flush). Pinned by
`sse.test.ts`.

## The one write — the policy editor's ETag lifecycle (d1)

`PolicyPanel` GETs `/policy`, seeds an editor from the raw file, and PUTs through
`submitPolicy` — the API is the *editor of the file*, so the panel surfaces the
DAEMON's own verdicts (the strict parser's 422 violations, the 412 conflict),
never its own guesses. The subtle part is the **ETag discipline**, the silent
failure mode the phase's adversarial verify targeted (the server honors
`If-Match` only when supplied, so a forgetful client silently reopens the
blind-overwrite window):

| step | rule |
|---|---|
| load | adopt the GET's ETag (header, else body `etag`, else null for an absent file) |
| PUT | **always** send `If-Match` once an etag is known (null = create, nothing to protect) |
| 200 | adopt the response's ETag **without a re-GET**; the echoed `PolicyDto` is the fresh read |
| 412 | adopt the verdict's `current` before the retry — a deliberate overwrite of a *seen* conflict, draft preserved |
| tagless 200 | map to null + re-seed via GET — **never `""`**, which the server IGNORES (`IsNullOrWhiteSpace`) and would write blind |

Verified end to end (a stale tag 412s with the file byte-identical after; a
save-twice with no reload 200s twice). Pinned by `policy.test.ts` and the
`policy.spec.ts` E2E. The write is effective on the next dispatch via
`ReloadingPolicy`'s stat-gate, exactly as a hand-edit — so editing policy in the
GUI surfaces a `policy.reload` in the very trace beside it.

## The handlers editor — install behind the verbatim confirm (ADR-0011)

The Supervision island's handlers.json section (`HandlersSection` in
`HandlersEditor.tsx`) grew from phase 8's read view into the install surface:
install / edit / uninstall as a **whole-file read-modify-write** over
`GET /handlers`+ETag / `PUT /handlers`+If-Match (`submitHandlers` in
`handlers.ts`), every write gated by the **verbatim-and-resolved confirm**
(d2 — the modal renders the exact command, args, events, mode, fail mode,
budget, cwd, env/passEnv, plus the exact entry JSON; the GUI mandates an
ABSOLUTE command path so "resolved" needs no server round-trip). The daemon's
verdicts are surfaced, never guessed: a 422 (including d3's skip-refusal)
lists the parser's own violations inside the modal.

**The 412 discipline INVERTS the policy editor's pin 3** — the phase's named
sharp edge. A raw-text editor may adopt `current` and retry (overwriting a
SEEN conflict on text the user is looking at); a composed read-modify-write
doing that silently clobbers the other editor's entries — the classic lost
update, because the compose was built over a stale file. So `submitHandlers`
maps a 412 to a tagless `conflict` verdict (blind retry is unrepresentable),
and the panel STOPS: the stale compose is discarded, the file re-fetched, the
user's draft form preserved for re-review over what is actually there. A
valid-but-not-yet-reconciled entry renders as **pending (live on the next
hook)** — registration rides the per-dispatch stat-gate, and the row says so
instead of reading like a fault.

**Enable/disable is composed from shipped layers** (d4): the toggle writes an
UNCONDITIONAL handler-level deny into dispatch.json through the existing
`PUT /policy` — PREPENDED, so first-match-wins can't be defeated by a later
hand-written allow; enable removes exactly those toggle-shaped denies and
nothing else. Scoped rules (event/project/session) render a "scoped" badge —
no clean off-switch is pretended. Each toggle GETs policy fresh (raw+etag),
composes, and PUTs conditionally; a 412 is surfaced and re-tried by the user.
The toggle never composes over a malformed or unparsable policy.

**The wiring hint is shown, never written** (d5): the confirm modal renders,
per selected event, the harness install template (`{captainShim}` → /status's
resolved `shimPath`, else the deploy-home fallback; `{event-kebab}` →
kebab-cased event) as the line to hand-paste into settings.json. The GUI
cannot know whether an event is already wired (N3) — the hint states the
requirement, not a diagnosis.

## Types are generated from the C# DTOs — drift is a build failure (d6)

The server is a dumb `HttpListener` (no ASP.NET, so no OpenAPI), so the pipeline
is BCL-native: `ApiSchema.Export` runs `JsonSchemaExporter` over the DTO records
→ a checked-in `web/schema/api.schema.json` (camelCase wire casing, NRT-honest
nullability, strict numbers) → `scripts/gen-types.mjs` runs
`json-schema-to-typescript` → `web/src/api.gen.ts`, consumed everywhere. The C#
DTO is the single source of truth; `ApiSchemaTests` is the drift detector (a DTO
change that forgets to regenerate fails at build time). **Convention: a DTO
change regenerates the schema AND the TS in the same commit.**

## The dev loop and the deploy (d2/d7)

The frontend `build --watch` writes into the `ui/` dir the daemon serves, so the
Playwright/agent loop drives the daemon's *own* same-origin `/ui` — an asset
rebuild + full page reload, **no Origin relaxation, zero auth holes**. (Vite HMR
on its own origin is a reserved, env-gated escape hatch, not built.) `/deploy`
stages the committed `ui/` as a **third artifact** beside the two executables —
NOT wire-coupled (no MVID/skew hazard; the daemon serves it as opaque bytes), so
a missing `ui/` degrades to a 404 GUI, never a broken hook; `bin.prev` rolls all
three back together. Deploy never runs npm.

**The screenshot loop** (ADR-0015 d5) is the eyes on that rebuild: `npm run snap`
starts a seeded sandbox daemon, drives every view × light/dark headless, and
writes PNGs into the gitignored `web/.screens/` for a human — or an agent — to
READ before a change is committed; `npm run ui:preview` holds ONE such daemon
open (printing its `/ui#t=` URL, taking `trail`/`hook`/`burst` commands on
stdin) for hand-driving. Both consume the same sandbox module as the e2e
fixture, so what the tests see and what the camera sees cannot drift. The
`ui-loop` skill documents the loop itself.

## E2E — the whole GUI against a real daemon

`web/e2e/` (`@playwright/test`, `npm run e2e`) drives the daemon's own
same-origin `/ui` end to end — the DOM + accessibility tree is the agent-legible
surface the ADR chose over TUI scraping. The reasoning is the **daemon sandbox**
(`e2e/daemon.ts`, extracted from the fixture by ADR-0015 d5 and shared with the
preview + snap scripts; `fixtures.ts` is now just the Playwright binding):
a fresh daemon per test, fully isolated from the live `~/.captainHook` tree (temp
`XDG_RUNTIME_DIR` / `CAPTAINHOOK_LOG` / harness dir / dispatch file), readiness by
the 0600 `api.json` appearing (polled, not slept), teardown SIGTERM-by-PID →
await true exit → SIGKILL; `global-setup.ts` builds the engine and stages the
fresh `ui/`. The phase's named flakiness bit twice, and the second bite was
misdiagnosed for a month: waiting for each daemon's true exit (drainers don't
pile up) and a thread-pool floor fixed a real warm starvation under the
browser's CPU load, but the *residual* per-run flake was never CPU at all —
measured 2026-08-11, it is **WSL2's wall clock stepping ±86s**
(doc/platform.md § Wall-clock steps) expiring the fixture's `Date.now()`
readiness deadline against a daemon that was healthy the whole time. The
deadline is `performance.now()` now — house invariant 2, which the harness owes
the engine — and the config's one retry stays for genuine contention.

## Ground truth

| what | where |
|---|---|
| `/ui` static route, bearer-exempt split, MIME map, traversal guard | `dotnet/captainHook/Api/ApiHost.cs` (`ServeUiAsync`, `IsUiPath`, `ResolveUiFile`, `Mime`, the `HandleAsync` split) |
| the bearer-exempt Host+Origin gate half | `dotnet/captainHook/Api/ApiAuthGate.cs` (`EvaluateShell`) |
| DTO → JSON-Schema exporter | `dotnet/captainHook/Api/ApiSchema.cs` (`Export`) |
| `captainHook ui` token-handoff verb | `dotnet/captainHook/Api/UiVerb.cs` (`BuildUrl`, `RunAsync`, `LauncherCommand`/`IsWsl`/`DefaultLauncher`); `Mode.Ui` in `captainHookWire/Cli.cs`; `Program.cs` case; shim refusal in `captainShim/ShimMain.cs` |
| `uiDir` wiring (defaults beside the executables) | `dotnet/captainHook/Core/DaemonHost.cs` |
| frontend project (React+Vite+Zustand, `base:'/ui/'`, outDir→ `ui/`) | `web/vite.config.ts`, `web/package.json`, `web/index.html` |
| token bootstrap (fragment→sessionStorage→scrub→bearer) | `web/src/auth.ts` (`bootstrapToken`, `apiFetch`, `currentToken`, `clearToken`) |
| the one store + fold reducer + contracts | `web/src/store.ts` (`useStore`, `foldTrace`, `SseFrame`, `PolicyVerdict`, `TRACE_CAP`; navigation: `view`, `setView`, `VIEWS`, `VIEW_LABELS`) |
| SSE fetch client (protocol layer + reconnect) | `web/src/sse.ts` (`splitRecords`, `parseRecord`, `recordToFrame`, `runEventStream`, `startEventStream`) |
| policy write client (ETag lifecycle) | `web/src/policy.ts` (`submitPolicy`) |
| handlers editor logic (compose, PUT client + 412 inversion, toggle compose, wiring hint) | `web/src/handlers.ts` (`parseEntries`, `serializeEntries`, `upsertEntry`, `submitHandlers`, `togglePolicyText`, `disabledState`, `wiringHint`) |
| handlers editor UI (form, verbatim confirm, pending/skipped/registered states) | `web/src/HandlersEditor.tsx` (`HandlersSection`, `EntryForm`, `ConfirmModal`, `VerbatimEntry`, `WiringHints`) |
| shared read hook (fetch-on-live, 401⇒session-dead) | `web/src/api.ts` (`useApiJson`) |
| pure display logic | `web/src/format.ts` (`dispatchHue`, `uptime`, `clockTime`, `traceMatches`) |
| islands + mount table | `web/src/{App,StatusPanel,SupervisionPanel,HarnessesPanel,PolicyPanel,TracePanel}.tsx`, `main.tsx`, `index.html` (rail + one view region) |
| the nav rail + session notice | `web/src/App.tsx` (`Nav` — `data-nav` buttons, `aria-current`; `SessionNotice` — `data-notice-session`) |
| design tokens (color/type/space/radius, both themes) | `web/src/styles.css` `:root` + its `prefers-color-scheme: dark` block; the console rail is dark in BOTH themes by design |
| DTO→schema→TS codegen | `web/scripts/gen-types.mjs`, `web/schema/api.schema.json`, `web/src/api.gen.ts` |
| deploy staging (third artifact) | `.claude/skills/deploy/SKILL.md` (§1 `cp -r ui`, §3 `/ui` shell check) |
| engine-side pins | `ApiUiRouteHttpTests`, `UiResolveGuardTests`, `UiShellGateTests` (route + guard + inert shell), `ApiSchemaTests` (codegen drift), `UiVerbTests` (verb + fragment + shim refusal) — `dotnet/captainHookTests/{ApiUiRouteTests,ApiSchemaTests,UiVerbTests}.cs` |
| frontend unit pins | `web/src/{store,sse,policy,format,handlers}.test.ts` (`node --test`, zero deps) |
| sandbox daemon (spawn, isolation, readiness, drain, build+stage) | `web/e2e/daemon.ts` (`startDaemon`, `DaemonHandle.stop`, `buildAndStage`) — one module, three consumers |
| E2E pins + daemon fixture | `web/playwright.config.ts`, `web/e2e/{global-setup,fixtures}.ts` (the binding over `daemon.ts`), `web/e2e/{shell,session,nav,panels,trace,policy,handlers}.spec.ts`; `gotoView` (the ONE nav helper every spec navigates through) |
| screenshot loop (preview + snap + seed) | `web/scripts/{preview,snap,seed}.mjs` (`npm run ui:preview`, `npm run snap`) → `web/.screens/`; the loop itself in `.claude/skills/ui-loop/SKILL.md` |
| decision record | `doc/adr/0008-management-gui.md`; the SSE resume-cursor contract `doc/adr/0009-trail-rotation.md`; the handlers editor + trust surface `doc/adr/0011-hook-trust-model.md` |
| the API this is a client of | [management-api.md](management-api.md) · the policy it edits | [dispatch-policy.md](dispatch-policy.md) · the dispatch it observes | [hook-dispatch.md](hook-dispatch.md) |
