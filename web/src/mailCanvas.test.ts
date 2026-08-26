import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import type { MailDto } from "./api.gen.ts";
import type { TrailLine } from "./store.ts";
import {
  seedMail, reduceMail, projectCursor, lineStatus, rolesOf, accepts, roleOf, type MailState,
} from "./mail.ts";
import {
  buildScene, canvasHeight, cardLines, cardMetrics, clampView, cursorKey, fitView, mapX, sceneSummary,
  slotPixels, threadLine, tierFor, unmapX, xForOffset, zoomView,
  GLYPH_H, LEDGER_LEFT, MAX_Z, MIN_Z, PAD_X, SLOT_W, SLOT_PAD, TIER_FAR_MAX, TIER_MID_MAX, ZOOM_STEP,
} from "./mailCanvas.ts";
import type { MailLane } from "./mailCanvas.ts";

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
      // A lane is a ROLE and a unicast lives in it (ADR-0018 d4): membership is
      // the address's role half, never the address whole — the old comparison
      // gave `main@laptop-a` a lane of its own.
      const cursors = state.cursors.filter((c) => c.role === lane.role);
      const mine = state.lines.filter((l) => l.envelope !== null && roleOf(l.envelope.to) === lane.role);

      // Exactly this role's mail, in ledger order, at its ledger position.
      assert.deepEqual(lane.glyphs.map((g) => g.offset), mine.map((l) => l.offset));
      for (const g of lane.glyphs) {
        const line = mine.find((l) => l.offset === g.offset)!;
        assert.equal(g.id, line.envelope!.id);
        assert.equal(g.address, line.envelope!.to);
        assert.ok(Math.abs(g.x - (xForOffset(scene, g.offset) + SLOT_PAD / 2)) < 1e-9,
          "a glyph must sit at its own ledger offset");
        // Per-cursor standing is the reducer's, verbatim — the canvas never
        // computes a status of its own — over exactly the cursors that MAY read
        // this envelope. A sibling that cannot is absent rather than listed as
        // `foreign`: a unicast hangs on its own sub-lane and nowhere else.
        const readers = cursors.filter((c) => accepts(c, line.envelope!.to));
        assert.deepEqual(
          g.perCursor,
          readers.map((c) => ({ key: cursorKey(c), session: c.session, status: lineStatus(state, c, line) })));
        assert.ok(g.perCursor.every((p) => p.status !== "foreign"), "a track that cannot read it is not shown holding it");
        if (readers.length === 0) assert.equal(g.status, "no-reader");
      }

      // Envelopes never overlap: one ledger line, one slot.
      const sorted = [...lane.glyphs].sort((a, b) => a.x - b.x);
      for (let i = 1; i < sorted.length; i++)
        assert.ok(sorted[i].x >= sorted[i - 1].x + sorted[i - 1].w, "glyphs overlap");

      // One track per cursor, at the position the reducer holds, carrying
      // exactly the held/expired set `projectCursor` reports.
      // One track per cursor — order is the canvas's (declared mailboxes first),
      // membership is the reducer's.
      assert.deepEqual([...lane.tracks.map((t) => t.key)].sort(), cursors.map(cursorKey).sort());
      assert.deepEqual(
        lane.tracks.map((t) => t.named),
        [...lane.tracks].map((t) => cursors.find((c) => cursorKey(c) === t.key)!.named));
      assert.ok(lane.tracks.every((t, i, all) => i === 0 || !(t.named && !all[i - 1].named)),
        "declared mailboxes sort ahead of windows");
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
    // Every scenario here is BROADCAST, so every mark has a ttl. Asserted
    // rather than silenced with `!`: a null reaching this loop would mean a
    // unicast envelope drew a countdown, which is the thing ADR-0018 d5 says
    // has no countdown to draw.
    assert.ok(m.ttlDeliveries !== null, "a broadcast mark must carry its ttl");
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
  assert.ok(expired[0].ttlDeliveries !== null, "only ttl'd mail can be spent at all");
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

/** Fold a scenario's real trail lines at successive instants. */
function foldTrail(state: MailState, lines: string[], startMs = 3000): MailState {
  return lines.reduce((acc, text, i) =>
    reduceMail(acc, { kind: "line", line: JSON.parse(text) as TrailLine, atMs: startMs + i }), state);
}

