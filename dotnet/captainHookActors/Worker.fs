namespace CaptainHook.Actors

open System
open System.Runtime.ExceptionServices
open System.Threading.Tasks

/// The worker's whole protocol: one request in, one reply out. The reply is a
/// Choice so the ASKER always hears back — either the value or the exception —
/// instead of deducing failure from an ask-timeout. Internal on purpose: the
/// DU and AsyncReplyChannel never leak past this assembly's boundary.
type internal WorkMsg<'Req, 'Reply> =
    | Work of req: 'Req * reply: AsyncReplyChannel<Choice<'Reply, exn>>

/// A GENERIC supervised request/reply worker around a plain .NET delegate.
///
/// Why generic? The dependency arrow points C# host -> F# lib, so this
/// assembly can never see the host's domain types (HookEvent, Effect, ...).
/// The worker therefore knows NOTHING about hooks: 'Req and 'Reply flow
/// through opaquely and only the delegate the host supplies ever looks
/// inside them. Rich actor machinery inside, boring .NET surface outside —
/// the same interop seam pattern as Counter.
type Worker<'Req, 'Reply> private (handle: ActorRef<WorkMsg<'Req, 'Reply>>) =

    /// Spawn a worker under `sup`. `handlerFactory` is the OTP child-spec:
    /// it runs once now and once per restart, and each run must yield a FRESH
    /// delegate — so a restart resets the handler's state, not just its
    /// mailbox. (A factory that returns a captured singleton opts out of the
    /// state reset; that is the caller's documented choice.)
    static member Supervised
        (sup: Supervisor, id: string, handlerFactory: Func<Func<'Req, Task<'Reply>>>) : Worker<'Req, 'Reply> =
        let factory () =
            // Fresh delegate FIRST (fresh state), then a fresh mailbox around it.
            let handler = handlerFactory.Invoke()
            MailboxProcessor.Start(fun inbox ->
                let rec loop () =
                    async {
                        match! inbox.Receive() with
                        | Work (req, rc) ->
                            // Async.Catch also captures a delegate that throws
                            // SYNCHRONOUSLY (before returning a Task), because
                            // the Invoke happens inside the wrapped async.
                            let! outcome =
                                Async.Catch(async { return! handler.Invoke req |> Async.AwaitTask })
                            match outcome with
                            | Choice1Of2 v ->
                                rc.Reply(Choice1Of2 v)
                                return! loop ()
                            | Choice2Of2 ex ->
                                // REPLY-THEN-CRASH, in that order:
                                //   1. Reply(error) — the asker learns the outcome
                                //      immediately instead of burning its full ask
                                //      timeout waiting on a corpse.
                                //   2. raise — the crash still escapes the loop, so
                                //      MailboxProcessor.Error fires and the
                                //      supervisor restarts us with fresh state.
                                // AsyncReplyChannel.Reply is one-shot: reply exactly
                                // once, and BEFORE the raise (nothing runs after it).
                                rc.Reply(Choice2Of2 ex)
                                return raise ex
                    }
                loop ())
        Worker(sup.Spawn(id, factory))

    /// ask — send one request, await the reply as a Task so C# can `await` it.
    /// A handler exception is rethrown HERE with its original stack preserved
    /// (ExceptionDispatchInfo), so to the caller it looks exactly like awaiting
    /// the handler directly. A dead (escalated) or overslow worker surfaces as
    /// TimeoutException from ActorRef.Ask instead.
    member _.AskAsync(req: 'Req, timeoutMs: int) : Task<'Reply> =
        task {
            let! outcome = handle.Ask((fun rc -> Work(req, rc)), timeoutMs)
            match outcome with
            | Choice1Of2 v -> return v
            | Choice2Of2 ex ->
                ExceptionDispatchInfo.Capture(ex).Throw()
                // Unreachable — Throw() never returns — but the type checker
                // needs every branch to produce a 'Reply.
                return Unchecked.defaultof<'Reply>
        }
