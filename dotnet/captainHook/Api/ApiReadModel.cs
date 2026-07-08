using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CaptainHook.Core;

namespace CaptainHook.Api;

// The read endpoints' projection (ADR-0007 decision 3): renders GET /status,
// /policy, /harnesses, /handlers from the SAME live objects the dispatch path
// runs — the policy resolver, the harness registry, the dispatcher's workers —
// so the API view is structurally incapable of drifting from daemon behavior.
// DaemonHost builds one and hands it to ApiHost; tests build one over the same
// Core types (no mocks). All reads are lock-free off thread-safe sources
// (ReloadingPolicy/-Registry stat-gate internally; ServeStats is Interlocked;
// the Worker accessors are Volatile) — the API never mutates the serve loop.
public sealed class ApiReadModel
{
    private readonly string _version;
    private readonly ServeStats _stats;
    private readonly Dispatcher _dispatcher;
    private readonly ReloadingHarnessRegistry _harnesses;
    private readonly ReloadingPolicy _policy;
    private readonly string? _policyPath;
    private readonly Func<long> _clock;
    private readonly long _startTick;

    public ApiReadModel(
        string version, ServeStats stats, Dispatcher dispatcher,
        ReloadingHarnessRegistry harnesses, ReloadingPolicy policy, string? policyPath,
        Func<long> clock, long startTick)
    {
        _version = version;
        _stats = stats;
        _dispatcher = dispatcher;
        _harnesses = harnesses;
        _policy = policy;
        _policyPath = policyPath;
        _clock = clock;
        _startTick = startTick;
    }

    /// `openStreams` arrives from the ApiHost serving the request (the host owns
    /// the SSE counter; the read model is built before the host exists).
    public StatusDto Status(int openStreams = 0) => new(
        Version: _version,
        Pid: Environment.ProcessId,
        UptimeMs: _clock() - _startTick,
        Active: _stats.Active,
        Served: _stats.Served,
        BackgroundPending: _dispatcher.BackgroundPending,
        OpenStreams: openStreams);

    public HandlersDto Handlers() => new(
        _dispatcher.Snapshot()
            .Select(h => new HandlerDto(
                Event: h.EventType,
                Name: h.Name,
                FailMode: h.OnFailure == CaptainHook.Core.FailMode.Closed ? "closed" : "open",
                Generation: h.Generation,
                Dead: h.Dead))
            .ToList());

    public HarnessesDto Harnesses()
    {
        var reg = _harnesses.Current;
        var list = reg.Known
            .Select(name =>
            {
                var s = reg.Get(name);
                return new HarnessDto(
                    Name: s.Name,
                    ResponseAdapter: s.ResponseAdapter,
                    Request: new HarnessRequestDto(
                        s.Request.EventNameField, s.Request.SessionIdField, s.Request.CwdField),
                    Events: s.Events,
                    Install: s.Install.ValueKind == JsonValueKind.Undefined ? null : s.Install);
            })
            .ToList();
        return new HarnessesDto(list);
    }

    public PolicyDto Policy()
    {
        // The resolved tri-state comes from the SAME stat-gated resolver the
        // dispatch path reads; raw+etag are a separate best-effort read of the
        // file so a PUT (Phase 6) has an If-Match token. The two reads can race
        // an edit — benign for a GET, the next one converges.
        var resolution = _policy.Current;
        var (state, error, doc) = resolution switch
        {
            PolicyResolution.Loaded l => ("loaded", (string?)null, Doc(l.Policy)),
            PolicyResolution.Malformed m => ("malformed", m.Error, (PolicyDocDto?)null),
            _ => ("absent", (string?)null, (PolicyDocDto?)null),
        };

        string? raw = null, etag = null;
        if (_policyPath is not null)
        {
            try
            {
                if (File.Exists(_policyPath))
                {
                    raw = File.ReadAllText(_policyPath);
                    etag = Etag(raw);
                }
            }
            catch { /* unreadable: raw/etag stay null; `state` already reflects malformed */ }
        }

        return new PolicyDto(state, error, doc, raw, _policyPath, etag);
    }

    private static PolicyDocDto Doc(DispatchPolicy p) => new(
        Default: p.Default.ToString().ToLowerInvariant(),
        Rules: p.Rules
            .Select(r => new PolicyRuleDto(
                r.Event, r.Handler, r.Project, r.Session, r.Decision.ToString().ToLowerInvariant()))
            .ToList());

    /// A strong ETag over the raw file bytes — 128 bits of SHA-256, quoted per
    /// RFC 7232. put-policy-write's If-Match compares it to guard a blind
    /// overwrite of a concurrent hand-edit.
    internal static string Etag(string raw) =>
        "\"" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..32] + "\"";
}
