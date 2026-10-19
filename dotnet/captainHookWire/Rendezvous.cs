using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;

namespace CaptainHook.Wire;

// ADR-0004 decision 3: the shim/daemon rendezvous is versioned by the binary's
// CONTENT identity — a hash of the ModuleVersionIds of all application
// assemblies in the app directory — deliberately not the informational version
// (an uncommitted dev rebuild keeps the version string while changing
// behavior; a fresh MVID is minted on every compile). Any rebuild, committed
// or dirty, host or F#-lib assembly, thus rendezvouses on a fresh socket by
// construction: shim/daemon version mismatch is unrepresentable, and no
// handshake or compat logic exists to get wrong.

/// Computes the binary's content identity.
public static class ContentIdentity
{
    // Cached: the app directory cannot change under a running process, and the
    // shim computes this exactly once per invocation on its hot path.
    private static readonly Lazy<string> _current = new(() => Compute(AppContext.BaseDirectory));

    /// The content identity of THIS process's application directory.
    public static string Current => _current.Value;

    /// Content identity of an arbitrary app directory: every managed assembly's
    /// (fileName, MVID), hashed. Files without managed metadata (native libs,
    /// stray non-assembly .dlls) are skipped — they don't carry an MVID and the
    /// managed assemblies are what determine dispatch behavior.
    public static string Compute(string appDir)
    {
        var modules = new List<(string Name, Guid Mvid)>();
        foreach (var path in Directory.EnumerateFiles(appDir, "*.dll"))
        {
            try
            {
                using var stream = File.OpenRead(path);
                using var pe = new PEReader(stream);
                if (!pe.HasMetadata) continue;
                var md = pe.GetMetadataReader();
                modules.Add((Path.GetFileName(path), md.GetGuid(md.GetModuleDefinition().Mvid)));
            }
            catch (BadImageFormatException) { /* not a managed assembly — no MVID to contribute */ }
        }
        if (modules.Count == 0)
            throw new InvalidOperationException($"no managed assemblies found in '{appDir}' — cannot compute a content identity");
        return Hash(modules);
    }

    /// Read one assembly's MVID; null when the file is missing, unreadable, or
    /// carries no managed metadata. The shim's wire-stamp skew guard leans on
    /// this (ADR-0004 decision 7 amendment): the same read Compute does, for
    /// one named file, never throwing — absence is an answer, not an error.
    public static Guid? TryReadMvid(string dllPath)
    {
        try
        {
            using var stream = File.OpenRead(dllPath);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) return null;
            var md = pe.GetMetadataReader();
            return md.GetGuid(md.GetModuleDefinition().Mvid);
        }
        catch
        {
            return null;
        }
    }

    /// The pure core: order-insensitive (sorted by file name here, so directory
    /// enumeration order can never change the identity), first 12 hex chars of
    /// SHA-256 over the (name, mvid) lines.
    public static string Hash(IEnumerable<(string Name, Guid Mvid)> modules)
    {
        var sb = new StringBuilder();
        foreach (var (name, mvid) in modules.OrderBy(m => m.Name, StringComparer.Ordinal))
            sb.Append(name).Append('=').Append(mvid.ToString("N")).Append('\n');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..12];
    }
}

