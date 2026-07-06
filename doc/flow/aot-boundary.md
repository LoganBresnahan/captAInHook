# Flow: the AOT boundary — what the code owes the native shim

captainShim is Native AOT (ADR-0004 decision 7 + 2026-07-06 amendment). That
buys the ~5ms procBoot behind every hook, and it costs a set of standing
constraints on *application* code — most of them invisible until violated,
several enforced only by tests or review. This doc is the one place they're
all named: what each accommodation is, why it exists, and what breaks it.
Touch anything in `captainHookWire/` or `captainShim/` with this page in
mind.

```
                 THE NATIVE IMAGE                          JIT (engine)
 ┌─────────────────────────────────────────┐   ┌───────────────────────────────┐
 │ captainShim ──────► captainHookWire     │   │ captainHook ──► captainHookWire│
 │  Program.cs          Frame + WireJson   │   │  dispatcher      (same source, │
 │  ShimMain            ContentIdentity    │   │  harness registry own compile) │
 │  SkewGuard           RendezvousPaths    │   │  handlers                      │
 │                      ShimClient         │   │      │                         │
 │  NOTHING ELSE:       DaemonSpawner      │   │      ▼                         │
 │  no dispatcher       WireLog seam       │   │ captainHookActors (F#)         │
 │  no F# / FSharp.Core WireJsonl renderer │   │  supervision, Log              │
 │  no reflection       argv contract      │   └───────────────────────────────┘
 └─────────────────────────────────────────┘
   everything left of this line is compiled INTO the native binary;
   a new reference here is a new resident of the native image
```

## The rules, and what enforces them

**1. The arrows are load-bearing, not style.**
`captainShim → captainHookWire ← captainHook → captainHookActors`; both
leaves reference nothing. The shim's AOT-cleanliness is precisely that
absence — the dispatcher, the reflection-based harness spec parsing, and
FSharp.Core are AOT-hostile and stay engine-side, where a warm daemon makes
AOT moot. Adding any reference to `captainShim.csproj` or
`captainHookWire.csproj` walks that dependency straight into the native
image. *Enforced by:* csproj review only — there is no analyzer for "you
added a reference"; treat the two csprojs as guarded files.

**2. The wire lib stays reflection-free, and the analyzers prove it.**
`captainHookWire` carries `IsAotCompatible=true`, so reflection-shaped code
is a build warning today instead of an ILC failure at deploy time. Keep the
build warning-free; a suppressed warning here is a deferred publish break.
*Enforced by:* the AOT/trim analyzers on every build.

**3. Wire JSON is source-generated — the context IS the protocol.**
`HookRequest`/`HookResponse` serialize only through `WireJson.Default`
(compile-time generated; no runtime reflection). Both artifacts run the same
generated code, so the frame JSON cannot fork. Adding a `[JsonSerializable]`
type to `WireJson` is a protocol change, not a convenience — treat it like
editing the frame layout. Reflection-based `JsonSerializer` calls stay
engine-side only. *Enforced by:* rule 2's analyzers (reflection serialization
in wire code warns) + `FrameTests`.

