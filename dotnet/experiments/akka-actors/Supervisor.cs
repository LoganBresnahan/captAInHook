using Akka.Actor;

namespace AkkaActors;

// ---------------------------------------------------------------------------
// Supervisor actor: creates its two children and declares HOW to react when one
// of them crashes.
//
// AKKA-vs-hand-rolled HIGHLIGHT #3 (supervision is declarative):
//   In the siblings the Supervisor had to be hand-coded: keep a dictionary of
//   live children + their factories, receive a bespoke "Exit"/"ChildExit"
//   message, prune a restart-timestamp history against a window, decide restart
//   vs. escalate, and rebuild the child from its factory. That is the bulk of
//   their ~430 / ~280 lines.
//
//   In Akka a parent AUTOMATICALLY supervises the children it creates. You do not
//   receive crash messages or rebuild anyone by hand. You just override ONE method
//   that returns a strategy value describing the policy — restart intensity
//   (maxNrOfRetries within a time window) and the per-exception decision — and
//   Akka enforces it, including tearing the child down and reconstructing it from
//   its Props. The whole restart-intensity loop from the siblings collapses into
//   the OneForOneStrategy below.
// ---------------------------------------------------------------------------
public sealed class Supervisor : ReceiveActor
{
    private readonly IActorRef _worker1;
    private readonly IActorRef _worker2;

    public Supervisor()
    {
        // Context.ActorOf makes these children OF this actor, which is precisely
        // what makes THIS actor their supervisor. `Props.Create(() => new Worker())`
        // is the "factory" (the same idea as the siblings' Factory funcs): Akka
        // calls it again to reconstruct a child on restart, giving fresh state.
        _worker1 = Context.ActorOf(Props.Create(() => new Worker()), "worker-1");
        _worker2 = Context.ActorOf(Props.Create(() => new Worker()), "worker-2");
        Trace.Line("[supervisor] created children worker-1 and worker-2 (each count=0)");

        // Hand the two child refs to whoever asks (Program), so it can Tell/Ask
        // the workers directly. (We could instead forward every message through
        // the supervisor; exposing the refs keeps the demo close to the siblings.)
        Receive<GetChildren>(_ => Sender.Tell(new Children(_worker1, _worker2)));
    }

    // The ENTIRE supervision policy, declaratively:
    //   * OneForOne : when a child fails, act on ONLY that child; its siblings keep
    //                 running untouched (contrast with AllForOne, which would
    //                 restart every child).
    //   * maxNrOfRetries / withinTimeRange : the restart-intensity budget. Up to 3
    //                 restarts within any 5s window; exceed it and Akka STOPS the
    //                 child instead of looping forever (the same guard the siblings
    //                 wrote by hand with a timestamp list).
    //   * localOnlyDecider : per-exception decision. Here every exception -> Restart.
    //                 (Other choices: Resume, Stop, Escalate.)
    protected override SupervisorStrategy SupervisorStrategy() =>
        new OneForOneStrategy(
            maxNrOfRetries: 3,
            withinTimeRange: TimeSpan.FromSeconds(5),
            localOnlyDecider: ex =>
            {
                Trace.Line($"[supervisor] one_for_one decision: '{ex.Message}' -> Directive.Restart");
                return Directive.Restart;
            });
}
