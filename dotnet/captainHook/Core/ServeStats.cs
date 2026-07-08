namespace CaptainHook.Core;

/// Live serve counters for the management API's GET /status (ADR-0007 decision
/// 3): in-flight connections and the lifetime served count. The serve loop
/// writes (OnConnect/OnDone under Interlocked); the API reads (Active/Served
/// under Volatile) — a purpose-built observability counter, never the dispatch
/// path's own mutable state (ADR-0007 d1). `Active` also feeds the drain and
/// idle watchdog, which previously kept it as a bare local.
public sealed class ServeStats
{
    private int _active;
    private long _served;

    /// A connection was accepted: one more in flight, one more served ever.
    public void OnConnect()
    {
        Interlocked.Increment(ref _active);
        Interlocked.Increment(ref _served);
    }

    /// A connection finished (its dispatch answered or failed).
    public void OnDone() => Interlocked.Decrement(ref _active);

    public int Active => Volatile.Read(ref _active);
    public long Served => Volatile.Read(ref _served);
}
