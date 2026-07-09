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
