using System.Text.Json;
using CaptainHook.Actors;

namespace CaptainHook.Core;

// Roadmap item 14 / ADR-0006 — the dispatch-policy MODEL and its STRICT parser.
// captAInHook's own front door: a user-editable ~/.captainHook/dispatch.json
// decides whether an arriving hook gets WORKED (dispatched to handlers) or
// short-circuited to a valid Noop. The hook is ALWAYS answered — policy only
// chooses between "worked" and "valid Noop on the wire" (ADR-0006 decision 3).
//
// This file holds the policy core: PARSE (JsonElement -> policy or errors),
// MATCH (Evaluate: a dispatch -> work-or-not + excluded handlers), the
// injectable path resolver, the file TRI-STATE (PolicyResolution.Resolve:
// absent | malformed | loaded — ADR-0006 decision 4), and the daemon's
// per-dispatch stat-gate (ReloadingPolicy, at the bottom). Everything is pure
// except Resolve (the I/O boundary — reads + classifies the file) and
// ReloadingPolicy (the (mtime,size) hot-reload wrapper over it). The
// classification is the adversarial-verify surface: absent-as-malformed would
// quiet every zero-config user; malformed-as-absent would silently grant
// execution. Both dispatch sites are wired (phase 5); the daemon reloads via
// ReloadingPolicy, the collapsed path resolves once.
//
// House precedent, followed deliberately: HarnessSpec.TryParse's strict walk
// (collect every violation, all-or-nothing accept, never throw on bad DATA)
// and HarnessRegistry.ResolveDir's injectable-path idiom. The one tightening
// vs HarnessSpec: unknown fields are MALFORMED here, not ignored — ADR-0006
// decision 1 says the policy dialect never guesses.

/// A front-door decision. Deliberately SMALLER than Verdict (no Ask): an "ask"
/// makes no sense at a door nobody is watching, so the parser rejects it
/// (ADR-0006 decision 1).
public enum PolicyDecision { Allow, Deny }

/// One ordered rule. Criteria (event/handler/project/session) AND together;
/// at least one must be present — a criteria-less rule is malformed, because
/// `default` is the sanctioned way to match everything (a bare rule is almost
/// always a typo). `event` is a canonical PascalCase name, `handler` a
/// registered handler name, `project` a PATH-PREFIX on the event's cwd,
/// `session` an exact id. The matcher (phase 2) reads these; this slice only
/// parses and validates their shape.
public sealed record PolicyRule(
    string? Event, string? Handler, string? Project, string? Session,
    PolicyDecision Decision);

/// The result of evaluating a policy against one dispatch. Work=false is an
/// event-level deny — the whole dispatch short-circuits to a valid Noop, no
/// worker asked (ADR-0006 decision 2). Work=true means proceed, dropping
/// ExcludedHandlers (the handler-level denies) from the fan-out — the set hands
/// straight to Dispatcher.DispatchAsync's excludedHandlers parameter.
public sealed record PolicyOutcome(bool Work, IReadOnlySet<string> ExcludedHandlers);

