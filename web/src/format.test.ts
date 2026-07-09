import { test } from "node:test";
import assert from "node:assert/strict";
import { dispatchHue, uptime, clockTime, traceMatches } from "./format.ts";
import type { TraceEntry } from "./store.ts";

test("dispatchHue is deterministic and in range", () => {
  assert.equal(dispatchHue("abc123"), dispatchHue("abc123"));
  for (const id of ["", "a", "deadbeef", "0123456789abcdef", "😀"]) {
    const h = dispatchHue(id);
    assert.ok(h >= 0 && h < 360, `hue ${h} out of range for ${id}`);
    assert.ok(Number.isInteger(h));
  }
  // Different ids generally differ (not a hard guarantee, but these must).
  assert.notEqual(dispatchHue("aaaa1111"), dispatchHue("bbbb2222"));
});

test("uptime picks the largest two units", () => {
  assert.equal(uptime(0), "0s");
  assert.equal(uptime(8_000), "8s");
  assert.equal(uptime(725_000), "12m 05s");
  assert.equal(uptime(3_780_000), "1h 03m");
  assert.equal(uptime(-5), "0s");   // never negative
});

test("clockTime extracts HH:MM:SS or empties on junk", () => {
  assert.equal(clockTime(undefined), "");
  assert.equal(clockTime("not-a-date"), "");
  assert.match(clockTime("2026-07-09T20:15:33.123Z"), /^\d{2}:\d{2}:\d{2}$/);
});

const line = (extra: Record<string, unknown>): TraceEntry => ({
  kind: "line", id: "1", raw: JSON.stringify(extra), line: extra,
});

test("traceMatches: empty query matches everything, incl. gap/reset", () => {
  assert.equal(traceMatches({ kind: "gap", dropped: 3 }, ""), true);
  assert.equal(traceMatches({ kind: "reset" }, ""), true);
  assert.equal(traceMatches(line({ evt: "x" }), ""), true);
});

test("traceMatches: case-insensitive across searchable fields", () => {
  const e = line({ evt: "shim.answered", msg: "warm", comp: "shim", dispatchId: "a1b2c3d4", level: "info" });
  for (const q of ["ANSWERED", "warm", "a1b2", "SHIM", "info"])
    assert.equal(traceMatches(e, q), true, `should match ${q}`);
  assert.equal(traceMatches(e, "nonesuch"), false);
});

test("traceMatches: gap/reset never satisfy a non-empty query (dividers drop in a narrowed view)", () => {
  assert.equal(traceMatches({ kind: "gap", dropped: 9 }, "shim"), false);
  assert.equal(traceMatches({ kind: "reset" }, "shim"), false);
});

test("traceMatches: an unparsed line matches on its raw text", () => {
  assert.equal(traceMatches({ kind: "unparsed", id: "2", raw: "garbled BOOM" }, "boom"), true);
});
