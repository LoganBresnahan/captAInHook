import { useEffect } from "react";
import { apiFetch, clearToken } from "./auth.ts";
import { useStore } from "./store.ts";
import type { StatusDto } from "./api.gen.ts";

// The app shell (ADR-0008 d8): header + the session banner, and the OWNER of
// the session lifecycle — the one fetch that turns `checking` into `live` (the
// bearer works) or `dead` (401/403 — a cutover rotated the token; decision 4's
// no-self-heal, because the browser cannot re-read the 0600 api.json). It seeds
// the store's `status` too, so the panels have a first value the instant they
// mount. Everything ELSE the daemon shows lives in its own island (Status,
// Supervision, Harnesses, Policy, Trace); this is just the chrome around them.
export function App() {
  const session = useStore((s) => s.session);
  const stream = useStore((s) => s.stream);
  const setSession = useStore((s) => s.setSession);
  const setStatus = useStore((s) => s.setStatus);

  useEffect(() => {
    if (session !== "checking") return;
    apiFetch("/api/v1/status")
      .then(async (resp) => {
        if (resp.status === 401 || resp.status === 403) {
          clearToken();
          setSession("dead");
          return;
        }
        setStatus((await resp.json()) as StatusDto);
        setSession("live");
      })
      .catch(() => setSession("dead"));
  }, [session, setSession, setStatus]);

  return (
    <header className="app-header">
      <h1>captAInHook</h1>
      <p className="session-line" data-session={session}>
        {session === "none" && (
          <>No session — launch with <code>captainHook ui</code> (it reads the daemon's credential and opens this page with a one-time token).</>
        )}
        {session === "checking" && <>Connecting…</>}
        {session === "dead" && (
          <>Session ended — the daemon was replaced or restarted. Re-run <code>captainHook ui</code> for a fresh session.</>
        )}
        {session === "live" && (
          <span className={`conn conn-${stream}`}>Connected{stream === "retrying" ? " · reconnecting the stream…" : ""}</span>
        )}
      </p>
    </header>
  );
}