/// Where a given build's daemon and shims meet: socket, lock, pid, and the
/// management-API discovery file, all carrying the content-identity version in
/// their names. The directory CHOICE is a pure function of the environment —
/// the shim is per-invocation with no memory and no side channel to the
/// daemon, so both sides must *compute* the identical path; probe-until-fits
/// or any stateful negotiation is unsound by construction (doc/platform.md).
/// The one filesystem read is the short dir's trust check (ShortRuntimeDir):
/// it never changes WHICH dir is chosen, only whether it may be used.
/// Creating the directory and binding are the lock-holder's business
/// (DaemonRendezvous, through EnsureRuntimeDir).
public sealed record RendezvousPaths(
    string Version, string RuntimeDir, string SocketPath, string LockPath, string PidPath, string ApiJsonPath)
{
    /// Tightest sun_path budget across supported platforms: macOS caps at 104
    /// bytes including the NUL, Linux at 108 (doc/platform.md). One number so
    /// a path that works here works everywhere.
    public const int MaxSocketPathBytes = 103;

    /// Why the short per-uid dir was chosen over the preferred one; null when
    /// the preferred dir is in use (or the dir was passed explicitly).
    public string? ShortDirReason { get; init; }

    /// Resolve for this process. `runtimeDir`/`version` overrides are for
    /// tests; production callers take the environment's answer. An explicit
    /// `runtimeDir` is used as given — the short-dir fallback is a property of
    /// the DEFAULT choice only.
    public static RendezvousPaths Resolve(string? runtimeDir = null, string? version = null)
    {
        version ??= ContentIdentity.Current;
        string dir;
        string? shortReason = null;
        if (runtimeDir is not null) dir = runtimeDir;
        else
        {
            var choice = ChooseRuntimeDir();
            // Refused short dir: the caller's existing catch keeps today's
            // behavior — no daemon, the hook collapses — and logs this text.
            if (choice.Refusal is not null)
                throw new InvalidOperationException(
                    $"{choice.ShortDirReason}; the short dir is refused: {choice.Refusal}");
            dir = choice.Dir;
            shortReason = choice.ShortDirReason;
        }

        var socket = Path.Combine(dir, $"captaind-{version}.sock");
        var bytes = Encoding.UTF8.GetByteCount(socket);
        if (bytes > MaxSocketPathBytes)
            throw new InvalidOperationException(
                $"socket path '{socket}' is {bytes} bytes; Unix domain socket paths cap at {MaxSocketPathBytes}. " +
                "Set XDG_RUNTIME_DIR to a short per-user directory (e.g. /run/user/<uid>).");

        // api.json is version-partitioned like its siblings — the port it
        // advertises is a global singleton (ADR-0007 N1), but the DISCOVERY
        // FILE is per-identity so a draining incumbent and its successor own
        // separate files and neither ever deletes the other's (the same
        // cross-version-delete hazard the versioned socket avoids). No sun_path
        // cap here: a regular file, not a socket.
        return new RendezvousPaths(
            version, dir, socket,
            Path.Combine(dir, $"captaind-{version}.lock"),
            Path.Combine(dir, $"captaind-{version}.pid"),
            Path.Combine(dir, $"captaind-{version}.api.json"))
        { ShortDirReason = shortReason };
    }

    /// Make the runtime dir exist, owner-only, before the lock is taken. The
    /// preferred dir is created 0700 and tightened best-effort (the log layer
    /// may have made ~/.captainHook at the umask default first). The short
    /// dir is never tightened: it is created 0700 and then PROVEN ours, and a
    /// directory that fails the proof throws — it is shared ground, and
    /// adopting one someone else made would hand them the socket.
    public void EnsureRuntimeDir()
    {
        if (ShortDirReason is not null)
        {
            if (ShortRuntimeDir.Ensure(RuntimeDir, ShortRuntimeDir.Uid) is { } refusal)
                throw new InvalidOperationException($"the short dir is refused: {refusal}");
            return;
        }
        // CA1416: POSIX-only, like the socket this directory exists to hold.
#pragma warning disable CA1416
        const UnixFileMode dirMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        Directory.CreateDirectory(RuntimeDir, dirMode);
        try { File.SetUnixFileMode(RuntimeDir, dirMode); } catch { /* not ours to tighten / odd FS */ }
#pragma warning restore CA1416
    }

    /// The longest socket file name a production identity produces
    /// (`captaind-` + 12 hex + `.sock`): the choice measures the preferred dir
    /// against it, so it never depends on which version is asking.
    private const string WorstCaseSocketName = "captaind-000000000000.sock";

    /// The runtime dir this environment uses, and why (ADR-0004 d3, amended
    /// 2026-10-19). Production reads XDG_RUNTIME_DIR, the home dir and the
    /// effective uid; doctor and `captainHook version` print the answer.
    public static RuntimeDirChoice ChooseRuntimeDir() =>
        ChooseRuntimeDir(DefaultRuntimeDir(), ShortRuntimeDir.Uid, ShortRuntimeDir.DefaultBase);

    /// The pure core plus the trust check: `preferred` when a socket fits in
    /// it, else `<shortBase>/captainhook-<uid>` with the reason — and, for the
    /// short dir only, `Refusal` when it exists but is not proven ours.
    public static RuntimeDirChoice ChooseRuntimeDir(string preferred, uint uid, string shortBase)
    {
        var bytes = Encoding.UTF8.GetByteCount(Path.Combine(preferred, WorstCaseSocketName));
        if (bytes <= MaxSocketPathBytes) return new RuntimeDirChoice(preferred, null, null);

        var dir = ShortRuntimeDir.PathFor(uid, shortBase);
        var reason = $"a socket under '{preferred}' would be {bytes} bytes, over the {MaxSocketPathBytes}-byte sun_path cap";
        return new RuntimeDirChoice(dir, reason, ShortRuntimeDir.Check(dir, uid));
    }

    /// The PREFERRED dir: $XDG_RUNTIME_DIR/captainHook when set (Linux/systemd:
    /// short by construction, per-user 0700 tmpfs — the idiomatic home for
    /// runtime state), else ~/.captainHook (macOS never sets XDG; home paths
    /// are short). "Is it set?" is itself the deterministic branch. When a
    /// socket would not fit in it, ChooseRuntimeDir falls back to the short dir.
    public static string DefaultRuntimeDir()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        return !string.IsNullOrEmpty(xdg)
            ? Path.Combine(xdg, "captainHook")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".captainHook");
    }
}

/// Which runtime dir is in use. `ShortDirReason` non-null ⇒ the short per-uid
/// dir was chosen, and why; `Refusal` non-null ⇒ that dir exists but is not
/// proven ours, so no daemon may use it (hooks collapse, as before the fallback).
public sealed record RuntimeDirChoice(string Dir, string? ShortDirReason, string? Refusal)
{
    /// The human line doctor and `captainHook version` print.
    public string Describe() =>
        ShortDirReason is null ? Dir
        : Refusal is null ? $"{Dir} (short dir: {ShortDirReason})"
        : $"{Dir} REFUSED — {Refusal} ({ShortDirReason}); hooks run without a daemon";
}
