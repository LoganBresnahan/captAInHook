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
  // One retry even locally: the only observed flake is the daemon's handler
  // warm stalling under a CPU spike (all cores pegged — no thread floor helps),
  // a pure environmental transient a fresh daemon clears. A real product break
  // fails both attempts; this only papers over contention, per the phase's
  // named flakiness risk.
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
  projects: [{ name: "chromium", use: { ...devices["Desktop Chrome"] } }],
});
