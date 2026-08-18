using System.Text.Json;
using CaptainHook.Actors;
using CaptainHook.Mail;

namespace CaptainHook.Core;

// Roadmap item 22 / ADR-0017 decision 7, slice `watch-rules` — the WATCHER's
// rule document and its strict parser. `~/.captainHook/watch.json` says which
// roles may be nudged by a robot, on what threshold, and inside what budget.
//
// It sits beside `DispatchPolicy` and copies its idiom deliberately — strict
// walk, every violation collected in one pass, all-or-nothing accept, never
// throws on bad DATA, a file tri-state at the I/O boundary — because an
// operator who has learned one consent document has learned both, and because
// every strictness rule here was already argued once in ADR-0006. What differs
// is the DIRECTION OF THE DEFAULT, and it is the whole reason this is a
// separate document rather than a section of that one:
//
//   * dispatch.json ABSENT means allow everything. Hooks are enhancement, and
//     a zero-config user gets the tool working.
//   * watch.json ABSENT means **zero robot nudges, ever**, and MALFORMED means
//     exactly the same thing plus a warn. There is no fail-open direction to
//     guard here because there is no fail-open direction at all: the channel
//     this document opens spends the owner's tokens and takes a turn on their
//     behalf (N1), so "I could not read your rules" can only ever mean "then I
//     do nothing".
//
// That symmetry is why this file has no `default` field. `dispatch.json` needs
// one because both baselines are legitimate; here a baseline of "nudge" would
// be a document whose absence and whose presence differ in the direction that
// costs money, and the only safe baseline is already the absent case.
//
// This slice PARSES and nothing else. Which rule wins for a role, how
// `quietFor` becomes a monotonic deadline, and how budgets are counted are
// `watcher-brain`'s — a pure function over these values (d4). Order is
// preserved so that function can be first-match-wins without re-reading the
// document.

/// How much mail-priority a rule cares about: exactly this class, or this class
/// and anything louder. `>=urgent` in the ADR's example is `(Urgent, AtLeast)`.
public sealed record WatchPriority(MailPriority Priority, bool AtLeast)
{
    public override string ToString() =>
        (AtLeast ? ">=" : "") + Priority.ToString().ToLowerInvariant();
}

/// The threshold half of a rule (d7's `when`). Every member is a FACT the
/// watcher already holds — pending mail, a monotonic quiet interval, whether
/// any session is live — because d4 makes the brain pure: a criterion that
/// needed an I/O call to evaluate would put the daemon's clock and filesystem
/// inside a decision that has to be reproducible from a fixture.
///
/// `QuietForMs` is MILLISECONDS and never a `TimeSpan` built from a wall clock:
/// it is one side of a monotonic subtraction (house invariant 2), and the parse
/// yields the number so nothing downstream is tempted to date-arithmetic it.
public sealed record WatchWhen(
    WatchPriority? Priority, int? QuietForMs, bool NoLiveSession);

/// The bound half (d11). Both counters, both required — see `TryParse`.
public sealed record WatchBudget(int PerEnvelope, int PerRoleHour);

/// One rule: for this ROLE, on this threshold, within this budget.
public sealed record WatchRule(string Role, WatchWhen When, WatchBudget Budget);

/// A parsed `watch.json`: the ordered rules and nothing else.
public sealed record WatchRules(IReadOnlyList<WatchRule> Rules)
{
    private static readonly IReadOnlySet<string> KnownTopLevel =
        new HashSet<string> { "version", "rules" };

    private static readonly IReadOnlySet<string> KnownRuleFields =
        new HashSet<string> { "role", "when", "budget" };

    private static readonly IReadOnlySet<string> KnownWhenFields =
        new HashSet<string> { "priority", "quietFor", "noLiveSession" };

    private static readonly IReadOnlySet<string> KnownBudgetFields =
        new HashSet<string> { "perEnvelope", "perRoleHour" };

    /// Strict parse: the rules, or null plus one error per violation. NEVER
    /// throws on bad DATA — a `JsonException` from malformed *bytes* is the
    /// caller's to catch, the same split `DispatchPolicy.TryParse` and
    /// `HarnessSpec.TryParse` use.
    public static WatchRules? TryParse(JsonElement root, out IReadOnlyList<string> errors)
    {
        var errs = new List<string>();
        errors = errs;

        if (root.ValueKind != JsonValueKind.Object)
        {
            errs.Add("watch rules must be a JSON object");
            return null;
        }

        CheckFields(root, KnownTopLevel, "", errs);

        if (!root.TryGetProperty("version", out var ver))
            errs.Add("'version' is required and must be the number 1");
        else if (ver.ValueKind != JsonValueKind.Number || !ver.TryGetInt32(out var v) || v != 1)
            errs.Add($"'version' must be 1 (got {RawText(ver)})");

        // `rules` is optional and an empty list is legal — "I have a watch file
        // and I want nothing nudged" is a real, sayable position, and it is the
        // one an operator reaches for when turning the channel off without
        // deleting their rules.
        var rules = new List<WatchRule>();
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

        return errs.Count > 0 ? null : new WatchRules(rules);
    }

