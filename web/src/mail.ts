import type { MailDto, MailCursorDto, MailLineDto, MailPendingDto } from "./api.gen.ts";
import type { TrailLine } from "./store.ts";

// mail-reducer (ADR-0016 decision 14, roadmap item 21 slice 3): the pure model
// behind the Mail canvas — `(state, trailLine) → state`, seeded from one
// `GET /api/v1/mail` snapshot. The canvas draws THIS; nothing here touches
// the network, the DOM, or a clock.
//
// What this file is, in one sentence from the ADR: an INTERPOLATOR between
// authoritative snapshots, never a second store (N8). The C# `MailCursors.
// Pending` is the only truth about what is pending; this reducer reproduces
// its arithmetic (frontier / held / TTL-in-deliveries / expired) so the picture
// can move between polls, and every re-snapshot REPLACES the reduced state
// with the daemon's view. Three rules keep that honest, and each has a shape
// in the code:
//
//   * APPLY what an event states, DERIVE what the ledger proves, FLAG what
//     neither gives. A `mail.cursorAdvance` states the new offset and the
//     deliveries count — those are applied verbatim. The held set it wrote is
//     re-derived from the pending set the reducer had plus the exact offsets
//     the event says were consumed, and CROSS-CHECKED against the counts the
//     event carries; a mismatch does not get papered over — the cursor is
//     marked `uncertain` and `resnapshot` is raised. An event the reducer
//     cannot read exactly (a missing key, a wrong type) is not applied at all:
//     refused, and a re-snapshot requested — a defaulted field would look
//     exactly like a legitimate state to everything downstream.
//   * DELIVERED comes from a `mail.deliver` line and nowhere else (d14 pin
//     iii). An advance moves a cursor past an envelope; that makes the
//     envelope "before the cursor". Only a delivery RECORD makes it
//     "delivered", and an envelope with none renders as *before cursor · no
//     record* — the trail is days-to-weeks and gets discarded, so an old
//     envelope's record may simply be gone, and the picture must say so
//     rather than infer.
//   * IDEMPOTENT under replay, LOUD under loss. The choreography may fold a
//     line the snapshot already reflects (the stream opened before the
//     snapshot landed): `deliveries` on an advance is a per-cursor sequence
//     number and its `offset` a position that never moves backwards; an
//     append's `offset` is a ledger position; a delivery record's identity
//     is its content. So a stale line is recognized and ignored, and a
//     SKIPPED one (a gap, a truncated trail) is recognized and flagged.
//     Nothing is guessed across a hole — and where two readings are truly
//     indistinguishable (a first advance replayed at the cursor's current
//     offset vs. a lineage restarted there with nothing new appended), the
//     answer is a flag and a snapshot, not the likelier story.
//
// Sequencing facts this leans on, all pinned engine-side (MailCursor.cs,
// MailStore.cs, MailDigest.cs): an advance emits `mail.expire`×N, then
// `mail.cursorAdvance`, and — only if the digest actually answered — one
// `mail.deliver`; `deliveries` increments by exactly one per advance; a first
// advance (deliveries 1) always comes from an anchor at offset 0, so its
// pending set is the whole retained ledger for the role; a re-anchor emits
// before the advance that follows it, carrying the counter it preserves.

// ---- ledger --------------------------------------------------------------------

export type MailSession = string | null;

/** A stored envelope as the reducer knows it. `body` is present only when the
 * line came from a snapshot (the trail never carries a body — d14); the same
 * goes for `ts`, `inReplyTo`, `prev`, `hash`. An id or topic learned from the
 * trail may be CLAMPED (`clampField`); joins compare clamped to clamped. */
export type MailEnvelopeView = {
  id: string;
  ts: string | null;
  from: { agent: string; harness: string; session: string | null };
  to: string;
  kind: string;
  topic: string;
  priority: string;
  inReplyTo: string | null;
  /** The thread's DEPTH as the line STORES it (ADR-0017 d8): 0 for a head and
   * for anything that answers nothing, `parent + 1` for a reply stamped against
   * a resident parent. Never walked here — the walk is the implementation that
   * silently resets the count when a head has rotated away, which is why the
   * engine stores it. Present on a trail arrival too (the `mail.append` row
   * carries `inReplyTo` and `hops` together), so a live picture counts the same
   * depth a snapshot does. */
  hops: number;
  /** Where an answer to THIS envelope should go (ADR-0018 d4) — an address
   * in `to`'s grammar. The thread's routing half beside `inReplyTo`'s
   * correlation half. Snapshot-only, like `inReplyTo`. */
  replyTo: string | null;
  /** Where a forward came from (ADR-0018 d8) — which envelope, and whose
   * mailbox it was stranded in. Snapshot-only, like `body` and `ts`: the trail
   * carries provenance, not the envelope's own references. */
  forwardedFrom: { id: string; address: string } | null;
  /** null = UNICAST, which has no TTL at all (ADR-0018 d5) — not unknown, not
   * zero. Nothing counts down for it; it is held until its one addressee
   * takes it. */
  ttlDeliveries: number | null;
  body: string | null;
  prev: string | null;
};

export type MailLedgerLine = {
  offset: number;
  /** Line length WITHOUT its terminator (MailLine.Bytes): the next line starts at offset + bytes + 1. */
  bytes: number;
  terminated: boolean;
  hash: string | null;
  envelope: MailEnvelopeView | null;
  errors: string[];
  source: "snapshot" | "trail";
};

// ---- cursors -------------------------------------------------------------------

/** A held entry: seen and passed over, waiting before the cursor's offset.
 * Carries the envelope's ttl and priority so expiry can be classified even
 * when the reducer's ledger lacks the line (a `?since=` snapshot). */
export type MailHeldItem = {
  offset: number;
  id: string;
  priority: string;
  /** null = unicast, no TTL (ADR-0018 d5): held, never spent. */
  ttlDeliveries: number | null;
  /** The deliveries value of the opportunity that first passed it over. */
  seenAt: number;
};

/** A fresh pending envelope: at or beyond the cursor's offset, addressed to
 * its role, parseable — first sighted, no TTL accrued. */
export type MailFreshItem = {
  offset: number;
  id: string;
  priority: string;
  /** null = unicast, no TTL (ADR-0018 d5). */
  ttlDeliveries: number | null;
};

export type MailAdvanceMark = {
  deliveries: number;
  offset: number;
  delivered: { offset: number; id: string }[];
  /** Filled by the `mail.deliver` that follows the advance; null until then
   * (or forever, if the digest died between the two — the ledger under-claims). */
  seam: string | null;
  vehicle: string | null;
  dispatchId: string | null;
  atMs: number;
};

export type MailCursorState = {
  role: string;
  /** THE CURSOR KEY, which since ADR-0018 d3 IS the mailbox's instance: `--as`
   * when a registration named one, else the window's session id. `null` is the
   * sessionless reader, which has no instance and therefore no address of its
   * own. */
  session: MailSession;
  /** Is this a mailbox somebody DECLARED, or a window's own? The key cannot
   * say — a name and a session id look alike — so the snapshot decides it from
   * the registrations, and the trail says it by carrying an `instance` column
   * at all (the engine writes that column exactly when the key is not the
   * window). A named mailbox is DURABLE: it outlives the window that reads it,
   * which is why the canvas draws it as a sub-lane and why a dead one is a
   * corpse rather than a closed tab. */
  named: boolean;
  gen: number | null;
  head: string | null;
  /** The cursor's read position; null when the reducer has only ever seen a
   * delivery record for it and no advance — position unknown. */
  offset: number | null;
  /** The TTL clock (opportunities consumed); null with the same meaning as offset null. */
  deliveries: number | null;
  lastDeliveredId: string | null;
  held: MailHeldItem[];     // offset ascending
  fresh: MailFreshItem[];   // offset ascending, every offset ≥ `offset`
  skippedMalformed: number;
  reanchored: boolean;
  reanchorReason: string | null;
  /** Where the reducer learned of this cursor. */
  origin: "snapshot" | "trail";
  /** Non-null when the reducer no longer trusts its own picture of this
   * cursor's pending set — the reason, verbatim. Positions still track; the
   * projection stays honest by saying so. Only a snapshot (or a full
   * reconstruction from a complete ledger) clears it. */
  uncertain: string | null;
  lastAdvance: MailAdvanceMark | null;
  lastEventKind: MailCursorEventKind | null;
  lastEventAtMs: number | null;
};

export type MailCursorEventKind = "advance" | "deliver" | "expire" | "reanchor" | "refuse" | "vanished";

// ---- records, notes, presence -------------------------------------------------

/** One `mail.deliver` ledger line — the ONLY thing that makes an envelope
 * "delivered". `offsets` are the reducer's resolution of `envelopeIds` to
 * ledger positions (null per id when it could not resolve one honestly). */
export type MailDeliveryRecord = {
  role: string;
  /** THE MAILBOX this pickup moved — the cursor key (`instance ?? sessionId`),
   * not necessarily the window (ADR-0018 d3). It has to be the key, because
   * this record is what turns "before the cursor" into "delivered" for one
   * cursor, and a named mailbox's record filed under the window it was read in
   * would join to no cursor at all. */
  session: MailSession;
  /** WHO read it, when that is not the same question — the window the pickup
   * ran in, or null for a sessionless reader. Display only; nothing joins on
   * it. */
  window: MailSession;
  dispatchId: string | null;
  hookEvent: string | null;
  seam: string;
  vehicle: string;
  envelopeIds: string[];
  offsets: (number | null)[];
  renderHash: string | null;
  bytesInjected: number | null;
  ts: string | null;
  atMs: number;
};

/** One `mail.nudge` trail line (ADR-0017 d10) — a nudge is a TRAIL LINE, never
 * an envelope: mail about mail would recurse, and "I poked them and they still
 * have not read it" is the trail's kind of fact. It belongs to a ROLE and
 * carries no session, exactly as d10 has it, which is why it draws on the LANE
 * and never on a track: nobody's cursor moved.
 *
 * `offsets` is this picture's placement of `envelopeIds` — null per id it
 * cannot place honestly. It is deliberately NOT `resolveDelivery`'s rule: that
 * one asks "which line did this cursor CONSUME", so it searches behind the
 * cursor and skips what is held. A nudge names mail the role has by definition
 * NOT read — ahead of every cursor — so the question is the simpler one, "which
 * line on this lane bears this id", answered only when the answer is unique. */
export type MailNudgeRecord = {
  role: string;
  /** ADR-0018 d6: set only by the dead-mailbox rule, where `role` is the
   * reaper's own lane and says nothing about whose mail is stranded. Null on an
   * ordinary nudge, where the role IS the subject. */
  address: string | null;
  workspace: string | null;
  envelopeIds: string[];
  offsets: (number | null)[];
  reason: string;
  /** d10's numbers, never the sentence: a reader must not parse prose to learn
   * what a poke cost. Null when the row carried none (the reaper's path). */
  budget: { envelope: number; perEnvelope: number; roleHour: number; perRoleHour: number } | null;
  /** What joins this poke to the turn it woke — `dispatch.start → exec.spawn →
   * exec.exit`, and the `mail.deliver` the woken turn's own pickup writes.
   * That last join is what lets the canvas draw a ROBOT pickup differently from
   * a human's (N6, as-built: see `pickupKind`). */
  dispatchId: string | null;
  ts: string | null;
  atMs: number;
};

/** One `mail.forward` trail line (ADR-0018 d6, slice `reaper-payloads`). Like a
 * nudge it belongs to no cursor and moves nothing: the mail it names was never
 * read by the mailbox it came out of, and the copies are ordinary appends that
 * arrive on their own rows. */
