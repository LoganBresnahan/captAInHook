import { test, expect } from "./fixtures.ts";

// The fragment token handoff (ADR-0008 d3) end to end in a real browser: the
// #t= token authenticates, is scrubbed from the URL, and survives a reload.
test.describe("token handoff", () => {
  test("navigating to /ui#t=<token> goes live, scrubs the hash, and survives reload", async ({ page, daemon }) => {
    await page.goto(daemon.url);
    await expect(page.locator('.session-line[data-session="live"]')).toBeVisible();

    // The credential is gone from the URL bar (replaceState) and never in the DOM.
    expect(page.url()).not.toContain("#t=");
    expect(page.url()).not.toContain(daemon.token);
    expect(await page.content()).not.toContain(daemon.token);

    // sessionStorage carries it across a reload with no fragment.
    await page.reload();
    await expect(page.locator('.session-line[data-session="live"]')).toBeVisible();
  });

  test("a bad token lands dead, not live", async ({ page, daemon }) => {
    await page.goto(`http://127.0.0.1:${daemon.port}/ui#t=${"0".repeat(64)}`);
    await expect(page.locator('.session-line[data-session="dead"]')).toBeVisible();
  });
});
