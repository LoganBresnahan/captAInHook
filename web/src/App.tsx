import { useEffect, useState } from "react";
import { apiFetch, clearToken } from "./auth.ts";
import type { StatusDto } from "./api.gen.ts";

// Scaffold shell. Real screens — Live trace, Supervision, Policy, Harnesses,
// Status — arrive as islands in later slices (ADR-0008 d1/d8). What lives here
// already, because it IS this slice: the session states of decision 4 —
// no-session (inert shell, say how to get one), live (the bearer works), and
// dead-credential (401/403 ⇒ stop, tell the user to re-run `captainHook ui`;
// the browser cannot re-read api.json, so there is no self-heal to attempt).
type Session = "none" | "checking" | "live" | "dead";

export function App({ hasSession }: { hasSession: boolean }) {
  const [session, setSession] = useState<Session>(hasSession ? "checking" : "none");
  const [status, setStatus] = useState<StatusDto | null>(null);

  useEffect(() => {
    if (!hasSession) return;
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
  }, [hasSession]);

  return (
    <main>
      <h1>captAInHook</h1>
      {session === "none" && (
        <p>
          No session — launch with <code>captainHook ui</code> (it reads the
          daemon's credential and opens this page with a one-time token).
        </p>
      )}
      {session === "checking" && <p>Connecting…</p>}
      {session === "dead" && (
        <p>
          Session ended — the daemon was replaced or restarted. Re-run{" "}
          <code>captainHook ui</code> for a fresh session.
        </p>
      )}
      {session === "live" && status && (
        <p data-session="live">
          Connected to captaind {status.version} (pid {status.pid}) — up{" "}
          {Math.round(status.uptimeMs / 1000)}s, {status.served} hooks served.
          Screens land in later slices.
        </p>
      )}
    </main>
  );
}
