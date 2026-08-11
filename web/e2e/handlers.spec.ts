import { readFileSync, writeFileSync, existsSync } from "node:fs";
import { test, expect, gotoView } from "./fixtures.ts";

// gui-handlers-editor + gui-verbatim-confirm + gui-enable-disable +
// gui-wiring-hint (ADR-0011 d2/d3/d4/d5), end to end against a real daemon's
// same-origin /ui: the install flow behind the verbatim confirm, the daemon's
// 422 surfaced in the modal, the 412 conflict STOPPING the write (the
// lost-update inversion — nothing clobbered, draft preserved), uninstall, and
// the dispatch.json toggle. Every write lands in the fixture's ISOLATED
// handlers.json/dispatch.json — never the live tree.

const fill = async (page: import("@playwright/test").Page, entry: {
  name: string; command: string; args?: string; events?: string[];
}) => {
  await page.locator("[data-install]").click();
  await page.locator('[data-field="name"]').fill(entry.name);
  await page.locator('[data-field="command"]').fill(entry.command);
  if (entry.args) await page.locator('[data-field="args"]').fill(entry.args);
  for (const ev of entry.events ?? ["UserPromptSubmit"]) {
    await page.locator(`[data-field="events"] label:has-text("${ev}") input`).check();
  }
};

