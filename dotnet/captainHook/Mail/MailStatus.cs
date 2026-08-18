using System.Text.Json;
using CaptainHook.Core;

namespace CaptainHook.Mail;

// mail-status (ADR-0017 d2, phase 1) — the human channel, always on and
// ruleless. `captainHook mail status` prints how much mail is waiting for the
// roles THIS window may read, for a harness's passive display (Claude Code's
// `statusLine`; any other harness's equivalent).
//
// It needs no rules because it interrupts nothing. Every other channel this ADR
// builds can spend the owner's tokens or take a turn on their behalf, and each
// of those gets a consent surface; a count on a status bar is looked at when the
// human chooses to look. That asymmetry is the whole reason this verb ships
// first and alone.
//
// **The role is never configured twice.** "Which roles may this window read?"
// already has an answer on disk: the `mail digest` handlers registered in
// handlers.json that survive `dispatch.json` for this cwd and session — the
// same evaluation the dispatcher performs before it fans out (ADR-0006), so a
// window whose digest is denied by policy is told about no mail it will never
// be handed. A second file naming a window's role would be a second truth to
// keep in sync, and the first drift would be silent.
//
// **Recognition, not a second parser.** A registration is a digest if
// `MailDigest.TryParseArgs` — the very code that would run it — accepts its
// args. `MailCursors.List`'s rule (ADR-0016 d13): what counts is what the real
// path would accept, never a lookalike spelling maintained beside it.
//
// **It reads and only reads.** `MailCursors.Pending` writes nothing (a
// re-anchor is a value it returns, not a file it stamps), `MailStore` creates
// its directory only on Append, and nothing here logs: a status line renders on
// a human's cadence, and a trail line per render would drown the trail it is
// supposed to help you read. What this verb has to say, it says on stdout.
public static class MailStatus
{
    public const string Usage = "usage: captainHook mail status   (hook-shaped JSON on stdin: session_id, cwd)";

    /// The glyph is the whole point on a status bar: it reads at a glance and
    /// it is the same one the digest's own header uses.
    private const string Glyph = "📬";

    /// One line per role that has mail, quietest-possible: no mail anywhere ⇒
    /// no output at all, so a wired status bar shows nothing until there is
    /// something to show. Exit is 0 for every state of the world except bad
    /// arguments — a display command that fails loudly on an absent config
    /// would put an error where a human expects a count.
    public static int Run(
        IReadOnlyList<string> argv, TextReader stdin, TextWriter stdout, TextWriter stderr,
        string? mailDir = null, string? handlersPath = null, string? policyPath = null)
    {
        if (argv.Count > 0)
        {
            stderr.WriteLine($"captainHook mail status: unexpected argument '{argv[0]}'");
            stderr.WriteLine(Usage);
            return 1;
        }

        var (session, cwd) = ReadCaller(stdin);

        var mailboxes = ReadableMailboxes(cwd, session, handlersPath, policyPath);
        if (mailboxes.Count == 0) return 0;

        // Named only when the window may read more than one mailbox: the single
        // case is every human window, and a bare count is what fits a status
        // bar. A swarm window reading two cannot be told "2 · 1 urgent" about
        // which, so there the name is not decoration.
        var qualify = mailboxes.Count > 1;

        var cursors = new MailCursors(new MailStore(MailStore.ResolveDir(mailDir)));
        foreach (var box in mailboxes)
        {
            // The count follows the CURSOR, and `--as` is what decides which
            // cursor this window reads (ADR-0018 d3). Counting the session's own
            // cursor for a named registration would report a mailbox nobody
            // reads — worse than silence, because it looks like an answer.
            //
            // The address goes in whole rather than as a pre-resolved key, so
            // the count is over exactly what the digest would be handed (d4):
            // a named mailbox's unicast mail is waiting for a human as surely
            // as its broadcast is, and a bar that omitted it would point at the
            // one kind of envelope that has nobody else to reach.
            var view = cursors.Pending(box, session);
            var line = Line(box.ToString(), view, qualify);
            if (line is not null) stdout.WriteLine(line);
        }
        return 0;
    }

