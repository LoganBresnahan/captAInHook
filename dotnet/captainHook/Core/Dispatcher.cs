using System.Diagnostics;
using System.Threading.Channels;
using CaptainHook.Actors;

namespace CaptainHook.Core;

/// A CHILD SPEC, not a live handler: everything the dispatcher needs to
/// (re)create a handler — its name, fail mode, a factory, and (ADR-0010
/// decision 9) an optional per-handler budget that may EXCEED the dispatcher
/// default. This is the OTP child-spec idea ported to the registry: "restart"
/// means "run the factory again", so a restarted worker gets a genuinely
/// fresh handler.
///
/// `ReloadFingerprint` (ADR-0010 phase 7) marks a spec as RELOADABLE and
/// carries its config identity: null ⇒ a coded/static handler frozen at
/// construction (never touched by a handlers.json reload); non-null ⇒ an exec
/// handler whose worker the reconcile diffs. Two reloads whose entry produced
/// the same fingerprint are "unchanged" — the warm resident child is left
/// running (no churn); a differing fingerprint replaces it. The event+name is
/// the worker's IDENTITY (its id); the fingerprint is its config VERSION.
internal sealed record HandlerSpec(string Name, FailMode OnFailure, Func<IHandler> Factory,
                                   TimeSpan? Budget = null, string? ReloadFingerprint = null);

/// Eager side-work a handler starts the moment it is ADMITTED to the teardown
/// seam — never in its constructor (ADR-0010 resident slice). Two orphan
/// holes close by that ordering: a post-drain supervised restart constructs
/// an instance the seam will never track (Start is simply not called), and a
/// restart's fresh child can sequence BEHIND the evicted predecessor's
/// TERM→grace→KILL via `predecessor` (exclusive resources — sockets, locks —
/// release before the successor spawns). Implementations MUST NOT throw
/// (the first factory run happens inside Supervisor.Spawn, where a throw is
/// fatal to Dispatcher construction) and MUST be idempotent (an
/// instance-registered singleton gets Start again after every restart).
public interface IEagerStart
{
    void Start(Task? predecessor);
}

public sealed class Registry
{
    // Registration order is preserved end-to-end: Merge's inject-concatenation
    // and last-replace-wins rules depend on it.
    private readonly List<(string EventType, HandlerSpec Spec)> _specs = new();

    /// Instance registration. Each handler is wrapped as a spec whose factory
    /// returns THE SAME instance — so instance-registered handlers are NOT
    /// state-reset by a supervisor restart (fine for stateless handlers, which
    /// is what this overload is for). Use the factory overload when a restart
    /// must produce fresh state. NOTE the Dispatcher takes DISPOSAL ownership
    /// of a disposable instance: never on restart (same-reference reuse), but
    /// escalation of its last worker and the drain's child phase DO dispose
    /// it — don't hand in an instance something else still uses.
    public Registry On(string eventType, params IHandler[] handlers)
    {
        foreach (var h in handlers)
            _specs.Add((eventType, new HandlerSpec(h.Name, h.OnFailure, () => h)));
        return this;
    }

    /// Instance registration with a per-handler budget window (ADR-0010 d9):
    /// unbounded by design — larger OR smaller than the dispatcher default.
    public Registry On(string eventType, IHandler handler, TimeSpan budget)
    {
        _specs.Add((eventType, new HandlerSpec(handler.Name, handler.OnFailure, () => handler,
            CheckBudget(budget, $"{eventType}/{handler.Name}"))));
        return this;
    }

    /// Spec registration: the factory runs once at Dispatcher construction and
    /// once per supervised restart, so a restart yields a genuinely fresh
    /// handler (fresh state, not just a fresh mailbox). `reloadFingerprint`
    /// (ADR-0010 phase 7) marks the spec reloadable and encodes its config
    /// identity — the exec-registration path passes it; coded handlers leave
    /// it null and are never reconciled.
    public Registry On(string eventType, string name, Func<IHandler> factory, FailMode onFailure = FailMode.Open,
                       TimeSpan? budget = null, string? reloadFingerprint = null)
    {
        _specs.Add((eventType, new HandlerSpec(name, onFailure, factory,
            budget is { } b ? CheckBudget(b, $"{eventType}/{name}") : null, reloadFingerprint)));
        return this;
    }

    /// "Unbounded by design" still lives inside int-millisecond ask-window
    /// math (windowMs = budgetMs + graceMs in the F# ask layer): a budget
    /// past ~24.8 days would overflow NEGATIVE and fault every dispatch of
    /// that handler with a misleading trail (caught by this slice's
    /// adversarial verify). Reject at REGISTRATION — a config bug should
    /// surface at construction, loudly, not per-dispatch.
    internal static TimeSpan CheckBudget(TimeSpan budget, string owner)
    {
        const double maxMs = int.MaxValue - 3_600_000;   // an hour of grace headroom
        if (budget <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(budget),
                $"{owner}: budget must be positive (got {budget})");
        if (budget.TotalMilliseconds > maxMs)
            throw new ArgumentOutOfRangeException(nameof(budget),
                $"{owner}: budget exceeds the ask-window ceiling (~24.8 days; int-ms math)");
        return budget;
    }

    /// Everything the Dispatcher needs to spawn one worker per registration.
    internal IReadOnlyList<(string EventType, HandlerSpec Spec)> Specs => _specs;
}

