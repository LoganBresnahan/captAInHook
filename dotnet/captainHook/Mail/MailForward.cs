using CaptainHook.Actors;

namespace CaptainHook.Mail;

// Roadmap item 23 / ADR-0018 decision 6, slice `reaper-payloads` — `captainHook
// mail forward <role@instance> --to <address>`: the FORWARD half of a
// disposition, as a verb.
//
// d6 gives a reaper three dispositions — forward, drop, hold — and one verb for
// the standing (`mail reap`). Drop and hold need no mechanism: drop IS a reap,
// hold is doing nothing. Forward needed one, and this is it, because the
// authority decision settled DETERMINISTIC (ADR-0017 § Scope amendment, carried
// into this ADR's table): the reaper that ships is a shell payload reading an
// operator's rule, and a shell payload cannot compose an envelope. That is not
// an accident of this slice — it is `mail send`'s founding sentence, that the
// strictness lives in tested C# exactly so senders need no jq and no
// hand-rolled JSON. A deterministic reaper that built envelopes with `sed`
// would be the shell-script alternative ADR-0016 rejected, wearing a hat.
//
// **It is BLIND, and that is the answer to d6's open authority question.** The
// reaper is the bus's first cross-mailbox reader, and what made that a question
// was an LLM reading other members' bodies to judge them. Nothing here reads a
// body: bodies are COPIED, from the ledger to the ledger, and neither the
// payload nor any model ever sees one. What the caller learns is a count and a
// list of ids — the same facts `mail.reap` already writes down.
//
// **A forward is a NEW envelope** (d6, d8): the original stays on the chain
// untouched, the copy carries `forwardedFrom {id, address}` — the id says which
// envelope, the address says whose mailbox it was stranded in, which is the
// half no reader could recover from a bare role in `to`. Three fields are
// preserved rather than re-decided, each for a stated reason (`kind`, `topic`,
// `priority`: a stranded urgent request is still an urgent request, and a verb
// that quietly downgraded it would be editing somebody else's mail); `replyTo`
// rides along BY DESIGN (d4: the forward's sender is the reaper and its reply
// address is the original asker's — two facts one `from` cannot carry);
// `inReplyTo` is dropped and the depth restarts at 0, which is the stated,
// accepted cost of a stored depth (`MailEnvelope.Hops`); and the TTL is the
// TARGET address's, not the original's — a fresh countdown at a live mailbox,
// null when the target is unicast, because the parser refuses a TTL there and
// a copy that could not be re-appended would be a forward that only ever fails.
//
// **Re-running it does not duplicate.** An envelope already forwarded from this
// mailbox to this target is SKIPPED — recognized on the ledger by
// `forwardedFrom.id` + `to`, the only two facts a re-run would repeat — so a
// partial failure (some copies appended, then the disk fills) is fixed by
// running the verb again rather than by a human diffing two lanes. This matters
// because the caller is usually unattended: the payload only reaps when the
// forward succeeded, so the alternative to idempotence is a nudge that comes
// back and forwards everything twice.
//
// A HUMAN/CLI verb like `send`, `ask` and `reap`: stdout is free for a
// confirmation line, and nothing here touches the sacred channel.
public static class MailForward
{
    public const string Usage =
        "usage: captainHook mail forward <role@instance> --to <address> [--by <address>]   "
        + "(copies what the mailbox still holds to another address, carrying forwardedFrom; "
        + "the originals stay on the ledger)";

