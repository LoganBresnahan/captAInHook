import { memo, useEffect, useMemo, useRef, useState } from "react";
import { useStore } from "./store.ts";
import { dispatchHue, clockTime, traceMatches, agoLabel } from "./format.ts";
import { useTick } from "./tick.ts";
import { emptyTraceReason } from "./streamHealth.ts";
import type { TraceEntry, StreamStats } from "./store.ts";

// The Live-trace island (ADR-0008 d1) — the observability payoff: dispatches as
// they happen, fed from the SSE stream via the store's `trace` slice (the one
// fold reducer, d8; this island only READS). The moderate parts live here:
//   * dispatchId CORRELATION — a stable color per id so the eye follows one
//     dispatch through the interleaved stream, and a click filters to it;
//   * client-side FILTER — a substring over the searchable fields;
//   * follow-the-tail SCROLL — auto-stick to the newest line unless the user
//     scrolls up to read history (then a "jump to latest" affordance);
//   * gap/reset rendered HONESTLY — the server's drop-oldest gap and the
//     truncation reset are dividers, never silently swallowed.

function StreamBadge({ state }: { state: string }) {
  const label = state === "live" ? "streaming"
    : state === "retrying" ? "reconnecting…"
    : state === "stalled" ? "stalled — reconnecting"
    : state === "dead" ? "disconnected"
    : "idle";
  return <span className={`stream-badge stream-${state}`} data-stream={state}>● {label}</span>;
}

/** What the stream has actually delivered, beside what the badge claims
 * (2026-08-16). "streaming" was set on headers and never questioned; a panel
 * could sit at 0 lines under a green dot while the daemon dispatched — and
 * nothing on screen could say which side was wrong. Now the count and the
 * age of the last frame are always shown, and the empty state compares the
 * daemon's own `served` counter against what this connection has seen. */
function StreamStatsLine({ stats, now }: { stats: StreamStats; now: number }) {
  return (
    <span className="muted trace-stats" data-frames={stats.frames} data-connects={stats.connects}>
      {" · "}{stats.frames.toLocaleString()} frame{stats.frames === 1 ? "" : "s"} received
      {stats.lastFrameAt !== null && ` · last ${agoLabel(now - stats.lastFrameAt)}`}
      {stats.connects > 1 && ` · ${stats.connects} connects`}
    </span>
  );
}

function chipStyle(id: string): React.CSSProperties {
  const hue = dispatchHue(id);
  return { background: `hsl(${hue} 70% 55% / 0.18)`, borderColor: `hsl(${hue} 70% 55% / 0.55)` };
}

// One row. MEMOIZED because the list is append-heavy: a single new line
// re-renders the <ol>, and without this every one of TRACE_CAP existing rows
// re-runs its render for nothing. `onPickDispatch` is a useState setter, which
// React guarantees is stable, so the memo actually holds (ADR-0015's rejected
// alternative: a virtualization dependency — memo + content-visibility carries
// the cap, measured, and costs no dependency).
//
// Every cell is ALWAYS rendered, empty when the field is absent: the row is a
// CSS grid, and an omitted cell would slide every later column left, which is
// exactly the ragged alignment this slice exists to fix.
const Row = memo(function Row(
  { entry, onPickDispatch }: { entry: TraceEntry; onPickDispatch: (id: string) => void },
) {
  if (entry.kind === "gap")
    return <li className="trace-divider gap" data-trace="gap">— {entry.dropped} event(s) dropped (slow consumer); a reconnect recovers them —</li>;
  if (entry.kind === "reset")
    return <li className="trace-divider reset" data-trace="reset">— stream reset: earlier history cleared —</li>;
  if (entry.kind === "unparsed")
    return <li className="trace-row unparsed" data-trace="unparsed"><code>{entry.raw}</code></li>;

  const l = entry.line;
  const did = typeof l.dispatchId === "string" ? l.dispatchId : null;
  return (
    <li className="trace-row" data-trace="line" data-dispatch={did ?? ""}>
      <span className="t-time">{clockTime(l.ts)}</span>
      <span className={`t-level lvl-${l.level ?? "info"}`}>{l.level ?? ""}</span>
      <span className="t-comp">{l.comp ?? ""}</span>
      <span className="t-evt">{l.evt ?? ""}</span>
      <span className="t-did-cell">
        {did && (
          <button className="t-did" style={chipStyle(did)} onClick={() => onPickDispatch(did)} title="filter to this dispatch">
            {did}
          </button>
        )}
      </span>
      <span className="t-dur">{typeof l.durMs === "number" ? `${l.durMs}ms` : ""}</span>
      <span className="t-msg">{l.msg ?? ""}</span>
    </li>
  );
});

