# ADR-0008 — Management GUI: an observability surface served same-origin by the daemon

**Status:** Accepted
**Date:** 2026-07-08 (accepted 2026-07-09; both open questions resolved — generated
TS types (d6), full-reload dev loop (d7); trail rotation split out to ADR-0009)

## Context

Roadmap item 6. ADR-0007 shipped the management API — the middle of the
*file → API → GUI* sentence ADR-0006 wrote — and left item 6 two explicit
hand-offs: "how the GUI acquires the token is item 6's decision (the obvious
shape: a CLI verb reads the file and opens the browser)," and the flow doc's
standing note that a browser must use fetch-streaming, not `EventSource`,
because the auth gate wants a bearer header on every request.

The API as it stands is `GET /status`, `/policy`, `/harnesses`, `/handlers`,
SSE `/events`, and `PUT /policy` — **read surface + live stream + one write**.
This ADR decides what the browser makes of it.

Two questions were put to the owner and answered up front:

- **What is the GUI *for*, and what interactivity?** Decided:
  **observability-first.** The questions a user actually has when a hook
  misbehaves are *did it fire, which handler, what effect, how long, did it time
  out, did the actor restart* — and every one of those is already on the wire.
  Control verbs (cancel a Background task, restart a handler, install/uninstall)
  are deferred: they need new backend plumbing *and* item 10's write-authz trust
  model, and — the house discipline — they should earn their way in on observed
  friction, exactly as ADR-0006 d7's pause verb waits for friction to be seen.
  The one write the GUI does expose is the one that already exists and needs no
  new trust surface: `PUT /policy`.

- **Audience.** The GUI ships toward *other people standing captAInHook up*, not
  just the author — which raises the token-handshake and first-run story from
  nicety to requirement.

- **Two owner goals shape the build, not just the design.** The UI is where the
  owner wants to *refresh React* (a stated learning goal), and **Playwright is
  wanted for the agent development loop** — an agent iterating on the UI needs to
  drive a real browser and see changes fast. Both push the UI toward a proper,
  separately-built frontend project rather than assets welded into the engine.

Three architectural facts from ADR-0007 shape the rest, and one of them nearly
decides the whole shape:

1. **The auth gate refuses a foreign `Origin`.** `ApiAuthGate` 403s any browser
   `Origin` that is not the API's own. A GUI served from a *different* origin —
   a Vite dev server on `:5173`, a packaged app on a custom scheme — is 403'd on
   every `/api/v1/*` call. **Same-origin is not a preference; it is forced.**
2. **`EventSource` cannot set headers.** The bearer is mandatory, so the live
   stream is consumed via `fetch` streaming, reconnect hand-rolled off the last
   byte-offset id.
3. **The token lives in a 0600 `api.json`.** A browser cannot read it; it must
   be *handed* the token, and the handoff must not leak it into a server log or
   browser history.

## Decision

1. **v1 scope: observe + edit policy, no control verbs — and NO new data
   endpoints.** The screens map 1:1 onto endpoints that already exist:

   | screen | source | interactivity |
   |---|---|---|
   | **Live trace** — dispatches as they happen, dispatchId-correlated | SSE `/events` | none (read); scroll/filter client-side |
   | **Supervision** — handlers, fail modes, generation, dead | `GET /handlers` | none (read) |
   | **Policy** — the resolved tri-state + raw file | `GET /policy` → **`PUT /policy`** | **edit** (validate-on-server, If-Match) |
   | **Harnesses** — the registry: specs, adapters, capabilities | `GET /harnesses` | none (read) |
   | **Status** — identity, pid, uptime, warm counters, open streams | `GET /status` | none (read) |

   The only genuinely new API-side surface v1 adds is a static-asset route
   (decision 2) and a CLI verb (decision 3) — no new *data* endpoint. Policy
   editing reuses the whole Phase-6 write path (strict validation → 422 with
   violations, If-Match → 412), so the editor surfaces the daemon's own verdict.

