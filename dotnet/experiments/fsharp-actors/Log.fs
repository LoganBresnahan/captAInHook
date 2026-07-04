namespace FSharpActors

open System

// -----------------------------------------------------------------------------
// Tiny synchronized logger. Actors run concurrently, so raw `printfn` (which
// issues several Console writes per line) can interleave mid-line. We serialize
// whole lines behind one lock purely so the demo TRACE is readable. This is a
// console concern, not part of the actor model itself.
// -----------------------------------------------------------------------------
module Log =
    let private gate = obj ()

    /// Print one whole line atomically. Usage: Log.line (sprintf "...")
    let line (text: string) =
        lock gate (fun () -> Console.Out.WriteLine text)
