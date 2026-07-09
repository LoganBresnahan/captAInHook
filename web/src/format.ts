import type { TraceEntry } from "./store.ts";

// Pure display helpers, exported for direct unit tests (the same
// pure-logic-out-of-the-component factoring the server uses for ApiAuthGate /
// ResolveUiFile). No React, no DOM.

/** A stable hue in [0,360) for a dispatchId, so every line of one dispatch
 * shares a color across the INTERLEAVED trace — the client-side dispatchId
 * correlation ADR-0008 d1's Live-trace screen calls for (concurrent dispatches
 * interleave, so grouping consecutive rows would be wrong; a stable color per
 * id lets the eye follow one dispatch and a click filters to it). */
export function dispatchHue(id: string): number {
  let h = 0;
  for (let i = 0; i < id.length; i++) h = (h * 31 + id.charCodeAt(i)) & 0xffff;
  return h % 360;
}

/** Compact monotonic uptime from a millisecond count: "8s", "12m 05s",
 * "1h 03m" (the largest two units, seconds dropped past an hour). */
export function uptime(ms: number): string {
  const s = Math.max(0, Math.floor(ms / 1000));
  const h = Math.floor(s / 3600);
  const m = Math.floor((s % 3600) / 60);
  const sec = s % 60;
  if (h > 0) return `${h}h ${String(m).padStart(2, "0")}m`;
  if (m > 0) return `${m}m ${String(sec).padStart(2, "0")}s`;
  return `${sec}s`;
}

/** The time-of-day HH:MM:SS from a trail line's ISO timestamp, for the trace
 * row's left gutter. Unparseable/absent ⇒ "" (the row still renders). */
export function clockTime(ts: string | undefined): string {
  if (ts === undefined) return "";
  const d = new Date(ts);
  if (Number.isNaN(d.getTime())) return "";
  return d.toTimeString().slice(0, 8);
}

/** The trace filter predicate — a case-insensitive substring across the fields
 * a user would search (event, message, component, dispatchId, level) plus the
 * raw line as a fallback. gap/reset are structural markers with no text to
 * match, so they never satisfy a query — the caller shows them only when the
 * filter is empty (a narrowed view drops the dividers between removed lines). */
export function traceMatches(entry: TraceEntry, query: string): boolean {
  if (query === "") return true;
  const needle = query.toLowerCase();
  if (entry.kind === "line") {
    const l = entry.line;
    const fields = [l.evt, l.msg, l.comp, l.dispatchId, l.level];
    return fields.some((v) => typeof v === "string" && v.toLowerCase().includes(needle))
      || entry.raw.toLowerCase().includes(needle);
  }
  if (entry.kind === "unparsed") return entry.raw.toLowerCase().includes(needle);
  return false;
}
