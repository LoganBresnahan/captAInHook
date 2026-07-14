import { useStore } from "./store.ts";
import { useApiJson } from "./api.ts";
import type { HandlersDto } from "./api.gen.ts";

// The Supervision island (ADR-0008 d1; ADR-0010 d8): every registered handler
// with its fail mode, live supervision state (generation = restart count, dead
// = escalated past its budget), and — for a resident exec handler — its live
// CHILD state (spawning/ready/failed + pid). Below the live table, the
// handlers.json EXPECTED-vs-REGISTERED view: the file's declared entries joined
// to what actually registered, so a warn-and-skip entry shows as skipped (never
// as a live row — the N2 caution) and a malformed file is loud. Polled every 4s.
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
      <ExpectedView dto={handlers} />
    </section>
  );
}

function childClass(state: string | null): string {
  if (state === "ready") return "ok";
  if (state === "failed") return "bad";
  return "muted"; // spawning, or no resident child
}

function ExpectedView({ dto }: { dto: HandlersDto }) {
  if (dto.source === "absent") {
    return (
      <p className="muted" data-file-state="absent">
        No handlers file — zero exec handlers ({dto.path ?? "default path"}).
      </p>
    );
  }
  if (dto.source === "malformed") {
    return (
      <p className="bad" data-file-state="malformed">
        Malformed handlers file — zero exec handlers. <code>{dto.error}</code>
      </p>
    );
  }
  return (
    <div data-file-state="loaded">
      {dto.expected.length === 0 ? (
        <p className="muted">Loaded — no entries.</p>
      ) : (
        <table>
          <thead>
            <tr><th>entry</th><th>events</th><th>mode</th><th>fail</th><th>registered</th></tr>
          </thead>
          <tbody>
            {dto.expected.map((e, i) => (
              <tr key={`${e.name}/${i}`} data-expected={e.name} data-registered={e.registered}>
                <td>{e.name}</td>
                <td>{e.events.join(", ")}</td>
                <td>{e.mode ?? "—"}</td>
                <td>{e.failMode ?? "—"}</td>
                <td className={e.registered ? "ok" : "bad"}>
                  {e.registered ? "registered" : "skipped"}
                  {e.skipReason ? <span className="muted"> — {e.skipReason}</span> : null}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  );
}
