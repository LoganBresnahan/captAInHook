import { useStore } from "./store.ts";
import { useApiJson } from "./api.ts";
import { uptime } from "./format.ts";
import type { StatusDto } from "./api.gen.ts";

// The Status island (ADR-0008 d1): identity + the live serve counters, its own
// createRoot mount reading the store's `status` slice. Polled — the counters
// (active, served, uptime, openStreams) are a moving dashboard, and a 3s
// loopback GET is free; App's session probe already seeded the first value, so
// there is no blank frame. A 401 on any poll flips the whole session to dead
// (useApiJson owns that), which unmounts this.
//
// Slice 7 gives the view a HIERARCHY. Seven equal boxes meant nothing led: a
// content hash the operator compares by eye against a deploy sat at the same
// weight as the count of dispatches served, and the seventh box wrapped alone
// onto a second row. Now identity is a reference strip (text, because that is
// what it is — something you read and compare, never a metric that moves), and
// the tiles are the LIVE numbers only, with the headline one leading.
export function StatusPanel() {
  const view = useStore((s) => s.view);
  const session = useStore((s) => s.session);
  const status = useStore((s) => s.status);
  const setStatus = useStore((s) => s.setStatus);
  useApiJson<StatusDto>("/api/v1/status", setStatus, 3000);

  // The view gate (ADR-0015 d1) sits AFTER the hooks — a hidden island keeps
  // polling, so switching to it shows current data, not a blank frame. Gating
  // before the hook would break the rules of hooks anyway.
  if (view !== "status" || session !== "live" || status === null) return null;

  return (
    <section className="card" data-island="status">
      <h2>Daemon</h2>

      {/* Reference data, not metrics: the identity a deploy is verified against
          and the pid a `kill`/strace needs. Monospace because every one of
          these is a string the daemon produced. */}
      <p className="identity-line" data-identity-strip>
        <span data-status-field="identity">
          <span className="muted">identity </span>
          <code>{status.version}</code>
        </span>
        <span data-status-field="pid">
          <span className="muted">pid </span>
          <code>{status.pid}</code>
        </span>
        {status.shimPath !== null && (
          <span data-status-field="shim">
            <span className="muted">shim </span>
            <code>{status.shimPath}</code>
          </span>
        )}
      </p>

      <dl className="tiles" data-tiles>
        {/* The headline: what this daemon has actually DONE. Its note carries
            the uptime, which is the context that makes the count mean
            something — 128 dispatches in four minutes and 128 since Tuesday
            are different daemons. */}
        <Tile
          label="served"
          value={status.served}
          note={`in ${uptime(status.uptimeMs)}`}
          lead
        />
        <Tile
          label="in flight"
          value={status.active}
          note={status.active > 0 ? "dispatching now" : "idle"}
        />
        <Tile
          label="background"
          value={status.backgroundPending}
          note={status.backgroundPending > 0 ? "draining" : "none pending"}
        />
        <Tile
          label="open streams"
          value={status.openStreams}
          note="incl. this page"
        />
      </dl>

      <Supervision />
    </section>
  );
}

/** One stat tile: a micro-label, the value, and an optional note — the note is
 * the tile's context slot (what the number MEANS right now), never decoration.
 * `tone` colors the value AND ships a glyph, because color is never allowed to
 * be the only channel carrying a state. */
function Tile({ label, value, note, tone, lead }: {
  label: string;
  value: string | number;
  note?: string | null;
  tone?: "warn" | "bad" | null;
  lead?: boolean;
}) {
  const mark = tone === "bad" ? "✕" : tone === "warn" ? "⚠" : null;
  return (
    <div className={lead ? "tile lead" : "tile"} data-metric={label} data-tone={tone ?? undefined}>
      <dt>{label}</dt>
      <dd className={tone ?? undefined}>
        {mark !== null && <span className="tone-mark" aria-hidden="true">{mark} </span>}
        {value}
      </dd>
      {note != null && <p className="tile-note">{note}</p>}
    </div>
  );
}

// The supervision summary (ADR-0015 d6): the daemon-wide health numbers that
// used to sit at the top of the handlers card. They belong here — "how many
// handlers are escalated" is a health question, and the per-handler detail
// (which one, on which event, with which child) lives on the Handlers view.
//
// `restarts` is derived, not reported: a worker's generation starts at 1, so
// the restarts it has survived is generation - 1 (ADR-0002's supervision
// model), summed across registrations. It reads 0 on a healthy daemon, which is
// the point — a non-zero number is the thing worth seeing from here.
//
// Each number carries its CONSEQUENCE as the tile note, because the number
// alone does not tell an operator what it costs them: an escalated worker is
// not a statistic, it is asks failing fast from here on (ADR-0004 d5).
function Supervision() {
  const handlers = useStore((s) => s.handlers);
  if (handlers === null) return null;

  const regs = handlers.handlers;
  const dead = regs.filter((h) => h.dead).length;
  const restarts = regs.reduce((n, h) => n + Math.max(0, h.generation - 1), 0);
  const children = regs.filter((h) => h.childState !== null);
  const ready = children.filter((h) => h.childState === "ready").length;
  const failed = children.filter((h) => h.childState === "failed").length;

  return (
    <>
      <h3>Supervision</h3>
      <dl className="tiles" data-supervision>
        <Tile label="registrations" value={regs.length} note="handler × event pairs" />
        <Tile
          label="escalated"
          value={dead}
          tone={dead > 0 ? "bad" : null}
          note={dead > 0 ? "asks fail fast — see Handlers" : "none"}
        />
        <Tile
          label="restarts"
          value={restarts}
          tone={restarts > 0 ? "warn" : null}
          note={restarts > 0 ? "survived, state was reset" : "none survived"}
        />
        <Tile
          label="resident children"
          value={children.length === 0 ? "—" : `${ready}/${children.length}`}
          tone={failed > 0 ? "bad" : null}
          note={children.length === 0 ? "no resident handlers" : failed > 0 ? `${failed} failed readiness` : "ready"}
        />
      </dl>
    </>
  );
}
