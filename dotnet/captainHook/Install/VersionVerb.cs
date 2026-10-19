using System.Reflection;
using System.Runtime.InteropServices;
using CaptainHook.Wire;

namespace CaptainHook.Install;

// `captainHook version` (ADR-0020 d5): what is installed, and from WHERE. The
// base line is the one an installer leans on — the release layout reaches the
// engine through a FILE symlink (~/.local/bin/captainHook → versions/<v>/…),
// and every co-location rule (identity over the dir's DLLs, the shim's skew
// guard, bosun's spawn rung, ui/) assumes AppContext.BaseDirectory is the REAL
// versioned folder, not the link's directory. Printing it makes that a check a
// smoke test can assert instead of a belief. Human verb: stdout on purpose.
public static class VersionVerb
{
    /// `baseDir` is the test seam; production reads the running process's.
    public static int Run(TextWriter stdout, TextWriter stderr, string? baseDir = null)
    {
        baseDir ??= AppContext.BaseDirectory;
        string identity;
        try { identity = ContentIdentity.Compute(baseDir); }
        catch (Exception ex)
        {
            // A dev tree with no loose DLLs beside the binary still has a
            // version to report — say why the identity is missing, don't fail.
            identity = $"unavailable ({ex.Message})";
        }
        stdout.WriteLine($"captainHook {Informational()}");
        stdout.WriteLine($"identity    {identity}");
        stdout.WriteLine($"base        {baseDir}");
        stdout.WriteLine($"rid         {RuntimeInformation.RuntimeIdentifier}");
        return 0;
    }

    /// The release version stamped at publish (`-p:Version=<v>`); 1.0.0 is the
    /// SDK default for an unstamped dev build. Source-control queries are off
    /// (build determinism), so no `+sha` suffix ever rides along.
    internal static string Informational() =>
        typeof(VersionVerb).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(VersionVerb).Assembly.GetName().Version?.ToString()
        ?? "unknown";
}
