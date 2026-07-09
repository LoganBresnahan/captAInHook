import { useStore } from "./store.ts";
import { useApiJson } from "./api.ts";
import type { HarnessesDto } from "./api.gen.ts";

// The Harnesses island (ADR-0008 d1): the registry projection (ADR-0003) — each
// known harness spec, its response adapter, and the per-event effect
// capabilities it declares. Essentially static (it changes only on a harness
// hot-reload), so it is fetched ONCE on live, no poll. The events map is
// {eventName: [allowed effect verbs]} — the closed Effect set a harness permits
// per lifecycle event.
export function HarnessesPanel() {
  const session = useStore((s) => s.session);
  const harnesses = useStore((s) => s.harnesses);
  const setHarnesses = useStore((s) => s.setHarnesses);
  useApiJson<HarnessesDto>("/api/v1/harnesses", setHarnesses);

  if (session !== "live" || harnesses === null) return null;

  return (
    <section className="card" data-island="harnesses">
      <h2>Harnesses</h2>
      <ul className="harnesses">
        {harnesses.harnesses.map((h) => (
          <li key={h.name} data-harness={h.name}>
            <div className="harness-head">
              <strong>{h.name}</strong>
              <span className="muted"> · {h.responseAdapter}</span>
            </div>
            <div className="events">
              {Object.entries(h.events).map(([evt, verbs]) => (
                <span key={evt} className="event-cap" title={verbs.join(", ")}>
                  {evt}
                  <span className="muted"> ({verbs.length})</span>
                </span>
              ))}
            </div>
          </li>
        ))}
      </ul>
    </section>
  );
}
