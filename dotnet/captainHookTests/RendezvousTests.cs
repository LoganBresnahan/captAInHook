using System.Text;
using CaptainHook.Core;
using CaptainHook.Wire;

namespace CaptainHook.Tests;

// content-identity-versioned-socket (ADR-0004 decision 3): the identity is
// deterministic — two builds differ, same build agrees — and path resolution
// is a pure function of the environment, guarded against the sun_path cap.

public class ContentIdentityTests
{
    [Fact]
    public void Hash_IsOrderInsensitive_EnumerationOrderCanNeverChangeIdentity()
    {
        var a = ("a.dll", Guid.Parse("11111111-1111-1111-1111-111111111111"));
        var b = ("b.dll", Guid.Parse("22222222-2222-2222-2222-222222222222"));
        Assert.Equal(ContentIdentity.Hash([a, b]), ContentIdentity.Hash([b, a]));
    }

    [Fact]
    public void Hash_DifferentMvid_DifferentIdentity()
    {
        // The rebuild case: same file name, fresh MVID (minted every compile).
        var v1 = ContentIdentity.Hash([("captainHook.dll", Guid.Parse("11111111-1111-1111-1111-111111111111"))]);
        var v2 = ContentIdentity.Hash([("captainHook.dll", Guid.Parse("22222222-2222-2222-2222-222222222222"))]);
        Assert.NotEqual(v1, v2);
    }

    [Fact]
    public void Hash_NameParticipates_SameMvidsDifferentFilesDiffer()
    {
        var mvid = Guid.Parse("11111111-1111-1111-1111-111111111111");
        Assert.NotEqual(
            ContentIdentity.Hash([("a.dll", mvid)]),
            ContentIdentity.Hash([("b.dll", mvid)]));
    }

    [Fact]
    public void Hash_IsShortLowercaseHex()
    {
        var v = ContentIdentity.Hash([("a.dll", Guid.NewGuid())]);
        Assert.Equal(12, v.Length);
        Assert.All(v, c => Assert.True("0123456789abcdef".Contains(c), $"non-hex char '{c}'"));
    }

    [Fact]
    public void Compute_OnThisTestBinDir_IsStableAcrossCalls()
    {
        // "Same build agrees": the whole rendezvous rests on shim and daemon
        // computing the same version from the same app directory.
        var dir = AppContext.BaseDirectory;   // contains captainHook.dll + captainHookActors.dll + deps
        Assert.Equal(ContentIdentity.Compute(dir), ContentIdentity.Compute(dir));
    }

    [Fact]
    public void Compute_AssemblySetParticipates_AndNonAssemblyDllsAreSkipped()
    {
        using var tmp = new TempHarnessDir();   // reused throwaway-dir helper
        var src = Path.Combine(AppContext.BaseDirectory, "captainHook.dll");

        File.Copy(src, Path.Combine(tmp.Path, "one.dll"));
        var oneAssembly = ContentIdentity.Compute(tmp.Path);

        // A second assembly (same bytes, new name — name participates) changes the identity...
        File.Copy(src, Path.Combine(tmp.Path, "two.dll"));
        var twoAssemblies = ContentIdentity.Compute(tmp.Path);
        Assert.NotEqual(oneAssembly, twoAssemblies);

        // ...but a .dll with no managed metadata contributes nothing (native
        // libs living next to the app must not perturb the identity).
        File.WriteAllBytes(Path.Combine(tmp.Path, "native.dll"), Encoding.UTF8.GetBytes("MZ not a managed assembly"));
        Assert.Equal(twoAssemblies, ContentIdentity.Compute(tmp.Path));
    }

    [Fact]
    public void Compute_NoManagedAssemblies_ThrowsInsteadOfMintingAnIdentity()
    {
        using var tmp = new TempHarnessDir();
        Assert.Throws<InvalidOperationException>(() => ContentIdentity.Compute(tmp.Path));
    }
}

public class RendezvousPathsTests
{
    [Fact]
    public void Resolve_AllThreeFilesCarryTheVersion_UnderTheGivenDir()
    {
        var p = RendezvousPaths.Resolve("/tmp/rv", "abc123def456");
        Assert.Equal("/tmp/rv/captaind-abc123def456.sock", p.SocketPath);
        Assert.Equal("/tmp/rv/captaind-abc123def456.lock", p.LockPath);
        Assert.Equal("/tmp/rv/captaind-abc123def456.pid", p.PidPath);
    }

    [Fact]
    public void Resolve_IsDeterministic_SameEnvSameAnswer()
    {
        // The memoryless shim and the daemon must compute the identical path.
        Assert.Equal(RendezvousPaths.Resolve("/tmp/rv", "v"), RendezvousPaths.Resolve("/tmp/rv", "v"));
    }

    [Fact]
    public void Resolve_SocketPathOverSunPathCap_FailsWithActionableError()
    {
        var longDir = "/tmp/" + new string('x', 120);
        var ex = Assert.Throws<InvalidOperationException>(() => RendezvousPaths.Resolve(longDir, "v"));
        // The error must name the escape hatch, not just refuse.
        Assert.Contains("XDG_RUNTIME_DIR", ex.Message);
        Assert.Contains(RendezvousPaths.MaxSocketPathBytes.ToString(), ex.Message);
    }

    [Fact]
    public void Resolve_DefaultDir_PrefersXdgRuntimeDir_ElseHomeDotCaptainHook()
    {
        // Env is process-global; the suite runs sequentially (TestInfra), so
        // set-and-restore is safe here.
        var original = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        try
        {
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", "/tmp/xdg-test");
            Assert.Equal("/tmp/xdg-test/captainHook", RendezvousPaths.Resolve(version: "v").RuntimeDir);

            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", null);
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Assert.Equal(Path.Combine(home, ".captainHook"), RendezvousPaths.Resolve(version: "v").RuntimeDir);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", original);
        }
    }
}
