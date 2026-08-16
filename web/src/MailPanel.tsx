import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useStore, type MailStreamState } from "./store.ts";
import { deliveriesFor, label as cursorLabel, opportunities } from "./mail.ts";
import {
  buildScene, canvasHeight, cardLines, clampView, cursorKey, fitView, mapX, sceneSummary,
  sessionLabel, slotPixels, tierFor, zoomView,
  GLYPH_H, LANE_HEAD_W, LEDGER_LEFT, SPINE_H, ZOOM_STEP,
} from "./mailCanvas.ts";
import type { MailGlyph, MailLane, MailScene, MailTier, MailTrack, MailView } from "./mailCanvas.ts";
import type { MailState } from "./mail.ts";

// The Mail island (ADR-0016 decision 14, roadmap item 21 slices 4–5) — the bus,
// watched, and since slice 5 watched LIVE. The whole view is one sentence made
// visible: **mail never moves; cursors move past it.** So the ledger is a fixed
// spine, each `to` role is a lane hanging off it, and each session reading that
// role is a cursor sliding along its own track — with the mail it passed over
// marked underneath, ageing.
//
// **Observation is not delivery.** This island reads `GET /api/v1/mail` ONCE and
// then listens; there is no verb here that could append or advance, the daemon
// exposes none under `/mail`, and the read model is handed a port with neither
// (three pins, d14). Watching a mailbox changes nothing on disk — including
// whether the mail counts as read.
//
// What it draws comes from the reducer (`mail.ts`), which is an INTERPOLATOR
// between authoritative snapshots and never a second store (N8). The snapshot
// and the live `mail.*` fold both arrive through `mailStream.ts`, which opens
// the stream at the exact trail position its snapshot was taken at — so the two
// meet with nothing lost and nothing replayed — and re-seeds whenever the
// reducer says the picture can no longer be trusted.
//
// FOUR MOTIONS, and each says something the still picture cannot: an envelope
// ARRIVES (dropping onto its lane from the spine, the direction mail actually
// travels here), a cursor SLIDES (reading forward — the only direction it ever
// reads), a cursor JUMPS (a re-anchor: nothing was read, it started over, and
// drawing that as a slide would depict a cursor reading backwards), and an
// envelope SPENDS its TTL. All of it is CSS keyed on state the reducer already
// computes — `data-arrival`, `data-motion`, `data-status` — so the animation is
// how you notice and never how you know, and `prefers-reduced-motion` removes
// every one of them without removing a single fact.
//
// **Delivered is now real.** It comes from a `mail.deliver` ledger line and
// from nowhere else; the snapshot cannot carry one (delivered mail is
// structurally ABSENT from a cursor — that is what makes the cursor small), so
// through slice 4 an envelope behind a cursor could only read *before cursor ·
// no record*. The live fold is what supplies the record, and an envelope with
// no record behind a cursor still reads exactly that, honestly.
//
// EVERY COORDINATE BELOW IS A CSS PIXEL. The svg's viewBox is 1:1 with its box,
// and the only thing pan/zoom touches is the ledger's x — see `MailView`. That
// is why no font size, stroke or label offset is scaled anywhere in this file.

/** The bus's own liveness, deliberately its own badge rather than the trace's:
 * these are two subscriptions and they can disagree, and the one that matters
 * for THIS picture is the one feeding it. `resyncing` is the state a log has no
 * equivalent of — the reducer distrusted the picture and a fresh snapshot is on
 * the way — and `snapshot` is the honest end state when no trail is served at
 * all: what you see is real and is not moving. Every state carries a word as
 * well as a colour. */
function MailStreamBadge({ state }: { state: MailStreamState }) {
  const label = state === "live" ? "live"
    : state === "retrying" ? "reconnecting…"
    : state === "resyncing" ? "re-reading the bus…"
    : state === "snapshotOnly" ? "snapshot (no trail served)"
    : state === "dead" ? "disconnected"
    : "idle";
  return (
    <span className={`stream-badge stream-${state}`} data-mail-stream={state}>
      ● {label}
    </span>
  );
}

