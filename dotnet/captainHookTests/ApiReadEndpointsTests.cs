using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CaptainHook.Actors;
using CaptainHook.Api;
using CaptainHook.Core;
using CaptainHook.Wire;
using static CaptainHook.Tests.TestUtil;

namespace CaptainHook.Tests;

// The read endpoints (ADR-0007 decision 3, Phase 4): GET /status, /policy,
// /harnesses, /handlers rendered from a read model over the SAME Core objects
// the dispatch path uses (no mocks — a real Dispatcher, registry, and policy
// resolver). Driven through a pure ApiHost with a test read model so the DTOs
// are pinned without standing up a whole daemon; the daemon wiring itself is
// covered by ApiHostInDaemonTests. All requests bear api.Token — the endpoints
// inherit the Phase-3 auth gate.
public class ApiReadEndpointsTests
{
    private static readonly Effect Noop = new Effect.Noop();

    // A read model over real Core types. uptime is clock-startTick = 5000ms.
    private static ApiReadModel Model(Dispatcher dispatcher, string? harnessDir, string? policyPath, ServeStats stats) =>
        new("testver", stats, dispatcher,
            new ReloadingHarnessRegistry(harnessDir), new ReloadingPolicy(policyPath), policyPath,
            clock: () => 6000, startTick: 1000);

    private static Dispatcher TwoHandlers() =>
        new(new Registry()
                .On("UserPromptSubmit", TestHandler.Returning("greeter", Noop))
                .On("SessionStart", TestHandler.Returning("gatekeeper", Noop, FailMode.Closed)),
            TimeSpan.FromSeconds(2));

