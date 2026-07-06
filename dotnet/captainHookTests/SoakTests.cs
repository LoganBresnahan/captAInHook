using System.Text;
using System.Text.Json;
using CaptainHook.Core;
using CaptainHook.Wire;
using static CaptainHook.Tests.TestUtil;

namespace CaptainHook.Tests;

// concurrency-audit-and-soak (ADR-0004, terminal gate): the Dispatcher only
// ever ran ONE dispatch per process in production before the daemon; these
// tests run the full stack — ShimClient -> socket -> DaemonHost -> classified
// asks -> supervised workers -> frame relay — under sustained concurrency and
// assert the properties the architecture promises:
//   * golden wire: the bytes a shim relays are EXACTLY the bytes the adapter
//     serialized (extending HarnessTests' golden bytes across the round trip);
//   * per-worker serialization is load-bearing: a stateful handler with a bare
//     `++` sees NO lost update across 200 concurrent dispatches — the injected
//     values form a perfect permutation of 1..N;
//   * the shared background queue keeps count under load and drains to zero;
//   * supervision under load: a crashing sibling escalates and fast-fails
//     without poisoning anyone else's effects.

public class SoakTests
{
    private static string NoHarness => "/tmp/chk-none-" + Guid.NewGuid().ToString("N")[..8];

    [Fact]
    public async Task GoldenWire_RoundTrip_BytesAreExactlyTheAdapterOutput()
    {
        // Deterministic effect (no timestamps): what the claude-hook-json
        // adapter serializes in-process must be byte-for-byte what arrives
        // through daemon + frame + shim relay.
        var reg = new Registry().On("UserPromptSubmit",
            TestHandler.Returning("fixed", new Effect.Inject("golden wire — café ✓ 🪝")));

        using var dir = new TempRuntimeDir();
        using var stop = new CancellationTokenSource();
        var daemon = Task.Run(() => DaemonHost.RunAsync(dir.Paths, NoHarness, stop.Token, reg));
        await PollUntilAsync(async () =>
            await ShimClient.TryForwardAsync(dir.Paths.SocketPath,
                new HookRequest("gw000000", "user-prompt-submit", "claude-code", "{}"u8.ToArray()))
                is ForwardOutcome.Answered,
            TimeSpan.FromSeconds(15), "daemon up");

        var outcome = await ShimClient.TryForwardAsync(dir.Paths.SocketPath,
            new HookRequest("gw000001", "user-prompt-submit", "claude-code", "{}"u8.ToArray()));
        var a = Assert.IsType<ForwardOutcome.Answered>(outcome);

        // The in-process truth, computed the same way HookRun/DaemonHost do.
        var spec = HarnessTestUtil.EmbeddedOnlyRegistry().Get("claude-code");
        var evt = new HookEvent("UserPromptSubmit", null, null, JsonDocument.Parse("{}").RootElement);
        var expected = ResponseAdapters.Get(spec.ResponseAdapter)
            .Serialize(evt, new Effect.Inject("golden wire — café ✓ 🪝"));

        Assert.Equal(Encoding.UTF8.GetBytes(expected), a.StdoutBytes);

        stop.Cancel();
        Assert.Equal(0, await daemon.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    /// Stateful on purpose, with a BARE increment: any lost update under
    /// concurrency would produce duplicate or skipped values. The mailbox is
    /// the only thing standing between this and a data race — that is the
    /// load-bearing claim being soaked.
    private sealed class SequenceHandler : IHandler
    {
        private int _n;
        public string Name => "seq";
        public FailMode OnFailure => FailMode.Open;
        public Task<Effect> HandleAsync(HookEvent e, HandlerContext ctx) =>
            Task.FromResult<Effect>(new Effect.Inject($"n={++_n}"));
    }

    [Fact]
    public async Task Soak_200ConcurrentDispatches_SerializationHoldsQueueDrainsSupervisionSurvives()
    {
        const int N = 200;
        const int Parallelism = 16;

        var bgRuns = 0;
        var crashes = 0;
        var reg = new Registry()
            .On("UserPromptSubmit", "seq", () => new SequenceHandler())
            .On("UserPromptSubmit", new TestHandler("bg", (_, _) =>
                Task.FromResult<Effect>(new Effect.Background(_ =>
                {
                    Interlocked.Increment(ref bgRuns);
                    return Task.CompletedTask;
                }))))
            // Crashes every 10th call: supervision under load. With the
            // default window (3 restarts / 5s) it escalates quickly and every
            // later ask fast-fails Dead — either way fail-open Noop, and the
            // sibling injects must be untouched throughout.
            .On("UserPromptSubmit", new TestHandler("flaky", (_, _) =>
                Interlocked.Increment(ref crashes) % 10 == 0
                    ? throw new InvalidOperationException("soak crash")
                    : Task.FromResult<Effect>(new Effect.Noop())));

        using var dir = new TempRuntimeDir();
        using var stop = new CancellationTokenSource();
        var daemon = Task.Run(() => DaemonHost.RunAsync(dir.Paths, NoHarness, stop.Token, reg,
            drainDeadline: TimeSpan.FromSeconds(20)));
        await PollUntilAsync(async () =>
            await ShimClient.TryForwardAsync(dir.Paths.SocketPath,
                new HookRequest("soakwarm", "session-start", "claude-code", "{}"u8.ToArray()))
                is ForwardOutcome.Answered,
            TimeSpan.FromSeconds(15), "daemon up");

        var gate = new SemaphoreSlim(Parallelism);
        var results = await Task.WhenAll(Enumerable.Range(1, N).Select(async i =>
        {
            await gate.WaitAsync();
            try
            {
                return await ShimClient.TryForwardAsync(dir.Paths.SocketPath,
                    new HookRequest($"soak{i:D4}", "user-prompt-submit", "claude-code", "{}"u8.ToArray()),
                    responseTimeout: TimeSpan.FromSeconds(30));   // queueing behind 199 siblings is legal
            }
            finally { gate.Release(); }
        }));

        // Every dispatch answered — no drops, no delivery failures, under
        // sustained concurrency with a crashing sibling in the registry.
        var answered = results.OfType<ForwardOutcome.Answered>().ToList();
        Assert.Equal(N, answered.Count);
        Assert.All(answered, a => Assert.Equal(0, a.ExitCode));

        // THE serialization proof: the seq values across all responses form a
        // perfect permutation of 1..N — no lost update, no duplicate, ever.
        var seen = new List<int>();
        foreach (var a in answered)
        {
            var text = JsonDocument.Parse(a.StdoutBytes).RootElement
                .GetProperty("hookSpecificOutput").GetProperty("additionalContext").GetString()!;
            var m = System.Text.RegularExpressions.Regex.Match(text, @"n=(\d+)");
            Assert.True(m.Success, $"no seq value in: {text}");
            seen.Add(int.Parse(m.Groups[1].Value));
        }
        seen.Sort();
        Assert.Equal(Enumerable.Range(1, N), seen);

        // Drain: the background queue must settle to exactly N completions.
        stop.Cancel();
        Assert.Equal(0, await daemon.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(N, Volatile.Read(ref bgRuns));
    }
}
