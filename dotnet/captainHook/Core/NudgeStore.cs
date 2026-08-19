using System.Text;
using System.Text.Json;
using CaptainHook.Actors;
using CaptainHook.Mail;

namespace CaptainHook.Core;

// Roadmap item 22 / ADR-0017 decision 4, slice `nudge-state-and-trail` — the
// brain's MEMORY on disk, and the one trail row that says a nudge happened.
//
// The brain (`WatcherBrain`) is pure: it is handed a `NudgeState` and hands one
// back. This file is the only place that state touches a file, and the only
// place a `mail.nudge` line is written — the two halves of "a nudge really
// happened", kept together so a charged budget and the record of it can never
// disagree (`Record`).
//
// **What is stored is AGES, never stamps.** `NudgeState`'s numbers are
// monotonic milliseconds of the process that made them, and a monotonic epoch
// does not survive a restart — so the state leaves as durations measured from
// the moment it was written (`NudgeState.ToAges`) and comes back as stamps
// re-derived from the moment it was read (`FromAges`). Two subtractions on one
// clock each; no `DateTime` anywhere near it (house invariant 2). The
// consequence the brain already states: TIME THE DAEMON WAS NOT RUNNING IS NOT
// COUNTED, which is the conservative direction — fewer nudges, later.
//
// **The file is JSONL and APPEND-ONLY, and that is the concurrency story.**
// Each save appends ONE line holding the whole state; a read takes the LAST
// line that parses. There is no lock, because there is exactly one writer by
// design (the daemon's watcher actor — `mail watch --once` is dry and writes
// nothing). What a lock would otherwise buy is bought by the reader instead:
// an append interrupted by a crash, or two daemons of different builds
// interleaving one, can only ever leave a line that does not parse, and a line
// that does not parse is SKIPPED. Skipping the tail costs the last save (the
// state one evaluation older, whose ages under-count elapsed quiet — fewer
// nudges again); skipping everything is a REANCHOR: `NudgeState.Empty`, every
// unread envelope first seen now, every quiet clock restarted. Nothing about a
// lost state file loses mail, spends a budget, or wakes anybody early.
//
// The file cannot grow without bound: past `CompactAtBytes` a save rewrites it
// to the single current line through a sibling temp + same-dir rename
// (`MailCursors.WriteAtomic`'s idiom), which is atomic, so a compaction that
// dies leaves the appended file exactly as it was.
//
// **Why a version is required rather than tolerated.** A line whose `v` this
// build does not know is skipped, which means an older daemon reading a newer
// daemon's state reanchors once — the honest reading of "I cannot tell what
// this says", and cheap, because the whole cost of a reanchor is a quiet
// period. The same strictness applies within a version: an unknown member or a
// wrong type rejects the LINE, never the file.
public sealed class NudgeStore
{
    /// Beside the ledger and the cursors (`~/.captainHook/mail/`): the brain's
    /// memory is about mail, and the mail dir is the one place a sandbox
    /// (a test, the e2e) redirects to move the whole subsystem at once.
    public const string FileName = "nudges.jsonl";

    /// The line format's identity. Bump it when a member's MEANING changes;
    /// adding one already rejects an older reader's line by the strict walk.
    public const int Version = 1;

    /// Past this, a save rewrites instead of appending. Generous — the state is
    /// a few hundred bytes per evaluation — but bounded, because the watcher
    /// evaluates on every `mail.append` and `mail.cursorAdvance` for as long as
    /// the daemon lives.
    public const long CompactAtBytes = 256 * 1024;

    private const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite;   // 0600
    private const UnixFileMode PrivateDir = Private | UnixFileMode.UserExecute;            // 0700

    public string Dir { get; }
    public string FilePath { get; }

    public NudgeStore(string mailDir)
    {
        Dir = mailDir;
        FilePath = System.IO.Path.Combine(mailDir, FileName);
    }

    /// The mail dir this process watches — `MailStore.ResolveDir`'s answer, never
    /// a second one: the state and the ledger it is about must never be able to
    /// point at different places.
    public static NudgeStore Resolve(string? mailDir = null) => new(MailStore.ResolveDir(mailDir));

