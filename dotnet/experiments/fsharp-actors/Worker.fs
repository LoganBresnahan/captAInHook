namespace FSharpActors

open System

// -----------------------------------------------------------------------------
// Worker actor: a MailboxProcessor<WorkerMsg> with PRIVATE, IMMUTABLE state.
//
// F#-vs-C# HIGHLIGHT #2 (native mailbox + immutable state threading):
//   C# has no built-in actor; you either pull in Akka.NET / Orleans or hand-roll
//   a `Channel<T>` + a background `while(await reader.ReadAsync())` loop and guard
//   mutable fields. Here the runtime primitive IS the actor: `inbox.Receive()`
//   delivers one message at a time (run-to-completion, no locks), and the private
//   counter is just a parameter `count` threaded through the recursive async
//   loop. State is never mutated in place — each turn produces the next state by
//   calling `loop (newCount)`. Restart = call the factory again => fresh `count`.
// -----------------------------------------------------------------------------

module Worker =

    /// A factory builds a *fresh* worker. Restarting a child is literally
    /// "call the factory again": new MailboxProcessor, new mailbox, counter
    /// back at 0. `supervisor` is threaded in so the child can report its own
    /// crash back as a ChildExit message.
    let create (id: int) (supervisor: MailboxProcessor<SupervisorMsg>) : MailboxProcessor<WorkerMsg> =
        MailboxProcessor.Start(fun inbox ->

            // The recursive loop carries the worker's entire private state
            // (here just `count`) as immutable parameters.
            let rec loop (count: int) =
                async {
                    // Receive blocks (asynchronously) until exactly one message
                    // is available, guaranteeing one-at-a-time processing.
                    let! msg = inbox.Receive()

                    // EXHAUSTIVE match — the compiler checks all WorkerMsg cases
                    // are handled.
                    match msg with
                    | Increment n ->
                        let count' = count + n
                        Log.line (sprintf "    [worker %d] Increment %d  -> count = %d" id n count')
                        return! loop count' // tail-call with the NEW immutable state

                    | GetCount reply ->
                        // "ask" side: answer over the channel embedded in the msg.
                        Log.line (sprintf "    [worker %d] GetCount        -> count = %d" id count)
                        reply.Reply count
                        return! loop count

                    | Poison ->
                        Log.line (sprintf "    [worker %d] Poison received -> throwing!" id)
                        // Raising here escapes the async and is caught below.
                        failwithf "worker %d was poisoned" id
                }

            // Turn a crash INTO A MESSAGE. Wrapping the loop in try/with means an
            // exception in the body does not silently kill the actor: we forward
            // it to the supervisor as ChildExit and let the loop terminate. The
            // supervisor then decides whether to restart (one_for_one).
            async {
                try
                    do! loop 0 // fresh state on every (re)start
                with ex ->
                    supervisor.Post(ChildExit(id, ex))
                // Falling off the end here ends the async => this mailbox stops.
            })
