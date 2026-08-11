using CaptainHook.Wire;

namespace CaptainHook.Tests;

// The trail is written by TWO processes at once — the shim relaying one hook
// while the daemon dispatches another — and ADR-0004 N3's convergence story
// assumes each line lands whole. Every BCL append path breaks that assumption
// the same way: no O_APPEND, then a pwrite at an offset resolved when the file
// was opened, so two writers in one window compute the same end offset and one
// line SILENTLY overwrites the other (strace-probed; doc/platform.md § File
// locking). Both emitters now open with real O_APPEND — the wire lib's
// `PosixTrail` for the shim, its F# mirror for the daemon (the leaf may not
// reference the wire lib, so the code is duplicated and both are pinned here).
//
// These tests are about the WRITE mechanics only; the rendered bytes are
// `WireJsonlTests`' golden job.

public class TrailAppendTests : IDisposable
{
    private readonly string _dir = Path.Combine("/tmp", "chk-trail-" + Guid.NewGuid().ToString("N")[..8]);
    private string Trail => Path.Combine(_dir, "logs", "captainHook.jsonl");

    // Both emitters are called with their log DIRECTORY already made — the wire
    // lib's `WireJsonl.Append` creates it, and F#'s `Log.defaultSink` creates it
    // once at first use. `PosixTrail` itself is the raw primitive underneath and
    // creates only the FILE, so the tests set the stage its real callers do.
    public TrailAppendTests() => Directory.CreateDirectory(Path.Combine(_dir, "logs"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    /// Both emitters, one file, at the same time — the production shape. Every
    /// line must survive: not merely "the file is big enough", but the exact
    /// multiset of lines written, so a lost line and a duplicated one can't
    /// cancel out in the count.
    [Fact]
    public async Task ConcurrentAppends_FromBothEmitters_LoseNoLines()
    {
        const int writers = 8, linesEach = 250;
        var expected = new List<string>();
        for (var w = 0; w < writers; w++)
            for (var i = 0; i < linesEach; i++)
                expected.Add($$"""{"w":{{w}},"i":{{i}},"pad":"the quick brown fox jumps over the lazy dog"}""");

        var start = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, writers).Select(w => Task.Run(async () =>
        {
            await start.Task;   // release them together — contention is the subject
            for (var i = 0; i < linesEach; i++)
            {
                var line = $$"""{"w":{{w}},"i":{{i}},"pad":"the quick brown fox jumps over the lazy dog"}""";
                // Even writers are the SHIM's emitter, odd ones the DAEMON's F#
                // mirror: the two implementations must interleave safely with
                // each other, not just with themselves.
                if (w % 2 == 0) WireJsonl.Append(Trail, line);
                else CaptainHook.Actors.PosixTrail.append(Trail, line + Environment.NewLine);
            }
        })).ToArray();

        start.SetResult();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30));

        var actual = File.ReadAllLines(Trail);
        Assert.Equal(expected.Count, actual.Length);
        Assert.Equal(expected.OrderBy(x => x, StringComparer.Ordinal),
                     actual.OrderBy(x => x, StringComparer.Ordinal));
    }

    /// No line may be torn or fused: with O_APPEND the kernel positions each
    /// write at EOF under the inode lock, so a reader can never see half of
    /// one line followed by another's tail.
    [Fact]
    public async Task ConcurrentAppends_ProduceOnlyWholeLines()
    {
        const int writers = 6, linesEach = 200;
        var start = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, writers).Select(w => Task.Run(async () =>
        {
            await start.Task;
            // Deliberately UNEQUAL lengths — an offset-clobbering writer leaves
            // a short line's tail inside a long one, which fixed-width lines
            // could hide.
            var pad = new string((char)('a' + w), 40 * (w + 1));
            for (var i = 0; i < linesEach; i++)
                if (w % 2 == 0) WireJsonl.Append(Trail, $"{w}:{i}:{pad}:end");
                else CaptainHook.Actors.PosixTrail.append(Trail, $"{w}:{i}:{pad}:end" + Environment.NewLine);
        })).ToArray();

        start.SetResult();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30));

        var lines = File.ReadAllLines(Trail);
        Assert.Equal(writers * linesEach, lines.Length);
        foreach (var l in lines)
        {
            var parts = l.Split(':');
            Assert.Equal(4, parts.Length);                        // never fused
            Assert.Equal("end", parts[3]);                        // never truncated
            var w = int.Parse(parts[0]);
            Assert.Equal(40 * (w + 1), parts[2].Length);          // never spliced from two writers
            Assert.Equal(new string((char)('a' + w), 40 * (w + 1)), parts[2]);
        }
    }

    /// The append path owns file creation too — the shim writes the very first
    /// line of a fresh install, and the F# mirror must agree (both create
    /// through the BCL, then open without the variadic O_CREAT form).
    [Fact]
    public void Append_CreatesTheFile_WhenAbsent()
    {
        Assert.False(File.Exists(Trail));

        Assert.True(CaptainHook.Actors.PosixTrail.append(Trail, "first\n"));
        WireJsonl.Append(Trail, "second");

        Assert.Equal(["first", "second"], File.ReadAllLines(Trail));
    }

    /// O_APPEND resolves EOF per write, so a truncation between appends is
    /// followed correctly — the offset is never a stale cached number. (This is
    /// exactly what the old pwrite path got wrong; the SSE tailer's
    /// truncation-reset handling assumes the writer behaves this way.)
    [Fact]
    public void Append_FollowsAnExternalTruncation()
    {
        WireJsonl.Append(Trail, "one");
        WireJsonl.Append(Trail, "two");

        File.WriteAllText(Trail, "");   // rotation, by the crudest possible means
        WireJsonl.Append(Trail, "three");

        Assert.Equal(["three"], File.ReadAllLines(Trail));
    }

    /// The sink contract both emitters share: a bad path is swallowed, never
    /// thrown — logging must not take a hook down.
    [Fact]
    public void Append_ToAnUnwritablePath_IsSwallowed()
    {
        var bad = Path.Combine(_dir, "logs");        // a DIRECTORY, not a file
        Directory.CreateDirectory(bad);

        WireJsonl.Append(bad, "nope");               // must not throw
        Assert.False(CaptainHook.Actors.PosixTrail.append(bad, "nope\n"));

        // Same contract for a missing parent directory: the primitive reports
        // failure, it does not create the tree (that is its caller's job) and
        // it does not throw into a dispatch.
        var orphan = Path.Combine(_dir, "no-such-dir", "trail.jsonl");
        Assert.False(CaptainHook.Actors.PosixTrail.append(orphan, "nope\n"));
        WireJsonl.Append(orphan, "made");            // this one DOES create the tree
        Assert.Equal(["made"], File.ReadAllLines(orphan));
    }
}
