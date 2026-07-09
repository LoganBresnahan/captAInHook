// sse-fetch-client protocol + reconnect semantics (ADR-0008 d4, ADR-0009 d2),
// tested pure / with stubbed fetch via node:test. The pins that must hold:
// records split correctly across arbitrary chunk boundaries; a gap never
// advances the cursor (the reconnect recovers the hole); a reset re-anchors
// the cursor to the restarted id space; 401/403 stops the loop (dead
// credential) while network drops retry; the first connect sends NO
// Last-Event-ID (live-from-now).
import { test } from "node:test";
import assert from "node:assert/strict";
import { splitRecords, parseRecord, recordToFrame, runEventStream } from "./sse.ts";
import type { SseFrame } from "./store.ts";

// ---- decoder ----------------------------------------------------------------

test("records split across arbitrary chunk boundaries", () => {
  let s = splitRecords("", "id: 12\nda");
  assert.deepEqual(s.records, []);
  s = splitRecords(s.buffer, 'ta: {"a":1}\n');
  assert.deepEqual(s.records, []);
  s = splitRecords(s.buffer, "\nid: 34\ndata: {}\n\n");
  assert.equal(s.records.length, 2);
  assert.deepEqual(parseRecord(s.records[0]), { id: "12", data: '{"a":1}' });
  assert.deepEqual(parseRecord(s.records[1]), { id: "34", data: "{}" });
  assert.equal(s.buffer, "");
});

test("CRLF pairs split across chunks never fabricate a record boundary", () => {
  // "id: 1\r" + "\ndata: x\r\n\r\n" — the CR is held until its LF arrives.
  let s = splitRecords("", "id: 1\r");
  assert.deepEqual(s.records, []);
  s = splitRecords(s.buffer, "\ndata: x\r\n\r\n");
  assert.equal(s.records.length, 1);
  assert.deepEqual(parseRecord(s.records[0]), { id: "1", data: "x" });
});

test("comments (heartbeats) parse to an empty record and no frame", () => {
  const rec = parseRecord(": hb");
  assert.deepEqual(rec, {});
  assert.equal(recordToFrame(rec), null);
});

test("the server's exact frame grammar maps onto the store contract", () => {
  assert.deepEqual(recordToFrame(parseRecord("id: 4093\ndata: {\"evt\":\"x\"}")),
    { kind: "line", id: "4093", text: '{"evt":"x"}' });
  assert.deepEqual(recordToFrame(parseRecord("event: reset\nid: 0\ndata: {}")),
    { kind: "reset" });
  assert.deepEqual(recordToFrame(parseRecord('event: gap\ndata: {"dropped":17}')),
    { kind: "gap", dropped: 17 });
});

test("spec tolerances: no space after colon, multi-data joins, unknown fields/events ignored", () => {
  assert.deepEqual(parseRecord("data:x"), { data: "x" });
  assert.deepEqual(parseRecord("data: a\ndata: b"), { data: "a\nb" });
  assert.deepEqual(parseRecord("weird: y\ndata: z"), { data: "z" });
  assert.equal(recordToFrame(parseRecord("event: future-thing\ndata: {}")), null);
  assert.deepEqual(parseRecord("retry: 250"), { retry: 250 });
});

// ---- reconnect loop -----------------------------------------------------------

const enc = new TextEncoder();

/** A Response streaming `body` then ending (like a daemon drain). */
function sseResponse(body: string, status = 200): Response {
  return new Response(
    new ReadableStream<Uint8Array>({
      start(c) {
        c.enqueue(enc.encode(body));
        c.close();
      },
    }),
    { status, headers: { "Content-Type": "text/event-stream" } },
  );
}

type Attempt = { lastEventId: string | null };

/** Drive runEventStream over scripted responses; capture each attempt's
 * Last-Event-ID. Instant injected sleep — no real timers. */
