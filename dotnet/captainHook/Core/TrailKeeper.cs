using System.Globalization;
using CaptainHook.Actors;

namespace CaptainHook.Core;

/// The trail's rotation and retention (ADR-0021 rules 1–4): ADR-0009's
/// segments, bounded by age. One `Tick` does at most two things, under the
/// rotate lock:
///   * ROTATE when the active file has reached the segment size: rename it to
///     `<stem>.<seq><ext>`; the next append (every writer opens per line with
///     O_APPEND, ADR-0004 N3) creates a fresh active file. A line racing the
///     rename lands whole in one file or the other.
///   * PRUNE renamed segments whose mtime (their newest line) is older than
///     the retention; the active file is never deleted.
///
/// Segment numbers come from the durable high-water mark `.trail-seq`, never
/// from the survivors alone: retention can delete the newest renamed segment
/// on a quiet machine, and a number derived from what is left would go
/// backwards, moving SSE ids already issued (ADR-0021 R1).
///
/// Only a daemon ticks (rule 2), and two daemons overlap during a deploy
/// handoff, so the lock file `.rotate.lock` serializes them: flock via
/// FileShare.None, released by the kernel on any death, never unlinked. A
/// keeper that cannot take it skips the tick.
///
/// Shipped dark in slice 1: nothing constructs a keeper yet. Slice 3 starts
/// it from DaemonHost once the cursor can follow a rename (rule 6).
public sealed class TrailKeeper
{
    /// The segment ceiling: seq × 2^40 + offset must stay below 2^53 (rule 5).
    internal const long MaxSeq = 1L << 13;

    internal const string SeqFile = ".trail-seq";
    internal const string LockFile = ".rotate.lock";

    private static readonly TimeSpan WarnEvery = TimeSpan.FromDays(1);

    private readonly string _active;
    private readonly long _segmentBytes;
    private readonly TimeSpan? _retention;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<long> _clock;
    private long? _heldWarnedAt, _exhaustedWarnedAt;

    /// `retention` null keeps every segment (ADR-0009's archive-forever).
    /// `now` is the wall clock retention is measured in (the age of persisted
    /// data, rule 3); `clock` is the monotonic source the once-a-day warnings
    /// pace themselves by (invariant 2).
    public TrailKeeper(string activePath, long segmentBytes, TimeSpan? retention,
                       Func<DateTimeOffset>? now = null, Func<long>? clock = null)
    {
        _active = activePath;
        _segmentBytes = Math.Max(1, segmentBytes);
        _retention = retention;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _clock = clock ?? (() => Environment.TickCount64);
    }

    public sealed record TickResult(bool LockHeld, int? Rotated, int Pruned, bool PruneHeld);

    public TickResult Tick()
    {
        var dir = Dir(_active);
        if (!Directory.Exists(dir)) return new(false, null, 0, false);
        using var lk = TryLock(dir);
        if (lk is null) return new(true, null, 0, false);

        int? rotated = null;
        try
        {
            if (File.Exists(_active) && new FileInfo(_active).Length >= _segmentBytes)
                rotated = Rotate();
        }
        catch (Exception ex)
        {
            Log.Warn("trail", "trail.rotateError", new LogFields { Msg = $"rotation skipped this tick: {ex.Message}" });
        }

        var (pruned, held) = _retention is { } r ? Prune(r) : (0, false);
        return new(false, rotated, pruned, held);
    }

    /// Rename the active file to the next segment number; returns that number,
    /// or null when the ceiling refuses it.
    private int? Rotate()
    {
        var seq = HighWater(_active) + 1;
        if (seq >= MaxSeq)
        {
            if (Due(ref _exhaustedWarnedAt))
                Log.Warn("trail", "trail.seqExhausted", new LogFields
                {
                    Msg = $"{MaxSeq} segment numbers used — not rotating; the trail grows unbounded, every id stays valid",
                });
            return null;
        }
        var bytes = new FileInfo(_active).Length;
        var target = SegmentPath(_active, seq);
        File.Move(_active, target, overwrite: false);
        WriteSeq(Dir(_active), seq);
        Log.Info("trail", "trail.rotate", new LogFields
        {
            Msg = $"rotated {bytes} bytes to {Path.GetFileName(target)}",
            Data = new Dictionary<string, object> { ["seq"] = seq, ["bytes"] = bytes },
        });
        return (int)seq;
    }

