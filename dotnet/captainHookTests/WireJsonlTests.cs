using CaptainHook.Actors;
using CaptainHook.Wire;

namespace CaptainHook.Tests;

// The golden cross-emitter test (ADR-0004 decision 7 amendment,
// wire-jsonl-logger): the trail has ONE schema and two emitters — F#
// LogEvent.ToJson() (engine) and WireJsonl.Render (AOT shim). This suite is
// the only place that sees both assemblies, so it is where the schema is
// pinned: the same event through both renderers must produce IDENTICAL BYTES.
// A red test here means an emitter moved alone; move both in one commit.

public class WireJsonlTests
{
    private static readonly DateTime Ts = new(2026, 7, 6, 3, 4, 5, 789, DateTimeKind.Utc);

    /// Build the SAME event in both worlds and render both ways.
    private static (string FSharp, string Wire) RenderBoth(
        string lvl, string src, string evt,
        string? dispatchId = null, string? sessionId = null, string? hookEvent = null,
        string? actorId = null, double? durMs = null, string? msg = null,
        Dictionary<string, object>? data = null)
    {
        var ff = new LogFields
        {
            DispatchId = dispatchId!, SessionId = sessionId!, HookEvent = hookEvent!,
            ActorId = actorId!, DurMs = durMs ?? default(double?), Msg = msg!, Data = data!,
        };
        var fsharp = new LogEvent(Ts, lvl, src, evt, ff).ToJson();

        var wf = new WireLogFields
        {
            DispatchId = dispatchId, SessionId = sessionId, HookEvent = hookEvent,
            ActorId = actorId, DurMs = durMs, Msg = msg, Data = data,
        };
        var wire = WireJsonl.Render(new WireLogEvent(Ts, lvl, src, evt, wf));

        return (fsharp, wire);
    }

    [Fact]
    public void MinimalEvent_IdenticalBytes()
    {
        var (fsharp, wire) = RenderBoth("info", "shim", "shim.answered");
        Assert.Equal(fsharp, wire);
        Assert.StartsWith("{\"ts\":\"2026-07-06T03:04:05.789Z\"", wire);   // and the schema is what we think
    }

    [Fact]
    public void EveryField_IdenticalBytes()
    {
        var (fsharp, wire) = RenderBoth("warn", "shim", "shim.fallback",
            dispatchId: "abc12345", sessionId: "s-1", hookEvent: "UserPromptSubmit",
            actorId: "echo-1", durMs: 13.4, msg: "connect: ConnectionRefused",
            data: new Dictionary<string, object> { ["exit"] = 0, ["stdoutBytes"] = 42 });
        Assert.Equal(fsharp, wire);
    }

    [Theory]
    [InlineData(13.4449999)]   // rounds to 13.445
    [InlineData(2.0)]          // renders as 2, not 2.0
    [InlineData(99.9999)]      // rounds to 100
    [InlineData(1.2345)]       // midpoint: both sides must use the same Math.Round
    [InlineData(0.0004)]       // rounds to 0
    public void DurMsRounding_IdenticalBytes(double durMs)
    {
        var (fsharp, wire) = RenderBoth("info", "shim", "shim.answered", durMs: durMs);
        Assert.Equal(fsharp, wire);
    }

    [Theory]
    [InlineData("café ✓ — naïve")]                    // non-ASCII: default encoder escapes both sides
    [InlineData("say \"hi\" \\ back")]                // quotes + backslash
    [InlineData("line1\nline2\ttabbed")]              // control whitespace
    [InlineData("<script>&amp;</script>")]            // HTML-sensitive chars
    [InlineData("nulbyte")]                     // low control char
    [InlineData("")]                                  // empty is present, not omitted
    public void MsgEscaping_IdenticalBytes(string msg)
    {
        var (fsharp, wire) = RenderBoth("error", "shim", "shim.deliveryFailed", msg: msg);
        Assert.Equal(fsharp, wire);
    }

    [Fact]
    public void DataValueKinds_IdenticalBytes()
    {
        // The wire contract's data value set: primitives, nested dict, sequence.
        var (fsharp, wire) = RenderBoth("info", "daemon", "daemon.listening",
            data: new Dictionary<string, object>
            {
                ["string"] = "value with ✓",
                ["int"] = 42,
                ["long"] = 5_000_000_000L,
                ["double"] = 99.5,
                ["bool"] = true,
                ["null"] = null!,
                ["nested"] = new Dictionary<string, object> { ["k"] = "v", ["n"] = 7 },
                ["seq"] = new object[] { "a", 1, false },
            });
        Assert.Equal(fsharp, wire);
    }

    [Fact]
    public void EmptyData_OmittedByBothSides()
    {
        var (fsharp, wire) = RenderBoth("info", "shim", "shim.answered",
            data: new Dictionary<string, object>());
        Assert.Equal(fsharp, wire);
        Assert.DoesNotContain("data", wire);
    }

    [Fact]
    public void DefaultLogPath_MirrorsTheFSharpResolution()
    {
        // Same env var, same fallback — shim and engine append to ONE file.
        // (CAPTAINHOOK_LOG is unset under the test runner unless a test sets
        // it; guard by setting it explicitly and restoring.)
        var prior = Environment.GetEnvironmentVariable("CAPTAINHOOK_LOG");
        try
        {
            Environment.SetEnvironmentVariable("CAPTAINHOOK_LOG", "/tmp/x/trail.jsonl");
            Assert.Equal("/tmp/x/trail.jsonl", WireJsonl.DefaultLogPath());

            Environment.SetEnvironmentVariable("CAPTAINHOOK_LOG", null);
            Assert.Equal(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".captainHook", "logs", "captainHook.jsonl"),
                WireJsonl.DefaultLogPath());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CAPTAINHOOK_LOG", prior);
        }
    }

    [Fact]
    public void Append_WritesOneLine_AndSurvivesUnwritablePaths()
    {
        var dir = Path.Combine(Path.GetTempPath(), "chk-wirejsonl-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var path = Path.Combine(dir, "nested", "trail.jsonl");   // dir does not exist: Append creates it
            WireJsonl.Append(path, """{"ts":"x"}""");
            WireJsonl.Append(path, """{"ts":"y"}""");
            Assert.Equal(new[] { """{"ts":"x"}""", """{"ts":"y"}""" }, File.ReadAllLines(path));

            // Unwritable: swallowed, never thrown — logging is never the hook's problem.
            WireJsonl.Append("/proc/definitely/not/writable/trail.jsonl", "{}");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }
}
