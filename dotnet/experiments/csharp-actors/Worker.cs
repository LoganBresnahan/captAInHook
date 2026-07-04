namespace CsharpActors;

// ---------------------------------------------------------------------------
// A trivial worker actor holding PRIVATE state: a counter. Only Handle() ever
// touches _count, and Handle() runs one-message-at-a-time, so the field needs
// no locking despite being mutated from a background loop.
// ---------------------------------------------------------------------------

// Messages the worker understands (tell-style).
public sealed record Increment(int By = 1);
public sealed record Poison; // makes the worker throw -> crash -> EXIT

// Ask-style request: "what is your current count?" (used via ActorCell.Ask).
public sealed record GetCount;

public sealed class Worker : IActor
{
    private int _count; // PRIVATE per-actor state. Reset to 0 on every restart,
                        // because restart builds a brand-new Worker via the factory.

    public string Name { get; }

    public Worker(string name) => Name = name;

    public ValueTask Handle(object message, IActorContext context)
    {
        switch (message)
        {
            case Increment inc:
                _count += inc.By;
                Console.WriteLine($"    [{Name}] increment(+{inc.By}) -> count={_count}");
                break;

            case Poison:
                // Crash on purpose. The ActorCell's try/catch turns this thrown
                // exception into an EXIT message to the supervisor.
                Console.WriteLine($"    [{Name}] received POISON -> throwing!");
                throw new InvalidOperationException($"{Name} was poisoned at count={_count}");

            case Ask<int> ask when ask.Request is GetCount:
                // ASK/reply: hand the current count back to the awaiting caller.
                ask.Reply.SetResult(_count);
                break;
        }

        return ValueTask.CompletedTask;
    }
}
