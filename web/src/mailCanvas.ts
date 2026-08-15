import type {
  MailState, MailCursorState, MailLedgerLine, MailLineStatus, MailPresenceTier, MailSession,
} from "./mail.ts";
import { lineStatus, projectCursor, rolesOf, presenceTier, sameSession, clampField } from "./mail.ts";

// mail-canvas, the geometry half (ADR-0016 decision 14, roadmap item 21 slice
// 4). `buildScene(state)` turns the reducer's model into coordinates; the
// island renders those coordinates as SVG and does nothing else to them. No
// DOM, no React, no clock — which is what lets the layout be tested at the
// same altitude the reducer is, and what keeps "is this picture right?"
// answerable without a browser.
//
// The picture is the MECHANISM, not a mailbox metaphor:
//
//   * The LEDGER IS THE SPINE. Mail never moves on this bus — an envelope is
//     appended once and stays at its offset forever; cursors move past it. So
//     the x axis is the ledger in append order and nothing in the drawing ever
//     slides along it except a cursor.
//   * A LANE PER `to` ROLE, hanging off the spine. An envelope is drawn in its
//     recipient's lane at its ledger x, joined to the spine by a drop line —
//     the same envelope cannot appear twice, because on the ledger it is one
//     line.
//   * A TRACK PER CURSOR inside the lane: one session reading that role. The
//     cursor is a marker at its offset; held mail is marked UNDER the envelope
//     it belongs to with its TTL countdown, and expired mail is marked spent.
//     Two sessions on one role are two tracks at two positions — the case that
//     forced the trail to name the session an advance moved.
//
// The x axis is SLOT-uniform, not byte-uniform: line i occupies slot i, and a
// byte offset inside a line interpolates within its slot. Byte-proportional
// spacing would let one 128KiB envelope swallow the whole ledger while a
// hundred small ones vanished, and the questions this view answers ("what is
// pending, where is each cursor, what got held") are ordinal, never metric.
// Offsets still map EXACTLY at line boundaries, which is where every cursor
// position, frontier, and delivery actually sits.

// ---- the coordinate space ---------------------------------------------------

/** Scene units. One unit is one CSS pixel at scale 1; the viewBox is what makes
 * that a lie in a controlled way. */
export const SLOT_W = 132;
export const SLOT_PAD = 12;          // breathing room inside a slot
export const PAD_TOP = 16;
export const SPINE_H = 20;
export const LANE_GAP = 22;
export const LANE_HEAD_H = 26;
export const GLYPH_H = 68;
export const TRACK_H = 28;
export const LANE_HEAD_W = 168;      // the fixed left gutter the role labels sit in
export const PAD_X = 16;             // a little air before the first slot
/** Room past the last slot for the frontier's own label — the end of the
 * ledger is a fact worth reading, and a clipped one reads as a bug. */
export const PAD_RIGHT = 150;
/** Where the ledger area begins inside the canvas: the gutter, then a gap. */
export const LEDGER_LEFT = LANE_HEAD_W + 14;

/** Zoom tiers, in rendered pixels per ledger slot — the honest unit for
 * semantic zoom, because what decides whether a card can be read is how much
 * SCREEN a slot owns, not an abstract scale factor. NEAR begins where a slot
 * owns at least its natural width, which is exactly when a four-line card fits
 * inside the glyph at constant text size (the alternative — scaling text with
 * the zoom — is what semantic zoom exists to avoid). */
export const TIER_FAR_MAX = 40;
export const TIER_MID_MAX = SLOT_W;

export type MailTier = "far" | "mid" | "near";

export function tierFor(slotPx: number): MailTier {
  if (!Number.isFinite(slotPx) || slotPx < TIER_FAR_MAX) return "far";
  return slotPx < TIER_MID_MAX ? "mid" : "near";
}

