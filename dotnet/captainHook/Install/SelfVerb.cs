using System.Diagnostics;

namespace CaptainHook.Install;

// `captainHook self …` (ADR-0020 d8): manage the install this engine runs from,
// with the installer that installed it. Every version folder carries its own
// install.sh (versions/<v>/install.sh), so `rollback`, `prune` and `uninstall`
// run THAT one — offline, and the same script that laid the folder out. `update`
// fetches the newest installer instead (or <v>'s with --version), so an update
// never runs an old one. The same verbs, flags and wording as geex's and pharos's
// `self`, so the three apps' installers stay interchangeable.
//
// The engine is only the launcher: it finds the install from its REAL location
// (AppContext.BaseDirectory — the bin link resolved, ADR-0020 d5), exports
// CAPTAINHOOK_INSTALL_DIR (and CAPTAINHOOK_BIN_DIR, from the link that launched
// it, else the one on PATH that points here — without it, a link outside
// ~/.local/bin would read as "nothing installed"), and hands the terminal to
// `sh`. The script goes in on stdin (`sh -s -- <flags>`, the `curl | sh` path),
// as geex's and pharos's launchers do: no command line names versions/, which
// their installers' in-use checks grep for. Uninstall's guard — refusing
// while settings.json still runs the shim — is install.sh's, unchanged. A dev
// tree or `dotnet run` engine has no versions/ around it and says how to
// install instead. Human command: stdout/stderr on purpose.
public static class SelfVerb
{
    internal const string Usage = "usage: captainHook self update [--version VERSION] | rollback | prune | uninstall";

    internal const string DefaultReleases = "https://github.com/LoganBresnahan/captAInHook/releases";

    /// `baseDir` is the test seam; production reads the running process's.
    public static int Run(string[] args, TextWriter stdout, TextWriter stderr, string? baseDir = null)
    {
        string[]? installArgs = args switch
        {
            ["rollback"] => ["--rollback"],
            ["prune"] => ["--prune"],
            ["uninstall"] => ["--uninstall"],
            ["update"] => [],
            ["update", "--version", var v] => ["--version", v.TrimStart('v')],
            _ => null,
        };
        if (installArgs is null)
        {
            stderr.WriteLine(Usage);
            return 2;
        }

        var dir = Path.TrimEndingDirectorySeparator(baseDir ?? AppContext.BaseDirectory);
        var versions = Path.GetDirectoryName(dir);
        var bundled = Path.Combine(dir, "install.sh");
        if (versions is null || Path.GetFileName(versions) != "versions" || !File.Exists(bundled))
        {
            stderr.WriteLine(
                "captainHook self: this captainHook wasn't installed by install.sh, so it has no install to manage.\n" +
                "Install or update captAInHook with:\n\n" +
                $"    curl -fsSL {DefaultReleases}/latest/download/install.sh | sh");
            return 1;
        }

        var psi = new ProcessStartInfo("sh") { UseShellExecute = false };
        psi.Environment["CAPTAINHOOK_INSTALL_DIR"] = Path.GetDirectoryName(versions)!;
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CAPTAINHOOK_BIN_DIR"))
            && (BinDirOfLaunchLink(dir) ?? BinDirOnPath(Environment.GetEnvironmentVariable("PATH"), dir)) is { } binDir)
            psi.Environment["CAPTAINHOOK_BIN_DIR"] = binDir;

        if (args[0] == "update")
        {
            // The newest installer, fetched by the same tools install.sh itself
            // uses (curl, else wget — either follows a file:// release URL in
            // tests), into a temp file it never outlives.
            var releases = Environment.GetEnvironmentVariable("CAPTAINHOOK_RELEASE_URL") is { Length: > 0 } r
                ? r : DefaultReleases;
            var url = installArgs.Length == 0
                ? $"{releases}/latest/download/install.sh"
                : $"{releases}/download/v{installArgs[1]}/install.sh";
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("""
                tmp=$(mktemp "${TMPDIR:-/tmp}/captainhook-install.XXXXXX") || exit 1
                trap 'rm -f "$tmp"' EXIT
                trap 'exit 130' INT TERM
                if command -v curl >/dev/null 2>&1; then curl -fsSL --retry 3 --connect-timeout 20 -o "$tmp" "$1"
                else wget -q -O "$tmp" "$1"; fi || { echo "captainHook self: couldn't download $1" >&2; exit 1; }
                shift
                sh -s -- "$@" <"$tmp"
                """);
            psi.ArgumentList.Add("captainHook-self");   // $0
            psi.ArgumentList.Add(url);
        }
        else
        {
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("""exec sh -s -- "$@" <"$0" """);
            psi.ArgumentList.Add(bundled);   // $0
        }
        foreach (var a in installArgs) psi.ArgumentList.Add(a);

        stdout.Flush();
        using var proc = Process.Start(psi);
        if (proc is null)
        {
            stderr.WriteLine("captainHook self: could not start sh");
            return 1;
        }
        proc.WaitForExit();
        return proc.ExitCode;
    }

    /// The directory of the link this process was launched through, when argv[0]
    /// is a path to a symlink resolving into `versionDir`. Linux only (the raw
    /// argv[0] is /proc/self/cmdline's first field; .NET reports the resolved
    /// path instead); elsewhere, and for a bare `captainHook`, the PATH scan.
    internal static string? BinDirOfLaunchLink(string versionDir)
    {
        try
        {
            if (!File.Exists("/proc/self/cmdline")) return null;
            var argv0 = File.ReadAllText("/proc/self/cmdline").Split('\0')[0];
            if (!argv0.Contains('/')) return null;
            var full = Path.GetFullPath(argv0);
            return ResolvesInto(full, versionDir) ? Path.GetDirectoryName(full) : null;
        }
        catch { return null; }
    }

    private static bool ResolvesInto(string link, string versionDir) =>
        new FileInfo(link).ResolveLinkTarget(returnFinalTarget: true) is { } target
        && Path.GetDirectoryName(target.FullName) == Path.TrimEndingDirectorySeparator(versionDir);

    /// The bin dir install.sh linked us from: the first PATH entry whose
    /// `captainHook` is a symlink resolving into `versionDir`. Null when none
    /// is — install.sh then uses its own default (or the user's env).
    internal static string? BinDirOnPath(string? path, string versionDir)
    {
        if (string.IsNullOrEmpty(path)) return null;
        foreach (var entry in path.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            try { if (ResolvesInto(Path.Combine(entry, "captainHook"), versionDir)) return entry; }
            catch { /* unreadable PATH entry: not ours */ }
        }
        return null;
    }
}
