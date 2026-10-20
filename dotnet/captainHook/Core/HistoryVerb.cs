using System.Globalization;
using System.Text;
using System.Text.Json;
using CaptainHook.Wire;

namespace CaptainHook.Core;

// `captainHook history [pattern]`: search the shell commands agents ran, as
// the trail recorded them (`tool.shell`, ShellTrail). Oldest first, like a
// shell history; `--limit N` keeps the N most recent matches.
//
// It sees exactly what the trail still holds: commands from the first build
// that records them (0.1.3), for as long as the trail keeps them. The durable
// long-term record is agent-shell-history's file; this is the searchable,
// structured window over the recent past (session, cwd, time, harness).
//
// The trail is streamed, never loaded: one line in memory at a time (the
// limit's ring buffer holds at most N entries). Human verb: stdout on purpose.
public static class HistoryVerb
{
    internal const string Usage =
        "usage: captainHook history [PATTERN] [--session ID-PREFIX] [--since 2d|3h|30m|ISO-DATE] " +
        "[--cwd DIR] [--harness NAME] [--limit N] [--full] [--json]";

    internal sealed record Options(
        string? Pattern, string? Session, DateTimeOffset? Since, string? Cwd, string? Harness,
        int? Limit, bool Full, bool Json);

    /// `width` is the terminal's column count, or null when stdout is not a
    /// terminal (a pipe gets whole lines: `hh -a` reads this). `trailPath` and
    /// `now` are test seams; production reads the resolved trail and the clock.
    public static int Run(string[] args, TextWriter stdout, TextWriter stderr,
        int? width = null, string? trailPath = null, DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.Now;
        if (Parse(args, at, out var error) is not { } o)
        {
            if (error is not null) stderr.WriteLine($"captainHook history: {error}");
            stderr.WriteLine(Usage);
            return 2;
        }

        var path = trailPath ?? WireJsonl.DefaultLogPath();
        var files = ShellTrail.Files(path);
        if (files.Count == 0)
        {
            stderr.WriteLine($"captainHook history: no trail at {path}");
            return 1;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var recorded = 0;
        var ring = o.Limit is { } n ? new Queue<ShellTrail.Entry>(n) : null;
        var matched = 0;
        foreach (var e in ShellTrail.Read(files))
        {
            recorded++;
            if (!Matches(e, o)) continue;
            matched++;
            if (ring is null) { Write(e, o, stdout, width, home); continue; }
            if (ring.Count == o.Limit) ring.Dequeue();
            ring.Enqueue(e);
        }
        if (ring is not null)
            foreach (var e in ring) Write(e, o, stdout, width, home);

        if (recorded == 0)
            stderr.WriteLine(
                "captainHook history: the trail holds no shell commands yet. Recording began in 0.1.3 (`tool.shell`); " +
                "older commands are only in agent-shell-history's file, if that payload was on.");
        return matched > 0 ? 0 : 1;
    }

    internal static Options? Parse(string[] args, DateTimeOffset now, out string? error)
    {
        error = null;
        string? pattern = null, session = null, cwd = null, harness = null;
        DateTimeOffset? since = null;
        int? limit = null;
        bool full = false, json = false;
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            string? Value()
            {
                if (i + 1 < args.Length) return args[++i];
                return null;
            }
            switch (a)
            {
                case "--full": full = true; break;
                case "--json": json = true; break;
                case "--session":
                    if ((session = Value()) is null) { error = "--session needs a value"; return null; }
                    break;
                case "--cwd":
                    if (Value() is not { } c) { error = "--cwd needs a value"; return null; }
                    cwd = Path.TrimEndingDirectorySeparator(Path.GetFullPath(HomePath.Expand(c)));
                    if (cwd.Length == 0) cwd = "/";
                    break;
                case "--harness":
                    if ((harness = Value()) is null) { error = "--harness needs a value"; return null; }
                    break;
                case "--since":
                    if (Value() is not { } s) { error = "--since needs a value"; return null; }
                    if (ParseSince(s, now) is not { } t) { error = $"--since '{s}': expected 30m, 3h, 2d, 1w or a date"; return null; }
                    since = t;
                    break;
                case "--limit":
                    if (Value() is not { } l || !int.TryParse(l, NumberStyles.None, CultureInfo.InvariantCulture, out var lim) || lim < 1)
                    { error = "--limit needs a positive whole number"; return null; }
                    limit = lim;
                    break;
                case "-h" or "--help":
                    return null;
                default:
                    if (a.StartsWith("--", StringComparison.Ordinal)) { error = $"unknown flag {a}"; return null; }
                    if (pattern is not null) { error = "one PATTERN only (quote it to search for spaces)"; return null; }
                    pattern = a;
                    break;
            }
        }
        return new Options(pattern, session, since, cwd, harness, limit, full, json);
    }

