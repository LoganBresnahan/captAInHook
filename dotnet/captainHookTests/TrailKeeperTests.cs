using System.Collections.Concurrent;
using CaptainHook.Core;
using CaptainHook.Wire;

namespace CaptainHook.Tests;

// ADR-0021 slice 1: the TrailKeeper rotates the trail by size under a lock,
// numbers segments from a durable high-water mark, and prunes by age, never
// before the 0.1.3 window's commands are copied out. Shipped dark: nothing
// starts a keeper yet; these tests drive Tick() directly.
public class TrailKeeperTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chk-keeper-" + Guid.NewGuid().ToString("N")[..8]);
    private string Active => Path.Combine(_dir, "captainHook.jsonl");
    private string Seg(long n) => Path.Combine(_dir, $"captainHook.{n}.jsonl");
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
    private long _tick;

    public TrailKeeperTests()
    {
        Directory.CreateDirectory(_dir);
        // The legacy copy exists, as after any 0.1.4 daemon start: pruning is free to run.
        File.WriteAllText(Path.Combine(_dir, "shell-history.0.jsonl"), "");
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private TrailKeeper Keeper(long segmentBytes = 100, TimeSpan? retention = null) =>
        new(Active, segmentBytes, retention, now: () => Now, clock: () => _tick);

    private void Fill(string path, int bytes) => File.WriteAllText(path, new string('x', bytes - 1) + "\n");

    private void Age(string path, TimeSpan age) => File.SetLastWriteTimeUtc(path, (Now - age).UtcDateTime);

    [Fact]
    public void BelowTheSegmentSize_NothingRotates()
    {
        Fill(Active, 99);
        Assert.Null(Keeper().Tick().Rotated);
        Assert.True(File.Exists(Active));
        Assert.Empty(TrailKeeper.Segments(Active));
    }

    [Fact]
    public void AtTheSegmentSize_TheActiveFileBecomesSegmentOne_AndTheMarkIsWritten()
    {
        using var log = new CapturedLog();
        Fill(Active, 100);

        Assert.Equal(1, Keeper().Tick().Rotated);

        Assert.False(File.Exists(Active));
        Assert.Equal(100, new FileInfo(Seg(1)).Length);
        Assert.Equal("1", File.ReadAllText(Path.Combine(_dir, ".trail-seq")).Trim());
        Assert.Equal(2, TrailKeeper.ActiveSeq(Active));
        Assert.Contains(log.Events, e => e.Evt == "trail.rotate");
    }

    /// ADR-0021 R1: pruning every segment must not pull the numbers back.
    [Fact]
    public void PruningEverySegment_ThenRotating_KeepsTheNumbersRising()
    {
        var k = Keeper(retention: TimeSpan.FromDays(30));
        for (var i = 1; i <= 3; i++) { Fill(Active, 100); Assert.Equal(i, k.Tick().Rotated); }
        foreach (var (_, p) in TrailKeeper.Segments(Active)) Age(p, TimeSpan.FromDays(31));

        Assert.Equal(3, k.Tick().Pruned);
        Assert.Empty(TrailKeeper.Segments(Active));
        Assert.Equal(4, TrailKeeper.ActiveSeq(Active));   // the active file did not drop to 1

        Fill(Active, 100);
        Assert.Equal(4, k.Tick().Rotated);
        Assert.True(File.Exists(Seg(4)));
    }

    /// A crash between the rename and the mark, or a lost mark: the surviving
    /// segments are the floor.
    [Fact]
    public void ALostMark_IsHealedBySurvivingSegments()
    {
        Fill(Seg(7), 10);
        Assert.Equal(7, TrailKeeper.HighWater(Active));
        Fill(Active, 100);
        Assert.Equal(8, Keeper().Tick().Rotated);
    }

    [Fact]
    public void Retention_DeletesOldSegments_KeepsNewAndFutureOnes_NeverTheActiveFile()
    {
        using var log = new CapturedLog();
        Fill(Seg(1), 10); Age(Seg(1), TimeSpan.FromDays(40));
        Fill(Seg(2), 10); Age(Seg(2), TimeSpan.FromDays(29));
        Fill(Seg(3), 10); Age(Seg(3), TimeSpan.FromDays(-2));    // a clock step: no age provable
        Fill(Active, 10); Age(Active, TimeSpan.FromDays(400));   // ancient, but active

        Assert.Equal(1, Keeper(retention: TimeSpan.FromDays(30)).Tick().Pruned);

        Assert.False(File.Exists(Seg(1)));
        Assert.True(File.Exists(Seg(2)));
        Assert.True(File.Exists(Seg(3)));
        Assert.True(File.Exists(Active));
        Assert.Contains(log.Events, e => e.Evt == "trail.prune");
    }

    [Fact]
    public void NoRetention_KeepsEverything()
    {
        Fill(Seg(1), 10); Age(Seg(1), TimeSpan.FromDays(4000));
        Assert.Equal(0, Keeper(retention: null).Tick().Pruned);
        Assert.True(File.Exists(Seg(1)));
    }

    /// Two daemons overlap during a deploy handoff: one keeper at a time.
    [Fact]
    public void AHeldLock_SkipsTheTick()
    {
        Fill(Active, 100);
        using (TrailKeeper.TryLock(_dir))
        {
            var r = Keeper().Tick();
            Assert.True(r.LockHeld);
            Assert.Null(r.Rotated);
            Assert.True(File.Exists(Active));
        }
        Assert.Equal(1, Keeper().Tick().Rotated);
    }

    /// ADR-0009 d8 / ADR-0021 rule 4: until the 0.1.3 window is copied out,
    /// nothing is deleted; rotation (which deletes nothing) still runs, and
    /// the warning is paced to once a day.
    [Fact]
    public void AFailedMigration_HoldsPruning_NotRotation()
    {
        using var log = new CapturedLog();
        File.Delete(Path.Combine(_dir, "shell-history.0.jsonl"));
        // Make the copy fail: a directory squats on its temp file's name.
        Directory.CreateDirectory(Path.Combine(_dir, $".shell-history.0.jsonl.{Environment.ProcessId}.tmp"));
        Fill(Seg(1), 10); Age(Seg(1), TimeSpan.FromDays(40));
        Fill(Active, 100);
        var k = Keeper(retention: TimeSpan.FromDays(30));

        var r = k.Tick();
        Assert.True(r.PruneHeld);
        Assert.Equal(2, r.Rotated);
        Assert.True(File.Exists(Seg(1)));

        k.Tick();                                   // the same day: no second warning
        _tick += (long)TimeSpan.FromDays(1).TotalMilliseconds;
        k.Tick();                                   // a day on: warned again
        Assert.Equal(2, log.Events.Count(e => e.Evt == "trail.pruneHeld"));
    }

    [Fact]
    public void AMissingMigration_IsRunBeforeTheFirstPrune()
    {
        File.Delete(Path.Combine(_dir, "shell-history.0.jsonl"));
        File.WriteAllText(Seg(1), """{"ts":"2026-09-01T00:00:00.000Z","lvl":"info","src":"tool","evt":"tool.shell","data":{"command":"from 0.1.3"}}""" + "\n");
        Age(Seg(1), TimeSpan.FromDays(40));

        Assert.Equal(1, Keeper(retention: TimeSpan.FromDays(30)).Tick().Pruned);

        Assert.Contains("from 0.1.3", File.ReadAllText(Path.Combine(_dir, "shell-history.0.jsonl")));
    }

    [Fact]
    public void PastTheCeiling_RotationIsRefused_AndSaidOnce()
    {
        using var log = new CapturedLog();
        File.WriteAllText(Path.Combine(_dir, ".trail-seq"), $"{TrailKeeper.MaxSeq - 1}\n");
        Fill(Active, 100);
        var k = Keeper();

        Assert.Null(k.Tick().Rotated);
        Assert.Null(k.Tick().Rotated);
        Assert.True(File.Exists(Active));
        Assert.Single(log.Events, e => e.Evt == "trail.seqExhausted");
    }

    /// The writers' O_APPEND opens race the rename: every line lands whole in
    /// exactly one file, none lost, none split. 20,000 lines from both emitters
    /// against a keeper rotating every ~2 KiB: before both writers looped their
    /// create-and-reopen, this lost a line in roughly every fifty rotations.
    [Fact]
    public async Task ConcurrentAppends_RacingRotations_LoseAndSplitNothing()
    {
        const int writers = 4, perWriter = 5000;
        var k = Keeper(segmentBytes: 2048);
        using var stop = new CancellationTokenSource();
        var rotations = 0;
        var failedAppends = 0;
        var keeper = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                if (k.Tick().Rotated is not null) Interlocked.Increment(ref rotations);
                await Task.Yield();
            }
        });
        await Task.WhenAll(Enumerable.Range(0, writers).Select(w => Task.Run(() =>
        {
            for (var i = 0; i < perWriter; i++)
            {
                // Both emitters, as in production: the shim's (wire lib) and the daemon's (F#).
                var line = $"{{\"w\":{w},\"i\":{i},\"pad\":\"{new string('p', 40)}\"}}";
                if (w % 2 == 0) WireJsonl.Append(Active, line);
                else if (!CaptainHook.Actors.PosixTrail.append(Active, line + "\n")) Interlocked.Increment(ref failedAppends);
            }
        })));
        stop.Cancel();
        await keeper;

        Assert.True(rotations >= 2, $"the test must actually race rotations (got {rotations})");
        var seen = new ConcurrentDictionary<string, int>();
        foreach (var f in ShellTrail.Files(Active))
            foreach (var line in File.ReadLines(f))
            {
                Assert.Matches("^\\{\"w\":\\d,\"i\":\\d+,\"pad\":\"p{40}\"\\}$", line);   // whole
                seen.AddOrUpdate(line[..line.IndexOf(",\"pad", StringComparison.Ordinal)], 1, (_, n) => n + 1);
            }
        var missing = Enumerable.Range(0, writers).SelectMany(w => Enumerable.Range(0, perWriter).Select(i => $"{{\"w\":{w},\"i\":{i}"))
            .Where(k => !seen.ContainsKey(k)).ToList();
        Assert.True(missing.Count == 0, $"[{failedAppends} appends returned false] {missing.Count} lost of {writers * perWriter} over {rotations} rotations: {string.Join(" ", missing.Take(10))}");
        Assert.All(seen.Values, n => Assert.Equal(1, n));             // none doubled
    }
}
