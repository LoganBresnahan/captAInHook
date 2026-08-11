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

  const metrics: [string, string | number][] = [
    ["identity", status.version],
    ["pid", status.pid],
    ["uptime", uptime(status.uptimeMs)],
    ["in flight", status.active],
    ["served", status.served],
    ["background", status.backgroundPending],
    ["open streams", status.openStreams],
  ];

  return (
    <section className="card" data-island="status">
      <h2>Daemon</h2>
      <dl className="metrics">
        {metrics.map(([label, value]) => (
          <div key={label} data-metric={label}>
            <dt>{label}</dt>
            <dd>{value}</dd>
          </div>
        ))}
      </dl>
      <Supervision />
    </section>
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
function Supervision() {
  const handlers = useStore((s) => s.handlers);
  if (handlers === null) return null;

  const regs = handlers.handlers;
  const dead = regs.filter((h) => h.dead).length;
  const restarts = regs.reduce((n, h) => n + Math.max(0, h.generation - 1), 0);
  const children = regs.filter((h) => h.childState !== null);
  const ready = children.filter((h) => h.childState === "ready").length;
  const failed = children.filter((h) => h.childState === "failed").length;

  const rows: [string, string | number, string | null][] = [
    ["registrations", regs.length, null],
    ["escalated", dead, dead > 0 ? "bad" : null],
    ["restarts", restarts, restarts > 0 ? "warn" : null],
    ["resident children", children.length === 0 ? "—" : `${ready}/${children.length} ready`,
      failed > 0 ? "bad" : null],
  ];

  return (
    <>
      <h3>Supervision</h3>
      <dl className="metrics" data-supervision>
        {rows.map(([label, value, tone]) => (
          <div key={label} data-metric={label}>
            <dt>{label}</dt>
            <dd className={tone ?? undefined}>{value}</dd>
          </div>
        ))}
      </dl>
    </>
  );
}
