import { useEffect, useMemo, useRef, useState } from "react";
import { useStore } from "./store.ts";
import { dispatchHue, clockTime, traceMatches } from "./format.ts";
import type { TraceEntry } from "./store.ts";

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
    : state === "dead" ? "disconnected"
    : "idle";
  return <span className={`stream-badge stream-${state}`} data-stream={state}>● {label}</span>;
}

function chipStyle(id: string): React.CSSProperties {
  const hue = dispatchHue(id);
  return { background: `hsl(${hue} 70% 55% / 0.18)`, borderColor: `hsl(${hue} 70% 55% / 0.55)` };
}

function Row({ entry, onPickDispatch }: { entry: TraceEntry; onPickDispatch: (id: string) => void }) {
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
      {did && (
        <button className="t-did" style={chipStyle(did)} onClick={() => onPickDispatch(did)} title="filter to this dispatch">
          {did}
        </button>
      )}
      {typeof l.durMs === "number" && <span className="t-dur">{l.durMs}ms</span>}
      <span className="t-msg">{l.msg ?? ""}</span>
    </li>
  );
}

export function TracePanel() {
  const session = useStore((s) => s.session);
  const trace = useStore((s) => s.trace);
  const truncated = useStore((s) => s.traceTruncated);
  const stream = useStore((s) => s.stream);
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

  if (session !== "live") return null;

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
      {truncated > 0 && (
        <p className="muted trace-trunc">showing the last {trace.length.toLocaleString()} lines ({truncated.toLocaleString()} older dropped)</p>
      )}
      <ol className="trace-list" ref={scrollRef} onScroll={onScroll} data-trace-count={shown.length}>
        {shown.length === 0 ? (
          <li className="muted trace-empty">
            {filter === "" ? "Waiting for hook activity — fire a prompt or tool call." : "No lines match the filter."}
          </li>
        ) : (
          shown.map((e, i) => (
            <Row key={e.kind === "line" || e.kind === "unparsed" ? e.id : `${e.kind}-${i}`} entry={e} onPickDispatch={setFilter} />
          ))
        )}
      </ol>
    </section>
  );
}
