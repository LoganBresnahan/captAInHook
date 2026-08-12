import { useStore } from "./store.ts";
import { useApiJson } from "./api.ts";
import { verbColumns, verbsLabel, effectLandsOn } from "./templates.ts";
import type { HarnessesDto } from "./api.gen.ts";

// The Harnesses island (ADR-0008 d1): the registry projection (ADR-0003) — each
// known harness spec, its response adapter, and the per-event effect
// capabilities it declares. Essentially static (it changes only on a harness
// hot-reload), so it is fetched ONCE on live, no poll.
//
// Slice 7 turns the capability display into a real MATRIX. The old chips read
// `Stop (0)` — a count, with the actual verbs hidden in a hover title, which
// answered no question anyone has: nobody wants to know how MANY effects an
// event permits, they want to know WHETHER the one they are about to write
// lands there. Events are rows, the declared verbs are columns, and a cell says
// yes or no. `Stop (0)` becomes the sentence it always meant: no loop effects.
export function HarnessesPanel() {
  const view = useStore((s) => s.view);
  const session = useStore((s) => s.session);
  const harnesses = useStore((s) => s.harnesses);
  const setHarnesses = useStore((s) => s.setHarnesses);
  useApiJson<HarnessesDto>("/api/v1/harnesses", setHarnesses);

  // The view gate (ADR-0015 d1), after the hooks.
  if (view !== "harnesses" || session !== "live" || harnesses === null) return null;

  return (
    <section className="card" data-island="harnesses">
      <h2>Harnesses</h2>
      <p className="muted panel-lede">
        What each harness lets a payload DO, per lifecycle event — the registry's
        own declaration (ADR-0003), not a guess. An event with no loop effects
        can still run a payload; it just cannot change the turn.
      </p>
      <ul className="harnesses">
        {harnesses.harnesses.map((h) => (
          <li key={h.name} data-harness={h.name}>
            <div className="harness-head">
              <strong>{h.name}</strong>
              <span className="event-cap" data-harness-adapter={h.responseAdapter}>
                {h.responseAdapter}
              </span>
            </div>
            <EffectMatrix events={h.events} />
          </li>
        ))}
      </ul>
    </section>
  );
}

// Rows = events, columns = every verb this harness declares anywhere. The
// columns are DERIVED from the data (`verbColumns`), so a harness declaring a
// verb this build has never heard of gets a column rather than a silent blank —
// the same "declare in data, look up in code" rule the registry itself follows.
//
// A permitted cell is marked with a glyph, not with color alone: the accent
// hue is the secondary channel (and it is the accent, not the reserved
// ok/warn/bad status palette — "this verb is permitted" is a capability, not a
// health state, and borrowing the health colors here would cheapen them where
// they do mean something, two panels over on Status).
function EffectMatrix({ events }: { events: Record<string, string[]> }) {
  const columns = verbColumns(events);
  const rows = Object.keys(events);

  // A harness that declares no verbs at all anywhere: there is no matrix to
  // draw, so say so rather than render a table of one empty column.
  if (columns.length === 0) {
    return (
      <p className="muted" data-no-verbs>
        This harness declares no loop effects on any event.
      </p>
    );
  }

  return (
    <table className="matrix" data-effect-matrix>
      <thead>
        <tr>
          <th>event</th>
          {columns.map((verb) => <th key={verb} data-verb-column={verb}>{verb}</th>)}
        </tr>
      </thead>
      <tbody>
        {rows.map((event) => {
          const declared = events[event] ?? [];
          return (
            <tr key={event} data-event-row={event}>
              <th scope="row">{event}</th>
              {declared.length === 0 ? (
                // The row that used to read `(0)`. One spanning cell carrying the
                // shared label, because an all-blank row is ambiguous — it looks
                // like missing data rather than a deliberate "nothing lands here".
                <td className="muted" colSpan={columns.length} data-no-effects>
                  {verbsLabel(events, event)}
                </td>
              ) : (
                columns.map((verb) => {
                  const on = effectLandsOn(events, event, verb);
                  return (
                    <td key={verb} data-cell={`${event}:${verb}`} data-on={on}>
                      <span aria-hidden="true">{on ? "✓" : "·"}</span>
                      <span className="sr-only">{on ? "permitted" : "not permitted"}</span>
                    </td>
                  );
                })
              )}
            </tr>
          );
        })}
      </tbody>
    </table>
  );
}