export type MailForwardRecord = {
  /** The mailbox the mail was stranded in — role + instance, the trail's own
   * two columns, joined here into the address a sender would write. */
  source: string;
  /** Where the copies went, verbatim as the copies' `to`. */
  to: string;
  /** Who did it, as an address. */
  by: string | null;
  /** The ORIGINALS that were copied (not the copies' ids — those arrive as
   * their own `mail.append` rows and carry the link). */
  forwardedIds: string[];
  /** Ids this run found already forwarded to this target and skipped. Absent on
   * a first run; present is the fact that distinguishes "nothing was there"
   * from "it had already been moved". */
  skippedIds: string[];
  ts: string | null;
  atMs: number;
};

export type MailNoteKind =
  | "malformed-event"      // a mail.* line the reducer refused to apply
  | "ledger-gap"           // an append landed past where the next line must start
  | "ledger-diverged"      // an append at a position the ledger already holds, with different content
  | "torn-terminated"      // a torn tail was terminated by an append (mail.torn)
  | "advance-unknown-cursor"
  | "advance-out-of-sequence"
  | "advance-past-frontier"
  | "advance-mismatch"     // the re-derived held/expired/delivered set disagrees with the event's counts
  | "lineage-restarted"    // deliveries fell back to 1: the cursor file was deleted (d13) and re-anchored silently
  | "reanchor"
  | "reanchor-partial"     // re-anchored below the snapshot's `since`: the fresh set cannot be rebuilt
  | "store-changed"        // re-anchored because the store's bytes changed: the whole ledger picture predates it
  | "refuse"
  | "vanished"
  | "expire-mismatch"
  | "deliver-unknown-cursor"
  | "deliver-unmatched"    // a delivery record naming an envelope the picture cannot place
  | "lock-busy"
  | "forward"              // stranded mail was copied to a live address (ADR-0018 d6)
  | "reaped"               // a mailbox's standing was removed; its mail stays on the ledger
  | "unknown-event";       // a mail.* kind this reducer does not know (forward compatibility)

export type MailNote = {
  kind: MailNoteKind;
  severity: "info" | "warn";
  role: string | null;
  session: MailSession;
  offset: number | null;
  message: string;
  atMs: number;
};

export type MailPresenceEntry = {
  session: string;
  roles: string[];
  /** In the CALLER's clock (the `atMs` it passes) — never a wall-clock
   * timestamp, so no browser has to reconcile two clocks; null = never seen
   * dispatching (a cursor-only session: quiet, or the daemon restarted). */
  lastSeenAtMs: number | null;
};

export type MailResnapshotReason =
  | "gap" | "reset" | "since-misaligned" | "malformed-event" | "ledger-gap"
  | "ledger-diverged" | "advance-out-of-sequence" | "advance-past-frontier"
  | "advance-mismatch" | "advance-unknown-cursor" | "reanchor-partial" | "store-changed"
  | "deliver-unknown-cursor" | "torn-terminated";

export type MailState = {
  seeded: boolean;
  since: number;
  /** End of the COMPLETE lines the reducer knows — where the next append must land. */
  frontier: number;
  /** Where this picture sits in the TRAIL's id space — the `Last-Event-ID` a
   * stream should open at to receive exactly the events this snapshot does not
   * already account for. Set by `seedMail` from the snapshot and never touched
   * by reduction (it is a fact about the snapshot, not about the reduced state);
   * a re-seed replaces it, which is what makes a resnapshot re-anchor the stream
   * as well as the picture. An OPAQUE token (ADR-0009 d2) — echo it, never
   * compare or arithmetic it; that is also why the mail stream is its own
   * subscription rather than a filter over the trace's, since filtering by id
   * would mean interpreting one. Null = the daemon serves no trail, so there is
   * no id space to align to and the caller must subscribe FIRST and fold the
   * overlap as replay — never treat it as "0", which means "from the earliest
   * still-reachable point" and would replay the whole trail as live. */
  trailEventId: string | null;
  /** Offset of the unterminated last line, when there is one (it is also the last entry of `lines`). */
  torn: number | null;
  chain: { ok: boolean; head: string | null; gen: number; faults: number; dirMode: string | null; fileMode: string | null };
  dir: string | null;
  lines: MailLedgerLine[];
  cursors: MailCursorState[];
  presence: MailPresenceEntry[];
  deliveries: MailDeliveryRecord[];
  /** True = the daemon read the WHOLE trail and trimmed nothing, so "no record"
   * means exactly that: nobody read it. False = the fold saw a suffix at best
   * (scan window, record cap) or had no trail to read, so an envelope with no
   * record may simply predate what can still be proven. Unseeded state is
   * false — it has read nothing and may not claim otherwise. */
  deliveriesComplete: boolean;
  /** Every `mail.nudge` this picture has watched go out (ADR-0017 d10), oldest
   * first. Trail-only: no snapshot serves them, so — like `deliveries` — they
   * survive a re-seed rather than being erased by it. A poke that happened is a
   * fact about the past, and N8's "the snapshot is authoritative" governs
   * STATE, not history. */
  nudges: MailNudgeRecord[];
  /** Every `mail.forward` this picture watched go out (ADR-0018 d6) — the row
   * that says somebody moved ANOTHER mailbox's mail. Trail-only history, kept
   * across a re-seed for `nudges`' reason: a forward that happened is a fact
   * about the past, and the snapshot is authoritative about STATE, not about
   * what it did not witness. The dead→live LINK the canvas draws does not come
   * from here — it comes from the copy's own `forwardedFrom`, which is on the
   * ledger and survives everything; this is what lets a lane say WHO moved it
   * and what was skipped as already-moved. */
  forwards: MailForwardRecord[];
  /** The thread index (ADR-0017 d8): a request's id → the id of the FIRST
   * `answer` that names it in `inReplyTo`. Seeded from the snapshot, which
   * builds it over the WHOLE ledger — a request and its answer routinely sit on
   * opposite sides of the `?since=` window, so this is the one join the client
   * cannot make from what it holds — and extended live, since a `mail.append`
   * carrying an answer's link closes its request in the picture the moment it
   * lands. First wins here too: a later answer is a LATE answer, not a second
   * closure. */
  closedBy: Record<string, string>;
  /** d8's N — how many envelopes one thread may carry, served by the snapshot
   * so `hop n/N` is the engine's own number and not a second spelling of it in
   * the browser. Null until seeded: an unseeded picture knows no policy. */
  hopBudget: number | null;
  notes: MailNote[];
  notesDropped: number;
  /** Non-null = the picture may be behind or wrong; the choreography should
   * fetch a fresh snapshot and re-seed. Cleared by `seedMail`. */
  resnapshot: { reason: MailResnapshotReason; detail: string; atMs: number } | null;
  seededAtMs: number | null;
  lastInputAtMs: number | null;
  /** A `mail.torn` warn seen and not yet explained by the append that follows it. */
  pendingTorn: number | null;
};

export type MailInput =
  | { kind: "line"; line: TrailLine; atMs: number }
  | { kind: "gap"; dropped: number; atMs: number }
  | { kind: "reset"; atMs: number };

export const MAIL_NOTES_CAP = 200;
export const MAIL_DELIVERIES_CAP = 500;
export const MAIL_NUDGES_CAP = 200;
/** Forwards are rarer than nudges by construction — one per disposed mailbox,
 * and a mailbox can only be disposed of once — so the bound exists to keep a
 * pathological trail from growing the picture without limit, not because a real
 * bus reaches it. */
export const MAIL_FORWARDS_CAP = 200;

// ---- helpers ---------------------------------------------------------------------

/** `MailEnvelope.ClampField` — one spelling, three surfaces (digest head,
 * `mail.append`, and now this join). UTF-16 units on both sides, never splits
 * a surrogate pair. */
export const HEAD_FIELD_CHARS = 120;
export function clampField(s: string): string {
  if (s.length <= HEAD_FIELD_CHARS) return s;
  let cut = HEAD_FIELD_CHARS;
  const c = s.charCodeAt(cut - 1);
  if (c >= 0xd800 && c <= 0xdbff) cut--;
  return s.slice(0, cut) + "…";
}

/** The cursor's own arithmetic: passed over at opportunities seenAt..deliveries. */
export function opportunities(deliveries: number, seenAt: number): number {
  return deliveries - seenAt + 1;
}
export function isExpired(item: MailHeldItem, deliveries: number): boolean {
  // No ttl, no expiry — the mirror of MailCursors.Pending's guard. A unicast
  // envelope is held until its addressee takes it (ADR-0018 d5); disposing of
  // a mailbox that never returns is the reaper's judgement (d6), never this
  // arithmetic. Drifting from the engine here paints mail as spent that the
  // digest will still deliver, which is N8 exactly.
  if (item.ttlDeliveries === null) return false;
  return opportunities(deliveries, item.seenAt) >= item.ttlDeliveries;
}

export function sameSession(a: MailSession, b: MailSession): boolean {
  return (a ?? null) === (b ?? null);
}

// ---- addresses (ADR-0018 d2/d3/d4) ---------------------------------------------
//
// An address is a ROLE — every mailbox holding it, the broadcast this bus began
// with — or a ROLE@INSTANCE, exactly one mailbox. The three helpers below are
// the browser's mirror of `MailAddress`, and they are deliberately a PROJECTION
// of the grammar rather than a second copy of it: an ungrammatical address
// cannot reach a snapshot or the trail (the engine's parser refuses it at the
// door), so all this side has to answer is which of the two shapes an
// already-valid address has. Re-checking `[a-z0-9][a-z0-9-]*` here would be the
// N8 hazard wearing an address for a hat — two spellings of one grammar, and
// the browser's would be the one nobody notices drifting.

/** The role half. `main@laptop-a` → `main`; a bare role is its own role. */
export function roleOf(address: string): string {
  const at = address.indexOf("@");
  return at < 0 ? address : address.slice(0, at);
}

/** The instance half, or null for a broadcast address. */
export function instanceOf(address: string): string | null {
  const at = address.indexOf("@");
  return at < 0 ? null : address.slice(at + 1);
}

/** How a mailbox is spelled — what a SENDER would write to reach it. The cursor
 * key IS the address's instance half (d3 as amended: `--as` when a registration
 * named one, else the window's session id), so this is a rendering and not a
 * derivation. A sessionless reader has no instance and therefore no address of
 * its own; it is reachable only through its role. */
export function addressOf(c: { role: string; session: MailSession }): string {
  return c.session === null ? c.role : `${c.role}@${c.session}`;
}

/** THE RECIPIENT PREDICATE — `MailAddress.Accepts`, mirrored (d4).
 *
 * A mailbox reads its role's BROADCAST always, plus its own `role@instance`
 * UNICAST. Naming a mailbox ADDS an address; it never replaces one. A sibling's
 * unicast is invisible here exactly as it is in the engine, and that invisibility
 * is the whole reason this is one function called from every site rather than a
 * comparison written out four times: the C# side keeps ONE `Accepts` for the same
 * reason, and a browser that mirrored it in three places and forgot the fourth
 * would paint a sibling's mail as pending on this track — a false picture, which
 * is worse than no picture (d14). */
export function accepts(c: { role: string; session: MailSession }, to: string): boolean {
  return to === c.role || (c.session !== null && to === `${c.role}@${c.session}`);
}

function byOffset<T extends { offset: number }>(a: T, b: T): number { return a.offset - b.offset; }

function isRecord(v: unknown): v is Record<string, unknown> {
  return typeof v === "object" && v !== null && !Array.isArray(v);
}
function str(v: unknown): string | null { return typeof v === "string" ? v : null; }

/** WHICH MAILBOX a cursor row is about (ADR-0018 d3). Every `mail.*` row that
 * names a cursor carries `sessionId` = the WINDOW that did the work and, when
 * the two differ, `instance` = the mailbox the work moved. The cursor key is
 * the instance when there is one, and the window otherwise — the engine's own
 * `Instance ?? hookSession`, mirrored.
 *
 * Keying on the session alone is not a smaller mistake, it is a different
 * picture: two windows sharing one `--as` are ONE cursor with one counter, so a
 * reader that split them would model two, then flag every second advance as
 * out-of-sequence and ask for a snapshot that says the same thing again. */
