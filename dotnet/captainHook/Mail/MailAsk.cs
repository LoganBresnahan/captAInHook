using System.Diagnostics;
using CaptainHook.Core;

namespace CaptainHook.Mail;

// ADR-0017 decision 8, slice `mail-ask-wait` (roadmap item 22, phase 2, thread
// lane) — `captainHook mail ask --to <address> --topic <t> [--wait <s>]`: put a
// REQUEST on the bus and, optionally, block until its answer arrives.
//
// This is the only synchronous shape ADR-0016 d9 was willing to admit. There is
// no transport and no daemon in the loop: `--wait` is a BOUNDED READ of the
// same ledger everything else reads, and the asker CHOOSES to yield its turn to
// it (a tool call that blocks). Nothing here subscribes, nothing here is pushed
// to, and a wait that expires costs the request nothing — it is on the chain,
// and the ordinary digest delivers it whenever the recipient next reads.
//
// **Waiting is not delivering.** The poll reads the store and never advances a
// cursor, so an answer this verb PRINTS is still pending for the asker's own
// digest and will be injected at its next seam. That is a deliberate cost, not
// an oversight: consuming here would be a write from a read-shaped verb, and it
// would hide the answer from the window that asked for it if the wait were
// interrupted a millisecond later. Seeing it twice is recoverable; losing it is
// not (ADR-0016 d14's rule, applied to a verb instead of a view).
//
// **Where the answer goes is never guessed** (ADR-0018 d4). `replyTo` is either
// spelled by the caller or inferred from the caller's OWN registrations — the
// same lookup `mail status` uses to learn which mailboxes a window may read —
// and when that is ambiguous or empty the verb REFUSES and says what it found.
// An ask whose answer has nowhere to go is the one failure this whole design is
// against, so it fails at the ask rather than at the answer.
//
// **The deadline is monotonic and there are three of them** (N4). A harness
// with a hard tool timeout truncates a blocked tool call, so the verb waits the
// SHORTEST of: the caller's `--wait`, the harness's timeout when the
// installation names one (`CAPTAINHOOK_ASK_TOOL_TIMEOUT_S`), and this verb's
// own cap — and it SAYS WHICH ONE fired. `Stopwatch` throughout, never a wall
// clock (house invariant 2); the clock and the sleeper are injected so the
// tests wait for nothing.

/// Which bound ended the wait — named in the output, because "unanswered" with
/// no reason leaves an agent unable to tell "nobody answered in a minute" from
/// "your harness cut me off after five seconds".
public enum MailAskBound { Wait, ToolTimeout, VerbCap }

public static class MailAsk
{
    public const string Usage =
        "usage: captainHook mail ask --to <role|role@instance> --topic <topic> "
        + "[--wait <seconds>] [--reply-to <address>] [--priority ambient|reconcile|urgent] "
        + "[--agent <name>] [--harness <name>] [--id <id>]   (the question's body on stdin)";

    /// The verb's own ceiling on a blocked tool call, in seconds. A wait longer
    /// than this is not a tool call any more, it is a background job — and the
    /// bus already has one of those (the digest delivers whenever the recipient
    /// reads). Ten minutes is generous for a human turnaround and short enough
    /// that a forgotten `--wait 99999` cannot pin a session open.
    public const int MaxWaitSeconds = 600;

    /// How often the ledger is re-read while waiting. A file read of a
    /// human-cadence store; the cost of asking more often is not worth the
    /// latency it would save, and the cost of asking less is a second of
    /// staring at an answer that already landed.
    public const int PollMs = 500;

    /// The installation's declaration of its harness's tool timeout, in
    /// seconds (N4). The ENGINE cannot know it — it is a property of the thing
    /// running the tool, not of the bus — so whoever installs the verb as a
    /// tool says it here, and the wait fails closed at the shorter bound
    /// instead of being killed mid-read with nothing printed.
    public const string ToolTimeoutEnv = "CAPTAINHOOK_ASK_TOOL_TIMEOUT_S";

