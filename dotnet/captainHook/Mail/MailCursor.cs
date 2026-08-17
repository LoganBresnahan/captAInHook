using System.Buffers;
using System.Text;
using System.Text.Json;
using CaptainHook.Actors;

namespace CaptainHook.Mail;

// Roadmap item 20 / ADR-0016 decisions 3, 4, 6, 13 — the per-(role, session)
// DELIVERY CURSOR: which mail this recipient has consumed, and how stale the
// rest is allowed to get. Phase 2 gave mail a durable chained store; this file
// gives each recipient its position in it.
//
// Advance-on-inject is TWO GUARANTEES AT ONCE, and every choice here serves
// one of them:
//
//   * AT-MOST-ONCE: the cursor is written (atomic rename) BEFORE the rendered
//     digest is emitted as an effect. A crash between the two loses that
//     digest's mail rather than delivering it twice — chosen deliberately,
//     because the double is the worse failure: it is also the Stop-loop
//     hazard, and lost mail at a crash boundary is at least visible in the
//     store while a double-inject is visible nowhere. Across PROCESSES the
//     guarantee is the per-cursor flock + staleness guard in Advance: the
//     advance is a read-then-write (the store's own flock reasoning, one
//     layer up), so two concurrent digests for one (role, session) serialize
//     on the lock and the second finds the deliveries counter moved and is
//     refused.
//   * THE STOP-LOOP GUARD (d4): a reconcile turn re-blocks only on genuinely
//     new inbound, because everything rendered into the reconcile digest is
//     behind the cursor by the time the block answer reaches the harness.
//
// THE FRONTIER + HELD SHAPE. The ADR's sketch shows a bare byte offset; a bare
// offset cannot express what delivery actually does. At a mid-turn seam the
// planner (phase 4) delivers urgent mail while HOLDING earlier ambient mail —
// out of file order — and a single offset must then either stop before the
// held line (re-reading the delivered one next turn: DOUBLE-INJECT) or advance
// past both (losing the held one: MAIL LOST). So the cursor is a frontier
// `offset` — everything at or beyond it is unread — plus `held`, the bounded
// list of exceptions before the frontier: envelopes seen but passed over, each
// carrying the id it had (so a changed file cannot silently substitute mail)
// and the `seenAt` stamp its TTL is measured from. Held shrinks as mail is
// delivered or expires; delivered mail is simply absent (behind the frontier,
// not in held), which is what makes redelivery impossible rather than merely
// avoided.
//
// THE TTL CLOCK (d3): `deliveries` counts this recipient's delivery
// opportunities — it increments by exactly one per Advance, never per event
// and never per wall clock. An envelope passed over at opportunity k is
// stamped seenAt=k; after being passed over at `ttlDeliveries` opportunities
// (deliveries − seenAt + 1 ≥ ttl) it is EXPIRED: reported once, then dropped
// by the next Advance with a `mail.expire` in the trail. Mail never rots while
// the recipient idles — no opportunities, no aging — which is house invariant
// 2's spirit at the delivery layer, and why nothing here reads a clock of any
// kind. Stated plainly: the unit is ADVANCES, not seams-where-this-mail-was-
// deliverable — the arithmetic cannot know seam classes, so every Advance
// ages everything held, and a chatty turn of urgent deliveries burns held
// ambient mail's TTL. Managing that is the PLANNER's obligation (phase 4):
// deliver-or-degrade rather than hold at seams that will advance, and only
// advance when writing state worth the opportunity.
//
// RE-ANCHOR SEMANTICS (d13: cursors are pure delivery state — deletable
// anytime). An ABSENT cursor anchors at offset 0: store-and-forward is the
// point, and mail sent to a role while nobody held it must reach the next
// session that does. Malformed bytes, a gen the store does not report, a head
// hash that is not this chain's, an offset past the file, an offset off every
// line boundary, or a held entry the file contradicts — each re-anchors at 0,
// LOUDLY (one warn naming the reason), preserving the monotonic `deliveries`
// counter when the old value is readable. Re-anchoring can redeliver mail a
// lost cursor had consumed; that is d13's stated cost ("a deleted cursor just
// re-anchors"), and the safe direction — the alternative to redelivery is
// guessing, and a guessed frontier loses mail silently. The cost INCLUDES
// resurrecting EXPIRED mail: the seenAt stamps die with the held list even
// though the deliveries clock survives, so d3's TTL restarts for anything
// still retained — expiry is a one-way door only while the cursor lives.
//
// `head` is the store's first-line hash, recorded at anchor time — the
// CHAIN-NATIVE rotation check. `gen` (the ADR's rotation generation) rides
// beside it: today the live store is always generation 1 (rotation machinery
// is d13's future work), so head comparison is what actually detects a
// replaced chain — phase 2 settled that every generation restarts at genesis,
// which makes "same path, different chain" exactly a head change.
//
// The unterminated tail: the frontier NEVER enters it (TrailCursor's
// half-written-line rule — the same reasoning, one layer up). An append in
// flight is invisible this read and complete the next; a torn line the next
// append terminates becomes an ordinary malformed line, consumed and counted.