async function drive(responses: (() => Response)[], opts?: { abortAfterAttempts?: number }) {
  const attempts: Attempt[] = [];
  const frames: SseFrame[] = [];
  const states: string[] = [];
  const ctrl = new AbortController();
  const result = await runEventStream({
    fetchFn: (_path, init) => {
      const h = new Headers(init?.headers);
      attempts.push({ lastEventId: h.get("Last-Event-ID") });
      if (opts?.abortAfterAttempts !== undefined && attempts.length > opts.abortAfterAttempts)
        ctrl.abort();
      if (ctrl.signal.aborted) return Promise.reject(new Error("aborted"));
      const make = responses[Math.min(attempts.length - 1, responses.length - 1)];
      return Promise.resolve(make());
    },
    onFrame: (f) => frames.push(f),
    onState: (s) => states.push(s),
    signal: ctrl.signal,
    sleep: () => (ctrl.signal.aborted ? Promise.reject(new Error("aborted")) : Promise.resolve()),
  });
  return { attempts, frames, states, result, ctrl };
}

test("first connect is live-from-now (no Last-Event-ID); resume echoes the last line id", async () => {
  const { attempts, frames } = await drive(
    [
      () => sseResponse("retry: 1000\n\nid: 100\ndata: {}\n\nid: 250\ndata: {}\n\n"),
      () => sseResponse("id: 300\ndata: {}\n\n"),
    ],
    { abortAfterAttempts: 2 },
  );
  assert.equal(attempts[0].lastEventId, null);          // decision 5: from now
  assert.equal(attempts[1].lastEventId, "250");         // opaque echo, verbatim
  assert.equal(frames.filter((f) => f.kind === "line").length, 3);
});

test("a gap does NOT advance the cursor — the reconnect recovers the hole", async () => {
  const { attempts, frames } = await drive(
    [
      () => sseResponse('id: 10\ndata: {}\n\nevent: gap\ndata: {"dropped":5}\n\n'),
      () => sseResponse("id: 99\ndata: {}\n\n"),
    ],
    { abortAfterAttempts: 2 },
  );
  assert.deepEqual(frames[1], { kind: "gap", dropped: 5 });
  assert.equal(attempts[1].lastEventId, "10");          // NOT advanced past the gap
});

test("a reset re-anchors the cursor to the restarted id space", async () => {
  const { attempts, frames } = await drive(
    [
      () => sseResponse("id: 5000\ndata: {}\n\nevent: reset\nid: 0\ndata: {}\n\n"),
      () => sseResponse("id: 40\ndata: {}\n\n"),
    ],
    { abortAfterAttempts: 2 },
  );
  assert.equal(frames.some((f) => f.kind === "reset"), true);
  assert.equal(attempts[1].lastEventId, "0");           // the old id space is gone
});

test("401 on the reconnect is a DEAD credential: loop stops, never retries", async () => {
  const { attempts, states, result } = await drive([
    () => sseResponse("id: 7\ndata: {}\n\n"),
    () => sseResponse("", 401),
    () => { throw new Error("must never attempt again"); },
  ]);
  assert.equal(result, "dead");
  assert.equal(attempts.length, 2);                     // no third attempt
  assert.equal(states.at(-1), "dead");
});

test("a network error is a transient: retrying state, then resume", async () => {
  let failed = false;
  const attempts: Attempt[] = [];
  const states: string[] = [];
  const ctrl = new AbortController();
  const result = await runEventStream({
    fetchFn: (_p, init) => {
      const h = new Headers(init?.headers);
      attempts.push({ lastEventId: h.get("Last-Event-ID") });
      if (attempts.length === 2 && !failed) {
        failed = true;
        return Promise.reject(new TypeError("network down"));
      }
      if (attempts.length >= 3) ctrl.abort();
      if (ctrl.signal.aborted) return Promise.reject(new Error("aborted"));
      return Promise.resolve(sseResponse("id: 11\ndata: {}\n\n"));
    },
    onFrame: () => {},
    onState: (s) => states.push(s),
    signal: ctrl.signal,
    sleep: () => (ctrl.signal.aborted ? Promise.reject(new Error("aborted")) : Promise.resolve()),
  });
  assert.equal(result, "stopped");
  assert.equal(states.includes("retrying"), true);
  assert.equal(attempts[2].lastEventId, "11");          // resumed after the blip
});
