using System.Diagnostics;
using CaptainHook.Actors;

namespace CaptainHook.Tests;

public class HotPathTests
{
    private static AuditEntry Entry(int i) => new($"audit line {i}");

    [Fact]
    public async Task EveryPostedEntry_IsProcessedAfterComplete()
    {
        // consumerDelayMs: 0 -> drain at full speed; this test is about
        // completeness, not pacing.
        var writer = new AuditWriter(capacity: 8, consumerDelayMs: 0);

        const int n = 500;
        for (var i = 0; i < n; i++)
            await writer.PostAsync(Entry(i));

        // CompleteAsync's task completes only once the consumer has drained
        // everything already queued — nothing may be dropped.
        await writer.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(n, writer.Processed);
    }

    [Fact]
    public void TryPost_ReturnsFalse_WhenMailboxIsFull()
    {
        const int capacity = 4;
        // A huge per-item delay pins the consumer inside its first item, so the
        // buffer deterministically fills: at most `capacity` queued + 1 in flight.
        var writer = new AuditWriter(capacity, consumerDelayMs: 60_000);

        var accepted = 0;
        var sawRejection = false;
        for (var i = 0; i < capacity + 5; i++)
        {
            if (writer.TryPost(Entry(i))) accepted++;
            else { sawRejection = true; break; }
        }

        // Explicit rejection instead of unbounded growth — the whole point of a
        // bounded mailbox. (We deliberately do NOT CompleteAsync here; draining
        // at 60s/item is not something a test should wait on.)
        Assert.True(sawRejection, "TryPost never returned false on a full mailbox");
        Assert.InRange(accepted, capacity, capacity + 1);
    }

    [Fact]
    public async Task PostAsync_AppliesBackpressure_SlowConsumerSlowsProducer()
    {
        const int capacity = 4;
        const int n = 40;
        const int delayMs = 5;
        var writer = new AuditWriter(capacity, delayMs);

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < n; i++)
            await writer.PostAsync(Entry(i));   // awaits a slot once the buffer fills
        sw.Stop();

        // Producing 40 items into a 4-slot mailbox drained at >=5ms/item cannot
        // finish instantly: ~35 writes each wait for a slot (~175ms+ in theory).
        // Assert a conservative floor so the test is CI-safe, not tuned-to-fail.
        Assert.True(sw.ElapsedMilliseconds >= 100,
            $"expected backpressure to slow posting to >=100ms, took {sw.ElapsedMilliseconds}ms");

        await writer.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(n, writer.Processed);
    }
}
