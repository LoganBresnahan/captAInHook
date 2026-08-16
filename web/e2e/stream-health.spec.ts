import { test, expect } from "./fixtures.ts";
import { createServer, type Server } from "node:http";
import type { AddressInfo } from "node:net";

// Stream health, end to end (2026-08-16). Born of a dogfood finding: the live
// GUI sat at 0 lines under a "● streaming" badge while the daemon dispatched,
// and trace.spec — appending to the trail file, milliseconds after a fresh
// daemon started, asserting within a second, Chromium only — could not have
// seen it. These specs break every one of those assumptions:
//   * the page is open FIRST and the producer is a REAL dispatch (shim → daemon
//     → handlers → trail), not a file write;
//   * the trail is aged (megabytes of history under the from-now anchor);
//   * there is a quiet gap between connect and the first event;
//   * the assertions are on frames RECEIVED (the telemetry the store now keeps),
//     never on the badge alone — the badge used to be unfalsifiable;
//   * and the stall path is driven for real: SIGSTOP the daemon (heartbeats
//     stop), the watchdog declares the socket stalled, SIGCONT, and the stream
//     resumes from its cursor with no gap. playwright.config runs all of it in
//     Chromium AND Firefox.

test.describe("stream health — real dispatches over an aged trail", () => {
  test.use({ agedTrailBytes: 2_000_000 });

  test("page first, quiet gap, then real hooks: rows render and the telemetry counts them", async ({ page, daemon }) => {
    await page.goto(daemon.url);
    await expect(page.locator('.session-line[data-session="live"]')).toBeVisible();
    await expect(page.locator('[data-stream="live"]')).toBeVisible();

    // The honest empty state while nothing has happened yet: connected, quiet.
    await expect(page.locator('[data-trace-empty="quiet"]')).toBeVisible();
    const stats = page.locator(".trace-stats");
    await expect(stats).toHaveAttribute("data-frames", "0");

    // A quiet gap: the stream must survive doing nothing.
    await page.waitForTimeout(2_500);
    await expect(page.locator('[data-stream="live"]')).toBeVisible();

    // Now a REAL dispatch, twice — the whole production producer path.
    daemon.fireHook("user-prompt-submit", { prompt: "stream-health-1" });
    daemon.fireHook("user-prompt-submit", { prompt: "stream-health-2" });

    const lines = page.locator('[data-island="trace"] [data-trace="line"]');
    // A dispatch writes several trail lines (dispatch.start … dispatch.done);
    // two of them cannot be fewer than two rows.
    await expect.poll(async () => lines.count()).toBeGreaterThanOrEqual(2);
    await expect(lines.filter({ hasText: "dispatch.done" })).toHaveCount(2);

    // Frames received is what proves delivery — and it agrees with the rows.
    const frames = Number(await stats.getAttribute("data-frames"));
    expect(frames).toBeGreaterThanOrEqual(await lines.count());
    await expect(stats).toContainText("last");            // "· last N s ago" — a frame was stamped
    // And nothing from the aged history leaked past the from-now anchor.
    await expect(lines.filter({ hasText: "aged history line" })).toHaveCount(0);
  });

  test("a stream that never delivers is called out: connected + daemon served since + 0 frames ⇒ STARVED", async ({ page, daemon, browserName }) => {
    // Firefox's request interception does not hand a rerouted response to the
    // page headers-first — a `route.continue({ url })` onto the hang server
    // never resolves the fetch, so the badge never goes live and the state
    // under test cannot be staged there. Harness limit, not the app: the
    // verdict itself is pure and pinned in streamHealth.test.ts; Chromium
    // drives it end to end here.
    test.skip(browserName === "firefox", "Firefox interception buffers a rerouted streaming response");
    // Reproduce the finding's SHAPE deterministically: the page's /events
    // request lands on a server that answers 200 text/event-stream and then
    // never writes a byte — headers arrive (the badge goes live, as it did in
    // life) and no frame ever follows — while the status poll, untouched,
    // carries the daemon's `served` upward under it. Before 2026-08-16 this
    // page read "● streaming / Waiting for hook activity". The stall watchdog
    // (default 40 s) is deliberately NOT shortened here: this is the window
    // in which the honest sentence must already be on screen.
    const hang: Server = createServer((_req, res) => {
      res.writeHead(200, { "Content-Type": "text/event-stream", "Cache-Control": "no-store" });
      res.flushHeaders();
    });
    await new Promise<void>((r) => hang.listen(0, "127.0.0.1", r));
    const hangUrl = `http://127.0.0.1:${(hang.address() as AddressInfo).port}/hang`;
    try {
      await page.route("**/api/v1/events", (route) => route.continue({ url: hangUrl }));
      await page.goto(daemon.url);
      await expect(page.locator('.session-line[data-session="live"]')).toBeVisible();
      await expect(page.locator('[data-stream="live"]')).toBeVisible();
      await expect(page.locator('[data-trace-empty="quiet"]')).toBeVisible();

      daemon.fireHook("user-prompt-submit", { prompt: "unseen-1" });
      daemon.fireHook("user-prompt-submit", { prompt: "unseen-2" });

      const starved = page.locator('[data-trace-empty="starved"]');
      await expect(starved).toBeVisible();                       // the next status poll (≤3 s) carries `served`
      await expect(starved).toContainText("2 dispatches since this stream connected");
      await expect(page.locator(".trace-stats")).toHaveAttribute("data-frames", "0");
      await expect(page.locator('[data-stream="live"]')).toBeVisible();   // and the badge, honestly, still says live
    } finally {
      hang.closeAllConnections();
      await new Promise<void>((r) => hang.close(() => r()));
    }
  });
});