2. **A separate `web/` frontend project, served same-origin in production from a
   disk `ui/` directory.** The UI is a *client* of the API, not part of the
   engine — the daemon owns no browser code, only the bytes it serves. So the UI
   is its own project (`web/`, React + Vite — decision 6) with its own toolchain,
   dev server, and tests; its **build output** is what the daemon serves. Forced
   by the Origin gate (Context fact 1): the browser's origin must equal the API's,
   so in production the built assets come off `http://127.0.0.1:<port>` itself —
   a new **`GET /ui`** (and `/ui/*`) route in the existing `Api/` area streams
   them from a **disk `ui/` directory** staged into the deploy beside the two
   executables (a third artifact in the `/deploy` swap; the UI is *not*
   wire-coupled, so the shim↔engine skew hazard does not extend to it). The route
   is off the hook/stdout path exactly as the rest of the API is (invariant 1),
   with a small MIME map and a path-traversal guard (serve only within `ui/`).

   Disk over embedded-in-`captainHook.dll` (the earlier draft's choice) is
   deliberate: embedding avoided a new deploy artifact, but it coupled every UI
   tweak to a full engine rebuild — the wrong inner loop for iterative UI work,
   and fatal for the agent-driven Playwright loop (decision 6). Serving from a
   disk dir the frontend build writes to makes the loop an asset rebuild, not an
   engine rebuild. The one-artifact virtue was minor next to the loop.

   - **The `/ui` shell is served WITHOUT the bearer; every `/api/v1/*` data route
     keeps the full gate.** This is the one deliberate hole, and it is safe: a
     top-level browser navigation carries no `Authorization` header (it cannot —
     that is the whole token-handoff problem) and no `Origin` header, so the
     shell must be reachable unauthenticated or it can never load. The shell is
     **inert** — zero data, zero secrets; it is a bundle of static bytes that
     then authenticates itself (decision 3). Host prefix-matching still applies
     (a foreign Host 404s at the listener), so the exemption is bearer-only, and
     it exposes nothing an attacker could not already fetch from the `ui/` dir.

3. **Token acquisition: a CLI verb opens the browser with the token in the URL
   *fragment*.** A new `captainHook ui` verb (collapsed-mode, no daemon of its
   own) reads the 0600 `api.json`, and launches the default browser at
   `http://127.0.0.1:<port>/ui#t=<token>` via the OS opener (`xdg-open` / `open`
   / `start`). The **fragment**, not a query parameter, carries the token:
   fragments are never sent to the server and never written to access logs. The
   shell's JS reads `location.hash` on load, stashes the token in `sessionStorage`
   (survives a reload, dies with the tab), immediately clears the hash
   (`history.replaceState`) so it leaves no trace in history, and attaches it as
   the `Authorization: Bearer` on every `fetch`. This is the "CLI verb reads the
   file and opens the browser" shape ADR-0007 d6 anticipated, hardened against
   the two leak channels (query-in-logs, token-in-history).

4. **The browser consumes `/events` via fetch-streaming, reconnect hand-rolled
   off `Last-Event-ID` — and it distinguishes a *transient drop* from a *dead
   credential*.** Not `EventSource` (Context fact 2). Two failure modes, two
   responses:
   - **Transient drop, token still valid** (a network blip, a heartbeat gap): the
     client reconnects and resumes from its last id — treated as an **opaque
     resume cursor** (ADR-0009 d2), never a file offset the client reasons about,
     so future trail segmentation stays non-breaking. A `reset` frame re-anchors to
     0; a `gap` frame is surfaced honestly in the trace (the backpressure contract,
     ADR-0007 d5).
   - **401/403 — the daemon rotated under a cutover** (a `/deploy` swapped in a
     successor with a fresh token, maybe a fresh port): the browser **cannot
     self-heal.** It has no filesystem access, so it cannot re-read the 0600
     `api.json` for the new credential, and the daemon must NOT embed the token
     into the unauthenticated `/ui` shell to compensate — that would let any
     co-located reader `curl /ui` and lift it, collapsing the 0600 trust root
     (decision 2's inert-shell rule). So on a 401/403 the tab stops retrying and
     surfaces **"session ended — re-run `captainHook ui`"**; the CLI verb
     (decision 3) is the only path back to a live credential. Acceptable because
     the GUI is a local operator tool, not an unattended dashboard — a cutover is
     rare and a human is present. *(An earlier draft had the client "re-read
     `api.json`" here; that is a CLI/test-client capability the browser does not
     have — corrected 2026-07-09.)*

5. **History is v1's one honest gap, and it rides the existing resume, not a new
   endpoint.** The trace opens "from now" (no `Last-Event-ID` ⇒ current end).
   Byte-offset ids already let a client resume from any earlier offset, and
   `Last-Event-ID: 0` replays the whole file — so "scroll back" is *possible*
   today, just unbounded. v1 ships live-from-now; a **bounded** backfill (a
   `?tail=N` affordance on `/events`, or a small `GET /trace?tail=N`) is the
   single most likely first addition and is called out as a revisit trigger, not
   built speculatively.

   **This gap is entangled with a storage fact the GUI exposed: the trail file is
   append-only and unbounded** (`Logging.fs` `File.AppendAllText`; no rotation —
   `TrailTail` even notes rotation is "rare and manual"). Bounded backfill and
   bounded *storage* are two halves of one problem — the resume id is an absolute
   byte offset into that single ever-growing file, so segment rotation and the
   backfill endpoint must be co-designed. That work is **ADR-0009** (trail
   rotation + the global-monotonic resume id + retention horizon → `reset`), a
   storage-layer decision amending ADR-0007 d5; this GUI is its first consumer.
   ADR-0008 stays live-from-now and defers the rest to ADR-0009.

6. **Stack: React + Vite, in a `web/` project — and it costs the architecture
   nothing.** The one native .NET web-UI option is Blazor, and it fights this
   stack specifically: **Blazor Server** needs ASP.NET Core + SignalR *in the
   daemon* — the exact runtime dependency invariant 3 and ADR-0007's BCL-
   `HttpListener` choice exist to refuse; **Blazor WASM** can publish to static
   files the daemon could serve without a runtime dep, but drags a multi-MB WASM
   runtime and the `wasm-tools` workload for a tiny panel. The deeper point:
   because the server is a deliberately *dumb* JSON+SSE+static socket (not
   ASP.NET Core), .NET offers **no native UI leverage here** — the daemon treats
   a React `dist/` and a Blazor publish identically, as opaque static bytes. So
   the UI framework is a free frontend choice, chosen on its own merits:
   **React + Vite** — best-in-class Playwright/dev-loop synergy, and it refreshes
   the owner's React (an explicit project goal). Frontend deps kept deliberately
   lean to honor the project's minimalism (React + Vite + a thin SSE/fetch layer;
   resist a state-manager *pile-on* — **decision 8** lands on **one ~1KB store**
   (Zustand), not a Redux stack and not a hand-rolled bus), no CDN (loopback is
   offline by nature) — the built bundle is self-contained.

   **TS types are generated from the C# DTOs, not hand-written** (resolved: the
   owner wants the widely-used shape, and codegen makes drift a build failure, not
   a silent bug). Because the server is a dumb `HttpListener` — no ASP.NET, so no
   OpenAPI/Swagger for NSwag to consume — the natural pipeline is BCL-native:
   .NET 10's `System.Text.Json.Schema.JsonSchemaExporter` emits JSON Schema from
   the DTO records (a small build step or a test, adding no engine dependency),
   and the `web/` build runs `json-schema-to-typescript` over it. The C# DTO is
   the single source of truth; the ~6-DTO, no-new-endpoint surface keeps the
   schema tiny. One cost: the `web/` build gains a build-order dependency on a
   schema artifact the engine emits (checked into the repo, regenerated on DTO
   change).

