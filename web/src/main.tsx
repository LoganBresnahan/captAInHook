import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { App } from "./App.tsx";
import { bootstrapToken } from "./auth.ts";
import { useStore } from "./store.ts";

// Startup order is load-bearing (ADR-0008 d3): the token bootstrap runs before
// ANY render or fetch — it reads the #t= fragment and scrubs it from the URL,
// so nothing downstream can ever observe or persist the credential-bearing
// address. The session verdict lands in the store (d8) — the store lives
// OUTSIDE the tree, so this is a plain call, no provider. Real screens mount
// as sibling createRoot islands in later slices; this file is the mount table.
useStore.getState().setSession(bootstrapToken() !== null ? "checking" : "none");

const root = document.getElementById("app");
if (root) {
  createRoot(root).render(
    <StrictMode>
      <App />
    </StrictMode>,
  );
}
