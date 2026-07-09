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
  const session = useStore((s) => s.session);
  const status = useStore((s) => s.status);
  const setStatus = useStore((s) => s.setStatus);
  useApiJson<StatusDto>("/api/v1/status", setStatus, 3000);

  if (session !== "live" || status === null) return null;

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
    </section>
  );
}