function mailboxKey(d: Record<string, unknown>, session: MailSession): MailSession {
  return str(d.instance) ?? session;
}
function num(v: unknown): number | null {
  return typeof v === "number" && Number.isFinite(v) && Number.isInteger(v) ? v : null;
}
function nonNeg(v: unknown): number | null {
  const n = num(v);
  return n !== null && n >= 0 ? n : null;
}

// ---- state construction --------------------------------------------------------

export function emptyMailState(): MailState {
  return {
    seeded: false,
    since: 0,
    frontier: 0,
    trailEventId: null,
    torn: null,
    chain: { ok: true, head: null, gen: 1, faults: 0, dirMode: null, fileMode: null },
    dir: null,
    lines: [],
    cursors: [],
    presence: [],
    deliveries: [],
    deliveriesComplete: false,
    nudges: [],
    forwards: [],
    closedBy: {},
    hopBudget: null,
    notes: [],
    notesDropped: 0,
    resnapshot: null,
    seededAtMs: null,
    lastInputAtMs: null,
    pendingTorn: null,
  };
}

function lineFromDto(l: MailLineDto): MailLedgerLine {
  const e = l.envelope;
  return {
    offset: l.offset,
    bytes: l.bytes,
    terminated: l.terminated,
    hash: l.hash,
    envelope: e === null ? null : {
      id: e.id, ts: e.ts,
      from: { agent: e.from.agent, harness: e.from.harness, session: e.from.session },
      to: e.to, kind: e.kind, topic: e.topic, priority: e.priority,
      inReplyTo: e.inReplyTo, hops: e.hops, replyTo: e.replyTo, forwardedFrom: e.forwardedFrom,
      ttlDeliveries: e.ttlDeliveries, body: e.body, prev: e.prev,
    },
    errors: [...l.errors],
    source: "snapshot",
  };
}

function heldFromDto(p: MailPendingDto): MailHeldItem {
  return { offset: p.offset, id: p.id, priority: p.priority, ttlDeliveries: p.ttlDeliveries, seenAt: p.seenAt as number };
}
function freshFromDto(p: MailPendingDto): MailFreshItem {
  return { offset: p.offset, id: p.id, priority: p.priority, ttlDeliveries: p.ttlDeliveries };
}

function cursorFromDto(c: MailCursorDto): MailCursorState {
  // Held = the pending entries that carry a seenAt PLUS the expired ones
  // (still in the cursor file's held list; the next advance drops them) —
  // exactly what MailCursors.Pending classifies from `cursor.Held`. Fresh =
  // pending with no stamp.
  const held = [...c.pending.filter((p) => p.seenAt !== null), ...c.expired].map(heldFromDto).sort(byOffset);
  const fresh = c.pending.filter((p) => p.seenAt === null).map(freshFromDto).sort(byOffset);
  return {
    role: c.role,
    session: c.session,
    named: c.named,
    gen: c.gen,
    head: c.head,
    offset: c.offset,
    deliveries: c.deliveries,
    lastDeliveredId: c.lastDeliveredId,
    held,
    fresh,
    skippedMalformed: c.skippedMalformed,
    reanchored: c.reanchored,
    reanchorReason: c.reanchorReason,
    origin: "snapshot",
    uncertain: null,
    lastAdvance: null,
    lastEventKind: null,
    lastEventAtMs: null,
  };
}

/** The snapshot's delivery PRELOAD (6a): `mail.deliver` lines the daemon folded
 * out of the trail, placed on this picture's ledger by `resolveDelivery` — the
 * same rule the live path uses for a record that arrives without its advance.
 *
 * Three things it deliberately does not do. It raises NO note for an id it
 * cannot place: the fold reaches back only as far as the trail does, so a
 * record naming an envelope outside this snapshot is the expected case, not an
 * anomaly (the live path's `deliver-unmatched` still means what it meant —
 * something arrived that should have matched). It touches NO cursor and NO
 * presence: a seed's cursors are the daemon's authoritative view, and a
 * historical record is a fact beside them, never evidence about where a cursor
 * is now. And a record that fails to place is still KEPT, with null offsets —
 * `deliveriesFor` matches on offset, so it shows nowhere, but it stays a
 * counted part of the history rather than being quietly dropped. */
function preloadDeliveries(
  dto: MailDto, lines: MailLedgerLine[], cursors: MailCursorState[], atMs: number, prev?: MailState,
): MailDeliveryRecord[] {
  const out: MailDeliveryRecord[] = [];
  for (const d of dto.deliveries) {
    // The record's own key is the MAILBOX (ADR-0018 d3) — `instance` when the
    // row carried one, else the window — so a `--as` mailbox's history joins to
    // the cursor that actually holds it. The fold ships both columns verbatim
    // for exactly this reason; deriving one from the other here would be the
    // reducer inventing a rule the engine did not send.
    const key = d.instance ?? d.session;
    const c = cursors.find((x) => x.role === d.role && sameSession(x.session, key))
      ?? blankCursor(d.role, key, d.instance !== null);
    const { offsets } = resolveDelivery(lines, c, d.envelopeIds, []);
    const record: MailDeliveryRecord = {
      role: d.role, session: key, window: d.session, dispatchId: d.dispatchId, hookEvent: d.hookEvent,
      seam: d.seam, vehicle: d.vehicle, envelopeIds: [...d.envelopeIds], offsets,
      renderHash: d.renderHash, bytesInjected: d.bytesInjected, ts: d.ts, atMs,
    };
    if (!out.some((r) => sameDelivery(r, record))) out.push(record);
  }
  // The preload is history and `prev` is what this client watched happen, so
  // history goes first: the cap trims the OLDEST, and a record the client saw
  // live keeps its own observation time where the two describe one delivery.
  for (const r of prev?.deliveries ?? []) {
    const at = out.findIndex((x) => sameDelivery(x, r));
    if (at >= 0) out[at] = r; else out.push(r);
  }
  if (out.length > MAIL_DELIVERIES_CAP) out.splice(0, out.length - MAIL_DELIVERIES_CAP);
  return out;
}

/** Seed from a snapshot — the authoritative read. This REPLACES the reduced
 * state (N8: interpolator, not store). What carries over from `prev` is the
 * notes log, and the delivery RECORDS this client watched arrive — which the
 * snapshot now also preloads from the trail (6a), so a pickup that predates the
 * page reads as delivered instead of *before cursor · no record*. The union of
 * the two is deduped by a record's content, its only identity. Everything else
 * is the daemon's view, verbatim. */
export function seedMail(dto: MailDto, atMs: number, prev?: MailState): MailState {
  const lines = dto.lines.map(lineFromDto).sort(byOffset);
  const last = lines.length > 0 ? lines[lines.length - 1] : null;
  const torn = last !== null && !last.terminated ? last.offset : null;
  const cursors = dto.cursors.map(cursorFromDto)
    .sort((a, b) => a.role < b.role ? -1 : a.role > b.role ? 1 : cmpSession(a.session, b.session));
  const presence: MailPresenceEntry[] = dto.presence.map((p) => ({
    session: p.session,
    roles: [...p.roles],
    lastSeenAtMs: p.lastDispatchAgeMs === null ? null : atMs - p.lastDispatchAgeMs,
  }));
  return {
    seeded: true,
    since: dto.since,
    frontier: dto.frontier,
    // Carried verbatim, including null: an absent stamp is a real answer (no
    // trail served) and must not be defaulted to 0 by anything downstream.
    trailEventId: dto.trailEventId,
    torn,
    chain: {
      ok: dto.chain.ok, head: dto.chain.head, gen: dto.chain.gen, faults: dto.chain.faults.length,
      dirMode: dto.chain.dirMode, fileMode: dto.chain.fileMode,
    },
    dir: dto.dir,
    lines,
    cursors,
    presence,
    deliveries: preloadDeliveries(dto, lines, cursors, atMs, prev),
    deliveriesComplete: dto.deliveriesComplete,
    // Kept across the re-seed like the delivery records, and RE-PLACED against
    // the new ledger: a nudge naming mail that was below the old `?since=`
    // window can become placeable when a wider snapshot arrives, and a nudge
    // whose lines have since rotated away must stop claiming a position it can
    // no longer prove. The row's own columns are never touched — only this
    // picture's reading of where they sit.
    nudges: (prev?.nudges ?? []).map((n) => ({ ...n, offsets: placeById(lines, nudgeLane(n), n.envelopeIds) })),
    // Kept for the same reason and re-placed for none: a forward record names
    // ORIGINALS by id and claims no ledger position of its own — the link the
    // canvas draws hangs off the copy's `forwardedFrom`, which is on the ledger
    // this snapshot just replaced.
    forwards: prev?.forwards ?? [],
    // The snapshot's index REPLACES whatever was folded live: it is built over
    // the whole ledger, so it can only be a superset of what a stream taught us.
    closedBy: { ...dto.closedBy },
    hopBudget: dto.hopBudget,
    notes: prev?.notes ?? [],
    notesDropped: prev?.notesDropped ?? 0,
    // A misaligned `since` means the bytes this client last read are gone:
    // the server declined to splice, and so does the reducer — ask again from 0.
    resnapshot: dto.sinceAligned ? null
      : { reason: "since-misaligned", detail: `since=${dto.since} rests on no line boundary — re-snapshot from 0`, atMs },
    seededAtMs: atMs,
    lastInputAtMs: atMs,
    pendingTorn: null,
  };
}

function cmpSession(a: MailSession, b: MailSession): number {
  const x = a ?? "", y = b ?? "";
  return x < y ? -1 : x > y ? 1 : 0;
}

// ---- the reducer -------------------------------------------------------------------

export function reduceMail(state: MailState, input: MailInput): MailState {
  switch (input.kind) {
    case "gap":
      return raise({ ...state, lastInputAtMs: input.atMs }, "gap", `the stream dropped ${input.dropped} line(s)`, input.atMs);
    case "reset":
      return raise({ ...state, lastInputAtMs: input.atMs }, "reset", "the trail's id space restarted", input.atMs);
    case "line":
      return reduceLine(state, input.line, input.atMs);
  }
}

function reduceLine(state: MailState, line: TrailLine, atMs: number): MailState {
  if (!isRecord(line)) return state;   // a trail frame that parsed to a non-object (`null`, a number): nothing to fold
  // An empty session is the SESSIONLESS reader (MailCursors.CursorPath
  // normalizes "" to null; the API reports null) — the trail may still spell
  // it "" because the request carried it, and two spellings would be two
  // cursors on the canvas for one file on disk.
  const sessionCol = typeof line.sessionId === "string" && line.sessionId.length > 0 ? line.sessionId : null;
  const evt = typeof line.evt === "string" ? line.evt : "";
  // Most trail lines are neither mail nor session-bearing: return the SAME
  // state object, so a store subscriber sees no change to render.
  if (!evt.startsWith("mail.") && sessionCol === null) return state;

  let s: MailState = { ...state, lastInputAtMs: atMs };
  // Presence is inferred from ANY line that names a session — the API's own
  // rule (cursor files ∪ recent dispatch sessions), applied to the live tail.
  if (sessionCol !== null) s = touchPresence(s, sessionCol, null, atMs);
  if (!evt.startsWith("mail.")) return s;

  const data: Record<string, unknown> = isRecord(line.data) ? line.data : {};
  const ts = typeof line.ts === "string" ? line.ts : null;
  switch (evt) {
    case "mail.append": return onAppend(s, data, atMs);
    case "mail.cursorAdvance": return onAdvance(s, data, sessionCol, atMs);
    case "mail.expire": return onExpire(s, data, sessionCol, atMs);
    case "mail.deliver": return onDeliver(s, data, sessionCol, line, ts, atMs);
    case "mail.cursorReanchor": return onReanchor(s, data, sessionCol, atMs);
    case "mail.cursorRefuse": return onRefuse(s, data, sessionCol, atMs);
    case "mail.cursorVanished": return onVanished(s, data, sessionCol, atMs);
    case "mail.torn": {
      const offset = nonNeg(data.offset);
      return { ...s, pendingTorn: offset };
    }
    case "mail.nudge": return onNudge(s, data, line, ts, atMs);
    case "mail.forward": return onForward(s, data, ts, atMs);
    case "mail.reap": return onReap(s, data, atMs);
    case "mail.lockBusy":
      return note(s, { kind: "lock-busy", severity: "info", role: null, session: null, offset: null,
        message: "the store's append lock was busy — a writer waited or gave up", atMs });
    default:
      return note(s, { kind: "unknown-event", severity: "info", role: null, session: null, offset: null,
        message: `unknown mail event '${evt}' — ignored`, atMs });
  }
}

