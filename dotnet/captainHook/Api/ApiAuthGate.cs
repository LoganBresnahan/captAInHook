using System.Security.Cryptography;
using System.Text;

namespace CaptainHook.Api;

// auth-token-origin (ADR-0007 decision 6): the pure authorization decision for
// the management API, factored out of HttpListener so the security logic is
// unit-tested DIRECTLY (ApiAuthGateTests) — not through the listener's Host-
// prefix matching, which already 404s a foreign Host before any user code runs
// (doc/platform.md § Loopback TCP). That listener behavior is the first layer of
// rebind defense; this gate is the PORTABLE, intentional control that does not
// lean on it, plus the CSRF (Origin) and authentication (token) checks the
// listener does nothing about.
//
// Loopback binding alone protects against neither another local user (anyone on
// the box dials 127.0.0.1) nor a browser (a malicious page can CSRF or DNS-
// rebind into localhost). So every request clears three checks, cheapest and
// most transport-forged first:
//   * Host — must be the exact loopback authority we bound. A rebind page
//     arrives naming its own domain; reject (403) before anything else.
//   * Origin — a browser attaches one; present ⇒ must be ours (403), absent ⇒
//     allowed so curl and non-browser clients (never CSRF vectors) work.
//   * Bearer token — required on every request, constant-time compared so a
//     near-miss guess leaks no timing signal; missing/mismatch ⇒ 401.
// We bind and answer 127.0.0.1 ONLY — not "localhost", which the listener would
// not dispatch anyway (its prefix is the IPv4 literal); a GUI is served from
// the same 127.0.0.1 origin. Reads are guarded too, not just writes: prompts
// and tool calls transit the trail the endpoints expose (decision 6).
internal sealed class ApiAuthGate
{
    private readonly byte[] _tokenUtf8;   // constant-time compare target
    private readonly string _authority;   // the one Host we answer: 127.0.0.1:port
    private readonly string _origin;      // the one browser Origin we accept

    internal ApiAuthGate(int port, string token)
    {
        _tokenUtf8 = Encoding.UTF8.GetBytes(token);
        _authority = $"127.0.0.1:{port}";
        _origin = $"http://127.0.0.1:{port}";
    }

    /// null ⇒ authorized. Otherwise the HTTP status + a short machine error.
    internal (int Status, string Error)? Evaluate(string? host, string? origin, string? authorization)
    {
        if (EvaluateShell(host, origin) is { } rej) return rej;
        if (!BearerMatches(authorization))
            return (401, "unauthorized");
        return null;
    }

    /// The bearer-exempt HALF of the gate — Host (rebind) + Origin (CSRF) only —
    /// for the `/ui` static shell (ADR-0008 decision 2): a top-level browser
    /// navigation cannot carry an Authorization header, so the shell must be
    /// reachable without the token. The exemption is BEARER-ONLY by construction:
    /// this method is the same two transport checks Evaluate runs first, so /ui
    /// and /api/v1/* can never drift on Host/Origin policy. Everything data-
    /// bearing stays behind Evaluate.
    internal (int Status, string Error)? EvaluateShell(string? host, string? origin)
    {
        if (!string.Equals(host, _authority, StringComparison.OrdinalIgnoreCase))
            return (403, "bad_host");
        if (origin is not null && !string.Equals(origin, _origin, StringComparison.OrdinalIgnoreCase))
            return (403, "bad_origin");
        return null;
    }

    // "Bearer <token>", scheme case-insensitive; the token compared in constant
    // time. FixedTimeEquals fast-fails on a length mismatch, but the token
    // length (64 hex) is public — only its CONTENT is secret, and that is what
    // is compared without an early-out.
    //
    // SECURITY: this comparison MUST stay constant-time — never replace
    // FixedTimeEquals with ==, SequenceEqual, or string equality. No test can
    // assert timing, so this invariant lives in review; a naive compare leaks
    // the token byte-by-byte to a co-located timing attacker and would pass the
    // whole suite green.
    private bool BearerMatches(string? authHeader)
    {
        const string scheme = "Bearer ";
        if (authHeader is null || !authHeader.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            return false;
        var presented = Encoding.UTF8.GetBytes(authHeader.AsSpan(scheme.Length).Trim().ToString());
        return CryptographicOperations.FixedTimeEquals(presented, _tokenUtf8);
    }
}
