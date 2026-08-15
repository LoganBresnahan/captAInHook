import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import type { MailDto } from "./api.gen.ts";
import type { TrailLine } from "./store.ts";
import { seedMail, reduceMail, projectCursor, lineStatus, rolesOf, type MailState } from "./mail.ts";
import {
  buildScene, canvasHeight, cardLines, clampView, cursorKey, fitView, mapX, sceneSummary,
  slotPixels, tierFor, unmapX, xForOffset, zoomView,
  LEDGER_LEFT, MAX_Z, MIN_Z, PAD_X, SLOT_W, SLOT_PAD, TIER_FAR_MAX, TIER_MID_MAX, ZOOM_STEP,
} from "./mailCanvas.ts";

// mail-canvas, the geometry (ADR-0016 d14, slice 4). The canvas's own failure
// mode is the one a screenshot catches — so these tests do NOT re-check that it
// looks right. They check the two things a picture cannot show you:
//
//   * the layout AGREES WITH THE MODEL. Every glyph is in its recipient's lane
//     at its ledger position; every cursor sits where `projectCursor` says it
//     does; every held mark carries the arithmetic the reducer computed. A
//     drawing that disagrees with the reducer is a false picture, and a false
//     picture is worse than no view at all (d14's whole reason for pinning
//     "delivered" to a record).
//   * the viewBox math is REVERSIBLE and bounded — zoom keeps its anchor, pan
//     cannot lose the bus, and the tier thresholds are what the tier readout
//     claims they are.
//
// The states are the ENGINE's: every scenario in `mail.golden.json` was
// produced by MailReducerGoldenTests.cs driving the real store, cursors and
// digest. Laying out a hand-written state would let the canvas look good
// against a bus the daemon cannot produce.

type Golden = {
  scenarios: { name: string; doc: string; since: number; before: MailDto; trail: string[]; after: MailDto }[];
};
const golden: Golden = JSON.parse(readFileSync(new URL("./mail.golden.json", import.meta.url), "utf8"));
const scenario = (name: string) => {
  const s = golden.scenarios.find((x) => x.name === name);
  assert.ok(s, `golden scenario '${name}' is gone — the corpus moved under this test`);
  return s;
};
const stateOf = (dto: MailDto, atMs = 1000): MailState => seedMail(dto, atMs);

// ---- the layout agrees with the model, on every engine-produced state -------

for (const sc of golden.scenarios) {
  test(`scene: ${sc.name} — every glyph, cursor and mark matches the reducer`, () => {
    const state = stateOf(sc.after);
    const scene = buildScene(state);

    // A lane per role the picture knows — from the ledger AND from cursors, so
    // a role with mail and no reader still gets one (and a reader whose mail is
    // all behind it does too).
    assert.deepEqual(scene.lanes.map((l) => l.role), rolesOf(state));

    for (const lane of scene.lanes) {
      const cursors = state.cursors.filter((c) => c.role === lane.role);
      const mine = state.lines.filter((l) => l.envelope !== null && l.envelope.to === lane.role);

      // Exactly this role's mail, in ledger order, at its ledger position.
      assert.deepEqual(lane.glyphs.map((g) => g.offset), mine.map((l) => l.offset));
      for (const g of lane.glyphs) {
        const line = mine.find((l) => l.offset === g.offset)!;
        assert.equal(g.id, line.envelope!.id);
        assert.ok(Math.abs(g.x - (xForOffset(scene, g.offset) + SLOT_PAD / 2)) < 1e-9,
          "a glyph must sit at its own ledger offset");
        // Per-cursor standing is the reducer's, verbatim — the canvas never
        // computes a status of its own.
        assert.deepEqual(
          g.perCursor,
          cursors.map((c) => ({ key: cursorKey(c), session: c.session, status: lineStatus(state, c, line) })));
        if (cursors.length === 0) assert.equal(g.status, "no-reader");
      }

      // Envelopes never overlap: one ledger line, one slot.
      const sorted = [...lane.glyphs].sort((a, b) => a.x - b.x);
      for (let i = 1; i < sorted.length; i++)
        assert.ok(sorted[i].x >= sorted[i - 1].x + sorted[i - 1].w, "glyphs overlap");

      // One track per cursor, at the position the reducer holds, carrying
      // exactly the held/expired set `projectCursor` reports.
      assert.deepEqual(lane.tracks.map((t) => t.key), cursors.map(cursorKey));
      for (const t of lane.tracks) {
        const c = cursors.find((x) => cursorKey(x) === t.key)!;
        const p = projectCursor(c);
        assert.equal(t.offset, c.offset);
        assert.equal(t.x, c.offset === null ? null : xForOffset(scene, c.offset));
        assert.deepEqual(
          t.marks.map((m) => [m.offset, m.status, m.opportunities, m.ttlDeliveries]),
          [...p.pending.filter((m) => m.seenAt !== null).map((m) => [m.offset, "held", m.opportunities, m.ttlDeliveries]),
           ...p.expired.map((m) => [m.offset, "expired", m.opportunities, m.ttlDeliveries])]
            .sort((a, b) => (a[0] as number) - (b[0] as number)));
        assert.equal(t.pendingCount, p.pending.length);
        assert.equal(t.expiredCount, p.expired.length);
        assert.equal(t.uncertain, c.uncertain);
      }

      // Nothing is laid out past the scene it declares.
      assert.ok(lane.y + lane.h <= scene.height, "a lane runs past the scene's height");
      for (const g of lane.glyphs) assert.ok(g.x + g.w <= scene.width, "a glyph runs past the scene's width");
    }
  });
}

