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
    /// Also bind the wire→Actors bridge, exactly as Program.cs does, so
    /// wire-layer events (ShimClient, DaemonSpawner) reach whatever sink a
    /// test installs even when the test never touches an engine type.
    [ModuleInitializer]
    internal static void Install()
    {
        Log.SetSink(_ => { });
        WireLogBridge.Bind();
    }
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

    /// A harness-override dir that does not exist: the registry falls back to
    /// the embedded specs, and nothing under ~/.captainHook is ever touched.
    public static string NoHarnessDir() =>
        Path.Combine("/tmp", "chk-none-" + Guid.NewGuid().ToString("N")[..8]);

    /// Authenticated GET against the management API (ADR-0007): attaches the
    /// bearer token the gate requires on every request. A default client sends a
    /// loopback Host and no Origin, so token-only is a valid authorized request.
    /// Returns status + body, read before the client is disposed.
    public static async Task<(System.Net.HttpStatusCode Status, string Body)> ApiGetAsync(
        int port, string token, string path)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}{path}");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var resp = await client.SendAsync(req);
        return (resp.StatusCode, await resp.Content.ReadAsStringAsync());
    }

    /// True if the API port answers with ANY HTTP status (the listener is
    /// bound), false if the connection is refused/times out (no listener). For
    /// liveness/cutover tests that assert port-BINDING, not authorization — a
    /// 401 still proves the listener handled the request.
    public static async Task<bool> ApiPortAnswersAsync(int port)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            _ = await client.GetAsync($"http://127.0.0.1:{port}/api/v1/x");
            return true;
        }
        catch (Exception) { return false; }
    }

    /// Grab a free loopback TCP port for a test HttpListener. HttpListener has
    /// no ephemeral-port (":0") mode, so bind-then-release a TcpListener to learn
    /// a currently-unused port. A tiny TOCTOU window, acceptable for tests.
    public static int FreeTcpPort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        try { return ((System.Net.IPEndPoint)l.LocalEndpoint).Port; }
        finally { l.Stop(); }
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
