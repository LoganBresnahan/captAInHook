# Flow: dispatch policy — the front door answers everything, works only what's allowed

captAInHook's own policy surface (ADR-0006, roadmap item 14): a user-editable
`~/.captainHook/dispatch.json` decides whether an arriving hook gets *worked* —
dispatched to handlers — or short-circuited to an immediate, valid Noop. The
hook is **always answered**; policy only ever chooses between "worked" and "a
valid Noop on the wire". This is the data the management API (item 5) will
manage and the GUI (item 6) will edit: file → API → GUI.

```
 dispatch.json  (~/.captainHook/, or CAPTAINHOOK_DISPATCH_FILE)   ← user-editable
        │  consulted at the gate, per dispatch
        ▼
 PolicyResolution.Resolve(path)   ── the TRI-STATE (the one I/O boundary)
   ├─ no file              → Absent          allow everything (zero-config default)
   ├─ present but          → Malformed(err)  deny everything, LOUD — carries the
   │  unreadable/unparseable                 fault. (directory, dangling symlink,
   │                                          empty, bad JSON, schema-invalid)
   └─ valid                → Loaded(policy)   evaluate
        │
        ▼
 PolicyGateFor(resolution, spec, evt, dispatchId)   ← the ONE shared gate (no drift)
   resolution.Evaluate(event, cwd, session) → PolicyOutcome{ Work, ExcludedHandlers }
   ├─ Work == false → SHORT-CIRCUIT: HookRun.DeniedStdout (byte-identical Noop)
   │                   · Malformed file  → deny every hook          ── policy.malformed
   │                   · event-level deny rule → deny this dispatch ── policy.skip
   │                   (dispatcher never built · no budget · no background drain)
   └─ Work == true  → PROCEED, dropping ExcludedHandlers from the fan-out ── policy.exclude
        │
        ▼
   Dispatcher.DispatchAsync(evt, dispatchId, excluded)   ── see hook-dispatch.md
```

The gate sits at the **identical seam in both dispatch sites** — after
`Harness.ParseEvent` (it needs event name, cwd, session), before the dispatcher
is touched:

- `HookRun.CollapsedAsync` (single-shot: resolve the file once);
- `DaemonHost.DispatchOneAsync` (long-lived: `ReloadingPolicy.Current`, a
  per-dispatch `(mtime,size)` stat-gate over `Resolve`).

Both call the one `HookRun.PolicyGateFor`, so the two paths cannot drift — the
decision, the denied stdout bytes, and the trail lines are computed once.

## The failure direction is the whole point

The two misclassifications are both costly and land in *opposite* directions
from an authz gate, because the domains differ. Hooks are *enhancement*, so:

- **Absent ⇒ allow everything.** No file is today's behavior — a zero-config
  user feels nothing.
- **Malformed ⇒ Noop everything, loudly.** A file that exists but cannot be
  parsed dispatches NOTHING; every hook answers Noop, and every skip logs
  `policy.malformed` with the parse error. A present file is intent-to-configure,
  and a policy you cannot parse must never silently grant execution — so it fails
  toward *quiet* (the agent is unharmed, the trail is loud), never toward
  running what the user tried to restrict. **No keep-last-good**: the same
  rejection as ADR-0005, and stateless everywhere else the rendezvous decides.

`Resolve` guards the *ambiguous* cases toward Malformed on purpose: a directory
at the path, and a dangling symlink or a file replaced mid-read (both look
"absent" to a naive `File.Exists`), classify Malformed, not Absent — a file the
user pointed at may have carried a restriction. `Resolve` never throws; every
failure mode lands in a case (a deliberately-`mkfifo`'d path is the one
pathological exception — out of scope).

## The rule language

One strict JSON file, the house policy dialect (ADR-0005 established the shape;
ADR-0006 reuses it). Parsed by `DispatchPolicy.TryParse` with the same
strict-walk as `HarnessSpec.TryParse` — collect every violation, all-or-nothing,
never throw on bad *data* — but *tighter*: **unknown fields, an unknown or
missing `version`, an `ask` decision, duplicate fields, and criteria-less rules
are all MALFORMED** (the dialect never guesses).

```json
{ "version": 1, "default": "allow",
  "rules": [
    { "event": "SessionStart", "decision": "deny" },
    { "handler": "echo", "project": "/home/oof/some-repo", "decision": "deny" },
    { "session": "abc123", "decision": "deny" } ] }
```

- Decisions are **`allow | deny` only** — smaller than `Verdict` (an `ask` at a
  door nobody watches makes no sense).
