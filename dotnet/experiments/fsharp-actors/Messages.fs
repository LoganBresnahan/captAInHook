namespace FSharpActors

// -----------------------------------------------------------------------------
// Message protocol as DISCRIMINATED UNIONS.
//
// F#-vs-C# HIGHLIGHT #1 (DU messages + exhaustive match):
//   In C# an actor "message" is usually an interface (`IMessage`) with one class
//   per case, and dispatch is a `switch` on the runtime type (or a visitor).
//   Nothing forces you to handle every case, and adding a case silently compiles.
//   In F# the whole protocol is ONE closed type. The compiler then makes the
//   `match` in the actor loop EXHAUSTIVE: forget a case and you get a warning at
//   compile time. The message *is* the type; no boilerplate class hierarchy.
// -----------------------------------------------------------------------------

/// Messages a Worker actor understands. `AsyncReplyChannel<'Reply>` is F#'s
/// native "ask" plumbing: the caller's reply slot travels *inside* the message.
type WorkerMsg =
    /// tell: bump this worker's private counter by n
    | Increment of int
    /// ask: reply with the current counter value over the supplied channel
    | GetCount of AsyncReplyChannel<int>
    /// tell: make the worker throw, exercising crash + supervision
    | Poison

/// Messages the Supervisor understands.
and SupervisorMsg =
    /// tell/ask: forward a WorkerMsg to child `id` (routing by id, NOT by a
    /// captured mailbox reference, so a restarted child transparently replaces
    /// the old one in the supervisor's map).
    | Route of id: int * msg: WorkerMsg
    /// internal: a child's body raised an exception and terminated. The child
    /// converts its own crash into this message (see Worker.fs), so the
    /// supervisor observes failures as ordinary mail and handles them
    /// one-at-a-time, in run-to-completion order.
    | ChildExit of id: int * error: exn
