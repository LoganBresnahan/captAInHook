using System.Buffers;
using System.Text;
using System.Text.Json;

namespace CaptainHook.Wire;

// The wire lib's JSONL rendering — the SECOND emitter of the trail's one
// schema (ADR-0004 decision 7 amendment). The first is the F# side's
// LogEvent.ToJson() (Logging.fs), which the engine keeps; the AOT captainShim
// binds WireLog.Sink to Render+Append here. The two renderings are pinned to
// IDENTICAL BYTES by the golden test (WireJsonlTests): key order, the
// absent-means-omit rules, durMs rounding, and the default-encoder escaping
// are all part of the schema — change either side and the suite goes red
// until both moved in the same commit.
//
// Rendering is Utf8JsonWriter, imperative and reflection-free: nothing here
// for the AOT analyzers to flag. `data` values are primitives (plus nested
// dict/sequence) BY WIRE CONTRACT — an arbitrary object falls back to
// ToString(), where the reflective F# serializer would expand it; keep rich
// objects out of `data` on wire-lib call sites.

public static class WireJsonl
{
    /// Render one event to a single JSONL line (no trailing newline) —
    /// byte-identical to Logging.fs's LogEvent.ToJson() for the same event.
    public static string Render(WireLogEvent e)
    {
        var buf = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buf))   // default options = default encoder, both sides
        {
            w.WriteStartObject();
            // Same format string as Logging.fs — current culture and all; the
            // golden test runs both under one process so drift cannot hide.
            w.WriteString("ts", e.Ts.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));
            w.WriteString("lvl", e.Lvl);
            w.WriteString("src", e.Src);
            w.WriteString("evt", e.Evt);
            var f = e.Fields;
            if (f.DispatchId is not null) w.WriteString("dispatchId", f.DispatchId);
            if (f.SessionId is not null) w.WriteString("sessionId", f.SessionId);
            if (f.HookEvent is not null) w.WriteString("hookEvent", f.HookEvent);
            if (f.ActorId is not null) w.WriteString("actorId", f.ActorId);
            if (f.DurMs is double d) w.WriteNumber("durMs", Math.Round(d, 3));
            if (f.Msg is not null) w.WriteString("msg", f.Msg);
            if (f.Data is { Count: > 0 } data)
            {
                w.WritePropertyName("data");
                WriteValue(w, data);
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buf.WrittenSpan);
    }

    private static void WriteValue(Utf8JsonWriter w, object? v)
    {
        switch (v)
        {
            case null: w.WriteNullValue(); break;
            case string s: w.WriteStringValue(s); break;
            case bool b: w.WriteBooleanValue(b); break;
            case int i: w.WriteNumberValue(i); break;
            case long l: w.WriteNumberValue(l); break;
            case double d: w.WriteNumberValue(d); break;
            case float fl: w.WriteNumberValue(fl); break;
            case IDictionary<string, object> dict:
                w.WriteStartObject();
                foreach (var kv in dict) { w.WritePropertyName(kv.Key); WriteValue(w, kv.Value); }
                w.WriteEndObject();
                break;
            case System.Collections.IEnumerable seq:
                w.WriteStartArray();
                foreach (var item in seq) WriteValue(w, item);
                w.WriteEndArray();
                break;
            default: w.WriteStringValue(v.ToString()); break;   // wire contract: keep rich objects out of data
        }
    }

    /// The trail's default path — the mirror of Logging.fs's defaultFilePath,
    /// so shim and engine append to the SAME file with no negotiation:
    /// $CAPTAINHOOK_LOG when set, else ~/.captainHook/logs/captainHook.jsonl.
    public static string DefaultLogPath()
    {
        var p = Environment.GetEnvironmentVariable("CAPTAINHOOK_LOG");
        return !string.IsNullOrWhiteSpace(p)
            ? p
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".captainHook", "logs", "captainHook.jsonl");
    }

    /// Append one rendered line. O_APPEND keeps concurrent writers whole
    /// (already the shim/daemon story — ADR-0004 N3); failures are swallowed —
    /// logging must never take the hook down, same contract as the F# sink.
    public static void Append(string path, string line)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.AppendAllText(path, line + Environment.NewLine);
        }
        catch { /* never the hook's problem */ }
    }
}