/// A parsed policy: the baseline decision for unmatched dispatches, plus the
/// ordered rules (first match wins — the matcher enforces order).
public sealed record DispatchPolicy(
    PolicyDecision Default, IReadOnlyList<PolicyRule> Rules)
{
    private static readonly IReadOnlySet<string> KnownTopLevel =
        new HashSet<string> { "version", "default", "rules" };

    private static readonly IReadOnlySet<string> KnownRuleFields =
        new HashSet<string> { "event", "handler", "project", "session", "decision" };

    /// Strict parse: returns the policy, or null plus one error per violation.
    /// NEVER throws on bad DATA — a JsonException from malformed *bytes* is the
    /// caller's to catch (same split as HarnessSpec.TryParse). Every strictness
    /// rule here is ADR-0006 decision 1: unknown fields, an unknown or missing
    /// version, unknown decision strings, and criteria-less rules are all
    /// MALFORMED, reported together in one pass (moby-style).
    public static DispatchPolicy? TryParse(JsonElement root, out IReadOnlyList<string> errors)
    {
        var errs = new List<string>();
        errors = errs;

        if (root.ValueKind != JsonValueKind.Object)
        {
            errs.Add("dispatch policy must be a JSON object");
            return null;
        }

        // Unknown OR duplicate top-level field => malformed. Strict never-guess:
        // a repeated known field (e.g. two `default`s) is ambiguous and
        // System.Text.Json would silently keep one — reject instead.
        var seenTop = new HashSet<string>(StringComparer.Ordinal);
        foreach (var prop in root.EnumerateObject())
        {
            if (!seenTop.Add(prop.Name))
                errs.Add($"duplicate field '{prop.Name}'");
            else if (!KnownTopLevel.Contains(prop.Name))
                errs.Add($"unknown field '{prop.Name}' (known: {string.Join(", ", KnownTopLevel)})");
        }

        // version: required, must be the number 1.
        if (!root.TryGetProperty("version", out var ver))
            errs.Add("'version' is required and must be the number 1");
        else if (ver.ValueKind != JsonValueKind.Number || !ver.TryGetInt32(out var v) || v != 1)
            errs.Add($"'version' must be 1 (got {RawText(ver)})");

        // default: optional; allow|deny only; missing => allow.
        var dflt = PolicyDecision.Allow;
        if (root.TryGetProperty("default", out var def) && !TryDecision(def, out dflt))
            errs.Add($"'default' must be \"allow\" or \"deny\" (got {RawText(def)})");

        // rules: optional array; missing => none. Each element strictly parsed.
        var rules = new List<PolicyRule>();
        if (root.TryGetProperty("rules", out var rs))
        {
            if (rs.ValueKind != JsonValueKind.Array)
                errs.Add("'rules' must be an array");
            else
            {
                var i = 0;
                foreach (var rule in rs.EnumerateArray())
                    ParseRule(rule, i++, rules, errs);
            }
        }

        return errs.Count > 0 ? null : new DispatchPolicy(dflt, rules);
    }

    private static void ParseRule(JsonElement rule, int idx, List<PolicyRule> into, List<string> errs)
    {
        if (rule.ValueKind != JsonValueKind.Object)
        {
            errs.Add($"rules[{idx}] must be a JSON object");
            return;
        }

        var before = errs.Count;

        // Unknown OR duplicate field inside a rule => malformed.
        var seenFields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var prop in rule.EnumerateObject())
        {
            if (!seenFields.Add(prop.Name))
                errs.Add($"rules[{idx}]: duplicate field '{prop.Name}'");
            else if (!KnownRuleFields.Contains(prop.Name))
                errs.Add($"rules[{idx}]: unknown field '{prop.Name}' (known: {string.Join(", ", KnownRuleFields)})");
        }

        // Canonicalize the event criterion to PascalCase, exactly as the ingest
        // path canonicalizes incoming event names (Harness.Canon). Without this a
        // rule written "user-prompt-submit" — the project's first-class kebab
        // spelling — parses fine yet NEVER matches the canonical
        // "UserPromptSubmit", silently turning a deny into a GRANT. (Caught by
        // the ADR-0006 phase-4 adversarial verify; the match is also
        // case-insensitive for the residual casing gap.)
        var ev = Crit(rule, "event", idx, errs);
        if (ev is not null) ev = Harness.Canon(ev);
        var handler = Crit(rule, "handler", idx, errs);
        var project = Crit(rule, "project", idx, errs);
        var session = Crit(rule, "session", idx, errs);

        // At least one criterion — a criteria-less rule matches everything and
        // is almost always a typo (a forgotten event/handler name); `default`
        // is how you set the baseline.
        if (ev is null && handler is null && project is null && session is null)
            errs.Add($"rules[{idx}]: a rule must name at least one of event/handler/project/session");

        // decision: required; allow|deny only.
        var decision = PolicyDecision.Allow;
        if (!rule.TryGetProperty("decision", out var dec))
            errs.Add($"rules[{idx}]: 'decision' is required and must be \"allow\" or \"deny\"");
        else if (!TryDecision(dec, out decision))
            errs.Add($"rules[{idx}]: 'decision' must be \"allow\" or \"deny\" (got {RawText(dec)})");

        // Materialize only a fully clean rule — a half-parsed one would poison
        // the matcher. (Moot when errs is non-empty, since TryParse then
        // discards the whole list; this keeps `into` coherent regardless.)
        if (errs.Count == before)
            into.Add(new PolicyRule(ev, handler, project, session, decision));
    }

    /// A criterion field: absent => null (fine); present must be a non-empty
    /// string, else an error. An empty/whitespace criterion matches nothing
    /// meaningful, so strict-never-guess rejects it.
    private static string? Crit(JsonElement rule, string field, int idx, List<string> errs)
    {
        if (!rule.TryGetProperty(field, out var v)) return null;
        if (v.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(v.GetString()))
        {
            errs.Add($"rules[{idx}].{field} must be a non-empty string");
            return null;
        }
        return v.GetString();
    }

    private static bool TryDecision(JsonElement e, out PolicyDecision d)
    {
        d = PolicyDecision.Allow;
        if (e.ValueKind != JsonValueKind.String) return false;
        switch (e.GetString())
        {
            case "allow": d = PolicyDecision.Allow; return true;
            case "deny": d = PolicyDecision.Deny; return true;
            default: return false;
        }
    }

    /// Quote strings, raw-render everything else — so an error message reads
    /// `got "maybe"` for a bad string but `got 2` / `got true` for a bad type.
    private static string RawText(JsonElement e) =>
        e.ValueKind == JsonValueKind.String ? $"\"{e.GetString()}\"" : e.GetRawText();

    private static readonly IReadOnlySet<string> NoExclusions = new HashSet<string>();

    /// First-match-wins evaluation (ADR-0006 decisions 1-2). One rule list, two
    /// questions. Handler-LESS rules answer "is the whole dispatch WORKED?" —
    /// the first matching rule wins, else Default; a deny there short-circuits
    /// and no handler question is asked. Handler-NAMED rules answer, per
    /// handler, "is THIS handler excluded?" — the first rule naming that handler
    /// and matching wins, so an earlier allow SHIELDS it from a later deny.
    /// Criteria within a rule AND together; a handler-rule's `handler` field is
    /// the SUBJECT it decides, not a criterion compared against the dispatch.
    public PolicyOutcome Evaluate(string eventType, string? cwd, string? sessionId)
    {
        // Event level: the first handler-less rule that matches this dispatch.
        var decision = Default;
        foreach (var rule in Rules)
            if (rule.Handler is null && Matches(rule, eventType, cwd, sessionId))
            {
                decision = rule.Decision;
                break;
            }
        if (decision == PolicyDecision.Deny)
            return new PolicyOutcome(Work: false, NoExclusions);

        // Handler level: per named handler, the first matching handler-rule
        // decides — allow shields, deny excludes. `decided` enforces
        // first-match-wins per handler; `excluded` collects only the denies.
        HashSet<string>? excluded = null;
        HashSet<string>? decided = null;
        foreach (var rule in Rules)
        {
            if (rule.Handler is null || !Matches(rule, eventType, cwd, sessionId)) continue;
            decided ??= new HashSet<string>();
            if (!decided.Add(rule.Handler)) continue;   // a later rule for a decided handler is dead
            if (rule.Decision == PolicyDecision.Deny)
                (excluded ??= new HashSet<string>()).Add(rule.Handler);
        }
        return new PolicyOutcome(Work: true, excluded ?? NoExclusions);
    }

    /// Event / project / session criteria, AND'd (ADR-0006 decision 1). The
    /// handler criterion is deliberately NOT checked here — the caller routes on
    /// rule.Handler's presence (absent => event-level, present => handler-level).
    private static bool Matches(PolicyRule rule, string eventType, string? cwd, string? sessionId)
    {
        // Event compared case-insensitively (rule.Event is already Canon'd at
        // parse) so a casing slip can't silently drop the rule; session stays an
        // exact id match and project is path-prefix.
        if (rule.Event is not null && !string.Equals(rule.Event, eventType, StringComparison.OrdinalIgnoreCase)) return false;
        if (rule.Session is not null && rule.Session != sessionId) return false;
        if (rule.Project is not null && !ProjectContains(rule.Project, cwd)) return false;
        return true;
    }

    /// Path-prefix containment (ADR-0006 decision 1: "a project contains its
    /// subdirectories"). cwd matches when it EQUALS project or sits strictly
    /// beneath it — a literal string prefix on a separator boundary, no
    /// realpath / no symlink resolution. Trailing separators are trimmed so
    /// "/repo" and "/repo/" behave identically, and the separator-boundary
    /// check is precisely what stops "/repo" from matching "/repo2". Ordinal,
    /// case-sensitive: POSIX cwd is the platform (doc/platform.md).
    private static bool ProjectContains(string project, string? cwd)
    {
        if (cwd is null) return false;   // a project-scoped rule can't match a cwd-less event
        var sep = Path.DirectorySeparatorChar;
        var p = project.TrimEnd(sep);
        var c = cwd.TrimEnd(sep);
        return c == p || c.StartsWith(p + sep, StringComparison.Ordinal);
    }

    /// The policy file this process reads: explicit, else the env var, else
    /// ~/.captainHook/dispatch.json. Mirrors HarnessRegistry.ResolveDir so the
    /// reload watcher (phase 6) and the loader always agree on the path.
    public static string ResolvePath(string? overridePath = null) =>
        overridePath
        ?? Environment.GetEnvironmentVariable("CAPTAINHOOK_DISPATCH_FILE")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        ".captainHook", "dispatch.json");
}