    /// The state as of `nowMs`. Absent file ⇒ `Empty`, silently: nothing has
    /// been watched yet, which is first contact, not a fault. Anything else that
    /// cannot be read is a reanchor, and says so on the trail.
    public NudgeState Load(long nowMs)
    {
        string[] lines;
        try
        {
            if (!File.Exists(FilePath)) return NudgeState.Empty;
            lines = File.ReadAllLines(FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Reanchor("unreadable", $"cannot read '{FilePath}': {ex.Message}", 0);
            return NudgeState.Empty;
        }

        // From the END: the last line that parses is the freshest state anybody
        // finished writing.
        var seen = 0;
        var skipped = 0;
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            if (lines[i].Length == 0) continue;
            seen++;
            if (!TryParseLine(lines[i], out var ages)) { skipped++; continue; }

            if (skipped > 0)
                // Not a reanchor — the state is one save older, and its ages are
                // measured from ITS write, so every clock reads younger than it
                // truly is. Conservative, and worth a line: a torn tail is the
                // shape a crashed or duplicated writer leaves.
                Log.Warn("watch", "watch.stateTorn", new LogFields
                {
                    Msg = "nudge state: the tail did not parse — the last complete line was used",
                    Data = new Dictionary<string, object>
                    {
                        ["path"] = FilePath, ["skipped"] = skipped, ["lines"] = lines.Length,
                    },
                });
            return NudgeState.FromAges(ages, nowMs);
        }

        if (seen > 0) Reanchor("malformed", "no line of the nudge state parsed", seen);
        return NudgeState.Empty;
    }

    /// Persist the state as of `nowMs`. Never throws: a watcher that died
    /// because a disk was full would stop watching, where a watcher that could
    /// not remember merely forgets — the same reanchor `Load` already handles.
    /// Returns whether the bytes landed, for a caller that wants to know.
    public bool Save(NudgeState state, long nowMs)
    {
        var line = Render(state.ToAges(nowMs));
        try
        {
            Directory.CreateDirectory(Dir, PrivateDir);
            // CreateDirectory's mode applies only when it CREATES; an earlier
            // component may have made ~/.captainHook at the umask default.
            try { File.SetUnixFileMode(Dir, PrivateDir); } catch { /* not ours / odd FS */ }

            using (var fs = new FileStream(FilePath, new FileStreamOptions
            {
                Mode = FileMode.Append,
                Access = FileAccess.Write,
                Share = FileShare.ReadWrite,
                UnixCreateMode = Private,   // applies on CREATE, so no window exposes it
            }))
                // ONE write of the whole line: a partial line is what the reader
                // is built to skip, but there is no reason to make them common.
                fs.Write(Encoding.UTF8.GetBytes(line));

            if (new FileInfo(FilePath).Length > CompactAtBytes) Compact(line);
            return true;
        }
        catch (Exception ex)   // permissions, races, device I/O, a read-only tree
        {
            Log.Warn("watch", "watch.stateUnwritable", new LogFields
            {
                Msg = "nudge state not saved — the watcher will re-anchor its clocks on the next start",
                Data = new Dictionary<string, object> { ["path"] = FilePath, ["error"] = ex.Message },
            });
            return false;
        }
    }

    /// **The two things that must happen together when a nudge really
    /// happened**: the trail row that says so, and the charge against the
    /// budgets. `Record` is the CALLER's by the brain's rule 3, and this is the
    /// one spelling of it, so a row and a charge can never disagree about
    /// whether a nudge went out.
    ///
    /// A DENIAL writes no `mail.nudge`. `dispatch.json` refusing a nudge means
    /// nobody was woken, and a mail-side row saying "we poked them" would put a
    /// poke on the picture that never happened; the denial's record is
    /// `nudge.denied`, which `MailNudgeEvent` already writes. The state still
    /// takes it — uncharged, quiet clock restarted — so a refusal recurs once
    /// per quiet period rather than on every evaluation.
    public static NudgeState Record(NudgeState state, MailNudge nudge, MailNudgeOutcome outcome, long nowMs)
    {
        if (outcome.Ran) LogNudge(nudge, outcome);
        return state.Record(nudge, nowMs, charged: outcome.Ran);
    }

