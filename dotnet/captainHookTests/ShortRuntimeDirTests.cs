using System.Text;
using System.Text.Json;
using CaptainHook.Core;
using CaptainHook.Shim;
using CaptainHook.Wire;
using static CaptainHook.Tests.TestUtil;

namespace CaptainHook.Tests;

// Roadmap item 26 / ADR-0004 d3 (amended 2026-10-19): a preferred runtime dir
// too long for a socket falls back to the short per-uid dir, which is trusted
// only once proven ours. The choice and the proof live in the wire lib
// (RendezvousPaths, ShortRuntimeDir), so these tests pin both halves the shim
// and the engine share: the boundary, every refusal, and — end to end, under a
// deliberately long XDG_RUNTIME_DIR — a daemon that binds there, a shim that
// finds it warm, and doctor reading the same dir.

public class ShortRuntimeDirTests : IDisposable
{
    private const UnixFileMode Mode0700 =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private const UnixFileMode Mode0755 = Mode0700 |
        UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    // A private base stands in for /tmp, so no test but the end-to-end one
    // touches the real /tmp/captainhook-<uid>.
    private readonly string _base = Path.Combine("/tmp", "chk-short-" + Guid.NewGuid().ToString("N")[..8]);
    private static uint Uid => ShortRuntimeDir.Uid;

