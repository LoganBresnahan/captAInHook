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
  session: MailSession;
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
  session: MailSession;
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

function byOffset<T extends { offset: number }>(a: T, b: T): number { return a.offset - b.offset; }

function isRecord(v: unknown): v is Record<string, unknown> {
  return typeof v === "object" && v !== null && !Array.isArray(v);
}
function str(v: unknown): string | null { return typeof v === "string" ? v : null; }
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
      inReplyTo: e.inReplyTo, ttlDeliveries: e.ttlDeliveries, body: e.body, prev: e.prev,
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
    const c = cursors.find((x) => x.role === d.role && sameSession(x.session, d.session))
      ?? blankCursor(d.role, d.session);
    const { offsets } = resolveDelivery(lines, c, d.role, d.envelopeIds, []);
    const record: MailDeliveryRecord = {
      role: d.role, session: d.session, dispatchId: d.dispatchId, hookEvent: d.hookEvent,
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
    case "mail.lockBusy":
      return note(s, { kind: "lock-busy", severity: "info", role: null, session: null, offset: null,
        message: "the store's append lock was busy — a writer waited or gave up", atMs });
    default:
      return note(s, { kind: "unknown-event", severity: "info", role: null, session: null, offset: null,
        message: `unknown mail event '${evt}' — ignored`, atMs });
  }
}

// ---- mail.append -------------------------------------------------------------------

function onAppend(s: MailState, d: Record<string, unknown>, atMs: number): MailState {
  const id = str(d.id), to = str(d.to), offset = nonNeg(d.offset), bytes = nonNeg(d.bytes);
  const kind = str(d.kind), topic = str(d.topic), priority = str(d.priority), ttl = num(d.ttlDeliveries);
  const from = isRecord(d.from) ? d.from : null;
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
      || (from !== null && from.session !== undefined && fromSession === null)) {
    return refuse(s, "mail.append", "id/to/offset/bytes/from/kind/topic/priority/ttlDeliveries", atMs, null, null, offset);
  }
  const line: MailLedgerLine = {
    offset, bytes, terminated: true, hash: null,
    envelope: { id, ts: null, from: { agent, harness, session: fromSession }, to, kind, topic, priority,
      inReplyTo: null, ttlDeliveries: ttlApplies ? ttl : null, body: null, prev: null },
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
    return { ...s, lines, cursors, torn: null, pendingTorn: null, frontier: offset + bytes + 1 };
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
  return { ...next, lines, cursors, frontier: offset + bytes + 1, torn: null };
}

/** A complete line became visible at or beyond a cursor's offset: fresh if it
 * is this role's parseable mail, a skipped count if unreadable, invisible if
 * another role's — MailCursors.Pending's walk, one line at a time. */
