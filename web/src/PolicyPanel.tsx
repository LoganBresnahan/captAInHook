import { useEffect, useRef, useState } from "react";
import { apiFetch } from "./auth.ts";
import { useStore } from "./store.ts";
import { submitPolicy } from "./policy.ts";
import type { PolicyDto } from "./api.gen.ts";

// The Policy island (ADR-0008 decisions 1+8): its own createRoot mount, no
// shared React parent with the shell — coordination happens through the store
// slices it subscribes to (policy, policyVerdict, session). The editor
// surfaces the DAEMON's verdicts (the strict parser's violations, the ETag
// conflict), never its own guesses. Local-only state stays local (the draft
// text, the in-flight If-Match tag — decision 8: props/locals within an
// island are fine; the store is for cross-island truth).
export function PolicyPanel() {
  const session = useStore((s) => s.session);
  const policy = useStore((s) => s.policy);
  const verdict = useStore((s) => s.policyVerdict);
  const setPolicy = useStore((s) => s.setPolicy);
  const setVerdict = useStore((s) => s.setPolicyVerdict);

  const [draft, setDraft] = useState<string | null>(null);   // null = untouched, seed from load
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

  if (session !== "live" || policy === null) return null;

  const text = draft ?? policy.raw ?? "{\n  \"version\": 1,\n  \"default\": \"allow\",\n  \"rules\": []\n}\n";

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
    setVerdict(null);
  };

  return (
    <section data-island="policy">
      <h2>Dispatch policy</h2>
      <p>
        {policy.state === "absent" && <>No policy file — every hook is worked (the zero-config default). Saving creates {policy.path ?? "the file"}.</>}
        {policy.state === "malformed" && <>Malformed policy — the daemon is denying everything, loudly: <code>{policy.error}</code></>}
        {policy.state === "loaded" && <>Loaded — default <code>{policy.policy?.default}</code>, {policy.policy?.rules.length ?? 0} rule(s).</>}
      </p>
      <textarea
        aria-label="policy JSON"
        rows={12}
        cols={72}
        spellCheck={false}
        value={text}
        onChange={(e) => setDraft(e.target.value)}
      />
      <div>
        <button onClick={() => void save()} disabled={saving}>
          {saving ? "Saving…" : "Save policy"}
        </button>{" "}
        {draft !== null && <button onClick={() => setDraft(null)}>Discard draft</button>}
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
