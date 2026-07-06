using System.Net.Sockets;
using System.Text.Json;
using CaptainHook.Core;
using CaptainHook.Wire;

namespace CaptainHook.Tests;

// lock-bind-rendezvous (ADR-0004 decision 3): the kernel settles spawn races.
// In-process these tests pin the single-winner property (flock conflicts are
// per open-file-description, so a second acquire in the SAME process conflicts
// exactly like a second process), the file lifecycle rules (pidfile before
// warmup; socket 0600; lock file never unlinked), and listening ⟺ ready.
// The cross-process race and kill -9 lock release are exercised by the slice's
// live verify driver — they need real process death.

/// A short throwaway runtime dir: sun_path is capped (~104 bytes), so these
/// paths must stay well under it — /tmp/chk-<8 hex>/captaind-v.sock ≈ 35B.
internal sealed class TempRuntimeDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine("/tmp", "chk-" + Guid.NewGuid().ToString("N")[..8]);
    public RendezvousPaths Paths => RendezvousPaths.Resolve(Path, "testver");
    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { /* best-effort */ }
    }
}

public class LockBindTests
{
    [Fact]
    public void Acquire_CreatesDir_WritesPidfileImmediately_BeforeAnyBind()
    {
        using var tmp = new TempRuntimeDir();
        using var rv = DaemonRendezvous.TryAcquire(tmp.Paths);

        Assert.NotNull(rv);   // fresh path: we are the daemon
        Assert.True(File.Exists(tmp.Paths.LockPath), "lock file must exist while held");

        // Pidfile lands BEFORE warmup/bind, so a daemon that wedges pre-bind
        // is still reapable by doctor. It carries pid + binary for the
        // PID-reuse guard.
        var pid = JsonSerializer.Deserialize<PidRecord>(File.ReadAllText(tmp.Paths.PidPath))!;
        Assert.Equal(Environment.ProcessId, pid.Pid);
        Assert.Equal(Environment.ProcessPath, pid.BinaryPath);

        // Rendezvous files are private like the socket: 0600 (dir is 0700).
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(tmp.Paths.LockPath));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(tmp.Paths.PidPath));

        // No socket yet: not warm, not bound, deliberately not connectable.
        Assert.False(File.Exists(tmp.Paths.SocketPath), "must not bind before BindWhenWarm");
    }

    [Fact]
    public void SecondAcquire_WhileHeld_LosesTheRace()
    {
        using var tmp = new TempRuntimeDir();
        using var winner = DaemonRendezvous.TryAcquire(tmp.Paths);
        Assert.NotNull(winner);

        // flock conflicts apply across open-file-descriptions, so a second
        // acquire here conflicts exactly as a second process would: null =
        // "some other daemon won, exit 0".
        using var loser = DaemonRendezvous.TryAcquire(tmp.Paths);
        Assert.Null(loser);
    }

    [Fact]
    public void ReleaseThenReacquire_Succeeds_AndLockFileIsNeverUnlinked()
    {
        using var tmp = new TempRuntimeDir();
        var first = DaemonRendezvous.TryAcquire(tmp.Paths);
        Assert.NotNull(first);
        first!.Dispose();

        // THE RULE: graceful exit released the lock but left the FILE — deleting
        // a held lock file is how mutual exclusion silently breaks (fresh-inode
        // relock), so no component ever unlinks one.
        Assert.True(File.Exists(tmp.Paths.LockPath), "lock file must survive graceful release");

        using var second = DaemonRendezvous.TryAcquire(tmp.Paths);
        Assert.NotNull(second);   // same inode, lock free: the next daemon wins
    }

    [Fact]
    public async Task BindWhenWarm_ListeningMeansReady_AndSocketIs0600()
    {
        using var tmp = new TempRuntimeDir();
        using var rv = DaemonRendezvous.TryAcquire(tmp.Paths)!;

        // Before bind: connect must FAIL — nobody may mistake a warming daemon
        // for a ready one (the whole point of binding after warmup).
        using (var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
            await Assert.ThrowsAsync<SocketException>(
                () => probe.ConnectAsync(new UnixDomainSocketEndPoint(tmp.Paths.SocketPath)));

        using var listener = rv.BindWhenWarm();

        // After bind: the socket file exists, private to the user (0600).
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(tmp.Paths.SocketPath));

        // Listening ⟺ ready: connect-success is the readiness signal.
        using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await client.ConnectAsync(new UnixDomainSocketEndPoint(tmp.Paths.SocketPath));
        Assert.True(client.Connected);
    }

    [Fact]
    public async Task BindWhenWarm_UnlinksStaleSocket_FromADeadPredecessor()
    {
        using var tmp = new TempRuntimeDir();
        Directory.CreateDirectory(tmp.Path);
        // A predecessor crashed and left its socket file behind. Holding the
        // lock PROVES it is dead — the file is stale by construction.
        File.WriteAllText(tmp.Paths.SocketPath, "stale corpse");

        using var rv = DaemonRendezvous.TryAcquire(tmp.Paths)!;
        using var listener = rv.BindWhenWarm();

        using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await client.ConnectAsync(new UnixDomainSocketEndPoint(tmp.Paths.SocketPath));
        Assert.True(client.Connected, "stale socket must be unlinked and rebound");
    }

    [Fact]
    public void Dispose_RemovesSocketAndPidfile_KeepsLockFile()
    {
        using var tmp = new TempRuntimeDir();
        var rv = DaemonRendezvous.TryAcquire(tmp.Paths)!;
        var listener = rv.BindWhenWarm();
        listener.Dispose();
        rv.Dispose();

        Assert.False(File.Exists(tmp.Paths.SocketPath), "graceful exit unlinks the socket");
        Assert.False(File.Exists(tmp.Paths.PidPath), "graceful exit removes the pidfile");
        Assert.True(File.Exists(tmp.Paths.LockPath), "the lock file stays, always");
    }

    [Fact]
    public void DoubleBind_Throws_OneListenerPerDaemon()
    {
        using var tmp = new TempRuntimeDir();
        using var rv = DaemonRendezvous.TryAcquire(tmp.Paths)!;
        using var listener = rv.BindWhenWarm();
        Assert.Throws<InvalidOperationException>(() => rv.BindWhenWarm());
    }
}
