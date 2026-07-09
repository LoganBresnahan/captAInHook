using System.Diagnostics;
using CaptainHook.Wire;

namespace CaptainHook.Shim;

// The AOT shim's whole program (ADR-0004 decision 7 amendment): the engine's
// shim mode minus the in-process fallback. Where the one-binary shim could
// collapse into its own dispatcher, this artifact carries none — so a
// NotDelivered outcome DELEGATES: exec the co-located JIT engine in collapsed
// mode and relay its stdout bytes, stderr text, and exit code verbatim. The
// sacred stdout channel crosses a pipe under the same byte-identical rule it
// crosses the socket. At-most-once is unchanged: FailedAfterDelivery never
// delegates — the daemon may already be running non-idempotent effects.
//
// Streams are injected (Program.cs passes the real Console; tests pass
// MemoryStreams) — the same seam HookRun uses, for the same reason: the
// stdout contract stays assertable in-process, in IL form, without an AOT
// publish per test run.
public static class ShimMain
{
    public static async Task<int> RunAsync(
        string[] args, Stream stdin, Stream stdout, TextWriter stderr,
        string? deployDir = null, string? socketPathOverride = null)
    {
        var inv = Invocation.Parse(args);
        if (inv.Mode is Mode.Daemon or Mode.Doctor or Mode.ActorsDemo or Mode.Ui)
        {
            // The shim is the hook command, nothing else. Engine work belongs
            // to the engine — a loud refusal beats silently being the wrong
            // binary.
            await stderr.WriteLineAsync(
                "captainShim: hook shim only — run captainHook for --daemon / doctor / actors-demo / ui");
            return 1;
        }

        // Deploy lays captainShim and captainHook side by side in ONE
        // directory: the identity hash is over that directory's managed DLLs,
        // so co-location is what makes shim and daemon compute the same
        // socket — and the engine is always exactly here.
        deployDir ??= AppContext.BaseDirectory;
        var engine = Path.Combine(deployDir, "captainHook");
        var dispatchId = Guid.NewGuid().ToString("N")[..8];

        // Raw stdin bytes, read once: forwarded VERBATIM inside the frame; a
        // delegation pipes the same bytes into the engine, exactly as the
        // host wrote them.
        using var stdinBuf = new MemoryStream();
        await stdin.CopyToAsync(stdinBuf);
        var stdinBytes = stdinBuf.ToArray();

        // The wire-stamp skew guard runs BEFORE anything touches the socket:
        // on a partial deploy this shim's framing and the daemon's differ, so
        // the only safe move is to never rendezvous — delegate and say why.
        // No daemon spawn either: skew is an ops error to surface, not to
        // optimize around.
        var guard = inv.Mode == Mode.Shim ? SkewGuard.Check(deployDir) : null;
        if (guard is { Ok: false })
        {
            WireLog.Error("shim", "shim.wireSkew", new WireLogFields
            {
                DispatchId = dispatchId, Msg = guard.Detail,
            });
            await stderr.WriteLineAsync($"captAInHook: {guard.Detail}; dispatching via the engine");
        }

        if (inv.Mode == Mode.Shim && guard is { Ok: true })
        {
            // Resolve the rendezvous. A failure here (pathological runtime
            // dir) must never cost the user their hook: delegate instead.
            string? socketPath = socketPathOverride;
            if (socketPath is null)
            {
                try { socketPath = RendezvousPaths.Resolve().SocketPath; }
                catch (Exception ex)
                {
                    WireLog.Warn("shim", "shim.rendezvousUnavailable", new WireLogFields
                    {
                        DispatchId = dispatchId, Msg = ex.Message,
                    });
                }
            }

            if (socketPath is not null)
            {
                var outcome = await ShimClient.TryForwardAsync(socketPath,
                    new HookRequest(dispatchId, inv.EventName, inv.HarnessName, stdinBytes));
                switch (outcome)
                {
                    case ForwardOutcome.Answered a:
                        await stdout.WriteAsync(a.StdoutBytes);
                        await stderr.WriteLineAsync(a.StderrText);
                        return a.ExitCode;

                    case ForwardOutcome.FailedAfterDelivery f:
                        // At-most-once: the dispatch may be running daemon-side.
                        // Zero stdout bytes, visible error, no delegation.
                        await stderr.WriteLineAsync(
                            $"captAInHook: dispatch {dispatchId} failed after delivery: {f.Reason}");
                        return 1;

                    case ForwardOutcome.NotDelivered:
                        // Provably never dispatched: spawn a daemon so the NEXT
                        // hook rides warm (its warmup overlaps our delegation),
                        // then delegate THIS one to the engine.
                        DaemonSpawner.SpawnDaemonForNextHook(dispatchId, exeOverride: engine);
                        break;
                }
            }
        }

        return await DelegateToEngineAsync(engine, args, stdinBytes, stdout, stderr, dispatchId);
    }

