namespace CaptainHook.Wire;

// The wire lib's diagnostics seam (ADR-0004 decision 7 amendment). This
// assembly is the AOT shim's whole dependency graph, so it cannot reference
// CaptainHook.Actors.Log — F# in the native image defeats the artifact.
// Instead: wire code logs through this seam and each artifact binds a sink at
// startup — the engine adapts events into Actors.Log (Core/WireLogBridge.cs),
// captainShim appends rendered JSONL directly (wire-jsonl-logger slice). An
// unbound sink drops events: a bare library has no business choosing where
// diagnostics go.

/// The wire twin of CaptainHook.Actors.LogFields — same fields, same
/// absent-means-omit semantics. The golden byte-equality test
/// (wire-jsonl-logger) pins the two renderings to identical bytes.
public sealed class WireLogFields
{
    public string? DispatchId { get; set; }
    public string? SessionId { get; set; }
    public string? HookEvent { get; set; }
    public string? ActorId { get; set; }
    public double? DurMs { get; set; }
    public string? Msg { get; set; }
    public IDictionary<string, object>? Data { get; set; }
}

/// One wire-layer log event, fully materialized. Ts is wall clock —
/// display/timestamps only, never compared (the house invariant).
public sealed record WireLogEvent(DateTime Ts, string Lvl, string Src, string Evt, WireLogFields Fields);

/// The static seam. Mirrors the Actors.Log call shape so a call site moving
/// across the assembly boundary is a one-word diff.
public static class WireLog
{
    /// Null drops events. Program.cs binds the engine's bridge; captainShim
    /// binds the JSONL appender; the test suite binds through the bridge in
    /// its module initializer.
    public static Action<WireLogEvent>? Sink;

    public static void Info(string src, string evt, WireLogFields fields) => Emit("info", src, evt, fields);
    public static void Warn(string src, string evt, WireLogFields fields) => Emit("warn", src, evt, fields);
    public static void Error(string src, string evt, WireLogFields fields) => Emit("error", src, evt, fields);

    private static void Emit(string lvl, string src, string evt, WireLogFields fields) =>
        Sink?.Invoke(new WireLogEvent(DateTime.UtcNow, lvl, src, evt, fields));
}