7. **The dev loop is same-origin by default, HMR is an opt-in escape hatch.**
   The agent-driven Playwright loop is a first-class goal, and it decides how
   the frontend runs in development:
   - **Default — no auth hole:** the frontend `build --watch` writes into the
     `ui/` dir the daemon serves, so Playwright drives the *daemon's* same-origin
     `http://127.0.0.1:<port>/ui`. Loop = asset rebuild + full page reload
     (~100–200ms), and — the point — the browser's origin is the API's origin,
     so **no Origin relaxation is needed**. This is the recommended v1 loop.
   - **Escape hatch — Vite HMR:** running the Vite dev server on its own origin
     (`:5173`) gives HMR but fetches cross-origin to the API → the Origin gate
     403s it. Enabling it requires a **dev-only, env-gated Origin allowance**
     (e.g. `ApiAuthGate` honors a `CAPTAINHOOK_API_DEV_ORIGIN` *only* when the
     env var is set, never in a deployed daemon) — a bounded, explicit amendment
     to ADR-0007's auth model.

   **Resolved: v1 ships the full-reload loop; HMR stays reserved, not built.** The
   Playwright/agent loop — the first-class goal here — gets *nothing* from HMR
   (Playwright navigates fresh each run, so HMR's state-preservation is a purely
   human ergonomic), this is ~5 panels so the reload penalty is trivial, and the
   full-reload loop keeps a security-surface API with **zero auth holes**. The
   env-gated allowance is reserved for if hand-hacking the UI is later shown to
   chafe — a reversible flip, not a v1 cost (revisit trigger).

