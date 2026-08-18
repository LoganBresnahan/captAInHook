using CaptainHook.Actors;
using CaptainHook.Core;

namespace CaptainHook.Mail;

// watcher-brain (ADR-0017 d4, phase 3) — the CLI half: `captainHook mail watch
// --once` runs the brain over the real store, cursors, registrations and rules
// ONCE and prints what it decided. It is verification, not a schedule (the ADR
// rejected cron by name): nothing here loops, nothing here sleeps, and nothing
// here dispatches. The in-daemon actor (`watcher-actor`) is what runs the same
// brain for real, fed by the trail and armed through the actor layer.
//
// Three things this verb cannot see, and says so instead of guessing:
//
//   * PRESENCE is the daemon's own view (`SessionPresence`, in-process). A CLI
//     process can honestly claim one thing: the window that invoked it — the
//     `session_id` on its stdin — is here right now, age 0. That is what the
//     verdict is computed from, and the report names it. Behind a hook (a
//     registration on UserPromptSubmit, say) that is the calling window; on a
//     bare terminal it is nobody.
//   * STATE. The brain's memory (`nudges.jsonl`) is `nudge-state-and-trail`'s
//     and does not exist yet, so every unread envelope is first seen NOW and no
//     `quietFor` threshold has been crossed. `--as-if-quiet` evaluates as though
//     every threshold already had — the operator's way to see what their rules
//     WOULD do once time has passed, without waiting for it.
//   * The dispatcher. A dry verdict costs nothing and spends nothing; the nudge
//     it prints is the value the actor would hand `MailNudgeEvent.DispatchAsync`.
//
// The output is a human's, on stdout, and one `watch.verdict` trail line — so a
// window that wires this behind a hook for a dogfood week leaves a record of
// what the brain would have done, and the field report can be written from it.
public static class MailWatch
{
    public const string Usage =
        "usage: captainHook mail watch --once [--as-if-quiet]   "
        + "(hook-shaped JSON or an exec-wire envelope on stdin names the live session; verification only — the daemon's watcher is the schedule)";

    /// How long `--as-if-quiet` pretends every unread envelope has been quiet:
    /// the largest `quietFor` the parser accepts, so "past every threshold" is
    /// literally true for any rule that loaded.
    public const long AsIfQuietMs = int.MaxValue;

