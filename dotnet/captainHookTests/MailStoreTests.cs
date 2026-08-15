using System.Text;
using System.Text.Json;
using CaptainHook.Actors;
using CaptainHook.Mail;

namespace CaptainHook.Tests;

/// ADR-0016 decision 11 — the hash-chained, flock-serialized mail store
/// (roadmap item 20, phase 2). These tests are written FIRST and pin the
/// DURABLE ON-DISK FORMAT: the canonical line, the genesis convention, what
/// exactly `prev` hashes, and how a torn tail is treated. Nothing may write
/// real mail before this format is settled, because every one of these choices
/// is unfixable once a store exists on a maintainer's disk.
///
/// The threat model is decision 11's, and it sets what a test must prove: exec
/// members run AS THE USER, so the processes with motive and opportunity to
/// rewrite history are exactly the swarm. The goal is not preventing tampering
/// (same-user boundary, accepted) — it is converting SILENT tampering into
/// DETECTABLE tampering. So "the chain verifies" is only half a test; the other
/// half is that a rewritten, deleted, or re-ordered line is FOUND.
public sealed class MailStoreTempDir : IDisposable
{
    public string Dir { get; } =
        Path.Combine(Path.GetTempPath(), "captainhook-mail-" + Guid.NewGuid().ToString("N"));

    public MailStoreTempDir() => Directory.CreateDirectory(Dir);
    public MailStore Store() => new(Dir);
    public string FilePath => Path.Combine(Dir, "mail.jsonl");

    /// The raw bytes on disk — every assertion about the format goes through
    /// bytes, never through a re-render, so a serializer change cannot make a
    /// format test pass by moving both sides.
    public byte[] Bytes() => File.Exists(FilePath) ? File.ReadAllBytes(FilePath) : [];
    public string Text() => Encoding.UTF8.GetString(Bytes());
    public string[] Lines() =>
        Text().Split('\n', StringSplitOptions.RemoveEmptyEntries);

    public void Dispose()
    {
        try { Directory.Delete(Dir, recursive: true); } catch { /* best-effort */ }
    }
}

internal static class MailFixtures
{
    /// The ADR-0016 d2 envelope, as a value. `Prev` is null — a freshly built
    /// envelope is unchained; the STORE writes that field (d11).
    public static MailEnvelope Envelope(
        string id = "m-01",
        string to = "main",
        string body = "opaque prose",
        MailPriority priority = MailPriority.Ambient,
        string? session = "s-77",
        string? inReplyTo = null,
        int ttl = 3) =>
        new(
            Id: id,
            Ts: "2026-08-12T19:00:00Z",
            From: new MailSender("intent-watcher", "claude-code", session),
            To: to,
            Kind: MailKind.Status,
            Topic: "build",
            Priority: priority,
            InReplyTo: inReplyTo,
            TtlDeliveries: ttl,
            Body: body,
            Prev: null);

    public static MailAppend.Appended AppendOk(MailStore store, MailEnvelope e)
    {
        var r = store.Append(e);
        Assert.True(r is MailAppend.Appended, $"expected append to succeed, got: {(r as MailAppend.Failed)?.Error}");
        return (MailAppend.Appended)r;
    }

    public static string Sha256Hex(string s) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(s)));
}

// ---- the canonical line: the format itself ---------------------------------

public class MailStoreFormatTests
{
    /// The GOLDEN LINE. This is the durable format in one assertion: field
    /// order, lowercase wire spellings for the closed sets, omit-null for the
    /// two optionals, and `prev` LAST (the store's own field, after every field
    /// the sender wrote). A change here is a change to bytes already on
    /// somebody's disk — which is exactly why it is pinned literally rather
    /// than round-tripped.
    [Fact]
    public void Render_IsTheCanonicalLine()
    {
        var line = MailStore.Render(MailFixtures.Envelope(), MailStore.Genesis);

        Assert.Equal(
            """
            {"v":1,"id":"m-01","ts":"2026-08-12T19:00:00Z","from":{"agent":"intent-watcher","harness":"claude-code","session":"s-77"},"to":"main","kind":"status","topic":"build","priority":"ambient","ttlDeliveries":3,"body":"opaque prose","prev":"0000000000000000000000000000000000000000000000000000000000000000"}
            """,
            line);
    }

    /// Absent optionals are OMITTED, not spelled null — the trail's rule
    /// (WireJsonl's absent-means-omit), applied to the two fields a sender may
    /// leave out. The parser accepts both spellings, so this is a choice about
    /// what WE write, and the shorter line is the archival record's friend.
    [Fact]
    public void Render_OmitsAbsentSessionAndInReplyTo()
    {
        var line = MailStore.Render(
            MailFixtures.Envelope(session: null, inReplyTo: null), MailStore.Genesis);

        Assert.DoesNotContain("session", line);
        Assert.DoesNotContain("inReplyTo", line);
        Assert.Contains("""{"agent":"intent-watcher","harness":"claude-code"}""", line);
    }

    [Fact]
    public void Render_WritesInReplyToWhenPresent()
    {
        var line = MailStore.Render(MailFixtures.Envelope(inReplyTo: "m-00"), MailStore.Genesis);
        Assert.Contains("""
            "inReplyTo":"m-00"
            """.Trim(), line);
    }

