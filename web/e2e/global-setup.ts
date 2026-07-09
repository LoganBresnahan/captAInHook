import { execFileSync } from "node:child_process";
import { cpSync, existsSync, rmSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

// Make the suite self-contained from a clean checkout (ADR-0008 phase 6): build
// the engine, build the frontend, and STAGE the freshly-built ui/ beside the
// engine binary — the daemon serves /ui from <engineDir>/ui
// (AppContext.BaseDirectory), so the E2E tests the real production bundle, not
// stale committed bytes. Runs once before any spec.
const webDir = dirname(dirname(fileURLToPath(import.meta.url)));   // web/
const repo = dirname(webDir);
const engineProj = join(repo, "dotnet/captainHook/captainHook.csproj");
export const engineBin = join(repo, "dotnet/captainHook/bin/Debug/net10.0");

export default function globalSetup() {
  const run = (cmd: string, args: string[], cwd: string) =>
    execFileSync(cmd, args, { cwd, stdio: "inherit" });

  // Engine (idempotent/incremental — fast when up to date).
  run("dotnet", ["build", engineProj, "-c", "Debug", "--nologo", "-v", "q"], repo);

  // Frontend → repo ui/, then stage a fresh copy next to the engine binary.
  run("npm", ["run", "build"], webDir);
  const staged = join(engineBin, "ui");
  if (existsSync(staged)) rmSync(staged, { recursive: true, force: true });
  cpSync(join(repo, "ui"), staged, { recursive: true });

  if (!existsSync(join(staged, "index.html")))
    throw new Error(`global-setup: ui/ not staged at ${staged}`);
}
