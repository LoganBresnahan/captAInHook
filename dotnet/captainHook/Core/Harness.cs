using System.Reflection;
using System.Text.Json;
using CaptainHook.Actors;

namespace CaptainHook.Core;

// The "declarative harness registry" (roadmap item 3). A HARNESS is the agent
// host driving us — Claude Code today, anything speaking JSON-over-stdio
// tomorrow. Everything host-specific used to be hardcoded in Program.cs; now
// it lives in a JSON spec and DATA SELECTS BEHAVIOR:
//
//   * request  — which payload fields carry the event name / session / cwd,
//   * response — WHICH coded adapter serializes our Effect back to the host,
//   * events   — which effect kinds each lifecycle event may carry (a
//                capability gate, pharos-config style),
//   * install  — opaque passthrough data for the future management API.
//
// The adapters themselves stay a CLOSED, coded set (no template language) —
// the deepseek-moby registry pattern: declare capabilities in data, provide
// lookup in code.

/// Payload field names the harness uses in its request JSON. Defaults match
/// Claude Code so a minimal spec can omit the block entirely.
public sealed record HarnessRequestSpec(
    string EventNameField = "hook_event_name",
    string SessionIdField = "session_id",
    string CwdField = "cwd");

/// One harness, fully described. Parsed from JSON, validated moby-style
/// (collect every violation as a clear error string; all-or-nothing accept).
public sealed record HarnessSpec(
    string Name,
    HarnessRequestSpec Request,
    string ResponseAdapter,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Events,
    JsonElement Install,   // v1: raw data passthrough — do not over-model
    TimeSpan? HookTimeoutHint = null)   // ADR-0010 d9: the harness's hook-command
                                        // timeout, INFORMATIONAL — we warn from it
                                        // (handlers.budgetBeyondHarness), never
                                        // enforce or auto-edit harness config
{
    /// The adapter name that means "this harness has no wire format" — the
    /// `internal` spec's, and the marker `AnswersHooks` reads.
    public const string NoWireAdapterName = "none";

    /// Can a hook dispatch be ANSWERED on this harness? False for an internal
    /// one (ADR-0017 d5): it has no stdout contract, so a hook arriving on it
    /// — `--harness internal`, which nothing but a typo or a probe would write
    /// — is refused at the wire sites rather than answered with the empty
    /// string. Keyed on the ADAPTER rather than on the name, because the
    /// property that makes a harness unanswerable is having no wire format,
    /// and a second internal harness must inherit the refusal for free.
    public bool AnswersHooks => ResponseAdapter != NoWireAdapterName;

    /// The effect kinds a spec may declare. Background is deliberately absent:
    /// background effects never survive Merge, so they never reach the gate.
    public static readonly IReadOnlySet<string> KnownEffectKinds =
        new HashSet<string> { "inject", "decide", "replace", "noop" };

    /// Validated parse: returns the spec, or null plus one error per violation.
    /// Never throws on bad DATA — only the caller decides whether bad data is
    /// fatal (embedded default) or merely warned about (user override).
    public static HarnessSpec? TryParse(JsonElement root, out IReadOnlyList<string> errors)
    {
        var errs = new List<string>();
        errors = errs;

        if (root.ValueKind != JsonValueKind.Object)
        {
            errs.Add("harness spec must be a JSON object");
            return null;
        }

        // name: required, non-empty — it is the registry key.
        var name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
            ? n.GetString() : null;
        if (string.IsNullOrWhiteSpace(name))
            errs.Add("'name' is required and must be a non-empty string");

        // request: optional block; each field falls back to the Claude names.
        var request = new HarnessRequestSpec();
        if (root.TryGetProperty("request", out var req) && req.ValueKind == JsonValueKind.Object)
            request = new HarnessRequestSpec(
                EventNameField: Str(req, "eventNameField") ?? request.EventNameField,
                SessionIdField: Str(req, "sessionIdField") ?? request.SessionIdField,
                CwdField: Str(req, "cwdField") ?? request.CwdField);

        // response.adapter: must name one of the CLOSED adapter set.
        var adapter = root.TryGetProperty("response", out var resp) && resp.ValueKind == JsonValueKind.Object
            ? Str(resp, "adapter") : null;
        if (adapter is null || !ResponseAdapters.Known.Contains(adapter))
            errs.Add($"'response.adapter' must be one of: {string.Join(", ", ResponseAdapters.Known)} (got '{adapter ?? "<missing>"}')");

        // events: map of event -> { effects: [kind...] }. Every kind must be known.
        var events = new Dictionary<string, IReadOnlyList<string>>();
        if (root.TryGetProperty("events", out var evs) && evs.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in evs.EnumerateObject())
            {
                var kinds = new List<string>();
                if (prop.Value.ValueKind == JsonValueKind.Object
                    && prop.Value.TryGetProperty("effects", out var effs)
                    && effs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var k in effs.EnumerateArray())
                    {
                        var kind = k.ValueKind == JsonValueKind.String ? k.GetString() : null;
                        if (kind is null || !KnownEffectKinds.Contains(kind))
                            errs.Add($"events.{prop.Name}: unknown effect kind '{kind ?? k.ToString()}' (known: {string.Join(", ", KnownEffectKinds)})");
                        else
                            kinds.Add(kind);
                    }
                }
                // Event KEYS canonicalize at load, exactly as ExecHandlersFile
                // does for registrations — both sides of the capability lookup
                // must agree, and dispatch canonicalizes its side. Without
                // this, a spec written `"stop"` (or `"user-prompt-submit"`)
                // declares an event no dispatch can ever name: the lookup
                // misses into the permissive undeclared path and a
                // deliberately restrictive declaration silently flips OPEN,
                // which is the one direction a capability gate must never
                // fail. Two spellings of one event is a contradiction, not a
                // merge — the spec says two different things about the same
                // seam and we refuse to guess which was meant.
                var key = Harness.Canon(prop.Name);
                if (events.ContainsKey(key))
                    errs.Add($"events.{prop.Name}: duplicate event declaration — '{key}' is already declared");
                else
                    events[key] = kinds;
            }
        }

        // install: opaque JsonElement passthrough. Clone() detaches it from the
        // backing JsonDocument so the spec outlives the parse.
        var install = root.TryGetProperty("install", out var inst) ? inst.Clone() : default;

        // hookTimeoutHintMs: optional, informational (ADR-0010 d9) — the
        // harness's own hook-command timeout. Claude Code speaks seconds in
        // ITS config; the spec speaks milliseconds like every budget field.
        TimeSpan? hint = null;
        if (root.TryGetProperty("hookTimeoutHintMs", out var th))
        {
            if (th.ValueKind == JsonValueKind.Number && th.TryGetInt64(out var ms) && ms > 0)
                hint = TimeSpan.FromMilliseconds(ms);
            else
                errs.Add($"'hookTimeoutHintMs' must be a positive integer millisecond count (got {th.GetRawText()})");
        }

        return errs.Count > 0
            ? null
            : new HarnessSpec(name!, request, adapter!, events, install, hint);
    }

    static string? Str(JsonElement obj, string prop) =>
        obj.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// Loads harness specs in two layers (pharos defaults()/load()/cached()):