    /// `priority` and `ttlDeliveries` are OPTIONAL on the wire but ALWAYS
    /// written. The stored line is the durable record: a defaulted value must be
    /// frozen at write time, never re-derived by a future reader whose default
    /// has moved. (If DefaultTtlDeliveries ever changes, old mail must keep
    /// meaning what it meant.)
    [Fact]
    public void Render_AlwaysWritesDefaultedPriorityAndTtl()
    {
        var line = MailStore.Render(MailFixtures.Envelope(), MailStore.Genesis);
        Assert.Contains("\"priority\":\"ambient\"", line);
        Assert.Contains("\"ttlDeliveries\":3", line);
    }

    /// A body containing a newline must not become two lines — JSONL framing is
    /// the store's whole structure, and the chain hashes lines. The default
    /// encoder (the trail's, deliberately) escapes it.
    [Fact]
    public void Render_EscapesNewlinesSoAlineStaysOneLine()
    {
        var line = MailStore.Render(
            MailFixtures.Envelope(body: "first\nsecond\r\nthird"), MailStore.Genesis);

        Assert.DoesNotContain("\n", line);
        Assert.DoesNotContain("\r", line);
        Assert.Contains("\\n", line);
    }

    /// Every line the store writes must re-parse through phase 1's strict
    /// parser — the two halves of the contract, joined. Note `prev` survives the
    /// round trip: phase 1 reserved the field name for exactly this moment.
    [Fact]
    public void Render_RoundTripsThroughTheStrictParser()
    {
        var original = MailFixtures.Envelope(inReplyTo: "m-00");
        var line = MailStore.Render(original, MailStore.Genesis);

        var parsed = MailEnvelope.TryParseLine(line, out var errors);

        Assert.True(parsed is not null, string.Join("; ", errors));
        Assert.Equal(original with { Prev = MailStore.Genesis }, parsed);
    }

    /// Unicode is escaped rather than emitted raw — the trail's default-encoder
    /// discipline (WireJsonl), so a mail line is pure ASCII on disk and cannot
    /// be mangled by a tool that guesses an encoding. The chain hashes bytes, so
    /// "which bytes" is a format decision, not an aesthetic one.
    [Fact]
    public void Render_EscapesNonAsciiLikeTheTrailDoes()
    {
        var line = MailStore.Render(MailFixtures.Envelope(body: "café 日本"), MailStore.Genesis);

        Assert.All(line, c => Assert.True(c < 128, $"non-ASCII char in line: {c}"));
        Assert.Contains("\\u", line);
        Assert.Equal("café 日本", MailEnvelope.TryParseLine(line, out _)!.Body);
    }

    /// Every member of BOTH closed sets round-trips store → parser, looped
    /// exhaustively rather than sampled: `Wire`'s lowercased member name must
    /// be a spelling `TryEnum` accepts for every member there will ever be —
    /// the guard for a future member whose wire spelling drifts from its name.
    [Fact]
    public void Render_RoundTripsEveryKindAndPriority()
    {
        foreach (var kind in Enum.GetValues<MailKind>())
            foreach (var priority in Enum.GetValues<MailPriority>())
            {
                var line = MailStore.Render(
                    MailFixtures.Envelope() with { Kind = kind, Priority = priority },
                    MailStore.Genesis);
                var parsed = MailEnvelope.TryParseLine(line, out var errors);

                Assert.True(parsed is not null, $"{kind}/{priority}: {string.Join("; ", errors)}");
                Assert.Equal(kind, parsed!.Kind);
                Assert.Equal(priority, parsed.Priority);
            }
    }

    /// NEVER THROW on bad DATA, extended to the writer: a body carrying a lone
    /// surrogate (unpaired UTF-16, unrepresentable in UTF-8) is a value a C#
    /// caller can construct, and the store must still produce one parseable
    /// line rather than taking down the sender.
    [Fact]
    public void Render_SurvivesALoneSurrogateInTheBody()
    {
        var line = MailStore.Render(MailFixtures.Envelope(body: "bad \ud800 tail"), MailStore.Genesis);

        Assert.DoesNotContain("\n", line);
        Assert.NotNull(MailEnvelope.TryParseLine(line, out var errors));
        Assert.Empty(errors);
    }
}

// ---- genesis and the chain link -------------------------------------------

public class MailStoreChainTests
{
    /// GENESIS: 64 zeros, the one `prev` value that means "nothing precedes
    /// me". A hash is never all-zero in practice, so the sentinel is
    /// unambiguous — and it makes head-truncation DETECTABLE, which an absent
    /// `prev` would not (an absent field cannot distinguish "first line" from
    /// "someone deleted the first line").
    [Fact]
    public void FirstLine_ChainsToGenesis()
    {
        using var tmp = new MailStoreTempDir();
        var appended = MailFixtures.AppendOk(tmp.Store(), MailFixtures.Envelope());

        Assert.Equal(MailStore.Genesis, appended.Prev);
        Assert.Equal(64, MailStore.Genesis.Length);
        Assert.All(MailStore.Genesis, c => Assert.Equal('0', c));
        Assert.Equal(MailStore.Genesis, MailEnvelope.TryParseLine(tmp.Lines()[0], out _)!.Prev);
    }