    /// Run the verb. `nowMs`/`sleep` are injected so tests drive the deadline
    /// with a fake clock and never sleep; `newId` and `nowTs` are the same
    /// determinism seams `mail send` has.
    public static int Run(
        IReadOnlyList<string> argv, TextReader stdin, TextWriter stdout, TextWriter stderr,
        string? mailDir = null, string? handlersPath = null, string? policyPath = null,
        string? cwd = null, Func<long>? nowMs = null, Action<int>? sleep = null,
        Func<string>? newId = null, string? nowTs = null, string? toolTimeoutS = null,
        MailSenderDeclaration? declaredFrom = null)
    {
        var opts = TryParseArgs(argv, out var argErrors);
        if (opts is null)
        {
            stderr.WriteLine("captainHook mail ask: refused — nothing written:");
            foreach (var e in argErrors) stderr.WriteLine($"  {e}");
            stderr.WriteLine(Usage);
            return 1;
        }

        // WHERE THE ANSWER GOES, decided before a byte is written. An explicit
        // `--reply-to` wins; otherwise the caller's own registrations must name
        // exactly one mailbox, and anything else is a refusal that lists what
        // was found (never a pick among candidates — a wrong return address
        // misroutes answers silently).
        var mine = MailStatus.ReadableMailboxes(
            cwd ?? Environment.CurrentDirectory, session: null, handlersPath, policyPath);
        MailAddress replyTo;
        if (opts.ReplyTo is { } stated) replyTo = stated;
        else
        {
            if (mine.Count == 1) replyTo = mine[0];
            else
            {
                stderr.WriteLine("captainHook mail ask: refused — nothing written:");
                stderr.WriteLine(mine.Count == 0
                    ? "  no `mail digest` registration in this workspace names a mailbox to answer, "
                      + "so an answer to this request would have nowhere to go — pass --reply-to <address>"
                    : "  this workspace reads " + mine.Count + " mailboxes ("
                      + string.Join(", ", mine.Select(m => m.ToString()))
                      + ") — pass --reply-to <address> to say which one the answer belongs to");
                return 1;
            }
        }

        // Provenance, learned rather than invented: the asker IS the mailbox it
        // wants the answer in, and the harness is the one its own digest
        // registration declares. `cli` is the honest fallback when neither is
        // knowable — this envelope really was written by a command line.
        //
        // A SPAWNING PAYLOAD'S DECLARATION OUTRANKS BOTH (slice
        // `turn-provenance`), and it has to: a member that says who it is on
        // `mail send` and something else on `mail ask` would be two provenances
        // for one process. It refuses an explicit `--agent`/`--harness` that
        // disagrees — the sender's CLAIM, the same case `mail send` refuses —
        // and simply replaces the DERIVATION below, which is a guess made from
        // a workspace's registrations and not a claim anybody made.
        var declared = declaredFrom ?? MailSenderDeclaration.FromEnvironment();
        var (declaredAgent, declaredHarness, declaredSession) =
            declared.Resolve(opts.Agent, opts.Harness, statedSession: null, out var senderRefusals);
        if (senderRefusals.Count > 0)
        {
            stderr.WriteLine("captainHook mail ask: refused — nothing written:");
            foreach (var r in senderRefusals) stderr.WriteLine($"  {r}");
            return 1;
        }

        var agent = declaredAgent ?? replyTo.Role;
        var harness = declaredHarness ?? HarnessOfCaller(handlersPath) ?? "cli";

        var body = ReadBody(stdin);
        var id = opts.Id ?? (newId ?? MintId)();

        var envelope = new MailEnvelope(
            Id: id,
            Ts: nowTs ?? DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            // Session: the spawning payload's stamp when there is one, else
            // absent — an ask from a window still names no window, because this
            // verb leaves `from.session` to whoever can actually know it.
            From: new MailSender(agent, harness, declaredSession),
            To: opts.To.ToString(),
            Kind: MailKind.Request,
            Topic: opts.Topic,
            Priority: opts.Priority,
            InReplyTo: null,
            Hops: 0,
            ReplyTo: replyTo.ToString(),
            ForwardedFrom: null,
            // The address decides, exactly as the parser does (ADR-0018 d5):
            // a unicast has no TTL to count, a role address takes the default.
            TtlDeliveries: opts.To.IsUnicast ? null : MailEnvelope.DefaultTtlDeliveries,
            Body: body,
            Prev: null);

        var store = new MailStore(MailStore.ResolveDir(mailDir));
        if (store.Append(envelope) is MailAppend.Failed failed)
        {
            stderr.WriteLine($"captainHook mail ask: {failed.Error}");
            return 1;
        }

        var (seconds, bound) = Deadline(opts.WaitSeconds, toolTimeoutS ?? Environment.GetEnvironmentVariable(ToolTimeoutEnv));

        stdout.WriteLine(
            $"mail: asked {opts.To} — id {id}, answer to {replyTo}"
            + (seconds is int s ? $" (waiting up to {s}s)" : ""));

        if (seconds is not int waitS) return 0;

        var answer = Wait(store, id, waitS, nowMs, sleep);
        if (answer is null)
        {
            // NOT an error exit: the request was written and is on the chain.
            // The asker's own digest will hand it the answer whenever one
            // arrives, so a wait that expires is a fact about timing, never a
            // failed send — and an agent chaining on `&&` should carry on.
            stdout.WriteLine(
                $"unanswered after {waitS}s ({Explain(bound)}) — {id} is on the bus and "
                + $"the answer will reach {replyTo} at its next digest");
            return 0;
        }

        stdout.WriteLine(Render(answer));
        return 0;
    }

