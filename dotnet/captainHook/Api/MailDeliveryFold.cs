using System.Text;
using System.Text.Json;

namespace CaptainHook.Api;

// mail-replay 6a (ADR-0016 d14, amended 2026-08-17): the delivery-record
// preload. Slice 5 made "delivered" real by folding `mail.deliver` off the live
// stream — but a stream only ever starts NOW, so every pickup that predates the
// page read *before cursor · no record* forever, which is the honest sentence
// for "the picture cannot see that far back" and a wrong-looking one for mail
// that was demonstrably read an hour ago. The fix is not to weaken the rule
// (d14 pin iii: DELIVERED comes from a `mail.deliver` line and nowhere else) —
// it is to hand the picture the older lines.
//
// Why the SERVER folds them:
//   * the client cannot ask for them. The SSE resume id is an opaque token a
//     client echoes and never interprets (ADR-0009 d2), so "subscribe from an
//     id older than the snapshot's" is arithmetic on a value that has no
//     arithmetic. The only client-reachable constant is "0" — replay the WHOLE
//     trail as live — which is precisely the thing `mail-stream-alignment`
//     exists to avoid;
//   * the snapshot already straddles both sources. It reads the store and it
//     stamps the trail's end; reading a second thing from the same trail file
//     adds no seam that was not already there.
// Observation is still not delivery: this reads a log file. It cannot append,
// cannot advance, and — like every other read behind `GET /mail` — is safe to
// serve on any request precisely because it changes nothing.
//
// What is deliberately NOT done here: resolving an envelope id to a ledger
// OFFSET. That is the reducer's arithmetic (`web/src/mail.ts`), and a second
// implementation of it in C# is N8 wearing a different hat — the picture would
// then have two disagreeing notions of which envelope a record refers to. The
// fold ships the ledger line's own columns, verbatim, and the reducer places
// them by the same rule it already uses for a record that arrives without its
// advance.

/// One `mail.deliver` line, as the trail stated it. Columns only — no
/// resolution, no derivation (see the note above).
public sealed record MailDeliveryLine(
    string Role,
    string? SessionId,
    string? DispatchId,
    string? HookEvent,
    string Seam,
    string Vehicle,
    IReadOnlyList<string> EnvelopeIds,
    string? RenderHash,
    long? BytesInjected,
    string? Ts);

/// What one fold saw, and how far back it could see. `Complete` is a narrow,
/// checkable claim: THIS FOLD READ THE WHOLE TRAIL FILE, from byte 0, and
/// dropped nothing to a cap. Anything else — a scan window that started inside
/// the file, a record cap that trimmed, a trail that is absent, unreadable, or
/// not served at all — is false, because in every one of those cases an
/// envelope with no record may simply predate what the fold could see. The
/// picture says "no record" either way; this is what lets it say which of the
/// two it means, and it never over-claims: a daemon serving no trail cannot
/// prove that nobody read anything.
public sealed record MailDeliveryFoldResult(
    IReadOnlyList<MailDeliveryLine> Records,
    bool Complete);

public static class MailDeliveryFold
{
    /// How far back into the trail one fold reads. The trail is operational
    /// telemetry with a days-to-weeks life (d13) and payload stderr lands in it
    /// (ADR-0010), so its size is not bounded by anything this project controls;
    /// a snapshot must not become a whole-file read on a busy machine. 4 MiB of
    /// a trail whose lines run ~300B is on the order of ten thousand events —
    /// far more history than a bus canvas draws.
    public const long MaxScanBytes = 4L * 1024 * 1024;

    /// And how many records survive it, newest kept. The reducer caps its own
    /// list (`MAIL_DELIVERIES_CAP`); this is the wire-side bound so a pathological
    /// trail cannot make one snapshot enormous.
    public const int MaxRecords = 500;

