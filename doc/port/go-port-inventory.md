# Go port inventory — subsystem by subsystem, with measured evidence

Companion to
[ADR-0013](../adr/0013-tri-platform-and-the-language-question.md). The ADR records
the *decision*; this file records the *survey* it rests on — what each subsystem
does, what it maps to in Go, what gets deleted, and what gets harder — plus, in
§ Part 4, the complete Go port plan should the ADR's recommendation be overruled.

**Lane note:** this is neither an ADR (no decision) nor a flow doc (nothing is
built). It is a pre-implementation survey and a contingency plan. It should be
deleted, or promoted into flow docs, once the port lands or is abandoned for good.
Do not cite Part 1–3 as a plan — the plan for the *accepted* path is ADR-0013
§ Implementation plan; Part 4 is the plan for the *rejected* one.

**Where this contradicts the ADR, the ADR wins** — several first-pass findings
here were overturned by later probes, and the corrections are marked ⚠ inline
rather than silently edited, because the *pattern* of what turned out wrong is
itself evidence about how much of a port survives first contact.

Every number below was **measured on 2026-07-27** on the maintainer's WSL2 box
(Go 1.26.2, .NET 10) against the **live deployed artifacts** in
`~/.captainHook/bin/`, not cited from documentation.

---

## Part 1 — Probes

### P1 · Cold start: Native AOT ties Go **on boot**; the win is deleted *work*

40 runs each, median (min in parens), `/bin/true` floor = 412 µs:

| what | measurement |
|---|---|
| **PROCESS BOOT** — `captainShim --daemon` (Native AOT, 3.7 MB, refuse+exit) | **1919 µs** (1529) |
| **PROCESS BOOT** — Go engine-shaped binary, noop (9.8 MB) | **1911 µs** (1560) |
| END-TO-END — `captainShim hook … --harness bogus` (skew guard + rendezvous + JSONL) | **5260 µs** (4620) |
| ENGINE — `captainHook --nonsense` (.NET single-file, 71 MB) | **174 145 µs** (165 616) |

Read these two rows together, because they say different things and a first pass
at this probe conflated them:

1. **On process boot Go and Native AOT are indistinguishable** (1911 vs 1919 µs).
   Go buys *nothing* there — ADR-0004 d7 already bought it.
2. **The shim's end-to-end hook path costs 3.3 ms more than its own boot**, and
   that is *work*: `SkewGuard` reading `captainHookWire.dll`'s MVID through
   `PEReader`, rendezvous resolution, the JSONL append. A meaningful slice of it —
   the skew guard — exists *only* because there are two artifacts. So Go's
   end-to-end advantage, where it exists, comes from **deleting work, not from
   booting faster.**
3. **174 ms is why the engine can never itself be the hook command**, which is the
   entire reason a second artifact exists.

### P2 · One binary is viable — the engine's import surface is free at boot

The one-binary claim had to be tested against a realistic engine, not a stub.
A 9.9 MB binary linking `net/http`, `encoding/json`, `os/exec`,
`debug/{elf,macho,pe}`, `crypto/sha256`, `crypto/subtle`, `text/template`,
`regexp`, `log/slog`, `os/signal`, plus an `embed.FS` carrying 460 KB of UI
assets, constructing a 32-route mux at startup:

| binary | boot | size |
|---|---|---|
| **engine-shaped Go binary** | **1.93 ms** | 9.9 MB |
| trivial Go stub | 1.73 ms | 4.1 MB |
| `captainShim` (Native AOT) | 1.95 ms | 3.7 MB |

**Linking the whole engine costs 0.2 ms.** One Go binary can be both hook command
and engine — which is ADR-0004 d1's original design, abandoned in d7 only because
.NET could not have both fast boot and the reflection-heavy engine in one artifact.

⚠ **Do not read the 174 ms as .NET process boot.** The engine has no work-free
code path — an unknown verb falls through to the full collapsed pipeline — so
that figure is *full collapsed dispatch*. The AOT control experiment
(ADR-0013 Finding 5) could not isolate the engine's boot either, because the AOT
binary aborts before completing that path. What is measured is the honest
comparison for "can the engine be the hook command", since the collapsed path *is*
what the engine does as a hook command — but it is not a boot number.

### P3 · Cross-compile: real, but much narrower than it first appears

Go: same source, `_unix.go`/`_windows.go` split, `CGO_ENABLED=0` — linux, darwin
and windows × amd64/arm64 all built here, 0.2–4.3 s each, 2.4–3.2 MB. No clang,
no per-RID publish, no Mac or Windows host.

**But the .NET side was not actually blocked.** Cross-publishing the *real*
projects from this same Linux box:

| artifact | win-x64 | osx-arm64 |
|---|---|---|
| `captainHook` engine (self-contained, single-file) | **OK — 71 MB PE32+** | **OK — 77 MB Mach-O arm64** |
| `captainShim` (`PublishAot`) | FAIL — *"Cross-OS native compilation is not supported"* | FAIL — linker |

⚠ **A first pass at this survey claimed .NET "cannot produce a macOS image from
Linux at all." That is wrong** — it is true only of `PublishAot`, i.e. of the
**254-LOC shim**. The engine already ships to all three OSes from one Linux host
today.

So Go's cross-compile win buys artifact **size** (~10 MB vs 71–77 MB) and removes
one build host for one small binary. Real, but not the categorical advantage it
looks like — and ADR-0012 N1 already accepted the size trade.

### P4 · The platform gap is compiler-enumerable

Before the build-tag split, `GOOS=windows go build` **failed to compile**:

