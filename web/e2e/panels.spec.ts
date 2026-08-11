import { test, expect, gotoView } from "./fixtures.ts";

// The read islands (ADR-0008 d1) render real daemon data end to end. Each lives
// on its own view now (ADR-0015 d1), so every test navigates first — Trace is
// the landing view.
test.describe("read panels", () => {
  test.beforeEach(async ({ page, daemon }) => {
    await page.goto(daemon.url);
    await expect(page.locator('.session-line[data-session="live"]')).toBeVisible();
  });

  test("status shows identity, a numeric pid, and the open-streams count", async ({ page }) => {
    await gotoView(page, "status");
    const status = page.locator('[data-island="status"]');
    await expect(status).toBeVisible();
    await expect(status.locator('[data-metric="identity"] dd')).not.toBeEmpty();
    await expect(status.locator('[data-metric="pid"] dd')).toHaveText(/^\d+$/);
    // The SSE stream this session opened is itself counted.
    await expect(status.locator('[data-metric="open streams"] dd')).toHaveText(/^[1-9]\d*$/);
  });

  test("supervision lists the registered handlers, all live", async ({ page }) => {
    await gotoView(page, "handlers");
    const rows = page.locator('[data-island="supervision"] tbody tr');
    await expect(rows.first()).toBeVisible();
    expect(await rows.count()).toBeGreaterThan(0);
    // A fresh daemon has no escalated handlers.
    await expect(page.locator('[data-island="supervision"] [data-dead="true"]')).toHaveCount(0);
    // The expected-vs-registered section renders its handlers.json tri-state
    // (ADR-0010 d8); no file is provisioned in this fixture ⇒ absent.
    await expect(page.locator('[data-island="supervision"] [data-file-state="absent"]')).toBeVisible();
  });

  test("harnesses lists the built-in claude-code with capability chips", async ({ page }) => {
    await gotoView(page, "harnesses");
    const claude = page.locator('[data-harness="claude-code"]');
    await expect(claude).toBeVisible();
    expect(await claude.locator(".event-cap").count()).toBeGreaterThan(0);
  });
});