test("scene: a role nobody reads is drawn, and says so", () => {
  const scene = buildScene(stateOf(scenario("appends-only").after));
  assert.equal(scene.lanes.length, 2, "both addressed roles get a lane, reader or not");
  for (const lane of scene.lanes) {
    assert.equal(lane.hasReader, false);
    assert.equal(lane.tracks.length, 0);
    // Not "fresh" — fresh is a standing RELATIVE to a cursor, and there is none.
    assert.ok(lane.glyphs.every((g) => g.status === "no-reader"));
    assert.equal(lane.counts.pending, 0);
    assert.ok(lane.counts.envelopes > 0);
  }
});

test("scene: two sessions on one role are two tracks at two positions", () => {
  const state = stateOf(scenario("two-sessions-one-role").after);
  const scene = buildScene(state);
  const lane = scene.lanes.find((l) => l.tracks.length === 2);
  assert.ok(lane, "the two cursors must share one lane");
  const [a, b] = lane.tracks;
  assert.notEqual(a.key, b.key);
  assert.notEqual(a.y, b.y);
  // Each glyph reports BOTH readings, and the lane shows the most pending of
  // them — a lane cannot have one status when its two readers disagree.
  for (const g of lane.glyphs) assert.equal(g.perCursor.length, 2);
});

test("scene: held mail carries its TTL arithmetic, under the envelope it belongs to", () => {
  const state = stateOf(scenario("hold-only-advance").after);
  const scene = buildScene(state);
  const marks = scene.lanes.flatMap((l) => l.tracks.flatMap((t) => t.marks));
  assert.ok(marks.length > 0, "an advance that delivered nothing holds everything");
  for (const m of marks) {
    assert.equal(m.status, "held");
    assert.ok(m.opportunities >= 1 && m.ttlDeliveries >= 1);
    // A mark sits under ITS envelope: the countdown is about that line, and a
    // mark drawn under the wrong glyph is a lie a screenshot cannot catch.
    const glyph = scene.lanes.flatMap((l) => l.glyphs).find((g) => g.offset === m.offset);
    assert.ok(glyph, "a held mark with no envelope above it");
    assert.equal(glyph.x, m.x);
    assert.equal(glyph.w, m.w);
  }
});