    /// `mail.nudge` (ADR-0017 d10) — a nudge is a TRAIL LINE, never an envelope:
    /// mail about mail would recurse, and "I poked them and they still have not
    /// read it" is the trail's kind of fact. The Mail canvas draws it as a mark
    /// on the role's lane.
    ///
    /// Three of the columns d10's prose named are as-built decisions:
    ///
    ///   * NO `channel`. The human channel is a pull — a count `mail status`
    ///     reads off the cursors — so it emits nothing and every line that can
    ///     exist here is a robot nudge. A column with one possible value is a
    ///     fact stated where it can drift instead of derived; a second channel
    ///     that leaves a record is when it earns its place.
    ///   * NO `sessionId`. A nudge belongs to a ROLE and carries no session
    ///     (`MailNudgeEvent` omits it for the same reason); stamping the trail's
    ///     session column here would put a window on the choreography that was
    ///     never involved. `dispatchId` is what joins this row to the
    ///     `dispatch.start → exec.spawn → exec.exit` rows of the woken turn.
    ///   * `budget` is the brain's own arithmetic as NUMBERS
    ///     (`MailNudgeBudget`), not the sentence in `reason` — a reader must
    ///     never have to parse prose to learn what a nudge spent, and the two
    ///     renderings come from one value, so they cannot disagree.
    ///
    /// `address` rides only a dead-mailbox nudge (ADR-0018 d6): the row goes to
    /// the reaper's lane, and the box it is about is the whole fact.
    private static void LogNudge(MailNudge nudge, MailNudgeOutcome outcome)
    {
        var data = new Dictionary<string, object>
        {
            ["role"] = nudge.Role,
            ["envelopeIds"] = nudge.EnvelopeIds.Select(MailEnvelope.ClampField).ToList(),
            ["reason"] = nudge.Reason,
        };
        if (nudge.Budget is { } b)
            data["budget"] = new Dictionary<string, object>
            {
                ["envelope"] = b.Envelope, ["perEnvelope"] = b.PerEnvelope,
                ["roleHour"] = b.RoleHour, ["perRoleHour"] = b.PerRoleHour,
            };
        if (nudge.Address is not null) data["address"] = nudge.Address;
        if (nudge.Workspace is not null) data["workspace"] = nudge.Workspace;

        Log.Info("mail", "mail.nudge", new LogFields
        {
            DispatchId = outcome.DispatchId,
            Msg = nudge.Address is null
                ? "robot nudge raised: a turn was woken for mail this role had not read"
                : "robot nudge raised: the reaper was woken about a dead mailbox",
            Data = data,
        });
    }

    private void Reanchor(string cause, string reason, int lines) =>
        Log.Warn("watch", "watch.stateReanchor", new LogFields
        {
            Msg = "nudge state re-anchored — every unread envelope is first seen now and every quiet clock restarts",
            Data = new Dictionary<string, object>
            {
                ["path"] = FilePath, ["cause"] = cause, ["reason"] = reason, ["lines"] = lines,
            },
        });

