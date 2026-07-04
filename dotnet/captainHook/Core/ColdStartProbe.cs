using System.Diagnostics;
using CaptainHook.Actors;

namespace CaptainHook.Core;

// Opt-in cold-start breakdown probe (CAPTAINHOOK_COLDSTART=1). Times the phases
// of ONE cold hook invocation — raw process start, framework construction, the
// first dispatch — so we can see which dominates before deciding whether Native
// AOT is worth it. ADR-0004 (decision 7) gates the AOT question on exactly this
// measurement: the win of a warm daemon is independent of compilation strategy,
// so AOT only pays if raw process start (what AOT attacks) is the big slice.
//
// Emits one `probe.coldstart` event through Log — JSONL + stderr, NEVER stdout,
// like every other diagnostic. Off by default so a live deployment pays nothing.
public sealed class ColdStartProbe
{
    // procBootMs is the ONE sanctioned wall-clock subtraction in the codebase.
    // The monotonic clock (Stopwatch / TickCount64) cannot start before managed
    // code runs, so process StartTime (wall) is the only source for "the OS
    // created this process -> first managed code". It is a one-shot diagnostic
    // that is emitted and never compared or used for control flow, so the
    // monotonic-clock rule ("wall clock for display only") is honored. Every
    // intra-managed boundary below rides the monotonic Stopwatch.
    readonly double _procBootMs;
    readonly Stopwatch _sw;
    double _resolved, _parsed, _built, _dispatched;

    ColdStartProbe(double procBootMs)
    {
        _procBootMs = procBootMs;
        _sw = Stopwatch.StartNew();
    }

    /// A live probe iff CAPTAINHOOK_COLDSTART=1, else null (the caller pays
    /// nothing). Call this FIRST in Main so the boot delta and the stopwatch
    /// anchor are as early as managed code allows.
    public static ColdStartProbe? StartIfEnabled()
    {
        if (Environment.GetEnvironmentVariable("CAPTAINHOOK_COLDSTART") != "1")
            return null;
        using var proc = Process.GetCurrentProcess();
        var bootMs = (DateTime.UtcNow - proc.StartTime.ToUniversalTime()).TotalMilliseconds;
        return new ColdStartProbe(bootMs);
    }

    /// Harness spec resolved (embedded + override registry load).
    public void Resolved() => _resolved = _sw.Elapsed.TotalMilliseconds;
    /// stdin read + payload + event parsed.
    public void Parsed() => _parsed = _sw.Elapsed.TotalMilliseconds;
    /// Dispatcher constructed — supervised workers spawned (framework construction).
    public void DispatcherBuilt() => _built = _sw.Elapsed.TotalMilliseconds;
    /// First DispatchAsync returned (first-dispatch JIT of the ask/merge path + handler work).
    public void Dispatched() => _dispatched = _sw.Elapsed.TotalMilliseconds;

    /// Emit the breakdown as one structured event; the cumulative marks are
    /// differenced into per-phase buckets here.
    public void Emit(string dispatchId)
    {
        static double R(double v) => Math.Round(v, 3);
        Log.Info("probe", "probe.coldstart", new LogFields
        {
            DispatchId = dispatchId,
            Data = new Dictionary<string, object>
            {
                ["procBootMs"] = R(_procBootMs),               // OS start -> managed: CLR + assembly load + entry JIT (the AOT target)
                ["resolveMs"]  = R(_resolved),                 // harness registry load (embedded specs + overrides)
                ["parseMs"]    = R(_parsed - _resolved),       // stdin read + payload/event parse (includes host I/O wait)
                ["buildMs"]    = R(_built - _parsed),          // Dispatcher ctor: worker spawn — the framework construction a daemon amortizes
                ["dispatchMs"] = R(_dispatched - _built),      // first DispatchAsync: JIT of the ask/merge path + handler work
                ["managedMs"]  = R(_dispatched),               // first managed code -> dispatched
                ["endToEndMs"] = R(_procBootMs + _dispatched), // what the host actually waits, process start -> effect ready
            },
        });
    }
}