    public static int Run(
        IReadOnlyList<string> argv, TextReader stdin, TextWriter stdout, TextWriter stderr,
        string? mailDir = null, string? handlersPath = null, string? watchPath = null,
        long? nowMs = null)
    {
        var once = false;
        var asIfQuiet = false;
        foreach (var arg in argv)
        {
            switch (arg)
            {
                case "--once": once = true; break;
                case "--as-if-quiet": asIfQuiet = true; break;
                default:
                    stderr.WriteLine($"captainHook mail watch: unexpected argument '{arg}'");
                    stderr.WriteLine(Usage);
                    return 1;
            }
        }
        if (!once)
        {
            // Refused rather than defaulted: a `mail watch` that ran without
            // `--once` would read as the schedule the ADR rejected.
            stderr.WriteLine("captainHook mail watch: --once is required — this verb evaluates the rules once and prints; it is not a schedule");
            stderr.WriteLine(Usage);
            return 1;
        }

        var session = ReadSession(stdin);
        var now = nowMs ?? Environment.TickCount64;

        // ---- the inputs, each named on the report ------------------------------
        var resolution = WatchResolution.Resolve(WatchRules.ResolvePath(watchPath));
        var rules = resolution.Effective();
        stdout.WriteLine(resolution switch
        {
            WatchResolution.Absent => "watch.json: absent — no rules, so no robot nudge can ever fire",
            WatchResolution.Malformed m => $"watch.json: malformed — no robot nudge can fire until it parses ({m.Error})",
            _ => $"watch.json: {rules.Count} rule(s)",
        });

        var handlers = ExecHandlersFile.Resolve(ExecHandlersFile.ResolvePath(handlersPath));
        var kinds = RoleKinds.From(handlers);
        stdout.WriteLine(handlers is ExecHandlersResolution.Loaded
            ? $"handlers.json: turn payload on mail-nudge: {(kinds.TurnPayloadInstalled ? "installed" : "none")}; human-held roles: "
              + (kinds.HumanHeld.Count == 0 ? "none" : string.Join(", ", kinds.HumanHeld.Order(StringComparer.Ordinal)))
            : "handlers.json: absent or malformed — nothing registered, every role is unserved");

        IReadOnlyList<(string Session, long AgeMs)> presence = session is null ? [] : [(session, 0L)];
        stdout.WriteLine(session is null
            ? "presence: not visible from the CLI (the daemon's own view) — no session treated as live"
            : $"presence: only what the CLI can claim — the calling session {session} is live now; no other window is visible from here");

        var cursors = new MailCursors(new MailStore(MailStore.ResolveDir(mailDir)));
        var roles = RolesToWatch(cursors, rules);
        var mailboxes = ReadMailboxes(cursors, roles);

        // `--as-if-quiet`: pretend every envelope has been unread since long
        // before any threshold. The state carries the pretence, so the brain
        // itself is untouched — the same function, a different memory.
        var state = asIfQuiet ? QuietForever(mailboxes, now) : NudgeState.Empty;
        stdout.WriteLine(asIfQuiet
            ? "state: --as-if-quiet — every unread envelope treated as past every quiet threshold"
            : "state: none — every unread envelope is first seen now, so no quiet threshold has been crossed (add --as-if-quiet to see past it)");

        // ---- the decision ---------------------------------------------------------
        var verdict = WatcherBrain.Evaluate(new WatchInput(mailboxes, presence, kinds, rules, state, now));

        stdout.WriteLine();
        if (verdict.Roles.Count == 0)
            stdout.WriteLine("no rule names a role — nothing to evaluate");
        foreach (var r in verdict.Roles)
        {
            var live = r.FreshestDispatchAgeMs is { } age ? $"freshest dispatch {WatcherBrain.Dur(age)} ago" : "no dispatch seen";
            stdout.WriteLine($"{r.Role}: {Wire(r.Kind)} · {r.Unread} unread · {live} · {Wire(r.Standing)} — {r.Detail}");
        }
        foreach (var d in verdict.Dead)
        {
            var seen = d.FreshestDispatchAgeMs is { } age ? $"freshest dispatch {WatcherBrain.Dur(age)} ago" : "no dispatch seen";
            stdout.WriteLine($"{d.Address}: dead-mailbox candidate · {d.Stranded} stranded · {seen} · {Wire(d.Standing)} — {d.Detail}");
        }
        foreach (var n in verdict.Nudges)
        {
            stdout.WriteLine();
            stdout.WriteLine($"WOULD NUDGE {n.Role}{(n.Address is { } about ? $" about {about}" : "")}: {string.Join(", ", n.EnvelopeIds)}");
            stdout.WriteLine($"  reason: {n.Reason}");
            foreach (var line in n.Digest.Split('\n')) stdout.WriteLine("  | " + line);
        }
        stdout.WriteLine();
        stdout.WriteLine(verdict.NextCheckMs is { } next
            ? $"next check: in {WatcherBrain.Dur(next - now)}"
            : "next check: nothing armed");

        Log.Info("watch", "watch.verdict", new LogFields
        {
            SessionId = session,
            Msg = $"mail watch --once: {verdict.Nudges.Count} nudge(s) due across {verdict.Roles.Count} rule role(s)",
            Data = new Dictionary<string, object>
            {
                ["asIfQuiet"] = asIfQuiet,
                ["nudges"] = verdict.Nudges.Select(n => new Dictionary<string, object>
                {
                    ["role"] = n.Role, ["envelopeIds"] = n.EnvelopeIds.ToList(), ["reason"] = n.Reason,
                    ["address"] = n.Address ?? "",
                }).ToList(),
                ["dead"] = verdict.Dead.Select(d => new Dictionary<string, object>
                {
                    ["address"] = d.Address, ["standing"] = Wire(d.Standing), ["stranded"] = d.Stranded,
                }).ToList(),
                ["roles"] = verdict.Roles.Select(r => new Dictionary<string, object>
                {
                    ["role"] = r.Role, ["kind"] = Wire(r.Kind), ["standing"] = Wire(r.Standing),
                    ["unread"] = r.Unread, ["due"] = r.Due,
                }).ToList(),
                ["nextCheckInMs"] = verdict.NextCheckMs is { } nc ? nc - now : (object)"none",
            },
        });
        return 0;
    }

    /// Every mailbox of every named role, read through the real cursors and
    /// writing nothing (`Pending` is a read; a re-anchor is a value it returns).
    /// A role with no cursor file at all is read SESSIONLESS — its broadcast
    /// from the anchor, which is everything ever addressed to it, which is the
    /// truth for a role nobody has picked up. Shared with the actor so the two
    /// callers of the brain cannot disagree about what a mailbox is.
    ///
    /// **A unicast to a mailbox with no cursor is still a mailbox.** `role@x`
    /// addressed before `x` ever read (a `--as` registration not yet fired, an
    /// answer to a window whose cursor was reaped) is accepted by no cursor
    /// file and would be invisible; so every instance the LEDGER addresses for
    /// the role is read too, as the fresh mailbox it is. Its broadcast history
    /// then reads pending from the anchor — and the brain's intersection rule
    /// keeps that honest: broadcast some other mailbox of the role has read is
    /// not unread, and only the unicast nobody else can accept is.
    public static IReadOnlyList<WatchedMailbox> ReadMailboxes(MailCursors cursors, IReadOnlyList<string> roles)
    {
        var files = MailCursors.List(cursors.Store.Dir);
        var wanted = new HashSet<string>(roles, StringComparer.Ordinal);

        // Instances the ledger names for a wanted role, one read of the store.
        var addressed = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var line in cursors.Store.Read())
        {
            if (line.Envelope is not { } e || !MailAddress.TryParse(e.To, out var to)) continue;
            if (to.Instance is null || !wanted.Contains(to.Role)) continue;
            if (!addressed.TryGetValue(to.Role, out var set)) addressed[to.Role] = set = new(StringComparer.Ordinal);
            set.Add(to.Instance);
        }

