import { useStore } from "./store.ts";
import { useApiJson } from "./api.ts";
import type { HandlersDto } from "./api.gen.ts";
import { HandlersSection } from "./HandlersEditor.tsx";

// The Supervision island (ADR-0008 d1; ADR-0010 d8): every registered handler
// with its fail mode, live supervision state (generation = restart count, dead
// = escalated past its budget), and — for a resident exec handler — its live
// CHILD state (spawning/ready/failed + pid). Below the live table, the
// handlers.json EXPECTED-vs-REGISTERED view: the file's declared entries joined
// to what actually registered, so a warn-and-skip entry shows as skipped (never
// as a live row — the N2 caution) and a malformed file is loud. Polled every 4s.
export function SupervisionPanel() {
  const view = useStore((s) => s.view);
  const session = useStore((s) => s.session);
  const handlers = useStore((s) => s.handlers);
  const setHandlers = useStore((s) => s.setHandlers);
  useApiJson<HandlersDto>("/api/v1/handlers", setHandlers, 4000);

  // The view gate (ADR-0015 d1), after the hooks so the poll keeps the slice
  // warm while hidden.
  if (view !== "handlers" || session !== "live" || handlers === null) return null;

  return (
    <section className="card" data-island="supervision">
      <h2>Handlers</h2>
      {handlers.handlers.length === 0 ? (
        <p className="muted">No handlers registered.</p>
      ) : (
        <table>
          <thead>
            <tr><th>event</th><th>handler</th><th>fail</th><th>gen</th><th>state</th><th>child</th></tr>
          </thead>
          <tbody>
            {handlers.handlers.map((h) => (
              <tr
                key={`${h.event}/${h.name}`}
                data-handler={h.name}
                data-dead={h.dead}
                data-child-state={h.childState ?? undefined}
              >
                <td>{h.event}</td>
                <td>{h.name}</td>
                <td>{h.failMode}</td>
                <td>{h.generation}</td>
                <td className={h.dead ? "bad" : "ok"}>{h.dead ? "dead" : "live"}</td>
                <td className={childClass(h.childState)}>
                  {h.childState
                    ? `${h.childState}${h.childPid ? ` (${h.childPid})` : ""}`
                    : "—"}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}

      <h3>handlers.json</h3>
      {/* Phase 8's read view, grown into ADR-0011's editor — the expected
          table now carries install/edit/uninstall + the enable toggle, every
          write behind the verbatim confirm. */}
      <HandlersSection dto={handlers} />
    </section>
  );
}

function childClass(state: string | null): string {
  if (state === "ready") return "ok";
  if (state === "failed") return "bad";
  return "muted"; // spawning, or no resident child
}
