import { buildAndStage } from "./daemon.ts";

// Make the suite self-contained from a clean checkout (ADR-0008 phase 6): build
// the engine, build the frontend, and STAGE the freshly-built ui/ beside the
// engine binary — the daemon serves /ui from <engineDir>/ui
// (AppContext.BaseDirectory), so the E2E tests the real production bundle, not
// stale committed bytes. Runs once before any spec. The build+stage steps live
// in ./daemon.ts so the preview and snapshot scripts do exactly the same thing
// (ADR-0015 d5).

export { engineBin, enginePath } from "./daemon.ts";

export default function globalSetup() {
  buildAndStage();
}