// ---- mail.append -------------------------------------------------------------------

/** Fold one COMPLETE line into the thread index: an `answer` that names a
 * request closes it, first one wins (a later answer is LATE, which is a tag and
 * not a second closure). Returns the same object when nothing changes, so an
 * ordinary append allocates nothing.
 *
 * Ids learned from the trail are CLAMPED and ids learned from a snapshot are
 * not — which is harmless here because both halves of any one lookup come from
 * the same place: a trail-learned key is compared against a trail-learned
 * envelope id, and the next snapshot replaces the whole map with the full-length
 * one built over the ledger. */
function closeThread(closedBy: Record<string, string>, line: MailLedgerLine): Record<string, string> {
  const e = line.envelope;
  if (e === null || e.kind !== "answer" || e.inReplyTo === null) return closedBy;
  if (closedBy[e.inReplyTo] !== undefined) return closedBy;
  return { ...closedBy, [e.inReplyTo]: e.id };
}

function onAppend(s: MailState, d: Record<string, unknown>, atMs: number): MailState {
  const id = str(d.id), to = str(d.to), offset = nonNeg(d.offset), bytes = nonNeg(d.bytes);
  const kind = str(d.kind), topic = str(d.topic), priority = str(d.priority), ttl = num(d.ttlDeliveries);
  const from = isRecord(d.from) ? d.from : null;
  // The thread's two columns ride the row TOGETHER or not at all (the engine
  // writes `hops` only inside the `inReplyTo` branch), so a row carrying one
  // without the other is a row the store could not have written — refused
  // rather than half-read, exactly like the ttl/address pair below.
  const inReplyTo = d.inReplyTo === undefined ? null : str(d.inReplyTo);
  const hops = d.hops === undefined ? null : nonNeg(d.hops);
  const threadOk = (d.inReplyTo === undefined && d.hops === undefined)
    || (inReplyTo !== null && hops !== null);
  // `forwardedFrom` rides the trail for `inReplyTo`'s reason and not for
  // `body`'s (ADR-0018 d8, as amended by `canvas-instances`): the canvas draws
  // the dead→live link, and a join visible on disk but invisible on the stream
  // would leave a forward unpaired until the next snapshot. Both halves or
  // neither — a row with one is a row the store could not have written.
  const fwd = isRecord(d.forwardedFrom) ? d.forwardedFrom : null;
  const fwdId = fwd ? str(fwd.id) : null, fwdAddress = fwd ? str(fwd.address) : null;
  const forwardedFrom = fwdId !== null && fwdAddress !== null ? { id: fwdId, address: fwdAddress } : null;
  const fwdOk = d.forwardedFrom === undefined || forwardedFrom !== null;
  const agent = from ? str(from.agent) : null, harness = from ? str(from.harness) : null;
  const fromSession = from && from.session !== undefined ? str(from.session) : null;
  // A unicast address carries NO ttl and a role address MUST carry one
  // (ADR-0018 d5) — the same rule the engine's parser enforces, so a trail line
  // that breaks it is a line the store could not have written and is refused
  // rather than half-read. `includes("@")` is a projection of the address, not
  // a second copy of its grammar: an ungrammatical `to` cannot reach the trail
  // (MailEnvelope.TryParse refuses it), so all this has to answer is which of
  // the two shapes an already-valid address has.
  const ttlApplies = to !== null && !to.includes("@");
  const ttlOk = ttlApplies
    ? ttl !== null && ttl >= 1
    : d.ttlDeliveries === undefined;
  if (id === null || to === null || offset === null || bytes === null || kind === null || topic === null
      || priority === null || !ttlOk || agent === null || harness === null
      || (from !== null && from.session !== undefined && fromSession === null) || !threadOk || !fwdOk) {
    return refuse(s, "mail.append", "id/to/offset/bytes/from/kind/topic/priority/ttlDeliveries/inReplyTo+hops/forwardedFrom", atMs, null, null, offset);
  }
  const line: MailLedgerLine = {
    offset, bytes, terminated: true, hash: null,
    envelope: { id, ts: null, from: { agent, harness, session: fromSession }, to, kind, topic, priority,
      // `inReplyTo`/`hops` DO ride the trail (unlike `ts`, `body`, `prev`),
      // because the live picture has to be able to close a thread the moment
      // its answer lands rather than at the next snapshot.
      inReplyTo, hops: hops ?? 0, replyTo: null, forwardedFrom,
      ttlDeliveries: ttlApplies ? ttl : null, body: null, prev: null },
    errors: [], source: "trail",
  };

  const tornLine = s.torn === null ? null : s.lines[s.lines.length - 1];
  const expected = tornLine !== null ? tornLine.offset + tornLine.bytes + 1 : s.frontier;

  if (tornLine !== null && offset === tornLine.offset) {
    // The snapshot caught THIS append in flight (readers never take the store
    // lock): the torn tail was its first bytes. Now it is whole — the event
    // says how long and what it carries. Keep the snapshot's parse when the
    // partial JSON already parsed (it was only missing its newline: richer,
    // body included); else the event's provenance is the envelope.
    const whole: MailLedgerLine = tornLine.envelope !== null && tornLine.bytes === bytes
      ? { ...tornLine, terminated: true }
      : { ...line, hash: tornLine.hash !== null && tornLine.bytes === bytes ? tornLine.hash : null };
    const lines = s.lines.slice(0, -1);
    lines.push(whole);
    const cursors = s.cursors.map((c) => absorbCompleteLine(c, whole));
    return { ...s, lines, cursors, closedBy: closeThread(s.closedBy, whole),
      torn: null, pendingTorn: null, frontier: offset + bytes + 1 };
  }

  if (offset < expected) {
    // Behind where the next line must start: either a replay of a line the
    // snapshot already holds (benign — the same envelope at the same place)
    // or a different ledger at this path (a replaced chain, a truncation and
    // regrowth): the picture is of a store that no longer exists.
    const known = s.lines.find((l) => l.offset === offset);
    if (known && known.envelope && clampField(known.envelope.id) === id && known.envelope.to === to) return s;
    return raise(note(s, { kind: "ledger-diverged", severity: "warn", role: null, session: null, offset,
      message: `an append landed at ${offset}, inside the ledger the picture already holds (next line expected at ${expected}) — a different store at this path?`, atMs }),
      "ledger-diverged", `append at ${offset} < expected ${expected}`, atMs);
  }

  let next = s;
  let cursors = s.cursors;
  if (tornLine !== null && offset === expected) {
    // The append terminated the torn tail: it is now an ordinary COMPLETE
    // line — malformed (stepped over and counted) or, if the JSON was whole
    // and only the newline missing, a legitimate envelope now consumable.
    const terminated = { ...tornLine, terminated: true };
    const lines = s.lines.slice(0, -1);
    lines.push(terminated);
    cursors = cursors.map((c) => absorbCompleteLine(c, terminated));
    next = { ...s, lines, torn: null, pendingTorn: null, frontier: expected };
  } else if (offset > expected) {
    // A hole in the ledger picture: appends we never saw (a stream gap, a
    // trail that started mid-life, or a torn tail written after the snapshot
    // and terminated now — `mail.torn` will have said so). The bytes between
    // are unknown: not fabricated, not counted. Cursors that would have seen
    // them cannot be trusted; say so and ask for a snapshot.
    const missing = offset - expected;
    const explainedByTorn = s.pendingTorn !== null
      && (s.pendingTorn === expected || (tornLine !== null && s.pendingTorn === tornLine.offset));
    const n: MailNote = explainedByTorn
      ? { kind: "torn-terminated", severity: "warn", role: null, session: null, offset: expected,
          message: `a torn tail at ${expected} (${missing - 1} byte(s), contents unknown to this picture) was terminated by the append at ${offset}`, atMs }
      : { kind: "ledger-gap", severity: "warn", role: null, session: null, offset: expected,
          message: `${missing} byte(s) of ledger between ${expected} and ${offset} were never seen — appends missed`, atMs };
    next = raise(note(s, n), explainedByTorn ? "torn-terminated" : "ledger-gap", n.message, atMs);
    cursors = cursors.map((c) =>
      c.offset !== null && c.offset <= expected && c.uncertain === null
        ? { ...c, uncertain: `the ledger between ${expected} and ${offset} is unknown to this picture` }
        : c);
    // If a torn line was known, the hole is (or begins with) its unseen
    // growth: it is terminated now, spanning up to the new line — its
    // contents past what the snapshot showed are unknown, which the flag
    // above already says.
    if (tornLine !== null) {
      const lines = next.lines.slice(0, -1);
      lines.push({ ...tornLine, terminated: true,
        bytes: explainedByTorn ? offset - tornLine.offset - 1 : tornLine.bytes });
      next = { ...next, lines, torn: null };
    }
    next = { ...next, pendingTorn: null };
  }

  const lines = [...next.lines, line];
  cursors = cursors.map((c) => absorbCompleteLine(c, line));
  return { ...next, lines, cursors, closedBy: closeThread(next.closedBy, line),
    frontier: offset + bytes + 1, torn: null };
}

/** A complete line became visible at or beyond a cursor's offset: fresh if it
 * is this role's parseable mail, a skipped count if unreadable, invisible if
 * another role's — MailCursors.Pending's walk, one line at a time. */
function absorbCompleteLine(c: MailCursorState, line: MailLedgerLine): MailCursorState {
  if (c.offset === null || line.offset < c.offset) return c;
  if (line.envelope === null) return { ...c, skippedMalformed: c.skippedMalformed + 1 };
  if (!accepts(c, line.envelope.to)) return c;   // the recipient predicate, site 1 of 4
  if (c.fresh.some((f) => f.offset === line.offset)) return c;
  const e = line.envelope;
  const fresh = [...c.fresh, { offset: line.offset, id: e.id, priority: e.priority, ttlDeliveries: e.ttlDeliveries }].sort(byOffset);
  return { ...c, fresh };
}

// ---- mail.cursorAdvance ------------------------------------------------------------

