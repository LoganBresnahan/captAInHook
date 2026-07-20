# ADR-0011 — Hook trust model: same-user install operations

**Status:** Accepted *(2026-07-19; drafted from and reviewed in the owner
discussion of the same date)*
**Date:** 2026-07-19

## Context

Roadmap item 10. Installing a hook is installing arbitrary code that runs on
every prompt. Today the only installer is the owner hand-editing
`~/.captainHook/handlers.json` — the trust decision is implicit in typing the
config yourself. The product vision ("browse, one-click install, watch it run
live") needs install *operations*, and four ADRs have parked obligations here
while the surface didn't exist:

- **ADR-0006 N1** — "who may write policy" beyond same-user.
- **ADR-0007** — install/uninstall endpoints deferred ("writing settings.json
  = installing arbitrary code"); revisit triggers say extend the write surface
  *with* the trust model, one ADR together, and re-examine auth's org story
  with it.
- **ADR-0008** — GUI control verbs (catalog, one-click install) deferred on
  this item's write-authz.
- **ADR-0010** — made the item concrete: installing = a command line + a
  `handlers.json` entry the GUI can show verbatim; its trust-model trigger
  also gates item 15's sandboxing half.

The security posture this ADR ratifies (probed live 2026-07-19, all modes
verified on disk):

| Surface | Defense | Against whom |
| --- | --- | --- |
| Hook path (UDS) | `0700` `$XDG_RUNTIME_DIR/captainHook/` + `0600` socket/lock/pid | other local users — kernel-enforced |
| TCP API :4665 | loopback bind + `0600` api.json bearer token (constant-time) + Host/Origin gate | other local users, browser tabs, DNS rebind |
| Config files | **nothing, deliberately** — `0644` user files | nobody: whoever writes your `$HOME` is already you |

The fork this ADR exists to close: item 10 read either as **local install
UX** (small, completes the vision for local hooks) or as a **distribution
platform** (provenance, signing, sandboxing untrusted code, org policy
distribution — a second product). Same-date owner decisions already narrowed
the field: items 7 and 11 dropped, .NET core + browser GUI only, universal
payload seam confirmed correct.

## Decision

1. **The trust boundary, ratified: the installer is the machine's own user,
   installing things they can read.** There is no privilege boundary against
   your own uid and this ADR does not invent one: a payload runs *as you*, and
   you can also kill the daemon, swap its binaries, or edit any config —
   captAInHook answers that with **resilience, not prevention** (at-most-once,
   collapsed fallback, doctor, respawn; chaos-tested under ADR-0004). Every
   future surface addition is measured against this recorded boundary.

2. **Consent is the threat model.** With same-user ratified, the only v1
   threat is *"I clicked install on something I didn't actually read."* The
   answer is UX, not crypto: before any write, the GUI shows the entry
   **verbatim and resolved** — command (absolute path), args, events, mode,
   fail mode, budget, env/passEnv, cwd — and requires an explicit confirm.
   Show-what-will-execute-before-touching-anything is the whole trust
   surface. We show the *entry*, not the script's contents: reading the
   script is the user's job on their own machine (see N2).

3. **One new write verb: `PUT /api/v1/handlers` — the whole file, mirroring
   `PUT /policy` exactly.** Strict-parse-before-write with the daemon's own
   `ExecHandlersFile` parser, atomic temp+rename in the target's directory,
   content-hash ETag + `If-Match`, hot reload (`ReloadingHandlers`) makes API
   writes ≡ hand edits on the next dispatch. One deliberate tightening: the
   *write* path refuses a file containing any entry that registration would
   warn-and-skip — hand edits stay entry-lenient (d4's split, unchanged), but
   the API never *installs* a known-broken entry. Install/uninstall/edit are
   GUI-side operations over the fetched file, PUT back whole; no entry-level
   endpoints (ADR-0007's minimal-surface discipline).

4. **Enable/disable needs no new machinery — it already shipped.** Disabling
   an installed handler is a `dispatch.json` handler rule (ADR-0006, layer 1),
   written through the existing `PUT /policy`. The GUI composes the two files;
   the engine changes not at all.

5. **v1 does not write `~/.claude/settings.json`.** The harness's config file
   holds unrelated user config, and hook wiring is per-*event*-per-machine
   (one-time), not per-payload — installing a payload edits `handlers.json`
   only, into already-wired events. For an unwired event the GUI shows the
   exact hook-command line to add (from the harness spec's `install` data)
   for the user to apply by hand. Auto-wiring earns its way in on observed
   first-run friction (the ADR-0006 d7 discipline), as an explicit verb with
   the same verbatim-confirm — never as a side effect of installing a
   payload. Carry-in: the embedded claude-code spec's `install.entry.command`
   still reads `{dotnet} {captainHookDll}` — stale since the shim (item 12);
   it must say `{captainShim} hook {event-kebab}` before anything renders it.

6. **Write-authz, ratified as-is.** The per-daemon bearer token *is* the
   trust model: holding it proves you can read the owner's `0600` api.json,
   so API auth reduces to filesystem permissions — the same boundary hand
   edits live behind. No new auth, no roles, no TLS. ADR-0007's "org story"
   revisit stays declined until a fleet exists (local data plane / central
   control plane, per the roadmap's fleet note).

7. **Not triggered, explicitly: sandboxing and provenance.** Item 15's
   enforcement half (namespaces, seccomp, cgroups, network deny) and any
   provenance machinery (content-hash pinning, signing) stay parked. The
   recorded trigger: **the first surface that puts code the user cannot read
   onto the machine** — a community registry, a shared catalog, any fetch —
   fires both, behind a new ADR, before that surface ships. Until then the
   env allowlist (ADR-0010 d5) is the containment story, and the registry
   stays in the parking lot.

## Rejected

| Alternative | Why not |
| --- | --- |
| Distribution-platform scope (provenance, signing, org distribution) | A second product; no consumer exists; every mechanism is decidable later behind d7's trigger without rework |
| Entry-level install/uninstall endpoints | Whole-file PUT + ETag already gives atomic read-modify-write; N endpoints for one file grows surface without power |
| Auto-editing settings.json on install | Foreign config file, unrelated keys, per-event not per-payload; a targeted-edit bug bricks the user's harness config — friction must be observed first |
| Daemon under a separate uid (real privilege boundary) | Multi-user machinery for a single-user tool; breaks the file-permission auth story and the spawn-on-demand lifecycle |
| Password/TLS on the API | Loopback + token-proves-file-access already reduces to the OS boundary; TLS on 127.0.0.1 defends against nothing here |

## Consequences

### Positive

- Item 10 shrinks to a small, buildable surface: one API verb, one GUI flow,
  one doc. The engine's write/reload/validate machinery all exists.
- The product vision's install half completes for local hooks — and the
  same-user boundary is now *written down*, so scope creep toward "platform"
  has to argue with a document.
- Enable/disable falls out of shipped layers for free (d4).

### Negative

- **N1 · Whole-file PUT is coarse.** Two concurrent editors race at file
  granularity; `If-Match` turns the race into an honest 412, but the loser
  re-merges by hand. Acceptable at single-user scale by construction.
- **N2 · Verbatim shows the command, not the script.** A user can still
  install `~/scripts/innocent.sh` without reading it. Same-user boundary says
  that's their right; the GUI shows the resolved absolute path so *what to
  read* is never ambiguous. Revisit only via d7's trigger.
- **N3 · Unwired events are silent.** An entry installed for an event the
  harness config never wired registers and never fires.
  Expected-vs-registered (item 9 phase 8) shows it registered; nothing today
  shows it unwired. Mitigation: the GUI's wiring hint (d5); a read-only
  settings.json wiring *check* is admissible later without violating d5 —
  reading is not writing.
- **N4 · The stale `install` template is now load-bearing** (d5 carry-in) —
  it must be corrected in the same change that first consumes it, with a
  spec-pin test.

## Implementation plan

*Decomposed via `/adr-plan` 2026-07-19: 10 slices → 5 phases. Critical path
`handlers-put-endpoint → handlers-etag-ifmatch → gui-handlers-editor →
gui-verbatim-confirm → docs-update`. Adversarial verify on exactly three
slices; **no ultracode** — every slice is single-file/single-flow work.*

**Phase 1 — server write seam + free-standing independents.**
- `handlers-put-endpoint` *(medium/moderate, verify)* — `PUT /api/v1/handlers`
  mirroring `ApiPolicyWriter` (sibling-temp same-dir rename, closed
  outcome→HTTP mapping, BOM discipline); the one new seam is
  `ExecHandlersFile`'s tri-state + `Skipped` shape vs `DispatchPolicy`'s
  all-or-nothing. `handlersPath` is already threaded. Verify = one
  end-to-end PUT-then-dispatch drive.
- `install-template-fix` *(low/mechanical)* — claude-code.json
  `install.entry.command` → `{captainShim} hook {event-kebab}` + a spec-pin
  test (N4). No upstream deps; must land before/with its first renderer.
- `gui-enable-disable` *(medium/moderate, low risk)* — composes shipped
  layers only (d4): toggle = a **prepended** unconditional handler-level deny
  rule in dispatch.json via the existing `PUT /policy`; must classify
  scoped-vs-unconditional deny rules for the badge and read-modify-write
  the rules array without clobbering hand-written entries; vitest on the
  pure compose/toggle functions.

**Phase 2 — harden the writer** *(one commit, or folded into phase 1's
endpoint commit — never left half-specified between commits).*
- `handlers-install-strictness` *(low/mechanical)* — non-empty `Skipped` ⇒
  422 with violations, **in the writer** so registration-time leniency
  (ADR-0010 d4) is untouched by construction.
- `handlers-etag-ifmatch` *(low/mechanical)* — content-hash ETag on
  `GET /handlers` (+ `raw`/`etag` on `HandlersDto`), If-Match on the PUT;
  near-copy of the solved RFC 7232 pattern.

**Phase 3 — pin the server + build the GUI editor (parallel tracks).**
- `handlers-write-adversarial-tests` *(medium/moderate, verify — the ADR's
  mandated pass)* — mirror `ApiPolicyWriteTests`; two vacuous-pass traps:
  the atomicity test must genuinely interleave the stat-gated resolver with
  writes, and the hot-reload test must assert NEXT-dispatch (not eventual)
  visibility through a real daemon.
- `gui-handlers-editor` *(medium/moderate, verify)* — whole-file
  read-modify-write over GET+ETag / PUT+If-Match. The sharp edge: 412
  handling must **invert** PolicyPanel's pin 3 — a composed editor must
  stop-and-re-merge, never adopt-current-and-retry (the lost-update bug a
  pattern-copying pass would ship; N1).

**Phase 4 — GUI trust layer** *(both layer on the editor; batchable).*
- `gui-verbatim-confirm` *(medium/moderate)* — the d2 modal: entry rendered
  verbatim and resolved before the PUT. Open design point to settle FIRST
  (so phase 3's tests can pin any new field): `ExecHandler` passes `Command`
  raw to `ProcessStartInfo` and no DTO exposes command fields — either the
  server grows a resolved-path field or the editor mandates absolute paths.
- `gui-wiring-hint` *(medium/moderate)* — d5's shown-not-written hook
  command for unwired events, rendered from the (now-fixed) install
  template; template substitution stays data-selects-code. First consumer
  of `install-template-fix`.

**Phase 5 — capstone.**
- `docs-update` *(low/mechanical)* — management-api + management-gui flow
  docs, roadmap item 10's box checked in the same commit, Ground truth
  back-filled. Terminal by construction.

## Ground truth

*Back-filled 2026-07-19 at the capstone (ADR-0008 precedent) — decision → code.*

| decision | lives in |
|---|---|
| d1 same-user boundary | This document + [management-api.md](../flow/management-api.md) § Auth (the token-proves-file-access reduction) — deliberately no new code |
| d2 verbatim-and-resolved confirm | `web/src/HandlersEditor.tsx` (`ConfirmModal`, `VerbatimEntry`); "resolved" = the absolute-path mandate in `web/src/handlers.ts` `formProblems` |
| d3 `PUT /api/v1/handlers` | `dotnet/captainHook/Api/ApiHandlersWriter.cs` (+ `HandlersWriteOutcome`), routed in `ApiHost.ServeHandlersPutAsync`, wired in `DaemonHost`; skip-refusal pinned by the contrast test in `ApiHandlersWriteTests` |
| d3 ETag chain | `HandlersDto.Raw/Etag` (`ApiDtos.cs`, `ApiReadModel.Handlers`), `GET /handlers` ETag header; client discipline + the 412 INVERSION in `web/src/handlers.ts` (`submitHandlers` → tagless `conflict`) |
| d4 enable/disable from shipped layers | `web/src/handlers.ts` (`togglePolicyText` prepend-deny, `disabledState` incl. "scoped"), zero engine change |
| d5 wiring hint shown-not-written | `web/src/handlers.ts` (`wiringHint`, `kebab`), `StatusDto.ShimPath` (`ApiReadModel.ResolveShimPath`), the corrected `{captainShim}` template in `harnesses/claude-code.json` (spec-pinned, N4) |
| d6 write-authz as-is | No code — the existing `ApiAuthGate` covers the new verb by construction (pinned: 401-without-token test) |
| d7 sandboxing/provenance un-triggered | No code; the trigger text in this document |
| tests | `ApiHandlersWriteTests` (17: mapping, atomicity interleave, both-directions next-dispatch daemon E2E), `HarnessTests` install-template pin, `web/src/handlers.test.ts` (unit), `web/e2e/handlers.spec.ts` (4 E2E: install-confirm-wiring-register, 422-in-modal, 412-stops-nothing-clobbered, uninstall+toggle) |
| flow docs | [management-api.md](../flow/management-api.md) § The writes; [management-gui.md](../flow/management-gui.md) § The handlers editor |