export function MailPanel() {
  const view = useStore((s) => s.view);
  const session = useStore((s) => s.session);
  const mail = useStore((s) => s.mail);
  const ui = useStore((s) => s.mailUi);
  const stream = useStore((s) => s.mailStream);
  const setMailView = useStore((s) => s.setMailView);
  const setMailSelected = useStore((s) => s.setMailSelected);
  // No poll: the snapshot and the live fold both come from the mail stream
  // (mailStream.ts), which is started on this view's first visit from main.tsx
  // and re-seeds itself whenever the reducer says the picture is untrustworthy.

  const [el, setEl] = useState<SVGSVGElement | null>(null);
  const [width, setWidth] = useState(900);
  const scene = useMemo(() => buildScene(mail), [mail]);
  const height = canvasHeight(scene);
  const areaW = Math.max(width - LEDGER_LEFT - 8, 80);          // the ledger's own room

  const canvas: MailView = ui.canvas ?? fitView(scene, areaW);
  const slotPx = slotPixels(canvas);
  const tier: MailTier = tierFor(slotPx);

  const setCanvas = useCallback((next: MailView) => {
    setMailView(clampView(next, scene, areaW));
  }, [setMailView, scene, areaW]);

  // The BUTTONS anchor on the ledger's leftmost visible position, not the
  // middle of the view: this is a left-to-right ledger, so "zoom in" should
  // magnify from where you are reading rather than drift the start of the
  // window sideways with every click — several clicks of a centre anchor walk
  // the view off into whatever happens to be mid-ledger. The WHEEL still
  // anchors under the pointer, which is the gesture's own contract.
  const zoomBy = (factor: number) => setCanvas(zoomView(canvas, factor, LEDGER_LEFT));

  useEffect(() => {
    if (el === null) return;
    const measure = () => {
      const r = el.getBoundingClientRect();
      if (r.width > 0) setWidth(r.width);
    };
    measure();
    const ro = new ResizeObserver(measure);
    ro.observe(el);
    return () => ro.disconnect();
  }, [el]);

  // Live refs for the imperative wheel handler below.
  const state = useRef({ canvas, scene, areaW });
  state.current = { canvas, scene, areaW };

  // Wheel zoom is attached by hand, non-passive: React routes `wheel` through a
  // PASSIVE root listener, where preventDefault does nothing — so a delegated
  // handler would zoom the canvas AND scroll the page behind it.
  useEffect(() => {
    if (el === null) return;
    const onWheel = (e: WheelEvent) => {
      e.preventDefault();
      const { canvas: c, scene: sc, areaW: aw } = state.current;
      const x = e.clientX - el.getBoundingClientRect().left;
      const factor = e.deltaY < 0 ? ZOOM_STEP : 1 / ZOOM_STEP;
      setMailView(clampView(zoomView(c, factor, x), sc, aw));
    };
    el.addEventListener("wheel", onWheel, { passive: false });
    return () => el.removeEventListener("wheel", onWheel);
  }, [el, setMailView]);

  // Drag to pan along the ledger. Capture is taken only once the pointer has
  // actually TRAVELLED (3px): a capture taken on pointerdown retargets the
  // click that follows to the canvas, which silently eats every click on an
  // envelope — a drag-to-pan that costs you selection entirely. Past the
  // threshold the capture is what keeps a drag tracking (and ending) when it
  // leaves the canvas, and the retargeted click is then exactly right: a pan
  // should not also select whatever it started on.
  const drag = useRef<{ id: number; x: number; from: number; panning: boolean } | null>(null);
  const onPointerDown = (e: React.PointerEvent<SVGSVGElement>) => {
    if (e.button !== 0) return;
    drag.current = { id: e.pointerId, x: e.clientX, from: canvas.x, panning: false };
  };
  const onPointerMove = (e: React.PointerEvent<SVGSVGElement>) => {
    const d = drag.current;
    if (d === null || d.id !== e.pointerId) return;
    const dx = e.clientX - d.x;
    if (!d.panning) {
      if (Math.abs(dx) < 3) return;
      d.panning = true;
      e.currentTarget.setPointerCapture(e.pointerId);
    }
    setCanvas({ ...canvas, x: d.from - dx / canvas.z });
  };
  const endDrag = (e: React.PointerEvent<SVGSVGElement>) => {
    if (drag.current?.id === e.pointerId) drag.current = null;
  };

  // Keyboard: the ledger axis only. Up/Down are deliberately NOT captured —
  // there is no vertical pan to drive, and swallowing them would break the
  // page scroll a tall bus needs.
  const onKeyDown = (e: React.KeyboardEvent<SVGSVGElement>) => {
    const step = areaW / canvas.z / 4;
    if (e.key === "ArrowLeft") { setCanvas({ ...canvas, x: canvas.x - step }); e.preventDefault(); }
    else if (e.key === "ArrowRight") { setCanvas({ ...canvas, x: canvas.x + step }); e.preventDefault(); }
    else if (e.key === "+" || e.key === "=") { zoomBy(ZOOM_STEP); e.preventDefault(); }
    else if (e.key === "-") { zoomBy(1 / ZOOM_STEP); e.preventDefault(); }
    else if (e.key === "0") { setMailView(null); e.preventDefault(); }
  };

  // The view gate (ADR-0015 d1), after every hook.
  if (view !== "mail" || session !== "live") return null;

  const selected = ui.selected === null ? null : mail.lines.find((l) => l.offset === ui.selected) ?? null;

  return (
    <section className="card mail" data-island="mail">
      <div className="mail-head">
        <h2>Mail</h2>
        <span className="mail-readonly" title="ADR-0016 d14: nothing under /api/v1/mail writes, ever.">
          read-only
        </span>
        <MailStreamBadge state={stream} />
        <div className="mail-zoom" role="group" aria-label="zoom">
          <button type="button" data-zoom="out" aria-label="zoom out" onClick={() => zoomBy(1 / ZOOM_STEP)}>−</button>
          <button type="button" data-zoom="fit" onClick={() => setMailView(null)}>fit</button>
          <button type="button" data-zoom="in" aria-label="zoom in" onClick={() => zoomBy(ZOOM_STEP)}>+</button>
          <span className="mail-tier" data-tier={tier}>{tier}</span>
        </div>
      </div>

      <p className="muted panel-lede">
        The ledger is the spine: mail never moves, cursors move past it. Each role is a
        lane, each session reading it a cursor — with what it passed over marked
        underneath, ageing by delivery opportunity. Watching changes nothing: this view
        can read the bus and has no way to write it.
      </p>

      <ChainStrip state={mail} />
      {mail.resnapshot !== null && (
        <p className="mail-resnapshot" data-mail-resnapshot={mail.resnapshot.reason}>
          <strong>{mail.resnapshot.reason}</strong> — {mail.resnapshot.detail}. Re-reading the
          bus from the daemon and re-anchoring the stream to the new snapshot.
        </p>
      )}

      {!mail.seeded ? (
        <p className="muted" data-mail-empty="loading">Reading the bus…</p>
      ) : scene.empty ? (
        <p className="muted" data-mail-empty="none">
          No mail on the bus yet. Anything that can run a process can put some there —{" "}
          <code>captainHook mail send</code> with one envelope on stdin — and a role is read
          by registering <code>mail digest --role &lt;role&gt;</code> as a handler.
        </p>
      ) : (
        <div className="mail-canvas-wrap">
          <svg
            ref={setEl}
            className="mail-canvas"
            data-mail-canvas
            data-tier={tier}
            viewBox={`0 0 ${width} ${height}`}
            width="100%"
            height={height}
            role="img"
            aria-label={sceneSummary(scene, mail)}
            tabIndex={0}
            onPointerDown={onPointerDown}
            onPointerMove={onPointerMove}
            onPointerUp={endDrag}
            onPointerCancel={endDrag}
            onKeyDown={onKeyDown}
          >
            <defs>
              {/* Everything on the ledger axis is clipped to the ledger's own
                  area, so panning slides mail cleanly under the role gutter
                  instead of printing it over the labels. */}
              <clipPath id="mailLedgerClip">
                <rect x={LEDGER_LEFT} y={0} width={Math.max(width - LEDGER_LEFT, 1)} height={height} />
              </clipPath>
            </defs>

            <g className="mail-lanes">
              {scene.lanes.map((lane) => (
                <Lane
                  key={lane.role} lane={lane} canvas={canvas} tier={tier}
                  width={width} selected={ui.selected} onSelect={setMailSelected}
                />
              ))}
            </g>
            <Spine scene={scene} state={mail} canvas={canvas} tier={tier} width={width} />
            <LaneHeads scene={scene} tier={tier} />
          </svg>
          <p className="mail-hint muted">
            drag to pan · wheel to zoom · <code>←</code>/<code>→</code>, <code>+</code>/
            <code>−</code>/<code>0</code> when the canvas has focus · click an envelope for
            its record
          </p>
        </div>
      )}

      <Legend />
      <Detail state={mail} line={selected} onClose={() => setMailSelected(null)} />
      <Notes state={mail} />
    </section>
  );
}

