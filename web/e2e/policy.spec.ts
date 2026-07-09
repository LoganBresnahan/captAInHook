import { readFileSync } from "node:fs";
import { test, expect } from "./fixtures.ts";

// The policy editor (ADR-0008 d1) writes through the real Phase-6 PUT path, and
// the ETag adoption (pin 2) holds end to end: a second save with NO reload
// still succeeds (a stale/blind write would 412).
test.describe("policy editor", () => {
  test("edit → save → written, then save again with the adopted ETag → written", async ({ page, daemon }) => {
    await page.goto(daemon.url);
    await expect(page.locator('.session-line[data-session="live"]')).toBeVisible();
    const island = page.locator('[data-island="policy"]');
    await expect(island).toBeVisible();

    const first = JSON.stringify({ version: 1, default: "allow", rules: [{ event: "session-start", decision: "deny" }] }, null, 2);
    await island.locator("textarea").fill(first);
    await island.getByRole("button", { name: "Save policy" }).click();
    await expect(island.locator('[data-verdict="written"]')).toBeVisible();

    // Second save, no reload: the 200's ETag was adopted, so this 200s too.
    const second = first.replace("deny", "allow");
    await island.locator("textarea").fill(second);
    await island.getByRole("button", { name: "Save policy" }).click();
    await expect(island.locator('[data-verdict="written"]')).toBeVisible();

    // The daemon really wrote the isolated file (never the live one).
    const onDisk = readFileSync(daemon.dispatchPath, "utf8");
    expect(onDisk).toContain('"allow"');
  });

  test("an invalid policy is refused with the daemon's own violations, nothing written", async ({ page, daemon }) => {
    await page.goto(daemon.url);
    await expect(page.locator('.session-line[data-session="live"]')).toBeVisible();
    const island = page.locator('[data-island="policy"]');
    await island.locator("textarea").fill('{"version":1,"default":"banana","rules":[]}');
    await island.getByRole("button", { name: "Save policy" }).click();
    await expect(island.locator('[data-verdict="invalid"]')).toBeVisible();
  });
});
