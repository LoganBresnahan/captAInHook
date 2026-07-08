using System.Text;
using System.Text.Json;
using CaptainHook.Core;

namespace CaptainHook.Api;

// put-policy-write (ADR-0007 decision 4): the API as EDITOR OF THE FILE, never
// owner of state. A PUT /policy validates the body with the SAME strict
// DispatchPolicy.TryParse the daemon loads with — the API refuses to write what
// the daemon would refuse to load — then writes it ATOMICALLY and lets
// ReloadingPolicy's stat-gate pick it up on the next dispatch, exactly as a hand
// edit does. No parallel store, no cache, no API-held config.
//
// Two sharp edges, both the adversarial-verify surface the ADR names:
//
//  1. ATOMICITY via temp+rename IN THE TARGET'S OWN DIRECTORY. A concurrent hook
//     stat-gating this file must see either the old inode or the new one, never
//     a truncated/absent write — else it transiently resolves Absent (allow-all)
//     or Malformed (deny-all) mid-write. rename(2) gives that atomicity, but ONLY
//     within one filesystem: the classic `Path.GetTempFileName()` (which lands in
//     /tmp, often a separate device/tmpfs) + `File.Move` degrades to a
//     cross-device copy+delete — non-atomic, and it PASSES green single-threaded
//     tests while corrupting the live hot path under load. So the temp is a
//     sibling of the target, and Move(overwrite:true) is a same-dir rename.
//     Atomicity, not durability: like a `mv` hand-edit, we do not fsync — a
//     concurrent reader is protected; surviving a kernel panic mid-write is not
//     this seam's promise.
//
//  2. TRI-STATE → HTTP. The result is a closed set the HTTP layer maps 1:1:
//       Written(etag)   -> 200 + ETag         the happy path
//       Invalid(errs)   -> 422                 bad UTF-8 / bad JSON / bad policy
//       Mismatch(cur)   -> 412                 If-Match supplied, file moved under us
//       Failed(msg)     -> 500                 I/O we could not complete
//
// Concurrency is GUARDED, not locked (ADR-0007 d4): GET returns a content-hash
// ETag, If-Match on PUT is honored when supplied. It narrows the blind-overwrite
// window but does not close it — the etag read and the rename are not one atomic
// step, so two racing PUTs both pass If-Match and the later rename wins. That is
// the accepted contract (a hand editor has the same race); the file is always a
// VALID whole policy, which is the invariant that matters to the hot path.
public sealed class ApiPolicyWriter
{
    private readonly string _path;

    public ApiPolicyWriter(string path) => _path = path;

    /// The file this writer edits — the SAME path the read model reports and the
    /// daemon's ReloadingPolicy stat-gates (DaemonHost wires one policyPath into
    /// all three), so a PUT is effective on the next dispatch with zero
    /// coordination.
    public string Path => _path;

    /// Validate `body`, honor `ifMatch` if supplied, and atomically install the
    /// policy. Never throws: every failure mode lands in a Result case, because
    /// this runs on an API request thread and a throw would only reach
    /// HandleAsync's catch-all as an opaque 500 — better to name the fault.
    public PolicyWriteOutcome Write(byte[] body, string? ifMatch)
    {
        // Strict UTF-8: an invalid byte sequence is a malformed request, not a
        // silent mojibake write. Decoding to text (rather than parsing the bytes
        // directly) also pins the round-trip: File.WriteAllText / ReadAllText both
        // use UTF-8 without BOM, so the ETag we return over `text` equals the one
        // a subsequent GET computes over the file it reads back.
        string text;
        try { text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(body); }
        catch (Exception ex) when (ex is DecoderFallbackException or ArgumentException)
        {
            return new PolicyWriteOutcome.Invalid(new[] { "request body is not valid UTF-8" });
        }

        // Strip a leading BOM so the writer and the daemon's LOADER agree. The
        // load path (PolicyResolution.Resolve / GET) reads via File.ReadAllText,
        // which silently strips a leading U+FEFF — so a BOM-prefixed body the
        // daemon would happily load must not be rejected here (JsonDocument.Parse
        // throws on a leading BOM). Stripping also keeps the written bytes
        // BOM-free, so the ETag we return over `text` equals the one GET computes
        // over File.ReadAllText of the same file — the round-trip If-Match depends
        // on. (Found by the put-policy-write adversarial verify, 2026-07-08.)
        if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..];

