using System.Runtime.CompilerServices;
using System.Text.Json;
using CaptainHook.Actors;
using CaptainHook.Core;

// Log is a process-global seam and several tests assert on timing — run test
// classes sequentially so a sink swapped by one test never sees another's events.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace CaptainHook.Tests;

internal static class TestLogSink
{
    /// Runs before any test: replace the default file+stderr sinks with a no-op
    /// so the suite never appends to the user's real ~/.captainHook JSONL file
    /// (actors log spawn/restart/etc. as a side effect of every actor test).
    [ModuleInitializer]
    internal static void Install() => Log.SetSink(_ => { });
}

/// Lambda-based handler so each test states its behavior inline.
internal sealed class TestHandler(
    string name,
    Func<HookEvent, HandlerContext, Task<Effect>> body,
    FailMode onFailure = FailMode.Open) : IHandler
{
    public string Name => name;
    public FailMode OnFailure => onFailure;
    public Task<Effect> HandleAsync(HookEvent e, HandlerContext ctx) => body(e, ctx);

    public static TestHandler Returning(string name, Effect effect, FailMode onFailure = FailMode.Open) =>
        new(name, (_, _) => Task.FromResult(effect), onFailure);

    public static TestHandler Throwing(string name, FailMode onFailure = FailMode.Open) =>
        new(name, (_, _) => throw new InvalidOperationException($"{name} exploded"), onFailure);

    /// Sleeps past any reasonable test budget; honors the budget token so a
    /// fail-open timeout is observed as OperationCanceledException.
    public static TestHandler Hanging(string name, FailMode onFailure = FailMode.Open) =>
        new(name, async (_, ctx) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ctx.Ct);
            return new Effect.Noop();
        }, onFailure);
}

internal static class TestUtil
{
    public static HookEvent Ev(string type = "UserPromptSubmit", string? sessionId = "s-test") =>
        new(type, sessionId, Cwd: null, Payload: JsonDocument.Parse("{}").RootElement);

    /// Poll until `probe` is true or `timeout` elapses — never a fixed sleep.
    /// Stopwatch, not DateTime.UtcNow: the deadline must be monotonic (WSL2
    /// wall-clock jumps must not shrink the window). Note a probe that asks a
    /// dead mailbox costs a full 2s ask-timeout, so keep `timeout` generous.
    public static async Task PollUntilAsync(Func<Task<bool>> probe, TimeSpan timeout, string what)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (await probe()) return;
            await Task.Delay(20);
        }
        Assert.Fail($"timed out after {sw.Elapsed.TotalSeconds:F1}s waiting for: {what}");
    }

    /// GetCountAsync that treats an ask-timeout (mailbox dead mid-restart) as
    /// "not there yet" so pollers can simply retry.
    public static async Task<int> CountOrMinusOne(Counter c)
    {
        try { return await c.GetCountAsync(); }
        catch (TimeoutException) { return -1; }
    }
}

/// Deterministic monotonic clock for supervisor tests: time advances ONLY when
/// the test says so, making restart-intensity windows immune to machine load
/// (and exercising the Supervisor's injectable-clock seam).
internal sealed class FakeClock
{
    private long _nowMs;
    public long Now() => Interlocked.Read(ref _nowMs);
    public void Advance(TimeSpan by) => Interlocked.Add(ref _nowMs, (long)by.TotalMilliseconds);
}
