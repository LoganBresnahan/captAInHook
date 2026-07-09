# ADR-0007 — Management API: a localhost HTTP face on the daemon; the files stay the truth

**Status:** Accepted
**Date:** 2026-07-07

## Context

Roadmap item 5. The daemon (ADR-0004) is resident and observable — the JSONL
trail carries dispatchId-correlated traces from two emitters — and item 14
(ADR-0006) shipped the first user-managed contract: `dispatch.json`, hot-reloaded,
with the explicit layering *file → API → GUI*. The API is the middle of that
sentence: the surface item 6's GUI consumes and the local data plane a central
control plane may front later (per-machine daemons stay the hot path — ADR-0004's
transport trigger stands; fleet distribution is a control-plane layer, not a
shared remote daemon).

Two prior threads land here by name. ADR-0004 anticipated the event fan-out
("bounded Channels… the management API's event fan-out, where a slow subscriber
must meet backpressure rather than grow the daemon") and left one revisit
trigger pointed at this ADR ("the management API lands — persistent or
multiplexed connections may replace one-connection-per-dispatch"). ADR-0006
decision 7 deferred pause/resume ergonomics to "item 5's API… (its ADR decides
how)."

The constraint that shapes everything: **zero new runtime dependencies**
(invariant 3). Kestrel is a package; even `<FrameworkReference
Include="Microsoft.AspNetCore.App">` — zero PackageReferences — makes the
ASP.NET Core runtime a deploy-host requirement, a new runtime dependency in
spirit. The server is BCL or it is hand-rolled.

Scope for v1, decided up front: **reads + the live stream + policy writes.**
Install/uninstall operations (writing `settings.json` = installing arbitrary
code) wait for item 10's trust model, which rides with item 6's install UX —
not because the endpoints are hard, but because shipping them without the
trust surface is N1 of ADR-0006 made worse.

## Decision

1. **BCL `HttpListener`, loopback-only, daemon-mode only.** A new `Api/` area
   in the `captainHook` host — no new project; the shim never learns the API
   exists (aot-boundary rule 1: nothing but sockets and bytes enters the
   native image). The listener starts after the daemon is warm and bound —
   the API is a face on a serving daemon, never a reason one exists — and its
   accept loop runs beside the UDS serve loop without touching the dispatch
   path.

2. **A fixed default port, a discovery file, and an explicit cutover story.**
   The UDS rendezvous is version-*partitioned* — contention unrepresentable
   by construction. A TCP port is a **global singleton**; the versioned-socket
   trick does not transfer, so the ADR must say who owns the port when two
   daemon identities briefly coexist:
   - Default port **4665** ("HOOK" on a phone keypad — arbitrary but
     memorable), overridable via `CAPTAINHOOK_API_PORT` (daemon-start
     configuration like the rest of `CAPTAINHOOK_*`; `0` disables the API).
     A fixed default keeps the GUI's URL bookmarkable.
   - `api.json` (0600) beside socket/lock/pid in the runtime dir: port,
     token, pid, content identity. Programmatic clients discover through it;
     it is written after successful bind and removed at exit.
   - **Cutover:** a superseded or draining daemon closes its API listener and
     **terminates open SSE streams at drain start** — not at exit — so the
     successor's retry-bind (short backoff) acquires the port while the
     incumbent finishes in-flight hook dispatches. `EventSource` reconnect
     lands on the successor. Bind failure past the retry window is a warn,
     never fatal: hooks outrank the API.

3. **Read surface, `/api/v1`:** `GET /status` (identity, pid, uptime, warm
   stats), `GET /policy` (the **resolved** tri-state — Absent / Malformed
   with its carried parse error / Loaded — plus the raw file), `GET
   /harnesses` (the registry view: specs, adapters, capabilities), `GET
   /handlers` (registered handlers, fail modes, supervision state). The API
   renders from the same code the dispatch path runs (`PolicyResolution`,
   the harness registry) — no parallel view to drift. Inventory of
   *installed* hooks per harness install-target joins the surface with the
   install ops (deferred). Plain reflection STJ for DTOs — the host is JIT;
   source-gen ceremony buys nothing here.

4. **Write surface v1: `PUT /policy` — and the files stay the single source
   of truth.** The API is an *editor of the file*, not an owner of state: the
   body is validated with the same strict `DispatchPolicy.TryParse` (a
   malformed PUT is a 422 carrying the violations — the API refuses to write
   what the daemon would refuse to load), written atomically
   (temp + rename, same directory), and becomes effective exactly as a hand
   edit does — `ReloadingPolicy`'s stat-gate on the next dispatch. No
   parallel store, no cache, no API-held config. Concurrent hand-edits are
   guarded, not locked: `GET /policy` returns a content-hash ETag,
   `If-Match` on PUT is honored when supplied, not required. **No
   pause/resume convenience verbs yet** — ADR-0006 d7's trigger is toggle
   *friction observed*, and a GUI can express pause as a PUT of
   `{"version":1,"default":"deny"}`; the verb waits for evidence.

5. **The live stream is SSE over a tail of the JSONL trail file.** Two
   decisions in one:
   - **SSE, not WebSocket.** The feed is one-way; SSE is plain HTTP response
     streaming (no upgrade-protocol uncertainty in managed `HttpListener` on
     Unix), and browser `EventSource` brings reconnect for free.
   - **The file, not an in-process tee.** An in-process tap sees only
     daemon-side lines: the shim's half (`shim.answered`, skew, spawn
     decisions) and every collapsed dispatch never pass through the daemon's
     process. The trail file is where the one-schema/two-emitters design
     (ADR-0004 d7) already converges both halves — N3's split-trail cost now
     pays a dividend: one reader gets the whole story. Mechanics: portable
     stat-poll tail (inotify is Linux-only; hook rates make polling cheap);
     SSE event id = byte offset in the file, so `Last-Event-ID` reconnects
     resume without loss. *(id semantics superseded by ADR-0009 d2 — treat the
     id as an opaque resume cursor, not a byte offset; this "byte offset" wording
     is amended when trail segmentation lands.)*
   - **Backpressure as ADR-0004 d6 reserved it:** a bounded Channel per
     subscriber; a slow consumer gets drop-oldest plus an explicit gap
     marker event (count of dropped lines) rather than growing the daemon or
     being disconnected — degraded honestly, never silently.

