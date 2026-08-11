import { test as base } from "@playwright/test";
import { startDaemon, type Daemon } from "./daemon.ts";

// The daemon fixture (ADR-0008 phase 6): every test gets a FRESH daemon, fully
// ISOLATED from the live ~/.captainHook tree. The lifecycle itself — spawn, env
// isolation, api.json readiness, clean drain, sandbox reclaim — lives in
// ./daemon.ts, shared verbatim with the preview and snapshot scripts
// (ADR-0015 d5); this file is just the Playwright binding around it.

export type { Daemon } from "./daemon.ts";

export const test = base.extend<{ daemon: Daemon }>({
  daemon: async ({}, use) => {
    const daemon = await startDaemon();
    try {
      await use(daemon);
    } finally {
      await daemon.stop();
    }
  },
});

export { expect } from "@playwright/test";
