using Akka.Actor;
using AkkaActors;

// ===========================================================================
// DEMO: one supervisor, two worker actors, each with a private counter.
// This is the SAME script as the two hand-rolled siblings so all three can be
// compared side by side:
//
//   1. spawn worker-1 and worker-2 under a one_for_one supervisor,
//   2. increment both a few times,
//   3. send Boom to worker-1 -> it throws -> Akka restarts ONLY it,
//   4. verify worker-1's counter reset to 0 while worker-2's is intact,
//   5. keep using both to prove the restarted worker-1 is fully alive again.
//
// Note: Akka logs the caught exception itself (an [ERROR]/[INFO] line with a
// stack trace) when it restarts the child — that framework noise is EXPECTED.
// Our own trace lines are all prefixed ("=== ... ===", "[supervisor]",
// "    [worker-1]", "[main]") so the narrative stays easy to read.
// ===========================================================================

// The ActorSystem is the runtime/container for all actors. Creating it spins up
// Akka's dispatcher threads, scheduler, event stream and logging — this is the
// startup weight the siblings don't have (they used raw tasks). One per process.
using var system = ActorSystem.Create("captainHook");

// Give the async crash -> restart hop a beat to finish so the printed trace
// (including Akka's own async log lines) lands in a readable order before we
// verify. Akka also guarantees per-sender FIFO ordering, so correctness does not
// depend on this delay — it is purely for a clean, deterministic-looking trace.
static Task Settle() => Task.Delay(400);

var ask = TimeSpan.FromSeconds(2); // ask timeout

Trace.Banner("boot supervisor");
// The supervisor is a top-level actor (child of the system's user guardian).
var supervisor = system.ActorOf(Props.Create(() => new Supervisor()), "supervisor");

Trace.Banner("reach the 2 workers");
// Ask the supervisor for its children so we can address the workers directly.
var children = await supervisor.Ask<Children>(new GetChildren(), ask);
var w1 = children.Worker1;
var w2 = children.Worker2;

Trace.Banner("send increments to both workers");
w1.Tell(new Increment()); // +1
w1.Tell(new Increment()); // +1
w1.Tell(new Increment()); // +1  -> worker-1 = 3
w2.Tell(new Increment()); // +1
w2.Tell(new Increment()); // +1  -> worker-2 = 2

// Ask is ordered behind the Tells above (same sender -> same actor is FIFO),
// so these read the settled counts.
Trace.Line($"[main] state before crash: worker-1={await w1.Ask<int>(new GetCount(), ask)}, " +
           $"worker-2={await w2.Ask<int>(new GetCount(), ask)}");

Trace.Banner("send Boom to worker-1 (should crash + one_for_one restart)");
w1.Tell(new Boom());
await Settle(); // let crash -> supervisor decision -> restart round-trip complete

Trace.Banner("verify one_for_one restart");
// The IActorRef `w1` is STABLE across the restart: Akka swapped the actor
// instance behind it, so we keep using the same reference.
var w1Count = await w1.Ask<int>(new GetCount(), ask);
var w2Count = await w2.Ask<int>(new GetCount(), ask);
Trace.Line($"[main] worker-1 counter after restart = {w1Count}  (expected 0 -> RESET by restart)");
Trace.Line($"[main] worker-2 counter unaffected    = {w2Count}  (expected 2 -> PRESERVED)");

Trace.Banner("prove restarted worker-1 still works");
w1.Tell(new Increment(By: 5)); // worker-1: 0 -> 5
w2.Tell(new Increment());      // worker-2: 2 -> 3
var w1Final = await w1.Ask<int>(new GetCount(), ask);
var w2Final = await w2.Ask<int>(new GetCount(), ask);
Trace.Line($"[main] FINAL: worker-1={w1Final} (0+5), worker-2={w2Final} (2+1)");

var ok = w1Count == 0 && w2Count == 2;
Trace.Line($"\n[main] one_for_one restart verified: {ok}");

Trace.Banner("shutdown");
await system.Terminate(); // graceful stop: all actors get PostStop
Trace.Line("[main] done.");

// Non-zero exit if the invariant failed, so the demo is CI-checkable.
Environment.Exit(ok ? 0 : 1);
