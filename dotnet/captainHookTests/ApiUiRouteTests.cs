using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using CaptainHook.Api;
using static CaptainHook.Tests.TestUtil;

namespace CaptainHook.Tests;

// ui-static-route + inert-shell-tests (ADR-0008 decision 2): the /ui shell is
// the API's ONE deliberate bearer-exempt surface, and these tests are what hold
// it to serving inert static bytes forever. Three contracts pinned here:
//   * the traversal guard (ResolveUiFile) never maps a request outside ui/ —
//     proven against files that really EXIST outside, so a broken guard fails
//     the test rather than passing on an unrelated 404;
//   * the bearer exemption is scoped to exactly /ui[/...]: every /api/v1/*
//     route still 401s, a /ui-prefixed sibling path is not exempt, and
//     Host/Origin still gate the shell (EvaluateShell);
//   * the shell is INERT: byte-identical with and without credentials, and the
//     daemon's bearer token never appears in served bytes.
public class UiResolveGuardTests : IDisposable
{
    private readonly string _root;     // the ui/ dir under test
    private readonly string _outside;  // a REAL secret one level up — escapes must be non-vacuous

    public UiResolveGuardTests()
    {
        _root = Directory.CreateTempSubdirectory("chk-ui-").FullName;
        File.WriteAllText(Path.Combine(_root, "index.html"), "<html>ui</html>");
        Directory.CreateDirectory(Path.Combine(_root, "assets"));
        File.WriteAllText(Path.Combine(_root, "assets", "app.js"), "js");
        _outside = Path.Combine(Path.GetDirectoryName(_root)!, "chk-ui-outside-" + Guid.NewGuid().ToString("N")[..8] + ".txt");
        File.WriteAllText(_outside, "SECRET");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        try { File.Delete(_outside); } catch { }
    }

    [Fact]
    public void EmptyRel_ResolvesIndexHtml() =>
        Assert.Equal(Path.Combine(_root, "index.html"), ApiHost.ResolveUiFile(_root, ""));

    [Fact]
    public void PlainAndNestedAssets_Resolve()
    {
        Assert.Equal(Path.Combine(_root, "index.html"), ApiHost.ResolveUiFile(_root, "index.html"));
        Assert.Equal(Path.Combine(_root, "assets", "app.js"), ApiHost.ResolveUiFile(_root, "assets/app.js"));
    }

    [Fact]
    public void DotDot_ToAnExistingFile_Null()
    {
        // The escape target EXISTS — a guard that resolved it would return a
        // real path, so null here proves the guard, not the filesystem.
        var rel = "../" + Path.GetFileName(_outside);
        Assert.True(File.Exists(Path.GetFullPath(Path.Combine(_root, rel))));   // non-vacuous
        Assert.Null(ApiHost.ResolveUiFile(_root, rel));
    }

    [Fact]
    public void DeepInteriorDotDot_Escaping_Null() =>
        Assert.Null(ApiHost.ResolveUiFile(_root, "assets/../../" + Path.GetFileName(_outside)));

    [Fact]
    public void InteriorDotDot_StayingInside_Resolves() =>
        // Normalization within the root is allowed — only ESCAPE is refused.
        Assert.Equal(Path.Combine(_root, "index.html"),
            ApiHost.ResolveUiFile(_root, "assets/../index.html"));

    [Fact]
    public void RootedPath_Null_EvenWhenItPointsInsideUiDir()
    {
        Assert.Null(ApiHost.ResolveUiFile(_root, "/etc/hostname"));
        // Absolute path to a file the route WOULD serve relatively: still null —
        // rooted request paths are refused outright, not resolved.
        Assert.Null(ApiHost.ResolveUiFile(_root, Path.Combine(_root, "index.html")));
    }

    [Fact]
    public void SiblingDirSharingThePrefix_Null()
    {
        // /x/ui vs /x/ui2: a prefix check without the separator would pass this.
        var sibling = _root + "2";
        Directory.CreateDirectory(sibling);
        try
        {
            File.WriteAllText(Path.Combine(sibling, "f.txt"), "SIBLING");
            Assert.Null(ApiHost.ResolveUiFile(_root, "../" + Path.GetFileName(sibling) + "/f.txt"));
        }
        finally { Directory.Delete(sibling, recursive: true); }
    }

    [Fact]
    public void TheRootItself_AndDirectories_Null()
    {
        Assert.Null(ApiHost.ResolveUiFile(_root, "."));
        Assert.Null(ApiHost.ResolveUiFile(_root, "assets"));   // a dir is not a file
    }

    [Fact]
    public void MissingFile_NulByte_LiteralPercentForm_AllNull()
    {
        Assert.Null(ApiHost.ResolveUiFile(_root, "nope.js"));
        Assert.Null(ApiHost.ResolveUiFile(_root, "a\0b"));
        // A percent form arriving here UNDECODED is a literal (weird) filename,
        // never an escape — the transport decodes before the guard runs.
        Assert.Null(ApiHost.ResolveUiFile(_root, "%2e%2e/" + Path.GetFileName(_outside)));
    }
}