    private static async Task<JsonElement> GetJson(ApiHost api, string path)
    {
        var (status, body) = await ApiGetAsync(api.Port, api.Token, path);
        Assert.Equal(HttpStatusCode.OK, status);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    [Fact]
    public async Task Status_ReportsIdentityPidUptimeAndCounters()
    {
        var stats = new ServeStats();
        stats.OnConnect(); stats.OnConnect(); stats.OnDone();   // served 2, active 1
        using var api = ApiHost.Start(FreeTcpPort(), readModel: Model(TwoHandlers(), NoHarnessDir(), null, stats));

        var s = await GetJson(api, "/api/v1/status");
        Assert.Equal("testver", s.GetProperty("version").GetString());
        Assert.Equal(Environment.ProcessId, s.GetProperty("pid").GetInt32());
        Assert.Equal(5000, s.GetProperty("uptimeMs").GetInt64());
        Assert.Equal(1, s.GetProperty("active").GetInt32());
        Assert.Equal(2, s.GetProperty("served").GetInt64());
        Assert.Equal(0, s.GetProperty("backgroundPending").GetInt32());
    }

    [Fact]
    public async Task Handlers_ListsRegistrations_WithFailModeAndSupervisionState()
    {
        using var api = ApiHost.Start(FreeTcpPort(), readModel: Model(TwoHandlers(), NoHarnessDir(), null, new ServeStats()));

        var h = await GetJson(api, "/api/v1/handlers");
        var handlers = h.GetProperty("handlers").EnumerateArray().ToList();
        Assert.Equal(2, handlers.Count);

        var greeter = handlers.Single(x => x.GetProperty("name").GetString() == "greeter");
        Assert.Equal("UserPromptSubmit", greeter.GetProperty("event").GetString());
        Assert.Equal("open", greeter.GetProperty("failMode").GetString());
        Assert.Equal(1, greeter.GetProperty("generation").GetInt32());   // fresh worker, never restarted
        Assert.False(greeter.GetProperty("dead").GetBoolean());

        var gate = handlers.Single(x => x.GetProperty("name").GetString() == "gatekeeper");
        Assert.Equal("closed", gate.GetProperty("failMode").GetString());   // FailMode.Closed crossed the boundary
    }

    [Fact]
    public async Task Harnesses_ProjectsTheRegistry_WithAdaptersAndEvents()
    {
        // NoHarnessDir → the embedded default only: claude-code (generic-json is
        // a response ADAPTER, not a shipped harness spec).
        using var api = ApiHost.Start(FreeTcpPort(), readModel: Model(TwoHandlers(), NoHarnessDir(), null, new ServeStats()));

        var h = await GetJson(api, "/api/v1/harnesses");
        var names = h.GetProperty("harnesses").EnumerateArray()
            .Select(x => x.GetProperty("name").GetString()).ToList();
        Assert.Contains("claude-code", names);

        var claude = h.GetProperty("harnesses").EnumerateArray()
            .Single(x => x.GetProperty("name").GetString() == "claude-code");
        Assert.Equal("claude-hook-json", claude.GetProperty("responseAdapter").GetString());
        Assert.Equal("hook_event_name", claude.GetProperty("request").GetProperty("eventNameField").GetString());
        Assert.True(claude.TryGetProperty("events", out _));
    }

    [Fact]
    public async Task Policy_Absent_WhenNoFileConfigured()
    {
        using var api = ApiHost.Start(FreeTcpPort(), readModel: Model(TwoHandlers(), NoHarnessDir(), null, new ServeStats()));

        var p = await GetJson(api, "/api/v1/policy");
        Assert.Equal("absent", p.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, p.GetProperty("etag").ValueKind);
    }

    [Fact]
    public async Task Policy_Loaded_RendersTriStatePlusEtag_AndTheEtagHeader()
    {
        var path = Path.Combine("/tmp", "chk-pol-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        await File.WriteAllTextAsync(path,
            """{"version":1,"default":"allow","rules":[{"event":"user-prompt-submit","decision":"deny"}]}""");
        try
        {
            using var api = ApiHost.Start(FreeTcpPort(), readModel: Model(TwoHandlers(), NoHarnessDir(), path, new ServeStats()));

            // Body: the resolved tri-state, the parsed doc, the raw file, an ETag.
            var p = await GetJson(api, "/api/v1/policy");
            Assert.Equal("loaded", p.GetProperty("state").GetString());
            Assert.Equal("allow", p.GetProperty("policy").GetProperty("default").GetString());
            var rule = p.GetProperty("policy").GetProperty("rules")[0];
            Assert.Equal("UserPromptSubmit", rule.GetProperty("event").GetString());   // canonicalized at parse
            Assert.Equal("deny", rule.GetProperty("decision").GetString());
            Assert.Contains("\"version\"", p.GetProperty("raw").GetString());
            var bodyEtag = p.GetProperty("etag").GetString();
            Assert.False(string.IsNullOrEmpty(bodyEtag));

            // The same ETag rides an HTTP header (put-policy-write's If-Match token).
            using var client = new HttpClient();
            using var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{api.Port}/api/v1/policy");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", api.Token);
            var resp = await client.SendAsync(req);
            Assert.Equal(bodyEtag, resp.Headers.ETag?.ToString());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Policy_Malformed_CarriesTheParseError()
    {
        var path = Path.Combine("/tmp", "chk-pol-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        await File.WriteAllTextAsync(path, """{"version":2}""");   // wrong version → malformed
        try
        {
            using var api = ApiHost.Start(FreeTcpPort(), readModel: Model(TwoHandlers(), NoHarnessDir(), path, new ServeStats()));

            var p = await GetJson(api, "/api/v1/policy");
            Assert.Equal("malformed", p.GetProperty("state").GetString());
            Assert.Contains("version", p.GetProperty("error").GetString());
            // Raw is still surfaced (the file is readable, just invalid).
            Assert.Contains("\"version\"", p.GetProperty("raw").GetString());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Endpoints_InheritTheAuthGate_401WithoutToken()
    {
        using var api = ApiHost.Start(FreeTcpPort(), readModel: Model(TwoHandlers(), NoHarnessDir(), null, new ServeStats()));
        using var client = new HttpClient();
        // No Authorization header → the gate 401s before the endpoint renders.
        var resp = await client.GetAsync($"http://127.0.0.1:{api.Port}/api/v1/status");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task UnknownRoute_404_EvenWithAReadModel()
    {
        using var api = ApiHost.Start(FreeTcpPort(), readModel: Model(TwoHandlers(), NoHarnessDir(), null, new ServeStats()));
        var (status, _) = await ApiGetAsync(api.Port, api.Token, "/api/v1/nonesuch");
        Assert.Equal(HttpStatusCode.NotFound, status);
    }
}