6. **Auth: a per-daemon bearer token plus Origin/Host validation, on every
   request.** Loopback binding alone protects against neither other local
   users (anyone on the machine reaches 127.0.0.1) nor the browser (a
   malicious page can CSRF/DNS-rebind into localhost). The daemon mints a
   random token at API start, stores it in `api.json` (0600 — filesystem
   permissions are the trust root, exactly like the socket), and requires it
   as a bearer header; requests with a browser `Origin` must match the
   API's own origin. The trail data alone justifies guarding reads (prompts
   and tool calls transit it), and this seam is where item 10's trust model
   later hangs write-authorization. How the GUI acquires the token is item
   6's decision (the obvious shape: a CLI verb reads the file and opens the
   browser); the API's contract is only that the token file is the sole
   credential source.

7. **Idle-exit, ADR-0004's open question answered:** any API request resets
   the idle clock; an **open SSE subscription defers idle-exit**, riding the
   same bookkeeping the non-empty background queue uses — an attached
   observer is a bounding parent in exactly the sense ADR-0004 said the
   daemon lacks. Two edges pinned: the defer counts **only for the current
   lock-holder** (decision 2's drain-start stream termination means a
   superseded daemon can never be pinned alive by a forgotten tab), and the
   API never *spawns* a daemon — hooks and an eventual CLI verb do; a dead
   daemon simply has no API.

   *2026-07-08 amendment (idle-exit-defer's adversarial verify):* as first
   written, "only for the current lock-holder" was an effect of drain, not a
   mechanism — drain-start terminates streams, but for a superseded daemon
   idle-exit is the only path TO drain, and the defer blocks idle-exit: a
   forgotten tab could pin a stale daemon on the singleton port forever
   (nothing SIGTERMs it on deploy; `doctor` would have silently become the
   only version-cutover reaper). The mechanism now exists: on quiet ticks
   (no hooks in flight) a daemon **with an API** re-fingerprints its own
   deploy directory (`ContentIdentity` of `AppContext.BaseDirectory`,
   start-vs-now); a mismatch means the deploy moved on — it logs
   `daemon.superseded` and drains itself, which terminates the streams,
   releases the port, and lands the tab's reconnect on the successor. This
   also gives decision 2's "a **superseded** or draining daemon closes its
   API listener" clause the code it previously lacked. Hook activity skips
   the check: a daemon serving hooks is current by definition (shims compute
   the socket from the deployed identity).

8. **ADR-0004's "management API lands" trigger: examined, declined.** The
   hook path keeps one UDS connection per dispatch, untouched. The API is a
   separate listener on a separate transport; nothing here multiplexes or
   persists hook connections. The trigger is discharged without action.

Zero new runtime dependencies, as always: BCL + FSharp.Core only.

## Consequences

### Positive

- **file → API → GUI is proven, not just asserted.** Item 14's data gets its
  managed surface with zero new state: the API writes what the user could
  hand-edit, hot reload makes both paths identical, and drift between the
  API's view and the daemon's behavior is structurally impossible (same
  resolver, same file).
- **Malformed policy becomes visible instead of mysterious.** ADR-0006 N2's
  "one typo quiets every hook" now has a face: `GET /policy` returns
  Malformed with the parse error, and the GUI can show it.
- **The observability layer was GUI-ready before the GUI existed** — the
  roadmap's claim, now cashed: the SSE feed is a tail of a file that already
  exists, in a schema already golden-pinned, with dispatchId correlation
  already present.
- **Item 6 needs no new daemon work to start** — catalog reads, live traces,
  and the policy editor are all served here.

### Negative

- **N1 · The port is a singleton the way the socket never was.** Cutover
  contention, bind failures, and a stolen-port squatter are all representable.
  Mitigated: drain-start release + successor retry (decision 2), bind failure
  non-fatal, and the token means a squatter on 4665 can deny service but
  cannot impersonate a trusted API to a client that got its token+port from
  `api.json`.
- **N2 · A TCP listener widens the daemon's attack surface** from a
  0600-filesystem-permissioned socket to a port any local process can dial.
  The token (same 0600 trust root) and Origin checks close credentialed
  access; the residual is unauthenticated surface (parsing, DoS) —
  loopback-only, and `CAPTAINHOOK_API_PORT=0` exists for the paranoid.
  "Who may *write* policy" enforcement beyond same-user remains item 10's
  trust model, as ADR-0006 N1 already said.
- **N3 · An open SSE stream pins the daemon warm.** A forgotten tab defeats
  idle-exit for the current identity (never a superseded one — decision 7).
  Accepted: the defer ends when the connection dies, and a pinned *current*
  daemon is warm capacity, not a stale one.
- **N4 · The JSONL schema gains a third consumer.** Two golden-pinned
  emitters, now one reader; schema evolution moves three things per commit.
  Bounded: the reader lives in the same repo and the existing golden tests
  already force emitter changes to be deliberate.
- **N5 · Managed `HttpListener` on Unix is the least-loved corner of the
  BCL.** Adequate for localhost JSON + SSE; its quirks (prefix semantics,
  connection limits, response streaming behavior) get recorded in
  platform.md as they are met, per the lane rule.

## Alternatives considered

| Option | Why not |
| --- | --- |
| Kestrel via `FrameworkReference Microsoft.AspNetCore.App` | Zero PackageReferences but the deploy host must carry the ASP.NET Core runtime — a new runtime dependency in spirit; invariant 3 is about the deployed surface, not the csproj syntax |
| Hand-rolled HTTP/1.1 over `TcpListener` | We hand-roll OS plumbing when the BCL has nothing (ADR-0004 N4); here it has something. Correct HTTP parsing (headers, keep-alive, chunking) is a solved wheel |
| WebSockets for the event stream | Bidirectional machinery for a one-way feed; `HttpListener`'s Unix upgrade path is exactly the platform uncertainty SSE avoids; `EventSource` reconnect is free |
| HTTP over the existing UDS | Browsers cannot dial a Unix socket, and the GUI is the point; a second UDS for curl-users can ride later if wanted |
| In-process log tee as the feed source | Sees only daemon-side lines — misses the shim half and every collapsed dispatch; the trail file is where the whole story already converges |
| API-owned state / config store | The file is the contract item 14 froze; a store adds a second truth that drifts and breaks hand-editability |
| Per-identity versioned port (mirror the socket trick) | Unbookmarkable GUI URL, port exhaustion over rebuilds, and discovery becomes mandatory for humans; the singleton port + drain-start handoff is the smaller cost |
| No auth (trust loopback) | 127.0.0.1 is every local user and every browser tab; the write surface edits policy today and installs code tomorrow. ~50 lines buys the seam item 10 extends |
| Full install ops in v1 | Writing `settings.json` is installing arbitrary code; shipping that without item 10's trust surface — which rides with item 6's install UX — is ADR-0006 N1 armed |

## Revisit triggers

- **Install/uninstall ops wanted** (item 6's catalog UX arrives) → extend the
  write surface WITH item 10's trust model — one ADR together, per the
  standing plan.
- **Pause/resume toggle friction observed** (ADR-0006 d7's trigger) → the
  convenience verb lands here, over the same file.
- **A central control plane materializes** (the fleet note on item 5) → the
  per-daemon token grows an org story; revisit auth WITH item 10, not before.
- **A bidirectional need appears** (interactive flows, config push
  negotiation) → re-open SSE vs WebSocket with a concrete consumer in hand.
- **Env/config push wanted** (ADR-0004 N1's mitigation endgame) → the API
  gains restart-or-mutate semantics for daemon-start config; needs its own
  design, deliberately not smuggled into v1.
- **`HttpListener` limits bite** (TLS need, connection ceilings, streaming
  bugs) → re-open the server choice; the handler code is transport-thin by
  construction.

## Ground truth (at acceptance)

| what | where |
| --- | --- |
| daemon serve loop + idle-exit / background-queue defer bookkeeping the SSE defer joins | `Core/DaemonHost.cs` |
| policy resolve tri-state + strict parse + hot-reload stat-gate the API reuses | `Core/DispatchPolicy.cs` (`DispatchPolicy.TryParse`, `PolicyResolution.Resolve`, `ReloadingPolicy`) |
| the shared policy gate whose view `GET /policy` must match | `Core/HookRun.cs` (`PolicyGateFor`) |
| trail file path + line schema the SSE tail reads | `captainHookWire/WireJsonl.cs` (`DefaultLogPath`, `Render`); golden pins in `WireJsonlTests` |
| runtime-dir resolution `api.json` sits beside | `captainHookWire/Rendezvous.cs` |
| harness registry the reads render | `Core/Harness.cs` (ADR-0003) |
| drain semantics decision 2 extends (close listener, end streams) | ADR-0004 decision 4; `Core/DaemonHost.cs` |
| the policy file contract PUT validates against | ADR-0006 decision 1 |

## Implementation plan

Generated by [`/adr-plan`](../../.claude/workflows/adr-plan.js) on 2026-07-07 — a
**stable ranking** of this ADR's work into effort-tagged, dependency-ordered build
phases. Derived from the decisions above, not a decision itself; changes only if
they do. **Progress is tracked on the [roadmap](../roadmap.md) item 5, not here.**
Tags: `effort/hardness · verify?`; ◆ = on the critical path. 13 slices → 7 build
phases; critical path length 6; adversarial verify on exactly 6 slices; no ultracode.

**Phase 1 — API foundation (sole DAG root)**
- `api-listener-host` ◆ — med/moderate · no adversarial verify — deps: none. A
  loopback `HttpListener` + `/api/v1` router + reflection-STJ writer in a new
  `Api/` area, its accept loop a task beside the UDS serve loop, started after
  `DaemonRendezvous.BindWhenWarm` and daemon-mode-only, no endpoints wired.
  Correctness is observable by driving it, not a silent-ship race — over-marking
  verify is the warned failure.

**Phase 2 — port spine (front-loaded hard-reasoning)**
- `port-config-and-cutover` ◆ — high/hard-reasoning · **verify (adversarial)** —
  deps: api-listener-host. Port 4665 / `CAPTAINHOOK_API_PORT` / `0`-disables is
  mechanical; the point is N1's drain-start handoff — the port is a *global
  singleton*, so the version-partitioned UDS-lock trick does NOT transfer. Two
  identities coexist during a deploy: incumbent closes its listener and
  terminates SSE **at drain start** (not at exit), successor retry-binds on a
  backoff spanning the drain deadline, bind failure past the window is a warn.
  Reshapes the sacred drain sequence — do it now, while the bind path is fresh
  and before endpoints pile on. Adversarial-verify the two-daemon handoff race
  (successor-never-binds / brief co-bind, SO_REUSEADDR/TIME_WAIT).

**Phase 3 — credential spine (discovery → auth, in this order)**
- `api-json-discovery` ◆ — low/moderate · no adversarial verify — deps:
  api-listener-host, port-config-and-cutover. Writes `api.json` (0600,
  version-partitioned, created-at-0600 like the lock, removed at exit) and mints
  the random token — mirrors `DaemonRendezvous`'s pidfile pattern one file over.
  This slice IS the token file / sole credential source (avoids a cycle with
  auth). Failures are loud (connection-refused, failed bearer check).
- `auth-token-origin` ◆ — high/moderate · **verify (adversarial)** — deps:
  api-listener-host, api-json-discovery. ~50 lines, but the credential gate on
  the whole TCP surface (the seam item 10 extends). Sharp edges a single pass can
  get wrong: constant-time compare (`CryptographicOperations.FixedTimeEquals`),
  `Origin` present ⇒ must match / absent ⇒ allowed (so curl works, a CSRF/rebind
  page doesn't), Host-header validation. Land it **before any endpoint is
  exposed** — never ship the surface unauthenticated.

**Phase 4 — read endpoints (parallel antichain, one STJ-DTO session)**
All four depend ONLY on api-listener-host, wrapped by the P3 auth middleware;
none warrant adversarial verify (read-only / fail-open / self-correcting).
- `get-status` — med/moderate · no adversarial verify — deps: api-listener-host.
  Content identity, pid, uptime, warm/serve counters (adds bookkeeping to
  `RunAsync`, which keeps state in locals today).
- `get-policy-read` — med/moderate · no adversarial verify — deps:
  api-listener-host. Renders `PolicyResolution.Resolve`'s tri-state + raw bytes +
  a content-hash **ETag** — the scheme `put-policy-write`'s If-Match consumes, so
  it must precede Phase 6.
- `get-harnesses` — low/mechanical · no adversarial verify — deps:
  api-listener-host. Registry projection off `ReloadingHarnessRegistry.Current`.
- `get-handlers` — med/moderate · no adversarial verify — deps:
  api-listener-host. Carries the batch's only new plumbing — plain-data `Worker`
  accessors (`Generation`/`IsDead`, mirroring the `AskStatus` seam; rich F# DUs
  stay inside) + a `Dispatcher` snapshot across the C#/F# boundary. **Do the F#
  accessor first** within the batch.

**Phase 5 — SSE trail + idle-defer (off the critical path)**
- `sse-trail-tail` — high/hard-reasoning · **verify (adversarial)** — deps:
  api-listener-host. **First** in the phase — owns the portable stat-poll file
  tail, byte-offset event ids, `Last-Event-ID` resume, and per-subscriber
  fan-out. Three sharp edges: never emit a half-written line (shim O_APPENDs
  concurrently), byte-offset resume with zero dup/loss, file lifecycle
  (not-yet-created / truncation / EOF). The trail's **third** consumer (N4) —
  watch the `WireJsonlTests` golden pins.
- `sse-backpressure` — high/hard-reasoning · **verify (adversarial)** — deps:
  sse-trail-tail. Per-subscriber bounded `Channel` + gap marker carrying the
  dropped-line count. `BoundedChannelFullMode.DropOldest` discards *silently* —
  it can't carry the count directly, so drop-and-count manually and make the gap
  marker itself un-droppable. Parallel with idle-exit-defer.
- `idle-exit-defer` — med/moderate · **verify (adversarial)** — deps:
  api-listener-host, sse-trail-tail, port-config-and-cutover. An open-observer
  counter mirroring `Dispatcher`'s `_sidePending`, folded into `DaemonHost`'s
  idle watchdog beside `active`/`BackgroundPending`; a request refreshes the
  stamp, an open SSE stream defers exit — **current-lock-holder only** (this is
  where port-cutover's drain-start SSE-termination stub gets cashed). Put the
  decrement in a `finally` — a leaked counter yields an immortal daemon that
  defeats the version-cutover reaper.

**Phase 6 — mutating write**
- `put-policy-write` ◆ — med/moderate · **verify (adversarial)** — deps:
  api-listener-host, auth-token-origin, get-policy-read. Editor-of-the-file, not
  owner-of-state: reuse `DispatchPolicy.TryParse` + `ReloadingPolicy`'s stat-gate
  (zero coordination). The genuinely new/sharp work is the **atomic temp+rename
  in the target's OWN directory** (the `GetTempFileName()`+`Move` mistake ships a
  cross-device, non-atomic write that transiently Noops every hook and *passes
  green tests*) and the tri-state → HTTP mapping (422 on malformed/violations,
  412 on If-Match mismatch). It mutates the LIVE `dispatch.json` on the hook hot
  path — mandatory end-to-end verify.

**Phase 7 — docs capstone**
- `docs-flow-platform` ◆ — low/moderate · no adversarial verify (shipshape only)
  — deps: all 12. Terminal by construction: a management-API flow doc (ASCII +
  why-prose + ground-truth table naming every endpoint/test) plus platform.md's
  `HttpListener`-on-Unix quirks (N5), recorded as met.

**Critical path:** api-listener-host → port-config-and-cutover → api-json-discovery
→ auth-token-origin → put-policy-write → docs-flow-platform (length 6). The SSE
chain (listener → trail → backpressure) and idle-defer trail off the critical path
and can run in parallel without extending it — the reads (Phase 4) are gap-filler,
unblocked from Phase 1 and slottable while the hard slices bake.

**Sequencing traps.** (1) `port-config-and-cutover` lands before any SSE exists, so
its "terminate SSE at drain start" is a no-op **stub** — leave an explicit seam in
the drain path and cash it at Phase 5; this edit touches the sacred `DaemonHost`
drain sequence, the feature's highest-blast-radius change. (2) The DAG permits the
Phase 4 reads before auth, but **never `/deploy` a build that exposes any endpoint
without `auth-token-origin`** — auth is a ship co-requisite for the whole TCP
surface, not just the write verb. (3) `put-policy-write` mutates the live
`dispatch.json` that governs whether every hook is worked — end-to-end verify, not
just green tests. (4) `idle-exit-defer` must follow port cutover so the
current-lock-holder-only edge is real; decrement in a `finally`. (5) `sse-trail-tail`
is the JSONL trail's third consumer (N4) — golden-pin awareness. (6) Gate
discipline: shipshape before every commit; suite green **twice** before any
`/deploy`. Adversarial verify on exactly six slices (port-config-and-cutover,
auth-token-origin, sse-trail-tail, sse-backpressure, idle-exit-defer,
put-policy-write); **no slice warrants ultracode** — each verify-worthy slice is one
cohesive mechanism, not parallelizable subwork.
