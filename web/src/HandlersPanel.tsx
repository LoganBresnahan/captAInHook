import { useStore } from "./store.ts";
import { useApiJson } from "./api.ts";
import type { HandlersDto } from "./api.gen.ts";
import { HandlersSection } from "./HandlersEditor.tsx";

// The Handlers view (ADR-0008 d1 + ADR-0010 d8, given its own full-width screen
// by ADR-0015 d6 — the island was called "supervision" while it shared a
// three-across card row, and the table it holds is far too wide for that; the
// overflow defect the ADR opens with was that shape, not this content).
//
// Two sections, in the order an operator asks about them:
//   1. REGISTERED — what the daemon is actually running right now: every
//      registered handler with its fail mode, live supervision state
//      (generation = restart count, dead = escalated past its budget) and, for
//      a resident exec handler, its live child state (spawning/ready/failed +
//      pid). Polled every 4s.
//   2. handlers.json — the file's declared entries joined to what actually
//      registered, so a warn-and-skip entry shows as skipped (never as a live
//      row — the N2 caution) and a malformed file is loud. This is the editor.
//
// The daemon-wide supervision SUMMARY (how many handlers, how many escalated,
// how many restarts) lives on Status instead: it is a health number, not a
// per-handler fact, and Status is where the other health numbers are.
export function HandlersPanel() {
  const view = useStore((s) => s.view);
  const session = useStore((s) => s.session);
  const handlers = useStore((s) => s.handlers);
  const setHandlers = useStore((s) => s.setHandlers);
  useApiJson<HandlersDto>("/api/v1/handlers", setHandlers, 4000);

  // The view gate (ADR-0015 d1), after the hooks so the poll keeps the slice
  // warm while hidden.
  if (view !== "handlers" || session !== "live" || handlers === null) return null;

  return (
    <section className="card" data-island="handlers">
      <h2>Handlers</h2>

      <h3>Registered</h3>
      {handlers.handlers.length === 0 ? (
        <p className="muted">No handlers registered.</p>
      ) : (
        <table className="registered">
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
          table carries install/edit/uninstall + the enable toggle, every
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
