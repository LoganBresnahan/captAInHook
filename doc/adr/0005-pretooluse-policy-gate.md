# ADR-0005 — PreToolUse policy gate: a declarative policy file that fails toward deny

**Status:** Accepted, **deferred** *(2026-07-06, same day: the product's
native policy story was reframed around what captAInHook itself brings —
ingress/dispatch policy (roadmap item 14), handler enable-disable (item 5),
and handler egress capabilities (item 15). Tool-call gating overlaps
harness-native permissions and is demoted to a secondary handler payload;
the decisions below stand unchanged for when that payload is built —
likely with item 9, after the GUI.)*
**Date:** 2026-07-06

## Context

Roadmap item 13. The before-tools seam is live and nearly free — the AOT
shim dispatches PreToolUse at ~7ms end-to-end (item 12) — and currently
carries zero handlers: all seam, no payload. The gate is the payload, and it
is the framework's first **fail-closed** handler in production: `Verdict`
(`Allow | Deny | Ask`), `Effect.Decide`, `FailMode.Closed`, and the
claude-code spec's `decide` capability on PreToolUse all exist and are
exercised only by tests today.

The handler itself is implementation. Two things rise to decision level:
the **policy file** is a new user-facing contract (the third after harness
overrides and the hook protocol itself), and its **failure semantics invert
the house default** — the framework is fail-open by design (ADR-0002), and
ADR-0003 deliberately warns-and-skips a malformed harness override. Neither
posture is acceptable for an authorization gate: config that selects wire
adapters is cosmetic; config that approves writes is a security boundary.

## Decision

1. **One policy file, JSON, data-selects-code-enforces.**
   `~/.captainHook/policy.json` — a single file, not a directory (a policy
   dir invites merge-order semantics nobody needs yet). House pattern from
   ADR-0003: the file *selects* among a CLOSED decision set; it never grows
   templates or expressions.

   ```json
   {
     "version": 1,
     "default": "allow",
     "rules": [
       { "tool": "Write", "decision": "ask" },
       { "tool": "Bash",  "decision": "deny", "reason": "shell off in this repo" }
     ]
   }
   ```

   `decision` values mirror `Verdict` exactly (`allow | ask | deny`).
   `tool` matches the harness's tool name **exactly, case-sensitive** in v1
   — no globs, no input-content matching (the honest v1 limit; see
   triggers). First matching rule wins (file order is load-bearing, the
   registration-order rhyme); unmatched tools take `default`; a missing
   `default` is `allow`. Parsing is strict, `Frame.Decode`-style: unknown
   `decision` strings, unknown top-level fields, or a missing `version` are
   MALFORMED — the codec never guesses, least of all here.

2. **Absent file ⇒ Noop. You opt into gating by creating the file.**
   No file means the gate handler returns `Effect.Noop` — today's live
   behavior, zero cost to users who never asked for a gate. This is the only
   permissive default in the design.

3. **Malformed file ⇒ deny everything, loudly, statelessly.**
   A policy that cannot be parsed cannot be enforced, and someone *intended*
   policy — failing open silently un-gates them. A malformed (or unreadable)
   file denies **every** PreToolUse dispatch with a reason string naming the
   file and the parse error, so the very next tool call surfaces the problem
   and keeps surfacing it until fixed. Deliberately rejected: keep-last-good
   (silent staleness in an authz path; also state where every other
   rendezvous decision is stateless) and deny-only-the-gated-set (the gated
   set is inside the file we failed to parse).

4. **Fail-closed supervision, as ADR-0002/0004 already define it.**
   The gate registers `FailMode.Closed`: a crash or budget timeout becomes
   `Decide(Deny)` with the failure as reason; an escalated worker fast-fails
   (~0ms, decision 5b) to the same deny. Consequence named below: a
   chronically crashing gate denies all gated tools until the worker
   respawns or the daemon restarts — for an authorization boundary that is
   the correct failure direction, and the trail makes it loud.

5. **Hot reload = the harness-override mechanism, same lesson included.**
   Per-dispatch stat of the policy file — (mtime, size), the in-place-
   overwrite lesson from ADR-0004 d1's amendment — reload on change.
   Edit-a-rule-effective-next-tool-call, exactly like
   edit-a-spec-effective-next-hook. A reload that parses clean replaces the
   snapshot; a reload that doesn't hits decision 3 (deny + loud), not
   last-good.

