using System.Net;
using System.Text.Json;
using CaptainHook.Api;
using CaptainHook.Core;
using CaptainHook.Wire;
using static CaptainHook.Tests.TestUtil;

namespace CaptainHook.Tests;

// api-listener-host (ADR-0007 Phase 1): the loopback management-API listener
// stands up beside the UDS serve loop, routes /api/v1/* (no endpoints wired yet,
// so every route 404s as JSON), serves requests CONCURRENTLY, and tears down at
// drain. Endpoints, auth, and SSE are later slices — none appear here.
public class ApiHostTests
{
    [Fact]
    public async Task Listener_Routes404Json_ForEveryUnwiredRoute()
    {
        using var api = ApiHost.Start(FreeTcpPort());
        using var client = new HttpClient();

        var resp = await client.GetAsync($"http://127.0.0.1:{api.Port}/api/v1/status");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Equal("application/json", resp.Content.Headers.ContentType?.MediaType);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("not_found", doc.RootElement.GetProperty("error").GetString());
        Assert.Equal("/api/v1/status", doc.RootElement.GetProperty("path").GetString());
    }

    [Fact]
    public async Task Listener_ServesConcurrently_NotOneConnectionAtATime()
    {
        // The accept loop fires each request on its own task and loops back
        // immediately, so many requests are in flight at once and all complete —
        // the answer to "are we limited to one connection?".
        using var api = ApiHost.Start(FreeTcpPort());
        using var client = new HttpClient();

        var results = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(i => client.GetAsync($"http://127.0.0.1:{api.Port}/api/v1/probe{i}")));

        Assert.All(results, r => Assert.Equal(HttpStatusCode.NotFound, r.StatusCode));
    }

    [Fact]
    public async Task Stop_IsIdempotent_AndHaltsAccepting()
    {
        var api = ApiHost.Start(FreeTcpPort());
        var port = api.Port;
        api.Stop();
        api.Stop();     // idempotent — a second Stop (and Dispose's Stop) is a no-op
        api.Dispose();

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        await Assert.ThrowsAnyAsync<Exception>(() =>
            client.GetAsync($"http://127.0.0.1:{port}/api/v1/status"));
    }
}

// The API host wired into a REAL daemon over the real rendezvous: it comes up
// beside the UDS serve loop (both answer, concurrently) and tears down when the
// daemon drains — the DaemonHost.RunAsync(apiPort:) seam this slice adds.
public class ApiHostInDaemonTests : IAsyncLifetime
{
    private readonly TempRuntimeDir _dir = new();
    private readonly CancellationTokenSource _stop = new();
    private Task<int>? _daemon;
    private int _apiPort;

    private string Sock => _dir.Paths.SocketPath;
    private static string NoHarnessDir => Path.Combine("/tmp", "chk-none-" + Guid.NewGuid().ToString("N")[..8]);

    public async Task InitializeAsync()
    {
        _apiPort = FreeTcpPort();
        _daemon = Task.Run(() => DaemonHost.RunAsync(_dir.Paths, NoHarnessDir, _stop.Token, apiPort: _apiPort));
        await PollUntilAsync(async () =>
        {
            var probe = await ShimClient.TryForwardAsync(Sock,
                new HookRequest("probe000", "session-start", "claude-code", "{}"u8.ToArray()));
            return probe is ForwardOutcome.Answered;
        }, TimeSpan.FromSeconds(15), "daemon starts listening");
    }

    public async Task DisposeAsync()
    {
        _stop.Cancel();
        if (_daemon is not null)
            Assert.Equal(0, await _daemon.WaitAsync(TimeSpan.FromSeconds(10)));
        _dir.Dispose();
    }

    [Fact]
    public async Task Api_ListensBesideTheUdsServeLoop_BothAnswer()
    {
        // The UDS dispatch path still works...
        var outcome = await ShimClient.TryForwardAsync(Sock, new HookRequest(
            "both0001", "user-prompt-submit", "claude-code", "{}"u8.ToArray()));
        Assert.IsType<ForwardOutcome.Answered>(outcome);

        // ...and the HTTP API answers on its own port at the same time.
        using var client = new HttpClient();
        var resp = await client.GetAsync($"http://127.0.0.1:{_apiPort}/api/v1/status");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Api_TearsDown_WhenTheDaemonDrains()
    {
        // Ensure the API is live first.
        using (var client0 = new HttpClient())
            Assert.Equal(HttpStatusCode.NotFound,
                (await client0.GetAsync($"http://127.0.0.1:{_apiPort}/api/v1/status")).StatusCode);

        // Trigger drain, wait for a clean exit, then the API port is dead.
        _stop.Cancel();
        Assert.Equal(0, await _daemon!.WaitAsync(TimeSpan.FromSeconds(10)));
        _daemon = null;   // DisposeAsync must not await it again

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        await Assert.ThrowsAnyAsync<Exception>(() =>
            client.GetAsync($"http://127.0.0.1:{_apiPort}/api/v1/status"));
    }
}
