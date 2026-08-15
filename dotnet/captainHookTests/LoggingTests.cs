using System.Collections.Concurrent;
using System.Text.Json;
using CaptainHook.Actors;
using CaptainHook.Core;
using static CaptainHook.Tests.TestUtil;

namespace CaptainHook.Tests;

/// Swaps in a capturing sink for the test's duration, then restores the
/// suite-wide no-op sink (NOT ResetSink — that would bring back the real
/// file+stderr sinks and pollute the user's JSONL log).
internal sealed class CapturedLog : IDisposable
{
    public ConcurrentQueue<LogEvent> Events { get; } = new();
    public CapturedLog() => Log.SetSink(Events.Enqueue);
    public void Dispose() => Log.SetSink(_ => { });
}

public class LoggingTests
{
    /// A STRUCTURED data value (the `from` object `mail.append` carries since
    /// ADR-0016 d14) used to reach the human stderr line as
    /// `System.Collections.Generic.Dictionary`2[System.String,System.Object]`
    /// while the JSONL beside it held the real object — the two renderings of
    /// one event disagreeing about what happened. Nested values now render as
    /// the same compact JSON the JSONL uses.
    [Fact]
    public void ToPretty_RendersNestedDataAsJson_NotATypeName()
    {
        var data = new Dictionary<string, object>
        {
            ["id"] = "m-01",
            ["from"] = new Dictionary<string, object> { ["agent"] = "seed", ["harness"] = "claude-code" },
            ["bytes"] = 373,
        };
        var pretty = new LogEvent(
            new DateTime(2026, 8, 15, 12, 0, 0, DateTimeKind.Utc),
            "info", "mail", "mail.append", new LogFields { Data = data }).ToPretty();

        Assert.Contains("id=m-01", pretty);
        Assert.Contains("bytes=373", pretty);
        Assert.Contains("""from={"agent":"seed","harness":"claude-code"}""", pretty);
        Assert.DoesNotContain("System.Collections", pretty);
    }

    [Fact]
    public async Task Dispatch_EmitsStartAndHandlerOk_SharingDispatchId()
    {
        using var captured = new CapturedLog();

        var reg = new Registry().On("UserPromptSubmit",
            TestHandler.Returning("echo-ish", new Effect.Inject("hi")));
        var dispatcher = new Dispatcher(reg, TimeSpan.FromSeconds(2));

        const string dispatchId = "ab12cd34";
        await dispatcher.DispatchAsync(Ev(sessionId: "s-log"), dispatchId);

        var events = captured.Events.ToArray();
        var start = Assert.Single(events, e => e.Evt == "dispatch.start");
        var ok = Assert.Single(events, e => e.Evt == "handler.ok");
        var done = Assert.Single(events, e => e.Evt == "dispatch.done");

        // Correlation contract: every lifecycle event of one invocation carries
        // the same dispatchId (plus sessionId + hookEvent).
        Assert.Equal(dispatchId, start.Fields.DispatchId);
        Assert.Equal(dispatchId, ok.Fields.DispatchId);
        Assert.Equal(dispatchId, done.Fields.DispatchId);
        Assert.Equal("s-log", start.Fields.SessionId);
        Assert.Equal("UserPromptSubmit", start.Fields.HookEvent);

        // handler.ok and the dispatch.done span both carry a duration.
        Assert.True(ok.Fields.DurMs.HasValue, "handler.ok missing durMs");
        Assert.True(done.Fields.DurMs.HasValue, "dispatch.done span missing durMs");
    }

    [Fact]
    public async Task Dispatch_EveryEvent_SerializesToWellFormedFlatJson()
    {
        using var captured = new CapturedLog();

        var reg = new Registry().On("UserPromptSubmit",
            TestHandler.Returning("a", new Effect.Inject("hi")),
            TestHandler.Throwing("b"));   // exercise handler.error too
        await new Dispatcher(reg, TimeSpan.FromSeconds(2)).DispatchAsync(Ev(), "ffee0011");

        Assert.NotEmpty(captured.Events);
        foreach (var e in captured.Events)
        {
            using var doc = JsonDocument.Parse(e.ToJson());   // throws if malformed
            var root = doc.RootElement;

            // Required envelope, camelCase keys.
            Assert.Equal(JsonValueKind.String, root.GetProperty("ts").ValueKind);
            Assert.Contains(root.GetProperty("lvl").GetString(), new[] { "debug", "info", "warn", "error" });
            Assert.False(string.IsNullOrEmpty(root.GetProperty("src").GetString()));
            Assert.Contains('.', root.GetProperty("evt").GetString()!);

            // Absent-fields-omitted: ActorId is never set by the dispatcher
            // itself. (Constructing a Dispatcher also spawns supervised workers,
            // whose actor.spawn events legitimately DO carry an actorId — so
            // scope this check to dispatcher-sourced events.)
            if (root.GetProperty("src").GetString() == "dispatcher")
                Assert.False(root.TryGetProperty("actorId", out _), "dispatcher event must omit actorId");
        }

        // The failing handler produced a handler.error carrying the fail mode.
        var err = Assert.Single(captured.Events, e => e.Evt == "handler.error");
        Assert.Equal("error", err.Lvl);
        Assert.Equal("fail-open", err.Fields.Data?["failMode"]);
    }

    [Fact]
    public void Span_CompleteIsIdempotent_DisposeAfterCompleteEmitsNothingExtra()
    {
        using var captured = new CapturedLog();

        using (var span = Log.Span("test", "test.span", new LogFields { ActorId = "x" }))
        {
            span.Complete();
        } // Dispose fires here — must be a no-op after Complete

        var e = Assert.Single(captured.Events);
        Assert.Equal("test.span", e.Evt);
        Assert.True(e.Fields.DurMs.HasValue, "span must stamp durMs");
    }
}
