import { test as base, expect, type Page } from "@playwright/test";
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

/** Navigate to a view and wait for its island (ADR-0015 d1). ONE helper, so the
 * nav's contract lives in a single place: a spec names the view it needs, not
 * the DOM that switches it. `island` defaults to the view name — only the
 * Handlers view differs, because its island predates the rename (the store's
 * `handlers` view renders `data-island="supervision"`, which slice 4 splits). */
export async function gotoView(
  page: Page,
  view: "trace" | "handlers" | "policy" | "harnesses" | "status",
): Promise<void> {
  await page.locator(`[data-nav="${view}"]`).click();
  const island = view === "handlers" ? "supervision" : view;
  await expect(page.locator(`[data-island="${island}"]`)).toBeVisible();
}

export { expect } from "@playwright/test";
