import { writeFileSync, mkdirSync } from "node:fs";
import { join } from "node:path";

// Seed data for the dev-loop sandbox (ADR-0015 d5). The preview and snapshot
// scripts both start an EMPTY isolated daemon; an empty daemon shows empty
// panels, and you cannot design (or screenshot) a UI against no data. This
// module fills that sandbox — and ONLY that sandbox — with a small, varied,
// deliberately imperfect state: three handlers (two oneshot, one resident with
// a readiness protocol), a policy with rules of every criterion shape, and a
// trail with every level and component the trace renders.
//
// The payload scripts are written INTO the sandbox rather than pointed at
// examples/payloads/, so nothing the preview runs can touch the operator's
// home tree — the demo payloads read and write under ~/.captainHook by design.

/** The seeded payload scripts, written into <sandbox>/payloads and chmod +x. */
const PAYLOADS = {
  "greeter.sh": `#!/bin/sh
# A oneshot Inject payload: one envelope on stdin, one effect on stdout.
read -r _envelope
printf '{"effect":"inject","text":"seeded preview context"}\\n'
`,
  "guard.sh": `#!/bin/sh
# A oneshot Decide payload: allows everything, but proves the verb renders.
read -r _envelope
printf '{"effect":"decide","verdict":"allow","reason":"preview seed"}\\n'
`,
  "watcher.sh": `#!/bin/sh
# A RESIDENT payload: announce readiness, then answer one line per envelope,
# echoing the dispatchId back (the mandatory attribution).
printf '{"ready":1}\\n'
while IFS= read -r line; do
  did=$(printf '%s' "$line" | sed -n 's/.*"dispatchId":"\\([^"]*\\)".*/\\1/p')
  printf '{"dispatchId":"%s","effect":"noop"}\\n' "$did"
done
`,
};

const TRAIL_SEED = [
  // A complete warm dispatch, shim + daemon halves.
  { comp: "shim", evt: "shim.answered", level: "info", dispatchId: "7f3c1a20", durMs: 15, msg: "warm" },
  { comp: "daemon", evt: "dispatch.start", level: "info", dispatchId: "7f3c1a20", data: { event: "UserPromptSubmit" } },
  { comp: "daemon", evt: "handler.effect", level: "info", dispatchId: "7f3c1a20", data: { handler: "greeter", effect: "inject" } },
  { comp: "daemon", evt: "dispatch.done", level: "info", dispatchId: "7f3c1a20", durMs: 12 },
  // A dispatch that spawned an exec payload through the bosun rung (item 18).
  { comp: "daemon", evt: "dispatch.start", level: "info", dispatchId: "b41e9d05", data: { event: "PreToolUse" } },
  { comp: "daemon", evt: "exec.spawn", level: "info", dispatchId: "b41e9d05", data: { handler: "guard", spawner: "bosun" } },
  { comp: "daemon", evt: "handler.effect", level: "info", dispatchId: "b41e9d05", data: { handler: "guard", effect: "decide" } },
  { comp: "daemon", evt: "dispatch.done", level: "info", dispatchId: "b41e9d05", durMs: 41 },
  // Supervision drama: a restart, then an escalation.
  { comp: "actors", evt: "handler.restart", level: "warn", dispatchId: "c9021bb7", msg: "worker restarted after fault" },
  { comp: "actors", evt: "handler.escalated", level: "error", dispatchId: "c9021bb7", msg: "3 restarts inside the window" },
  { comp: "daemon", evt: "dispatch.done", level: "warn", dispatchId: "c9021bb7", durMs: 2004, msg: "degraded" },
  // Policy at work.
  { comp: "policy", evt: "policy.reload", level: "info", msg: "dispatch.json changed" },
  { comp: "policy", evt: "policy.exclude", level: "info", dispatchId: "1d55e830", data: { handlers: "watcher" } },
  { comp: "policy", evt: "policy.skip", level: "info", dispatchId: "2a70f611", data: { event: "SessionStart" } },
  // A slow one, and a plain error, so the trace shows its whole range.
  { comp: "daemon", evt: "dispatch.start", level: "info", dispatchId: "3e88ca94", data: { event: "Stop" } },
  { comp: "daemon", evt: "handler.timeout", level: "warn", dispatchId: "3e88ca94", durMs: 3000, msg: "budget exhausted" },
  { comp: "daemon", evt: "dispatch.done", level: "info", dispatchId: "3e88ca94", durMs: 3011 },
  { comp: "api", evt: "api.request", level: "info", data: { method: "GET", path: "/api/v1/status" } },
  { comp: "daemon", evt: "handlerError", level: "error", dispatchId: "4b12ff08", msg: "payload wrote malformed JSON: unexpected token" },
];