// ---- the header strip -------------------------------------------------------

/** The store's own facts, stated rather than inferred: the chain's integrity,
 * the modes d13 promises, and where the ledger ends. A fault here is not a
 * rendering problem — it is the bus telling you its history was rewritten. */
function ChainStrip({ state }: { state: MailState }) {
  const c = state.chain;
  return (
    <p className="identity-line mail-chain" data-mail-chain={c.ok ? "ok" : "faulted"}>
      <span><span className="muted">store </span><code>{state.dir ?? "—"}</code></span>
      <span>
        <span className="muted">chain </span>
        {c.ok
          ? <span className="ok" data-chain-ok>✓ intact</span>
          : <span className="bad" data-chain-faults={c.faults}>✗ {c.faults} fault(s)</span>}
      </span>
      <span><span className="muted">head </span><code>{c.head === null ? "—" : c.head.slice(0, 12)}</code></span>
      <span><span className="muted">gen </span><code>{c.gen}</code></span>
      <span><span className="muted">frontier </span><code data-mail-frontier>{state.frontier}</code> B</span>
      <span><span className="muted">modes </span><code>{c.dirMode ?? "?"}</code>/<code>{c.fileMode ?? "?"}</code></span>
      {state.since > 0 && <span><span className="muted">since </span><code>{state.since}</code></span>}
    </p>
  );
}