    /// The link, stated exactly: `prev` is the lowercase-hex SHA-256 of the
    /// PREVIOUS LINE'S EXACT BYTES, EXCLUDING its terminating newline. The
    /// terminator is framing, not content — excluding it is what lets a reader
    /// split on '\n' and hash precisely what it read, and what makes a torn
    /// (unterminated) final line hashable on the same rule as any other.
    [Fact]
    public void SecondLine_ChainsToTheHashOfTheFirstLinesBytes()
    {
        using var tmp = new MailStoreTempDir();
        var store = tmp.Store();

        var first = MailFixtures.AppendOk(store, MailFixtures.Envelope(id: "m-01"));
        var second = MailFixtures.AppendOk(store, MailFixtures.Envelope(id: "m-02"));

        Assert.Equal(MailFixtures.Sha256Hex(first.Line), second.Prev);
        Assert.Equal(64, second.Prev.Length);
        Assert.Equal(second.Prev.ToLowerInvariant(), second.Prev);
    }

    /// `prev` is a property of the line's POSITION, never of the message: the
    /// SAME envelope appended twice necessarily gets different links, so a
    /// sender-supplied `prev` can only ever corrupt the chain and is ignored.
    /// (It reaches here from `mail send` whenever a user copies a stored line
    /// back into a send — a mistake that must not be able to forge history.)
    [Fact]
    public void Append_IgnoresASenderSuppliedPrev()
    {
        using var tmp = new MailStoreTempDir();
        var store = tmp.Store();
        var forged = MailFixtures.Envelope() with { Prev = new string('f', 64) };

        var first = MailFixtures.AppendOk(store, forged);
        var second = MailFixtures.AppendOk(store, forged);

        Assert.Equal(MailStore.Genesis, first.Prev);
        Assert.Equal(MailFixtures.Sha256Hex(first.Line), second.Prev);
        Assert.Empty(store.VerifyChain());
    }

    [Fact]
    public void ThreeAppends_VerifyClean()
    {
        using var tmp = new MailStoreTempDir();
        var store = tmp.Store();
        foreach (var id in new[] { "m-01", "m-02", "m-03" })
            MailFixtures.AppendOk(store, MailFixtures.Envelope(id: id));

        Assert.Empty(store.VerifyChain());
        Assert.Equal(3, store.Read().Count);
        Assert.Equal(new[] { "m-01", "m-02", "m-03" }, store.Read().Select(l => l.Envelope!.Id).ToArray());
    }

    /// Offsets are the byte position where the line STARTS — phase 3's cursor
    /// anchors on exactly this number, so it is part of the append contract.
    [Fact]
    public void Append_ReportsTheByteOffsetWhereTheLineStarts()
    {
        using var tmp = new MailStoreTempDir();
        var store = tmp.Store();

        var first = MailFixtures.AppendOk(store, MailFixtures.Envelope(id: "m-01"));
        var second = MailFixtures.AppendOk(store, MailFixtures.Envelope(id: "m-02"));

        Assert.Equal(0, first.Offset);
        Assert.Equal(Encoding.UTF8.GetByteCount(first.Line) + 1, second.Offset);   // +1 for '\n'
        Assert.Equal(new[] { 0L, second.Offset }, store.Read().Select(l => l.Offset).ToArray());
    }

    /// The terminator is LF, ALWAYS — never Environment.NewLine. The chain
    /// hashes line bytes, so a CRLF host would produce a store no other host
    /// could verify. The format is platform-independent by construction.
    [Fact]
    public void Terminator_IsLineFeedNotThePlatformNewline()
    {
        using var tmp = new MailStoreTempDir();
        MailFixtures.AppendOk(tmp.Store(), MailFixtures.Envelope());

        var bytes = tmp.Bytes();
        Assert.Equal((byte)'\n', bytes[^1]);
        Assert.DoesNotContain((byte)'\r', bytes);
    }
}

// ---- detection: the point of the chain -------------------------------------

public class MailStoreTamperTests
{
    private static MailStore Seeded(MailStoreTempDir tmp, int lines = 3)
    {
        var store = tmp.Store();
        for (var i = 1; i <= lines; i++)
            MailFixtures.AppendOk(store, MailFixtures.Envelope(id: $"m-{i:00}", body: $"body {i}"));
        return store;
    }

    /// REWRITING a line is the threat that matters (an exec member editing what
    /// another agent said). The rewritten line still parses — that is the whole
    /// problem — so only the chain catches it, and it surfaces at the FOLLOWING
    /// line, whose declared `prev` no longer matches what precedes it.
    [Fact]
    public void RewritingALine_IsDetectedAtTheFollowingLine()
    {
        using var tmp = new MailStoreTempDir();
        var store = Seeded(tmp);
        var lines = tmp.Lines();

        var tampered = lines[1].Replace("body 2", "body X");
        Assert.NotEqual(lines[1], tampered);
        File.WriteAllText(tmp.FilePath, string.Join('\n', lines[0], tampered, lines[2]) + "\n");

        var faults = store.VerifyChain();

        var fault = Assert.Single(faults);
        Assert.Equal(MailChainFaultKind.PrevMismatch, fault.Kind);
        // Reported against the line whose link is wrong — the THIRD line — not
        // the tampered one: the store cannot know which of the two moved, and
        // saying so precisely is better than guessing.
        Assert.Equal(store.Read()[2].Offset, fault.Offset);
    }

