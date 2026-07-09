import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { App } from "./App.tsx";

// Scaffold entry (web-scaffold slice, ADR-0008). The islands + Zustand store
// (d8) and the SSE/policy wiring land in later slices; for now this proves the
// toolchain and the `base: "/ui/"` same-origin serving path end to end. When
// the islands arrive, each screen becomes its own createRoot mount (DOM
// siblings, no shared React parent) and this file becomes the mount table.
const root = document.getElementById("app");
if (root) {
  createRoot(root).render(
    <StrictMode>
      <App />
    </StrictMode>,
  );
}
