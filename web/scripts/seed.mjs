import { writeFileSync, readFileSync, mkdirSync, realpathSync } from "node:fs";
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
      // A DECLARED mailbox (ADR-0018 d3): `--as` makes `builder@ci-runner` a
      // durable box an operator owns, keyed by its NAME rather than by whichever
      // window happens to read it. It is what the canvas draws as a sub-lane,
      // and the only kind of mailbox a unicast can be addressed to on purpose.
      {
        name: "mail-builder-ci", command: daemon.enginePath,
        args: ["mail", "digest", "--role", "builder", "--as", "ci-runner", "--seam", "ambient"],
        events: ["UserPromptSubmit"], mode: "oneshot", failMode: "open", budgetMs: 4000,
        env: dotnetRootEnv(),
      },
      // …and one that is about to become a CORPSE. It is registered now so the
      // cursor gets created by a real digest; `seedMail` then takes the
      // registration away, which is exactly what a dead mailbox is — standing
      // on disk that nobody declares any more (d6's condition ii).
      {
        name: "mail-reviewer-retired", command: daemon.enginePath,
        args: ["mail", "digest", "--role", "reviewer", "--as", "retired-box", "--seam", "ambient"],
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
export const mailEnvelope = (id, to, over) => {
  const e = {
    v: 1, id, to, kind: "status", priority: "ambient", ttlDeliveries: 3,
    from: { agent: "seed", harness: "claude-code", session: MAIL_SESSIONS.alpha },
    topic: "seed", body: "seeded envelope", ...over,
  };
  // A UNICAST address has no TTL and the parser REFUSES one rather than
  // ignoring it (ADR-0018 d5), and `null` is a present field, not an absent
  // one — so the key is dropped outright. The address decides, exactly as it
  // does in `mail ask`.
  if (to.includes("@")) delete e.ttlDeliveries;
  return e;
};

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
  // UNICAST (ADR-0018 d1/d4): addressed to ONE mailbox, so it hangs on that
  // sub-lane and on no other — a sibling of the same role cannot read it, which
  // is the fact the predicate exists to keep true in four places.
  send("ci-status-12", "builder@ci-runner", {
    kind: "status", priority: "ambient", topic: "nightly build",
    body: "The nightly is green on linux-x64; the AOT leg took 41s.",
  });
  // …and one addressed to a mailbox that does not exist. Nothing broadcasts a
  // unicast to a second reader, so this envelope has nobody at all — which the
  // canvas says rather than drawing it as somebody's pending mail.
  send("ci-status-13", "builder@laptop-gone", {
    kind: "status", priority: "ambient", topic: "nightly build",
    body: "Addressed to a mailbox that was never registered: nobody reads this.",
  });
  // The envelope that will be STRANDED: sent to the box that is about to lose
  // its registration, and never read by it.
  send("handover-14", "reviewer@retired-box", {
    kind: "request", priority: "ambient", topic: "handover",
    body: "Take the read-port review; the box you are reading this in is being retired.",
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

  // A THREAD, and it crosses lanes on purpose (ADR-0017 d8): the request went
  // to `reviewer`, and its answers come back to the asker's own lane. That is
  // the shape the canvas's link exists to draw — inside one lane the ledger's
  // left-to-right order would already tell you.
  send("api-review-answer-10", "builder", {
    kind: "answer", priority: "ambient", ttlDeliveries: 3, topic: "re: review the read port",
    inReplyTo: "api-review-02", hops: 1,
    body: "Read it: the write half is unreachable — no append handle, no non-GET route.",
  });
  // And a SECOND answer to the same request. N5's race: two members both
  // answered one ask, which is normal on a bus with no locking. The first
  // closes the thread; this one is drawn `late` and hidden from nothing,
  // because it may well be the better answer.
  send("api-review-answer-11", "builder", {
    kind: "answer", priority: "ambient", ttlDeliveries: 3, topic: "re: review the read port",
    inReplyTo: "api-review-02", hops: 1,
    body: "Also checked the reducer: it interpolates between snapshots and stores nothing.",
  });
  // THE CORPSE (ADR-0018 d6). `retired-box` read once — that is what put its
  // cursor on disk — and then its registration goes away, which is precisely
  // what a dead mailbox is: standing nobody declares any more, holding mail
  // nobody will come for. The handlers file is rewritten rather than the cursor
  // being hand-built, so the box the picture greys is one the engine really
  // made and really abandoned.
  daemon.fireHook("user-prompt-submit", { session_id: alpha });   // ci-runner + retired-box read
  send("handover-15", "reviewer@retired-box", {
    kind: "request", priority: "ambient", topic: "handover (2)",
    body: "Second try — still addressed to the retired box, and still nobody is reading it.",
  });
  unregisterRetiredBox(daemon);

  // What a REAPER did about it: a copy on a live lane carrying `forwardedFrom`,
  // the original left exactly where it was. The reap itself is deliberately NOT
  // seeded — this is the moment between the two verbs (the state a failed reap
  // also leaves), and it is the only one in which the picture can show both the
  // corpse and the rescue at once.
  send("fwd-handover-16", "builder", {
    kind: "request", priority: "ambient", ttlDeliveries: 3, topic: "handover",
    from: { agent: "reaper", harness: "cli" },
    forwardedFrom: { id: "handover-15", address: "reviewer@retired-box" },
    body: "Second try — still addressed to the retired box, and still nobody is reading it.",
  });

  // `plan-slice-04` and `deploy-window-09` stay unanswered — the canvas's
  // `awaiting` tag needs an open question to point at.
  return { sessions: MAIL_SESSIONS, roles: ["reviewer", "builder", "archivist"] };
}

/** Take the retired box's registration away, leaving its cursor behind. The
 * rest of the file is rewritten verbatim from what `seedFiles` wrote, minus
 * that one entry — a corpse is made by REMOVING a declaration, never by
 * deleting a mailbox. */
function unregisterRetiredBox(daemon) {
  const file = JSON.parse(readFileSync(daemon.handlersPath, "utf8"));
  file.handlers = file.handlers.filter((h) => h.name !== "mail-reviewer-retired");
  writeFileSync(daemon.handlersPath, JSON.stringify(file, null, 2) + "\n");
}

/** The ROBOT NUDGES (ADR-0017 d10), appended with a live page already
 * subscribed — for the same reason `seedTrail` is: a nudge is a TRAIL LINE and
 * never an envelope, so it reaches the canvas on the mail stream and not in the
 * snapshot, and a line written before the tab connected would never arrive.
 *
 * The rows are spelled by hand here, and the shape is pinned elsewhere rather
 * than trusted here: `NudgeStore.LogNudge` is the one emitter, and the
 * `nudged-and-late-answer` golden carries a REAL row from it, so a column that
 * moves fails `MailReducerGoldenTests` + the reducer suite rather than quietly
 * making this seed a lie.
 *
 * Two pokes about ONE envelope, because that is the fact worth drawing: the
 * second one means the first did not work.
 */
export function seedMailNudges(daemon) {
  const rows = [
    { role: "reviewer", envelopeIds: ["build-broken-03"], dispatchId: "n0a1b2c3",
      reason: "urgent mail unread for 10m and no live session",
      budget: { envelope: 1, perEnvelope: 2, roleHour: 1, perRoleHour: 4 } },
    { role: "reviewer", envelopeIds: ["build-broken-03"], dispatchId: "n0d4e5f6",
      reason: "still unread after the first nudge; quiet for 20m",
      budget: { envelope: 2, perEnvelope: 2, roleHour: 2, perRoleHour: 4 } },
    { role: "archivist", envelopeIds: ["archive-06"], dispatchId: "n0778899",
      reason: "no cursor has ever read this role",
      budget: { envelope: 1, perEnvelope: 2, roleHour: 1, perRoleHour: 4 } },
    // The DEAD-MAILBOX nudge (ADR-0018 d6): it goes to the `reaper` and names
    // the corpse in `address` — the one row shape that is about somebody else's
    // mailbox, which is why the canvas draws its mark on the corpse's lane and
    // greys the corpse's own track.
    { role: "reaper", address: "reviewer@retired-box", envelopeIds: ["handover-15"], dispatchId: "n09aabb",
      reason: "dead-mailbox reviewer@retired-box · 2 stranded past quiet (2h+)",
      budget: { envelope: 1, perEnvelope: 2, roleHour: 1, perRoleHour: 4 } },
  ];
  for (const { role, address, envelopeIds, reason, budget, dispatchId } of rows) {
    daemon.appendTrail({
      ts: new Date().toISOString(), lvl: "info", src: "mail", evt: "mail.nudge", dispatchId,
      msg: "robot nudge raised: a turn was woken for mail this role had not read",
      data: { role, envelopeIds, reason, budget, ...(address === undefined ? {} : { address }) },
    });
  }
  // …and what the reaper did with what it found: the copy is already on the
  // ledger (the seed sent it), and this row is the half a copy cannot state —
  // WHO moved it, out of which mailbox.
  daemon.appendTrail({
    ts: new Date().toISOString(), lvl: "info", src: "mail", evt: "mail.forward",
    msg: "stranded mail copied to a live address: the originals stay on the ledger",
    data: { role: "reviewer", instance: "retired-box", to: "builder", by: "reaper",
      forwardedIds: ["handover-15"], skippedIds: ["handover-14"] },
  });
  return rows.length + 1;
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