        // Parse + validate with the daemon's own strict parser. A JsonException is
        // caught here (bad bytes are the caller's, per DispatchPolicy.TryParse's
        // contract); a well-formed-but-invalid policy comes back as errors.
        DispatchPolicy? parsed;
        IReadOnlyList<string> errors;
        try
        {
            using var doc = JsonDocument.Parse(text);
            parsed = DispatchPolicy.TryParse(doc.RootElement, out errors);
        }
        catch (JsonException ex)
        {
            return new PolicyWriteOutcome.Invalid(new[] { $"not valid JSON: {ex.Message}" });
        }
        if (parsed is null)
            return new PolicyWriteOutcome.Invalid(errors);

        // Preconditions are evaluated AFTER validation on purpose (RFC 7232 §6: a
        // server ignores preconditions when the unconditional response would not
        // be 2xx/412 — a 422 for a bad body wins regardless of If-Match). Reading
        // the current etag here also decides absent-vs-present for If-Match: "*".
        var current = TryCurrentEtag();
        if (!string.IsNullOrWhiteSpace(ifMatch) && !IfMatchSatisfied(ifMatch!, current))
            return new PolicyWriteOutcome.Mismatch(current);

        try
        {
            AtomicInstall(text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new PolicyWriteOutcome.Failed(ex.Message);
        }

        return new PolicyWriteOutcome.Written(ApiReadModel.Etag(text));
    }

    // Write to a sibling temp file, then rename over the target — a same-directory
    // (same-filesystem) rename is the atomic swap. The temp is best-effort cleaned
    // on any failure so a botched write never litters. Directory.CreateDirectory
    // is idempotent and mirrors what a hand-edit needs (the dir must exist to hold
    // the file); it never retightens an existing dir's mode.
    private void AtomicInstall(string text)
    {
        var full = System.IO.Path.GetFullPath(_path);
        var dir = System.IO.Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(dir);
        // A random sibling name: dotfile-hidden, unique per PUT so concurrent
        // writers never collide on the temp itself (only the final rename races,
        // which is the guarded-not-locked contract). Runtime Guid — build
        // determinism governs compiled bytes, not runtime filenames.
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

    // The current file's ETag, computed exactly as GET does (ReadAllText → the
    // shared Etag scheme), or null if the file is absent or unreadable. A null
    // current means any concrete If-Match — and If-Match: "*" — fails the
    // precondition: the client believed it was editing content that is not there.
    private string? TryCurrentEtag()
    {
        try { return File.Exists(_path) ? ApiReadModel.Etag(File.ReadAllText(_path)) : null; }
        catch { return null; }
    }

    // RFC 7232 §3.1 If-Match: a comma-separated list of entity-tags, or "*".
    // Satisfied when any listed tag equals the current strong tag, or "*" is
    // present AND the resource exists. Strong comparison only (we never emit weak
    // tags; a W/-prefixed token simply won't equal our quoted-hex tag).
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

/// The closed outcome set of a policy write, mapped 1:1 to HTTP by ApiHost. A DU
/// so the mapping is exhaustive and a new case can't silently fall through to a
/// wrong status (house pattern: PolicyResolution, SseEvent).
public abstract record PolicyWriteOutcome
{
    private PolicyWriteOutcome() { }   // closed set: exactly the four below

    /// The file was replaced. Etag is over exactly what we wrote — authoritative
    /// for the client's next If-Match, independent of any concurrent write that
    /// may land before the client's next GET.
    public sealed record Written(string Etag) : PolicyWriteOutcome;

    /// The body could not become a valid policy — bad UTF-8, bad JSON, or a
    /// policy the daemon would refuse to load. Violations are the parser's own,
    /// one per fault (422).
    public sealed record Invalid(IReadOnlyList<string> Violations) : PolicyWriteOutcome;

    /// If-Match was supplied and did not match the file's current tag (or the
    /// file is gone). Current is the tag now on disk, or null if absent (412).
    public sealed record Mismatch(string? Current) : PolicyWriteOutcome;

    /// The write itself failed (permissions, disk, a non-rename Move). The file
    /// is untouched — the rename is all-or-nothing (500).
    public sealed record Failed(string Message) : PolicyWriteOutcome;
}