// ---- the spine --------------------------------------------------------------

/** The ledger itself: one segment per line, in append order, at the top of the
 * canvas. It is the only thing every lane's geometry refers to. */
function Spine(
  { scene, state, canvas, tier, width }: {
    scene: MailScene; state: MailState; canvas: MailView; tier: MailTier; width: number;
  },
) {
  const y = scene.spineY;
  const fx = mapX(canvas, scene.frontierX);
  return (
    <g className="mail-spine" data-mail-spine clipPath="url(#mailLedgerClip)">
      <rect className="spine-rail" x={LEDGER_LEFT} y={y + SPINE_H / 2 - 1.5} width={Math.max(width - LEDGER_LEFT - 8, 1)} height={3} />
      {scene.slots.map((s) => {
        const x = mapX(canvas, s.x), w = s.w * canvas.z;
        if (x + w < LEDGER_LEFT || x > width) return null;      // off-screen: not drawn at all
        return (
          <g key={s.index}>
            {s.kind === "unknown" ? (
              <>
                <rect className="spine-unknown" x={x + 4} y={y + 2} width={Math.max(w - 8, 2)} height={SPINE_H - 4} rx={3} />
                {tier !== "far" && (
                  <text className="spine-label" x={x + w / 2} y={y + SPINE_H / 2 + 4} textAnchor="middle" style={{ fontSize: 10 }}>
                    never seen
                  </text>
                )}
              </>
            ) : (
              <>
                <rect
                  className={`spine-slot slot-${s.line?.envelope === null ? "unreadable" : s.line?.terminated === false ? "torn" : "line"}`}
                  x={x + 5} y={y + 4} width={Math.max(w - 10, 2)} height={SPINE_H - 8} rx={2} />
                {tier !== "far" && (
                  <text className="spine-label" x={x + w / 2} y={y - 3} textAnchor="middle" style={{ fontSize: 10 }}>
                    {s.offset}
                  </text>
                )}
              </>
            )}
          </g>
        );
      })}
      <g className="spine-frontier" data-spine-frontier>
        <line x1={fx} y1={y - 4} x2={fx} y2={y + SPINE_H + 4} />
        <text x={fx + 6} y={y + SPINE_H + 12} style={{ fontSize: 10 }}>frontier {state.frontier}</text>
      </g>
    </g>
  );
}

