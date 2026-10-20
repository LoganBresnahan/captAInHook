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
 dispatch ──► spawn (prefix) ──► envelope → stdin, close ──► answer|exit RACE
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

## The spawn prefix: which wrapper makes the group

Every payload — both modes — is spawned *through a wrapper* that makes the
child a session leader and then execs the payload IN PLACE (same pid, so
`Process.Id` is both the payload's pid and its pgid). The wrapper is
structurally necessary: `setsid(2)` must run inside the child between fork
and exec, a window no managed runtime exposes and FFI cannot reach (P/Invoke
would set the attribute on the *daemon*).

`ProcessGroup.Resolve(baseDir, PATH)` picks one of three rungs, once per
process, and the winner rides every `exec.spawn` line as `spawner=`:

```
  1. bosun    co-located in the deploy dir (AppContext.BaseDirectory)
              the artifact we ship + pin (ADR-0014); works on macOS;
              argv: bosun -- <command> [args...]      ← the `--` is MANDATORY
  2. setsid   from PATH — util-linux, so absent on stock macOS
              argv: setsid <command> [args...]
  3. none     no wrapper: direct exec, pgroup=false, tree-walk kills only
              (the reparented-grandchild hole is open — loud per spawn)
```

Rung 1 must win on a live deploy; `/deploy` treats a missing co-located
`bosun` as a staging defect, not an acceptable degrade. A dev tree normally
resolves rung 2 — which is why the bosun argv contract is pinned by tests
(`SpawnPrefixTests`) rather than by whichever rung the machine happens to
have. bosun is native ⇒ carries no MVID ⇒ invisible to content identity (the
`captainShim` rule), so a bosun-only swap does not roll the socket identity
and takes effect on the next *spawn*.

## Teardown & records

Kill paths take the process GROUP (the spawn prefix made it; TERM→2s
grace→KILL); graceful oneshot conclusions release it. Resident children die at: budget
overrun, protocol failure, readiness failure, instance eviction (supervised
restart), escalation, daemon drain (the N6 child phase), idle-exit, and
DisposeAsync — never survive their daemon. Each resident child writes
`~/.captainHook/children/child-<pid>.json` (pid + start-time identity —
raw `/proc` ticks on Linux, BCL `Process.StartTime` on macOS, ADR-0012 d3
proof + entry + daemonPid) at spawn — deliberately NOT the XDG tmpfs, which
is wiped exactly when the forensic record matters (SIGKILLed daemon) —
deleted only at confirmed GROUP death; a per-process sweep clears stale
records (doctor-orphans, phase 8, is then pure read-side).

### After a hard death: the boot reap (2026-10-19 amendment)

Every path above needs a daemon alive to run it. A SIGKILLed daemon tells
its children nothing but EOF, and measured on both OSes three kinds outlive
it: a resident that ignores stdin EOF, any member of a resident's group (a
forked grandchild), and a oneshot mid-run. The NEXT daemon kills them:

```
 daemon wins its rendezvous (TryAcquire)
   │
   ├─ OrphanReaper PLAN — synchronous, before the dispatcher spawns anything
   │    daemon-*.spawns of a DEAD owner ─► in-flight entries (spawn, no Done)
   │                                       become child-<pid>.json; ledger deleted
   │    child-*.json, per record — ChildRecords.Classify:
   │      Gone (leader dead, group empty) / Reused (start time differs) ─► delete
   │      owner alive (pid + DaemonStartTime; a collapsed run counts) ─► leave
   │      Alive (start time proven)          ─► target: kill its group
   │      GroupSurvives (leader dead, -pgid answers) ─► target: kill the group
   │      Unproven (no recorded start time)  ─► keep; doctor reports; no signal
   ├─ SpawnLedger.Open — daemon-<ownpid>.spawns, fd held for the daemon's life
   ├─ warm up, bind, serve …                     (meanwhile, in the background:)
   │                                  KILL: TERM group → 2s → KILL, daemon.reapOrphan,
   │                                        record deleted if it is still the same one
   └─ drain: … child phase … await the reap (bounded) → ledger closed + deleted
             (kept when the child phase timed out — the next boot reaps from it)
```

The ledger is the oneshot's record without the item-12 tax: per spawn, one
appended line at Process.Start and a `{"Done":pid}` line when the dispatch
lets go (`Untrack`, or teardown) — two `write(2)`s on an open fd, no create,
unlink or fsync. The Done lines narrow the reap's candidates to what was in
flight at the death. Collapsed runs keep none (`SpawnLedger.Current` is null
outside a daemon; a test daemon passes no `childrenDir`, so it never reads or
signals from the live tree). Doctor reads the same verdict, reports both
shapes plus a dead daemon's in-flight oneshots, and still signals nothing.

