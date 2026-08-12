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
    // Identity and pid are the reference STRIP (slice 7) — text you read and
    // compare against a deploy, not metrics that move.
    await expect(status.locator('[data-status-field="identity"] code')).not.toBeEmpty();
    await expect(status.locator('[data-status-field="pid"] code')).toHaveText(/^\d+$/);
    // The SSE stream this session opened is itself counted.
    await expect(status.locator('[data-metric="open streams"] dd')).toHaveText(/^[1-9]\d*$/);
  });

  test("the live counters are tiles, led by served — and the identity is NOT one", async ({ page }) => {
    await gotoView(page, "status");
    const status = page.locator('[data-island="status"]');
    // Exactly one lead tile per view (the headline number), and it is served.
    const lead = status.locator("[data-tiles] .tile.lead");
    await expect(lead).toHaveCount(1);
    await expect(lead).toHaveAttribute("data-metric", "served");
    await expect(lead.locator("dd")).toHaveText(/^\d+$/);
    // The note carries the context that makes the count mean something.
    await expect(lead.locator(".tile-note")).toHaveText(/^in /);
    // The reference data stayed OUT of the tile grid — the whole point of the
    // restructure was that a content hash is not a metric.
    await expect(status.locator('[data-tiles] [data-metric="identity"]')).toHaveCount(0);
    await expect(status.locator('[data-tiles] [data-metric="pid"]')).toHaveCount(0);
  });

  test("the handlers view lists the registered handlers, all live", async ({ page }) => {
    await gotoView(page, "handlers");
    const rows = page.locator('[data-island="handlers"] table.registered tbody tr');
    await expect(rows.first()).toBeVisible();
    expect(await rows.count()).toBeGreaterThan(0);
    // A fresh daemon has no escalated handlers.
    await expect(page.locator('[data-island="handlers"] [data-dead="true"]')).toHaveCount(0);
    // The expected-vs-registered section renders its handlers.json tri-state
    // (ADR-0010 d8); no file is provisioned in this fixture ⇒ absent.
    await expect(page.locator('[data-island="handlers"] [data-file-state="absent"]')).toBeVisible();
  });

  test("status carries the supervision summary, not the handlers view", async ({ page }) => {
    // ADR-0015 d6: the daemon-wide health numbers moved to Status; the
    // per-handler detail stayed on Handlers.
    await gotoView(page, "status");
    const summary = page.locator("[data-supervision]");
    await expect(summary).toBeVisible();
    await expect(summary.locator('[data-metric="registrations"] dd')).toHaveText(/^[1-9]\d*$/);
    await expect(summary.locator('[data-metric="escalated"] dd')).toHaveText("0");
    await expect(summary.locator('[data-metric="restarts"] dd')).toHaveText("0");
    await expect(page.locator('[data-island="handlers"]')).toHaveCount(0);
    // A healthy daemon carries no tone anywhere — the glyph+color channel is
    // reserved for something actually being wrong (slice 7).
    await expect(summary.locator("[data-tone]")).toHaveCount(0);
    await expect(summary.locator('[data-metric="escalated"] .tile-note')).toHaveText("none");
  });

  test("harnesses renders the effect MATRIX: events × declared verbs, with Stop stating it plainly", async ({ page }) => {
    // Slice 7: the chips used to read `Stop (0)` — a count, with the verbs
    // themselves hidden in a hover title. Rows are events, columns are the
    // verbs the registry declares, and a cell answers the only question anyone
    // has: does the effect I am about to write land here?
    await gotoView(page, "harnesses");
    const claude = page.locator('[data-harness="claude-code"]');
    await expect(claude).toBeVisible();

    const matrix = claude.locator("[data-effect-matrix]");
    await expect(matrix).toBeVisible();
    // Columns are DERIVED from the spec's own declarations (ADR-0003), so the
    // built-in harness yields exactly the verbs it declares — no more.
    await expect(matrix.locator("[data-verb-column]")).toHaveText(["inject", "decide", "replace"]);

    // The cells the claude-code spec pins: decide lands on PreToolUse only,
    // replace on PostToolUse only.
    await expect(matrix.locator('[data-cell="PreToolUse:decide"]')).toHaveAttribute("data-on", "true");
    await expect(matrix.locator('[data-cell="PreToolUse:replace"]')).toHaveAttribute("data-on", "false");
    await expect(matrix.locator('[data-cell="PostToolUse:replace"]')).toHaveAttribute("data-on", "true");
    await expect(matrix.locator('[data-cell="SessionStart:inject"]')).toHaveAttribute("data-on", "true");

    // Stop declares nothing: one spanning cell saying so, and NO yes/no cells
    // at all — an all-blank row would read as missing data.
    const stop = matrix.locator('[data-event-row="Stop"]');
    await expect(stop.locator("[data-no-effects]")).toHaveText("no loop effects");
    await expect(stop.locator("[data-cell]")).toHaveCount(0);
  });
});
