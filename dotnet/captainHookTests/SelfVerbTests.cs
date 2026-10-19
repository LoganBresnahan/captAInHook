using CaptainHook.Install;
using CaptainHook.Shim;
using CaptainHook.Wire;

namespace CaptainHook.Tests;

// `captainHook self …` (ADR-0020 d8): the engine runs the install.sh its version
// folder carries (rollback / prune / uninstall), or the newest one fetched from
// the release URL (update), with CAPTAINHOOK_INSTALL_DIR set from where the
// engine really lives. A fake install.sh records what reached it; the real
// script's behavior is the release smoke's (`self prune`, `self uninstall`).

public class SelfVerbTests : IDisposable
{
    private readonly string _root = Path.Combine("/tmp", "chk-self-" + Guid.NewGuid().ToString("N")[..8]);
    private string Install => Path.Combine(_root, "inst");
    private string VersionDir => Path.Combine(Install, "versions", "1.2.3");
    private string Record => Path.Combine(_root, "ran");

    public SelfVerbTests()
    {
        Directory.CreateDirectory(VersionDir);
        File.WriteAllText(Path.Combine(VersionDir, "install.sh"), FakeInstaller("bundled"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    /// An install.sh that writes who it is, its argv and the install dir it got.
    private string FakeInstaller(string who) =>
        $"printf '%s|%s|%s\\n' '{who}' \"$*\" \"$CAPTAINHOOK_INSTALL_DIR\" > '{Record}'\nexit 7\n";

    private (int Exit, string Stderr) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = SelfVerb.Run(args, stdout, stderr, baseDir: VersionDir + "/");
        return (exit, stderr.ToString());
    }

    [Theory]
    [InlineData("rollback", "--rollback")]
    [InlineData("prune", "--prune")]
    [InlineData("uninstall", "--uninstall")]
    public void LocalVerbs_RunTheBundledInstaller_WithTheInstallDirFromTheEnginesLocation(string verb, string flag)
    {
        var (exit, _) = Run(verb);
        Assert.Equal(7, exit);   // install.sh's own exit code, passed through
        Assert.Equal($"bundled|{flag}|{Install}", File.ReadAllText(Record).Trim());
    }

    [Fact]
    public void Update_FetchesTheLatestInstaller_FromTheReleaseUrl()
    {
        var latest = Path.Combine(_root, "rel", "latest", "download");
        Directory.CreateDirectory(latest);
        File.WriteAllText(Path.Combine(latest, "install.sh"), FakeInstaller("latest"));
        WithReleaseUrl("file://" + Path.Combine(_root, "rel"), () =>
        {
            var (exit, _) = Run("update");
            Assert.Equal(7, exit);
            Assert.Equal($"latest||{Install}", File.ReadAllText(Record).Trim());
        });
    }

    [Fact]
    public void UpdateToAVersion_FetchesThatReleasesInstaller_AndPassesTheVersion()
    {
        var pinned = Path.Combine(_root, "rel", "download", "v0.9.0");
        Directory.CreateDirectory(pinned);
        File.WriteAllText(Path.Combine(pinned, "install.sh"), FakeInstaller("v0.9.0"));
        WithReleaseUrl("file://" + Path.Combine(_root, "rel"), () =>
        {
            var (exit, _) = Run("update", "--version", "v0.9.0");
            Assert.Equal(7, exit);
            Assert.Equal($"v0.9.0|--version 0.9.0|{Install}", File.ReadAllText(Record).Trim());
        });
    }

    [Fact]
    public void Update_UnreachableRelease_Fails_AndRunsNothing()
    {
        // (The "couldn't download" line goes to the inherited terminal, not
        // the injected writer — the exit code and the absent record are the
        // assertable half.)
        WithReleaseUrl("file://" + Path.Combine(_root, "nowhere"), () =>
        {
            var (exit, _) = Run("update");
            Assert.Equal(1, exit);
            Assert.False(File.Exists(Record), "no installer ran");
        });
    }

    [Theory]
    [InlineData]
    [InlineData("frob")]
    [InlineData("update", "--version")]
    [InlineData("prune", "--force")]
    public void BadArguments_PrintUsage_Exit2(params string[] args)
    {
        var (exit, stderr) = Run(args);
        Assert.Equal(2, exit);
        Assert.Contains(SelfVerb.Usage, stderr);
        Assert.False(File.Exists(Record));
    }

    [Fact]
    public void ADevEngine_WithNoVersionsLayout_PrintsTheOneLiner_Exit1()
    {
        var dev = Path.Combine(_root, "bin", "Debug");
        Directory.CreateDirectory(dev);
        var stderr = new StringWriter();
        Assert.Equal(1, SelfVerb.Run(["prune"], new StringWriter(), stderr, baseDir: dev));
        Assert.Contains("wasn't installed by install.sh", stderr.ToString());
        Assert.Contains("releases/latest/download/install.sh | sh", stderr.ToString());
    }

    [Fact]
    public void AVersionFolderWithoutItsInstaller_IsNotAnInstallToManage()
    {
        File.Delete(Path.Combine(VersionDir, "install.sh"));   // a pre-0.1.1 folder
        var (exit, stderr) = Run("prune");
        Assert.Equal(1, exit);
        Assert.Contains("wasn't installed by install.sh", stderr);
    }

    [Fact]
    public void BinDirOnPath_FindsTheLinkThatResolvesIntoThisVersion()
    {
        var bin = Path.Combine(_root, "bin");
        var other = Path.Combine(_root, "other");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(VersionDir, "captainHook"), "");
        File.CreateSymbolicLink(Path.Combine(other, "captainHook"), "/nonexistent/captainHook");
        File.CreateSymbolicLink(Path.Combine(bin, "captainHook"), Path.Combine(VersionDir, "captainHook"));
        Assert.Equal(bin, SelfVerb.BinDirOnPath($"/nope:{other}:{bin}", VersionDir + "/"));
        Assert.Null(SelfVerb.BinDirOnPath($"/nope:{other}", VersionDir));
        Assert.Null(SelfVerb.BinDirOnPath(null, VersionDir));
    }

    [Fact]
    public void Self_IsAnEngineVerb_TheShimRefusesIt()
    {
        Assert.Equal(Mode.Self, Invocation.Parse(["self", "update"]).Mode);
        var stderr = new StringWriter();
        var exit = ShimMain.RunAsync(["self", "prune"], new MemoryStream(), new MemoryStream(), stderr)
            .GetAwaiter().GetResult();
        Assert.Equal(1, exit);
        Assert.Contains("self", stderr.ToString());
    }

    private static void WithReleaseUrl(string url, Action body)
    {
        var original = Environment.GetEnvironmentVariable("CAPTAINHOOK_RELEASE_URL");
        try
        {
            Environment.SetEnvironmentVariable("CAPTAINHOOK_RELEASE_URL", url);
            body();
        }
        finally { Environment.SetEnvironmentVariable("CAPTAINHOOK_RELEASE_URL", original); }
    }
}
