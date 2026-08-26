import type {
  MailState, MailCursorEventKind, MailCursorState, MailLedgerLine, MailLineStatus,
  MailNudgeRecord, MailPresenceTier, MailSession, MailThreadView,
} from "./mail.ts";
import {
  lineStatus, projectCursor, rolesOf, presenceTier, sameSession, clampField, threadOf, nudgesFor,
  accepts, addressOf, roleOf, instanceOf, deadNudgeFor, forwardsFrom, nudgeLane,
} from "./mail.ts";
import type { MailForwardRecord } from "./mail.ts";

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
// 82, not 68: the near tier's card grew a FIFTH line with `thread-canvas`, and
// at the old height the five-line card printed its last line through the
// glyph's own bottom border — the exact overflow ADR-0015 d5 opens with, and
// the one a green test suite will happily ship. The alternative was tightening
// the step until five lines fit inside 58px, which buys back the pixels by
// making every card harder to read. 14 more pixels per lane is the cheaper side
// of that trade.
export const GLYPH_H = 82;
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
  /** The envelope's `to`, verbatim — a role for a broadcast, `role@instance`
   * for a UNICAST (ADR-0018 d1). The glyph sits in the role's lane either way;
   * this is what says whether it is the lane's mail or one mailbox's. */
  address: string;
  /** The instance half, or null. Non-null means this envelope hangs on ONE
   * sub-lane: nothing else on this role can read it, and the picture must not
   * draw it as pending for the siblings (d4's named hazard). */
  instance: string | null;
  kind: string;
  topic: string;
  priority: string;
  /** null = unicast: no TTL to count down (ADR-0018 d5). */
  ttlDeliveries: number | null;
  from: string;
  bytes: number;
  status: MailGlyphStatus;
  perCursor: { key: string; session: MailSession; status: MailLineStatus }[];
  /** A malformed line (stepped over and counted) or an unterminated tail: both
   * are drawn in the lane they would have belonged to only when the ledger can
   * say — an unreadable line names no role, so it lives on the spine alone. */
  torn: boolean;
  /** How this envelope entered the picture: `trail` means it ARRIVED while we
   * were watching (a `mail.append` we folded), `snapshot` means it was already
   * on the bus when we read it. The arrival animation keys on this and on
   * nothing else — in particular not on a timestamp, so nothing has to decide
   * how recent "recent" is, and a re-seed (whose lines are all `snapshot`
   * again) correctly replays no arrivals at all. */
  arrival: "snapshot" | "trail";
  /** Where this envelope stands in its thread (ADR-0017 d8), or null when it is
   * in none. READ off the reducer's one index — the canvas joins nothing of its
   * own. `awaiting` and `late` are the two the picture exists to show: a
   * request nobody has answered, and an answer that lost a race it did not
   * know it was in (N5). */
  thread: MailThreadView | null;
  /** Where this envelope was forwarded FROM, when it is a copy (ADR-0018 d8):
   * which envelope, and whose mailbox it was stranded in. The link the scene
   * draws hangs off this and nothing else — it is on the ledger, so it survives
   * a re-seed, a rotation of the trail, and a page that watched nothing. */
  forwardedFrom: { id: string; address: string } | null;
  /** The `mail.nudge` rows that named this envelope (d10). A poke is about
   * mail that has NOT been read, so this is drawn on the envelope and never on
   * a track: no cursor moved, which is the whole reason the poke happened. */
  nudges: MailNudgeRecord[];
};

/** One request→answer join, drawn as a link between two glyphs that are
 * usually in DIFFERENT lanes — which is the point of drawing it at all. A
 * thread is the one relation on this bus that crosses roles, and the ledger's
 * left-to-right order already shows that the answer came after; the link shows
 * WHICH ask it came after, across a lane boundary that otherwise hides it.
 *
 * Built from the ANSWER end, so an answer whose request is off this picture's
 * ledger simply draws no link (and keeps its own tag) rather than anchoring one
 * at a made-up position. */
export type MailThreadLink = {
  requestOffset: number;
  answerOffset: number;
  x1: number; x2: number;
  /** The two ends' LANE tops (`MailLane.glyphY`), not their glyph tops: a
   * glyph's height is the TIER's, which is a fact about the view and not about
   * the scene. The renderer offsets these by the tier's glyph height so the
   * curve leaves the top EDGE of each card — anchored at the centre it ran
   * straight through the card's own text, which the first screenshot of this
   * slice showed and no test would have. */
  y1: number; y2: number;
  /** N5: this answer named a request another answer had already closed. Drawn
   * differently, never hidden — the second answer may be the better one. */
  late: boolean;
};