test.describe("stream health — two subscriptions on one URL", () => {
  test("the Mail view's own stream connects BESIDE the trace's — same URL, both live", async ({ page, daemon }) => {
    // The finding of 2026-08-16 in one line. Firefox serializes concurrent
    // requests to the same URL behind its HTTP-cache entry lock, held for the
    // whole streaming body: with the trace's /api/v1/events open, the Mail
    // view's second /api/v1/events never got headers — "idle" forever, and the
    // daemon's openStreams sat at 1 under a page showing both views. apiFetch
    // sends `cache: "no-store"` for exactly this; the pin is engine-agnostic
    // and the Firefox project is the one that would fail without it.
    await page.goto(daemon.url);
    await expect(page.locator('[data-stream="live"]')).toBeVisible();
    await page.locator('[data-nav="mail"]').click();
    await expect(page.locator('[data-mail-stream="live"]')).toBeVisible();
    await expect(page.locator('[data-stream="live"]')).toHaveCount(0);   // trace island unmounted from view…
    await page.locator('[data-nav="trace"]').click();
    await expect(page.locator('[data-stream="live"]')).toBeVisible();     // …but its stream never dropped
    await expect(page.locator(".trace-stats")).toHaveAttribute("data-connects", "1");
  });
});

test.describe("stream health — the stall watchdog", () => {
  // Fast heartbeat on the daemon, short window on the client (via the
  // sessionStorage seam), so a healthy quiet stream never trips (heartbeat ≪
  // window) and a frozen daemon trips within seconds.
  test.use({ heartbeatMs: 500 });
  test.beforeEach(async ({ page }) => {
    await page.addInitScript(() => sessionStorage.setItem("captainhook.stallMs", "3000"));
  });

  test("a healthy quiet stream stays LIVE on heartbeats alone", async ({ page, daemon }) => {
    await page.goto(daemon.url);
    await expect(page.locator('[data-stream="live"]')).toBeVisible();
    await page.waitForTimeout(6_000);           // two full stall windows of silence — heartbeats carry it
    await expect(page.locator('[data-stream="live"]')).toBeVisible();
    await expect(page.locator(".trace-stats")).toHaveAttribute("data-connects", "1");
    void daemon;
  });

  test("a frozen daemon ⇒ STALLED within the window; thawed ⇒ LIVE again, resumed from the cursor, no gap", async ({ page, daemon }) => {
    test.skip(process.platform === "win32", "SIGSTOP/SIGCONT are POSIX");
    await page.goto(daemon.url);
    await expect(page.locator('[data-stream="live"]')).toBeVisible();
    daemon.appendTrail({ ts: new Date().toISOString(), lvl: "info", src: "e2e", evt: "before.freeze", dispatchId: "f0f0f0f0" });
    const lines = page.locator('[data-island="trace"] [data-trace="line"]');
    await expect(lines).toHaveCount(1);

    // Freeze: the daemon stops writing anything, heartbeats included. The
    // socket stays open — exactly the shape the watchdog exists for.
    process.kill(daemon.pid!, "SIGSTOP");
    try {
      await expect(page.locator('[data-stream="stalled"]')).toBeVisible({ timeout: 10_000 });
      await expect(page.locator(".trace-head")).toContainText("stalled");
    } finally {
      process.kill(daemon.pid!, "SIGCONT");
    }
    // Thaw: the immediate reconnect (no backoff) lands, and the stream is
    // live again — a SECOND connect, from the cursor.
    await expect(page.locator('[data-stream="live"]')).toBeVisible({ timeout: 10_000 });
    await expect(page.locator(".trace-stats")).toHaveAttribute("data-connects", "2");
    // Everything appended after the thaw arrives; nothing before it is
    // replayed (the cursor held) and no gap divider was ever drawn.
    daemon.appendTrail({ ts: new Date().toISOString(), lvl: "info", src: "e2e", evt: "after.thaw", dispatchId: "f0f0f0f1" });
    await expect(lines).toHaveCount(2);
    await expect(lines.nth(1)).toContainText("after.thaw");
    await expect(page.locator('[data-trace="gap"]')).toHaveCount(0);
    await expect(page.locator('[data-trace="reset"]')).toHaveCount(0);
  });
});