/** What the canvas is looking at. ONE axis moves.
 *
 * The bus is one-dimensional — a ledger read left to right — so pan and zoom
 * are `x` (a scroll along it, in scene units) and `z` (how many pixels a scene
 * unit gets). Everything vertical is laid out in CSS pixels and stays there:
 * lanes, tracks, glyph heights, every label.
 *
 * That split is deliberate, and it was arrived at by drawing the other one
 * first. A uniformly scaled `viewBox` (the ADR's sketch) scales the CHROME with
 * the content: the pinned role gutter is either a fixed patch of scene — which
 * shrinks under its own constant-size labels until one lane's text prints
 * inside the next — or a fixed patch of screen, which then grows over the
 * ledger it is supposed to sit beside. There is no width that is both. Scaling
 * only the ledger axis removes the conflict at the root: chrome is measured in
 * pixels because it is chrome, and no text is ever scaled, which is what
 * semantic zoom is for in the first place. It also means the view can never
 * lose a lane off the top — there is no vertical pan to get lost in; a bus with
 * more roles simply makes a taller canvas, and the page scrolls the way a page
 * does. */
export type MailView = { x: number; z: number };

export const MIN_Z = 0.06;
export const MAX_Z = 3;

// ---- the scene --------------------------------------------------------------

/** One position on the spine. `unknown` slots are regions the picture has never
 * held — everything below a `?since=` snapshot, and any hole the reducer
 * flagged — drawn as a break rather than closed up, because closing it up would
 * draw a continuous ledger that does not exist. */
export type MailSlot = {
  index: number;
  x: number;
  w: number;
  offset: number;
  bytes: number;
  kind: "line" | "unknown";
  line: MailLedgerLine | null;
};

/** A glyph's standing. `MailLineStatus` is per-CURSOR; a lane may have two
 * cursors disagreeing about one envelope, so the glyph shows the most-pending
 * of them (fresh > held > expired > delivered > passed) and carries the whole
 * per-cursor breakdown for the detail card. `no-reader` is the lane's own case:
 * nothing holds a cursor on this role, so no standing exists at all — which is
 * a fact about the bus, not a rendering failure. */
export type MailGlyphStatus = MailLineStatus | "no-reader";

const STATUS_RANK: Record<string, number> = {
  fresh: 6, held: 5, expired: 4, torn: 3, unreadable: 3, delivered: 2, passed: 1, unknown: 0, foreign: 0,
};

export type MailGlyph = {
  offset: number;
  x: number;
  w: number;
  id: string;
  kind: string;
  topic: string;
  priority: string;
  ttlDeliveries: number;
  from: string;
  bytes: number;
  status: MailGlyphStatus;
  perCursor: { key: string; session: MailSession; status: MailLineStatus }[];
  /** A malformed line (stepped over and counted) or an unterminated tail: both
   * are drawn in the lane they would have belonged to only when the ledger can
   * say — an unreadable line names no role, so it lives on the spine alone. */
  torn: boolean;
};

/** One held/expired item under a cursor track, aligned with its envelope. */
export type MailMark = {
  offset: number;
  x: number;
  w: number;
  status: "held" | "expired";
  opportunities: number;
  ttlDeliveries: number;
  id: string;
};

export type MailTrack = {
  key: string;
  role: string;
  session: MailSession;
  sessionLabel: string;
  y: number;
  /** The cursor's x, or null when the picture does not know its position. */
  x: number | null;
  offset: number | null;
  deliveries: number | null;
  lastDeliveredId: string | null;
  presence: MailPresenceTier;
  marks: MailMark[];
  pendingCount: number;
  expiredCount: number;
  reanchored: boolean;
  uncertain: string | null;
  /** True when the cursor sits at the store's end: nothing is pending ahead. */
  atFrontier: boolean;
};

export type MailLane = {
  role: string;
  y: number;
  h: number;
  glyphY: number;
  glyphs: MailGlyph[];
  tracks: MailTrack[];
  counts: { pending: number; held: number; expired: number; fresh: number; envelopes: number };
  hasReader: boolean;
};

export type MailScene = {
  width: number;
  height: number;
  slots: MailSlot[];
  lanes: MailLane[];
  spineY: number;
  laneTop: number;
  frontierX: number;
  /** Ledger lines the spine holds that belong to no lane: malformed ones (no
   * role to file them under) and the unterminated tail. */
  spineOnly: { offset: number; x: number; w: number; kind: "unreadable" | "torn" }[];
  empty: boolean;
};

// ---- building ---------------------------------------------------------------

