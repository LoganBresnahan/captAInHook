# ADR-0006 — Dispatch policy: the front door answers everything, works only what's allowed

**Status:** Accepted
**Date:** 2026-07-06

## Context

Roadmap item 14, from the 2026-07-06 reframe: captAInHook's policy surface
is what *it* brings, not a second copy of harness-native permissions. The
first native domain is the front door — an event arrives (the harness is
blocked on the shim's stdout) and policy decides whether it gets *worked*:
dispatched to handlers, or short-circuited to an immediate, valid Noop.
This covers both ingress ("SessionStart does nothing on this machine") and
execution ("the echo handler is off in this repo") in one contract, and it
is the exact data the management API (item 5) will manage and the GUI
(item 6) will edit: file → API → GUI.

Today the registry is hardcoded (`BuildDefaultRegistry`) and the only off
switch is uninstalling hook commands from the harness config — static,
per-harness, and blunt. Handler egress capabilities (item 15) are a
separate future contract, deliberately not here.

## Decision

1. **One strict JSON file: `~/.captainHook/dispatch.json`.** The house
   policy dialect (ADR-0005 established it; this file reuses the shape):
   `version`, `default`, ordered `rules`, strict parsing that never guesses
   — unknown fields, unknown decision strings, or a missing `version` are
   MALFORMED. Data selects among a closed set; no templates, ever.

   ```json
   {
     "version": 1,
     "default": "allow",
     "rules": [
       { "event": "SessionStart", "decision": "deny" },
       { "handler": "echo", "project": "/home/oof/some-repo", "decision": "deny" },
       { "session": "abc123", "decision": "deny" }
     ]
   }
   ```

   The decision set is **`allow | deny`** — deliberately smaller than
   `Verdict` (an `ask` makes no sense at a front door nobody is watching;
   the strict parser rejects it). A rule may name any combination of
   `event` (canonical PascalCase name), `handler` (registered handler
   name), `project` (**path-prefix** match on the event's cwd — a project
   contains its subdirectories), and `session` (exact id); criteria within
   a rule AND together, rules evaluate in file order, first match wins,
   unmatched dispatches take `default`, missing `default` is `allow`.

2. **Deny at event level skips the dispatch; deny at handler level skips
   the handler.** A rule without `handler` that matches denies the whole
   dispatch — answered immediately as Noop, no worker asked, no budget
   spent. A rule naming a `handler` excludes exactly that handler from the
   matched dispatch (as if unregistered for it); the rest run normally.

3. **Every hook is answered, always.** Policy can only choose between
   "worked" and "valid Noop on the wire" — never silence, never an error
   exit. The harness must be structurally unable to distinguish a
   policy-skipped hook from an uneventful one, except by our trail.

4. **Absent ⇒ allow everything. Malformed ⇒ Noop everything, loudly.**
   No file is today's behavior — zero-config users feel nothing. A file
   that exists but cannot be parsed dispatches NOTHING: every hook answers
   Noop, every skip logs `policy.malformed` with the parse error, and the
   human trace line (which rides the socket to the shim's stderr) says so.
   The shared principle with ADR-0005, stated once: *a policy file you
   cannot parse never silently grants execution* — and the failure lands
   in opposite directions by domain, because the domains differ: hooks are
   enhancement, so unparseable policy quiets them (agent unharmed, trail
   loud); an authz gate is a boundary, so unparseable policy denies the
   gated actions. Keep-last-good is rejected here for the same reason as
   there: silent staleness plus state where every other rendezvous
   decision is stateless.

5. **Evaluated daemon-side AND in the collapsed pipeline — one evaluator,
   both paths.** The shim stays policy-free (aot-boundary rule 1). The
   evaluator runs after harness resolve + event parse (it needs event
   name, cwd, session) and before any worker is asked, in both
   `DaemonHost`'s serve loop and `HookRun`'s collapsed pipeline —
   otherwise a cold daemonless hook would run exactly what policy denies.

6. **Hot reload, the proven mechanism.** Per-dispatch stat — (mtime,
   size), the in-place-overwrite lesson from ADR-0004 d1's amendment —
   reload on change, edit-a-rule-effective-next-hook. A reload that
   doesn't parse hits decision 4, not last-good.

7. **No pause mechanism.** "Pause" is the degenerate policy
   `{"default": "deny"}` — the language already says it, and a first-class
   toggle would be a second mechanism for the same sentence. If toggle
   ergonomics matter once real, item 5's API grows a convenience verb on
   top of this same file (its ADR decides how); nothing here forecloses
   that.

## Consequences

### Positive

- The switches users actually reach for — "this event does nothing," "that
  handler is off in this repo," "kill everything" — exist as one small
  file, hot-editable, with every skip visible in the trail.
- Item 5 gets its first managed contract, already shaped: the API writes
  what the GUI edits, and this ADR froze the semantics first.
- Zero cost to the hot path in practice: one stat (already the reload
  pattern's price) plus an in-memory match before any worker is touched;
  an event-level deny is *cheaper* than dispatching.

### Negative

- **N1 · Dispatch policy is the outermost layer and can disable anything —
  including a future security-payload handler** (e.g., ADR-0005's gate).
  Coherent — the user owns both files — but it means "who may write these
  files" is where enforcement really lives; that is item 10's trust model,
  and it lands with the surfaces that write policy (items 5–6).
- **N2 · Malformed ⇒ Noop-everything quiets ALL hooks over one typo.**
  Accepted deliberately (decision 4): the trail and the relayed trace make
  it loud, and the agent itself is never harmed.
- **N3 · Handler-level exclusion interacts with registration order and
  fail modes** — an excluded handler contributes nothing, including its
  fail-mode behavior. Semantics are "as if unregistered for this
  dispatch"; the slices must pin this against ADR-0002's ordering rules.

## Alternatives considered

| Option | Why not |
| --- | --- |
| A first-class pause (sentinel file or field) | Degenerate case of the language (`default: deny`); a second mechanism for a sentence the file already says. Revisit as an API convenience if toggle friction turns real |
| Disable by editing harness config (settings.json) | Harness-side and static: loses per-project/per-session scoping, hot reload, and trail visibility; churns the installed surface item 10 wants stable |
| Flags in code / registry hardcode | Policy is data (the ADR-0003 house pattern); code changes need deploys |
| One mega `policy.json` with per-domain sections | Couples blast radii — a malformed egress section (item 15) would Noop dispatch. One file per domain, one strict contract each |
| Shim-side enforcement (don't even forward) | Breaks aot-boundary rule 1 (the shim stays policy-free), adds a parse surface to the native image, saves ~7ms on skipped events nobody will feel |
| Keep-last-good on malformed reload | Silent staleness + state; same rejection as ADR-0005 |

## Revisit triggers

- **Toggle ergonomics bite** — users hand-edit `default` back and forth →
  item 5's API grows pause/resume as a convenience verb over this file.
- **Matching outgrows v1** — glob/regex on project paths, harness as a
  criterion, time windows → extend the rule shape; the closed decision set
  holds.
- **The fleet/control-plane arrives** (item 5's enterprise note) — a
  per-user file gains a distribution story and possibly an org-owned
  layer; revisit WITH item 10's trust model, not before.
- **Item 15 (egress capabilities) lands** — verify the two files stay
  separate contracts with separate blast radii, per the alternative
  rejected above.

## Ground truth (at acceptance)

| what | where |
| --- | --- |
| the two dispatch sites the evaluator must cover | `Core/DaemonHost.cs` (`DispatchOneAsync`), `Core/HookRun.cs` (`CollapsedAsync`) |
| event fields the matcher needs (name, session, cwd) | `HookEvent` in `Core/Model.cs`; populated via `Harness.ParseEvent` per the spec's request fields |
| reload mechanism inherited | `ReloadingHarnessRegistry` + ADR-0004 d1 amendment (per-file mtime+size stat) |
| the shim's exclusion from all of this | doc/flow/aot-boundary.md rule 1 |
| the policy dialect's first speaker | ADR-0005 (deferred) — same version/default/rules shape, different domain and failure direction |
| registration-order semantics N3 must respect | ADR-0002 |

## Implementation plan

Generated by [`/adr-plan`](../../.claude/workflows/adr-plan.js) on 2026-07-06 — a
**stable ranking** of this ADR's work into effort-tagged, dependency-ordered build
phases. Derived from the decisions above, not a decision itself; changes only if
they do. **Progress is tracked on the [roadmap](../roadmap.md) item 14, not
here.** Tags: `effort/hardness · verify?`; ◆ = on the critical path.

**Phase 1 — foundations (two independent roots; disjoint code)**
- `dispatch-policy-file` ◆ — med/moderate · no adversarial verify (pure parser,
  loud-by-design failure; house precedent: `HarnessSpec.TryParse`'s strict walk,
  `ResolveDir`'s injectable-path idiom) — the `DispatchPolicy` model + strict
  parser, new file under `Core/`.
- `handler-level-exclusion` — low/mechanical · no adversarial verify — an
  optional excluded-names filter on `DispatchAsync`'s per-dispatch Runner list
  (order-preserving, pre-fan-out, snapshot registry untouched); dead until wired.

**Phase 2 — pure logic + side-lane pins**
- `rule-matcher` ◆ — med/moderate · no adversarial verify — deps:
  dispatch-policy-file. First-match-wins over AND'd criteria; the one sharp edge
  is the path-prefix boundary (`some-repo` vs `some-repo2`, trailing separators,
  cwd == project) — unit tests per criterion pin it.
- `exclusion-ordering-failmode-pins` — med/moderate · no adversarial verify
  (test-only; these pins ARE the adversarial pass for N3) — deps:
  handler-level-exclusion. Excluded fail-closed handler contributes no deny;
  survivors keep registration-order merge; the excluded handler's supervised
  Worker is left untouched (filtered, never restarted — a restart wipes state).

**Phase 3 — first wire-touching behavior**
- `event-level-deny-shortcircuit` ◆ — med/moderate · **verify** (drive it; no
  adversarial pass — a byte-compare test pins the wire contract) — deps:
  dispatch-policy-file, rule-matcher. Early return after `ParseEvent`, before
  `DispatchAsync`, reusing the existing gate+serialize tail: the denied answer
  must be byte-indistinguishable from an uneventful hook (invariant 1), and it
  must skip `CompleteBackgroundAsync` (nothing dispatched). This seam is reused
  twice downstream — a fumble here propagates.

**Phase 4 — fallback tri-state**
- `absent-allow-malformed-noop` ◆ — med/moderate · **verify (adversarial)** —
  deps: dispatch-policy-file, event-level-deny-shortcircuit. Absent vs unreadable
  vs unparseable resolution, parse error carried in the poisoned policy for
  per-dispatch `policy.malformed`, no keep-last-good. Both misclassification
  directions are costly: absent-as-malformed quiets every zero-config user;
  malformed-as-absent silently grants execution.

**Phase 5 — wire the evaluator into both dispatch sites**
- `evaluator-both-paths` ◆ — med/moderate · **verify (adversarial)** — deps: all
  of phases 1–4. One evaluation call at the identical seam in
  `DaemonHost.DispatchOneAsync` AND `HookRun.CollapsedAsync`; the two paths must
  not drift when most runs exercise only one. Never land this before the
  absent⇒allow default exists — zero-config users break otherwise.

**Phase 6 — tail: reload + observability + doc-pin (mutually independent)**
- `policy-hot-reload` ◆ — med/moderate · **verify (adversarial)** — deps: the
  wired evaluator. Transplant of `ReloadingHarnessRegistry`'s (mtime,size) swap;
  the sharp edge: a failed reload must poison AND advance the stamp — a first
  pass plausibly ships keep-last-good (the rejected alternative) or a re-parse
  storm.
- `skip-trail-visibility` — low/mechanical · no adversarial verify — deps: both
  skip mechanisms + wired evaluator. `policy.skip`/`policy.exclude` JSONL +
  human trace lines; the event-deny path fires before `DispatchAsync` builds the
  Trace, so it mints its own trace text.
- `default-deny-pause-pin` — low/mechanical · no adversarial verify — deps:
  wired evaluator. One pin test: `{"version":1,"default":"deny"}` Noops every
  hook through both paths — decision 7's pause story, demonstrated not built.

**Critical path:** dispatch-policy-file → rule-matcher →
event-level-deny-shortcircuit → absent-allow-malformed-noop →
evaluator-both-paths → policy-hot-reload.

**Sequencing traps.** (1) The evaluator goes live only after the absent⇒allow
default exists (phase order is load-bearing). (2) The phase-3 short-circuit must
keep both dispatch sites consistent and skip background drain — it is reused by
phases 4–5. (3) Hot reload's edit-effective-next-hook is only observable once
the evaluator is consulted per dispatch; don't attempt it earlier. (4) Every
dispatch-site test swaps the `Log` sink and uses an injectable policy path —
`~/.captainHook/` is the live deployment tree. (5) Batching: phases 1–2 pair
naturally (policy-file+matcher in one session; exclusion+pins as the side-lane);
phase 6 batches the two low slices, hot-reload lands alone with its verify pass.
(6) Adversarial verification on exactly three slices (4, 5, hot-reload); no
slice warrants ultracode. `/deploy` only after the whole stack lands.