// ---- lane headers -----------------------------------------------------------

/** The role gutter: a fixed column, never panned, never scaled — the ledger
 * slides under it. Labels here are chrome, so they are measured in pixels like
 * every other piece of chrome on the page. */
function LaneHeads({ scene, tier }: { scene: MailScene; tier: MailTier }) {
  const far = tier === "far";
  return (
    <g className="mail-lane-heads">
      {scene.lanes.map((lane) => (
        <g key={lane.role} data-lane-head={lane.role}>
          <rect className="lane-head-bg" x={0} y={lane.y - 4} width={LANE_HEAD_W} height={lane.h + 8} rx={6} />
          <text className="lane-role" data-lane-label={lane.role} x={12} y={lane.y + 13} style={{ fontSize: 13 }}>
            {lane.role}
          </text>
          {/* At the far tier a lane is a few dozen pixels tall: one line of
              counts, which is what far is for (roles, weight, pulse). */}
          <text className="lane-counts" x={12} y={lane.y + 28} style={{ fontSize: 10 }}>
            {lane.counts.envelopes} mail
            {lane.counts.pending > 0 ? ` · ${lane.counts.pending} pending` : ""}
            {far && !lane.hasReader ? " · no reader" : ""}
          </text>
          {!far && (
            <>
              <text className="lane-noreader" x={12} y={lane.y + 41} style={{ fontSize: 10 }}>
                {!lane.hasReader ? "no reader" : lane.counts.expired > 0 ? `${lane.counts.expired} spent` : ""}
              </text>
              {lane.tracks.map((t) => (
                <text key={t.key} className={`lane-session presence-${t.presence}`} x={12} y={t.y + 4}
                  style={{ fontSize: 10 }} data-track-label={t.key}>
                  {t.session === null ? "sessionless" : shortSession(t.session)}
                  {t.deliveries !== null ? ` · d${t.deliveries}` : ""}
                </text>
              ))}
            </>
          )}
        </g>
      ))}
    </g>
  );
}

function shortSession(s: string): string {
  return s.length <= 17 ? s : `${s.slice(0, 16)}…`;
}

// ---- a lane -----------------------------------------------------------------

function Lane(
  { lane, canvas, tier, width, selected, onSelect }: {
    lane: MailLane; canvas: MailView; tier: MailTier; width: number;
    selected: number | null; onSelect: (offset: number | null) => void;
  },
) {
  return (
    <g className="mail-lane" data-lane={lane.role}>
      <rect className="lane-bg" x={LEDGER_LEFT - 6} y={lane.y - 4}
        width={Math.max(width - LEDGER_LEFT - 2, 1)} height={lane.h + 8} rx={6} />
      <g clipPath="url(#mailLedgerClip)">
        {lane.glyphs.map((g) => (
          <Glyph key={g.offset} g={g} lane={lane} canvas={canvas} tier={tier} width={width}
            selected={selected === g.offset} onSelect={onSelect} />
        ))}
        {lane.tracks.map((t) => (
          <Track key={t.key} track={t} lane={lane} canvas={canvas} tier={tier} width={width} />
        ))}
      </g>
    </g>
  );
}

/** One envelope, at its ledger position, in its recipient's lane. Colour is
 * never the only channel: every glyph carries a status GLYPH and a spoken
 * <title>, and the near tier says the whole thing in words. */