public sealed record DispatchResult(Effect Merged, Trace Trace);

/// What a hot-reload reconcile did (ADR-0010 phase 7): counts of exec workers
/// added, removed, changed (config differed ⇒ child replaced) and kept
/// (unchanged ⇒ warm child untouched). Carried on the `handlers.reload` trail
/// line so an edit's effect is visible without diffing the file.
public sealed record ReconcileSummary(int Added, int Removed, int Changed, int Kept);

/// One handler's registration plus its live supervision state, as plain data —
/// the read API's view (ADR-0007 get-handlers). Generation is the supervised
/// worker's restart count; Dead is true once it has escalated past its window.
public sealed record HandlerStatus(
    string EventType, string Name, FailMode OnFailure, int Generation, bool Dead);

public sealed class Dispatcher
{
    private sealed record Outcome(string Name, Effect Effect);

    /// A registered handler, converged onto the actor layer: the spec's name,
    /// fail mode, and RESOLVED budget window (per-handler override or the
    /// dispatcher default, with its grace) ride along in C#, while the handler
    /// itself lives inside a supervised F# Worker actor and is only ever
    /// reached via ask.
    private sealed record Runner(string Name, FailMode OnFailure, TimeSpan Budget, TimeSpan Grace, bool BudgetOverridden,
                                 Worker<(HookEvent, HandlerContext), Effect> Worker);

    /// A reloadable exec worker's diff state (ADR-0010 phase 7): its event, its
    /// config Fingerprint (the reconcile compares this to decide keep-vs-
    /// replace), and the live Runner. Keyed in _execWorkers by worker id.
    private sealed record ExecWorker(string EventType, string Fingerprint, Runner Runner);

    private readonly TimeSpan _budget;
    // Held for the hot-reload reconcile: the same supervisor spawns/removes
    // exec workers at runtime, and an explicit ctor grace (the classification-
    // test seam) pins EVERY handler's grace while null lets each scale to its
    // own budget (GraceFor).
    private readonly Supervisor _sup;
    private readonly TimeSpan? _graceOverride;

    // THE DISPATCH FAN-OUT SNAPSHOT (event -> runners). A volatile, treated-as-
    // immutable snapshot swapped WHOLE on a hot reload (ADR-0010 phase 7): the
    // hot dispatch path reads it lock-free — a ReloadingPolicy-style benign
    // race, a dispatch mid-reconcile sees either the old or the new snapshot,
    // both valid — while Reconcile rebuilds a fresh dictionary under
    // _reconcileLock and publishes it with one atomic reference write.
    // Case-INSENSITIVE event lookup: a casing difference must never split the
    // event space — "userPromptSubmit" in a handlers.json entry registers a
    // worker that MUST fire when the canonical "UserPromptSubmit" dispatches
    // (the ADR-0006 silent-grant lesson's registration-side twin, caught live
    // by this slice's adversarial verify). DispatchPolicy matches events
    // case-insensitively for exactly the same reason.
    private volatile Dictionary<string, List<Runner>> _runners;

    // The hot-reload split (ADR-0010 phase 7). Coded (non-reloadable) runners
    // are frozen at construction, per event in registration order — the part
    // of _runners a handlers.json reload NEVER touches; Reconcile recomposes
    // _runners = these ++ the fresh exec runners. (Coded handlers are
    // reconstructed-and-discarded by every reload's BuildDefaultRegistry — its
    // fresh coded specs are ignored here — so they must stay cheap and
    // non-disposable; user logic is exec, not coded, ADR-0010 d1.) The exec
    // workers are keyed by worker id and carry the diff state: id = (event,
    // name) identity, Fingerprint = config version. Mutated only under
    // _reconcileLock (and once, single-threaded, at construction).
    private readonly Dictionary<string, List<Runner>> _codedRunners;
    private readonly Dictionary<string, ExecWorker> _execWorkers = new(StringComparer.Ordinal);
    private readonly object _reconcileLock = new();

    // THE TEARDOWN SEAM (ADR-0010 d6, kill-discipline slice). IHandler has no
    // teardown member and the F# lib must never see IHandler — so disposal
    // threads through the C# factory closure: each worker's CURRENT handler
    // instance is tracked here (worker id → instance), the closure swaps it
    // on every factory run (disposing the replaced instance), the supervisor's
    // escalation signal disposes the LAST instance (MarkDead never re-runs the
    // factory — without this hook an escalated ExecHandler's child would
    // orphan, the N3 leak class), and DisposeHandlersAsync is the drain's
    // child phase. Disposal from restart/escalation paths is fire-and-forget:
    // both run on the supervisor's one-at-a-time fault mailbox, which must
    // never block behind a TERM→grace→KILL window.
    private readonly object _teardownLock = new();
    private readonly Dictionary<string, IHandler> _liveInstances = new(StringComparer.Ordinal);
    // Fire-and-forget disposals still in flight (restart evictions,
    // escalations). The drain must AWAIT these too (adversarial-verify find):
    // an instance evicted seconds before cutover may be mid-TERM-grace on its
    // lingering children — a drain that only covers CURRENT instances would
    // let the daemon exit before that SIGKILL escalation lands.
    private readonly HashSet<Task> _pendingDisposals = new();
    private bool _tornDown;