/** One forward, drawn as a link from the ORIGINAL envelope to the COPY that
 * carries it on (ADR-0018 d6/d8). Like a thread link it usually crosses lanes,
 * and for a better reason: the whole point of a forward is that the mail left a
 * mailbox nobody reads for one somebody does, and the ledger's left-to-right
 * order shows only that the copy came later.
 *
 * Built from the COPY end, because the copy is the end that NAMES the other
 * (`forwardedFrom` is a field on it, where the original knows nothing about
 * having been forwarded) — the same reason `buildThreads` builds from the
 * answer. An original that has rotated away, or sits below a `?since=` window,
 * draws no link: the copy keeps its own provenance and the picture declines to
 * anchor a line at a position it would have to invent. */
export type MailForwardLink = {
  originalOffset: number;
  copyOffset: number;
  x1: number; x2: number;
  /** The two ends' LANE tops, as a thread link carries them. */
  y1: number; y2: number;
  /** The mailbox it was stranded in — what the link is ABOUT, and the half no
   * reader could recover from the original's `to` when that is a bare role. */
  source: string;
};

/** A poke on a role's lane, aligned with the envelope it named (d10). Its own
 * position on the x axis is the ENVELOPE's, not a time: the ledger axis is the
 * ledger, and a nudge has no offset of its own to occupy. A nudge whose
 * envelope this picture cannot place draws nothing — the lane says so in its
 * count instead of pointing at the wrong line. */
export type MailNudgeMark = {
  offset: number;
  x: number;
  w: number;
  /** How many pokes have named this envelope: the second one is the fact worth
   * seeing, because it means the first did not work. */
  count: number;
  /** The most recent poke's own row, for the mark's title and the detail card. */
  last: MailNudgeRecord;
};

/** One held/expired item under a cursor track, aligned with its envelope. */
export type MailMark = {
  offset: number;
  x: number;
  w: number;
  status: "held" | "expired";
  opportunities: number;
  /** null = unicast. `opportunities` still counts (it is how long the mailbox
   * has been quiet), but there is no denominator and no spending. */
  ttlDeliveries: number | null;
  id: string;
};

