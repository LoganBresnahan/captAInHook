// The empty-state verdict (2026-08-16): the sentence under an empty trace must
// be chosen from what is known. The one that matters is `starved` — connected,
// the daemon has served dispatches since, and no frame arrived — because that
// is exactly the state that used to read "Waiting for hook activity".
import { test } from "node:test";
import assert from "node:assert/strict";
import { emptyTraceReason } from "./streamHealth.ts";
import { emptyStreamStats, noteFrame, noteState } from "./store.ts";

const fresh = emptyStreamStats();

test("a filter that matches nothing says so, whatever the stream is doing", () => {
  assert.equal(emptyTraceReason("x", "live", fresh, 10).kind, "filtered");
  assert.equal(emptyTraceReason("x", "dead", fresh, null).kind, "filtered");
});

test("stalled / retrying / idle name their own state, not the daemon's", () => {
  assert.equal(emptyTraceReason("", "stalled", fresh, 10).kind, "stalled");
  assert.equal(emptyTraceReason("", "retrying", fresh, 10).kind, "retrying");
  assert.equal(emptyTraceReason("", "idle", fresh, 10).kind, "idle");
  assert.equal(emptyTraceReason("", "dead", fresh, 10).kind, "idle");
});

test("live, nothing dispatched since connect ⇒ quiet (an honest wait)", () => {
  const st = noteState(fresh, "live", 78, 1000);
  assert.equal(emptyTraceReason("", "live", st, 78).kind, "quiet");
  // no status polled yet either side ⇒ cannot claim starvation
  assert.equal(emptyTraceReason("", "live", noteState(fresh, "live", null, 0), null).kind, "quiet");
});

test("live, daemon served N since connect, 0 frames ⇒ STARVED, and the count is in the sentence", () => {
  const st = noteState(fresh, "live", 78, 1000);
  const why = emptyTraceReason("", "live", st, 99);
  assert.equal(why.kind, "starved");
  assert.match(why.text, /21 dispatches since this stream connected/);
  assert.match(emptyTraceReason("", "live", st, 79).text, /1 dispatch since/);
});

test("once a frame has arrived the list is not empty by starvation — a later empty state is quiet", () => {
  // (frames > 0 with an empty list only happens under a filter or after a
  // reset; the verdict must not cry starvation over a stream that delivered.)
  const st = noteFrame(noteState(fresh, "live", 78, 1000), 1500);
  assert.equal(emptyTraceReason("", "live", st, 99).kind, "quiet");
});

test("telemetry: only LIVE counts as a connect and pins served; frames stamp and count", () => {
  let st = noteState(fresh, "retrying", 5, 10);
  assert.deepEqual(st, fresh);
  st = noteState(st, "live", 5, 20);
  assert.equal(st.connects, 1); assert.equal(st.liveSince, 20); assert.equal(st.servedAtConnect, 5);
  st = noteFrame(noteFrame(st, 30), 40);
  assert.equal(st.frames, 2); assert.equal(st.lastFrameAt, 40);
  st = noteState(st, "stalled", 9, 50);
  assert.equal(st.frames, 2);            // a stall erases nothing
  st = noteState(st, "live", 9, 60);
  assert.equal(st.connects, 2); assert.equal(st.servedAtConnect, 9); assert.equal(st.frames, 2);
});
