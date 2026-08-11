import { useEffect, useRef, useState } from "react";
import { apiFetch } from "./auth.ts";
import { useStore } from "./store.ts";
import type { HandlersDto } from "./api.gen.ts";
import {
  type ExecEntry, parseEntries, serializeEntries, upsertEntry, removeEntry,
  formProblems, submitHandlers, disabledState, togglePolicyText, wiringHint,
} from "./handlers.ts";

// The handlers.json section, grown from phase 8's read-only expected view into
// ADR-0011's editor: install / edit / uninstall as a whole-file
// read-modify-write over GET+ETag / PUT+If-Match (d3), every write gated by
// the VERBATIM-AND-RESOLVED confirm (d2 — the modal IS the trust surface),
// enable/disable composed from dispatch.json rules through the existing
// PUT /policy (d4), and the shown-never-written wiring hint (d5).
//
// The 412 discipline here INVERTS PolicyPanel's pin 3 — see handlers.ts. On
// conflict: the stale compose is DISCARDED, the fresh file re-fetched, and the
// user's DRAFT preserved for them to re-review over what is actually there.

type FormState = { entry: ExecEntry; originalName: string | null };
type ConfirmState =
  | { kind: "install"; entry: ExecEntry; originalName: string | null }
  | { kind: "remove"; name: string };
type Notice =
  | { kind: "saved" }
  | { kind: "conflict" }
  | { kind: "error"; detail: string };

const EMPTY_ENTRY: ExecEntry = {
  name: "", command: "", args: [], events: [], mode: "oneshot", failMode: "open",
};