export type MailTrack = {
  key: string;
  role: string;
  session: MailSession;
  sessionLabel: string;
  /** How a SENDER would reach this mailbox (ADR-0018 d3 as amended): the cursor
   * key IS the address's instance half, so every track has one — a `--as`
   * mailbox by its name, a window by its session id — except the sessionless
   * reader, which is reachable only through its role. */
  address: string;
  /** Declared by a registration (durable) rather than a window's own
   * (ephemeral). The canvas draws the two differently because they DIE
   * differently: a window's mailbox goes when the tab closes and nobody
   * expects mail to keep arriving in it; a declared one outlives every window
   * and is where a corpse comes from. */
  named: boolean;
  /** Non-null when the WATCHER has said this mailbox is dead — the reason off
   * the `mail.nudge` that named its address (d6). Never the canvas's own
   * judgement: "dead" is the brain's rule and this is a row it wrote. */
  dead: string | null;
  /** Forwards that moved mail OUT of this mailbox, oldest first — what a
   * reaper did about it after the nudge. */
  forwards: MailForwardRecord[];
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
  /** The last thing that happened to this cursor, which decides HOW it moves
   * rather than merely that it did. The distinction that matters is
   * `reanchor`: an advance slides (the cursor read forward past mail, which is
   * the only direction it ever reads), while a re-anchor JUMPS — usually
   * backwards to 0 — because the cursor did not read anything, it started
   * over. Animating that as a slide would draw a cursor reading backwards,
   * which never happens on this bus. Null before anything has moved it. */
  motion: MailCursorEventKind | null;
  /** WHO the last pickup was for (ADR-0017 N6, as built). N6 asks the canvas to
   * show the difference between a human reading their mail and a woken turn
   * reading it, and names `vehicle: payload` as the signal. There is no such
   * vehicle: a turn payload does its own pickup through the ordinary hooks, so
   * its `mail.deliver` says `inject` like anyone's. The difference the trail
   * really carries is the DISPATCH: a nudge's `dispatchId` is what joins the
   * poke to the turn it woke (d10), and a pickup under that same dispatch id is
   * by construction the woken turn's own. So `robot` means "this advance
   * happened under a dispatch a nudge raised", which is a join and not an
   * inference; `human` means the picture has a pickup and no such join; null
   * means nothing has been picked up here yet. */
  pickup: "human" | "robot" | null;
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
  /** The pokes raised about this lane's mail, one mark per envelope named. */
  nudges: MailNudgeMark[];
  /** Pokes this lane's rows named that the picture cannot place — counted so
   * the lane can SAY there were more, rather than drawing them at a guess. */
  nudgesUnplaced: number;
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
  /** Request→answer joins, both ends placed. Scene-level rather than
   * lane-level because a thread's two ends are usually in two lanes: the link
   * belongs to neither, and drawing it inside one would clip it against that
   * lane's own bounds. */
  threads: MailThreadLink[];
  /** Dead→live joins: what a reaper moved, and out of which corpse. */
  forwards: MailForwardLink[];
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
    // SUB-LANE ORDER: declared mailboxes first, then windows, then the
    // sessionless reader — durability descending, which is also the order in
    // which they stop being anybody's problem. Ties keep the reducer's own
    // ordering (the snapshot's, sorted by key), so the layout is stable across
    // polls and a screenshot is comparable.
    const cursors = state.cursors.filter((c) => c.role === role)
      .map((c, i) => ({ c, i }))
      .sort((a, b) => rank(a.c) - rank(b.c) || a.i - b.i)
      .map((x) => x.c);
    const glyphY = y + LANE_HEAD_H;
    const glyphs: MailGlyph[] = [];

    for (const slot of slots) {
      const line = slot.line;
      // A unicast envelope belongs to its ROLE's lane and to one sub-lane
      // inside it (ADR-0018 d4) — never to a lane of its own, and never to
      // every track on the role.
      if (line === null || line.envelope === null || roleOf(line.envelope.to) !== role) continue;
      const e = line.envelope;
      // ONLY the cursors that can actually read it. `lineStatus` would answer
      // `foreign` for the others, which is true but draws a row per sibling
      // saying "not mine" under every unicast on the bus; the honest picture is
      // that a unicast hangs on the ONE track that may take it.
      const readers = cursors.filter((c) => accepts(c, e.to));
      const perCursor = readers.map((c) => ({
        key: cursorKey(c), session: c.session, status: lineStatus(state, c, line),
      }));
      glyphs.push({
        offset: line.offset,
        x: slot.x + SLOT_PAD / 2,
        w: slot.w - SLOT_PAD,
        id: e.id,
        address: e.to,
        instance: instanceOf(e.to),
        kind: e.kind,
        topic: e.topic,
        priority: e.priority,
        ttlDeliveries: e.ttlDeliveries,
        from: e.from.agent === "" ? e.from.harness : `${e.from.agent}@${e.from.harness}`,
        bytes: line.bytes,
        // `no-reader` is now a fact about THIS envelope rather than about the
        // lane: a unicast addressed to a mailbox that has no cursor has nobody
        // to read it even on a lane full of readers, which is exactly the state
        // the reaper exists for.
        status: readers.length === 0 ? "no-reader" : summarize(perCursor.map((p) => p.status)),
        perCursor,
        torn: !line.terminated,
        arrival: line.source,
        thread: threadOf(state, e),
        forwardedFrom: e.forwardedFrom,
        nudges: nudgesFor(state, line.offset),
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
      const address = addressOf(c);
      const dead = deadNudgeFor(state, address);
      return {
        key: cursorKey(c),
        role,
        session: c.session,
        sessionLabel: sessionLabel(c.session),
        address,
        named: c.named,
        dead: dead === null ? null : dead.reason,
        forwards: forwardsFrom(state, address),
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
        motion: c.lastEventKind,
        pickup: pickupKind(state, c),
      };
    });

    const counts = {
      envelopes: glyphs.length,
      fresh: glyphs.filter((g) => g.status === "fresh").length,
      held: glyphs.filter((g) => g.status === "held").length,
      expired: glyphs.filter((g) => g.status === "expired").length,
      pending: glyphs.filter((g) => g.status === "fresh" || g.status === "held").length,
    };

    // A nudge names a role's mail, so its marks belong to that role's lane —
    // grouped by ENVELOPE, because two pokes about one envelope is the fact the
    // picture should make loud (the first one did not work). A poke whose
    // envelope this picture cannot place is counted, never drawn: the lane can
    // honestly say "and one more the picture cannot place", and cannot honestly
    // point at a line.
    const laneNudges = new Map<number, MailNudgeRecord[]>();
    let nudgesUnplaced = 0;
    for (const n of state.nudges) {
      // A dead-mailbox nudge goes TO the reaper and is ABOUT somebody else's
      // mailbox (ADR-0018 d6) — so it belongs on the lane where the stranded
      // mail hangs, not on a `reaper` lane that usually does not exist.
      if (nudgeLane(n) !== role) continue;
      let placed = false;
      for (const o of n.offsets) {
        if (o === null) continue;
        if (!glyphs.some((g) => g.offset === o)) continue;
        placed = true;
        laneNudges.set(o, [...(laneNudges.get(o) ?? []), n]);
      }
      if (!placed) nudgesUnplaced++;
    }
    const nudgeMarks: MailNudgeMark[] = [...laneNudges.entries()]
      .sort((a, b) => a[0] - b[0])
      .map(([offset, rows]) => {
        const g = glyphs.find((x) => x.offset === offset)!;
        return { offset, x: g.x, w: g.w, count: rows.length, last: rows[rows.length - 1] };
      });

    const h = LANE_HEAD_H + GLYPH_H + Math.max(tracks.length, 1) * TRACK_H;
    lanes.push({ role, y, h, glyphY, glyphs, tracks, counts, hasReader: cursors.length > 0,
      nudges: nudgeMarks, nudgesUnplaced });
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
    threads: buildThreads(lanes),
    forwards: buildForwards(lanes),
    empty: slots.length === 0 && lanes.length === 0,
  };
}

