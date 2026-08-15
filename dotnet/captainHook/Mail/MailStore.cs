using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CaptainHook.Actors;

namespace CaptainHook.Mail;

// Roadmap item 20 / ADR-0016 decision 11 — the DURABLE mail store: a
// hash-chained JSONL file whose appends are serialized by an flock'd lock file.
// Phase 1 gave the envelope its parser; this file gives the envelope a place to
// live, and settles the three things phase 1 deliberately left open — the
// genesis convention, the `prev` encoding, and what happens to a torn tail.
//
// EVERYTHING HERE IS A DURABLE FORMAT DECISION. Bytes written by this file
// outlive the code that wrote them (d13: the mail store is the longest-lived
// thing on disk, the inter-agent influence record), so each choice is stated
// with its reason rather than left to the implementation:
//
//   * THE LINE is canonical JSON in a fixed field order, default-encoder
//     escaped (the trail's discipline, WireJsonl), LF-terminated on every
//     platform. ASCII on disk, exactly one line per envelope, and a `\n` in a
//     body can never become a framing break.
//   * `prev` IS THE LOWERCASE-HEX SHA-256 OF THE PREVIOUS LINE'S EXACT BYTES,
//     EXCLUDING its terminating newline. The terminator is framing, not
//     content: excluding it means a reader that splits on '\n' hashes precisely
//     what it read, and an unterminated (torn) line hashes by the same rule as
//     every other.
//   * GENESIS is 64 zeros — the one `prev` that means "nothing precedes me".
//     An ABSENT `prev` would have been cheaper and is wrong: it cannot
//     distinguish "the first line" from "someone deleted the first line", and
//     head-deletion is exactly the edit the chain exists to expose.
//   * A TORN TAIL is TERMINATED, NEVER REPAIRED. The store appends a newline
//     after a partial line and chains onto its bytes as they are. Truncating or
//     rewriting a partial line to tidy the file is the silent history edit this
//     whole mechanism exists to prevent.
//   * ROTATION (d13's `gen`) STARTS A NEW CHAIN — settled now, before any real
//     byte exists, because genesis is being pinned: when a generation is
//     archived (phase 3+), the successor file's first line declares GENESIS
//     again, so every generation is an independent, self-verifiable chain. A
//     cross-file `prev` (gen N+1's first line = hash of gen N's tail) was
//     considered and REFUSED: it would make a file unverifiable in isolation,
//     and d13 archives and prunes generations independently. The honest cost,
//     same class as tail truncation below: deleting an ENTIRE archived
//     generation is invisible to `prev` — continuity across generations is
//     owned by the cursor's `gen` and the rotation record, never by the chain.
//
// What the chain does and does not buy (d11, stated honestly): rewriting,
// deleting, or re-ordering a line is DETECTABLE — the goal is converting silent
// tampering into detectable tampering, and it catches accidental corruption
// long before malice. Truncating the TAIL is NOT detectable from the chain
// alone; a shortened chain is internally consistent, and telling it apart from
// "no mail has arrived yet" needs an external record of where the chain reached
// (phase 3's cursor offsets). Nothing here stops a determined same-user
// attacker who re-chains the whole file — that trust boundary is accepted and
// unchanged (d11), and re-chaining is not silent to anyone holding a cursor.
//
// The flock is a CORRECTNESS requirement, not a nicety: appending is a
// read-last-line-then-write, so two unsynchronized senders would read the same
// last line, compute the same `prev`, and fork the chain. Multi-process
// `mail send` (phase 3) needs it anyway.
//
// The oversized-line question (phase 2's named carry-in) is CLOSED AT THE
// WRITE: `MaxLineBytes` caps what Append will store, so no writer — the send
// verb included — can produce a line a windowed reader would drop. The chain
// still hashes any length it FINDS (a windowed hash would break on bytes some
// other writer forced in), and the cursor's reader is windowless anyway; the
// cap exists so every future windowed reader is guaranteed a fit, instead of
// mail being written durably and never delivered.

/// The result of one append — a closed set on the `PolicyResolution` precedent,
/// so a caller must handle both cases. NEVER a throw: an append reaches here
/// from a user's own process (`mail send`) and from handler payloads, and a
/// store that throws takes a sender down over a permission bit.
public abstract record MailAppend
{
    private MailAppend() { }   // closed set: exactly the two below

