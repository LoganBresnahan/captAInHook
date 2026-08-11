import { spawn, execFileSync, type ChildProcess } from "node:child_process";
import { createServer } from "node:net";
import {
  mkdtempSync, rmSync, readdirSync, readFileSync, writeFileSync,
  openSync, closeSync, existsSync, cpSync,
} from "node:fs";
import { tmpdir } from "node:os";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

// The sandboxed-daemon module (ADR-0015 decision 5). This logic was the E2E
// fixture's whole substance — spawn an isolated daemon, prove readiness by its
// own 0600 api.json, tear it down cleanly — and it is now shared by THREE
// consumers: the Playwright fixture (e2e/fixtures.ts, behavior unchanged), the
// persistent dev sandbox (scripts/preview.mjs), and the screenshot pass
// (scripts/snap.mjs). The eyes of the GUI loop and the e2e suite therefore look
// at the SAME daemon shape; a divergence between "what the tests see" and "what
// I see in the browser" cannot open up.
//
// Isolation is the load-bearing property (CLAUDE.md's pollution warning): the
// runtime dir, trail, harness dir, dispatch policy and handlers file all live
// under a per-run temp dir, so a spec — or a dev poking at the preview — never
// touches the operator's real logs, policy, or daemons.
//
// Plain TypeScript with erasable syntax only: Playwright compiles this file for
// the fixture, and `node` strips its types directly for the two .mjs scripts.

const thisDir = dirname(fileURLToPath(import.meta.url));            // web/e2e/
export const webDir = dirname(thisDir);                             // web/
export const repoRoot = dirname(webDir);
export const engineProj = join(repoRoot, "dotnet/captainHook/captainHook.csproj");
export const engineBin = join(repoRoot, "dotnet/captainHook/bin/Debug/net10.0");
export const engineExe = process.platform === "win32" ? "captainHook.exe" : "captainHook";
export const enginePath = join(engineBin, engineExe);

export type Daemon = {
  port: number;
  token: string;
  /** /ui with the token in the fragment — the exact handoff URL (ADR-0008 d3). */
  url: string;
  /** The isolated trail file; a spec appends JSONL here and the SSE tail streams it. */
  trailPath: string;
  /** The isolated dispatch policy path; PUT /policy edits THIS, never the live one. */
  dispatchPath: string;
  /** The isolated handlers.json path; the editor's PUT /handlers edits THIS,
   * never the live one (ADR-0011 — the fixture gap was read-only harmless
   * before the write verb existed, and load-bearing after). */
  handlersPath: string;
  /** Read the current trail bytes (for assertions about what was written). */
  readTrail: () => string;
  /** Append one JSONL trail line the live trace will ingest. */
  appendTrail: (obj: unknown) => void;
  /** Fire one real hook through the daemon (the engine's shim mode inside the
   * SAME sandbox env), returning its stdout. What drives the per-dispatch
   * stat-gate — an API handlers write reconciles on the NEXT hook, and this
   * is how a spec makes "next hook" happen. */
  fireHook: (event: string) => string;
};

/** A started daemon plus the handles only a lifecycle owner needs. */
export type DaemonHandle = Daemon & {
  /** The per-run temp root (runtime dir, trail, policy, handlers, daemon.err). */
  sandbox: string;
  pid: number | undefined;
  /** Clean drain by PID (SIGTERM → SIGKILL past the budget), then reclaim the sandbox. */
  stop: () => Promise<void>;
};

export type StartDaemonOptions = {
  /** Idle window; the default out-lives any run — the owner's `stop()` kills it. */
  idleMs?: number;
  /** Explicit API port; default picks a free one. */
  port?: number;
};