    private static void ParseRule(JsonElement rule, int idx, List<WatchRule> into, List<string> errs)
    {
        if (rule.ValueKind != JsonValueKind.Object)
        {
            errs.Add($"rules[{idx}] must be a JSON object");
            return;
        }

        var before = errs.Count;
        CheckFields(rule, KnownRuleFields, $"rules[{idx}]: ", errs);

        var role = ParseRole(rule, idx, errs);
        var when = ParseWhen(rule, idx, errs);
        var budget = ParseBudget(rule, idx, errs);

        if (errs.Count == before && role is not null && when is not null && budget is not null)
            into.Add(new WatchRule(role, when, budget));
    }

    /// The role is checked against `MailAddress.IsRole` — the envelope parser's
    /// own predicate, never a second spelling of the grammar (ADR-0018 d2). A
    /// rule naming a role no sender could address is a rule that can never fire,
    /// silently, forever; loud here instead.
    ///
    /// A `role@instance` address is REFUSED, and this is the one place the
    /// refusal is a judgement rather than a grammar check: a rule is about a
    /// role's POLICY — may a robot be woken for this kind of work — while the
    /// mailbox a nudge eventually names is an instance the watcher FOUND. Per-
    /// instance rules are a real thing somebody may want later, and refusing an
    /// unbuilt spelling now is the reversible direction: no `watch.json` anybody
    /// has written becomes invalid when it is allowed.
    private static string? ParseRole(JsonElement rule, int idx, List<string> errs)
    {
        if (!rule.TryGetProperty("role", out var el))
        {
            errs.Add($"rules[{idx}]: 'role' is required");
            return null;
        }
        var s = el.ValueKind == JsonValueKind.String ? TryReadString(el) : null;
        if (string.IsNullOrEmpty(s))
        {
            errs.Add($"rules[{idx}].role must be a non-empty string");
            return null;
        }
        if (s.Contains('@'))
        {
            errs.Add($"rules[{idx}].role must be a bare role, not an address (got '{s}') — "
                + "a rule decides whether a ROLE may be woken; which mailbox gets nudged is the watcher's to find");
            return null;
        }
        if (!MailAddress.IsRole(s))
        {
            errs.Add($"rules[{idx}].role must match [a-z0-9][a-z0-9-]* (got '{s}') — "
                + "no sender could address a role spelled this way");
            return null;
        }
        return s;
    }

    /// `when` is REQUIRED, and it must name at least one of `priority` /
    /// `quietFor`. `noLiveSession` alone does not count: it DEFAULTS to true
    /// (d7's mixed-role rule), so a `when` containing only it states no
    /// threshold at all, and a rule with no threshold wakes a model the instant
    /// mail lands. Somebody who genuinely wants that writes `"quietFor": "0s"`
    /// and can be held to having meant it.
    private static WatchWhen? ParseWhen(JsonElement rule, int idx, List<string> errs)
    {
        if (!rule.TryGetProperty("when", out var el))
        {
            errs.Add($"rules[{idx}]: 'when' is required — a rule that opens the robot channel says on what threshold");
            return null;
        }
        if (el.ValueKind != JsonValueKind.Object)
        {
            errs.Add($"rules[{idx}].when must be a JSON object");
            return null;
        }

        var before = errs.Count;
        CheckFields(el, KnownWhenFields, $"rules[{idx}].when: ", errs);

        WatchPriority? priority = null;
        if (el.TryGetProperty("priority", out var p))
        {
            if (TryPriority(p, out var parsed)) priority = parsed;
            else
                errs.Add($"rules[{idx}].when.priority must be a mail priority, optionally prefixed \">=\" "
                    + $"(ambient, reconcile, urgent) (got {RawText(p)})");
        }

        int? quietForMs = null;
        if (el.TryGetProperty("quietFor", out var q))
        {
            if (TryDuration(q, out var ms)) quietForMs = ms;
            else
                errs.Add($"rules[{idx}].when.quietFor must be a duration — a non-negative whole number "
                    + $"followed by ms, s, min, or h (e.g. \"10min\") (got {RawText(q)})");
        }

        var noLiveSession = true;
        if (el.TryGetProperty("noLiveSession", out var n))
        {
            if (n.ValueKind is JsonValueKind.True or JsonValueKind.False)
                noLiveSession = n.ValueKind == JsonValueKind.True;
            else
                errs.Add($"rules[{idx}].when.noLiveSession must be true or false (got {RawText(n)})");
        }

        if (priority is null && quietForMs is null)
            errs.Add($"rules[{idx}].when must name at least one of priority/quietFor — "
                + "noLiveSession defaults to true and states no threshold on its own");

        return errs.Count == before ? new WatchWhen(priority, quietForMs, noLiveSession) : null;
    }

