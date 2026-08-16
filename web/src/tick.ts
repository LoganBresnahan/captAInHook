import { useEffect, useState } from "react";

/** Re-render on a cadence while `active` — for labels that describe elapsed
 * time ("last frame 3 s ago") over MONOTONIC stamps that nothing else would
 * ever refresh. Off when the island is hidden: an invisible label earns no
 * timer. Returns a counter only so callers have a dependency to hang on. */
export function useTick(ms: number, active: boolean): number {
  const [n, setN] = useState(0);
  useEffect(() => {
    if (!active) return;
    const t = setInterval(() => setN((k) => k + 1), ms);
    return () => clearInterval(t);
  }, [ms, active]);
  return n;
}
