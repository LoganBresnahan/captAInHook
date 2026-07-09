import { useStore } from "./store.ts";
import { useApiJson } from "./api.ts";
import type { HandlersDto } from "./api.gen.ts";

// The Supervision island (ADR-0008 d1): every registered handler with its fail
// mode and live supervision state — generation (restart count) and dead
// (escalated, no longer serving). Polled every 4s: restarts/escalations are
// rare and event-driven, but a poll keeps the view honest without correlating
// SSE (a live-from-the-stream version is a later refinement, not v1). A
// generation > 0 means the handler's supervised Worker has restarted; `dead`
// means it escalated past its restart budget and the dispatcher fails it
// closed/open per its FailMode.
export function SupervisionPanel() {
  const session = useStore((s) => s.session);
  const handlers = useStore((s) => s.handlers);
  const setHandlers = useStore((s) => s.setHandlers);
  useApiJson<HandlersDto>("/api/v1/handlers", setHandlers, 4000);

  if (session !== "live" || handlers === null) return null;

  return (
    <section className="card" data-island="supervision">
      <h2>Handlers</h2>
      {handlers.handlers.length === 0 ? (
        <p className="muted">No handlers registered.</p>
      ) : (
        <table>
          <thead>
            <tr><th>event</th><th>handler</th><th>fail</th><th>gen</th><th>state</th></tr>
          </thead>
          <tbody>
            {handlers.handlers.map((h) => (
              <tr key={`${h.event}/${h.name}`} data-handler={h.name} data-dead={h.dead}>
                <td>{h.event}</td>
                <td>{h.name}</td>
                <td>{h.failMode}</td>
                <td>{h.generation}</td>
                <td className={h.dead ? "bad" : "ok"}>{h.dead ? "dead" : "live"}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </section>
  );
}
