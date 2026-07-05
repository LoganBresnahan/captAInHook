namespace CaptainHook.Actors

open System
open System.Runtime.ExceptionServices
open System.Threading
open System.Threading.Tasks

/// The worker's whole protocol: one request in, one reply out. The reply is a
/// Choice so the ASKER always hears back — either the value or the exception —
/// instead of deducing failure from an ask-timeout. `receipt` is flipped the
/// moment the worker DEQUEUES the message: it lets the ask layer distinguish a
/// wedge (received, never answered) from backlog (still queued behind a busy
/// sibling dispatch) — ADR-0004 decision 5. Internal on purpose: the DU and
/// AsyncReplyChannel never leak past this assembly's boundary.
type internal WorkMsg<'Req, 'Reply> =
    | Work of req: 'Req * receipt: bool ref * reply: AsyncReplyChannel<Choice<'Reply, exn>>

/// How one classified ask ended (ADR-0004 decision 5). A plain enum — C# sees
/// boring .NET, never a DU.
type AskStatus =
    /// Reply arrived: the handler's value.
    | Ok = 0
    /// Reply arrived: the handler's exception (reply-then-crash). An
    /// OperationCanceledException here means the budget token was HONORED.
    | Faulted = 1
    /// Received but never answered within budget + grace. The worker has been
    /// reported to the supervisor (abandon-and-respawn; counts to the window).
    | Wedged = 2
    /// Never received within budget + grace — queued behind a busy sibling.
    /// Backlog, not a defect: nothing is reported, nothing counts.
    | Backlogged = 3
    /// The worker was already escalated; the ask failed fast without waiting.
    | Dead = 4

/// One classified ask outcome. Reply is only meaningful for Ok, Error for Faulted.
type AskOutcome<'Reply> internal (status: AskStatus, reply: 'Reply, error: exn) =
    member _.Status = status
    member _.Reply = reply
    member _.Error = error

/// A GENERIC supervised request/reply worker around a plain .NET delegate.
///
/// Why generic? The dependency arrow points C# host -> F# lib, so this
/// assembly can never see the host's domain types (HookEvent, Effect, ...).
/// The worker therefore knows NOTHING about hooks: 'Req and 'Reply flow
/// through opaquely and only the delegate the host supplies ever looks
/// inside them. Rich actor machinery inside, boring .NET surface outside —
/// the same interop seam pattern as Counter.
type Worker<'Req, 'Reply> private (sup: Supervisor, id: string, handle: ActorRef<WorkMsg<'Req, 'Reply>>) =

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
                        | Work (req, receipt, rc) ->
                            // Mark receipt BEFORE running the handler: from here
                            // on, "no reply" means wedged, not backlogged.
                            Volatile.Write(&receipt.contents, true)
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
                                //      supervisor classifies it (an OCE — budget
                                //      token honored — restarts WITHOUT counting;
                                //      anything else counts toward escalation).
                                // AsyncReplyChannel.Reply is one-shot: reply exactly
                                // once, and BEFORE the raise (nothing runs after it).
                                rc.Reply(Choice2Of2 ex)
                                return raise ex
                    }
                loop ())
        Worker(sup, id, sup.Spawn(id, factory))

    /// ask — send one request, await the reply as a Task so C# can `await` it.
    /// A handler exception is rethrown HERE with its original stack preserved
    /// (ExceptionDispatchInfo), so to the caller it looks exactly like awaiting
    /// the handler directly. A dead (escalated) or overslow worker surfaces as
    /// TimeoutException from ActorRef.Ask instead. The plain, unclassified ask
    /// — dispatch uses AskClassifiedAsync.
    member _.AskAsync(req: 'Req, timeoutMs: int) : Task<'Reply> =
        task {
            let! outcome = handle.Ask((fun rc -> Work(req, ref false, rc)), timeoutMs)
            match outcome with
            | Choice1Of2 v -> return v
            | Choice2Of2 ex ->
                ExceptionDispatchInfo.Capture(ex).Throw()
                // Unreachable — Throw() never returns — but the type checker
                // needs every branch to produce a 'Reply.
                return Unchecked.defaultof<'Reply>
        }

    /// The classified ask (ADR-0004 decision 5). Waits budget + grace: the
    /// grace exists so a token-honoring handler's cancellation reply — which
    /// leaves the handler AT the budget and arrives a beat later — lands
    /// INSIDE the window as Faulted(OperationCanceledException) instead of
    /// racing the deadline; a true no-reply timeout is then unambiguous and is
    /// classified by the receipt flag: received-but-silent = Wedged (reported
    /// to the supervisor, which abandons the worker and counts it),
    /// never-received = Backlogged (a busy sibling's queue — no report, no
    /// count). A dead handle fails fast without posting. `correlationId` is an
    /// opaque string for the trail (the host passes its dispatchId).
    member _.AskClassifiedAsync(req: 'Req, budgetMs: int, graceMs: int, correlationId: string) : Task<AskOutcome<'Reply>> =
        task {
            if handle.IsDead then
                return AskOutcome<'Reply>(AskStatus.Dead, Unchecked.defaultof<'Reply>, null)
            else
                let receipt = ref false
                let gen = handle.Generation
                let windowMs = budgetMs + graceMs
                // The mailbox-level timeout sits far beyond our own window:
                // THIS layer owns the deadline; the inner one is only a
                // never-leak backstop for the abandoned task.
                let replyTask = handle.Ask((fun rc -> Work(req, receipt, rc)), windowMs + 60_000)
                let! winner = Task.WhenAny(replyTask :> Task, Task.Delay windowMs)
                if obj.ReferenceEquals(winner, replyTask :> Task) then
                    match! replyTask with
                    | Choice1Of2 v -> return AskOutcome<'Reply>(AskStatus.Ok, v, null)
                    | Choice2Of2 ex -> return AskOutcome<'Reply>(AskStatus.Faulted, Unchecked.defaultof<'Reply>, ex)
                else
                    // We are abandoning replyTask; observe its eventual fault so
                    // it can never surface as an unobserved task exception.
                    replyTask.ContinueWith(
                        (fun (t: Task<Choice<'Reply, exn>>) -> t.Exception |> ignore),
                        TaskContinuationOptions.OnlyOnFaulted) |> ignore
                    if Volatile.Read(&receipt.contents) then
                        sup.ReportWedge(id, gen, correlationId)
                        return AskOutcome<'Reply>(AskStatus.Wedged, Unchecked.defaultof<'Reply>, null)
                    else
                        return AskOutcome<'Reply>(AskStatus.Backlogged, Unchecked.defaultof<'Reply>, null)
        }
