# ADR-0008 — Management GUI: an observability surface served same-origin by the daemon

**Status:** Proposed
**Date:** 2026-07-08

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
   off `Last-Event-ID`.** Not `EventSource` (Context fact 2). On a dropped
   stream — including a 401/403, which means the daemon rotated under a cutover —
   the client **re-reads `api.json`** (new token, maybe new port) rather than
   hammering the dead credential, then resumes from its last byte-offset id. A
   `reset` frame re-anchors to 0; a `gap` frame is surfaced honestly in the trace
   (the backpressure contract, ADR-0007 d5).

5. **History is v1's one honest gap, and it rides the existing resume, not a new
   endpoint.** The trace opens "from now" (no `Last-Event-ID` ⇒ current end).
   Byte-offset ids already let a client resume from any earlier offset, and
   `Last-Event-ID: 0` replays the whole file — so "scroll back" is *possible*
   today, just unbounded. v1 ships live-from-now; a **bounded** backfill (a
   `?tail=N` affordance on `/events`, or a small `GET /trace?tail=N`) is the
   single most likely first addition and is called out as a revisit trigger, not
   built speculatively.

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
   resist a state-manager / UI-kit pile-on — **decision 8** makes that concrete:
   coordination rides a native event bus, not a store library), no CDN (loopback
   is offline by nature) — the built bundle is self-contained.

   The one thing a C# UI would have bought — sharing the DTO records
   (`StatusDto`, `PolicyDto`, …) with zero drift — is a small, bounded cost in
   React: the data surface is ~6 DTOs and v1 adds no new data endpoint, so
   hand-written TS interfaces (or codegen from the C#) carry near-zero churn.
   *Open question (owner deciding): hand-written vs generated TS types.*

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
     to ADR-0007's auth model. Reserved, not built, until the full-reload loop is
     shown to chafe. *Open question (owner deciding): ship the HMR allowance in
     v1, or start with the no-hole full-reload loop?*

8. **Frontend architecture: isolated islands over an event bus, not one
   prop-threaded tree.** *How* the React is written, decided as deliberately as
   the stack. The default React shape — a single `<App>` root, shared state lifted
   to the top, props threaded down through components that don't care about them —
   is rejected for this UI in favor of the **islands + bus** style: each screen in
   decision 1's table mounts as its own `createRoot` (DOM siblings with no shared
   React parent), owns its local view state, and coordinates with the others
   *only* through an event bus that lives outside the tree. This is the shape the
   owner already runs in the deepseek-moby project (isolated declarative
   components over a `CustomEvent` bus), and — the load-bearing point — it is the
   concrete realization of decision 6's "no state-manager" minimalism, not a
   departure from it.

   - **The bus is native, zero-dependency.** A thin `EventTarget` subclass
     (`emit` / `on`, where `on` returns its own unsubscribe) — browser API, no
     library. Two React bridges sit on top: **`useBusEvent`** for transient
     signals (a `useEffect` that subscribes and returns the unsubscribe as
     cleanup) and **`useBusState`** for a persistent value many islands read,
     built on **`useSyncExternalStore`** — React's own first-class primitive for
     "subscribe to a source of truth outside the tree," the same one Redux /
     Zustand / Jotai wrap. So the style is against React's *default* but squarely
     on its *grain*: it refreshes the owner's React on the real coordination
     primitive rather than the props-and-Context path.

   - **SSE is the bus's natural upstream.** The fetch-streaming client
     (decision 4) does not hand frames down a prop tree — it `emit`s each
     dispatch / `reset` / `gap` frame onto the bus, and the Live-trace /
     Supervision / Status islands subscribe to what they care about. The server is
     *already* an event stream (ADR-0007's `/events`); islands + bus preserve that
     nature end-to-end instead of flattening it into request/response state.
     `PUT /policy`'s verdict (decision 1) likewise returns as a bus event the
     Policy island renders.

   - **Props are not purged — drilling is.** A leaf taking a prop or two from its
     immediate parent *within* one island stays; the bus is for cross-island and
     "far" state, exactly the threading the owner objects to. The discipline the
     style demands: events named per concern and atoms kept few, or an event bus
     rots into implicit spaghetti (the negative below).

## Consequences

### Positive

- **v1 adds no new *data* endpoint.** The whole observability surface is the API
  that already shipped; the engine-side blast radius is one static route + a CLI
  verb. All the interesting work is browser code in its own project.
- **The UI is a proper client with a fast, agent-friendly loop.** A separate
  `web/` React+Vite project gets its native toolchain and the Playwright loop the
  agent-dev goal wants — an asset rebuild, not an engine rebuild, and the UI's
  release cadence decouples from the engine's double-green + swap.
- **Coordination adds no dependency and keeps the tree shallow.** The islands+bus
  style (decision 8) realizes decision 6's no-state-manager minimalism with a
  native `EventTarget` + `useSyncExternalStore` — no store library, no prop
  drilling — and the SSE stream stays an event stream all the way to render, and
  each island mounts and E2E-tests alone (a natural fit for a panel-at-a-time
  Playwright loop).
- **Same-origin (default loop) keeps the auth model strict and CORS absent.** The
  Origin gate stays exactly as hardened; there is no second origin to allow, no
  preflight — unless the HMR escape hatch (decision 7) is deliberately enabled in
  dev, which is env-gated and prod-refused.
- **The token never touches a log or history.** Fragment handoff +
  `replaceState` + `sessionStorage` closes both leak channels the obvious
  query-param shape would open.
- **No new write-authz surface.** Observability + the existing policy write only,
  so item 10's trust model stays legitimately deferred.
- **Cutover is already handled.** The fetch-streaming client re-discovers on
  401/403, so a deploy that rotates the daemon reconnects the tab to the
  successor with no special-casing (ADR-0007's cutover pays this dividend).

### Negative

- **The engine's `HttpListener` gains a static-file responsibility.** Small and
  off the hook path, but new: a `/ui` route, a MIME map, disk streaming from the
  `ui/` dir, and a path-traversal guard. Mitigated by keeping it a dumb
  serve-within-one-dir.
- **A third deploy artifact.** The `ui/` dir stages into the `/deploy` swap
  beside the two executables. Not wire-coupled (no skew hazard), but one more
  thing that must move, and a Node/npm build prerequisite creeps toward the repo
  for a *source* build of the UI (mitigable by shipping prebuilt assets so the
  UI source-build stays opt-in).
- **The `/ui` shell is unauthenticated.** A deliberate, bounded hole (inert
  bytes, Host-gated, no data) — but it *is* an exemption in a surface whose whole
  point was "credential on every request," and it must be held to serving inert
  static bytes forever (a data leak into the shell would breach the gate). Pinned
  by a test that the shell route carries no daemon state.
- **DTO types are re-expressed in TypeScript.** The one thing a C# UI would have
  shared for free; a ~6-DTO, no-new-endpoint surface makes the drift risk small,
  but it is real (hand-written or generated — the open question).
- **History is unbounded or absent in v1.** "From now" is honest but thin; a user
  opening the tab mid-session sees only what happens next until they choose to
  replay (potentially the whole file). The bounded-tail follow-up addresses it
  when friction appears.
- **The bus is a non-default React idiom with its own failure mode.** Islands + a
  `CustomEvent` bus is against React's props-and-Context default, so it reads as
  less familiar to a React reader, and an unnamed-event free-for-all rots into
  implicit spaghetti. Held in check by naming events per concern, keeping atoms
  few, and the fact that the primitives (`useSyncExternalStore`) are themselves
  idiomatic — Zustand is the same idea shrink-wrapped if native-bus coordination
  ever chafes (revisit trigger).

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
- **Conventional prop-tree React** (single root, state lifted to the top / React
  Context, or a store library — Zustand, Redux). Rejected for v1: it is the
  default shape the owner set out to avoid, and the app is a handful of
  independent panels over a server event stream — the exact case islands + an
  event bus fit best (decision 8). Zustand is noted not as wrong but as the
  community-blessed form of the same `useSyncExternalStore` idea, the fallback if
  the native bus is shown to chafe.
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
- **"Scroll back" friction is observed** ⇒ add a bounded history affordance
  (`/events?tail=N` or `GET /trace?tail=N`), the named v1 gap.
- **A control verb earns its way in** (a genuinely long Background task worth
  abandoning; a handler worth restarting from the UI) ⇒ its own ADR or an
  amendment here, co-designed with item 10's write-authz trust model.
- **The TS/DTO drift bites** (a hand-written type diverges from a shipped DTO) ⇒
  move to generated types (JSON schema from the DTOs, or NSwag).
- **Native-bus coordination gets gnarly** (a panel's state outgrows a few atoms,
  or the event wiring turns implicit and hard to trace) ⇒ adopt Zustand for that
  surface — the same external-store idea, shrink-wrapped — or XState for a
  genuinely stateful panel (decision 8).
- **A second production origin becomes unavoidable** (e.g. an embedded webview
  with a custom scheme) ⇒ revisit the same-origin decision and the Origin gate
  together.

## Ground truth (at acceptance)

*To be filled by the implementation slices (see `/adr-plan`).* Expected homes:
the `/ui` static route + MIME map + disk streaming (path-traversal-guarded) from
the `ui/` dir in `dotnet/captainHook/Api/`; the `captainHook ui` verb in
`Program.cs`; the frontend as a separate **`web/`** React+Vite project whose build
emits to the deploy's `ui/` dir; the frontend's islands + event-bus wiring
(`web/src/bus.*`, the `useBusEvent` / `useBusState` hooks, per-screen `createRoot`
mounts, and the SSE→bus adapter feeding decision 4's fetch-stream frames onto the
bus); Playwright E2E in `web/`; the `/deploy` skill
gaining a `ui/`-staging step; the token-handoff, same-origin serving, and
unauthenticated-inert-shell behavior pinned in the test suite; mechanics recorded
in a `doc/flow/management-gui.md` and this ADR's decisions cited where they land.
