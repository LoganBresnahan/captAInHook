using System.Text.Json;

namespace CaptainHook.Core;

// handlers.json (ADR-0010 decision 4): the registration file that makes
// ExecHandler user-reachable — the fourth user-facing contract. Semantics
// deliberately SPLIT two precedents: the FILE level is strict like
// dispatch.json (bad JSON / wrong shape / bad version ⇒ MALFORMED ⇒ zero
// exec handlers, loudly), while ENTRIES are warn-and-skip like harness
// overrides (an invalid entry is skipped with reasons; valid siblings
// register) — registration config, not authorization (the deliberate
// opposite of ADR-0005 d3; N2 records the tension and its mitigations).

/// One validated exec-handler registration. Events are CANONICALIZED at
/// parse (kebab → PascalCase, the ADR-0006 phase-4 silent-grant lesson: an
/// entry written "pre-tool-use" must register on the canonical event the
/// dispatch path uses, or it would parse fine and never fire). Env/PassEnv/
/// Cwd/ReadinessTimeoutMs parse as plain data here — enforcement lands with
/// the child-env-allowlist and resident-child-runtime slices.
public sealed record ExecEntry(
    string Name,
    string Command,
    IReadOnlyList<string> Args,
    IReadOnlyList<string> Events,
    ExecMode Mode,
    FailMode OnFailure,
    TimeSpan? Budget,
    TimeSpan? ReadinessTimeout,
    IReadOnlyDictionary<string, string> Env,
    IReadOnlyList<string> PassEnv,
    string? Cwd);

public enum ExecMode { Oneshot, Resident }

/// An entry that failed validation: skipped loudly, never registered.
public sealed record SkippedEntry(string Label, IReadOnlyList<string> Violations);

/// The tri-state of resolving handlers.json — same shape as
/// PolicyResolution (ADR-0006 decision 4), same edges (a directory is
/// Malformed, not Absent; unreadable is Malformed; empty file is Malformed —
/// presence signals intent).
public abstract record ExecHandlersResolution
{
    public sealed record Absent : ExecHandlersResolution;
    public sealed record Malformed(string Error) : ExecHandlersResolution;
    public sealed record Loaded(IReadOnlyList<ExecEntry> Entries, IReadOnlyList<SkippedEntry> Skipped)
        : ExecHandlersResolution;
}

public static class ExecHandlersFile
{
    private static readonly IReadOnlySet<string> KnownTopLevel =
        new HashSet<string> { "version", "handlers" };

    private static readonly IReadOnlySet<string> KnownEntryFields =
        new HashSet<string>
        {
            "name", "command", "args", "events", "mode", "failMode",
            "budgetMs", "readinessTimeoutMs", "env", "passEnv", "cwd",
        };

    // Budget bounds mirror Registry.CheckBudget EXACTLY — but as entry
    // VALIDATION, not a throw: bad data in a user file is a skipped entry,
    // never an exception (registration must survive any file content).
    private const double MaxBudgetMs = int.MaxValue - 3_600_000;

