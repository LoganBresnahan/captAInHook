import { chromium } from "@playwright/test";
import { build, stageUi, startDaemon } from "../e2e/daemon.ts";
import { seedFiles, seedTrail, burstTrail } from "./seed.mjs";

// perf — the trace view at TRACE_CAP, measured against a REAL seeded daemon
// (ADR-0015 slice 3). This exists because the ADR rejected a virtualization
// dependency in favour of `React.memo` rows + `content-visibility: auto`, and
// that rejection is only honest if someone measures it. Re-run it after any
// change to TracePanel or the trace CSS.
//
// What it measures, and what it deliberately does NOT:
//   · long tasks (>50ms) while lines stream in at the cap — the "stays fluid" bar;
//   · filter keystroke → filtered render — the interaction an operator feels;
//   · clearing the filter (re-render of every row) — the heaviest thing the view
//     does, A/B'd with and without content-visibility;
//   · append → visible, which is dominated by the SERVER's 200ms trail stat-poll
//     (TrailTail.cs) and so says nothing about rendering — reported for honesty.
// A requestAnimationFrame scroll loop is NOT a useful probe here: it is capped
// at ~16.7ms whatever work sits behind it, and scrolling a container is a paint
// offset that invalidates no layout. Both read "fine" even when nothing is.
//
// All timing is monotonic (`performance.now()`) — house invariant 2; this box's
// wall clock steps by tens of seconds (doc/platform.md § Wall-clock steps) and
// an earlier cut of this script duly reported a NEGATIVE duration.
//
// Usage:  node scripts/perf.mjs [--no-build]

const CAP = 2000;   // keep in step with TRACE_CAP in src/store.ts

if (!process.argv.includes("--no-build")) build();
stageUi();

const daemon = await startDaemon({ idleMs: 10 * 60 * 1000 });
seedFiles(daemon);

const browser = await chromium.launch();
const page = await (await browser.newContext({
  viewport: { width: 1440, height: 900 }, colorScheme: "dark",
})).newPage();

try {
  await page.goto(daemon.url);
  await page.locator('.session-line[data-session="live"]').waitFor();
  await page.locator('[data-stream="live"]').waitFor();
  seedTrail(daemon);

  const shown = () =>
    page.locator("[data-trace-count]").getAttribute("data-trace-count").then(Number);

  const fillStart = performance.now();
  while ((await shown()) < CAP) {
    burstTrail(daemon, 500);
    await page.waitForTimeout(250);
  }
  console.log(`filled to ${await shown()} rows in ${Math.round(performance.now() - fillStart)}ms of appends`);

  // 1. Long tasks while 200 lines stream in on a FULL list.
  await page.evaluate(() => {
    window.__long = [];
    new PerformanceObserver((l) => { for (const e of l.getEntries()) window.__long.push(Math.round(e.duration)); })
      .observe({ entryTypes: ["longtask"] });
  });
  burstTrail(daemon, 200);
  await page.waitForTimeout(3000);
  const long = await page.evaluate(() => window.__long);

  // 2. Filter keystroke → filtered render.
  const filterMs = [];
  for (const term of ["escalated", "exec.spawn", "policy.skip", "timeout", "handler.effect"]) {
    await page.locator(".trace-filter").fill("");
    await page.waitForTimeout(100);
    const t = performance.now();
    await page.locator(".trace-filter").fill(term);
    await page.waitForFunction(
      (n) => Number(document.querySelector("[data-trace-count]")?.getAttribute("data-trace-count")) < n,
      CAP, { timeout: 5000 });
    filterMs.push(Math.round(performance.now() - t));
  }

  // 3. The heavy one, A/B'd: clearing the filter re-renders every row.
  const widen = async () => {
    await page.locator(".trace-filter").fill("escalated");
    await page.waitForTimeout(150);
    const t = performance.now();
    await page.locator(".trace-filter").fill("");
    await page.waitForFunction(
      (n) => Number(document.querySelector("[data-trace-count]")?.getAttribute("data-trace-count")) === n,
      CAP, { timeout: 10_000 });
    await page.evaluate(() => new Promise((r) => requestAnimationFrame(() => requestAnimationFrame(r))));
    return performance.now() - t;
  };
  const best = async () => Math.min(await widen(), await widen(), await widen());
  const widenWith = await best();
  await page.addStyleTag({ content: ".trace-row { content-visibility: visible !important; }" });
  await page.waitForTimeout(300);
  const widenWithout = await best();

  // 4. Append → visible (server-poll bound; reported, not a render metric).
  const endToEnd = [];
  for (let i = 0; i < 8; i++) {
    const t = performance.now();
    daemon.appendTrail({ ts: "2026-08-11T12:00:00Z", level: "info", comp: "daemon", evt: "perf.probe", dispatchId: `p${i}`, msg: `probe-${i}-mark` });
    await page.waitForFunction(
      (mark) => document.querySelector(".trace-list")?.textContent.includes(mark),
      `probe-${i}-mark`, { timeout: 5000 });
    endToEnd.push(Math.round(performance.now() - t));
  }

  const dom = await page.evaluate(() => {
    const rows = [...document.querySelectorAll(".trace-row")];
    const box = document.querySelector(".trace-list").getBoundingClientRect();
    return {
      rows: rows.length,
      visible: rows.filter((r) => {
        const b = r.getBoundingClientRect();
        return b.bottom > box.top && b.top < box.bottom;
      }).length,
    };
  });

  const stat = (a) => {
    const s = a.slice().sort((x, y) => x - y);
    return `min ${s[0]}ms p50 ${s[Math.floor(s.length / 2)]}ms max ${s[s.length - 1]}ms`;
  };
  console.log(`rows in the DOM: ${dom.rows} (inside the viewport: ${dom.visible})`);
  console.log(`long tasks (>50ms) while 200 lines streamed in at the cap: ${long.length}${long.length ? ` [${long.join(", ")}]` : ""}`);
  console.log(`filter keystroke → filtered render: ${stat(filterMs)}`);
  console.log(`clear the filter (re-render of ${CAP} rows, best of 3): ${widenWith.toFixed(0)}ms WITH content-visibility, ${widenWithout.toFixed(0)}ms without`);
  console.log(`append → visible (server 200ms poll-bound, not a render metric): ${stat(endToEnd)}`);
} finally {
  await browser.close();
  await daemon.stop();
}
