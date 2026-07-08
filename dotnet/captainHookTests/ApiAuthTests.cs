using System.Net;
using System.Net.Http.Headers;
using CaptainHook.Api;
using static CaptainHook.Tests.TestUtil;

namespace CaptainHook.Tests;

// auth-token-origin (ADR-0007 decision 6), the pure decision: the whole
// security logic — Host (DNS-rebind), Origin (CSRF), bearer token (authn,
// constant-time) — tested directly, deterministically, with no HttpListener in
// the way. This is where the Host→403 branch is exercised: over real HTTP the
// managed listener's prefix match refuses a foreign Host with 404 before the
// gate ever runs (ApiAuthHttpTests pins that platform behavior), so the gate's
// own portable Host check can only be proven here.
public class ApiAuthGateTests
{
    private const string Token = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private static ApiAuthGate Gate(int port = 4665) => new(port, Token);

    private static string Authority(int port = 4665) => $"127.0.0.1:{port}";
    private static string Bearer(string t) => $"Bearer {t}";

    [Fact]
    public void RightAuthority_NoOrigin_RightToken_Authorized() =>
        Assert.Null(Gate().Evaluate(Authority(), origin: null, Bearer(Token)));

    [Fact]
    public void ForeignHost_403_BadHost() =>
        Assert.Equal((403, "bad_host"), Gate().Evaluate("attacker.example", null, Bearer(Token)));

    [Fact]
    public void NullHost_403_BadHost() =>
        Assert.Equal((403, "bad_host"), Gate().Evaluate(null, null, Bearer(Token)));

    [Fact]
    public void WrongPortInHost_403_BadHost() =>
        // A different loopback port is a different authority — exact match only.
        Assert.Equal((403, "bad_host"), Gate(4665).Evaluate("127.0.0.1:5000", null, Bearer(Token)));

    [Fact]
    public void ForeignOrigin_403_BadOrigin_EvenWithRightToken() =>
        Assert.Equal((403, "bad_origin"),
            Gate().Evaluate(Authority(), "http://evil.example", Bearer(Token)));

    [Fact]
    public void OwnOrigin_Authorized() =>
        Assert.Null(Gate().Evaluate(Authority(), $"http://127.0.0.1:4665", Bearer(Token)));

    [Fact]
    public void AbsentOrigin_Authorized_SoCurlWorks() =>
        Assert.Null(Gate().Evaluate(Authority(), origin: null, Bearer(Token)));

    [Fact]
    public void NoAuthHeader_401() =>
        Assert.Equal((401, "unauthorized"), Gate().Evaluate(Authority(), null, authorization: null));

    [Fact]
    public void NonBearerScheme_401() =>
        Assert.Equal((401, "unauthorized"), Gate().Evaluate(Authority(), null, "Basic dTpw"));

    [Fact]
    public void BareTokenWithoutScheme_401() =>
        Assert.Equal((401, "unauthorized"), Gate().Evaluate(Authority(), null, Token));

    [Fact]
    public void WrongToken_SameLength_401() =>
        Assert.Equal((401, "unauthorized"),
            Gate().Evaluate(Authority(), null, Bearer(new string('a', Token.Length))));

    [Fact]
    public void WrongToken_DifferentLength_401() =>
        Assert.Equal((401, "unauthorized"), Gate().Evaluate(Authority(), null, Bearer("short")));

    [Fact]
    public void OneFlippedChar_401()
    {
        var chars = Token.ToCharArray();
        chars[^1] = chars[^1] == 'f' ? 'e' : 'f';
        Assert.Equal((401, "unauthorized"), Gate().Evaluate(Authority(), null, Bearer(new string(chars))));
    }

    [Fact]
    public void SchemeIsCaseInsensitive_ButTokenIsNot()
    {
        Assert.Null(Gate().Evaluate(Authority(), null, $"bEaReR {Token}"));            // scheme casing ok
        Assert.Equal((401, "unauthorized"),
            Gate().Evaluate(Authority(), null, Bearer(Token.ToUpperInvariant())));      // token casing is content
    }

    [Fact]
    public void HostCheckPrecedesOrigin_PrecedesToken()
    {
        // Ordering is observable through the returned reason: a request that
        // fails all three reports bad_host (checked first), not unauthorized.
        Assert.Equal((403, "bad_host"),
            Gate().Evaluate("attacker.example", "http://evil.example", authorization: null));
        // Valid host, bad origin, bad token → bad_origin (origin before token).
        Assert.Equal((403, "bad_origin"),
            Gate().Evaluate(Authority(), "http://evil.example", authorization: null));
    }
}

