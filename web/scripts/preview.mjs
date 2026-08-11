import { createInterface } from "node:readline";
import { build, stageUi, startDaemon } from "../e2e/daemon.ts";
import { seedFiles, seedTrail, burstTrail } from "./seed.mjs";

// preview — ONE persistent sandboxed daemon for the GUI dev loop (ADR-0015 d5).
//
// The daemon's Origin gate 403s a second origin by design (ADR-0007), so the
// loop is not a Vite dev server: it is `vite build --watch` writing ui/, and
// THIS daemon serving that ui/ same-origin at the URL printed below. Edit →
// rebuild → reload the tab → (or run `npm run snap` and read the pictures).
//
// The sandbox is fully isolated: its own runtime dir, trail, policy, handlers,
// and payload scripts under a temp tree. It never reads or writes the live
// ~/.captainHook — you can deny every event and delete every handler here
// without touching your real hooks.
//
// Usage:  node scripts/preview.mjs [--no-build] [--port <n>]
// Then type commands on stdin: `hook <event>`, `burst [n]`, `url`, `quit`.

const argv = process.argv.slice(2);
const noBuild = argv.includes("--no-build");
const portArg = argv.indexOf("--port");
const port = portArg >= 0 ? Number(argv[portArg + 1]) : undefined;

if (!noBuild) {
  console.log("preview: building engine + ui (pass --no-build to skip)…");
  build();
}
// ALWAYS stage — see snap.mjs: --no-build skips COMPILING, never staging.
stageUi();

const daemon = await startDaemon({ port, idleMs: 24 * 60 * 60 * 1000 });
const seeded = seedFiles(daemon);

console.log(`
preview: a seeded, isolated daemon is up.

  URL      ${daemon.url}
  pid      ${daemon.pid}
  sandbox  ${daemon.sandbox}
    trail     ${daemon.trailPath}
    policy    ${daemon.dispatchPath}
    handlers  ${daemon.handlersPath}
  seeded   3 handlers · 3 policy rules · ${seeded.fired.length} real hook(s) fired

Open the URL (the #t= fragment is the one-time token; it is scrubbed on load),
THEN type \`trail\` here — the live stream anchors at the end of the trail, so
lines written before the tab connects never reach it.
Rebuild the UI in another terminal with:  npm run dev   (vite build --watch)

Commands:  trail | hook <event> | burst [n] | url | quit    Ctrl-C also stops it.
`);

const rl = createInterface({ input: process.stdin });
rl.on("line", (raw) => {
  const [cmd, ...rest] = raw.trim().split(/\s+/);
  try {
    switch (cmd) {
      case "": break;
      case "hook": {
        const evt = rest[0] ?? "user-prompt-submit";
        const out = daemon.fireHook(evt);
        console.log(`hook ${evt} → ${out.trim() || "(no stdout)"}`);
        break;
      }
      case "trail": {
        console.log(`appended ${seedTrail(daemon)} varied trail lines`);
        break;
      }
      case "burst": {
        const n = Number(rest[0] ?? 200);
        burstTrail(daemon, n);
        console.log(`appended ${n} trail lines`);
        break;
      }
      case "url":
        console.log(daemon.url);
        break;
      case "quit":
      case "exit":
        rl.close();
        break;
      default:
        console.log(`unknown command: ${cmd} (trail | hook | burst | url | quit)`);
    }
  } catch (e) {
    console.error(`command failed: ${e instanceof Error ? e.message : String(e)}`);
  }
});

let stopping = false;
const shutdown = async () => {
  if (stopping) return;
  stopping = true;
  console.log("\npreview: draining the daemon and reclaiming the sandbox…");
  rl.close();
  await daemon.stop();
  process.exit(0);
};
rl.on("close", shutdown);
process.on("SIGINT", shutdown);
process.on("SIGTERM", shutdown);