- A rule ANDs its criteria: `event` (canonical PascalCase — kebab and casing are
  normalized so `user-prompt-submit` can't silently fail to match), `handler`
  (registered name), `project` (**path-prefix** on cwd — a project contains its
  subdirectories; separator-boundary-aware so `/repo` never matches `/repo2`),
  `session` (exact id). Rules evaluate in file order.
- `Evaluate` answers **two questions from one list**: handler-*less* rules decide
  whether the whole dispatch is **worked** (first match wins, else `default`; a
  deny short-circuits); handler-*named* rules decide, per handler, which are
  **excluded** (first match per handler wins — an earlier `allow` shields a
  handler from a later `deny`). An event-level deny fires before any handler
  question is asked.
- **`default: deny` is the pause** (decision 7) — the degenerate policy that
  Noops every hook. No separate toggle; the language already says it.

## Handler-level exclusion — "as if unregistered for this dispatch"

An excluded handler contributes nothing to the dispatch: not its effect, not its
fail-mode deny. `DispatchAsync` takes an optional excluded-names set and filters
the per-dispatch runner list **before** fan-out (order-preserving, so `Merge`'s
registration order is intact; pre-fan-out, so an excluded fail-closed gate never
runs and so contributes no `Deny`). The snapshot registry and each excluded
handler's supervised `Worker` are untouched — *filtered, never restarted* (a
restart would wipe its state).

## Hot reload — poison and advance

The daemon holds one `ReloadingPolicy` for its lifetime and reads `.Current` per
dispatch: it stamps the file `(mtime,size)` and re-resolves only when it moves
(the in-place-overwrite lesson from ADR-0004 d1 — a `cat >` never bumps a parent
dir's mtime, so the stamp is the file's own). Because `Resolve` never throws, a
broken edit needs no special case: the swap is unconditional, so a bad edit
**poisons** (`.Current` becomes Malformed → deny all, loud) **and advances** the
stamp (no re-parse per dispatch). Keep-last-good is *not* an option — a broken
policy must not keep serving the old one. An unchanged file returns the same
resolution instance (zero reload work); a fix is picked up on the next hook. The
policy *path* is fixed at daemon start (env is daemon-start config, ADR-0004 d1);
the file *contents* hot-reload. The collapsed path is single-shot and just
resolves once.

## The trail

Every non-happy outcome leaves a structured line, emitted in the one shared gate
so the daemon and collapsed trails match too: `policy.skip` (event-level deny),
`policy.malformed` (unparseable file, carries the error — a `warn`),
`policy.exclude` (names the dropped handlers), and `policy.reload` (a reload
swapped the resolution). The plain proceed-with-no-exclusions path is silent. The
denied dispatch's `Trace` line rides stderr (collapsed) or the `HookResponse`
trace (daemon) — never stdout, so a policy-skipped hook is byte-indistinguishable
from an uneventful one *except by our trail* (invariant 1).

## Ground truth

| what | where |
|---|---|
| `DispatchPolicy` (model, `TryParse`, `Evaluate`/`Matches`/`ProjectContains`, `ResolvePath`) | `dotnet/captainHook/Core/DispatchPolicy.cs` |
| `PolicyDecision`, `PolicyRule`, `PolicyOutcome` | `dotnet/captainHook/Core/DispatchPolicy.cs` |
| `PolicyResolution` (Absent/Malformed/Loaded, `Resolve`, `Evaluate`), `ReloadingPolicy` | `dotnet/captainHook/Core/DispatchPolicy.cs` |
| `PolicyGate`, `HookRun.PolicyGateFor` (the shared gate), `HookRun.DeniedStdout` | `dotnet/captainHook/Core/HookRun.cs` |
| collapsed dispatch site (resolve once) | `HookRun.CollapsedAsync` (`policyPath`) |
| daemon dispatch site (per-dispatch `ReloadingPolicy.Current`) | `DaemonHost.DispatchOneAsync`, threaded from `RunAsync` |
| handler-level exclusion filter | `Dispatcher.DispatchAsync` (`excludedHandlers`) |
| event-name canonicalization shared with ingest | `Harness.Canon` (`Core/Harness.cs`) |
| policy file | `~/.captainHook/dispatch.json` (or `CAPTAINHOOK_DISPATCH_FILE`); absent ⇒ allow all |
| log events | `policy.skip`, `policy.exclude`, `policy.malformed` (warn), `policy.reload` (src `policy`) |
| pinned by | `DispatchPolicyTests.cs` (parse, matcher + path-prefix boundary, tri-state resolution, hot-reload poison/advance); `DispatcherTests.cs` (`HandlerExclusionTests`, `ExclusionSemanticsPins` — the N3 pins); `CliTests.cs` (`HookRunPolicyDenyTests` — byte-identity, malformed, exclusion, trail, default-deny pause); `DaemonHostTests.cs` (`DaemonPolicyTests` — daemon deny + collapsed no-drift cross-check) |
| decision record | `doc/adr/0006-dispatch-policy.md` (and ADR-0005 for the dialect's first speaker) |
| dispatch flow this gates | [hook-dispatch.md](hook-dispatch.md) |