test("scene: spent mail greys out at the instant the reducer says it is spent", () => {
  // No SNAPSHOT carries an expired item — the next advance drops it, so expiry
  // exists between two reads. That is precisely the interval a live canvas is
  // for, so the state is taken from the engine's own trail: fold
  // `hold-then-expire` line by line and stop at the first state where the
  // cursor's arithmetic has spent the envelope.
  const sc = scenario("hold-then-expire");
  let s = stateOf(sc.before);
  let spent: MailState | null = null;
  sc.trail.forEach((text, i) => {
    s = reduceMail(s, { kind: "line", line: JSON.parse(text) as TrailLine, atMs: 2000 + i });
    if (spent === null && s.cursors.some((c) => projectCursor(c).expired.length > 0)) spent = s;
  });
  assert.ok(spent, "the scenario's trail must pass through a spent envelope");
  const scene = buildScene(spent);
  const expired = scene.lanes.flatMap((l) => l.tracks.flatMap((t) => t.marks)).filter((m) => m.status === "expired");
  assert.equal(expired.length, 1);
  assert.ok(expired[0].opportunities >= expired[0].ttlDeliveries, "spent means the TTL was reached");
});

test("scene: a partial snapshot draws the region it has never seen as a break", () => {
  const sc = scenario("partial-since");
  const state = stateOf(sc.after);
  assert.ok(state.since > 0);
  const scene = buildScene(state);
  const unknown = scene.slots.filter((s) => s.kind === "unknown");
  assert.equal(unknown.length, 1, "everything below `since` is one unseen region");
  assert.equal(unknown[0].offset, 0);
  assert.equal(scene.slots[0].kind, "unknown", "and it comes first — the ledger starts before the picture does");
  // The ledger is not closed up over it: the first real line still sits after it.
  assert.ok(scene.slots[1].x > scene.slots[0].x);
});

test("scene: a malformed line and a torn tail live on the spine, not in a lane", () => {
  const state = stateOf(scenario("torn-tail-terminated").after);
  const scene = buildScene(state);
  const unreadable = state.lines.filter((l) => l.envelope === null || !l.terminated);
  assert.deepEqual(scene.spineOnly.map((s) => s.offset), unreadable.map((l) => l.offset));
  // A line with no envelope names no recipient, so no lane may claim it.
  for (const lane of scene.lanes)
    for (const g of lane.glyphs) assert.ok(!unreadable.some((l) => l.offset === g.offset && l.envelope === null));
});

test("scene: an empty bus is empty, not a scene with nothing in it", () => {
  const scene = buildScene(seedMail(
    { ...scenario("appends-only").after, lines: [], cursors: [], presence: [], frontier: 0 }, 1));
  assert.equal(scene.empty, true);
  assert.equal(scene.lanes.length, 0);
});

// ---- the spine's coordinate map --------------------------------------------

test("xForOffset is exact at every line boundary and lands the frontier at the end", () => {
  const state = stateOf(scenario("hold-then-expire").after);
  const scene = buildScene(state);
  for (const slot of scene.slots) assert.equal(xForOffset(scene, slot.offset), slot.x);
  // Strictly increasing across the ledger: the spine is append order, always.
  const xs = state.lines.map((l) => xForOffset(scene, l.offset));
  for (let i = 1; i < xs.length; i++) assert.ok(xs[i] > xs[i - 1]);
  // The frontier is the end of the last line, so it lands at the last slot's edge.
  const last = scene.slots[scene.slots.length - 1];
  assert.equal(scene.frontierX, last.x + last.w);
  assert.equal(xForOffset(scene, 0), PAD_X);
});

test("xForOffset interpolates inside a line rather than jumping", () => {
  const state = stateOf(scenario("hold-then-expire").after);
  const scene = buildScene(state);
  const s = scene.slots[0];
  const mid = xForOffset(scene, s.offset + Math.floor(s.bytes / 2));
  assert.ok(mid > s.x && mid < s.x + s.w);
});

// ---- semantic zoom ----------------------------------------------------------

