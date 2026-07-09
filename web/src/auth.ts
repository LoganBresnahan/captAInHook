// token-handoff-bootstrap (ADR-0008 decision 3): `captainHook ui` opens the
// browser at /ui#t=<token> — the FRAGMENT, never a query param, because
// fragments are sent to no server and written to no access log. This module is
// the receiving end, in the ADR's scripted order:
//   1. read the token out of location.hash;
//   2. stash it in sessionStorage (survives a reload, dies with the tab);
//   3. IMMEDIATELY scrub the hash via history.replaceState, so the token never
//      lingers in the URL bar or browser history;
//   4. attach it as `Authorization: Bearer` on every API fetch (apiFetch).
// A fresh hash token beats any stale sessionStorage one (a re-run of
// `captainHook ui` after a daemon cutover must win); neither present ⇒ the
// shell stays inert and the UI says how to get a session (decision 4's
// "re-run `captainHook ui`" line).

const KEY = "captainhook.token";

/** Run once at startup, before anything fetches. Returns the active token, or
 * null when the tab has no session. */
export function bootstrapToken(): string | null {
  const match = /^#t=([0-9a-f]+)$/.exec(location.hash);
  if (match) {
    sessionStorage.setItem(KEY, match[1]);
    // Scrub before anything else can observe or persist the URL.
    history.replaceState(null, "", location.pathname + location.search);
    return match[1];
  }
  return sessionStorage.getItem(KEY);
}

/** The tab's token, if any — reads the stash, never the URL (bootstrapToken
 * owns the URL exactly once, at startup). */
export function currentToken(): string | null {
  return sessionStorage.getItem(KEY);
}

/** Drop the session (a 401/403 told us the credential is dead — a daemon
 * cutover rotated it; the browser cannot re-read api.json, so the only way
 * back is re-running `captainHook ui`). */
export function clearToken(): void {
  sessionStorage.removeItem(KEY);
}

/** Same-origin API fetch with the bearer attached. Throws TypeError on network
 * failure exactly like fetch; authorization failures come back as 401/403
 * responses the caller maps to the dead-credential UX (decision 4). */
export function apiFetch(path: string, init?: RequestInit): Promise<Response> {
  const token = currentToken();
  const headers = new Headers(init?.headers);
  if (token !== null) headers.set("Authorization", `Bearer ${token}`);
  return fetch(path, { ...init, headers });
}