    public ShortRuntimeDirTests() => Directory.CreateDirectory(_base);

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch { /* best-effort */ }
    }

    private string ShortDir => ShortRuntimeDir.PathFor(Uid, _base);

    /// A preferred dir whose worst-case socket path is exactly `bytes` long
    /// (`<dir>/captaind-<12 hex>.sock`: the dir, a slash, 26 bytes).
    private static string PreferredWithSocketOf(int bytes) =>
        "/tmp/" + new string('p', bytes - "/tmp/".Length - 1 - "captaind-000000000000.sock".Length);

    // ---- the choice ---------------------------------------------------------

    [Fact]
    public void Choose_SocketThatFits_KeepsThePreferredDir()
    {
        var preferred = PreferredWithSocketOf(RendezvousPaths.MaxSocketPathBytes);
        var c = RendezvousPaths.ChooseRuntimeDir(preferred, Uid, _base);
        Assert.Equal(preferred, c.Dir);
        Assert.Null(c.ShortDirReason);
        Assert.Null(c.Refusal);
        Assert.Equal(preferred, c.Describe());
    }

    [Fact]
    public void Choose_OneByteOverTheCap_FallsBackToTheShortDir_AndSaysWhy()
    {
        var preferred = PreferredWithSocketOf(RendezvousPaths.MaxSocketPathBytes + 1);
        var c = RendezvousPaths.ChooseRuntimeDir(preferred, Uid, _base);
        Assert.Equal(Path.Combine(_base, $"captainhook-{Uid}"), c.Dir);
        Assert.Contains(preferred, c.ShortDirReason);
        Assert.Contains($"{RendezvousPaths.MaxSocketPathBytes + 1} bytes", c.ShortDirReason);
        Assert.Null(c.Refusal);   // absent: nothing to distrust yet
        Assert.Contains("short dir", c.Describe());
    }

    [Fact]
    public void Choose_IsIndependentOfTheAskingVersion()
    {
        // Every caller — shim, daemon, doctor — must land in the same dir;
        // the choice measures the worst-case name, never the caller's version.
        var preferred = PreferredWithSocketOf(RendezvousPaths.MaxSocketPathBytes);
        Assert.Equal(RendezvousPaths.ChooseRuntimeDir(preferred, Uid, _base),
                     RendezvousPaths.ChooseRuntimeDir(preferred, Uid, _base));
    }

    [Fact]
    public void Choose_ARefusedShortDir_CarriesTheRefusal()
    {
        Directory.CreateDirectory(ShortDir);
        File.SetUnixFileMode(ShortDir, Mode0755);
        var c = RendezvousPaths.ChooseRuntimeDir(PreferredWithSocketOf(200), Uid, _base);
        Assert.NotNull(c.Refusal);
        Assert.Contains("REFUSED", c.Describe());
    }

    // ---- the proof ----------------------------------------------------------

    [Fact]
    public void Check_Absent_IsNotARefusal() =>
        Assert.Null(ShortRuntimeDir.Check(ShortDir, Uid));

    [Fact]
    public void Check_OurOwn0700Dir_IsTrusted()
    {
        Directory.CreateDirectory(ShortDir, Mode0700);
        File.SetUnixFileMode(ShortDir, Mode0700);
        Assert.Null(ShortRuntimeDir.Check(ShortDir, Uid));
    }

    [Fact]
    public void Check_WrongOwner_IsRefused()
    {
        // No second account in CI: ask for an owner it does not have. Paired
        // with the trusted case above, this also proves the lstat read of the
        // owner is OUR uid — a misread offset would fail one or the other.
        Directory.CreateDirectory(ShortDir, Mode0700);
        File.SetUnixFileMode(ShortDir, Mode0700);
        var refusal = ShortRuntimeDir.Check(ShortDir, Uid + 1);
        Assert.Contains($"owned by uid {Uid}", refusal);
    }

    [Theory]
    [InlineData(UnixFileMode.GroupExecute)]
    [InlineData(UnixFileMode.OtherExecute)]
    [InlineData(UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)]
    public void Check_GroupOrOtherBits_AreRefused(UnixFileMode extra)
    {
        Directory.CreateDirectory(ShortDir);
        File.SetUnixFileMode(ShortDir, Mode0700 | extra);
        Assert.Contains("group and other bits must be clear", ShortRuntimeDir.Check(ShortDir, Uid));
    }

    [Fact]
    public void Check_OwnerWithoutRwx_IsRefused()
    {
        Directory.CreateDirectory(ShortDir);
        File.SetUnixFileMode(ShortDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);   // 0500
        Assert.Contains("owner needs rwx", ShortRuntimeDir.Check(ShortDir, Uid));
    }

    [Fact]
    public void Check_ASymlink_IsRefused_EvenToOurOwn0700Dir()
    {
        var target = Path.Combine(_base, "real");
        Directory.CreateDirectory(target, Mode0700);
        File.SetUnixFileMode(target, Mode0700);
        Directory.CreateSymbolicLink(ShortDir, target);
        Assert.Contains("symlink", ShortRuntimeDir.Check(ShortDir, Uid));
    }

    [Fact]
    public void Check_ARegularFile_IsRefused()
    {
        File.WriteAllText(ShortDir, "");
        Assert.Contains("not a directory", ShortRuntimeDir.Check(ShortDir, Uid));
    }

    [Fact]
    public void Ensure_Absent_CreatesIt0700()
    {
        Assert.Null(ShortRuntimeDir.Ensure(ShortDir, Uid));
        Assert.Equal(Mode0700, File.GetUnixFileMode(ShortDir));
    }

    [Fact]
    public void Ensure_ALooseDir_IsRefused_NotRepaired()
    {
        Directory.CreateDirectory(ShortDir);
        File.SetUnixFileMode(ShortDir, Mode0755);
        Assert.NotNull(ShortRuntimeDir.Ensure(ShortDir, Uid));
        Assert.Equal(Mode0755, File.GetUnixFileMode(ShortDir));   // never chmodded
    }

    [Fact]
    public void EnsureRuntimeDir_OnTheShortDir_ThrowsOnRefusal_SoNoDaemonBindsThere()
    {
        Directory.CreateDirectory(ShortDir);
        File.SetUnixFileMode(ShortDir, Mode0755);
        var paths = RendezvousPaths.Resolve(ShortDir, "v") with { ShortDirReason = "test" };
        var ex = Assert.Throws<InvalidOperationException>(paths.EnsureRuntimeDir);
        Assert.Contains("refused", ex.Message);
        Assert.Throws<InvalidOperationException>(() => DaemonRendezvous.TryAcquire(paths));
        Assert.False(File.Exists(paths.LockPath), "no lock file in a refused dir");
    }

    // ---- end to end: a long XDG_RUNTIME_DIR still gets a daemon -------------

    [Fact]
    public async Task LongXdgRuntimeDir_DaemonBindsInTheShortDir_ShimAnswersWarm_DoctorReadsIt()
    {
        using var echo = new EchoOn();   // the echo answer is this test's round-trip proof (opt-in since 0.1.4)
        var original = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        var longDir = "/tmp/chk-long-" + new string('x', 110);
        var realShort = ShortRuntimeDir.PathFor(Uid);
        // The real /tmp/captainhook-<uid> is the point here. A machine whose
        // copy is not ours fails loudly — it is not this test's to repair.
        if (ShortRuntimeDir.Check(realShort, Uid) is { } refused)
            Assert.Fail($"precondition: {realShort} must be absent or trusted: {refused}");
        using var stop = new CancellationTokenSource();
        Task<int>? daemon = null;
        RendezvousPaths? paths = null;
        try
        {
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", longDir);

            // Every caller computes the same answer from the environment.
            paths = RendezvousPaths.Resolve();
            Assert.Equal(realShort, paths.RuntimeDir);
            Assert.NotNull(paths.ShortDirReason);
            Assert.Contains(longDir, paths.ShortDirReason);

            // The daemon resolves on its own (no override) — and binds there.
            daemon = Task.Run(() => DaemonHost.RunAsync(
                harnessDir: "/tmp/chk-none-" + Guid.NewGuid().ToString("N")[..8], ct: stop.Token));
            var sock = paths.SocketPath;
            await PollUntilAsync(async () =>
                await ShimClient.TryForwardAsync(sock, new HookRequest(
                    "probe000", "session-start", "claude-code", "{}"u8.ToArray())) is ForwardOutcome.Answered,
                TimeSpan.FromSeconds(15), "daemon listens in the short dir");
            Assert.Equal(Mode0700, File.GetUnixFileMode(realShort));

            // The shim resolves on its own too, and is answered warm: an
            // effect on stdout, and no delegation (which would need an engine).
            using var stdout = new MemoryStream();
            var stderr = new StringWriter();
            var exit = await ShimMain.RunAsync(["hook", "user-prompt-submit"],
                new MemoryStream(Encoding.UTF8.GetBytes("""{"prompt":"short dir"}""")), stdout, stderr);
            Assert.Equal(0, exit);
            using var doc = JsonDocument.Parse(stdout.ToArray());
            Assert.Equal("UserPromptSubmit",
                doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("hookEventName").GetString());
            Assert.DoesNotContain("dispatching via the engine", stderr.ToString());

            // Doctor names the short dir, and finds this daemon's record in it.
            // (isOurProcess:false — the daemon is THIS test process, which
            // doctor must never signal; the held lock leaves the record alone.)
            var describe = RendezvousPaths.ChooseRuntimeDir().Describe();
            Assert.StartsWith(realShort, describe);
            Assert.Contains("short dir", describe);
            var verdicts = await Doctor.RunAsync(grace: TimeSpan.FromSeconds(1), isOurProcess: (_, _) => false);
            Assert.Contains(verdicts, v => v.Version == paths.Version && v.Action == "lock-held");
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", original);
            stop.Cancel();
            if (daemon is not null) Assert.Equal(0, await daemon.WaitAsync(TimeSpan.FromSeconds(15)));
            // The daemon removed its socket and pidfile; the lock file is never
            // unlinked by design — a test leaves no trace, so it goes here.
            if (paths is not null) try { File.Delete(paths.LockPath); } catch { /* best-effort */ }
        }
    }
}
