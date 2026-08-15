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

/// A REAL `O_APPEND` line appender — the mirror of `captainHookWire`'s
/// `PosixTrail`, duplicated rather than shared because this assembly is a leaf
/// and may reference nothing (the two emitters share the trail FILE, not code).
///
/// Every BCL append path — `File.AppendAllText`, `FileStream(FileMode.Append)`,
/// `File.OpenHandle(FileMode.Append)` — opens WITHOUT `O_APPEND` and `pwrite`s
/// at an offset resolved at open (strace-probed on .NET 10 / linux-x64,
/// 2026-08-11). The daemon and the shim append the same trail concurrently, so
/// two opens in one window compute the same end offset and one line silently
/// overwrites the other. `O_APPEND` puts the seek-to-end inside the kernel's
/// write, under the inode lock.
module internal PosixTrail =
    open System.Runtime.InteropServices
    open System.Text

    // open(2) flags are per-OS ABI constants, not portable numbers.
    let private O_WRONLY = 1
    let private O_APPEND = if OperatingSystem.IsMacOS() then 0x0008 else 0x0400
    let private O_CLOEXEC = if OperatingSystem.IsMacOS() then 0x1000000 else 0x80000
    let private ENOENT = 2
    let private EINTR = 4

    /// The trail's own permissions (ADR-0016 d13) — 0600 file / 0700 directory,
    /// the owner-only shape the mail store, cursors, `api.json`, and the
    /// rendezvous files already carry. The trail was the one store that missed
    /// the rule and inherited the process umask (0644 in practice), and it
    /// earns the mode on its CONTENTS: payload stderr is captured verbatim
    /// (`exec.stderr`), so a trail holds whatever an arbitrary user process
    /// wrote. Mirrored in captainHookWire's PosixTrail — the two emitters share
    /// the trail FILE, not code, so whichever creates it first must produce the
    /// same mode, and a fix to one is only half a fix.
    let internal trailFileMode = UnixFileMode.UserRead ||| UnixFileMode.UserWrite

    let internal trailDirMode =
        UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute

    // The TWO-argument form of open(2), deliberately: open() is variadic, and
    // on Apple arm64 the variadic tail uses a different calling convention than
    // fixed args, so a three-arg declaration would pass `mode` in the wrong
    // place. The file is created through the BCL instead, and opened with no
    // variadic argument at all.
    [<DllImport("libc", SetLastError = true, EntryPoint = "open")>]
    extern int private sys_open(byte[] path, int flags)

    [<DllImport("libc", SetLastError = true, EntryPoint = "write")>]
    extern nativeint private sys_write(int fd, byte[] buf, unativeint count)

    [<DllImport("libc", SetLastError = true, EntryPoint = "close")>]
    extern int private sys_close(int fd)

    /// Append `text` to `path` as one O_APPEND write; false if it did not land
    /// (the caller is a log sink and owns the decision to swallow).
    let append (path: string) (text: string) : bool =
        let cPath = Array.zeroCreate<byte> (Encoding.UTF8.GetByteCount path + 1)
        Encoding.UTF8.GetBytes(path, 0, path.Length, cPath, 0) |> ignore   // trailing 0 = NUL

        let flags = O_WRONLY ||| O_APPEND ||| O_CLOEXEC
        let mutable fd = sys_open (cPath, flags)
        if fd < 0 && Marshal.GetLastPInvokeError() = ENOENT then
            // Absent file: the BCL owns creation (and the directory), then one retry.
            try
                // 0600 AT CREATION: the two-argument open(2) above cannot carry
                // a mode, so this BCL create is the only place on this side
                // where the trail comes into existence. UnixCreateMode applies
                // on CREATE only — a pre-existing loose trail is discarded at
                // deploy rather than chmod'ed.
                let opts = FileStreamOptions()
                opts.Mode <- FileMode.OpenOrCreate
                opts.Access <- FileAccess.Write
                opts.Share <- FileShare.ReadWrite
                opts.UnixCreateMode <- trailFileMode
                (new FileStream(path, opts)).Dispose()
                fd <- sys_open (cPath, flags)
            with _ -> ()

        if fd < 0 then
            false
        else
            try
                let buf = Encoding.UTF8.GetBytes text
                let mutable offset = 0
                let mutable failed = false
                while not failed && offset < buf.Length do
                    // One write(2) for the whole line is the atomic unit; a
                    // short write (signal, ENOSPC) copies the remainder and
                    // re-enters, which another writer may split — rare, and
                    // strictly better than losing the line entirely.
                    let chunk = if offset = 0 then buf else buf[offset..]
                    let n = sys_write (fd, chunk, unativeint chunk.Length)
                    if n > 0n then offset <- offset + int n
                    elif not (n < 0n && Marshal.GetLastPInvokeError() = EINTR) then failed <- true
                not failed
            finally
                sys_close fd |> ignore

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
                    // 0700 on the log directory — the half that also covers
                    // files the engine never creates (a payload writing its own
                    // log beside ours does so at its umask). A no-op on an
                    // existing directory: an already-loose logs/ is discarded at
                    // deploy, never silently retightened under the user.
                    if not (String.IsNullOrEmpty dir) then
                        Directory.CreateDirectory(dir, PosixTrail.trailDirMode) |> ignore
                    fileReady <- true
                PosixTrail.append filePath (e.ToJson() + Environment.NewLine) |> ignore
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
