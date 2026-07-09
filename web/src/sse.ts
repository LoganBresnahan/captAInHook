import { useStore, type SseFrame, type StreamState } from "./store.ts";
import { apiFetch, clearToken } from "./auth.ts";

// sse-fetch-client (ADR-0008 decision 4): the live stream, consumed via fetch
// streaming because EventSource cannot send the Authorization header the gate
// requires. Three server-pinned semantics this client must honor exactly
// (ApiHost.Frame / TrailSubscription):
//   * a LINE carries `id:` — the resume cursor, an OPAQUE token (ADR-0009 d2):
//     store it, echo it in Last-Event-ID on reconnect, never interpret it;
//   * a GAP carries NO id — the cursor must NOT advance over the hole, which
//     is precisely what lets the reconnect recover the dropped region from the
//     file (the server's drop-oldest contract, ADR-0007 d5);
//   * a RESET re-anchors: it arrives with `id: 0`, so the cursor follows to
//     the restarted id space and the fold clears the trace.
// And the two failure modes are DIFFERENT (decision 4): a transport drop or a
// draining daemon retries with backoff and resumes from the cursor; a 401/403
// is a DEAD CREDENTIAL — a cutover rotated the token, the browser cannot
// re-read the 0600 api.json, so the loop STOPS and the session ends. The
// dead-credential answer typically arrives on the reconnect AFTER a drop —
// classifying it as "another drop" would retry a dead token forever, which is
// why the check sits on the response status, before any retry decision.
//
// The protocol layer (splitRecords / parseRecord / recordToFrame) is pure and
// exported for direct unit tests, the same factoring as ApiAuthGate and
// ResolveUiFile server-side.

// ---- pure protocol layer ----------------------------------------------------

/** One SSE record's parsed fields. Absent field ⇒ the record never carried it
 * (an absent `id` is load-bearing: it is what keeps gaps from advancing the
 * cursor). Comment-only records parse to all-absent. */
export type SseRecord = { event?: string; data?: string; id?: string; retry?: number };

/** Accumulate a chunk onto the carry buffer and split off complete records
 * (blank-line terminated). Handles records and even CRLF pairs split across
 * chunk boundaries: a trailing CR is held back until the next chunk decides
 * whether it is a bare CR or half of CRLF. */
export function splitRecords(buffer: string, chunk: string): { buffer: string; records: string[] } {
  let all = buffer + chunk;
  let held = "";
  if (all.endsWith("\r")) {
    held = "\r";
    all = all.slice(0, -1);
  }
  all = all.replace(/\r\n/g, "\n").replace(/\r/g, "\n");
  const records: string[] = [];
  let idx: number;
  while ((idx = all.indexOf("\n\n")) !== -1) {
    records.push(all.slice(0, idx));
    all = all.slice(idx + 2);
  }
  return { buffer: all + held, records };
}

/** Parse one raw record per the SSE grammar subset the server emits (plus the
 * spec's tolerances: optional space after the colon, multi-`data:` joined with
 * newlines, comment lines ignored, ids containing NUL ignored). Unknown field
 * names are ignored per spec — forward compatibility. */
export function parseRecord(raw: string): SseRecord {
  const rec: SseRecord = {};
  const data: string[] = [];
  for (const line of raw.split("\n")) {
    if (line === "" || line.startsWith(":")) continue;
    const colon = line.indexOf(":");
    const field = colon === -1 ? line : line.slice(0, colon);
    let value = colon === -1 ? "" : line.slice(colon + 1);
    if (value.startsWith(" ")) value = value.slice(1);
    switch (field) {
      case "event": rec.event = value; break;
      case "data": data.push(value); break;
      case "id": if (!value.includes("\0")) rec.id = value; break;
      case "retry": if (/^\d+$/.test(value)) rec.retry = Number(value); break;
    }
  }
  if (data.length > 0) rec.data = data.join("\n");
  return rec;
}

/** Map a record onto the store's frame contract. Heartbeats (comment-only)
 * and retry-only records carry no frame; unknown named events are skipped —
 * a future server event type must not break an old client. */
export function recordToFrame(rec: SseRecord): SseFrame | null {
  if (rec.event === "reset") return { kind: "reset" };
  if (rec.event === "gap") {
    let dropped = 0;
    try {
      const n = (JSON.parse(rec.data ?? "{}") as { dropped?: unknown }).dropped;
      if (typeof n === "number" && Number.isFinite(n)) dropped = n;
    } catch { /* a gap with an unreadable count is still a gap */ }
    return { kind: "gap", dropped };
  }
  if (rec.event === undefined && rec.data !== undefined)
    return { kind: "line", id: rec.id ?? "", text: rec.data };
  return null;
}

// ---- the reconnect loop ------------------------------------------------------

export type RunResult = "dead" | "stopped";