    /// The shortest of the three bounds, and which one it was. Null = no wait
    /// was asked for, which is the plain append (`mail send`'s shape with the
    /// envelope composed for you).
    ///
    /// The tool timeout is read as a STRING and silently ignored when it is not
    /// a positive integer: it is set by whoever installs the tool, and a typo
    /// there must not turn every ask into an argument error — the caller's own
    /// `--wait` and the verb's cap still bound the read.
    internal static (int? Seconds, MailAskBound Bound) Deadline(int? wait, string? toolTimeout)
    {
        if (wait is not int w) return (null, MailAskBound.Wait);

        var seconds = w;
        var bound = MailAskBound.Wait;
        if (int.TryParse(toolTimeout, out var tool) && tool > 0 && tool < seconds)
        {
            seconds = tool;
            bound = MailAskBound.ToolTimeout;
        }
        if (MaxWaitSeconds < seconds)
        {
            seconds = MaxWaitSeconds;
            bound = MailAskBound.VerbCap;
        }
        return (seconds, bound);
    }

    private static string Explain(MailAskBound bound) => bound switch
    {
        MailAskBound.ToolTimeout => $"your harness's tool timeout, {ToolTimeoutEnv}",
        MailAskBound.VerbCap => $"this verb's cap of {MaxWaitSeconds}s",
        _ => "your --wait",
    };

    /// The bounded read: re-scan the ledger until an `answer` names this id or
    /// the deadline passes. MONOTONIC (Stopwatch by default, injectable), and
    /// the check is deadline-first so a zero-length wait reads exactly once —
    /// an answer that landed between the append and here is still found.
    private static MailEnvelope? Wait(
        MailStore store, string id, int seconds, Func<long>? nowMs, Action<int>? sleep)
    {
        var clock = nowMs ?? Monotonic();
        var nap = sleep ?? (ms => Thread.Sleep(ms));
        var deadline = clock() + seconds * 1000L;

        while (true)
        {
            if (AnswerTo(store, id) is { } answer) return answer;
            var remaining = deadline - clock();
            if (remaining <= 0) return null;
            // Never sleep past the deadline: the last nap is the remainder, so
            // the verb returns when it said it would rather than up to a poll
            // interval late.
            nap((int)Math.Min(PollMs, remaining));
        }
    }

    /// The FIRST answer on the chain that names this request (ADR-0017 d8's
    /// closing rule, and the read model's `closedBy` uses the same one). Only
    /// `kind: answer` closes — a status quoting the request is a comment on it,
    /// and a wait that ended on a comment would report the wrong thing as the
    /// answer.
    private static MailEnvelope? AnswerTo(MailStore store, string id) =>
        store.Read()
            .Where(l => l.Terminated && l.Envelope is { Kind: MailKind.Answer } e && e.InReplyTo == id)
            .Select(l => l.Envelope)
            .FirstOrDefault();

    /// The answer as the asker sees it: the digest's own head shape (so one
    /// rendering of provenance exists in a reader's head, whichever surface it
    /// arrived on), then the body indented beneath it.
    private static string Render(MailEnvelope e)
    {
        var head = $"answer from {MailEnvelope.ClampField(e.From.Agent)} "
            + $"({MailEnvelope.ClampField(e.From.Harness)}) · id {MailEnvelope.ClampField(e.Id)} · "
            + $"topic: {MailEnvelope.ClampField(e.Topic)} · hop {e.Hops + 1}/{MailEnvelope.HopBudget}";
        return e.Body.Length == 0
            ? head
            : head + "\n" + string.Join('\n', e.Body.Split('\n').Select(l => "   " + l));
    }

    /// The harness the CALLER's own digest registration declares — provenance
    /// learned from the file the operator already wrote, never asked for twice.
    /// Null when there is no registration to learn from.
    private static string? HarnessOfCaller(string? handlersPath) =>
        ExecHandlersFile.Resolve(ExecHandlersFile.ResolvePath(handlersPath))
            is ExecHandlersResolution.Loaded loaded
            ? loaded.Entries
                .Where(e => e.Args.Count >= 2 && e.Args[0] == "mail" && e.Args[1] == "digest")
                .Select(e => MailDigest.TryParseArgs([.. e.Args.Skip(2)], out _)?.Harness)
                .FirstOrDefault(h => h is not null)
            : null;

    private static Func<long> Monotonic()
    {
        var sw = Stopwatch.StartNew();
        return () => sw.ElapsedMilliseconds;
    }

