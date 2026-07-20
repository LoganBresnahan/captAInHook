using System.Text;
using System.Text.Json;
using CaptainHook.Core;

namespace CaptainHook.Api;

// PUT /api/v1/handlers (ADR-0011 decision 3): the API as EDITOR OF THE FILE,
// on ApiPolicyWriter's proven pattern — validate with the daemon's OWN parser,
// honor If-Match when supplied, install atomically via sibling-temp + same-dir
// rename, and let ReloadingHandlers' stat-gate make it effective on the next
// dispatch exactly as a hand edit does. See ApiPolicyWriter for the two sharp
// edges (same-filesystem rename atomicity; BOM-strip so writer and loader
// agree) — both apply verbatim here and are pinned by ApiHandlersWriteTests.
//
// The ONE divergence from the policy writer (ADR-0011 d3's tightening): the
// handlers parser is deliberately FILE-strict/ENTRY-lenient at registration
// (ADR-0010 d4 — a typo'd entry warns-and-skips so one bad entry never kills
// the rest), but the WRITE path refuses BOTH fault classes. A body whose parse
// yields any skipped entry is Invalid here: the API must never *install* a
// known-broken entry ("installing" is the consent the verbatim-confirm gated —
// silently persisting an entry that will never register betrays it). Hand
// edits keep d4's leniency untouched — the split lives in this writer, not in
// ExecHandlersFile.
public sealed class ApiHandlersWriter
{
    private readonly string _path;

    public ApiHandlersWriter(string path) => _path = path;

    /// The file this writer edits — the SAME path the read model reports and
    /// the daemon's ReloadingHandlers stat-gates (DaemonHost wires one
    /// handlersPath into all three), so a PUT is effective on the next dispatch
    /// with zero coordination.
    public string Path => _path;

    /// Validate `body`, honor `ifMatch` if supplied, and atomically install the
    /// handlers file. Never throws: every failure mode lands in an outcome case
    /// (the ApiPolicyWriter.Write contract).
    public HandlersWriteOutcome Write(byte[] body, string? ifMatch)
    {
        // Strict UTF-8 + BOM strip: identical reasoning to ApiPolicyWriter —
        // the loader (File.ReadAllText) strips a leading BOM, so the writer
        // must accept what the loader would load, and the written bytes stay
        // BOM-free so the returned ETag equals the one a subsequent GET
        // computes over the file.
        string text;
        try { text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(body); }
        catch (Exception ex) when (ex is DecoderFallbackException or ArgumentException)
        {
            return new HandlersWriteOutcome.Invalid(new[] { "request body is not valid UTF-8" });
        }
        if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..];

        // Parse + validate with the daemon's own strict parser. File-level
        // errors are Invalid as everywhere; a non-empty skipped list is ALSO
        // Invalid here (d3's write-side strictness) — each skipped entry's
        // violations are surfaced labeled so the client can fix the exact
        // entry, mirroring the parser's own per-entry collection.
        IReadOnlyList<SkippedEntry> skipped;
        IReadOnlyList<string> fileErrors;
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (!ExecHandlersFile.TryParse(doc.RootElement, out _, out skipped, out fileErrors))
                return new HandlersWriteOutcome.Invalid(fileErrors);
        }
        catch (JsonException ex)
        {
            return new HandlersWriteOutcome.Invalid(new[] { $"not valid JSON: {ex.Message}" });
        }
        if (skipped.Count > 0)
            return new HandlersWriteOutcome.Invalid(
                skipped.SelectMany(s => s.Violations.Select(v => $"{s.Label}: {v}")).ToList());

        // Preconditions AFTER validation (RFC 7232 §6 — a 422 wins regardless
        // of If-Match), exactly as the policy writer orders them.
        var current = TryCurrentEtag();
        if (!string.IsNullOrWhiteSpace(ifMatch) && !IfMatchSatisfied(ifMatch!, current))
            return new HandlersWriteOutcome.Mismatch(current);

        try
        {
            AtomicInstall(text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new HandlersWriteOutcome.Failed(ex.Message);
        }

        return new HandlersWriteOutcome.Written(ApiReadModel.Etag(text));
    }

    // Sibling temp + same-dir rename — the atomic swap ApiPolicyWriter
    // documents (rename(2) is atomic only within one filesystem; a /tmp temp
    // would degrade to copy+delete and flash a torn state at the stat-gated
    // reader). Atomicity, not durability: no fsync, same as a hand `mv`.
    private void AtomicInstall(string text)
    {
        var full = System.IO.Path.GetFullPath(_path);
        var dir = System.IO.Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(dir);
        var tmp = System.IO.Path.Combine(
            dir, "." + System.IO.Path.GetFileName(full) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(tmp, text);            // UTF-8, no BOM
            File.Move(tmp, full, overwrite: true);   // same-dir => rename(2), atomic
        }
        finally
        {
            if (File.Exists(tmp)) { try { File.Delete(tmp); } catch { /* best-effort */ } }
        }
    }

    // The current file's ETag, computed exactly as GET /handlers does
    // (ReadAllText → the shared Etag scheme), or null if absent/unreadable. A
    // null current fails any If-Match, including "*" — the client believed it
    // was editing content that is not there.
    private string? TryCurrentEtag()
    {
        try { return File.Exists(_path) ? ApiReadModel.Etag(File.ReadAllText(_path)) : null; }
        catch { return null; }
    }

    // RFC 7232 §3.1 — identical to ApiPolicyWriter.IfMatchSatisfied: any listed
    // strong tag equal to current, or "*" with the resource present.
    private static bool IfMatchSatisfied(string ifMatch, string? current)
    {
        foreach (var raw in ifMatch.Split(','))
        {
            var tag = raw.Trim();
            if (tag == "*") { if (current is not null) return true; }
            else if (tag == current) return true;
        }
        return false;
    }
}

/// The closed outcome set of a handlers write, mapped 1:1 to HTTP by ApiHost —
/// its own DU (not PolicyWriteOutcome) so each domain's mapping stays
/// exhaustive per type, the house closed-set pattern.
public abstract record HandlersWriteOutcome
{
    private HandlersWriteOutcome() { }   // closed set: exactly the four below

    /// The file was replaced. Etag is over exactly what we wrote —
    /// authoritative for the client's next If-Match.
    public sealed record Written(string Etag) : HandlersWriteOutcome;

    /// The body could not become an installable handlers file — bad UTF-8, bad
    /// JSON, file-level violations, or any entry the daemon would warn-and-skip
    /// (d3: the write path refuses what registration would tolerate) (422).
    public sealed record Invalid(IReadOnlyList<string> Violations) : HandlersWriteOutcome;

    /// If-Match was supplied and did not match the file's current tag (or the
    /// file is gone). Current is the tag now on disk, or null if absent (412).
    public sealed record Mismatch(string? Current) : HandlersWriteOutcome;

    /// The write itself failed; the file is untouched — the rename is
    /// all-or-nothing (500).
    public sealed record Failed(string Message) : HandlersWriteOutcome;
}
