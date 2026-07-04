using System.Threading.Channels;
using CsharpActors;

// ===========================================================================
// DEMO: one supervisor, two worker actors, each with a private counter.
//
// Script:
//   1. spawn worker-1 and worker-2 under a one_for_one supervisor,
//   2. increment both a few times,
//   3. POISON worker-1 so it throws -> crash -> EXIT -> supervisor restarts it,
//   4. show worker-1's counter reset to 0 while worker-2's counter is intact,
//   5. keep using both to prove the restarted worker-1 is fully alive again.
// ===========================================================================

static void Banner(string s) => Console.WriteLine($"\n=== {s} ===");

// Small helper: give the actors' background loops a beat to drain their
// mailboxes so the printed trace lines up with the narrative. (Real code would
// use ask/await for ordering; sleeps keep the demo output readable.)
static Task Settle() => Task.Delay(120);

Banner("boot supervisor");

// The supervisor is itself an actor, so it needs its own cell + mailbox. We
// build the Supervisor behaviour first, then wire its own mailbox writer back
// into it so children know where to send EXIT.
var supervisor = new Supervisor(maxRestarts: 3, window: TimeSpan.FromSeconds(5));
var supCell = new ActorCell("supervisor", supervisor);
// Bind the supervisor to a writer that reaches ITS OWN cell mailbox so that
// children (wired below) can deliver EXIT messages back to it.
supervisor.BindMailbox(new ForwardingWriter(supCell));

Banner("spawn 2 workers");
await supCell.Tell(new Spawn(new ChildSpec { Id = "worker-1", Factory = () => new Worker("worker-1") }));
await supCell.Tell(new Spawn(new ChildSpec { Id = "worker-2", Factory = () => new Worker("worker-2") }));
await Settle();

var w1 = supervisor.Child("worker-1")!;
var w2 = supervisor.Child("worker-2")!;

Banner("send increments to both workers");
await w1.Tell(new Increment());
await w1.Tell(new Increment());
await w1.Tell(new Increment()); // worker-1 -> 3
await w2.Tell(new Increment());
await w2.Tell(new Increment()); // worker-2 -> 2
await Settle();

Console.WriteLine($"\nstate before crash: worker-1={await w1.Ask<int>(new GetCount())}, " +
                  $"worker-2={await w2.Ask<int>(new GetCount())}");

Banner("POISON worker-1 (should crash + one_for_one restart)");
await w1.Tell(new Poison());
await Settle(); // let the crash -> EXIT -> restart round-trip complete

Banner("verify one_for_one restart");
// Re-fetch worker-1: the supervisor replaced its cell with a fresh one.
var w1b = supervisor.Child("worker-1")!;
var w1Count = await w1b.Ask<int>(new GetCount());
var w2Count = await w2.Ask<int>(new GetCount());
Console.WriteLine($"worker-1 counter after restart = {w1Count}  (expected 0: fresh state)");
Console.WriteLine($"worker-2 counter unaffected    = {w2Count}  (expected 2: never restarted)");

Banner("prove restarted worker-1 still works");
await w1b.Tell(new Increment(By: 5));
await w2.Tell(new Increment());
await Settle();
Console.WriteLine($"\nFINAL: worker-1={await w1b.Ask<int>(new GetCount())} (0+5), " +
                  $"worker-2={await w2.Ask<int>(new GetCount())} (2+1)");

Banner("shutdown");
await supCell.DisposeAsync();
await w1b.DisposeAsync();
await w2.DisposeAsync();
Console.WriteLine("done.");


// ---------------------------------------------------------------------------
// Tiny adapter so the Supervisor can be handed a ChannelWriter<object> that
// actually routes into its ActorCell's mailbox (children send EXIT here).
// ---------------------------------------------------------------------------
sealed class ForwardingWriter : ChannelWriter<object>
{
    private readonly ActorCell _target;
    public ForwardingWriter(ActorCell target) => _target = target;

    public override bool TryWrite(object item)
    {
        _ = _target.Tell(item);
        return true;
    }

    public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(true);

    public override async ValueTask WriteAsync(object item, CancellationToken cancellationToken = default)
        => await _target.Tell(item);
}