8. **Frontend architecture: isolated islands over one shared store, not a
   prop-threaded tree.** *How* the React is written, decided as deliberately as
   the stack. The default React shape — a single `<App>` root, shared state lifted
   to the top, props threaded down through components that don't care about them —
   is rejected for this UI in favor of **islands + an external store**: each screen
   in decision 1's table mounts as its own `createRoot` (DOM siblings with no
   shared React parent), owns its local view state, and coordinates with the
   others *only* through a single store that lives outside the tree. This keeps
   the property the owner wants (isolated declarative components, no giant tree, no
   prop drilling — the deepseek-moby taste) while landing on the widely-used tool
   rather than a bespoke bus.

   - **The store is Zustand, chosen over a hand-rolled `EventTarget` bus.** Both
     kill the two things the owner objects to — the giant tree and prop drilling —
     identically, because both are just `useSyncExternalStore` (React's own
     primitive for "subscribe to a source of truth outside the tree") underneath;
     Zustand *is* that primitive, shrink-wrapped. It wins on three counts: it is
     what the ecosystem uses (the owner's stated reason, same as generated types —
     transferable, not bespoke), it is ~1KB and needs **no provider** (so islands
     each subscribe to the same store independently, no wrapping context), and it
     is *less* code than hand-rolling atoms + fold logic. The style is against
     React's *default* but squarely on its *grain*.

   - **The UI is state-shaped, not event-shaped — so a store fits better than a
     bus.** Almost everything rendered is *state* (current status / policy /
     handlers, the accumulating trace list), not fire-and-forget signals. The one
     genuine event source is the SSE stream, and the clean pattern is to **fold
     each frame into the store** in the `onMessage` handler (decision 4): one
     reducer, one place state ever mutates — cleaner than a bus's N listeners
     mutating N things. Islands subscribe to the slices they care about
     (`useStore(s => s.trace)`); `PUT /policy`'s verdict (decision 1) lands in the
     store the same way. The server's event nature (ADR-0007's `/events`) is
     preserved *into* the store, then read as state.

   - **Props are not purged — drilling is.** A leaf taking a prop or two from its
     immediate parent *within* one island stays; the store is for cross-island and
     "far" state, exactly the threading the owner objects to. If a genuinely
     transient cross-island signal ever appears (a toast), a ~10-line `EventTarget`
     alongside is fine — but it is not built speculatively, and a store slice
     covers nearly every case.

## Consequences

### Positive

- **v1 adds no new *data* endpoint.** The whole observability surface is the API
  that already shipped; the engine-side blast radius is one static route + a CLI
  verb. All the interesting work is browser code in its own project.
- **The UI is a proper client with a fast, agent-friendly loop.** A separate
  `web/` React+Vite project gets its native toolchain and the Playwright loop the
  agent-dev goal wants — an asset rebuild, not an engine rebuild, and the UI's
  release cadence decouples from the engine's double-green + swap.
- **Coordination is one ~1KB store and the tree stays shallow.** The islands +
  Zustand style (decision 8) keeps decision 6's minimalism (one tiny, provider-
  less store — not a Redux stack) while killing prop drilling; SSE frames fold
  into the store in one reducer (decision 4), and each island mounts and
  E2E-tests alone (a natural fit for a panel-at-a-time Playwright loop). The store
  is `useSyncExternalStore` underneath, so the style is on React's grain and
  serves the owner's refresh-React goal on a tool the ecosystem actually uses.
- **Same-origin (default loop) keeps the auth model strict and CORS absent.** The
  Origin gate stays exactly as hardened; there is no second origin to allow, no
  preflight — unless the HMR escape hatch (decision 7) is deliberately enabled in
  dev, which is env-gated and prod-refused.
- **The token never touches a log or history.** Fragment handoff +
  `replaceState` + `sessionStorage` closes both leak channels the obvious
  query-param shape would open.
- **No new write-authz surface.** Observability + the existing policy write only,
  so item 10's trust model stays legitimately deferred.