    /// Delete renamed segments past the retention — but only once the 0.1.3
    /// window's commands are safe in shell-history.0.jsonl (ADR-0009 d8):
    /// until then, nothing is deleted (rule 4).
    private (int Pruned, bool Held) Prune(TimeSpan retention)
    {
        if (!File.Exists(ShellTrail.LegacyPath(_active)))
        {
            try { ShellTrail.MigrateLegacy(_active); }
            catch (Exception ex)
            {
                if (Due(ref _heldWarnedAt))
                    Log.Warn("trail", "trail.pruneHeld", new LogFields
                    {
                        Msg = $"not pruning: the 0.1.3 window's commands are not copied out yet ({ex.Message})",
                    });
                return (0, true);
            }
        }

        var now = _now();
        var cutoff = now - retention;
        int pruned = 0;
        long bytes = 0;
        foreach (var (_, path) in Segments(_active))
        {
            try
            {
                var mtime = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
                // A future mtime (a clock step) is kept: no age can be proven.
                if (mtime > now || mtime >= cutoff) continue;
                var len = new FileInfo(path).Length;
                File.Delete(path);
                pruned++;
                bytes += len;
            }
            catch (Exception) { /* one stubborn segment never stops the rest */ }
        }
        if (pruned > 0)
            Log.Info("trail", "trail.prune", new LogFields
            {
                Msg = $"deleted {pruned} segment(s), {bytes} bytes, older than {retention.TotalDays:0.#} days",
                Data = new Dictionary<string, object> { ["segments"] = pruned, ["bytes"] = bytes },
            });
        return (pruned, false);
    }

    private bool Due(ref long? last)
    {
        var t = _clock();
        if (last is { } l && TimeSpan.FromMilliseconds(t - l) < WarnEvery) return false;
        last = t;
        return true;
    }

    // ---- the segment model, shared with readers ---------------------------

    /// The renamed segments beside `activePath`, ascending by number.
    public static IReadOnlyList<(long Seq, string Path)> Segments(string activePath)
    {
        var dir = Dir(activePath);
        var stem = Path.GetFileNameWithoutExtension(activePath);
        var ext = Path.GetExtension(activePath);
        var found = new List<(long, string)>();
        if (!Directory.Exists(dir)) return found;
        foreach (var f in Directory.EnumerateFiles(dir, $"{stem}.*{ext}"))
        {
            var middle = Path.GetFileName(f)[(stem.Length + 1)..^ext.Length];
            if (long.TryParse(middle, NumberStyles.None, CultureInfo.InvariantCulture, out var seq) && seq > 0)
                found.Add((seq, f));
        }
        return found.OrderBy(s => s.Item1).ToList();
    }

    /// The last segment number ever used: `.trail-seq`, or a surviving
    /// segment above it (a crash between rename and write, or a lost file).
    public static long HighWater(string activePath)
    {
        var fromFile = ReadSeq(Dir(activePath));
        var segs = Segments(activePath);
        return Math.Max(fromFile, segs.Count > 0 ? segs[^1].Seq : 0);
    }

    /// The active file's own segment number: it keeps it when renamed, so a
    /// line's id (seq × 2^40 + offset, rule 5) never changes.
    public static long ActiveSeq(string activePath) => HighWater(activePath) + 1;

    public static string SegmentPath(string activePath, long seq) =>
        Path.Combine(Dir(activePath),
            $"{Path.GetFileNameWithoutExtension(activePath)}.{seq.ToString(CultureInfo.InvariantCulture)}{Path.GetExtension(activePath)}");

    private static string Dir(string activePath) => Path.GetDirectoryName(Path.GetFullPath(activePath)) ?? ".";

    private static long ReadSeq(string dir)
    {
        try
        {
            return long.TryParse(File.ReadAllText(Path.Combine(dir, SeqFile)).Trim(), NumberStyles.None,
                CultureInfo.InvariantCulture, out var v) ? v : 0;
        }
        catch (Exception) { return 0; }
    }

    private static void WriteSeq(string dir, long seq)
    {
        var path = Path.Combine(dir, SeqFile);
        var tmp = $"{path}.{Environment.ProcessId}.tmp";
        using (var fs = new FileStream(tmp, new FileStreamOptions
        {
            Mode = FileMode.Create, Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        }))
        using (var w = new StreamWriter(fs))
            w.Write(seq.ToString(CultureInfo.InvariantCulture) + "\n");
        File.Move(tmp, path, overwrite: true);
    }

    /// flock(LOCK_EX|NB) through FileShare.None, as DaemonRendezvous takes the
    /// version lock: null when another keeper holds it.
    internal static FileStream? TryLock(string dir)
    {
        try
        {
            return new FileStream(Path.Combine(dir, LockFile), new FileStreamOptions
            {
                Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            });
        }
        catch (IOException) { return null; }
    }
}
