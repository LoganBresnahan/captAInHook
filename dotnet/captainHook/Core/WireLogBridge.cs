using CaptainHook.Actors;
using CaptainHook.Wire;

namespace CaptainHook.Core;

// The engine's binding of the wire lib's diagnostics seam (ADR-0004 decision 7
// amendment): every wire-layer event flows into Actors.Log, so engine-side the
// "all diagnostics flow through Log" invariant keeps its letter — one surface,
// one set of sinks, and SetSink still captures everything in tests. The AOT
// captainShim binds the seam differently (a direct JSONL append); this file is
// exactly the piece it must never reference.

public static class WireLogBridge
{
    /// Idempotent. Called from Program.cs (production) and the tests' module
    /// initializer (so wire events reach a captured sink even in a test that
    /// never touches an engine type).
    public static void Bind() => WireLog.Sink = static e =>
    {
        var f = new LogFields
        {
            DispatchId = e.Fields.DispatchId!,
            SessionId = e.Fields.SessionId!,
            HookEvent = e.Fields.HookEvent!,
            ActorId = e.Fields.ActorId!,
            DurMs = e.Fields.DurMs ?? default(double?),
            Msg = e.Fields.Msg!,
            Data = e.Fields.Data!,
        };
        switch (e.Lvl)
        {
            case "debug": Log.Debug(e.Src, e.Evt, f); break;
            case "warn": Log.Warn(e.Src, e.Evt, f); break;
            case "error": Log.Error(e.Src, e.Evt, f); break;
            default: Log.Info(e.Src, e.Evt, f); break;
        }
    };
}
