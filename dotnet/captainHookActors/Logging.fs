namespace CaptainHook.Actors

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Text.Json

// Structured logging + tracing for captAInHook. Lives in the F# lib because the
// dependency arrow points C# host -> F# actors: this is the one assembly both
// layers can see, so it is where the shared Log surface must live.
//
// Contract (sacred): in hook mode STDOUT carries exactly one effect JSON object
// for the agent host — so this module NEVER touches stdout. Machine-readable
// JSONL goes to a file; the human one-liners go to stderr.

/// Optional correlation / context for a log event. A plain mutable property bag
/// so C# can say `new LogFields { DispatchId = id }` and F# can say
/// `LogFields(DispatchId = id)` — no FSharpOption leaks across the boundary.
/// Null (or, for DurMs, empty Nullable) means "absent: omit from the JSON".
type LogFields() =
    member val DispatchId: string = null with get, set
    member val SessionId: string = null with get, set
    member val HookEvent: string = null with get, set
    member val ActorId: string = null with get, set
    member val DurMs: Nullable<float> = Nullable() with get, set
    member val Msg: string = null with get, set
    /// Escape hatch for event-specific extras (counts, fail modes, ...).
    member val Data: IDictionary<string, obj> = null with get, set

/// One log event, fully materialized. Immutable record so a test sink can hold
/// onto events safely; ToJson/ToPretty are the two renderings every sink needs.
type LogEvent =
    { Ts: DateTime          // always UTC
      Lvl: string           // debug | info | warn | error
      Src: string           // e.g. dispatcher, sup:root, actor:counter-1, audit
      Evt: string           // dot-namespaced, e.g. dispatch.start, actor.restart
      Fields: LogFields }

    /// Flat, digest-friendly JSON — one object, camelCase keys, absent fields
    /// omitted entirely (a Dictionary only serializes what we put in it).
    member this.ToJson() : string =
        let o = Dictionary<string, obj>()
        o["ts"]  <- this.Ts.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
        o["lvl"] <- this.Lvl
        o["src"] <- this.Src
        o["evt"] <- this.Evt
        let f = this.Fields
        if not (isNull f.DispatchId) then o["dispatchId"] <- f.DispatchId
        if not (isNull f.SessionId)  then o["sessionId"]  <- f.SessionId
        if not (isNull f.HookEvent)  then o["hookEvent"]  <- f.HookEvent
        if not (isNull f.ActorId)    then o["actorId"]    <- f.ActorId
        if f.DurMs.HasValue          then o["durMs"]      <- Math.Round(f.DurMs.Value, 3)
        if not (isNull f.Msg)        then o["msg"]        <- f.Msg
        if not (isNull f.Data) && f.Data.Count > 0 then o["data"] <- f.Data
        JsonSerializer.Serialize o   // BCL serializer handles all string escaping

    /// The human one-liner for stderr — keeps today's readable feel.
    member this.ToPretty() : string =
        let f = this.Fields
        let piece label (v: string) = if isNull v then "" else sprintf " %s=%s" label v
        String.Concat(
            this.Ts.ToString("HH:mm:ss.fff"), " ",
            this.Lvl.ToUpperInvariant().PadRight(5), " ",
            (sprintf "[%s]" this.Src).PadRight(18), " ",
            this.Evt,
            (if f.DurMs.HasValue then sprintf " %.1fms" f.DurMs.Value else ""),
            piece "dispatch" f.DispatchId,
            piece "actor" f.ActorId,
            (if isNull f.Data then ""
             else f.Data |> Seq.map (fun kv -> sprintf " %s=%O" kv.Key kv.Value) |> String.concat ""),
            (if isNull f.Msg then "" else "  " + f.Msg))

/// Timing helper: starts a stopwatch at construction, emits ONE event with
/// durMs filled in when completed (or disposed — `using` gives you a span for
/// free). Complete is idempotent so dispose-after-complete emits nothing extra.
type LogSpan internal (lvl: string, src: string, evt: string, fields: LogFields, emit: LogEvent -> unit) =
    let sw = Stopwatch.StartNew()
    let mutable completed = false

    member _.ElapsedMs = sw.Elapsed.TotalMilliseconds

    /// Finish the span with the fields captured at start.
    member this.Complete() = this.Complete fields

    /// Finish the span with final fields (status known only at the end);
    /// durMs is stamped here regardless of what the caller set.
    member _.Complete(finalFields: LogFields) =
        if not completed then
            completed <- true
            sw.Stop()
            finalFields.DurMs <- Nullable sw.Elapsed.TotalMilliseconds
            emit { Ts = DateTime.UtcNow; Lvl = lvl; Src = src; Evt = evt; Fields = finalFields }

    interface IDisposable with
        member this.Dispose() = this.Complete()

