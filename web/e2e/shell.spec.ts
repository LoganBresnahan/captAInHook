import { test, expect } from "./fixtures.ts";

// The shell's auth boundary (ADR-0008 d2): /ui is bearer-EXEMPT and inert;
// /api/v1/* is not. Proven end to end through a real browser + daemon.
test.describe("shell and auth boundary", () => {
  test("the shell loads WITHOUT a token and sits in the no-session state", async ({ page, daemon }) => {
    // No fragment: a plain navigation to /ui, exactly as a bookmark would.
    await page.goto(`http://127.0.0.1:${daemon.port}/ui`);
    const line = page.locator(".session-line");
    await expect(line).toHaveAttribute("data-session", "none");
    await expect(line).toContainText("captainHook ui");
    // The inert shell served no credential.
    expect(await page.content()).not.toContain(daemon.token);
  });

  test("a data route is refused without the bearer, even though the shell was not", async ({ request, daemon }) => {
    const shell = await request.get(`http://127.0.0.1:${daemon.port}/ui`);
    expect(shell.status()).toBe(200);
    const data = await request.get(`http://127.0.0.1:${daemon.port}/api/v1/status`);
    expect(data.status()).toBe(401);
  });
});
