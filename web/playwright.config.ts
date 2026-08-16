import { defineConfig, devices } from "@playwright/test";

// playwright-e2e (ADR-0008 § Implementation plan, phase 6): the GUI's end-to-end
// pin, driving a REAL daemon's same-origin /ui. The specs are mechanical
// (islands mount alone: navigate fresh + assert — the loop decisions 7/8
// designed for); the reasoning lives in the daemon fixture (e2e/fixtures.ts):
// spawn an isolated daemon, wait on its 0600 api.json for readiness (no real
// sleeps), navigate to /ui#<token> so the ApiAuthGate's Host/Origin/Bearer
// checks pass same-origin with no auth hole. globalSetup builds the engine and
// stages the freshly-built ui/ beside it, so the suite tests the real bundle
// from a clean checkout.
//
// Headless by default — the agent-dev loop navigates fresh each run, so HMR /
// a visible window buy nothing (decision 7); PWDEBUG=1 or `--headed` shows a
// window on a machine with a display (e.g. WSLg). Serialized: each test spawns
// its own daemon on its own port under its own temp tree, but keeping workers
// at 1 avoids port/FD pressure and keeps the double-green ship bar stable
// (the phase's named flakiness risk).
export default defineConfig({
  testDir: "./e2e",
  globalSetup: "./e2e/global-setup.ts",
  fullyParallel: false,
  workers: 1,
  forbidOnly: !!process.env.CI,
  // One retry even locally, for environmental transients only: a real product
  // break fails both attempts. The long-blamed "handler warm stall under a CPU
  // spike" was MISDIAGNOSED — measured 2026-08-11, it was WSL2's wall clock
  // stepping ±86s (doc/platform.md § Wall-clock steps) expiring the fixture's
  // `Date.now()` readiness deadline against a healthy daemon; the deadline is
  // monotonic now. The retry stays for genuine contention under load.
  retries: process.env.CI ? 2 : 1,
  reporter: [["list"]],
  timeout: 30_000,
  expect: { timeout: 10_000 },
  use: {
    trace: "on-first-retry",
    video: "retain-on-failure",
    screenshot: "only-on-failure",
    actionTimeout: 10_000,
  },
  // Two engines (2026-08-16): the client streams the trail via fetch +
  // ReadableStream rather than EventSource, and chunk delivery is where
  // engines differ most — a single engine proves a single engine.
  // `npx playwright install chromium firefox` pulls both.
  projects: [
    { name: "chromium", use: { ...devices["Desktop Chrome"] } },
    { name: "firefox", use: { ...devices["Desktop Firefox"] } },
  ],
});
