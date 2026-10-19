using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CaptainHook.Core;

namespace CaptainHook.Install;

// `captainHook install-hooks` (ADR-0020 d4): wire captAInHook into a harness's
// hook config WITH CONSENT. The harness spec already says how (ADR-0003's
// `install` block: configFile, hooksPath, entry template) — this verb is the
// first thing that reads it. The default run is a DRY RUN: it prints the exact
// unified diff it would make and writes nothing. `--apply` writes that diff
// after a timestamped byte-for-byte backup. Nothing is ever written silently.
//
// Ownership: the verb edits ONLY entries it recognizes as captAInHook's own —
// a hook command whose program is captainShim / captainHook (or the ancient
// `dotnet …captainHook.dll`) followed by `hook <event>`. Everything else in
// the file (other tools' hooks, permissions, plugins) is carried through
// untouched, in order. Re-running converges: same input ⇒ "up to date".
//
// Event set: an explicit --events list wins; else the events the file ALREADY
// wires to captAInHook (an upgrade repoints, it never widens what you chose);
// else, on a first install, every event the harness spec declares.
public static class InstallHooksVerb
{
    public sealed record Options(
        bool Apply, bool Remove, string Harness, string? SettingsPath,
        string? ShimPath, IReadOnlyList<string>? Events);

    internal const string Usage =
        "usage: captainHook install-hooks [--apply] [--remove] [--events a,b] "
        + "[--shim <path>] [--settings <path>] [--harness <name>]";