/** Lay the whole bus out. Deterministic: same state ⇒ same numbers, which is
 * what makes the layout testable and a screenshot comparable.
 *
 * `nowMs` is the clock presence decays against — the CALLER's, never a wall
 * clock (the reducer's rule, so no browser reconciles two clocks). It defaults
 * to the last input the reducer saw, which is what makes the scene a pure
 * function of state: every poll re-seeds with a fresh `atMs` and fresh
 * server-reported ages, so presence decays with the snapshots rather than with
 * React's render cadence. */
export function buildScene(state: MailState, nowMs?: number): MailScene {
  const now = nowMs ?? state.lastInputAtMs ?? state.seededAtMs ?? 0;
  const slots = buildSlots(state);
  const width = PAD_X + Math.max(slots.length, 1) * SLOT_W + PAD_RIGHT;
  const spineY = PAD_TOP;
  const laneTop = spineY + SPINE_H + LANE_GAP;

  const xOf = (offset: number) => xForOffsetIn(slots, offset);
  const roles = rolesOf(state);
  const lanes: MailLane[] = [];
  let y = laneTop;

  for (const role of roles) {
    const cursors = state.cursors.filter((c) => c.role === role);
    const glyphY = y + LANE_HEAD_H;
    const glyphs: MailGlyph[] = [];

    for (const slot of slots) {
      const line = slot.line;
      if (line === null || line.envelope === null || line.envelope.to !== role) continue;
      const e = line.envelope;
      const perCursor = cursors.map((c) => ({
        key: cursorKey(c), session: c.session, status: lineStatus(state, c, line),
      }));
      glyphs.push({
        offset: line.offset,
        x: slot.x + SLOT_PAD / 2,
        w: slot.w - SLOT_PAD,
        id: e.id,
        kind: e.kind,
        topic: e.topic,
        priority: e.priority,
        ttlDeliveries: e.ttlDeliveries,
        from: e.from.agent === "" ? e.from.harness : `${e.from.agent}@${e.from.harness}`,
        bytes: line.bytes,
        status: cursors.length === 0 ? "no-reader" : summarize(perCursor.map((p) => p.status)),
        perCursor,
        torn: !line.terminated,
      });
    }

    const tracks: MailTrack[] = cursors.map((c, i) => {
      const p = projectCursor(c);
      const marks: MailMark[] = [
        ...p.pending.filter((m) => m.seenAt !== null).map((m) => ({ ...m, status: "held" as const })),
        ...p.expired.map((m) => ({ ...m, status: "expired" as const })),
      ]
        .sort((a, b) => a.offset - b.offset)
        .map((m) => {
          const slot = slots.find((s) => s.offset === m.offset);
          return {
            offset: m.offset,
            x: (slot?.x ?? xOf(m.offset)) + SLOT_PAD / 2,
            w: (slot?.w ?? SLOT_W) - SLOT_PAD,
            status: m.status,
            opportunities: m.opportunities,
            ttlDeliveries: m.ttlDeliveries,
            id: m.id,
          };
        });
      return {
        key: cursorKey(c),
        role,
        session: c.session,
        sessionLabel: sessionLabel(c.session),
        y: glyphY + GLYPH_H + i * TRACK_H,
        x: c.offset === null ? null : xOf(c.offset),
        offset: c.offset,
        deliveries: c.deliveries,
        lastDeliveredId: c.lastDeliveredId,
        presence: presenceOf(state, c.session, now),
        marks,
        pendingCount: p.pending.length,
        expiredCount: p.expired.length,
        reanchored: c.reanchored,
        uncertain: c.uncertain,
        atFrontier: c.offset !== null && c.offset >= state.frontier,
      };
    });

    const counts = {
      envelopes: glyphs.length,
      fresh: glyphs.filter((g) => g.status === "fresh").length,
      held: glyphs.filter((g) => g.status === "held").length,
      expired: glyphs.filter((g) => g.status === "expired").length,
      pending: glyphs.filter((g) => g.status === "fresh" || g.status === "held").length,
    };

    const h = LANE_HEAD_H + GLYPH_H + Math.max(tracks.length, 1) * TRACK_H;
    lanes.push({ role, y, h, glyphY, glyphs, tracks, counts, hasReader: cursors.length > 0 });
    y += h + LANE_GAP;
  }

  const spineOnly = slots
    .filter((s) => s.line !== null && (s.line.envelope === null || !s.line.terminated))
    .map((s) => ({
      offset: s.offset,
      x: s.x + SLOT_PAD / 2,
      w: s.w - SLOT_PAD,
      kind: (s.line!.terminated ? "unreadable" : "torn") as "unreadable" | "torn",
    }));

  return {
    width,
    height: Math.max(y - LANE_GAP + PAD_TOP, laneTop + GLYPH_H),
    slots,
    lanes,
    spineY,
    laneTop,
    frontierX: xForOffsetIn(slots, state.frontier),
    spineOnly,
    empty: slots.length === 0 && lanes.length === 0,
  };
}