export function HandlersSection({ dto }: { dto: HandlersDto }) {
  const setHandlers = useStore((s) => s.setHandlers);
  const policy = useStore((s) => s.policy);
  const setPolicy = useStore((s) => s.setPolicy);
  const status = useStore((s) => s.status);
  const harnesses = useStore((s) => s.harnesses);

  const [form, setForm] = useState<FormState | null>(null);
  const [confirm, setConfirm] = useState<ConfirmState | null>(null);
  const [violations, setViolations] = useState<string[]>([]);
  const [notice, setNotice] = useState<Notice | null>(null);
  const [busy, setBusy] = useState(false);
  const [toggleErr, setToggleErr] = useState<string | null>(null);

  // The claude-code spec's event vocabulary + install block (data, not code).
  const spec = harnesses?.harnesses.find((h) => h.name === "claude-code")
    ?? harnesses?.harnesses[0] ?? null;
  const knownEvents = spec ? Object.keys(spec.events) : [];

  // The composable gate requires the raw to actually PARSE, not just the
  // server's source flag: the read model's source and raw come from two
  // separate reads, so a hand edit racing the GET can pair source:"loaded"
  // with unparseable raw — composing over that would replace content we never
  // showed (adversarial-verify F5). Absent is composable (a create); loaded
  // is composable only over entries we can faithfully re-serialize.
  const parsed = parseEntries(dto.raw ?? null);
  const entries = parsed ?? [];
  const composable = dto.source === "absent" || (dto.source === "loaded" && parsed !== null);

  const refetch = async () => {
    try {
      const resp = await apiFetch("/api/v1/handlers");
      if (resp.ok) setHandlers((await resp.json()) as HandlersDto);
    } catch { /* the 4s poll converges */ }
  };

  // The one write path — compose over the CURRENT dto at confirm time, send
  // its etag, and obey the verdict. On conflict the compose is dead: re-GET,
  // keep the draft, make the user re-review over the fresh file (never
  // adopt-and-retry — the lost-update inversion).
  const executeConfirmed = async (c: ConfirmState) => {
    setBusy(true);
    setViolations([]);
    try {
      const next = c.kind === "install"
        ? upsertEntry(entries, c.entry, c.originalName)
        : removeEntry(entries, c.name);
      const verdict = await submitHandlers(apiFetch, serializeEntries(next), dto.etag ?? null);
      switch (verdict.kind) {
        case "written":
          if (verdict.dto) setHandlers(verdict.dto);   // the echo IS the fresh read
          setConfirm(null);
          setForm(null);
          setNotice({ kind: "saved" });
          break;
        case "invalid":
          setViolations(verdict.violations);           // shown inside the confirm modal
          break;
        case "conflict":
          setConfirm(null);                            // the stale compose is discarded
          setNotice({ kind: "conflict" });             // draft (form) preserved for re-review
          await refetch();
          break;
        case "failed":
          setConfirm(null);
          setNotice({ kind: "error", detail: verdict.detail });
          break;
      }
    } finally {
      setBusy(false);
    }
  };

  // d4: the toggle writes dispatch.json through PUT /policy — fresh GET for
  // raw+etag per toggle (no cross-island etag sharing), compose, conditional
  // PUT. A 412 here is surfaced and retried by the USER; nothing composes
  // over stale policy.
  const toggle = async (name: string, disable: boolean) => {
    setToggleErr(null);
    setBusy(true);
    try {
      const resp = await apiFetch("/api/v1/policy");
      if (!resp.ok) { setToggleErr(`policy read failed (HTTP ${resp.status})`); return; }
      const pdto = (await resp.json()) as { raw: string | null; etag: string | null; state: string };
      if (pdto.state === "malformed") {
        setToggleErr("dispatch.json is malformed — fix it in the policy editor first");
        return;
      }
      const text = togglePolicyText(pdto.raw, name, disable);
      if (text === null) { setToggleErr("could not compose over the current policy file"); return; }
      const put = await apiFetch("/api/v1/policy", {
        method: "PUT",
        headers: {
          "Content-Type": "application/json",
          ...(pdto.etag !== null ? { "If-Match": pdto.etag } : {}),
        },
        body: text,
      });
      if (put.status === 200) setPolicy(await put.json());
      else if (put.status === 412) setToggleErr("dispatch.json changed concurrently — try the toggle again");
      else setToggleErr(`policy write failed (HTTP ${put.status})`);
    } catch (e) {
      setToggleErr(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };

  return (
    <div data-editor="handlers">
      <FileState dto={dto} />
      {dto.source === "loaded" && dto.expected.length > 0 && (
        <table>
          <thead>
            <tr><th>entry</th><th>events</th><th>mode</th><th>fail</th><th>registered</th><th>enabled</th><th></th></tr>
          </thead>
          <tbody>
            {dto.expected.map((e, i) => {
              const state = disabledState(policy?.raw ?? null, e.name);
              const entry = entries.find((x) => x.name === e.name) ?? null;
              return (
                <tr key={`${e.name}/${i}`} data-expected={e.name} data-registered={e.registered}
                    data-enabled-state={state}>
                  <td>{e.name}</td>
                  <td>{e.events.join(", ")}</td>
                  <td>{e.mode ?? "—"}</td>
                  <td>{e.failMode ?? "—"}</td>
                  {/* Three honest states: registered (live worker), skipped
                      (warn-and-skip, violations shown), or PENDING — a valid
                      entry whose reconcile hasn't run yet (registration rides
                      the per-dispatch stat-gate, so an API install is live on
                      the NEXT hook, and this row says so instead of reading
                      like a fault). */}
                  <td className={e.registered ? "ok" : e.skipReason ? "bad" : "muted"}>
                    {e.registered ? "registered"
                      : e.skipReason ? <>skipped<span className="muted"> — {e.skipReason}</span></>
                      : "pending (live on the next hook)"}
                  </td>
                  <td>
                    {policy?.state === "malformed"
                      // F3: a malformed policy means the daemon denies ALL —
                      // an on/off badge computed over unreadable rules would
                      // lie in either direction. Say so instead.
                      ? <span className="muted" data-toggle-unavailable title="dispatch.json is malformed — the daemon is denying everything; fix it in the policy editor">—</span>
                      : state === "scoped"
                      ? <span className="muted" title="scoped dispatch.json rules mention this handler first — enablement depends on event/project/session; no clean off-switch">scoped</span>
                      : (
                        <button
                          data-toggle={e.name}
                          disabled={busy}
                          onClick={() => void toggle(e.name, state === "enabled")}
                        >
                          {state === "disabled" ? "off" : "on"}
                        </button>
                      )}
                  </td>
                  <td>
                    {entry !== null && (
                      <>
                        <button data-edit={e.name} disabled={busy}
                                onClick={() => { setNotice(null); setForm({ entry, originalName: e.name }); }}>
                          edit
                        </button>{" "}
                        <button data-remove={e.name} disabled={busy}
                                onClick={() => { setNotice(null); setConfirm({ kind: "remove", name: e.name }); }}>
                          ✕
                        </button>
                      </>
                    )}
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      )}
      {composable ? (
        <p>
          <button data-install disabled={busy}
                  onClick={() => { setNotice(null); setForm({ entry: EMPTY_ENTRY, originalName: null }); }}>
            + install
          </button>
        </p>
      ) : dto.source === "loaded" && (
        <p className="bad" data-editor-uncomposable>
          The file on disk contains entries this editor cannot faithfully
          re-serialize — editing is disabled so a GUI write never drops them.
          Edit the file by hand.
        </p>
      )}

      {notice?.kind === "saved" && <p className="ok" data-notice="saved">Saved — live on the next hook (hot reload).</p>}
      {notice?.kind === "conflict" && (
        <p className="bad" data-notice="conflict">
          handlers.json changed while you were editing (another tab, or a hand
          edit). Nothing was written; the file has been reloaded — please
          re-review your change against it.
        </p>
      )}
      {notice?.kind === "error" && <p className="bad" data-notice="error">Write failed: {notice.detail}</p>}
      {toggleErr && <p className="bad" data-notice="toggle-error">{toggleErr}</p>}

      {form !== null && (
        <EntryForm
          form={form}
          knownEvents={knownEvents}
          busy={busy}
          onCancel={() => setForm(null)}
          onReview={(entry) => { setViolations([]); setConfirm({ kind: "install", entry, originalName: form.originalName }); }}
        />
      )}

      {confirm !== null && (
        <ConfirmModal
          confirm={confirm}
          entries={entries}
          violations={violations}
          busy={busy}
          shimPath={status?.shimPath ?? null}
          install={spec?.install ?? null}
          onCancel={() => { setConfirm(null); setViolations([]); }}
          onConfirm={() => void executeConfirmed(confirm)}
        />
      )}
    </div>
  );
}

function FileState({ dto }: { dto: HandlersDto }) {
  if (dto.source === "absent") {
    return (
      <p className="muted" data-file-state="absent">
        No handlers file — zero exec handlers ({dto.path ?? "default path"}). Installing creates it.
      </p>
    );
  }
  if (dto.source === "malformed") {
    return (
      <p className="bad" data-file-state="malformed">
        Malformed handlers file — zero exec handlers, and the editor is
        disabled (a GUI write would replace content it cannot read; fix the
        file by hand). <code>{dto.error}</code>
      </p>
    );
  }
  return dto.expected.length === 0
    ? <p className="muted" data-file-state="loaded">Loaded — no entries.</p>
    : <span data-file-state="loaded" />;
}

// ---- the entry form ---------------------------------------------------------

function EntryForm({ form, knownEvents, busy, onCancel, onReview }: {
  form: FormState;
  knownEvents: string[];
  busy: boolean;
  onCancel: () => void;
  onReview: (entry: ExecEntry) => void;
}) {
  const [e, setE] = useState<ExecEntry>(form.entry);
  const [touched, setTouched] = useState(false);
  const problems = formProblems(e);
  const set = (patch: Partial<ExecEntry>) => { setTouched(true); setE({ ...e, ...patch }); };
  const events = knownEvents.length > 0 ? knownEvents
    : ["SessionStart", "UserPromptSubmit", "PreToolUse", "PostToolUse", "Stop", "SessionEnd"];

  return (
    <div className="entry-form" data-form={form.originalName ?? "new"}>
      <h4>{form.originalName === null ? "Install handler" : `Edit ${form.originalName}`}</h4>
      <div className="entry-form-grid">
      <label>name <input value={e.name} data-field="name"
        onChange={(ev) => set({ name: ev.target.value })} /></label>
      <label>command <input value={e.command} data-field="command" placeholder="/absolute/path/to/payload"
        onChange={(ev) => set({ command: ev.target.value })} /></label>
      <label className="wide">args (one per line) <textarea rows={2} data-field="args"
        value={(e.args ?? []).join("\n")}
        onChange={(ev) => set({ args: ev.target.value.length === 0 ? [] : ev.target.value.split("\n") })} /></label>
      <fieldset className="wide" data-field="events">
        <legend>events</legend>
        {events.map((name) => (
          <label key={name} className="event-check">
            <input type="checkbox" checked={e.events.includes(name)}
              onChange={(ev) => set({
                events: ev.target.checked
                  ? [...e.events, name]
                  : e.events.filter((x) => x !== name),
              })} />
            {name}
          </label>
        ))}
      </fieldset>
      <label>mode{" "}
        <select value={e.mode ?? "oneshot"} data-field="mode"
          onChange={(ev) => set({ mode: ev.target.value as ExecEntry["mode"] })}>
          <option value="oneshot">oneshot</option>
          <option value="resident">resident</option>
        </select>
      </label>
      <label>failMode{" "}
        <select value={e.failMode ?? "open"} data-field="failMode"
          onChange={(ev) => set({ failMode: ev.target.value as ExecEntry["failMode"] })}>
          <option value="open">open</option>
          <option value="closed">closed</option>
        </select>
      </label>
      <label>budgetMs (blank = per-event default) <input value={e.budgetMs ?? ""} data-field="budgetMs"
        inputMode="numeric"
        onChange={(ev) => set({ budgetMs: ev.target.value === "" ? undefined : Number(ev.target.value) })} /></label>
      {/* Resident-only, and the form said nothing about it until ADR-0015
          slice 4: a resident child must announce {"ready":1} within this window
          or the daemon gives up on it (ADR-0010). `ExecEntry` already carried
          the field, so a hand-written value round-tripped through an edit — it
          just could not be SET here. Rendered for both modes rather than hidden
          behind `mode`, so an existing value is never invisible. */}
      <label>readinessTimeoutMs (resident only; blank = default) <input
        value={e.readinessTimeoutMs ?? ""} data-field="readinessTimeoutMs"
        inputMode="numeric"
        onChange={(ev) => set({ readinessTimeoutMs: ev.target.value === "" ? undefined : Number(ev.target.value) })} /></label>
      <label>cwd (blank = event cwd) <input value={e.cwd ?? ""} data-field="cwd"
        onChange={(ev) => set({ cwd: ev.target.value === "" ? undefined : ev.target.value })} /></label>
      <label className="wide">env (KEY=VALUE, one per line) <textarea rows={2} data-field="env"
        value={Object.entries(e.env ?? {}).map(([k, v]) => `${k}=${v}`).join("\n")}
        onChange={(ev) => {
          const env: Record<string, string> = {};
          for (const line of ev.target.value.split("\n")) {
            if (line.length === 0) continue;
            const i = line.indexOf("=");
            if (i > 0) env[line.slice(0, i)] = line.slice(i + 1);
          }
          set({ env: Object.keys(env).length === 0 ? undefined : env });
        }} /></label>
      <label>passEnv (comma-separated) <input data-field="passEnv"
        value={(e.passEnv ?? []).join(",")}
        onChange={(ev) => set({
          passEnv: ev.target.value === "" ? undefined
            : ev.target.value.split(",").map((s) => s.trim()).filter(Boolean),
        })} /></label>
      </div>
      {touched && problems.length > 0 && (
        <ul className="bad" data-form-problems>
          {problems.map((p, i) => <li key={i}>{p}</li>)}
        </ul>
      )}
      <div className="form-actions">
        <button data-review className="btn-primary" disabled={busy || problems.length > 0} onClick={() => onReview(e)}>
          Review &amp; {form.originalName === null ? "install" : "save"}
        </button>{" "}
        <button onClick={onCancel} disabled={busy}>Cancel</button>
      </div>
    </div>
  );
}

// ---- the verbatim confirm (d2 — the trust surface) --------------------------

function ConfirmModal({ confirm, entries, violations, busy, shimPath, install, onCancel, onConfirm }: {
  confirm: ConfirmState;
  entries: ExecEntry[];
  violations: string[];
  busy: boolean;
  shimPath: string | null;
  install: unknown;
  onCancel: () => void;
  onConfirm: () => void;
}) {
  const entry = confirm.kind === "install"
    ? confirm.entry
    : entries.find((e) => e.name === confirm.name) ?? null;

  const dialog = useRef<HTMLDivElement>(null);

  // Modal accessibility, hand-rolled — no dependency (ADR-0015 N4). This dialog
  // is the ADR-0011 trust surface: the one screen where a user says "yes, run
  // this process as me". A trust surface a keyboard user cannot escape from, or
  // can tab out of behind the backdrop while it still looks modal, is not one.
  // Three obligations: focus moves IN on open, Tab cycles WITHIN, and focus
  // goes back where it came from on close (so the row's edit/✕ button is still
  // where the user left it).
  useEffect(() => {
    const opener = document.activeElement as HTMLElement | null;
    const focusables = () => [
      ...(dialog.current?.querySelectorAll<HTMLElement>(
        'button:not([disabled]), [href], input:not([disabled]), select, textarea, summary, [tabindex]:not([tabindex="-1"])',
      ) ?? []),
    ];
    focusables()[0]?.focus() ?? dialog.current?.focus();

    const onKey = (ev: KeyboardEvent) => {
      if (ev.key === "Escape") {
        ev.preventDefault();
        onCancel();
        return;
      }
      if (ev.key !== "Tab") return;
      const f = focusables();
      if (f.length === 0) return;
      const first = f[0], last = f[f.length - 1];
      const active = document.activeElement;
      // Wrap at both ends, and pull focus back in if it has escaped the dialog
      // entirely (a click on the backdrop, a stray programmatic focus).
      if (!dialog.current?.contains(active)) {
        ev.preventDefault();
        (ev.shiftKey ? last : first).focus();
      } else if (ev.shiftKey && active === first) {
        ev.preventDefault();
        last.focus();
      } else if (!ev.shiftKey && active === last) {
        ev.preventDefault();
        first.focus();
      }
    };
    document.addEventListener("keydown", onKey);
    return () => {
      document.removeEventListener("keydown", onKey);
      opener?.focus?.();
    };
  }, [onCancel]);

  return (
    <div className="modal-backdrop" data-confirm={confirm.kind}>
      <div className="modal" role="dialog" aria-modal="true" ref={dialog} tabIndex={-1}>
        {confirm.kind === "install" ? (
          <>
            <h4>Confirm: {confirm.originalName === null ? "install" : "save"} {confirm.entry.name}</h4>
            <p>
              This exact process will run <strong>as you</strong> on every{" "}
              {confirm.entry.events.join(" / ")} hook:
            </p>
          </>
        ) : (
          <h4>Confirm: uninstall {confirm.name}</h4>
        )}
        {entry !== null && <VerbatimEntry entry={entry} />}
        {confirm.kind === "install" && (
          <p className="muted">
            Read the script before confirming — captAInHook shows the entry,
            not the file&apos;s contents.
          </p>
        )}
        {confirm.kind === "install" && (
          <WiringHints entry={confirm.entry} shimPath={shimPath} install={install} />
        )}
        {violations.length > 0 && (
          <div className="bad" data-confirm-violations>
            <p>The daemon refused it (nothing was written):</p>
            <ul>{violations.map((v, i) => <li key={i}><code>{v}</code></li>)}</ul>
          </div>
        )}
        <div>
          <button onClick={onCancel} disabled={busy}>Cancel</button>{" "}
          <button data-confirm-go className="btn-primary" disabled={busy} onClick={onConfirm}>
            {busy ? "Writing…" : confirm.kind === "remove" ? "Uninstall" : "Install"}
          </button>
        </div>
      </div>
    </div>
  );
}

// Byte-faithful render of the entry that will be WRITTEN: the exact JSON,
// plus a resolved field table. No summarizing, no re-wording (d2 calls this
// render "the whole trust surface"; a dishonest render defeats the ADR).
function VerbatimEntry({ entry }: { entry: ExecEntry }) {
  return (
    <>
      <dl className="verbatim" data-verbatim>
        <dt>command</dt><dd><code data-v="command">{entry.command}</code></dd>
        <dt>args</dt><dd><code data-v="args">{(entry.args ?? []).map((a) => JSON.stringify(a)).join(" ") || "—"}</code></dd>
        <dt>events</dt><dd data-v="events">{entry.events.join(", ")}</dd>
        <dt>mode</dt><dd data-v="mode">{entry.mode ?? "oneshot"}</dd>
        <dt>failMode</dt><dd data-v="failMode">{entry.failMode ?? "open"}</dd>
        <dt>budget</dt><dd data-v="budget">{entry.budgetMs !== undefined ? `${entry.budgetMs}ms` : "per-event default"}</dd>
        {(entry.mode === "resident" || entry.readinessTimeoutMs !== undefined) && (
          <>
            <dt>readiness</dt>
            <dd data-v="readinessTimeoutMs">
              {entry.readinessTimeoutMs !== undefined ? `${entry.readinessTimeoutMs}ms` : "default"}
            </dd>
          </>
        )}
        <dt>cwd</dt><dd data-v="cwd">{entry.cwd ?? "(event cwd)"}</dd>
        <dt>env</dt><dd data-v="env">
          fixed allowlist{Object.entries(entry.env ?? {}).length > 0
            ? ` + ${Object.entries(entry.env ?? {}).map(([k, v]) => `${k}=${v}`).join(", ")}` : ""}
        </dd>
        <dt>passEnv</dt><dd data-v="passEnv">{(entry.passEnv ?? []).join(", ") || "(none)"}</dd>
      </dl>
      <details>
        <summary>exact JSON to be written for this entry</summary>
        <pre data-verbatim-json>{JSON.stringify(entry, null, 2)}</pre>
      </details>
    </>
  );
}

// d5: the shown-never-written wiring line, one per selected event, rendered
// from the harness spec's install template ({captainShim} → /status's resolved
// shim). Informational — the GUI cannot know whether settings.json already
// wires the event (N3), so the hint states the requirement, not a diagnosis.
function WiringHints({ entry, shimPath, install }: {
  entry: ExecEntry; shimPath: string | null; install: unknown;
}) {
  const hints = entry.events
    .map((ev) => ({ ev, hint: wiringHint(install, shimPath, ev) }))
    .filter((h) => h.hint !== null);
  if (hints.length === 0) return null;
  return (
    <div className="muted" data-wiring-hint>
      <p>
        Each event must be wired in {hints[0].hint!.configFile} for this handler
        to ever fire — if it isn&apos;t yet, add the hook command:
      </p>
      <ul>
        {hints.map(({ ev, hint }) => (
          <li key={ev}>
            {ev}: <code data-wiring-command={ev}>{hint!.command}</code>
          </li>
        ))}
      </ul>
    </div>
  );
}
