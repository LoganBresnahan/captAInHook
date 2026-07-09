import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { App } from "./App.tsx";
import { bootstrapToken } from "./auth.ts";

// Startup order is load-bearing (ADR-0008 d3): the token bootstrap runs before
// ANY render or fetch — it reads the #t= fragment and scrubs it from the URL,
// so nothing downstream can ever observe or persist the credential-bearing
// address. The islands + Zustand store (d8) and the real screens land in later
// slices; this file becomes the mount table then.
const hasSession = bootstrapToken() !== null;

const root = document.getElementById("app");
if (root) {
  createRoot(root).render(
    <StrictMode>
      <App hasSession={hasSession} />
    </StrictMode>,
  );
}
