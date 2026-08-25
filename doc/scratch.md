# Scratch

Running list — jot ideas here, promote to DESIGN.md / real tasks when they firm up.

## Figuring out auto-updates to my development process

- [ ] skills
- [ ] docs
- [ ] tests
- [ ] updates

## Trail integrity follow-up

- [x] **DONE 2026-08-11** — both emitters now open with real `O_APPEND` over
      libc (`captainHookWire/PosixTrail.cs` + its `Logging.fs` mirror), pinned by
      `TrailAppendTests`; the probe that justified it (55% line loss at 8×250)
      and the per-OS flag/ABI facts are in platform.md § File locking, the
      story in ADR-0004 N3's amendment. Original note:
- [ ] ~~**Emitters don't actually O_APPEND**~~ (surfaced by ADR-0007 sse-trail-tail's
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

## Item 15 sandboxing — the container-runtime shape (2026-07-20 design chat)

Non-authoritative sketch for when item 15 (handler egress / sandboxing) or the
community registry firms up. The insight: **a container is just another way to
spawn a wire-speaking child**, so most of this is *already available* and the
"build" is small and deferred.

- **Free today, zero framework code.** Payloads are processes speaking ExecWire
  over stdin/stdout — and `docker run -i --rm <image>` is exactly that. A user
  can put `"command": "/usr/bin/docker", "args": ["run","-i","--rm",
  "--network=none","-v","/home/me/notes:/notes:ro","img"]` in `handlers.json`
  NOW; captAInHook spawns it, pipes the envelope, reads the Effect, and never
  knows a container is in the middle. The flags ARE the sandbox. Invariant 3
  holds — we don't ship/require Docker, the user's command invokes it (same as
  it invokes `bash`). Worth an `examples/payloads/` demo as the documented
  "cage a payload" pattern.
- **Why it beats per-OS seccomp/namespaces:** the container runtime already
  solved multi-OS. One vocabulary (`--network`/`-v`/`--memory`/`--read-only`/
  `--cap-drop`) means the same on every host it supports; captAInHook writes
  NO platform-specific isolation code. This is the better answer than the
  Linux-seccomp path ADR-0011's d7 trigger note gestures at.
- **Three seams where it stops being free:**
  1. *Resident wants to be the container.* `docker run` per oneshot re-adds the
     cold-start tax item 12 killed; but `docker run -i` once + the lock-step
     JSONL loop over its stdin/stdout for the daemon's life = the container IS
     the warm child. Containerized ⇒ resident is the natural shape.
  2. *Kill discipline differs.* Phase-5 `setsid`+`kill(-pgid)` assumes a host
     process tree; a container's lifecycle is `docker stop` (reaps everything
     inside — cleaner, but a DIFFERENT kill path the current ProcessGroup code
     doesn't cover). See doc/flow/actor-supervision.md + ExecHandler kill code.
  3. *Event cwd is a host path; the container has its own FS.* The set of
     mounts is precisely the "what may this handler reach" declaration — item
     15's egress question, rendered portable and OS-neutral.
- **The design fork if we build it (needs an ADR):**
  - *Dumb (ships today):* user writes the whole `docker run`; sandboxing is
    their expression, we stay a spawner. House pattern at its limit — no
    template language because there's no template.
  - *Structured (item 15 scope):* `handlers.json` grows `sandbox:{image,
    mounts,network,memory}`, a CLOSED set of coded runtime adapters
    (docker/podman) render it — ADR-0003's declare-in-data/bind-in-code applied
    to isolation. The GUI's verbatim confirm (ADR-0011 d2) then shows the
    sandbox spec ("no network, ro ~/notes, 256MB") — a far more honest trust
    surface than "runs as you." Earns its ADR only when the community registry
    makes UNTRUSTED payloads real (a registry entry can't be trusted to write
    its own docker flags — it must carry a declarative, auditable spec).
- **Caveats, not overselling:** mounting the Docker socket / `--privileged` is
  its own escape hatch (containerized ≠ safe; the flags must be right); a
  container is heavyweight for the simple "deny network" case a 10-line seccomp
  filter also solves. But as THE cross-platform answer that costs captAInHook
  nothing and reuses the process-boundary-as-isolation-seam the design already
  leans on, this is the shape item 15 should take.

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

## Deferred-unescape hazard in the sibling parsers

