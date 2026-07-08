using System.Text.Json;

namespace CaptainHook.Api;

// api-json-discovery (ADR-0007 decision 2): the discovery + credential file
// beside socket/lock/pid in the runtime dir. Programmatic clients (item 6's
// GUI, an eventual CLI verb) learn the port and the bearer token from here —
// it is the SOLE credential source, so the auth gate (auth-token-origin) reads
// the same token this writes. Filesystem permissions are the trust root: 0600,
// exactly like the socket and the lock, so "who can read the token" is "who can
// read the daemon's private runtime dir" (ADR-0007 decision 6).
//
// The file exists iff the API holds the port: ApiHost writes it under the same
// lock that flips `_listening` true on bind, and deletes it when that flips
// false on Stop — so a client never reads a port+token for a listener that has
// already handed the singleton port to a successor. A crash leaks it like the
// pidfile; `doctor` reaps it once the lock proves the owner dead.
public sealed record ApiDiscovery(int Port, string Token, int Pid, string Version)
{
    // Web defaults: camelCase, matching every other JSON the API emits (ApiJson).
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// Write `d` to `path` as 0600 JSON, 0600 AT BIRTH — never WriteAllText-
    /// then-chmod, whose window leaves the secret token at the umask default
    /// (0644) long enough for a co-located user to race open() and steal it
    /// (the runtime dir is NOT reliably 0700 — CreateDirectory can't retighten a
    /// pre-existing dir, and the log layer may have made it 0755 first). Delete
    /// any stale file so UnixCreateMode — which applies only on CREATE — governs
    /// the new one; this is exactly the co-located lock file's create-time-0600
    /// pattern (DaemonRendezvous). Throws on I/O failure; the caller decides
    /// whether that is fatal (ApiHost: log-and-degrade, never un-bind).
    public static void Write(string path, ApiDiscovery d)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(d, Options);
        File.Delete(path);   // so the create (not a truncate) applies UnixCreateMode
        using var fs = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,   // 0600, no window
        });
        fs.Write(bytes);
    }

    /// Read a discovery file, or null when it is absent/unreadable/malformed —
    /// absence is an answer ("no live API of this identity"), never an error,
    /// exactly as the shim treats a missing socket.
    public static ApiDiscovery? TryRead(string path)
    {
        try { return JsonSerializer.Deserialize<ApiDiscovery>(File.ReadAllText(path), Options); }
        catch { return null; }
    }
}
