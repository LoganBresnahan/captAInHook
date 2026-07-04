using Akka.Actor;

namespace AkkaActors;

// ===========================================================================
// Message protocol as immutable RECORDS.
//
// AKKA-vs-hand-rolled HIGHLIGHT #1 (messages are just POCOs):
//   In the hand-rolled C#/F# siblings a "message" is whatever object you post
//   into a Channel / MailboxProcessor, and dispatch is a manual `switch`/`match`
//   inside the loop. Akka is the same idea — a message is any object — but the
//   dispatch is done for you: `ReceiveActor.Receive<T>(handler)` registers one
//   strongly-typed handler per message type, and Akka routes each message to the
//   matching handler. So the message TYPES look identical to the siblings; only
//   the wiring differs.
// ===========================================================================

// ---- Worker protocol -------------------------------------------------------

/// tell: bump this worker's private counter by `By`.
public sealed record Increment(int By = 1);

/// ask: reply to Sender with the current counter value.
public sealed record GetCount();

/// tell: make the worker throw. This is a CUSTOM crash message — deliberately
/// NOT Akka's built-in PoisonPill (which is a *graceful* stop, not a crash).
/// Throwing from the handler is how an actor "fails" and triggers supervision.
public sealed record Boom();

// ---- Supervisor protocol ---------------------------------------------------

/// ask: "hand me references to your two workers so I can talk to them directly."
public sealed record GetChildren();

/// The reply to GetChildren: the two live child IActorRefs.
public sealed record Children(IActorRef Worker1, IActorRef Worker2);
