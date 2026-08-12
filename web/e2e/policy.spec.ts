import { readFileSync, writeFileSync } from "node:fs";
import { test, expect, gotoView } from "./fixtures.ts";

// The policy editor (ADR-0008 d1) writes through the real Phase-6 PUT path, and
// the ETag adoption (pin 2) holds end to end: a second save with NO reload
// still succeeds (a stale/blind write would 412).
test.describe("policy editor", () => {
  test("edit → save → written, then save again with the adopted ETag → written", async ({ page, daemon }) => {
    await page.goto(daemon.url);
    await expect(page.locator('.session-line[data-session="live"]')).toBeVisible();
    await gotoView(page, "policy");
    const island = page.locator('[data-island="policy"]');
    await expect(island).toBeVisible();
    await island.locator('[data-policy-mode="raw"]').click();

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
    await gotoView(page, "policy");
    const island = page.locator('[data-island="policy"]');
    await island.locator('[data-policy-mode="raw"]').click();
    await island.locator("textarea").fill('{"version":1,"default":"banana","rules":[]}');
    await island.getByRole("button", { name: "Save policy" }).click();
    await expect(island.locator('[data-verdict="invalid"]')).toBeVisible();
  });
});

// The rule builder (ADR-0015 d4) over the same real write path: rows compose to
// the strict dialect, order is editable because first-match-wins makes it
// load-bearing, and a file the builder cannot rebuild faithfully LOCKS to raw
// instead of being silently rewritten.
test.describe("policy rule builder", () => {
  test.beforeEach(async ({ page, daemon }) => {
    await page.goto(daemon.url);
    await expect(page.locator('.session-line[data-session="live"]')).toBeVisible();
    await gotoView(page, "policy");
  });

  test("build a rule from empty → save → the daemon has exactly that policy", async ({ page, daemon }) => {
    const island = page.locator('[data-island="policy"]');
    await expect(island.locator("[data-rule-builder]")).toBeVisible();
    await expect(island.locator("[data-no-rules]")).toBeVisible();

    await island.locator("[data-rule-add]").click();
    await island.locator('[data-rule-field="event"][data-rule-index="0"]').fill("SessionStart");
    await island.locator('[data-rule-field="decision"][data-rule-index="0"]').selectOption("deny");
    await island.locator("[data-rule-add]").click();
    await island.locator('[data-rule-field="handler"][data-rule-index="1"]').fill("echo");
    await island.locator('[data-rule-field="project"][data-rule-index="1"]').fill("/repo");

    // The raw toggle shows EXACTLY what Save will write — no second serializer.
    await island.locator('[data-policy-mode="raw"]').click();
    const shown = await island.locator("textarea").inputValue();
    expect(JSON.parse(shown)).toEqual({
      version: 1,
      default: "allow",
      rules: [
        { event: "SessionStart", decision: "deny" },
        { handler: "echo", project: "/repo", decision: "deny" },
      ],
    });

    await island.locator('[data-policy-mode="builder"]').click();
    await island.getByRole("button", { name: "Save policy" }).click();
    await expect(island.locator('[data-verdict="written"]')).toBeVisible();

    // The daemon wrote the strict dialect, unset criteria OMITTED (an empty
    // string would have been a 422).
    const onDisk = JSON.parse(readFileSync(daemon.dispatchPath, "utf8"));
    expect(onDisk).toEqual({
      version: 1,
      default: "allow",
      rules: [
        { event: "SessionStart", decision: "deny" },
        { handler: "echo", project: "/repo", decision: "deny" },
      ],
    });
  });

  test("order is editable, because first match wins", async ({ page, daemon }) => {
    const island = page.locator('[data-island="policy"]');
    await island.locator("[data-rule-add]").click();
    await island.locator('[data-rule-field="handler"][data-rule-index="0"]').fill("first");
    await island.locator("[data-rule-add]").click();
    await island.locator('[data-rule-field="handler"][data-rule-index="1"]').fill("second");

    await island.locator('[data-rule-down="0"]').click();
    await expect(island.locator('[data-rule-field="handler"][data-rule-index="0"]')).toHaveValue("second");

    await island.getByRole("button", { name: "Save policy" }).click();
    await expect(island.locator('[data-verdict="written"]')).toBeVisible();
    const onDisk = JSON.parse(readFileSync(daemon.dispatchPath, "utf8"));
    expect(onDisk.rules.map((r: { handler: string }) => r.handler)).toEqual(["second", "first"]);
  });

  test("a hand-written policy loads INTO the rows, unchanged until the user changes it", async ({ page, daemon }) => {
    // The round-trip guard, end to end: a file written by hand (kebab event
    // spelling and all) must come back byte-equivalent through the builder.
    writeFileSync(daemon.dispatchPath, JSON.stringify({
      version: 1, default: "deny",
      rules: [{ event: "user-prompt-submit", handler: "echo", decision: "allow" }],
    }, null, 2) + "\n");
    await page.reload();
    await expect(page.locator('.session-line[data-session="live"]')).toBeVisible();
    await gotoView(page, "policy");

    const island = page.locator('[data-island="policy"]');
    await expect(island.locator('[data-rule-field="event"][data-rule-index="0"]'))
      .toHaveValue("user-prompt-submit");   // the user's spelling, not canonicalized behind their back
    await expect(island.locator("[data-policy-default]")).toHaveValue("deny");

    // Saving without touching anything writes the same MEANING back.
    await island.getByRole("button", { name: "Save policy" }).click();
    await expect(island.locator('[data-verdict="written"]')).toBeVisible();
    expect(JSON.parse(readFileSync(daemon.dispatchPath, "utf8"))).toEqual({
      version: 1, default: "deny",
      rules: [{ event: "user-prompt-submit", handler: "echo", decision: "allow" }],
    });
  });

  test("RAW-LOCK: a malformed file disables the builder rather than rewriting it", async ({ page, daemon }) => {
    writeFileSync(daemon.dispatchPath, '{"version":1,"pause":true,"rules":[]}\n');
    await page.reload();
    await expect(page.locator('.session-line[data-session="live"]')).toBeVisible();
    await gotoView(page, "policy");

    const island = page.locator('[data-island="policy"]');
    await expect(island.locator("[data-policy-locked]")).toBeVisible();
    await expect(island.locator('[data-policy-mode="builder"]')).toBeDisabled();
    await expect(island.locator("[data-rule-builder]")).toHaveCount(0);
    // The raw editor holds the file's own text, and nothing was written.
    await expect(island.locator("textarea")).toHaveValue(/"pause": ?true/);
    expect(readFileSync(daemon.dispatchPath, "utf8")).toBe('{"version":1,"pause":true,"rules":[]}\n');
  });
});