function Glyph(
  { g, lane, canvas, tier, width, selected, onSelect }: {
    g: MailGlyph; lane: MailLane; canvas: MailView; tier: MailTier; width: number;
    selected: boolean; onSelect: (offset: number | null) => void;
  },
) {
  const x = mapX(canvas, g.x), w = Math.max(g.w * canvas.z, 3);
  if (x + w < LEDGER_LEFT || x > width) return null;
  const h = tier === "far" ? 12 : tier === "mid" ? 26 : GLYPH_H - 10;
  const y = lane.glyphY + (GLYPH_H - 10 - h) / 2;
  const pick = () => onSelect(selected ? null : g.offset);
  return (
    <g
      className={`mail-glyph glyph-${g.status} prio-${g.priority}${selected ? " is-selected" : ""}`}
      data-glyph={g.offset}
      data-status={g.status}
      data-priority={g.priority}
      // The arrival animation runs on INSERTION — a CSS animation on an element
      // that has just entered the DOM, which is what an envelope landing on the
      // bus actually is. Nothing schedules it and nothing has to decide when it
      // has been "recent enough" to stop; a re-render cannot restart it, and a
      // re-seed (every line `snapshot` again) correctly replays nothing.
      data-arrival={g.arrival}
      role="button"
      tabIndex={0}
      onClick={pick}
      onKeyDown={(e) => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); pick(); } }}
    >
      <title>{`${g.id} · ${g.priority} ${g.kind} · ${g.topic} · ${statusWords(g.status)}`}</title>
      {/* the drop line to the spine: this envelope's place on the ledger */}
      <line className="glyph-drop" x1={x + w / 2} y1={lane.y - 4} x2={x + w / 2} y2={y} />
      <rect className="glyph-body" x={x} y={y} width={w} height={h} rx={4} />
      <rect className="glyph-prio" x={x} y={y} width={4} height={h} />
      {tier === "mid" && w > 44 && (
        <text className="glyph-topic" x={x + 9} y={y + h / 2 + 4} style={{ fontSize: 11 }}>
          {truncate(g.topic, (w - 20) / 6.6)}
        </text>
      )}
      {tier === "near" && (
        <g data-envelope-card={g.offset}>
          {/* Budgeted against the card's OWN width, not the slot's: the glyph
              is the slot minus its padding, and a line measured against the
              wider number is a line that touches the border. */}
          {cardLines(g, (w - 20) / 6.7).map((line, i) => (
            <text key={i} className={`card-line card-line-${i}`} x={x + 9} y={y + 15 + i * 13}
              style={{ fontSize: i === 1 ? 12 : 10.5 }}>
              {line}
            </text>
          ))}
        </g>
      )}
      {tier !== "far" && w > 26 && (
        <text className="glyph-mark" x={x + w - 6} y={y + 12} textAnchor="end" style={{ fontSize: 10 }} aria-hidden="true">
          {STATUS_GLYPH[g.status]}
        </text>
      )}
    </g>
  );
}

const STATUS_GLYPH: Record<string, string> = {
  fresh: "●", held: "◐", expired: "○", delivered: "✓", passed: "·",
  "no-reader": "–", unreadable: "✗", torn: "⋯", unknown: "?", foreign: "",
};

function statusWords(status: string): string {
  switch (status) {
    case "fresh": return "pending, not yet passed over";
    case "held": return "held: passed over, still within its TTL";
    case "expired": return "spent: passed over its TTL times, dropped at the next advance";
    case "delivered": return "delivered — a mail.deliver record names it";
    case "passed": return "before the cursor · no delivery record in this picture";
    case "no-reader": return "no cursor reads this role yet";
    case "unreadable": return "a malformed line: stepped over and counted";
    case "torn": return "an unterminated tail: not consumable by anyone yet";
    default: return "the cursor's position is unknown";
  }
}

/** One session reading one role. The cursor is a position, the marks under it
 * are what it passed over and is still holding — each with the arithmetic that
 * decides when it is spent (`opportunities of ttl`, never a wall clock). */