/// embedded defaults first, then user overrides from a directory — a valid
/// user file whose 'name' matches an embedded spec REPLACES it wholesale
/// (v1: no deep merge); a new name ADDS a harness. An INVALID user file is
/// warned about and skipped — a bad override must never crash the live hook.
public sealed class HarnessRegistry
{
    private readonly Lazy<Dictionary<string, HarnessSpec>> _specs;   // cached(): load once

    /// Program.cs uses the default directory (CAPTAINHOOK_HARNESS_DIR env,
    /// else ~/.captainHook/harnesses); tests pass an explicit directory.
    public HarnessRegistry(string? overrideDir = null)
    {
        _specs = new Lazy<Dictionary<string, HarnessSpec>>(() => Load(ResolveDir(overrideDir)));
    }

    /// The override directory this process uses: explicit, else the env var,
    /// else ~/.captainHook/harnesses. Shared with ReloadingHarnessRegistry so
    /// the watcher and the loader always agree on the directory.
    public static string ResolveDir(string? overrideDir = null) =>
        overrideDir
        ?? Environment.GetEnvironmentVariable("CAPTAINHOOK_HARNESS_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        ".captainHook", "harnesses");

    public HarnessSpec Get(string name) =>
        _specs.Value.TryGetValue(name, out var spec)
            ? spec
            : throw new InvalidOperationException(
                $"unknown harness '{name}' — known harnesses: {string.Join(", ", Known)}");

    public IReadOnlyCollection<string> Known => _specs.Value.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();

    private static Dictionary<string, HarnessSpec> Load(string overrideDir)
    {
        var specs = new Dictionary<string, HarnessSpec>();

        // Layer 1 — embedded defaults. These ship inside the assembly, so a
        // broken one is a build defect: fail loudly rather than limp along.
        var asm = Assembly.GetExecutingAssembly();
        foreach (var res in asm.GetManifestResourceNames()
                               .Where(r => r.Contains(".harnesses.") && r.EndsWith(".json")))
        {
            using var stream = asm.GetManifestResourceStream(res)!;
            using var doc = JsonDocument.Parse(stream);
            var spec = HarnessSpec.TryParse(doc.RootElement, out var errors)
                ?? throw new InvalidOperationException(
                    $"embedded harness spec '{res}' is invalid: {string.Join("; ", errors)}");
            specs[spec.Name] = spec;
        }

        // Layer 2 — user overrides. Same-name replaces, new-name adds,
        // invalid warns and keeps whatever layer 1 provided.
        if (Directory.Exists(overrideDir))
        {
            foreach (var file in Directory.EnumerateFiles(overrideDir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    var spec = HarnessSpec.TryParse(doc.RootElement, out var errors);
                    if (spec is null)
                    {
                        Log.Warn("harness", "harness.specInvalid", new LogFields
                        {
                            Msg = string.Join("; ", errors),
                            Data = new Dictionary<string, object> { ["file"] = file },
                        });
                        continue;
                    }
                    specs[spec.Name] = spec;
                }
                catch (JsonException ex)
                {
                    Log.Warn("harness", "harness.specInvalid", new LogFields
                    {
                        Msg = $"not valid JSON: {ex.Message}",
                        Data = new Dictionary<string, object> { ["file"] = file },
                    });
                }
            }
        }

        return specs;
    }
}

/// Request-side plumbing: spec-driven payload parsing plus the capability
/// gate that keeps a harness from receiving effect kinds it never declared.
public static class Harness
{
    /// Build the normalized HookEvent from the raw payload using the spec's
    /// field names. The CLI arg (kebab-case, cavemem style) wins over the
    /// payload's own field; either way the name is canonicalized to Pascal.
    public static HookEvent ParseEvent(HarnessSpec spec, string? cliEventName, JsonElement payload)
    {
        var name = cliEventName;
        name ??= payload.TryGetProperty(spec.Request.EventNameField, out var hen) ? hen.GetString() : null;
        name = Canon(name ?? "UserPromptSubmit");

        return new HookEvent(
            Type: name,
            SessionId: payload.TryGetProperty(spec.Request.SessionIdField, out var sid) ? sid.GetString() : null,
            Cwd: payload.TryGetProperty(spec.Request.CwdField, out var cwd) ? cwd.GetString() : null,
            Payload: payload);
    }