        var boxes = new List<WatchedMailbox>();
        foreach (var role in roles)
        {
            var mine = files.Where(f => f.Role == role).ToList();
            var keys = new HashSet<string?>(mine.Select(f => f.Session));
            if (mine.Count == 0)
                boxes.Add(new WatchedMailbox(new MailAddress(role, null), cursors.Pending(role, null).Pending, HasCursor: false));
            foreach (var (_, key) in mine)
            {
                // The cursor key IS the instance (ADR-0018 d3): read the mailbox
                // the file names, on behalf of nobody — `hookSession` only ever
                // rides the trail, and this read writes none.
                var address = new MailAddress(role, key);
                boxes.Add(new WatchedMailbox(address, cursors.Pending(address, hookSession: null).Pending));
            }
            if (addressed.TryGetValue(role, out var instances))
                foreach (var instance in instances.Where(i => !keys.Contains(i)))
                {
                    var address = new MailAddress(role, instance);
                    boxes.Add(new WatchedMailbox(
                        address, cursors.Pending(address, hookSession: null).Pending, HasCursor: false));
                }
        }
        return boxes;
    }

    /// Which roles one evaluation reads. The rules' roles, always — they are the
    /// only ones the role rule can decide anything about.
    ///
    /// Plus, when a `reaper` rule exists, EVERY role that has a cursor file: the
    /// dead-mailbox rule (ADR-0018 d6) is about somebody else's mailbox, and the
    /// role whose window died is typically human-held with no rule of its own —
    /// gathering only rule roles would make the whole rule unreachable for
    /// exactly the boxes the field report found. A cursor file is the bound:
    /// standing is what a reap removes, so a role with none has no corpse.
    public static IReadOnlyList<string> RolesToWatch(MailCursors cursors, IReadOnlyList<WatchRule> rules)
    {
        var roles = rules.Select(r => r.Role).Distinct(StringComparer.Ordinal).ToList();
        if (!rules.Any(r => r.Role == WatcherBrain.ReaperRole)) return roles;

        var seen = new HashSet<string>(roles, StringComparer.Ordinal);
        // Ordinal, not the filesystem's order: two runs over one mail dir must
        // hand the brain the same list.
        foreach (var role in MailCursors.List(cursors.Store.Dir)
                     .Select(f => f.Role).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            if (seen.Add(role)) roles.Add(role);
        return roles;
    }

    /// The `--as-if-quiet` memory: every unread envelope first seen, and quiet
    /// since, the parser's maximum duration ago — past every threshold a loaded
    /// rule can state. A finite number rather than a sentinel, so the report's
    /// durations render and the brain sees nothing it would not see in life.
    private static NudgeState QuietForever(IReadOnlyList<WatchedMailbox> mailboxes, long now)
    {
        var longAgo = now - AsIfQuietMs;
        var seen = new HashSet<(string, string)>();
        var entries = new List<WatchedEnvelope>();
        foreach (var box in mailboxes)
            foreach (var mail in box.Pending)
                // Both keys the brain tracks under: the ROLE (the role rule's)
                // and the ADDRESS (the dead-mailbox rule's subject, ADR-0018 d6).
                // Keying only the role would leave `--as-if-quiet` blind to the
                // one rule an operator most wants to preview.
                foreach (var subject in new[] { box.Address.Role, box.Address.ToString() }.Distinct(StringComparer.Ordinal))
                    if (seen.Add((subject, mail.Envelope.Id)))
                        entries.Add(new WatchedEnvelope(subject, mail.Envelope.Id, longAgo, longAgo, 0));
        return new NudgeState(entries, []);
    }

    /// The calling session, from either shape stdin can carry: an exec-wire
    /// envelope (this verb behind a hook registration) or hook-shaped JSON (a
    /// status-line style caller, or a human's `printf`). Neither present ⇒ null:
    /// nobody is claimed live, and the report says so.
    private static string? ReadSession(TextReader stdin)
    {
        string text;
        try { text = stdin.ReadToEnd(); }
        catch (IOException) { return null; }
        if (string.IsNullOrWhiteSpace(text)) return null;

        var firstLine = text.Split('\n', 2)[0];
        if (MailDigest.TryParseRequest(firstLine, out _) is { } req) return req.SessionId;
        return MailStatus.ReadCaller(new StringReader(text)).Session;
    }

    private static string Wire(RoleKind k) => k switch
    {
        RoleKind.HumanHeld => "human-held",
        RoleKind.RobotServable => "robot-servable",
        RoleKind.Mixed => "mixed",
        _ => "unserved",
    };

    private static string Wire(WatchStanding s) => s switch
    {
        WatchStanding.NoMail => "no-mail",
        WatchStanding.HumanHeld => "human-held",
        WatchStanding.Unserved => "unserved",
        WatchStanding.NotDue => "not-due",
        WatchStanding.LiveSession => "live-session",
        WatchStanding.Exhausted => "exhausted",
        _ => "nudge",
    };
}
