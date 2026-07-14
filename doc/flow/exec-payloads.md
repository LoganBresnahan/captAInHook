# Flow: exec payloads — user processes as handlers (ADR-0010)

The engine's one extensibility seam: a configured command run as the payload,
its stdout mapped onto the closed `Effect` set. Two modes, one wire contract.

## The child wire (both modes)

```
 engine ──stdin──►  {"v":1,"dispatchId":"…","event":{type,sessionId,cwd,payload}}\n
 engine ◄─stdout──  {"effect":"inject","text":…[,"dispatchId":"…"]}\n     (one line)
                    {"effect":"decide","verdict":"allow|deny|ask"[,…]}
                    {"effect":"replace","text":…} | {"effect":"noop"}
 engine ◄─stderr──  diagnostics → exec.stderr (trail), never protocol
```

Strict per the Frame.Decode house rules: unknown/duplicate fields, unknown
effect kinds, trailing content ⇒ MALFORMED (never guessed). Children must
ignore unknown ENVELOPE fields — additive evolution inside `v:1`; shape
changes bump `v`. The `dispatchId` echo is optional-must-match for oneshot,
**mandatory** for resident (attribution in a multi-conversation stream).

## Oneshot: spawn per dispatch

```
 dispatch ──► spawn (setsid) ──► envelope → stdin, close ──► answer|exit RACE
                                                              │
        answer parsed ⇒ effect now, reaper owns the afterlife │ EOF+exit0 ⇒ Noop
        (reply-then-linger)                                   │ exit≠0 ⇒ fail mode
                                                              │ garbage ⇒ kill, fail mode
        budget cancel ⇒ TERM group now, grace→KILL detached, OCE rethrown
```

## Resident: the daemon holds the child warm

```
        Dispatcher ctor (daemon warm-up, pre-bind)
              │ factory → TrackSwap(ADMISSION) → IEagerStart.Start(predecessor)
              ▼            (torn-down: refused — no spawn, ever)
         ┌─ SPAWNING ─── {"ready":1} within readinessTimeoutMs ────► READY
         │     │                                                      │
         │     │ timeout / early exit / non-handshake first line      │ spontaneous exit
         │     ▼                                                      ▼
         │  FAILED ◄──────────────────────────────────────────── FAILED
         │  (kill group; each transition exactly once, winner kills)
         │
         │ dispatch while SPAWNING + budget expires ⇒ fail-mode effect,
         │   exec.notReady, UNCOUNTED — the child keeps warming
         │ dispatch finds FAILED ⇒ throw (COUNTED) ⇒ restart ⇒ fresh
         │   instance ⇒ fresh spawn (sequenced behind the predecessor's kill)
         ▼
        READY: lock-step conversation per dispatch
          envelope line → stdin (stays open) → read ONE answer line
            echo == sent      ⇒ effect (exec.answered)
            echo missing/off  ⇒ protocol failure ⇒ kill + COUNTED throw
            malformed         ⇒ protocol failure ⇒ kill + COUNTED throw
            EOF               ⇒ died mid-conversation ⇒ COUNTED throw
            budget overrun    ⇒ kill + raw OCE (honored cancel, UNCOUNTED)
          — every timeout path replaces the child (primary stale-answer
            defense); the mandatory echo is the backstop.
```

Warming dispatches never kill the booting child (the dispatch token must not
race the readiness timer — the boot-starvation find); a chronically failing
child is loud per dispatch and escalates only under fast traffic (no
cross-generation memory, by the fresh-state doctrine). One instance per
(event, entry): an entry on N events runs N independent children
(`handlers.residentFanout` says so; externalize shared state).

## Teardown & records

Kill paths take the process GROUP (setsid at spawn; TERM→2s grace→KILL);
graceful oneshot conclusions release it. Resident children die at: budget
overrun, protocol failure, readiness failure, instance eviction (supervised
restart), escalation, daemon drain (the N6 child phase), idle-exit, and
DisposeAsync — never survive their daemon. Each resident child writes
`~/.captainHook/children/child-<pid>.json` (pid + /proc starttime identity
proof + entry + daemonPid) at spawn — deliberately NOT the XDG tmpfs, which
is wiped exactly when the forensic record matters (SIGKILLed daemon) —
deleted only at confirmed GROUP death; a per-process sweep clears stale
records (doctor-orphans, phase 8, is then pure read-side).

Collapsed runs (no daemon): a resident entry DEGRADES to oneshot-lifecycle
(ADR-0010 d3, phase 6) — the collapsed run passes its one event so only that
event's handlers register (an unrelated event's resident child never spawns
for a single-shot hook), the real `ResidentExecHandler` runs
spawn→serve-one→die, and `CollapsedAsync` awaits `DisposeHandlersAsync` in a
`finally` AFTER the stdout answer so the child is always reaped (N3's
no-orphan is structural). A fail-closed gate genuinely runs — real verdict,
or fail-closed deny on any spawn/ready/protocol failure — never a silent
grant. `handlers.residentDegraded` marks it in the trail.

Demo payloads live in [`examples/payloads/`](../../examples/payloads/):
`retriever.sh` (resident, PreToolUse, injects a matched note) and
`memory.sh` (oneshot, Stop, appends a durable log) — dependency-free POSIX
`sh`, driven end-to-end through the daemon by `DemoPayloadTests`.

