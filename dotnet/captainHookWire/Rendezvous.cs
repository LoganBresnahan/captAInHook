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

/// Where a given build's daemon and shims meet: socket, lock, and pid paths,
/// all carrying the content-identity version in their names. Resolution is a
/// PURE FUNCTION of the environment — the shim is per-invocation with no
/// memory and no side channel to the daemon, so both sides must *compute* the
/// identical path; probe-until-fits or any stateful negotiation is unsound by
/// construction (doc/platform.md). Nothing here touches the filesystem:
/// creating the directory and binding are the lock-holder's business
/// (lock-bind-rendezvous slice).
public sealed record RendezvousPaths(string Version, string RuntimeDir, string SocketPath, string LockPath, string PidPath)
{
    /// Tightest sun_path budget across supported platforms: macOS caps at 104
    /// bytes including the NUL, Linux at 108 (doc/platform.md). One number so
    /// a path that works here works everywhere.
    public const int MaxSocketPathBytes = 103;

    /// Resolve for this process. `runtimeDir`/`version` overrides are for
    /// tests; production callers take the environment's answer.
    public static RendezvousPaths Resolve(string? runtimeDir = null, string? version = null)
    {
        version ??= ContentIdentity.Current;
        var dir = runtimeDir ?? DefaultRuntimeDir();

        var socket = Path.Combine(dir, $"captaind-{version}.sock");
        var bytes = Encoding.UTF8.GetByteCount(socket);
        if (bytes > MaxSocketPathBytes)
            throw new InvalidOperationException(
                $"socket path '{socket}' is {bytes} bytes; Unix domain socket paths cap at {MaxSocketPathBytes}. " +
                "Set XDG_RUNTIME_DIR to a short per-user directory (e.g. /run/user/<uid>).");

        return new RendezvousPaths(
            version, dir, socket,
            Path.Combine(dir, $"captaind-{version}.lock"),
            Path.Combine(dir, $"captaind-{version}.pid"));
    }

    /// $XDG_RUNTIME_DIR/captainHook when set (Linux/systemd: short by
    /// construction, per-user 0700 tmpfs — the idiomatic home for runtime
    /// state), else ~/.captainHook (macOS/Windows never set XDG; their home
    /// paths are short). "Is it set?" is itself the deterministic branch.
    private static string DefaultRuntimeDir()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        return !string.IsNullOrEmpty(xdg)
            ? Path.Combine(xdg, "captainHook")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".captainHook");
    }
}