- **Transient stream drops self-heal; a *cutover* asks for a re-launch.** The
  fetch-streaming client resumes a network blip from its last id with the same
  token (ADR-0007's resume pays this dividend). A cutover that rotates the
  credential (401/403) is honestly *not* self-healing — the browser can't read the
  new `api.json` — so the tab prompts "re-run `captainHook ui`" (decision 4). A
  clean line, not a hidden gap: a local operator tool with a human present.

### Negative

- **The engine's `HttpListener` gains a static-file responsibility.** Small and
  off the hook path, but new: a `/ui` route, a MIME map, disk streaming from the
  `ui/` dir, and a path-traversal guard. Mitigated by keeping it a dumb
  serve-within-one-dir.
- **A third deploy artifact.** The `ui/` dir stages into the `/deploy` swap
  beside the two executables. Not wire-coupled (no skew hazard), but one more
  thing that must move. **Node/npm is a dev-only build tool, never a runtime or
  deploy dependency:** the built assets are committed to the repo, so deploy and
  anyone *running* captAInHook need no Node — only someone *modifying* the UI runs
  `npm install && npm run build`. The `/ui` route itself is pure C# static-file
  streaming with zero Node. Cost of committing built assets: they can drift from
  source, so a UI change is edit → rebuild → recommit (the agent loop does this
  anyway; a CI freshness check is the mitigation if it bites).
- **The `/ui` shell is unauthenticated.** A deliberate, bounded hole (inert
  bytes, Host-gated, no data) — but it *is* an exemption in a surface whose whole
  point was "credential on every request," and it must be held to serving inert
  static bytes forever (a data leak into the shell would breach the gate). Pinned
  by a test that the shell route carries no daemon state.
- **DTO types are re-expressed in TypeScript — but generated, not hand-kept.**
  The one thing a C# UI would have shared for free; codegen from the DTOs
  (decision 6) makes drift a build failure rather than a silent bug, at the cost
  of a schema-emit build step and a build-order dependency the `web/` build must
  honor.
- **History is unbounded or absent in v1, on top of an unbounded trail file.**
  "From now" is honest but thin, and the trail it tails is append-only with no
  rotation — so the file grows without bound and `Last-Event-ID: 0` replays all of
  it. Both are deferred *together* to **ADR-0009** (rotation + retention +
  bounded backfill); v1 lives with live-from-now and a growing file.
- **Islands-over-a-store is a non-default React shape.** A conventional React
  reader expects one `<App>` tree; independent `createRoot` islands sharing an
  external store reads as unfamiliar, and one global store can become a dumping
  ground. Held in check by Zustand *slices* (islands subscribe to only what they
  read), folding SSE in one reducer (decision 4), and the fact that the primitive
  (`useSyncExternalStore`) and Zustand itself are thoroughly idiomatic — this is a
  recognized pattern, not a bespoke one.

## Alternatives considered

- **Embedding the built assets in `captainHook.dll`** (manifest resources, the
  house "ships inside the assembly" pattern — the earlier draft's choice).
  Rejected: it avoids the third deploy artifact, but couples every UI change to a
  full engine rebuild — the wrong inner loop for iterative UI work and the
  agent-Playwright loop. The disk-dir cost is worth the loop. Revisit only if
  artifact-count becomes a real deploy pain.
- **Blazor Server** (C# UI, ASP.NET Core + SignalR in the daemon). Rejected: it
  reintroduces the exact ASP.NET Core runtime dependency invariant 3 and
  ADR-0007's BCL-`HttpListener` choice exist to refuse.
- **Blazor WebAssembly** (C# UI, static-served, DTO types shared for free).
  Tenable — it needs no daemon runtime dep — but drags a multi-MB WASM runtime
  and the `wasm-tools` build workload for a small panel, a weaker Playwright loop,
  and it does not serve the React-refresh goal. The shared-DTO win is real but
  outweighed.
- **A separate-origin SPA in production** (a standalone static host / packaged
  app). Rejected: the Origin gate 403s it on every call, and it doubles the
  deploy. Same-origin is the architecture's own answer; the *dev-only* Vite HMR
  origin is the sole, env-gated exception (decision 7).
- **Vanilla JS/HTML, no framework.** The leanest option (no build step), but the
  live trace + policy editor have enough interactivity to want a framework, and
  it does not serve the React-refresh goal.
- **Conventional prop-tree React** (single `<App>` root, state lifted to the top
  / React Context, or a heavy store — Redux + toolkit). Rejected: the prop-tree /
  lifted-state / Context default is the shape the owner set out to avoid, and a
  Redux stack is the "state-manager pile-on" decision 6 warns against. The chosen
  middle is one ~1KB store (Zustand) under islands (decision 8) — no drilling, no
  stack.
- **A hand-rolled `EventTarget` event bus** (the deepseek-moby shape, an earlier
  draft of decision 8). Considered and rejected in favor of Zustand: both solve
  drilling identically (both are `useSyncExternalStore` underneath), but the bus
  is bespoke — it teaches nothing transferable (the owner's explicit reason for
  choosing widely-used tools), is more code (hand-rolled atoms + fold logic), and
  is event-shaped where this UI is state-shaped. A tiny `EventTarget` survives
  only as the reserved escape hatch for a genuinely transient signal (decision 8).
- **Token via query parameter** (`/ui?t=...`). Rejected: query strings land in
  server access logs and browser history — exactly the two places a bearer must
  not. The fragment is sent to neither.
- **`EventSource` for the stream.** Rejected: it cannot set the `Authorization`
  header the gate requires. Fetch-streaming is the only header-bearing option.
- **Control verbs in v1** (stop a Background task, restart a handler). Rejected as
  premature: synchronous dispatch is deadline-bounded (nothing to "stop"), the
  only long-lived work is deliberately fire-and-forget `Effect.Background` with
  no cancellation handle, and any control surface needs item 10's trust model.
  Deferred to observed friction.

## Revisit triggers

- **The full-reload dev loop chafes** ⇒ enable the env-gated Vite HMR origin
  allowance (decision 7) — the reserved, prod-refused escape hatch.
- **"Scroll back" friction is observed, or the trail file grows painful** ⇒
  **ADR-0009** (trail rotation + retention + bounded backfill) — the two are one
  problem (decision 5); v1's live-from-now is the placeholder until it lands.
- **A control verb earns its way in** (a genuinely long Background task worth
  abandoning; a handler worth restarting from the UI) ⇒ its own ADR or an
  amendment here, co-designed with item 10's write-authz trust model.
- **The schema-codegen step is friction** (the emit build step or the build-order
  dependency chafes on a ~6-DTO surface) ⇒ fall back to hand-written TS interfaces
  and pin them with a test that they match the DTOs.
- **The single store gets gnarly** (a panel's state outgrows a Zustand slice — a
  wizard, an editor with undo) ⇒ reach for XState for that one panel (a state
  machine as the external truth), still islands, still no prop tree (decision 8).
- **A second production origin becomes unavoidable** (e.g. an embedded webview
  with a custom scheme) ⇒ revisit the same-origin decision and the Origin gate
  together.

## Ground truth (at acceptance)

All 13 slices landed 2026-07-09 (roadmap item 6). Mechanics live in
[doc/flow/management-gui.md](../flow/management-gui.md); this table is the
decision→code index.

| decision | where it landed |
|---|---|
| d2 `/ui` static route + MIME + traversal guard + bearer-exempt split | `dotnet/captainHook/Api/ApiHost.cs` (`ServeUiAsync`, `IsUiPath`, `ResolveUiFile`, `Mime`, `HandleAsync`); `ApiAuthGate.EvaluateShell`; `uiDir` in `Core/DaemonHost.cs` |
| d3 token handoff (fragment → sessionStorage → scrub → bearer) | `web/src/auth.ts`; the `captainHook ui` verb `Api/UiVerb.cs` + `Mode.Ui` (`captainHookWire/Cli.cs`, `Program.cs`, shim refusal `captainShim/ShimMain.cs`) |
| d4 fetch-streaming client (opaque cursor, reset/gap, dead-credential) | `web/src/sse.ts` (`runEventStream` + protocol layer); the from-now anchor fix in `Api/ApiHost.cs` `ServeEventsAsync` |
| d1 policy editor + ETag lifecycle | `web/src/policy.ts` (`submitPolicy`), `web/src/PolicyPanel.tsx`; server path in `Api/ApiPolicyWriter.cs` (ADR-0007) |
| d1 read islands | `web/src/{StatusPanel,SupervisionPanel,HarnessesPanel,TracePanel}.tsx`, shared `api.ts`, pure `format.ts` |
| d6 stack + DTO→schema→TS codegen | `web/` React+Vite+Zustand; `Api/ApiSchema.cs` (`Export`) → `web/schema/api.schema.json` → `web/scripts/gen-types.mjs` → `web/src/api.gen.ts` |
| d8 islands over one store | `web/src/store.ts` (`useStore`, `foldTrace`), `web/src/main.tsx` mount table, `web/index.html` leaf divs |
| d2/d7 same-origin serving, third deploy artifact, full-reload loop | `.claude/skills/deploy/SKILL.md` (`ui/` staging + shell check); Vite `base:'/ui/'` `web/vite.config.ts` |
| pins (route/guard/inert-shell, codegen drift, verb, unit, E2E) | `dotnet/captainHookTests/{ApiUiRouteTests,ApiSchemaTests,UiVerbTests}.cs`; `web/src/*.test.ts`; `web/e2e/*.spec.ts` + the isolated daemon fixture `web/e2e/fixtures.ts` |
| SSE resume-cursor contract this consumes | ADR-0009 d2 (`doc/adr/0009-trail-rotation.md`) |

Adversarial verify (3 slices, per the plan) confirmed the traversal guard + gate
scoping (no encoded escape; `/ui/../api/v1/*` meets the full 401), the SSE
client's resume/reset/gap + dead-credential logic (and CAUGHT the server's
from-now anchor race, fixed), and the policy ETag discipline (and caught the
`""`-If-Match blind-write trap, fixed).

## Implementation plan

Generated by [`/adr-plan`](../../.claude/workflows/adr-plan.js) on 2026-07-09 — a
**stable ranking** of this ADR's work into effort-tagged, dependency-ordered build
phases. Derived from the decisions above, not a decision itself; changes only if
they do. **Progress is tracked on the [roadmap](../roadmap.md) item 6, not here.**
Tags: `effort/hardness · verify?`; ◆ = on the critical path. 13 slices → 7 build
phases; critical path length 7; adversarial verify on exactly 3 slices; no
ultracode.

**Phase 1 — two independent roots (engine route ∥ web toolchain)**
- `ui-static-route` — med/moderate · **verify (adversarial)** — deps: none. The
  `GET /ui` (+ `/ui/*`) route in the existing `Api/` area: MIME map, disk
  streaming from the deploy's `ui/` dir, path-traversal guard, and the bearer
  exemption (decision 2) — which forces splitting the currently-atomic
  `ApiAuthGate.Evaluate` scoping so `/ui` skips ONLY the bearer while the Host
  check and the full gate on `/api/v1/*` survive intact. Two
  single-pass-ships-it-wrong shapes on a deliberately unauthenticated route: a
  traversal escape (encoded dot-dot, a rooted `Path.Combine` segment, a prefix
  check without the trailing separator) and an auth-scoping off-by-one that
  widens the hole — probe both adversarially, and confirm `/api/v1/*` still
  401s. Can serve a placeholder `ui/`; nothing web-side blocks it.
- `web-scaffold` ◆ — low/mechanical · no adversarial verify — deps: none. The
  `web/` React+Vite+Zustand project (decision 6): own toolchain, `build` /
  `build --watch` scripts, Vite `base: '/ui/'`, `outDir` → the committed repo
  `ui/` dir, no CDN. Every choice is pre-made by the ADR; the one trap (`base`)
  fails loudly the moment the route serves it. Node stays dev-only (invariant 3
  untouched).

**Phase 2 — pin the auth hole + plumbing fan-out (five, mutually independent)**
- `inert-shell-tests` — med/moderate · no adversarial verify — deps:
  ui-static-route. **Land immediately after — ideally in the same commit as —
  the route**: until these exist, the deliberate unauthenticated hole and the
  gate split are unpinned — the plan's one window of unpinned risk. The craft
  is non-vacuous assertions: shell byte-identity across authed/unauthed
  requests, token-absence in the served bytes, traversal tests covering encoded
  forms (`%2e%2e`, absolute paths) so they don't pass on an unrelated 404.
  Rides the mature `ApiAuthGateTests`/`ApiAuthHttpTests` idioms; a dummy `ui/`
  of fake bytes decouples it from the scaffold.
- `dto-schema-codegen` ◆ — med/moderate · no adversarial verify — deps:
  web-scaffold. `JsonSchemaExporter` over the ~10 `ApiDtos.cs` records
  (BCL-only, invariant 3 intact) → checked-in schema → `json-schema-to-typescript`
  in the `web/` build (decision 6). The reasoning is exporter-option alignment
  with `ApiJson`'s camelCase Web options and the awkward shapes (`JsonElement?`
  → `any`, state-dependent nullability going all-optional in TS). The
  checked-in-schema diff test IS the drift detector; failures are loud
  build-time errors, never silent runtime bugs.
- `token-handoff-bootstrap` — low/mechanical · no adversarial verify — deps:
  web-scaffold. Decision 3's four scripted steps: read `location.hash` →
  `sessionStorage` → `history.replaceState` → `Authorization: Bearer` on every
  fetch. Ordering (clear the hash first) and precedence (hash beats stale
  sessionStorage; neither ⇒ inert shell) are the only reasoning; a mistake
  reopens the leak channels the design closes, but errors are
  inspection-visible and later pinned by inert-shell-tests + Playwright —
  drive it once in a real browser.
- `ui-cli-verb` — low/mechanical · no adversarial verify — deps:
  ui-static-route. One new collapsed-mode verb in `Program.cs`:
  `ApiDiscovery.TryRead` (exists) → format `http://127.0.0.1:<port>/ui#t=<token>`
  → OS opener (`xdg-open`/`open`/`start`). Pin the URL builder with one unit
  test (fragment not query; token never echoed to stderr/log); absent/stale
  api.json ⇒ a clear "daemon not running".
- `deploy-ui-staging` — low/mechanical · no adversarial verify — deps:
  web-scaffold, ui-static-route. A /deploy skill edit: stage the committed
  `ui/` into `$STAGE` beside the two executables before the existing atomic
  swap (+ a `/ui` fetch in the warm-path check). Not wire-coupled — none of
  the MVID/skew machinery extends to it; `bin.prev` rollback covers it for
  free. Land any time after the scaffold, but before the first real /deploy
  of the GUI.

**Phase 3 — the store contract**
- `zustand-store` ◆ — med/moderate · no adversarial verify — deps:
  web-scaffold, dto-schema-codegen. The one design decision with five-slice
  blast radius: the slice shape (trace / status / policy / handlers /
  harnesses) is the contract every island and the SSE client build against,
  and slice boundaries are decision 8's own mitigation against the
  dumping-ground failure mode. The code is near-mechanical (provider-less,
  `useSyncExternalStore` underneath); spend the thought on the shape, typed by
  the generated TS.

**Phase 4 — stream client + the one write (parallel)**
- `sse-fetch-client` ◆ — high/hard-reasoning · **verify (adversarial)** — deps:
  web-scaffold, token-handoff-bootstrap, zustand-store. **The plan's hardest
  slice and its schedule bottleneck — start it the moment its deps land.** Real
  protocol logic: hand-rolled SSE parsing over a fetch ReadableStream (frames
  split across chunk boundaries), reconnect off an OPAQUE cursor (ADR-0009 d2 —
  this client is where that contract is honored), and three server-pinned
  semantics: a `gap` frame carries NO id so the cursor must not advance on it
  (that is what lets a resume recover the dropped region), `reset` re-anchors
  to 0, and 401/403 STOPS the retry loop (dead credential, decision 4) while a
  network drop retries with backoff — the dead credential only manifests on
  the reconnect after a drop, an easy misclassification. A subtle bug silently
  drops or duplicates trace lines: verify against a live daemon (kill/restart,
  truncate the trail for `reset`, rotate the token for the 401 split).
- `policy-editor-island` — med/moderate · **verify (adversarial)** — deps:
  web-scaffold, dto-schema-codegen, zustand-store, token-handoff-bootstrap.
  The Phase-6 write path already exists server-side; the genuinely subtle
  piece is the client's ETag lifecycle — ALWAYS send If-Match, adopt the 200
  response's ETag (no re-GET), adopt a 412's `current` before retry. The
  server honors If-Match only when supplied, so a forgetful client silently
  reopens the blind-overwrite window with zero visible symptom — the exact
  silent-degradation shape adversarial verify exists for. Does not need the
  SSE client or the read panels.

**Phase 5 — read panels**
- `read-panels-islands` ◆ — med/moderate · no adversarial verify — deps:
  web-scaffold, zustand-store, sse-fetch-client, dto-schema-codegen,
  token-handoff-bootstrap. Largest by volume, lowest reasoning density: three
  of the four panels (Supervision, Harnesses, Status) are fetch-and-render
  tables over codegen'd DTOs; the Live trace panel's dispatchId grouping,
  client-side filter, and scroll-on-an-unbounded-list are the only moderate
  parts — the subtle stream logic lives upstream. Islands mount independently
  (decision 8), so build and eyeball one panel at a time.

**Phase 6 — end-to-end pin**
- `playwright-e2e` ◆ — med/moderate · no adversarial verify (it IS the
  verification layer) — deps: ui-static-route, web-scaffold,
  token-handoff-bootstrap, read-panels-islands, policy-editor-island. The
  specs are mechanical (islands mount alone: navigate fresh + assert — the
  loop decisions 7/8 designed for); the real work is the FIXTURE: spawn a real
  daemon, wait on the 0600 `api.json` for readiness (no real sleeps), read the
  token, navigate to `/ui#t=...` same-origin. Mandatory isolation from the
  live `~/.captainHook` tree (CLAUDE.md's pollution warning): temp
  `CAPTAINHOOK_LOG` / harness dir / `XDG_RUNTIME_DIR`. Environment flakiness
  here threatens the double-green ship bar — budget a hardening iteration.

**Phase 7 — flow-doc capstone**
- `flow-doc-management-gui` ◆ — med/moderate · no adversarial verify (shipshape
  only) — deps: all 12. `doc/flow/management-gui.md` per house discipline
  (ASCII + why-prose + Ground truth table of real files/symbols/tests) —
  rendering the landed mechanics (resume/reset/gap + the dead-credential
  split, the fragment handoff, the DTO→schema→TS pipeline) from the
  implementation, not this ADR's prose — plus back-filling this ADR's
  Ground-truth placeholder.

**Critical path:** web-scaffold → dto-schema-codegen → zustand-store →
sse-fetch-client → read-panels-islands → playwright-e2e →
flow-doc-management-gui (length 7). The engine side (route → shell tests → CLI
verb → deploy staging) runs entirely off the critical path and parallelizes
with the web spine.

**Sequencing traps.** (1) `ui-static-route` + `inert-shell-tests` are one work
session, ideally one commit — the gap between them is the only window where the
deliberate auth hole is unpinned. (2) `sse-fetch-client` is the schedule
bottleneck — don't let it queue behind the parallelizable Phase-2 tail work.
(3) Committed `ui/` dist assets can drift from source starting at the FIRST
scaffold commit — establish the rebuild-before-commit habit (a shipshape
check) in Phase 1, not when deploy staging lands. (4) Any later DTO change must
regenerate schema + TS in the same commit, or the drift-detector test blocks —
make that the codegen slice's committed convention. (5) The Playwright
fixture's isolation and readiness-wait are where flake enters — no real sleeps
(invariant 2's spirit), and never the live `~/.captainHook` tree. (6) Gate
discipline: shipshape before every commit; suite green **twice** before any
/deploy. Adversarial verify on exactly three slices (ui-static-route,
sse-fetch-client, policy-editor-island); **no slice warrants ultracode** — each
is one cohesive change with no parallelizable subwork.