    /// Parse the verb's own flags (everything after `install-hooks`).
    internal static Options? ParseArgs(string[] args, TextWriter stderr)
    {
        bool apply = false, remove = false;
        string harness = "claude-code";
        string? settings = null, shim = null;
        List<string>? events = null;
        for (int i = 0; i < args.Length; i++)
        {
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (args[i])
            {
                case "--apply": apply = true; break;
                case "--remove": remove = true; break;
                case "--harness": harness = Next() ?? ""; break;
                case "--settings": settings = Next(); break;
                case "--shim": shim = Next(); break;
                case "--events":
                    events = (Next() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(Harness.Canon).Distinct(StringComparer.Ordinal).ToList();
                    break;
                default:
                    stderr.WriteLine($"captainHook install-hooks: unknown argument '{args[i]}'");
                    stderr.WriteLine(Usage);
                    return null;
            }
        }
        if (harness.Length == 0 || (events is { Count: 0 }) || args.Contains("--settings") && settings is null
            || args.Contains("--shim") && shim is null)
        {
            stderr.WriteLine(Usage);
            return null;
        }
        return new(apply, remove, harness, settings, shim, events);
    }

    /// `registry`, `defaultShim` and `now` are test seams; production passes none.
    public static int Run(string[] args, TextWriter stdout, TextWriter stderr,
                          HarnessRegistry? registry = null, string? defaultShim = null,
                          Func<DateTime>? now = null)
    {
        var opt = ParseArgs(args, stderr);
        if (opt is null) return 1;

        HarnessSpec spec;
        try { spec = (registry ?? new HarnessRegistry()).Get(opt.Harness); }
        catch (InvalidOperationException ex) { stderr.WriteLine($"captainHook install-hooks: {ex.Message}"); return 1; }

        if (InstallSpec.TryFrom(spec) is not { } install)
        {
            stderr.WriteLine($"captainHook install-hooks: harness '{spec.Name}' declares no usable 'install' block — nothing to wire");
            return 1;
        }

        var settingsPath = ExpandHome(opt.SettingsPath ?? install.ConfigFile);
        var shim = opt.ShimPath ?? defaultShim ?? DefaultShimPath(stdout);
        var shimExists = File.Exists(shim);

        // Read. An absent file is an empty object (a first install creates it);
        // an unparseable one is refused — never "fix" a file we can't read.
        string original = "";
        JsonObject root;
        var exists = File.Exists(settingsPath);
        if (exists)
        {
            original = File.ReadAllText(settingsPath);
            try
            {
                root = JsonNode.Parse(original, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Disallow }) as JsonObject
                       ?? throw new JsonException("top level is not a JSON object");
            }
            catch (JsonException ex)
            {
                stderr.WriteLine($"captainHook install-hooks: {settingsPath} is not valid JSON ({ex.Message}) — refusing to touch it");
                return 1;
            }
        }
        else root = new JsonObject();

        var existingEvents = OwnedEvents(root);
        IReadOnlyList<string> events;
        string why;
        if (opt.Remove) { events = []; why = "removing every captAInHook entry"; }
        else if (opt.Events is not null) { events = opt.Events; why = "as given by --events"; }
        else if (existingEvents.Count > 0) { events = existingEvents; why = "the events you already wire to captAInHook"; }
        else { events = spec.Events.Keys.ToList(); why = $"every event the '{spec.Name}' harness declares (first install)"; }

        var unknown = events.Where(e => !spec.Events.ContainsKey(e)).ToList();
        if (unknown.Count > 0)
        {
            stderr.WriteLine($"captainHook install-hooks: unknown event(s) for '{spec.Name}': {string.Join(", ", unknown)} "
                             + $"— known: {string.Join(", ", spec.Events.Keys)}");
            return 1;
        }

        Converge(root, install, events, shim);
        var updated = Serialize(root, trailingNewline: !exists || original.EndsWith('\n'));

        stdout.WriteLine($"captainHook install-hooks: {settingsPath}");
        if (!opt.Remove)
        {
            stdout.WriteLine($"  hook command  {install.Render(shim, "<event>")}");
            stdout.WriteLine($"  events        {(events.Count == 0 ? "(none)" : string.Join(", ", events))}  — {why}");
            var notWired = spec.Events.Keys.Except(events).ToList();
            if (notWired.Count > 0 && opt.Events is null)
                stdout.WriteLine($"  not wired     {string.Join(", ", notWired)}  (add them with --events)");
            if (!shimExists)
                stdout.WriteLine($"  ⚠ {shim} does not exist — every hook would fail to start");
        }

        if (updated == original)
        {
            stdout.WriteLine("  up to date — nothing to change");
            return 0;
        }

        // The serializer writes 2-space JSON (Claude Code's own shape). A file
        // in any other layout would be re-indented wholesale — say so, so a big
        // diff is never a surprise; the diff itself shows every line.
        if (exists && Serialize(JsonNode.Parse(original)!.AsObject(), original.EndsWith('\n')) != original)
            stdout.WriteLine("  note: this file is not in 2-space JSON form; writing also normalizes its layout (see the diff)");

        stdout.Write(UnifiedDiff.Render(original, updated, "a/" + Path.GetFileName(settingsPath), "b/" + Path.GetFileName(settingsPath)));

        if (!opt.Apply)
        {
            stdout.WriteLine("dry run — nothing written. Re-run with --apply to write it (a timestamped backup is made first).");
            return 0;
        }
        if (!opt.Remove && !shimExists)
        {
            stderr.WriteLine($"captainHook install-hooks: refusing to --apply: {shim} does not exist (pass --shim <path>)");
            return 1;
        }

        var target = exists ? ResolveLinks(settingsPath) : settingsPath;
        string? backup = null;
        if (exists)
        {
            backup = $"{target}.bak-captainhook-{(now ?? (() => DateTime.Now))():yyyyMMdd-HHmmss}";
            File.Copy(target, backup, overwrite: false);
        }
        WriteAtomically(target, updated);
        stdout.WriteLine(backup is null
            ? $"wrote {target} (new file)"
            : $"wrote {target} — backup of the previous bytes: {backup}");
        return 0;
    }

    // ---- ownership -------------------------------------------------------

