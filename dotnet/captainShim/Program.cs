using CaptainHook.Shim;
using CaptainHook.Wire;

// captainShim — the thin AOT hook shim (ADR-0004 decision 7 amendment).
// Program.cs is only the seam between the real Console streams and ShimMain,
// mirroring the engine's Program.cs discipline: stdout carries exactly the
// relayed effect bytes, diagnostics go to the JSONL trail.
//
// The shim's own record is the trail FILE only: WireLog binds straight to the
// JSONL appender (same schema and path as the engine — golden-pinned), and
// stderr carries the daemon's relayed trace plus fatal human errors, never
// shim diagnostics. (The engine's stderr pretty sink is an engine trait; a
// per-hook native process keeps its side channel quiet.)

var logPath = WireJsonl.DefaultLogPath();
WireLog.Sink = e => WireJsonl.Append(logPath, WireJsonl.Render(e));

// Cold-start visibility, gated like the engine's probe: ~procBoot for THIS
// native image, straight into the trail for the decision-7 re-measurement.
if (Environment.GetEnvironmentVariable("CAPTAINHOOK_COLDSTART") == "1")
{
    var boot = DateTime.UtcNow - System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime();
    WireLog.Info("shim", "shim.boot", new WireLogFields { DurMs = boot.TotalMilliseconds });
}

using var stdout = Console.OpenStandardOutput();
return await ShimMain.RunAsync(args, Console.OpenStandardInput(), stdout, Console.Error);