// ---- the choreography's inputs (slice 5) ------------------------------------
//
// The canvas computes no motion of its own: an envelope animates because the
// REDUCER says it arrived on the trail, and a cursor jumps rather than slides
// because the reducer says the last thing that happened to it was a re-anchor.
// Both facts are carried through the scene so the styles have something to key
// on and the e2e has something to assert that is not a timing.

test("an envelope from the snapshot is not an arrival; one folded from the trail is", () => {
  const sc = golden.scenarios.find((s) => s.name === "first-delivery")!;
  const seeded = seedMail(sc.before, 0);
  for (const g of buildScene(seeded).lanes.flatMap((l: MailLane) => l.glyphs))
    assert.equal(g.arrival, "snapshot");   // a photograph contains no arrivals

  // Fold the real trail: anything it APPENDS arrived while we were watching.
  const after = foldTrail(seeded, sc.trail);
  const appended = after.lines.filter((l) => l.source === "trail").map((l) => l.offset);
  const glyphs = buildScene(after).lanes.flatMap((l: MailLane) => l.glyphs);
  for (const g of glyphs)
    assert.equal(g.arrival, appended.includes(g.offset) ? "trail" : "snapshot");
});

test("a re-seed replays no arrivals — a resync is not the bus moving", () => {
  const sc = golden.scenarios.find((s) => s.name === "first-delivery")!;
  const live = foldTrail(seedMail(sc.before, 0), sc.trail);
  const reseeded = seedMail(sc.after, 100, live);
  for (const g of buildScene(reseeded).lanes.flatMap((l: MailLane) => l.glyphs))
    assert.equal(g.arrival, "snapshot");
});

test("a track carries the motion that moved it, and a re-anchor is not an advance", () => {
  const advanced = golden.scenarios.find((s) => s.name === "first-delivery")!;
  const a = foldTrail(seedMail(advanced.before, 0), advanced.trail);
  const moved = buildScene(a).lanes.flatMap((l: MailLane) => l.tracks).filter((t) => t.motion !== null);
  assert.ok(moved.length > 0);
  assert.ok(moved.every((t) => t.motion !== "reanchor"));

  // Caught at the moment it lands: a later advance legitimately overwrites the
  // motion, and it is the re-anchoring INSTANT that must not draw as a slide.
  const re = golden.scenarios.find((s) => s.name === "reanchor-preserves-deliveries")!;
  let r = seedMail(re.before, 0);
  let jumped = false;
  re.trail.forEach((text, i) => {
    r = reduceMail(r, { kind: "line", line: JSON.parse(text) as TrailLine, atMs: 3000 + i });
    if (buildScene(r).lanes.flatMap((l: MailLane) => l.tracks).some((t) => t.motion === "reanchor"))
      jumped = true;
  });
  assert.ok(jumped,
    "a re-anchored cursor must be distinguishable from one that read forward — " +
    "drawn as a slide it would depict a cursor reading backwards");
});

// ---- threads and pokes, laid out (slice `thread-canvas`) -------------------------
//
// The two new things this layer draws are the two a screenshot cannot check on
// its own: a link is only right if both ends really are the request's glyph and
// the answer's glyph (a curve between the WRONG pair looks exactly as good),
// and a poke is only right if it sits over the envelope it named. Both are
// joins, so both are tested here against the reducer's own reading.

const threadState = () => {
  const sc = scenario("nudged-and-late-answer");
  let s = seedMail(sc.before, 500);
  sc.trail.forEach((text, i) => {
    s = reduceMail(s, { kind: "line", line: JSON.parse(text) as TrailLine, atMs: 1000 + i });
  });
  return s;
};
const glyphOf = (scene: ReturnType<typeof buildScene>, id: string) => {
  for (const lane of scene.lanes) for (const g of lane.glyphs) if (g.id === id) return { g, lane };
  throw new Error(`no glyph for ${id}`);
};