    /// Sibling temp + same-dir rename — `MailCursors.WriteAtomic`'s idiom, with
    /// the temp CREATED 0600 rather than mode-fixed after, so no window exposes
    /// it. A compaction that dies leaves the appended file untouched.
    private void Compact(string line)
    {
        var tmp = System.IO.Path.Combine(
            Dir, "." + FileName + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var fs = new FileStream(tmp, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                UnixCreateMode = Private,
            }))
                fs.Write(Encoding.UTF8.GetBytes(line));
            File.Move(tmp, FilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) { try { File.Delete(tmp); } catch { /* best-effort */ } }
        }
    }

    /// One line, terminated. Hand-written like every other line this project
    /// makes durable: the shape is the format, and a serializer's defaults are
    /// not a format decision anybody made.
    internal static string Render(NudgeStateAges ages)
    {
        using var buf = new MemoryStream();
        using (var w = new Utf8JsonWriter(buf))
        {
            w.WriteStartObject();
            w.WriteNumber("v", Version);
            w.WriteStartArray("envelopes");
            foreach (var e in ages.Envelopes)
            {
                w.WriteStartObject();
                w.WriteString("subject", e.Subject);
                w.WriteString("id", e.Id);
                w.WriteNumber("unreadForMs", e.UnreadForMs);
                w.WriteNumber("quietForMs", e.QuietForMs);
                w.WriteNumber("nudged", e.Nudged);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("nudges");
            foreach (var n in ages.Nudges)
            {
                w.WriteStartObject();
                w.WriteString("role", n.Role);
                w.WriteNumber("agoMs", n.AgoMs);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buf.ToArray()) + "\n";
    }

    /// A strict walk, and never a throw: an unknown member, a wrong type, a
    /// version this build does not know, or a lone surrogate that only explodes
    /// at `GetString` all reject the LINE and nothing else.
    internal static bool TryParseLine(string line, out NudgeStateAges ages)
    {
        ages = new NudgeStateAges([], []);
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            var envelopes = new List<WatchedEnvelopeAges>();
            var nudges = new List<RoleNudgeAges>();
            bool sawVersion = false, sawEnvelopes = false, sawNudges = false;

            foreach (var m in root.EnumerateObject())
            {
                switch (m.Name)
                {
                    case "v":
                        if (m.Value.ValueKind != JsonValueKind.Number
                            || !m.Value.TryGetInt32(out var v) || v != Version) return false;
                        sawVersion = true;
                        break;
                    case "envelopes":
                        if (m.Value.ValueKind != JsonValueKind.Array) return false;
                        foreach (var e in m.Value.EnumerateArray())
                        {
                            if (!TryEnvelope(e, out var entry)) return false;
                            envelopes.Add(entry);
                        }
                        sawEnvelopes = true;
                        break;
                    case "nudges":
                        if (m.Value.ValueKind != JsonValueKind.Array) return false;
                        foreach (var n in m.Value.EnumerateArray())
                        {
                            if (!TryNudge(n, out var entry)) return false;
                            nudges.Add(entry);
                        }
                        sawNudges = true;
                        break;
                    default: return false;
                }
            }
            if (!sawVersion || !sawEnvelopes || !sawNudges) return false;
            ages = new NudgeStateAges(envelopes, nudges);
            return true;
        }
        catch (JsonException) { return false; }
        catch (InvalidOperationException) { return false; }   // the deferred-unescape trap
    }

    private static bool TryEnvelope(JsonElement e, out WatchedEnvelopeAges entry)
    {
        entry = new WatchedEnvelopeAges("", "", 0, 0, 0);
        if (e.ValueKind != JsonValueKind.Object) return false;
        string? subject = null, id = null;
        long? unread = null, quiet = null;
        int? nudged = null;
        foreach (var m in e.EnumerateObject())
        {
            switch (m.Name)
            {
                case "subject": subject = Str(m.Value); if (subject is null) return false; break;
                case "id": id = Str(m.Value); if (id is null) return false; break;
                case "unreadForMs": if (!Num(m.Value, out var u)) return false; unread = u; break;
                case "quietForMs": if (!Num(m.Value, out var q)) return false; quiet = q; break;
                case "nudged":
                    if (m.Value.ValueKind != JsonValueKind.Number || !m.Value.TryGetInt32(out var n)) return false;
                    nudged = n; break;
                default: return false;
            }
        }
        if (subject is null || id is null || unread is null || quiet is null || nudged is null) return false;
        entry = new WatchedEnvelopeAges(subject, id, unread.Value, quiet.Value, nudged.Value);
        return true;
    }

    private static bool TryNudge(JsonElement e, out RoleNudgeAges entry)
    {
        entry = new RoleNudgeAges("", 0);
        if (e.ValueKind != JsonValueKind.Object) return false;
        string? role = null;
        long? ago = null;
        foreach (var m in e.EnumerateObject())
        {
            switch (m.Name)
            {
                case "role": role = Str(m.Value); if (role is null) return false; break;
                case "agoMs": if (!Num(m.Value, out var a)) return false; ago = a; break;
                default: return false;
            }
        }
        if (role is null || ago is null) return false;
        entry = new RoleNudgeAges(role, ago.Value);
        return true;
    }

    /// Null means "not a readable string" — `WatchRules.TryReadString`'s guard,
    /// for the same reason: `JsonDocument` defers unescaping, so a lone
    /// surrogate parses fine and throws at `GetString`.
    private static string? Str(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.String) return null;
        try { return e.GetString(); }
        catch (InvalidOperationException) { return null; }
    }

    private static bool Num(JsonElement e, out long value)
    {
        value = 0;
        return e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out value);
    }
}
