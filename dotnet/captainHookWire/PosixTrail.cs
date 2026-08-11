using System.Runtime.InteropServices;
using System.Text;

namespace CaptainHook.Wire;

/// A REAL `O_APPEND` line appender for the trail — the one thing the BCL will
/// not give us.
///
/// The trail is written by two processes at once (the shim relays a hook while
/// the daemon dispatches another), and ADR-0004 N3's convergence story rests on
/// each line landing whole. Every BCL append path breaks that, identically:
/// `File.AppendAllText`, `new FileStream(FileMode.Append)`, and
/// `File.OpenHandle(FileMode.Append)` all open **without** `O_APPEND` and then
/// `pwrite` at an offset resolved when the file was opened (strace-probed on
/// .NET 10 / linux-x64, 2026-08-11). Two writers that open in the same window
/// compute the SAME end offset and overwrite each other — a silently lost
/// line, not a torn one. `O_APPEND` moves the seek-to-end inside the kernel's
/// write, under the inode lock, so concurrent appenders interleave whole lines
/// instead of clobbering.
///
/// Mirrored by `Logging.fs`'s file sink, which cannot reference this assembly
/// (the F# lib is a leaf) — the two emitters share the trail file, not code, so
/// a fix to one is only half a fix. Both were changed together.
internal static class PosixTrail
{
    // open(2) flags are per-OS ABI constants, NOT portable numbers — macOS and
    // Linux disagree on every one of these but O_WRONLY.
    private const int O_WRONLY = 1;
    private static readonly int O_APPEND = OperatingSystem.IsMacOS() ? 0x0008 : 0x0400;
    private static readonly int O_CLOEXEC = OperatingSystem.IsMacOS() ? 0x1000000 : 0x80000;
    private const int ENOENT = 2;
    private const int EINTR = 4;

    // The TWO-argument form of open(2), deliberately. open() is variadic
    // (`open(const char*, int, ...)`) and on Apple arm64 the variadic tail uses
    // a DIFFERENT calling convention than fixed args — a three-arg P/Invoke
    // declaring `mode` as fixed would pass it in the wrong place. Passing no
    // variadic argument at all is well-defined everywhere, so the file is
    // created through the BCL first and opened without O_CREAT.
    [DllImport("libc", SetLastError = true, EntryPoint = "open")]
    private static extern int sys_open(byte[] path, int flags);

    [DllImport("libc", SetLastError = true, EntryPoint = "write")]
    private static extern nint sys_write(int fd, byte[] buf, nuint count);

    [DllImport("libc", SetLastError = true, EntryPoint = "close")]
    private static extern int sys_close(int fd);

    /// Append `text` to `path` as one O_APPEND write. Returns false if the
    /// bytes did not land — every caller is a log sink, so the decision to
    /// swallow stays with them.
    internal static bool Append(string path, string text)
    {
        var cPath = new byte[Encoding.UTF8.GetByteCount(path) + 1];
        Encoding.UTF8.GetBytes(path, cPath);   // trailing 0 is the NUL terminator

        var fd = sys_open(cPath, O_WRONLY | O_APPEND | O_CLOEXEC);
        if (fd < 0)
        {
            // Absent file: create it through the BCL (which owns directory
            // creation and permissions anyway) and open exactly once more.
            if (Marshal.GetLastPInvokeError() != ENOENT) return false;
            try { using (File.Open(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite)) { } }
            catch (Exception) { return false; }
            fd = sys_open(cPath, O_WRONLY | O_APPEND | O_CLOEXEC);
            if (fd < 0) return false;
        }

        try
        {
            var buf = Encoding.UTF8.GetBytes(text);
            var offset = 0;
            while (offset < buf.Length)
            {
                // One write(2) for the whole line is the atomic unit. A short
                // write (signal, ENOSPC) is the only path that re-enters, and
                // it copies the remainder rather than needing unsafe pointer
                // math — the leaf stays safe code. The remainder is a separate
                // append, so another writer CAN land between the halves: rare,
                // and strictly better than the whole line vanishing.
                var chunk = offset == 0 ? buf : buf[offset..];
                var n = sys_write(fd, chunk, (nuint)chunk.Length);
                if (n > 0) { offset += (int)n; continue; }
                if (n < 0 && Marshal.GetLastPInvokeError() == EINTR) continue;
                return false;
            }
            return true;
        }
        finally { sys_close(fd); }
    }
}