function absorbCompleteLine(c: MailCursorState, line: MailLedgerLine): MailCursorState {
  if (c.offset === null || line.offset < c.offset) return c;
  if (line.envelope === null) return { ...c, skippedMalformed: c.skippedMalformed + 1 };
  if (line.envelope.to !== c.role) return c;
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
  const ev = { role, session, offset, deliveries, deliveredOffsets, heldCount, expiredCount, atMs };
  const idx = s.cursors.findIndex((c) => c.role === role && sameSession(c.session, session));
  const c = idx >= 0 ? s.cursors[idx] : null;

  let next = touchPresence(s, session, role, atMs);
  let cursor: MailCursorState;

  if (c === null || c.offset === null || c.deliveries === null) {
    // A cursor the picture did not have (or had only through a delivery
    // record). deliveries 1 is FIRST CONTACT — anchored at 0, so its pending
    // set was the whole retained ledger for the role: reconstructible exactly
    // when the ledger is complete. Anything else joined mid-lineage: its held
    // set is unknown until a snapshot says.
    const base: MailCursorState = c ?? blankCursor(role, session);
    if (deliveries === 1) {
      const r = reconstructFromZero(next, base, ev, "first contact");
      cursor = r.cursor;
      next = r.state;
      if (c === null) next = note(next, { kind: "advance-unknown-cursor", severity: "info", role, session, offset,
        message: `first contact: ${label(role, session)} anchored at 0 and advanced to ${offset} (deliveries 1)`, atMs });
    } else {
      cursor = { ...base, offset, deliveries, held: [], fresh: freshFromLedger(next, role, offset),
        skippedMalformed: skippedFromLedger(next, offset), reanchored: false, reanchorReason: null,
        uncertain: `first seen mid-lineage at deliveries ${deliveries} — its held set is unknown until a snapshot`,
        lastAdvance: mark(ev, []), lastEventKind: "advance", lastEventAtMs: atMs };
      next = raise(note(next, { kind: "advance-unknown-cursor", severity: "warn", role, session, offset,
        message: `${label(role, session)} advanced (deliveries ${deliveries}) but the picture never saw it anchor — held set unknown`, atMs }),
        "advance-unknown-cursor", `${label(role, session)} joined at deliveries ${deliveries}`, atMs);
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
      message: `${label(role, session)}'s deliveries fell from ${c.deliveries} back to 1 — its cursor file was deleted and re-anchored silently; everything retained redelivered`, atMs });
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
  const broken: MailCursorState = { ...c, offset, deliveries, held: [], fresh: freshFromLedger(next, role, offset),
    skippedMalformed: skippedFromLedger(next, offset), reanchored: false, reanchorReason: null,
    uncertain: why, lastAdvance: mark(ev, []), lastEventKind: "advance", lastEventAtMs: atMs };
  next = raise(note(next, { kind: "advance-out-of-sequence", severity: "warn", role, session, offset, message: why, atMs }),
    "advance-out-of-sequence", `${label(role, session)}: ${why}`, atMs);
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
    ...c, offset: 0, deliveries: 0, held: [], fresh: freshFromLedger(s, c.role, 0),
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

function freshFromLedger(s: MailState, role: string, from: number): MailFreshItem[] {
  return s.lines
    .filter((l) => l.terminated && l.offset >= from && l.envelope !== null && l.envelope.to === role)
    .map((l) => ({ offset: l.offset, id: l.envelope!.id, priority: l.envelope!.priority, ttlDeliveries: l.envelope!.ttlDeliveries }));
}
function skippedFromLedger(s: MailState, from: number): number {
  return s.lines.filter((l) => l.terminated && l.offset >= from && l.envelope === null).length;
}

function blankCursor(role: string, session: MailSession): MailCursorState {
  return {
    role, session, gen: null, head: null, offset: null, deliveries: null, lastDeliveredId: null,
    held: [], fresh: [], skippedMalformed: 0, reanchored: false, reanchorReason: null,
    origin: "trail", uncertain: null, lastAdvance: null, lastEventKind: null, lastEventAtMs: null,
  };
}

// ---- mail.expire -------------------------------------------------------------------

function onExpire(s: MailState, d: Record<string, unknown>, session: MailSession, atMs: number): MailState {
  const id = str(d.id), role = str(d.to), offset = nonNeg(d.offset), ttl = num(d.ttlDeliveries), seenAt = nonNeg(d.seenAt);
  if (id === null || role === null || offset === null || ttl === null || seenAt === null)
    return refuse(s, "mail.expire", "id/to/offset/ttlDeliveries/seenAt", atMs, role, session, offset);
  const idx = s.cursors.findIndex((c) => c.role === role && sameSession(c.session, session));
  if (idx < 0) return note(s, { kind: "expire-mismatch", severity: "warn", role, session, offset,
    message: `expire for ${label(role, session)}, a cursor the picture does not have`, atMs });
  const c = s.cursors[idx];
  const h = c.held.find((x) => x.offset === offset);
  let next = s;
  let cursor = c;
  if (h === undefined) {
    // Already dropped by an advance the picture applied (a stale replay), or
    // never held here at all — the second is a divergence.
    if (c.offset !== null && offset < c.offset && !c.fresh.some((f) => f.offset === offset)) return s;
    next = note(next, { kind: "expire-mismatch", severity: "warn", role, session, offset,
      message: `the digest expired ${id} at ${offset} but the picture does not hold it for ${label(role, session)}`, atMs });
    cursor = { ...c, uncertain: c.uncertain ?? `expired an envelope (${offset}) the picture did not hold` };
  } else if (c.deliveries !== null && !isExpired(h, c.deliveries)) {
    next = note(next, { kind: "expire-mismatch", severity: "warn", role, session, offset,
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

  let next = touchPresence(s, session, role, atMs);
  const idx = next.cursors.findIndex((c) => c.role === role && sameSession(c.session, session));
  let c = idx >= 0 ? next.cursors[idx] : null;

  if (c === null) {
    // A delivery proves a cursor exists (only an advance that succeeded emits
    // one) — but not WHERE it is. Record the fact, place nothing.
    c = { ...blankCursor(role, session), uncertain: "seen only through a delivery record — position unknown until a snapshot" };
    next = raise(note(next, { kind: "deliver-unknown-cursor", severity: "warn", role, session, offset: null,
      message: `a delivery to ${label(role, session)}, a cursor the picture never saw advance`, atMs }),
      "deliver-unknown-cursor", label(role, session), atMs);
  }

  // Resolve ids → offsets. First against the advance this delivery belongs
  // to (the exact consumed set, matched once each so duplicate ids map to
  // distinct offsets); then, for a record that arrives without its advance
  // (a replay after a re-seed), against consumed lines — accepted only when
  // UNIQUE, since a wrong duplicate is a false picture too. Else null.
  const pool = c.lastAdvance !== null && c.lastAdvance.seam === null ? [...c.lastAdvance.delivered] : [];
  const { offsets, unmatched } = resolveDelivery(next.lines, c, role, ids, pool);
  if (unmatched.length > 0)
    next = note(next, { kind: "deliver-unmatched", severity: "warn", role, session, offset: null,
      message: `a delivery to ${label(role, session)} names ${unmatched.length} envelope(s) the picture cannot place (${unmatched.map((u) => `'${u}'`).join(", ")}) — the trail may be truncated, or the append predates this picture`, atMs });

  const record: MailDeliveryRecord = {
    role, session, dispatchId, hookEvent, seam, vehicle, envelopeIds: [...ids], offsets,
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
  lines: MailLedgerLine[], c: MailCursorState, role: string, ids: string[], pool: { id: string; offset: number }[],
): { offsets: (number | null)[]; unmatched: string[] } {
  const offsets: (number | null)[] = [];
  const unmatched: string[] = [];
  for (const id of ids) {
    const hit = pool.findIndex((p) => clampField(p.id) === id);
    if (hit >= 0) { offsets.push(pool[hit].offset); pool.splice(hit, 1); continue; }
    const behind = c.offset;
    const cands = lines.filter((l) => l.envelope !== null && l.envelope.to === role && clampField(l.envelope.id) === id
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

// ---- the re-anchor family --------------------------------------------------------

function onReanchor(s: MailState, d: Record<string, unknown>, session: MailSession, atMs: number): MailState {
  const role = str(d.role), reason = str(d.reason), deliveries = nonNeg(d.deliveries), cause = str(d.cause);
  if (role === null || reason === null || deliveries === null || (cause !== "cursor" && cause !== "store"))
    return refuse(s, "mail.cursorReanchor", "role/reason/cause/deliveries", atMs, role, session, null);
  let next = touchPresence(s, session, role, atMs);
  const idx = next.cursors.findIndex((c) => c.role === role && sameSession(c.session, session));
  const known = idx >= 0 ? next.cursors[idx] : null;
  // The counter a re-anchor carries is the disk cursor's (or 0 when the file
  // was unreadable). It cannot be BELOW a count the picture already saw and
  // still be current — a re-anchor read later than an advance carries at
  // least that advance's count — so a lower non-zero count is a replay.
  if (known !== null && known.deliveries !== null && deliveries > 0 && deliveries < known.deliveries) return s;
  const base = known ?? blankCursor(role, session);
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
    fresh: storeChanged ? [] : freshFromLedger(next, role, 0),
    skippedMalformed: storeChanged ? 0 : skippedFromLedger(next, 0), lastDeliveredId: null,
    reanchored: true, reanchorReason: reason, uncertain,
    lastAdvance: null, lastEventKind: "reanchor", lastEventAtMs: atMs,
  };
  next = note(next, { kind: "reanchor", severity: "warn", role, session, offset: 0,
    message: `${label(role, session)} re-anchored at 0 (${reason}) — retained mail for the role is pending again; deliveries ${deliveries} carried`, atMs });
  if (storeChanged)
    next = raise(note(next, { kind: "store-changed", severity: "warn", role, session, offset: null,
      message: `the store changed under ${label(role, session)} (${reason}) — every line in this picture is suspect; re-snapshot`, atMs }),
      "store-changed", reason, atMs);
  else if (partial)
    next = raise(note(next, { kind: "reanchor-partial", severity: "warn", role, session, offset: 0,
      message: `${label(role, session)} re-anchored below the picture's since (${next.since}) — re-snapshot to see what is pending`, atMs }),
      "reanchor-partial", label(role, session), atMs);
  return replaceCursor(next, idx, cursor);
}

function onRefuse(s: MailState, d: Record<string, unknown>, session: MailSession, atMs: number): MailState {
  const role = str(d.role) ?? "?", reason = str(d.reason) ?? "(no reason)";
  const next = note(s, { kind: "refuse", severity: "info", role, session, offset: null,
    message: `${label(role, session)}: an advance was refused (${reason}) — the mail stays pending; usually a concurrent delivery winning the race`, atMs });
  return stampCursor(next, role, session, "refuse", atMs);
}

function onVanished(s: MailState, d: Record<string, unknown>, session: MailSession, atMs: number): MailState {
  const role = str(d.role) ?? "?";
  const viewDeliveries = nonNeg(d.viewDeliveries);
  const next = note(s, { kind: "vanished", severity: "warn", role, session, offset: null,
    message: `${label(role, session)}'s cursor file vanished under a view read at deliveries ${viewDeliveries ?? "?"} — the advance proceeds as a fresh lineage; mail it consumed may redeliver`, atMs });
  return stampCursor(next, role, session, "vanished", atMs);
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
  if (line.envelope.to !== c.role) return "foreign";
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
  for (const l of s.lines) if (l.envelope !== null) set.add(l.envelope.to);
  for (const c of s.cursors) set.add(c.role);
  return [...set].sort();
}

export function findCursor(s: MailState, role: string, session: MailSession): MailCursorState | null {
  return s.cursors.find((c) => c.role === role && sameSession(c.session, session)) ?? null;
}