/** Join every placed answer to its request. Built from the ANSWER end for two
 * reasons: an answer names its request directly (`counterpart` is `inReplyTo`,
 * a field on the envelope), where a request only learns its closer through the
 * index; and it is the end that can be LATE, which is a property of the link
 * and not of either envelope alone.
 *
 * Both ends must be glyphs in this scene. A thread whose request has rotated
 * away, or sits below a `?since=` window, draws no link at all — the answer
 * keeps its own tag and the picture declines to anchor a line at a position it
 * would have to invent. That is the same refusal `buildSlots` makes about a
 * hole in the ledger, applied to a relation instead of a region. */
function buildThreads(lanes: MailLane[]): MailThreadLink[] {
  const byId = new Map<string, { g: MailGlyph; lane: MailLane }>();
  for (const lane of lanes) for (const g of lane.glyphs) byId.set(clampField(g.id), { g, lane });

  const links: MailThreadLink[] = [];
  for (const lane of lanes) {
    for (const g of lane.glyphs) {
      const t = g.thread;
      if (t === null || t.end !== "answer" || t.counterpart === null) continue;
      const req = byId.get(clampField(t.counterpart));
      if (req === undefined) continue;
      links.push({
        requestOffset: req.g.offset,
        answerOffset: g.offset,
        x1: req.g.x + req.g.w / 2,
        y1: req.lane.glyphY,
        x2: g.x + g.w / 2,
        y2: lane.glyphY,
        late: t.standing === "late",
      });
    }
  }
  return links.sort((a, b) => a.answerOffset - b.answerOffset);
}

/** Join every placed copy to the original it carries on. Both ends must be
 * glyphs in this scene, and the original must be on the lane its
 * `forwardedFrom.address` names — the id alone is not enough, because ids are
 * not unique on this bus (the store does not dedup, and a rotation can bring
 * one back). */
function buildForwards(lanes: MailLane[]): MailForwardLink[] {
  const byIdAndRole = new Map<string, { g: MailGlyph; lane: MailLane }>();
  for (const lane of lanes) for (const g of lane.glyphs) byIdAndRole.set(`${lane.role}\u0000${clampField(g.id)}`, { g, lane });

  const links: MailForwardLink[] = [];
  for (const lane of lanes) {
    for (const g of lane.glyphs) {
      const f = g.forwardedFrom;
      if (f === null) continue;
      const original = byIdAndRole.get(`${roleOf(f.address)}\u0000${clampField(f.id)}`);
      if (original === undefined) continue;
      links.push({
        originalOffset: original.g.offset,
        copyOffset: g.offset,
        x1: original.g.x + original.g.w / 2,
        y1: original.lane.glyphY,
        x2: g.x + g.w / 2,
        y2: lane.glyphY,
        source: f.address,
      });
    }
  }
  return links.sort((a, b) => a.copyOffset - b.copyOffset);
}