/** The spine's slots: every line the picture holds, plus an explicit `unknown`
 * slot wherever the ledger is not contiguous — below a partial snapshot's
 * `since`, and across any hole. A hole drawn as a break is the whole point:
 * the reducer refuses to guess what was in it, and so does the picture. */
function buildSlots(state: MailState): MailSlot[] {
  const slots: MailSlot[] = [];
  let x = PAD_X;
  const push = (s: Omit<MailSlot, "index" | "x" | "w">) => {
    slots.push({ ...s, index: slots.length, x, w: SLOT_W });
    x += SLOT_W;
  };

  let expected = 0;
  if (state.since > 0) {
    push({ offset: 0, bytes: Math.max(state.since - 1, 0), kind: "unknown", line: null });
    expected = state.since;
  }
  for (const line of state.lines) {
    if (line.offset > expected) {
      push({ offset: expected, bytes: line.offset - expected - 1, kind: "unknown", line: null });
    }
    push({ offset: line.offset, bytes: line.bytes, kind: "line", line });
    expected = line.offset + line.bytes + 1;
  }
  return slots;
}

/** Scene x for a ledger byte offset. Exact at line boundaries — where cursors,
 * frontiers and deliveries all sit — and linear inside a slot for anything in
 * between (which only a malformed offset can be). */
export function xForOffset(scene: MailScene, offset: number): number {
  return xForOffsetIn(scene.slots, offset);
}

function xForOffsetIn(slots: MailSlot[], offset: number): number {
  if (slots.length === 0) return PAD_X;
  if (offset <= slots[0].offset) return slots[0].x;
  for (const s of slots) {
    const span = s.bytes + 1;
    if (offset < s.offset + span) {
      if (offset <= s.offset) return s.x;
      return s.x + ((offset - s.offset) / span) * s.w;
    }
  }
  const last = slots[slots.length - 1];
  return last.x + last.w;
}

export function cursorKey(c: { role: string; session: MailSession }): string {
  return `${c.role} ${c.session ?? ""}`;
}

export function sessionLabel(session: MailSession): string {
  return session === null ? "sessionless" : session;
}

function presenceOf(state: MailState, session: MailSession, nowMs: number): MailPresenceTier {
  if (session === null) return "unknown";
  const p = state.presence.find((e) => e.session === session);
  // Presence DECAYS and is never claimed: an entry the picture has no sighting
  // for is `unknown`, which the canvas draws as a fade, not as "gone".
  return p === undefined ? "unknown" : presenceTier(p, nowMs);
}

function summarize(statuses: MailLineStatus[]): MailGlyphStatus {
  let best: MailLineStatus = "passed";
  for (const s of statuses) if ((STATUS_RANK[s] ?? 0) > (STATUS_RANK[best] ?? 0)) best = s;
  return best;
}

// ---- the near tier's card text ----------------------------------------------

/** The lines of an envelope card, budgeted to the width a slot actually owns.
 * Truncation happens HERE — in a pure function with a character budget — rather
 * than by letting SVG text overflow its glyph, because overflowing text is the
 * one failure a screenshot shows and a test does not. */
export function cardLines(g: MailGlyph, maxChars: number): string[] {
  const budget = Math.max(6, Math.floor(maxChars));
  const cut = (s: string) => (s.length <= budget ? s : s.slice(0, Math.max(1, budget - 1)) + "…");
  return [
    cut(`${g.priority} · ${g.kind}`),
    cut(g.topic),
    cut(g.from),
    cut(`${clampField(g.id)} · ttl ${g.ttlDeliveries}`),
  ];
}