    /// Forward one mailbox's stranded mail. `mailDir`, `lockWaitMs`, `newId`
    /// and `nowTs` are test seams; production passes none of them.
    ///
    /// Exit 1 is for what a caller can fix — bad arguments, an unsayable
    /// sender, a lock it could not take, a store that refused an append — and
    /// nothing else. A mailbox with no cursor or nothing pending is exit 0 with
    /// a line saying so: there was nothing to move, which is an outcome and not
    /// a failure, and the payload that calls this must be able to go on and
    /// reap.
    public static int Run(
        IReadOnlyList<string> argv, TextWriter stdout, TextWriter stderr,
        string? mailDir = null, int lockWaitMs = MailStore.DefaultLockWaitMs,
        Func<string>? newId = null, string? nowTs = null,
        MailSenderDeclaration? declaredFrom = null)
    {
        if (!TryParseArgs(argv, out var source, out var target, out var by, out var argError))
        {
            stderr.WriteLine($"captainHook mail forward: {argError}");
            stderr.WriteLine(Usage);
            return 1;
        }

        // Forwarding a mailbox to itself would append copies straight back into
        // the box nobody is reading — a loop with a fresh TTL on every lap, and
        // the one mistake an operator's `REAPER_FORWARD_TO` typo can actually
        // make.
        if (source.Equals(target))
        {
            stderr.WriteLine($"captainHook mail forward: refused — '{target}' is the mailbox being forwarded");
            return 1;
        }

        // Who is doing it. The same two sources `mail ask` uses and in the same
        // order — a spawning payload's declaration outranks, `--by` is the
        // caller's own word — except that here one of them is REQUIRED: this
        // verb puts somebody else's mail on a live lane, and an envelope that
        // arrives from a sender nobody can name is the provenance defect
        // ADR-0017 N6 just finished fixing. `cli` is the honest harness for a
        // command line (`mail ask`'s fallback, same word).
        var declared = declaredFrom ?? MailSenderDeclaration.FromEnvironment();
        var (declaredAgent, declaredHarness, declaredSession) =
            declared.Resolve(by?.Role, statedHarness: null, statedSession: by?.Instance, out var senderRefusals);
        if (senderRefusals.Count > 0)
        {
            stderr.WriteLine("captainHook mail forward: refused — nothing written:");
            foreach (var r in senderRefusals) stderr.WriteLine($"  {r}");
            return 1;
        }
        if (declaredAgent is not { } agent)
        {
            stderr.WriteLine("captainHook mail forward: refused — nothing written:");
            stderr.WriteLine("  say who is forwarding: --by <address>, or a sender declaration in the environment");
            return 1;
        }
        var from = new MailSender(agent, declaredHarness ?? "cli", declaredSession);

        var store = new MailStore(MailStore.ResolveDir(mailDir));
        var cursors = new MailCursors(store);
        var path = cursors.CursorPath(source.Role, source.Instance);

        // No cursor, no standing, nothing stranded: a mailbox that never
        // existed holds nothing to move. Checked before the lock for the same
        // reason `mail reap` checks first — taking a lock creates its file, and
        // a lock beside a mailbox that was never there is litter.
        if (!File.Exists(path)) return NothingToForward(stdout, source, "no mailbox");

        MailPendingView view;
        IReadOnlyList<MailLine> lines;
        FileStream? held = null;
        try
        {
            // The cursor lock, for the READ only. It is the same lock an
            // advance takes, so what this verb sees is what the mailbox really
            // held at one instant rather than a set something moved underneath
            // it. It is released before the first append: holding a cursor lock
            // across writes to the store would make a slow forward block a
            // digest that has nothing to do with it, and the appends do not
            // touch the cursor at all.
            held = MailStore.TryLock(path + ".lock", lockWaitMs, out var lockError);
            if (held is null)
            {
                stderr.WriteLine($"captainHook mail forward: {lockError}");
                return 1;
            }
            if (!File.Exists(path)) return NothingToForward(stdout, source, "no mailbox");

            // ONE read of the store, and both answers come out of it: what this
            // mailbox is holding, and what has already been forwarded from it.
            // `hookSession` is null because a forward has no window — the
            // address is the whole of "which mailbox" (d3 as amended).
            lines = store.Read();
            view = cursors.Pending(source, hookSession: null, lines);
        }
        catch (Exception ex)   // permissions, a vanished mail dir, a directory at the path
        {
            stderr.WriteLine($"captainHook mail forward: cannot read '{source}': {ex.Message}");
            return 1;
        }
        finally
        {
            // Not unlinked, for the reason `mail reap` spells out: flock is on
            // the inode, and unlinking a held lock lets the next caller take a
            // lock that excludes nobody.
            held?.Dispose();
        }

        // EXPIRED mail is not forwarded, the same rule `mail.reap`'s
        // `pendingIds` follows: those envelopes are spent, no digest was ever
        // going to hand them over again, and moving them would resurrect at a
        // live address what died at a dead one.
        if (view.Pending.Count == 0) return NothingToForward(stdout, source, "nothing pending");

        var already = AlreadyForwarded(lines, source, target);
        var forwarded = new List<string>();
        var skipped = new List<string>();

        foreach (var pending in view.Pending)
        {
            var original = pending.Envelope;
            if (already.Contains(original.Id))
            {
                skipped.Add(original.Id);
                continue;
            }

            var copy = new MailEnvelope(
                Id: (newId ?? MintId)(),
                Ts: nowTs ?? DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
                From: from,
                To: target.ToString(),
                Kind: original.Kind,
                Topic: original.Topic,
                Priority: original.Priority,
                // A forward starts a new chain: it is not a reply to the
                // envelope it copies, and claiming otherwise would put a thread
                // link on the ledger that no exchange ever made.
                InReplyTo: null,
                Hops: 0,
                // The asker's return address, carried: an answer to a forwarded
                // request belongs to whoever asked, not to the reaper (d4).
                ReplyTo: original.ReplyTo,
                ForwardedFrom: new MailForwardedFrom(original.Id, source.ToString()),
                TtlDeliveries: target.IsUnicast ? null : MailEnvelope.DefaultTtlDeliveries,
                Body: original.Body,
                Prev: null);

            if (store.Append(copy) is MailAppend.Failed failed)
            {
                // Stop at the first failure and say exactly how far it got: the
                // copies already appended are real mail at a live address, and
                // a re-run will skip them rather than double them.
                LogForward(source, target, from, forwarded, skipped);
                stderr.WriteLine($"captainHook mail forward: {failed.Error}");
                stderr.WriteLine(forwarded.Count == 0
                    ? $"  nothing was forwarded from {source}"
                    : $"  {forwarded.Count} of {view.Pending.Count} forwarded before this — re-run to finish, "
                      + "those are skipped rather than doubled");
                return 1;
            }
            forwarded.Add(original.Id);
        }

        LogForward(source, target, from, forwarded, skipped);

        try
        {
            stdout.WriteLine(
                $"mail: forwarded {forwarded.Count} from {source} to {target}"
                + (skipped.Count > 0 ? $" ({skipped.Count} already forwarded, skipped)" : "")
                + " — the originals stay on the ledger");
        }
        catch (IOException) { /* a closed pipe cannot un-append an envelope */ }
        return 0;
    }