    // The LONG-LIVED background queue (ADR-0004: built here, once). Background
    // effects outlive their responses in a daemon by design: DispatchAsync
    // enqueues and returns; this single consumer runs them in order. Collapsed
    // mode awaits CompleteBackgroundAsync before exit — same drain-before-exit
    // contract as before, relocated. _sidePending is the bookkeeping the
    // sigterm-drain and mandatory-idle-exit slices share ("a non-empty
    // background queue defers exit") — one counter, both readers.
    private sealed record SideWork(string Name, HookEvent E, string? DispatchId, Effect.Background Effect, Trace Trace);
    private readonly Channel<SideWork> _side = Channel.CreateUnbounded<SideWork>();
    private readonly Task _sideConsumer;
    private int _sidePending;

    /// Workers are spawned HERE, from a snapshot of the registry — register all
    /// handlers BEFORE constructing the Dispatcher; later Registry.On calls are
    /// not picked up. The optional supervisor is the test seam (inject one
    /// built on a fake clock to make restart-window math deterministic).
    /// `grace` (default: 10% of the handler's EFFECTIVE budget, clamped
    /// 100ms..1s) widens the ask window past the budget so a token-honoring
    /// handler's cancellation reply lands INSIDE it — that reply leaves the
    /// handler AT the budget and needs a beat to travel (ADR-0004 decision
    /// 5). Worst-case dispatch wall time is max over its handlers of
    /// (budget_i + grace_i), paid only when a handler never answers
    /// (ADR-0010 d9: per-handler windows).
    public Dispatcher(Registry registry, TimeSpan budget, Supervisor? supervisor = null, TimeSpan? grace = null)
    {
        _budget = Registry.CheckBudget(budget, "dispatcher default");
        // Grace scales with each handler's EFFECTIVE budget (ADR-0010 d9: a
        // 10-minute window deserves more reply-travel headroom than a 100ms
        // one — a fixed grace mis-sized against a small budget would turn an
        // honoring handler's cancellation reply into a counted wedge). An
        // explicit ctor grace (the classification-test seam) still wins for
        // every handler.
        _graceOverride = grace;
        _sideConsumer = Task.Run(ConsumeSideAsync);
        _sup = supervisor ?? new Supervisor("dispatcher", maxRestarts: 3, TimeSpan.FromSeconds(5));

        // ONE worker per (eventType, spec) registration, from the registry
        // snapshot. The worker is generic — the F# side never sees
        // HookEvent/Effect (dependency arrow: C# host -> F# lib); the (event,
        // ctx) tuple flows through it opaquely and only the delegate inside
        // SpawnWorker ever looks inside. Coded handlers land in _codedRunners
        // (frozen); exec handlers (ReloadFingerprint set) additionally register
        // in _execWorkers so a later reload can diff them. _runners is the
        // composed fan-out snapshot both paths dispatch through.
        var composed = new Dictionary<string, List<Runner>>(StringComparer.OrdinalIgnoreCase);
        var coded = new Dictionary<string, List<Runner>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, eventType, spec) in AssignIds(registry.Specs))
        {
            var runner = SpawnWorker(id, spec);
            AddRunner(composed, eventType, runner);
            if (spec.ReloadFingerprint is null)
                AddRunner(coded, eventType, runner);
            else
                _execWorkers[id] = new ExecWorker(eventType, spec.ReloadFingerprint, runner);
        }
        _codedRunners = coded;
        _runners = composed;

        // Escalation half of the teardown seam. SubscribeEscalated, never
        // OnEscalated: that slot belongs to the host (last-writer-wins), and
        // silently clobbering it — or being clobbered — is exactly the bug
        // class this seam exists to close.
        _sup.SubscribeEscalated((id, _) =>
        {
            // Register the disposal UNDER the same lock that removes the
            // instance (FireDisposeLocked) — never across two lock sections, or
            // the drain's snapshot could catch the instance gone from
            // _liveInstances but not yet in _pendingDisposals and await neither
            // (the drain-await gap two phase-7 skeptics confirmed).
            lock (_teardownLock)
                if (_liveInstances.Remove(id, out var h) && !SharedElsewhereNoLock(id, h))
                    FireDisposeLocked(h, id, "escalated");
        });
    }

    /// Assign each spec its stable worker id, mirroring registration order and
    /// the name-collision disambiguation. DETERMINISTIC: the same specs in the
    /// same order yield the same ids, so across a hot reload an unchanged exec
    /// entry keeps its id — and therefore its warm child. Coded handlers come
    /// first (BuildDefaultRegistry order) and never change, so they contribute
    /// a constant id-space seed; exec names are file-unique (the parser rejects
    /// duplicates), so an exec worker's id is a pure function of its (event,
    /// name) plus that constant seed. The ctor and Reconcile both run this over
    /// their registry, so the ids line up and the diff is meaningful.
    private static IEnumerable<(string Id, string EventType, HandlerSpec Spec)> AssignIds(
        IReadOnlyList<(string EventType, HandlerSpec Spec)> specs)
    {
        var idCounts = new Dictionary<string, int>();
        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (eventType, spec) in specs)
        {
            var baseId = $"{eventType}/{spec.Name}";
            var n = idCounts.TryGetValue(baseId, out var c) ? c + 1 : 1;
            var id = n == 1 ? baseId : $"{baseId}#{n}";   // disambiguate name collisions
            // Re-probe until genuinely unused: a handler literally NAMED "h#2"
            // beside two "h" registrations would otherwise collide — and a
            // duplicate worker id corrupts both supervision (one child's faults
            // restart the other) and the teardown seam (the second TrackSwap
            // disposes the first's live instance).
            while (!usedIds.Add(id)) id = $"{baseId}#{++n}";
            idCounts[baseId] = n;
            yield return (id, eventType, spec);
        }
    }

    /// Spawn one supervised worker for a spec — the per-registration body
    /// shared by construction and the hot-reload reconcile. The factory is the
    /// OTP child-spec: a restart re-runs it (fresh state, fresh child), the
    /// REPLACED instance is disposed (teardown seam: its child must not outlive
    /// its generation), eager side-work starts only AFTER admission (a
    /// torn-down dispatcher never lets a straggling restart spawn — the
    /// design-panel orphan find), and a successor's spawn sequences behind its
    /// evicted predecessor's kill (exclusive resources release first). A
    /// hot-reload CHANGE rides this SAME TrackSwap path — Supervisor.Remove
    /// frees the id, then a fresh SpawnWorker at that id evicts the old
    /// instance and threads its TERM→grace→KILL as the new child's predecessor.
    private Runner SpawnWorker(string id, HandlerSpec spec)
    {
        var worker = Worker<(HookEvent, HandlerContext), Effect>.Supervised(_sup, id, () =>
        {
            var h = spec.Factory();
            var (admitted, predecessor) = TrackSwap(id, h);
            if (admitted && h is IEagerStart eager) eager.Start(predecessor);
            return req => h.HandleAsync(req.Item1, req.Item2);
        });
        var effective = spec.Budget ?? _budget;
        return new Runner(spec.Name, spec.OnFailure, effective,
            _graceOverride ?? GraceFor(effective), spec.Budget is not null, worker);
    }

    private static void AddRunner(Dictionary<string, List<Runner>> d, string eventType, Runner r)
    {
        if (!d.TryGetValue(eventType, out var list)) d[eventType] = list = new List<Runner>();
        list.Add(r);
    }

    /// Internal (not private) for the registration-time budget-vs-drain warn
    /// (handlers.budgetBeyondDrain): the warn must compare the same
    /// budget+grace the ask window will actually use.
    internal static TimeSpan GraceFor(TimeSpan budget) =>
        TimeSpan.FromMilliseconds(Math.Clamp(budget.TotalMilliseconds * 0.10, 100, 1000));

    /// Factory-closure half of the teardown seam: record the worker's new
    /// current instance; dispose the one it replaced. Reference-equal swap =
    /// instance registration's captured singleton coming back after a restart
    /// — REUSED, never disposed. An instance still referenced by another
    /// worker's slot (one singleton on several events) survives too, until
    /// its last slot lets go. Post-drain restarts get their fresh instance
    /// disposed on the spot: after teardown, NOTHING may hold a live child.
    /// Returns the ADMISSION verdict (false after teardown — the caller must
    /// not start eager side-work) and the evicted predecessor's disposal
    /// task, so an IEagerStart successor can sequence its spawn behind the
    /// predecessor's kill.
    private (bool Admitted, Task? Predecessor) TrackSwap(string id, IHandler next)
    {
        // The eviction and its disposal-registration stay in ONE lock section
        // (FireDisposeLocked): the drain's single-lock snapshot must never see
        // the evicted instance gone from _liveInstances yet absent from
        // _pendingDisposals, or it would await neither and drop its kill.
        lock (_teardownLock)
        {
            if (_tornDown)
            {
                FireDisposeLocked(next, id, "post-drain restart");
                return (false, null);
            }
            _liveInstances.TryGetValue(id, out var old);
            _liveInstances[id] = next;
            Task? predecessor = null;
            if (old is not null && !ReferenceEquals(old, next) && !SharedElsewhereNoLock(id, old))
                predecessor = FireDisposeLocked(old, id, "restart");
            return (true, predecessor);
        }
    }

    private bool SharedElsewhereNoLock(string exceptId, IHandler inst) =>
        _liveInstances.Any(kv => kv.Key != exceptId && ReferenceEquals(kv.Value, inst));

    /// Fire-and-forget disposal, registered so the drain can await it —
    /// fire-and-forget, not fire-and-lost — and RETURNED so a successor's eager
    /// start can sequence behind it. MUST be called while holding _teardownLock:
    /// the pending-registration is thereby ATOMIC with the caller's
    /// _liveInstances removal, closing the drain-await gap (two phase-7 skeptics)
    /// where a snapshot between "removed" and "registered" would await neither
    /// and drop the kill. Only the kill's fast SYNCHRONOUS prefix runs under the
    /// lock — DisposeInstanceAsync returns at its first await (a resident's
    /// DisposeAsync cancels its lifetime then awaits the group kill), and that
    /// prefix touches the handler's OWN lock, never _teardownLock, so no
    /// reentrancy. The restart/escalation callers still don't BLOCK on the grace
    /// window: they get the Task back and move on.
    private Task FireDisposeLocked(IHandler h, string id, string reason)
    {
        var t = DisposeInstanceAsync(h, id, reason);
        if (t.IsCompleted) return t;
        _pendingDisposals.Add(t);
        t.ContinueWith(done =>
        {
            lock (_teardownLock) _pendingDisposals.Remove(done);
        }, TaskScheduler.Default);
        return t;
    }

    private static async Task DisposeInstanceAsync(IHandler h, string id, string reason)
    {
        if (h is not (IAsyncDisposable or IDisposable)) return;   // nothing to tear down
        try
        {
            Log.Info("dispatcher", "handler.teardown", new LogFields { ActorId = id, Msg = reason });
            if (h is IAsyncDisposable ad) await ad.DisposeAsync();
            else ((IDisposable)h).Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("dispatcher", "handler.teardownError",
                new LogFields { ActorId = id, Msg = $"{reason}: {ex.Message}" });
        }
    }

    /// The drain's CHILD PHASE (ADR-0010 d6 + the N6 decision): dispose every
    /// worker's current handler instance — an ExecHandler kills its live
    /// children TERM→grace→KILL and refuses new spawns — each instance once,
    /// however many slots share it. Awaited (unlike the restart/escalation
    /// paths): the caller IS the drain and budgets for the grace window.
    /// After this the dispatcher is torn down: any straggling supervised
    /// restart has its fresh instance disposed immediately. Returns how many
    /// instances had a teardown to run.
    public async Task<int> DisposeHandlersAsync()
    {
        List<(string Id, IHandler H)> targets = new();
        Task[] pending;
        lock (_teardownLock)
        {
            _tornDown = true;
            var seen = new HashSet<IHandler>(ReferenceEqualityComparer.Instance);
            foreach (var (id, h) in _liveInstances)
                if (seen.Add(h) && h is (IAsyncDisposable or IDisposable))
                    targets.Add((id, h));
            _liveInstances.Clear();
            // Restart-evicted / escalation disposals still mid-flight: an
            // instance evicted seconds ago may be mid-TERM-grace on its
            // children — exiting before its SIGKILL escalation lands would
            // orphan them (adversarial-verify find).
            pending = _pendingDisposals.ToArray();
        }
        await Task.WhenAll(targets.Select(t => DisposeInstanceAsync(t.H, t.Id, "drain")).Concat(pending));
        return targets.Count;
    }

    /// Drain-timeout companion (N6): close the background intake WITHOUT
    /// awaiting the consumer. A dispatch cut by the child phase may still
    /// resolve and try to enqueue a sibling's Background effect; with the
    /// writer completed, that enqueue hits the loud side.dropped path instead
    /// of silently STARTING work the exiting daemon will abandon mid-run.
    public void CloseBackgroundIntake() => _side.Writer.TryComplete();

    /// THE HOT-RELOAD RECONCILE (ADR-0010 phase 7). Diff the reloadable (exec)
    /// workers against a freshly-built registry and converge to it WITHOUT
    /// churning unchanged warm children:
    ///   * unchanged (same id, same fingerprint) ⇒ KEEP — the resident child
    ///     keeps running, untouched (the whole point of "warm");
    ///   * removed (id gone from fresh) ⇒ Supervisor.Remove + evict-and-kill
    ///     the instance (drain-then-die, TERM→grace→KILL — NOT a GC drop);
    ///   * changed (same id, new fingerprint) ⇒ Supervisor.Remove frees the id,
    ///     then a fresh SpawnWorker at that id rides TrackSwap to evict the old
    ///     instance and sequence the new child behind its predecessor's kill;
    ///   * added (new id) ⇒ SpawnWorker, no predecessor.
    /// A malformed/absent handlers.json yields a fresh registry with ZERO exec
    /// handlers, so every current resident becomes a removal — malformed reload
    /// kills ALL residents, no keep-last-good (ADR-0010 d4). Coded handlers are
    /// never touched (they carry no fingerprint). The kills are fire-and-forget
    /// through the same teardown seam the drain awaits (_pendingDisposals), so
    /// this returns fast — the triggering hook proceeds on the new snapshot and
    /// a just-added resident is Spawning until its readiness handshake (the
    /// documented fail-mode-while-warming). Serialized by _reconcileLock;
    /// _runners is published with one atomic write for the lock-free readers.
    public ReconcileSummary Reconcile(Registry fresh)
    {
        lock (_reconcileLock)
        {
            var freshExec = AssignIds(fresh.Specs)
                .Where(x => x.Spec.ReloadFingerprint is not null)
                .ToList();   // ordered; ids unique by construction
            var freshIds = new HashSet<string>(freshExec.Select(x => x.Id), StringComparer.Ordinal);

            int added = 0, removed = 0, changed = 0, kept = 0;

            // Removals: current exec ids absent from the fresh set. Stop the
            // worker (Supervisor.Remove: no restart on a late exit, asks fail
            // fast) and evict+kill its instance.
            foreach (var id in _execWorkers.Keys.Where(k => !freshIds.Contains(k)).ToList())
            {
                _sup.Remove(id);
                EvictInstance(id, "reload: entry removed");
                _execWorkers.Remove(id);
                removed++;
            }

            // Adds + changes, walked in fresh order (Compose below reuses it).
            // A KEEP is a no-op — the warm child is never touched.
            foreach (var (id, eventType, spec) in freshExec)
            {
                if (_execWorkers.TryGetValue(id, out var cur))
                {
                    if (cur.Fingerprint == spec.ReloadFingerprint) { kept++; continue; }
                    // CHANGED: free the id so the re-spawn's Supervisor.Spawn
                    // won't hit the duplicate-id refusal; the old instance stays
                    // in _liveInstances so the new worker's TrackSwap evicts it
                    // and threads its kill as the fresh child's predecessor
                    // (the restart path, reused — no bespoke eviction here).
                    _sup.Remove(id);
                    _execWorkers[id] = new ExecWorker(eventType, spec.ReloadFingerprint!, SpawnWorker(id, spec));
                    changed++;
                }
                else
                {
                    _execWorkers[id] = new ExecWorker(eventType, spec.ReloadFingerprint!, SpawnWorker(id, spec));
                    added++;
                }
            }

            _runners = Compose(freshExec);
            return new ReconcileSummary(added, removed, changed, kept);
        }
    }

    /// Retire an exec instance outside the restart path (a reload REMOVAL only):
    /// drop it from the live-instance map and fire-and-forget its disposal — the
    /// resident's DisposeAsync kills the group TERM→grace→KILL, registered in
    /// _pendingDisposals so the drain still awaits it (never a GC drop). A
    /// CHANGE does NOT come here: its re-spawn's TrackSwap owns the eviction so
    /// the kill can thread as the successor's predecessor. No-op after teardown
    /// (DisposeHandlersAsync already cleared _liveInstances) and for a singleton
    /// still referenced by another slot.
    private void EvictInstance(string id, string reason)
    {
        // Removal + disposal-registration in ONE lock section (FireDisposeLocked)
        // so the drain can never miss this kill mid-registration.
        lock (_teardownLock)
            if (_liveInstances.Remove(id, out var h) && !SharedElsewhereNoLock(id, h))
                FireDisposeLocked(h, id, reason);
    }

    /// Recompose the dispatch snapshot: coded runners first (frozen, in their
    /// construction order), then the exec runners in FRESH file order — so
    /// Merge's inject concatenation follows the file. Reads _execWorkers, so
    /// callers hold _reconcileLock; the returned dictionary is published to
    /// _runners with one atomic reference write.
    private Dictionary<string, List<Runner>> Compose(
        IReadOnlyList<(string Id, string EventType, HandlerSpec Spec)> freshExecOrder)
    {
        var composed = new Dictionary<string, List<Runner>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (eventType, list) in _codedRunners)
            composed[eventType] = new List<Runner>(list);
        foreach (var (id, eventType, _) in freshExecOrder)
            if (_execWorkers.TryGetValue(id, out var w))
                AddRunner(composed, eventType, w.Runner);
        return composed;
    }

    public async Task<DispatchResult> DispatchAsync(HookEvent e, string? dispatchId = null,
                                                    IReadOnlySet<string>? excludedHandlers = null)
    {
        // One volatile read of the fan-out snapshot: a hot reload may swap it
        // concurrently (ADR-0010 phase 7), so this dispatch pins one coherent
        // generation of it — old or new, both valid.
        var runners = _runners;
        var registered = runners.TryGetValue(e.Type, out var list)
            ? (IReadOnlyList<Runner>)list : Array.Empty<Runner>();
        // Handler-level exclusion (ADR-0006 decision 2 / N3): drop named
        // handlers from THIS dispatch's view — "as if unregistered for this
        // dispatch". Order-preserving (.Where over the registration-ordered
        // list, which Merge depends on), and pre-fan-out, so an excluded
        // fail-closed handler never runs and therefore contributes no Deny.
        // The snapshot registry and each excluded handler's supervised Worker
        // are untouched — filtered, never restarted (a restart wipes state).
        // DEAD until the evaluator wires it in (phases 3+): the default null
        // path is today's behavior, byte-identical.
        var handlers = excludedHandlers is { Count: > 0 }
            ? registered.Where(r => !excludedHandlers.Contains(r.Name)).ToList()
            : registered;
        var trace = new Trace(e.Type, _budget, handlers.Count);

        // Structured trail alongside the human Trace: dispatch.start now, and a
        // span that stamps durMs onto dispatch.done when the whole run finishes.
        // budgetMs here is the dispatcher DEFAULT; a handler whose spec
        // overrides it carries its own budgetMs on its handler.* trail lines
        // (ADR-0010 d9 trail truthfulness).
        Log.Info("dispatcher", "dispatch.start", Fields(e, dispatchId,
            data: new Dictionary<string, object>
            {
                ["handlers"] = handlers.Count,
                ["budgetMs"] = _budget.TotalMilliseconds,
            }));
        using var span = Log.Span("dispatcher", "dispatch.done", Fields(e, dispatchId));

        // FAN-OUT: every handler runs concurrently on the work-stealing thread
        // pool, each under its OWN budget window (ADR-0010 d9 — the dispatch
        // completes when every handler has answered or hit its own window;
        // WhenAll gives that for free). This is the .NET analogue of BEAM's
        // Task.async_stream or Node's Promise.allSettled — but backed by real,
        // parallel threads.
        var outcomes = await Task.WhenAll(
            handlers.Select(h => RunGuarded(h, e, dispatchId, trace)));

        // Route fire-and-forget effects onto the LONG-LIVED side channel; keep
        // loop-effects to merge. The response never waits on background work —
        // the daemon's consumer runs it after this method returns; collapsed
        // mode drains via CompleteBackgroundAsync before exit.
        var loop = new List<Effect>();
        foreach (var o in outcomes)
        {
            if (o.Effect is Effect.Background bg)
            {
                Interlocked.Increment(ref _sidePending);
                if (!_side.Writer.TryWrite(new SideWork(o.Name, e, dispatchId, bg, trace)))
                {
                    // Reached DELIBERATELY by a drain-cut dispatch (N6): the
                    // drain-timeout path closes the intake before the child
                    // phase, so a cut dispatch's sibling Background effect
                    // lands here — loud — instead of silently starting work
                    // the exiting daemon would abandon mid-run.
                    Interlocked.Decrement(ref _sidePending);
                    Log.Error("dispatcher", "side.dropped", Fields(e, dispatchId,
                        msg: "background effect enqueued after queue completion — dropped",
                        data: Handler(o.Name)));
                }
            }
            else loop.Add(o.Effect);
        }

        var merged = Merge(loop);
        span.Complete(Fields(e, dispatchId,
            data: new Dictionary<string, object>
            {
                ["handlers"] = handlers.Count,
                ["effect"] = merged.GetType().Name,
            }));
        return new DispatchResult(merged, trace);
    }

    /// Background effects not yet completed — queued or running. The idle and
    /// drain machinery reads this: a non-empty queue defers exit.
    public int BackgroundPending => Volatile.Read(ref _sidePending);

    /// A plain-data view of every registered handler for the read API (ADR-0007
    /// get-handlers): event, name, fail mode, and the supervised worker's live
    /// generation + dead flag. Read-only projection off the SAME runners the
    /// dispatch path uses — no parallel registry to drift. Registration order is
    /// preserved within each event (the order Merge depends on); the rich F# DU
    /// stays inside the actor lib — only the int/bool accessors cross.
    public IReadOnlyList<HandlerStatus> Snapshot() =>
        _runners.SelectMany(kv => kv.Value.Select(r =>
            new HandlerStatus(kv.Key, r.Name, r.OnFailure, r.Worker.Generation, r.Worker.IsDead)))
            .ToList();

    /// Single-shot mode: no more dispatches will come — complete the side
    /// queue and wait for the consumer to finish everything already enqueued.
    /// The dispatcher is done after this; daemon mode calls it only at drain.
    public async Task CompleteBackgroundAsync()
    {
        _side.Writer.Complete();
        await _sideConsumer;
    }

    private async Task ConsumeSideAsync()
    {
        await foreach (var w in _side.Reader.ReadAllAsync())
        {
            var sw = Stopwatch.StartNew();
            try
            {
                await w.Effect.Run(CancellationToken.None);
                w.Trace.Side(w.Name, sw.Elapsed, true);
                Log.Info("dispatcher", "side.ok", Fields(w.E, w.DispatchId, sw.Elapsed.TotalMilliseconds,
                    data: Handler(w.Name)));
            }
            catch (Exception ex)
            {
                w.Trace.Side(w.Name, sw.Elapsed, false, ex.Message);
                Log.Error("dispatcher", "side.error", Fields(w.E, w.DispatchId, sw.Elapsed.TotalMilliseconds,
                    msg: ex.Message, data: Handler(w.Name)));
            }
            finally
            {
                Interlocked.Decrement(ref _sidePending);
            }
        }
    }

    private async Task<Outcome> RunGuarded(Runner r, HookEvent e, string? dispatchId, Trace trace)
    {
        var sw = Stopwatch.StartNew();
        // Per-handler window (ADR-0010 d9): each runner gets its own CTS and
        // deadline sized to ITS budget — an override may exceed the dispatcher
        // default (a min-clamp under one shared token could only ever shorten).
        using var budgetCts = new CancellationTokenSource(r.Budget);
        var ctx = new HandlerContext(DateTimeOffset.UtcNow + r.Budget, budgetCts.Token, dispatchId);
        try
        {
            // Dispatch IS a CLASSIFIED ask (ADR-0004 decision 5): the budget
            // token cancels the handler at budget; the ask itself waits budget
            // + grace so an honoring handler's cancellation reply lands inside
            // the window and "no reply" is unambiguous. Classification of what
            // no-reply MEANS (wedge vs backlog) happens in the ask layer, which
            // reports wedges to the supervisor — the supervisor owns all
            // counting; this method only converts outcomes to effects and logs.
            var res = await r.Worker.AskClassifiedAsync(
                (e, ctx), (int)r.Budget.TotalMilliseconds, (int)r.Grace.TotalMilliseconds, dispatchId ?? "");
            switch (res.Status)
            {
                case AskStatus.Ok:
                    trace.Handler(r.Name, sw.Elapsed, "ok");
                    Log.Info("dispatcher", "handler.ok", Fields(e, dispatchId, sw.Elapsed.TotalMilliseconds,
                        data: HandlerData(r)));
                    return new Outcome(r.Name, res.Reply);

                case AskStatus.Faulted when res.Error is OperationCanceledException:
                    // The handler HONORED its budget token: a timeout, not a
                    // fault. (The supervisor sees the same OCE via
                    // reply-then-crash and restarts the worker WITHOUT counting
                    // it — carry-in c.) Chronic slowness stays visible here.
                    return Timeout(r, e, dispatchId, sw, trace, "cancelled");

                case AskStatus.Wedged:
                    // Received but never answered: the ask layer has reported
                    // it; the supervisor abandons the worker and counts it
                    // (carry-in a). The stuck task is leaked, deliberately.
                    return Timeout(r, e, dispatchId, sw, trace, "wedged");

                case AskStatus.Backlogged:
                    // Never received — queued behind a busy sibling dispatch.
                    // Backlog, not a defect: nothing counted against the worker
                    // (sustained backlog is the router evidence ADR-0004 d6
                    // waits for).
                    return Timeout(r, e, dispatchId, sw, trace, "backlogged");

                case AskStatus.Dead:
                    // Escalated worker: fail fast in ~0ms instead of burning
                    // the budget per dispatch (carry-in b).
                    trace.Handler(r.Name, sw.Elapsed, $"DEAD -> fail-{Mode(r)}");
                    Log.Warn("dispatcher", "handler.dead", Fields(e, dispatchId, sw.Elapsed.TotalMilliseconds,
                        data: HandlerData(r, Mode(r))));
                    return new Outcome(r.Name, Fail(r));

                default:   // AskStatus.Faulted, non-cancellation: the handler crashed
                    trace.Handler(r.Name, sw.Elapsed, $"ERROR -> fail-{Mode(r)} ({res.Error.Message})");
                    Log.Error("dispatcher", "handler.error", Fields(e, dispatchId, sw.Elapsed.TotalMilliseconds,
                        msg: res.Error.Message, data: HandlerData(r, Mode(r))));
                    return new Outcome(r.Name, Fail(r));
            }
        }
        catch (Exception ex)
        {
            // Belt-and-braces: the classified ask routes every expected outcome
            // into a status; anything landing here is infrastructure failure.
            trace.Handler(r.Name, sw.Elapsed, $"ERROR -> fail-{Mode(r)} ({ex.Message})");
            Log.Error("dispatcher", "handler.error", Fields(e, dispatchId, sw.Elapsed.TotalMilliseconds,
                msg: ex.Message, data: HandlerData(r, Mode(r))));
            return new Outcome(r.Name, Fail(r));
        }
    }

    private Outcome Timeout(Runner r, HookEvent e, string? dispatchId, Stopwatch sw, Trace trace, string classification)
    {
        trace.Handler(r.Name, sw.Elapsed, $"TIMEOUT({classification}) -> fail-{Mode(r)}");
        var data = HandlerData(r, Mode(r));
        data["classification"] = classification;
        Log.Warn("dispatcher", "handler.timeout", Fields(e, dispatchId, sw.Elapsed.TotalMilliseconds, data: data));
        return new Outcome(r.Name, Fail(r));
    }

    // Correlation bundle every dispatcher event carries: who asked (sessionId),
    // which lifecycle event, and the per-invocation dispatchId from Program.cs.
    private static LogFields Fields(HookEvent e, string? dispatchId, double? durMs = null,
                                    string? msg = null, IDictionary<string, object>? data = null) =>
        new()
        {
            DispatchId = dispatchId,
            SessionId = e.SessionId,
            HookEvent = e.Type,
            DurMs = durMs,
            Msg = msg,
            Data = data,
        };

    /// Handler(...) plus ADR-0010 d9 trail truthfulness: a spec-overridden
    /// budget rides on that handler's own trail lines (dispatch.start's
    /// budgetMs stays the dispatcher default).
    private static Dictionary<string, object> HandlerData(Runner r, string? failMode = null)
    {
        var d = Handler(r.Name, failMode);
        if (r.BudgetOverridden) d["budgetMs"] = r.Budget.TotalMilliseconds;
        return d;
    }

    private static Dictionary<string, object> Handler(string name, string? failMode = null)
    {
        var d = new Dictionary<string, object> { ["handler"] = name };
        if (failMode is not null) d["failMode"] = $"fail-{failMode}";
        return d;
    }

    private static string Mode(Runner r) => r.OnFailure == FailMode.Closed ? "closed" : "open";

    private static Effect Fail(Runner r) => r.OnFailure == FailMode.Closed
        ? new Effect.Decide(Verdict.Deny, $"{r.Name} failed under fail-closed policy")
        : new Effect.Noop();

    // Deterministic merge: deny wins, then ask, then replace-output, then
    // injects concatenate in registration order.
    private static Effect Merge(IReadOnlyList<Effect> effects)
    {
        if (effects.OfType<Effect.Decide>().FirstOrDefault(d => d.Verdict == Verdict.Deny) is { } deny) return deny;
        if (effects.OfType<Effect.Decide>().FirstOrDefault(d => d.Verdict == Verdict.Ask) is { } ask) return ask;
        if (effects.OfType<Effect.Replace>().LastOrDefault() is { } rep) return rep;
        var injects = effects.OfType<Effect.Inject>().Select(i => i.Text).ToList();
        return injects.Count > 0 ? new Effect.Inject(string.Join("\n", injects)) : new Effect.Noop();
    }
}

public sealed class Trace(string ev, TimeSpan budget, int handlerCount)
{
    // Locked: the long-lived background consumer appends Side lines after the
    // response is built in daemon mode, so writes can race a Render.
    private readonly List<string> _lines = new();

    public void Handler(string name, TimeSpan took, string status)
    {
        lock (_lines) _lines.Add($"  handler {name,-14} {took.TotalMilliseconds,7:F1}ms  {status}");
    }

    public void Side(string name, TimeSpan took, bool ok, string? err = null)
    {
        lock (_lines) _lines.Add($"  side    {name,-14} {took.TotalMilliseconds,7:F1}ms  {(ok ? "ok" : "ERR: " + err)}");
    }

    public string Render()
    {
        lock (_lines)
            return $"[captAInHook] {ev}  ({handlerCount} handler(s), budget {budget.TotalMilliseconds:F0}ms)\n" +
                   (_lines.Count > 0 ? string.Join("\n", _lines) : "  (no handlers registered)");
    }
}