    /// `Offset` is the byte position where the line STARTS — phase 3's cursor
    /// anchors on exactly this number. `Bytes` is the line's UTF-8 length
    /// excluding its terminator, so Offset + Bytes + 1 is the next line's start.
    public sealed record Appended(long Offset, int Bytes, string Prev, string Line) : MailAppend;

    public sealed record Failed(string Error) : MailAppend;
}

/// One line as it exists ON DISK. `Text` is the decoded line (invalid UTF-8
/// decodes to U+FFFD rather than throwing) and is what the parser reads; `Hash`
/// is over the RAW bytes, so it stays exact even when the text is lossy — the
/// chain's currency is bytes, never a re-render. `Terminated` false means the
/// file ends mid-write: a torn tail, which a reader must be able to tell from a
/// clean end of file.
public sealed record MailLine(
    long Offset,
    int Bytes,
    string Text,
    bool Terminated,
    string Hash,
    MailEnvelope? Envelope,
    IReadOnlyList<string> Errors);

/// Why a line's link does not hold. Each kind is a different accusation, and
/// the store never guesses between them.
public enum MailChainFaultKind
{
    /// The FIRST line does not declare genesis — the head of the store was
    /// removed (or the file was assembled from the middle of another).
    Genesis,

    /// The line declares a link that is not the hash of what precedes it: a
    /// line was rewritten, deleted, or moved.
    PrevMismatch,

    /// The line parses but carries no `prev` at all — appended by something
    /// that is not this store. Unchained history must never read as genuine.
    PrevMissing,

    /// The line does not parse, so its own link cannot be checked. Reported
    /// once, against itself: the NEXT line is still verified against this
    /// line's actual bytes, so one corruption stays one fault.
    Unreadable,
}

public sealed record MailChainFault(long Offset, MailChainFaultKind Kind, string Detail);

public sealed class MailStore(string dir)
{
    /// The `prev` of the first line in the chain: 64 zeros. A SHA-256 is never
    /// all-zero in practice, so the sentinel is unambiguous.
    public const string Genesis = "0000000000000000000000000000000000000000000000000000000000000000";

    /// The most bytes one line (terminator included) may occupy on disk —
    /// TrailCursor's 128KiB read-window precedent, adopted as the mail
    /// format's hard cap so a line always fits a windowed reader. Enforced at
    /// Append; a body that renders past it is REFUSED, never truncated (a
    /// truncated body would be the store rewriting what a sender said).
    public const int MaxLineBytes = 128 * 1024;

    /// How long an append waits for the lock before giving up. Generous
    /// (appends are milliseconds and mail volume is a human/agent cadence), but
    /// BOUNDED: a sender that blocks forever on a stale lock is worse than one
    /// that says it could not write.
    public const int DefaultLockWaitMs = 5_000;

    private const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite;             // 0600
    private const UnixFileMode PrivateDir = Private | UnixFileMode.UserExecute;                      // 0700

    public string Dir { get; } = dir;
    public string FilePath { get; } = Path.Combine(dir, "mail.jsonl");

    /// The append lock. A SEPARATE file from the store, and never unlinked —
    /// DaemonRendezvous's rule for the same reason: deleting a held lock lets
    /// the next writer lock a FRESH INODE at the same path and silently break
    /// mutual exclusion. Locking the store file itself would be worse still,
    /// since a reader opening it must never contend with a writer.
    public string LockPath { get; } = Path.Combine(dir, "mail.lock");