function onAdvance(s: MailState, d: Record<string, unknown>, session: MailSession, atMs: number): MailState {
  const role = str(d.role), offset = nonNeg(d.offset), deliveries = nonNeg(d.deliveries);
  const deliveredCount = nonNeg(d.delivered), heldCount = nonNeg(d.held), expiredCount = nonNeg(d.expired);
  const deliveredOffsets = Array.isArray(d.deliveredOffsets) && d.deliveredOffsets.every((o) => nonNeg(o) !== null)
    ? (d.deliveredOffsets as number[]).slice().sort((a, b) => a - b) : null;
  if (role === null || offset === null || deliveries === null || deliveries < 1 || deliveredCount === null
      || heldCount === null || expiredCount === null || deliveredOffsets === null
      || deliveredOffsets.length !== deliveredCount
      || new Set(deliveredOffsets).size !== deliveredOffsets.length) {   // one position is one envelope
    return refuse(s, "mail.cursorAdvance", "role/offset/deliveries/delivered/deliveredOffsets/held/expired", atMs, role, session, offset);
  }
  // WHICH MAILBOX, not which window (ADR-0018 d3): the cursor this advance
  // moved is keyed by the row's `instance` when it carries one. Presence
  // already learned the WINDOW from the row's `sessionId`, one layer up — the
  // two facts are separate and stay separate.
  const key = mailboxKey(d, session);
  const ev = { role, session: key, offset, deliveries, deliveredOffsets, heldCount, expiredCount, atMs };
  const idx = s.cursors.findIndex((c) => c.role === role && sameSession(c.session, key));
  const c = idx >= 0 ? s.cursors[idx] : null;

  let next = touchPresence(s, session, role, atMs);
  let cursor: MailCursorState;

  if (c === null || c.offset === null || c.deliveries === null) {
    // A cursor the picture did not have (or had only through a delivery
    // record). deliveries 1 is FIRST CONTACT — anchored at 0, so its pending
    // set was the whole retained ledger for the role: reconstructible exactly
    // when the ledger is complete. Anything else joined mid-lineage: its held
    // set is unknown until a snapshot says.
    const base: MailCursorState = c ?? blankCursor(role, key, str(d.instance) !== null);
    if (deliveries === 1) {
      const r = reconstructFromZero(next, base, ev, "first contact");
      cursor = r.cursor;
      next = r.state;
      if (c === null) next = note(next, { kind: "advance-unknown-cursor", severity: "info", role, session: key, offset,
        message: `first contact: ${label(role, key)} anchored at 0 and advanced to ${offset} (deliveries 1)`, atMs });
    } else {
      cursor = { ...base, offset, deliveries, held: [], fresh: freshFromLedger(next, { role, session: key }, offset),
        skippedMalformed: skippedFromLedger(next, offset), reanchored: false, reanchorReason: null,
        uncertain: `first seen mid-lineage at deliveries ${deliveries} — its held set is unknown until a snapshot`,
        lastAdvance: mark(ev, []), lastEventKind: "advance", lastEventAtMs: atMs };
      next = raise(note(next, { kind: "advance-unknown-cursor", severity: "warn", role, session: key, offset,
        message: `${label(role, key)} advanced (deliveries ${deliveries}) but the picture never saw it anchor — held set unknown`, atMs }),
        "advance-unknown-cursor", `${label(role, key)} joined at deliveries ${deliveries}`, atMs);
    }
    return replaceCursor(next, idx, cursor);
  }

  // Known cursor: `deliveries` is its sequence number, `offset` its position.
  // A real advance NEVER moves the position backwards (a shrinking store
  // announces itself with a re-anchor first), so an event behind the picture
  // is a replay of something already reflected — whatever its count, and
  // including an old lineage's advance arriving after a restart. So is the
  // very same advance again, and so is a lower count at the SAME offset with
  // more than one delivery behind it (an earlier held-mail delivery at a
  // quiet frontier: the ADR's own replay shape). The one count that is not
  // read as stale at the same offset is 1 — see below.
  if (offset < c.offset) return s;
  if (deliveries === c.deliveries && offset === c.offset) return s;
  if (deliveries < c.deliveries && deliveries !== 1 && offset === c.offset) return s;
  if (deliveries === c.deliveries + 1) {
    const r = applyAdvance(next, c, ev);
    return replaceCursor(r.state, idx, r.cursor);
  }
  if (deliveries === 1 && offset > c.offset) {
    // The counter fell back to 1 while the position moved FORWARD: only a
    // deleted cursor re-anchoring silently does that (d13's quiet corner —
    // first contact and post-deletion are byte-identical on disk). The engine
    // cannot be loud about it; the picture can. Reconstructible exactly, as
    // any first advance is.
    const r = reconstructFromZero(next, c, ev, "lineage restarted");
    next = note(r.state, { kind: "lineage-restarted", severity: "warn", role, session, offset,
      message: `${label(role, key)}'s deliveries fell from ${c.deliveries} back to 1 — its cursor file was deleted and re-anchored silently; everything retained redelivered`, atMs });
    return replaceCursor(next, idx, r.cursor);
  }
  // Out of sequence in a way nothing explains: skipped advances (a gap), a
  // stale event indistinguishable from a restart (same offset, lower count),
  // or a lineage the reducer never saw begin. Apply the position — the event
  // is authoritative about where the cursor IS — and refuse to pretend about
  // the rest.
  const why = deliveries === 1
    ? `deliveries went from ${c.deliveries} back to 1 at the same offset — a replay of the first advance or a lineage restarted with nothing new appended; the two are indistinguishable here`
    : deliveries < c.deliveries
      ? `deliveries went from ${c.deliveries} to ${deliveries} while the position moved forward — a restarted lineage whose first advance was never seen`
      : `deliveries jumped from ${c.deliveries} to ${deliveries} — ${deliveries - c.deliveries - 1} advance(s) never seen`;
  const broken: MailCursorState = { ...c, offset, deliveries, held: [], fresh: freshFromLedger(next, { role, session: key }, offset),
    skippedMalformed: skippedFromLedger(next, offset), reanchored: false, reanchorReason: null,
    uncertain: why, lastAdvance: mark(ev, []), lastEventKind: "advance", lastEventAtMs: atMs };
  next = raise(note(next, { kind: "advance-out-of-sequence", severity: "warn", role, session: key, offset, message: why, atMs }),
    "advance-out-of-sequence", `${label(role, key)}: ${why}`, atMs);
  return replaceCursor(next, idx, broken);
}

type AdvanceEvent = {
  role: string; session: MailSession; offset: number; deliveries: number;
  deliveredOffsets: number[]; heldCount: number; expiredCount: number; atMs: number;
};

function mark(ev: AdvanceEvent, delivered: { offset: number; id: string }[]): MailAdvanceMark {
  return { deliveries: ev.deliveries, offset: ev.offset, delivered, seam: null, vehicle: null, dispatchId: null, atMs: ev.atMs };
}

/** The in-sequence advance: the exact arithmetic of MailCursors.Advance over
 * the pending set the reducer holds, cross-checked against the counts the
 * event carries. */
function applyAdvance(s: MailState, c: MailCursorState, ev: AdvanceEvent): { state: MailState; cursor: MailCursorState } {
  const oldOffset = c.offset as number, oldDeliveries = c.deliveries as number;
  let next = s;
  let uncertain = c.uncertain;
  const problems: string[] = [];

  if (ev.offset < oldOffset) problems.push(`the cursor moved backwards (${oldOffset} → ${ev.offset})`);
  if (ev.offset > s.frontier) {
    problems.push(`advanced to ${ev.offset}, past the ledger the picture knows (frontier ${s.frontier}) — appends missed`);
    next = raise(note(next, { kind: "advance-past-frontier", severity: "warn", role: c.role, session: c.session, offset: ev.offset,
      message: `${label(c.role, c.session)} advanced to ${ev.offset} but the picture's ledger ends at ${s.frontier}`, atMs: ev.atMs }),
      "advance-past-frontier", `${label(c.role, c.session)} → ${ev.offset} > frontier ${s.frontier}`, ev.atMs);
  }

  // The view the digest advanced from: held (classified at the OLD count) +
  // the fresh items the read could see (offset < the new offset — anything
  // appended after the read is not consumed by it: MailArrivingAfterTheRead).
  const heldPending = c.held.filter((h) => !isExpired(h, oldDeliveries));
  const expired = c.held.filter((h) => isExpired(h, oldDeliveries));
  const consumedFresh = c.fresh.filter((f) => f.offset < ev.offset);
  const remainingFresh = c.fresh.filter((f) => f.offset >= ev.offset);
  const pending: { offset: number; id: string; priority: string; ttlDeliveries: number | null; seenAt: number | null }[] = [
    ...heldPending.map((h) => ({ ...h, seenAt: h.seenAt as number | null })),
    ...consumedFresh.map((f) => ({ ...f, seenAt: null })),
  ].sort(byOffset);

  const delivered = new Set(ev.deliveredOffsets);
  const notPending = ev.deliveredOffsets.filter((o) => !pending.some((p) => p.offset === o));
  if (notPending.length > 0) problems.push(`delivered offset(s) ${notPending.join(", ")} were not pending in the picture`);

  const newHeld: MailHeldItem[] = pending.filter((p) => !delivered.has(p.offset))
    .map((p) => ({ offset: p.offset, id: p.id, priority: p.priority, ttlDeliveries: p.ttlDeliveries, seenAt: p.seenAt ?? ev.deliveries }));
  if (newHeld.length !== ev.heldCount) problems.push(`held: picture ${newHeld.length}, digest wrote ${ev.heldCount}`);
  if (expired.length !== ev.expiredCount) problems.push(`expired: picture ${expired.length}, digest dropped ${ev.expiredCount}`);

  const deliveredItems = pending.filter((p) => delivered.has(p.offset)).map((p) => ({ offset: p.offset, id: p.id }));
  const lastDeliveredId = deliveredItems.length > 0 ? deliveredItems[deliveredItems.length - 1].id : c.lastDeliveredId;

  if (problems.length > 0) {
    const why = problems.join("; ");
    uncertain = uncertain ?? why;
    next = raise(note(next, { kind: "advance-mismatch", severity: "warn", role: c.role, session: c.session, offset: ev.offset,
      message: `${label(c.role, c.session)}: the picture's arithmetic disagrees with the digest's — ${why}`, atMs: ev.atMs }),
      "advance-mismatch", `${label(c.role, c.session)}: ${why}`, ev.atMs);
  }

  const cursor: MailCursorState = {
    ...c, offset: ev.offset, deliveries: ev.deliveries, held: newHeld, fresh: remainingFresh, lastDeliveredId,
    skippedMalformed: skippedFromLedger(next, ev.offset),
    reanchored: false, reanchorReason: null, uncertain,
    lastAdvance: mark(ev, deliveredItems), lastEventKind: "advance", lastEventAtMs: ev.atMs,
  };
  return { state: next, cursor };
}

/** A first advance (deliveries 1) came from an anchor at 0: pending was every
 * retained line for the role, in file order. With a complete ledger this is
 * an EXACT reconstruction — and it clears any earlier uncertainty, because
 * nothing about the cursor's past matters after an anchor. With a partial
 * ledger (`since` > 0) it is not, and says so. */
function reconstructFromZero(s: MailState, c: MailCursorState, ev: AdvanceEvent, why: string): { state: MailState; cursor: MailCursorState } {
  const anchored: MailCursorState = {
    ...c, offset: 0, deliveries: 0, held: [], fresh: freshFromLedger(s, c, 0),
    skippedMalformed: skippedFromLedger(s, 0), lastDeliveredId: null,
    reanchored: false, reanchorReason: null,
    uncertain: s.since > 0
      ? `${why} at deliveries 1, but the picture's ledger starts at ${s.since} — the pending set it advanced over is unknown`
      : null,
  };
  const r = applyAdvance(s, anchored, ev);
  if (anchored.uncertain !== null && r.cursor.uncertain !== null)
    r.state = raise(r.state, "advance-unknown-cursor", `${label(c.role, c.session)}: ${anchored.uncertain}`, ev.atMs);
  return r;
}

function freshFromLedger(s: MailState, box: { role: string; session: MailSession }, from: number): MailFreshItem[] {
  return s.lines
    .filter((l) => l.terminated && l.offset >= from && l.envelope !== null && accepts(box, l.envelope.to))
    .map((l) => ({ offset: l.offset, id: l.envelope!.id, priority: l.envelope!.priority, ttlDeliveries: l.envelope!.ttlDeliveries }));
}
function skippedFromLedger(s: MailState, from: number): number {
  return s.lines.filter((l) => l.terminated && l.offset >= from && l.envelope === null).length;
}