    /// kebab-case (cavemem style) -> PascalCase (host style).
    ///
    /// A SINGLE-WORD event is just a one-segment kebab and takes the same
    /// rule: the earlier `Contains('-')` short-circuit passed `stop` through
    /// untouched, which read as a distinct event from the spec's `Stop` —
    /// every declaration missed, so the capability gate fell to its permissive
    /// undeclared path and any emitted `hookEventName` was a word the host
    /// rejects. Harmless while no single-word event was wired up; load-bearing
    /// the moment one is (ADR-0016 d5's Stop seam). Already-PascalCase input
    /// is unchanged, which is why this stays idempotent for every caller.
    public static string Canon(string s) =>
        string.Concat(s.Split('-').Select(p => p.Length == 0 ? p : char.ToUpperInvariant(p[0]) + p[1..]));

    /// Capability gate, applied AFTER Merge to the single merged effect.
    /// Declared event + undeclared effect kind => warn and downgrade to Noop
    /// (never send a harness something it can't represent). An event ABSENT
    /// from the spec is permissively allowed with a debug line — new/unknown
    /// events must not silently eat effects. Noop always passes: it is the
    /// downgrade target, so gating it would be circular.
    public static Effect ApplyCapabilityGate(HarnessSpec spec, HookEvent e, Effect merged, string? dispatchId = null)
    {
        var kind = KindOf(merged);
        if (kind == "noop") return merged;

        if (!spec.Events.TryGetValue(e.Type, out var allowed))
        {
            Log.Debug("harness", "harness.eventUndeclared", new LogFields
            {
                DispatchId = dispatchId,
                HookEvent = e.Type,
                Data = new Dictionary<string, object> { ["harness"] = spec.Name, ["effect"] = kind },
            });
            return merged;
        }

        if (allowed.Contains(kind)) return merged;

        Log.Warn("harness", "harness.effectUnsupported", new LogFields
        {
            DispatchId = dispatchId,
            HookEvent = e.Type,
            Data = new Dictionary<string, object> { ["harness"] = spec.Name, ["effect"] = kind },
        });
        return new Effect.Noop();
    }