    /// Ids are the sender's to choose and this verb is the sender. `ask-` plus
    /// 8 hex is the shim's dispatch-id shape — short enough to quote in a reply
    /// by hand, wide enough that two asks in one second do not collide.
    private static string MintId() => "ask-" + Guid.NewGuid().ToString("N")[..8];

    /// The question itself. Unreadable stdin is an EMPTY body rather than an
    /// error: the topic already carries a question, and refusing the ask over a
    /// closed pipe would lose a request that was otherwise complete.
    private static string ReadBody(TextReader stdin)
    {
        try { return stdin.ReadToEnd().TrimEnd('\n'); }
        catch (IOException) { return ""; }
    }

    // ---- arguments -----------------------------------------------------------

    internal sealed record AskOptions(
        MailAddress To, string Topic, MailPriority Priority, int? WaitSeconds,
        MailAddress? ReplyTo, string? Agent, string? Harness, string? Id);

    /// The strict walk, `MailDigest.TryParseArgs`' shape: every violation
    /// collected, all-or-nothing accept, unknown flags refused rather than
    /// ignored.
    ///
    /// `--priority` DEFAULTS TO AMBIENT — the envelope parser's own default,
    /// deliberately not raised because an ask is not automatically an
    /// interruption. Urgent is the class that buys a mid-turn seam and can trip
    /// a `watch.json` rule into spending the owner's tokens, and this verb must
    /// not be a cheaper way to buy that than `mail send` is; somebody who means
    /// it writes `--priority urgent` and can be held to having meant it.
    internal static AskOptions? TryParseArgs(IReadOnlyList<string> args, out IReadOnlyList<string> errors)
    {
        var errs = new List<string>();
        errors = errs;

        string? to = null, topic = null, replyTo = null, agent = null, harness = null, id = null;
        var priority = MailPriority.Ambient;
        int? wait = null;

        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--to" when i + 1 < args.Count: to = args[++i]; break;
                case "--topic" when i + 1 < args.Count: topic = args[++i]; break;
                case "--reply-to" when i + 1 < args.Count: replyTo = args[++i]; break;
                case "--agent" when i + 1 < args.Count: agent = args[++i]; break;
                case "--harness" when i + 1 < args.Count: harness = args[++i]; break;
                case "--id" when i + 1 < args.Count: id = args[++i]; break;

                case "--priority" when i + 1 < args.Count:
                {
                    var raw = args[++i];
                    // By NAME and case-insensitively — the closed-set rule the
                    // envelope parser and `watch.json` both follow; a casing
                    // slip must not silently downgrade an urgent ask.
                    var match = Enum.GetNames<MailPriority>()
                        .FirstOrDefault(n => string.Equals(n, raw, StringComparison.OrdinalIgnoreCase));
                    if (match is null)
                        errs.Add($"--priority must be one of: {string.Join(", ", Enum.GetNames<MailPriority>().Select(n => n.ToLowerInvariant()))} (got '{raw}')");
                    else priority = Enum.Parse<MailPriority>(match);
                    break;
                }

                case "--wait" when i + 1 < args.Count:
                {
                    var raw = args[++i];
                    if (!int.TryParse(raw, out var s) || s < 0)
                        errs.Add($"--wait must be a whole number of seconds >= 0 (got '{raw}')");
                    else wait = s;
                    break;
                }

                case "--to" or "--topic" or "--reply-to" or "--agent" or "--harness"
                     or "--id" or "--priority" or "--wait":
                    errs.Add($"{args[i]} needs a value");
                    break;

                default:
                    errs.Add($"unknown argument '{args[i]}'");
                    break;
            }
        }

        MailAddress? toAddr = null;
        if (to is null) errs.Add("--to is required (a role, or role@instance)");
        else if (!MailAddress.TryParse(to, out var parsedTo))
            errs.Add($"--to must be {MailAddress.GrammarHelp} (got '{to}')");
        else toAddr = parsedTo;

        MailAddress? replyAddr = null;
        if (replyTo is not null)
        {
            if (!MailAddress.TryParse(replyTo, out var parsedReply))
                errs.Add($"--reply-to must be {MailAddress.GrammarHelp} (got '{replyTo}')");
            else replyAddr = parsedReply;
        }

        // A topic is how every other surface names this envelope — the digest
        // head, the canvas card, `mail status`' line. Deriving one from the
        // body would be the protocol reading prose, which ADR-0016 d2 refuses.
        if (string.IsNullOrWhiteSpace(topic)) errs.Add("--topic is required");

        return errs.Count > 0
            ? null
            : new AskOptions(toAddr!.Value, topic!, priority, wait, replyAddr, agent, harness, id);
    }
}
