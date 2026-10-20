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
        // The command-history file (ShellTrail, ADR-0009 d8) is its own
        // writer, not a Log sink: swapped the same way, or every test that
        // dispatches a PostToolUse would append to the user's real
        // ~/.captainHook/logs/shell-history.jsonl.
        ShellTrail.Sink = _ => { };
    }
}

/// Capture `tool.shell` lines for the life of a test, then restore the no-op.
internal sealed class CapturedShellHistory : IDisposable
{
    public System.Collections.Concurrent.ConcurrentQueue<string> Lines { get; } = new();
    public CapturedShellHistory() => ShellTrail.Sink = Lines.Enqueue;
    public void Dispose() => ShellTrail.Sink = _ => { };

    /// The captured lines, parsed back the way history reads them.
    public ShellTrail.Entry[] Entries => Lines.Select(ShellTrail.Parse).OfType<ShellTrail.Entry>().ToArray();
}

/// CAPTAINHOOK_ECHO=1 for the life of a test (or a fixture): the echo demo
/// handler is off by default since 0.1.4, and the tests that read its answer
/// as a round-trip proof opt in, as a user would. Tests run sequentially, so
/// a process-wide env flip is safe; a daemon reads it once, at start.
internal sealed class EchoOn : IDisposable
{
    private readonly string? _saved = Environment.GetEnvironmentVariable("CAPTAINHOOK_ECHO");
    public EchoOn() => Environment.SetEnvironmentVariable("CAPTAINHOOK_ECHO", "1");
    public void Dispose() => Environment.SetEnvironmentVariable("CAPTAINHOOK_ECHO", _saved);
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
    /// Is `pid` a live, non-zombie process? Linux reads /proc/<pid>/stat (a
    /// Z-state leader awaiting its reap means the kill won); macOS has no
    /// /proc, so `ps` answers the same question. The per-file /proc helpers
    /// this replaces read "no /proc" as "dead" — every kill assertion passed
    /// VACUOUSLY on macOS and every liveness assertion failed (first macOS CI
    /// run, 2026-10-19).
    public static bool Alive(int pid)
    {
        if (Directory.Exists("/proc/self"))
        {
            try
            {
                var stat = File.ReadAllText($"/proc/{pid}/stat");
                return stat[stat.LastIndexOf(')') + 2] != 'Z';
            }
            catch (Exception) { return false; }
        }
        var psi = new System.Diagnostics.ProcessStartInfo("/bin/ps") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-o", "stat=", "-p", pid.ToString() }) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        var state = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        return p.ExitCode == 0 && state.Length > 0 && state[0] != 'Z';
    }

    /// The path with every symlink resolved, as a child's `$PWD` reports it.
    /// macOS's temp dir lives under /var, itself a link to /private/var, so a
    /// child chdir'd to Path.GetTempPath() prints the /private form (the
    /// first macOS CI run, 2026-10-19). `pwd -P` is the POSIX realpath.
    public static string RealPath(string path)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("/bin/sh") { RedirectStandardOutput = true };
        foreach (var a in new[] { "-c", "cd \"$1\" && pwd -P", "sh", path }) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        var real = p.StandardOutput.ReadToEnd().TrimEnd('\n');
        p.WaitForExit();
        return p.ExitCode == 0 && real.Length > 0 ? real : path;
    }

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

    /// Authenticated PUT against the management API (put-policy-write, ADR-0007
    /// decision 4): bearer token plus an optional If-Match precondition header.
    /// Returns status, body, and the response ETag (null when absent), read
    /// before the client is disposed.
    public static async Task<(System.Net.HttpStatusCode Status, string Body, string? Etag)> ApiPutAsync(
        int port, string token, string path, string body, string? ifMatch = null)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var req = new HttpRequestMessage(HttpMethod.Put, $"http://127.0.0.1:{port}{path}")
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        if (ifMatch is not null) req.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        var resp = await client.SendAsync(req);
        return (resp.StatusCode, await resp.Content.ReadAsStringAsync(), resp.Headers.ETag?.ToString());
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

/// The PATH a payload test hands its child: the system directories, plus the
/// directory holding `jq` when the system has none of its own. Linux has jq
/// in /usr/bin; a macOS host has it from Homebrew (/opt/homebrew/bin,
/// /usr/local/bin) — so a hard-coded "/usr/bin:/bin" read as "jq missing"
/// there and the payload under test fell to its no-jq exit (the first macOS
/// CI run, 2026-10-19). Only jq's own directory is added, AFTER the system
/// ones, so nothing else on the host (a real `claude`) leaks onto the
/// child's PATH.
internal static class TestPath
{
    public static readonly string Minimal = Build();

    private static string Build()
    {
        const string system = "/usr/bin:/bin";
        if (File.Exists("/usr/bin/jq") || File.Exists("/bin/jq")) return system;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':'))
            if (dir.Length > 0 && File.Exists(Path.Combine(dir, "jq")))
                return system + ":" + dir;
        return system;
    }
}
