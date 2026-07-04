module FSharpActors.Program

open System
open FSharpActors
open FSharpActors.Supervisor

// -----------------------------------------------------------------------------
// Demo: mirror of the C# sibling so the two can be compared directly.
//
//   * a supervisor spawns 2 workers, each with a PRIVATE counter
//   * we send increments to both
//   * a Poison message makes worker #1 throw
//   * the supervisor restarts ONLY worker #1 (counter resets to 0) while
//     worker #2 keeps its counter (one_for_one)
//   * we print a clear, labeled trace throughout
// -----------------------------------------------------------------------------

/// tell: fire-and-forget a WorkerMsg at child `id` via the supervisor.
let tell (sup: MailboxProcessor<SupervisorMsg>) id msg =
    sup.Post(Route(id, msg))

/// ask: request child `id`'s counter and block for the reply. The reply channel
/// is created by PostAndReply and travels inside GetCount, so the worker answers
/// the ORIGINAL caller directly. (Synchronous here purely to keep the demo trace
/// deterministic.)
let askCount (sup: MailboxProcessor<SupervisorMsg>) id : int =
    sup.PostAndReply(fun reply -> Route(id, GetCount reply))

/// Give the async crash -> ChildExit -> restart hop time to settle before the
/// next phase, so the printed trace stays in a readable order.
let settle () = Threading.Thread.Sleep 250

[<EntryPoint>]
let main _ =
    Log.line (sprintf "== F# MailboxProcessor actors + one_for_one supervision ==")

    // One line to describe the whole supervised set. Restart == re-run Factory.
    let specs =
        [ { Id = 1; Factory = Worker.create }
          { Id = 2; Factory = Worker.create } ]

    let sup = Supervisor.start specs
    Log.line (sprintf "\n[main] supervisor started with children 1 and 2 (counters = 0)\n")

    // --- Phase 1: send increments to both workers -------------------------
    Log.line (sprintf "[main] --- phase 1: increments ---")
    tell sup 1 (Increment 5)
    tell sup 1 (Increment 3)
    tell sup 2 (Increment 10)
    settle ()
    Log.line (sprintf "[main] worker 1 count = %d" (askCount sup 1))
    Log.line (sprintf "[main] worker 2 count = %d" (askCount sup 2))

    // --- Phase 2: poison worker #1 (it throws) ----------------------------
    Log.line (sprintf "\n[main] --- phase 2: poison worker 1 ---")
    tell sup 1 Poison
    settle () // let crash -> EXIT -> one_for_one restart happen

    // --- Phase 3: observe one_for_one outcome -----------------------------
    Log.line (sprintf "\n[main] --- phase 3: state after restart ---")
    let c1 = askCount sup 1 // fresh worker => 0
    let c2 = askCount sup 2 // untouched  => 10
    Log.line (sprintf "[main] worker 1 count = %d  (expected 0  -> RESET by restart)" c1)
    Log.line (sprintf "[main] worker 2 count = %d  (expected 10 -> PRESERVED)" c2)

    // Prove worker #1 is a working, fresh actor post-restart.
    tell sup 1 (Increment 100)
    settle ()
    Log.line (sprintf "[main] worker 1 count = %d  (after +100 on the restarted actor)" (askCount sup 1))

    let ok = c1 = 0 && c2 = 10
    Log.line (sprintf "\n[main] one_for_one restart verified: %b" ok)
    if ok then 0 else 1
