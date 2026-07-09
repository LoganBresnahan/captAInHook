using CaptainHook.Api;
using CaptainHook.Shim;
using CaptainHook.Wire;

namespace CaptainHook.Tests;

// ui-cli-verb (ADR-0008 decision 3): the token handoff's CLI half. The pins
// that matter: the token rides the FRAGMENT (never a query param a server or
// log would see), the verb never echoes the token to either stream, and an
// absent api.json is a clear refusal, not a spawn.
public class UiVerbTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("chk-uiverb-").FullName;
    private string ApiJson => Path.Combine(_dir, "captaind-test.api.json");
    private const string Token = "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff";

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void BuildUrl_TokenInFragment_NeverInQuery()
    {
        var url = UiVerb.BuildUrl(4665, Token);
        Assert.Equal($"http://127.0.0.1:4665/ui#t={Token}", url);
        Assert.DoesNotContain("?", url);   // a query param lands in access logs; the fragment does not
        // The shell's bootstrap regex (web/src/auth.ts) must be able to parse
        // what we emit — pin the exact fragment shape here, its consuming end.
        Assert.Matches("#t=[0-9a-f]+$", url);
    }

    [Fact]
    public async Task AbsentApiJson_Exit1_SaysNotRunning_NeverLaunches()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var launched = false;
        var exit = await UiVerb.RunAsync(stdout, stderr, ApiJson, _ => launched = true);
        Assert.Equal(1, exit);
        Assert.False(launched);
        Assert.Contains("daemon isn't running", stderr.ToString());
    }

    [Fact]
    public async Task PresentApiJson_LaunchesTheExactUrl_AndNeverPrintsTheToken()
    {
        ApiDiscovery.Write(ApiJson, new ApiDiscovery(4665, Token, 1234, "test"));
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        string? url = null;
        var exit = await UiVerb.RunAsync(stdout, stderr, ApiJson, u => { url = u; return true; });
        Assert.Equal(0, exit);
        Assert.Equal(UiVerb.BuildUrl(4665, Token), url);
        // The credential goes to the browser and NOWHERE else — scrollback is
        // a log too.
        Assert.DoesNotContain(Token, stdout.ToString());
        Assert.DoesNotContain(Token, stderr.ToString());
        Assert.Contains("http://127.0.0.1:4665/ui", stdout.ToString());
    }

    [Fact]
    public async Task LauncherFailure_Exit1_TellsTheFallback_StillNoToken()
    {
        ApiDiscovery.Write(ApiJson, new ApiDiscovery(4665, Token, 1234, "test"));
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await UiVerb.RunAsync(stdout, stderr, ApiJson, _ => false);
        Assert.Equal(1, exit);
        Assert.Contains("could not launch", stderr.ToString());
        Assert.DoesNotContain(Token, stdout.ToString() + stderr.ToString());
    }

    [Fact]
    public void Parse_UiVerb_IsUiMode()
    {
        Assert.Equal(Mode.Ui, Invocation.Parse(["ui"]).Mode);
        // And it never shadows a hook dispatch: "hook ui-event" stays a hook.
        Assert.Equal(Mode.Shim, Invocation.Parse(["hook", "user-prompt-submit"]).Mode);
    }

    [Fact]
    public async Task Shim_RefusesTheUiVerb_LikeEveryEngineVerb()
    {
        using var stdin = new MemoryStream();
        using var stdout = new MemoryStream();
        var stderr = new StringWriter();
        var exit = await ShimMain.RunAsync(["ui"], stdin, stdout, stderr);
        Assert.Equal(1, exit);
        Assert.Equal(0, stdout.Length);   // stdout is the hook channel — untouched
        Assert.Contains("hook shim only", stderr.ToString());
    }
}
