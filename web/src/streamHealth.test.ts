// The empty-state verdict (2026-08-16): the sentence under an empty trace must
// be chosen from what is known. The one that matters is `starved` — connected,
// the daemon has served dispatches since, and no frame arrived — because that
// is exactly the state that used to read "Waiting for hook activity".
import { test } from "node:test";
import assert from "node:assert/strict";
import { emptyTraceReason } from "./streamHealth.ts";
import { emptyStreamStats, noteFrame, noteState, pinServed } from "./store.ts";

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
  const st = pinServed(noteState(fresh, "live", 1000), 78);
  assert.equal(emptyTraceReason("", "live", st, 78).kind, "quiet");
  // no baseline pinned yet (no status poll since the connect) ⇒ cannot claim starvation
  assert.equal(emptyTraceReason("", "live", noteState(fresh, "live", 0), 99).kind, "quiet");
});

test("live, daemon served N since connect, 0 frames ⇒ STARVED, and the count is in the sentence", () => {
  const st = pinServed(noteState(fresh, "live", 1000), 78);
  const why = emptyTraceReason("", "live", st, 99);
  assert.equal(why.kind, "starved");
  assert.match(why.text, /21 dispatches since this stream connected/);
  assert.match(emptyTraceReason("", "live", st, 79).text, /1 dispatch since/);
});

test("the baseline is pinned from the FIRST poll AFTER the connect, never the stale one before it (review 1ee7218 #6)", () => {
  // A dispatch that predates the from-now anchor must not count as "since
  // connect": the last poll before the connect said 78, then a dispatch
  // happened, then the connect anchored past it, then the next poll says 79.
  let st = noteState(fresh, "live", 1000);          // connect: baseline cleared
  assert.equal(st.servedAtConnect, null);
  st = pinServed(st, 79);                            // first poll after connect pins 79
  assert.equal(emptyTraceReason("", "live", st, 79).kind, "quiet");   // NOT starved
  st = pinServed(st, 85);                            // later polls do not re-pin
  assert.equal(st.servedAtConnect, 79);
  assert.equal(emptyTraceReason("", "live", st, 85).kind, "starved");
});

test("starvation is judged on frames SINCE CONNECT, not lifetime (review 1ee7218 #5)", () => {
  // Frames flowed on the first connect; the stream then reconnected and has
  // delivered nothing since while the daemon served on.
  let st = noteFrame(pinServed(noteState(fresh, "live", 1000), 10), 1500);
  st = pinServed(noteState(st, "live", 5000), 20);   // reconnect, baseline re-pinned
  assert.equal(st.frames, 1);
  assert.equal(st.framesSinceConnect, 0);
  assert.equal(emptyTraceReason("", "live", st, 24).kind, "starved");
});

test("once a frame has arrived on THIS connect the verdict is quiet, whatever served says", () => {
  const st = noteFrame(pinServed(noteState(fresh, "live", 1000), 78), 1500);
  assert.equal(emptyTraceReason("", "live", st, 99).kind, "quiet");
});

test("telemetry: only LIVE counts as a connect; frames stamp and count; a stall erases nothing", () => {
  let st = noteState(fresh, "retrying", 10);
  assert.deepEqual(st, fresh);
  st = noteState(st, "live", 20);
  assert.equal(st.connects, 1); assert.equal(st.liveSince, 20); assert.equal(st.servedAtConnect, null);
  st = noteFrame(noteFrame(st, 30), 40);
  assert.equal(st.frames, 2); assert.equal(st.framesSinceConnect, 2); assert.equal(st.lastFrameAt, 40);
  st = noteState(st, "stalled", 50);
  assert.equal(st.frames, 2);
  st = noteState(st, "live", 60);
  assert.equal(st.connects, 2); assert.equal(st.frames, 2); assert.equal(st.framesSinceConnect, 0);
});
