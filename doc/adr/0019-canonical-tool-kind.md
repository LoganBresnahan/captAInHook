# ADR-0019 — Canonical tool kind: the harness names the tool, the spec says what it is, the engine tells the payload

**Status:** **Rejected 2026-10-12** — before any of it was built. The
incident it answers was a payload that filtered on a host's tool name before
looking at what the call did; the fix is to stop filtering on the name, not
to teach the engine every host's names. `deploy-guard.sh` now inspects every
tool call's content (the live path + a mutation verb) whatever the host calls
the tool, pinned by `DeployGuardTests` (a `Bash`, a `run_shell` and an
unknown tool name all `ask` on the same mutation). The engine stays
tool-name-blind, which is the ADR-0010 posture this ADR set out to amend.
The three other payloads that match on names (`doc-pointer.sh`,
`starter-decide.sh`, `starter-mail-observer.sh`) are not gates: under a host
whose names they do not know they contribute nothing, which is their
fail-open default, not an incident. **What this leaves:** the guard's verb
list is shell-shaped, so a file-editing tool writing into the live tree is
still allowed, as it always was under Claude Code too. **Revisit** when a
payload is a *gate* that must know a call's class and cannot read it off the
call's content; ADR-0005's un-deferral is the likely one. The body below is
the design as proposed, kept as the record of what was weighed.
**Date:** 2026-08-26
**Builds on:** [ADR-0003](0003-declarative-harness-registry.md) (the harness
spec: *declare capabilities in data, provide lookup in code*; its "second real
harness" revisit trigger — this ADR is that trigger firing one level below
event names), [ADR-0010](0010-exec-handlers.md) (decision 5 — the stripped
child environment; the exec-wire envelope), [ADR-0017](0017-watcher-nudge-and-ask.md)
(slice `turn-provenance` — the environment as the one input to a child the
model did not compose), [ADR-0005](0005-pretooluse-policy-gate.md) (deferred;
its rules are keyed on Claude's tool names and inherit this decision).
**Evidence:** a moby session on 2026-08-26 running the shipped
`deploy-guard.sh` under a second harness: every `PreToolUse` answered `allow`,
including a shell command mutating `~/.captainHook/bin`, because the guard's
first gate is `case $envelope in *'"tool_name":"Bash"'*)` and moby's shell tool
is `run_shell`.

## Context

The engine never looks at a tool name. The shim forwards stdin verbatim, the
daemon parses the three fields the spec declares (`eventNameField`,
`sessionIdField`, `cwdField`) and hands every handler the raw payload
untouched. That is by design — ADR-0010 made payloads user processes that
read the host's envelope themselves — and it means every payload that cares
*which tool* a `PreToolUse`/`PostToolUse` is about has to know the host's
name for it.

Four shipped payloads do exactly that today, by substring on the raw line:
`deploy-guard.sh`, `doc-pointer.sh`, `starter-decide.sh`,
`starter-mail-observer.sh`. All four spell it `"tool_name":"Bash"`. That is
Claude Code's vocabulary: its shell is `Bash`, its editor `Edit`, an MCP tool
`mcp__<server>__<tool>`. Moby's are `run_shell`, `edit_file`,
`mcp__pharos__hover`. ADR-0003 named this gap for *event* names and chose not
to design the neutral vocabulary speculatively; tool names are one level
deeper and were never named at all.

What makes this worth an ADR rather than a four-line patch is the failure
shape. `deploy-guard.sh` is a policy gate. Under moby it ran, exited 0, and
answered `allow` for a command it exists to stop — not because it decided
anything, but because its first check ("is this the shell?") could not see
the shell and fell through to the *not-my-business* branch. The trail shows a
guard that saw and approved. This codebase treats a control that cannot see
reading as a control that saw as the worst available outcome (ADR-0017's
N-series, ADR-0018 d6: a corpse is detected, never assumed alive), and a
third harness would reproduce it exactly.

Three ways out were weighed on the bus that day:

1. **Every payload learns every host's name** — match `Bash` *or*
   `run_shell`. Four edits today; every future payload and every future
   harness pays it again, and nothing stops the next payload author from
   spelling one name.
2. **The harness spec maps names to a canonical kind, and the engine hands
   the kind to the payload** — data in the spec, a closed vocabulary in code,
   one lookup, every payload benefits. ADR-0003's house pattern.
3. **The second harness lies** — moby calls its shell `Bash`. Refused: the
   same blind-control shape from the other side, now baked into a host's wire
   format for the benefit of one payload.

(2) is this ADR. (1) is what ships *meanwhile*, in the same slice as the
default-direction fix below, because the guard is blind on the live tree today.

## Decision

1. **The spec's `request` block grows a tool-name field and a kind map.**

   ```json
   "request": {
     "eventNameField": "hook_event_name",
     "sessionIdField": "session_id",
     "cwdField": "cwd",
     "toolNameField": "tool_name",
     "toolKinds": { "shell": ["Bash"], "edit": ["Edit", "Write", "MultiEdit"],
                    "read": ["Read"], "search": ["Grep", "Glob"],
                    "web": ["WebFetch", "WebSearch"], "agent": ["Agent", "Task"] }
   }
   ```

   `toolNameField` defaults to `tool_name` (Claude's, like its three siblings).
   `toolKinds` is a map from a **kind** to the host's names for it. The kinds
   are a CLOSED set in code — `ToolKinds.Known` — on the `KnownEffectKinds` /
   `ResponseAdapters.Known` precedent: data selects among a coded vocabulary,
   it never grows one. A spec naming an unknown kind is a violation
   (embedded → build defect, override → warned-and-skipped, ADR-0003's
   rule); a host name listed under two kinds is a violation. One additional
   rule, the `mcp` kind: it is not listed, it is **derived** — a name matching
   `mcp__<server>__<tool>` is kind `mcp` on every harness, because that
   prefix is the MCP convention rather than any host's, and a spec may not
   list a name under `mcp` (it would be declaring a convention as if it were
   local).

   The v1 kind set: `shell`, `edit`, `read`, `search`, `web`, `agent`, `mcp`.
   Deliberately coarse — a kind answers *what class of thing is this*, the
   question a gate asks first; it does not try to be a capability model
   (item 15's, not this ADR's).

2. **The engine resolves the kind where the spec and the payload first
   meet, and carries it on the event.** `Harness.ParseEvent` — the one site
   holding both the `HarnessSpec` and the raw `JsonElement` — reads
   `toolNameField` and looks the value up. `HookEvent` gains two optional
   members, `ToolName` (the host's string, verbatim) and `ToolKind` (the
   resolved kind, or null). Null means one of three true things: the event
   has no tool field (a `UserPromptSubmit`), the name is not in the map, or
   the spec declares no map. Those are not distinguished on the event; the
   trail distinguishes them (d5).

3. **The seam to the payload is the ENVIRONMENT, not the envelope.** The
   spawner stamps `CAPTAINHOOK_TOOL_KIND=<kind>` and
   `CAPTAINHOOK_TOOL_NAME=<name>` into the child's environment when, and only
   when, the event carries them. An absent kind stamps NOTHING — not an empty
   string (the `MailSenderDeclaration` rule: a blank value is not a
   declaration, and `[ -z "$CAPTAINHOOK_TOOL_KIND" ]` must mean *unresolved*
   in exactly one way).

   Why the environment and not a field in the exec-wire envelope, which the
   engine already composes (`{"v":1,"dispatchId":…,"event":{…}}`):

   - It is the channel the model did not write. The envelope's `payload` is
     the host's bytes, which on some harnesses a model composed; the
     environment is what the child *inherited* — the same reasoning that put
     `CAPTAINHOOK_MAIL_FROM_*` there (ADR-0017 `turn-provenance`).
   - It reaches the payloads that exist. The shipped guards are `sh` doing
     substring matches because a JSON parser is not a given in a hook's
     PATH; `[ "$CAPTAINHOOK_TOOL_KIND" = shell ]` costs nothing, while a
     wrapper field would need either a `v` bump every codec re-learns or a
     regex over the wrapper that is the very fragility this ADR removes.
   - It leaves the exec-wire contract untouched. `v` stays 1; a payload
     written before this ADR sees an identical envelope. The environment is
     already an allowlisted, engine-owned surface (ADR-0010 d5), so two new
     engine-owned names there are an addition to an existing promise, not a
     new one.

   The two names join `ApplyEnvAllowlist`'s engine-owned set with the
   precedence every other engine stamp has: applied AFTER the entry's
   literal `env{}`, so a `handlers.json` entry cannot forge a kind —
   and an `env{}` map naming either variable is refused at parse rather
   than silently overwritten, a NEW rule (today nothing in `env{}` is
   reserved; `CAPTAINHOOK_MAIL_FROM_*` is exported by the payload itself,
   not by the entry, so it never needed one). Resident payloads
   (ADR-0010's long-lived mode) get their environment once at spawn and so
   cannot read a per-dispatch kind from it; for them the kind rides the
   envelope's `event` object as `toolKind`/`toolName` — the same two values,
   optional, additive, no `v` bump — and the stamp on `HookEvent` (d2) is
   what makes the two renderings agree by construction.

4. **A gate that cannot classify does not approve.** This is the half of the
   fix the mapping cannot deliver. `deploy-guard.sh`'s first gate becomes:
   kind `shell` → inspect; kind present and not `shell` → *not my business*,
   allow; kind ABSENT → fall back to the raw-name match (`Bash`, `run_shell`
   — the meanwhile list, as a `DEPLOY_GUARD_SHELL_TOOLS` env default so an
   operator can extend it without editing the script), and if *that* also
   fails to classify, answer `ask`, never `allow`, with a reason that says
   the guard could not tell what tool this was. A false `ask` costs one
   confirmation; a false `allow` is the incident. The starter payloads that
   are not gates keep `allow`-on-unknown — they contribute nothing rather
   than nag — but read the kind first and say so in a comment.

5. **The resolution is observable.** `dispatch.start` gains `toolName` and
   `toolKind` in its data (both nullable). An event whose tool name is
   present but unmapped logs one `harness.toolKindUnmapped` warning per
   (harness, name) per daemon lifetime — the same once-per-key discipline as
   the capability gate's downgrade warning — so an operator adding a harness
   learns every name they forgot to map from the trail, not from a guard
   that quietly went blind.

6. **The vocabulary is Claude-shaped and says so.** As ADR-0003 did for event
   names: the kinds are derived from what Claude Code and moby actually have,
   not from a taxonomy. A third harness that has a tool no kind fits leaves
   it unmapped (d2's null, d5's warning) rather than forcing it; two real
   misfits are the trigger to grow `ToolKinds.Known`, one is not.

## Rejected alternatives

| Option | Why not |
| --- | --- |
| Per-payload name list only (option 1 forever) | Every payload re-learns every harness; a payload that spells one name is blind on the next host and nothing in the engine can tell. Ships *meanwhile* only, inside d4's fallback. |
| Rewrite the host's `tool_name` in the payload to the canonical name | Payloads would read a field the host never wrote, under the host's own key; a payload built against the host's docs would be wrong under captAInHook. The raw bytes stay raw (ADR-0010). |
| A field in the exec-wire wrapper only, no env var | Reaches resident payloads and nothing else cheaply: the oneshot `sh` guards would need a JSON parser or a second regex. Kept as the resident-mode rendering (d3), not the primary seam. |
| An open kind vocabulary (specs may name any kind) | ADR-0003's template-language trap one level down: two harnesses would mint `shell` and `bash` and every payload would match both. Closed set; grow it by code. |
| Moby calls its shell `Bash` | A host lying about its own tool to satisfy one consumer is the blind-control shape mirrored, and it would have to lie about every tool. |
| Defer until a third harness | The guard is blind on the live tree *today*, and ADR-0003 already spent its one deferral on this exact question. |

## Consequences

### Positive

- One JSON override per harness maps every tool for every payload; a
  payload written against `CAPTAINHOOK_TOOL_KIND` works on a harness its
  author never saw.
- The exec-wire `v` does not move. Existing payloads see byte-identical
  envelopes; the new information is additive on both seams.
- A blind gate is now a *loud* gate: unmapped names surface as a warning in
  the trail and as `ask` on the one payload where allow-by-default was the
  incident.
- ADR-0005's deferred rule shape (`"tool": "Bash"`) has a canonical key to
  match on when it is wanted — `kind: shell` instead of a host's name.

### Negative

- **Two spellings of the kind** (env var for oneshot, envelope field for
  resident). Pinned equal by construction (both read `HookEvent.ToolKind`)
  and by one test; still two places a reader has to know.
- **The kind set is Claude/moby-shaped**, as the event vocabulary is. A host
  with a tool that is genuinely two kinds (a "run this and edit that" tool)
  gets one or none.
- **Null is three things** (no field, unmapped, no map) on the event; only
  the trail tells them apart. Accepted: a payload's decision is the same in
  all three cases — it does not know — and folding a reason into the value
  would be a second enum every payload must handle.
- **Stated laundering cost:** a `handlers.json` `passEnv` naming
  `CAPTAINHOOK_TOOL_KIND` passes the *daemon's* value, which is never set, so
  it is inert; but a wrapper script between the engine and the payload
  (bosun is not one; a user's is) can export anything. The kind is
  provenance for an honest payload, not a trust boundary — ADR-0017 N1's
  sentence, unchanged.
- The four shipped payloads carry a raw-name fallback list for as long as
  the kind can be absent, which is forever. Bounded: the list is the
  fallback, not the gate, and a host that is mapped never reaches it.

## Implementation plan

Two slices, the first shippable alone.

| # | slice | model | effort | verify |
| --- | --- | --- | --- | --- |
| 1 | `guard-does-not-approve-blind` — d4 on the four shipped payloads: read `CAPTAINHOOK_TOOL_KIND` first, raw-name fallback (`Bash`, `run_shell`) behind `DEPLOY_GUARD_SHELL_TOOLS`, `ask` when the guard cannot classify; comment in the three non-gates. No engine change — the env var is simply unset today, so the fallback is the whole path. | opus | low | `DeployGuardTests` (real process, the `TurnPayloadTests` shape): a `Bash` envelope on the live path → `ask`; a `run_shell` envelope on the live path → `ask`; an envelope with an unknown tool name touching the live path → `ask` with the could-not-classify reason; `CAPTAINHOOK_TOOL_KIND=edit` exported + a `Bash` string in the body → `allow` (the kind wins over the substring); `CAPTAINHOOK_TOOL_KIND=shell` + no recognisable name → inspected. Land ALONE and deploy — it closes the live hole. |
| 2 | `tool-kind-seam` — d1/d2/d3/d5: `ToolKinds.Known`, `HarnessRequestSpec` gains `ToolNameField` + `ToolKinds` with the violations listed in d1; `ParseEvent` resolves; `HookEvent.ToolName`/`ToolKind`; `ApplyEnvAllowlist` stamps both after `env{}`, refuses them in `env{}` at parse; resident envelope gains the two fields; `dispatch.start` data; the once-per-key warning; embedded `claude-code.json` gains the map; `harnesses/README` or the flow doc gets the kind table. Regenerate the exec-wire golden if the resident rendering is pinned. | opus | medium | **Verify (adversarial):** the spec violations (unknown kind, one name under two kinds, a name listed under `mcp`) each refuse with the named error; embedded parse still succeeds (a violation there is a build defect, so the test IS the gate); `mcp__x__y` resolves `mcp` on a spec with no map at all; a `handlers.json` `env{"CAPTAINHOOK_TOOL_KIND":…}` is refused at parse; an unset kind stamps NO variable (assert the key is absent, not empty); oneshot env and resident envelope agree on a dispatch that carries both; a `UserPromptSubmit` stamps nothing and logs nothing. Then re-run slice 1's tests with the daemon stamping — the fallback path must no longer be reached on a mapped `Bash`. Suite green twice. |

Slice 2 flips this ADR to Accepted. A moby override spec
(`~/.captainHook/harnesses/moby.json`) is the maintainer's file, not the
repo's, until moby is a harness this repo tests against — that is ADR-0003's
"second real harness lands" trigger, and it fires on the event vocabulary,
not here.

## Ground truth

| what | where |
| --- | --- |
| kind vocabulary (`ToolKinds.Known`), `mcp` derivation | `dotnet/captainHook/Core/Harness.cs` |
| spec fields `toolNameField` / `toolKinds`, violations | `HarnessSpec.TryParse`, `HarnessRequestSpec` |
| resolution site | `Harness.ParseEvent` |
| event members | `HookEvent.ToolName`, `HookEvent.ToolKind` (`Core/Model.cs`) |
| env stamp + `env{}` refusal | `ExecHandler.ApplyEnvAllowlist`, `ExecHandlersFile` parse |
| resident rendering | `ExecWire` envelope `event.toolKind` / `event.toolName` |
| trail | `dispatch.start` data `toolName`/`toolKind`; `harness.toolKindUnmapped` |
| the gate | `examples/payloads/deploy-guard.sh` (d4) |
| tests | `DeployGuardTests` (slice 1), `HarnessSpecTests` / `ExecHandlerTests` additions (slice 2) |

## Revisit triggers

- **A third real harness** with a tool no v1 kind fits — grow `ToolKinds.Known`
  from three data points, as d6 says; do not grow it from one.
- **ADR-0005 is un-deferred** — its rule key becomes `kind` with `tool` as the
  host-specific escape hatch, and its matching reads `HookEvent.ToolKind`
  rather than the payload.
- **Item 15 (egress policy) wants a capability model** — the kind is the
  wrong shape for it (coarse, single-valued); design the capability set
  there and consider whether the kind collapses into it.
- **A payload needs the kind and the name to disagree** (a host whose shell
  tool is also its editor) — the single-kind assumption (Negative 2) is what
  breaks; that is the moment for a kind *set*, and it moves the env var to a
  delimited list.
