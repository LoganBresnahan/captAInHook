import { useEffect } from "react";
import { apiFetch, clearToken } from "./auth.ts";
import { useStore, VIEWS, VIEW_LABELS } from "./store.ts";
import type { StatusDto } from "./api.gen.ts";

// The nav island (ADR-0008 d8, restructured by ADR-0015 d1): the persistent
// left sidebar — brand, the five view buttons, and the session line — and still
// the OWNER of the session lifecycle: the one fetch that turns `checking` into
// `live` (the bearer works) or `dead` (401/403 — a cutover rotated the token;
// ADR-0008 d4's no-self-heal, because the browser cannot re-read the 0600
// api.json). It seeds the store's `status` too, so the panels have a first
// value the instant they mount.
//
// Navigation is one store write. Everything the daemon SHOWS lives in its own
// island (Trace, Handlers, Policy, Harnesses, Status), each of which renders
// null unless `view` names it — so this component never knows what a screen
// contains, only which one is on.
export function Nav() {
  const view = useStore((s) => s.view);
  const setView = useStore((s) => s.setView);
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
    <aside className="sidebar">
      <div className="brand">
        capt<span className="brand-ai">AI</span>nHook
      </div>

      <nav className="nav" aria-label="Views">
        {VIEWS.map((v) => (
          <button
            key={v}
            type="button"
            className="nav-item"
            data-nav={v}
            aria-current={view === v ? "page" : undefined}
            onClick={() => setView(v)}
          >
            {VIEW_LABELS[v]}
          </button>
        ))}
      </nav>

      <div className="sidebar-foot">
        {/* The rail states the session TERSELY; the actionable instruction
            lives in the view region (SessionNotice), where a bookmark visit
            actually looks. A 210px rail is the wrong home for a paragraph. */}
        <p className="session-line" data-session={session}>
          {session === "none" && <>No session</>}
          {session === "checking" && <>Connecting…</>}
          {session === "dead" && <>Session ended</>}
          {session === "live" && (
            <span className={`conn conn-${stream}`}>
              <span className="conn-dot" aria-hidden="true" />
              Connected{stream === "retrying" ? " · reconnecting…" : ""}
            </span>
          )}
        </p>
      </div>
    </aside>
  );
}

// The view region's session state (ADR-0015 d1): with a sidebar, every screen
// island renders null when the session isn't live — which would leave a
// bookmark visit staring at an empty page beside a nav that does nothing. This
// island fills that region instead, and it is where the launch instruction
// belongs: the one thing a credential-less visitor can act on.
export function SessionNotice() {
  const session = useStore((s) => s.session);
  if (session === "live") return null;

  return (
    <section className="card empty-state" data-notice-session={session}>
      {session === "none" && (
        <>
          <h2>No session</h2>
          <p>
            This page is served without a credential and holds none. Launch it with{" "}
            <code>captainHook ui</code> — the verb reads the daemon's 0600 <code>api.json</code>{" "}
            and opens this address with a one-time token in the fragment.
          </p>
        </>
      )}
      {session === "checking" && (
        <>
          <h2>Connecting…</h2>
          <p>Checking the token against the daemon.</p>
        </>
      )}
      {session === "dead" && (
        <>
          <h2>Session ended</h2>
          <p>
            The daemon was replaced or restarted, so this token no longer opens
            anything. Re-run <code>captainHook ui</code> for a fresh session — the
            browser cannot read the new credential on its own.
          </p>
        </>
      )}
    </section>
  );
}
