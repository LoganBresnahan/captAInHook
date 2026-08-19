using System.Text.Json;
using CaptainHook.Actors;
using CaptainHook.Api;
using CaptainHook.Mail;

namespace CaptainHook.Core;

// Roadmap item 22 / ADR-0017 decision 4, slice `watcher-actor` — the WATCHER
// as it runs for real: in-daemon, supervised, fed by the trail, armed with one
// monotonic deadline, persisting before it dispatches.
//
// The brain (`WatcherBrain`) is pure and the CLI (`mail watch --once`) is dry;
// this file is the only place the two are joined to a schedule, and the
// schedule is deliberately NOT a timer. Three pieces:
//
//   * THE FEED is a tail of the daemon's own trail file — the same JSONL both
//     emitters append and the SSE stream already tails (`TrailCursor`) — never
//     an in-process tee: `mail send` is a CLI process and `mail digest` is an
//     exec child, so neither `mail.append` nor `mail.cursorAdvance` is ever
//     raised inside this process. The pump polls from the file's END at start
//     (history is on disk as `nudges.jsonl`, not in the tail) and evaluates
//     ONCE per batch of relevant lines. Relevance is the row's `evt` — parsed,
//     not substring-matched — so a payload's stderr quoting "mail.append" is
//     nothing, and so are the watcher's own rows (`watch.*`, `mail.nudge`,
//     `nudge.*`, and the woken turn's `dispatch.*`/`exec.*`): the loop the
//     plan's verify names — the actor's own output re-triggering it — is
//     closed by the gate, not by luck. The rows a woken turn DOES cause to
//     appear (its `mail digest --as` advancing a cursor, its reply's append)
//     re-trigger on purpose: that is the condition clearing, and each
//     evaluation of it is a read that finds less unread than the last.
//
//   * THE DEADLINE is one number. The brain hands back `NextCheckMs` (rule 1
//     in `WatcherBrain`'s header) and the pump HOLDS it — nothing is scheduled;
//     each poll tick compares the injected monotonic clock against the held
//     value and evaluates when it has passed. A second envelope re-arms by
//     replacing it; a cursor advance that clears the condition just makes the
//     next evaluation return a later one or none. Latency is the poll cadence
//     (a second, against thresholds of minutes), and there is no
//     `Task.Delay(deadline − now)`, no `System.Threading.Timer`, and no
//     `DateTime` anywhere in this file — the poll's own pacing delay is the
//     tail's, the same one `TrailSubscription` runs on, and it is not
//     control-flow timing (house invariant 2). Tests inject `FakeClock` and
//     drive `StepAsync` by hand; the deadline fires when the clock says so.
//
//   * THE ACTOR is an F# `Worker` under its own `Supervisor`, and the reason is
//     restart semantics rather than concurrency: an evaluation that throws is
//     reply-then-crash, the supervisor runs the factory again, and the fresh
//     instance starts from the durable state — this process's last evaluated
//     one when there is one (its monotonic stamps are still valid; the fresh
//     instance watched every minute the crashed one did), else `nudges.jsonl`
//     as ages — which is OTP's "restart = fresh state" with the state being
//     the memory the brain owns rather than anything the crashed instance
//     held mid-evaluation. The pump sees the fault, re-arms for NOW, and the
//     fresh instance evaluates on the next tick. Three crashes inside the window escalate;
//     the handle is dead, the pump says so once (`watch.dead`) and stops —
//     the daemon goes on serving hooks with no watcher, which is what
//     "supervised" means when the supervisor gives up.
//
// **Persist THEN dispatch.** For each nudge the brain emits: put it to the
// policy (`MailNudgeEvent.Admit` — a decision, no action), record it into the
// state (charged if admitted, quiet clock restarted either way; the
// `mail.nudge` row is written here, `NudgeStore.Record`), SAVE the state, and
// only then wake the turn (`MailNudgeEvent.RunAsync`). A crash between the
// save and the wake costs one poke that never went out — visible on the trail
// as a `mail.nudge` with no `dispatch.start` after it — and can never double
// one, which is the conservative direction every choice in this ADR takes.
// The wake is fire-and-forget FROM THE ACTOR: a turn can run for minutes and
// the mailbox must not be blocked behind it (the next `mail.append` for some
// other role deserves an evaluation now); it is counted as daemon ACTIVITY
// (`ServeStats.OnInternalStart`) so the idle watchdog does not starve a
// daemon mid-turn and the drain gives it its chance — the armed DEADLINE, by
// contrast, defers nothing (N2): a daemon with a watcher and no work idle-exits
// exactly as before, and the deadline that falls while it sleeps is honored on
// the next start because the state came back as ages.
//
// **On start** the first evaluation waits one poll interval — deliberately.
// A daemon is spawned BY a hook, and that hook's session stamps presence when
// it is served, a few milliseconds after listening; an evaluation that ran
// before it would see nobody home and, under a `noLiveSession` rule, wake a
// robot for the very window that just came back. One interval later the
// spawning hook has been served, and a deadline that had fallen while the
// daemon slept is due at once — which is what N2 promises.
//
// The watcher exists only when the daemon is handed a `watchPath`
// (`DaemonHost.RunAsync`): production always passes one (`Program.cs`); a
// test daemon that passes none has no watcher, and can therefore never write
// a `nudges.jsonl` into — or raise a nudge against — the operator's live tree.

