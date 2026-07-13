using System.Text;
using System.Text.Json;

namespace CaptainHook.Core;

/// How a child's stdout parsed (ADR-0010 decision 2). A closed set, like
/// ForwardOutcome: the adapter pattern-matches. `Empty` is its own case —
/// NOT an error and NOT silently Noop at this layer — because the Noop
/// mapping is conditional on exit code (exit 0 + empty stdout ⇒ Noop is the
/// ADAPTER's decision; exit 0 + garbage is a failure), and the codec cannot
/// see exit codes.
public abstract record ExecAnswer
{
    /// One valid effect object. Never Effect.Background — fire-and-forget
    /// does not cross the wire (a child replies-then-lingers instead).
    /// `DispatchId` is the echo when the answer carried one (null when
    /// omitted): the codec only EXTRACTS it — whether it is required
    /// (resident: MUST echo, ADR-0010 d3) or optional-must-match (oneshot)
    /// is the adapter's business, mode being invisible at this layer.
    public sealed record Ok(Effect Effect, string? DispatchId = null) : ExecAnswer;

    /// Nothing but whitespace (or a lone BOM). Exit-0 ⇒ Noop, decided upstream.
    public sealed record Empty : ExecAnswer;

    /// Bad DATA, every violation collected (house rule: never guess, report
    /// all at once — HarnessSpec.TryParse / DispatchPolicy.TryParse style).
    public sealed record Malformed(IReadOnlyList<string> Violations) : ExecAnswer;
}

/// The child wire contract — the hook protocol turned inward (ADR-0010
/// decision 2). Engine-side codec for both directions:
///
///   stdin  (us → child):  one single-line JSON envelope
///     {"v":1,"dispatchId":"…","event":{"type":"…","sessionId":"…","cwd":"…","payload":{…}}}
///   stdout (child → us):  one JSON object from the CLOSED answer grammar
///     {"effect":"inject","text":…} | {"effect":"decide","verdict":"allow|deny|ask","reason":…}
///     {"effect":"replace","text":…} | {"effect":"noop"}
///     — each optionally + "dispatchId":"…" (the echo; resident answers MUST
///     carry it, oneshot may — enforcement is the adapter's, per mode)
///   resident readiness (child → us, first line): {"ready":1}
///
/// The envelope is the fourth user-facing stable contract (ADR-0010 N1):
/// field ORDER and escaping are pinned by golden tests, `v` is bumped on any
/// breaking change, and the single-line guarantee (no raw newlines, ever) is
/// what the resident mode's line-framed lock-step protocol stands on.
/// Parsing is strict per the Frame.Decode house rules: unknown fields,
/// unknown effect kinds, duplicate fields, wrong types, and trailing content
/// are all MALFORMED — the codec never guesses, and System.Text.Json's
/// permissive defaults (ignore unknowns, keep last duplicate) are
/// deliberately inverted here.
public static class ExecWire
{
    public const int Version = 1;

    // Every answer kind may carry the optional dispatchId echo (resident's
    // lock-step attribution — ADR-0010 d3; oneshot children may echo too).
    private static readonly IReadOnlySet<string> InjectFields =
        new HashSet<string> { "effect", "text", "dispatchId" };
    private static readonly IReadOnlySet<string> DecideFields =
        new HashSet<string> { "effect", "verdict", "reason", "dispatchId" };
    private static readonly IReadOnlySet<string> ReplaceFields =
        new HashSet<string> { "effect", "text", "dispatchId" };
    private static readonly IReadOnlySet<string> NoopFields =
        new HashSet<string> { "effect", "dispatchId" };

    /// Encode the envelope the child receives on stdin: one line, compact,
    /// deterministic field order (v, dispatchId, event{type, sessionId, cwd,
    /// payload}). Absent sessionId/cwd are OMITTED, not null (the WireJsonl
    /// omit-null precedent). The payload is re-written compact through the
    /// writer, so caller-side formatting can never smuggle a raw newline into
    /// the line-framed stream.
    public static string Envelope(HookEvent e, string dispatchId)
    {
        using var buf = new MemoryStream();
        using (var w = new Utf8JsonWriter(buf))   // not Indented ⇒ single line
        {
            w.WriteStartObject();
            w.WriteNumber("v", Version);
            w.WriteString("dispatchId", dispatchId);
            w.WritePropertyName("event");
            w.WriteStartObject();
            w.WriteString("type", e.Type);
            if (e.SessionId is not null) w.WriteString("sessionId", e.SessionId);
            if (e.Cwd is not null) w.WriteString("cwd", e.Cwd);
            w.WritePropertyName("payload");
            if (e.Payload.ValueKind == JsonValueKind.Undefined)
                w.WriteNullValue();   // a default-constructed HookEvent (tests); real parses always carry a payload
            else
                e.Payload.WriteTo(w);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buf.ToArray());
    }

