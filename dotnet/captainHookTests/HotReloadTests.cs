using CaptainHook.Core;

namespace CaptainHook.Tests;

// harness-hot-reload (ADR-0004 decision 1, carrying ADR-0003's contract into
// the daemon): edit a spec, effective next hook — for EVERY kind of edit. The
// composite per-file stamp exists precisely because dir-mtime misses in-place
// overwrites; that case gets its own test. File mtimes need to differ between
// writes — File.SetLastWriteTimeUtc pins them explicitly so no test ever
// sleeps to "let the clock move".

public class HotReloadTests
{
    private static void WriteSpec(string dir, string file, string name, string adapter = "generic-json",
        DateTime? mtime = null)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, file);
        File.WriteAllText(path, HarnessTestUtil.MinimalSpecJson(name, adapter));
        if (mtime is not null) File.SetLastWriteTimeUtc(path, mtime.Value);
    }

    [Fact]
    public void AddedSpecFile_IsKnown_OnTheNextLook()
    {
        using var dir = new TempHarnessDir();
        var reg = new ReloadingHarnessRegistry(dir.Path);
        Assert.DoesNotContain("added-h", reg.Current.Known);

        WriteSpec(dir.Path, "added.json", "added-h");
        Assert.Contains("added-h", reg.Current.Known);
    }

    [Fact]
    public void InPlaceOverwrite_IsPickedUp_TheCaseDirMtimeMisses()
    {
        // `cat > spec.json` (truncate + write, same inode, same directory
        // entry) does NOT bump the parent dir's mtime on Linux — the whole
        // reason the stamp is per-file. Same byte length on both writes so
        // only the mtime can distinguish them; pinned mtimes make it certain.
        using var dir = new TempHarnessDir();
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        WriteSpec(dir.Path, "mine.json", "aaaa-h", mtime: t0);
        var reg = new ReloadingHarnessRegistry(dir.Path);
        Assert.Contains("aaaa-h", reg.Current.Known);

        // In-place: overwrite the SAME file with a same-length different name.
        WriteSpec(dir.Path, "mine.json", "bbbb-h", mtime: t0.AddSeconds(1));

        Assert.Contains("bbbb-h", reg.Current.Known);
        Assert.DoesNotContain("aaaa-h", reg.Current.Known);
    }

    [Fact]
    public void DeletedSpecFile_RevertsToEmbeddedDefaults()
    {
        using var dir = new TempHarnessDir();
        WriteSpec(dir.Path, "extra.json", "extra-h");
        var reg = new ReloadingHarnessRegistry(dir.Path);
        Assert.Contains("extra-h", reg.Current.Known);

        File.Delete(Path.Combine(dir.Path, "extra.json"));
        Assert.DoesNotContain("extra-h", reg.Current.Known);
        Assert.Contains("claude-code", reg.Current.Known);   // embedded layer intact
    }

    [Fact]
    public void UnchangedDirectory_ReturnsTheSameRegistryInstance_NoRebuildChurn()
    {
        using var dir = new TempHarnessDir();
        WriteSpec(dir.Path, "steady.json", "steady-h");
        var reg = new ReloadingHarnessRegistry(dir.Path);

        var first = reg.Current;
        var second = reg.Current;
        Assert.Same(first, second);   // stamp equal -> zero reload work
    }

    [Fact]
    public void InvalidEdit_KeepsServing_ThenTheFixLands()
    {
        // Mid-edit garbage must never take the live hook down: the invalid
        // load WARNS and the registry keeps the embedded default; the
        // corrected write is effective on the next look. (Same-name override
        // reverting to the default mid-edit is the documented cost.)
        using var dir = new TempHarnessDir();
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        WriteSpec(dir.Path, "hook.json", "custom-h", mtime: t0);
        var reg = new ReloadingHarnessRegistry(dir.Path);
        Assert.Contains("custom-h", reg.Current.Known);

        var path = Path.Combine(dir.Path, "hook.json");
        File.WriteAllText(path, "{ not json");
        File.SetLastWriteTimeUtc(path, t0.AddSeconds(1));
        var mid = reg.Current;                       // reloads: invalid file skipped
        Assert.DoesNotContain("custom-h", mid.Known);
        Assert.Contains("claude-code", mid.Known);   // still serving

        WriteSpec(dir.Path, "hook.json", "custom-h", mtime: t0.AddSeconds(2));
        Assert.Contains("custom-h", reg.Current.Known);
    }

    [Fact]
    public void DirectoryCreatedAfterStartup_IsNoticed()
    {
        // The daemon may outlive the first-ever `mkdir ~/.captainHook/harnesses`.
        var late = Path.Combine("/tmp", "chk-late-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var reg = new ReloadingHarnessRegistry(late);   // dir absent at construction
            Assert.DoesNotContain("late-h", reg.Current.Known);

            WriteSpec(late, "late.json", "late-h");
            Assert.Contains("late-h", reg.Current.Known);
        }
        finally
        {
            try { Directory.Delete(late, recursive: true); } catch { /* best-effort */ }
        }
    }
}