// auth-token-origin over real HTTP: the gate wired into ApiHost, plus the
// platform behavior it composes with. Requests carry the bearer token from
// api.Token (pure-listener hosts) since the discovery-file path is exercised in
// ApiDiscoveryTests.
public class ApiAuthHttpTests
{
    // Default path is a genuinely UNWIRED route so an authorized request lands
    // on the 404 router — the auth tests assert the GATE, never endpoint content
    // (real endpoints are pinned in ApiReadEndpointsTests).
    private static async Task<(HttpStatusCode Status, string? WwwAuth)> Send(
        int port, string? bearer, string? origin = null, string? host = null, string path = "/api/v1/nonesuch")
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}{path}");
        if (bearer is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (origin is not null) req.Headers.Add("Origin", origin);
        if (host is not null) req.Headers.Host = host;
        var resp = await client.SendAsync(req);
        return (resp.StatusCode, resp.Headers.WwwAuthenticate.ToString() is { Length: > 0 } w ? w : null);
    }

    [Fact]
    public async Task NoToken_401_WithWwwAuthenticateBearer()
    {
        using var api = ApiHost.Start(FreeTcpPort());
        var (status, wwwAuth) = await Send(api.Port, bearer: null);
        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Contains("Bearer", wwwAuth);   // RFC 7235: a 401 names the scheme
    }

    [Fact]
    public async Task RightToken_PassesGate_ReachesThe404Router()
    {
        using var api = ApiHost.Start(FreeTcpPort());
        var (status, _) = await Send(api.Port, bearer: api.Token);
        Assert.Equal(HttpStatusCode.NotFound, status);   // authorized → router → 404 (no endpoints yet)
    }

    [Fact]
    public async Task WrongToken_401()
    {
        using var api = ApiHost.Start(FreeTcpPort());
        var (status, _) = await Send(api.Port, bearer: new string('a', api.Token.Length));
        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }

    [Fact]
    public async Task ForeignOrigin_403_EvenWithTheRightToken()
    {
        // A malicious tab holding the token still cannot drive the API from its
        // own page: a present, foreign Origin is refused. (Host matches the
        // prefix, so this reaches the gate — unlike a foreign Host.)
        using var api = ApiHost.Start(FreeTcpPort());
        var (status, _) = await Send(api.Port, bearer: api.Token, origin: "http://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, status);
    }

    [Fact]
    public async Task OwnOrigin_Allowed()
    {
        using var api = ApiHost.Start(FreeTcpPort());
        var (status, _) = await Send(api.Port, bearer: api.Token, origin: $"http://127.0.0.1:{api.Port}");
        Assert.Equal(HttpStatusCode.NotFound, status);
    }

    [Fact]
    public async Task ForeignHost_RefusedByTheListener_BeforeTheGate()
    {
        // DNS-rebind (Host: attacker.example) is refused by managed HttpListener's
        // Host-prefix match — a 404 from the listener, before the gate runs. Both
        // sends carry NO token, which is what isolates the layer: a request that
        // REACHED the gate with no token would be 401, so the foreign-Host 404
        // (vs the valid-Host 401) proves the listener refused it FIRST. (The
        // gate's own Host→403 fallback is proven in ApiAuthGateTests.)
        using var api = ApiHost.Start(FreeTcpPort());
        var (foreignHost, _) = await Send(api.Port, bearer: null, host: "attacker.example");
        var (validNoToken, _) = await Send(api.Port, bearer: null);
        Assert.Equal(HttpStatusCode.NotFound, foreignHost);       // listener refused (pre-gate)
        Assert.Equal(HttpStatusCode.Unauthorized, validNoToken);  // a reached gate → 401
        Assert.NotEqual(foreignHost, validNoToken);
    }

    [Fact]
    public async Task GateShadowsEveryRoute_NotJustStatus()
    {
        // The gate wraps the router, so an unauthenticated request to ANY path
        // is 401 — never a 404 that would leak which routes exist.
        using var api = ApiHost.Start(FreeTcpPort());
        foreach (var path in new[] { "/", "/api/v1/policy", "/api/v1/anything", "/favicon.ico" })
        {
            var (status, _) = await Send(api.Port, bearer: null, path: path);
            Assert.Equal(HttpStatusCode.Unauthorized, status);
        }
    }
}