```
unknown field Setpgid in struct literal of type syscall.SysProcAttr
undefined: syscall.Getpgid
undefined: syscall.Kill
```

Contrast `ProcessStartInfo.CreateNewProcessGroup`, which compiles on every .NET
target and throws `PlatformNotSupportedException` at *runtime* on Unix
(platform.md § Process groups). In Go the unsupported surface enumerates itself
per-GOOS and cannot silently rot. platform.md's per-OS table exists because in
.NET it can.

### P5 · `SysProcAttr{Setpgid: true}` — the seam .NET does not expose

```
spawn.pid=701414 pgid=701414 pgid_eq_pid=true
groupkill: TERM to -pgid took a backgrounded grandchild
```

pgid == pid at fork, no `setsid(1)`. Deletes `ProcessGroup.Probe()`,
`SetsidPath`, `ExecHandler.Pgrouped`, the `bool pgroup` parameter on four
functions and its 17 call sites, the `Process.Kill(entireProcessTree)` fallback,
the per-spawn `pgroup=false` trail field, and **22 `if (SetsidPath is null)
return;` guards in tests** that currently silently no-op rather than skip.

It also **ends the macOS degradation**: stock macOS ships no `setsid(1)`, so
every exec spawn there runs `pgroup=false` today with the reparented-grandchild
hole open (platform.md § Process groups), and ADR-0012 accepted that as the
macOS baseline. Go makes Linux and macOS one implementation with no degrade path.

### P6 · Wire format ports unchanged

Go's `json.Marshal` of `[]byte` emits base64 — `{"stdin":"aGk="}` — the same
convention `System.Text.Json` uses for `byte[]`. A 4-byte LE length-prefixed
frame round-tripped over `net.Listen("unix")` first try. **A Go shim can speak
the existing C# daemon's protocol**, which makes the wire a genuine
cross-language seam.

### P7 · Content identity — the obvious substrates FAIL; one binary rescues it

No MVID in Go; this is the single hardest port decision. Measured on the 9.8 MB
binary (the shim recomputes identity on **every hook**, so hot-path cost is the
whole question):

| substrate | cross-format? | hot-path cost |
|---|---|---|
| `.note.go.buildid` **section** via `debug/elf` | **NO — ELF only** | 0.077 ms |
| **Go build-ID `contentID`, combined reader** | **YES — all three** | **0.012–0.054 ms** |
| SHA-256 of the whole binary | yes | **6.97 ms** |
| `debug/buildinfo.ReadFile` | yes | 0.02 ms ELF / 1.30 ms Mach-O / **5.43 ms PE** |
| link-time `-ldflags -X` stamp | yes | 0 — a compiled-in constant |

Two dead ends and the answer:

1. **`.note.go.buildid` does not exist off ELF.** Probed the section tables of
   real darwin/arm64 and windows/amd64 builds: Mach-O carries
   `__DATA/__go_buildinfo` (a different blob); PE has no such section at all.
   A `debug/elf`→`debug/macho`→`debug/pe` reader ships a working Linux identity
   and a **broken macOS/Windows one**.
2. **Hashing the binary is hot-path-hostile** — 6.97 ms against a 1.9 ms process
   budget, on every hook. `debug/buildinfo` is no better where it now matters
   (5.4 ms on PE).
3. **The answer is the ~40-line combined reader** `cmd/internal/buildid` itself
   uses: try the ELF note, else scan the first 32 KiB for the
   `\xff Go build ID: "…"\n \xff` sentinel. Implemented and run here against real
   cross-built binaries — **contentID recovered on all three formats at
   0.012–0.054 ms**, i.e. cost parity with today's 0.05 ms MVID read and ~100×
   cheaper than SHA-256.

⚠ **A first pass at this survey concluded "the build-ID note fails off ELF, so use
a link-time `-X` stamp." The premise is right, the conclusion was too strong** —
the *section* is ELF-only, but the *build ID* is recoverable everywhere.

**And the contentID is semantically better than either alternative.** Measured
across build legs: a **comment-only edit leaves the contentID IDENTICAL** while
the file bytes change; a real behavioral edit changes it. So it tracks compiled
*output*, not build inputs — which makes ADR-0004 d3's stated property ("a
comment-only rebuild keeps its socket") **literally true in Go**, where P12 below
shows .NET does not actually deliver it.

This does not remove the coupling to one-binary, it softens it: with two artifacts
the identity is still *computable* cheaply, but the shim-vs-directory comparison
that `SkewGuard` performs only disappears when there is one file to compare.

### P8 · JSON strictness — `encoding/json` v1 cannot express the house parser

The parsers are strict never-guess (`HarnessSpec.TryParse`,
`DispatchPolicy.TryParse`, `ExecHandlersFile`, `ExecWire`); ADR-0006's
adversarial verify added duplicate-field rejection *after a real silent-grant
bug*. The mechanism is `JsonDocument`/`JsonElement`, which **preserves**
duplicate keys (`EnumerateObject` yields both) and does case-**sensitive**
lookup. Measured Go v1:

| hazard | v1 | v2 (`GOEXPERIMENT=jsonv2`) |
|---|---|---|
| `"EVENT"` for field `event` | **silently binds** | case-sensitive — does not bind |
| …+ `DisallowUnknownFields()` | **still binds** | — |
| duplicate key | **last wins, no error** | `duplicate object member name` |
| unknown field + strict opt | errors | errors via `RejectUnknownMembers` |