function Track(
  { track, lane, canvas, tier, width }: {
    track: MailTrack; lane: MailLane; canvas: MailView; tier: MailTier; width: number;
  },
) {
  const y = track.y;
  const cx = track.x === null ? null : mapX(canvas, track.x);
  return (
    <g className={`mail-track presence-${track.presence}${track.uncertain !== null ? " is-uncertain" : ""}`}
      data-track={track.key} data-presence={track.presence}
      data-motion={track.motion ?? undefined}
      data-uncertain={track.uncertain !== null ? "yes" : undefined}>
      <title>
        {`${cursorLabel(track.role, track.session)} — ${track.offset === null ? "position unknown"
          : `at byte ${track.offset}${track.atFrontier ? " (the frontier: nothing pending ahead)" : ""}`}`
          + `; ${track.pendingCount} pending, ${track.expiredCount} spent`
          + (track.uncertain !== null ? `; uncertain: ${track.uncertain}` : "")}
      </title>
      <line className="track-rail" x1={LEDGER_LEFT} y1={y} x2={width - 8} y2={y} />
      {cx !== null && <line className="track-read" x1={LEDGER_LEFT} y1={y} x2={cx} y2={y} />}
      {track.marks.map((m) => {
        const x = mapX(canvas, m.x), w = Math.max(m.w * canvas.z, 3);
        if (x + w < LEDGER_LEFT || x > width) return null;
        return (
          <g key={m.offset} className={`track-mark mark-${m.status}`} data-mark={m.status} data-mark-offset={m.offset}>
            <rect x={x} y={y - 5} width={w} height={10} rx={3} />
            {tier !== "far" && w > 30 && (
              <text x={x + w / 2} y={y + 3.5} textAnchor="middle" style={{ fontSize: 9 }}>
                {m.status === "expired" ? "spent" : `${m.opportunities}/${m.ttlDeliveries}`}
              </text>
            )}
          </g>
        );
      })}
      {cx === null ? (
        tier !== "far" && (
          <text className="track-unknown" x={LEDGER_LEFT + 6} y={y - 8} style={{ fontSize: 10 }}>
            position unknown
          </text>
        )
      ) : (
        // A CSS transform rather than the `transform` ATTRIBUTE, because only
        // the former transitions: the slide is the cursor reading forward past
        // mail, and it is the one motion on this canvas that carries meaning
        // rather than decoration. A re-anchor suppresses it in styles.css —
        // that cursor did not read backwards, it started over.
        <g className="track-cursor" data-cursor={track.key}
          style={{ transform: `translate(${cx}px, ${y}px)` }}>
          {/* the stem reaches up into the envelope row: a cursor's position is
              a statement ABOUT the ledger above it, not a mark on its own rail */}
          <line className="cursor-stem" x1={0} y1={lane.glyphY - y} x2={0} y2={0} />
          <path d="M 0 -9 L 9 0 L 0 9 Z" />
        </g>
      )}
      {track.reanchored && tier !== "far" && (
        <text className="track-flag" x={LEDGER_LEFT + 6} y={y + 12} style={{ fontSize: 9 }}>re-anchored</text>
      )}
    </g>
  );
}

function truncate(s: string, maxChars: number): string {
  const n = Math.max(3, Math.floor(maxChars));
  return s.length <= n ? s : `${s.slice(0, n - 1)}…`;
}

// ---- legend, detail, anomalies ----------------------------------------------

const LEGEND: { status: string; label: string }[] = [
  { status: "fresh", label: "pending" },
  { status: "held", label: "held (n of ttl)" },
  { status: "expired", label: "spent" },
  { status: "delivered", label: "delivered (from a record)" },
  { status: "passed", label: "before cursor · no record" },
  { status: "no-reader", label: "no reader" },
  { status: "unreadable", label: "malformed line" },
];

function Legend() {
  return (
    <ul className="mail-legend" data-mail-legend>
      {LEGEND.map((l) => (
        <li key={l.status} data-legend={l.status}>
          <span className={`legend-swatch glyph-${l.status}`} aria-hidden="true">{STATUS_GLYPH[l.status]}</span>
          {l.label}
        </li>
      ))}
    </ul>
  );
}

/** The near tier in words, and the only place a BODY is shown. Bodies live in
 * the store and reach the browser through the snapshot alone — the trail never
 * carries one (d14), which is why the live fold can animate an arrival and
 * still not know what it said. */