/// The static Log API — the single seam every layer (C# host, F# actors) logs
/// through. Two sinks by default:
///   (a) JSONL appended to $CAPTAINHOOK_LOG (default ~/.captainHook/logs/
///       captainHook.jsonl), one object per line, thread-safe via lock;
///   (b) a pretty one-liner on stderr, controlled by CAPTAINHOOK_LOG_STDERR =
///       off | pretty | json (default pretty).
/// Tests swap the whole pipeline with SetSink (see below) — no env, no files.
[<AbstractClass; Sealed>]
type Log private () =
    static let gate = obj ()   // serializes file appends — actors log concurrently

    // Resolved lazily so tests that call SetSink first never touch the filesystem.
    static let mutable filePath: string = null
    static let mutable fileReady = false
    static let mutable stderrMode: string = null

    /// null = default sinks; non-null REPLACES them entirely (tests capture
    /// events in memory and nothing hits disk or stderr).
    static let mutable customSink: Action<LogEvent> = null

    static let defaultFilePath () =
        match Environment.GetEnvironmentVariable "CAPTAINHOOK_LOG" with
        | p when not (String.IsNullOrWhiteSpace p) -> p
        | _ ->
            Path.Combine(
                Environment.GetFolderPath Environment.SpecialFolder.UserProfile,
                ".captainHook", "logs", "captainHook.jsonl")

    static let defaultStderrMode () =
        match Environment.GetEnvironmentVariable "CAPTAINHOOK_LOG_STDERR" with
        | m when not (String.IsNullOrWhiteSpace m) -> m.Trim().ToLowerInvariant()
        | _ -> "pretty"

    static let defaultSink (e: LogEvent) =
        // File sink: append one JSONL line. Failures are swallowed — logging
        // must never take the hook down or pollute stdout with an exception.
        lock gate (fun () ->
            try
                if not fileReady then
                    filePath <- defaultFilePath ()
                    let dir = Path.GetDirectoryName filePath
                    if not (String.IsNullOrEmpty dir) then Directory.CreateDirectory dir |> ignore
                    fileReady <- true
                File.AppendAllText(filePath, e.ToJson() + Environment.NewLine)
            with _ -> ())
        // stderr sink: human-readable by default, NEVER stdout.
        if isNull stderrMode then stderrMode <- defaultStderrMode ()
        match stderrMode with
        | "off" -> ()
        | "json" -> eprintfn "%s" (e.ToJson())
        | _ -> eprintfn "%s" (e.ToPretty())

    static let dispatch (e: LogEvent) =
        match customSink with
        | null -> defaultSink e
        | sink -> sink.Invoke e

    static let emit lvl src evt (fields: LogFields) =
        dispatch { Ts = DateTime.UtcNow; Lvl = lvl; Src = src; Evt = evt; Fields = fields }

    // ---- sink control (the testability seam) --------------------------------
    /// Replace BOTH default sinks with a delegate; every event flows to it and
    /// only it. Pass what tests need: `Log.SetSink(e => captured.Add(e))`.
    static member SetSink(sink: Action<LogEvent>) = customSink <- sink

    /// Restore the default file + stderr sinks and re-read the env vars
    /// (so a test that mutated CAPTAINHOOK_LOG* gets a clean slate too).
    static member ResetSink() =
        customSink <- null
        fileReady <- false
        stderrMode <- null

    // ---- leveled emit, one overload triple per level -------------------------
    static member Debug(src, evt) = emit "debug" src evt (LogFields())
    static member Debug(src, evt, msg: string) = emit "debug" src evt (LogFields(Msg = msg))
    static member Debug(src, evt, fields: LogFields) = emit "debug" src evt fields

    static member Info(src, evt) = emit "info" src evt (LogFields())
    static member Info(src, evt, msg: string) = emit "info" src evt (LogFields(Msg = msg))
    static member Info(src, evt, fields: LogFields) = emit "info" src evt fields

    static member Warn(src, evt) = emit "warn" src evt (LogFields())
    static member Warn(src, evt, msg: string) = emit "warn" src evt (LogFields(Msg = msg))
    static member Warn(src, evt, fields: LogFields) = emit "warn" src evt fields

    static member Error(src, evt) = emit "error" src evt (LogFields())
    static member Error(src, evt, msg: string) = emit "error" src evt (LogFields(Msg = msg))
    static member Error(src, evt, fields: LogFields) = emit "error" src evt fields

    // ---- spans ----------------------------------------------------------------
    /// Start a timed span; the event (with durMs) fires at Complete/Dispose.
    static member Span(src, evt) : LogSpan = new LogSpan("info", src, evt, LogFields(), dispatch)
    static member Span(src, evt, fields: LogFields) : LogSpan = new LogSpan("info", src, evt, fields, dispatch)
    static member Span(lvl: string, src, evt, fields: LogFields) : LogSpan = new LogSpan(lvl, src, evt, fields, dispatch)
