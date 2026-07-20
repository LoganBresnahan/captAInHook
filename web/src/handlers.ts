import type { HandlersDto } from "./api.gen.ts";

// gui-handlers-editor + gui-enable-disable (ADR-0011 d3/d4): the pure logic
// under the handlers section — parse/compose/serialize for the whole-file
// read-modify-write, the PUT client, the dispatch.json toggle compose, and the
// wiring-hint substitution. Pure and side-effect-free so node:test pins it
// without a browser (the policy.ts precedent).
//
// The 412 discipline INVERTS PolicyPanel's pin 3 (the plan's named sharp
// edge): a raw-text editor may adopt `current` and retry — overwriting a SEEN
// conflict is the user's call on text they are looking at. A COMPOSED
// read-modify-write that did the same would silently clobber the other
// editor's entries (the classic lost update: our compose was built over a
// stale file). So on mismatch the PANEL must stop, re-GET, and re-compose over
// the fresh file — submitHandlers reports the conflict; it never hands back a
// tag to blindly retry with.

// ---- entry model ------------------------------------------------------------

/** One handlers.json entry, exactly the parser's field set (ExecHandlersFile.
 * KnownEntryFields) — no extra fields can exist in a valid file (strict
 * parse), so this shape is lossless for compose. */
export type ExecEntry = {
  name: string;
  command: string;
  args?: string[];
  events: string[];
  mode?: "oneshot" | "resident";
  failMode?: "open" | "closed";
  budgetMs?: number;
  readinessTimeoutMs?: number;
  env?: Record<string, string>;
  passEnv?: string[];
  cwd?: string;
};

/** Parse the server's `raw` into entries, or null when there is nothing usable
 * to compose over (absent file, or malformed — the caller then starts from
 * empty or surfaces the malformed state; composing over a file we cannot
 * parse would destroy it). */
export function parseEntries(raw: string | null): ExecEntry[] | null {
  if (raw === null) return null;
  try {
    const doc = JSON.parse(raw) as { version?: number; handlers?: unknown };
    if (doc.version !== 1 || !Array.isArray(doc.handlers)) return null;
    // Defensive per-element check: the daemon's parser warns-and-skips a
    // non-object entry (the file stays "loaded"), so this function must not
    // hand the render a null/number that crashes the boundary-less GUI
    // (adversarial-verify F1). A file with any such entry is not composable —
    // whole-file replace would silently drop what we cannot represent.
    return doc.handlers.every((h) => typeof h === "object" && h !== null
        && typeof (h as ExecEntry).name === "string")
      ? doc.handlers as ExecEntry[]
      : null;
  } catch {
    return null;
  }
}

/** Serialize entries into the canonical file text the PUT installs. Two-space
 * pretty JSON — a GUI write re-formats the file (whole-file replace is the d3
 * contract; hand formatting is not preserved, hand CONTENT is). */
export function serializeEntries(entries: ExecEntry[]): string {
  return JSON.stringify({ version: 1, handlers: entries }, null, 2) + "\n";
}

/** Add or replace one entry by name. `originalName` names the entry being
 * edited (a rename replaces it in place, preserving file order); null = a new
 * install, appended. */
export function upsertEntry(
  entries: ExecEntry[], entry: ExecEntry, originalName: string | null,
): ExecEntry[] {
  if (originalName !== null && entries.some((e) => e.name === originalName)) {
    return entries.map((e) => (e.name === originalName ? entry : e));
  }
  return [...entries, entry];
}

export function removeEntry(entries: ExecEntry[], name: string): ExecEntry[] {
  return entries.filter((e) => e.name !== name);
}

/** Client-side form hints ONLY — the daemon's strict parser (422) stays the
 * authority. The one real rule here is ADR-0011 d2's "resolved": the GUI
 * mandates an ABSOLUTE command path so the verbatim confirm shows exactly the
 * file that will execute, with no PATH ambiguity (hand edits stay free to use
 * PATH lookup). */
export function formProblems(e: ExecEntry): string[] {
  const problems: string[] = [];
  if (!e.name.trim()) problems.push("name is required");
  if (e.name.includes("#")) problems.push("name must not contain '#'");
  if (!e.command.trim()) problems.push("command is required");
  else if (!e.command.startsWith("/"))
    problems.push("command must be an absolute path (the confirm panel shows exactly what will run)");
  if (e.events.length === 0) problems.push("at least one event is required");
  return problems;
}

// ---- the PUT client ---------------------------------------------------------

export type HandlersVerdict =
  | { kind: "written"; etag: string | null; dto: HandlersDto | null }
  | { kind: "invalid"; violations: string[] }        // 422 — includes d3's skip-refusal
  | { kind: "conflict" }                              // 412 — STOP: re-GET and re-compose (never retry the stale compose)
  | { kind: "failed"; detail: string };

export async function submitHandlers(
  fetchFn: (path: string, init?: RequestInit) => Promise<Response>,
  body: string,
  etag: string | null,
): Promise<HandlersVerdict> {
  let resp: Response;
  try {
    resp = await fetchFn("/api/v1/handlers", {
      method: "PUT",
      headers: {
        "Content-Type": "application/json",
        // null etag (absent file) sends no If-Match — the PUT is a create.
        // Never send "" — the server ignores an empty If-Match (the policy
        // editor's adversarial-verify lesson) and "" would pretend to protect.
        ...(etag !== null ? { "If-Match": etag } : {}),
      },
      body,
    });
  } catch (e) {
    return { kind: "failed", detail: e instanceof Error ? e.message : String(e) };
  }

  try {
    switch (resp.status) {
      case 200: {
        const dto = (await resp.json()) as HandlersDto;
        return { kind: "written", etag: resp.headers.get("ETag") ?? dto.etag ?? null, dto };
      }
      case 422: {
        const b = (await resp.json()) as { violations?: string[] };
        return { kind: "invalid", violations: b.violations ?? [] };
      }
      case 412:
        // Deliberately DISCARD `current`: adopting it invites retrying a
        // compose built over a file that has since changed — the lost update.
        // The caller re-GETs and re-composes over what is actually there.
        return { kind: "conflict" };
      case 413:
        return { kind: "failed", detail: "handlers file too large" };
      default:
        return { kind: "failed", detail: `HTTP ${resp.status}` };
    }
  } catch (e) {
    return { kind: "failed", detail: e instanceof Error ? e.message : String(e) };
  }
}