/// The tri-state of resolving the dispatch-policy file (ADR-0006 decision 4).
/// The classification IS the contract, and the two wrong directions are both
/// costly, so each case is deliberate:
///   * ABSENT — no file — is today's zero-config behavior: allow everything,
///     feel nothing. This is the ONLY case that permits work by default.
///   * MALFORMED — a file that EXISTS but cannot be read or parsed into a valid
///     policy — Noops every hook LOUDLY: `Error` names the fault so each
///     dispatch logs `policy.malformed` and the trace says so. A file present
///     is intent to configure; an unreadable one must never silently grant
///     execution, and (hooks being enhancement) it fails toward quiet, not
///     toward denying an authz boundary. NO keep-last-good (ADR-0006 rejects
///     it): stateless everywhere else, stale-and-silent nowhere.
///   * LOADED — a valid file — carries the parsed policy to evaluate.
/// Resolve is the one I/O boundary; Evaluate maps each case to a dispatch
/// decision so the two wire sites (phase 5) consume it uniformly and cannot
/// drift.
public abstract record PolicyResolution
{
    private PolicyResolution() { }   // closed set: exactly the three below

    public sealed record Absent : PolicyResolution;
    public sealed record Malformed(string Error) : PolicyResolution;
    public sealed record Loaded(DispatchPolicy Policy) : PolicyResolution;

