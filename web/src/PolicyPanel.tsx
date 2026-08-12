import { useEffect, useRef, useState } from "react";
import { apiFetch } from "./auth.ts";
import { useStore } from "./store.ts";
import {
  submitPolicy, parsePolicyRows, serializePolicyRows,
  EMPTY_ROW, type PolicyRow, type PolicyRows, type PolicyDecision,
} from "./policy.ts";
import { eventVerbs } from "./templates.ts";
import type { PolicyDto } from "./api.gen.ts";

// The Policy island (ADR-0008 decisions 1+8): its own createRoot mount, no
// shared React parent with the shell — coordination happens through the store
// slices it subscribes to (policy, policyVerdict, session). The editor
// surfaces the DAEMON's verdicts (the strict parser's violations, the ETag
// conflict), never its own guesses. Local-only state stays local (the draft
// text, the in-flight If-Match tag — decision 8: props/locals within an
// island are fine; the store is for cross-island truth).
export function PolicyPanel() {
  const view = useStore((s) => s.view);
  const session = useStore((s) => s.session);
  const policy = useStore((s) => s.policy);
  const verdict = useStore((s) => s.policyVerdict);
  const setPolicy = useStore((s) => s.setPolicy);
  const setVerdict = useStore((s) => s.setPolicyVerdict);
  const harnesses = useStore((s) => s.harnesses);

  const [draft, setDraft] = useState<string | null>(null);   // null = untouched, seed from load
  const [rowDraft, setRowDraft] = useState<PolicyRows | null>(null);  // the builder's draft
  const [wantRaw, setWantRaw] = useState(false);             // the user's toggle choice
  const [saving, setSaving] = useState(false);
  // The If-Match discipline's local half: the tag the NEXT PUT will send.
  // Adopted from the load, then from every 200 (its ETag) and every 412 (its
  // `current`) — never re-fetched just to learn it.
  const etagRef = useRef<string | null>(null);

  useEffect(() => {
    if (session !== "live") return;
    apiFetch("/api/v1/policy")
      .then(async (resp) => {
        if (!resp.ok) return;   // shell handles dead sessions; nothing to render here
        const dto = (await resp.json()) as PolicyDto;
        etagRef.current = resp.headers.get("ETag") ?? dto.etag ?? null;
        setPolicy(dto);
      })
      .catch(() => { /* load failure: panel stays unmounted; the shell owns session errors */ });
  }, [session, setPolicy]);

  // The view gate (ADR-0015 d1), after the hooks. Rendering null does NOT
  // unmount this island — the component stays alive with its local state, so an
  // unsaved draft and the pending If-Match tag survive a trip to another view
  // and come back exactly as they were. (A route swap would have thrown them
  // away; not routing is what makes this free.)
  if (view !== "policy" || session !== "live" || policy === null) return null;

  // What the builder can represent (ADR-0015 d4). A MALFORMED file never
  // reaches the builder: the daemon could not read it, so neither can we with
  // any honesty — and it is exactly the case where a lossy rewrite would be
  // most destructive, since the user is mid-repair. `absent` is representable
  // (there is nothing of theirs to lose).
  const fileRows = policy.state === "malformed" ? null : parsePolicyRows(policy.raw ?? null);
  const locked = fileRows === null;
  const rows = rowDraft ?? fileRows;
  const mode: "builder" | "raw" = locked || wantRaw || rows === null ? "raw" : "builder";

  // The raw text is the builder's own output while the builder is driving, so
  // the toggle never shows something other than what Save would write.
  const text = mode === "builder" && rows !== null
    ? serializePolicyRows(rows)
    : draft ?? policy.raw ?? "{\n  \"version\": 1,\n  \"default\": \"allow\",\n  \"rules\": []\n}\n";

  const editRows = (next: PolicyRows) => { setRowDraft(next); setVerdict(null); };

  const save = async () => {
    setSaving(true);
    setVerdict(null);
    try {
      const r = await submitPolicy(apiFetch, text, etagRef.current);
      setVerdict(r.verdict);
      switch (r.verdict.kind) {
        case "written":
          if (r.verdict.etag !== null) {
            etagRef.current = r.verdict.etag;   // pin 2: adopt, no re-GET
            if (r.policy) setPolicy(r.policy);  // the echo IS the fresh read
            setDraft(null);                      // draft merged; reseed from truth
            setRowDraft(null);
          } else {
            // A 200 with no tag (server-contract breach): the write landed but
            // the next If-Match is unknown — re-seed via GET rather than hold
            // "" or null and write blind next time.
            await loadTheirs();
          }
          break;
        case "mismatch":
          etagRef.current = r.verdict.current;  // pin 3: the retry overwrites a SEEN conflict
          break;                                 // draft preserved — the user decides
      }
    } finally {
      setSaving(false);   // never wedge the Save button, whatever happened
    }
  };

  const loadTheirs = async () => {
    // The mismatch's other exit: discard the draft, re-read the file.
    const resp = await apiFetch("/api/v1/policy");
    if (!resp.ok) return;
    const dto = (await resp.json()) as PolicyDto;
    etagRef.current = resp.headers.get("ETag") ?? dto.etag ?? null;
    setPolicy(dto);
    setDraft(null);
    setRowDraft(null);
    setVerdict(null);
  };

  return (
    <section className="card" data-island="policy">
      <h2>Dispatch policy</h2>
      <p>
        {policy.state === "absent" && <>No policy file — every hook is worked (the zero-config default). Saving creates {policy.path ?? "the file"}.</>}
        {policy.state === "malformed" && <>Malformed policy — the daemon is denying everything, loudly: <code>{policy.error}</code></>}
        {policy.state === "loaded" && <>Loaded — default <code>{policy.policy?.default}</code>, {policy.policy?.rules.length ?? 0} rule(s).</>}
      </p>
      <div className="policy-modes">
        <button
          data-policy-mode="builder"
          aria-pressed={mode === "builder"}
          disabled={locked}
          title={locked ? "This file cannot be edited as rules — see the notice" : undefined}
          onClick={() => setWantRaw(false)}
        >
          Rules
        </button>{" "}
        <button
          data-policy-mode="raw"
          aria-pressed={mode === "raw"}
          onClick={() => {
            // Carry the builder's exact output into the raw editor, so the
            // toggle shows what Save would have written rather than the file's
            // older text.
            if (mode === "builder" && rows !== null) setDraft(serializePolicyRows(rows));
            setWantRaw(true);
          }}
        >
          Raw JSON
        </button>
      </div>

      {locked && (
        <p className="bad" data-policy-locked>
          {policy.state === "malformed"
            ? <>This file is malformed, so the rule builder is locked: the daemon cannot read it, and rebuilding it from rules would replace text we never understood. Fix it here, and the builder returns.</>
            : <>This file uses something the rule builder cannot rebuild faithfully, so it is locked to raw. Nothing here will rewrite it — edit the JSON directly.</>}
        </p>
      )}

      {mode === "builder" && rows !== null ? (
        <RuleBuilder rows={rows} events={Object.keys(eventVerbs(harnesses))} onChange={editRows} />
      ) : (
        <textarea
          aria-label="policy JSON"
          rows={12}
          cols={72}
          spellCheck={false}
          value={text}
          onChange={(e) => setDraft(e.target.value)}
        />
      )}
      <div>
        <button className="btn-primary" onClick={() => void save()} disabled={saving}>
          {saving ? "Saving…" : "Save policy"}
        </button>{" "}
        {(draft !== null || rowDraft !== null) && (
          <button onClick={() => { setDraft(null); setRowDraft(null); }}>Discard draft</button>
        )}
      </div>
      {verdict?.kind === "written" && <p data-verdict="written">Saved — live on the next hook (hot reload).</p>}
      {verdict?.kind === "invalid" && (
        <div data-verdict="invalid">
          <p>The daemon refused it (nothing was written):</p>
          <ul>{verdict.violations.map((v, i) => <li key={i}><code>{v}</code></li>)}</ul>
        </div>
      )}
      {verdict?.kind === "mismatch" && (
        <p data-verdict="mismatch">
          The file changed since you loaded it (an edit elsewhere or another
          tab). Save again to overwrite what you now know is there, or{" "}
          <button onClick={() => void loadTheirs()}>discard yours and load theirs</button>.
        </p>
      )}
      {verdict?.kind === "failed" && <p data-verdict="failed">Write failed: {verdict.detail}</p>}
    </section>
  );
}