/** A one-line description of the whole picture, for the canvas's aria-label —
 * an SVG bus is unreadable to a screen reader without one. */
export function sceneSummary(scene: MailScene, state: MailState): string {
  if (scene.empty) return "The mail bus is empty: no envelopes on the ledger and no cursors.";
  const envelopes = scene.lanes.reduce((n, l) => n + l.counts.envelopes, 0);
  const cursors = scene.lanes.reduce((n, l) => n + l.tracks.length, 0);
  const pending = scene.lanes.reduce((n, l) => n + l.counts.pending, 0);
  return `The mail bus: ${envelopes} envelope(s) on the ledger across ${scene.lanes.length} role(s), `
    + `${cursors} cursor(s) reading, ${pending} pending. The ledger ends at byte ${state.frontier}.`;
}

// ---- the view: one axis moves ----------------------------------------------

/** The natural width of the ledger area's content, in scene units. */
export function ledgerWidth(scene: MailScene): number {
  return scene.width;
}

/** Open on the whole ledger if it fits, at natural size if it does not need
 * shrinking. The TIER this lands on is therefore a fact about the bus, not a
 * preference: a nine-envelope store opens on cards, a two-hundred-envelope
 * store opens on role pulses, and each is the right first answer for its size.
 * Never zoomed PAST natural size — blowing a two-envelope bus up to fill the
 * width would imply a precision the ledger does not have. */
export function fitView(scene: MailScene, areaW: number): MailView {
  const z = Math.min(1, Math.max(areaW, 1) / Math.max(scene.width, 1));
  return { x: 0, z: Math.min(MAX_Z, Math.max(MIN_Z, z)) };
}

/** Scene x → canvas x. The ONE place the ledger's geometry meets the screen. */
export function mapX(view: MailView, sceneX: number): number {
  return LEDGER_LEFT + (sceneX - view.x) * view.z;
}

/** Canvas x → scene x — the inverse, for anchoring a zoom under the pointer. */
export function unmapX(view: MailView, canvasX: number): number {
  return view.x + (canvasX - LEDGER_LEFT) / view.z;
}

/** Pixels of screen one ledger slot owns — the input to `tierFor`. */
export function slotPixels(view: MailView): number {
  return SLOT_W * view.z;
}

/** The canvas is exactly as tall as the scene: the vertical axis is never
 * scaled, so there is no fit to compute and never any slack to centre. A bus
 * with many roles makes a taller canvas and the page scrolls — which beats
 * hiding lanes behind a vertical pan the operator has to discover. */
export function canvasHeight(scene: MailScene): number {
  return Math.max(scene.height, 200);
}

export const ZOOM_STEP = 1.4;

/** Zoom about a point on the canvas: the ledger position under the pointer
 * stays under the pointer, which is the only zoom that does not feel like the
 * page fighting back. */
export function zoomView(view: MailView, factor: number, canvasX: number): MailView {
  const at = unmapX(view, canvasX);
  const z = Math.min(MAX_Z, Math.max(MIN_Z, view.z * factor));
  return { z, x: at - (canvasX - LEDGER_LEFT) / z };
}

/** Keep the ledger reachable: a pan can run a little past either end (so the
 * frontier and offset 0 are not glued to the frame) and no further. */
export function clampView(view: MailView, scene: MailScene, areaW: number): MailView {
  // 60px of overscroll — but never more than half the ledger, or a bus zoomed
  // far out (where 60px is a great many scene units) could be panned clean off
  // its own canvas with nothing but `fit` to get it back.
  const slack = Math.min(60 / view.z, scene.width / 2);
  const maxX = Math.max(0, scene.width - Math.max(areaW, 1) / view.z);
  return { ...view, x: Math.min(Math.max(view.x, -slack), maxX + slack) };
}

/** The cursor a lane's track belongs to, for the detail aside. */
export function trackCursor(state: MailState, track: MailTrack): MailCursorState | null {
  return state.cursors.find((c) => c.role === track.role && sameSession(c.session, track.session)) ?? null;
}
