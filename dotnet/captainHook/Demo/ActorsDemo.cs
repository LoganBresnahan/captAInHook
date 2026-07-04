using System.Diagnostics;
using CaptainHook.Actors;

namespace CaptainHook.Demo;

/// Drives the F# actor layer from the C# host — proving the ProjectReference
/// interop is real: supervised MailboxProcessor actors for the 95% path, a
/// bounded Channels actor for the hot path, all consumed as ordinary .NET types.
public static class ActorsDemo
{
    public static async Task RunAsync()
    {
        Console.WriteLine("=== F# actor layer driven from C# ===\n");

        // ---- supervised counter: MailboxProcessor + one_for_one supervisor ----
        var sup = new Supervisor("root", maxRestarts: 3, window: TimeSpan.FromSeconds(5));
        sup.OnEscalated = (id, ex) => Console.Error.WriteLine($"[host] ESCALATED by supervisor: {id}: {ex.Message}");

        var counter = Counter.Supervised(sup, "counter-1");
        counter.Increment(3);
        Console.WriteLine($"count before crash      = {await counter.GetCountAsync()}   (expect 3)");

        counter.Boom();                 // crash -> supervisor restarts from the factory
        await Task.Delay(200);          // let the crash->EXIT->restart round-trip land

        Console.WriteLine($"count after restart     = {await counter.GetCountAsync()}   (expect 0 — fresh state)");
        counter.Increment(5);
        Console.WriteLine($"restarted actor works   = {await counter.GetCountAsync()}   (expect 5)");

        // ---- hot path: bounded Channels actor (backpressure made visible) ----
        Console.WriteLine("\n=== bounded Channels actor: capacity 8, slow consumer, 64 posts ===");
        var audit = new AuditWriter(capacity: 8);
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 64; i++)
            await audit.PostAsync(new AuditEntry($"event {i}"));   // awaits whenever the mailbox is full
        sw.Stop();
        await audit.CompleteAsync();

        Console.WriteLine($"posted 64 in {sw.ElapsedMilliseconds}ms — producer was THROTTLED to consumer pace (unbounded would return ~instantly and pile up in memory)");
        Console.WriteLine($"audit entries processed = {audit.Processed}   (expect 64 — none dropped)");
    }
}
