using CaptainHook.Actors;

namespace CaptainHook.Mail;

// Roadmap item 23 / ADR-0018 decision 6, slice `reap-verb` — `captainHook mail
// reap <address>`: the one sanctioned way a mailbox's STANDING is removed.
//
// A cursor file has always been deletable at any moment (ADR-0016 d13 says so
// out loud, and `mail.cursorVanished` is the warn a digest emits when it
// notices), so this verb adds no new power. What it adds is a RECORD: the
// difference between a mailbox that vanished — leaving a fresh anchor to
// redeliver everything with no explanation — and a mailbox that was disposed
// of, by somebody, with what it was still holding written down. d6's whole
// shape is that detection is automatic (the watcher, ADR-0017) and disposition
// is a member's; this is the verb that member calls when it has decided.
//
// **It does not judge.** Nothing here asks whether the mailbox is really dead,
// whether its window is live, or whether the mail it holds should have been
// forwarded first. It cannot know any of that — presence lives in the read
// model and disposition is the reaper's judgement (d6: forward / drop / hold,
// THEN reap). A verb that second-guessed its caller would either refuse a
// legitimate drop or invent a deadness test that disagrees with the watcher's,
// which is one more implementation of "is this box dead" than this subsystem
// can afford (ADR-0016 N8).
//
// **What survives.** Only the standing is gone. Every envelope the mailbox
// ever read, and every one it never did, stays on the append-only chain — this
// verb writes nothing to the store and reads it only to say what was pending.
// If the instance ever comes back, its next pickup is a fresh first contact,
// and the `mail.reap` line is what explains to a reader why it sees mail twice.
//
// A HUMAN/CLI verb like `send` and `doctor`: stdout is free for a confirmation
// line, and nothing here touches the sacred channel.
public static class MailReap
{
    public const string Usage =
        "usage: captainHook mail reap <role@instance> [--by <address>]   "
        + "(removes the mailbox's cursor; its mail stays on the ledger)";

    /// Reap one mailbox. `mailDir` and `lockWaitMs` are test seams; production
    /// passes neither.
    ///
    /// Exit 1 is for the two things a caller can fix — a bad argument and a
    /// lock it could not take — and nothing else. Reaping a mailbox that is
    /// already gone is exit 0: a reaper that ran twice, or raced another one,
    /// has got the outcome it asked for, and a disposal verb that failed on
    /// "already disposed" would make every retry look like a problem.
    public static int Run(
        IReadOnlyList<string> argv, TextWriter stdout, TextWriter stderr,
        string? mailDir = null, int lockWaitMs = MailStore.DefaultLockWaitMs)
    {
        if (!TryParseArgs(argv, out var mailbox, out var by, out var argError))
        {
            stderr.WriteLine($"captainHook mail reap: {argError}");
            stderr.WriteLine(Usage);
            return 1;
        }

        var cursors = new MailCursors(new MailStore(MailStore.ResolveDir(mailDir)));
        var path = cursors.CursorPath(mailbox.Role, mailbox.Instance);

        // Checked BEFORE the lock is taken, because taking it creates the lock
        // file: reaping a mailbox that does not exist must not leave a lock
        // behind for a mailbox that never did. Re-checked under the lock below
        // — this one is about litter, that one is about correctness.
        if (!File.Exists(path)) return NothingToReap(stdout, mailbox);

        MailPendingView view;
        FileStream? held = null;
        try
        {
            held = MailStore.TryLock(path + ".lock", lockWaitMs, out var lockError);
            if (held is null)
            {
                // A digest is mid-advance on this very cursor. Refusing is the
                // whole reason the lock is here: deleting the file underneath
                // a running advance would let it write its cursor back a
                // moment later, leaving a reaped mailbox standing again with
                // no line on the trail saying so.
                stderr.WriteLine($"captainHook mail reap: {lockError}");
                return 1;
            }

            // Authoritative under the lock: another reaper may have taken it
            // between the check above and the acquisition here.
            if (!File.Exists(path)) return NothingToReap(stdout, mailbox);

            // What this mailbox is still holding, read under the lock so the
            // record is exactly what was stranded at the moment its standing
            // ended — not a count that an advance could have moved in between.
            //
            // `hookSession` is null because there is no window here: a reap is
            // performed BY somebody (`--by`) ON a mailbox, and the address is
            // the whole of "which mailbox" (d3 as amended). For a bare role
            // that resolves to the sessionless reader's own cursor, which is
            // the same mailbox `Pending` would read for it.
            view = cursors.Pending(mailbox, hookSession: null);

            // THE COMMIT POINT. Everything that can fail is above it; nothing
            // below may turn a mailbox that IS reaped into a reported failure.
            File.Delete(path);
        }
        catch (Exception ex)   // permissions, a directory at the path, a vanished mail dir
        {
            stderr.WriteLine($"captainHook mail reap: cannot reap '{mailbox}': {ex.Message}");
            return 1;
        }
        finally
        {
            // The lock file itself is NOT unlinked. `flock` is on the inode:
            // unlinking it while holding it would let the next caller create a
            // fresh inode and take a lock that excludes nobody — the classic
            // lock-file race, and here it would race an ADVANCE rather than
            // another reap. A stray `cursor.<...>.json.lock` beside no cursor
            // is the cost, and it is invisible: `MailCursors.List` matches
            // `cursor.*.json` and a lock is not one, so no listing, snapshot
            // or canvas ever sees it.
            held?.Dispose();
        }

        // Reporting, outside the try and after the lock, because the mailbox is
        // already gone: a broken stdout (`mail reap … | head -1`) must not print
        // "cannot reap" about a reap that happened. The trail line comes AFTER
        // the delete, never before — a crash in that window loses the record of
        // a real reap, which reads on the ledger exactly like the bare deletion
        // d13 already tolerates, whereas the other order would put a reap that
        // never occurred on an append-only chain, and nothing can take it back.
        LogReap(mailbox, by, view);

        var pending = view.Pending.Count;
        try
        {
            stdout.WriteLine(
                $"mail: reaped {mailbox} — cursor removed, "
                + (pending == 0
                    ? "nothing was pending"
                    : $"{pending} envelope{(pending == 1 ? "" : "s")} still pending (on the ledger, undelivered)"));
        }
        catch (IOException) { /* a closed pipe cannot un-reap a mailbox */ }
        return 0;
    }