/// Why an evaluation ran. Rides the `watch.evaluate` row so the trail says
/// which of the three wake-ups produced each decision.
public enum WatchTrigger
{
    /// The first evaluation of a daemon — due deadlines are honored here.
    Start,
    /// A batch of `mail.append` / `mail.cursorAdvance` rows arrived on the tail.
    Trail,
    /// The held monotonic deadline passed.
    Deadline,
    /// The supervisor restarted the actor after a fault; the fresh instance
    /// re-evaluates from the reloaded state.
    Restart,
}

/// One evaluation's outcome, as the actor reports it back to the pump (and to
/// a test): what woke it, how many nudges the brain emitted and how many the
/// policy admitted, the one deadline to hold, and whether the state landed.
public sealed record WatchStep(WatchTrigger Trigger, int Nudges, int Admitted, long? NextCheckMs, bool StateSaved);

/// Everything the watcher is wired to. `InternalSpec` and `Policy` are
/// functions because both reload (`ReloadingHarnessRegistry`, `ReloadingPolicy`)
/// and the actor must see the current one per evaluation. `Stats` is the
/// daemon's activity counter — null in a bare test. `Supervisor` is a test
/// seam (a fake clock's supervisor); production builds its own.
public sealed record MailWatcherOptions(
    string MailDir,
    string WatchPath,
    string? HandlersPath,
    string TrailPath,
    Dispatcher Dispatcher,
    Func<HarnessSpec> InternalSpec,
    Func<PolicyResolution> Policy,
    SessionPresence Presence,
    Func<long> Clock,
    ServeStats? Stats = null,
    TimeSpan? Poll = null,
    Supervisor? Supervisor = null);

public sealed class MailWatcher : IAsyncDisposable
{
    public const string ActorId = "watcher";

    /// The tail's cadence, and therefore the deadline's resolution. A second:
    /// thresholds are minutes, and a stat per second on an idle daemon is
    /// nothing. Tests tighten it.
    public static readonly TimeSpan DefaultPoll = TimeSpan.FromSeconds(1);

    /// One evaluation's budget on the classified ask: a store read, the brain,
    /// at most one file append and the SPAWN of a dispatch (never its run).
    /// Generous, because past budget+grace the ask layer reports a WEDGE and
    /// the supervisor abandons the instance — right for a hung disk, wrong for
    /// a merely large store.
    private const int EvaluateBudgetMs = 10_000;
    private const int EvaluateGraceMs = 2_000;

    private readonly MailWatcherOptions _o;
    private readonly TimeSpan _poll;
    private readonly Supervisor _sup;
    private readonly Worker<WatchTrigger, WatchStep> _worker;
    private readonly TrailCursor _cursor;
    private readonly StatGated<IReadOnlyList<WatchRule>> _rules;
    private readonly StatGated<RoleKinds> _kinds;
    private readonly CancellationTokenSource _stop = new();
    private Task? _pump;
    private int _inFlight;
    private long _armed = long.MinValue;   // the pump's held deadline; MinValue = none
    private NudgeState? _lastState;        // this process's freshest state — a restart's starting point

    /// Fault-injection seam for the supervision tests: runs at the top of every
    /// evaluation, on the actor's thread. A throw here is a crash the
    /// supervisor sees, exactly as a throw anywhere in `Evaluate` would be.
    internal Action<WatchTrigger>? BeforeEvaluate { get; set; }

