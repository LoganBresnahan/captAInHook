using System.Diagnostics;
using System.Text;
using Xunit;

namespace CaptainHook.Tests;

// `release/runtime-floor.sh`, run as a real process against fixture files:
// package.sh calls it to refuse a build whose .NET is older than
// `release/runtime-floor` (the engine's bundled runtime, read from the
// bundle's embedded deps.json; the shim's AOT compiler, read from its
// project.assets.json). 0.1.5 shipped runtime 10.0.13 while the dev host
// built on 10.0.9, both below the CVE-2026-111497 fix; this is the gate that
// would have said so. The floor is read from the real file, so raising it
// needs no test edit.
public class RuntimeFloorTests : IDisposable
{
    private static string ReleaseDir =>
        Path.GetFullPath(Path.Combine(DogfoodInfra.PayloadsDir(), "..", "..", "release"));

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rtfloor-" + Guid.NewGuid().ToString("N"));

    public RuntimeFloorTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } }

    private static (int Major, int Minor, int Patch) Floor()
    {
        var line = File.ReadAllLines(Path.Combine(ReleaseDir, "runtime-floor"))
            .Single(l => l.Length > 0 && char.IsDigit(l[0]));
        var p = line.Trim().Split('.').Select(int.Parse).ToArray();
        return (p[0], p[1], p[2]);
    }

    private static string V(int major, int minor, int patch) => $"{major}.{minor}.{patch}";

    /// A stand-in for the single-file engine: binary noise around the deps.json
    /// library key the real bundle embeds.
    private string Bundle(string? runtime)
    {
        var path = Path.Combine(_dir, "captainHook");
        using var f = File.Create(path);
        f.Write(new byte[] { 0x7f, 0x45, 0x4c, 0x46, 0, 1, 2, 0xff, 0xfe });
        if (runtime is not null)
            f.Write(Encoding.UTF8.GetBytes(
                $"{{\"runtimepack.Microsoft.NETCore.App.Runtime.linux-x64/{runtime}\": {{\"runtime\": {{}}}}}}"));
        f.Write(new byte[] { 0, 0, 0x90, 0x90 });
        return path;
    }

    private string Assets(string? ilc)
    {
        var path = Path.Combine(_dir, "project.assets.json");
        var libs = ilc is null ? "" :
            $"\"Microsoft.DotNet.ILCompiler/{ilc}\": {{\"type\": \"package\"}}, " +
            $"\"runtime.linux-x64.Microsoft.DotNet.ILCompiler/{ilc}\": {{\"type\": \"package\"}}";
        File.WriteAllText(path, $"{{\"version\": 3, \"libraries\": {{{libs}}}}}");
        return path;
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> RunAsync(string bundle, string assets)
    {
        var psi = new ProcessStartInfo("sh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(Path.Combine(ReleaseDir, "runtime-floor.sh"));
        psi.ArgumentList.Add(bundle);
        psi.ArgumentList.Add(assets);
        psi.Environment["PATH"] = TestPath.Minimal;
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);
        return (p.ExitCode, await stdout, await stderr);
    }

    [Fact]
    public async Task AtTheFloor_Passes_AndKeepsStdoutEmpty()
    {
        var (ma, mi, pa) = Floor();
        var (exit, stdout, stderr) = await RunAsync(Bundle(V(ma, mi, pa)), Assets(V(ma, mi, pa)));
        Assert.Equal(0, exit);
        Assert.Equal("", stdout);   // package.sh's stdout is the archive path alone
        Assert.Contains("engine runtime .NET", stderr);
        Assert.Contains("shim AOT compiler .NET", stderr);
    }

    [Fact]
    public async Task AboveTheFloor_Passes_ComparedAsNumbersNotText()
    {
        var (ma, mi, pa) = Floor();
        // a three-digit patch sorts BELOW the floor as text ("10.0.100" < "10.0.15")
        var (exit, _, _) = await RunAsync(Bundle(V(ma, mi, 100 + pa)), Assets(V(ma, mi + 1, 0)));
        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task AnEngineRuntimeBelowTheFloor_IsRefused_ByName()
    {
        var (ma, mi, pa) = Floor();
        var (exit, _, stderr) = await RunAsync(Bundle(V(ma, mi, pa - 1)), Assets(V(ma, mi, pa)));
        Assert.Equal(1, exit);
        Assert.Contains($"engine runtime is .NET {V(ma, mi, pa - 1)}, below the floor", stderr);
    }

    [Fact]
    public async Task AShimCompilerBelowTheFloor_IsRefused_EvenWhenTheEngineIsCurrent()
    {
        var (ma, mi, pa) = Floor();
        var (exit, _, stderr) = await RunAsync(Bundle(V(ma, mi, pa)), Assets(V(ma, mi, pa - 1)));
        Assert.Equal(1, exit);
        Assert.Contains("shim AOT compiler is .NET", stderr);
    }

    [Fact]
    public async Task ASingleDigitPatch_IsBelowATwoDigitFloor()
    {
        // 10.0.9 sorts ABOVE 10.0.15 as text: the dev host's old SDK must still fail.
        var (ma, mi, pa) = Floor();
        Assert.True(pa >= 10, "the floor's patch is two digits, so a one-digit patch probes the text-sort trap");
        var (exit, _, stderr) = await RunAsync(Bundle(V(ma, mi, 9)), Assets(V(ma, mi, 9)));
        Assert.Equal(1, exit);
        Assert.Contains($"engine runtime is .NET {V(ma, mi, 9)}", stderr);
    }

    [Fact]
    public async Task AVersionItCannotFind_IsRefused_NotPassed()
    {
        var (exit, _, stderr) = await RunAsync(Bundle(null), Assets(null));
        Assert.Equal(1, exit);
        Assert.Contains("can't find the engine runtime's .NET version", stderr);
        Assert.Contains("can't find the shim AOT compiler's .NET version", stderr);
    }

    [Fact]
    public async Task MissingFiles_AreRefused()
    {
        var (exit, _, _) = await RunAsync(Path.Combine(_dir, "nope"), Path.Combine(_dir, "nope.json"));
        Assert.Equal(1, exit);
    }
}
