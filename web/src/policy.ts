import type { PolicyDto } from "./api.gen.ts";
import type { PolicyVerdict } from "./store.ts";

// policy-editor-island (ADR-0008 decision 1): the ONE write the GUI exposes,
// riding the finished Phase-6 server path (strict TryParse → 422 with the
// daemon's own violations, If-Match → 412, atomic install → hot reload). The
// client's whole job is the ETag DISCIPLINE — the silent failure mode this
// slice's adversarial verify exists for: the server honors If-Match only when
// SUPPLIED, so a client that forgets the header silently reopens the
// blind-overwrite window with zero visible symptom. The three pins:
//   1. ALWAYS send If-Match when an etag is known (the load's, then see 2/3);
//   2. a 200 hands back the authoritative next etag (header, echoed body) —
//      adopt it, never re-GET for it;
//   3. a 412 hands back `current` — adopt THAT for the retry, so the retry is
//      a deliberate overwrite of a seen conflict, not a blind one.
// An etag of null (no file yet — the absent tri-state) sends no If-Match: the
// PUT is a create, and there is nothing to protect.

export type SubmitResult = { verdict: PolicyVerdict; policy?: PolicyDto };

// ---------------------------------------------------------------------------
// The rule builder (ADR-0015 d4): rows ⇄ the strict JSON dialect.
//
// The hazard this code exists to avoid is silent destruction. A user's
// `dispatch.json` is the daemon's front door; a builder that loads it, drops a
// field it did not understand, and writes the remainder back would leave the
// page looking correct while the daemon quietly enforces something the user
// never wrote — and no screenshot, and no green e2e, would show it.
//
// So representability is decided by ROUND TRIP, not by a checklist of cases:
// `parsePolicyRows` builds rows, re-serializes them, and compares the MEANING
// of the result against the meaning of the input. Anything that fails to match
// — including a shape nobody thought to enumerate here — returns null, and the
// island locks to the raw editor with a notice. The safe answer is always
// "refuse to represent it", never "represent most of it".
//
// The daemon's parser remains the ONLY validator of what is *legal* (its 422
// carries its own violations, and this file never second-guesses them). These
// functions decide only what the builder can faithfully REPRESENT.

export type PolicyDecision = "allow" | "deny";

/** One builder row. Unset criteria are the empty string here and are OMITTED
 * from the JSON — the daemon rejects an empty-string criterion, so `""` must
 * never reach the file. */
export type PolicyRow = {
  event: string;
  handler: string;
  project: string;
  session: string;
  decision: PolicyDecision;
};

export type PolicyRows = { default: PolicyDecision; rules: PolicyRow[] };

export const EMPTY_ROW: PolicyRow = {
  event: "", handler: "", project: "", session: "", decision: "deny",
};

const CRITERIA = ["event", "handler", "project", "session"] as const;
const KNOWN_TOP = new Set(["version", "default", "rules"]);
const KNOWN_RULE = new Set<string>([...CRITERIA, "decision"]);

const isDecision = (v: unknown): v is PolicyDecision => v === "allow" || v === "deny";

/** Rows → the exact strict JSON the daemon accepts, formatted like a
 * hand-written file (2-space indent, trailing newline) because this text is
 * also what the raw toggle shows and what the user's editor will see next. */
export function serializePolicyRows(rows: PolicyRows): string {
  const doc = {
    version: 1,
    default: rows.default,
    rules: rows.rules.map((r) => {
      const rule: Record<string, string> = {};
      for (const c of CRITERIA) if (r[c] !== "") rule[c] = r[c];
      rule.decision = r.decision;
      return rule;
    }),
  };
  return JSON.stringify(doc, null, 2) + "\n";
}

/** Raw JSON → rows, or null when the builder cannot represent it faithfully.
 *
 * `null` input is the ABSENT file (no policy yet): that is representable as the
 * zero-config default — allow everything, no rules — because there is nothing
 * of the user's to lose.
 *
 * Every other refusal means "lock the editor to raw". Note what is NOT checked
 * here: duplicate JSON keys, which `JSON.parse` silently collapses. They need
 * no client detector because the builder is only offered for a file the DAEMON
 * has already accepted (`loaded`) or one that does not exist (`absent`) — a
 * duplicate key makes the file malformed server-side, and malformed always
 * locks to raw. */
