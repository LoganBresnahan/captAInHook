using System.Text.Json;

namespace CaptainHook.Core;

// Roadmap item 14 / ADR-0006 — the dispatch-policy MODEL and its STRICT parser.
// captAInHook's own front door: a user-editable ~/.captainHook/dispatch.json
// decides whether an arriving hook gets WORKED (dispatched to handlers) or
// short-circuited to a valid Noop. The hook is ALWAYS answered — policy only
// chooses between "worked" and "valid Noop on the wire" (ADR-0006 decision 3).
//
// This file is the pure core: PARSE (JsonElement -> policy or errors), MATCH
// (Evaluate: a dispatch -> work-or-not + excluded handlers), and the injectable
// path resolver. It has NO callers yet — dead until the evaluator wires it in
// (phases 3+). It deliberately stops short of the absent/malformed TRI-STATE
// (absent-allow-malformed-noop, phase 4), which wraps TryParse; keeping that
// out keeps the whole failure story in one place downstream.
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

        // Unknown top-level field => malformed (strict never-guess).
        foreach (var prop in root.EnumerateObject())
            if (!KnownTopLevel.Contains(prop.Name))
                errs.Add($"unknown field '{prop.Name}' (known: {string.Join(", ", KnownTopLevel)})");

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

        // Unknown field inside a rule => malformed.
        foreach (var prop in rule.EnumerateObject())
            if (!KnownRuleFields.Contains(prop.Name))
                errs.Add($"rules[{idx}]: unknown field '{prop.Name}' (known: {string.Join(", ", KnownRuleFields)})");

        var ev = Crit(rule, "event", idx, errs);
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
        if (rule.Event is not null && rule.Event != eventType) return false;
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
