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
  /** Where the FIRST connect resumes from, echoed verbatim in `Last-Event-ID`
   * — an opaque token this code never interprets (ADR-0009 d2). Absent/null is
   * the trace's own behaviour: open "from now". The Mail stream passes
   * `MailDto.trailEventId` here, which is what makes its snapshot and its
   * stream meet exactly: zero loss, zero duplicate. Note that null and the
   * token "0" are NOT the same instruction — "0" means "from the earliest
   * still-reachable point", i.e. replay everything. */
  initialCursor?: string | null;
  retryBaseMs?: number;
  retryMaxMs?: number;
  /** Test seam — resolves after ms or rejects when the signal aborts. */
  sleep?: (ms: number, signal: AbortSignal) => Promise<void>;
  /** The stall window (2026-08-16). The server writes a `: hb` comment every
   * 15 s on a quiet trail (TrailSubscription's heartbeat) — so a connection
   * that goes this long without a single BYTE is not quiet, it is wedged: a
   * half-open socket, a relay that stopped forwarding, a subscription the
   * daemon lost. Before this the client had no way to know: `live` was set
   * on headers and never left while the socket sat open. Default 2.5×
   * heartbeat + slack; the arm/disarm is `timer`, injectable, so tests fire
   * it by hand and never wait. */
  stallMs?: number;
  timer?: (ms: number, cb: () => void) => () => void;
};

/** Default `timer` — setTimeout, disarmed by the returned thunk. */
export function defaultTimer(ms: number, cb: () => void): () => void {
  const t = setTimeout(cb, ms);
  return () => clearTimeout(t);
}

export const DEFAULT_STALL_MS = 40_000;

/** The stall window the app runs with. `sessionStorage["captainhook.stallMs"]`
 * overrides it — an e2e seam (the suite sets it via addInitScript against a
 * daemon whose heartbeat is likewise shortened, CAPTAINHOOK_SSE_HEARTBEAT_MS)
 * so the stall path can be pinned in seconds. Anything unreadable ⇒ default. */
export function stallWindowMs(): number {
  try {
    const v = Number(sessionStorage.getItem("captainhook.stallMs"));
    return Number.isFinite(v) && v > 0 ? v : DEFAULT_STALL_MS;
  } catch { return DEFAULT_STALL_MS; }
}

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
 * connect sends no Last-Event-ID unless `initialCursor` gives it one: the trace
 * opens "from now" (decision 5), the Mail stream opens at the exact position
 * its snapshot was taken (ADR-0016 d14 as-built). */
export async function runEventStream(o: RunOptions): Promise<RunResult> {
  const path = o.path ?? "/api/v1/events";
  const base = o.retryBaseMs ?? 1000;
  const max = o.retryMaxMs ?? 15000;
  const sleep = o.sleep ?? defaultSleep;
  const timer = o.timer ?? defaultTimer;
  const stallMs = o.stallMs ?? DEFAULT_STALL_MS;
  let cursor: string | null = o.initialCursor ?? null;
  let retryHint: number | null = null;
  let delay = base;
  let first = true;
  let stalled = false;

  while (!o.signal.aborted) {
    if (!first && !stalled) {
      o.onState("retrying");
      try { await sleep(Math.min(delay, max), o.signal); } catch { return "stopped"; }
      delay = Math.min(delay * 2, max);
    }
    // A stall reconnects at once, still reading "stalled" until the new headers
    // arrive: the socket was open, the server was not answering on it, and the
    // honest thing is to try again from the cursor — not to back off as if the
    // network were down. If THAT connect fails, the normal retry path takes over.
    first = false;
    stalled = false;

    // The connect deadline (reviewer finding on 1ee7218, review-1ee7218-reply):
    // the body watchdog below arms only once headers arrive, so a connect that
    // hangs BEFORE headers — the very shape of the Firefox same-URL lock this
    // client now sidesteps — had no timer, no backoff, no retry. Each attempt
    // gets its own abort, chained to the caller's; the same stall window
    // bounds it, and a timed-out connect is a transient (retrying + backoff),
    // not a stall: nothing was ever open to stall.
    const attempt = new AbortController();
    const onOuterAbort = () => attempt.abort();
    o.signal.addEventListener("abort", onOuterAbort, { once: true });
    const disarmConnect = timer(stallMs, () => attempt.abort());
    let resp: Response;
    try {
      resp = await o.fetchFn(path, {
        signal: attempt.signal,
        headers: {
          Accept: "text/event-stream",
          ...(cursor !== null ? { "Last-Event-ID": cursor } : {}),
        },
      });
    } catch {
      disarmConnect();
      o.signal.removeEventListener("abort", onOuterAbort);
      if (o.signal.aborted) return "stopped";
      continue;
    }
    disarmConnect();

    if (resp.status === 401 || resp.status === 403) {
      // The dead credential (decision 4): stop retrying entirely. Reached on
      // the reconnect after a cutover dropped us — never misread as a blip.
      o.signal.removeEventListener("abort", onOuterAbort);
      o.onState("dead");
      return "dead";
    }
    if (!resp.ok || resp.body === null) {
      try { await resp.body?.cancel(); } catch { /* already gone */ }
      o.signal.removeEventListener("abort", onOuterAbort);
      continue;   // 503 while draining, or any odd answer: a transient
    }

    o.onState("live");
    delay = retryHint ?? base;   // a healthy connect resets the backoff

    const reader = resp.body.getReader();
    const decoder = new TextDecoder();   // handles UTF-8 split across chunks
    let buffer = "";
    // The watchdog: re-armed on every chunk (heartbeats count — they are bytes),
    // and when it fires it cancels the reader, which resolves the pending read
    // as done and lets the loop below fall out with `stalled` set.
    let disarm: () => void = () => {};
    const arm = () => {
      disarm();
      disarm = timer(stallMs, () => { stalled = true; void reader.cancel(); });
    };
    arm();
    try {
      for (;;) {
        const { done, value } = await reader.read();
        if (done) break;
        arm();
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
      disarm();
      // Release the connection on EVERY exit — including an exception thrown
      // out of onFrame (a store subscriber blowing up). Without this the old
      // TCP stream lives on while we reconnect, and server-side each zombie
      // holds an open subscription that defers the daemon's idle-exit — the
      // lifetime-critical openStreams counter (adversarial verify, 2026-07-09:
      // one throwing onFrame pinned openStreams at 2 on a live daemon).
      try { await reader.cancel(); } catch { /* already torn */ }
      o.signal.removeEventListener("abort", onOuterAbort);
    }
    if (o.signal.aborted) return "stopped";
    if (stalled) o.onState("stalled");
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
    stallMs: stallWindowMs(),
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