    // A captAInHook hook command: [quote][dir/]captainShim|captainHook[quote]
    // hook <event>, or the pre-apphost `dotnet …captainHook.dll hook <event>`.
    private static readonly Regex Ours = new(
        @"^\s*(?:(?:[^'""\s]*/)?(?:captainShim|captainHook)|'(?:[^']*/)?(?:captainShim|captainHook)'|""(?:[^""]*/)?(?:captainShim|captainHook)""|\S*dotnet\s+['""]?\S*captainHook\.dll['""]?)\s+hook\s+\S+",
        RegexOptions.CultureInvariant);

    internal static bool IsOurs(JsonNode? hook) =>
        hook is JsonObject o && o["command"] is JsonValue v && v.TryGetValue<string>(out var cmd) && Ours.IsMatch(cmd);

    /// Events (hooks keys, canonical) that already carry a captAInHook entry.
    internal static List<string> OwnedEvents(JsonObject root)
    {
        var found = new List<string>();
        if (root["hooks"] is not JsonObject hooks) return found;
        foreach (var (ev, groups) in hooks)
            if (groups is JsonArray arr && arr.OfType<JsonObject>().Any(g => g["hooks"] is JsonArray hs && hs.Any(IsOurs)))
                found.Add(Harness.Canon(ev));
        return found;
    }

    /// Make `root`'s hooks carry exactly one captAInHook entry per wanted event
    /// (repointed in place when one exists, so its position among other tools'
    /// hooks is kept), and none anywhere else. Groups and event arrays that
    /// become empty BECAUSE we removed our entry go too; nothing else changes.
    internal static void Converge(JsonObject root, InstallSpec install, IReadOnlyList<string> events, string shim)
    {
        var hooks = root["hooks"] as JsonObject;
        if (hooks is null)
        {
            if (events.Count == 0) return;
            hooks = new JsonObject();
            root["hooks"] = hooks;
        }

        foreach (var (key, groupsNode) in hooks.ToList())
        {
            if (groupsNode is not JsonArray groups) continue;
            var ev = Harness.Canon(key);
            var want = events.Contains(ev);
            var kept = false;
            foreach (var g in groups.OfType<JsonObject>().ToList())
            {
                if (g["hooks"] is not JsonArray hs) continue;
                var touched = false;
                foreach (var h in hs.ToList())
                {
                    if (!IsOurs(h)) continue;
                    touched = true;
                    if (want && !kept)
                    {
                        var cmd = install.Render(shim, ev);
                        if (h!["command"]!.GetValue<string>() != cmd) h["command"] = cmd;
                        kept = true;
                    }
                    else hs.Remove(h);
                }
                if (touched && hs.Count == 0) groups.Remove(g);
            }
            if (want && !kept)
            {
                groups.Add(NewGroup(install, shim, ev));
                kept = true;
            }
            if (groups.Count == 0) hooks.Remove(key);
        }

        // Wanted events with no key yet: append in the spec's declared order.
        foreach (var ev in events)
            if (!hooks.Any(kv => Harness.Canon(kv.Key) == ev))
                hooks[install.HooksKey(ev)] = new JsonArray(NewGroup(install, shim, ev));

        if (hooks.Count == 0) root.Remove("hooks");
    }

    private static JsonObject NewGroup(InstallSpec install, string shim, string ev)
    {
        var entry = new JsonObject();
        foreach (var (k, v) in install.Entry)
            entry[k] = k == "command" ? install.Render(shim, ev) : v?.DeepClone();
        return new JsonObject { ["hooks"] = new JsonArray(entry) };
    }

    // ---- io --------------------------------------------------------------

    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true, IndentSize = 2, NewLine = "\n",
        // Claude Code writes non-ASCII as-is (JSON.stringify); the default
        // encoder would rewrite every — and é to \uXXXX, a diff on lines we
        // never touched.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    internal static string Serialize(JsonObject root, bool trailingNewline) =>
        root.ToJsonString(Pretty) + (trailingNewline ? "\n" : "");

