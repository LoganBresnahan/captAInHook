import { test, expect } from "./fixtures.ts";

// The live trace (ADR-0008 d1) ingests appended trail lines over SSE and its
// dispatchId + text filters narrow correctly — end to end through a real
// daemon's stream.
test.describe("live trace", () => {
  test("appended trail lines stream in; dispatch-chip and text filters narrow", async ({ page, daemon }) => {
    await page.goto(daemon.url);
    await expect(page.locator('.session-line[data-session="live"]')).toBeVisible();
    // Wait until the stream is actually connected before appending, so the
    // lines land after the from-now anchor (the anchor-race fix guarantees
    // anchor ≤ live; appending after "live" is the honest ordering to assert).
    await expect(page.locator('[data-stream="live"]')).toBeVisible();

    daemon.appendTrail({ ts: "2026-07-09T20:15:33.1Z", level: "info", comp: "shim", evt: "shim.answered", dispatchId: "a1b2c3d4", durMs: 16, msg: "warm" });
    daemon.appendTrail({ ts: "2026-07-09T20:15:34.2Z", level: "info", comp: "daemon", evt: "dispatch.done", dispatchId: "a1b2c3d4", durMs: 12 });
    daemon.appendTrail({ ts: "2026-07-09T20:15:35.3Z", level: "warn", comp: "actors", evt: "handler.restart", dispatchId: "ffff0000", msg: "restarted" });

    const lines = page.locator('[data-island="trace"] [data-trace="line"]');
    await expect(lines).toHaveCount(3);

    // Clicking a dispatch chip filters to that dispatch's two lines.
    await page.locator('[data-dispatch="a1b2c3d4"] .t-did').first().click();
    await expect(lines).toHaveCount(2);
    for (const l of await lines.all())
      await expect(l).toHaveAttribute("data-dispatch", "a1b2c3d4");

    // Clearing, then a free-text filter narrows to the single warn line.
    await page.locator(".trace-head button", { hasText: "clear" }).click();
    await expect(lines).toHaveCount(3);
    await page.locator(".trace-filter").fill("handler.restart");
    await expect(lines).toHaveCount(1);
    await expect(lines.first()).toContainText("handler.restart");
  });
});
