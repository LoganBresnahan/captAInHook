import { useEffect } from "react";
import { apiFetch, clearToken } from "./auth.ts";
import { useStore } from "./store.ts";
import type { StatusDto } from "./api.gen.ts";

// Scaffold shell — now the store's first consumer (decision 8 proven end to
// end: state lives outside the tree, this island subscribes to the slices it
// reads and mutates nothing directly). Real screens — Live trace, Supervision,
// Policy, Harnesses, Status — arrive as sibling islands in later slices. The
// session states are decision 4's: no-session (inert shell, say how to get
// one), live, and dead-credential (401/403 ⇒ stop, re-run `captainHook ui`;
// the browser cannot re-read api.json, so there is no self-heal to attempt).
export function App() {
  const session = useStore((s) => s.session);
  const status = useStore((s) => s.status);
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
