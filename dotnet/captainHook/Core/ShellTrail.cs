using System.Text.Json;
using CaptainHook.Actors;
using CaptainHook.Wire;

namespace CaptainHook.Core;

/// The agent shell-command record (ADR-0009 d7, 2026-10-19; its own file
/// since d8, 2026-10-19): one `tool.shell` line per shell command an agent
/// ran, appended to `shell-history.jsonl` beside the trail. That file is what
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
/// Its own file, kept FOREVER (d8, the owner's explicit choice): the trail is
/// days-to-weeks telemetry and will be rotated and capped (roadmap item 32);
/// command history is not, and nothing in the trail's policy ever touches
/// this file. Stated plainly: the command text is kept whole and
/// indefinitely, so a token an agent puts on a command line stays here as it
/// would in a shell history. 0600 in the 0700 logs dir (ADR-0016 d13), written
/// with the trail's own O_APPEND primitive so the shim and the daemon never
/// clobber each other's lines.
///
/// The 0.1.3 window wrote these lines into the trail itself. A daemon copies
/// them once into `shell-history.0.jsonl` (`MigrateLegacy`) so they outlive
/// the trail's rotation; until then history reads them from the trail.
public static class ShellTrail
{
    internal const string Evt = "tool.shell";

    /// The active command-history file: `shell-history.jsonl` beside the
    /// trail (so `CAPTAINHOOK_LOG` moves both, and a sandboxed HOME holds both).
    public static string HistoryPath(string? trailPath = null) =>
        Path.Combine(Path.GetDirectoryName(trailPath ?? WireJsonl.DefaultLogPath()) ?? ".", HistoryFile);

    /// The 0.1.3 window's lines, copied once out of the trail (`MigrateLegacy`).
    public static string LegacyPath(string? trailPath = null) =>
        Path.Combine(Path.GetDirectoryName(trailPath ?? WireJsonl.DefaultLogPath()) ?? ".", LegacyFile);

    internal const string HistoryFile = "shell-history.jsonl";
    internal const string LegacyFile = "shell-history.0.jsonl";

    /// Where a rendered line goes: an O_APPEND write to `HistoryPath()` (dir
    /// 0700, file 0600). Tests swap it (TestInfra) so the suite never writes
    /// the user's real history, exactly as they swap the Log sink.
    internal static Action<string> Sink = line => WireJsonl.Append(HistoryPath(), line);

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
            // The trail's own line shape, so one reader parses both files.
            Sink(new LogEvent(DateTime.UtcNow, "info", "tool", Evt, new LogFields
            {
                DispatchId = dispatchId,
                SessionId = e.SessionId,
                HookEvent = e.Type,
                Data = data,
            }).ToJson());
        }
        catch (Exception) { /* the record is best-effort; the hook is not */ }
    }

    /// Copy the trail's `tool.shell` lines (the 0.1.3 window, before d8 gave
    /// them their own file) into `shell-history.0.jsonl`, once. Written to a
    /// temp file and renamed into place, so a reader sees all of it or none,
    /// and a crash mid-copy leaves nothing behind but a temp the next run
    /// replaces. Two daemons racing (a deploy handoff) both write the same
    /// lines and the second rename wins harmlessly. An empty file is written
    /// when the trail holds none, so later starts skip the scan. Returns the
    /// lines copied, or -1 when the file already existed. A daemon calls this
    /// in the background at start; history never writes.
    public static int MigrateLegacy(string trailPath)
    {
        var target = LegacyPath(trailPath);
        if (File.Exists(target)) return -1;
        var dir = Path.GetDirectoryName(target) ?? ".";
        // Unique per call: the daemon's start-time copy and the keeper's guard
        // (ADR-0021 rule 4) can run at once in one process.
        var tmp = Path.Combine(dir, $".{LegacyFile}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        const string marker = "\"evt\":\"" + Evt + "\"";
        var copied = 0;
        Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using (var fs = new FileStream(tmp, new FileStreamOptions
        {
            Mode = FileMode.Create, Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        }))
        using (var w = new StreamWriter(fs, new System.Text.UTF8Encoding(false)) { NewLine = "\n" })
        {
            foreach (var file in Files(trailPath))
            {
                IEnumerable<string> lines;
                try { lines = File.ReadLines(file); }
                catch (Exception) { continue; }
                foreach (var line in lines)
                {
                    if (!line.Contains(marker, StringComparison.Ordinal) || Parse(line) is null) continue;
                    w.WriteLine(line);
                    copied++;
                }
            }
        }
        File.Move(tmp, target, overwrite: true);
        return copied;
    }

    /// The files history reads, oldest first: the 0.1.3 window (the migrated
    /// copy, or the trail itself until a daemon has made it), then the active
    /// command-history file.
    public static IReadOnlyList<string> HistoryFiles(string trailPath)
    {
        var files = new List<string>();
        var legacy = LegacyPath(trailPath);
        if (File.Exists(legacy)) files.Add(legacy);
        else files.AddRange(Files(trailPath));
        var active = HistoryPath(trailPath);
        if (File.Exists(active)) files.Add(active);
        return files;
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

    /// Every file that may hold the TRAIL, oldest first: ADR-0009 d3's
    /// numbered segments (`<stem>.<seq>.jsonl`, seq ascending) and then the
    /// active file (TrailKeeper's segment model, ADR-0021). Only the migration
    /// and history's pre-migration fallback read the trail now.
    public static IReadOnlyList<string> Files(string activePath)
    {
        var files = TrailKeeper.Segments(activePath).Select(s => s.Path).ToList();
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
