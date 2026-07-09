import { useEffect } from "react";
import { apiFetch, clearToken } from "./auth.ts";
import { useStore } from "./store.ts";

// The read panels' shared fetch discipline (ADR-0008 d1: the read islands are
// fetch-and-render over DTOs). One hook so the credential-death handling lives
// in ONE place, not copied into four panels: a 401/403 means the token rotated
// under a cutover (decision 4) — the browser cannot re-read api.json, so the
// whole SESSION dies (which unmounts every panel and stops the stream), not
// just this fetch. Any other error is a transient blip swallowed silently —
// the next poll, or the SSE stream's own reconnect, recovers.
export function useApiJson<T>(
  path: string,
  onData: (data: T) => void,
  intervalMs?: number,
): void {
  const session = useStore((s) => s.session);
  useEffect(() => {
    if (session !== "live") return;
    let alive = true;
    const load = async () => {
      try {
        const resp = await apiFetch(path);
        if (resp.status === 401 || resp.status === 403) {
          clearToken();
          useStore.getState().setSession("dead");
          return;
        }
        if (!resp.ok || !alive) return;
        const data = (await resp.json()) as T;
        if (alive) onData(data);
      } catch {
        /* transient (a blip, a drain): the next poll or the stream recovers */
      }
    };
    void load();
    if (intervalMs === undefined) return () => { alive = false; };
    const t = setInterval(() => void load(), intervalMs);
    return () => { alive = false; clearInterval(t); };
    // onData is a stable store setter; path/intervalMs are literals.
  }, [session, path, onData, intervalMs]);
}
