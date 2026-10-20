using System.Text.Json;
using CaptainHook.Actors;

namespace CaptainHook.Core;

/// The agent shell-command record (ADR-0009 d7, 2026-10-19): one
/// `tool.shell` trail line per shell command an agent ran, which is what
/// `captainHook history` searches.
///
/// WHAT counts as a shell command is read off the call's CONTENT: a
/// PostToolUse whose `tool_input.command` is a string. Never the tool's
/// name: Claude Code calls its shell `Bash`, moby calls it `run_shell`, and a
/// filter keyed on one name reads the other host's shell as not-a-shell
/// (ADR-0019's incident). PostToolUse only, the same line agent-shell-history
/// draws: a command that was denied or interrupted never ran, so it is not
/// history; one that ran and failed is.
///
/// Recorded at BOTH wire sites (the collapsed run and the daemon), before the
/// dispatch-policy gate: the policy decides which handlers see an event, and
/// a command that ran is a fact whatever handlers are allowed to say about it
/// (the same reasoning that stamps session presence before the gate).
///
/// What this puts in the trail, stated rather than engineered around: the
/// command text, whole. A token an agent puts on a command line lands here
/// exactly as it lands in a shell history; the trail is 0600 in a 0700 dir
/// (ADR-0016 d13). And what history can see is exactly what the trail still
/// holds: commands from the first build that records them, for as long as
/// the trail keeps them. The durable long-term record stays agent-shell-
/// history's file.
public static class ShellTrail
{
    internal const string Evt = "tool.shell";

    /// Emit the `tool.shell` line for `e` if it is a shell command that ran;
    /// otherwise do nothing. Never throws: a hook must not fail because its
    /// record could not be written.
    public static void Record(HookEvent e, string? dispatchId)
    {
        try
        {
            if (Command(e) is not { } command) return;
            var data = new Dictionary<string, object> { ["command"] = command };
            if (e.Cwd is { } cwd) data["cwd"] = cwd;
            if (e.Harness is { } harness) data["harness"] = harness;
            // The host's own name for the tool, kept as data for display:
            // recognition never reads it.
            if (e.Payload.TryGetProperty("tool_name", out var tn) && tn.ValueKind == JsonValueKind.String)
                data["tool"] = tn.GetString()!;
            Log.Info("tool", Evt, new LogFields
            {
                DispatchId = dispatchId,
                SessionId = e.SessionId,
                HookEvent = e.Type,
                Data = data,
            });
        }
        catch (Exception) { /* the record is best-effort; the hook is not */ }
    }

    /// The command a PostToolUse ran, or null when `e` is not a shell call.
    internal static string? Command(HookEvent e)
    {
        if (!string.Equals(e.Type, "PostToolUse", StringComparison.Ordinal)) return null;
        if (e.Payload.ValueKind != JsonValueKind.Object) return null;
        if (!e.Payload.TryGetProperty("tool_input", out var input) || input.ValueKind != JsonValueKind.Object) return null;
        if (!input.TryGetProperty("command", out var c) || c.ValueKind != JsonValueKind.String) return null;
        var command = c.GetString();
        return string.IsNullOrWhiteSpace(command) ? null : command;
    }

    /// One recorded command, as history reads it back.
    public sealed record Entry(
        DateTimeOffset Ts, string? SessionId, string? Cwd, string? Harness, string? Tool,
        string Command, string? DispatchId);

    /// Every file that may hold the trail, oldest first: ADR-0009 d3's
    /// numbered segments (`<stem>.<seq>.jsonl`, seq ascending) and then the
    /// active file. Segmentation is designed but not built, so today this is
    /// the active file alone; reading the segments now means history needs no
    /// change on the day rotation lands.
    public static IReadOnlyList<string> Files(string activePath)
    {
        var files = new List<string>();
        var dir = Path.GetDirectoryName(activePath);
        var stem = Path.GetFileNameWithoutExtension(activePath);
        var ext = Path.GetExtension(activePath);
        if (dir is not null && Directory.Exists(dir))
        {
            var segments = new List<(long Seq, string Path)>();
            foreach (var f in Directory.EnumerateFiles(dir, $"{stem}.*{ext}"))
            {
                var middle = Path.GetFileName(f)[(stem.Length + 1)..^ext.Length];
                if (long.TryParse(middle, System.Globalization.NumberStyles.None, null, out var seq))
                    segments.Add((seq, f));
            }
            files.AddRange(segments.OrderBy(s => s.Seq).Select(s => s.Path));
        }
        if (File.Exists(activePath)) files.Add(activePath);
        return files;
    }

    /// Stream every `tool.shell` entry from `files`, in file order. One line
    /// in memory at a time: the trail is tens of megabytes and growing. A line
    /// is parsed only if it names the event (a substring test first), and an
    /// unparseable line is skipped: a torn final line from a writer killed
    /// mid-append is expected, not an error.
    public static IEnumerable<Entry> Read(IEnumerable<string> files)
    {
        const string marker = "\"evt\":\"" + Evt + "\"";
        foreach (var file in files)
        {
            IEnumerable<string> lines;
            try { lines = File.ReadLines(file); }
            catch (Exception) { continue; }
            using var it = lines.GetEnumerator();
            while (true)
            {
                string line;
                try
                {
                    if (!it.MoveNext()) break;
                    line = it.Current;
                }
                catch (IOException) { break; }
                if (!line.Contains(marker, StringComparison.Ordinal)) continue;
                if (Parse(line) is { } entry) yield return entry;
            }
        }
    }

    internal static Entry? Parse(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (Str(root, "evt") != Evt) return null;
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return null;
            if (Str(data, "command") is not { } command) return null;
            if (!DateTimeOffset.TryParse(Str(root, "ts"), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var ts)) return null;
            return new Entry(ts, Str(root, "sessionId"), Str(data, "cwd"), Str(data, "harness"),
                Str(data, "tool"), command, Str(root, "dispatchId"));
        }
        catch (JsonException) { return null; }
    }

    private static string? Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
