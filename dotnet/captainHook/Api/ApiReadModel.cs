using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CaptainHook.Core;
using CaptainHook.Mail;

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
    private readonly string? _handlersPath;
    private readonly Func<long> _clock;
    private readonly long _startTick;
    private readonly MailReadPort? _mail;          // null => GET /mail 404s (no bus to observe)
    private readonly SessionPresence? _presence;   // null => presence is cursor-files only

    public ApiReadModel(
        string version, ServeStats stats, Dispatcher dispatcher,
        ReloadingHarnessRegistry harnesses, ReloadingPolicy policy, string? policyPath,
        Func<long> clock, long startTick, string? handlersPath = null,
        MailReadPort? mail = null, SessionPresence? presence = null)
    {
        _mail = mail;
        _presence = presence;
        _version = version;
        _stats = stats;
        _dispatcher = dispatcher;
        _harnesses = harnesses;
        _policy = policy;
        _policyPath = policyPath;
        _handlersPath = handlersPath;
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
        OpenStreams: openStreams,
        ShimPath: ResolveShimPath());

    // The co-located native shim, if this deploy dir carries one — the wiring
    // hint's {captainShim} substitution (ADR-0011 d5). Resolved at read time
    // from the SAME BaseDirectory the daemon's identity is keyed to, so the
    // hint always names the executable a hand-paste would actually run.
    private static string? ResolveShimPath()
    {
        var p = System.IO.Path.Combine(AppContext.BaseDirectory, "captainShim");
        return File.Exists(p) ? p : null;
    }

    public HandlersDto Handlers()
    {
        // Live registrations (coded + exec), each with its resident child state
        // (null for oneshot/coded) — the SAME workers the dispatch path runs.
        var registered = _dispatcher.Snapshot()
            .Select(h => new HandlerDto(
                Event: h.EventType,
                Name: h.Name,
                FailMode: h.OnFailure == CaptainHook.Core.FailMode.Closed ? "closed" : "open",
                Generation: h.Generation,
                Dead: h.Dead,
                ChildState: h.ChildState,
                ChildPid: h.ChildPid))
            .ToList();

        // Expected-vs-registered (ADR-0010 d8): resolve the SAME handlers.json
        // the daemon registers from (null path in tests => absent), then JOIN
        // each declared entry against the live set by name. A valid entry that
        // (for any reason) has no live worker reads Registered:false — honest,
        // never assumed true; a warn-and-skip entry reads Registered:false with
        // its violations and NEVER appears as a live row (the N2 caution).
        var liveNames = new HashSet<string>(registered.Select(h => h.Name), StringComparer.Ordinal);
        var resolution = _handlersPath is null
            ? new ExecHandlersResolution.Absent()
            : ExecHandlersFile.Resolve(_handlersPath);
        var (source, error, expected) = resolution switch
        {
            ExecHandlersResolution.Loaded(var entries, var skipped) => (
                "loaded", (string?)null,
                entries.Select(e => new ExpectedHandlerDto(
                        e.Name, e.Events,
                        e.Mode.ToString().ToLowerInvariant(),
                        e.OnFailure == CaptainHook.Core.FailMode.Closed ? "closed" : "open",
                        Registered: liveNames.Contains(e.Name), SkipReason: null))
                    .Concat(skipped.Select(s => new ExpectedHandlerDto(
                        s.Label, [], null, null, Registered: false, SkipReason: string.Join("; ", s.Violations))))
                    .ToList()),
            ExecHandlersResolution.Malformed(var m) =>
                ("malformed", (string?)m, new List<ExpectedHandlerDto>()),
            _ => ("absent", (string?)null, new List<ExpectedHandlerDto>()),
        };

        // raw+etag mirror Policy(): a separate best-effort read so PUT /handlers
        // has an If-Match token (ADR-0011 d3). The two reads can race an edit —
        // benign for a GET, the next one converges.
        string? raw = null, etag = null;
        if (_handlersPath is not null)
        {
            try
            {
                if (File.Exists(_handlersPath))
                {
                    raw = File.ReadAllText(_handlersPath);
                    etag = Etag(raw);
                }
            }
            catch { /* unreadable: raw/etag stay null; `source` already reflects malformed */ }
        }

        return new HandlersDto(registered, source, error, _handlersPath, expected, raw, etag);
    }

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

    /// GET /mail (ADR-0016 d14): one read-only snapshot of the bus — chain
    /// status, the ledger from `since`, every cursor's pending view, and the
    /// inferred presence behind them. Null when no mail port was wired (the
    /// route then 404s, exactly like the other capability-gated endpoints).
    ///
    /// WHAT THIS METHOD CANNOT DO is the point: `_mail` is a `MailReadPort`,
    /// which has no Append and no Advance, so no shape of bug or feature here
    /// can deliver mail. Reading a mailbox changes nothing on disk — every
    /// call below (`Read`, `VerifyChain`, `HeadHash`, `Cursors`, `Pending`) is
    /// a pure read, which is also what makes it safe to serve on every poll.
    public MailDto? Mail(long since)
    {
        if (_mail is null) return null;

        var lines = _mail.Read();
        // The frontier stops BEFORE an unterminated tail — MailCursors.Pending's
        // rule, applied identically here so the picture and the delivery agree
        // about where the store ends. The torn line itself is still REPORTED
        // (an operator watching an interrupted write should see it), just never
        // counted as consumable.
        var torn = lines.Count > 0 && !lines[^1].Terminated;
        var complete = torn ? lines.Take(lines.Count - 1).ToList() : lines.ToList();
        var frontier = torn
            ? lines[^1].Offset
            : complete.Count > 0 ? complete[^1].Offset + complete[^1].Bytes + 1 : 0;
        var bytes = lines.Count > 0
            ? lines[^1].Offset + lines[^1].Bytes + (lines[^1].Terminated ? 1 : 0)
            : 0;

        // Alignment, on the cursor's own rule: a legitimate resume offset rests
        // on a line boundary or at the frontier. Anything else means the bytes
        // the client last read are gone — it must re-snapshot from 0 rather
        // than splice a stale prefix onto a fresh tail.
        var aligned = since == 0 || since == frontier || lines.Any(l => l.Offset == since);

        var faults = _mail.VerifyChain();
        var chain = new MailChainDto(
            Ok: faults.Count == 0,
            Head: _mail.HeadHash(),
            Gen: _mail.Gen,
            Lines: lines.Count,
            Bytes: bytes,
            DirMode: Mode(_mail.Dir),
            FileMode: Mode(_mail.FilePath),
            Faults: faults
                .Select(f => new MailChainFaultDto(f.Offset, Camel(f.Kind.ToString()), f.Detail))
                .ToList());

        var lineDtos = lines
            .Where(l => l.Offset >= since)
            .Select(l => new MailLineDto(
                l.Offset, l.Bytes, l.Terminated, l.Hash,
                l.Envelope is null ? null : Envelope(l.Envelope),
                l.Errors))
            .ToList();

        var cursors = _mail.Cursors()
            .Select(id => Cursor(_mail.Pending(id.Role, id.Session)))
            .ToList();

        return new MailDto(_mail.Dir, chain, since, aligned, frontier, lineDtos, cursors, Presence(cursors));
    }

    private static MailCursorDto Cursor(MailPendingView v) => new(
        v.Role, v.Session, v.Gen, v.Head, v.Frontier, v.Deliveries, v.LastDeliveredId,
        v.Reanchored, v.ReanchorReason,
        v.Pending.Select(p => Pending(p, v.Deliveries)).ToList(),
        v.Expired.Select(p => Pending(p, v.Deliveries)).ToList(),
        v.SkippedMalformed);

    // `Opportunities` is the cursor's own arithmetic (deliveries − seenAt + 1),
    // computed HERE rather than in the client so the TTL countdown a viewer
    // sees is the same number MailCursors compares against ttlDeliveries. Fresh
    // mail (never passed over) has consumed nothing: 0.
    private static MailPendingDto Pending(PendingMail p, long deliveries) => new(
        p.Offset, p.Envelope.Id, Camel(p.Envelope.Priority.ToString()), p.Envelope.TtlDeliveries,
        p.SeenAt, p.SeenAt is { } seen ? deliveries - seen + 1 : 0);

    // Presence = cursor files ∪ recently-dispatched sessions (ADR-0016 d14).
    // Both halves are INFERENCE: the first says "this session was delivered to
    // once", the second "this daemon served a hook of its N ms ago". Neither is
    // a liveness claim, and a session holding no cursor and sending no hooks
    // simply is not here — which is honest, because nothing on this machine
    // knows otherwise.
    private IReadOnlyList<MailPresenceDto> Presence(IReadOnlyList<MailCursorDto> cursors)
    {
        var roles = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var c in cursors)
        {
            if (c.Session is null) continue;   // a sessionless reader has no presence to infer
            if (!roles.TryGetValue(c.Session, out var list)) roles[c.Session] = list = [];
            if (!list.Contains(c.Role)) list.Add(c.Role);
        }

        var ages = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (session, ageMs) in _presence?.Recent() ?? [])
            ages[session] = ageMs;

        return roles.Keys.Union(ages.Keys, StringComparer.Ordinal)
            .Select(s => new MailPresenceDto(
                s,
                roles.TryGetValue(s, out var r) ? r : [],
                ages.TryGetValue(s, out var a) ? a : null))
            // Freshest first, then the quiet cursor-only sessions by name — a
            // stable order so a re-render never reshuffles the lanes.
            .OrderBy(p => p.LastDispatchAgeMs ?? long.MaxValue)
            .ThenBy(p => p.Session, StringComparer.Ordinal)
            .ToList();
    }

    private static MailEnvelopeDto Envelope(MailEnvelope e) => new(
        e.Id, e.Ts, new MailSenderDto(e.From.Agent, e.From.Harness, e.From.Session),
        e.To, Camel(e.Kind.ToString()), e.Topic, Camel(e.Priority.ToString()),
        e.InReplyTo, e.TtlDeliveries, e.Body, e.Prev);

    /// The unix mode as three octal digits ("600"), or null when the path is
    /// absent or its mode unreadable. Owner-only is a d13 guarantee about
    /// these files; showing it beats asserting it.
    private static string? Mode(string path)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return null;
            return Convert.ToString((int)File.GetUnixFileMode(path), 8).PadLeft(3, '0');
        }
        catch (Exception) { return null; }   // unsupported platform, permissions, a race
    }

    /// A closed set's member name in the wire's camelCase — the enum spelling
    /// every other DTO here uses ("prevMismatch", "urgent").
    private static string Camel(string name) => char.ToLowerInvariant(name[0]) + name[1..];

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