function blankCursor(role: string, session: MailSession, named = false): MailCursorState {
  return {
    role, session, named, gen: null, head: null, offset: null, deliveries: null, lastDeliveredId: null,
    held: [], fresh: [], skippedMalformed: 0, reanchored: false, reanchorReason: null,
    origin: "trail", uncertain: null, lastAdvance: null, lastEventKind: null, lastEventAtMs: null,
  };
}

// ---- mail.expire -------------------------------------------------------------------

function onExpire(s: MailState, d: Record<string, unknown>, session: MailSession, atMs: number): MailState {
  // `to` is the ENVELOPE's address, which for a unicast is `role@instance` —
  // so the lane it belongs to is its ROLE half, and the mailbox that spent it
  // is the row's own `instance` (else the window). Reading `to` as a role
  // outright would look for a cursor on a lane called `main@laptop-a`, which
  // no reader is ever keyed by.
  const id = str(d.id), to = str(d.to), offset = nonNeg(d.offset), ttl = num(d.ttlDeliveries), seenAt = nonNeg(d.seenAt);
  const role = to === null ? null : roleOf(to);
  if (id === null || role === null || offset === null || ttl === null || seenAt === null)
    return refuse(s, "mail.expire", "id/to/offset/ttlDeliveries/seenAt", atMs, role, session, offset);
  const key = mailboxKey(d, session);
  const idx = s.cursors.findIndex((c) => c.role === role && sameSession(c.session, key));
  if (idx < 0) return note(s, { kind: "expire-mismatch", severity: "warn", role, session: key, offset,
    message: `expire for ${label(role, key)}, a cursor the picture does not have`, atMs });
  const c = s.cursors[idx];
  const h = c.held.find((x) => x.offset === offset);
  let next = s;
  let cursor = c;
  if (h === undefined) {
    // Already dropped by an advance the picture applied (a stale replay), or
    // never held here at all — the second is a divergence.
    if (c.offset !== null && offset < c.offset && !c.fresh.some((f) => f.offset === offset)) return s;
    next = note(next, { kind: "expire-mismatch", severity: "warn", role, session: key, offset,
      message: `the digest expired ${id} at ${offset} but the picture does not hold it for ${label(role, key)}`, atMs });
    cursor = { ...c, uncertain: c.uncertain ?? `expired an envelope (${offset}) the picture did not hold` };
  } else if (c.deliveries !== null && !isExpired(h, c.deliveries)) {
    next = note(next, { kind: "expire-mismatch", severity: "warn", role, session: key, offset,
      message: `the digest expired ${id} at ${offset} but the picture's arithmetic (${opportunities(c.deliveries, h.seenAt)} of ${h.ttlDeliveries} opportunities) says it is still held`, atMs });
    cursor = { ...c, uncertain: c.uncertain ?? `expiry arithmetic diverged at ${offset}` };
  }
  cursor = { ...cursor, lastEventKind: "expire", lastEventAtMs: atMs };
  return replaceCursor(next, idx, cursor);
}

// ---- mail.deliver ------------------------------------------------------------------

function onDeliver(s: MailState, d: Record<string, unknown>, session: MailSession, line: TrailLine, ts: string | null, atMs: number): MailState {
  const role = str(d.role), seam = str(d.seam), vehicle = str(d.vehicle);
  const ids = Array.isArray(d.envelopeIds) && d.envelopeIds.every((x) => typeof x === "string") ? (d.envelopeIds as string[]) : null;
  if (role === null || seam === null || vehicle === null || ids === null)
    return refuse(s, "mail.deliver", "role/seam/vehicle/envelopeIds", atMs, role, session, null);
  const dispatchId = typeof line.dispatchId === "string" ? line.dispatchId : null;
  const hookEvent = typeof line.hookEvent === "string" ? line.hookEvent : null;

  // Presence is the WINDOW's (it really did dispatch); the cursor is the
  // MAILBOX's (ADR-0018 d3). For a named registration those are two different
  // names, and filing this record under the window would join it to no cursor
  // at all — the `delivered` glyph would never appear for a `--as` mailbox.
  const key = mailboxKey(d, session);
  let next = touchPresence(s, session, role, atMs);
  const idx = next.cursors.findIndex((c) => c.role === role && sameSession(c.session, key));
  let c = idx >= 0 ? next.cursors[idx] : null;

  if (c === null) {
    // A delivery proves a cursor exists (only an advance that succeeded emits
    // one) — but not WHERE it is. Record the fact, place nothing.
    c = { ...blankCursor(role, key, str(d.instance) !== null), uncertain: "seen only through a delivery record — position unknown until a snapshot" };
    next = raise(note(next, { kind: "deliver-unknown-cursor", severity: "warn", role, session: key, offset: null,
      message: `a delivery to ${label(role, key)}, a cursor the picture never saw advance`, atMs }),
      "deliver-unknown-cursor", label(role, key), atMs);
  }

  // Resolve ids → offsets. First against the advance this delivery belongs
  // to (the exact consumed set, matched once each so duplicate ids map to
  // distinct offsets); then, for a record that arrives without its advance
  // (a replay after a re-seed), against consumed lines — accepted only when
  // UNIQUE, since a wrong duplicate is a false picture too. Else null.
  const pool = c.lastAdvance !== null && c.lastAdvance.seam === null ? [...c.lastAdvance.delivered] : [];
  const { offsets, unmatched } = resolveDelivery(next.lines, c, ids, pool);
  if (unmatched.length > 0)
    next = note(next, { kind: "deliver-unmatched", severity: "warn", role, session: key, offset: null,
      message: `a delivery to ${label(role, key)} names ${unmatched.length} envelope(s) the picture cannot place (${unmatched.map((u) => `'${u}'`).join(", ")}) — the trail may be truncated, or the append predates this picture`, atMs });

  const record: MailDeliveryRecord = {
    role, session: key, window: session, dispatchId, hookEvent, seam, vehicle, envelopeIds: [...ids], offsets,
    renderHash: str(d.renderHash), bytesInjected: nonNeg(d.bytesInjected), ts, atMs,
  };
  // The same ledger line again (a reconnect re-sent it, or a re-seed kept the
  // record and the stream replays the region): one delivery, one record.
  // A record has no sequence number, so its identity is its content — the
  // dispatch it answered, the recipient, and the exact rendering it attests.
  if (next.deliveries.some((r) => sameDelivery(r, record))) return s;
  const deliveries = [...next.deliveries, record];
  if (deliveries.length > MAIL_DELIVERIES_CAP) deliveries.splice(0, deliveries.length - MAIL_DELIVERIES_CAP);

  const lastAdvance = c.lastAdvance !== null && c.lastAdvance.seam === null
    ? { ...c.lastAdvance, seam, vehicle, dispatchId } : c.lastAdvance;
  const cursor: MailCursorState = { ...c, lastAdvance, lastEventKind: "deliver", lastEventAtMs: atMs };
  return replaceCursor({ ...next, deliveries }, idx, cursor);
}

/** Place a record's envelope ids on the ledger, by ONE rule wherever a record
 * comes from — the live stream (`onDeliver`) or the snapshot's preload
 * (`seedMail`). First against the advance this delivery belongs to, when there
 * is one (the exact consumed set, each entry matched once so duplicate ids map
 * to distinct offsets); then against consumed lines, accepted only when UNIQUE,
 * since a wrong duplicate is a false picture too. Else null — an id the picture
 * cannot place honestly is placed nowhere. */
function resolveDelivery(
  lines: MailLedgerLine[], c: MailCursorState, ids: string[], pool: { id: string; offset: number }[],
): { offsets: (number | null)[]; unmatched: string[] } {
  const offsets: (number | null)[] = [];
  const unmatched: string[] = [];
  for (const id of ids) {
    const hit = pool.findIndex((p) => clampField(p.id) === id);
    if (hit >= 0) { offsets.push(pool[hit].offset); pool.splice(hit, 1); continue; }
    const behind = c.offset;
    const cands = lines.filter((l) => l.envelope !== null && accepts(c, l.envelope.to) && clampField(l.envelope.id) === id
      && (behind === null || l.offset < behind) && !c.held.some((h) => h.offset === l.offset));
    if (cands.length === 1) { offsets.push(cands[0].offset); continue; }
    offsets.push(null);
    unmatched.push(id);
  }
  return { offsets, unmatched };
}

function sameDelivery(a: MailDeliveryRecord, b: MailDeliveryRecord): boolean {
  return a.role === b.role && sameSession(a.session, b.session) && a.dispatchId === b.dispatchId
    && a.hookEvent === b.hookEvent && a.seam === b.seam && a.vehicle === b.vehicle
    && a.renderHash === b.renderHash && a.envelopeIds.length === b.envelopeIds.length
    && a.envelopeIds.every((id, i) => id === b.envelopeIds[i]);
}

// ---- mail.nudge --------------------------------------------------------------------

/** Fold one `mail.nudge` (ADR-0017 d10). Three things it deliberately does NOT
 * do, each because a nudge is a poke and not a delivery:
 *
 *   * it moves NO cursor and marks nothing delivered — being nudged about mail
 *     is the opposite of having read it, and this row exists precisely because
 *     nobody has;
 *   * it claims NO presence. The row carries no session by design, and a
 *     dispatch raised BY the watcher must never count as a sighting: presence
 *     feeds the watcher's own "is anybody live?" question (d4), and a nudge
 *     that fed it would let the watcher's action answer the watcher's question;
 *   * it raises no anomaly for an id it cannot place. A nudge names mail that
 *     is by definition unread, which a `?since=` window may simply start above.
 *
 * A DENIED nudge writes no row at all (d10) — `nudge.denied` is not a `mail.*`
 * event and never reaches here — so every row this folds is a poke that really
 * went out, which is exactly what makes drawing it honest. */
function onNudge(s: MailState, d: Record<string, unknown>, line: TrailLine, ts: string | null, atMs: number): MailState {
  const role = str(d.role), reason = str(d.reason);
  const ids = Array.isArray(d.envelopeIds) && d.envelopeIds.every((x) => typeof x === "string")
    ? (d.envelopeIds as string[]) : null;
  if (role === null || reason === null || ids === null)
    return refuse(s, "mail.nudge", "role/envelopeIds/reason", atMs, role, null, null);

  // The budget rides as four numbers or not at all: a row carrying a PARTIAL
  // budget is a row whose arithmetic disagrees with itself, and half a cost is
  // worse than no cost — it renders as a number a reader would believe.
  const b = isRecord(d.budget) ? d.budget : null;
  const nums = b === null ? null : {
    envelope: nonNeg(b.envelope), perEnvelope: nonNeg(b.perEnvelope),
    roleHour: nonNeg(b.roleHour), perRoleHour: nonNeg(b.perRoleHour),
  };
  const budget = nums !== null && nums.envelope !== null && nums.perEnvelope !== null
    && nums.roleHour !== null && nums.perRoleHour !== null
    ? { envelope: nums.envelope, perEnvelope: nums.perEnvelope, roleHour: nums.roleHour, perRoleHour: nums.perRoleHour }
    : null;

  const address = str(d.address);
  const record: MailNudgeRecord = {
    role,
    address,
    workspace: str(d.workspace),
    envelopeIds: [...ids],
    // Placed on the lane the nudge is ABOUT, which for a dead-mailbox row is
    // the corpse's, not the reaper's.
    offsets: placeById(s.lines, address === null ? role : roleOf(address), ids),
    reason,
    budget,
    dispatchId: typeof line.dispatchId === "string" ? line.dispatchId : null,
    ts,
    atMs,
  };
  // One poke, one mark. A nudge has no sequence number either, so its identity
  // is its content — the dispatch it raised, the lane, and the envelopes it
  // named — and a reconnect that re-sends the region must not draw it twice.
  if (s.nudges.some((n) => sameNudge(n, record))) return s;
  const nudges = [...s.nudges, record];
  if (nudges.length > MAIL_NUDGES_CAP) nudges.splice(0, nudges.length - MAIL_NUDGES_CAP);
  return { ...s, nudges };
}

