# Roadmap

Living document — what to build and in what order. Decisions get ADRs
(`doc/adr/`), mechanics get flow docs (`doc/flow/`); this file only orders the
work. Check items off in the commit that lands them; reorder freely.

**The product vision this points at:** a runtime for managing custom
hooks/skills for AI agents — browse, one-click install (writes
`~/.claude/settings.json` / `.claude/skills/`), configure, and *watch them
run live*. The framework underneath is what exists today.

## Now

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
- [ ] **5. Management API** — HTTP + SSE on the daemon: inventory of
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
  Install operations carry item 10's
  trust model with them. The fleet/enterprise shape (one org, many
  employees) is local-data-plane + central-control-plane: per-machine
  daemons exactly as today, with policy distribution / config / telemetry
  aggregation as the centralized layer this API eventually fronts —
  never a shared remote daemon on the hot path (ADR-0004's transport
  revisit trigger stands).
- [ ] **6. GUI v1: browser UI** — localhost web app served by the daemon.
  Catalog + one-click install, live dispatch traces, supervision view
  (restarts/escalations as they happen). Web-first per the GUI direction
  below; on WSL2 this is the *best* UX, not a fallback. Lands WITH a
  Playwright harness (Microsoft.Playwright, same xunit suite): the DOM +
  accessibility tree is the agent-legible surface — semantic locators and
  auto-waiting beat TUI screen-scraping for the agentic dev loop.

## Later

- [ ] **7. Desktop shell** — wrap the same web assets in Photino (native
  window, .NET runtime in-process) once the browser UI proves the workflows.
  ADR to record Photino vs Tauri vs staying browser-only.
- [ ] **8. TUI** — geex-style terminal UI against the same API, for product
  reasons (SSH-side admin, terminal-native users) — not as the agent's
  feedback instrument: the feedback pyramid is API assertions (bulk) →
  Playwright over the web UI (visual) → TUI capture only to test the TUI.
- [ ] **9. Real handlers** — the payloads the framework exists for: retriever
  (forced-RAG on UserPromptSubmit) and memory (SessionStart/Stop,
  cavemem-shaped); the tool-gate (item 13 / ADR-0005) rejoins them as a
  payload. These deliberately wait for item 6 — the retriever needs
  retrieval infrastructure and all are better built once the GUI can show
  them running.
- [ ] **15. Handler capability policy (egress)** — layer 3 of the native
  policy story: what may a running handler *reach* — datastores, external
  agents/LLMs, network, filesystem, spawn, token/cost budgets. The
  enforcement principle to hold: handlers affect the loop only via the
  closed `Effect` set (ADR-0002) and reach the world only via
  `HandlerContext` — ctx hands out capabilities, policy gates what ctx
  hands out. Becomes real (and gets its ADR) with item 9's first
  egress-bearing handler; pairs with item 10's trust model; ADR-0004 N2's
  process isolation is the backstop for untrusted handler code.
- [ ] **10. Hook trust model** — installing a hook = installing arbitrary code
  that runs on every prompt. The install UX must show exactly what will
  execute, from where, before touching settings. **Rides WITH items 5–6**
  (the install operations and install UX are its only real surface), not a
  phase of its own.
- [ ] **11. N-runtime harness** — port the core spec to Node and BEAM
  (DESIGN.md's comparison thesis — still the point of the exercise).

## Parking lot

- **Mobile** — a responsive browser UI over LAN already answers the likely
  need; no app until a real use case demands one.
- **Community registry** — discovery/versioning for shared hooks & skills.
- **shipshape as a Stop-hook** — the repo verifying itself with the very
  mechanism it demonstrates.
- **Packaging** — single-file publish for the JIT engine (the shim's
  Native AOT half promoted to item 12).

## GUI direction (current leaning — becomes an ADR when work starts)

**One API, three faces, in this order:**

1. **Browser UI first.** The runtime becomes a daemon anyway (item 4); serving
   localhost HTML/JS reuses web skills we already have, needs zero packaging,
   and is first-class from WSL2 (Windows browser → localhost), where desktop
   GUI apps under WSLg are second-class.
2. **Photino when a desktop feel is wanted.** Same web assets in a native
   window with the .NET runtime *in-process* — UI to actors is a method call.
   Tauri's host is Rust, which would force the .NET runtime into a sidecar
   process behind an IPC boundary — at which point plain localhost-in-browser
   already does the same job with less machinery. Tauri is right when the
   core is Rust; ours is .NET.
3. **TUI as the dev-loop instrument** (item 8), driving the same API.

The structured log stream (JSONL + correlation ids) is the GUI's live data
feed — the observability layer was built GUI-ready before the GUI existed.
