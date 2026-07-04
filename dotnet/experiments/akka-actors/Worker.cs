using Akka.Actor;

namespace AkkaActors;

// ---------------------------------------------------------------------------
// Worker actor: a ReceiveActor holding PRIVATE state — a single int counter.
//
// AKKA-vs-hand-rolled HIGHLIGHT #2 (the runtime IS the actor):
//   In the siblings you had to BUILD the actor runtime yourself: a Channel /
//   MailboxProcessor for the mailbox, a background loop that reads one message at
//   a time (run-to-completion), and a try/catch that turns a thrown exception
//   into an "Exit"/"ChildExit" message for the supervisor. Akka supplies ALL of
//   that. You subclass ReceiveActor, register `Receive<T>` handlers, and get:
//     * a mailbox + single-threaded run-to-completion loop (so `_count` needs no
//       lock — only one message is ever in flight),
//     * automatic crash capture: throwing here is caught by Akka and reported to
//       THIS actor's parent as a failure (no hand-written Exit plumbing),
//     * lifecycle hooks (PreStart/PreRestart/PostRestart/PostStop) so you can see
//       and shape the restart.
// ---------------------------------------------------------------------------
public sealed class Worker : ReceiveActor
{
    // PRIVATE per-actor state. A restart throws this whole object away and builds
    // a fresh Worker from its Props, so `_count` is naturally reset to 0 — exactly
    // like "call the factory again" in the hand-rolled siblings.
    private int _count;

    // The actor's name lives on its path (assigned by the parent at ActorOf time),
    // so we read it back for the trace instead of passing it into the constructor.
    private string Name => Self.Path.Name;

    public Worker()
    {
        // Runs on first start AND on every restart (a brand-new instance is built).
        Trace.Line($"    [{Name}] ctor: new instance, fresh state (count={_count})");

        // One handler per message type. Akka routes by runtime type for us.
        Receive<Increment>(m =>
        {
            _count += m.By;
            Trace.Line($"    [{Name}] Increment(+{m.By}) -> count={_count}");
        });

        Receive<GetCount>(_ =>
        {
            // "ask" side: reply to whoever asked. Akka threads the reply address
            // (Sender) through for us — no reply channel embedded in the message.
            Trace.Line($"    [{Name}] GetCount -> {_count}");
            Sender.Tell(_count);
        });

        Receive<Boom>(_ =>
        {
            // Crash on purpose. We just THROW — Akka catches it and escalates the
            // failure to our parent (the Supervisor), which applies its strategy.
            Trace.Line($"    [{Name}] Boom received -> throwing!");
            throw new InvalidOperationException($"{Name} was poisoned at count={_count}");
        });
    }

    // ---- Lifecycle hooks: make the restart visible in the trace --------------

    protected override void PreStart() =>
        Trace.Line($"    [{Name}] PreStart (now accepting messages)");

    // Called on the FAILED instance, just before it is discarded. `_count` here is
    // still the pre-crash value, proving the about-to-be-lost state.
    protected override void PreRestart(Exception reason, object message)
    {
        Trace.Line($"    [{Name}] PreRestart: discarding state (count was {_count}) due to: {reason.Message}");
        base.PreRestart(reason, message); // default: stop children + call PostStop
    }

    // Called on the FRESH instance, just after Akka rebuilds it. `_count` is 0.
    protected override void PostRestart(Exception reason)
    {
        Trace.Line($"    [{Name}] PostRestart: restarted with fresh state (count={_count})");
        base.PostRestart(reason); // default: call PreStart
    }

    protected override void PostStop() =>
        Trace.Line($"    [{Name}] PostStop (instance torn down)");
}