function sameNudge(a: MailNudgeRecord, b: MailNudgeRecord): boolean {
  return a.role === b.role && a.address === b.address && a.dispatchId === b.dispatchId
    && a.reason === b.reason && a.envelopeIds.length === b.envelopeIds.length
    && a.envelopeIds.every((id, i) => id === b.envelopeIds[i]);
}

/** Where an id sits on a lane, or null. The nudge's placement rule, and NOT
 * `resolveDelivery`'s: that one answers "which line did this cursor consume"
 * and is therefore cursor-relative (behind the offset, not held). This one
 * answers "which line on this role's lane bears this id", which has nothing to
 * do with any cursor — a nudge is about mail every cursor is still behind.
 *
 * Unique-or-nothing, for the same reason the delivery rule is: a wrong
 * placement draws a poke pointing at the wrong envelope, which is a false
 * picture, and two ids can collide across generations after a rotation.
 * Ids are compared CLAMPED on both sides — the trail's are clamped at the
 * source (`MailEnvelope.ClampField`) and a snapshot's are whole. */
function placeById(lines: MailLedgerLine[], role: string, ids: string[]): (number | null)[] {
  return ids.map((id) => {
    const key = clampField(id);
    // The LANE, not the address: a dead-mailbox nudge names mail stranded in
    // `role@instance`, and what is stranded there is a mix of the role's
    // broadcast and that mailbox's own unicast (ADR-0018 d4). Both live on the
    // role's lane, which is where the mark is drawn, so that is the set to
    // search. Unique-or-nothing still decides, as it always did.
    const cands = lines.filter((l) => l.envelope !== null && roleOf(l.envelope.to) === role
      && clampField(l.envelope.id) === key);
    return cands.length === 1 ? cands[0].offset : null;
  });
}

/** Which LANE a nudge is about. An ordinary nudge is about its role; a
 * dead-mailbox nudge (d6) goes TO the reaper and is about somebody else's
 * mailbox, so its subject is the address's role half — the lane the stranded
 * mail is actually hanging on. Drawing it on a `reaper` lane would put the mark
 * where the mail is not, and that lane usually does not exist at all. */
export function nudgeLane(n: MailNudgeRecord): string {
  return n.address === null ? n.role : roleOf(n.address);
}

/** Every nudge that named this ledger position — what the lane draws a mark
 * for, and what the detail card lists. */
export function nudgesFor(state: MailState, offset: number): MailNudgeRecord[] {
  return state.nudges.filter((n) => n.offsets.includes(offset));
}

// ---- mail.forward / mail.reap (ADR-0018 d6) ----------------------------------------

/** Fold one `mail.forward`. It moves NO cursor and marks nothing delivered —
 * the whole point of a forward is that the mailbox it came out of never read
 * this mail — and it places nothing on the ledger, because the copies arrive as
 * their own `mail.append` rows carrying `forwardedFrom`, which is the join the
 * picture actually draws. What this row adds is the half a copy cannot state:
 * WHO moved it, and what was skipped as already moved. */
function onForward(s: MailState, d: Record<string, unknown>, ts: string | null, atMs: number): MailState {
  const role = str(d.role), to = str(d.to);
  const ids = Array.isArray(d.forwardedIds) && d.forwardedIds.every((x) => typeof x === "string")
    ? (d.forwardedIds as string[]) : null;
  const skipped = d.skippedIds === undefined ? [] :
    (Array.isArray(d.skippedIds) && d.skippedIds.every((x) => typeof x === "string") ? (d.skippedIds as string[]) : null);
  if (role === null || to === null || ids === null || skipped === null)
    return refuse(s, "mail.forward", "role/to/forwardedIds/skippedIds", atMs, role, null, null);

  // The source is spelled the way the trail spells every mailbox — `role` plus
  // `instance` when there is one — and joined here into the address a sender
  // would write, because that is what the copies' `forwardedFrom.address`
  // holds and the two have to compare equal.
  const instance = str(d.instance);
  const source = instance === null ? role : `${role}@${instance}`;
  const record: MailForwardRecord = { source, to, by: str(d.by), forwardedIds: [...ids], skippedIds: [...skipped], ts, atMs };
  // One row, one record: a reconnect that re-sends the line must not double it.
  // A forward has no sequence number, so its identity is its content — the same
  // rule the delivery records follow.
  if (s.forwards.some((f) => sameForward(f, record))) return s;
  const forwards = [...s.forwards, record];
  if (forwards.length > MAIL_FORWARDS_CAP) forwards.splice(0, forwards.length - MAIL_FORWARDS_CAP);
  return note({ ...s, forwards }, { kind: "forward", severity: "info", role, session: instance, offset: null,
    message: `${ids.length} envelope(s) stranded in ${source} were copied to ${to}${record.by === null ? "" : ` by ${record.by}`}`
      + `${skipped.length > 0 ? ` (${skipped.length} already forwarded, skipped)` : ""} — the originals stay on the ledger`, atMs });
}

function sameForward(a: MailForwardRecord, b: MailForwardRecord): boolean {
  return a.source === b.source && a.to === b.to && a.by === b.by && a.ts === b.ts
    && a.forwardedIds.length === b.forwardedIds.length && a.forwardedIds.every((id, i) => id === b.forwardedIds[i]);
}

/** Fold one `mail.reap`: the mailbox's STANDING is gone (ADR-0018 d6), so the
 * cursor leaves the picture — which is exactly what the next snapshot will say,
 * since the file it was read from no longer exists.
 *
 * Nothing else changes. The mail this box was holding stays on the ledger, at
 * its offsets, unread: `pendingIds` names it, and after this it is pending for
 * nobody rather than pending for a corpse. That is the honest picture, and it
 * is why the reap leaves a NOTE — a track that simply disappeared would read as
 * a rendering bug, where the note says a member disposed of it. */
function onReap(s: MailState, d: Record<string, unknown>, atMs: number): MailState {
  const role = str(d.role);
  const ids = Array.isArray(d.pendingIds) && d.pendingIds.every((x) => typeof x === "string")
    ? (d.pendingIds as string[]) : null;
  if (role === null || ids === null) return refuse(s, "mail.reap", "role/pendingIds", atMs, role, null, null);
  const instance = str(d.instance);
  const address = instance === null ? role : `${role}@${instance}`;
  const by = str(d.by);

  const next = note(s, { kind: "reaped", severity: "warn", role, session: instance, offset: null,
    message: `${address}'s standing was removed${by === null ? " by hand" : ` by ${by}`}`
      + `${ids.length === 0 ? " — nothing was pending" : ` — ${ids.length} envelope(s) it never read stay on the ledger, now pending for nobody`}`, atMs });
  // A reap the picture never had a cursor for is not an anomaly: the mailbox
  // may have been created and disposed of between two snapshots, or below this
  // one's `?since=`. Removing nothing is the right answer either way.
  return { ...next, cursors: next.cursors.filter((c) => !(c.role === role && sameSession(c.session, instance))) };
}

// ---- the thread, read off the index (ADR-0017 d8) ----------------------------------

/** How an envelope stands in its thread. `awaiting` is a request nothing has
 * answered yet; `answered` is a request whose closer this picture knows, and
 * the answer that closed it; `late` is an answer that named a request some
 * OTHER answer had already closed — N5's case, where two members both answer
 * one ask, which is a normal race on a bus with no locking and not a fault.
 * The later answer is TAGGED, never hidden: it may well be the better one. */
export type MailThreadStanding = "awaiting" | "answered" | "late";

export type MailThreadView = {
  /** Which end of the thread this envelope is. */
  end: "request" | "answer";
  standing: MailThreadStanding;
  /** The other end, when this picture knows it: the answer that closed a
   * request, or the request an answer names. Null for an unanswered request. */
  counterpart: string | null;
  hops: number;
};

/** The thread reading of one envelope, or null when it is in no thread at all.
 *
 * Both halves are joins onto `closedBy`, which is the ONE index (seeded from
 * the snapshot's whole-ledger build, extended live by `closeThread`) — the
 * canvas computes no second one, because a thread the picture draws and a
 * thread the digest delivers on must be the same thread (N8's rule, worn as a
 * thread rather than as a pending set).
 *
 * Ids are compared CLAMPED throughout. Within one provenance it changes
 * nothing; across the mixed case it is load-bearing — a snapshot-seeded index
 * holds whole ids and a live `mail.append` teaches clamped ones, and comparing
 * the two raw would read every long-id answer as `late`, which is the one
 * misreading here that accuses somebody of losing a race they won. */
export function threadOf(state: MailState, e: MailEnvelopeView): MailThreadView | null {
  const closer = closerOf(state, e.id);
  if (e.kind === "request")
    return { end: "request", standing: closer === null ? "awaiting" : "answered", counterpart: closer, hops: e.hops };
  if (e.kind !== "answer" || e.inReplyTo === null) return null;
  const closedIts = closerOf(state, e.inReplyTo);
  // No entry at all is not "this one won" — it is a request this picture has
  // never seen closed, which happens when the index predates the answer (a
  // stream folded past a re-seed). Standing stays `answered` only when the
  // index NAMES this envelope; otherwise the honest tag is what it says.
  const standing: MailThreadStanding = closedIts === null ? "answered"
    : clampField(closedIts) === clampField(e.id) ? "answered" : "late";
  return { end: "answer", standing, counterpart: e.inReplyTo, hops: e.hops };
}

/** The id of the answer that closed this request, or null. Tries the key as
 * given and as clamped, because the index is keyed by whichever spelling the
 * side that taught it used. */
function closerOf(state: MailState, id: string): string | null {
  return state.closedBy[id] ?? state.closedBy[clampField(id)] ?? null;
}

// ---- the re-anchor family --------------------------------------------------------

function onReanchor(s: MailState, d: Record<string, unknown>, session: MailSession, atMs: number): MailState {
  const role = str(d.role), reason = str(d.reason), deliveries = nonNeg(d.deliveries), cause = str(d.cause);
  if (role === null || reason === null || deliveries === null || (cause !== "cursor" && cause !== "store"))
    return refuse(s, "mail.cursorReanchor", "role/reason/cause/deliveries", atMs, role, session, null);
  const key = mailboxKey(d, session);
  let next = touchPresence(s, session, role, atMs);
  const idx = next.cursors.findIndex((c) => c.role === role && sameSession(c.session, key));
  const known = idx >= 0 ? next.cursors[idx] : null;
  // The counter a re-anchor carries is the disk cursor's (or 0 when the file
  // was unreadable). It cannot be BELOW a count the picture already saw and
  // still be current — a re-anchor read later than an advance carries at
  // least that advance's count — so a lower non-zero count is a replay.
  if (known !== null && known.deliveries !== null && deliveries > 0 && deliveries < known.deliveries) return s;
  const base = known ?? blankCursor(role, key, str(d.instance) !== null);
  // The effective cursor after a re-anchor is Fresh(head, deliveries): offset
  // 0, nothing held, the counter carried. Its fresh set is every retained line
  // for the role — rebuildable only from a ledger that starts at 0 AND is
  // still the ledger on disk. `cause: "store"` says it is not: the bytes the
  // cursor described are gone or changed, and so is every line this picture
  // holds from before this instant — nothing here may be rebuilt from them.
  const partial = next.since > 0;
  const storeChanged = cause === "store";
  const uncertain = storeChanged
    ? `re-anchored because the STORE changed (${reason}) — the picture's ledger predates it`
    : partial ? `re-anchored at 0 but the picture's ledger starts at ${next.since} — the pending set below it is unknown` : null;
  const cursor: MailCursorState = {
    ...base, offset: 0, deliveries, held: [],
    fresh: storeChanged ? [] : freshFromLedger(next, { role, session: key }, 0),
    skippedMalformed: storeChanged ? 0 : skippedFromLedger(next, 0), lastDeliveredId: null,
    reanchored: true, reanchorReason: reason, uncertain,
    lastAdvance: null, lastEventKind: "reanchor", lastEventAtMs: atMs,
  };
  next = note(next, { kind: "reanchor", severity: "warn", role, session: key, offset: 0,
    message: `${label(role, key)} re-anchored at 0 (${reason}) — retained mail for the role is pending again; deliveries ${deliveries} carried`, atMs });
  if (storeChanged)
    next = raise(note(next, { kind: "store-changed", severity: "warn", role, session: key, offset: null,
      message: `the store changed under ${label(role, key)} (${reason}) — every line in this picture is suspect; re-snapshot`, atMs }),
      "store-changed", reason, atMs);
  else if (partial)
    next = raise(note(next, { kind: "reanchor-partial", severity: "warn", role, session: key, offset: 0,
      message: `${label(role, key)} re-anchored below the picture's since (${next.since}) — re-snapshot to see what is pending`, atMs }),
      "reanchor-partial", label(role, key), atMs);
  return replaceCursor(next, idx, cursor);
}

