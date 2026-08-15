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

  test("readinessTimeoutMs round-trips: form → confirm → file → back into the edit form", async ({ page, daemon }) => {
    // ADR-0015 slice 4: the field existed in the client type (so a hand-written
    // value survived an edit) but could not be SET from the form. A resident
    // handler's readiness window is exactly the thing a GUI should be able to
    // tune, so the round trip is pinned end to end through the real PUT.
    await fill(page, { name: "resident-one", command: "/bin/true" });
    await page.locator('[data-field="mode"]').selectOption("resident");
    await page.locator('[data-field="readinessTimeoutMs"]').fill("7500");
    await page.locator("[data-review]").click();

    const modal = page.locator('[data-confirm="install"]');
    await expect(modal.locator('[data-v="readinessTimeoutMs"]')).toHaveText("7500ms");
    await modal.locator("[data-confirm-go]").click();
    await expect(page.locator('[data-notice="saved"]')).toBeVisible();

    // The daemon wrote it, typed as a number rather than a string…
    const onDisk = JSON.parse(readFileSync(daemon.handlersPath, "utf8"));
    expect(onDisk.handlers[0].readinessTimeoutMs).toBe(7500);
    // …and re-opening the entry shows it, so an edit cannot silently drop it.
    await page.locator('[data-edit="resident-one"]').click();
    await expect(page.locator('[data-field="readinessTimeoutMs"]')).toHaveValue("7500");
  });

  test("template gallery: pick → script shown → pre-filled form → install → row", async ({ page, daemon }) => {
    // ADR-0015 d3, end to end. The gallery is CLIENT-SIDE: no API verb writes a
    // script, so what this proves is that the curated metadata reaches the form
    // and the ordinary install path takes it from there.
    await page.locator("[data-gallery-toggle]").click();
    const card = page.locator('[data-template="starter-decide"]');
    await expect(card).toBeVisible();
    // The card states the effect and the harness's own verbs for that event —
    // data the client always had and never showed.
    await expect(card.locator('[data-template-effect="decide"]')).toBeVisible();
    await expect(card.locator('[data-template-event="PreToolUse"]')).toContainText("decide");

    await card.locator("[data-template-use]").click();
    const detail = page.locator('[data-template-detail="starter-decide"]');
    // The whole script is on screen — the user is about to make this executable.
    await expect(detail.locator("[data-template-script]")).toContainText("#!/bin/sh");
    await expect(detail.locator("[data-template-script]")).toContainText('"effect":"decide"');
    // The save path is derived from the LIVE daemon's shim path, not invented.
    await expect(detail.locator("[data-template-path]")).toContainText("/payloads/starter-decide.sh");

    await detail.locator("[data-template-install]").click();

    // The form arrives pre-filled from the template's metadata…
    await expect(page.locator('[data-field="name"]')).toHaveValue("guard");
    await expect(page.locator('[data-field="mode"]')).toHaveValue("oneshot");
    await expect(page.locator('[data-field="budgetMs"]')).toHaveValue("1500");
    await expect(page.locator('[data-field="command"]')).toHaveValue(/payloads\/starter-decide\.sh$/);
    await expect(page.locator('[data-field="events"] label:has-text("PreToolUse") input')).toBeChecked();
    // …including the per-event verbs beside each checkbox. Stop declares
    // `decide` since ADR-0016's reconcile seam (item 20), and SessionEnd is the
    // event that still declares nothing — which the label states in words.
    await expect(page.locator('[data-event-verbs="PreToolUse"]')).toContainText("decide");
    await expect(page.locator('[data-event-verbs="Stop"]')).toContainText("decide");
    await expect(page.locator('[data-event-verbs="SessionEnd"]')).toContainText("no loop effects");

    // From here it is the ordinary install path, verbatim confirm and all.
    await page.locator("[data-review]").click();
    await page.locator('[data-confirm="install"] [data-confirm-go]').click();
    await expect(page.locator('[data-notice="saved"]')).toBeVisible();
    await expect(page.locator('[data-expected="guard"]')).toBeVisible();

    const onDisk = JSON.parse(readFileSync(daemon.handlersPath, "utf8"));
    expect(onDisk.handlers[0].name).toBe("guard");
    expect(onDisk.handlers[0].events).toEqual(["PreToolUse"]);
    expect(onDisk.handlers[0].command).toMatch(/payloads\/starter-decide\.sh$/);
  });

  test("the confirm dialog traps focus and closes on Escape, restoring focus", async ({ page }) => {
    // ADR-0015 N4: this modal is ADR-0011's trust surface — the screen where a
    // user consents to running a process as themselves. A keyboard user must be
    // able to leave it, and must not be able to tab out behind it.
    await page.locator("[data-install]").click();
    await page.locator('[data-field="name"]').fill("esc-victim");
    await page.locator('[data-field="command"]').fill("/bin/true");
    await page.locator('[data-field="events"] label:has-text("UserPromptSubmit") input').check();
    const review = page.locator("[data-review]");
    await review.click();

    const modal = page.locator('[data-confirm="install"]');
    await expect(modal).toBeVisible();
    // Focus moved INTO the dialog on open.
    expect(await modal.evaluate((m) => m.contains(document.activeElement))).toBe(true);

    // Tab all the way round: focus stays inside, never escapes to the form.
    for (let i = 0; i < 12; i++) {
      await page.keyboard.press("Tab");
      expect(await modal.evaluate((m) => m.contains(document.activeElement))).toBe(true);
    }
    // Shift-Tab off the first element wraps backwards, still inside.
    await page.keyboard.press("Shift+Tab");
    expect(await modal.evaluate((m) => m.contains(document.activeElement))).toBe(true);

    // Escape closes it, writes nothing, and gives focus back to the opener.
    await page.keyboard.press("Escape");
    await expect(modal).toHaveCount(0);
    await expect(page.locator('[data-notice="saved"]')).toHaveCount(0);
    await expect(review).toBeFocused();
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
