using System.Net.Sockets;
using System.Text.Json;

namespace CaptainHook.Core;

// ADR-0004 decision 3, daemon side: spawn races are KERNEL-settled. Startup
// takes an exclusive lock on captaind-<ver>.lock (a FileShare.None handle —
// flock underneath on Unix — held for the process lifetime and released by
// the OS on ANY death, crash and SIGKILL included); the holder may unlink a
// stale socket and bind; a starter that cannot take the lock lost the race —
// some other daemon of this exact build won — and should exit 0.
//
// Two rules this type enforces by construction:
//   * LOCK FILES ARE NEVER UNLINKED, graceful exit included. Deleting a held
//     lock file lets a second daemon lock a FRESH INODE at the same path and
//     silently break mutual exclusion. The files are tiny and version-bounded;
//     they stay.
//   * The SOCKET is unlinked only by the lock holder. Holding the lock proves
//     every same-version predecessor is dead, so a socket file found at bind
//     time is stale by definition — no liveness probe needed.
public sealed class DaemonRendezvous : IDisposable
{
    private readonly RendezvousPaths _paths;
    private readonly FileStream _lock;      // the held lock: disposed on exit, never deleted
    private bool _bound;

    private DaemonRendezvous(RendezvousPaths paths, FileStream held)
    {
        _paths = paths;
        _lock = held;
    }

    /// Try to become THE daemon for this content identity. Null means another
    /// daemon already holds the lock — the caller lost the spawn race and
    /// should exit 0 (never wait, never retry: the winner is already warming).
    ///
    /// On success the pidfile is written IMMEDIATELY — before any warmup — so
    /// a daemon that wedges before ever binding is still discoverable by
    /// `doctor` (pid + binary path for the PID-reuse guard) even though it
    /// never produced a socket.
    public static DaemonRendezvous? TryAcquire(RendezvousPaths paths)
    {
        // 0700: the rendezvous directory is per-user private, like the socket.
        Directory.CreateDirectory(paths.RuntimeDir,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        FileStream held;
        try
        {
            // OpenOrCreate + FileShare.None: on Unix this is flock(LOCK_EX|NB)
            // on the fd — advisory, exclusive, and released by the kernel when
            // the process dies however it dies. Never O_EXCL on existence: the
            // FILE persisting is fine and expected; the LOCK is the exclusion.
            held = new FileStream(paths.LockPath, new FileStreamOptions
            {
                Mode = FileMode.OpenOrCreate,
                Access = FileAccess.ReadWrite,
                Share = FileShare.None,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,   // 0600, like the socket
            });
        }
        catch (IOException)
        {
            return null;   // lock held elsewhere: lost the race, exit 0
        }

        try
        {
            File.WriteAllText(paths.PidPath, JsonSerializer.Serialize(new PidRecord(
                Environment.ProcessId,
                Environment.ProcessPath ?? "",
                DateTimeOffset.UtcNow)));   // wall clock: display/forensics only, never compared
            File.SetUnixFileMode(paths.PidPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
            held.Dispose();   // release the lock rather than hold it half-initialized
            throw;
        }
        return new DaemonRendezvous(paths, held);
    }

    /// Unlink any stale socket, bind (0600) and listen. Call this ONLY when
    /// fully warm: the daemon binds after registry + dispatcher + workers are
    /// built, so connect-success IS the readiness signal and the hot path
    /// needs no probe ("listening ⟺ ready" — ADR-0004 decision 3, inverting
    /// pharos's active readiness probe, which is needed only when you don't
    /// control the server end; we control both).
    public Socket BindWhenWarm(int backlog = 64)
    {
        if (_bound) throw new InvalidOperationException("already bound");

        // Stale by proof, not by probe: we hold the lock, so every prior
        // same-version daemon is dead and its socket file is a leftover.
        File.Delete(_paths.SocketPath);

        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(_paths.SocketPath));
            // 0600 between Bind (file exists) and Listen (connects possible):
            // no window where a foreign user can reach an accepting socket.
            File.SetUnixFileMode(_paths.SocketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            listener.Listen(backlog);
        }
        catch
        {
            listener.Dispose();
            throw;
        }
        _bound = true;
        return listener;
    }

    /// Graceful-exit cleanup: unlink the socket and pidfile, release (but
    /// NEVER delete) the lock. On a crash none of this runs — the kernel
    /// releases the lock anyway, and the next winner unlinks the stale socket
    /// while `doctor` reaps the stale pidfile.
    public void Dispose()
    {
        try { File.Delete(_paths.SocketPath); } catch { /* best-effort */ }
        try { File.Delete(_paths.PidPath); } catch { /* best-effort */ }
        _lock.Dispose();
    }
}

/// What captaind-<ver>.pid holds — for CLEANUP only, never discovery (the
/// socket is the rendezvous). BinaryPath backs doctor's PID-reuse guard:
/// liveness plus "is it still OUR binary" before any SIGTERM.
public sealed record PidRecord(int Pid, string BinaryPath, DateTimeOffset StartedAt);
