import { create } from "zustand";
import type { StatusDto, PolicyDto, HandlersDto, HarnessesDto } from "./api.gen.ts";

// zustand-store (ADR-0008 decision 8): ONE provider-less store outside the
// React tree; each island subscribes to the slice it reads
// (`useStore(s => s.trace)`) and nothing is prop-threaded across screens. The
// UI is state-shaped, so the server's event stream is preserved INTO the store
// by exactly one reducer — foldFrame — and read back as state everywhere; the
// PUT /policy verdict lands here the same way (decision 4). This file is the
// CONTRACT the SSE client and every island build against: the slice shape maps
// 1:1 onto decision 1's screen table, the frame type onto the server's SSE
// grammar (ApiHost.Frame), and the verdict onto the closed PolicyWriteOutcome.

// ---- the SSE contract (what the phase-4 fetch-stream client emits) ---------

/** One server frame, decoded but unparsed. `id` is the resume cursor — an
 * OPAQUE token (ADR-0009 d2): store it, echo it in Last-Event-ID, never do
 * arithmetic on it. A gap frame carries NO id on purpose — the cursor must not
 * advance over the hole, so a reconnect recovers the dropped region. */
export type SseFrame =
  | { kind: "line"; id: string; text: string }
  | { kind: "gap"; dropped: number }
  | { kind: "reset" };

/** The trace island's connection banner. The client owns the transitions:
 * live ⇄ retrying on network drops, dead on 401/403 — a rotated credential the
 * browser cannot refresh (decision 4: stop retrying, say re-run
 * `captainHook ui`). */
export type StreamState = "idle" | "live" | "retrying" | "dead";

/** The tab's credential state (token-handoff-bootstrap). Distinct from
 * StreamState: fetch-once panels care about the session even with no stream. */
export type SessionState = "none" | "checking" | "live" | "dead";

// ---- trace entries ----------------------------------------------------------

/** A parsed trail line. The tailer and transport are schema-blind; the client
 * parses ONLY for display, so everything is optional and unknown keys ride
 * along — a trail schema change must degrade rendering, never break the fold. */
export type TrailLine = {
  ts?: string;
  level?: string;
  comp?: string;
  evt?: string;
  dispatchId?: string;
  msg?: string;
  durMs?: number;
  data?: Record<string, unknown>;
  [k: string]: unknown;
};

export type TraceEntry =
  | { kind: "line"; id: string; line: TrailLine; raw: string }
  | { kind: "unparsed"; id: string; raw: string }   // honest: shown raw, never dropped
  | { kind: "gap"; dropped: number }                // the server evicted `dropped` lines
  | { kind: "reset" };                              // id space re-anchored; older history is gone

/** Client-side cap on the accumulating trace list (the ONLY state that grows).
 * Oldest entries drop; `traceTruncated` counts them so the UI can say
 * "showing the last N" instead of implying completeness. */
export const TRACE_CAP = 2000;

// ---- PUT /policy verdict ------------------------------------------------------

/** Mirror of the server's closed PolicyWriteOutcome → HTTP mapping. The editor
 * island renders this; the 200 case ALSO refreshes the policy slice (the PUT
 * echoes the fresh PolicyDto — one round-trip, no re-GET). */
export type PolicyVerdict =
  | { kind: "written"; etag: string }
  | { kind: "invalid"; violations: string[] }        // 422 — the daemon's own parser said no
  | { kind: "mismatch"; current: string | null }     // 412 — adopt `current` before retrying
  | { kind: "failed"; detail: string };              // 500 / network

// ---- the store ---------------------------------------------------------------

export type Store = {
  // one slice per screen (decision 1's table)
  trace: TraceEntry[];
  traceTruncated: number;
  status: StatusDto | null;
  policy: PolicyDto | null;
  policyVerdict: PolicyVerdict | null;
  handlers: HandlersDto | null;
  harnesses: HarnessesDto | null;
  // cross-cutting
  session: SessionState;
  stream: StreamState;

  // the ONE place stream state ever mutates (decision 8)
  foldFrame: (frame: SseFrame) => void;

  // fetch-result setters — plain state lands, no logic
  setStatus: (s: StatusDto) => void;
  setPolicy: (p: PolicyDto) => void;
  setPolicyVerdict: (v: PolicyVerdict | null) => void;
  setHandlers: (h: HandlersDto) => void;
  setHarnesses: (h: HarnessesDto) => void;
  setSession: (s: SessionState) => void;
  setStream: (s: StreamState) => void;
};

/** Fold one frame into the trace list — pure, exported for tests. A reset
 * CLEARS the list (the id space restarted; counting or keeping entries from a
 * replaced file would lie) and supersedes the truncation count. */
export function foldTrace(
  trace: TraceEntry[], truncated: number, frame: SseFrame,
): { trace: TraceEntry[]; truncated: number } {
  let entry: TraceEntry;
  switch (frame.kind) {
    case "reset":
      return { trace: [{ kind: "reset" }], truncated: 0 };
    case "gap":
      entry = { kind: "gap", dropped: frame.dropped };
      break;
    case "line":
      try {
        entry = { kind: "line", id: frame.id, line: JSON.parse(frame.text) as TrailLine, raw: frame.text };
      } catch {
        entry = { kind: "unparsed", id: frame.id, raw: frame.text };
      }
      break;
  }
  const next = [...trace, entry];
  const overflow = next.length - TRACE_CAP;
  return overflow > 0
    ? { trace: next.slice(overflow), truncated: truncated + overflow }
    : { trace: next, truncated };
}

export const useStore = create<Store>((set) => ({
  trace: [],
  traceTruncated: 0,
  status: null,
  policy: null,
  policyVerdict: null,
  handlers: null,
  harnesses: null,
  session: "none",
  stream: "idle",

  foldFrame: (frame) =>
    set((s) => {
      const { trace, truncated } = foldTrace(s.trace, s.traceTruncated, frame);
      return { trace, traceTruncated: truncated };
    }),

  setStatus: (status) => set({ status }),
  setPolicy: (policy) => set({ policy }),
  setPolicyVerdict: (policyVerdict) => set({ policyVerdict }),
  setHandlers: (handlers) => set({ handlers }),
  setHarnesses: (harnesses) => set({ harnesses }),
  setSession: (session) => set({ session }),
  setStream: (stream) => set({ stream }),
}));