    /// `budget` is REQUIRED and so are both of its counters. There is no
    /// default, because every candidate default is a number of model calls this
    /// code would be choosing to spend on the owner's behalf without being told
    /// (N1). A rule that opens the loud channel states its bound, or it is not a
    /// rule.
    private static WatchBudget? ParseBudget(JsonElement rule, int idx, List<string> errs)
    {
        if (!rule.TryGetProperty("budget", out var el))
        {
            errs.Add($"rules[{idx}]: 'budget' is required — a channel that spends the owner's tokens states its bound");
            return null;
        }
        if (el.ValueKind != JsonValueKind.Object)
        {
            errs.Add($"rules[{idx}].budget must be a JSON object");
            return null;
        }

        var before = errs.Count;
        CheckFields(el, KnownBudgetFields, $"rules[{idx}].budget: ", errs);

        var perEnvelope = Counter(el, "perEnvelope", idx, errs);
        var perRoleHour = Counter(el, "perRoleHour", idx, errs);

        return errs.Count == before && perEnvelope is int e && perRoleHour is int h
            ? new WatchBudget(e, h)
            : null;
    }

    /// A budget counter: required, a whole number, at least 1. Zero is refused
    /// rather than read as "never" — a rule that can never fire is written by
    /// deleting it or by emptying `rules`, and a 0 here is far more often a
    /// half-finished edit than an intention.
    private static int? Counter(JsonElement budget, string field, int idx, List<string> errs)
    {
        if (!budget.TryGetProperty(field, out var el))
        {
            errs.Add($"rules[{idx}].budget: '{field}' is required and must be a whole number of at least 1");
            return null;
        }
        if (el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out var n) || n < 1)
        {
            errs.Add($"rules[{idx}].budget.{field} must be a whole number of at least 1 (got {RawText(el)})");
            return null;
        }
        return n;
    }

    /// `urgent` or `>=urgent`. The member name is matched case-insensitively —
    /// the deliberate divergence an address does NOT get (ADR-0018 d2): a
    /// priority is a CLOSED set the parser can enumerate and correct a casing
    /// slip against, exactly as `MailEnvelope` folds `kind`/`priority` on the
    /// way in. Matched by NAME rather than `Enum.TryParse`, which also accepts
    /// "2", comma lists and padded spellings — none of them wire spellings we
    /// advertise.
    private static bool TryPriority(JsonElement el, out WatchPriority? result)
    {
        result = null;
        if (el.ValueKind != JsonValueKind.String) return false;
        var s = TryReadString(el);
        if (string.IsNullOrEmpty(s)) return false;

        var atLeast = s.StartsWith(">=", StringComparison.Ordinal);
        var name = atLeast ? s[2..] : s;

        var member = Enum.GetNames<MailPriority>()
            .FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        if (member is null) return false;

        result = new WatchPriority(Enum.Parse<MailPriority>(member), atLeast);
        return true;
    }

    /// `<whole number><unit>`, units `ms` / `s` / `min` / `h`, yielding
    /// milliseconds. A CLOSED unit set, spelled out, because this number ends up
    /// on one side of a monotonic subtraction and a duration nobody can read
    /// back is a threshold nobody can reason about. No fractions (`1.5h` is
    /// `90min`), no bare numbers (a unitless `600` is ambiguous between seconds
    /// and milliseconds by a factor of a thousand, and guessing wrong is the
    /// difference between a nudge in ten minutes and a nudge in one second), no
    /// negatives. Zero IS legal and means "no quiet period" — the explicit way
    /// to say "the moment it lands".
    private static bool TryDuration(JsonElement el, out int ms)
    {
        ms = 0;
        if (el.ValueKind != JsonValueKind.String) return false;
        var s = TryReadString(el);
        if (string.IsNullOrEmpty(s)) return false;

        var digits = 0;
        while (digits < s.Length && s[digits] is >= '0' and <= '9') digits++;
        if (digits == 0 || digits == s.Length) return false;   // no number, or no unit

        var perUnit = s[digits..] switch
        {
            "ms" => 1L,
            "s" => 1_000L,
            "min" => 60_000L,
            "h" => 3_600_000L,
            _ => 0L,
        };
        if (perUnit == 0) return false;

        // Parsed as long and range-checked, so a document with a comically large
        // number is a REFUSAL rather than a silent overflow into a small — or
        // negative — deadline.
        if (!long.TryParse(s.AsSpan(0, digits), out var count)) return false;
        var total = count * perUnit;
        if (count != 0 && total / perUnit != count) return false;   // overflowed
        if (total > int.MaxValue) return false;

        ms = (int)total;
        return true;
    }

    /// Unknown OR duplicate field ⇒ malformed, at every level. Strict
    /// never-guess (ADR-0006 d1): a repeated known field is ambiguous and
    /// System.Text.Json would silently keep one.
    private static void CheckFields(
        JsonElement obj, IReadOnlySet<string> known, string prefix, List<string> errs)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var prop in obj.EnumerateObject())
        {
            if (!seen.Add(prop.Name))
                errs.Add($"{prefix}duplicate field '{prop.Name}'");
            else if (!known.Contains(prop.Name))
                errs.Add($"{prefix}unknown field '{prop.Name}' (known: {string.Join(", ", known)})");
        }
    }

    /// JsonDocument DEFERS string unescaping: a lone-surrogate escape parses as
    /// a fine document and throws `InvalidOperationException` at `GetString` —
    /// not `JsonException` at `Parse` — so without this guard it escapes the
    /// never-throw-on-DATA contract. Null means "present but unreadable"; every
    /// caller treats that as a violation. (The same guard, for the same reason,
    /// as `DispatchPolicy.TryReadString`.)
    private static string? TryReadString(JsonElement e)
    {
        try { return e.GetString(); }
        catch (InvalidOperationException) { return null; }
    }

    private static string RawText(JsonElement e) =>
        e.ValueKind == JsonValueKind.String && TryReadString(e) is { } s ? $"\"{s}\"" : e.GetRawText();

    /// The watch file this process reads: explicit, else the env var, else
    /// ~/.captainHook/watch.json — `DispatchPolicy.ResolvePath`'s idiom, so a
    /// sandbox (a test, the e2e's stub harness) can point the whole watcher at
    /// its own document without ever touching the operator's live tree.
    public static string ResolvePath(string? overridePath = null) =>
        overridePath
        ?? Environment.GetEnvironmentVariable("CAPTAINHOOK_WATCH_FILE")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        ".captainHook", "watch.json");
}