export function TracePanel() {
  const view = useStore((s) => s.view);
  const session = useStore((s) => s.session);
  const trace = useStore((s) => s.trace);
  const truncated = useStore((s) => s.traceTruncated);
  const stream = useStore((s) => s.stream);
  const stats = useStore((s) => s.streamStats);
  const served = useStore((s) => s.status?.served ?? null);
  const [filter, setFilter] = useState("");
  const [following, setFollowing] = useState(true);
  const scrollRef = useRef<HTMLOListElement>(null);

  const shown = useMemo(
    () => (filter === "" ? trace : trace.filter((e) => traceMatches(e, filter))),
    [trace, filter],
  );

  // Follow-the-tail: stick to the bottom on new lines UNLESS the user scrolled
  // up. Keyed on the shown length so it fires exactly when rows are added.
  useEffect(() => {
    if (!following) return;
    const el = scrollRef.current;
    if (el) el.scrollTop = el.scrollHeight;
  }, [shown.length, following]);

  // The "last frame N s ago" label ages against a monotonic stamp; nothing
  // else re-renders it, so tick while visible.
  useTick(1000, view === "trace" && session === "live");

  // The view gate (ADR-0015 d1). The SSE client lives OUTSIDE React (main.tsx)
  // and folds into the store regardless of what is rendered, so a hidden Trace
  // loses nothing: leave, come back, and the whole stream is there — including
  // lines that arrived while another view was on screen.
  if (view !== "trace" || session !== "live") return null;

  const onScroll = () => {
    const el = scrollRef.current;
    if (!el) return;
    // Within 24px of the bottom counts as "at the tail" — resume following;
    // scroll up past that and we stop yanking the view down.
    const atBottom = el.scrollHeight - el.scrollTop - el.clientHeight < 24;
    setFollowing(atBottom);
  };

  return (
    <section className="card trace" data-island="trace">
      <div className="trace-head">
        <h2>Live trace</h2>
        <StreamBadge state={stream} />
        <input
          className="trace-filter"
          type="search"
          placeholder="filter (event, dispatch, message…)"
          value={filter}
          onChange={(e) => setFilter(e.target.value)}
          aria-label="filter trace"
        />
        {filter !== "" && <button onClick={() => setFilter("")}>clear</button>}
        {!following && <button onClick={() => setFollowing(true)}>jump to latest ↓</button>}
      </div>
      <p className="trace-meta">
        <span className="muted">
          {shown.length.toLocaleString()}
          {filter !== "" && ` of ${trace.length.toLocaleString()}`} line
          {shown.length === 1 ? "" : "s"}
        </span>
        {truncated > 0 && (
          <span className="muted trace-trunc">· {truncated.toLocaleString()} older dropped (client cap)</span>
        )}
        <StreamStatsLine stats={stats} now={performance.now()} />
      </p>
      <ol className="trace-list" ref={scrollRef} onScroll={onScroll} data-trace-count={shown.length}>
        {shown.length === 0 ? (
          (() => {
            const why = emptyTraceReason(filter, stream, stats, served);
            return <li className="muted trace-empty" data-trace-empty={why.kind}>{why.text}</li>;
          })()
        ) : (
          shown.map((e, i) => (
            <Row key={e.kind === "line" || e.kind === "unparsed" ? e.id : `${e.kind}-${i}`} entry={e} onPickDispatch={setFilter} />
          ))
        )}
      </ol>
    </section>
  );
}
