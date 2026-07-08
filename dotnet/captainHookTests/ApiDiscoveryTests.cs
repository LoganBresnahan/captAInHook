using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using CaptainHook.Api;
using CaptainHook.Core;
using CaptainHook.Wire;
using static CaptainHook.Tests.TestUtil;

namespace CaptainHook.Tests;

// api-json-discovery (ADR-0007 decisions 2+6): on bind the API writes a 0600
// api.json (port, token, pid, identity) beside socket/lock/pid and mints the
// bearer token that IS the sole credential. The file's lifecycle is pinned to
// the port: written when the host binds, gone when it stops — version-
// partitioned so a draining incumbent never deletes its successor's file.
public class ApiDiscoveryTests
{
    // A runtime dir that exists (the daemon makes it via TryAcquire in
    // production; here we make it so the discovery write has somewhere to land).
    private static TempRuntimeDir LiveDir()
    {
        var d = new TempRuntimeDir();
        Directory.CreateDirectory(d.Path);
        return d;
    }

    [Fact]
    public void Start_PublishesDiscovery_0600_WithPortTokenPidIdentity()
    {
        using var dir = LiveDir();
        using var api = ApiHost.Start(FreeTcpPort(), dir.Paths);

        var path = dir.Paths.ApiJsonPath;
        Assert.True(File.Exists(path), "api.json must exist while the API holds the port");

        // 0600 — filesystem permissions are the trust root (decision 6), same as
        // the socket and lock. No group/other bits.
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));

        var d = ApiDiscovery.TryRead(path);
        Assert.NotNull(d);
        Assert.Equal(api.Port, d!.Port);
        Assert.Equal(api.Token, d.Token);            // the file carries the same token the host minted
        Assert.Equal(Environment.ProcessId, d.Pid);
        Assert.Equal(dir.Paths.Version, d.Version);
        Assert.Equal(64, api.Token.Length);          // 256 bits, hex — no encoding ambiguity in a header
    }

    [Fact]
    public void Stop_RemovesDiscovery()
    {
        using var dir = LiveDir();
        var api = ApiHost.Start(FreeTcpPort(), dir.Paths);
        Assert.True(File.Exists(dir.Paths.ApiJsonPath));

        api.Stop();
        Assert.False(File.Exists(dir.Paths.ApiJsonPath),
            "a stopped API must stop advertising its port+token");
        api.Dispose();
    }

    [Fact]
    public void EachStartMintsAFreshToken()
    {
        // A superseded daemon's token dies with it; the successor mints its own.
        using var dir = LiveDir();
        using var a = ApiHost.Start(FreeTcpPort(), dir.Paths);
        var first = a.Token;
        a.Stop();
        using var b = ApiHost.Start(FreeTcpPort(), dir.Paths);
        Assert.NotEqual(first, b.Token);
    }

    [Fact]
    public void VersionPartitioned_NeitherHostDeletesTheOthersFile()
    {
        // Two identities in ONE runtime dir (a deploy's incumbent + successor):
        // separate api.json files, so the incumbent's Stop can never delete the
        // successor's freshly published credentials — the cross-version-delete
        // hazard the versioned socket avoids, one file over.
        using var dir = new TempRuntimeDir();
        Directory.CreateDirectory(dir.Path);
        var pathsA = RendezvousPaths.Resolve(dir.Path, "verA");
        var pathsB = RendezvousPaths.Resolve(dir.Path, "verB");
        Assert.NotEqual(pathsA.ApiJsonPath, pathsB.ApiJsonPath);

        using var a = ApiHost.Start(FreeTcpPort(), pathsA);
        using var b = ApiHost.Start(FreeTcpPort(), pathsB);
        Assert.True(File.Exists(pathsA.ApiJsonPath));
        Assert.True(File.Exists(pathsB.ApiJsonPath));

        a.Stop();
        Assert.False(File.Exists(pathsA.ApiJsonPath), "A removed its own file");
        Assert.True(File.Exists(pathsB.ApiJsonPath), "B's file untouched by A's stop");
    }

    [Fact]
    public async Task Retrying_DoesNotPublish_UntilItActuallyBinds()
    {
        // A host that does not hold the port must not advertise one: no api.json
        // exists while the bind is blocked, so a client never gets a token for a
        // port the incumbent still owns. It appears only once the bind lands.
        using var dir = LiveDir();
        var port = FreeTcpPort();
        var squatter = new TcpListener(IPAddress.Loopback, port);
        squatter.Start();
        try
        {
            using var api = ApiHost.StartRetrying(port,
                fastWindow: TimeSpan.FromMilliseconds(150), rendezvous: dir.Paths,
                slowRetry: TimeSpan.FromMilliseconds(50));
            Assert.False(api.IsListening);
            Assert.False(File.Exists(dir.Paths.ApiJsonPath),
                "no discovery file while the port is not held");

            squatter.Stop();
            await PollUntilAsync(() => Task.FromResult(api.IsListening),
                TimeSpan.FromSeconds(10), "retry-bind lands");
            Assert.True(File.Exists(dir.Paths.ApiJsonPath),
                "discovery published exactly when the bind succeeds");
        }
        finally { squatter.Stop(); }
    }

    [Fact]
    public void TryRead_NullsOnMissingAndMalformed()
    {
        using var dir = LiveDir();
        Assert.Null(ApiDiscovery.TryRead(dir.Paths.ApiJsonPath));   // absent
        File.WriteAllText(dir.Paths.ApiJsonPath, "{ not json");
        Assert.Null(ApiDiscovery.TryRead(dir.Paths.ApiJsonPath));   // malformed
    }
}

// The DaemonHost → ApiHost → api.json wiring, end to end with a real daemon:
// the file appears in the runtime dir when the daemon is warm and is gone once
// it drains.
public class ApiDiscoveryInDaemonTests : IAsyncLifetime
{
    private readonly TempRuntimeDir _dir = new();
    private readonly CancellationTokenSource _stop = new();
    private Task<int>? _daemon;
    private int _apiPort;

    public async Task InitializeAsync()
    {
        _apiPort = FreeTcpPort();
        _daemon = Task.Run(() => DaemonHost.RunAsync(_dir.Paths, NoHarnessDir(), _stop.Token, apiPort: _apiPort));
        await PollUntilAsync(async () => await ShimClient.TryForwardAsync(_dir.Paths.SocketPath,
                new HookRequest("disc0001", "session-start", "claude-code", "{}"u8.ToArray()))
            is ForwardOutcome.Answered, TimeSpan.FromSeconds(15), "daemon warm");
    }

    public async Task DisposeAsync()
    {
        _stop.Cancel();
        if (_daemon is not null)
            try { await _daemon.WaitAsync(TimeSpan.FromSeconds(10)); } catch { /* asserted per-test */ }
        _dir.Dispose();
    }

    [Fact]
    public async Task Daemon_PublishesRealDiscovery_ThenRemovesItOnDrain()
    {
        // Warm: api.json names the real port + a token, at 0600.
        var d = ApiDiscovery.TryRead(_dir.Paths.ApiJsonPath);
        Assert.NotNull(d);
        Assert.Equal(_apiPort, d!.Port);
        Assert.NotEmpty(d.Token);
        Assert.Equal(_dir.Paths.Version, d.Version);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(_dir.Paths.ApiJsonPath));

        // Drain: the file is gone (removed at drain start, with the port).
        _stop.Cancel();
        Assert.Equal(0, await _daemon!.WaitAsync(TimeSpan.FromSeconds(10)));
        _daemon = null;
        Assert.False(File.Exists(_dir.Paths.ApiJsonPath),
            "discovery file removed when the daemon drains");
    }
}
