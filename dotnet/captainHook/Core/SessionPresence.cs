namespace CaptainHook.Core;

/// Which sessions this daemon has dispatched for lately, and how long ago —
/// the second half of the mail view's presence inference (ADR-0016 d14; the
/// first is who holds a cursor file). ServeStats' sibling: a purpose-built
/// observability structure the serve loop writes and the API reads, never the
/// dispatch path's own state (ADR-0007 d1).
///
/// This is deliberately NOT a session registry, and d14 says so: there is no
/// registration, no heartbeat, and no "gone" event — a session appears here
/// because a hook of its arrived, and its entry simply gets older. The canvas
/// FADES a stale session; it never claims liveness. The daemon's own lifetime
/// bounds the memory: a restart forgets everyone, and the cursor files (which
/// survive) are what keep a quiet recipient visible at all.
///
/// Ages are computed from the injected MONOTONIC clock (house invariant 2) and
/// reported as a DURATION rather than a timestamp, so nothing downstream —
/// least of all a browser with its own clock — has to reconcile two wall
/// clocks to render "last seen 4s ago".
public sealed class SessionPresence(Func<long> clock, int capacity = SessionPresence.DefaultCapacity)
{
    /// Enough for any plausible fleet of concurrent sessions on one machine,
    /// small enough that the whole structure is a rounding error. Past it the
    /// OLDEST entry is evicted: presence is about who is here now, and the
    /// alternative (unbounded growth keyed by a value hooks control) is a leak
    /// with a sender's name on it.
    public const int DefaultCapacity = 64;

    private readonly object _gate = new();
    private readonly Dictionary<string, long> _lastSeen = new(StringComparer.Ordinal);

    /// Stamp a session as seen NOW. Null/blank ids are ignored — a harness
    /// that names no session has no presence to report, and "" is not a
    /// session (the same normalization the cursor path applies).
    public void Seen(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        var now = clock();
        lock (_gate)
        {
            _lastSeen[sessionId] = now;
            if (_lastSeen.Count <= capacity) return;
            // One eviction per insert keeps the scan O(capacity) and the
            // structure at its bound; ties break arbitrarily and harmlessly.
            var oldest = sessionId;
            var oldestTick = now;
            foreach (var kv in _lastSeen)
                if (kv.Value < oldestTick) { oldest = kv.Key; oldestTick = kv.Value; }
            _lastSeen.Remove(oldest);
        }
    }

    /// Every remembered session with its age in milliseconds, freshest first.
    /// A negative age is impossible by construction (one monotonic source).
    public IReadOnlyList<(string Session, long AgeMs)> Recent()
    {
        var now = clock();
        lock (_gate)
            return _lastSeen
                .Select(kv => (Session: kv.Key, AgeMs: Math.Max(0, now - kv.Value)))
                .OrderBy(x => x.AgeMs)
                .ThenBy(x => x.Session, StringComparer.Ordinal)
                .ToList();
    }
}