    private static readonly IReadOnlySet<string> None = new HashSet<string>();

    /// Read and classify the REGULAR file at `path`. Never throws for one — every
    /// failure mode (missing, a directory, unreadable, non-JSON, schema-invalid)
    /// lands in a case, because a throw on the hot path would be worse than any
    /// classification. Two guarded subtleties, both chosen so the AMBIGUOUS cases
    /// fail toward quiet (Malformed) rather than toward a silent grant (Absent):
    ///   * a DIRECTORY at the path reads as Malformed, not Absent (File.Exists is
    ///     false for a directory, which would otherwise silently allow);
    ///   * a present-but-unreadable file — including a DANGLING SYMLINK (File.Exists
    ///     is true, the read throws) and the tiny window where an atomic save
    ///     replaces the file mid-resolve — is Malformed, not Absent. A file the
    ///     user pointed at may have carried a restriction (e.g. a policy on an
    ///     unmounted path); treating "can't read it" as "no policy" would silently
    ///     grant what it meant to deny, so we deny-loud and let the next hook
    ///     self-heal. (Both directions weighed in the ADR-0006 phase-4 adversarial
    ///     verify.)
    /// Caveat: a deliberately-created FIFO/socket at the path could block the read
    /// (a pathological misconfiguration, out of scope — not a regular file).
    public static PolicyResolution Resolve(string path)
    {
        if (Directory.Exists(path))
            return new Malformed($"'{path}' is a directory, not a policy file");
        if (!File.Exists(path))
            return new Absent();

        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception ex)   // permissions, races, device I/O — exists but unreadable
        {
            return new Malformed($"cannot read '{path}': {ex.Message}");
        }