// ---- enable/disable via dispatch.json (d4: composed from shipped layers) ----

type PolicyRule = {
  event?: string; handler?: string; project?: string; session?: string;
  decision: string;
};
type PolicyDoc = { version?: number; default?: string; rules?: PolicyRule[] };

/** Is this rule an UNCONDITIONAL handler-level rule for `name` — the only
 * shape the toggle owns? Scoped rules (event/project/session) are someone's
 * deliberate policy; the toggle never claims or removes them. */
function isToggleRule(r: PolicyRule, name: string): boolean {
  return r.handler === name && r.event === undefined
    && r.project === undefined && r.session === undefined;
}

/** The badge, on the DAEMON's own first-match-per-handler semantics
 * (DispatchPolicy.Evaluate — scoped rules included): the FIRST rule
 * mentioning `name` decides. Unconditional ⇒ its decision is authoritative in
 * every context ("disabled"/"enabled"); scoped ⇒ the answer is
 * context-dependent and the badge says "scoped" rather than lying in either
 * direction (adversarial-verify F2: an earlier scoped allow really does run
 * the handler on its event, whatever a later unconditional deny says). The
 * GUI's own toggle PREPENDS, so GUI-authored state always classifies
 * unconditionally. */
export function disabledState(
  policyRaw: string | null, name: string,
): "enabled" | "disabled" | "scoped" {
  const doc = parsePolicy(policyRaw);
  if (doc === null) return "enabled";
  for (const r of doc.rules ?? []) {
    if (r.handler !== name) continue;
    if (isToggleRule(r, name)) return r.decision === "deny" ? "disabled" : "enabled";
    return "scoped";
  }
  return "enabled";
}

function parsePolicy(raw: string | null): PolicyDoc | null {
  if (raw === null) return null;
  try {
    const doc = JSON.parse(raw) as PolicyDoc;
    if (typeof doc !== "object" || doc === null) return null;
    // Defensive shape checks (adversarial-verify F1): a valid-JSON policy the
    // daemon classifies malformed (rules not an array, a null rule) must not
    // crash the render — it reads as "no usable rules", and the toggle
    // separately refuses to compose over a malformed policy.
    if (doc.rules !== undefined && (!Array.isArray(doc.rules)
        || doc.rules.some((r) => typeof r !== "object" || r === null))) return null;
    return doc;
  } catch {
    return null;
  }
}

/** Compose the toggle into policy text, or null when the current policy text
 * cannot be parsed (never compose blind over a file we can't read — the
 * malformed state is the daemon's loud deny-all and a GUI write must not
 * silently replace it). Disable PREPENDS the deny rule (first-match-wins: a
 * later hand-written allow must not defeat the toggle); enable removes every
 * unconditional handler-deny for the name and touches nothing else. */
export function togglePolicyText(
  policyRaw: string | null, name: string, disable: boolean,
): string | null {
  const doc = policyRaw === null
    ? { version: 1, default: "allow", rules: [] }
    : parsePolicy(policyRaw);
  if (doc === null) return null;
  const rules = doc.rules ?? [];
  // Both arms remove only toggle-shaped DENIES: disable prepends its deny
  // (position 0 wins every context) and leaves a hand-written unconditional
  // allow in place — inert behind the deny, restored the moment enable
  // removes it. That makes off→on an IDENTITY on hand policy
  // (adversarial-verify F4: the old filter deleted the allow on disable and
  // could never give it back, silently un-shielding a later scoped deny).
  const kept = rules.filter((r) => !(isToggleRule(r, name) && r.decision === "deny"));
  const next = disable ? [{ handler: name, decision: "deny" }, ...kept] : kept;
  return JSON.stringify({ ...doc, version: doc.version ?? 1, rules: next }, null, 2) + "\n";
}

// ---- the wiring hint (d5: shown, never written) -----------------------------

/** PascalCase event → the kebab spelling the hook command takes
 * ("UserPromptSubmit" → "user-prompt-submit"). */
export function kebab(event: string): string {
  return event.replace(/([a-z0-9])([A-Z])/g, "$1-$2").toLowerCase();
}

/** Render the harness install template for one event. `install` is the spec's
 * opaque install block (data-selects-code: we substitute the two known tokens
 * and never grow a template language); shimPath is /status's resolved shim
 * (ADR-0011 d5), with the documented deploy home as the honest fallback. */
export function wiringHint(
  install: unknown, shimPath: string | null, event: string,
): { configFile: string; command: string } | null {
  const i = install as { configFile?: string; entry?: { command?: string } } | null;
  const template = i?.entry?.command;
  if (typeof template !== "string") return null;
  return {
    configFile: i?.configFile ?? "~/.claude/settings.json",
    command: template
      .replace("{captainShim}", shimPath ?? "~/.captainHook/bin/captainShim")
      .replace("{event-kebab}", kebab(event))
      .replace("{event}", event),
  };
}