test("tierFor names the tier the thresholds define", () => {
  assert.equal(tierFor(TIER_FAR_MAX - 1), "far");
  assert.equal(tierFor(TIER_FAR_MAX), "mid");
  assert.equal(tierFor(TIER_MID_MAX - 1), "mid");
  assert.equal(tierFor(TIER_MID_MAX), "near");
  assert.equal(tierFor(0), "far");
  assert.equal(tierFor(Number.NaN), "far");   // an unmeasured canvas is not "near"
});

test("the fit shows the whole ledger, and never magnifies it", () => {
  for (const sc of golden.scenarios) {
    const scene = buildScene(stateOf(sc.after));
    if (scene.empty) continue;
    for (const areaW of [400, 900, 1900]) {
      const v = fitView(scene, areaW);
      assert.equal(v.x, 0, `${sc.name} did not open at the start of the ledger`);
      assert.ok(v.z <= 1 + 1e-9, "the fit must never blow the ledger up past natural size");
      // Either the whole ledger is on screen, or it is on screen at 1:1 — the
      // second only when it already fits.
      assert.ok(scene.width * v.z <= areaW + 1e-6 || v.z === MIN_Z,
        `${sc.name} clipped the ledger at ${areaW}px`);
    }
  }
});

test("the canvas is exactly as tall as the scene — no vertical fit to get wrong", () => {
  for (const sc of golden.scenarios) {
    const scene = buildScene(stateOf(sc.after));
    if (scene.empty) continue;
    assert.equal(canvasHeight(scene), Math.max(scene.height, 200));
    const lowest = Math.max(...scene.lanes.map((l) => l.y + l.h));
    assert.ok(canvasHeight(scene) >= lowest, "a lane fell off the bottom of the canvas");
  }
});

test("the tier the fit lands on is a fact about the bus, not a preference", () => {
  // A small store opens on cards; a big one opens on role pulses. Same fit, and
  // the difference is only ever how much ledger there is to show.
  const small = buildScene(stateOf(scenario("first-delivery").after));
  assert.equal(tierFor(slotPixels(fitView(small, 1100))), "near");

  const wide = buildScene(stateOf(withLines(scenario("first-delivery").after, 120)));
  assert.equal(tierFor(slotPixels(fitView(wide, 1100))), "far");
});

/** The same snapshot with its ledger repeated up to `n` lines — a bigger bus,
 * still shaped like one the engine produced. */
function withLines(dto: MailDto, n: number): MailDto {
  const src = dto.lines;
  const lines = [];
  let offset = 0;
  for (let i = 0; i < n; i++) {
    const l = src[i % src.length];
    lines.push({ ...l, offset, envelope: l.envelope === null ? null : { ...l.envelope, id: `${l.envelope.id}-${i}` } });
    offset += l.bytes + 1;
  }
  return { ...dto, lines, cursors: [], frontier: offset };
}

test("mapX and unmapX are inverses, and the ledger starts clear of the gutter", () => {
  const scene = buildScene(stateOf(scenario("hold-then-expire").after));
  const v = fitView(scene, 900);
  assert.equal(mapX(v, 0), LEDGER_LEFT);
  for (const sceneX of [0, 12, 300, scene.width]) {
    assert.ok(Math.abs(unmapX(v, mapX(v, sceneX)) - sceneX) < 1e-9);
  }
  // Slot order survives the mapping — the ledger is append order on screen too.
  const xs = scene.slots.map((s) => mapX(v, s.x));
  for (let i = 1; i < xs.length; i++) assert.ok(xs[i] > xs[i - 1]);
});

test("zoom keeps the point under the pointer under the pointer", () => {
  const scene = buildScene(stateOf(scenario("two-sessions-one-role").after));
  const v = fitView(scene, 1000);
  const at = LEDGER_LEFT + 317;                      // somewhere in the ledger area
  const ledgerPoint = unmapX(v, at);
  for (const factor of [ZOOM_STEP, 1 / ZOOM_STEP, 2.5]) {
    const next = zoomView(v, factor, at);
    assert.ok(Math.abs(unmapX(next, at) - ledgerPoint) < 1e-9, "the ledger slid under the pointer");
    assert.ok(Math.abs(next.z - v.z * factor) < 1e-9);
  }
});

