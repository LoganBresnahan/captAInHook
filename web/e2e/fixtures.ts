import { test as base } from "@playwright/test";
import { spawn, type ChildProcess } from "node:child_process";
import { createServer } from "node:net";
import { mkdtempSync, rmSync, readdirSync, readFileSync, writeFileSync, openSync, closeSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { engineBin } from "./global-setup.ts";

// The daemon fixture (ADR-0008 phase 6, the slice's real reasoning): every test
// gets a FRESH daemon, fully ISOLATED from the live ~/.captainHook tree
// (CLAUDE.md's pollution warning) — XDG_RUNTIME_DIR, CAPTAINHOOK_LOG, the
// harness dir, and the dispatch file all live under a per-test temp dir, so a
// spec can append trail lines and rewrite policy without ever touching the
// operator's real logs or daemons. Readiness is proven by the 0600 api.json
// appearing (the same file `captainHook ui` reads) — polled, never a fixed
// sleep (invariant 2's spirit). Teardown SIGTERMs by the recorded PID (a clean
// drain) and removes the sandbox.

export type Daemon = {
  port: number;
  token: string;
  /** /ui with the token in the fragment — the exact handoff URL (decision 3). */
  url: string;
  /** The isolated trail file; a spec appends JSONL here and the SSE tail streams it. */
  trailPath: string;
  /** The isolated dispatch policy path; PUT /policy edits THIS, never the live one. */
  dispatchPath: string;
  /** Read the current trail bytes (for assertions about what was written). */
  readTrail: () => string;
  /** Append one JSONL trail line the live trace will ingest. */
  appendTrail: (obj: unknown) => void;
};

function freePort(): Promise<number> {
  return new Promise((resolve, reject) => {
    const srv = createServer();
    srv.on("error", reject);
    srv.listen(0, "127.0.0.1", () => {
      const addr = srv.address();
      const port = typeof addr === "object" && addr ? addr.port : 0;
      srv.close(() => resolve(port));
    });
  });
}

export const test = base.extend<{ daemon: Daemon }>({
  daemon: async ({}, use, testInfo) => {
    const sandbox = mkdtempSync(join(tmpdir(), "chk-e2e-"));
    const runtimeDir = join(sandbox, "runtime");
    const trailPath = join(sandbox, "trail.jsonl");
    const dispatchPath = join(sandbox, "dispatch.json");
    writeFileSync(trailPath, "");   // exists-but-empty: the tail starts clean

    const port = await freePort();
    const engine = process.platform === "win32" ? "captainHook.exe" : "captainHook";
    // Capture the daemon's own stderr so a startup failure is diagnosable, not
    // a blind "api.json never appeared". Stderr is chatty in a dev run; the
    // trail file (CAPTAINHOOK_LOG) is the real record.
    const daemonLog = join(sandbox, "daemon.err");
    const logFd = openSync(daemonLog, "a");
    const proc: ChildProcess = spawn(join(engineBin, engine), ["--daemon"], {
      env: {
        ...process.env,
        XDG_RUNTIME_DIR: runtimeDir,
        CAPTAINHOOK_LOG: trailPath,
        CAPTAINHOOK_HARNESS_DIR: join(sandbox, "no-harness"),
        CAPTAINHOOK_DISPATCH_FILE: dispatchPath,
        CAPTAINHOOK_API_PORT: String(port),
        CAPTAINHOOK_IDLE_MS: "600000",   // out-live the test; teardown kills it
        CAPTAINHOOK_LOG_STDERR: "on",    // to daemon.err, for diagnosis on a stall
        // Give the daemon a thread-pool FLOOR: warming handlers spawns F#
        // supervised actors, and under the browser's CPU load the pool grows
        // too slowly (observed: a 58s stall between actor spawns), blowing the
        // readiness deadline. A floor of ready threads keeps warm continuations
        // scheduled. Test-env only — production warms in isolation and never
        // starves. (Hex value per the .NET knob.)
        DOTNET_ThreadPool_ForceMinWorkerThreads: "20",
      },
      stdio: ["ignore", logFd, logFd],
    });
    // A daemon that dies on startup must fail the test FAST with its exit code,
    // not hang until the api.json deadline. Two distinct failures: `exit` (the
    // process ran then died) and `error` (spawn itself failed — EAGAIN/ENOMEM
    // under the build+browser load; the process never ran, hence no output).
    let exited: { code: number | null; signal: NodeJS.Signals | null } | null = null;
    let spawnErr: Error | null = null;
    proc.on("exit", (code, signal) => { exited = { code, signal }; });
    proc.on("error", (e) => { spawnErr = e; });

    try {
      const readReady = async () => {
        const deadline = Date.now() + 40_000;   // headroom for a warm stall under load
        for (;;) {
          if (spawnErr !== null)
            throw new Error(`daemon spawn failed: ${(spawnErr as Error).message}`);
          if (exited !== null)
            throw new Error(`daemon exited early (code=${exited.code} signal=${exited.signal}):\n${tail(daemonLog)}`);
          try {
            const rvDir = join(runtimeDir, "captainHook");
            const f = readdirSync(rvDir).find((n) => n.endsWith(".api.json"));
            if (f) {
              const j = JSON.parse(readFileSync(join(rvDir, f), "utf8")) as { port: number; token: string };
              if (j.port && j.token) return j;
            }
          } catch { /* dir/file not there yet */ }
          if (Date.now() > deadline) {
            let rv = "(runtime dir missing)";
            try { rv = readdirSync(join(runtimeDir, "captainHook")).join(", "); } catch { /* none */ }
            throw new Error(
              `api.json never appeared in 40s (pid=${proc.pid} killed=${proc.killed} exitCode=${proc.exitCode}).\n`
              + `runtime dir: [${rv}]\n`
              + `daemon.err: ${tail(daemonLog)}\n`
              + `trail: ${tail(trailPath)}`);
          }
          await new Promise((r) => setTimeout(r, 50));
        }
      };
      const { port: apiPort, token } = await readReady();
      await use({
        port: apiPort,
        token,
        url: `http://127.0.0.1:${apiPort}/ui#t=${token}`,
        trailPath,
        dispatchPath,
        readTrail: () => { try { return readFileSync(trailPath, "utf8"); } catch { return ""; } },
        appendTrail: (obj) => writeFileSync(trailPath, JSON.stringify(obj) + "\n", { flag: "a" }),
      });
    } finally {
      // WAIT for the daemon to actually exit before the next test — a fixed
      // short sleep let draining daemons (SIGTERM drains in-flight, up to the
      // drain budget) pile up across tests, and several .NET processes warming
      // + draining at once STARVE the next daemon's handler-actor spawns (a
      // 58s thread-pool stall was observed, blowing the readiness deadline).
      // Clean drain by PID (never pkill-by-pattern — could hit the live daemon),
      // escalate to SIGKILL if the drain overruns, and only then reclaim.
      if (proc.pid !== undefined && exited === null) {
        const dead = new Promise<void>((res) => proc.once("exit", () => res()));
        try { process.kill(proc.pid, "SIGTERM"); } catch { /* already gone */ }
        const timedOut = await Promise.race([
          dead.then(() => false),
          new Promise<boolean>((r) => setTimeout(() => r(true), 6000)),
        ]);
        if (timedOut) {
          try { process.kill(proc.pid, "SIGKILL"); } catch { /* already gone */ }
          await dead.catch(() => {});
        }
      }
      try { closeSync(logFd); } catch { /* already closed */ }
      try { rmSync(sandbox, { recursive: true, force: true }); } catch { /* best-effort */ }
      void testInfo;
    }
  },
});

/** The last few KB of a file, for a failure message. */
function tail(path: string): string {
  try {
    const s = readFileSync(path, "utf8");
    return s.length > 2000 ? "…" + s.slice(-2000) : s || "(no daemon output)";
  } catch {
    return "(daemon log unreadable)";
  }
}

export { expect } from "@playwright/test";