    /// The delegation fallback: run the engine in collapsed mode with the
    /// ORIGINAL argv (+ --no-daemon), pipe the captured stdin in, relay
    /// stdout BYTES, stderr text, and the exit code verbatim. The engine is
    /// self-consistent with its own DLLs, so this is also where a skewed or
    /// engineless deploy degrades to — a working hook, never a lost one.
    private static async Task<int> DelegateToEngineAsync(
        string engine, string[] args, byte[] stdinBytes,
        Stream stdout, TextWriter stderr, string dispatchId)
    {
        if (!File.Exists(engine))
        {
            // No daemon answered and there is no engine to delegate to:
            // nothing can dispatch. Zero stdout bytes and a non-zero exit —
            // the host treats it like any failed hook.
            WireLog.Error("shim", "shim.engineMissing", new WireLogFields
            {
                DispatchId = dispatchId, Msg = engine,
            });
            await stderr.WriteLineAsync(
                $"captAInHook: engine not found at '{engine}' and no daemon answered — deploy captainHook next to captainShim");
            return 1;
        }

        var sw = Stopwatch.StartNew();
        var psi = new ProcessStartInfo(engine)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (!args.Contains("--no-daemon")) psi.ArgumentList.Add("--no-daemon");

        try
        {
            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("engine process did not start");

            // Write stdin and read both output pipes concurrently — a
            // sequential read risks deadlock once a pipe buffer fills.
            var stdinTask = Task.Run(async () =>
            {
                await proc.StandardInput.BaseStream.WriteAsync(stdinBytes);
                proc.StandardInput.Close();
            });
            using var outBuf = new MemoryStream();
            var stdoutTask = proc.StandardOutput.BaseStream.CopyToAsync(outBuf);
            var stderrTask = proc.StandardError.ReadToEndAsync();

            await Task.WhenAll(stdinTask, stdoutTask, stderrTask);
            await proc.WaitForExitAsync();

            WireLog.Info("shim", "shim.delegated", new WireLogFields
            {
                DispatchId = dispatchId, DurMs = sw.Elapsed.TotalMilliseconds,
                Data = new Dictionary<string, object>
                {
                    ["exit"] = proc.ExitCode,
                    ["stdoutBytes"] = (int)outBuf.Length,
                },
            });

            await stdout.WriteAsync(outBuf.GetBuffer().AsMemory(0, (int)outBuf.Length));
            await stderr.WriteAsync(await stderrTask);
            return proc.ExitCode;
        }
        catch (Exception ex)
        {
            WireLog.Error("shim", "shim.delegationFailed", new WireLogFields
            {
                DispatchId = dispatchId, DurMs = sw.Elapsed.TotalMilliseconds, Msg = ex.Message,
            });
            await stderr.WriteLineAsync($"captAInHook: delegation to engine failed: {ex.Message}");
            return 1;
        }
    }
}
