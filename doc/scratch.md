# Scratch

Running list — jot ideas here, promote to DESIGN.md / real tasks when they firm up.

## Figuring out auto-updates to my development process

- [ ] skills
- [ ] docs
- [ ] tests
- [ ] updates

## Trail integrity follow-up

- [ ] **Emitters don't actually O_APPEND** (surfaced by ADR-0007 sse-trail-tail's
      adversarial verify, 2026-07-08; probed via strace). `File.AppendAllText`
      on .NET 10/Linux opens WITHOUT O_APPEND and pwrites at offsets cached at
      open — so the shim and daemon appending the trail in the same sub-ms
      window can overwrite each other's lines, contradicting WireJsonl.Append's
      comment and ADR-0004 N3's convergence story. Fix is emitter-side (both
      `WireJsonl.Append` AND `Logging.fs`'s file sink need a true O_APPEND
      open); wire lib is the AOT leaf + golden-pinned, so it rides its own
      slice, likely an ADR-0004 amendment. See doc/platform.md § File locking.

## Policy hot-reload robustness follow-up

- [ ] **The `(mtime,size)` stat-gate can miss a same-LENGTH policy change on a
      coarse-mtime filesystem** (surfaced by ADR-0007 put-policy-write's
      adversarial verify, 2026-07-08; probed in isolation). `ReloadingPolicy.Stamp`
      (DispatchPolicy.cs) keys reload on `"{LastWriteTimeUtc.Ticks}|{Length}"`.
      For a length-preserving edit (toggle a rule's `decision`, swap a same-length
      `session`/`project`) detection rests entirely on mtime resolution — two
      writes within one mtime granule + equal length ⇒ identical stamp ⇒ the
      daemon keeps the stale policy, no `policy.reload`. **NOT reproducible on a
      normal local `~/.captainHook`** (ext4/APFS/NTFS mtime is ns/100ns — 0
      collisions in 2000 rapid same-length PUT pairs); real only on
      FAT/CIFS/old-ext3 (≥1s granularity). Pre-existing ReloadingPolicy property
      (ADR-0006), not new to put-policy-write — but the write API makes rapid
      same-length writes programmatically reachable where a human editor can't.
      Fix belongs in ReloadingPolicy/ADR-0006, and the OBVIOUS "add the content
      hash to the stamp" is WRONG here — it would read+hash the file on every
      dispatch, taxing the hot path the cheap stat deliberately spares. A right
      fix advances a discriminator at zero read cost: the inode number (changes on
      every atomic rename-replace) or ctime, via a small `stat` P/Invoke (host is
      JIT, not the AOT leaf). See doc/platform.md § File locking (mtime resolution).

## Use-case catalogue — "what can I do with captAInHook?" (README material)

Raw material for a README section that answers *"what is this for?"* Organizing
insight: **deterministic-data-pull + `Inject` is just ONE cell of a grid** — one
Effect verb × one event × the deterministic-vs-LLM axis. The other four verbs are
each an unexploited category. Frame the README by the closed Effect set (our own
vocabulary) rather than a flat feature list.

### By Effect verb

- **`Decide` — guardrails & policy gates (PreToolUse).** Seeded by ADR-0005. Deny
  destructive bash; gate writes to protected paths; secret-scan tool *inputs*
  before they run; require approval for outbound network; rate-limit expensive
  tools; block `--force` push to main. Our angle vs. the harness's native
  permissions: **portable across harnesses, hot-reloadable, every decision lands
  in the trail as a `Decide` trace.**
- **`Replace` — rewrite the payload in flight.** Redact secrets/PII from prompts
  before the model sees them; scrub/truncate giant tool outputs before they
  re-enter context (token-budget win); expand house macros/aliases; canonicalize
  inputs; enforce a structured-output schema on the way back. Quietest verb,
  probably the most under-leveraged.
- **`Background` — fire-and-forget side effects & observability.** The trail is
  already JSONL — lean in. Per-tool telemetry + cost/token accounting to a
  dashboard; audit stream to a SIEM; Slack/webhook on `Stop`; trigger CI or a
  re-index on file writes; warm a cache. None of it touches model output ⇒ pure
  `Background`.
- **`Inject`, non-data-source.** Beyond data pulls: git/worktree state, ticket or
  on-call status, budget-remaining warnings, staleness flags ("this file changed
  since you last read it"), prior-decision reminders, RAG memory.

### Two cross-cutting multipliers

- **LLM-backed handlers — the actual thesis (DESIGN.md).** Splice an *LLM*
  subsystem at a guaranteed seam, deterministically supervised. Semantic policy
  ("does this bash command *look* destructive?" as `Decide`); prompt
  classification → route to a different harness/model; auto-summarize long tool
  output (`Replace`); self-critique injected at `Stop` ("you claimed done — did
  tests actually run?"). The supervision/actor layer is what makes an unreliable
  LLM call safe in the hot path.
- **Lifecycle-event-specific plays** (free from the event set). `SessionStart`:
  warm the daemon, health-check deps, auto-`/orient` context load. `Stop` /
  `SubagentStop`: verification gates — block "done" until the suite is green;
  aggregate subagent results. `PreCompact`: preserve critical state with our own
  summarization before the harness compacts.

### Highest-leverage under-explored picks (for the demo narrative)

- **`Background` observability** — the trail is already the substrate; shortest
  path to a demo people *feel*.
- **LLM-backed `Decide`** — the only category that genuinely *requires* our
  supervision story, so it's the most defensible differentiator vs. plain shell
  hooks. Deterministic-inject is the safe opener; these two are where the
  "composition primitive" claim earns its keep.

## Security follow-ups

- [ ] **Trail-at-rest hardening** (surfaced by ADR-0007 auth-token-origin's
      adversarial verify, 2026-07-07). The JSONL trail holds prompts + tool
      calls but is created 0644, and `WireJsonl.Append` makes `~/.captainHook`
      at the umask default (0755) on the first hook — so a co-located user can
      read the trail in the window before a daemon tightens the dir to 0700, or
      in pure-collapsed/no-daemon runs. The API TOKEN is already safe (0600 at
      birth) and the daemon now tightens the dir, but closing the trail window
      means the shim/`WireJsonl` creating the dir 0700 + the trail file 0600 —
      a wire-lib (AOT leaf) change, cross-platform-guarded, likely an ADR-0004
      amendment. See doc/platform.md § Runtime directories (the residual note).
- [ ] **`captainHook ui` token-in-argv exposure** (noted landing ADR-0008's
      ui-cli-verb, 2026-07-09). The verb hands `http://…/ui#t=<token>` to the
      OS opener via argv, which is briefly world-readable in
      `/proc/<pid>/cmdline` while xdg-open runs — a hostile OTHER-user on the
      box can race it and steal the bearer, stepping around the 0600 api.json
      trust root. Narrower than a query param (no logs/history/server) and
      inherent to decision 3's "CLI opens browser" shape — browsers take URLs
      no other way. Hardening candidate if multi-user boxes ever matter: open
      a token-free `/ui` and hand the credential over a one-time, expiring
      redirect (or local ws pairing) instead of the URL. Comment lives at
      `UiVerb.DefaultLauncher`.