function onRefuse(s: MailState, d: Record<string, unknown>, session: MailSession, atMs: number): MailState {
  const role = str(d.role) ?? "?", reason = str(d.reason) ?? "(no reason)";
  const key = mailboxKey(d, session);
  const next = note(s, { kind: "refuse", severity: "info", role, session: key, offset: null,
    message: `${label(role, key)}: an advance was refused (${reason}) — the mail stays pending; usually a concurrent delivery winning the race`, atMs });
  return stampCursor(next, role, key, "refuse", atMs);
}

function onVanished(s: MailState, d: Record<string, unknown>, session: MailSession, atMs: number): MailState {
  const role = str(d.role) ?? "?";
  const key = mailboxKey(d, session);
  const viewDeliveries = nonNeg(d.viewDeliveries);
  const next = note(s, { kind: "vanished", severity: "warn", role, session: key, offset: null,
    message: `${label(role, key)}'s cursor file vanished under a view read at deliveries ${viewDeliveries ?? "?"} — the advance proceeds as a fresh lineage; mail it consumed may redeliver`, atMs });
  return stampCursor(next, role, key, "vanished", atMs);
}

// ---- shared mutations ------------------------------------------------------------

function refuse(s: MailState, evt: string, fields: string, atMs: number, role: string | null, session: MailSession, offset: number | null): MailState {
  const message = `${evt} carried a field the reducer cannot read (${fields}) — not applied; the picture may be behind`;
  return raise(note(s, { kind: "malformed-event", severity: "warn", role, session, offset, message, atMs }),
    "malformed-event", `${evt}: ${fields}`, atMs);
}

function raise(s: MailState, reason: MailResnapshotReason, detail: string, atMs: number): MailState {
  // The first reason wins until a seed clears it — the earliest cause is the
  // one worth reading; later ones are usually consequences.
  return s.resnapshot !== null ? s : { ...s, resnapshot: { reason, detail, atMs } };
}

function note(s: MailState, n: MailNote): MailState {
  const notes = [...s.notes, n];
  let dropped = s.notesDropped;
  if (notes.length > MAIL_NOTES_CAP) {
    dropped += notes.length - MAIL_NOTES_CAP;
    notes.splice(0, notes.length - MAIL_NOTES_CAP);
  }
  return { ...s, notes, notesDropped: dropped };
}

function replaceCursor(s: MailState, idx: number, cursor: MailCursorState): MailState {
  const cursors = idx >= 0 ? s.cursors.map((c, i) => (i === idx ? cursor : c)) : [...s.cursors, cursor];
  return { ...s, cursors };
}

function stampCursor(s: MailState, role: string, session: MailSession, kind: MailCursorEventKind, atMs: number): MailState {
  const idx = s.cursors.findIndex((c) => c.role === role && sameSession(c.session, session));
  if (idx < 0) return s;
  return replaceCursor(s, idx, { ...s.cursors[idx], lastEventKind: kind, lastEventAtMs: atMs });
}

function touchPresence(s: MailState, session: MailSession, role: string | null, atMs: number): MailState {
  if (session === null) return s;   // a sessionless reader has no presence to infer — the API's rule
  const idx = s.presence.findIndex((p) => p.session === session);
  if (idx < 0) {
    return { ...s, presence: [...s.presence, { session, roles: role === null ? [] : [role], lastSeenAtMs: atMs }] };
  }
  const p = s.presence[idx];
  const roles = role !== null && !p.roles.includes(role) ? [...p.roles, role] : p.roles;
  if (roles === p.roles && p.lastSeenAtMs === atMs) return s;
  const entry = { ...p, roles, lastSeenAtMs: atMs };
  return { ...s, presence: s.presence.map((x, i) => (i === idx ? entry : x)) };
}

export function label(role: string, session: MailSession): string {
  return session === null ? `${role} (sessionless)` : `${role}/${session}`;
}

// ---- selectors: the projection the canvas reads --------------------------------------

export type MailPendingItem = {
  offset: number;
  id: string;
  priority: string;
  /** null = unicast: no TTL, so the canvas draws a hold rather than a
   * countdown (ADR-0018 d5). */
  ttlDeliveries: number | null;
  seenAt: number | null;
  /** deliveries − seenAt + 1 for held mail; 0 for fresh — MailPendingDto's arithmetic. */
  opportunities: number;
};

export type MailCursorProjection = {
  role: string;
  session: MailSession;
  offset: number | null;
  deliveries: number | null;
  lastDeliveredId: string | null;
  pending: MailPendingItem[];
  expired: MailPendingItem[];
  skippedMalformed: number;
  reanchored: boolean;
  reanchorReason: string | null;
  uncertain: string | null;
};

/** What `MailCursors.Pending` would report for this cursor, from the reduced
 * state: held first (cursor order, offset ascending) then fresh in file order,
 * expired classified at the current deliveries count. */
export function projectCursor(c: MailCursorState): MailCursorProjection {
  const deliveries = c.deliveries;
  const pending: MailPendingItem[] = [];
  const expired: MailPendingItem[] = [];
  if (deliveries !== null) {
    for (const h of c.held) {
      const item = { offset: h.offset, id: h.id, priority: h.priority, ttlDeliveries: h.ttlDeliveries,
        seenAt: h.seenAt, opportunities: opportunities(deliveries, h.seenAt) };
      (isExpired(h, deliveries) ? expired : pending).push(item);
    }
    for (const f of c.fresh)
      pending.push({ offset: f.offset, id: f.id, priority: f.priority, ttlDeliveries: f.ttlDeliveries, seenAt: null, opportunities: 0 });
  }
  return {
    role: c.role, session: c.session, offset: c.offset, deliveries, lastDeliveredId: c.lastDeliveredId,
    pending, expired, skippedMalformed: c.skippedMalformed,
    reanchored: c.reanchored, reanchorReason: c.reanchorReason, uncertain: c.uncertain,
  };
}

export type MailLineStatus =
  | "fresh"        // at/after the cursor, this role's, parseable — pending, no TTL accrued
  | "held"         // passed over, still within its ttl
  | "expired"      // passed over ttl times — dropped at the next advance
  | "delivered"    // behind the cursor AND a mail.deliver record names it (the only source of this word)
  | "passed"       // behind the cursor, NO record — "before cursor · no record"
  | "foreign"      // another role's mail — invisible to this cursor
  | "unreadable"   // a malformed line — stepped over and counted
  | "torn"         // the unterminated tail — not consumable by anyone yet
  | "unknown";     // the cursor's position is unknown

/** One envelope's standing for one cursor. The word "delivered" comes from a
 * record and from nothing else (d14 pin iii). */
export function lineStatus(s: MailState, c: MailCursorState, line: MailLedgerLine): MailLineStatus {
  if (!line.terminated) return "torn";
  if (line.envelope === null) return "unreadable";
  // The recipient predicate, site 4 of 4 — and the one whose failure is
  // loudest: a sibling's unicast drawn as pending on this track is the false
  // picture d4's verify note names.
  if (!accepts(c, line.envelope.to)) return "foreign";
  if (c.offset === null || c.deliveries === null) return "unknown";
  const h = c.held.find((x) => x.offset === line.offset);
  if (h !== undefined) return isExpired(h, c.deliveries) ? "expired" : "held";
  if (line.offset >= c.offset) return "fresh";
  return deliveriesFor(s, c, line.offset).length > 0 ? "delivered" : "passed";
}

/** The delivery records that name this offset for this cursor. */
export function deliveriesFor(s: MailState, c: MailCursorState, offset: number): MailDeliveryRecord[] {
  return s.deliveries.filter((r) => r.role === c.role && sameSession(r.session, c.session) && r.offsets.includes(offset));
}

export type MailPresenceTier = "active" | "idle" | "stale" | "unknown";
export const PRESENCE_ACTIVE_MS = 60_000;
export const PRESENCE_IDLE_MS = 10 * 60_000;

/** Presence DECAYS; it is never claimed. `unknown` = a session that holds a
 * cursor but has not been seen dispatching (quiet, or the daemon restarted). */
export function presenceTier(p: MailPresenceEntry, nowMs: number): MailPresenceTier {
  if (p.lastSeenAtMs === null) return "unknown";
  const age = nowMs - p.lastSeenAtMs;
  return age < PRESENCE_ACTIVE_MS ? "active" : age < PRESENCE_IDLE_MS ? "idle" : "stale";
}

/** Every `to` role the picture knows — from the ledger and from cursors, so a
 * lane exists for a role that has mail but no reader yet, and for a reader
 * whose mail is all behind it. Sorted for stable lanes. */
export function rolesOf(s: MailState): string[] {
  const set = new Set<string>();
  // A LANE IS A ROLE, and a unicast envelope's lane is its role half (ADR-0018
  // d1/d4). Adding `to` whole would give `main@laptop-a` a top-level lane of
  // its own, beside `main`, as if the two were unrelated addresses — which is
  // precisely the picture this slice exists to stop drawing: an instance is a
  // sub-lane of its role, because a mailbox named there reads the role's
  // broadcast too.
  for (const l of s.lines) if (l.envelope !== null) set.add(roleOf(l.envelope.to));
  for (const c of s.cursors) set.add(c.role);
  return [...set].sort();
}

/** The mailboxes a `mail.nudge` has named as DEAD, newest row per address
 * (ADR-0018 d6). The address rides only a dead-mailbox nudge — an ordinary
 * nudge is about a role and names none — so its presence IS the discriminator,
 * and the picture never parses the reason's prose to learn what happened.
 *
 * This is READ off the nudge history rather than re-derived from cursors and
 * presence: "is this mailbox dead" is the watcher's rule (an instance mailbox,
 * unregistered, no live session, quiet past a threshold), and a second
 * implementation of it in the browser would be N8 exactly — two answers to one
 * question, and the browser's would be the one nobody notices drifting. What
 * the canvas shows is that the WATCHER said so, which is a fact with a row
 * behind it. */
export function deadNudgeFor(s: MailState, address: string): MailNudgeRecord | null {
  let last: MailNudgeRecord | null = null;
  for (const n of s.nudges) if (n.address === address) last = n;
  return last;
}

/** Every forward that moved mail OUT of this mailbox, oldest first. */
export function forwardsFrom(s: MailState, address: string): MailForwardRecord[] {
  return s.forwards.filter((f) => f.source === address);
}

export function findCursor(s: MailState, role: string, session: MailSession): MailCursorState | null {
  return s.cursors.find((c) => c.role === role && sameSession(c.session, session)) ?? null;
}