- [ ] **`GetString()` on lazily-unescaped JsonElements can throw
      `InvalidOperationException`** (found by the ADR-0015 slice-6 skeptic pass,
      2026-08-11). A lone-surrogate escape (`"\ud800"`) parses as a valid
      document and only fails at read — `JsonDocument` defers unescaping — so a
      `catch (JsonException)` does not cover it. `DispatchPolicy` is FIXED
      (`TryReadString`, pinned at TryParse/Resolve/ApiPolicyWriter). The same
      pattern survives in `Core/Harness.cs` (user harness overrides read from
      disk), `Core/ExecHandlersFile.cs` (user- AND API-written handlers.json —
      a hostile PUT body is a 500, and a hand-edit could crash the loader), and
      `Core/ExecWire.cs` (payload stdout — external data). Sweep them onto the
      same helper when next touching each file; the handlers.json one is the
      most exposed (API surface).

## Per-instance mailboxes: addressing an AGENT, not only a role

*(Raised 2026-08-17 by the maintainer, watching the Mail canvas show six
cursors on one `maintainer` lane — two of them live windows that both received
everything. **Graduated the same day into
[ADR-0018](adr/0018-instance-addressing.md)** — the sketch below is kept as the
raw note; the ADR is authoritative and adds the reaper role.)*

**The idea.** Today `to` names a ROLE and delivery fans out: every session
running a `mail digest --role R` gets every envelope addressed to R, each
spending its own TTL opportunities. That is right for "all maintainers, read
this" and wrong for "*you*, the instance I am talking to, read this". The ask is
a second address kind — a per-agent mailbox — so a sender can choose:

```
to: maintainer            → the ROLE: every instance holding it (today's behavior)
to: maintainer/laptop-a   → ONE instance (unicast), whoever that agent is
```

Two "maintainers" then means two agents holding one role, each with a mailbox of
its own, addressable either way.

**What already exists (do not rebuild it).**
- Cursors are already keyed **role × session** (`cursor.<role>.<session>.json`),
  so "a mailbox per reader" is half-built — what is missing is the ability to
  ADDRESS one.
- `dispatch.json` already scopes which digest runs in which window (project
  path-prefix, session id), which is how two windows get two roles today.
- One append-only ledger with one hash chain serves every role; "two mailboxes"
  almost certainly should NOT mean two files — an address is a routing key over
  the one chain, and keeping that preserves the chain, the canvas, and the
  audit story.
- `from` already carries `{agent, harness, session}`, so senders are already
  identified; recipients are the half that is not.

**The five decisions a plan has to make.**

1. **What is an instance's name, and where does it come from?** A session id is
   ephemeral — a new window is a new id, so a mailbox keyed to it dies when the
   window closes (visible today: four of the six `maintainer` cursors are dead
   sessions holding pending mail forever). A durable name has to be assigned at
   BOOT: an env var the window carries, a cwd-derived name, or a registration
   flag (`mail digest --role maintainer --as laptop-a`). This is the crux; the
   rest follows from it.
2. **Address syntax that cannot collide.** Role names and instance names share
   one `to` namespace. Either a separator with a rule (`role` vs `role/instance`)
   or explicit kinds (`role:maintainer`, `agent:laptop-a`). Whatever it is, the
   parser must refuse ambiguity rather than guess — a misrouted envelope is
   silent.
3. **Role delivery stays broadcast — or does it?** "Every reader gets it" and
   "any ONE reader claims it" are different systems: the second needs claim /
   lease semantics, which the current cursor model deliberately does not have.
   Recommend: keep role = broadcast, instance = unicast, and put a work-queue
   role kind explicitly out of scope until something needs it.
4. **What TTL means for a unicast envelope.** Per-cursor delivery opportunities
   make sense for a broadcast (nobody can say "it was read"). With exactly one
   addressee, "delivered / not delivered" becomes answerable — and probably
   wants a different expiry story than "spent after N seams somebody else
   attended".
5. **Dead mailboxes.** Per-instance addressing multiplies cursors, and nothing
   reaps them today (`doctor` does not touch mail). A named instance that never
   comes back holds pending mail forever, and its lane grows monotonically.

**Why it entangles with ADR-0017.** `mail ask --wait` and `inReplyTo` want the
ANSWER to come back to the asker, not to everyone wearing the asker's role —
i.e. ask/reply is unicast by nature, and today the only available unicast key is
the ephemeral session. Deciding instance identity first would make the reply
path fall out; deciding it after would mean reworking it. Likewise the watcher's
role-kind inference (d3) is stated per role and would need to say what a
per-instance address means for human-held vs robot-servable.

