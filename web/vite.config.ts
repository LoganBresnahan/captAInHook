import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// ADR-0008 d2/d7: the daemon serves the built assets same-origin from a disk
// `ui/` dir (a sibling of `web/`, committed and staged into the /deploy swap).
// `base: "/ui/"` makes every emitted asset URL absolute under the daemon's
// `GET /ui/*` route — the moment it is wrong the shell 404s its own bundle, so
// the trap fails loudly. No CDN: loopback is offline by nature, so the bundle
// is fully self-contained. `emptyOutDir` with an outDir OUTSIDE the project
// root needs the explicit opt-in.
export default defineConfig({
  base: "/ui/",
  plugins: [react()],
  build: {
    outDir: "../ui",
    emptyOutDir: true,
  },
});