    internal static string KindOf(Effect eff) => eff switch
    {
        Effect.Inject => "inject",
        Effect.Decide => "decide",
        Effect.Replace => "replace",
        _ => "noop",
    };
}

/// Response side: our internal Effect -> the harness's wire format. This is
/// the CLOSED adapter set the specs select from by name — data picks WHICH
/// adapter, code defines WHAT it emits. Adding a wire format = one class here
/// plus its name in Known; zero changes in Program.cs.
public interface IResponseAdapter
{
    string Serialize(HookEvent e, Effect eff);
}

public static class ResponseAdapters
{
    private static readonly Dictionary<string, IResponseAdapter> ByName = new()
    {
        ["claude-hook-json"] = new ClaudeHookJsonAdapter(),
        ["generic-json"] = new GenericJsonAdapter(),
        ["none"] = new NoWireAdapter(),
    };

    public static IReadOnlyCollection<string> Known => ByName.Keys;

    public static IResponseAdapter Get(string name) =>
        ByName.TryGetValue(name, out var a)
            ? a
            : throw new InvalidOperationException(
                $"unknown response adapter '{name}' — known adapters: {string.Join(", ", Known)}");
}

/// The absence of a wire format, as a member of the closed set (ADR-0017 d5).
/// An INTERNAL event — one the daemon raises for itself, `MailNudge` being the
/// first — has no shim, no stdout, and no caller waiting for an answer: every
/// effect a handler returns is logged and ignored, and the reply travels back
/// on the mailbox bus instead.
///
/// A spec still has to name an adapter, so rather than lending an internal
/// harness a real one (and leaving a serializer one refactor away from the
/// sacred channel), the closed set gains the honest answer. Reaching this at
/// all is a BUG — the internal dispatch path returns before serialization —
/// so it emits nothing and says so loudly, which is strictly better than a
/// throw on a daemon path and infinitely better than bytes on a stdout that
/// belongs to no hook.
internal sealed class NoWireAdapter : IResponseAdapter
{
    public string Serialize(HookEvent e, Effect eff)
    {
        Log.Warn("harness", "harness.noWireSerialize", new LogFields
        {
            HookEvent = e.Type,
            Msg = "an internal event reached a stdout serializer — nothing was written; this is a wiring bug",
            Data = new Dictionary<string, object> { ["effect"] = Harness.KindOf(eff) },
        });
        return "";
    }
}

/// Claude Code's hook stdout contract — moved VERBATIM from Program.cs's old
/// ClaudeCode class so the live deployment's output stays byte-identical.
/// NOTE: field names follow the Agent SDK hook docs; verify against current
/// docs before relying on them in a live settings.json wire-up.
internal sealed class ClaudeHookJsonAdapter : IResponseAdapter
{
    /// Events whose blocking verb rides the TOP-LEVEL `decision`/`reason` pair
    /// instead of a `hookSpecificOutput` member — ADR-0016 decision 5's
    /// "coded-adapter work inside ADR-0003's closed set, not config".
    ///
    /// The host parses `hookSpecificOutput` as a UNION keyed on
    /// `hookEventName` and declares NO member for Stop, so the
    /// PreToolUse-shaped `permissionDecision` every other event takes is not
    /// merely ignored at turn end — it fails the union parse and the block is
    /// lost SILENTLY, which is the whole hazard this branch exists to close.
    /// Verified against the shipped host's own schemas, not the published
    /// docs, which describe a different (nested) Stop shape; the version this
    /// was read from and how to re-probe it live in
    /// doc/platform.md § The Stop block shape.
    ///
    /// `SubagentStop` rides the same contract and is declared decide-only in
    /// the spec exactly like Stop: were it UNDECLARED, an Inject there would
    /// pass ApplyCapabilityGate's permissive path and ship the nested shape —
    /// the same silent loss this branch closes for Stop, one event over.
    private static bool DecidesAtTopLevel(string eventType) =>
        eventType is "Stop" or "SubagentStop";