    /// Strict-parse a child's stdout into one answer. NEVER throws on bad
    /// data — malformed bytes, trailing content, and a second object all come
    /// back as ExecAnswer.Malformed (JsonDocument.Parse rejects anything past
    /// the first complete value, which IS decision 2's one-object rule). One
    /// leading BOM is stripped before parsing, mirroring the policy loader
    /// (the ADR-0007 put-policy BOM-asymmetry lesson).
    public static ExecAnswer ParseAnswer(string stdout)
    {
        var text = stdout.Length > 0 && stdout[0] == '\uFEFF' ? stdout[1..] : stdout;
        if (string.IsNullOrWhiteSpace(text)) return new ExecAnswer.Empty();

        JsonDocument doc;
        try { doc = JsonDocument.Parse(text); }
        catch (JsonException ex)
        {
            return new ExecAnswer.Malformed([$"invalid JSON: {ex.Message}"]);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new ExecAnswer.Malformed(["answer must be a JSON object"]);

            var errs = new List<string>();

            // Duplicate fields are ambiguous — System.Text.Json would silently
            // keep one; reject instead (the DispatchPolicy strictness lesson).
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var prop in root.EnumerateObject())
                if (!seen.Add(prop.Name))
                    errs.Add($"duplicate field '{prop.Name}'");

            string? kind = null;
            if (!root.TryGetProperty("effect", out var eff))
                errs.Add("'effect' is required (one of: inject, decide, replace, noop)");
            else if (eff.ValueKind != JsonValueKind.String)
                errs.Add($"'effect' must be a string (got {eff.ValueKind})");
            else
                kind = eff.GetString();

            var known = kind switch
            {
                "inject" => InjectFields,
                "decide" => DecideFields,
                "replace" => ReplaceFields,
                "noop" => NoopFields,
                _ => null,
            };
            if (kind is not null && known is null)
                errs.Add($"unknown effect '{kind}' (one of: inject, decide, replace, noop)");
            if (known is not null)
                foreach (var prop in root.EnumerateObject())
                    if (!known.Contains(prop.Name))
                        errs.Add($"unknown field '{prop.Name}' for effect '{kind}'");

            Effect? parsed = known is null ? null : kind switch
            {
                "inject" => Text(root, kind, errs) is { } t ? new Effect.Inject(t) : null,
                "replace" => Text(root, kind, errs) is { } t ? new Effect.Replace(t) : null,
                "decide" => Decide(root, errs),
                "noop" => new Effect.Noop(),
                _ => null,
            };

            string? echo = null;
            if (root.TryGetProperty("dispatchId", out var did))
            {
                if (did.ValueKind != JsonValueKind.String)
                    errs.Add($"'dispatchId' must be a string when present (got {did.ValueKind})");
                else
                    echo = did.GetString();
            }

            return errs.Count > 0
                ? new ExecAnswer.Malformed(errs)
                : new ExecAnswer.Ok(parsed!, echo);
        }
    }

    /// Strict-parse a resident child's READINESS line (ADR-0010 d3): exactly
    /// `{"ready":1}` — an object with the single field `ready`, number value
    /// exactly 1 (the envelope version the child speaks; this engine requires
    /// 1). Same tolerance envelope as ParseAnswer (one leading BOM,
    /// surrounding whitespace via the JSON parser, the reader owns \r\n) —
    /// and the same never-guess strictness inside: anything else on the
    /// first non-empty line is a protocol failure, including a valid ANSWER
    /// (answering before ready is out of protocol).
    public static bool TryParseReady(string line)
    {
        var text = line.Length > 0 && line[0] == '\uFEFF' ? line[1..] : line;
        if (string.IsNullOrWhiteSpace(text)) return false;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(text); }
        catch (JsonException) { return false; }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            var count = 0;
            foreach (var prop in root.EnumerateObject())
            {
                count++;
                if (prop.Name != "ready") return false;
                if (prop.Value.ValueKind != JsonValueKind.Number) return false;
                if (!prop.Value.TryGetInt32(out var v) || v != Version) return false;
            }
            return count == 1;
        }
    }

    private static string? Text(JsonElement root, string kind, List<string> errs)
    {
        if (!root.TryGetProperty("text", out var t) || t.ValueKind != JsonValueKind.String)
        {
            errs.Add($"'text' is required for effect '{kind}' and must be a string");
            return null;
        }
        return t.GetString();
    }

    private static Effect? Decide(JsonElement root, List<string> errs)
    {
        Verdict? verdict = null;
        if (!root.TryGetProperty("verdict", out var v) || v.ValueKind != JsonValueKind.String)
            errs.Add("'verdict' is required for effect 'decide' and must be a string");
        else
            verdict = v.GetString() switch
            {
                // Exact lowercase, mirroring the Verdict enum the way the
                // ADR-0005 policy dialect does. "Deny"/"DENY" are violations —
                // the codec never guesses at intent.
                "allow" => Verdict.Allow,
                "deny" => Verdict.Deny,
                "ask" => Verdict.Ask,
                _ => null,
            };
        if (root.TryGetProperty("verdict", out var v2) && v2.ValueKind == JsonValueKind.String && verdict is null)
            errs.Add($"'verdict' must be \"allow\", \"deny\", or \"ask\" (got {v2.GetRawText()})");

        string? reason = null;
        if (root.TryGetProperty("reason", out var r))
        {
            if (r.ValueKind != JsonValueKind.String)
            {
                // Explicit null included: absent means "no reason"; null is noise.
                errs.Add($"'reason' must be a string when present (got {r.ValueKind})");
                return null;
            }
            reason = r.GetString();
        }

        return verdict is { } ok ? new Effect.Decide(ok, reason) : null;
    }
}