    /// DELETING the head is what a naive "first line has no prev" convention
    /// would miss entirely: line 2 would simply become a legitimate-looking
    /// first line. Genesis is what makes it detectable.
    [Fact]
    public void DeletingTheFirstLine_IsDetectedAsABadGenesis()
    {
        using var tmp = new MailStoreTempDir();
        var store = Seeded(tmp);
        var lines = tmp.Lines();
        File.WriteAllText(tmp.FilePath, string.Join('\n', lines[1], lines[2]) + "\n");

        var fault = Assert.Single(store.VerifyChain());

        Assert.Equal(MailChainFaultKind.Genesis, fault.Kind);
        Assert.Equal(0, fault.Offset);
    }

    [Fact]
    public void DeletingAMiddleLine_IsDetected()
    {
        using var tmp = new MailStoreTempDir();
        var store = Seeded(tmp);
        var lines = tmp.Lines();
        File.WriteAllText(tmp.FilePath, string.Join('\n', lines[0], lines[2]) + "\n");

        var fault = Assert.Single(store.VerifyChain());
        Assert.Equal(MailChainFaultKind.PrevMismatch, fault.Kind);
    }

    [Fact]
    public void ReorderingLines_IsDetected()
    {
        using var tmp = new MailStoreTempDir();
        var store = Seeded(tmp);
        var lines = tmp.Lines();
        File.WriteAllText(tmp.FilePath, string.Join('\n', lines[0], lines[2], lines[1]) + "\n");

        Assert.NotEmpty(store.VerifyChain());
    }

    /// ONE corruption must produce ONE fault, not a cascade. The expected link
    /// is computed from the bytes ACTUALLY on disk rather than from a running
    /// "what it should have been" — so a single break does not make every later
    /// line look tampered, and an operator reading the report sees one incident
    /// instead of a wall of noise.
    [Fact]
    public void OneCorruption_DoesNotCascadeIntoManyFaults()
    {
        using var tmp = new MailStoreTempDir();
        var store = Seeded(tmp, lines: 6);
        var lines = tmp.Lines();
        lines[2] = lines[2].Replace("body 3", "body Z");
        File.WriteAllText(tmp.FilePath, string.Join('\n', lines) + "\n");

        Assert.Single(store.VerifyChain());
    }

    /// A line the store did NOT write — a hand-appended envelope with no `prev`
    /// at all — is a chain break, not a pass. Silence here would let anyone
    /// append unchained history and have it read as genuine.
    [Fact]
    public void ALineWithNoPrev_IsAChainBreak()
    {
        using var tmp = new MailStoreTempDir();
        var store = Seeded(tmp, lines: 1);
        var unchained = """
            {"v":1,"id":"m-99","ts":"2026-08-12T19:00:00Z","from":{"agent":"a","harness":"claude-code"},"to":"main","kind":"status","topic":"t","priority":"ambient","ttlDeliveries":3,"body":"snuck in"}
            """;
        File.AppendAllText(tmp.FilePath, unchained + "\n");

        var fault = Assert.Single(store.VerifyChain());
        Assert.Equal(MailChainFaultKind.PrevMissing, fault.Kind);
    }

    /// An UNPARSEABLE line cannot have its own link checked, and says so — but
    /// it does not swallow the lines around it: the next line still verifies
    /// against the garbage's actual bytes, so exactly one fault is reported.
    [Fact]
    public void AGarbageLine_IsOneUnreadableFaultAndBreaksNothingAfterIt()
    {
        using var tmp = new MailStoreTempDir();
        var store = Seeded(tmp, lines: 1);
        File.AppendAllText(tmp.FilePath, "not json at all\n");
        MailFixtures.AppendOk(store, MailFixtures.Envelope(id: "m-after"));

        var fault = Assert.Single(store.VerifyChain());

        Assert.Equal(MailChainFaultKind.Unreadable, fault.Kind);
        Assert.Equal("m-after", store.Read()[^1].Envelope!.Id);
    }

    /// The store NEVER rewrites history to make the chain look good: appending
    /// onto a tampered file links to the tampered bytes and leaves the break
    /// exactly where it is. A store that "healed" itself would erase the only
    /// evidence there ever was.
    [Fact]
    public void AppendingOntoATamperedFile_LeavesTheBreakVisible()
    {
        using var tmp = new MailStoreTempDir();
        var store = Seeded(tmp);
        var lines = tmp.Lines();
        File.WriteAllText(tmp.FilePath, string.Join('\n', lines[0], lines[1].Replace("body 2", "body X"), lines[2]) + "\n");

        MailFixtures.AppendOk(store, MailFixtures.Envelope(id: "m-04"));

        var fault = Assert.Single(store.VerifyChain());
        Assert.Equal(MailChainFaultKind.PrevMismatch, fault.Kind);
        Assert.Equal(4, store.Read().Count);
    }