    public string Serialize(HookEvent e, Effect eff) => eff switch
    {
        Effect.Inject inj => J(new { hookSpecificOutput = new { hookEventName = e.Type, additionalContext = inj.Text } }),
        Effect.Decide dec when DecidesAtTopLevel(e.Type) => TopLevelDecision(e, dec),
        Effect.Decide dec => J(new { hookSpecificOutput = new { hookEventName = e.Type, permissionDecision = dec.Verdict.ToString().ToLowerInvariant(), permissionDecisionReason = dec.Reason ?? "" } }),
        Effect.Replace rep => J(new { hookSpecificOutput = new { hookEventName = e.Type, replaceOutput = rep.Text } }),
        _ => "{}",
    };

    /// The top-level vocabulary is `approve|block` — NOT the allow/deny/ask
    /// the nested shape speaks — and any third word fails the host's schema
    /// validation, discarding the whole decision (surfaced only as a
    /// non-blocking hook error). `ask` has no meaning at turn end anyway (there is no
    /// pending action to ask about), so it degrades to noop on the house rule
    /// ApplyCapabilityGate already states one layer up: never send a harness
    /// something it cannot represent. The gate cannot make this call itself —
    /// it reasons about effect KINDS, and `decide` is genuinely declared here;
    /// only the adapter knows which VERDICTS the wire has words for.
    private static string TopLevelDecision(HookEvent e, Effect.Decide dec) => dec.Verdict switch
    {
        Verdict.Deny => J(new { decision = "block", reason = dec.Reason ?? "" }),
        // Unreachable through a real dispatch — Merge has no Allow case, so a
        // lone Decide(Allow) falls through to Noop and never arrives here. It
        // is spelled out anyway because the adapter's job is to be TOTAL over
        // the wire vocabulary: the day Merge learns an allow, this must not be
        // the place that has to be remembered.
        Verdict.Allow => J(new { decision = "approve", reason = dec.Reason ?? "" }),
        _ => Unrepresentable(e, dec.Verdict),
    };