    /// `30m`, `3h`, `2d`, `1w` back from `now`, or an ISO date/time (local
    /// unless it names an offset).
    internal static DateTimeOffset? ParseSince(string s, DateTimeOffset now)
    {
        if (s.Length >= 2 && long.TryParse(s[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var n))
        {
            TimeSpan? span = s[^1] switch
            {
                'm' => TimeSpan.FromMinutes(n),
                'h' => TimeSpan.FromHours(n),
                'd' => TimeSpan.FromDays(n),
                'w' => TimeSpan.FromDays(7 * n),
                _ => null,
            };
            if (span is { } d) return now - d;
        }
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var t)
            ? t : null;
    }

    /// Every filter must hold. The pattern is a plain substring of the WHOLE
    /// command (never the cut display), case-insensitive: a search, not a regex.
    internal static bool Matches(ShellTrail.Entry e, Options o)
    {
        if (o.Since is { } since && e.Ts < since) return false;
        if (o.Session is { } s && !(e.SessionId ?? "").StartsWith(s, StringComparison.OrdinalIgnoreCase)) return false;
        if (o.Harness is { } h && !string.Equals(e.Harness, h, StringComparison.OrdinalIgnoreCase)) return false;
        if (o.Cwd is { } c && !UnderDir(e.Cwd, c)) return false;
        if (o.Pattern is { } p && !e.Command.Contains(p, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    /// Whether `cwd` is `dir` or below it, by path component: `--cwd ~/proj`
    /// matches ~/proj/src, never ~/project.
    internal static bool UnderDir(string? cwd, string dir)
    {
        if (cwd is null) return false;
        var trimmed = Path.TrimEndingDirectorySeparator(cwd);
        if (dir == "/") return trimmed.StartsWith('/');
        return trimmed == dir || trimmed.StartsWith(dir + "/", StringComparison.Ordinal);
    }

    private static void Write(ShellTrail.Entry e, Options o, TextWriter stdout, int? width, string home)
    {
        if (o.Json)
        {
            stdout.WriteLine(JsonSerializer.Serialize(new Dictionary<string, string?>
            {
                ["ts"] = e.Ts.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
                ["sessionId"] = e.SessionId,
                ["cwd"] = e.Cwd,
                ["harness"] = e.Harness,
                ["tool"] = e.Tool,
                ["command"] = e.Command,
                ["dispatchId"] = e.DispatchId,
            }));
            return;
        }

        var prefix = Prefix(e, home);
        if (o.Full)
        {
            // Multi-line as multi-line: continuation lines line up under the
            // command's first line.
            var lines = e.Command.Replace("\r\n", "\n").Split('\n');
            stdout.WriteLine(prefix + lines[0]);
            var pad = new string(' ', prefix.Length);
            foreach (var l in lines.Skip(1)) stdout.WriteLine(pad + l);
            return;
        }
        stdout.WriteLine(Cut(prefix + Flatten(e.Command), width));
    }

    /// `time  session8  cwd  ` — local time to the minute, the session id's
    /// first 8 characters (the same short form agent-shell-history tags with),
    /// the cwd with the home directory as `~`.
    internal static string Prefix(ShellTrail.Entry e, string home)
    {
        var time = e.Ts.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        var session = e.SessionId is { Length: > 0 } s ? (s.Length > 8 ? s[..8] : s).PadRight(8) : "-".PadRight(8);
        var cwd = e.Cwd ?? "-";
        if (home.Length > 1 && (cwd == home || cwd.StartsWith(home + "/", StringComparison.Ordinal)))
            cwd = "~" + cwd[home.Length..];
        return $"{time}  {session}  {cwd}  ";
    }

    /// One line for one command: newlines become " ⏎ " (agent-shell-history's
    /// marker), other control characters a space.
    internal static string Flatten(string command)
    {
        var sb = new StringBuilder(command.Length);
        var t = command.Trim().Replace("\r\n", "\n");
        foreach (var ch in t)
        {
            if (ch == '\n') sb.Append(" ⏎ ");
            else if (char.IsControl(ch)) sb.Append(' ');
            else sb.Append(ch);
        }
        return sb.ToString();
    }

    /// Cut to `width` columns with an ellipsis; null width (not a terminal)
    /// cuts nothing. Never splits a surrogate pair.
    internal static string Cut(string line, int? width)
    {
        if (width is not { } w || w < 2 || line.Length <= w) return line;
        var keep = w - 1;
        if (char.IsHighSurrogate(line[keep - 1])) keep--;
        return line[..keep] + "…";
    }
}