    /// Named honestly rather than left implied: truncating the TAIL leaves an
    /// internally consistent chain. A shortened chain cannot be distinguished
    /// from a store that simply has not received those messages yet — detecting
    /// it needs an external record of where the chain reached (phase 3's cursor
    /// offsets, and any future generation record), never `prev` alone.
    [Fact]
    public void TruncatingTheTail_IsNotDetectableByTheChainAlone()
    {
        using var tmp = new MailStoreTempDir();
        var store = Seeded(tmp);
        var lines = tmp.Lines();
        File.WriteAllText(tmp.FilePath, string.Join('\n', lines[0], lines[1]) + "\n");

        Assert.Empty(store.VerifyChain());
        Assert.Equal(2, store.Read().Count);
    }
}

// ---- the write gate: every stored line re-parses ---------------------------

/// Skeptic find (phase 2 verify pass): Append takes an in-process VALUE no
/// parser has vouched for, and Render will serialize whatever the record
/// holds — so without a gate, a ttl of 0 or an out-of-range enum lands on
/// disk as a line every future reader warns-and-skips: mail silently lost
/// behind a successful-looking append. The store now validates the EXACT
/// rendered bytes through the strict parser and refuses, writing nothing.
public class MailStoreWriteGateTests
{
    [Fact]
    public void Append_RefusesATtlTheParserWouldReject_AndWritesNothing()
    {
        using var tmp = new MailStoreTempDir();

        var result = tmp.Store().Append(MailFixtures.Envelope(ttl: 0));

        var failed = Assert.IsType<MailAppend.Failed>(result);
        Assert.Contains("ttlDeliveries", failed.Error);
        Assert.False(File.Exists(tmp.FilePath));   // refused means NOTHING written
    }

    /// An enum value outside the closed set — `(MailKind)99` is one cast away
    /// in any C# caller — renders as a spelling the parser has never heard of.
    [Fact]
    public void Append_RefusesAnOutOfRangeEnumValue()
    {
        using var tmp = new MailStoreTempDir();
        var bad = MailFixtures.Envelope() with { Kind = (MailKind)99 };

        var failed = Assert.IsType<MailAppend.Failed>(tmp.Store().Append(bad));

        Assert.Contains("'kind'", failed.Error);
        Assert.False(File.Exists(tmp.FilePath));
    }

    /// A refusal against an EXISTING store leaves it byte-identical — the gate
    /// sits before the write, not around it.
    [Fact]
    public void Append_RefusesABlankAddressAndLeavesTheChainUntouched()
    {
        using var tmp = new MailStoreTempDir();
        var store = tmp.Store();
        MailFixtures.AppendOk(store, MailFixtures.Envelope(id: "m-01"));
        var before = tmp.Bytes();

        var failed = Assert.IsType<MailAppend.Failed>(store.Append(MailFixtures.Envelope(to: "   ")));

        Assert.Contains("'to'", failed.Error);
        Assert.Equal(before, tmp.Bytes());
        Assert.Empty(store.VerifyChain());
    }
}

// ---- the torn tail ---------------------------------------------------------

public class MailStoreTornTailTests
{
    /// A file not ending in '\n' means the last write was INTERRUPTED (crash,
    /// ENOSPC, a power cut mid-append). The store TERMINATES the torn line and
    /// appends after it. It does not truncate, repair, or rewrite the torn
    /// bytes: this is an evidence record, and destroying a partial line to make
    /// the file tidy is precisely the silent history edit the chain exists to
    /// prevent.
    [Fact]
    public void TornFinalLine_IsTerminatedAndPreservedByteForByte()
    {
        using var tmp = new MailStoreTempDir();
        var store = tmp.Store();
        MailFixtures.AppendOk(store, MailFixtures.Envelope(id: "m-01"));

        var torn = """{"v":1,"id":"m-02","ts":"2026-08""";
        File.AppendAllText(tmp.FilePath, torn);

        var appended = MailFixtures.AppendOk(store, MailFixtures.Envelope(id: "m-03"));

        var lines = tmp.Lines();
        Assert.Equal(3, lines.Length);
        Assert.Equal(torn, lines[1]);                                  // preserved exactly
        Assert.Equal(MailFixtures.Sha256Hex(torn), appended.Prev);      // chained onto what IS there
        Assert.Equal("m-03", MailEnvelope.TryParseLine(lines[2], out _)!.Id);
    }

    /// The torn line stays MALFORMED to every reader — warned-and-skipped, per
    /// the envelope's failure direction — while the chain across it still
    /// verifies. Corruption is thus visible in one place (the reader's errors)
    /// without being counted twice (as a fake chain break).
    [Fact]
    public void AfterTerminatingATornLine_TheChainStillVerifies()
    {
        using var tmp = new MailStoreTempDir();
        var store = tmp.Store();
        MailFixtures.AppendOk(store, MailFixtures.Envelope(id: "m-01"));
        File.AppendAllText(tmp.FilePath, """{"v":1,"id":"m-02","ts":"2026-08""");
        MailFixtures.AppendOk(store, MailFixtures.Envelope(id: "m-03"));

        var faults = store.VerifyChain();
        var read = store.Read();

        Assert.Equal(MailChainFaultKind.Unreadable, Assert.Single(faults).Kind);
        Assert.Null(read[1].Envelope);
        Assert.NotEmpty(read[1].Errors);
        Assert.NotNull(read[2].Envelope);
    }