    public MailWatcher(MailWatcherOptions options)
    {
        _o = options;
        _poll = options.Poll ?? DefaultPoll;
        // A one-minute window: three faults inside it and the watcher is
        // escalated. Wide, because the fault that recurs is a bad store or a
        // bad file, and a tight window would loop through a transient one.
        _sup = options.Supervisor ?? new Supervisor("watcher", maxRestarts: 3, TimeSpan.FromMinutes(1), options.Clock);
        _rules = new StatGated<IReadOnlyList<WatchRule>>(options.WatchPath,
            path => WatchResolution.Resolve(path!).Effective());
        // A null handlers path is "no handlers file" — every role unserved,
        // no turn payload — and NEVER the default location: a test daemon
        // must not read the operator's live registrations.
        _kinds = new StatGated<RoleKinds>(options.HandlersPath,
            path => path is null ? RoleKinds.None : RoleKinds.From(ExecHandlersFile.Resolve(path)));
        // From the END: what happened before this daemon is on disk as ages
        // (`nudges.jsonl`), not in the tail. Trusted offset — our own stat.
        _cursor = new TrailCursor(options.TrailPath, CurrentLength(options.TrailPath));
        // The factory is the restart: a fresh instance LOADS the durable state.
        _worker = Worker<WatchTrigger, WatchStep>.Supervised(_sup, ActorId, () =>
        {
            var instance = new Instance(this);
            return trigger => Task.FromResult(instance.Evaluate(trigger));
        });
    }

    /// Whether the actor has been escalated past its restart window.
    public bool IsDead => _worker.IsDead;

    /// The actor's restart count — the live instance's generation.
    public int Generation => _worker.Generation;

    /// Nudge dispatches this watcher has started that have not finished.
    public int InFlight => Volatile.Read(ref _inFlight);

    /// The deadline the pump holds, or null. Test observability.
    internal long? Armed => Volatile.Read(ref _armed) is var a && a != long.MinValue ? a : null;

    /// Start the pump. Idempotent; the pump ends on `DisposeAsync`.
    public void Start()
    {
        if (_pump is not null) return;
        Log.Info("watch", "watch.start", new LogFields
        {
            Msg = "mail watcher up — evaluating on mail.append / mail.cursorAdvance and one held deadline",
            Data = new Dictionary<string, object>
            {
                ["trail"] = _o.TrailPath, ["fromOffset"] = _cursor.Offset,
                ["pollMs"] = _poll.TotalMilliseconds, ["watch"] = _o.WatchPath,
                ["handlers"] = _o.HandlersPath ?? "<none>", ["mailDir"] = _o.MailDir,
            },
        });
        _pump = Task.Run(() => PumpAsync(_stop.Token));
    }

    /// One evaluation through the supervised actor, classified. Null when it
    /// did not produce a step — a fault (the supervisor is restarting the
    /// instance), a stall, or a dead handle — each said on the trail. Public
    /// so a test drives the actor without the pump; the pump calls this too.
    public async Task<WatchStep?> StepAsync(WatchTrigger trigger)
    {
        var outcome = await _worker.AskClassifiedAsync(trigger, EvaluateBudgetMs, EvaluateGraceMs, ActorId);
        switch (outcome.Status)
        {
            case AskStatus.Ok:
                return outcome.Reply;
            case AskStatus.Faulted:
                Log.Warn("watch", "watch.evaluateFailed", new LogFields
                {
                    Msg = "watcher evaluation threw — the supervisor restarts it from the persisted state",
                    Data = new Dictionary<string, object>
                    {
                        ["trigger"] = trigger.ToString(), ["error"] = outcome.Error?.Message ?? "",
                    },
                });
                return null;
            case AskStatus.Dead:
                Log.Error("watch", "watch.dead", new LogFields
                {
                    Msg = "watcher escalated past its restart window — no robot nudge fires until the daemon restarts",
                    Data = new Dictionary<string, object> { ["trigger"] = trigger.ToString() },
                });
                return null;
            default:   // Wedged / Backlogged / Abandoned
                Log.Warn("watch", "watch.evaluateStalled", new LogFields
                {
                    Msg = "watcher evaluation did not answer within its budget",
                    Data = new Dictionary<string, object>
                    {
                        ["trigger"] = trigger.ToString(), ["status"] = outcome.Status.ToString(),
                    },
                });
                return null;
        }
    }

