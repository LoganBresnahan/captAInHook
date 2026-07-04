using CaptainHook.Core;

namespace CaptainHook.Tests;

public class ProbeTests
{
    [Fact]
    public void StartIfEnabled_ReturnsNull_WhenEnvUnset()
    {
        var prev = Environment.GetEnvironmentVariable("CAPTAINHOOK_COLDSTART");
        Environment.SetEnvironmentVariable("CAPTAINHOOK_COLDSTART", null);
        try { Assert.Null(ColdStartProbe.StartIfEnabled()); }
        finally { Environment.SetEnvironmentVariable("CAPTAINHOOK_COLDSTART", prev); }
    }

    [Fact]
    public void Emit_WritesOneColdStartEvent_WithAllPhaseBuckets()
    {
        using var captured = new CapturedLog();
        var prev = Environment.GetEnvironmentVariable("CAPTAINHOOK_COLDSTART");
        Environment.SetEnvironmentVariable("CAPTAINHOOK_COLDSTART", "1");
        try
        {
            var probe = ColdStartProbe.StartIfEnabled();
            Assert.NotNull(probe);

            // Mark phases in order (real, tiny elapsed) and emit.
            probe!.Resolved();
            probe.Parsed();
            probe.DispatcherBuilt();
            probe.Dispatched();
            probe.Emit("probe123");
        }
        finally { Environment.SetEnvironmentVariable("CAPTAINHOOK_COLDSTART", prev); }

        // Exactly one event, correct envelope, correlated.
        var e = Assert.Single(captured.Events, x => x.Evt == "probe.coldstart");
        Assert.Equal("info", e.Lvl);
        Assert.Equal("probe", e.Src);
        Assert.Equal("probe123", e.Fields.DispatchId);

        // Every phase bucket present and non-negative (marks are ordered, so the
        // differenced buckets can never be negative).
        var data = e.Fields.Data!;
        foreach (var key in new[]
                 { "procBootMs", "resolveMs", "parseMs", "buildMs", "dispatchMs", "managedMs", "endToEndMs" })
        {
            Assert.True(data.ContainsKey(key), $"missing bucket: {key}");
            Assert.True(Convert.ToDouble(data[key]) >= 0, $"{key} is negative");
        }
    }
}
