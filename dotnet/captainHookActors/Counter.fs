namespace CaptainHook.Actors

open System.Threading.Tasks

/// One closed DU per actor = the whole message protocol in one place, and the
/// compiler enforces every handler match is EXHAUSTIVE — add a case and any
/// loop that forgot it fails to build. (This is what C#'s object+switch can't do.)
type CounterMsg =
    | Increment of int
    | GetCount of AsyncReplyChannel<int>
    | Boom

module private CounterBody =
    /// Factory: every call = fresh state + new mailbox. The supervisor re-runs
    /// this on restart, so "restart resets state" falls out for free.
    let create (id: string) : MailboxProcessor<CounterMsg> =
        MailboxProcessor.Start(fun inbox ->
            // State threaded IMMUTABLY through the recursive loop — no fields,
            // no locks; one message handled to completion at a time.
            let rec loop (count: int) =
                async {
                    match! inbox.Receive() with
                    | Increment n ->
                        Log.Debug(
                            sprintf "actor:%s" id, "counter.increment",
                            LogFields(ActorId = id, Data = dict [ "n", box n; "count", box (count + n) ]))
                        return! loop (count + n)
                    | GetCount rc ->
                        rc.Reply count
                        return! loop count
                    | Boom ->
                        Log.Warn(
                            sprintf "actor:%s" id, "counter.boom",
                            LogFields(ActorId = id, Msg = sprintf "poison pill at count=%d -> throwing" count))
                        // Uncaught throw kills the agent; the supervisor sees it
                        // via .Error and restarts us from the factory.
                        return invalidOp (sprintf "%s was poisoned at count=%d" id count)
                }
            loop 0)

/// C#-friendly facade — THE interop seam pattern: the DU, AsyncReplyChannel,
/// and F# funcs stay implementation details inside this assembly; C# sees plain
/// methods and Tasks. Rich types inside, boring .NET surface at the boundary.
type Counter private (handle: ActorRef<CounterMsg>) =
    /// Create a counter supervised by `sup` (crash -> one_for_one restart).
    static member Supervised(sup: Supervisor, id: string) : Counter =
        Counter(sup.Spawn(id, fun () -> CounterBody.create id))

    member _.Increment(n: int) = handle.Post(Increment n)
    member _.Boom() = handle.Post Boom
    member _.GetCountAsync() : Task<int> = handle.Ask(fun rc -> GetCount rc)