    /// Fold `mail.deliver` lines out of the trail file. Every failure answers
    /// "nothing yet", never an error — the same answer `TrailLength` gives, and
    /// for the same reason: a snapshot whose delivery history is missing is a
    /// picture with an honest gap, while a 500 is no picture at all.
    public static MailDeliveryFoldResult Read(string? path, long maxScanBytes = MaxScanBytes, int maxRecords = MaxRecords)
    {
        // No trail served: no history, and no standing to call that history
        // complete — absence of a record proves nothing when there is no file
        // in which a record could ever have been found.
        if (path is null) return new MailDeliveryFoldResult([], false);

        var records = new List<MailDeliveryLine>();
        var complete = true;

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            if (fs.Length > maxScanBytes)
            {
                // Start inside the file, then discard forward to the next line
                // boundary — TrailCursor's rule, for the same reason: an offset
                // that does not rest on a '\n' would hand the parser a half
                // line. A UTF-8 continuation byte can never be 0x0A, so the
                // byte split is safe ahead of the decode.
                fs.Seek(fs.Length - maxScanBytes, SeekOrigin.Begin);
                complete = false;
                if (!DiscardToLineBoundary(fs)) return new MailDeliveryFoldResult([], false);
            }

            using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
            while (reader.ReadLine() is { } line)
            {
                // A cheap gate, not a decision: `exec.stderr` puts arbitrary
                // payload output in this file, so a line CONTAINING this text
                // proves nothing. The parse below is what decides.
                if (line.Length == 0 || !line.Contains("\"mail.deliver\"", StringComparison.Ordinal)) continue;
                if (TryParse(line) is { } rec) records.Add(rec);
            }
        }
        catch (IOException) { return new MailDeliveryFoldResult([], false); }
        catch (UnauthorizedAccessException) { return new MailDeliveryFoldResult([], false); }

        if (records.Count > maxRecords)
        {
            records.RemoveRange(0, records.Count - maxRecords);
            complete = false;
        }
        return new MailDeliveryFoldResult(records, complete);
    }

    /// True when the stream now rests just past a '\n'; false when the rest of
    /// the window holds no boundary at all (one line longer than the scan).
    private static bool DiscardToLineBoundary(FileStream fs)
    {
        var buf = new byte[8192];
        int n;
        while ((n = fs.Read(buf, 0, buf.Length)) > 0)
        {
            var nl = Array.IndexOf(buf, (byte)'\n', 0, n);
            if (nl >= 0)
            {
                fs.Seek(-(n - nl - 1), SeekOrigin.Current);
                return true;
            }
        }
        return false;
    }

    /// One trail line ⇒ a record, or null for anything that is not a
    /// well-formed `mail.deliver`. Malformed is SKIPPED rather than reported:
    /// unlike a store line — whose malformation is a fact about the bus an
    /// operator must see — a trail line the fold cannot read is a fact about the
    /// LOG, and the snapshot already says the history may be incomplete.
    private static MailDeliveryLine? TryParse(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (Str(root, "evt") != "mail.deliver") return null;
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return null;

            var role = Str(data, "role");
            var seam = Str(data, "seam");
            var vehicle = Str(data, "vehicle");
            if (role is null || seam is null || vehicle is null) return null;

            if (!data.TryGetProperty("envelopeIds", out var idsEl) || idsEl.ValueKind != JsonValueKind.Array) return null;
            var ids = new List<string>(idsEl.GetArrayLength());
            foreach (var el in idsEl.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.String) return null;
                if (SafeString(el) is not { } s) return null;
                ids.Add(s);
            }

            long? bytes = null;
            if (data.TryGetProperty("bytesInjected", out var b) && b.ValueKind == JsonValueKind.Number
                && b.TryGetInt64(out var bv)) bytes = bv;

            return new MailDeliveryLine(
                role, Str(root, "sessionId"), Str(root, "dispatchId"), Str(root, "hookEvent"),
                seam, vehicle, ids, Str(data, "renderHash"), bytes, Str(root, "ts"));
        }
        catch (JsonException) { return null; }
    }

    private static string? Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? SafeString(el) : null;

    /// `JsonDocument` defers unescaping, so a lone-surrogate escape parses as a
    /// valid document and throws at `GetString` rather than at `Parse` — the
    /// trap the policy skeptic pass found (ADR-0015, `TryReadString`). A trail
    /// line is arbitrary bytes from two emitters and payload output besides, so
    /// the read is guarded here too.
    private static string? SafeString(JsonElement el)
    {
        try { return el.GetString(); }
        catch (InvalidOperationException) { return null; }
    }
}