6. **The gate is stateless.** Each dispatch reads an immutable policy
   snapshot; there is no cross-dispatch state — which also means ADR-0004
   decision 6's head-of-line concern cannot apply to it.

**Not decided here** (each is a trigger, not scope creep): input-content
matching (`Bash` command patterns — the obvious next want), per-directory or
per-session scoping, glob tool matching, policy editing via the management
API/GUI (items 5–6, where item 10's trust model also lands), and
multi-policy composition.

## Consequences

### Positive

- The seam item 12 built finally pays rent: a real decision on every tool
  call at a ~7ms floor, and the first production traffic through
  escalate-and-deny supervision.
- Item 5's event stream gets designed against genuine `Decide` traces
  instead of echo traffic.
- The contract is a data file: testable with fixture files, hot-editable
  live, GUI-editable later without a schema change.

### Negative

- **N1 · A broken gate denies gated work until restarted.** Fail-closed
  means a chronically crashing gate (or a wedged one, post-escalation)
  bricks gated tools — visibly, with reasons in the trail. Accepted: that
  is what fail-closed is for; the mitigation is the loud trail plus
  `--replace`/doctor restart.
- **N2 · Malformed ⇒ deny-all is harsh.** A typo in policy.json stops every
  tool call until fixed. Accepted deliberately — the alternative is a
  security boundary that shrugs. The reason string names the file and error.
- **N3 · Exact-name matching is coarse.** `"Bash": "allow"` allows *all* of
  Bash; there is no way to gate `rm` but not `ls` in v1. Honest limit,
  named; content matching is the first revisit trigger.
- **N4 · Ask-fatigue is possible** if a user gates broad tools with `ask`.
  Their choice, their file; the GUI (item 6) is where ergonomics land.

## Alternatives considered

| Option | Why not |
| --- | --- |
| Rely on Claude Code's own `settings.json` permissions | It exists, but it is harness-specific, not observable in our trail, and not the point — the gate demonstrates a *portable*, hot-reloadable, supervised policy layer that works per-harness via capability specs (and the trail's `Decide` traces feed items 5–6) |
| Keep-last-good on malformed reload | Silent staleness in an authorization path; introduces state the design otherwise avoids |
| Hardcode a write-class tool list in code | The gated set is policy, and policy is data — the house pattern (ADR-0003) puts it in the file |
| Policy directory with merged files | Merge order is a semantics tax with no current buyer; one file, one contract |
| Embed policy in the harness spec | The spec declares what a harness *can* express (wire capabilities, owner: the framework); policy declares what the user *permits* (owner: the user). Different owners, different files |
| Fail-open on gate crash (FailMode.Open) | Availability over authz inverts the gate's purpose; ADR-0002 reserved Closed for exactly this |

## Revisit triggers

- **Content matching wanted** — the first time exact tool names can't
  express a real policy (`Bash` yes, `rm -rf` no) → extend the rule shape;
  the closed-decision-set principle holds, the matcher grows.
- **A second harness goes live** — verify the gate's `Decide` degrades
  correctly where a spec declares no `decide` capability (the capability
  gate already strips it; confirm the deny still surfaces in the trail).
- **Untrusted policy sources appear** (community registry, item 10) — the
  gate's inputs stop being the user's own file; revisit with the trust
  model, possibly behind process isolation (ADR-0004 N2's mitigation).
- **Ask-fatigue or policy ergonomics bite** — the management API/GUI grows
  policy editing (items 5–6).

## Ground truth (at acceptance)

| what | where |
| --- | --- |
| effect model already in place | `Core/Model.cs` — `Verdict { Allow, Deny, Ask }`, `Effect.Decide(Verdict, string?)` |
| capability already declared | `harnesses/claude-code.json` — `"PreToolUse": { "effects": ["decide", "inject"] }` |
| supervision semantics inherited | ADR-0002 (fail-closed), ADR-0004 d5 (timeout/wedge/escalation classification) |
| reload mechanism inherited | `ReloadingHarnessRegistry` + ADR-0004 d1 amendment (per-file mtime+size stat) |
| the seam it rides | item 12 — PreToolUse live at ~7ms, zero handlers today |