    /// The pump: poll the tail, evaluate on relevant rows, on the first tick,
    /// or when the held deadline has passed; hold the deadline the step hands
    /// back. Never stamps activity — an armed deadline defers no idle-exit.
    private async Task PumpAsync(CancellationToken ct)
    {
        var started = false;
        var retry = false;
        while (!ct.IsCancellationRequested)
        {
            // Pacing FIRST (see the header: the first evaluation waits one
            // interval so the spawning hook has stamped presence).
            try { await Task.Delay(_poll, ct); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }   // a slow stop already disposed the source

            // Drain every batch the tail has before deciding — a burst is one
            // evaluation, not one per 128 KB window.
            var relevant = 0;
            TrailPoll poll;
            do
            {
                poll = _cursor.Poll();
                foreach (var line in poll.Lines)
                    if (IsTrigger(line.Text)) relevant++;
            } while (poll.More && !ct.IsCancellationRequested);

            var now = _o.Clock();
            WatchTrigger? trigger =
                relevant > 0 ? WatchTrigger.Trail
                : !started ? WatchTrigger.Start
                : retry ? WatchTrigger.Restart
                : Armed is { } due && now >= due ? WatchTrigger.Deadline
                : null;
            if (trigger is not { } t) continue;

            var step = await StepAsync(t);
            started = true;
            if (step is null)
            {
                if (_worker.IsDead) break;   // watch.dead already on the trail
                // Fault or stall: the fresh (or freed) instance re-evaluates
                // on the next tick, tagged as what it is. The held deadline
                // is dropped — the fresh instance hands back its own.
                retry = true;
                Volatile.Write(ref _armed, long.MinValue);
                continue;
            }
            retry = false;
            Volatile.Write(ref _armed, step.NextCheckMs ?? long.MinValue);
        }
        Log.Info("watch", "watch.stop", new LogFields
        {
            Data = new Dictionary<string, object> { ["inFlight"] = InFlight, ["dead"] = _worker.IsDead },
        });
    }

