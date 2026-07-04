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
    JsonElement Install)   // v1: raw data passthrough — do not over-model
{
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
                events[prop.Name] = kinds;
            }
        }

        // install: opaque JsonElement passthrough. Clone() detaches it from the
        // backing JsonDocument so the spec outlives the parse.
        var install = root.TryGetProperty("install", out var inst) ? inst.Clone() : default;

        return errs.Count > 0
            ? null
            : new HarnessSpec(name!, request, adapter!, events, install);
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
        var dir = overrideDir
            ?? Environment.GetEnvironmentVariable("CAPTAINHOOK_HARNESS_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                            ".captainHook", "harnesses");
        _specs = new Lazy<Dictionary<string, HarnessSpec>>(() => Load(dir));
    }

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
    public static string Canon(string s) =>
        s.Contains('-')
            ? string.Concat(s.Split('-').Select(p => p.Length == 0 ? p : char.ToUpperInvariant(p[0]) + p[1..]))
            : s;

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
    };

    public static IReadOnlyCollection<string> Known => ByName.Keys;

    public static IResponseAdapter Get(string name) =>
        ByName.TryGetValue(name, out var a)
            ? a
            : throw new InvalidOperationException(
                $"unknown response adapter '{name}' — known adapters: {string.Join(", ", Known)}");
}

/// Claude Code's hook stdout contract — moved VERBATIM from Program.cs's old
/// ClaudeCode class so the live deployment's output stays byte-identical.
/// NOTE: field names follow the Agent SDK hook docs; verify against current
/// docs before relying on them in a live settings.json wire-up.
internal sealed class ClaudeHookJsonAdapter : IResponseAdapter
{
    public string Serialize(HookEvent e, Effect eff) => eff switch
    {
        Effect.Inject inj => J(new { hookSpecificOutput = new { hookEventName = e.Type, additionalContext = inj.Text } }),
        Effect.Decide dec => J(new { hookSpecificOutput = new { hookEventName = e.Type, permissionDecision = dec.Verdict.ToString().ToLowerInvariant(), permissionDecisionReason = dec.Reason ?? "" } }),
        Effect.Replace rep => J(new { hookSpecificOutput = new { hookEventName = e.Type, replaceOutput = rep.Text } }),
        _ => "{}",
    };

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