    private static string Unrepresentable(HookEvent e, Verdict verdict)
    {
        Log.Warn("harness", "harness.verdictUnsupported", new LogFields
        {
            HookEvent = e.Type,
            Data = new Dictionary<string, object>
            {
                ["adapter"] = "claude-hook-json",
                ["verdict"] = verdict.ToString().ToLowerInvariant(),
            },
        });
        return "{}";
    }

    static string J(object o) => JsonSerializer.Serialize(o);
}

/// The proof that a second harness needs ZERO code in Program.cs: a neutral
/// envelope any JSON-speaking host could consume.
internal sealed class GenericJsonAdapter : IResponseAdapter
{
    public string Serialize(HookEvent e, Effect eff)
    {
        object effect = eff switch
        {
            Effect.Inject inj => new { kind = "inject", text = inj.Text },
            Effect.Decide dec => new { kind = "decide", verdict = dec.Verdict.ToString().ToLowerInvariant(), reason = dec.Reason ?? "" },
            Effect.Replace rep => new { kind = "replace", text = rep.Text },
            _ => new { kind = "noop" },
        };
        return JsonSerializer.Serialize(new { @event = e.Type, effect });
    }
}

/// The daemon's harness view (ADR-0004 decision 1): environment is read once
/// at daemon start, but harness specs keep ADR-0003's contract — edit a spec,
/// effective next hook. Per dispatch the daemon takes a COMPOSITE STAMP of
/// the override directory — every *.json's (name, mtime, size), a handful of
/// stats — and rebuilds the registry when it moves; the embedded defaults are
/// fixed at build. The composite deliberately upgrades the ADR's
/// dir-mtime sketch: an IN-PLACE overwrite (`cat > spec.json`) never bumps
/// the parent directory's mtime on Linux, which would silently exempt a whole
/// class of editors from the contract; per-file stamps close that hole while
/// staying trivially cheap. Stamp comparison is EQUALITY on wall-clock
/// mtimes (change detection, like content identity), never interval math —
/// the monotonic rule is untouched.
public sealed class ReloadingHarnessRegistry(string? overrideDir = null)
{
    private readonly string _dir = HarnessRegistry.ResolveDir(overrideDir);
    private HarnessRegistry _current = new(overrideDir);
    private string _stamp = Stamp(HarnessRegistry.ResolveDir(overrideDir));

    private static string Stamp(string dir)
    {
        if (!Directory.Exists(dir)) return "<absent>";
        var sb = new System.Text.StringBuilder();
        foreach (var f in Directory.EnumerateFiles(dir, "*.json").OrderBy(x => x, StringComparer.Ordinal))
        {
            var fi = new FileInfo(f);
            sb.Append(f).Append('|').Append(fi.LastWriteTimeUtc.Ticks).Append('|').Append(fi.Length).Append('\n');
        }
        return sb.ToString();
    }

    /// The registry as of now. Benign race under concurrent dispatches: two
    /// callers seeing a fresh stamp both rebuild and one wins the reference
    /// swap — both registries are valid loads of the same directory. Write
    /// order (_current before _stamp) means the worst interleaving serves one
    /// stale registry for one dispatch, then converges.
    public HarnessRegistry Current
    {
        get
        {
            var s = Stamp(_dir);
            if (s != _stamp)
            {
                var fresh = new HarnessRegistry(_dir);
                var known = fresh.Known;   // force the load NOW, inside the reload story
                _current = fresh;
                _stamp = s;
                Log.Info("harness", "harness.reload", new LogFields
                {
                    Data = new Dictionary<string, object>
                    {
                        ["dir"] = _dir,
                        ["known"] = string.Join(",", known),
                    },
                });
            }
            return _current;
        }
    }
}