test("threads: both answers link back to the request ACROSS lanes, and only the second is late", () => {
  const state = threadState();
  const scene = buildScene(state);
  const req = glyphOf(scene, "req-1"), ans1 = glyphOf(scene, "ans-1"), ans2 = glyphOf(scene, "ans-2");

  // The case the link exists for: the request is on `reviewer`, the answers on
  // `main`. Inside one lane the ledger's own order would already show it.
  assert.equal(req.lane.role, "reviewer");
  assert.equal(ans1.lane.role, "main");
  assert.equal(scene.threads.length, 2);

  for (const [ans, late] of [[ans1, false], [ans2, true]] as const) {
    const link = scene.threads.find((t) => t.answerOffset === ans.g.offset)!;
    assert.equal(link.requestOffset, req.g.offset, "a link's request end is the envelope inReplyTo names");
    assert.equal(link.late, late);
    // Anchored at the glyph centres, in the lanes the glyphs are actually in —
    // the whole failure mode here is a curve drawn to a plausible wrong place.
    assert.equal(link.x1, req.g.x + req.g.w / 2);
    assert.equal(link.x2, ans.g.x + ans.g.w / 2);
    // Lane tops, so the renderer can put the curve on each card's top EDGE at
    // whatever height the tier draws — never through the card's own text.
    assert.equal(link.y1, req.lane.glyphY);
    assert.equal(link.y2, ans.lane.glyphY);
    assert.notEqual(link.y1, link.y2, "the two ends are in different lanes and must not share a y");
  }
});

test("threads: an answer whose request is off this picture's ledger draws NO link, and keeps its own tag", () => {
  const state = threadState();
  // A `?since=` window that starts above the request: the answers remain, the
  // request does not. The picture must decline to anchor a curve at a position
  // it would have to invent — the same refusal a ledger hole gets.
  const cut = state.lines.find((l) => l.envelope?.id === "ans-1")!.offset;
  const narrowed = { ...state, since: cut, lines: state.lines.filter((l) => l.offset >= cut) };
  const scene = buildScene(narrowed);

  assert.deepEqual(scene.threads, []);
  assert.equal(glyphOf(scene, "ans-2").g.thread?.standing, "late", "the tag is the envelope's own reading, not the link's");
});

test("nudges: the poke lands on the ROLE's lane over the envelope it named, and is never a track mark", () => {
  const state = threadState();
  const scene = buildScene(state);
  const reviewer = scene.lanes.find((l) => l.role === "reviewer")!;
  const req = glyphOf(scene, "req-1");

  assert.equal(reviewer.nudges.length, 1);
  const mark = reviewer.nudges[0];
  assert.equal(mark.offset, req.g.offset);
  assert.equal(mark.x, req.g.x, "a poke is drawn against its envelope, not at a time of its own");
  assert.equal(mark.w, req.g.w);
  assert.equal(mark.count, 1);
  assert.equal(mark.last.role, "reviewer");
  assert.equal(reviewer.nudgesUnplaced, 0);

  // It belongs to the lane and to nothing that reads: a track mark is a
  // cursor's arithmetic about mail it passed over, and a nudge is the fact that
  // no cursor came at all.
  for (const t of reviewer.tracks) assert.deepEqual(t.marks.filter((m) => m.offset === req.g.offset), []);
  // The other lane's mail is not this lane's poke.
  assert.deepEqual(scene.lanes.find((l) => l.role === "main")!.nudges, []);
  assert.equal(glyphOf(scene, "req-1").g.nudges.length, 1);
  assert.equal(glyphOf(scene, "ans-1").g.nudges.length, 0);
});

test("nudges: two pokes about one envelope are ONE mark that counts them; an unplaceable poke is counted, never drawn", () => {
  const state = threadState();
  const req = state.lines.find((l) => l.envelope?.id === "req-1")!;
  const nudge = state.nudges[0];
  const scene = buildScene({
    ...state,
    nudges: [
      nudge,
      { ...nudge, dispatchId: "n-2", reason: "still unread", offsets: [req.offset] },
      { ...nudge, dispatchId: "n-3", envelopeIds: ["gone-1"], offsets: [null] },
    ],
  });
  const reviewer = scene.lanes.find((l) => l.role === "reviewer")!;

  assert.equal(reviewer.nudges.length, 1, "one envelope, one mark");
  assert.equal(reviewer.nudges[0].count, 2, "the second poke means the first did not work — that is the fact to show");
  assert.equal(reviewer.nudges[0].last.reason, "still unread", "the mark speaks for the most recent poke");
  assert.equal(reviewer.nudgesUnplaced, 1, "a poke the picture cannot place is counted, so the lane can SAY so");
});