    /// A torn line is readable as a line — Read reports it unterminated, so a
    /// consumer (and phase 3's cursor) can tell "the file ends mid-write" from
    /// "the file ends".
    [Fact]
    public void Read_ReportsTheUnterminatedTail()
    {
        using var tmp = new MailStoreTempDir();
        var store = tmp.Store();
        MailFixtures.AppendOk(store, MailFixtures.Envelope(id: "m-01"));
        File.AppendAllText(tmp.FilePath, "partial");

        var read = store.Read();

        Assert.True(read[0].Terminated);
        Assert.False(read[1].Terminated);
        Assert.Equal("partial", read[1].Text);
    }

    /// Skeptic find (phase 2 verify pass): readers deliberately never take the
    /// append lock, so a VerifyChain racing a concurrent `mail send` can catch
    /// the tail mid-write(2) — an unterminated, unparseable line that is
    /// perfectly healthy a moment later. Still one Unreadable fault (it IS
    /// unreadable), but the detail says what the state can be instead of
    /// reading as a tamper accusation an operator would false-alarm on — and
    /// the softening applies ONLY to the unterminated tail: the same bytes
    /// once TERMINATED are a plain fault again.
    [Fact]
    public void AnUnterminatedGarbageTail_SaysInFlightRatherThanCryingTamper()
    {
        using var tmp = new MailStoreTempDir();
        var store = tmp.Store();
        MailFixtures.AppendOk(store, MailFixtures.Envelope(id: "m-01"));
        File.AppendAllText(tmp.FilePath, """{"v":1,"id":"m-02","ts":"2026-""");   // no terminator

        var fault = Assert.Single(store.VerifyChain());
        Assert.Equal(MailChainFaultKind.Unreadable, fault.Kind);
        Assert.Contains("append in flight", fault.Detail);

        MailFixtures.AppendOk(store, MailFixtures.Envelope(id: "m-03"));   // terminates the torn line

        var after = Assert.Single(store.VerifyChain());
        Assert.Equal(MailChainFaultKind.Unreadable, after.Kind);
        Assert.DoesNotContain("append in flight", after.Detail);
    }

    /// The tail scan must not depend on a read window: a line longer than
    /// whatever chunk the appender reads still hashes whole, or every long-body
    /// message would silently break the chain. (100KB: within MaxLineBytes,
    /// beyond the 8KB and 32KB windows, so the growth loop is exercised.)
    [Fact]
    public void AVeryLongPreviousLine_IsHashedWhole()
    {
        using var tmp = new MailStoreTempDir();
        var store = tmp.Store();

        var first = MailFixtures.AppendOk(store, MailFixtures.Envelope(id: "m-01", body: new string('x', 100_000)));
        var second = MailFixtures.AppendOk(store, MailFixtures.Envelope(id: "m-02"));

        Assert.Equal(MailFixtures.Sha256Hex(first.Line), second.Prev);
        Assert.Empty(store.VerifyChain());
    }

    /// Phase 2's named carry-in, closed at the write: a line that would not
    /// fit a windowed reader (MaxLineBytes, terminator included) is REFUSED —
    /// never truncated, never written-then-undeliverable.
    [Fact]
    public void Append_RefusesALinePastMaxLineBytes()
    {
        using var tmp = new MailStoreTempDir();

        var result = tmp.Store().Append(
            MailFixtures.Envelope(body: new string('x', MailStore.MaxLineBytes)));

        var failed = Assert.IsType<MailAppend.Failed>(result);
        Assert.Contains("caps a line", failed.Error);
        Assert.False(File.Exists(tmp.FilePath));
    }
}

// ---- the file, the directory, the lock -------------------------------------

public class MailStoreFileTests
{
    [Fact]
    public void AbsentStore_ReadsEmptyAndVerifiesClean()
    {
        using var tmp = new MailStoreTempDir();
        var store = tmp.Store();

        Assert.Empty(store.Read());
        Assert.Empty(store.VerifyChain());
    }

    [Fact]
    public void EmptyFile_TakesGenesisOnTheNextAppend()
    {
        using var tmp = new MailStoreTempDir();
        File.WriteAllBytes(tmp.FilePath, []);

        Assert.Equal(MailStore.Genesis, MailFixtures.AppendOk(tmp.Store(), MailFixtures.Envelope()).Prev);
    }

    /// The mail directory is created 0700 and the store 0600 — d13's "all three
    /// are 0600 like api.json". The store is the inter-agent influence record;
    /// a co-located user reading it reads every agent's traffic.
    [Fact]
    public void Append_CreatesTheDirectoryAndFilePrivate()
    {
        var dir = Path.Combine(Path.GetTempPath(), "captainhook-mail-perm-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new MailStore(dir);
            MailFixtures.AppendOk(store, MailFixtures.Envelope());

            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(dir));
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(store.FilePath));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    /// Never throw on bad DATA, extended to bad PATHS: a directory where the
    /// store should be is a Failed result an operator can read, not an
    /// exception out of a sender's process.
    [Fact]
    public void ADirectoryWhereTheFileBelongs_FailsWithoutThrowing()
    {
        using var tmp = new MailStoreTempDir();
        Directory.CreateDirectory(tmp.FilePath);

        var result = tmp.Store().Append(MailFixtures.Envelope());

        var failed = Assert.IsType<MailAppend.Failed>(result);
        Assert.Contains("mail.jsonl", failed.Error);
        Assert.Empty(tmp.Store().Read());
    }

