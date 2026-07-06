using CaptainHook.Wire;

namespace CaptainHook.Shim;

// The wire-stamp skew guard (ADR-0004 decision 7 amendment). Two artifacts
// can skew where one binary could not: a partial deploy leaves this shim's
// statically linked wire code ≠ the directory's captainHookWire.dll — the
// shim would compute the OLD identity and speak its NEW framing at that
// socket. The stamp needs no build machinery: Native AOT preserves
// Module.ModuleVersionId (verified 2026-07-06 — the AOT image reports the
// exact MVID of the wire assembly it linked), so the shim compares what it
// IS against what the directory ADVERTISES. On mismatch the socket is never
// touched — delegate to the co-located engine, which is self-consistent with
// its own DLLs. Skew fails SAFE (collapse + a loud trail line), never WRONG
// (a mis-framed dispatch against a live daemon).

public static class SkewGuard
{
    public sealed record Verdict(bool Ok, string Detail);

    /// Compare the wire MVID compiled into THIS image against the
    /// captainHookWire.dll in `deployDir`. A missing or unreadable DLL is a
    /// failed check — there is no valid co-located deploy to rendezvous for.
    public static Verdict Check(string deployDir)
    {
        var embedded = typeof(Frame).Module.ModuleVersionId;
        var dllPath = Path.Combine(deployDir, "captainHookWire.dll");
        var onDisk = ContentIdentity.TryReadMvid(dllPath);
        if (onDisk is null)
            return new(false, $"no readable captainHookWire.dll at '{dllPath}' — partial or missing deploy");
        if (onDisk.Value != embedded)
            return new(false,
                $"wire skew: shim linked {embedded:N}, directory advertises {onDisk.Value:N} — redeploy both artifacts together");
        return new(true, "wire contract matches");
    }
}