    /// The handlers file this process reads: explicit, else the env var,
    /// else ~/.captainHook/handlers.json — the DispatchPolicy.ResolvePath
    /// idiom, so the phase-7 reload watcher and this loader always agree.
    public static string ResolvePath(string? overridePath = null) =>
        overridePath
        ?? Environment.GetEnvironmentVariable("CAPTAINHOOK_HANDLERS_FILE")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        ".captainHook", "handlers.json");

    public static ExecHandlersResolution Resolve(string path)
    {
        if (Directory.Exists(path))
            return new ExecHandlersResolution.Malformed($"'{path}' is a directory, not a handlers file");
        if (!File.Exists(path))
            return new ExecHandlersResolution.Absent();

        string text;
        try { text = File.ReadAllText(path); }   // BOM-stripped by the reader (the put-policy lesson)
        catch (Exception ex)
        {
            return new ExecHandlersResolution.Malformed($"cannot read '{path}': {ex.Message}");
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            return TryParse(doc.RootElement, out var entries, out var skipped, out var fileErrors)
                ? new ExecHandlersResolution.Loaded(entries, skipped)
                : new ExecHandlersResolution.Malformed(
                    $"'{path}' is not a valid handlers file: {string.Join("; ", fileErrors)}");
        }
        catch (JsonException ex)
        {
            return new ExecHandlersResolution.Malformed($"'{path}' is not valid JSON: {ex.Message}");
        }
    }

    /// FILE-level strict parse (returns false ⇒ Malformed): the root must be
    /// an object with version:1 and a handlers array, no unknown or duplicate
    /// top-level fields. ENTRY-level violations never fail the file — each
    /// invalid entry lands in `skipped` with every violation collected.
    public static bool TryParse(JsonElement root,
        out IReadOnlyList<ExecEntry> entries, out IReadOnlyList<SkippedEntry> skipped,
        out IReadOnlyList<string> fileErrors)
    {
        var errs = new List<string>();
        var ok = new List<ExecEntry>();
        var bad = new List<SkippedEntry>();
        entries = ok; skipped = bad; fileErrors = errs;

        if (root.ValueKind != JsonValueKind.Object)
        {
            errs.Add("handlers file must be a JSON object");
            return false;
        }

        var seenTop = new HashSet<string>(StringComparer.Ordinal);
        foreach (var prop in root.EnumerateObject())
        {
            if (!seenTop.Add(prop.Name))
                errs.Add($"duplicate field '{prop.Name}'");
            else if (!KnownTopLevel.Contains(prop.Name))
                errs.Add($"unknown field '{prop.Name}' (known: {string.Join(", ", KnownTopLevel)})");
        }

        if (!root.TryGetProperty("version", out var ver))
            errs.Add("'version' is required and must be the number 1");
        else if (ver.ValueKind != JsonValueKind.Number || !ver.TryGetInt32(out var v) || v != 1)
            errs.Add($"'version' must be 1 (got {ver.GetRawText()})");

        if (!root.TryGetProperty("handlers", out var hs))
            errs.Add("'handlers' is required and must be an array");
        else if (hs.ValueKind != JsonValueKind.Array)
            errs.Add("'handlers' must be an array");
        else if (errs.Count == 0)
        {
            var namesSeen = new HashSet<string>(StringComparer.Ordinal);
            var i = 0;
            foreach (var entry in hs.EnumerateArray())
                ParseEntry(entry, i++, namesSeen, ok, bad);
        }

        return errs.Count == 0;
    }

    private static void ParseEntry(JsonElement entry, int idx, HashSet<string> namesSeen,
                                   List<ExecEntry> ok, List<SkippedEntry> bad)
    {
        var v = new List<string>();
        string label = $"handlers[{idx}]";

        if (entry.ValueKind != JsonValueKind.Object)
        {
            bad.Add(new SkippedEntry(label, [$"must be a JSON object (got {entry.ValueKind})"]));
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var prop in entry.EnumerateObject())
        {
            if (!seen.Add(prop.Name))
                v.Add($"duplicate field '{prop.Name}'");
            else if (!KnownEntryFields.Contains(prop.Name))
                v.Add($"unknown field '{prop.Name}' (known: {string.Join(", ", KnownEntryFields)})");
        }

        var name = ReqString(entry, "name", v);
        if (name is not null) label = $"handlers[{idx}] '{name}'";
        var command = ReqString(entry, "command", v);

        // events: required, non-empty, strings — CANONICALIZED here, and
        // duplicates (post-canon) rejected: "pre-tool-use" and "PreToolUse"
        // in one entry would double-register the handler on one event.
        var events = new List<string>();
        if (!entry.TryGetProperty("events", out var evs) || evs.ValueKind != JsonValueKind.Array)
            v.Add("'events' is required and must be a non-empty array of event names");
        else
        {
            foreach (var ev in evs.EnumerateArray())
            {
                if (ev.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(ev.GetString()))
                {
                    v.Add($"'events' entries must be non-empty strings (got {ev.GetRawText()})");
                    continue;
                }
                var canon = Harness.Canon(ev.GetString()!);
                if (events.Contains(canon, StringComparer.OrdinalIgnoreCase))
                    v.Add($"duplicate event '{canon}' (after canonicalization)");
                else events.Add(canon);
            }
            if (events.Count == 0 && !v.Any(x => x.StartsWith("'events'")))
                v.Add("'events' must not be empty");
        }

        var args = OptStringArray(entry, "args", v) ?? [];
        var passEnv = OptStringArray(entry, "passEnv", v) ?? [];
        var cwd = OptString(entry, "cwd", v);

        var mode = ExecMode.Oneshot;
        if (OptString(entry, "mode", v) is { } m)
            mode = m switch
            {
                "oneshot" => ExecMode.Oneshot,
                "resident" => ExecMode.Resident,
                _ => Fail<ExecMode>(v, $"'mode' must be \"oneshot\" or \"resident\" (got \"{m}\")"),
            };

        var failMode = FailMode.Open;
        if (OptString(entry, "failMode", v) is { } f)
            failMode = f switch
            {
                "open" => FailMode.Open,
                "closed" => FailMode.Closed,
                _ => Fail<FailMode>(v, $"'failMode' must be \"open\" or \"closed\" (got \"{f}\")"),
            };

        var budget = OptMs(entry, "budgetMs", v);
        var readiness = OptMs(entry, "readinessTimeoutMs", v);

        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        if (entry.TryGetProperty("env", out var envEl))
        {
            if (envEl.ValueKind != JsonValueKind.Object)
                v.Add("'env' must be an object of string values");
            else
                foreach (var p in envEl.EnumerateObject())
                {
                    if (p.Value.ValueKind != JsonValueKind.String)
                        v.Add($"env '{p.Name}' must be a string (got {p.Value.ValueKind})");
                    else if (!env.TryAdd(p.Name, p.Value.GetString()!))
                        v.Add($"env: duplicate variable '{p.Name}'");
                }
        }

        if (name is not null && !namesSeen.Add(name))
            v.Add($"duplicate handler name '{name}' — names must be unique (dispatch.json exclusion keys on them)");

        if (v.Count > 0)
        {
            bad.Add(new SkippedEntry(label, v));
            return;
        }

        ok.Add(new ExecEntry(name!, command!, args, events, mode, failMode, budget, readiness, env, passEnv, cwd));
    }

    private static T Fail<T>(List<string> v, string msg) where T : struct
    {
        v.Add(msg);
        return default;
    }

    private static string? ReqString(JsonElement e, string field, List<string> v)
    {
        if (e.TryGetProperty(field, out var el) && el.ValueKind == JsonValueKind.String
            && el.GetString() is { Length: > 0 } s && !string.IsNullOrWhiteSpace(s))
            return s;
        v.Add($"'{field}' is required and must be a non-empty string");
        return null;
    }

    private static string? OptString(JsonElement e, string field, List<string> v)
    {
        if (!e.TryGetProperty(field, out var el)) return null;
        if (el.ValueKind == JsonValueKind.String && el.GetString() is { Length: > 0 } s) return s;
        v.Add($"'{field}' must be a non-empty string when present (got {el.GetRawText()})");
        return null;
    }

    private static IReadOnlyList<string>? OptStringArray(JsonElement e, string field, List<string> v)
    {
        if (!e.TryGetProperty(field, out var el)) return null;
        if (el.ValueKind != JsonValueKind.Array)
        {
            v.Add($"'{field}' must be an array of strings");
            return null;
        }
        var list = new List<string>();
        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                v.Add($"'{field}' entries must be strings (got {item.GetRawText()})");
                return null;
            }
            list.Add(item.GetString()!);
        }
        return list;
    }

    private static TimeSpan? OptMs(JsonElement e, string field, List<string> v)
    {
        if (!e.TryGetProperty(field, out var el)) return null;
        if (el.ValueKind != JsonValueKind.Number || !el.TryGetInt64(out var ms))
        {
            v.Add($"'{field}' must be an integer millisecond count (got {el.GetRawText()})");
            return null;
        }
        if (ms <= 0 || ms > MaxBudgetMs)
        {
            // The Registry.CheckBudget bounds, as data validation: a value
            // the dispatcher would reject at registration must be a skipped
            // entry here, never a construction-time throw off user DATA.
            v.Add($"'{field}' must be positive and at most {(long)MaxBudgetMs}ms (~24.8 days; got {ms})");
            return null;
        }
        return TimeSpan.FromMilliseconds(ms);
    }
}