    /// The rendering, alone and pure so a golden can pin it. Null = nothing to
    /// say about this role. EXPIRED mail is not counted: those envelopes are
    /// spent (the next advance drops them), and a count that included them would
    /// ask a human to go read something no digest will ever hand over. Held mail
    /// IS counted — it has not been delivered yet, which is exactly the state
    /// this line exists to surface.
    /// `address` is what to CALL this mailbox when the line names one — a bare
    /// role for an unnamed reader, `role@instance` for a named one (ADR-0018
    /// d3), which is the same spelling a sender would address it by.
    public static string? Line(string address, MailPendingView view, bool qualify)
    {
        var total = view.Pending.Count;
        if (total == 0) return null;
        var urgent = view.Pending.Count(p => p.Envelope.Priority == MailPriority.Urgent);

        var head = qualify ? $"{Glyph} {address} {total}" : $"{Glyph} {total}";
        return urgent > 0 ? $"{head} · {urgent} urgent" : head;
    }

    /// Who is asking. The payload is hook-shaped because every harness surface
    /// that can run this already speaks it; `workspace.current_dir` is honored
    /// beside `cwd` because Claude Code's status payload carries both.
    ///
    /// Every failure is the same answer — an unknown caller — and never an
    /// error: a status bar handed no stdin, or malformed stdin, still wants a
    /// number if one is true. An unknown SESSION is not a fudge: null is the
    /// sessionless reader's own cursor (ADR-0016 d4), a real cursor with a real
    /// count, so the number stays the answer to a question that was actually
    /// asked rather than a guess about which window is asking.
    internal static (string? Session, string? Cwd) ReadCaller(TextReader stdin)
    {
        string text;
        try { text = stdin.ReadToEnd(); }
        catch (IOException) { return (null, null); }
        if (string.IsNullOrWhiteSpace(text)) return (null, null);

        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, null);

            var session = Str(root, "session_id");
            var cwd = Str(root, "cwd");
            if (cwd is null && root.TryGetProperty("workspace", out var ws) && ws.ValueKind == JsonValueKind.Object)
                cwd = Str(ws, "current_dir");
            return (session, cwd);
        }
        catch (JsonException) { return (null, null); }
    }

    /// The roles this caller may read, sorted, deduped. A role served by two
    /// registrations (an ambient seam and an urgent one — the normal shape)
    /// appears once: the cursor is per role × session, not per registration.
    private static IReadOnlyList<MailAddress> ReadableMailboxes(
        string? cwd, string? session, string? handlersPath, string? policyPath)
    {
        if (ExecHandlersFile.Resolve(ExecHandlersFile.ResolvePath(handlersPath))
            is not ExecHandlersResolution.Loaded loaded)
            return [];   // absent or malformed: no registrations to read, nothing to say

        // A malformed policy resolves to Malformed, whose Evaluate is the
        // fail-closed answer the dispatcher would also get — so this verb
        // reports mail exactly when the digest would have been allowed to
        // deliver it, including when the answer is "none".
        var policy = PolicyResolution.Resolve(DispatchPolicy.ResolvePath(policyPath));

        // Keyed by the ADDRESS, so a role registered at two seams stays one
        // line (one cursor, one count) while the same role under two different
        // `--as` names is honestly two mailboxes.
        var boxes = new SortedDictionary<string, MailAddress>(StringComparer.Ordinal);
        foreach (var entry in loaded.Entries)
        {
            if (MailDigest.MailboxOf(entry) is not { } box) continue;
            // Registered on several events, delivered at whichever comes first:
            // surviving policy for ANY of them means this window will be handed
            // this role's mail, so the count belongs on its status bar.
            var reachable = entry.Events.Any(ev =>
            {
                var outcome = policy.Evaluate(ev, cwd, session);
                return outcome.Work && !outcome.ExcludedHandlers.Contains(entry.Name);
            });
            if (reachable) boxes[box.ToString()] = box;
        }
        return [.. boxes.Values];
    }

    private static string? Str(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String) return null;
        // Deferred unescaping: a lone surrogate parses and throws at GetString
        // (the policy skeptic pass's find). An unreadable field is an unknown
        // caller, never a crashed status bar.
        try { return el.GetString(); }
        catch (InvalidOperationException) { return null; }
    }
}
