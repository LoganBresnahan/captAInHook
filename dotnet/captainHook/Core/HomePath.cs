namespace CaptainHook.Core;

/// `~` in a path a user wrote (ADR-0010, 2026-10-19 amendment): a leading
/// `~/`, or the whole string `~`, becomes the user's home directory, the same
/// one the engine roots `~/.captainHook` at (SpecialFolder.UserProfile). That
/// is all. No `~user`, no `$VAR`, nothing mid-string: one handlers.json can
/// serve two machines whose homes differ, and config still never becomes a
/// template language (ADR-0003's rule, which ADR-0010 d4 inherits).
public static class HomePath
{
    /// `home` is the test seam; null reads the real one. A home that cannot
    /// be resolved (empty) leaves the path exactly as written.
    public static string Expand(string path, string? home = null)
    {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home)) return path;
        if (path == "~") return home;
        return path.StartsWith("~/", StringComparison.Ordinal)
            ? home.TrimEnd('/') + "/" + path[2..]
            : path;
    }
}