        // An empty or whitespace-only file is NOT absent — the file is present
        // (intent to configure) but has no parseable content. JsonDocument.Parse
        // throws on both, so both land in Malformed below.
        try
        {
            using var doc = JsonDocument.Parse(text);
            var policy = DispatchPolicy.TryParse(doc.RootElement, out var errors);
            return policy is null
                ? new Malformed($"'{path}' is not a valid dispatch policy: {string.Join("; ", errors)}")
                : new Loaded(policy);
        }
        catch (JsonException ex)
        {
            return new Malformed($"'{path}' is not valid JSON: {ex.Message}");
        }
    }

    /// The dispatch decision this resolution yields — the uniform surface the
    /// wire sites consume. Absent allows everything; Malformed denies everything
    /// (the loudness is the caller's, via a `policy.malformed` log keyed off the
    /// Malformed case); Loaded delegates to the parsed policy's matcher.
    public PolicyOutcome Evaluate(string eventType, string? cwd, string? sessionId) => this switch
    {
        Loaded l => l.Policy.Evaluate(eventType, cwd, sessionId),
        Malformed => new PolicyOutcome(Work: false, None),
        _ => new PolicyOutcome(Work: true, None),   // Absent
    };
}

/// The daemon's policy view (ADR-0006 decision 6, phase 6): resolve once at
/// start, then per dispatch take a cheap (mtime,size) stamp of the file and
/// re-resolve ONLY when it moves — an edit is effective next hook without a
/// re-parse per hook. A failed reload is NOT special-cased: Resolve never throws
/// (it returns Malformed), and the swap is UNCONDITIONAL — so a broken edit
/// POISONS (every hook denied, loudly) AND advances the stamp: no keep-last-good
/// (ADR-0006 rejects it, the same reason ADR-0005 does), no re-parse storm.
/// Stamp comparison is EQUALITY on the wall-clock mtime (change detection, like
/// content identity), never interval math — the monotonic rule is untouched.
/// Mirrors ReloadingHarnessRegistry; the collapsed path is single-shot and just
/// resolves once, so only the long-lived daemon needs this.
public sealed class ReloadingPolicy
{
    private readonly string? _path;
    private PolicyResolution _current;
    private string _stamp;

    public ReloadingPolicy(string? policyPath)
    {
        _path = policyPath;
        _current = Load(policyPath);
        _stamp = Stamp(policyPath);
    }

    private static PolicyResolution Load(string? path) =>
        path is null ? new PolicyResolution.Absent() : PolicyResolution.Resolve(path);

    private static string Stamp(string? path)
    {
        if (path is null) return "<null>";
        // Mirror Resolve's precedence: a directory is Malformed, not Absent, and
        // FileInfo.Exists is FALSE for one — so without this a dir appearing where
        // the file belongs would stamp identically to "absent" and the stat-gate
        // would never flip Absent(allow-all) → Malformed(deny-all): a silent grant.
        // (ADR-0006 phase-6 adversarial verify.)
        if (Directory.Exists(path)) return "<dir>";
        var fi = new FileInfo(path);
        return fi.Exists ? $"{fi.LastWriteTimeUtc.Ticks}|{fi.Length}" : "<absent>";
    }

    /// The resolution as of now. Benign race under concurrent dispatches: two
    /// callers seeing a fresh stamp both re-resolve and one wins the swap — both
    /// are valid resolutions of the same file. Write order (_current before
    /// _stamp) bounds the worst interleaving to one stale-by-one dispatch, then
    /// it converges.
    public PolicyResolution Current
    {
        get
        {
            var s = Stamp(_path);
            if (s != _stamp)
            {
                _current = Load(_path);
                _stamp = s;
                Log.Info("policy", "policy.reload", new LogFields
                {
                    Data = new Dictionary<string, object>
                    {
                        ["path"] = _path ?? "<none>",
                        ["state"] = _current.GetType().Name,   // Absent | Malformed | Loaded
                    },
                });
            }
            return _current;
        }
    }
}
