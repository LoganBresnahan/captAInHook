using System.Diagnostics;
using CaptainHook.Wire;

namespace CaptainHook.Api;

// ui-cli-verb (ADR-0008 decision 3): `captainHook ui` is the token handoff —
// the ONE sanctioned path from the 0600 api.json to a browser session. Read
// the discovery file (the sole credential source, ADR-0007 d6), build
// http://127.0.0.1:<port>/ui#t=<token>, and hand it to the OS opener. The
// FRAGMENT carries the token: fragments are sent to no server and written to
// no access log, and the shell's bootstrap scrubs it from the URL bar on
// load. Collapsed-mode, no daemon of its own — the API never spawns a daemon
// (ADR-0007 d7), so an absent api.json is a clear "not running", never a spawn.
public static class UiVerb
{
    /// The exact URL shape the shell's bootstrap parses (web/src/auth.ts:
    /// /^#t=([0-9a-f]+)$/) — fragment, never a query parameter.
    internal static string BuildUrl(int port, string token) =>
        $"http://127.0.0.1:{port}/ui#t={token}";

    /// `apiJsonPath`/`launcher` are test seams; production passes neither.
    /// Exit 0 = browser launched; 1 = no live API or no launcher. The token is
    /// never written to stdout/stderr — only the fragment-free address is.
    public static async Task<int> RunAsync(
        TextWriter stdout, TextWriter stderr,
        string? apiJsonPath = null, Func<string, bool>? launcher = null)
    {
        string path;
        if (apiJsonPath is not null) path = apiJsonPath;
        else
        {
            try { path = RendezvousPaths.Resolve().ApiJsonPath; }
            catch (Exception ex)
            {
                await stderr.WriteLineAsync($"captAInHook: rendezvous unavailable: {ex.Message}");
                return 1;
            }
        }

        if (ApiDiscovery.TryRead(path) is not { } api)
        {
            await stderr.WriteLineAsync(
                "captAInHook: no live management API — the daemon isn't running "
                + "(fire any hook to warm one, then retry), or the API is disabled "
                + "(CAPTAINHOOK_API_PORT=0).");
            return 1;
        }

        var url = BuildUrl(api.Port, api.Token);
        // Announce the destination WITHOUT the credential: the fragment must
        // not land in terminal scrollback any more than in a log.
        await stdout.WriteLineAsync(
            $"captAInHook: opening http://127.0.0.1:{api.Port}/ui in the default browser");
        if (!(launcher ?? DefaultLauncher)(url))
        {
            await stderr.WriteLineAsync(
                $"captAInHook: could not launch a browser — open http://127.0.0.1:{api.Port}/ui "
                + $"yourself and append '#t=' + the token from {path}");
            return 1;
        }
        return 0;
    }

    // The OS opener. The URL rides argv, which is briefly visible in
    // /proc/<pid>/cmdline to OTHER local users while the opener runs — a
    // narrower exposure than a query param (no logs, no history, no server)
    // but real on a hostile multi-user box; accepted with decision 3's shape
    // (browsers take URLs no other way) and noted in scratch as a hardening
    // candidate (a one-time-redirect handoff would close it).
    private static bool DefaultLauncher(string url)
    {
        try
        {
            var (cmd, args) =
                OperatingSystem.IsWindows() ? ("cmd", $"/c start \"\" \"{url}\"") :
                OperatingSystem.IsMacOS() ? ("open", url) :
                ("xdg-open", url);
            return Process.Start(new ProcessStartInfo(cmd, args)
            {
                RedirectStandardOutput = true,   // openers can chatter; not our stdout's business
                RedirectStandardError = true,
            }) is not null;
        }
        catch
        {
            return false;
        }
    }
}