/** Write the seeded payload scripts; returns { name → absolute path }. */
function writePayloads(sandbox) {
  const dir = join(sandbox, "payloads");
  mkdirSync(dir, { recursive: true });
  const paths = {};
  for (const [name, body] of Object.entries(PAYLOADS)) {
    const p = join(dir, name);
    writeFileSync(p, body, { mode: 0o755 });
    paths[name] = p;
  }
  return paths;
}

/**
 * Fill a started sandbox daemon's FILES — payload scripts, handlers, policy.
 * The daemon picks them up on its next dispatch (the per-dispatch stat-gate),
 * which the two real hooks fired at the end make happen.
 *
 * Split from `seedTrail` deliberately: an SSE subscription anchors at the END
 * of the trail (ADR-0007 d5), so lines written before a browser connects are
 * never streamed to it — seed the files first, then seed the trail once the
 * page is live, or the trace panel screenshots empty.
 *
 * @param {import("../e2e/daemon.ts").DaemonHandle} daemon
 */
export function seedFiles(daemon) {
  const p = writePayloads(daemon.sandbox);

  writeFileSync(daemon.handlersPath, JSON.stringify({
    version: 1,
    handlers: [
      {
        name: "greeter", command: p["greeter.sh"],
        events: ["UserPromptSubmit"], mode: "oneshot", failMode: "open", budgetMs: 2000,
      },
      {
        name: "guard", command: p["guard.sh"],
        events: ["PreToolUse"], mode: "oneshot", failMode: "closed", budgetMs: 1500,
      },
      {
        name: "watcher", command: p["watcher.sh"],
        events: ["PreToolUse", "Stop"], mode: "resident", failMode: "open",
        budgetMs: 2000, readinessTimeoutMs: 5000,
      },
    ],
  }, null, 2) + "\n");

  // A policy exercising every criterion shape, while still letting the seeded
  // events through — the preview must stay usable, not paused.
  writeFileSync(daemon.dispatchPath, JSON.stringify({
    version: 1,
    default: "allow",
    rules: [
      { event: "SessionStart", decision: "deny" },
      { handler: "watcher", event: "Stop", decision: "deny" },
      { project: "/tmp/some-other-repo", decision: "deny" },
    ],
  }, null, 2) + "\n");

  // Two REAL hooks: they register the handlers (the stat-gate reconciles on the
  // next dispatch) and lay down genuine trail lines beside the synthetic ones.
  const fired = [];
  for (const evt of ["user-prompt-submit", "pre-tool-use"]) {
    try { daemon.fireHook(evt); fired.push(evt); } catch { /* a seed hook is best-effort */ }
  }
  return { payloads: p, fired };
}

/** Append the varied synthetic trail — call it with a live page already
 * subscribed, so the lines actually stream. Returns how many were written. */
export function seedTrail(daemon) {
  for (const line of TRAIL_SEED) {
    daemon.appendTrail({ ts: new Date().toISOString(), ...line });
  }
  return TRAIL_SEED.length;
}

/** Append `n` synthetic trail lines (a burst, for perf/scroll work). */
export function burstTrail(daemon, n) {
  for (let i = 0; i < n; i++) {
    const t = TRAIL_SEED[i % TRAIL_SEED.length];
    daemon.appendTrail({ ts: new Date().toISOString(), ...t, dispatchId: `burst${String(i).padStart(4, "0")}` });
  }
}
