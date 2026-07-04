namespace CaptainHook.Actors

open System.Threading.Channels
open System.Threading.Tasks

/// What a queued message looks like (an F# record — C# sees a normal class
/// with a constructor and a .Line property).
type AuditEntry = { Line: string }

/// Hot-path actor built on System.Threading.Channels — the SAME BCL type the
/// C# core uses, called from F#. Channels is a .NET runtime library, not a C#
/// feature. Reach for this shape instead of MailboxProcessor when an actor
/// needs a BOUNDED mailbox (backpressure) or high throughput; keep
/// MailboxProcessor for the 95% where ergonomics matter more.
/// `consumerDelayMs` is a testability affordance: the per-item simulated sink
/// latency. The single-arg constructor keeps today's default (1ms per item);
/// tests pass 0 (drain at full speed) or a large value (deterministically full).
type AuditWriter(capacity: int, consumerDelayMs: int) =
    let mailbox =
        Channel.CreateBounded<AuditEntry>(
            BoundedChannelOptions(
                capacity,
                SingleReader = true,                       // exactly one consumer loop = actor semantics
                FullMode = BoundedChannelFullMode.Wait))   // full => WriteAsync AWAITS a slot: backpressure,
                                                           // not OOM (the MailboxProcessor can't do this)

    // Only the consumer loop writes this; the demo reads it after CompleteAsync
    // completes, so the task-completion edge orders the read. (Class-level
    // `let mutable` is a field — closures may mutate it, unlike mutable locals.)
    let mutable processed = 0

    // task { } rather than async { }: Channels speaks Task/ValueTask, and F#'s
    // task CE consumes those natively — THIS is "using Channels from F#".
    // A task CE is hot: the consumer starts as soon as the actor is constructed.
    let consumer =
        task {
            let mutable draining = true
            while draining do
                let! canRead = mailbox.Reader.WaitToReadAsync()   // parks; holds NO thread
                if not canRead then
                    draining <- false                             // writer completed + fully drained
                    // ONE summary event for the whole drain — per-item logging on
                    // a hot path would cost more than the work being logged.
                    Log.Info(
                        "actor:audit-writer", "audit.drain",
                        LogFields(Data = dict [ "count", box processed ]))
                else
                    let mutable keep = true
                    while keep do
                        match mailbox.Reader.TryRead() with
                        | true, _entry ->
                            if consumerDelayMs > 0 then
                                do! Task.Delay consumerDelayMs    // simulate a slow sink (disk/HTTP)
                            processed <- processed + 1
                        | _ ->
                            keep <- false
        }

    /// Default: 1ms per item — the original demo behavior.
    new(capacity: int) = AuditWriter(capacity, 1)

    /// tell with backpressure — when the mailbox is full this AWAITS a free
    /// slot, so a fast producer is slowed to the consumer's pace instead of
    /// growing the queue without bound.
    member _.PostAsync(entry: AuditEntry) : ValueTask = mailbox.Writer.WriteAsync entry

    /// Non-waiting variant: returns false when full (explicit rejection).
    member _.TryPost(entry: AuditEntry) : bool = mailbox.Writer.TryWrite entry

    /// Signal no-more-messages; the returned Task completes once the consumer
    /// has drained everything already queued.
    member _.CompleteAsync() : Task =
        mailbox.Writer.Complete()
        consumer

    member _.Processed = processed