// EvaluateShell — the bearer-exempt half of the gate, tested as directly as
// Evaluate is in ApiAuthGateTests: Host and Origin must hold on the shell even
// though the bearer does not.
public class UiShellGateTests
{
    private const string Token = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private static ApiAuthGate Gate(int port = 4665) => new(port, Token);

    [Fact]
    public void RightHost_NoOrigin_Allowed_WithoutAnyBearer() =>
        Assert.Null(Gate().EvaluateShell("127.0.0.1:4665", origin: null));

    [Fact]
    public void ForeignHost_403() =>
        Assert.Equal((403, "bad_host"), Gate().EvaluateShell("attacker.example", null));

    [Fact]
    public void ForeignOrigin_403_OwnOrigin_Allowed()
    {
        Assert.Equal((403, "bad_origin"), Gate().EvaluateShell("127.0.0.1:4665", "http://evil.example"));
        Assert.Null(Gate().EvaluateShell("127.0.0.1:4665", "http://127.0.0.1:4665"));
    }
}

// The route over real HTTP: serving, MIME, the exemption's exact scope, and the
// inert-shell contract. Traversal probes go over a RAW socket — HttpClient (and
// Uri) collapse dot segments CLIENT-side, so only a hand-written request line
// proves what the SERVER does with one.
public class ApiUiRouteHttpTests : IDisposable
{
    private readonly string _root;
    private readonly string _outsideName;   // secret file beside ui/, escape target
    private const string IndexHtml = "<html><body data-app>captainhook shell</body></html>";
    private const string AppJs = "console.log('captainhook');";

