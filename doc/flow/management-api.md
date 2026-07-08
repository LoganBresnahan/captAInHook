# Flow: the management API — a face on a serving daemon

The daemon's local control surface (ADR-0007, roadmap item 5): a loopback-only
HTTP API a GUI (item 6) uses to read what the daemon is doing and edit the one
piece of user config the daemon owns — the dispatch policy. It is a **face on a
serving daemon, never a reason one exists**: the daemon binds its socket and
warms its workers first, *then* starts this; the shim never learns it exists
(aot-boundary rule 1 — the listener lives in the JIT engine, not the wire lib);
and it is structurally off the sacred hook/stdout path (invariant 1). Zero new
runtime deps — the managed `HttpListener` is BCL.

```
 GUI / curl  ──HTTP──►  127.0.0.1:4665   (CAPTAINHOOK_API_PORT; 0 disables)
                            │  managed HttpListener, prefix http://127.0.0.1:<port>/
                            │  ── PREFIX-MATCHES Host: foreign/localhost → 404 here,
                            │     empty → 400, BEFORE any handler (platform.md)
                            ▼
                     AcceptLoopAsync  ── accept-and-HAND-OFF: each ctx runs on its
                            │            own task, the loop returns to accepting at
                            │            once. NEVER awaits a handler — a long-lived
                            │            SSE stream would otherwise wedge the API.
                            ▼
                     HandleAsync(ctx)
                       1. _onRequest()      ── stamp the daemon's idle clock (d7),
                       │                        BEFORE the gate: even a 401 proves
                       │                        interaction, and a warm daemon is capacity
                       2. ApiAuthGate.Evaluate(Host, Origin, Authorization)  ── EVERY request
                       │      ├─ Host   ≠ 127.0.0.1:<port>  → 403 bad_host
                       │      ├─ Origin present & ≠ own      → 403 bad_origin
                       │      ├─ Bearer ≠ token (const-time) → 401 unauthorized (+ WWW-Authenticate)
                       │      └─ ok → null
                       3. RouteAsync(ctx)   ── method + path, each gated on its collaborator
                            ├─ GET  /api/v1/status     → StatusDto      (+ openStreams)
                            ├─ GET  /api/v1/policy      → PolicyDto      (+ ETag header)
                            ├─ GET  /api/v1/harnesses   → HarnessesDto
                            ├─ GET  /api/v1/handlers    → HandlersDto    (gen/dead across C#/F#)
                            ├─ GET  /api/v1/events      → SSE tail       (long-lived; d5)
                            ├─ PUT  /api/v1/policy       → 200/422/412/413/500  (d4)
                            └─ else                      → 404 not_found
```

The reads (`ApiReadModel`) project the **same live Core objects the dispatch
path runs** — the policy resolver, the harness registry, the dispatcher's
supervised workers, the serve counters — so the API view is structurally
incapable of drifting from daemon behavior. No mocks, no parallel store, no
cache. The write (`ApiPolicyWriter`) is the mirror: an *editor of the file*, not
an owner of state.

## Discovery + credential — the file is the trust root

On bind the host writes a **0600 `api.json`** beside the socket
(`captaind-<version>.api.json`: port, token, pid, identity) and mints a random
256-bit bearer token — the SOLE credential source, exactly like the socket's
filesystem permissions are for the UDS. Written **under the same lock that flips
`_listening` true** and deleted when `Stop()` flips it false, so `api.json`
exists ⟺ this host holds the port: a client never reads a port+token for a
listener that has already handed the port off. Version-partitioned, so it is
always *ours* to delete, never a successor's; `doctor` backstops a leak.

## Auth — loopback is not a boundary

127.0.0.1 is every local user *and* every browser tab, so binding loopback
alone protects against neither. `ApiAuthGate` runs on **every** request before
the router (an authorized request that matches no route still 404s; an
unauthorized one never reaches the router):

- **Host** must be exactly `127.0.0.1:<port>` — defense-in-depth behind the
  listener's own prefix-match (a DNS-rebind page naming `attacker.example` is
  already 404'd by the listener; the gate makes the refusal portable and
  unit-tested).
- **Origin**, when a browser sends one, must equal the API's own origin — the
  CSRF/DNS-rebind guard.
- **Bearer token** compared with `CryptographicOperations.FixedTimeEquals` —
  constant-time, locked against a refactor-to-`==` by a SECURITY comment. A
  mismatch is 401 with `WWW-Authenticate: Bearer` (RFC 7235).

The token dies with the daemon that minted it: a successor mints its own, so a
forgotten tab's credential goes stale at cutover (see the GUI notes).

## Lifecycle — the port is a singleton the socket never was

The UDS rendezvous is version-partitioned; the TCP port is **one global**, so
two daemon identities that briefly coexist during a deploy contend for it. The
cutover contract (ADR-0007 d2, N1):