## Hot reload (phase 7) — edit the file, next hook obeys

```
 DispatchOneAsync ─► ReloadingHandlers.MaybeReload()  (per dispatch, before fan-out)
                       │ stat (mtime,size) == last?  ─ yes ─► return (lock-free)
                       ▼ no
                     BuildDefaultRegistry(file)   (re-validate: malformed/skip/warn all re-logged)
                       │
                       ▼
                     Dispatcher.Reconcile(fresh)  ── diff exec workers by id+fingerprint ──►
                       id+fp unchanged  ⇒ KEEP    (warm child untouched — NO churn)
                       id gone          ⇒ REMOVE  (Supervisor.Remove + evict→kill, drain-then-die)
                       id, fp changed   ⇒ CHANGE  (Supervisor.Remove, re-Spawn same id;
                                                    TrackSwap evicts old, threads its kill as predecessor)
                       new id           ⇒ ADD     (Spawn, warms; no predecessor)
                       └─ publish _runners (coded ++ exec, fresh order) with one volatile write
```

The Dispatcher only ever spawned workers in its ctor; phase 7 invents the
RUNTIME refresh seam. Identity is a STABLE worker id — `(event, name)`,
deterministic via `AssignIds` (coded handlers are a constant id-space seed,
exec names file-unique) — so an unchanged entry keeps the same id across
reloads and therefore its warm child. Version is a config FINGERPRINT
(`HookRun.ExecFingerprint` — an INJECTIVE length-prefixed encoding of
command/args/mode/failMode/budget/readiness/env/passEnv/cwd; event+name are the
id, deliberately excluded, so editing an entry's events list churns only the
added/removed events' children). The stat-gate is a literal `ReloadingPolicy`
mirror (`ReloadingHandlers` — same `(mtime,size)` stamp, same benign race,
change-detection-not-interval on the wall clock); the collapsed path is
single-shot and never reloads. A malformed edit re-builds to ZERO exec handlers
⇒ every resident is a REMOVE — malformed kills ALL residents, no keep-last-good,
`handlers.malformed` loud; the reconcile summary rides `handlers.reload`
(added/removed/changed/kept). The kills are the SAME restart/drain paths (a
CHANGE rides `TrackSwap`, a REMOVE the fire-and-forget disposal the drain
awaits) — the reload adds no bespoke kill path. `Supervisor.Remove` is the one
new F# member: the inverse of Spawn (MarkDead, drop the child-spec so a late
exit never restarts a retired worker), logged `actor.remove`.

## Ground truth

| what | where |
|---|---|
| envelope/answer codec, `TryParseReady`, echo extraction | `dotnet/captainHook/Core/ExecWire.cs` |
| oneshot adapter (answer/exit race, reply-then-linger, echo-if-present) | `dotnet/captainHook/Handlers/ExecHandler.cs` |
| resident runtime (state machine, lock-step, mandatory echo, eager start) | `dotnet/captainHook/Handlers/ResidentExecHandler.cs` |
| kill mechanics (setsid probe, group TERM→grace→KILL, group-aware liveness) | `dotnet/captainHook/Handlers/ProcessGroup.cs` |
| child records + sweep | `dotnet/captainHook/Handlers/ChildRecords.cs` |
| admission seam (`IEagerStart`, `TrackSwap`, `DisposeHandlersAsync`) | `dotnet/captainHook/Core/Dispatcher.cs` |
| hot-reload seam (`Reconcile`, `AssignIds`, `ExecFingerprint`, volatile `_runners`, `ReloadingHandlers`, `Supervisor.Remove`) | `dotnet/captainHook/Core/Dispatcher.cs`, `Core/HookRun.cs`, `dotnet/captainHookActors/Supervision.fs` |
| doctor-orphans (report-only double-guard) + observability (child state + expected-vs-registered) | `Core/Doctor.cs` (`SweepOrphans`), `Core/Dispatcher.cs` (`IResidentObservable`, `Snapshot`), `Api/ApiReadModel.cs` + `Api/ApiDtos.cs`, `web/src/SupervisionPanel.tsx` |
| registration (tri-state file, warns, resident degrade, per-dispatch reconcile) | `dotnet/captainHook/Core/ExecHandlersFile.cs`, `Core/HookRun.cs` |
| trail events | `exec.spawn/ready/notReady/answered/exit/stderr/kill/protocolError/recordError`, `handlers.malformed/entrySkipped/slowShape/fieldIgnored/budgetBeyondHarness/budgetBeyondDrain/readinessBeyondBudget/residentFanout/residentDegraded/collapsedTeardownTimeout/reload`, `handler.teardown(-Error)`, `daemon.drainChildren/drainCut/drainChildrenTimeout`, `actor.remove`, `doctor.orphan` |
| pinned by | `ExecWireTests`, `ExecHandlerTests`, `ExecHandlersFileTests`, `KillDisciplineTests`, `ResidentExecHandlerTests` (incl. the daemon E2E), `HandlersHotReloadTests` (reconcile diff + stat-gate + `Supervisor.Remove` + child-state Snapshot), `DoctorOrphansTests`, `ApiReadEndpointsTests` (expected-vs-registered) + the `SupervisionPanel` e2e |
| decisions | `doc/adr/0010-exec-handlers.md` (d1–d9 + amendments); platform facts in `doc/platform.md` § Process groups |