A child's stderr is drained in full but RECORDED capped: `exec.stderr` carries
at most `ExecHandler.StderrCap` (2048) characters, and when it cuts, the row
says so — `truncated: true` plus the `chars` there were, because a reader
cannot tell an ellipsis the child typed from one the engine added and a counter
cannot be told anything by either. Silent under the cap. This matters most
where the stderr row is the child's whole output rather than its diagnostics: a
turn payload sends the model's stdout to stderr on purpose (our stdout is the
exec wire), so the cap is what stands between the trail and the answer
(ADR-0017, slice `nudge-accounting`). What the row still does NOT carry is what
the child SPENT — see that slice's row for why that is a recorded limit rather
than a column.

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
`sh`, driven end-to-end through the daemon by `DemoPayloadTests`. Five more
run as LIVE dogfood on the maintainer's own hooks: `git-orient.sh` (oneshot,
SessionStart — once-per-session git bearing), `deploy-guard.sh` (oneshot,
PreToolUse — `Decide(ask)` when any tool call — whatever the host names it — would mutate
the live install — the versioned `~/.local/share/captainhook` layout and its `~/.local/bin` links
(ADR-0020), or the legacy `~/.captainHook/bin`; pinned by `DeployGuardTests`), `session-pulse.sh` (oneshot, Stop — per-turn activity
ledger), `doc-pointer.sh` (resident, PreToolUse — warm ground-truth index,
injects keep-docs-in-sync reminders), and `orient-brief.sh` (oneshot,
SessionStart — LLM-backed "since last time" brief via `claude -p`, 30min
cache; ⚠ a payload must not transitively fire its own event — the
serialized worker self-blocks; orient-brief's `--setting-sources ""` is the
pattern, roadmap item 17). Field reports: [doc/dogfood/](../dogfood/).

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
| kill mechanics (group TERM→grace→KILL, group-aware liveness) + the spawn-prefix rungs (`SpawnPrefix`, `Resolve`, `Prefix`) | `dotnet/captainHook/Handlers/ProcessGroup.cs`; argv assembled in `ExecHandler.BuildPsi` |
| child records + sweep + the one staleness verdict (`Classify`, group-aware) + owner identity (`OwnerAlive`) | `dotnet/captainHook/Handlers/ChildRecords.cs` |
| boot-time orphan reap (plan before the first spawn; background kills; `daemon.reapOrphan`) | `dotnet/captainHook/Handlers/OrphanReaper.cs`; `Core/DaemonHost.cs` (`childrenDir`, passed by `Program.cs` only) |
| oneshot spawn ledger | `dotnet/captainHook/Handlers/SpawnLedger.cs`; appended by `ExecHandler` (spawn, `Untrack`, `DisposeAsync`) |
| admission seam (`IEagerStart`, `TrackSwap`, `DisposeHandlersAsync`) | `dotnet/captainHook/Core/Dispatcher.cs` |
| hot-reload seam (`Reconcile`, `AssignIds`, `ExecFingerprint`, volatile `_runners`, `ReloadingHandlers`, `Supervisor.Remove`) | `dotnet/captainHook/Core/Dispatcher.cs`, `Core/HookRun.cs`, `dotnet/captainHookActors/Supervision.fs` |
| doctor-orphans (report-only; group-aware; records + dead daemons' ledgers) + observability (child state + expected-vs-registered) | `Core/Doctor.cs` (`SweepOrphans`), `Core/Dispatcher.cs` (`IResidentObservable`, `Snapshot`), `Api/ApiReadModel.cs` + `Api/ApiDtos.cs`, `web/src/HandlersPanel.tsx` |
| registration (tri-state file, warns, resident degrade, per-dispatch reconcile) | `dotnet/captainHook/Core/ExecHandlersFile.cs`, `Core/HookRun.cs` |
| trail events | `exec.spawn/ready/notReady/answered/exit/stderr/kill/protocolError/recordError/ledgerError`, `handlers.malformed/entrySkipped/slowShape/fieldIgnored/budgetBeyondHarness/budgetBeyondDrain/readinessBeyondBudget/residentFanout/residentDegraded/collapsedTeardownTimeout/reload`, `handler.teardown(-Error)`, `daemon.drainChildren/drainCut/drainChildrenTimeout/reapOrphan/reapError`, `actor.remove`, `doctor.orphan` |
| pinned by | `ExecWireTests`, `ExecHandlerTests`, `ExecHandlersFileTests`, `KillDisciplineTests`, `SpawnPrefixTests` (rung order, degrade, per-rung argv, the bosun `--` contract against a real in-place exec), `ResidentExecHandlerTests` (incl. the daemon E2E), `HandlersHotReloadTests` (reconcile diff + stat-gate + `Supervisor.Remove` + child-state Snapshot), `DoctorOrphansTests`, `OrphanReaperTests` (verdict table, ledger, real-group reaps, daemon-start wiring), `release/smoke.sh` step 10 (SIGKILLed installed daemon, both OSes), `ApiReadEndpointsTests` (expected-vs-registered) + the Handlers-view e2e (`panels.spec.ts`) |
| decisions | `doc/adr/0010-exec-handlers.md` (d1–d9 + amendments); the spawn prefix in `doc/adr/0014-bosun-spawn-wrapper.md` (d1–d5); platform facts in `doc/platform.md` § Process groups |
| the wrapper itself | <https://github.com/LoganBresnahan/bosun> — pinned by tag + `SHA256SUMS` in `.claude/skills/deploy/SKILL.md` § 1a |
