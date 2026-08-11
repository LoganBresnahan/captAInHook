import { test, expect, gotoView } from "./fixtures.ts";

// The sidebar (ADR-0015 d1): one view at a time, Trace as the landing view, and
// the property that makes "no router" the right call — the SSE stream lives
// OUTSIDE React and folds into the store, so navigating away from Trace loses
// nothing. A router that unmounted the tree, or a client that tore the stream
// down with the view, would drop lines silently; this pins that it does not.
test.describe("navigation", () => {
  test.beforeEach(async ({ page, daemon }) => {
    await page.goto(daemon.url);
    await expect(page.locator('.session-line[data-session="live"]')).toBeVisible();
  });

  test("Trace is the landing view and exactly one view is on at a time", async ({ page }) => {
    await expect(page.locator('[data-island="trace"]')).toBeVisible();
    await expect(page.locator('[data-nav="trace"]')).toHaveAttribute("aria-current", "page");
    // The other four screens are not merely hidden — they render nothing.
    for (const island of ["status", "supervision", "policy", "harnesses"])
      await expect(page.locator(`[data-island="${island}"]`)).toHaveCount(0);

    await gotoView(page, "status");
    await expect(page.locator('[data-island="trace"]')).toHaveCount(0);
    await expect(page.locator('[data-nav="status"]')).toHaveAttribute("aria-current", "page");
    await expect(page.locator('[data-nav="trace"]')).not.toHaveAttribute("aria-current", "page");
  });

  test("the SSE stream survives a view switch: lines arriving off-screen are all there on return", async ({ page, daemon }) => {
    // Anchor: the stream is live, and one line has made the whole round trip.
    await expect(page.locator('[data-stream="live"]')).toBeVisible();
    daemon.appendTrail({ ts: "2026-08-11T10:00:00.0Z", level: "info", comp: "daemon", evt: "before.switch", dispatchId: "aaaa1111" });
    const lines = page.locator('[data-island="trace"] [data-trace="line"]');
    await expect(lines).toHaveCount(1);

    // Leave Trace entirely — the island renders null while these arrive.
    await gotoView(page, "harnesses");
    await expect(page.locator('[data-island="trace"]')).toHaveCount(0);
    for (let i = 0; i < 3; i++)
      daemon.appendTrail({ ts: `2026-08-11T10:00:0${i + 1}.0Z`, level: "info", comp: "daemon", evt: "while.away", dispatchId: `bbbb${i}` });

    // Come back: every off-screen line is present, in order, with the first.
    await gotoView(page, "trace");
    await expect(lines).toHaveCount(4);
    await expect(lines.first()).toContainText("before.switch");
    await expect(lines.nth(3)).toContainText("while.away");

    // And the stream is still the SAME one — still live, still feeding.
    await expect(page.locator('[data-stream="live"]')).toBeVisible();
    daemon.appendTrail({ ts: "2026-08-11T10:00:09.0Z", level: "info", comp: "daemon", evt: "after.return", dispatchId: "cccc3333" });
    await expect(lines).toHaveCount(5);
  });
});