test.describe("handlers editor", () => {
  test.beforeEach(async ({ page, daemon }) => {
    await page.goto(daemon.url);
    await expect(page.locator('.session-line[data-session="live"]')).toBeVisible();
    await gotoView(page, "handlers");
    await expect(page.locator('[data-editor="handlers"]')).toBeVisible();
  });

  test("install: form → verbatim confirm (with wiring hint) → file written, entry registered", async ({ page, daemon }) => {
    await expect(page.locator('[data-file-state="absent"]')).toBeVisible();
    await fill(page, {
      name: "greeter", command: "/bin/sh",
      args: "-c\nprintf '{\"effect\":\"noop\"}'",
    });
    await page.locator("[data-review]").click();

    // The verbatim confirm IS the trust surface (d2): exact command, args,
    // events, mode/fail defaults — and the exact JSON to be written.
    const modal = page.locator('[data-confirm="install"]');
    await expect(modal).toBeVisible();
    await expect(modal.locator('[data-v="command"]')).toHaveText("/bin/sh");
    await expect(modal.locator('[data-v="args"]')).toHaveText('"-c" "printf \'{\\"effect\\":\\"noop\\"}\'"');
    await expect(modal.locator('[data-v="events"]')).toHaveText("UserPromptSubmit");
    await expect(modal.locator('[data-v="mode"]')).toHaveText("oneshot");
    await expect(modal.locator('[data-v="failMode"]')).toHaveText("open");
    // d5: the wiring hint renders the install template with the kebab event —
    // shown, never written (the e2e engine stages no shim ⇒ the deploy-home
    // fallback).
    await expect(modal.locator('[data-wiring-command="UserPromptSubmit"]'))
      .toHaveText(/captainShim hook user-prompt-submit$/);

    await modal.locator("[data-confirm-go]").click();
    await expect(page.locator('[data-notice="saved"]')).toBeVisible();

    // The file the daemon actually loads carries the entry…
    const onDisk = JSON.parse(readFileSync(daemon.handlersPath, "utf8"));
    expect(onDisk.handlers.map((h: { name: string }) => h.name)).toEqual(["greeter"]);
    // …shown honestly as PENDING: registration rides the per-dispatch
    // stat-gate, and no hook has arrived since the PUT.
    const row = page.locator('[data-expected="greeter"]');
    await expect(row).toHaveAttribute("data-registered", "false");
    await expect(row).toContainText("pending");
    // Fire a real hook → the reconcile runs → the next poll shows it live.
    daemon.fireHook("user-prompt-submit");
    await expect(page.locator('[data-expected="greeter"][data-registered="true"]'))
      .toBeVisible({ timeout: 15_000 });   // one 4s poll beat + headroom
  });

  test("daemon 422 (a skip-worthy entry) surfaces violations in the modal; nothing written", async ({ page, daemon }) => {
    // budgetMs -5 passes the client form (it only mandates shape basics) but
    // the daemon's parser refuses it — d3: the write path refuses what
    // registration would warn-and-skip.
    await fill(page, { name: "bad-budget", command: "/bin/true" });
    await page.locator('[data-field="budgetMs"]').fill("-5");
    await page.locator("[data-review]").click();
    await page.locator("[data-confirm-go]").click();

    const violations = page.locator("[data-confirm-violations]");
    await expect(violations).toBeVisible();
    await expect(violations).toContainText("bad-budget");
    expect(existsSync(daemon.handlersPath)).toBe(false);   // never created
  });

  test("412 conflict: a concurrent hand edit STOPS the write — nothing clobbered, draft preserved", async ({ page, daemon }) => {
    // Seed via the editor so the GUI holds a real etag.
    await fill(page, { name: "first", command: "/bin/true" });
    await page.locator("[data-review]").click();
    await page.locator('[data-confirm="install"] [data-confirm-go]').click();
    await expect(page.locator('[data-notice="saved"]')).toBeVisible();

    // Open an edit (the draft), then yank the file out from under it.
    await page.locator('[data-edit="first"]').click();
    const outOfBand =
      '{"version":1,"handlers":[{"name":"theirs","command":"/bin/true","events":["Stop"]}]}\n';
    writeFileSync(daemon.handlersPath, outOfBand);

    await page.locator('[data-field="command"]').fill("/bin/false");
    await page.locator("[data-review]").click();
    await page.locator('[data-confirm="install"] [data-confirm-go]').click();

    // The inversion (N1): conflict is surfaced, the stale compose is DEAD —
    // the out-of-band content survives byte-identical (their entry was never
    // clobbered), and the user's form stays open for re-review.
    await expect(page.locator('[data-notice="conflict"]')).toBeVisible();
    expect(readFileSync(daemon.handlersPath, "utf8")).toEqual(outOfBand);
    await expect(page.locator('[data-form="first"]')).toBeVisible();
    // The reloaded truth is on screen: their entry, not ours.
    await expect(page.locator('[data-expected="theirs"]')).toBeVisible();
  });

  test("uninstall behind its confirm; enable/disable toggles a dispatch.json rule", async ({ page, daemon }) => {
    await fill(page, { name: "victim", command: "/bin/true" });
    await page.locator("[data-review]").click();
    await page.locator('[data-confirm="install"] [data-confirm-go]').click();
    await expect(page.locator('[data-expected="victim"]')).toBeVisible();

    // d4: OFF prepends the unconditional handler-deny through PUT /policy…
    await page.locator('[data-toggle="victim"]').click();
    await expect(page.locator('[data-expected="victim"][data-enabled-state="disabled"]')).toBeVisible();
    const policy = JSON.parse(readFileSync(daemon.dispatchPath, "utf8"));
    expect(policy.rules[0]).toEqual({ handler: "victim", decision: "deny" });
    // …and ON removes it.
    await page.locator('[data-toggle="victim"]').click();
    await expect(page.locator('[data-expected="victim"][data-enabled-state="enabled"]')).toBeVisible();
    expect(JSON.parse(readFileSync(daemon.dispatchPath, "utf8")).rules).toEqual([]);

    // Uninstall: confirm modal names the entry; the file empties.
    await page.locator('[data-remove="victim"]').click();
    await expect(page.locator('[data-confirm="remove"]')).toBeVisible();
    await page.locator('[data-confirm="remove"] [data-confirm-go]').click();
    await expect(page.locator('[data-expected="victim"]')).toHaveCount(0);
    expect(JSON.parse(readFileSync(daemon.handlersPath, "utf8")).handlers).toEqual([]);
  });
});