```
 successor: StartRetrying(port, fastWindow = drainDeadline)
   ├─ one SYNC bind attempt          ── the common free-port case stays deterministic
   ├─ HttpListenerException?         ── incumbent still holds it: api.bindContended
   │    └─ retry: 100ms→1s backoff spanning the drain deadline …
   │       then ONE api.bindBlocked warn, then a steady slow cadence, forever until Stop
   └─ bind lands → PublishDiscovery under the gate → AcceptLoopAsync

 incumbent: Stop() at DRAIN START (not exit)
   ├─ UnpublishDiscovery + api.stopped (logged BEFORE release → deterministic trail order)
   ├─ _stop.Cancel()  ── wakes the retry backoff AND every SSE writer riding the token
   └─ listener teardown on a BOUNDED-BACKGROUND task  ── platform.md: Stop()/Close()
        BLOCK behind a write wedged on a zero-window client; the port frees the
        INSTANT Stop() begins, so backgrounding costs the handoff nothing and a
        stalled subscriber can't make an unkillable daemon
```

Bind failure is **never fatal** — hooks serve throughout, the retry task touches
none of the idle-exit bookkeeping. An incumbent that lingers to idle-exit (up to
the idle window — nothing SIGTERMs it on deploy) still hands the port over
whenever it finally drains.

**Idle-exit defer (d7):** any API request stamps the idle clock; an **open SSE
subscription defers idle-exit**, `ApiHost.OpenStreams` (finally-decremented)
joining `active` and `BackgroundPending` in the watchdog's activity fold.
Current-lock-holder-ONLY by construction — drain-start `Stop()` terminates every
stream — so a superseded daemon is never pinned by a forgotten tab. And the
watchdog's quiet-tick **supersession self-check** re-fingerprints the deploy dir
and self-drains (`daemon.superseded`) if a newer build has landed: the immortal-
superseded-daemon loop, closed (d7's 2026-07-08 amendment).

## The live stream — SSE over a stat-poll tail of the JSONL trail

`GET /events` is Server-Sent Events (one-way; plain HTTP streaming, no
upgrade-protocol uncertainty in managed `HttpListener`; `EventSource` reconnect
for free) over a **portable stat-poll tail of the trail FILE** — not an
in-process tee. The file is where the one-schema/two-emitters design (ADR-0004
d7) converges both halves: the shim's lines (`shim.answered`, skew, spawn
decisions) and every collapsed dispatch never pass through the daemon's process,
so only the file gets the whole story. `TrailCursor` is **schema-blind** — it
ships opaque newline-delimited bytes, shrinking N4's third-consumer coupling to
"the trail is newline-delimited". The SSE **event id is the byte offset**, so
`Last-Event-ID` resumes without loss; no header ⇒ start at the file's current
end ("from now").

Backpressure is ADR-0004 d6 as reserved: a **bounded Channel per subscriber**,
drop-oldest **counted by hand**, with the gap and reset markers traveling
out-of-band (`Interlocked` fields) so they are structurally **un-droppable**. A
slow consumer — no room within one poll-beat of grace — gets a `gap` event
carrying the exact dropped count (no id: a reconnect recovers the dropped region
from the file), never a growing daemon and never a silent disconnect.

### `TrailCursor` edge behaviors (the file is a live, racing, hostile input)

| edge | behavior |
|---|---|
| resume at `Last-Event-ID` | seek that byte offset; a partial trailing line is held back (`More`) until its `\n` arrives — ids are exact byte ends, so no dup / no loss |
| **oversized line** (≥ `maxBytes`, the 128 KiB read window) | **skipped** across polls and surfaced as a `gap` — a line that can never fit the window must not wedge the feed forever (the correctness-threatening find of the sse-trail-tail verify). The 128 KiB window is a HARD limit. |
| truncation (file shrank) | `reset` event, id space restarts at 0 — the client re-anchors `Last-Event-ID` to 0 |
| mid-line resume (bogus offset) | alignment **self-heals** to the next `\n` boundary (the byte before a legit id is always `\n`) |
| truncate-then-regrow (255→256) | caught by a boundary-byte re-check every poll, not just on shrink |
| file absent / vanishes mid-read | quiet — an empty poll, retried; never an exception out of the tailer |
| CRLF | trimmed; `\r\n` and `\n` behave identically |

## The write — PUT /policy, editor of the file

`ApiPolicyWriter` validates the body with the daemon's OWN strict
`DispatchPolicy.TryParse` (**refuse to write what the daemon would refuse to
load**), honors `If-Match` when supplied (the content-hash ETag `GET /policy`
returns), and installs **atomically — temp+rename in the target's OWN
directory** so a concurrent hook stat-gating the file sees the old whole file or
the new whole file, never a torn/absent seam (`GetTempFileName()`+`Move` would
ship a cross-device, non-atomic write that transiently Noops every hook and
passes green single-threaded tests). It becomes effective on the next dispatch
via `ReloadingPolicy`'s `(mtime,size)` stat-gate, exactly as a hand-edit does —
no parallel store, no API-held config. See [dispatch-policy.md](dispatch-policy.md)
for what the policy then governs.

The closed `PolicyWriteOutcome` DU maps 1:1 to HTTP, so a new case can't silently
fall through to a wrong status:

| outcome | HTTP | when |
|---|---|---|
| `Written(etag)` | **200** + `ETag` | installed; the tag we WROTE (authoritative for the next `If-Match`) |
| `Invalid(errs)` | **422** | bad UTF-8, bad JSON, or a policy the daemon would refuse to load |
| `Mismatch(cur)` | **412** | `If-Match` supplied and stale (or the file is gone) |
| — (body cap) | **413** | body over 1 MiB, refused before any parse |
| `Failed(msg)` | **500** | the write itself failed (permissions, disk) — the file is untouched |

Concurrency is **guarded, not locked** (d4): `If-Match` narrows the blind-
overwrite window but the etag-read and the rename are not one atomic step — two
racing PUTs both pass and the later rename wins. The file is always a VALID whole
policy, which is the invariant that matters to the hot path. A leading BOM is
stripped so the writer and the daemon's loader agree (both `File.ReadAllText`-
strip it), keeping the ETag round-trip exact.

## Notes for the GUI (item 6)

- **Read `api.json`** (0600) for the port + token; it exists iff a daemon holds
  the port.
- **Use fetch-streaming, not `EventSource`, for `/events`.** Browser
  `EventSource` cannot set an `Authorization` header, and the API requires the
  bearer on every request. Read the SSE frames off a `fetch` `ReadableStream` and
  hand-roll reconnect off the last received id (`Last-Event-ID`).
- **Stop reconnecting on 401/403.** The token rotates with the daemon: a 401
  after a working stream means a cutover happened and the credential is dead —
  re-read `api.json` (new token, maybe new port) rather than hammering.
- **Match `api.json`'s `Version`.** It is version-partitioned; a tab holding a
  superseded daemon's identity should re-discover, not assume continuity.

## Ground truth

| what | where |
|---|---|
| listener host, accept-hand-off loop, `Start`/`StartRetrying`, `Stop`/`Dispose`, cutover, `ServeEventsAsync`, `ServePolicyPutAsync`, `ReadBodyAsync`, SSE `Frame` | `dotnet/captainHook/Api/ApiHost.cs` |
| auth decision (Host/Origin/Bearer, const-time compare) | `dotnet/captainHook/Api/ApiAuthGate.cs` |
| discovery file (0600 at birth, version-partitioned, `Write`/`TryRead`) | `dotnet/captainHook/Api/ApiDiscovery.cs` |
| read projection over live Core objects | `dotnet/captainHook/Api/ApiReadModel.cs` (+ `Etag`) |
| write path (validate → If-Match → atomic temp+rename), `PolicyWriteOutcome` DU | `dotnet/captainHook/Api/ApiPolicyWriter.cs` |
| SSE tailer (`TrailCursor`, `TrailSubscription`, `SseEvent`, backpressure) | `dotnet/captainHook/Api/TrailTail.cs` |
| response DTOs (camelCase, reflection STJ) + `ApiJson.WriteAsync` | `dotnet/captainHook/Api/ApiDtos.cs`, `ApiJson.cs` |
| daemon wiring (read model + writer + sse + `onRequest` idle stamp; supersession probe) | `dotnet/captainHook/Core/DaemonHost.cs` |
| serve counters (Interlocked) | `dotnet/captainHook/Core/ServeStats.cs` |
| port resolution (`4665`, `CAPTAINHOOK_API_PORT`, `0` disables) | `ApiHost.ResolvePort`, threaded via `Program.cs` |
| discovery path | `RendezvousPaths.ApiJsonPath` (`captainHookWire/Rendezvous.cs`) |
| endpoints | `GET /api/v1/{status,policy,harnesses,handlers,events}`, `PUT /api/v1/policy`, else 404 |
| log events | `api.listening`, `api.stopped`, `api.bindContended`, `api.bindBlocked`, `api.discoveryFailed`, `api.tailError`, `api.handlerError`, `api.loopCrashed`; `daemon.superseded` (src `api`/`daemon`) |
| pinned by | `ApiHostTests`, `ApiHostInDaemonTests` (listener + hand-off + in-daemon); `ApiAuthGateTests`, `ApiAuthHttpTests` (auth, listener prefix-match); `ApiDiscoveryTests`, `ApiDiscoveryInDaemonTests` (0600, publish⟺holds-port); `ApiPortResolveTests`, `ApiRetryBindTests`, `ApiCutoverTests` (port singleton + two-daemon cutover); `ApiReadEndpointsTests` (read DTOs); `TrailCursorTests`, `TrailSubscriptionTests`, `SseBackpressureTests`, `SseIdleDeferTests`, `ApiSseHttpTests` (SSE edges, backpressure, idle-defer, wedged-Stop bound); `ApiPolicyWriteTests` (write mapping, atomicity probe, end-to-end deny-short-circuit) |
| platform facts | `doc/platform.md` § Loopback TCP & managed `HttpListener` (co-bind, SO_REUSEADDR/TIME_WAIT pairwise, teardown-blocks-behind-wedged-write, Host prefix-match); § Runtime directories (0600 token, mtime resolution) |
| decision record | `doc/adr/0007-management-api.md` (d7 carries the 2026-07-08 supersession amendment) |
| the policy this API edits | [dispatch-policy.md](dispatch-policy.md) · the dispatch it observes | [hook-dispatch.md](hook-dispatch.md) |
