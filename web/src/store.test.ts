// foldTrace — the one reducer (ADR-0008 d8) — tested pure, zero test deps:
// Node's built-in runner + type stripping (`npm test`). The contracts that
// must hold: a reset CLEARS (a replaced file's history would lie), a gap is an
// entry (never silently absorbed), an unparsable line is shown raw (never
// dropped), and the client cap counts what it evicts.
import { test } from "node:test";
import assert from "node:assert/strict";
import { foldTrace, TRACE_CAP, type TraceEntry } from "./store.ts";

const fold = (trace: TraceEntry[], truncated: number, ...frames: Parameters<typeof foldTrace>[2][]) =>
  frames.reduce((acc, f) => foldTrace(acc.trace, acc.truncated, f), { trace, truncated });

test("a line parses into the entry, id preserved verbatim", () => {
  const { trace } = fold([], 0, { kind: "line", id: "1234", text: '{"evt":"shim.answered","durMs":16}' });
  assert.equal(trace.length, 1);
  const e = trace[0];
  assert.equal(e.kind, "line");
  if (e.kind === "line") {
    assert.equal(e.id, "1234");                    // opaque cursor, untouched
    assert.equal(e.line.evt, "shim.answered");
    assert.equal(e.raw, '{"evt":"shim.answered","durMs":16}');
  }
});

test("an unparsable line is kept raw, never dropped", () => {
  const { trace } = fold([], 0, { kind: "line", id: "9", text: "not json {" });
  assert.deepEqual(trace, [{ kind: "unparsed", id: "9", raw: "not json {" }]);
});

test("a gap becomes a visible entry with the exact count", () => {
  const { trace } = fold([], 0,
    { kind: "line", id: "1", text: "{}" },
    { kind: "gap", dropped: 42 });
  assert.deepEqual(trace[1], { kind: "gap", dropped: 42 });
});

test("a reset clears everything and supersedes the truncation count", () => {
  const seeded = fold([], 7,
    { kind: "line", id: "1", text: "{}" },
    { kind: "gap", dropped: 3 },
    { kind: "reset" });
  assert.deepEqual(seeded, { trace: [{ kind: "reset" }], truncated: 0 });
});

test("the cap drops oldest and counts every eviction", () => {
  let acc: { trace: TraceEntry[]; truncated: number } = { trace: [], truncated: 0 };
  const total = TRACE_CAP + 25;
  for (let i = 0; i < total; i++)
    acc = foldTrace(acc.trace, acc.truncated, { kind: "line", id: String(i), text: "{}" });
  assert.equal(acc.trace.length, TRACE_CAP);
  assert.equal(acc.truncated, 25);
  const first = acc.trace[0];
  assert.equal(first.kind === "line" && first.id, String(25));   // oldest 25 evicted
});

test("fold never mutates its input (zustand set() depends on it)", () => {
  const before: TraceEntry[] = [{ kind: "reset" }];
  const frozen = Object.freeze([...before]) as TraceEntry[];
  const { trace } = foldTrace(frozen, 0, { kind: "line", id: "1", text: "{}" });
  assert.equal(frozen.length, 1);
  assert.equal(trace.length, 2);
});