    /// Nothing to move, said once and successfully. Nothing is logged: no mail
    /// changed hands, and a trail line per empty forward would be a record of
    /// an event that did not happen (`mail reap`'s rule for its own no-op).
    private static int NothingToForward(TextWriter stdout, MailAddress source, string why)
    {
        stdout.WriteLine($"mail: nothing to forward from '{source}' — {why}");
        return 0;
    }

    /// The ids this mailbox has already had forwarded TO THIS TARGET. Both
    /// halves matter: forwarding one dead box's mail to two different roles is
    /// a legitimate thing an operator may do in two runs, and matching on the
    /// id alone would silently make the second run a no-op.
    ///
    /// Malformed lines are skipped, as everywhere else on this bus — an
    /// unreadable line is not evidence that a forward did or did not happen,
    /// and the cost of being wrong here is a duplicate rather than a loss.
    private static HashSet<string> AlreadyForwarded(
        IReadOnlyList<MailLine> lines, MailAddress source, MailAddress target)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var sourceText = source.ToString();
        var targetText = target.ToString();
        foreach (var line in lines)
        {
            if (line.Envelope is not { ForwardedFrom: { } f } e) continue;
            if (e.To == targetText && f.Address == sourceText) seen.Add(f.Id);
        }
        return seen;
    }

    /// `mail.forward` (d8: every reaper read is a trail line). ONE row per run,
    /// not one per envelope — each copy already writes its own `mail.append`,
    /// and the fact this row exists to record is the one they cannot show: that
    /// somebody moved another mailbox's mail, which mailbox, and where to.
    ///
    /// The source is spelled `role` + `instance`, the same two columns
    /// `mail.deliver`, `mail.cursorAdvance` and `mail.reap` use; the target is
    /// a single `to`, spelled as an envelope's `to` is, because that is what it
    /// literally became. `by` is the sender that was actually written, so the
    /// row and the envelopes cannot disagree about who did this.
    private static void LogForward(
        MailAddress source, MailAddress target, MailSender by,
        IReadOnlyList<string> forwarded, IReadOnlyList<string> skipped)
    {
        var data = new Dictionary<string, object>
        {
            ["role"] = source.Role,
            ["to"] = target.ToString(),
            ["by"] = by.Session is { } s ? $"{by.Agent}@{s}" : by.Agent,
            ["forwardedIds"] = forwarded.Select(MailEnvelope.ClampField).ToList(),
        };
        if (source.Instance is not null) data["instance"] = source.Instance;
        // Written only when there were any: a zero here would be a column about
        // the ordinary case, and the ordinary case is a first run.
        if (skipped.Count > 0) data["skippedIds"] = skipped.Select(MailEnvelope.ClampField).ToList();

        Log.Info("mail", "mail.forward", new LogFields
        {
            Msg = "stranded mail copied to a live address: the originals stay on the ledger",
            Data = data,
        });
    }

    private static string MintId() => "fwd-" + Guid.NewGuid().ToString("N")[..8];

    /// One positional source address, a required `--to`, an optional `--by`.
    /// All three go through `MailAddress.TryParse` — the envelope parser's own
    /// gate, never a second spelling of the grammar (`mail reap`'s rule).
    private static bool TryParseArgs(
        IReadOnlyList<string> argv,
        out MailAddress source, out MailAddress target, out MailAddress? by, out string? error)
    {
        source = default;
        target = default;
        by = null;
        error = null;

        string? sourceText = null, toText = null, byText = null;
        for (var i = 0; i < argv.Count; i++)
        {
            if (argv[i] == "--to" && i + 1 < argv.Count)
            {
                if (toText is not null) { error = "--to given twice"; return false; }
                toText = argv[++i];
            }
            else if (argv[i] == "--by" && i + 1 < argv.Count)
            {
                if (byText is not null) { error = "--by given twice"; return false; }
                byText = argv[++i];
            }
            else if (argv[i].StartsWith('-'))
            {
                error = $"unknown or incomplete argument '{argv[i]}'";
                return false;
            }
            else if (sourceText is not null)
            {
                // Two sources is a caller looping wrong, and forwarding the
                // first while ignoring the second is the silent half of it.
                error = $"unexpected argument '{argv[i]}' — forward takes exactly one source address";
                return false;
            }
            else sourceText = argv[i];
        }

        if (sourceText is null)
        {
            error = "a source address is required: " + MailAddress.GrammarHelp;
            return false;
        }
        if (!MailAddress.TryParse(sourceText, out source))
        {
            error = $"'{sourceText}' is not an address — {MailAddress.GrammarHelp}";
            return false;
        }
        if (toText is null)
        {
            error = "--to <address> is required — a forward has to go somewhere";
            return false;
        }
        if (!MailAddress.TryParse(toText, out target))
        {
            error = $"--to '{toText}' is not an address — {MailAddress.GrammarHelp}";
            return false;
        }
        if (byText is not null)
        {
            if (!MailAddress.TryParse(byText, out var parsedBy))
            {
                error = $"--by '{byText}' is not an address — {MailAddress.GrammarHelp}";
                return false;
            }
            by = parsedBy;
        }
        return true;
    }
}