/// The tri-state of resolving `watch.json` — `PolicyResolution`'s shape, with
/// the crucial difference that TWO of the three cases mean the same thing.
///
/// `Effective()` is where that is stated once and for all: absent and malformed
/// both yield NO rules, so there is no fail-open asymmetry for a reader of this
/// file to have to reason about. Malformed additionally warns, because a
/// document that exists is intent to configure and silently ignoring it would
/// leave an operator waiting forever for nudges their typo cancelled.
public abstract record WatchResolution
{
    private WatchResolution() { }   // closed set: exactly the three below

    public sealed record Absent : WatchResolution;
    public sealed record Malformed(string Error) : WatchResolution;
    public sealed record Loaded(WatchRules Rules) : WatchResolution;

    private static readonly IReadOnlyList<WatchRule> None = [];

    /// Read and classify the regular file at `path`. Never throws: every failure
    /// mode (missing, a directory, unreadable, non-JSON, schema-invalid) lands
    /// in a case.
    ///
    /// Unlike `PolicyResolution.Resolve` there is no "fails toward quiet vs.
    /// fails toward a silent grant" weighing to do — every ambiguous case lands
    /// in Malformed and Malformed means zero nudges, which is also what Absent
    /// means. A directory at the path is still classified Malformed rather than
    /// Absent, not because the outcome differs but because the OPERATOR's
    /// situation does, and only one of the two says so out loud.
    public static WatchResolution Resolve(string path)
    {
        if (Directory.Exists(path))
            return new Malformed($"'{path}' is a directory, not a watch-rules file");
        if (!File.Exists(path))
            return new Absent();

        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception ex)   // permissions, races, device I/O — exists but unreadable
        {
            return new Malformed($"cannot read '{path}': {ex.Message}");
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            var rules = WatchRules.TryParse(doc.RootElement, out var errors);
            return rules is null
                ? new Malformed($"'{path}' is not a valid watch-rules file: {string.Join("; ", errors)}")
                : new Loaded(rules);
        }
        catch (JsonException ex)
        {
            return new Malformed($"'{path}' is not valid JSON: {ex.Message}");
        }
    }

    /// The rules in force. Absent and malformed are the SAME answer — none —
    /// which is the whole consent posture of the robot channel in one method.
    /// Malformed says so on the trail; hold the resolution rather than
    /// re-resolving, or the warn repeats per call by design.
    public IReadOnlyList<WatchRule> Effective()
    {
        switch (this)
        {
            case Loaded l: return l.Rules.Rules;

            case Malformed m:
                Log.Warn("watch", "watch.malformed", new LogFields
                {
                    Msg = "watch rules unreadable — no robot nudges will fire until it parses",
                    Data = new Dictionary<string, object> { ["error"] = m.Error },
                });
                return None;

            default: return None;
        }
    }
}