`encoding/json/v2` fixes exactly the two that matter but is **`GOEXPERIMENT`-gated
in Go 1.26** — verified in GOROOT: every file in `src/encoding/json/v2/` carries
`//go:build goexperiment.jsonv2`, and its own `doc.go` says it "only exists when
building with the GOEXPERIMENT=jsonv2 environment variable set." Shipping a
product on a non-default GOEXPERIMENT is not acceptable; and enabling it does not
merely *add* v2, it reimplements v1 on top of it. Not half-adoptable.

**But the gap is smaller than it first looks, and it is closeable stdlib-only.**
Measured:

| the house parser needs | v1 gives it? |
|---|---|
| reject non-integral number into an int field | **YES, free** — `cannot unmarshal number 1.0 into … type int` |
| reject trailing content after one top-level value | **YES, free** — but only via `Unmarshal`; `Decoder.Decode` silently stops at the first value. *Use `Unmarshal` wherever the C# used `JsonDocument.Parse`.* |
| **see** duplicate keys | not via `Unmarshal` (struct keeps last; map collapses) — **but `Decoder.Token()` yields the full stream** `[{ event a event b decision deny }]`, both occurrences visible |
| case-sensitive key lookup | no — needs the token walk |
| collect *every* violation in one pass | no — `DisallowUnknownFields` reports only the **first** unknown field |

⇒ The answer is **one leaf package** (`internal/jsonstrict`, ~120–150 LOC + its
own adversarial tests) exposing an ordered, duplicate-preserving object walker
built on `Decoder.Token()`, consumed by all four parsers — not four hand-rolled
walks, and not a dependency.

Separately: Go's `encoding/json` **HTML-escapes `<>&`** unless
`SetEscapeHTML(false)`, **sorts map keys alphabetically** (the trail's `data`
object preserves insertion order today), and formats floats differently from
`Math.Round(d,3)` + `Utf8JsonWriter`. The JSONL trail's exact bytes change — a
one-time deliberate schema break, currently pinned by golden tests.

Also: Go's `encoding/json` **HTML-escapes `<>&`** unless `SetEscapeHTML(false)`,
**sorts map keys alphabetically** (the trail's `data` object preserves insertion
order today), and formats floats differently from `Math.Round(d,3)` +
`Utf8JsonWriter`. The JSONL trail's exact bytes change — a one-time deliberate
schema break, pinned today by golden tests.

### P9 · Socket permissions — a regression Go introduces

```
uds.default_mode = -rwxr-xr-x   (umask-derived)
uds.after_chmod  = -rw-------
```

`DaemonRendezvous.BindWhenWarm` deliberately chmods 0600 **between Bind and
Listen** so there is no window where a foreign user can reach an accepting
socket. Go's `net.Listen("unix")` binds *and* listens in one call — that window
cannot be closed through it (golang/go#11822: `Listen`/`ListenUnix` set no modes
or ACLs).