    [Fact]
    public void AnUnreadableFile_ReadsEmptyWithoutThrowing()
    {
        if (Environment.UserName == "root") return;   // permissions cannot bite as root
        using var tmp = new MailStoreTempDir();
        var store = tmp.Store();
        MailFixtures.AppendOk(store, MailFixtures.Envelope());
        File.SetUnixFileMode(tmp.FilePath, UnixFileMode.None);

        try
        {
            Assert.Empty(store.Read());
            Assert.Empty(store.VerifyChain());
        }
        finally { File.SetUnixFileMode(tmp.FilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
    }

    /// The lock file is NEVER unlinked — DaemonRendezvous's rule, for the same
    /// reason: deleting a held lock lets the next writer lock a FRESH INODE at
    /// the same path and silently break mutual exclusion.
    [Fact]
    public void TheLockFile_SurvivesTheAppendThatUsedIt()
    {
        using var tmp = new MailStoreTempDir();
        var store = tmp.Store();
        MailFixtures.AppendOk(store, MailFixtures.Envelope());

        Assert.True(File.Exists(store.LockPath));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(store.LockPath));
    }

    /// Explicit beats env beats home — HarnessRegistry.ResolveDir /
    /// DispatchPolicy.ResolvePath's idiom, so every process on the box agrees on
    /// where mail lives without negotiating.
    [Fact]
    public void ResolveDir_PrefersExplicitThenEnvThenHome()
    {
        var prior = Environment.GetEnvironmentVariable("CAPTAINHOOK_MAIL_DIR");
        try
        {
            Environment.SetEnvironmentVariable("CAPTAINHOOK_MAIL_DIR", "/tmp/env-mail");
            Assert.Equal("/explicit/mail", MailStore.ResolveDir("/explicit/mail"));
            Assert.Equal("/tmp/env-mail", MailStore.ResolveDir());

            Environment.SetEnvironmentVariable("CAPTAINHOOK_MAIL_DIR", null);
            Assert.Equal(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".captainHook", "mail"),
                MailStore.ResolveDir());
        }
        finally { Environment.SetEnvironmentVariable("CAPTAINHOOK_MAIL_DIR", prior); }
    }
}

// ---- serialization: the flock is the correctness argument ------------------

public class MailStoreConcurrencyTests
{
    /// The chain's read-last-line-then-append is a READ-MODIFY-WRITE, so
    /// concurrent senders without a lock would compute the same `prev` and
    /// produce a forked chain. Every line must land, exactly once, in a single
    /// verifiable chain — this is the test the lock exists for.
    [Fact]
    public async Task ConcurrentAppenders_ProduceOneUnbrokenChain()
    {
        using var tmp = new MailStoreTempDir();
        const int writers = 8, each = 6;

        await Task.WhenAll(Enumerable.Range(0, writers).Select(w => Task.Run(() =>
        {
            // A separate MailStore instance per writer: nothing is shared in
            // memory, so the file lock is doing all of the work — the same
            // shape as N `mail send` processes.
            var store = new MailStore(tmp.Dir);
            for (var i = 0; i < each; i++)
                MailFixtures.AppendOk(store, MailFixtures.Envelope(id: $"m-{w}-{i}"));
        })));

        var read = tmp.Store().Read();
        Assert.Equal(writers * each, read.Count);
        Assert.All(read, l => Assert.NotNull(l.Envelope));
        Assert.Empty(tmp.Store().VerifyChain());
        Assert.Equal(writers * each, read.Select(l => l.Envelope!.Id).Distinct().Count());
    }