test("pickup: an advance under a nudge's own dispatch id is a ROBOT pickup; anything else is a human's", () => {
  // N6 as built. The signal is the DISPATCH, not a vehicle: a woken turn does
  // its pickup through the ordinary hooks, so its `mail.deliver` says `inject`
  // like anybody's, and the only thing that joins the poke to the turn is the
  // id the nudge raised it under (d10).
  const state = threadState();
  const main = state.cursors.find((c) => c.role === "main")!;
  assert.equal(buildScene(state).lanes.find((l) => l.role === "main")!.tracks[0].pickup, "human");

  const dispatchId = main.lastAdvance!.dispatchId;
  assert.ok(dispatchId !== null, "the golden's digest advance carries the dispatch it ran under");
  const woken = buildScene({ ...state, nudges: [{ ...state.nudges[0], dispatchId }] });
  assert.equal(woken.lanes.find((l) => l.role === "main")!.tracks[0].pickup, "robot");

  // A cursor nothing has picked up from yet says nothing rather than guessing.
  const untouched = buildScene({
    ...state,
    cursors: state.cursors.map((c) => (c.role === "main" ? { ...c, lastAdvance: null } : c)),
  });
  assert.equal(untouched.lanes.find((l) => l.role === "main")!.tracks[0].pickup, null);
});

test("the card grows a FIFTH line for a thread, and the five-line card still fits inside its glyph", () => {
  const state = threadState();
  const scene = buildScene(state);
  const { g: req } = glyphOf(scene, "req-1");
  const { g: late } = glyphOf(scene, "ans-2");
  const { g: plain } = glyphOf(scene, "ans-1");

  assert.equal(threadLine(req), "answered by ans-1 · hop 1");
  assert.equal(threadLine(late), "late answer to req-1 · hop 2");
  assert.match(cardLines(req, 60).at(-1)!, /^answered by/);
  assert.equal(cardLines(req, 60).length, 5);
  assert.equal(cardLines(plain, 60).length, 5);

  // The one failure a screenshot shows and a test does not: text printed
  // through the card's own border. The glyph's inner height is GLYPH_H - 10.
  const m = cardMetrics(5);
  // Baseline plus descender: a line whose baseline is exactly the glyph's
  // bottom edge still prints its tails through the border, which is what the
  // first screenshot of this slice showed.
  assert.ok(m.top + 4 * m.step + 4 <= GLYPH_H - 10, "the fifth line, descenders and all, must sit inside the glyph");
  const m4 = cardMetrics(4);
  assert.ok(m4.top + 3 * m4.step <= GLYPH_H - 10, "and so must the four-line card");
  assert.equal(m4.step, m.step, "one step for every card — a thread does not squeeze the text");

  // An envelope in no thread keeps its four lines, and nothing shifts.
  const plainState = stateOf(scenario("appends-only").after);
  const g0 = buildScene(plainState).lanes[0].glyphs[0];
  assert.equal(threadLine(g0), null);
  assert.equal(cardLines(g0, 60).length, 4);
});

test("the spoken summary names what is WAITING on a person: awaiting requests, late answers, pokes raised", () => {
  const state = threadState();
  const said = sceneSummary(buildScene(state), state);
  assert.match(said, /1 late answer\(s\)/);
  assert.match(said, /1 nudge\(s\) raised/);
  assert.doesNotMatch(said, /awaiting/, "req-1 was answered — the summary must not invent an open question");

  const open = seedMail(scenario("nudged-and-late-answer").before, 500);
  assert.match(sceneSummary(buildScene(open), open), /1 request\(s\) awaiting an answer/);
});

// ---- instances: sub-lanes, corpses and the forward link (ADR-0018 d4/d6/d7) -------

test("scene: a unicast hangs in its role's lane, on ONE sub-lane", () => {
  const state = stateOf(scenario("named-instance-and-unicast").after);
  const scene = buildScene(state);

  // One lane, not three: `main@laptop-a` and `main@laptop-b` are addresses on
  // the `main` lane, and a top-level lane per address is the picture this slice
  // replaced.
  assert.deepEqual(scene.lanes.map((l) => l.role), ["main"]);
  const lane = scene.lanes[0];
  const glyph = (id: string) => lane.glyphs.find((g) => g.id === id)!;

  assert.equal(glyph("m-01").instance, null, "a broadcast belongs to the whole lane");
  assert.equal(glyph("u-a").instance, "laptop-a");
  assert.equal(glyph("u-a").address, "main@laptop-a");

  // The broadcast is on both tracks; the unicast is on exactly the one mailbox
  // that may take it; the sibling's is on none — and `no-reader` says so.
  assert.deepEqual(glyph("m-01").perCursor.map((p) => p.session).sort(), ["laptop-a", "s-1"]);
  assert.deepEqual(glyph("u-a").perCursor.map((p) => p.session), ["laptop-a"]);
  assert.deepEqual(glyph("u-b").perCursor, []);
  assert.equal(glyph("u-b").status, "no-reader");
});