/** An ephemeral free loopback port (bind-0, read, release). */
export function freePort(): Promise<number> {
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

/** Build the engine, build the frontend, and stage the fresh ui/ beside the
 * engine binary — the daemon serves /ui from AppContext.BaseDirectory, so this
 * is what makes a run test the bundle just built rather than stale committed
 * bytes. Idempotent and incremental; shared by global-setup and the scripts. */
export function buildAndStage(): void {
  const run = (cmd: string, args: string[], cwd: string) =>
    execFileSync(cmd, args, { cwd, stdio: "inherit" });

  run("dotnet", ["build", engineProj, "-c", "Debug", "--nologo", "-v", "q"], repoRoot);
  run("npm", ["run", "build"], webDir);

  const staged = join(engineBin, "ui");
  if (existsSync(staged)) rmSync(staged, { recursive: true, force: true });
  cpSync(join(repoRoot, "ui"), staged, { recursive: true });
  if (!existsSync(join(staged, "index.html")))
    throw new Error(`buildAndStage: ui/ not staged at ${staged}`);
}

/** Spawn one isolated daemon and wait until it is answering. Readiness is
 * proven by the 0600 api.json appearing (the same file `captainHook ui` reads)
 * — polled, never a fixed sleep (invariant 2's spirit). */
export async function startDaemon(opts: StartDaemonOptions = {}): Promise<DaemonHandle> {
  const sandbox = mkdtempSync(join(tmpdir(), "chk-e2e-"));
  const runtimeDir = join(sandbox, "runtime");
  const trailPath = join(sandbox, "trail.jsonl");
  const dispatchPath = join(sandbox, "dispatch.json");
  const handlersPath = join(sandbox, "handlers.json");
  writeFileSync(trailPath, "");   // exists-but-empty: the tail starts clean

  const port = opts.port ?? await freePort();
  // One env for the daemon AND for fireHook's shim-mode runs — the shim must
  // rendezvous inside the SAME sandbox (socket via XDG_RUNTIME_DIR; identity
  // matches because both run the same engineBin build).
  const sandboxEnv = {
    ...process.env,
    XDG_RUNTIME_DIR: runtimeDir,
    CAPTAINHOOK_LOG: trailPath,
    CAPTAINHOOK_HARNESS_DIR: join(sandbox, "no-harness"),
    CAPTAINHOOK_DISPATCH_FILE: dispatchPath,
    CAPTAINHOOK_HANDLERS_FILE: handlersPath,
  };
  // Capture the daemon's own stderr so a startup failure is diagnosable, not
  // a blind "api.json never appeared". Stderr is chatty in a dev run; the
  // trail file (CAPTAINHOOK_LOG) is the real record.
  const daemonLog = join(sandbox, "daemon.err");
  const logFd = openSync(daemonLog, "a");
  const proc: ChildProcess = spawn(enginePath, ["--daemon"], {
    env: {
      ...sandboxEnv,
      CAPTAINHOOK_API_PORT: String(port),
      CAPTAINHOOK_IDLE_MS: String(opts.idleMs ?? 600_000),   // out-live the run; stop() kills it
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
  // A daemon that dies on startup must fail FAST with its exit code, not hang
  // until the api.json deadline. Two distinct failures: `exit` (the process ran
  // then died) and `error` (spawn itself failed — EAGAIN/ENOMEM under the
  // build+browser load; the process never ran, hence no output).
  let exited: { code: number | null; signal: NodeJS.Signals | null } | null = null;
  let spawnErr: Error | null = null;
  proc.on("exit", (code, signal) => { exited = { code, signal }; });
  proc.on("error", (e) => { spawnErr = e; });

  const reclaim = () => {
    try { closeSync(logFd); } catch { /* already closed */ }
    try { rmSync(sandbox, { recursive: true, force: true }); } catch { /* best-effort */ }
  };
  // WAIT for the daemon to actually exit before the caller moves on — a fixed
  // short sleep let draining daemons (SIGTERM drains in-flight, up to the drain
  // budget) pile up across tests, and several .NET processes warming + draining
  // at once STARVE the next daemon's handler-actor spawns (a 58s thread-pool
  // stall was observed, blowing the readiness deadline). Clean drain by PID
  // (never pkill-by-pattern — could hit the live daemon), escalate to SIGKILL
  // if the drain overruns, and only then reclaim.
  const stop = async () => {
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
    reclaim();
  };

  let ready: { port: number; token: string };
  try {
    const deadline = Date.now() + 40_000;   // headroom for a warm stall under load
    for (;;) {
      if (spawnErr !== null)
        throw new Error(`daemon spawn failed: ${(spawnErr as Error).message}`);
      if (exited !== null)
        throw new Error(`daemon exited early (code=${exited.code} signal=${exited.signal}):\n${tail(daemonLog)}`);
      let found: { port: number; token: string } | null = null;
      try {
        const rvDir = join(runtimeDir, "captainHook");
        const f = readdirSync(rvDir).find((n) => n.endsWith(".api.json"));
        if (f) {
          const j = JSON.parse(readFileSync(join(rvDir, f), "utf8")) as { port: number; token: string };
          if (j.port && j.token) found = j;
        }
      } catch { /* dir/file not there yet */ }
      if (found) { ready = found; break; }
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
  } catch (e) {
    // A daemon that never became ready must not leak its process or its sandbox.
    await stop();
    throw e;
  }

  return {
    port: ready.port,
    token: ready.token,
    url: `http://127.0.0.1:${ready.port}/ui#t=${ready.token}`,
    trailPath,
    dispatchPath,
    handlersPath,
    sandbox,
    pid: proc.pid,
    readTrail: () => { try { return readFileSync(trailPath, "utf8"); } catch { return ""; } },
    appendTrail: (obj) => writeFileSync(trailPath, JSON.stringify(obj) + "\n", { flag: "a" }),
    fireHook: (event) =>
      execFileSync(enginePath, ["hook", event], {
        env: sandboxEnv, input: "{}", encoding: "utf8", timeout: 15_000,
      }),
    stop,
  };
}

/** The last few KB of a file, for a failure message. */
export function tail(path: string): string {
  try {
    const s = readFileSync(path, "utf8");
    return s.length > 2000 ? "…" + s.slice(-2000) : s || "(no daemon output)";
  } catch {
    return "(daemon log unreadable)";
  }
}