    public ApiUiRouteHttpTests()
    {
        _root = Directory.CreateTempSubdirectory("chk-uihttp-").FullName;
        File.WriteAllText(Path.Combine(_root, "index.html"), IndexHtml);
        Directory.CreateDirectory(Path.Combine(_root, "assets"));
        File.WriteAllText(Path.Combine(_root, "assets", "app.js"), AppJs);
        _outsideName = "chk-secret-" + Guid.NewGuid().ToString("N")[..8] + ".txt";
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(_root)!, _outsideName), "TOPSECRET");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        try { File.Delete(Path.Combine(Path.GetDirectoryName(_root)!, _outsideName)); } catch { }
    }

    private ApiHost StartUiHost() => ApiHost.Start(FreeTcpPort(), uiDir: _root);

    private static async Task<(HttpStatusCode Status, string Body, string? ContentType)> Get(
        int port, string path, string? bearer = null, string? origin = null, string method = "GET")
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var req = new HttpRequestMessage(new HttpMethod(method), $"http://127.0.0.1:{port}{path}");
        if (bearer is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (origin is not null) req.Headers.Add("Origin", origin);
        var resp = await client.SendAsync(req);
        return (resp.StatusCode, await resp.Content.ReadAsStringAsync(),
            resp.Content.Headers.ContentType?.ToString());
    }

    /// A verbatim HTTP/1.1 request the client stack cannot canonicalize first.
    private static async Task<string> RawGet(int port, string rawPath)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        var stream = tcp.GetStream();
        var req = Encoding.ASCII.GetBytes(
            $"GET {rawPath} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(req);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();   // status line + headers + body
    }

    [Fact]
    public async Task Shell_ServedWithoutAnyToken_AsHtml()
    {
        using var api = StartUiHost();
        var (status, body, contentType) = await Get(api.Port, "/ui");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(IndexHtml, body);
        Assert.StartsWith("text/html", contentType);
    }

    [Fact]
    public async Task Shell_ByteIdentical_AuthedAndUnauthed_AndAcrossSpellings()
    {
        // The inert contract: credentials change NOTHING about the shell — no
        // daemon state, no per-request content, ever.
        using var api = StartUiHost();
        var spellings = new[] { "/ui", "/ui/", "/ui/index.html" };
        foreach (var path in spellings)
        {
            var (_, unauthed, _) = await Get(api.Port, path);
            var (_, authed, _) = await Get(api.Port, path, bearer: api.Token);
            Assert.Equal(IndexHtml, unauthed);
            Assert.Equal(unauthed, authed);
        }
    }

    [Fact]
    public async Task ServedBytes_NeverContainTheToken()
    {
        using var api = StartUiHost();
        foreach (var path in new[] { "/ui", "/ui/assets/app.js" })
        {
            var (_, body, _) = await Get(api.Port, path);
            Assert.DoesNotContain(api.Token, body);
        }
    }

    [Fact]
    public async Task Asset_ServedWithItsMime()
    {
        using var api = StartUiHost();
        var (status, body, contentType) = await Get(api.Port, "/ui/assets/app.js");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(AppJs, body);
        Assert.StartsWith("text/javascript", contentType);
    }

    [Fact]
    public async Task MissingAsset_404()
    {
        using var api = StartUiHost();
        var (status, _, _) = await Get(api.Port, "/ui/nope.js");
        Assert.Equal(HttpStatusCode.NotFound, status);
    }

    [Fact]
    public async Task ApiRoutes_StillFullyGated_WhileTheShellIsExempt()
    {
        // The scoping pin: configuring a UI dir must not loosen ONE data route.
        using var api = StartUiHost();
        foreach (var path in new[] { "/api/v1/status", "/api/v1/policy", "/api/v1/events", "/api/v1/nonesuch" })
        {
            var (status, _, _) = await Get(api.Port, path);
            Assert.Equal(HttpStatusCode.Unauthorized, status);
        }
        var (shell, _, _) = await Get(api.Port, "/ui");
        Assert.Equal(HttpStatusCode.OK, shell);
    }

    [Fact]
    public async Task UiPrefixedSiblingPath_NotExempt()
    {
        // "/uifoo" shares the string prefix but not the path: full gate applies.
        using var api = StartUiHost();
        var (status, _, _) = await Get(api.Port, "/uifoo");
        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }

    [Fact]
    public async Task ForeignOrigin_403_OnTheShellToo()
    {
        // Bearer-only exemption: a hostile page's Origin is still refused.
        using var api = StartUiHost();
        var (status, _, _) = await Get(api.Port, "/ui", origin: "http://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, status);
    }

    [Fact]
    public async Task NonGetMethods_404_NeverServe()
    {
        using var api = StartUiHost();
        foreach (var method in new[] { "POST", "PUT", "DELETE" })
        {
            var (status, body, _) = await Get(api.Port, "/ui", method: method);
            Assert.Equal(HttpStatusCode.NotFound, status);
            Assert.DoesNotContain("captainhook shell", body);
        }
    }

    [Fact]
    public async Task WithoutAUiDir_UiStaysFullyGated()
    {
        // A pure API host (every production daemon before a GUI is staged next
        // to it passes a dir; hosts constructed WITHOUT one) keeps /ui behind
        // the full gate — the exemption exists only when there is a shell.
        using var api = ApiHost.Start(FreeTcpPort());
        var (status, _, _) = await Get(api.Port, "/ui");
        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }

    [Fact]
    public async Task MissingUiDirOnDisk_404sQuietly()
    {
        // Production before the first GUI deploy: the dir is configured but not
        // staged. Exempt, inert, empty — a 404, never an error or a hang.
        using var api = ApiHost.Start(FreeTcpPort(),
            uiDir: Path.Combine(_root, "not-staged-yet"));
        var (status, _, _) = await Get(api.Port, "/ui");
        Assert.Equal(HttpStatusCode.NotFound, status);
    }

    [Theory]
    [InlineData("/ui/../{0}")]                    // literal dot-dot, verbatim on the wire
    [InlineData("/ui/%2e%2e/{0}")]                // percent-encoded dots
    [InlineData("/ui/..%2f{0}")]                  // encoded separator
    [InlineData("/ui/%2e%2e%2f{0}")]              // both encoded
    [InlineData("/ui/assets/../../{0}")]          // interior climb
    [InlineData("/ui/assets%2f..%2f..%2f{0}")]    // interior climb, encoded
    public async Task Traversal_OverTheRawWire_NeverLeaks(string template)
    {
        // Raw socket: nothing client-side collapses the dots first. The secret
        // file REALLY exists one level above ui/ — whatever the server answers
        // (404 from the guard, 401/400 from earlier layers after ITS OWN
        // canonicalization), the secret bytes must never come back.
        using var api = StartUiHost();
        var response = await RawGet(api.Port, string.Format(template, _outsideName));
        Assert.DoesNotContain("TOPSECRET", response);
        Assert.False(response.StartsWith("HTTP/1.1 200"),
            $"traversal path answered 200: {response.Split('\r')[0]}");
    }

    [Fact]
    public async Task RawEncodedDotDot_TowardApiRoutes_GainsNoExemption()
    {
        // A UI-shaped prefix must not smuggle a request INTO the data surface:
        // if the transport normalizes /ui/../api/v1/status to a data route, the
        // full gate (401) must meet it — never the shell exemption.
        using var api = StartUiHost();
        foreach (var raw in new[] { "/ui/../api/v1/status", "/ui/%2e%2e/api/v1/status" })
        {
            var response = await RawGet(api.Port, raw);
            Assert.False(response.StartsWith("HTTP/1.1 200"),
                $"{raw} reached a data route unauthenticated: {response.Split('\r')[0]}");
            Assert.DoesNotContain("\"pid\"", response);   // /status's body shape
        }
    }
}
