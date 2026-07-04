using System.Threading.Channels;

namespace CsharpActors;

// ---------------------------------------------------------------------------
// Core actor abstractions.
//
// An ACTOR owns:
//   * a private MAILBOX (a Channel<object>) — the ONLY way to talk to it,
//   * a single long-running consumer loop that pulls one message at a time,
//   * private state that only its own handler is allowed to mutate.
//
// RUN-TO-COMPLETION: the loop awaits the handler for message N fully before it
// dequeues message N+1. There is never more than one message "in flight", so a
// handler can touch its private state with zero locks — the mailbox serialises
// everything for us. That is the whole trick behind the actor model.
// ---------------------------------------------------------------------------

/// <summary>
/// The behaviour half of an actor: how it reacts to a single message.
/// The runtime (see <see cref="ActorCell"/>) supplies the mailbox + loop, so a
/// concrete actor only has to answer "given this message, mutate my state".
/// </summary>
public interface IActor
{
    /// <summary>A human-readable name used purely for the demo trace.</summary>
    string Name { get; }

    /// <summary>
    /// Handle exactly one message. Because of run-to-completion this is the
    /// only place the actor's private state is read or written, so no locking
    /// is required. Throwing here is how an actor "crashes".
    /// </summary>
    ValueTask Handle(object message, IActorContext context);
}

/// <summary>
/// Handed to an actor while it processes a message. Kept tiny on purpose — it
/// just exposes the actor's own identity for logging.
/// </summary>
public interface IActorContext
{
    string SelfId { get; }
}

/// <summary>
/// Envelope used for the ASK pattern (request/reply). The sender parks a
/// <see cref="TaskCompletionSource{TResult}"/> inside the message; the actor
/// completes it, which wakes up the awaiting caller. This is how we get a
/// return value out of an otherwise fire-and-forget mailbox.
/// </summary>
public sealed class Ask<TReply>
{
    public required object Request { get; init; }
    public required TaskCompletionSource<TReply> Reply { get; init; }
}

/// <summary>
/// Message the child loop posts to its SUPERVISOR's mailbox when it crashes.
/// Crash detection is "just" a try/catch that turns an exception into a normal
/// message — the supervisor then handles EXIT one-at-a-time like anything else.
/// </summary>
public sealed class Exit
{
    public required string ChildId { get; init; }
    public required Exception Error { get; init; }
}