**4. Wire code logs through `WireLog`, never `Actors.Log`.**
The shim cannot see F#, so the wire lib owns a sink seam: the engine binds
it to `Actors.Log` (`Core/WireLogBridge.cs`, bound in `Program.cs` and the
tests' module initializer), the shim binds it to the JSONL appender. A
`using CaptainHook.Actors;` inside `captainHookWire/` is rule 1 broken via
the side door. New wire-side log events: use `WireLog.Info/Warn/Error` and
primitive `Data` values (string/bool/int/long/double, nested dict/sequence)
— an arbitrary object falls back to `ToString()` where the engine's
reflective serializer would expand it. *Enforced by:* rule 1 (the reference
doesn't exist to use) + `ShimMainTests`/`SpawnTests` asserting events arrive.

**5. One trail schema, two renderers, byte-pinned.**
`WireJsonl.Render` (imperative `Utf8JsonWriter`) must produce bytes
identical to F# `LogEvent.ToJson()` — key order, absent-means-omit,
`Math.Round(durMs, 3)`, default-encoder escaping, the exact `ts` format
string, and the mirrored default log path. Changing the trail schema means
moving `Logging.fs` and `WireJsonl.cs` in the SAME commit. *Enforced by:*
the 17 golden cross-emitter tests in `WireJsonlTests` — schema drift is a
red build, by design.

**6. Content identity ignores the shim, and that is correct.**
The rendezvous hash covers the deploy dir's managed DLLs only; a native
image has no MVID and `ContentIdentity.Compute` skips it. Sound because:
daemon behavior = engine assemblies (hashed); the shared contract = exactly
`captainHookWire.dll` (hashed); shim-local changes never require a fresh
daemon. Never "fix" this by hashing the shim binary (daemon churn for
nothing) and never introduce identity negotiation — both sides must compute
the same answer from the filesystem alone. *Enforced by:*
`RendezvousTests`; the reasoning lives in ADR-0004 d7's amendment.

**7. The skew guard leans on AOT preserving `ModuleVersionId`.**
`SkewGuard` compares the wire MVID compiled into the shim
(`typeof(Frame).Module.ModuleVersionId` — probed to survive Native AOT,
identical to the IL value) against the deploy dir's `captainHookWire.dll`.
Mismatch or missing DLL ⇒ never touch the socket, delegate, `shim.wireSkew`
in the trail. If a future SDK breaks MVID-under-AOT, the guard fails toward
"missing/unreadable ⇒ delegate" — safe but noisy; platform.md carries the
fact. *Enforced by:* `ShimMainTests` (both skew directions, live-socket
never-accepted assert).

**8. Build determinism is part of the rendezvous.**
Identity = f(source) requires the compiled bytes to be too: every shipped
project sets `EnableSourceControlManagerQueries=false` (the SDK otherwise
bakes git HEAD into the compilation — every commit rolled every MVID), and
the F# lib sets `ProduceReferenceAssembly=false` (fsc ref assemblies are
nondeterministic and poisoned the engine's hash through the reference). A
new shipped project copies both habits and re-runs the verdict probe: clean
publish ×2 at one HEAD + ×1 behind an empty commit ⇒ identical MVIDs.
*Enforced by:* nothing automatic — the probe is manual; CLAUDE.md carries
the tripwire, platform.md the facts.

**9. The shim runs invariant-globalization; wire code stays culture-blind.**
`InvariantGlobalization=true` drops ICU from the image. Wire code must use
ordinal comparisons and culture-stable formatting only (it already does —
`StringComparer.Ordinal`, fixed `ts` format). A culture-sensitive API in the
wire lib behaves differently in the shim than in the engine. *Enforced by:*
rule 5's golden tests catch formatting drift on the log path; elsewhere,
review.

**10. Co-location is the deployment contract.**
One directory holds `captainShim` (the hook command), `captainHook` (the
engine, found by literal name next to the shim), and the DLLs the identity
hashes. The two artifacts move together — `/deploy` stages and swaps the
whole dir; a partial copy is exactly the skew rule 7 guards. Never point
the shim at an engine elsewhere; never deploy one artifact. *Enforced by:*
the `/deploy` skill (the only sanctioned path) + rule 7 at runtime.

**11. Delegation is the shim's only fallback, and at-most-once survives it.**
The shim carries no dispatcher: `NotDelivered` ⇒ spawn the engine as daemon
for the NEXT hook, exec `captainHook … --no-daemon` for THIS one, relay
stdout bytes / stderr / exit verbatim (the sacred channel crosses the pipe
byte-identically). `FailedAfterDelivery` never delegates — the daemon may
already be running non-idempotent effects. Engine-only modes are refused
loudly. *Enforced by:* `ShimMainTests` (verbatim relay, at-most-once,
refusal) + `AtMostOnceTests` engine-side.

**12. The shim's native form is tested at publish, not in the suite.**
`ShimMain` takes injected streams (the `HookRun` seam) so the artifact's
whole program runs in IL form under xunit; the AOT publish only changes the
compiler. The native binary itself is exercised by `/deploy`'s acceptance
test (cold delegate + warm answer + zero skew in the live trail). Don't add
an AOT publish to the test suite — it's ~40s of clang per run for coverage
the IL tests already give. *Enforced by:* convention + the `/deploy` skill.

## Ground truth

| what | where |
|---|---|
| the two guarded csprojs (rule 1) | `dotnet/captainShim/captainShim.csproj`, `dotnet/captainHookWire/captainHookWire.csproj` |
| AOT analyzers + determinism opt-outs (rules 2, 8) | csproj properties + comments in all three shipped projects |
| wire JSON context (rule 3) | `dotnet/captainHookWire/Frame.cs` (`WireJson`) |
| log seam + bridge (rule 4) | `dotnet/captainHookWire/WireLog.cs`, `dotnet/captainHook/Core/WireLogBridge.cs` |
| schema twin renderers (rule 5) | `dotnet/captainHookWire/WireJsonl.cs` ≡ `dotnet/captainHookActors/Logging.fs` |
| identity + skew guard (rules 6–7) | `dotnet/captainHookWire/Rendezvous.cs`, `dotnet/captainShim/SkewGuard.cs` |
| shim program + delegation (rules 11–12) | `dotnet/captainShim/ShimMain.cs`, `Program.cs` |
| pinned by | `WireJsonlTests.cs` (golden schema), `ShimMainTests.cs` (relay/skew/at-most-once), `FrameTests.cs`, `RendezvousTests.cs` |
| decisions behind all of it | `doc/adr/0004-daemon-topology.md` decision 7 + 2026-07-06 amendment |
| environment facts | `doc/platform.md` §§ Native AOT, Build determinism |
