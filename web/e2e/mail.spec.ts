import { test, expect, gotoView } from "./fixtures.ts";
import { seedFiles, seedMail } from "../scripts/seed.mjs";

// The Mail view (ADR-0016 d14, roadmap item 21 slice 4) — the bus, end to end
// against a real daemon reading a real store. Everything below is seeded
// through the ENGINE's own verbs (`mail send`, then hooks that run a registered
// `mail digest`), so a picture that passes here is a picture of a bus the
// daemon can actually produce.
//
// Three things are worth an e2e rather than a unit test:
//   * the view renders the daemon's own snapshot — lanes, cursors, held mail —
//     which no amount of reducer testing proves;
//   * SEMANTIC ZOOM actually changes what is drawn (cards appear near, the
//     ledger and the role labels stay legible far), because that is the one
//     claim the layout tests cannot make about the DOM;
//   * observation is not delivery, at the browser's edge: whatever this page
//     does, it only ever GETs.
test.describe("mail", () => {
  test.beforeEach(async ({ page, daemon }) => {
    seedFiles(daemon);
    seedMail(daemon);
    await page.goto(daemon.url);
    await expect(page.locator('.session-line[data-session="live"]')).toBeVisible();
    await gotoView(page, "mail");
    // Wait for the BUS, not just the island: until the first snapshot lands the
    // view shows "Reading the bus…" and there is no canvas at all — and an
    // empty scene fits at natural scale, so the tier readout says "near" before
    // there is anything near to look at. Every assertion below is about a bus
    // that has arrived.
    await expect(page.locator("[data-mail-canvas]")).toBeVisible();
  });

  test("the bus is drawn from the daemon's snapshot: a lane per role, a track per session", async ({ page }) => {
    const mail = page.locator('[data-island="mail"]');
    // The seed addresses three roles and reads two of them.
    for (const role of ["reviewer", "builder", "archivist"])
      await expect(mail.locator(`[data-lane="${role}"]`)).toHaveCount(1);

    // `reviewer` is read by two sessions — the case that made the trail name
    // the session an advance moved (the reducer slice's find).
    const reviewer = mail.locator('[data-lane="reviewer"]');
    await expect(reviewer.locator("[data-track]")).toHaveCount(2);
    await expect(mail.locator('[data-lane="builder"] [data-track]')).toHaveCount(1);
    // …and `archivist` is read by nobody, which the lane SAYS rather than
    // rendering as an empty row.
    await expect(mail.locator('[data-lane="archivist"] [data-track]')).toHaveCount(0);
    await expect(mail.locator('[data-lane-head="archivist"]')).toContainText("no reader");

    // The urgent seam delivered the alerts and held the rest: held mail is
    // marked under its own envelope with the cursor's arithmetic, and the
    // ttl-1 envelope beta still holds is spent.
    await expect(reviewer.locator('[data-mark="held"]').first()).toBeVisible();
    await expect(reviewer.locator('[data-mark="expired"]')).toHaveCount(1);

    // The store's own facts, stated: an intact chain and the real frontier.
    await expect(mail.locator("[data-chain-ok]")).toBeVisible();
    await expect(mail.locator("[data-mail-frontier]")).toHaveText(/^[1-9]\d*$/);
  });

  test("semantic zoom: cards near, ledger and roles still legible far", async ({ page }) => {
    const mail = page.locator('[data-island="mail"]');
    const tier = mail.locator(".mail-tier");   // the readout, not the canvas
    const cards = mail.locator("[data-envelope-card]");

    // Zoom IN to the near tier: envelopes become cards carrying provenance.
    for (let i = 0; i < 12 && (await tier.getAttribute("data-tier")) !== "near"; i++)
      await mail.locator('[data-zoom="in"]').click();
    await expect(tier).toHaveAttribute("data-tier", "near");
    // Cards, carrying provenance. WHICH envelopes are on screen depends on
    // where the zoom landed (glyphs outside the ledger area are not rendered at
    // all), so the claim under test is the tier's, not any one envelope's: at
    // near, an envelope is a card with its id and TTL on it.
    await expect(cards.first()).toBeVisible();
    // (the id/ttl line truncates to the card's width — by design — so the
    // assertion is on the line that always fits: what the envelope IS.)
    await expect(cards.first()).toContainText(/(ambient|reconcile|urgent) · (status|request|answer|alert)/);

    // Zoom OUT to far: the cards go (a card in a lane that thin is unreadable),
    // and the two things the tier is FOR stay — the ledger and the roles.
    for (let i = 0; i < 14 && (await tier.getAttribute("data-tier")) !== "far"; i++)
      await mail.locator('[data-zoom="out"]').click();
    await expect(tier).toHaveAttribute("data-tier", "far");
    await expect(cards).toHaveCount(0);
    await expect(mail.locator("[data-mail-spine]")).toBeVisible();
    await expect(mail.locator("[data-spine-frontier]")).toBeVisible();
    for (const role of ["reviewer", "builder", "archivist"])
      await expect(mail.locator(`[data-lane-label="${role}"]`)).toBeVisible();

    // `fit` comes home, and it is the same view the page opened on.
    await mail.locator('[data-zoom="fit"]').click();
    await expect(tier).not.toHaveAttribute("data-tier", "far");
  });

  test("clicking an envelope opens its record — body, provenance, and each reader's standing", async ({ page }) => {
    const mail = page.locator('[data-island="mail"]');
    await mail.locator('[data-lane="builder"] [data-glyph]').first().click();

    const detail = mail.locator("[data-mail-detail]");
    await expect(detail).toBeVisible();
    // The BODY only ever comes from the store's snapshot — the trail never
    // carries one (d14), so this is the proof the view read the real store.
    await expect(detail.locator("[data-detail-body]")).toContainText("Take the canvas slice next");
    await expect(detail).toContainText("seed");
    // One standing line per reader of that role, in the cursor's own words.
    await expect(detail.locator("[data-standing]")).toHaveCount(1);

    // An envelope nobody reads says so instead of inventing a standing.
    await mail.locator('[data-lane="archivist"] [data-glyph]').first().click();
    await expect(detail.locator("[data-detail-noreader]")).toBeVisible();
  });

  test("nothing this page does is a write: every /mail request is a GET", async ({ page }) => {
    const methods: string[] = [];
    page.on("request", (r) => {
      if (r.url().includes("/api/v1/mail")) methods.push(r.method());
    });

    // Exercise the whole surface: zoom, pan, select, poll.
    const mail = page.locator('[data-island="mail"]');
    await mail.locator('[data-zoom="in"]').click();
    await mail.locator('[data-zoom="out"]').click();
    await mail.locator('[data-glyph]').first().click();
    const canvas = mail.locator("[data-mail-canvas]");
    const box = await canvas.boundingBox();
    if (box) {
      await page.mouse.move(box.x + box.width * 0.6, box.y + box.height * 0.5);
      await page.mouse.down();
      await page.mouse.move(box.x + box.width * 0.3, box.y + box.height * 0.5, { steps: 5 });
      await page.mouse.up();
    }
    // Let at least one poll land on top of the interactions.
    await expect.poll(() => methods.length, { timeout: 15_000 }).toBeGreaterThan(1);
    expect(new Set(methods)).toEqual(new Set(["GET"]));
  });
});