/** N6, as built: was the last pickup on this cursor a human reading their mail
 * or a woken turn reading it? See `MailTrack.pickup` for why the signal is the
 * dispatch id and not a vehicle. Answered only from a pickup the picture has
 * actually seen — an advance whose dispatch id it never learned is `human`
 * rather than "probably robot", because the failure direction that matters is
 * never claiming a robot read something a person did. */
function pickupKind(state: MailState, c: MailCursorState): "human" | "robot" | null {
  const id = c.lastAdvance?.dispatchId ?? null;
  if (c.lastAdvance === null) return null;
  if (id !== null && state.nudges.some((n) => n.dispatchId === id)) return "robot";
  return "human";
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

/** Durability rank for sub-lane order: a declared mailbox (0) outlives the
 * windows (1); the sessionless reader (2) is neither — it is the one mailbox
 * with no address at all. */
function rank(c: MailCursorState): number {
  return c.named ? 0 : c.session === null ? 2 : 1;
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
    ...(threadLine(g) === null ? [] : [cut(threadLine(g)!)]),
  ];
}

/** Where a card's lines sit inside the glyph, for the count it actually has:
 * the block is CENTRED in the glyph's own height at a constant step.
 *
 * Card text is laid out HERE, beside the truncation, for the same reason — a
 * glyph is a fixed height and text that does not fit prints through its border,
 * which is the one failure a screenshot shows and a test does not. Deriving the
 * top from `GLYPH_H` rather than pinning it is what makes that a fact the code
 * keeps: the four-line card and the five-line card (an envelope in a thread)
 * both sit inside their glyph, and a sixth line one day would move the block
 * rather than silently overflowing it. */
export function cardMetrics(lines: number): { top: number; step: number } {
  const step = 13;
  const inner = GLYPH_H - 10;   // the glyph's own height at the near tier
  return { top: Math.max(13, Math.round((inner - (lines - 1) * step) / 2) + 4), step };
}

/** The card's thread line, when there is a thread — the one sentence that says
 * what an envelope is WAITING on or what it ANSWERED, which nothing else on the
 * card can say (the topic is prose and `kind` is a word, but neither names the
 * other end). Null when the envelope is in no thread, so the card keeps its
 * four lines and nothing shifts. */
export function threadLine(g: MailGlyph): string | null {
  const t = g.thread;
  if (t === null) return null;
  const hop = `hop ${t.hops + 1}`;
  if (t.end === "request")
    return t.standing === "awaiting"
      ? `awaiting an answer · ${hop}`
      : `answered by ${clampField(t.counterpart ?? "")} · ${hop}`;
  const to = t.counterpart === null ? "" : ` ${clampField(t.counterpart)}`;
  return t.standing === "late" ? `late answer to${to} · ${hop}` : `answers${to} · ${hop}`;
}

/** A one-line description of the whole picture, for the canvas's aria-label —
 * an SVG bus is unreadable to a screen reader without one. */
export function sceneSummary(scene: MailScene, state: MailState): string {
  if (scene.empty) return "The mail bus is empty: no envelopes on the ledger and no cursors.";
  const envelopes = scene.lanes.reduce((n, l) => n + l.counts.envelopes, 0);
  const cursors = scene.lanes.reduce((n, l) => n + l.tracks.length, 0);
  const pending = scene.lanes.reduce((n, l) => n + l.counts.pending, 0);
  // The thread and the poke belong in the spoken summary for the same reason
  // they are drawn: a request nobody answered and a role somebody had to be
  // poked about are the two things on this bus that are WAITING on a person,
  // and a screen reader that only ever hears counts would never hear them.
  const awaiting = scene.lanes.reduce((n, l) =>
    n + l.glyphs.filter((g) => g.thread?.standing === "awaiting").length, 0);
  const late = scene.lanes.reduce((n, l) =>
    n + l.glyphs.filter((g) => g.thread?.standing === "late").length, 0);
  const nudges = scene.lanes.reduce((n, l) => n + l.nudges.reduce((m, k) => m + k.count, 0), 0);
  const extra = [
    awaiting > 0 ? `${awaiting} request(s) awaiting an answer` : null,
    late > 0 ? `${late} late answer(s)` : null,
    nudges > 0 ? `${nudges} nudge(s) raised` : null,
  ].filter((x) => x !== null);
  return `The mail bus: ${envelopes} envelope(s) on the ledger across ${scene.lanes.length} role(s), `
    + `${cursors} cursor(s) reading, ${pending} pending.`
    + (extra.length > 0 ? ` ${extra.join(", ")}.` : "")
    + ` The ledger ends at byte ${state.frontier}.`;
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
