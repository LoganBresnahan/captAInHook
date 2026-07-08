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

    /// Write `d` to `path` as 0600 JSON. Create-then-chmod has a sub-millisecond
    /// window at the umask default before the tighten; acceptable because the
    /// runtime dir itself is already 0700 (DaemonRendezvous), so no other user
    /// can open the file even during that window — defense in depth, not the
    /// only wall. Throws on I/O failure; the caller decides whether that is
    /// fatal (ApiHost: log-and-degrade, never un-bind).
    public static void Write(string path, ApiDiscovery d)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(d, Options));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);   // 0600
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
