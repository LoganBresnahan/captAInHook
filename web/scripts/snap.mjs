import { mkdirSync, rmSync, existsSync } from "node:fs";
import { join } from "node:path";
import { chromium } from "@playwright/test";
import { build, stageUi, startDaemon, webDir } from "../e2e/daemon.ts";
import { seedFiles, seedTrail } from "./seed.mjs";

// snap — the eyes of the GUI loop (ADR-0015 d5). Starts a seeded, isolated
// daemon exactly like the preview, drives a headless browser over every view ×
// light/dark, and writes PNGs into a gitignored web/.screens/ for the agent (or
// the human) to READ before a slice is called done. Ugliness never survives a
// commit because someone looked at it.
//
// View discovery is deliberately dynamic: the sidebar does not exist until the
// `tokens-and-sidebar` slice lands, so if no [data-nav] element is present this
// captures the current one-page shell as the single view `all` — which is
// exactly what the "before" baseline needs. The script does not change when the
// nav arrives; it just starts finding views.
//
// Usage:  node scripts/snap.mjs [--no-build] [--tag <name>] [--out <dir>]
//                               [--views a,b] [--themes light,dark] [--keep]

const argv = process.argv.slice(2);
const flag = (name, fallback) => {
  const i = argv.indexOf(`--${name}`);
  return i >= 0 ? argv[i + 1] : fallback;
};
const noBuild = argv.includes("--no-build");
const keep = argv.includes("--keep");
const tag = flag("tag", "");
const outDir = flag("out", join(webDir, ".screens"));
const wantViews = flag("views", "");
const themes = flag("themes", "light,dark").split(",").filter(Boolean);

if (!noBuild) {
  console.log("snap: building engine + ui (pass --no-build to skip)…");
  build();
}
// ALWAYS stage, even under --no-build: `npm run dev` writes ui/ on every edit
// and stages nothing, so skipping this would screenshot the previous build.
stageUi();

if (existsSync(outDir) && !keep) rmSync(outDir, { recursive: true, force: true });
mkdirSync(outDir, { recursive: true });

const daemon = await startDaemon({ idleMs: 10 * 60 * 1000 });
seedFiles(daemon);

const browser = await chromium.launch();
const written = [];
try {
  for (const theme of themes) {
    const ctx = await browser.newContext({
      colorScheme: theme,
      viewport: { width: 1440, height: 900 },
      deviceScaleFactor: 2,
    });
    const page = await ctx.newPage();
    await page.goto(daemon.url);
    // The same readiness the specs use: the shell's own session probe answered.
    await page.locator('.session-line[data-session="live"]').waitFor({ timeout: 30_000 });
    // Seed the trail only now: the subscription anchors at the file's end, so
    // lines written before this page connected would never reach it (and the
    // trace would screenshot empty). Once per context, for the same reason.
    const seeded = seedTrail(daemon);
    await page.locator('[data-trace="line"]').nth(seeded - 1).waitFor({ timeout: 15_000 })
      .catch(() => console.warn("snap: trace lines did not all arrive; capturing anyway"));

    const navs = await page.locator("[data-nav]").evaluateAll(
      (els) => els.map((e) => e.getAttribute("data-nav")).filter((v) => v));
    const views = wantViews ? wantViews.split(",").filter(Boolean)
      : navs.length ? navs
      : ["all"];   // pre-sidebar: the whole one-page shell is the only view

    for (const view of views) {
      if (navs.includes(view)) {
        await page.locator(`[data-nav="${view}"]`).click();
        await page.waitForTimeout(150);   // let the island paint before the shutter
      }
      const file = join(outDir, `${tag ? `${tag}-` : ""}${view}-${theme}.png`);
      await page.screenshot({ path: file, fullPage: true });
      written.push(file);
    }
    await ctx.close();
  }
} finally {
  await browser.close();
  await daemon.stop();
}

console.log(`\nsnap: ${written.length} screenshot(s) in ${outDir}`);
for (const f of written) console.log(`  ${f}`);
