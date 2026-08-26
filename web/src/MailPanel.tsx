import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useStore, type MailStreamState } from "./store.ts";
import {
  accepts, deliveriesFor, nudgesFor, opportunities, threadOf,
} from "./mail.ts";
import {
  buildScene, canvasHeight, cardLines, cardMetrics, clampView, cursorKey, fitView, mapX, sceneSummary,
  sessionLabel, slotPixels, tierFor, zoomView,
  GLYPH_H, LANE_HEAD_W, LEDGER_LEFT, SPINE_H, ZOOM_STEP,
} from "./mailCanvas.ts";
import type {
  MailGlyph, MailLane, MailNudgeMark, MailScene, MailThreadLink, MailTier, MailTrack, MailView,
} from "./mailCanvas.ts";
import type { MailNudgeRecord, MailState, MailThreadView } from "./mail.ts";

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
// **And the record no longer has to arrive while you watch** (6a): the snapshot
// preloads `mail.deliver` lines the daemon folded out of the trail, so a pickup
// that happened before the page was open reads ✓ like any other. The rule did
// not move — it is still a ledger line and nothing else — only how far back the
// picture can see one. That reach is finite (the trail's own lifetime, the
// fold's window), so when the fold reports itself INCOMPLETE the detail card
// says which of the two "no record" means: nobody read it, or nobody can still
// prove they did.
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
    : state === "stalled" ? "stalled — reconnecting"
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
  const streamStats = useStore((s) => s.mailStreamStats);
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
        <span className="muted mail-stream-stats" data-frames={streamStats.frames} title="mail.* frames folded into this picture since the view opened">
          {streamStats.frames.toLocaleString()} frame{streamStats.frames === 1 ? "" : "s"}
        </span>
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
            <Threads scene={scene} canvas={canvas} tier={tier} width={width} />
            <Forwards scene={scene} canvas={canvas} tier={tier} width={width} />
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
              {/* A sub-lane per MAILBOX (ADR-0018 d3/d7). A DECLARED one is
                  named by the operator, so it is spelled out whole — that name
                  is how a sender reaches it — while a window's mailbox is a
                  session id, shortened as it always was because nobody types
                  one. A dead one keeps its name and says so: the corpse is the
                  thing you are meant to see. */}
              {lane.tracks.map((t) => (
                <text key={t.key}
                  className={`lane-session presence-${t.presence}${t.named ? " is-named" : ""}${t.dead !== null ? " is-dead" : ""}`}
                  x={12} y={t.y + 4} style={{ fontSize: 10 }} data-track-label={t.key}
                  data-named={t.named ? "yes" : undefined} data-dead={t.dead !== null ? "yes" : undefined}>
                  {/* Only the NAME is struck: the strike says "this mailbox is
                      finished", and striking the words that explain WHY would
                      make the one legible thing on a greyed row the hardest to
                      read. */}
                  <tspan className={t.dead !== null ? "is-struck" : undefined}>
                    {t.session === null ? "sessionless" : t.named ? t.session : shortSession(t.session)}
                  </tspan>
                  {t.deliveries !== null ? ` · d${t.deliveries}` : ""}
                  {/* Just the word here: the gutter is 168px and what is
                      STRANDED is said on the track itself, where there is room
                      for the sentence. A count that spills out of the gutter
                      and into the ledger is the overflow ADR-0015 d5 opens
                      with. */}
                  {t.dead !== null ? " · dead" : ""}
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

/** The request→answer joins, drawn as one layer ABOVE the lanes and below the
 * spine. They live outside any lane on purpose: a thread's two ends are usually
 * two roles, so a link drawn inside a lane would be clipped by that lane's own
 * bounds exactly where it becomes interesting.
 *
 * The shape is a curve with its control points pushed VERTICALLY, so the link
 * leaves the request and enters the answer at the top and bottom of each glyph
 * rather than crossing the ledger sideways through everything between them.
 * At the FAR tier they are suppressed: at four pixels a slot the whole picture
 * is a pulse, and a mesh of curves over it reads as noise rather than as
 * threads. */
function Threads(
  { scene, canvas, tier, width }: { scene: MailScene; canvas: MailView; tier: MailTier; width: number },
) {
  if (tier === "far" || scene.threads.length === 0) return null;
  // The scene hands over LANE tops; the glyph's height is the tier's, so this
  // is where a link learns where its cards' top edges actually are.
  const lift = (GLYPH_H - 10 - glyphHeight(tier)) / 2;
  return (
    <g className="mail-threads" clipPath="url(#mailLedgerClip)" aria-hidden="true">
      {scene.threads.map((t) => (
        <ThreadLink key={`${t.requestOffset}-${t.answerOffset}`} t={t} canvas={canvas} width={width} top={lift} />
      ))}
    </g>
  );
}

function ThreadLink(
  { t, canvas, width, top }: { t: MailThreadLink; canvas: MailView; width: number; top: number },
) {
  const x1 = mapX(canvas, t.x1), x2 = mapX(canvas, t.x2);
  const y1 = t.y1 + top, y2 = t.y2 + top;
  // Both ends off-screen on the same side: nothing to draw. One end off-screen
  // still draws — a link running off the edge is how you learn the thread
  // continues past what you are looking at.
  if ((x1 < LEDGER_LEFT && x2 < LEDGER_LEFT) || (x1 > width && x2 > width)) return null;
  const arc = Math.max(18, Math.min(64, Math.abs(y2 - y1) / 2 + 18));
  const d = `M ${x1} ${y1} C ${x1} ${y1 - arc}, ${x2} ${y2 - arc}, ${x2} ${y2}`;
  return (
    <g className={`thread-link${t.late ? " is-late" : ""}`} data-thread={`${t.requestOffset}->${t.answerOffset}`}
      data-late={t.late ? "true" : undefined}>
      <path className="thread-path" d={d} fill="none" />
      <circle className="thread-end" cx={x2} cy={y2} r={3} />
    </g>
  );
}

/** The dead→live joins (ADR-0018 d6): what a reaper carried out of a mailbox
 * nobody reads into one somebody does. Drawn like a thread link and
 * deliberately not the same as one — a thread is a conversation, a forward is a
 * rescue — so it takes the same curve and its own class, and it points at the
 * COPY: that is the end a reader can still do something about. Suppressed at
 * the far tier for `Threads`' reason. */
function Forwards(
  { scene, canvas, tier, width }: { scene: MailScene; canvas: MailView; tier: MailTier; width: number },
) {
  if (tier === "far" || scene.forwards.length === 0) return null;
  const lift = (GLYPH_H - 10 - glyphHeight(tier)) / 2;
  return (
    <g className="mail-forwards" clipPath="url(#mailLedgerClip)" aria-hidden="true">
      {scene.forwards.map((f) => {
        const x1 = mapX(canvas, f.x1), x2 = mapX(canvas, f.x2);
        const y1 = f.y1 + lift, y2 = f.y2 + lift;
        if ((x1 < LEDGER_LEFT && x2 < LEDGER_LEFT) || (x1 > width && x2 > width)) return null;
        const arc = Math.max(18, Math.min(64, Math.abs(y2 - y1) / 2 + 18));
        return (
          <g key={`${f.originalOffset}-${f.copyOffset}`} className="forward-link"
            data-forward={`${f.originalOffset}->${f.copyOffset}`} data-forward-source={f.source}>
            <path className="forward-path" d={`M ${x1} ${y1} C ${x1} ${y1 - arc}, ${x2} ${y2 - arc}, ${x2} ${y2}`} fill="none" />
            <circle className="forward-end" cx={x2} cy={y2} r={3} />
          </g>
        );
      })}
    </g>
  );
}

/** The pokes about this lane's mail (ADR-0017 d10), aligned with the envelope
 * each one named — above the glyph, where a mark cannot be confused with the
 * held/expired marks that live under a TRACK. The distinction is the point: a
 * held mark is a cursor's arithmetic about mail it passed over, and a nudge is
 * the daemon saying nobody's cursor came at all. */
function Nudges(
  { lane, canvas, tier, width }: { lane: MailLane; canvas: MailView; tier: MailTier; width: number },
) {
  if (lane.nudges.length === 0) return null;
  return (
    <g className="lane-nudges" data-lane-nudges={lane.role}>
      {lane.nudges.map((n) => (
        <NudgeMark key={n.offset} n={n} lane={lane} canvas={canvas} tier={tier} width={width} />
      ))}
    </g>
  );
}

function NudgeMark(
  { n, lane, canvas, tier, width }: {
    n: MailNudgeMark; lane: MailLane; canvas: MailView; tier: MailTier; width: number;
  },
) {
  const x = mapX(canvas, n.x), w = Math.max(n.w * canvas.z, 3);
  if (x + w < LEDGER_LEFT || x > width) return null;
  // Just above the GLYPH, not above the lane: a glyph is centred in its row and
  // its height is the tier's, so a bar pinned to the row's top floats free of
  // the envelope it is about — which is the one thing this mark must not do.
  const y = lane.glyphY + (GLYPH_H - 10 - glyphHeight(tier)) / 2 - 6;
  const budget = n.last.budget;
  return (
    <g className="nudge-mark" data-nudge={n.offset} data-nudge-count={n.count}>
      <title>
        {`${n.count} nudge(s) raised about this envelope · ${n.last.reason}`
          + (budget === null ? ""
            : ` · budget envelope ${budget.envelope}/${budget.perEnvelope} · role ${budget.roleHour}/${budget.perRoleHour} this hour`)}
      </title>
      <line className="nudge-bar" x1={x + 1} y1={y} x2={x + w - 1} y2={y} />
      {tier !== "far" && w > 22 && (
        <text className="nudge-count" x={x + w / 2} y={y - 3} textAnchor="middle" style={{ fontSize: 9 }}>
          {n.count > 1 ? `nudged ×${n.count}` : "nudged"}
        </text>
      )}
    </g>
  );
}

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
        {/* The drop lines are the LANE's, not the envelopes'. They were inside
            the glyph group until `thread-canvas` grew the glyph and the bug
            surfaced: a drop line runs from the lane's top edge DOWN to a body
            that is centred in the row, so it stretched the group's bounding box
            upward until the button's own clickable centre sat above the
            envelope, in empty lane. A control whose centre is not on the thing
            it controls is broken however good it looks — and it looked fine. */}
        {lane.glyphs.map((g) => (
          <Drop key={g.offset} g={g} lane={lane} canvas={canvas} tier={tier} width={width} />
        ))}
        {lane.glyphs.map((g) => (
          <Glyph key={g.offset} g={g} lane={lane} canvas={canvas} tier={tier} width={width}
            selected={selected === g.offset} onSelect={onSelect} />
        ))}
        {lane.tracks.map((t) => (
          <Track key={t.key} track={t} lane={lane} canvas={canvas} tier={tier} width={width} />
        ))}
        <Nudges lane={lane} canvas={canvas} tier={tier} width={width} />
      </g>
    </g>
  );
}

/** The line joining an envelope to the spine: this envelope's place on the
 * ledger. Drawn per glyph but OWNED by the lane — see the note in `Lane` — and
 * carrying its own `data-arrival` so the arrival animation keys on the line
 * itself rather than on an ancestor it no longer has. */
function Drop(
  { g, lane, canvas, tier, width }: {
    g: MailGlyph; lane: MailLane; canvas: MailView; tier: MailTier; width: number;
  },
) {
  const x = mapX(canvas, g.x), w = Math.max(g.w * canvas.z, 3);
  if (x + w < LEDGER_LEFT || x > width) return null;
  const y = lane.glyphY + (GLYPH_H - 10 - glyphHeight(tier)) / 2;
  return (
    <line className="glyph-drop" data-arrival={g.arrival} aria-hidden="true"
      x1={x + w / 2} y1={lane.y - 4} x2={x + w / 2} y2={y} />
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
  const h = glyphHeight(tier);
  const y = lane.glyphY + (GLYPH_H - 10 - h) / 2;
  const pick = () => onSelect(selected ? null : g.offset);
  // Only the two standings that mean something is OUTSTANDING get a mark: an
  // open question, and an answer that arrived after the race was over (N5). A
  // closed request and the answer that won it are already drawn — by the link
  // between them — and marking every threaded envelope would be marking none.
  const threadMark = THREAD_MARK[g.thread?.standing ?? ""] ?? null;
  return (
    <g
      className={`mail-glyph glyph-${g.status} prio-${g.priority}`
        + (g.thread === null ? "" : ` thread-${g.thread.standing}`)
        + (g.nudges.length > 0 ? " is-nudged" : "")
        + (selected ? " is-selected" : "")}
      data-glyph={g.offset}
      data-status={g.status}
      data-priority={g.priority}
      data-thread={g.thread?.standing}
      data-nudged={g.nudges.length > 0 ? g.nudges.length : undefined}
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
      <title>
        {`${g.id} · ${g.priority} ${g.kind} · ${g.topic} · ${statusWords(g.status)}`
          + (g.thread === null ? "" : ` · ${threadWords(g.thread)}`)}
      </title>
      <rect className="glyph-body" x={x} y={y} width={w} height={h} rx={4} />
      <rect className="glyph-prio" x={x} y={y} width={4} height={h} />
      {/* At the MID tier the glyph is one 26px row with a topic on the left and
          a status glyph on the right, and a thread tag is a THIRD thing wanting
          the same row. Spelled as a word it printed straight through the topic
          — so it is a mark before the topic instead, sized in the space the
          topic then gives up. The word itself lives at the near tier, in the
          card, and in the <title> at every tier. Two DIFFERENT marks, never one
          mark in two colours: colour is not a channel this canvas relies on. */}
      {tier === "mid" && w > 44 && (
        <>
          {threadMark !== null && (
            <text className={`glyph-thread thread-${g.thread!.standing}`} x={x + 9} y={y + h / 2 + 4}
              style={{ fontSize: 10 }} data-thread-tag={g.thread!.standing} aria-hidden="true">
              {threadMark}
            </text>
          )}
          <text className="glyph-topic" x={x + (threadMark === null ? 9 : 21)} y={y + h / 2 + 4} style={{ fontSize: 11 }}>
            {truncate(g.topic, (w - 20 - (threadMark === null ? 0 : 12)) / 6.6)}
          </text>
        </>
      )}
      {tier === "near" && (
        <g data-envelope-card={g.offset}>
          {/* Budgeted against the card's OWN width, not the slot's: the glyph
              is the slot minus its padding, and a line measured against the
              wider number is a line that touches the border. */}
          {(() => {
            const lines = cardLines(g, (w - 20) / 6.7);
            const { top, step } = cardMetrics(lines.length);
            return lines.map((line, i) => (
              <text key={i} className={`card-line card-line-${i}`} x={x + 9} y={y + top + i * step}
                style={{ fontSize: i === 1 ? 12 : 10.5 }}>
                {line}
              </text>
            ));
          })()}
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

/** How tall a glyph is at each tier — one spelling, because the nudge bar
 * hangs off the glyph's top edge and a second copy of this arithmetic would
 * drift the mark away from the envelope it is about. */
function glyphHeight(tier: MailTier): number {
  return tier === "far" ? 12 : tier === "mid" ? 26 : GLYPH_H - 10;
}

/** Two DISTINCT marks, so the tag survives a colourblind reader and a
 * greyscale print: an open diamond is a question still open, a filled one is an
 * answer that landed after the thread had closed. `answered` is deliberately
 * absent — see `threadMark`. */
const THREAD_MARK: Record<string, string> = { awaiting: "◇", late: "◆" };

const STATUS_GLYPH: Record<string, string> = {
  fresh: "●", held: "◐", expired: "○", delivered: "✓", passed: "·",
  "no-reader": "–", unreadable: "✗", torn: "⋯", unknown: "?", foreign: "",
};

/** The thread standing, spoken. The `<title>` is what a screen reader and a
 * hover both get, and "awaiting" alone would not say awaiting WHAT. */
function threadWords(t: MailThreadView): string {
  switch (t.standing) {
    case "awaiting": return "a request no answer has closed yet";
    case "answered": return t.end === "request"
      ? `answered by ${t.counterpart}` : `answers ${t.counterpart}`;
    case "late": return `a late answer to ${t.counterpart} — another answer closed it first`;
  }
}

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
    <g className={`mail-track presence-${track.presence}${track.uncertain !== null ? " is-uncertain" : ""}`
        + `${track.dead !== null ? " is-dead" : ""}${track.named ? " is-named" : ""}`}
      data-track={track.key} data-presence={track.presence} data-pickup={track.pickup ?? undefined}
      data-motion={track.motion ?? undefined} data-address={track.address}
      data-dead={track.dead !== null ? "yes" : undefined} data-named={track.named ? "yes" : undefined}
      data-uncertain={track.uncertain !== null ? "yes" : undefined}>
      <title>
        {`${track.address} — ${track.offset === null ? "position unknown"
          : `at byte ${track.offset}${track.atFrontier ? " (the frontier: nothing pending ahead)" : ""}`}`
          + `; ${track.pendingCount} pending, ${track.expiredCount} spent`
          + (track.uncertain !== null ? `; uncertain: ${track.uncertain}` : "")
          + (track.dead !== null ? `; the watcher calls this mailbox dead — ${track.dead}` : "")}
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
                {m.status === "expired" ? "spent"
                  // A unicast envelope has no denominator to count toward
                  // (ADR-0018 d5): it is held until its one addressee takes it.
                  // `n held` states the fact the mark actually knows, where
                  // `n/3` would state one it does not.
                  : m.ttlDeliveries === null ? `${m.opportunities} held`
                  : `${m.opportunities}/${m.ttlDeliveries}`}
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
      {/* A corpse says so ON the track, not only in the gutter: the mail is
          still hanging above it and a reader has to know nobody is coming for
          it. The word is the WATCHER's — a `mail.nudge` named this address —
          never a judgement the browser made (ADR-0018 d6). */}
      {track.dead !== null && tier !== "far" && (
        <text className="track-flag is-dead" x={LEDGER_LEFT + 6} y={y + 12} style={{ fontSize: 9 }}
          data-track-dead={track.address}>
          dead — nobody reads this mailbox
          {track.pendingCount > 0 ? ` · ${track.pendingCount} stranded` : ""}
          {track.forwards.length > 0 ? ` · forwarded to ${track.forwards[track.forwards.length - 1].to}` : ""}
        </text>
      )}
      {track.reanchored && track.dead === null && tier !== "far" && (
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

/** The thread and the poke are legends of their own, not statuses: an envelope
 * has exactly one STATUS (what a cursor makes of it) and may additionally be in
 * a thread and have been nudged about. Folding them into `LEGEND` would imply a
 * fresh envelope could not also be awaiting an answer, which is the ordinary
 * case rather than the exception. */
const THREAD_LEGEND: { key: string; swatch: string; label: string }[] = [
  { key: "awaiting", swatch: "◇", label: "request awaiting an answer" },
  { key: "answered", swatch: "→", label: "request → the answer that closed it" },
  { key: "late", swatch: "◆", label: "late answer — another closed it first" },
  { key: "nudged", swatch: "▔", label: "nudged: a turn was woken about it" },
  // The two ADR-0018 d6 facts a reader has to be able to name: a mailbox
  // nobody reads any more, and the copy a reaper carried out of one.
  { key: "forwarded", swatch: "⇢", label: "forwarded out of a dead mailbox — a new envelope" },
  { key: "dead", swatch: "⌁", label: "dead mailbox: standing with nobody behind it" },
];

function Legend() {
  return (
    <>
      <ul className="mail-legend" data-mail-legend>
        {LEGEND.map((l) => (
          <li key={l.status} data-legend={l.status}>
            <span className={`legend-swatch glyph-${l.status}`} aria-hidden="true">{STATUS_GLYPH[l.status]}</span>
            {l.label}
          </li>
        ))}
      </ul>
      <ul className="mail-legend mail-legend-thread" data-mail-legend="thread">
        {THREAD_LEGEND.map((l) => (
          <li key={l.key} data-legend={l.key}>
            <span className={`legend-swatch legend-${l.key}`} aria-hidden="true">{l.swatch}</span>
            {l.label}
          </li>
        ))}
      </ul>
    </>
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
  // The recipient predicate again, and the one place it is easiest to forget:
  // "who reads this envelope" is `MailAddress.Accepts`, not a role comparison —
  // a unicast is read by ONE mailbox, and listing every sibling's standing
  // under it would be the card contradicting the lane (ADR-0018 d4).
  const readers = state.cursors.filter((c) => e !== null && accepts(c, e.to));
  const closedBy = e === null ? undefined : state.closedBy[e.id];
  const thread = e === null ? null : threadOf(state, e);
  const nudges = nudgesFor(state, line.offset);
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
            {/* The thread's two halves, shown only when set: `inReplyTo` says
                which envelope this answers, `replyTo` where an answer to THIS
                one should go (ADR-0018 d4). Snapshot-only fields, so a card
                for a trail-learned line simply lacks them. */}
            {e.inReplyTo !== null && <div><dt>answers</dt><dd>
              <code>{e.inReplyTo}</code>
              {/* The depth against d8's budget, as the envelope's ORDINAL in
                  its thread (a head is 1/N) so N/N reads as full — the same
                  spelling the digest head hands a model. STORED, never walked:
                  the number is what this line's own bytes say. */}
              {" "}· <span className="muted" data-detail-hops>
                hop {e.hops + 1}{state.hopBudget === null ? "" : `/${state.hopBudget}`}
              </span>
            </dd></div>}
            {/* Who closed it, from the thread index — the server builds it over
                the WHOLE ledger, so a request answered before this page loaded
                still reads as answered. */}
            {closedBy !== undefined && <div><dt>answered by</dt><dd>
              <code data-detail-closed-by>{closedBy}</code>
            </dd></div>}
            {/* Where this envelope stands in its thread — the reading the
                canvas draws, spelled out. `late` is the one that needs the
                sentence rather than the tag: an answer arriving second is a
                normal race on a bus with no locking (N5), and a reader who sees
                only the word would take it for a fault. */}
            {thread !== null && <div><dt>thread</dt><dd data-detail-thread={thread.standing}>
              {threadWords(thread)}
              {thread.standing === "late" && <>
                {" "}— <span className="muted">two members answering one question is a race, not a fault;
                the later answer may be the better one.</span>
              </>}
            </dd></div>}
            {e.replyTo !== null && <div><dt>reply to</dt><dd><code data-detail-reply-to>{e.replyTo}</code></dd></div>}
            {/* A COPY, and where it was stranded (ADR-0018 d6/d8). Both halves,
                because the id says which envelope and the address says whose
                mailbox — the half no reader could recover from the original's
                `to` when that is a bare role a dozen mailboxes hold. */}
            {e.forwardedFrom !== null && <div><dt>forwarded</dt><dd data-detail-forwarded-from={e.forwardedFrom.id}>
              from <code>{e.forwardedFrom.address}</code>, which held <code>{e.forwardedFrom.id}</code>
              {" "}— <span className="muted">a new envelope; the original stays on the ledger, unread</span>
            </dd></div>}
            <div><dt>ttl</dt><dd>{e.ttlDeliveries === null
              ? <>none — unicast, delivered once to <code>{e.to}</code></>
              : <>{e.ttlDeliveries} delivery opportunit{e.ttlDeliveries === 1 ? "y" : "ies"}</>}</dd></div>
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

      {nudges.length > 0 && (
        <>
          <h3>Nudges</h3>
          {/* What a poke COST, as numbers (d10) — never the row's own prose,
              which a reader would have to parse to learn what it spent. */}
          <ul className="mail-nudges" data-mail-nudges={nudges.length}>
            {nudges.map((n, i) => <li key={i} data-nudge-row={n.dispatchId ?? i}>{nudgeWords(n)}</li>)}
          </ul>
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
                  ? held.ttlDeliveries === null
                    ? <>holds it — passed over {opportunities(c.deliveries, held.seenAt)} times, and unicast mail does not expire</>
                    : <>holds it — passed over at {opportunities(c.deliveries, held.seenAt)} of {held.ttlDeliveries} opportunities</>
                  : c.offset === null ? <>position unknown</>
                  : line.offset >= c.offset ? <>has not reached it yet</>
                  : records.length > 0
                    ? <>delivered at the <code>{records[0].seam}</code> seam by <code>{records[0].vehicle}</code>
                        {records[0].dispatchId !== null
                          && state.nudges.some((n) => n.dispatchId === records[0].dispatchId)
                          && <span data-pickup="robot"> — to a turn a nudge woke</span>}</>
                    : <>is past it · <span className="muted" data-no-record={state.deliveriesComplete ? "complete" : "bounded"}>
                        {state.deliveriesComplete
                          ? "no delivery record in this picture"
                          : "no delivery record — the trail fold reaches back only so far"}
                      </span></>}
              </li>
            );
          })}
        </ul>
      )}
    </div>
  );
}

/** One nudge row in words. The budget is spelled with the same four numbers
 * `MailNudgeBudget.Clause` renders on the engine side, in the same order, so a
 * maintainer reading the trail and a maintainer reading this page are reading
 * one arithmetic. */
function nudgeWords(n: MailNudgeRecord): string {
  const who = n.address === null ? `the ${n.role} lane` : `${n.address}, via the ${n.role} lane`;
  const cost = n.budget === null ? ""
    : ` · budget envelope ${n.budget.envelope}/${n.budget.perEnvelope}`
      + ` · role ${n.budget.roleHour}/${n.budget.perRoleHour} this hour`;
  const at = n.ts === null ? "" : ` · ${n.ts}`;
  return `${who} — ${n.reason}${cost}${at}`;
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