export type RunOptions = {
  fetchFn: (path: string, init?: RequestInit) => Promise<Response>;
  onFrame: (f: SseFrame) => void;
  onState: (s: StreamState) => void;
  signal: AbortSignal;
  path?: string;
  retryBaseMs?: number;
  retryMaxMs?: number;
  /** Test seam — resolves after ms or rejects when the signal aborts. */
  sleep?: (ms: number, signal: AbortSignal) => Promise<void>;
};

function defaultSleep(ms: number, signal: AbortSignal): Promise<void> {
  return new Promise((resolve, reject) => {
    if (signal.aborted) return reject(new Error("aborted"));
    const t = setTimeout(() => { signal.removeEventListener("abort", onAbort); resolve(); }, ms);
    const onAbort = () => { clearTimeout(t); reject(new Error("aborted")); };
    signal.addEventListener("abort", onAbort, { once: true });
  });
}

/** Run the stream until the credential dies ("dead") or the caller aborts
 * ("stopped"). Everything else — network errors, a draining daemon's 503, the
 * connection ending — is a transient: back off (exponential from the server's
 * `retry:` hint or retryBaseMs, capped) and resume from the cursor. The FIRST
 * connect sends no Last-Event-ID: the trace opens "from now" (decision 5). */
export async function runEventStream(o: RunOptions): Promise<RunResult> {
  const path = o.path ?? "/api/v1/events";
  const base = o.retryBaseMs ?? 1000;
  const max = o.retryMaxMs ?? 15000;
  const sleep = o.sleep ?? defaultSleep;
  let cursor: string | null = null;
  let retryHint: number | null = null;
  let delay = base;
  let first = true;

  while (!o.signal.aborted) {
    if (!first) {
      o.onState("retrying");
      try { await sleep(Math.min(delay, max), o.signal); } catch { return "stopped"; }
      delay = Math.min(delay * 2, max);
    }
    first = false;

    let resp: Response;
    try {
      resp = await o.fetchFn(path, {
        signal: o.signal,
        headers: {
          Accept: "text/event-stream",
          ...(cursor !== null ? { "Last-Event-ID": cursor } : {}),
        },
      });
    } catch {
      if (o.signal.aborted) return "stopped";
      continue;
    }

    if (resp.status === 401 || resp.status === 403) {
      // The dead credential (decision 4): stop retrying entirely. Reached on
      // the reconnect after a cutover dropped us — never misread as a blip.
      o.onState("dead");
      return "dead";
    }
    if (!resp.ok || resp.body === null) {
      try { await resp.body?.cancel(); } catch { /* already gone */ }
      continue;   // 503 while draining, or any odd answer: a transient
    }

    o.onState("live");
    delay = retryHint ?? base;   // a healthy connect resets the backoff

    const reader = resp.body.getReader();
    const decoder = new TextDecoder();   // handles UTF-8 split across chunks
    let buffer = "";
    try {
      for (;;) {
        const { done, value } = await reader.read();
        if (done) break;
        const step = splitRecords(buffer, decoder.decode(value, { stream: true }));
        buffer = step.buffer;
        for (const raw of step.records) {
          const rec = parseRecord(raw);
          if (rec.retry !== undefined) retryHint = rec.retry;
          // ANY record carrying an id advances the cursor — lines do, the
          // reset's `id: 0` re-anchors, and gaps (no id) leave it be.
          if (rec.id !== undefined) cursor = rec.id;
          const frame = recordToFrame(rec);
          if (frame !== null) o.onFrame(frame);
        }
      }
    } catch { /* read torn down mid-stream: fall through to reconnect */
    } finally {
      // Release the connection on EVERY exit — including an exception thrown
      // out of onFrame (a store subscriber blowing up). Without this the old
      // TCP stream lives on while we reconnect, and server-side each zombie
      // holds an open subscription that defers the daemon's idle-exit — the
      // lifetime-critical openStreams counter (adversarial verify, 2026-07-09:
      // one throwing onFrame pinned openStreams at 2 on a live daemon).
      try { await reader.cancel(); } catch { /* already torn */ }
    }
    if (o.signal.aborted) return "stopped";
  }
  return "stopped";
}

// ---- store wiring -------------------------------------------------------------

/** Start the app-level stream service: frames fold into the store (the ONE
 * reducer, decision 8), stream state lands beside them, and a dead credential
 * ends the whole session (the panels' next fetch would 401 anyway). */
export function startEventStream(): { stop: () => void } {
  const ctrl = new AbortController();
  void runEventStream({
    fetchFn: apiFetch,
    onFrame: (f) => useStore.getState().foldFrame(f),
    onState: (s) => useStore.getState().setStream(s),
    signal: ctrl.signal,
  }).then((result) => {
    if (result === "dead") {
      clearToken();
      useStore.getState().setSession("dead");
    }
  });
  return {
    stop: () => {
      ctrl.abort();
      useStore.getState().setStream("idle");
    },
  };
}