export function parsePolicyRows(raw: string | null): PolicyRows | null {
  if (raw === null) return { default: "allow", rules: [] };

  let doc: unknown;
  try {
    doc = JSON.parse(raw);
  } catch {
    return null;
  }
  if (typeof doc !== "object" || doc === null || Array.isArray(doc)) return null;
  const top = doc as Record<string, unknown>;

  for (const key of Object.keys(top)) if (!KNOWN_TOP.has(key)) return null;
  if (top.version !== 1) return null;
  if (top.default !== undefined && !isDecision(top.default)) return null;
  if (top.rules !== undefined && !Array.isArray(top.rules)) return null;

  const rules: PolicyRow[] = [];
  for (const entry of (top.rules as unknown[] | undefined) ?? []) {
    if (typeof entry !== "object" || entry === null || Array.isArray(entry)) return null;
    const rule = entry as Record<string, unknown>;
    for (const key of Object.keys(rule)) if (!KNOWN_RULE.has(key)) return null;
    if (!isDecision(rule.decision)) return null;

    const row: PolicyRow = { ...EMPTY_ROW, decision: rule.decision };
    let criteria = 0;
    for (const c of CRITERIA) {
      const v = rule[c];
      if (v === undefined) continue;
      // Mirror the daemon exactly: it refuses non-strings, empty AND
      // whitespace-only criteria (IsNullOrWhiteSpace), and a lone-surrogate
      // escape (unreadable to System.Text.Json). In /u mode \p{Surrogate}
      // matches only UNPAIRED surrogates — a well-formed pair is one
      // non-surrogate code point — so this is an isWellFormed check without
      // the ES2024 lib. (Skeptic-pass tightening, 2026-08-11.)
      if (typeof v !== "string" || v.trim() === "" || /\p{Surrogate}/u.test(v)) return null;
      row[c] = v;
      criteria++;
    }
    // A criteria-less rule matches everything; the daemon calls it malformed,
    // and a builder row that rendered as "no criteria" would invite exactly the
    // typo it guards against.
    if (criteria === 0) return null;
    rules.push(row);
  }

  const parsedRows: PolicyRows = {
    default: (top.default as PolicyDecision | undefined) ?? "allow",
    rules,
  };

  // The round-trip guard itself. Everything above is what we KNOW to reject;
  // this is what catches what we did not think of: if re-serializing these rows
  // does not mean the same thing as the input, we cannot represent it.
  return sameMeaning(raw, serializePolicyRows(parsedRows)) ? parsedRows : null;
}

/** Do two policy texts mean the same thing to the daemon? `default` absent is
 * allow and `rules` absent is none — those two normalizations are the ONLY
 * differences a faithful round trip may introduce (they are how the dialect is
 * defined, not our invention). Key order and whitespace are not meaning; a
 * different criterion, a dropped field, or an added one is. */
function sameMeaning(a: string, b: string): boolean {
  const canon = (s: string) => {
    const d = JSON.parse(s) as Record<string, unknown>;
    const rules = ((d.rules ?? []) as Record<string, unknown>[]).map((r) => {
      const out: Record<string, unknown> = {};
      for (const k of Object.keys(r).sort()) out[k] = r[k];
      return out;
    });
    return JSON.stringify({ version: d.version, default: d.default ?? "allow", rules });
  };
  try {
    return canon(a) === canon(b);
  } catch {
    return false;
  }
}

export async function submitPolicy(
  fetchFn: (path: string, init?: RequestInit) => Promise<Response>,
  body: string,
  etag: string | null,
): Promise<SubmitResult> {
  let resp: Response;
  try {
    resp = await fetchFn("/api/v1/policy", {
      method: "PUT",
      headers: {
        "Content-Type": "application/json",
        ...(etag !== null ? { "If-Match": etag } : {}),
      },
      body,
    });
  } catch (e) {
    return { verdict: { kind: "failed", detail: e instanceof Error ? e.message : String(e) } };
  }

  try {
    switch (resp.status) {
      case 200: {
        // The PUT echoes the freshly-resolved PolicyDto (one round-trip, no
        // re-GET); the ETag header is the authoritative next If-Match. A 200
        // with NO tag anywhere (a server-contract breach — never today) maps
        // to null so the island re-seeds via GET: an empty string would be
        // WORSE than nothing, because the server silently ignores an empty
        // If-Match (IsNullOrWhiteSpace) — "" pretends to protect and
        // reopens the exact blind-overwrite this discipline exists to close
        // (adversarial-verify finding, 2026-07-09).
        const dto = (await resp.json()) as PolicyDto;
        const next = resp.headers.get("ETag") ?? dto.etag ?? null;
        return { verdict: { kind: "written", etag: next }, policy: dto };
      }
      case 422: {
        const b = (await resp.json()) as { violations?: string[] };
        return { verdict: { kind: "invalid", violations: b.violations ?? [] } };
      }
      case 412: {
        const b = (await resp.json()) as { current?: string | null };
        return { verdict: { kind: "mismatch", current: b.current ?? resp.headers.get("ETag") } };
      }
      case 413:
        return { verdict: { kind: "failed", detail: "policy too large" } };
      default:
        return { verdict: { kind: "failed", detail: `HTTP ${resp.status}` } };
    }
  } catch (e) {
    // A non-JSON body on a status that promises JSON: degrade to failed —
    // submitPolicy never throws (a rejection here would wedge the island's
    // saving flag).
    return { verdict: { kind: "failed", detail: e instanceof Error ? e.message : String(e) } };
  }
}