    /// A lock held by another process makes an append FAIL — bounded, loud, and
    /// with nothing written — rather than block forever or write unserialized.
    /// The wait is monotonic (house invariant 2): a wall-clock deadline would
    /// expire early on the WSL2 clock steps recorded in platform.md.
    [Fact]
    public void AHeldLock_FailsTheAppendWithinItsDeadlineAndWritesNothing()
    {
        using var tmp = new MailStoreTempDir();
        var store = tmp.Store();
        Directory.CreateDirectory(tmp.Dir);

        using (var _ = new FileStream(store.LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = store.Append(MailFixtures.Envelope(), lockWaitMs: 250);
            sw.Stop();

            var failed = Assert.IsType<MailAppend.Failed>(result);
            Assert.Contains("lock", failed.Error, StringComparison.OrdinalIgnoreCase);
            Assert.InRange(sw.ElapsedMilliseconds, 200, 5_000);
            Assert.False(File.Exists(tmp.FilePath));
        }

        MailFixtures.AppendOk(store, MailFixtures.Envelope());   // released: back to normal
    }

    /// The crash window the plan names: a sender that takes the lock, reads the
    /// last line, and DIES before writing. The lock is released by the kernel,
    /// the next appender reads the same last line, and the result is neither a
    /// gap nor a double — the store is exactly as it was, plus one line.
    [Fact]
    public void ACrashBetweenReadAndAppend_LeavesTheStoreIntact()
    {
        using var tmp = new MailStoreTempDir();
        var store = tmp.Store();
        var first = MailFixtures.AppendOk(store, MailFixtures.Envelope(id: "m-01"));

        // Stand in for the dead process: hold the lock, do nothing, release.
        using (var _ = new FileStream(store.LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) { }

        var second = MailFixtures.AppendOk(store, MailFixtures.Envelope(id: "m-02"));

        Assert.Equal(MailFixtures.Sha256Hex(first.Line), second.Prev);
        Assert.Equal(2, store.Read().Count);
        Assert.Empty(store.VerifyChain());
    }
}

// ---- what an append tells the trail (ADR-0016 d14) --------------------------

/// Slice `mail-append-provenance-fields`: the mail canvas animates arrivals off
/// the trail, so `mail.append` must characterize the envelope — and must not
/// carry its BODY. The trail is operational-lifetime and payload-readable
/// (`exec.stderr` already lands arbitrary process output in it); the store is
/// the archival record with the lifetime and the modes for content. Everything
/// here is therefore two assertions at once: this field is present, and the
/// body is nowhere.
public class MailAppendProvenanceTests
{
    private static JsonElement Data(LogEvent e) =>
        JsonDocument.Parse(e.ToJson()).RootElement.GetProperty("data").Clone();

    [Fact]
    public void Append_CarriesProvenance_AndNeverTheBody()
    {
        using var log = new CapturedLog();
        using var tmp = new MailStoreTempDir();

        const string secret = "the-body-text-that-must-not-travel";
        MailFixtures.AppendOk(tmp.Store(), MailFixtures.Envelope(
            id: "m-01", to: "reviewer", body: secret, priority: MailPriority.Urgent, ttl: 7));

        var ev = Assert.Single(log.Events, e => e.Evt == "mail.append");
        var data = Data(ev);
        Assert.Equal("m-01", data.GetProperty("id").GetString());
        Assert.Equal("reviewer", data.GetProperty("to").GetString());
        Assert.Equal(0, data.GetProperty("offset").GetInt64());
        Assert.Equal("status", data.GetProperty("kind").GetString());
        Assert.Equal("build", data.GetProperty("topic").GetString());
        Assert.Equal("urgent", data.GetProperty("priority").GetString());
        Assert.Equal(7, data.GetProperty("ttlDeliveries").GetInt32());

        var from = data.GetProperty("from");
        Assert.Equal("intent-watcher", from.GetProperty("agent").GetString());
        Assert.Equal("claude-code", from.GetProperty("harness").GetString());
        Assert.Equal("s-77", from.GetProperty("session").GetString());

        // The pin: not the field, not the text, in ANY event this append
        // emitted — a future field that quietly inlines the envelope fails
        // here rather than in someone's trail.
        foreach (var e in log.Events)
        {
            Assert.DoesNotContain(secret, e.ToJson());
            Assert.DoesNotContain("\"body\"", e.ToJson());
        }
    }

    /// A write-only member has no session (d5), so the field is OMITTED rather
    /// than spelled null — the trail's absent-means-omit rule, which is also
    /// what keeps every consumer from testing for it.
    [Fact]
    public void Append_OmitsAnAbsentSenderSession()
    {
        using var log = new CapturedLog();
        using var tmp = new MailStoreTempDir();

        MailFixtures.AppendOk(tmp.Store(), MailFixtures.Envelope(session: null));

        var from = Data(Assert.Single(log.Events, e => e.Evt == "mail.append")).GetProperty("from");
        Assert.False(from.TryGetProperty("session", out _));
        Assert.Equal("intent-watcher", from.GetProperty("agent").GetString());
    }

    /// Id and topic are SENDER-CONTROLLED up to the 128KiB line cap, so a trail
    /// line would inherit that without a clamp — the same clamp the digest head
    /// applies, so the two surfaces spell a clamped id identically and join.
    [Fact]
    public void Append_ClampsTheSenderControlledStrings()
    {
        using var log = new CapturedLog();
        using var tmp = new MailStoreTempDir();

        var long1 = new string('i', 5_000);
        var long2 = new string('t', 5_000);
        var e = MailFixtures.Envelope() with { Id = long1, Topic = long2 };
        MailFixtures.AppendOk(tmp.Store(), e);

        var data = Data(Assert.Single(log.Events, ev => ev.Evt == "mail.append"));
        Assert.Equal(MailEnvelope.ClampField(long1), data.GetProperty("id").GetString());
        Assert.Equal(MailEnvelope.HeadFieldChars + 1, data.GetProperty("id").GetString()!.Length);
        Assert.EndsWith("…", data.GetProperty("topic").GetString());

        // The STORE is untouched by the display clamp — the record keeps what
        // the sender wrote, in full.
        Assert.Equal(long1, tmp.Store().Read().Single().Envelope!.Id);
    }
}
