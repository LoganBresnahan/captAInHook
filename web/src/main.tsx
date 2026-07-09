import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { App } from "./App.tsx";
import { PolicyPanel } from "./PolicyPanel.tsx";
import { bootstrapToken } from "./auth.ts";
import { useStore } from "./store.ts";
import { startEventStream } from "./sse.ts";

// The mount table (ADR-0008 d8): every island is its own createRoot on a
// sibling div — no shared React parent, the store is the only coordination.
//
// Startup order is load-bearing (d3): the token bootstrap runs before ANY
// render or fetch — it reads the #t= fragment and scrubs it from the URL, so
// nothing downstream can ever observe or persist the credential-bearing
// address. The session verdict lands in the store; the stream service follows
// the session from OUTSIDE the tree (below) — an app-level concern, not an
// island's.
useStore.getState().setSession(bootstrapToken() !== null ? "checking" : "none");

const mount = (id: string, node: React.ReactNode) => {
  const el = document.getElementById(id);
  if (el) createRoot(el).render(<StrictMode>{node}</StrictMode>);
};

mount("app", <App />);
mount("policy", <PolicyPanel />);

// The stream rides the session: start once the bearer proves live, stop when
// the session dies (the client also self-reports dead on a 401/403 reconnect —
// a cutover rotated the token — and flips the session itself; decision 4).
let stream: { stop: () => void } | null = null;
useStore.subscribe((s) => {
  if (s.session === "live" && stream === null) stream = startEventStream();
  if (s.session !== "live" && s.session !== "checking" && stream !== null) {
    stream.stop();
    stream = null;
  }
});