test("scene: a declared mailbox is a durable sub-lane and sorts ahead of a window", () => {
  const scene = buildScene(stateOf(scenario("named-instance-and-unicast").after));
  const tracks = scene.lanes[0].tracks;

  assert.deepEqual(tracks.map((t) => [t.address, t.named]),
    [["main@laptop-a", true], ["main@s-1", false]]);
  // A track's y is its position in the lane: declared first, and the geometry
  // agrees with the order rather than only the list.
  assert.ok(tracks[0].y < tracks[1].y);
});

test("scene: a dead mailbox is drawn dead — the watcher's word, with what it still holds", () => {
  // The state BETWEEN the nudge and the reap: the corpse still has standing, so
  // it is still a track, and the picture can say why it is greyed.
  const sc = scenario("dead-mailbox-forwarded-and-reaped");
  let s = stateOf(sc.before);
  let nudged: MailState | null = null;
  sc.trail.forEach((text, i) => {
    const line = JSON.parse(text) as TrailLine;
    if ((line as { evt?: string }).evt === "mail.reap") return;   // stop short of the disposal
    s = reduceMail(s, { kind: "line", line, atMs: 2000 + i });
    if (nudged === null && s.nudges.length > 0) nudged = s;
  });
  assert.ok(nudged, "the scenario's trail must pass through a dead-mailbox nudge");

  const track = buildScene(s).lanes.find((l) => l.role === "main")!.tracks
    .find((t) => t.address === "main@laptop-a")!;
  assert.match(track.dead!, /dead-mailbox main@laptop-a/);
  assert.equal(track.pendingCount, 1, "…and it says what is stranded in it");
  // What the reaper did about it, on the track it did it to.
  assert.deepEqual(track.forwards.map((f) => [f.source, f.to]), [["main@laptop-a", "maintainer"]]);
});

test("scene: the forward is a link from the corpse's envelope to the live copy", () => {
  const sc = scenario("dead-mailbox-forwarded-and-reaped");
  let s = stateOf(sc.before);
  sc.trail.forEach((text, i) => {
    s = reduceMail(s, { kind: "line", line: JSON.parse(text) as TrailLine, atMs: 2000 + i });
  });
  const scene = buildScene(s);

  const link = scene.forwards[0];
  assert.equal(scene.forwards.length, 1);
  assert.equal(link.source, "main@laptop-a");
  const original = scene.lanes.find((l) => l.role === "main")!.glyphs.find((g) => g.id === "m-02")!;
  const copy = scene.lanes.find((l) => l.role === "maintainer")!.glyphs.find((g) => g.id === "fwd-1")!;
  assert.equal(link.originalOffset, original.offset);
  assert.equal(link.copyOffset, copy.offset);
  // It crosses lanes, which is the whole reason it is drawn at all.
  assert.notEqual(link.y1, link.y2);

  // …and the reaped mailbox leaves no track: its standing is gone, and a
  // cursor drawn for a mailbox that no longer exists is a false picture.
  assert.deepEqual(scene.lanes.find((l) => l.role === "main")!.tracks.map((t) => t.address), []);
});

test("scene: a dead-mailbox nudge marks the CORPSE's lane, not the reaper's", () => {
  const sc = scenario("dead-mailbox-forwarded-and-reaped");
  let s = stateOf(sc.before);
  sc.trail.forEach((text, i) => {
    s = reduceMail(s, { kind: "line", line: JSON.parse(text) as TrailLine, atMs: 2000 + i });
  });
  const scene = buildScene(s);

  // The row's `role` is `reaper` — who was woken — and its `address` is what it
  // is about. There is no reaper lane on this bus, and the mark belongs where
  // the stranded envelope hangs.
  assert.equal(s.nudges[0].role, "reaper");
  assert.equal(scene.lanes.some((l) => l.role === "reaper"), false);
  const lane = scene.lanes.find((l) => l.role === "main")!;
  assert.deepEqual(lane.nudges.map((n) => n.offset),
    [lane.glyphs.find((g) => g.id === "m-02")!.offset]);
  assert.equal(lane.nudgesUnplaced, 0);
});
