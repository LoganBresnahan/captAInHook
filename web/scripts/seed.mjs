import { writeFileSync, mkdirSync, realpathSync } from "node:fs";
import { execFileSync } from "node:child_process";
import { dirname, join } from "node:path";

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

/**
 * `env` for a digest registration that runs the DEV-TREE engine.
 *
 * ExecHandler clears the child's environment and rebuilds it from a fixed
 * allowlist (ADR-0010 d5) that deliberately excludes `DOTNET_ROOT` — so a
 * framework-dependent apphost spawned as a payload cannot find a .NET runtime
 * installed anywhere but the machine default, and answers nothing. The DEPLOYED
 * engine never hits this (it is self-contained single-file, ADR-0012), which is
 * why only the sandbox needs to say it; `env{}` is the file's own mechanism for
 * exactly this, and it beats the allowlist by design.
 */
function dotnetRootEnv() {
  const root = process.env.DOTNET_ROOT
    ?? (() => {
      try { return dirname(realpathSync(execFileSync("sh", ["-c", "command -v dotnet"], { encoding: "utf8" }).trim())); }
      catch { return null; }
    })();
  return root === null ? {} : { DOTNET_ROOT: root };
}

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
      // The bus's readers (ADR-0016 d7): a role is READ by registering the
      // engine's own `mail digest` verb as a handler. The two seams are
      // deliberately different, because that difference is what the Mail canvas
      // is for — `reviewer` reads at an URGENT seam, so only urgent mail
      // delivers there and everything else piles up held (and then expires),
      // while `builder` reads at an AMBIENT seam, which delivers everything it
      // can and leaves nothing behind. A picture seeded with one seam would
      // show one shape of cursor and teach nothing.
      {
        name: "mail-reviewer", command: daemon.enginePath,
        args: ["mail", "digest", "--role", "reviewer", "--seam", "urgent"],
        events: ["PreToolUse"], mode: "oneshot", failMode: "open", budgetMs: 4000,
        env: dotnetRootEnv(),
      },
      {
        name: "mail-builder", command: daemon.enginePath,
        args: ["mail", "digest", "--role", "builder", "--seam", "ambient"],
        events: ["UserPromptSubmit"], mode: "oneshot", failMode: "open", budgetMs: 4000,
        env: dotnetRootEnv(),
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

// ---- the bus (ADR-0016 d14 — what the Mail canvas draws) --------------------
//
// A scripted SWARM rather than a hand-written store: every envelope goes on the
// bus through the real `mail send` verb and every cursor is moved by a real
// `mail digest` at a real fired hook, so the seeded picture is one the engine
// could actually produce. Hand-writing mail.jsonl and a cursor file would be
// faster and would let the canvas look good against a state the digest can
// never reach — the same trap the payload smoke tests exist to avoid.
//
// The choreography below is chosen so that ONE snapshot contains every standing
// the canvas can draw: delivered-and-passed, fresh, held mid-TTL, expired, mail
// with no reader at all, two sessions at different positions on one role, and a
// role whose cursor sits exactly at the frontier.
const MAIL_SESSIONS = { alpha: "sess-alpha-4f21", beta: "sess-beta-9c07" };

/** One envelope in the store's own dialect. EXPORTED so a spec that puts mail
 * on the bus mid-test spells it exactly as the seed does — the strict parser
 * refuses anything else (`v` is required and must be 1), and one hand-rolled
 * literal drifting from this shape is a test that fails for the wrong reason. */
export const mailEnvelope = (id, to, over) => ({
  v: 1, id, to, kind: "status", priority: "ambient", ttlDeliveries: 3,
  from: { agent: "seed", harness: "claude-code", session: MAIL_SESSIONS.alpha },
  topic: "seed", body: "seeded envelope", ...over,
});

/**
 * Put the swarm on the sandbox bus and move its cursors. Call AFTER seedFiles
 * (whose hooks register the two digest handlers) and BEFORE the page opens:
 * everything here lands in the snapshot the view seeds from, so unlike the
 * trail it does not have to wait for a live subscription. Mail sent AFTER the
 * page is up is a different thing entirely — it arrives on the stream, which is
 * what the choreography specs drive.
 *
 * @param {import("../e2e/daemon.ts").DaemonHandle} daemon
 */
export function seedMail(daemon) {
  const send = (id, to, over) => daemon.mailSend(mailEnvelope(id, to, over));
  const { alpha, beta } = MAIL_SESSIONS;

  // Three roles' worth of mail, none of it read yet.
  send("drift-report-01", "reviewer", {
    kind: "status", priority: "ambient", ttlDeliveries: 1, topic: "nightly drift",
    body: "3 files drifted from the formatter's output overnight.",
  });
  send("api-review-02", "reviewer", {
    kind: "request", priority: "reconcile", ttlDeliveries: 3, topic: "review the read port",
    body: "Please re-read MailReadPort before the next deploy: the write half must stay unreachable.",
  });
  send("build-broken-03", "reviewer", {
    kind: "alert", priority: "urgent", ttlDeliveries: 2, topic: "build is red",
    body: "main is red on linux-x64: the AOT publish leg failed at clang.",
  });
  send("plan-slice-04", "builder", {
    kind: "request", priority: "ambient", ttlDeliveries: 3, topic: "next slice",
    body: "Take the canvas slice next; the reducer is verified and waiting.",
  });
  send("ledger-note-05", "builder", {
    kind: "status", priority: "ambient", ttlDeliveries: 3, topic: "ledger",
    body: "The ledger is the spine — mail never moves, cursors do.",
  });
  // A role NOBODY reads: the store keeps it, and the canvas must say so rather
  // than draw an empty lane that looks like a rendering failure.
  send("archive-06", "archivist", {
    kind: "status", priority: "ambient", ttlDeliveries: 5, topic: "retention",
    from: { agent: "cron", harness: "none" },   // a write-only member: no session
    body: "Nothing reads this role yet — store-and-forward keeps it anyway.",
  });

  // alpha reads `reviewer` at the urgent seam: the alert delivers, the other two
  // are passed over and stamped (the ttl-1 one is spent the moment it is held).
  daemon.fireHook("pre-tool-use", { session_id: alpha });
  // alpha reads `builder` at an ambient seam: everything pending delivers, so
  // its cursor lands exactly on the frontier with nothing held.
  daemon.fireHook("user-prompt-submit", { session_id: alpha });

  // Fresh mail lands after those reads — the canvas's "ahead of the cursor".
  send("build-broken-07", "reviewer", {
    kind: "alert", priority: "urgent", ttlDeliveries: 2, topic: "build is red (still)",
    body: "Still red after the retry; the clang toolchain is missing on the runner.",
  });
  // beta joins the SAME role: first contact anchors at 0, so it sees everything
  // retained and ends up at a different position from alpha, holding what alpha
  // has already dropped. Two cursors on one lane is the case a role-only trail
  // could not attribute (the reducer slice's find).
  daemon.fireHook("pre-tool-use", { session_id: beta });

  send("style-nit-08", "reviewer", {
    kind: "status", priority: "ambient", ttlDeliveries: 3, topic: "naming",
    body: "`slotPx` reads better than `pxPerSlot` in the tier math.",
  });
  // alpha's second read: it delivers the newer alert, ages what it still holds,
  // and drops the envelope that expired at the first one.
  daemon.fireHook("pre-tool-use", { session_id: alpha });

  send("deploy-window-09", "builder", {
    kind: "request", priority: "reconcile", ttlDeliveries: 4, topic: "deploy window",
    body: "Ship after the suite is green twice — the flaky guard, not a formality.",
  });
  return { sessions: MAIL_SESSIONS, roles: ["reviewer", "builder", "archivist"] };
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