function Detail(
  { state, line, onClose }: {
    state: MailState;
    line: MailState["lines"][number] | null;
    onClose: () => void;
  },
) {
  if (line === null) return null;
  const e = line.envelope;
  const readers = state.cursors.filter((c) => e !== null && c.role === e.to);
  return (
    <div className="mail-detail" data-mail-detail={line.offset}>
      <div className="mail-detail-head">
        <h3>Envelope at byte {line.offset}</h3>
        <button type="button" onClick={onClose} aria-label="close envelope detail">close</button>
      </div>
      {e === null ? (
        <p className="warn" data-detail-unreadable>
          A malformed line ({line.bytes} B): every cursor steps over it and counts it.
          {line.errors.length > 0 && <> — {line.errors.join("; ")}</>}
        </p>
      ) : (
        <>
          <dl className="mail-detail-grid">
            <div><dt>id</dt><dd><code>{e.id}</code></dd></div>
            <div><dt>to</dt><dd><code>{e.to}</code></dd></div>
            <div><dt>from</dt><dd>
              <code>{e.from.agent}</code> via <code>{e.from.harness}</code>
              {e.from.session !== null ? <> · <code>{e.from.session}</code></> : <> · <span className="muted">write-only member</span></>}
            </dd></div>
            <div><dt>kind</dt><dd>{e.kind} · {e.priority}</dd></div>
            <div><dt>ttl</dt><dd>{e.ttlDeliveries} delivery opportunit{e.ttlDeliveries === 1 ? "y" : "ies"}</dd></div>
            <div><dt>sent</dt><dd><code>{e.ts ?? "—"}</code></dd></div>
            <div><dt>chain</dt><dd>
              <code>{line.hash === null ? "—" : line.hash.slice(0, 12)}</code>
              {e.prev !== null && <> after <code>{e.prev.slice(0, 12)}</code></>}
            </dd></div>
            <div><dt>bytes</dt><dd>{line.bytes}{line.terminated ? "" : " · unterminated"}</dd></div>
          </dl>
          <p className="mail-topic"><strong>{e.topic}</strong></p>
          {e.body !== null && <p className="mail-body" data-detail-body>{e.body}</p>}
        </>
      )}

      <h3>Standing</h3>
      {readers.length === 0 ? (
        <p className="muted" data-detail-noreader>No cursor reads this role — the store keeps it regardless.</p>
      ) : (
        <ul className="mail-standing">
          {readers.map((c) => {
            const held = c.held.find((h) => h.offset === line.offset);
            const records = deliveriesFor(state, c, line.offset);
            return (
              <li key={cursorKey(c)} data-standing={cursorKey(c)}>
                <code>{sessionLabel(c.session)}</code>{" "}
                {held !== undefined && c.deliveries !== null
                  ? <>holds it — passed over at {opportunities(c.deliveries, held.seenAt)} of {held.ttlDeliveries} opportunities</>
                  : c.offset === null ? <>position unknown</>
                  : line.offset >= c.offset ? <>has not reached it yet</>
                  : records.length > 0
                    ? <>delivered at the <code>{records[0].seam}</code> seam by <code>{records[0].vehicle}</code></>
                    : <>is past it · <span className="muted">no delivery record in this picture</span></>}
              </li>
            );
          })}
        </ul>
      )}
    </div>
  );
}

/** What the reducer could not reconcile. These are the picture admitting its
 * own limits — a hole in the ledger, an advance whose arithmetic disagreed —
 * and they belong on screen for the same reason the daemon writes them down. */
function Notes({ state }: { state: MailState }) {
  const notes = state.notes.slice(-4).reverse();
  if (notes.length === 0) return null;
  return (
    <div className="mail-notes" data-mail-notes={state.notes.length}>
      <h3>Anomalies</h3>
      <ul>
        {notes.map((n, i) => (
          <li key={i} className={n.severity === "warn" ? "warn" : "muted"} data-note={n.kind}>
            <code>{n.kind}</code> {n.message}
          </li>
        ))}
      </ul>
      {state.notesDropped > 0 && <p className="muted">{state.notesDropped} older note(s) dropped.</p>}
    </div>
  );
}