**Surfaces it would touch:** `MailEnvelope` (`to` parse + validation),
`MailCursors` (key shape), `MailDigest.Plan` (which addresses a registration
reads), the read model + `web/src/mail.ts` + the canvas (a lane per role with
per-instance sub-lanes, or lanes per address), and `handlers.json` registration
args. The wire is versioned by nothing — `to` is a bare string today, so the
syntax choice in (2) is effectively permanent once mail is written with it.

## The member model — and the scope pullback that answers it (2026-08-20)

*Thinking-out-loud from a design conversation, recorded so it is not lost.
NON-AUTHORITATIVE (this whole file is). **The pullback at the end WAS decided,
2026-08-20, and is recorded where decisions live: ADR-0017 § Scope amendment.**
Read that, not this, for what was settled; this file keeps the exploration —
including the parts deliberately not built.*

### What "a session" is, per scenario

**A — one workspace, one sit-down, several sessions.**

- *several terminals, each `claude`* — **works today.** Distinct `session_id` per
  window ⇒ distinct mailbox (`cursor.<role>.<session>.json`). Role is decided by
  cwd through `dispatch.json` (the live install does this: cwd `/profile` ⇒
  `reviewer`, elsewhere ⇒ `maintainer`).
- *Task subagents inside one session* — **not a bus member.** A subagent shares
  its parent's `session_id` (claude-code.json declares `SubagentStop` as a hook
  of the same session), so it has no address, no cursor, no mailbox. Verify
  against the CLI before designing on it.
- *agent-spawned `claude -p`* — the turn payload's shape, but woken BY the
  watcher (absence) rather than requested BY a session, and it dies at the end of
  the turn.

**B — several harnesses, several sessions each.** Attended goose windows need
only `harnesses/goose.json` + a `mail digest` registration — no engine change,
that is the harness registry's whole job. A goose *turn* would need
`turn-goose.sh` AND role→harness routing, which does not exist: fan-out is by
event, so two turn payloads both spawn, both attempt the pickup, one wins the
cursor race and spends a model call, the other exits having spent nothing.
Exactly one model call, but WHICH harness serves the role is a race, not a
choice.

**The gap scenario A points at:** nothing lets a session say *"give me a
reviewer."* Mail to an empty role waits for the watcher's absence rule.

### Attended vs headless — a member class the system does not record

|  | attended (human at the wheel) | headless (spawned, unwatched) |
|---|---|---|
| notified by | PULL — `mail status`, looked at when the human chooses | PUSH — the nudge; the mail IS the prompt |
| reads mail when | it next acts (a seam) | immediately, as its first act |
| costs | the injected characters | a model call on the owner's bill |
| needs consent | no — it interrupts nothing | yes — `watch.json` + `dispatch.json` + budgets |
| ends by | the human closing it | finishing, or being retired (NOT BUILT) |

Today `RoleKinds` classifies a ROLE (`Unserved`/`HumanHeld`/`RobotServable`/
`Mixed`) but nothing records a MEMBER's class. That absence is why ADR-0018 d6's
dead-mailbox rule has to reconstruct "was anybody ever really here?" from cursor
files and dispatch ages.

**Attended notification is per-harness and should be DATA.** `mail status` is
generic (a pull any process can run); *where you wire it* is not — `statusLine`
is a claude-code config key, and the 2026-08-17 dogfood found the count renders
in the TUI and NOT in the VS Code extension. A `"display"` block in the harness
spec beside `events`/`install`, with `"kind": "none"` a legal and honest answer,
would say so out loud instead of leaving an operator to discover it by watching
a status bar that never changes.

### Discoverability: the fork

