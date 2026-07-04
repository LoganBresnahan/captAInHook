namespace FSharpActors

open System

// -----------------------------------------------------------------------------
// Supervisor actor: itself a MailboxProcessor, holding a map of live children
// and their factories. Implements a ONE_FOR_ONE restart strategy with a restart
// intensity budget (max 3 restarts within 5s => escalate / give up).
//
// F#-vs-C# HIGHLIGHT #3 (immutable supervisor state + selective concerns):
//   The supervisor's own state (which children are alive, recent restart times)
//   is threaded immutably through its loop exactly like the worker's counter.
//   No shared mutable dictionary, no lock: mail arrives one at a time, so
//   ChildExit is handled to completion before the next message. A child crash is
//   just another case in the same exhaustive `match`.
// -----------------------------------------------------------------------------

module Supervisor =

    /// How a supervisor knows to (re)build a child: its id + a factory that,
    /// given the supervisor mailbox, produces a brand-new worker.
    type ChildSpec =
        { Id: int
          Factory: int -> MailboxProcessor<SupervisorMsg> -> MailboxProcessor<WorkerMsg> }

    // Restart intensity budget.
    let private maxRestarts = 3
    let private withinWindow = TimeSpan.FromSeconds 5.0

    /// Start a supervisor for the given child specs. Returns the supervisor
    /// mailbox; callers interact with children by posting `Route(id, msg)`.
    let start (specs: ChildSpec list) : MailboxProcessor<SupervisorMsg> =
        MailboxProcessor.Start(fun inbox ->

            // Spawn the initial children. Each factory captures `inbox` (this
            // supervisor) so children can report crashes back to us.
            let spawnAll () =
                specs
                |> List.map (fun spec -> spec.Id, spec.Factory spec.Id inbox)
                |> Map.ofList

            let specById =
                specs |> List.map (fun s -> s.Id, s) |> Map.ofList

            // Supervisor state is immutable:
            //   children     : id -> current live worker mailbox
            //   restartTimes : recent restart timestamps (for the intensity budget)
            let rec loop (children: Map<int, MailboxProcessor<WorkerMsg>>)
                         (restartTimes: DateTime list) =
                async {
                    let! msg = inbox.Receive()

                    match msg with
                    | Route (id, workerMsg) ->
                        match Map.tryFind id children with
                        | Some child -> child.Post workerMsg
                        | None -> Log.line (sprintf "[supervisor] no live child %d to route to" id)
                        return! loop children restartTimes

                    | ChildExit (id, error) ->
                        Log.line (sprintf "[supervisor] EXIT from child %d: %s" id error.Message)

                        // Prune restart history to just the last `withinWindow`.
                        let now = DateTime.UtcNow
                        let recent =
                            restartTimes
                            |> List.filter (fun t -> now - t < withinWindow)

                        if List.length recent >= maxRestarts then
                            // Intensity budget blown: ESCALATE / give up. We stop
                            // the offending child from being resurrected. (In a
                            // full OTP tree this would terminate the supervisor and
                            // propagate to *its* supervisor.)
                            Log.line (sprintf "[supervisor] restart intensity exceeded (%d in %gs) -> GIVING UP on child %d"
                                          maxRestarts withinWindow.TotalSeconds id)
                            let children' = Map.remove id children
                            return! loop children' recent
                        else
                            // ONE_FOR_ONE: restart ONLY the failed child. All other
                            // children keep running with their state intact.
                            match Map.tryFind id specById with
                            | Some spec ->
                                Log.line (sprintf "[supervisor] restarting child %d (one_for_one) -> fresh state" id)
                                let fresh = spec.Factory spec.Id inbox
                                let children' = Map.add id fresh children
                                return! loop children' (now :: recent)
                            | None ->
                                return! loop (Map.remove id children) recent
                }

            loop (spawnAll ()) [])