**Downgraded on measurement:** `os.Chmod` *after* `Listen` succeeds
(`-rwxr-xr-x` → `-rw-------`), so the fix is one line, not raw syscall plumbing.
The residual window is real but sits **inside the 0700 runtime directory**, which
is the same argument ADR-0004 d3 already makes for every other file there. Also
verified: `SetUnlinkOnClose(false)` restores today's unlink-at-process-exit shape
(Go's default would move the unlink to drain start).

### P10 · The supervision layer ports to ~40 lines — and the race IS a `select`

Ported `ActorRef`'s atomically-swapped `Instance` + the item-17a classified ask
(`Supervision.fs:23-104` — the most recently hardened code in the repo, born of
a live incident) to Go: `atomic.Pointer[Instance]`, a `chan struct{}` closed on
supersession, one `select` for the three-way race. Three tests green in 0.002 s
under **`testing/synctest`** (stdlib virtual clock, stable Go 1.25+), with a
20-second budget elapsing instantly and **no project-owned `FakeClock` /
`PollUntilAsync` infrastructure**.

Deleted by construction: `observeReplyFault` (no unobserved-exception concept),
the `windowMs + 60_000` mailbox backstop (a size-1 buffered reply channel means
an abandoned worker's send never blocks), and the continuation-inlining hazard
`RunContinuationsAsynchronously` exists to dodge (a channel receive always
resumes on the receiver's own goroutine).

### P11 · …and that port was SILENTLY WRONG. This is the best evidence for N1.

Go's `select` chooses **uniformly at random** among ready cases — it has no
priority. Probed: both ready ⇒ 5080/10000 chose the deadline. The classified ask
depends on a reply beating the window (`Worker.fs:169,183` re-checks
`replyTask.IsCompleted` for exactly this reason).

The 40-line port above carried that defect **and all three tests passed.** A
test that actually races a reply against the deadline:

```
--- FAIL: TestReplyRacingTheDeadline_MustNotBeWedged
    reply-racing-deadline classified Wedged 400/400 times
```

400/400 — deterministic, not 50%. A handler that answers exactly on time is
classified `Wedged`; **wedges count toward escalation** (`Supervision.fs:253-262`),
so a healthy worker is restarted and, repeatedly, escalated to permanently DEAD.
Fix: three lines (non-blocking reply re-check inside the timer case ⇒ 0/10000).

A second latent crash in the same 40 lines: `close()` of an already-closed
channel **panics**, where `TrySetResult` is idempotent — and double-abort is
reachable (escalation `MarkDead`s but leaves the entry in `children` forever,
`Supervision.fs:181,347-348`; a later hot-reload `Remove` aborts it again). A
naive transliteration crashes the daemon.

**This is the most important datum in the survey.** It is not an argument against
the port — it is the *measurement* of what ADR-0013's N1 costs: faithful-looking
Go can be silently wrong in ways targeted tests do not catch.

### P12 · Reproducible builds hold; and a doc-drift find

`go build -trimpath -buildvcs=false` twice (cache purged between) ⇒ byte-identical.
`-buildvcs=false` is the exact analogue of `EnableSourceControlManagerQueries=false`
— Go stamps VCS by default too, and has the same one-flag fix.

Incidental find, **true regardless of the Go decision**: ADR-0004 d3 and
platform.md § Build determinism claim a comment-only rebuild emits identical
bytes and keeps its warm daemon. Measured false in both languages — clean rebuild
⇒ identical, touch-only ⇒ identical, **comment-only ⇒ different bytes**. The
"identity differs ⟺ behavior may differ" claim is overstated in that one
direction as written. Worth a doc fix either way.

Curiosity for the deferred branch: Go's build-ID `contentID` **is**
comment-insensitive (measured — identical across a comment-only edit, changed on a
real one), so the property ADR-0004 d3 claims would be literally true there.

### P13 · The AOT control experiment — the .NET escape hatch, measured

The cheapest experiment that could moot the whole Go question: Native-AOT the C#
engine. If it works, one-binary and most of the deletion ledger arrive without a
rewrite. It was run against the real project. Full narrative in
[ADR-0013 Finding 5](../adr/0013-tri-platform-and-the-language-question.md);
the measurements:

| step | result |
|---|---|
| `dotnet publish -p:PublishAot=true -r linux-x64` | **compiles and links** → **9.2 MB** native binary (Go equivalent: 9.8 MB) |
| first hook dispatch | **crashes** — `Reflection-based serialization has been disabled`, at `HookRun.CollapsedAsync` |
| reflection surface | **compiler-enumerated**: ~16 `JsonSerializer` sites across 10 files |
| **`ContentIdentity.Compute`** | **throws** — *"no managed assemblies found … cannot compute a content identity"* |

The third row is mechanical (source-gen contexts; four genuinely dynamic sites
need restructuring — the trail's `Dictionary<string,object>`, `ApiJson`'s
polymorphic writer, the harness adapters' `object`, and `JsonSchemaExporter`).

**The fourth row is architectural and is the real finding.** A native image has no
MVID (platform.md § Native AOT states the fact; here it is observed as a failure),
and ADR-0004 d3's whole identity scheme reads managed assemblies off disk. So
**the .NET route to one binary also needs a new identity substrate** — the same
design work the Go port needs. The AOT escape hatch narrows the gap between the
two options; it does not close it.

---

## Part 2 — Subsystem inventory

Difficulty is for the *port*, not the original: `mechanical` (transliteration),
`moderate` (idiom change), `hard` (correctness argument must be rebuilt),
`redesign` (no equivalent; new design required).

### 2.1 · Engine core — **hard** (~2,560 LOC)

`Core/Model.cs`, `Dispatcher.cs`, `HookRun.cs`, `Harness.cs`, `DispatchPolicy.cs`

| gains | costs |
|---|---|
| `embed.FS` beats `GetManifestResourceStream` outright — no mangled resource names, no `.Contains(".harnesses.")` substring filter (`Harness.cs:171`), and a missing harness file is a **compile error** instead of an empty registry | `encoding/json` **cannot see duplicate keys at all**; `JsonElement` preserves them, which is how `DispatchPolicy.cs:84-91` and `ExecHandlersFile.cs:123-130` detect them today |
| `context.Context` is the language's own idiom, so "honor `ctx.Ct` on every await" stops being a CLAUDE.md rule policed by review and becomes what the stdlib signature and `go vet` push you toward | `DisallowUnknownFields` is not a substitute — it works only on struct targets and reports only the **first** unknown field, while every parser here collects **every** violation in one pass |
| `time.Time` carries a monotonic reading, so invariant 2 stops being hand-enforced; residual risk narrows to times crossing a serialization boundary | No DUs, no exhaustiveness: a new `Effect` kind must be handled in four type-switch sites plus a six-way `AskStatus` switch, silently. *Mitigating fact:* today's switches already end in `_ =>` catch-alls, so C# is not checking them either |
| `atomic.Pointer[T]` states the "immutable snapshot swapped whole, read lock-free" contract (`Dispatcher.cs:159-171`) more explicitly than `volatile Dictionary` + a paragraph | No records — value equality, `with`, and positional deconstruction all hand-written |
| `io.Reader`/`io.Writer` injection is Go's default rather than a designed-in test seam | Send-on-closed-channel **panics**, so the deliberately-reached `side.dropped` path (`Dispatcher.cs:623-633`) needs a mutex-guarded queue with an explicit closed flag |

Hardest units: the **teardown seam** (live-instance tracking, fire-and-forget-but-
awaitable disposal, the drain child phase) and **`DispatchPolicy`'s strict parser**.

### 2.2 · Daemon + wire + shim — **hard** (~2,050 LOC)

Biggest deletions in the codebase, and the biggest single unsolved question.

**Deleted:** `DaemonSpawner`'s `/bin/sh -c 'exec … &'` hop and its `sh.WaitForExit`
zombie dance (`SysProcAttr{Setsid:true}` + `os.DevNull` + `Process.Release()`, ~15
lines); the dotnet-muxer guard and its platform fact (`os.Executable()` is always
the binary); `ShimMain`'s three-task pipe-deadlock avoidance (`exec.Cmd` spawns its
own copiers); the wire-skew guard, `KeepAppAssembliesLooseForIdentity`, the
four-loose-DLL deploy postcondition, and most of `aot-boundary.md`'s 12 rules;
`WireJsonlTests`' 17 golden cross-emitter tests (155 lines) — they exist *only*
because C# and F# render the trail independently.

**Also fixed incidentally:** `os.OpenFile(p, O_APPEND|O_CREATE|O_WRONLY, 0600)`
closes the trail-clobbering bug platform.md § Runtime directories documents against
`File.AppendAllText` (which does **not** open `O_APPEND`).

**Better than ADR-0012 assumed:** Go's `net` supports AF_UNIX **on Windows**
(`net/unixsock_posix.go` build tag is `unix || js || wasip1 || windows`). The
transport — the part platform.md treats as the scary bit — is portable out of the
box. Only the lock, the stop signal, and the ACL story fork.

**Costs:** no sum types, so `ForwardOutcome`'s three sealed cases stop being a
compiler-checked at-most-once property and `AtMostOnceTests` becomes load-bearing
for what the compiler used to prove. Per-assembly identity granularity is gone —
today a shim-only change deliberately does *not* roll the socket
(`aot-boundary` rule 6); one binary rolls on any change. `net.UnixListener`
unlinks the socket on `Close()` by default, moving the unlink from process exit to
**drain start** — arguably better (new shims fall back sooner), but a deliberate
behavior change; `SetUnlinkOnClose(false)` restores today's shape.

### 2.3 · F# actor / supervision runtime — **moderate** (~880 LOC)

The subsystem most feared, and the one that ports **best**. See P10/P11.

Beyond those: `HotPath.fs` (76 lines) and ADR-0001's entire two-flavor hybrid
rationale exist because `MailboxProcessor` is unbounded and `Channels` is not —
**a Go channel is both**, so the flow doc's two-flavor table becomes one row.
`ChildEntry`'s four-closure record is exactly what a Go interface is for.

Real costs, in order of severity:

1. **Fault isolation gets weaker — the sharpest loss in the whole port.** An
   unrecovered panic in *any* goroutine kills the process, and nothing outside
   that goroutine can prevent it. .NET swallows unobserved `Task` exceptions and
   a `MailboxProcessor` body's throw dies silently on `.Error`. So the engine
   must `defer recover()` at every goroutine it creates and gets **zero**
   protection against goroutines that in-process handler code creates.
   DESIGN.md's "a crashing handler must not take down the dispatch" rail is
   strictly harder to guarantee. Mitigated — not removed — by ADR-0010 making
   payloads user processes.
2. `select` has no priority (P11).
3. `close()` is not idempotent (P11).
4. Go forbids type parameters on **methods**, so `Supervisor.Spawn<'Msg>`,
   `ActorRef.Ask<'Reply>`, and `AskTracked<'Reply>` all become free functions —
   a shape change rippling through every call site. No optional parameters either.
5. No unbounded channel, so `Post` never blocks is not free. Nearly moot in
   practice: production has no `Post` caller.

### 2.4 · Exec handlers / payload lifecycle — **hard** (~2,550 LOC)

Where Go's advantage is largest *and* where it most clearly does not extend to
Windows.

**Gains:** P5's setpgid seam. `cmd.WaitDelay` **is** `ExecHandler`'s `PipeGrace`
in stdlib — probed: with a `sleep 5 & exit 0` payload, `WaitDelay=0` makes
`Wait()` ride the grandchild for 5 s (the exact wedge `ExecHandler.cs:189-196`
dodges); `WaitDelay=250ms` returns in 250 ms with the buffered answer intact.
`cmd.Start()` fails *before* the fork on a missing binary, restoring the clean
`BinaryNotFound` error the setsid prefix destroyed (`ExecHandler.cs:493-497`).
`*os.File` read deadlines genuinely interrupt a blocked child-pipe read, deleting
the `Observe(task)` abandoned-read idiom.

**Costs:**

- **macOS process start time: Go's stdlib gives nothing on darwin** — no
  `Kinfo_proc`, `Extern_proc`, or `SysctlRaw` in `syscall` for GOOS=darwin. .NET's
  BCL gives it free and stable (platform.md § Native AOT). So the pid-reuse
  identity guard is **SAME on Linux, WORSE on macOS, a wash on Windows**. This
  directly contradicts a naive "Go is better for portability" claim.
- **The read-deadline win is Unix-only.** `os.Pipe` on Windows is not pollable
  (`newFile(..., kindPipe, pollable=false)`), so `setDeadlineImpl` returns
  `ErrNoDeadline` — back to the abandoned-goroutine idiom, i.e. parity with .NET.
- `cmd.Wait()` is once-only, but three independent watchers currently await the
  same `Process`; needs a `sync.Once` + broadcast channel.
- **Windows kill discipline is net-new design.** Job objects
  (`CreateJobObject` + `AssignProcessToJobObject` +
  `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`) are the right primitive —
  `CREATE_NEW_PROCESS_GROUP` only affects Ctrl+C/Ctrl+Break delivery. There is no
  SIGTERM, so the graceful half needs an in-band protocol.

### 2.5 · Management API + GUI — **moderate** (~2,320 LOC)

Go's `net/http` deletes platform.md's entire `HttpListener` scar-tissue section:

- **`srv.Close()` actually closes connections.** The headline quirk — `Stop()`/
  `Close()`/`Abort()` block indefinitely behind a write parked on a zero-window
  client, and terminate nothing for healthy ones — is fully gone. That removes the
  `Task.Run(...).Wait(2s)` teardown hack in both `ApiHost.Stop` and `Dispose` plus
  the two paragraphs explaining why an unkillable daemon was otherwise possible.
- **`http.ResponseController.SetWriteDeadline`** prevents the wedge at source
  rather than working around it. `HttpListenerResponse` has no counterpart.
- Goroutine-per-connection deletes `AcceptLoopAsync`, its "NEVER await the handler
  in this loop" invariant, the `Task.Run` hand-off, and the `api.loopCrashed` guard.
- `r.Context().Done()` fires on client disconnect, so the `openStreams` counter
  that governs daemon *lifetime* gets a direct signal instead of a heartbeat-inferred one.
- `select` has no abandoned-read hazard, so `TrailTail`'s "NEVER `WhenAny` over
  `ReadAsync`" warning has no Go equivalent; drop-oldest becomes three lines.

Costs: **`JsonSchemaExporter` has no stdlib analogue** — the DTO→schema→TS
pipeline is a *redesign*, and ADR-0008 d6's "BCL-native, adds no dependency"
justification evaporates. The listener's free Host prefix-match defense is gone
(a lost layer, not a lost control — `ApiAuthGate` was written portable for exactly
this reason). Go does not pre-normalize dot-segments, so ADR-0008's
`/ui/../api/v1/status` reasoning stops being true without explicit `path.Clean`.
`mime.TypeByExtension` is machine-dependent — keep the closed map.

### 2.6 · Test suite — **hard** (11,825 LOC, 521 test methods)

The dominant cost, and the reason ADR-0013 gates on a spike.

**Gains:** `go test -race` turns `SoakTests`' *inferred* lost-update property (a
bare `++` whose corruption is detected only via a 1..200 permutation assert) into
a directly checked one, and would police the whole actor layer for free — there
is no .NET equivalent in this suite. `go test -timeout` panics with every
goroutine's stack. `t.Setenv`/`t.TempDir` replace five throwaway-dir classes.
`net.Listen("tcp","127.0.0.1:0")` deletes `TestInfra`'s admitted-TOCTOU
`FreeTcpPort`. Build time 3.8 s cold / 0.20 s warm vs ~40 s of clang per AOT
publish — so `aot-boundary` rule 12 ("never put an AOT publish in the suite")
dissolves and the *real shipped binary* becomes testable.

**Costs:**

- **~2,280 LOC of child-process fixtures are POSIX shell**, including the resident
  lock-step protocol in `sh` parameter expansion and a fake engine as a
  `#!/bin/sh` script. Windows means replacing all of it with a compiled
  cross-platform helper binary. *This cost is Windows's, not Go's — but the port
  is when you pay it.*
- `LockBindTests` (8 facts) encodes POSIX flock's open-file-description semantics
  and the never-unlink-a-held-lock rule. No Windows analogue: a second rendezvous
  **design** plus its own tests, not a translation.
- `KillDisciplineTests` (12 facts) assumes process groups, SIGTERM, a grace
  window, and zombie state. Windows has job objects and no SIGTERM — a parallel
  suite asserting *different properties*.
- 20 `UnixFileMode` assertions across 4 files express the 0600/0700 trust root.
  On Windows that is ACLs — a different claim needing different tests, or an
  explicitly documented weaker guarantee.
- No stdlib assertion library: ~600 assertions get more verbose, worst on the
  `Effect` tiers.
- **Go cannot kill a goroutine**, so the abandon-and-respawn design strands work
  permanently; the port needs goroutine-leak accounting and an explicit
  bounded-leak policy that .NET let stay implicit.

---

## Part 3 — The deletion ledger

What a Go port removes, as opposed to re-implements. This is ADR-0013's actual
case; everything else is a wash or a cost.

| deleted | why it existed | lines |
|---|---|---|
| `captainShim/` project + `SkewGuard` | .NET engine cold start (167 ms) forced a second artifact | ~254 |
| the wire-skew guard *mechanism* | two artifacts can deploy partially | — |
| `ContentIdentity` MVID/`PEReader` machinery | needed a per-build identity to name the socket | ~50 |
| `KeepAppAssembliesLooseForIdentity` + loose-DLL publish shape | single-file bundles hide the DLLs identity reads | csproj |
| `ShimMain.DelegateToEngineAsync` + pipe-deadlock dance | shim carries no dispatcher | ~70 |
| `doc/flow/aot-boundary.md` — **12 standing rules** | one artifact must stay AOT-clean | 1 doc |
| `DaemonSpawner`'s `/bin/sh -c 'exec … &'` hop + zombie wait | .NET cannot setsid or redirect to /dev/null | ~30 |
| the dotnet-muxer guard + its platform fact | `ProcessPath` is the muxer under `dotnet foo.dll` | ~14 |
| `ProcessGroup.Probe` / `SetsidPath` / `Pgrouped` / `pgroup=false` | .NET exposes no setpgid seam | ~40 + 17 call sites |
| the tree-walk degrade path (**= today's macOS baseline**) | `setsid(1)` may be absent | — |
| `WireJsonlTests`' 17 golden cross-emitter tests | C# and F# render the trail independently | 155 |
| `HotPath.fs` + the two-flavor hybrid rationale | `MailboxProcessor` unbounded vs `Channels` bounded | 76 |
| `observeReplyFault`, the `windowMs+60_000` backstop, `RunContinuationsAsynchronously` | .NET async bookkeeping | ~25 |
| `ApiHost` bounded-background teardown + `AcceptLoopAsync` + `api.loopCrashed` | managed `HttpListener` on Unix | ~60 |
| `TestInfra.FreeTcpPort` (admitted TOCTOU) | no listener-handoff idiom | ~10 |
| 22 `if (SetsidPath is null) return;` test guards | silent no-ops, not skips | 22 |
| the C#↔F# boundary rules (ADR-0001/0002) | two assemblies, two languages | doc |

Against which: **the 521-test suite must be re-earned**, ~2,280 LOC of POSIX-shell
fixtures must become a cross-platform helper binary, four strict parsers need
hand-rolled token walks, and three new Windows subsystems (rendezvous, kill
discipline, trust root) must be *designed*, not ported.

---

## Part 4 — The Go port plan, if the recommendation is overruled

[ADR-0013](../adr/0013-tri-platform-and-the-language-question.md) recommends
against the Go rewrite *on the stated rationale*. That is a recommendation, not a
veto — Finding 3's merits are real, and ADR-0013 Finding 5 shows the .NET route to
them is itself a redesign. **This section is the complete, executable plan** so
that overruling costs a decision, not a re-derivation.

### 4.1 · The strategy that survives, and the four that don't

| strategy | verdict |
|---|---|
| **(A) Big bang** | **Dead.** P11 is the existence proof: a faithful-looking port went green on three targeted tests and was silently wrong. Big bang has no oracle to catch that class, and the cutover destination is the maintainer's live hooks. |
| **(B) Strangler via the wire — Go shim, C# daemon** | **Dead, and this was tested rather than argued.** ~130 LOC of Go (`debug/pe` + an ECMA-335 walk: COR20 dir 14 → `BSJB` → `#~`/`#GUID` → Module row 1) computed the live deploy dir's identity as `5b6dc0cc8b5a`, matching the running socket exactly, at 0.32 ms warm. So the crossing *works*. But `SkewGuard.cs:26` compares `typeof(Frame).Module.ModuleVersionId` — a Go shim links no assembly, so **the comparison has no left-hand side** — and the shim is deliberately excluded from the identity hash (`Rendezvous.cs:34` globs `*.dll`; the native shim has no extension — `aot-boundary` rule 6). A Go-shim-only change would roll no socket and trip no guard, making ADR-0004 d3's "version mismatch is unrepresentable" *representable*. And it buys nothing: the shim is already at parity (P1) and the plan retires it. |
| **(C) Strangler via exec payloads** | **Category error.** ADR-0010 already made payloads language-agnostic; everything expensive (dispatcher fan-out and merge precedence, the closed `Effect` set, the policy gate, the six-status ask, the rendezvous, invariant 1's single-stdout-object rule) lives *above* the payload boundary and never crosses it. Legitimate residual use: write the ExecWire codec in Go as a live payload — free fluency, validates one contract. Practice, not migration. |
| **(E) Go for Windows only** | **Strictly dominated.** Two emitters of five stable contracts across three OSes, one maintainer, no CI — and the maintainer dogfoods on Linux, so the divergent half is the one nobody runs. `WireJsonlTests` exists because two emitters in *one* language already drifted. |
| **(D′) Parallel implementation against an oracle** | **Survives — with two prerequisites the obvious version omits.** |

### 4.2 · The irreversible decisions — settle these in Phase 0

Each is expensive to revisit and cheap to decide now.

1. **One binary vs two.** Everything hangs off it: whether identity must be
   *computed* at all, whether a skew guard exists, `/deploy`'s artifact count
   (today it stages **three**: shim + engine + `ui/`), whether the twelve
   AOT-boundary rules retire. P2's 0.2 ms measurement supports one.
2. **The identity mechanism AND its namespace.** The Go build-ID `contentID` via
   the combined reader (P7). But the socket *name format* is separately
   irreversible: it must be **structurally non-colliding** with the C# 12-lowercase-hex
   form, or the coexistence gate and the canary are both impossible and `doctor`
   cannot tell the lineages apart. An explicit distinct prefix is free now and a
   migration later.
3. **The escaping / byte-schema break.** One decision, four surfaces: the JSONL
   trail, the **ExecWire envelope** (a *user-facing* contract read by deployed
   payloads), harness-adapter stdout, and the API schema. It is the comparator's
   input, so deciding it late means deriving every golden twice.
4. **The Windows rendezvous substrate.** AF_UNIX + hand-rolled `LockFileEx` vs
   named pipes with `FILE_FLAG_FIRST_PIPE_INSTANCE` (structurally better — the pipe
   name *is* the lock — but wants `go-winio`, which invariant 3 does not pre-admit).
5. **The DU-replacement idiom — two idioms, not one.** Unexported-marker sealed
   interfaces for payload-carrying sets (`Effect`, `ForwardOutcome`, `ExecAnswer`,
   `PolicyResolution`); iota enums for `AskStatus`/`Verdict`; `default: panic` at
   every switch; `go-check-sumtype` in CI (a *dev* dependency — invariant 3 governs
   the runtime graph).
6. **Module layout + the arrow gate.** ~14 `internal/` packages replace 5 assembly
   arrows. Go is **stronger** on cycles (a compile error) and **absent** on intent
   (nothing declares "this leaf sees nothing"), so a `go list -deps` allowlist check
   must exist from the first commit or the arrows rot silently.
7. **The C#-freeze decision.** Not technical, equally irreversible: if the
   incumbent keeps taking roadmap features during a multi-month port, the oracle
   drifts and the parallel implementation never converges. Date it and write it down.

### 4.3 · Phases and gates

**Phase −1 · Prerequisites.** `ci-matrix` — there is **no `.github`**, so every
later gate ("green on Linux *and* a real Windows box") reads an instrument that does
not exist. `adr-0014` — Phase 0's rendezvous spike is defined as "ADR-0014's design
running"; you cannot spike a design that does not exist.
**Gate:** 3-OS CI green twice on the **existing C# suite**. If the incumbent cannot
run in CI, no Go gate is trustworthy.

**Phase 0 · Spike (throwaway).** `go-rendezvous-spike`, `go-supervision-spike`,
`go-strict-parser-spike`, `escaping-decision` (irreversible #3 — the comparator's
input), `aot-control-experiment` (already run — see ADR-0013 Finding 5).
**Gate:** the supervision spike must **first reproduce both P11 bugs as FAILING
tests** — the select-priority `Wedged` misclassification and the double-`close`
panic — and only then pass. *A spike that merely goes green proves nothing; that is
exactly what happened the first time.* Plus green on a real Windows box.

**Phase 1 · The oracle.** `conformance-corpus`, driven against the **C# daemon
first** — it must be green on the incumbent before it may judge the successor.
**Gate correction:** byte-comparison is **unachievable** (Go sorts map keys,
HTML-escapes `<>&`, formats floats differently; `api.schema.json` carries non-ASCII).
The comparator is **canonical-JSON semantic equality**, with an ADR-recorded list of
what stays byte-golden (ASCII-only fixtures).
**Honest limitation, and it is severe:** a corpus replaying recorded payloads
**cannot** catch the bug class this project actually ships. Every finding in the
git log came from an adversarial verify *against the design* or from a live
incident — not from replay. A corpus would not have caught P11. It bounds
regression risk; it does not bound *design* risk. Per-slice adversarial verify
remains mandatory, and the ADR that fixed each bug is the checklist.

**Phase 2 · Leaves.** `jsonstrict` (the ~120–150 LOC duplicate-preserving walker,
P8), `wire-frame`, `content-identity`, `jsonl-trail`, `harness-registry`
(`embed.FS`), `dispatch-policy`.
**Gate:** the 24 `DispatchPolicy` parse tests + the duplicate-key and
trailing-content cases green; all six `GOOS/GOARCH` build; `-trimpath
-buildvcs=false` byte-identical across a wiped cache **and** an empty commit.

**Phase 3 · Actor layer.** `supervision`, `worker`, `panic-containment` first-class
(a panic in any goroutine kills the process — .NET contains this structurally).
**Gate:** the six-status table under `synctest`; `go test -race -count=100`;
goleak with an **explicitly bounded** wedge allowance, since the design strands
work on purpose.

**Phase 4 · Dispatch + daemon.** `dispatcher`, `daemon-host`, `rendezvous`,
`serve-loop`, `drain`, `idle-exit`.
**Gate:** Phase-1 corpus green on all three OSes, **plus a coexistence proof** —
C# and Go daemons alive simultaneously on one machine, distinct sockets, distinct
trails, and neither `doctor` reaping the other's lineage. This gate is what makes
the canary possible.

**Phase 5 · Payload lifecycle.** `exec-handler`, `resident-exec-handler`,
`process-group` (`_unix.go`/`_windows.go`), `child-records`, `doctor`,
`test-helper-binary`.
**Gate:** `KillDisciplineTests`' properties incl. the reparented grandchild on all
three OSes, **plus** the four payloads running live on the maintainer's own hooks
(`git-orient`, `deploy-guard`, `session-pulse`, `doc-pointer`) still answering.

**Phase 6 · API + GUI.** `api-host` (net/http), `auth-gate` (+ explicit
`path.Clean` — Go does not pre-normalize dot-segments, and `os.OpenRoot` can
replace the traversal guard at the syscall layer), `read-endpoints`,
`sse-trail-tail`, `put-policy`, `ui-embed`, `schema-codegen` (~250 LOC hand-written
reflect exporter; nullability must become `*T` either way).
**Gate:** the Playwright suite **unmodified** and green against the Go daemon —
it is a black-box HTTP client and should not know. Plus the regenerated
`api.schema.json`/`api.gen.ts` diff reviewed as a one-time deliberate change.

**Phase 7 · Canary, then cutover.** Do **not** jump from Phase 6 to
`/deploy`-to-live. The architecture makes a canary nearly free: deploy Go to a
**separate** dir (`~/.captainHook/bin-go/`) with `CAPTAINHOOK_LOG` pointed at a
**separate trail**, and flip one line in `settings.json`. Rollback is flipping it
back — no rebuild, no swap, `bin/` untouched. This works precisely because the
socket *name* is the entire version negotiation ("no handshake or compat logic
exists to get wrong"), so the lineages cannot collide. The separate trail is
mandatory, not hygiene: one JSONL file with two escaping regimes is what the GUI's
SSE tailer reads.
**Gate:** N consecutive days of real sessions on the Go build, a `doc/dogfood/`
field report, zero skew-class incidents — *then* the one-artifact `/deploy` rework
and the `aot-boundary.md` tombstone.

### 4.4 · Dependency posture

- **`golang.org/x/sys` — admit it.** Justify on the verifiable fact, not vibes:
  v0.47.0 has **zero transitive module dependencies** (`go mod graph` shows one
  edge) and ships on the Go release train. Needed for macOS `sysctl(KERN_PROC)`
  (the pid-reuse guard) and Windows job objects/ACLs.
- **`Microsoft/go-winio` — do NOT pre-admit.** AF_UNIX on Windows compiles and Go
  ships a windows-gated `unixsock` test, so the *transport* needs no dependency.
  Named pipes must earn it in ADR-0014, on the merits.
- **`encoding/json/v2` — unusable.** Every file in `src/encoding/json/v2/` carries
  `//go:build goexperiment.jsonv2` on Go 1.26.2, and enabling the experiment
  reimplements v1 on top of v2. Not half-adoptable. Revisit if a future Go
  stabilizes it.
- **Dev-only tools are outside invariant 3**, which governs the *runtime* graph:
  `go-check-sumtype`, `exhaustive`, `goleak`.
