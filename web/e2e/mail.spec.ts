import { truncateSync } from "node:fs";
import { test, expect, gotoView } from "./fixtures.ts";
import { seedFiles, seedMail, mailEnvelope } from "../scripts/seed.mjs";

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

    // Reload with the listener already attached, so the SNAPSHOT request is
    // observed too — since slice 5 it is the only one a session ever makes,
    // and a test that watched only the interactions would have nothing to
    // assert the method of.
    await page.reload();
    await gotoView(page, "mail");
    await expect(page.locator("[data-mail-canvas]")).toBeVisible();
    expect(methods).toEqual(["GET"]);
    methods.length = 0;

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
    // And the interactive surface reaches the bus not at all: since slice 5
    // there is exactly ONE /mail request in a session — the snapshot the stream
    // is anchored to — so panning, zooming and selecting issue nothing, GET
    // included. This is the browser's end of "observation is not delivery".
    expect(methods).toEqual([]);
  });

  // ---- the choreography (slice 5) ------------------------------------------
  //
  // These are the tests slice 4 could not write, because there was nothing live
  // to watch. Each drives the REAL engine — `mail send`, then a hook that runs a
  // registered `mail digest` — and asserts on DOM STATE rather than on timing:
  // that an envelope arrived, that a cursor moved past it, that a delivery
  // record exists. The animations those states drive are CSS; asserting a
  // keyframe would test the browser, and asserting a duration would be a flake
  // waiting to happen.

  test("an envelope put on the bus arrives in its lane, live, with no poll", async ({ page, daemon }) => {
    const mail = page.locator('[data-island="mail"]');
    const before = await mail.locator('[data-lane="builder"] [data-glyph]').count();

    // Every glyph on screen came from the snapshot: nothing has arrived yet.
    await expect(mail.locator('[data-lane="builder"] [data-arrival="trail"]')).toHaveCount(0);

    daemon.mailSend(mailEnvelope("live-arrival-01", "builder", {
      topic: "landed while watching",
      body: "This envelope was appended after the page was already looking at the bus.",
    }));

    // It appears because `mail.append` was FOLDED — the only other way it could
    // get here is a re-snapshot, and `data-arrival` distinguishes them: a line
    // from a snapshot is never an arrival.
    await expect(mail.locator('[data-lane="builder"] [data-glyph]')).toHaveCount(before + 1);
    const arrived = mail.locator('[data-lane="builder"] [data-arrival="trail"]');
    await expect(arrived).toHaveCount(1);
    await expect(arrived).toHaveAttribute("data-status", "fresh");
  });

  test("a digest at a seam moves the cursor past the mail and RECORDS the delivery", async ({ page, daemon }) => {
    // The payoff of the live fold, and the one thing the snapshot structurally
    // cannot show: `delivered` comes from a `mail.deliver` ledger line and from
    // nowhere else, so until this slice every envelope behind a cursor read
    // "before cursor · no record". Now the record arrives on the stream.
    const mail = page.locator('[data-island="mail"]');
    const builder = mail.locator('[data-lane="builder"]');
    await expect(builder.locator('[data-glyph][data-status="delivered"]')).toHaveCount(0);

    daemon.mailSend(mailEnvelope("live-delivery-01", "builder", {
      kind: "request", topic: "read me now",
      body: "The next ambient seam should deliver this and say so on the ledger.",
    }));
    await expect(builder.locator('[data-arrival="trail"]')).toHaveCount(1);

    // The SAME session that already holds builder's cursor reads again: a real
    // hook, a real registered digest, a real advance.
    daemon.fireHook("user-prompt-submit", { session_id: "sess-alpha-4f21" });

    // The envelope WE watched arrive is now delivered — asserted on that glyph
    // rather than on a count, because the digest also delivers whatever else
    // the seed left pending on this role (`deploy-window-09`), and a count
    // would be a test about the seed instead of about the fold. `delivered` is
    // granted by the reducer on a `mail.deliver` record and nothing else
    // (d14 pin iii), which is exactly what the snapshot could never carry.
    await expect(builder.locator('[data-arrival="trail"]')).toHaveAttribute("data-status", "delivered");
    await expect(builder.locator('[data-glyph][data-status="delivered"]').first()).toBeVisible();
    // …and the cursor says a forward READ moved it — an advance, then the
    // delivery record that followed it — rather than a re-anchor. The two
    // animate differently on purpose: a slide depicts reading past mail, which
    // is the only direction a cursor ever goes, while a re-anchor cuts because
    // nothing was read at all. (Which of the two forward events landed last is
    // the fold's business, not this test's; that it was neither a re-anchor nor
    // nothing is the claim.)
    const motion = await builder.locator("[data-track]").getAttribute("data-motion");
    expect(["advance", "deliver"]).toContain(motion);
  });

  test("the live picture is a fold, not a poll: no /mail request follows the first", async ({ page, daemon }) => {
    // Slice 4 re-read the whole bus every four seconds. If that poll came back
    // the choreography would still "work" — envelopes would appear — while
    // every arrival animation silently stopped firing, since a snapshot's lines
    // are never arrivals. So the absence of the poll is part of the contract.
    const snapshots: string[] = [];
    page.on("request", (r) => { if (r.url().includes("/api/v1/mail")) snapshots.push(r.url()); });

    daemon.mailSend(mailEnvelope("live-nopoll-01", "builder", {
      topic: "no poll", body: "Arriving on the stream, not on a timer.",
    }));
    await expect(page.locator('[data-island="mail"] [data-arrival="trail"]')).toHaveCount(1);

    // The stream carried it. Nothing re-read the bus to find out.
    expect(snapshots).toHaveLength(0);
    await expect(page.locator("[data-mail-stream]")).toHaveAttribute("data-mail-stream", "live");
  });

  test("a replaced trail is a RESYNC, not a wrong picture: the view re-reads and re-anchors", async ({ page, daemon }) => {
    // The reducer refuses to guess. When the trail's id space restarts — a
    // rotation, a truncation, a replaced file — the server says `reset` and the
    // reducer raises `resnapshot` rather than folding on into a stream whose
    // positions no longer mean what they meant. This drives that for real
    // (truncate the trail under a live daemon) and asserts the END STATE: the
    // picture is rebuilt from a fresh snapshot and the stream is anchored to
    // it, which is the only reason a re-seed is allowed to replace state.
    //
    // The GAP half of the same path — a slow consumer dropping lines — is
    // pinned at the unit level instead (foldMail raises `resnapshot` on a gap;
    // the driver resyncs and re-anchors at the new stamp). Driving it here
    // would mean exposing the SSE buffer's capacity as daemon CONFIGURATION,
    // and a production surface added only so a test can reach it is a worse
    // trade than two mechanical unit pins.
    const mail = page.locator('[data-island="mail"]');
    await expect(page.locator("[data-mail-stream]")).toHaveAttribute("data-mail-stream", "live");
    const lanesBefore = await mail.locator("[data-lane]").count();

    truncateSync(daemon.trailPath, 0);
    // Something has to be written for the tailer's next poll to notice the file
    // shrank — a reset is reported on the poll that observes it.
    daemon.appendTrail({ ts: new Date().toISOString(), level: "info", evt: "e2e.marker" });

    // It comes back live, on a fresh snapshot, with the bus intact — a resync
    // is a re-read, never a loss.
    await expect(page.locator("[data-mail-stream]")).toHaveAttribute("data-mail-stream", "live");
    await expect(mail.locator("[data-lane]")).toHaveCount(lanesBefore);
    await expect(mail.locator("[data-mail-canvas]")).toBeVisible();

    // And the re-read really happened: mail that lands after it still arrives
    // on the stream, so the new anchor is a working one and not a dead cursor.
    daemon.mailSend(mailEnvelope("post-resync-01", "builder", {
      topic: "after the reset", body: "The stream was re-anchored to a fresh snapshot.",
    }));
    await expect(mail.locator('[data-lane="builder"] [data-arrival="trail"]')).toHaveCount(1);
  });
});