Today membership is INFERRED (cursor files ∪ recently-dispatched sessions; "no
registry and no heartbeat"; a stale entry means unknown, never gone). The
alternative is DECLARED: a member announces `{address, harness, class,
capabilities}` on connect, and anyone can list the directory.

- inference: costs nothing, cannot lie about the past, no lifecycle to get
  wrong — but nobody is discoverable and dead mailboxes accumulate.
- declaration: discoverable, routing becomes a choice rather than a race — but
  needs heartbeats, staleness rules, and a crash leaves a lie in the file.

**The reaper is the price of inference.** A declared directory makes reaping the
exception rather than the main path — but it still needs one, for members that
die without saying so.

### Two jobs currently spelled the same

- **reap a MAILBOX** — built (`mail reap`): removes one cursor under its own
  flock, writes `mail.reap`. Standing only; no process is signalled.
- **retire a MEMBER** — not built. Ending a headless member is process lifecycle
  (bosun / ProcessGroup, ADR-0014), not a mailbox act. A long-lived spawned agent
  nobody can retire is a leak with a token budget.

### The decisions this would have needed

1. Directory: declared or inferred? (Half-measure: inference stays the floor,
   declaration is an optional upgrade.)
2. May a session REQUEST a member? "Spawn me a reviewer" is an agent spending the
   owner's money — same consent shape as a nudge, plus a cap on live members.
3. Is a headless member one-shot (bounded, contextless) or long-lived (continuity,
   but needs retirement and idle timeouts)?
4. Attended notification as declared harness data (`display`).
5. Role → harness routing (payload self-scopes via `TURN_ROLES`, or the rule
   names the harness).
6. Reap semantics: does `reap` grow a second meaning, or does member lifetime get
   its own verb? (Own verb — "remove standing" and "end a process" failing the
   same way would be bad.)

### THE PULLBACK — and why it settles most of the above

The maintainer's reframe, 2026-08-20, and it is the right one:

> This is all too much. It ruins the hook nature of the application. We may not
> even need a watcher and/or reaper. Identity between session / workspace /
> project is still important. We don't re-invent agent orchestration (langgraph
> etc.) — we stick to what we do: a **passive mailbox that runs during hook
> times**. Orchestration comes from the higher level; if a user wants two agents
> in the mail system as part of their langgraph setup, they set up the harness
> json and the policy json to participate.

What this keeps, because it is hook-shaped by construction:

- the bus (ledger, cursors, TTL counted in delivery opportunities, hash chain)
- `mail status` — a pull, ruleless, interrupts nothing
- `mail ask --wait` — the synchronous shape, and note it needed **no watcher at
  all**: a bounded read of the ledger by the asking process
- identity and addressing (`role`, `role@instance`, cwd/project scoping, named
  mailboxes) — the part the maintainer explicitly wants kept
- the thread fields (`inReplyTo` read, stored `hops`, the closed-by index)

What it puts in question:

- the WATCHER — the one component that acts on its own. It is a timer in a
  daemon, and it needed monotonic deadlines, persistence across restarts,
  budgets, consent, a self-feed guard and an idle-exit interaction: ~7 slices of
  machinery for "when nobody is home, wake somebody". **It exists to compensate
  for captAInHook not being the orchestrator.** Accept that it is not the
  orchestrator and the job mostly evaporates.
- the LLM REAPER (`reaper-payloads`' second half) — another autonomous agent,
  the exact thing being pulled back from.

What survives the pullback that might look like it should not:

- **mailbox hygiene is a real bus problem, independent of the watcher.** Closed
  human windows left 4 of 7 cursors holding mail forever on the live bus
  (2026-08-17), *before* the watcher existed. So: keep the deterministic reaper
  (forward-all / drop-all is data, not judgement); drop the LLM one.
- the thesis still demos without a daemon-side timer: an LLM-backed HANDLER at a
  seam (`starter-llm.sh`) is "splice an LLM-backed subsystem into an agent's loop
  at a guaranteed seam" in full, with no autonomy anywhere.

Consequences that fall straight out, if the pullback holds:

- `nudge-workspace` ⇒ **WITHDRAW** the claim (ADR-0017 d5's Ground truth sentence
  is deleted, `project` rules are documented as not applying to nudges, and a
  `project` rule that can never match a nudge is made LOUD). Growing `watch.json`
  a field is adding config surface to a component being demoted.
- `turn-role-routing` ⇒ don't build; the orchestrator picks the harness.
- a member DIRECTORY (decisions 1–3) ⇒ don't build. Presence-by-inference is the
  hook-shaped answer.
- the watcher ⇒ **freeze, don't rip.** It is already inert by default (absent
  `watch.json` ⇒ zero nudges), it is the best existing proof of the ADR-0001/0002
  thesis, and removing it costs more than parking it. Reframe it in the docs from
  "the product" to "the reference integration", and stop extending it past the
  honesty fixes (`nudge-accounting`'s wrong budget column, `turn-provenance`).
- what "participation is declarative" already means, today, with no new
  mechanism: a harness spec json + a `mail digest` registration in handlers.json
  + `dispatch.json` rules. Any process that can run `captainHook mail send` is
  already a member.