    /// The mail directory this process reads: explicit, else the env var, else
    /// ~/.captainHook/mail. The DIRECTORY rather than the file, because phase
    /// 3's per-(role, session) cursors live beside the store (d4) and the two
    /// must never be pointed at different places. Mirrors
    /// DispatchPolicy.ResolvePath / HarnessRegistry.ResolveDir.
    public static string ResolveDir(string? overrideDir = null) =>
        overrideDir
        ?? Environment.GetEnvironmentVariable("CAPTAINHOOK_MAIL_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        ".captainHook", "mail");

    /// Append one envelope, chained onto whatever is currently last. The
    /// envelope's own `Prev` is IGNORED: `prev` is a property of the line's
    /// POSITION, not of the message — the same envelope appended twice
    /// necessarily gets different links — so honoring a sender's value could
    /// only ever corrupt the chain (or forge it, when the sender is a member
    /// copying a stored line back into a send).
    public MailAppend Append(MailEnvelope envelope, int lockWaitMs = DefaultLockWaitMs)
    {
        FileStream? held = null;
        try
        {
            Directory.CreateDirectory(Dir, PrivateDir);
            // CreateDirectory's mode applies only when it CREATES the dir; an
            // earlier component may have made ~/.captainHook at the umask
            // default, so tighten explicitly (DaemonRendezvous's reasoning —
            // this tree is the trust root for a 0600 record).
            try { File.SetUnixFileMode(Dir, PrivateDir); } catch { /* not ours / odd FS */ }

            held = AcquireLock(lockWaitMs, out var lockError);
            if (held is null) return new MailAppend.Failed(lockError!);

            var (prev, torn, length, tornStart) = TailLink();
            var line = Render(envelope, prev);

            // EVERY LINE THE STORE WRITES RE-PARSES CLEAN — enforced, not
            // assumed. The envelope arrived as an in-process VALUE no parser
            // has vouched for (ttl 0, an out-of-range enum, a blank `to` all
            // render without complaint), and a line the strict parser rejects
            // would be warned-and-skipped by every future reader: mail
            // silently lost behind a successful-looking append, the exact
            // too-loose failure the envelope's direction-of-failure note
            // warns about. Refusing here is the write-side mirror of the
            // reader's warn-and-skip, and it validates the exact bytes about
            // to become durable.
            if (MailEnvelope.TryParseLine(line, out var violations) is null)
                return new MailAppend.Failed(
                    "refusing to append a line the strict parser would reject: "
                    + string.Join("; ", violations));

            var lineBytes = Encoding.UTF8.GetByteCount(line);
            if (lineBytes + 1 > MaxLineBytes)
                return new MailAppend.Failed(
                    $"refusing to append a {lineBytes}-byte line: the format caps a line "
                    + $"(with terminator) at {MaxLineBytes} bytes so it always fits a windowed reader");

            // The file is created through a CREATING mode so UnixCreateMode
            // actually applies (PosixTrail's create-first idiom), then opened
            // for append. Both happen under the lock, so the two-step is not a
            // race.
            if (!File.Exists(FilePath))
                using (new FileStream(FilePath, new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.ReadWrite,
                    UnixCreateMode = Private,
                }))
                { }

            // One write for the whole payload. A torn tail gets its missing
            // terminator here — prepended to THIS write rather than patched
            // separately, so a crash mid-append can never leave the file in a
            // state this code has not already thought about (it is just another
            // torn tail).
            var payload = Encoding.UTF8.GetBytes((torn ? "\n" : "") + line + "\n");
            using (var fs = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                fs.Write(payload);

            // No fsync, deliberately, on the trail's precedent: the bytes are
            // visible to every other process the moment write(2) returns, so
            // only machine-level power loss can lose the tail — and that lands
            // as the torn-final-line case this format already handles without
            // losing the chain. Revisit if a real durability incident appears.

            var offset = length + (torn ? 1 : 0);
            if (torn)
                // The offset reported is the TORN line's own start — the line
                // the warn is about — not the new line's.
                Log.Warn("mail", "mail.torn", new LogFields
                {
                    Msg = "terminated a torn final line rather than repairing it",
                    Data = new Dictionary<string, object> { ["path"] = FilePath, ["offset"] = tornStart },
                });

            // PROVENANCE, NOT CONTENT (ADR-0016 d14). The trail is the live
            // signal the mail canvas animates from, and an arrival it cannot
            // characterize is a dot with no story: who sent it, what class it
            // is, how urgently it asked to be delivered, and how many
            // opportunities it survives. All of that rides here — and the
            // `body` NEVER does. The trail is operational-lifetime and
            // payload-readable (exec.stderr already puts arbitrary process
            // output in it); the body belongs to the archival store alone,
            // which is the one surface with the lifetime and the modes for it.
            // The two sender-controlled strings are clamped by the same
            // spelling the digest head uses, so an id here and an id in a
            // rendered digest are the same string. `bytes` (the line's
            // length, terminator excluded — `MailLine.Bytes`) rides beside
            // `offset` so a reader tailing these events knows where the NEXT
            // line must start: the store's frontier becomes derivable from
            // the trail alone, and an append that lands anywhere else is a
            // gap the reader can name instead of a picture that quietly
            // drifts.
            var from = new Dictionary<string, object> { ["agent"] = envelope.From.Agent };
            from["harness"] = envelope.From.Harness;
            // Absent-means-omit, the trail's rule: a write-only member has no
            // session, and spelling that null would make every consumer test
            // for it.
            if (envelope.From.Session is { } session) from["session"] = session;

            Log.Debug("mail", "mail.append", new LogFields
            {
                Data = new Dictionary<string, object>
                {
                    ["id"] = MailEnvelope.ClampField(envelope.Id),
                    ["to"] = envelope.To,
                    ["offset"] = offset,
                    ["bytes"] = lineBytes,
                    ["from"] = from,
                    ["kind"] = Wire(envelope.Kind),
                    ["topic"] = MailEnvelope.ClampField(envelope.Topic),
                    ["priority"] = Wire(envelope.Priority),
                    ["ttlDeliveries"] = envelope.TtlDeliveries,
                },
            });

            return new MailAppend.Appended(offset, lineBytes, prev, line);
        }
        catch (Exception ex)   // a directory at the path, permissions, a full disk
        {
            return new MailAppend.Failed($"cannot append to '{FilePath}': {ex.Message}");
        }
        finally { held?.Dispose(); }
    }

    /// Every line on disk, in order, each parsed through phase 1's strict
    /// parser. A line that does not parse is RETURNED with its errors rather
    /// than dropped — the reader (phase 4's digest) warns-and-skips it, and a
    /// caller walking offsets must still see the bytes it is stepping over.
    /// Never throws: an unreadable store reads as empty.
    public IReadOnlyList<MailLine> Read()
    {
        byte[] bytes;
        try
        {
            if (!File.Exists(FilePath)) return [];
            bytes = File.ReadAllBytes(FilePath);
        }
        catch (Exception) { return []; }   // permissions, a directory, a mid-write race

        var lines = new List<MailLine>();
        var start = 0;
        for (var i = 0; i <= bytes.Length; i++)
        {
            var atEnd = i == bytes.Length;
            if (!atEnd && bytes[i] != (byte)'\n') continue;
            if (atEnd && start == i) break;   // trailing terminator: no unterminated remainder

            var span = bytes.AsSpan(start, i - start);
            // Decode for PARSING; hash the RAW bytes. Invalid UTF-8 decodes to
            // U+FFFD (never a throw), which would change a re-encoded hash — so
            // the hash never goes near the decoded text.
            var text = Encoding.UTF8.GetString(span);
            var envelope = MailEnvelope.TryParseLine(text, out var errors);
            lines.Add(new MailLine(start, span.Length, text, !atEnd, HashOf(span), envelope, errors));
            start = i + 1;
        }
        return lines;
    }

    /// Walk the chain and report every link that does not hold (d11's whole
    /// point: silent tampering becomes detectable tampering). An empty result
    /// means every line is linked to the exact bytes before it, back to genesis.
    ///
    /// The expected link is computed from the bytes ACTUALLY on disk rather than
    /// from a running "what it should have been", so ONE corruption produces ONE
    /// fault instead of a cascade that buries the incident in noise.
    public IReadOnlyList<MailChainFault> VerifyChain()
    {
        var faults = new List<MailChainFault>();
        var expected = Genesis;

        foreach (var (line, index) in Read().Select((l, i) => (l, i)))
        {
            if (line.Envelope is null)
            {
                // An UNTERMINATED unreadable tail is a distinct situation from
                // garbage mid-file: it is what an interrupted write — or an
                // append IN FLIGHT right now, since readers deliberately never
                // take the lock — looks like, and the next append terminates
                // and chains over it. The kind stays Unreadable (it is), but
                // the detail must not read as a tamper accusation an operator
                // would false-alarm on.
                var tail = line.Terminated
                    ? ""
                    : " (unterminated final line: an interrupted write or an append in flight, not necessarily tampering)";
                faults.Add(new MailChainFault(line.Offset, MailChainFaultKind.Unreadable,
                    $"line does not parse, so its link cannot be checked: {string.Join("; ", line.Errors)}{tail}"));
            }
            else if (index == 0 && line.Envelope.Prev != Genesis)
                // The first line must DECLARE genesis. Both "no prev" and "some
                // other hash" mean the same thing here — the head of this file
                // is not the head of the chain.
                faults.Add(new MailChainFault(line.Offset, MailChainFaultKind.Genesis,
                    $"first line declares prev '{line.Envelope.Prev ?? "(absent)"}', expected genesis"));
            else if (index > 0 && line.Envelope.Prev is null)
                faults.Add(new MailChainFault(line.Offset, MailChainFaultKind.PrevMissing,
                    "line carries no 'prev': appended by something other than this store"));
            else if (index > 0 && line.Envelope.Prev != expected)
                faults.Add(new MailChainFault(line.Offset, MailChainFaultKind.PrevMismatch,
                    $"line declares prev '{line.Envelope.Prev}', but the preceding line hashes to '{expected}'"));

            expected = line.Hash;
        }
        return faults;
    }

    /// The canonical line for one envelope at one position — no trailing
    /// newline. Public because it IS the format: the golden test pins it
    /// literally, and phase 3's `mail send` renders through here rather than
    /// growing a second serializer.
    ///
    /// Utf8JsonWriter with DEFAULT options, matching WireJsonl: the default
    /// encoder escapes control characters and every non-ASCII rune, so a line is
    /// pure ASCII on disk, cannot carry a raw newline through a body, and cannot
    /// be mangled by a tool that guesses an encoding. Invalid UTF-16 (a lone
    /// surrogate a C# caller can construct) is written as U+FFFD rather than
    /// throwing — the never-throw-on-DATA contract, extended to the writer.
    public static string Render(MailEnvelope e, string prev)
    {
        var buf = new ArrayBufferWriter<byte>(512);
        using (var w = new Utf8JsonWriter(buf))
        {
            w.WriteStartObject();
            w.WriteNumber("v", 1);
            w.WriteString("id", e.Id);
            w.WriteString("ts", e.Ts);

            w.WritePropertyName("from");
            w.WriteStartObject();
            w.WriteString("agent", e.From.Agent);
            w.WriteString("harness", e.From.Harness);
            // Absent optionals are OMITTED, not spelled null (the trail's
            // absent-means-omit rule). The parser accepts both, so this is only
            // a choice about what we write, and the shorter line is the
            // archival record's friend.
            if (e.From.Session is not null) w.WriteString("session", e.From.Session);
            w.WriteEndObject();

            w.WriteString("to", e.To);
            w.WriteString("kind", Wire(e.Kind));
            w.WriteString("topic", e.Topic);
            // priority and ttlDeliveries are optional on the wire but ALWAYS
            // written: the stored line is the durable record, so a defaulted
            // value is frozen at write time rather than re-derived by a future
            // reader whose default has moved.
            w.WriteString("priority", Wire(e.Priority));
            if (e.InReplyTo is not null) w.WriteString("inReplyTo", e.InReplyTo);
            w.WriteNumber("ttlDeliveries", e.TtlDeliveries);
            w.WriteString("body", e.Body);
            // `prev` LAST: every field the SENDER wrote, then the one field the
            // STORE wrote. The line reads in the order it was decided.
            w.WriteString("prev", prev);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buf.WrittenSpan);
    }

    /// The chain's hash function, in one place: lowercase hex SHA-256 over
    /// exactly these bytes (PolicyContent's encoding, d12 — one hash spelling
    /// across the project so a ledger hash and a chain link are read the same
    /// way).
    public static string HashOf(ReadOnlySpan<byte> lineBytes) =>
        Convert.ToHexStringLower(SHA256.HashData(lineBytes));

    /// The store's CURRENT chain identity: the first COMPLETE line's hash,
    /// null when no complete first line exists (absent, empty, or torn-only
    /// store). This is the same head rule `MailCursors.Pending` applies to its
    /// full read — the two MUST agree (pinned by test), because Advance
    /// re-checks this value under its lock and a drift between the two rules
    /// would either refuse every advance or silently disable the chain-changed
    /// guard. Reads only the first line, not the store (N4: the store grows
    /// unbounded; a guard must not cost a full read).
    public string? HeadHash()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            using var fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var first = new MemoryStream();
            var buf = new byte[8 * 1024];
            int read;
            while ((read = fs.Read(buf, 0, buf.Length)) > 0)
            {
                var nl = Array.IndexOf(buf, (byte)'\n', 0, read);
                if (nl >= 0)
                {
                    first.Write(buf, 0, nl);
                    return HashOf(first.GetBuffer().AsSpan(0, (int)first.Length));
                }
                first.Write(buf, 0, read);
            }
            return null;   // no terminator anywhere: a torn-only store has no head yet
        }
        catch (Exception) { return null; }   // unreadable now ≡ no identifiable chain
    }

    /// The link the NEXT line must declare, plus whether the current tail is
    /// torn, the file's current length, and the tail line's own start offset
    /// (meaningful when Torn: it is the number the `mail.torn` warn reports).
    /// Reads only the tail: an append must not cost a full read of a store
    /// that grows unbounded (N4).
    private (string Prev, bool Torn, long Length, long TailStart) TailLink()
    {
        if (!File.Exists(FilePath)) return (Genesis, false, 0, 0);

        using var fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var length = fs.Length;
        if (length == 0) return (Genesis, false, 0, 0);

        // Grow the window until the line's start is found, so a line longer
        // than any fixed window still hashes WHOLE — a windowed hash would
        // silently break the chain on the first long body.
        var window = 8 * 1024;
        while (true)
        {
            var from = Math.Max(0, length - window);
            var buf = new byte[length - from];
            fs.Position = from;
            fs.ReadExactly(buf);

            var torn = buf[^1] != (byte)'\n';
            var contentEnd = torn ? buf.Length : buf.Length - 1;   // drop the terminator: framing, not content
            var nl = buf.AsSpan(0, contentEnd).LastIndexOf((byte)'\n');

            if (nl < 0 && from > 0)
            {
                window *= 4;
                continue;
            }
            var lineStart = nl + 1;   // nl < 0 (start of file) gives 0, which is right
            return (HashOf(buf.AsSpan(lineStart, contentEnd - lineStart)), torn, length, from + lineStart);
        }
    }

    /// Take the append lock, or say why not. `FileShare.None` is flock(LOCK_EX |
    /// LOCK_NB) on Unix — advisory, exclusive, and released by the KERNEL on any
    /// death, crash and SIGKILL included, which is what makes a sender that dies
    /// mid-append harmless (the next writer simply reads the same last line).
    /// The BCL has no blocking acquire, so waiting is a bounded retry — timed on
    /// Stopwatch, never the wall clock (house invariant 2: a WSL2 clock step
    /// would otherwise expire the wait instantly, the exact bug platform.md
    /// § Wall-clock steps records).
    private FileStream? AcquireLock(int waitMs, out string? error) =>
        TryLock(LockPath, waitMs, out error);

    /// Shared with MailCursors: a cursor advance is the same read-then-write
    /// shape as the chained append, and needs the same serialization for the
    /// same reason.
    internal static FileStream? TryLock(string lockPath, int waitMs, out string? error)
    {
        error = null;
        var sw = Stopwatch.StartNew();
        var delayMs = 1;
        while (true)
        {
            try
            {
                return new FileStream(lockPath, new FileStreamOptions
                {
                    Mode = FileMode.OpenOrCreate,
                    Access = FileAccess.ReadWrite,
                    Share = FileShare.None,
                    UnixCreateMode = Private,
                });
            }
            catch (IOException) { /* held by another writer: wait and retry */ }

            if (sw.ElapsedMilliseconds >= waitMs)
            {
                error = $"lock '{lockPath}' is held by another writer (waited {waitMs}ms)";
                Log.Warn("mail", "mail.lockBusy", new LogFields
                {
                    Msg = error,
                    Data = new Dictionary<string, object>
                    {
                        ["path"] = lockPath, ["waitedMs"] = sw.ElapsedMilliseconds,
                    },
                });
                return null;
            }
            Thread.Sleep(delayMs);
            delayMs = Math.Min(delayMs * 2, 20);
        }
    }

    /// The wire spelling of a closed set is its member name, lowercased — the
    /// spelling MailEnvelope.TryEnum accepts on the way back in.
    private static string Wire<T>(T value) where T : struct, Enum =>
        value.ToString()!.ToLowerInvariant();
}