    /// A dotfiles-managed settings.json is often a symlink: write through to
    /// the real file, so the link (and the repo behind it) stays intact.
    private static string ResolveLinks(string path) =>
        File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? path;

    private static void WriteAtomically(string path, string content)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, $".{Path.GetFileName(path)}.captainhook-{Environment.ProcessId}.tmp");
        File.WriteAllText(tmp, content);
        if (!OperatingSystem.IsWindows() && File.Exists(path))
            File.SetUnixFileMode(tmp, File.GetUnixFileMode(path));   // keep the user's mode
        File.Move(tmp, path, overwrite: true);                     // rename(2): readers see old or new, never half
    }

    internal static string ExpandHome(string path) =>
        path == "~" || path.StartsWith("~/")
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.Length > 2 ? path[2..] : "")
            : path;

    /// The hook command target: the STABLE link an installer maintains
    /// ($CAPTAINHOOK_BIN_DIR, else ~/.local/bin) — so an upgrade never edits
    /// settings.json — when it exists; else the shim beside this engine (a
    /// dev tree, or a hand-staged directory), which is named as such.
    internal static string DefaultShimPath(TextWriter stdout)
    {
        var binDir = Environment.GetEnvironmentVariable("CAPTAINHOOK_BIN_DIR");
        if (string.IsNullOrEmpty(binDir))
            binDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin");
        var stable = Path.Combine(binDir, "captainShim");
        if (File.Exists(stable)) return stable;
        var colocated = Path.Combine(AppContext.BaseDirectory, "captainShim");
        stdout.WriteLine($"  note: no {stable}; using the shim beside this engine ({colocated}) — pass --shim to choose");
        return colocated;
    }
}

/// The harness spec's `install` block, validated into the closed vocabulary
/// this verb understands. Data selects, code renders (ADR-0003): the only
/// placeholders are {captainShim}, {event} and {event-kebab}, and the only
/// hooks path shape is `hooks.{event}` — anything else is refused, never
/// interpreted.
public sealed record InstallSpec(string ConfigFile, JsonObject Entry, string CommandTemplate)
{
    public static InstallSpec? TryFrom(HarnessSpec spec)
    {
        if (spec.Install.ValueKind != JsonValueKind.Object) return null;
        var node = JsonNode.Parse(spec.Install.GetRawText())!.AsObject();
        if (node["configFile"]?.GetValue<string>() is not { Length: > 0 } cfg) return null;
        if (node["hooksPath"]?.GetValue<string>() != "hooks.{event}") return null;
        if (node["entry"] is not JsonObject entry) return null;
        if (entry["command"]?.GetValue<string>() is not { } tpl || !tpl.Contains("{captainShim}")) return null;
        var stripped = tpl.Replace("{captainShim}", "").Replace("{event-kebab}", "").Replace("{event}", "");
        if (stripped.Contains('{') || stripped.Contains('}')) return null;   // an unknown placeholder
        return new(cfg, entry, tpl);
    }

    /// The hooks object's key for an event — the spec's canonical name.
    public string HooksKey(string ev) => ev;

    public string Render(string shim, string ev) =>
        CommandTemplate
            .Replace("{captainShim}", ShellQuote(shim))
            .Replace("{event-kebab}", ev == "<event>" ? ev : Kebab(ev))
            .Replace("{event}", ev);

    internal static string Kebab(string pascal) =>
        Regex.Replace(pascal, "(?<=[a-z0-9])([A-Z])", "-$1").ToLowerInvariant();

    /// The host runs the command through a shell: a path with anything outside
    /// a conservative set gets single-quoted.
    internal static string ShellQuote(string s) =>
        Regex.IsMatch(s, @"^[A-Za-z0-9/._+~-]+$") ? s : "'" + s.Replace("'", "'\\''") + "'";
}