    /// End the pump. Nudge dispatches already started belong to the dispatcher
    /// (the daemon's drain and child phase own them), not to this.
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_pump is { } p)
        {
            try { await p.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (Exception) { /* a stalled ask; the daemon is exiting */ }
        }
        _stop.Dispose();
    }

    /// The gate: is this trail row one of the two the watcher evaluates on?
    /// Substring first (cheap; most rows are neither), then the parsed `evt`
    /// — the substring is a FILTER, never the decision (`MailDeliveryFold`'s
    /// rule): a payload's stderr, an `exec.stderr` row quoting the pretty
    /// rendering, a `msg` mentioning the name — none of them is the event.
    internal static bool IsTrigger(string line)
    {
        if (line.Length == 0) return false;
        if (!line.Contains("\"mail.append\"", StringComparison.Ordinal)
            && !line.Contains("\"mail.cursorAdvance\"", StringComparison.Ordinal))
            return false;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("evt", out var evt) || evt.ValueKind != JsonValueKind.String) return false;
            return evt.GetString() is "mail.append" or "mail.cursorAdvance";
        }
        catch (JsonException) { return false; }
        catch (InvalidOperationException) { return false; }   // the deferred-unescape trap
    }

    /// Wake the turn — fire-and-forget from the actor, counted as activity.
    private void Fire(MailNudgeAdmission admission, MailNudge nudge, HarnessSpec spec)
    {
        Interlocked.Increment(ref _inFlight);
        _o.Stats?.OnInternalStart();
        _ = Task.Run(async () =>
        {
            try { await MailNudgeEvent.RunAsync(admission, nudge, _o.Dispatcher, spec); }
            catch (Exception ex)
            {
                Log.Warn("watch", "watch.dispatchFailed", new LogFields
                {
                    DispatchId = admission.DispatchId,
                    Msg = "a robot nudge's dispatch threw — the budget was charged; the turn did not run",
                    Data = new Dictionary<string, object> { ["role"] = nudge.Role, ["error"] = ex.Message },
                });
            }
            finally
            {
                _o.Stats?.OnInternalDone();
                Interlocked.Decrement(ref _inFlight);
            }
        });
    }

    private static long CurrentLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }

    /// One live instance of the actor: the brain's memory, loaded from disk at
    /// construction — which is why a supervised restart is a reload — and the
    /// evaluation that reads everything else fresh.
    private sealed class Instance
    {
        private readonly MailWatcher _w;
        private readonly NudgeStore _store;
        private NudgeState _state;

        public Instance(MailWatcher w)
        {
            _w = w;
            _store = new NudgeStore(w._o.MailDir);
            // The durable state — from THIS process's last evaluation when there
            // was one, else from disk. Both are the same memory; the difference
            // is freshness. Stamps are monotonic milliseconds of this process,
            // valid across a supervised restart (same clock), whereas the file
            // holds the state as of its last SAVE, re-derived as ages: a quiet
            // bus that saved nothing for six minutes would come back six
            // minutes younger, and a restart would delay every nudge by up to
            // a full quiet period. Only a new PROCESS has to pay that (N2's
            // "time not watched is not counted"); a restarted actor watched
            // every one of those minutes.
            _state = Volatile.Read(ref w._lastState) ?? _store.Load(w._o.Clock());
        }

        public WatchStep Evaluate(WatchTrigger trigger)
        {
            _w.BeforeEvaluate?.Invoke(trigger);

            var o = _w._o;
            var now = o.Clock();
            var rules = _w._rules.Current;
            var kinds = _w._kinds.Current;
            var cursors = new MailCursors(new MailStore(o.MailDir));
            var roles = MailWatch.RolesToWatch(cursors, rules);
            var mailboxes = MailWatch.ReadMailboxes(cursors, roles);
            var presence = o.Presence.Recent();

            var verdict = WatcherBrain.Evaluate(new WatchInput(mailboxes, presence, kinds, rules, _state, now));

            // Admit + record every nudge BEFORE anything is woken. The spec and
            // policy are read once per evaluation, only when there is a nudge
            // to put to them.
            var state = verdict.State;
            var admitted = new List<(MailNudgeAdmission Admission, MailNudge Nudge)>();
            HarnessSpec? spec = null;
            PolicyResolution? policy = null;
            foreach (var nudge in verdict.Nudges)
            {
                spec ??= o.InternalSpec();
                policy ??= o.Policy();
                var admission = MailNudgeEvent.Admit(nudge, spec, policy);
                state = NudgeStore.Record(state, nudge, admission.Admitted, admission.DispatchId, now);
                if (admission.Admitted) admitted.Add((admission, nudge));
            }

            // Persist — only when something changed, so a quiet bus does not
            // grow the file one line per cursor advance. A save that fails
            // has already warned (`watch.stateUnwritable`); the nudges still go,
            // charged in this process's memory: holding them would let a
            // read-only tree silently kill the robot channel, and the trail
            // says what happened either way.
            var changed = !Same(state, _state);
            var saved = changed && _store.Save(state, now);
            _state = state;
            Volatile.Write(ref _w._lastState, state);

            // …then dispatch.
            foreach (var (admission, nudge) in admitted) _w.Fire(admission, nudge, spec!);

            Log.Debug("watch", "watch.evaluate", new LogFields
            {
                Msg = $"watcher evaluated ({trigger}): {verdict.Nudges.Count} nudge(s), {admitted.Count} admitted",
                Data = new Dictionary<string, object>
                {
                    ["trigger"] = trigger.ToString(),
                    ["roles"] = verdict.Roles.Count,
                    ["mailboxes"] = mailboxes.Count,
                    ["nudges"] = verdict.Nudges.Count,
                    ["admitted"] = admitted.Count,
                    ["dead"] = verdict.Dead.Count,
                    ["nextCheckInMs"] = verdict.NextCheckMs is { } nc ? nc - now : (object)"none",
                    ["stateSaved"] = saved,
                    ["stateChanged"] = changed,
                },
            });
            return new WatchStep(trigger, verdict.Nudges.Count, admitted.Count, verdict.NextCheckMs, saved);
        }

        /// Structural equality over the two lists — the records inside are
        /// value types by declaration; the lists are not.
        private static bool Same(NudgeState a, NudgeState b) =>
            ReferenceEquals(a, b)
            || (a.Envelopes.SequenceEqual(b.Envelopes) && a.Nudges.SequenceEqual(b.Nudges));
    }

    /// A file's parse, re-read only when its (mtime, size) moves —
    /// `ReloadingPolicy`'s idiom for the two documents the watcher reads
    /// (`watch.json`, `handlers.json`), so a malformed file warns once per
    /// change rather than once per evaluation (`WatchResolution.Effective`'s
    /// own instruction). Null path ⇒ the loader runs once with null and never
    /// again. Single-threaded by construction: only the actor reads it.
    private sealed class StatGated<T>
    {
        private readonly string? _path;
        private readonly Func<string?, T> _load;
        private string? _stamp;
        private T _value = default!;

        public StatGated(string? path, Func<string?, T> load)
        {
            _path = path;
            _load = load;
        }

        public T Current
        {
            get
            {
                var s = Stamp(_path);
                if (s != _stamp)
                {
                    _value = _load(_path);
                    _stamp = s;
                }
                return _value;
            }
        }

        private static string Stamp(string? path)
        {
            if (path is null) return "<null>";
            if (Directory.Exists(path)) return "<dir>";
            var fi = new FileInfo(path);
            return fi.Exists ? $"{fi.LastWriteTimeUtc.Ticks}|{fi.Length}" : "<absent>";
        }
    }
}
