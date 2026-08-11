using System.Diagnostics;
using CaptainHook.Handlers;

namespace CaptainHook.Tests;

// bosun-resolution-seam (ADR-0014 d1/d2): the spawn half of the kill
// discipline stops renting `setsid(1)` from the host's package set and takes
// the wrapper we ship. Three rungs, loud at every step — co-located bosun →
// setsid from PATH → no prefix at all — and an argv contract that differs
// between them (bosun requires an explicit `--` before the command; setsid
// takes it bare). The rung the live process resolved is NOT the rung a deploy
// runs, so both the resolution and the argv shape are pinned directly rather
// than through whichever one this machine happens to have.

public class SpawnPrefixTests
{
    /// A stand-in binary: content is irrelevant to resolution — only the
    /// executable bit is. `exec` false writes a present-but-unusable file,
    /// which must fall THROUGH the rung rather than fail every spawn.
    private static string Plant(string dir, string name, bool exec = true, string? body = null)
    {
        Directory.CreateDirectory(dir);
        var p = Path.Combine(dir, name);
        File.WriteAllText(p, body ?? "#!/bin/sh\nexit 0\n");
        File.SetUnixFileMode(p, exec
            ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            : UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return p;
    }

    private sealed class TempTree : IDisposable
    {
        public string Root { get; } = Path.Combine("/tmp", "chk-spawn-" + Guid.NewGuid().ToString("N")[..8]);
        public string Deploy => Path.Combine(Root, "bin");
        public string PathDir => Path.Combine(Root, "usr-bin");
        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* best-effort */ }
        }
    }

    // ---- rung resolution ----------------------------------------------------

    [Fact]
    public void Resolve_ColocatedBosunWins_EvenWhenSetsidIsAvailable()
    {
        // Rung order is the decision: on a live deploy the artifact we ship
        // must beat whatever the host happens to carry, or the macOS fix is
        // silently undone by a Linux box that has both.
        using var t = new TempTree();
        var bosun = Plant(t.Deploy, "bosun");
        Plant(t.PathDir, "setsid");

        var p = ProcessGroup.Resolve(t.Deploy, t.PathDir);

        Assert.Equal("bosun", p.Name);
        Assert.Equal(bosun, p.Exe);
        Assert.True(p.Pgroup);
    }

    [Fact]
    public void Resolve_NonExecutableBosun_FallsThroughToSetsid()
    {
        // Present-but-unusable is the trap: naming it FileName would fail
        // every spawn with a Win32Exception blaming the payload.
        using var t = new TempTree();
        Plant(t.Deploy, "bosun", exec: false);
        var setsid = Plant(t.PathDir, "setsid");

        var p = ProcessGroup.Resolve(t.Deploy, t.PathDir);

        Assert.Equal("setsid", p.Name);
        Assert.Equal(setsid, p.Exe);
    }

    [Fact]
    public void Resolve_NoBosun_TakesSetsidFromPath_ScanningInOrder()
    {
        // The dev-tree and test rung, kept deliberately (ADR-0014 d2).
        using var t = new TempTree();
        Directory.CreateDirectory(t.Deploy);
        var second = Path.Combine(t.Root, "second");
        var setsid = Plant(second, "setsid");

        var p = ProcessGroup.Resolve(t.Deploy, $"{t.PathDir}:{second}");

        Assert.Equal("setsid", p.Name);
        Assert.Equal(setsid, p.Exe);
    }

    [Fact]
    public void Resolve_NeitherRung_Degrades_WithoutThrowing()
    {
        // Stock macOS today, and any minimal container: no wrapper anywhere.
        // The degrade must be a value, not an exception — and must be legible
        // in the trail (`spawner=none` beside the existing `pgroup=false`).
        using var t = new TempTree();

        var p = ProcessGroup.Resolve(t.Deploy, t.PathDir);

        Assert.Equal("none", p.Name);
        Assert.Null(p.Exe);
        Assert.False(p.Pgroup);
        Assert.Empty(p.PreArgs);
    }

    [Fact]
    public void Resolve_AbsentBaseDirOrPath_IsAnswered_NotCrashed()
    {
        // Both inputs are environment-supplied and may be null (a single-file
        // host with no BaseDirectory, an empty PATH); neither may throw.
        Assert.Equal("none", ProcessGroup.Resolve(null, null).Name);
        Assert.Equal("none", ProcessGroup.Resolve("", "").Name);
    }

    [Fact]
    public void LivePrefix_IsSelfConsistent()
    {
        // The process-wide snapshot every kill site keys on: Pgroup ⟺ a
        // wrapper was resolved, and a named rung always has an executable.
        var p = ProcessGroup.Prefix;

        Assert.Equal(p.Exe is not null, p.Pgroup);
        Assert.Contains(p.Name, new[] { "bosun", "setsid", "none" });
        if (p.Exe is not null) Assert.True(File.Exists(p.Exe), $"resolved {p.Name} at {p.Exe} must exist");
    }

    // ---- argv contract per rung ---------------------------------------------

    private static List<string> Argv(ProcessGroup.SpawnPrefix prefix)
    {
        var psi = ExecHandler.BuildPsi("/bin/echo", ["a", "b"], env: null, passEnv: null, prefix: prefix);
        return [psi.FileName, .. psi.ArgumentList];
    }

    [Fact]
    public void BuildPsi_BosunRung_PutsTheMandatoryTerminatorBeforeTheCommand()
    {
        // bosun parses its own flags until `--`; without the terminator it
        // exits 125 and NOTHING runs. This is the one shape difference
        // between the rungs, so it is pinned literally.
        var argv = Argv(new ProcessGroup.SpawnPrefix("bosun", "/deploy/bosun", ["--"]));

        Assert.Equal(["/deploy/bosun", "--", "/bin/echo", "a", "b"], argv);
    }

    [Fact]
    public void BuildPsi_SetsidRung_PassesTheCommandBare()
    {
        var argv = Argv(new ProcessGroup.SpawnPrefix("setsid", "/usr/bin/setsid", []));

        Assert.Equal(["/usr/bin/setsid", "/bin/echo", "a", "b"], argv);
    }

    [Fact]
    public void BuildPsi_DegradedRung_ExecsTheCommandItself()
    {
        var argv = Argv(ProcessGroup.SpawnPrefix.None);

        Assert.Equal(["/bin/echo", "a", "b"], argv);
    }

    [Fact]
    public void BuildPsi_DefaultsToTheLiveRung()
    {
        // Production never passes a prefix — the omitted-argument path must be
        // the resolved one, or the seam is tested and unused.
        var psi = ExecHandler.BuildPsi("/bin/echo", null, null, null);

        Assert.Equal(ProcessGroup.Prefix.Exe ?? "/bin/echo", psi.FileName);
    }

    // ---- the contract against a real exec -----------------------------------

    [Fact]
    public async Task BosunShapedSpawn_ExecsInPlace_AndTheChildLeadsItsOwnGroup()
    {
        // The rung-1 wiring end-to-end, without requiring the real artifact in
        // a test tree: a wrapper that ENFORCES bosun's `--` contract (exit 125
        // otherwise) and then does what bosun does — become a session leader
        // and exec in place. A dropped terminator or a mis-ordered argv fails
        // here, not on a live deploy.
        var setsid = ProcessGroup.Resolve(null, Environment.GetEnvironmentVariable("PATH"));
        if (setsid.Name != "setsid") return;   // xunit 2.x: no dynamic skip — needs a real in-place wrapper

        using var t = new TempTree();
        var fake = Plant(t.Deploy, "bosun", body: $$"""
            #!/bin/sh
            # bosun's argv contract: flags, then '--', then the command.
            [ "$1" = "--" ] || { echo "bosun: missing terminator" >&2; exit 125; }
            shift
            exec {{setsid.Exe}} "$@"
            """);

        var psi = ExecHandler.BuildPsi(
            "/bin/sh", ["-c", """printf '%s %s\n' "$$" "$(ps -o pgid= -p $$ | tr -d ' ')" """],
            env: null, passEnv: null,
            prefix: new ProcessGroup.SpawnPrefix("bosun", fake, ["--"]));

        using var proc = Process.Start(psi)!;
        var line = (await proc.StandardOutput.ReadToEndAsync()).Trim();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();

        Assert.Equal(0, proc.ExitCode);          // 125 ⇒ the terminator never arrived
        Assert.Equal("", stderr.Trim());
        var parts = line.Split(' ');
        Assert.Equal(2, parts.Length);
        Assert.Equal(parts[0], parts[1]);        // pid == pgid: exec'd in place, own group
    }
}