    /// Idempotent success, said once. Nothing is logged: no standing changed,
    /// and a trail line per no-op reap would be a record of an event that did
    /// not happen.
    private static int NothingToReap(TextWriter stdout, MailAddress mailbox)
    {
        stdout.WriteLine($"mail: nothing to reap — no mailbox at '{mailbox}'");
        return 0;
    }

    /// `mail.reap` (d6). The mailbox is spelled `role` + `instance`, the SAME
    /// two columns `mail.deliver` and `mail.cursorAdvance` use, rather than the
    /// single joined `address` the ADR's prose named: two spellings of "which
    /// mailbox" on one trail is the second implementation this subsystem keeps
    /// refusing to grow (N8), and a reader that already learned the advance's
    /// columns needs nothing new to follow a reap. `instance` is written only
    /// when the address has one — the same write-when-named rule, which here
    /// means the row for the sessionless reader's box carries a role alone.
    ///
    /// There is no `sessionId`: a reap has no window. `by` names WHO decided,
    /// as an address, and is absent when a human ran the verb by hand — which
    /// is the honest record of a human at a terminal, not a gap to fill in.
    private static void LogReap(MailAddress mailbox, MailAddress? by, MailPendingView view)
    {
        var data = new Dictionary<string, object>
        {
            ["role"] = mailbox.Role,
            // What was still waiting when the standing ended. Ids, as the
            // nudge and `mail.deliver` spell them (d6 names `pendingIds`);
            // clamped like every id on the trail. EXPIRED mail is not here —
            // it was already spent, and listing it would report as stranded
            // what no digest was ever going to hand over again.
            ["pendingIds"] = view.Pending.Select(p => MailEnvelope.ClampField(p.Envelope.Id)).ToList(),
        };
        if (mailbox.Instance is not null) data["instance"] = mailbox.Instance;
        if (by is not null) data["by"] = by.Value.ToString();

        Log.Info("mail", "mail.reap", new LogFields
        {
            Msg = "mailbox reaped: its standing is gone, its mail stays on the ledger",
            Data = data,
        });
    }

    /// One positional address and an optional `--by`. Both go through
    /// `MailAddress.TryParse` — the envelope parser's own gate, never a second
    /// spelling of the grammar: an address this verb accepted but no sender
    /// could write would name a mailbox that cannot exist, and `--by` naming
    /// an unwritable address would put a reaper on the ledger nobody can reach
    /// to ask about it.
    private static bool TryParseArgs(
        IReadOnlyList<string> argv, out MailAddress mailbox, out MailAddress? by, out string? error)
    {
        mailbox = default;
        by = null;
        error = null;

        string? target = null, byText = null;
        for (var i = 0; i < argv.Count; i++)
        {
            if (argv[i] == "--by" && i + 1 < argv.Count)
            {
                if (byText is not null) { error = "--by given twice"; return false; }
                byText = argv[++i];
            }
            else if (argv[i].StartsWith('-'))
            {
                error = $"unknown or incomplete argument '{argv[i]}'";
                return false;
            }
            else if (target is not null)
            {
                // Two addresses is a reaper looping wrong, and reaping the
                // first while ignoring the second would be the silent half of
                // that bug.
                error = $"unexpected argument '{argv[i]}' — reap takes exactly one address";
                return false;
            }
            else target = argv[i];
        }

        if (target is null)
        {
            error = "an address is required: " + MailAddress.GrammarHelp;
            return false;
        }
        if (!MailAddress.TryParse(target, out mailbox))
        {
            error = $"'{target}' is not an address — {MailAddress.GrammarHelp}";
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