test("zoom is bounded in both directions, and every tier stays reachable", () => {
  const scene = buildScene(stateOf(scenario("hold-then-expire").after));
  let v = fitView(scene, 1000);
  for (let i = 0; i < 40; i++) v = zoomView(v, ZOOM_STEP, LEDGER_LEFT);
  assert.equal(v.z, MAX_Z);
  assert.equal(tierFor(slotPixels(v)), "near");
  for (let i = 0; i < 80; i++) v = zoomView(v, 1 / ZOOM_STEP, LEDGER_LEFT);
  assert.equal(v.z, MIN_Z);
  assert.equal(tierFor(slotPixels(v)), "far");
  // And the mid tier is not a gap between two extremes.
  let mid = fitView(scene, 1000);
  while (tierFor(slotPixels(mid)) !== "mid" && mid.z > MIN_Z) mid = zoomView(mid, 1 / ZOOM_STEP, LEDGER_LEFT);
  assert.equal(tierFor(slotPixels(mid)), "mid");
});

test("a pan cannot lose the ledger off-screen", () => {
  const scene = buildScene(stateOf(scenario("first-delivery").after));
  const areaW = 900;
  for (const z of [MIN_Z, 0.5, 1, MAX_Z]) {
    for (const far of [-1e6, 1e6]) {
      const c = clampView({ x: far, z }, scene, areaW);
      // Some of the ledger is still inside the area: its right edge is past the
      // left of the view, and its left edge is before the right of the view.
      assert.ok(c.x < scene.width, "the ledger left the frame to the left");
      assert.ok(c.x + areaW / z > 0, "the ledger left the frame to the right");
    }
  }
});

// ---- the near tier's text ---------------------------------------------------

test("card text is budgeted, never overflowing its slot", () => {
  const scene = buildScene(stateOf(scenario("first-delivery").after));
  const g = scene.lanes[0].glyphs[0];
  for (const budget of [6, 12, 24, 80]) {
    const lines = cardLines(g, budget);
    assert.equal(lines.length, 4);
    for (const l of lines) assert.ok(l.length <= Math.max(6, budget), `"${l}" exceeds ${budget} chars`);
  }
  // A generous budget shows the real strings rather than a truncation.
  const full = cardLines(g, 400);
  assert.ok(full[1] === g.topic);
  assert.ok(full[0].includes(g.priority) && full[0].includes(g.kind));
});

test("the canvas has a text equivalent that states what it draws", () => {
  const state = stateOf(scenario("two-sessions-one-role").after);
  const summary = sceneSummary(buildScene(state), state);
  assert.match(summary, /\d+ envelope\(s\)/);
  assert.match(summary, /2 cursor\(s\)/);
  assert.match(summary, new RegExp(`byte ${state.frontier}`));
});

// ---- presence fades, never claims ------------------------------------------

test("presence decays against the caller's clock", () => {
  const dto = scenario("first-delivery").after;
  const state = stateOf(dto);
  const seen = state.presence.filter((p) => p.lastSeenAtMs !== null);
  if (seen.length === 0) return;                       // no dispatch half in this fixture
  const fresh = buildScene(state, state.seededAtMs ?? 0);
  const old = buildScene(state, (state.seededAtMs ?? 0) + 30 * 60_000);
  const tiers = (s: ReturnType<typeof buildScene>) => s.lanes.flatMap((l) => l.tracks.map((t) => t.presence));
  assert.ok(tiers(fresh).includes("active"));
  assert.ok(tiers(old).every((t) => t === "stale" || t === "unknown"));
});

test("scene geometry is a pure function of state", () => {
  const state = stateOf(scenario("hold-then-expire").after);
  assert.deepEqual(JSON.stringify(buildScene(state)), JSON.stringify(buildScene(state)));
  // …and of nothing else: a slot is one SLOT_W wide whatever the store's bytes.
  const scene = buildScene(state);
  for (const s of scene.slots) assert.equal(s.w, SLOT_W);
});