// ---------------------------------------------------------------------------
// The rule builder (ADR-0015 d4). Rows compose to exactly the JSON the raw
// toggle shows — there is no second serializer and no hidden state: the rows
// ARE the document while the builder is driving.
//
// Order is load-bearing and therefore visible: evaluation is first-match-wins
// (ADR-0006), so the rows are numbered and can be moved, and the panel says so
// rather than leaving a user to discover it from a doc.
function RuleBuilder({ rows, events, onChange }: {
  rows: PolicyRows;
  events: string[];
  onChange: (next: PolicyRows) => void;
}) {
  const setRow = (i: number, patch: Partial<PolicyRow>) =>
    onChange({ ...rows, rules: rows.rules.map((r, j) => (j === i ? { ...r, ...patch } : r)) });

  const move = (i: number, by: number) => {
    const j = i + by;
    if (j < 0 || j >= rows.rules.length) return;
    const next = [...rows.rules];
    [next[i], next[j]] = [next[j], next[i]];
    onChange({ ...rows, rules: next });
  };

  return (
    <div data-rule-builder>
      <label className="policy-default">
        default (every dispatch no rule matches){" "}
        <select
          data-policy-default
          value={rows.default}
          onChange={(e) => onChange({ ...rows, default: e.target.value as PolicyDecision })}
        >
          <option value="allow">allow</option>
          <option value="deny">deny</option>
        </select>
        {rows.default === "deny" && (
          <span className="warn" data-default-deny-note>
            {" "}— this is the pause: every hook not explicitly allowed is answered with a Noop.
          </span>
        )}
      </label>

      {rows.rules.length === 0 ? (
        <p className="muted" data-no-rules>No rules — the default decides every dispatch.</p>
      ) : (
        <table className="rules">
          <thead>
            <tr>
              <th>#</th><th>event</th><th>handler</th><th>project (path prefix)</th>
              <th>session</th><th>decision</th><th></th>
            </tr>
          </thead>
          <tbody>
            {rows.rules.map((r, i) => (
              <tr key={i} data-rule-row={i}>
                <td className="muted">{i + 1}</td>
                <td>
                  {/* A free-text list rather than a closed select: the harness
                      registry is data and may lag the daemon, and a rule naming
                      an event we have not heard of must stay editable. */}
                  <input
                    data-rule-field="event" data-rule-index={i}
                    list="policy-events" placeholder="(any)"
                    value={r.event} onChange={(e) => setRow(i, { event: e.target.value })}
                  />
                </td>
                <td>
                  <input data-rule-field="handler" data-rule-index={i} placeholder="(any)"
                    value={r.handler} onChange={(e) => setRow(i, { handler: e.target.value })} />
                </td>
                <td>
                  <input data-rule-field="project" data-rule-index={i} placeholder="(any)"
                    value={r.project} onChange={(e) => setRow(i, { project: e.target.value })} />
                </td>
                <td>
                  <input data-rule-field="session" data-rule-index={i} placeholder="(any)"
                    value={r.session} onChange={(e) => setRow(i, { session: e.target.value })} />
                </td>
                <td>
                  <select data-rule-field="decision" data-rule-index={i}
                    value={r.decision} onChange={(e) => setRow(i, { decision: e.target.value as PolicyDecision })}>
                    <option value="allow">allow</option>
                    <option value="deny">deny</option>
                  </select>
                </td>
                <td className="rule-actions">
                  <button data-rule-up={i} disabled={i === 0} title="earlier (first match wins)"
                    onClick={() => move(i, -1)}>↑</button>{" "}
                  <button data-rule-down={i} disabled={i === rows.rules.length - 1} title="later"
                    onClick={() => move(i, 1)}>↓</button>{" "}
                  <button data-rule-remove={i} title="remove this rule"
                    onClick={() => onChange({ ...rows, rules: rows.rules.filter((_, j) => j !== i) })}>✕</button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}

      <datalist id="policy-events">
        {events.map((e) => <option key={e} value={e} />)}
      </datalist>

      <p>
        <button data-rule-add onClick={() => onChange({ ...rows, rules: [...rows.rules, { ...EMPTY_ROW }] })}>
          + rule
        </button>
      </p>
      <p className="muted policy-hint">
        Rules are evaluated in order, first match wins. A rule with no handler
        decides whether the whole dispatch is worked; a rule naming a handler
        excludes just that one. Leave a field blank for “any”.
      </p>
    </div>
  );
}
