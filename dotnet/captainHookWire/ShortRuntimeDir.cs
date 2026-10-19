using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace CaptainHook.Wire;

/// The short per-uid rendezvous dir (ADR-0004 d3, amended 2026-10-19): where
/// the socket, lock, pid and api.json go when the preferred runtime dir is too
/// long for a socket path. `/tmp/captainhook-<uid>` is SHARED ground — any
/// local user can create that name first — so nothing in it is trusted until
/// the directory itself is proven ours: a real directory (not a symlink),
/// owned by this uid, with no group or other permission bits. A refusal is an
/// answer, not an error: the caller keeps today's behavior (no daemon, every
/// hook collapses) and logs the reason it returns.
///
/// The ownership half is the one thing the BCL will not give us
/// (`File.GetUnixFileMode` has no owner sibling), so this reads `lstat(2)`
/// directly — per-OS struct layouts, pinned below and cross-checked against
/// the BCL's own mode read on every call, so a wrong offset refuses instead
/// of trusting.
// CA1416: the mode/ownership APIs are POSIX-only, and so is every line of this
// file — a Unix domain socket's directory, on the two OSes we ship (ADR-0012).
#pragma warning disable CA1416
public static class ShortRuntimeDir
{
    /// The base the short dir lives under. Fixed, not `$TMPDIR`: the shim and
    /// the daemon must compute the same path from the environment alone, and
    /// `/tmp` is the one short, world-writable, sticky directory both macOS
    /// and Linux guarantee.
    public const string DefaultBase = "/tmp";

    private const UnixFileMode DirMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// This process's effective uid: the identity every file it creates is
    /// owned by, so the one the directory must belong to.
    public static uint Uid => sys_geteuid();

    /// `<base>/captainhook-<uid>`.
    public static string PathFor(uint uid, string baseDir = DefaultBase) =>
        System.IO.Path.Combine(baseDir, "captainhook-" + uid.ToString(CultureInfo.InvariantCulture));

    /// Null when `dir` is absent (nothing to trust yet: no daemon can be
    /// listening there) or proven ours; otherwise the reason it is refused.
    /// `uid` is the owner it must have — production passes `Uid`; tests pass
    /// another to exercise the wrong-owner refusal without a second account.
    public static string? Check(string dir, uint uid)
    {
        var st = LStat(dir);
        if (st.Errno == ENOENT) return null;
        if (st.Errno != 0)
            return $"cannot inspect '{dir}' (errno {st.Errno})";
        if (st.Unsupported is { } why)
            return $"cannot inspect '{dir}': {why}";

        var type = st.Mode & S_IFMT;
        if (type == S_IFLNK) return $"'{dir}' is a symlink, not a directory";
        if (type != S_IFDIR) return $"'{dir}' is not a directory";
        if (st.Uid != uid) return $"'{dir}' is owned by uid {st.Uid}, not {uid}";

        var perms = st.Mode & 0x1FF;   // 0777
        // The layout cross-check: our struct read must agree with the BCL's
        // own stat of the same (non-symlink) directory. A disagreement means
        // the offsets are wrong for this platform — refuse rather than trust a
        // misread owner.
        try
        {
            if ((uint)File.GetUnixFileMode(dir) != perms)
                return $"cannot inspect '{dir}': stat layout mismatch on {RuntimeInformation.RuntimeIdentifier}";
        }
        catch (Exception ex) { return $"cannot inspect '{dir}': {ex.Message}"; }

        if ((perms & 0x3F) != 0)   // 0077
            return $"'{dir}' has mode {Convert.ToString(perms, 8)}; group and other bits must be clear (0700)";
        if ((perms & 0x1C0) != 0x1C0)   // 0700
            return $"'{dir}' has mode {Convert.ToString(perms, 8)}; the owner needs rwx (0700)";
        return null;
    }

    /// The daemon side: create `dir` 0700 when absent, then prove it — the
    /// check runs AFTER the create, so a directory someone else made in the
    /// window between a shim's check and this call is refused, never adopted.
    /// Never chmods: a loose directory is refused, not repaired.
    public static string? Ensure(string dir, uint uid)
    {
        try { Directory.CreateDirectory(dir, DirMode); }
        catch (Exception ex) { return $"cannot create '{dir}': {ex.Message}"; }
        var refusal = Check(dir, uid);
        if (refusal is null && !Directory.Exists(dir))
            return $"'{dir}' vanished after it was created";
        return refusal;
    }

    // ---- lstat(2) ---------------------------------------------------------

    private const int ENOENT = 2;
    private const uint S_IFMT = 0xF000, S_IFDIR = 0x4000, S_IFLNK = 0xA000;   // same on Linux and macOS

    private readonly record struct Stat(int Errno, uint Mode, uint Uid, string? Unsupported);

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint sys_geteuid();

    // glibc exports `lstat` since 2.33 (older ones only `__lxstat`; there the
    // lookup throws and the short dir is refused — today's behavior). macOS
    // arm64's `lstat` is the 64-bit-inode one; x64 keeps the legacy 32-bit
    // layout under that name and the modern one under `lstat$INODE64`.
    [DllImport("libc", SetLastError = true, EntryPoint = "lstat")]
    private static extern int sys_lstat(byte[] path, byte[] buf);

    [DllImport("libc", SetLastError = true, EntryPoint = "lstat$INODE64")]
    private static extern int sys_lstat_inode64(byte[] path, byte[] buf);

    /// (st_mode offset, st_mode width, st_uid offset) for the four shipped
    /// RIDs. Linux x64: dev 8, ino 8, nlink 8, mode 4, uid. Linux arm64
    /// (asm-generic): dev 8, ino 8, mode 4, nlink 4, uid. macOS (64-bit
    /// inode, both arches): dev 4, mode 2, nlink 2, ino 8, uid.
    private static (int ModeAt, int ModeWidth, int UidAt)? Layout() =>
        (OperatingSystem.IsMacOS(), RuntimeInformation.ProcessArchitecture) switch
        {
            (true, Architecture.Arm64 or Architecture.X64) => (4, 2, 16),
            (false, Architecture.X64) when OperatingSystem.IsLinux() => (24, 4, 28),
            (false, Architecture.Arm64) when OperatingSystem.IsLinux() => (16, 4, 24),
            _ => null,
        };

    private static Stat LStat(string path)
    {
        if (Layout() is not { } layout)
            return new Stat(0, 0, 0, $"no lstat layout for {RuntimeInformation.RuntimeIdentifier}");

        var cPath = new byte[Encoding.UTF8.GetByteCount(path) + 1];
        Encoding.UTF8.GetBytes(path, cPath);   // trailing 0 is the NUL terminator
        var buf = new byte[256];               // ≥ every layout's sizeof(struct stat)
        int rc;
        try
        {
            rc = OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.X64
                ? sys_lstat_inode64(cPath, buf)
                : sys_lstat(cPath, buf);
        }
        catch (EntryPointNotFoundException) { return new Stat(0, 0, 0, "this libc exports no lstat"); }
        if (rc != 0) return new Stat(Marshal.GetLastPInvokeError(), 0, 0, null);

        var mode = layout.ModeWidth == 2
            ? BitConverter.ToUInt16(buf, layout.ModeAt)
            : BitConverter.ToUInt32(buf, layout.ModeAt);
        return new Stat(0, mode, BitConverter.ToUInt32(buf, layout.UidAt), null);
    }
}
#pragma warning restore CA1416