/// One cursor-file `held` entry: an envelope seen and passed over, waiting
/// before the frontier. `Id` pins identity — a file whose line at `Offset` no
/// longer carries this id has changed under the cursor, which is a re-anchor,
/// never a silent substitution. `SeenAt` is the `deliveries` value of the
/// opportunity that first passed it over.
public sealed record MailHeld(long Offset, string Id, long SeenAt);

/// The cursor as it exists on disk (one JSON object, 0600, atomic-renamed).
public sealed record MailCursor(
    int Gen,
    string? Head,
    long Offset,
    string? LastDeliveredId,
    long Deliveries,
    IReadOnlyList<MailHeld> Held)
{
    private static readonly IReadOnlySet<string> KnownFields =
        new HashSet<string> { "v", "gen", "head", "offset", "lastDeliveredId", "deliveries", "held" };

    private static readonly IReadOnlySet<string> KnownHeldFields =
        new HashSet<string> { "offset", "id", "seenAt" };

    /// Strict parse on the house pattern (DispatchPolicy / MailEnvelope):
    /// collect every violation, all-or-nothing, unknown AND duplicate fields
    /// malformed, never a throw on bad DATA. The failure direction is the
    /// cursor's own: a malformed cursor RE-ANCHORS (it is delivery state, not
    /// a record) — so the caller treats null as "anchor fresh", loudly.
    public static MailCursor? TryParse(string text, out IReadOnlyList<string> errors)
    {
        var errs = new List<string>();
        errors = errs;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(text); }
        catch (JsonException ex)
        {
            errs.Add($"not valid JSON: {ex.Message}");
            return null;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                errs.Add("cursor must be a JSON object");
                return null;
            }

            CheckFields(root, KnownFields, "cursor", errs);

            if (!root.TryGetProperty("v", out var v)
                || v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var ver) || ver != 1)
                errs.Add("'v' is required and must be the number 1");

            var gen = ReadNonNegativeInt(root, "gen", required: true, errs);
            var offset = ReadNonNegativeLong(root, "offset", required: true, errs);
            var deliveries = ReadNonNegativeLong(root, "deliveries", required: true, errs);
            var head = OptionalString(root, "head", errs);
            var lastId = OptionalString(root, "lastDeliveredId", errs);

            var held = new List<MailHeld>();
            if (root.TryGetProperty("held", out var h))
            {
                if (h.ValueKind != JsonValueKind.Array)
                    errs.Add("'held' must be an array");
                else
                {
                    // Duplicate held OFFSETS are malformed: one file position
                    // is one envelope, and a cursor listing it twice would
                    // render it twice in one digest — a double-inject needing
                    // no race at all. Duplicate held IDS stay legal on
                    // purpose: the store does not dedup ids, so the same
                    // envelope appended twice is two legitimate held lines.
                    var offsets = new HashSet<long>();
                    foreach (var entry in h.EnumerateArray())
                    {
                        if (entry.ValueKind != JsonValueKind.Object)
                        {
                            errs.Add("'held' entries must be objects");
                            continue;
                        }
                        CheckFields(entry, KnownHeldFields, "held", errs);
                        var eo = ReadNonNegativeLong(entry, "offset", required: true, errs, "held.");
                        var seen = ReadNonNegativeLong(entry, "seenAt", required: true, errs, "held.");
                        string? id = null;
                        if (!entry.TryGetProperty("id", out var idEl)
                            || idEl.ValueKind != JsonValueKind.String
                            || string.IsNullOrWhiteSpace(id = TryReadString(idEl)))
                            errs.Add("'held.id' is required and must be a non-empty string");
                        if (eo is { } dup && !offsets.Add(dup))
                            errs.Add($"duplicate held offset {dup}");
                        else if (eo is { } o && id is not null && seen is { } s)
                            held.Add(new MailHeld(o, id, s));
                    }
                }
            }

            return errs.Count > 0
                ? null
                : new MailCursor(gen!.Value, head, offset!.Value, lastId, deliveries!.Value, held);
        }
    }

    /// The canonical cursor document: fixed field order, absent-means-omit for
    /// the optionals, `held` written only when non-empty. One serializer —
    /// this is also what the golden test pins.
    public string Render()
    {
        var buf = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buf))
        {
            w.WriteStartObject();
            w.WriteNumber("v", 1);
            w.WriteNumber("gen", Gen);
            if (Head is not null) w.WriteString("head", Head);
            w.WriteNumber("offset", Offset);
            if (LastDeliveredId is not null) w.WriteString("lastDeliveredId", LastDeliveredId);
            w.WriteNumber("deliveries", Deliveries);
            if (Held.Count > 0)
            {
                w.WritePropertyName("held");
                w.WriteStartArray();
                foreach (var e in Held)
                {
                    w.WriteStartObject();
                    w.WriteNumber("offset", e.Offset);
                    w.WriteString("id", e.Id);
                    w.WriteNumber("seenAt", e.SeenAt);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buf.WrittenSpan);
    }

    private static void CheckFields(
        JsonElement obj, IReadOnlySet<string> known, string at, List<string> errs)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var prop in obj.EnumerateObject())
        {
            if (!seen.Add(prop.Name)) errs.Add($"duplicate field '{at}.{prop.Name}'");
            else if (!known.Contains(prop.Name)) errs.Add($"unknown field '{at}.{prop.Name}'");
        }
    }

    private static int? ReadNonNegativeInt(
        JsonElement obj, string field, bool required, List<string> errs, string prefix = "")
    {
        if (!obj.TryGetProperty(field, out var v))
        {
            if (required) errs.Add($"'{prefix}{field}' is required and must be a non-negative integer");
            return null;
        }
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var n) || n < 0)
        {
            errs.Add($"'{prefix}{field}' must be a non-negative integer");
            return null;
        }
        return n;
    }

    private static long? ReadNonNegativeLong(
        JsonElement obj, string field, bool required, List<string> errs, string prefix = "")
    {
        if (!obj.TryGetProperty(field, out var v))
        {
            if (required) errs.Add($"'{prefix}{field}' is required and must be a non-negative integer");
            return null;
        }
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt64(out var n) || n < 0)
        {
            errs.Add($"'{prefix}{field}' must be a non-negative integer");
            return null;
        }
        return n;
    }

    private static string? OptionalString(JsonElement obj, string field, List<string> errs)
    {
        if (!obj.TryGetProperty(field, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        string? s = null;
        if (v.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(s = TryReadString(v)))
        {
            errs.Add($"'{field}' must be a non-empty string or null");
            return null;
        }
        return s;
    }

    /// The deferred-unescape guard (JsonDocument parses "\ud800" fine and
    /// throws at GetString) — local copy per the pending consolidation sweep
    /// recorded in doc/scratch.md.
    private static string? TryReadString(JsonElement e)
    {
        try { return e.GetString(); }
        catch (InvalidOperationException) { return null; }
    }
}

/// One envelope the recipient has not consumed. `SeenAt` null means fresh —
/// first sighted by THIS read, no TTL accrued; a value means the envelope is
/// held, stamped at the opportunity that first passed it over.
public sealed record PendingMail(long Offset, MailEnvelope Envelope, long? SeenAt);

/// Everything one read of the store-through-a-cursor yields. `Offset` is
/// where the cursor IS — its read position, 0 for a fresh or re-anchored one
/// (everything at or beyond it is unread; `Frontier` is where Advance will
/// move it — the end of the COMPLETE lines read, never inside an unterminated
/// tail. `Expired` are TTL-consumed envelopes: reported this once (so the
/// digest can say so), dropped by the next Advance. `SkippedMalformed` counts
/// this-role's unreadable lines being stepped over — warn-and-skip made
/// visible, mirroring the envelope's failure direction.
/// `Session` IS THE CURSOR KEY — the instance half of role × instance
/// (ADR-0018 d3), which is `mail digest --as <instance>` when a registration
/// names one and the hook's session id when it does not. `HookSession` is the
/// REAL session the dispatch came from, and it exists only so the trail can
/// keep naming the window that did the work.
///
/// The two are equal for every unnamed reader, which is every reader that
/// predates ADR-0018 — so the split is invisible until a registration is named,
/// and then it says the thing neither field could say alone: *which mailbox*
/// moved, and *who* moved it. Collapsing them would cost one or the other —
/// key on the session and a named mailbox stops being durable; log the instance
/// and two windows sharing a name become indistinguishable in the trail.
public sealed record MailPendingView(
    string Role,
    string? Session,
    string? HookSession,
    int Gen,
    string? Head,
    long Offset,
    long Frontier,
    long Deliveries,
    string? LastDeliveredId,
    bool Reanchored,
    string? ReanchorReason,
    IReadOnlyList<PendingMail> Pending,
    IReadOnlyList<PendingMail> Expired,
    int SkippedMalformed)
{
    /// Does this read go through a mailbox whose name is not its window's?
    ///
    /// Inferred from the two fields rather than carried as a third, and correct
    /// in every case BECAUSE it is inferred: an unnamed reader passes its own
    /// session as the instance, and a registration whose `--as` happens to
    /// equal the session id keys the same cursor an unnamed one would — so
    /// "equal" always means "there is nothing extra to say", and the trail can
    /// stay byte-identical for every reader that predates ADR-0018.
    public bool Named => Session != HookSession;
}

/// The result of one Advance — closed set, never a throw (MailAppend's rule:
/// delivery state must not take a digest run down over a permission bit).
public abstract record MailCursorWrite
{
    private MailCursorWrite() { }
    public sealed record Written(MailCursor Cursor, string Path) : MailCursorWrite;
    public sealed record Failed(string Error) : MailCursorWrite;
}

public sealed class MailCursors(MailStore store)
{
    /// The store generation the live file is, until rotation machinery (d13's
    /// future work) exists to say otherwise. Head-hash comparison is what
    /// actually detects a replaced chain meanwhile — phase 2 settled that
    /// every generation restarts at genesis, so a rotation IS a head change.
    public const int CurrentGen = 1;

    private const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite;   // 0600

    public MailStore Store { get; } = store;

    /// `cursor.<role>.<session>.json` beside the store (d4: the two must never
    /// point at different places), with role and session PERCENT-ENCODED so an
    /// arbitrary role name can never escape the mail directory, collide with
    /// another role's file, or eat the `.` separators. `[A-Za-z0-9_-]` pass
    /// through; everything else (including `.` and `%` themselves) becomes
    /// %XX per UTF-8 byte — deterministic and collision-free by construction.
    /// A null session (a reader that has none to name) is the empty segment —
    /// and an EMPTY session string is normalized to null, because the two
    /// would otherwise share one file while claiming to be two cursors (a
    /// hook payload with `session_id: ""` must not consume the sessionless
    /// reader's mail).
    public string CursorPath(string role, string? session) =>
        Path.Combine(Store.Dir,
            $"cursor.{Enc(role)}.{(string.IsNullOrEmpty(session) ? "" : Enc(session))}.json");

    /// The inverse of `Enc`, or null when the text is not something `Enc`
    /// could have produced (a stray `%`, a bad hex pair, bytes that are not
    /// UTF-8). Null is a REFUSAL, never a guess: the only caller is the
    /// observation surface's file listing, and a filename this cannot decode
    /// is a file we did not write — reporting a guessed role would invent a
    /// mailbox on the canvas.
    public static string? Dec(string s)
    {
        var bytes = new List<byte>(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] != '%')
            {
                // Only the passthrough alphabet may appear unescaped; anything
                // else means the name was not produced by Enc.
                if (s[i] is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-')
                {
                    bytes.Add((byte)s[i]);
                    continue;
                }
                return null;
            }
            if (i + 2 >= s.Length
                || !byte.TryParse(s.AsSpan(i + 1, 2), System.Globalization.NumberStyles.HexNumber,
                                  System.Globalization.CultureInfo.InvariantCulture, out var b))
                return null;
            bytes.Add(b);
            i += 2;
        }
        try
        {
            // Strict decode: invalid UTF-8 must not become U+FFFD here, or two
            // different files would decode to the same displayed role.
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes.ToArray());
        }
        catch (DecoderFallbackException) { return null; }
    }

    public static string Enc(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var b in Encoding.UTF8.GetBytes(s))
        {
            var c = (char)b;
            if (c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-')
                sb.Append(c);
            else
                sb.Append('%').Append(b.ToString("X2"));
        }
        return sb.ToString();
    }

    /// Every (role, session) that currently holds a cursor file in `dir` —
    /// the observation surface's answer to "who is on this bus?" (d14). A
    /// PURE LISTING: it opens nothing and writes nothing, so asking the
    /// question can never create a mailbox or advance one.
    ///
    /// A cursor file is the ONLY on-disk trace a recipient leaves (the daemon
    /// keeps no session registry, and d14 does not add one), which is exactly
    /// why the answer is inference rather than presence: a session that has
    /// never been delivered to holds no cursor and does not appear here.
    /// Files this cannot decode back to the name that produced them are
    /// SKIPPED, not guessed — `cursor.<role>.<session>.json` with both
    /// segments percent-encoded, so the lock files (`.lock` suffix) and the
    /// atomic-write temps (leading `.`) fall out by the same rule rather than
    /// by a special case. Never throws: an unreadable directory lists empty.
    public static IReadOnlyList<(string Role, string? Session)> List(string dir)
    {
        string[] files;
        try { files = Directory.GetFiles(dir, "cursor.*.json"); }
        catch (Exception) { return []; }   // absent, unreadable, not a directory

        var found = new List<(string Role, string? Session)>();
        foreach (var file in files)
            if (TryParseCursorFileName(Path.GetFileName(file)) is { } id)
                found.Add(id);
        return found
            .OrderBy(x => x.Role, StringComparer.Ordinal)
            .ThenBy(x => x.Session ?? "", StringComparer.Ordinal)
            .ToList();
    }

    /// `cursor.<encRole>.<encSession>.json` → the names that produced it, or
    /// null. `Enc` never emits `.`, so the two dots inside the name are
    /// unambiguous separators; the round-trip check (re-encoding must give
    /// back the exact filename) is what makes this a recognition rather than
    /// a parse — a hand-made `cursor.a.b.json` with an unencoded byte is not
    /// ours and is refused.
    public static (string Role, string? Session)? TryParseCursorFileName(string fileName)
    {
        const string prefix = "cursor.", suffix = ".json";
        if (!fileName.StartsWith(prefix, StringComparison.Ordinal)
            || !fileName.EndsWith(suffix, StringComparison.Ordinal)
            || fileName.Length <= prefix.Length + suffix.Length)
            return null;

        var middle = fileName[prefix.Length..^suffix.Length];
        var dot = middle.IndexOf('.');
        if (dot < 0 || middle.IndexOf('.', dot + 1) >= 0) return null;   // exactly one separator

        var encRole = middle[..dot];
        var encSession = middle[(dot + 1)..];
        if (encRole.Length == 0) return null;                            // a role always has a name

        if (Dec(encRole) is not { } role) return null;
        string? session = null;
        if (encSession.Length > 0)
        {
            if (Dec(encSession) is not { } s) return null;
            session = s;
        }
        // Canonicality: only a name WE would have written is ours to report.
        return Enc(role) == encRole && (session is null ? "" : Enc(session)) == encSession
            ? (role, session)
            : null;
    }

    /// Read the store through this recipient's cursor: what is pending, what
    /// has expired, and where the frontier sits. Never throws; every anomaly
    /// lands as a loud re-anchor, and an unreadable store reads as empty.
    /// Read a mailbox whose name IS its window's — every reader before
    /// ADR-0018, and every unnamed reader after it. Delegates with the key as
    /// the window, which is what makes the split invisible here: the trail
    /// keeps naming exactly what it always named.
    ///
    /// An OVERLOAD rather than an optional parameter, deliberately. A defaulted
    /// `hookSession = null` reads as harmless and is not: any caller that took
    /// the default and then advanced would write trail lines with no
    /// `sessionId`, silently unlinking the choreography from the window that
    /// drove it. The golden corpus caught exactly that, which is the argument
    /// for making the safe call the SHORT one and the split explicit.
    public MailPendingView Pending(string role, string? key) => Pending(role, key, key);

    /// `instance` is the cursor key's second half (`--as`, else the session
    /// id); `hookSession` is the session the dispatch actually came from and
    /// rides the trail untouched. They differ only for a named registration.
    public MailPendingView Pending(string role, string? instance, string? hookSession)
    {
        var lines = Store.Read();

        // The frontier stops BEFORE an unterminated tail: those bytes may be
        // an append in flight, and a frontier inside them would consume mail
        // that does not exist yet. Complete lines only, from here down.
        var torn = lines.Count > 0 && !lines[^1].Terminated;
        var complete = torn ? lines.Take(lines.Count - 1).ToList() : lines.ToList();
        var frontier = torn
            ? lines[^1].Offset
            : complete.Count > 0 ? complete[^1].Offset + complete[^1].Bytes + 1 : 0;
        // Chain identity comes from COMPLETE lines only, like everything else:
        // a store that is one in-flight torn line has no head yet, and hashing
        // the partial bytes would persist a head that "changes" the moment the
        // append completes — a tamper-flavored false alarm on a healthy store.
        var head = complete.Count > 0 ? complete[0].Hash : null;

        var (cursor, reanchorReason) = LoadOrAnchor(role, instance, head, frontier, complete);

        var byOffset = complete.ToDictionary(l => l.Offset);
        var pending = new List<PendingMail>();
        var expired = new List<PendingMail>();
        var skipped = 0;

        // Held first (they are the oldest), verified against the file: after
        // LoadOrAnchor a held entry is known to point at a parseable line with
        // the recorded id, so this walk only classifies expiry.
        foreach (var h in cursor.Held)
        {
            var env = byOffset[h.Offset].Envelope!;
            var item = new PendingMail(h.Offset, env, h.SeenAt);
            // Passed over at opportunities seenAt..deliveries — that is
            // (deliveries − seenAt + 1) times. At ttl times, it is spent.
            //
            // A UNICAST envelope has no ttl (ADR-0018 d5) and therefore no
            // "spent": it is held until its one addressee takes it. That is not
            // a leak with no bound — a mailbox that never returns is the
            // reaper's problem (d6), which disposes of it by JUDGEMENT and
            // leaves a trail line, rather than by an arithmetic that quietly
            // drops mail nobody has read.
            if (env.TtlDeliveries is int ttl && cursor.Deliveries - h.SeenAt + 1 >= ttl)
                expired.Add(item);
            else pending.Add(item);
        }

        foreach (var line in complete.Where(l => l.Offset >= cursor.Offset))
        {
            if (line.Envelope is null)
            {
                // Unreadable bytes being stepped over. Counted only when they
                // could have been this role's mail — which is unknowable, so
                // every malformed line counts; the digest surfaces the number.
                skipped++;
                continue;
            }
            if (line.Envelope.To != role) continue;   // other roles' mail: invisible, consumed by the frontier
            pending.Add(new PendingMail(line.Offset, line.Envelope, SeenAt: null));
        }

        return new MailPendingView(
            role, instance, hookSession, cursor.Gen, head, cursor.Offset, frontier,
            cursor.Deliveries, cursor.LastDeliveredId,
            reanchorReason is not null, reanchorReason,
            pending, expired, skipped);
    }

    /// ADVANCE-ON-INJECT. The caller (phase 4's digest) calls this with the
    /// view it planned from and the offsets it is about to render into the
    /// effect — and it calls BEFORE emitting that effect: a crash between the
    /// two must lose the digest, never double it (the Stop-loop guard).
    /// One call consumes one delivery opportunity: `deliveries` += 1, pending
    /// mail not delivered becomes (or stays) held with its seenAt stamp, and
    /// expired mail is dropped with a `mail.expire` in the trail.
    ///
    /// AT-MOST-ONCE ACROSS PROCESSES is the lock + guard together: the check
    /// is a read-then-write (the store's own flock reasoning, one layer up),
    /// so it runs under a per-cursor flock — two concurrent digests for one
    /// (role, session) serialize here, the second finding the counter moved
    /// and refusing. The guard alone would be advisory; locked, it is the
    /// backstop.
    public MailCursorWrite Advance(
        MailPendingView view, IReadOnlyCollection<long> deliveredOffsets,
        int lockWaitMs = MailStore.DefaultLockWaitMs)
    {
        var path = CursorPath(view.Role, view.Session);
        FileStream? held = null;
        try
        {
            var delivered = new HashSet<long>(deliveredOffsets);
            var pendingOffsets = new HashSet<long>();
            foreach (var p in view.Pending)
                if (!pendingOffsets.Add(p.Offset))
                    return new MailCursorWrite.Failed(
                        $"the view lists offset {p.Offset} twice — refusing to advance from it");
            foreach (var o in delivered)
                if (!pendingOffsets.Contains(o))
                    // Delivering what is not pending is either a double
                    // (already behind the frontier) or an invention — both are
                    // the exact corruption this cursor exists to prevent.
                    return new MailCursorWrite.Failed(
                        $"offset {o} is not pending for {view.Role} — refusing to advance over it");

            Directory.CreateDirectory(Store.Dir, Private | UnixFileMode.UserExecute);
            held = MailStore.TryLock(path + ".lock", lockWaitMs, out var lockError);
            if (held is null) return new MailCursorWrite.Failed(lockError!);

            // THE CHAIN-CHANGED GUARD (the cursor-edge adversarial campaign's
            // find): the staleness guard below deliberately ignores a disk
            // cursor on a different chain ("vouches for nothing") — which is
            // right when the DISK cursor is the stale one, and exactly
            // backwards when the VIEW is: a view read from chain A, with the
            // store since replaced by chain B and B's mail already delivered
            // by a fresher digest, would sail past that guard and clobber B's
            // cursor — whereupon the next read re-anchors on the head
            // mismatch and B's delivered mail is pending AGAIN: a
            // double-inject needing no tampering, just a replacement between
            // read and advance (exactly what d13's rotation will one day do).
            // So the advance re-reads the store's identity under the lock and
            // refuses a view of a chain that is no longer there — the mail
            // the view wanted to deliver is gone or re-anchorable anyway, and
            // noop-now-redeliver-later is the safe direction. Honest bounds:
            // same-head tail truncation stays invisible here (chain-invisible
            // by phase 2's own statement — the truncation-reset re-anchor
            // owns it, loudly), and a replacement racing the microseconds
            // between this read and the rename below remains possible because
            // nothing serializes store replacement itself; this guard turns a
            // constructible interleaving into a vanishing one, and d13's
            // rotation machinery owns the real answer (a `gen` bump). (The
            // skeptic pass traced the residual window tighter than this first
            // stated: a competing advance needs this same per-cursor flock,
            // so no fresher delivery can land INSIDE the window — an
            // in-window replacement degrades to a stale cursor write the
            // next read re-anchors LOUDLY, redelivery being the safe
            // direction; the silent-clobber shape needs a head flap.)
            var currentHead = Store.HeadHash();
            if (currentHead != view.Head)
                return Refused(view.Role, view.HookSession, path,
                    $"the store's chain changed between the read and the advance (view read head "
                    + $"'{view.Head ?? "(none)"}', store now has '{currentHead ?? "(none)"}') "
                    + "— re-read before advancing");

            // The STALENESS guard (the If-Match pattern, ApiPolicyWriter's
            // precedent), now authoritative under the lock: a view whose
            // deliveries counter the on-disk cursor has moved past was planned
            // from consumed state — applying it would re-deliver everything it
            // delivered. Only comparable when the disk cursor describes the
            // same chain the view read; a cursor the view legitimately
            // re-anchored away from (malformed, foreign gen, replaced head)
            // vouches for nothing and blocks nothing — the chain-changed
            // guard above already proved the VIEW's chain is the live one.
            if (File.Exists(path))
            {
                if (MailCursor.TryParse(File.ReadAllText(path), out _) is { } disk
                    && disk.Gen == view.Gen && disk.Head == view.Head
                    && disk.Deliveries != view.Deliveries)
                    return Refused(view.Role, view.HookSession, path,
                        $"view is stale: it was read at {view.Deliveries} deliveries but the cursor "
                        + $"is at {disk.Deliveries} — re-read before advancing");
            }
            else if (view.Deliveries > 0)
                // The cursor this view was read from is GONE (d13: deletable
                // anytime). The advance proceeds — refusing would strand the
                // rendered digest for no gain, and redelivery-after-deletion
                // is the stated cost — but it proceeds LOUDLY: a vanished
                // lineage means mail this view already consumed may be
                // delivered again by whoever anchors fresh. (At deliveries 0
                // the same deletion is indistinguishable from first contact
                // and stays quiet — the one corner of the deletion cost that
                // CANNOT be loud, pinned as such by the edge campaign.)
                Log.Warn("mail", "mail.cursorVanished", new LogFields
                {
                    SessionId = view.HookSession,
                    Msg = "the cursor this view was read from no longer exists — advancing starts a fresh lineage",
                    Data = new Dictionary<string, object>
                    {
                        ["role"] = view.Role, ["path"] = path,
                        ["viewDeliveries"] = view.Deliveries,
                    },
                });

            var deliveries = view.Deliveries + 1;
            var heldList = view.Pending
                .Where(p => !delivered.Contains(p.Offset))
                .Select(p => new MailHeld(p.Offset, p.Envelope.Id, p.SeenAt ?? deliveries))
                .ToList();

            var lastDelivered = delivered.Count > 0
                ? view.Pending.First(p => p.Offset == delivered.Max()).Envelope.Id
                : view.LastDeliveredId;

            var cursor = new MailCursor(
                view.Gen, view.Head, view.Frontier, lastDelivered, deliveries, heldList);

            WriteAtomic(path, cursor.Render());

            // The expire record lands AFTER the rename, so the ledger states a
            // fact: had the write failed, the mail was NOT dropped, and an
            // expiry event for it would have been fiction (repeated fiction,
            // on every retry).
            //
            // WHO MOVED (ADR-0016 d14, the reducer's find): every event this
            // method emits names the cursor it is about — the session rides
            // the trail's first-class `sessionId` column (d10's as-built rule
            // for `mail.deliver`, so one session filter sees the whole
            // choreography) and the role rides data. Without it a watcher
            // cannot tell which of a role's sessions advanced, and a canvas
            // that guessed would draw a cursor moving that did not — the one
            // wrong picture no screenshot catches. The expire carries the
            // envelope's OFFSET beside its id because ids are not unique on
            // this bus (the store does not dedup); the advance carries the
            // exact offsets it consumed for the same reason: a count says how
            // many, an offset list says which, and only the list lets a
            // reader reproduce the held set the digest just wrote.
            foreach (var e in view.Expired)
                Log.Info("mail", "mail.expire", new LogFields
                {
                    SessionId = view.HookSession,
                    Msg = "mail expired undelivered: passed over at its full ttlDeliveries of opportunities",
                    Data = new Dictionary<string, object>
                    {
                        ["id"] = MailEnvelope.ClampField(e.Envelope.Id), ["to"] = view.Role,
                        ["offset"] = e.Offset,
                        // Non-null by construction: only the ttl comparison above
                        // puts an envelope in Expired, and it cannot fire without
                        // a ttl. Spelled `.Value` rather than defaulted, because
                        // a `?? 0` here would invent a number for a row whose
                        // whole job is to say which ttl was spent.
                        ["ttlDeliveries"] = e.Envelope.TtlDeliveries!.Value, ["seenAt"] = e.SeenAt!,
                    },
                });

            var advanceData = new Dictionary<string, object>
            {
                ["role"] = view.Role, ["offset"] = view.Frontier,
                ["delivered"] = delivered.Count,
                ["deliveredOffsets"] = delivered.OrderBy(o => o).ToList(),
                ["held"] = heldList.Count,
                ["expired"] = view.Expired.Count, ["deliveries"] = deliveries,
            };
            // WHICH MAILBOX, when that is not the same question as which window
            // (ADR-0018 d3). `sessionId` names the window that did the work;
            // `instance` names the cursor the work moved. They differ only for a
            // NAMED registration, and the column is written only then — so every
            // line a pre-ADR-0018 reader has ever seen keeps its exact shape,
            // and a reader that cares can tell one durable mailbox's advances
            // from another's without inferring anything from the session.
            //
            // This matters before the canvas learns instances (`canvas-instances`):
            // two windows sharing one `--as` are ONE cursor with one counter, and
            // a reader keying on `sessionId` alone would model two, then flag
            // every second advance as out-of-sequence.
            if (view.Named) advanceData["instance"] = view.Session!;

            Log.Debug("mail", "mail.cursorAdvance", new LogFields
            {
                SessionId = view.HookSession,
                Data = advanceData,
            });
            return new MailCursorWrite.Written(cursor, path);
        }
        catch (Exception ex)   // permissions, a directory at the path, a full disk
        {
            return new MailCursorWrite.Failed($"cannot advance cursor '{path}': {ex.Message}");
        }
        finally { held?.Dispose(); }
    }

    /// Load the cursor, or anchor a fresh one at 0. Every distrusted state —
    /// unreadable bytes, a strict-parse violation, a chain this cursor never
    /// walked, an offset the file contradicts — re-anchors LOUDLY, preserving
    /// the monotonic `deliveries` counter whenever the old value is readable
    /// (the TTL clock survives what the position does not).
    private (MailCursor Cursor, string? ReanchorReason) LoadOrAnchor(
        string role, string? session, string? head, long frontier, List<MailLine> complete)
    {
        var path = CursorPath(role, session);
        string text;
        try
        {
            if (!File.Exists(path))
                // First contact is an ANCHOR, not a re-anchor: offset 0 so the
                // whole retained store is pending — store-and-forward is the
                // point, and mail sent while nobody held the role must reach
                // the next session that does.
                return (Fresh(head, deliveries: 0), null);
            text = File.ReadAllText(path);
        }
        catch (Exception)
        {
            return Reanchor(role, session, path, head, deliveries: 0, ReanchorCause.Cursor, "cursor file unreadable");
        }

        var cursor = MailCursor.TryParse(text, out var errors);
        if (cursor is null)
            return Reanchor(role, session, path, head, deliveries: 0, ReanchorCause.Cursor,
                $"cursor malformed: {string.Join("; ", errors)}");

        if (cursor.Gen != CurrentGen)
            return Reanchor(role, session, path, head, cursor.Deliveries, ReanchorCause.Cursor,
                $"cursor gen {cursor.Gen}, store gen {CurrentGen} — rotated");

        if (cursor.Head != head && cursor.Head is not null)
            // A different first-line hash is a different CHAIN at the same
            // path (phase 2: every generation restarts at genesis) — rotation
            // or wholesale replacement. Either way this cursor's offsets
            // describe a file that no longer exists.
            return Reanchor(role, session, path, head, cursor.Deliveries, ReanchorCause.Store,
                "store head hash changed — a different chain at the same path");

        if (cursor.Offset > frontier)
            return Reanchor(role, session, path, head, cursor.Deliveries, ReanchorCause.Store,
                $"cursor offset {cursor.Offset} past the frontier {frontier} — store truncated");

        if (cursor.Offset != frontier && !complete.Any(l => l.Offset == cursor.Offset))
            // Legitimate offsets rest on a line boundary or at the frontier —
            // ours always do, so one that does not was written against bytes
            // that are gone (TrailCursor's alignment self-heal, one layer up).
            return Reanchor(role, session, path, head, cursor.Deliveries, ReanchorCause.Store,
                $"cursor offset {cursor.Offset} rests on no line boundary");

        foreach (var h in cursor.Held)
        {
            if (h.Offset >= cursor.Offset)
                return Reanchor(role, session, path, head, cursor.Deliveries, ReanchorCause.Cursor,
                    $"held entry at {h.Offset} is not behind the frontier {cursor.Offset}");
            var line = complete.FirstOrDefault(l => l.Offset == h.Offset);
            if (line?.Envelope is null || line.Envelope.Id != h.Id)
                return Reanchor(role, session, path, head, cursor.Deliveries, ReanchorCause.Store,
                    $"held entry at {h.Offset} (id '{h.Id}') no longer matches the file");
            if (line.Envelope.To != role)
                // A held entry may only ever name this role's own mail — a
                // cursor claiming another role's line is a changed file or a
                // hand edit, never a substitutable delivery.
                return Reanchor(role, session, path, head, cursor.Deliveries, ReanchorCause.Store,
                    $"held entry at {h.Offset} is addressed to '{line.Envelope.To}', not '{role}'");
        }

        return (cursor, null);
    }

    /// A guard refusal, made first-class on the trail (the skeptic pass's
    /// finding: without this, a refusal's loudness rode second-hand on the
    /// digest's captured stderr). Info, not Warn — a refused advance is
    /// usually a LEGITIMATE concurrent delivery winning the race, and the
    /// refusal is the guard doing its job, not a fault.
    private static MailCursorWrite.Failed Refused(string role, string? session, string path, string reason)
    {
        Log.Info("mail", "mail.cursorRefuse", new LogFields
        {
            SessionId = session,
            Msg = "cursor advance refused — the view is not safe to apply; the mail stays pending",
            Data = new Dictionary<string, object>
            {
                ["role"] = role, ["path"] = path, ["reason"] = reason,
            },
        });
        return new MailCursorWrite.Failed(reason);
    }

    private static MailCursor Fresh(string? head, long deliveries) =>
        new(CurrentGen, head, Offset: 0, LastDeliveredId: null, deliveries, []);

    /// Which side of the cursor⇄store agreement broke. `Cursor`: the file's
    /// own bytes are distrusted (unreadable, malformed, a foreign gen, a held
    /// entry ahead of its own frontier) and the STORE is believed intact —
    /// a watcher holding a picture of the ledger may keep it. `Store`: the
    /// bytes the cursor described are gone or changed (a different chain, a
    /// truncation, an offset off every boundary, a held line that no longer
    /// matches) — every picture of the ledger taken before this read is
    /// suspect. Ambiguous cases (a held mismatch could be either) are
    /// `Store`, the direction that costs a re-read rather than a lie.
    public enum ReanchorCause { Cursor, Store }

    // `deliveries` rides the event because it is the one thing a re-anchor
    // KEEPS: a watcher applying "offset 0, held empty" needs the counter the
    // fresh cursor inherits, or its next TTL arithmetic starts from a guess.
    // `cause` rides it because the reason is prose and a watcher must not
    // parse prose to learn whether its ledger picture survived (the reducer
    // skeptic pass's top find: a store-truncation re-anchor rebuilt a
    // pending set from lines that no longer existed, and nothing flagged).
    private (MailCursor, string?) Reanchor(
        string role, string? session, string path, string? head, long deliveries,
        ReanchorCause cause, string reason)
    {
        Log.Warn("mail", "mail.cursorReanchor", new LogFields
        {
            SessionId = session,
            Msg = "cursor re-anchored at 0 — retained mail for this role is pending again",
            Data = new Dictionary<string, object>
            {
                ["role"] = role, ["path"] = path, ["reason"] = reason,
                ["cause"] = cause == ReanchorCause.Store ? "store" : "cursor",
                ["deliveries"] = deliveries,
            },
        });
        return (Fresh(head, deliveries), reason);
    }

    // Sibling temp + same-dir rename — ApiPolicyWriter's atomic-install idiom,
    // with the temp CREATED 0600 (d13: cursors are private like everything in
    // this tree) rather than mode-fixed after, so no window ever exposes it.
    private void WriteAtomic(string path, string text)
    {
        Directory.CreateDirectory(Store.Dir, Private | UnixFileMode.UserExecute);
        var tmp = Path.Combine(
            Store.Dir, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var fs = new FileStream(tmp, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                UnixCreateMode = Private,
            }))
                fs.Write(Encoding.UTF8.GetBytes(text));
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) { try { File.Delete(tmp); } catch { /* best-effort */ } }
        }
    }
}
