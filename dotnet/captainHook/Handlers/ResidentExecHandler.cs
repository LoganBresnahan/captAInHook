using System.Diagnostics;
using System.Text;
using CaptainHook.Actors;
using CaptainHook.Core;

namespace CaptainHook.Handlers;

/// A resident child's lifecycle state — pharos's LspState shape (ADR-0010
/// d8): Spawning → Ready on the handshake, → Failed on readiness timeout /
/// early exit / spawn failure / protocol failure; Ready → Failed on a
/// spontaneous exit. Each transition happens exactly once.
public enum ChildState
{
    Spawning = 0,
    Ready = 1,
    Failed = 2,
}

/// ADR-0010 decision 3: the RESIDENT payload — the daemon holds the child
/// warm, spoken to over lock-step JSONL (one envelope line in, one answer
/// line out; the answer MUST echo dispatchId, a mismatch or missing echo is
/// a protocol failure ⇒ kill + counted restart + fail mode). One instance
/// per (event, entry) registration; each instance owns ONE child — the
/// pharos one-worker-owns-one-child model (an entry on N events runs N
/// independent children; state-sharing services externalize their state).
///
/// LIFECYCLE, panel-hardened:
///   * Construction is PASSIVE (fields only) — the spawn happens in Start,
///     called by the Dispatcher's factory closure only after teardown-seam
///     ADMISSION (a post-drain restart never spawns) and sequenced behind
///     the evicted predecessor's kill (exclusive resources release first).
///     Start never throws: a spawn failure is a Failed transition, not a
///     Dispatcher-construction fatality.
///   * Readiness is a three-way race — {"ready":1} line vs readinessTimeoutMs
///     (default 10s) vs early exit — resolved into ONE transition. A first
///     line that is not the handshake (answering before ready included) is a
///     protocol failure.
///   * A dispatch whose budget expires while the child is still Spawning
///     takes the handler's FAIL MODE with a loud trail line (exec.notReady)
///     — no kill, no restart, UNCOUNTED: the child keeps warming toward its
///     own readiness timer (ADR d3; the panel's boot-starvation find — the
///     dispatch token must never race the readiness timer for the kill).
///   * A dispatch that finds the child Failed THROWS (counted crash ⇒
///     supervised restart ⇒ fresh instance ⇒ fresh spawn attempt) — the
///     respawn path, escalation-capped under fast traffic; at slow cadence
///     it degrades to one loud spawn attempt per dispatch, self-healing when
///     the payload is fixed.
///   * Mid-conversation, the child overrunning the budget is killed and the
///     OCE rethrown RAW — an honored cancel, uncounted (ADR-0004 d5
///     carry-in c; chronic slowness stays visible via handler.timeout, not
///     the breaker). Every timeout path replaces the child: the PRIMARY
///     stale-answer defense; the mandatory echo is the backstop.
///   * Protocol failures (malformed answer, missing/mismatched echo) and
///     mid-conversation EOF kill the child and throw — counted, so a
///     chronically out-of-protocol child escalates.
///   * The exit watch caches the exit code (a teardown Dispose must not
///     erase the diagnostic) and record/untrack cleanup happens only at
///     confirmed GROUP death, never mere leader exit (the kill-discipline
///     lesson). Teardown (DisposeAsync — restart eviction, escalation,
///     drain) kills the group TERM→grace→KILL awaited.
public sealed class ResidentExecHandler(
    string name,
    string command,
    IReadOnlyList<string>? args = null,
    FailMode onFailure = FailMode.Open,
    IReadOnlyDictionary<string, string>? env = null,
    IReadOnlyList<string>? passEnv = null,
    string? cwd = null,
    TimeSpan? readinessTimeout = null,
    string eventType = "") : IHandler, IEagerStart, IAsyncDisposable, IResidentObservable
{
    public string Name => name;
    public FailMode OnFailure => onFailure;

    internal static readonly TimeSpan DefaultReadinessTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan KillGrace = TimeSpan.FromSeconds(2);
    /// Bound on awaiting the evicted predecessor before spawning: its kill is
    /// ≤ grace + reap beat by construction; never wait longer than the drain's
    /// child phase would.
    private static readonly TimeSpan PredecessorBound = TimeSpan.FromSeconds(4);
    private const int StderrCapBytes = 8 * 1024;

    private readonly object _lock = new();
    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly StringBuilder _stderr = new();
    private readonly Stopwatch _sinceStart = new();

    private ChildState _state = ChildState.Spawning;
    private string? _failReason;
    private int _cachedExitCode = int.MinValue;
    private bool _started;
    private bool _disposed;
    private Task _startTask = Task.CompletedTask;
    private Process? _proc;
    private int _pid;
    private string? _recordPath;
    private StreamReader? _stdout;

    /// Phase-8 read model surface: live state + pid, plain data.
    public ChildState State { get { lock (_lock) return _state; } }
    public int? ChildPid { get { lock (_lock) return _pid == 0 ? null : _pid; } }

    /// IResidentObservable (Core seam): the wire-shaped projection the
    /// /handlers API reads via Dispatcher.Snapshot. Explicit so the string
    /// member never clashes with the `ChildState` enum type name; `ChildPid`
    /// is already the public property above.
    string IResidentObservable.ChildState => State.ToString().ToLowerInvariant();

    // ---- eager start (IEagerStart — called by the Dispatcher AFTER admission)

    public void Start(Task? predecessor)
    {
        lock (_lock)
        {
            if (_started || _disposed) return;   // idempotent; never after teardown
            _started = true;
            _startTask = Task.Run(() => StartCoreAsync(predecessor));
        }
    }

    /// Never throws (the first factory run happens inside Supervisor.Spawn,
    /// where a throw kills Dispatcher construction — one bad handlers.json
    /// entry must never take the daemon down): every failure is a Failed
    /// transition the first dispatch converts into a counted crash.
    private async Task StartCoreAsync(Task? predecessor)
    {
        try
        {
            if (predecessor is not null)
                try { await predecessor.WaitAsync(PredecessorBound, _lifetime.Token); }
                catch (TimeoutException) { /* spawn anyway — the drain awaits the predecessor itself */ }
                catch (OperationCanceledException) { return; }   // torn down BEFORE spawning: nothing to kill

            // Teardown can land during the predecessor wait: never spawn a
            // child no seam will track (the doomed path below only covers
            // the Process.Start window itself).
            lock (_lock) if (_disposed) return;

            var psi = ExecHandler.BuildPsi(command, args, env, passEnv);
            // Cwd rungs: config → runtime home. The event rung is
            // inapplicable — a resident child spawns BEFORE any event exists.
            var (resolvedCwd, cwdFallback) = ResolveCwd();
            if (resolvedCwd is not null) psi.WorkingDirectory = resolvedCwd;

            _sinceStart.Start();
            var proc = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
            var pid = proc.Id;

            var doomed = false;
            lock (_lock)
            {
                if (_disposed) doomed = true;   // teardown raced the spawn
                else
                {
                    _proc = proc;
                    _pid = pid;
                    _stdout = proc.StandardOutput;
                }
            }
            if (doomed)
            {
                await ProcessGroup.TermThenKillAsync(pid, ExecHandler.Pgrouped, proc, KillGrace);
                proc.Dispose();
                TryFail("torn down during spawn", killGroup: false);
                return;
            }

            _recordPath = ChildRecords.Write(pid, command, name, eventType);

            var spawnData = new Dictionary<string, object>
            {
                ["handler"] = name, ["pid"] = pid, ["command"] = command,
                ["mode"] = "resident", ["event"] = eventType,
            };
            if (!ExecHandler.Pgrouped) spawnData["pgroup"] = false;
            if (resolvedCwd is not null) spawnData["cwd"] = resolvedCwd;
            if (cwdFallback) spawnData["cwdFallback"] = true;
            Log.Info("exec", "exec.spawn", Fields(data: spawnData));

            // Watchers last, on fully-published state; each body is
            // exception-fenced into the Failed path (a watcher bug degrades
            // to the counted-crash road, never an unobserved fault).
            _ = Fenced(DrainStderrAsync(proc), "stderr watch");
            _ = Fenced(WatchExitAsync(proc, pid), "exit watch");
            _ = Fenced(WatchReadinessAsync(proc, pid), "readiness watch");
        }
        catch (Exception ex)
        {
            TryFail($"spawn failed: {ex.Message}", killGroup: false);
        }
    }

    private async Task Fenced(Task body, string what)
    {
        try { await body; }
        catch (Exception ex)
        {
            TryFail($"{what} faulted: {ex.Message}", killGroup: true);
        }
    }

    // ---- the three-way readiness race -----------------------------------

    private async Task WatchReadinessAsync(Process proc, int pid)
    {
        var timeout = readinessTimeout ?? DefaultReadinessTimeout;
        var read = ExecHandler.FirstNonEmptyLineAsync(proc.StandardOutput);
        ExecHandler.Observe(read);   // a timeout abandons it; the kill resolves it
        string? line;
        try
        {
            line = await read.WaitAsync(timeout, _lifetime.Token);
        }
        catch (TimeoutException)
        {
            if (TryFail($"no ready handshake within {timeout.TotalMilliseconds:F0}ms", killGroup: true))
                Log.Warn("exec", "exec.protocolError", Fields(
                    msg: $"readiness timeout: no {{\"ready\":1}} within {timeout.TotalMilliseconds:F0}ms",
                    data: new Dictionary<string, object> { ["handler"] = name, ["pid"] = pid }));
            return;
        }
        catch (OperationCanceledException) { return; }   // teardown — nothing to decide

        if (line is null) return;   // EOF: the exit watch owns that story

        if (ExecWire.TryParseReady(line))
        {
            if (TryReady())
                Log.Info("exec", "exec.ready", Fields(_sinceStart.Elapsed.TotalMilliseconds,
                    data: new Dictionary<string, object> { ["handler"] = name, ["pid"] = pid }));
            return;
        }

        // First line was not the handshake — answering (or chattering)
        // before ready is out of protocol (ADR d3 / pharos ADR-024: readiness
        // must be demonstrated before anything else is believed).
        if (TryFail("first line was not the ready handshake", killGroup: true))
            Log.Error("exec", "exec.protocolError", Fields(
                msg: $"first line was not the ready handshake — expected {{\"ready\":1}}, got: {Truncate(line, 200)}",
                data: new Dictionary<string, object> { ["handler"] = name, ["pid"] = pid }));
    }

    private async Task WatchExitAsync(Process proc, int pid)
    {
        try { await proc.WaitForExitAsync(_lifetime.Token); }
        catch (OperationCanceledException)
        {
            // Teardown owns a LIVE child's story — but if the child had
            // already exited when the cancel won the race, falling out here
            // would silence the exit diagnostics forever (no exec.exit, no
            // stderr flush, no cached code for a concurrent EOF throw).
            bool exited;
            try { exited = proc.HasExited; } catch (Exception) { exited = false; }
            if (!exited) return;
        }

        int code;
        try { code = proc.ExitCode; } catch (Exception) { code = int.MinValue; }
        bool deliberate;
        lock (_lock)
        {
            _cachedExitCode = code;
            deliberate = _disposed;
        }

        // Let the pipes settle, then flush the diagnostics once.
        await ExecHandler.BoundedAsync(Task.Delay(50, CancellationToken.None), TimeSpan.FromMilliseconds(100));
        Log.Info("exec", "exec.exit", Fields(_sinceStart.Elapsed.TotalMilliseconds,
            data: new Dictionary<string, object> { ["handler"] = name, ["pid"] = pid, ["code"] = code }));
        lock (_stderr)
            if (_stderr.Length > 0)
                Log.Info("exec", "exec.stderr", Fields(
                    msg: Truncate(_stderr.ToString(), 2048),
                    data: new Dictionary<string, object> { ["handler"] = name, ["pid"] = pid }));

        if (deliberate) return;   // DisposeAsync owns kill + cleanup

        TryFail($"child exited {code}", killGroup: false);
        // Confirmed-GROUP-death cleanup — never mere leader exit: survivors
        // die with the group (a resident's spontaneous exit is a failure,
        // always a kill path), and only then does the record go.
        await ProcessGroup.TermThenKillAsync(pid, ExecHandler.Pgrouped, proc, KillGrace);
        ChildRecords.Delete(_recordPath);
    }

    private async Task DrainStderrAsync(Process proc)
    {
        try
        {
            var buf = new char[4096];
            int n;
            while ((n = await proc.StandardError.ReadAsync(buf, 0, buf.Length)) > 0)
                lock (_stderr)
                {
                    if (_stderr.Length < StderrCapBytes)
                        _stderr.Append(buf, 0, Math.Min(n, StderrCapBytes - _stderr.Length));
                }
        }
        catch (Exception) { /* pipe torn by a kill — the buffer holds what arrived */ }
    }

    // ---- transitions (each edge exactly once) ----------------------------

    private bool TryReady()
    {
        lock (_lock)
        {
            if (_state != ChildState.Spawning) return false;
            _state = ChildState.Ready;
        }
        _gate.TrySetResult();
        return true;
    }

    /// Failed wins from Spawning OR Ready, once. The winner (and only the
    /// winner) kills the group when asked — transition first, kill second,
    /// so a raced ready line can never be murdered by a losing timer.
    private bool TryFail(string reason, bool killGroup)
    {
        Process? proc;
        int pid;
        lock (_lock)
        {
            if (_state == ChildState.Failed) return false;
            _state = ChildState.Failed;
            _failReason = reason;
            proc = _proc;
            pid = _pid;
        }
        _gate.TrySetResult();
        if (killGroup && proc is not null && pid != 0)
            _ = Task.Run(async () =>
            {
                var how = await ProcessGroup.TermThenKillAsync(pid, ExecHandler.Pgrouped, proc, KillGrace);
                if (how != "gone")
                    Log.Warn("exec", "exec.kill", Fields(msg: reason,
                        data: new Dictionary<string, object> { ["handler"] = name, ["pid"] = pid, ["how"] = how }));
                ChildRecords.Delete(_recordPath);
            });
        return true;
    }

    // ---- dispatch: the lock-step conversation ----------------------------

    public async Task<Effect> HandleAsync(HookEvent e, HandlerContext ctx)
    {
        // Expired-in-queue guard (verify-round HIGH): a Backlogged ask stays
        // in the worker mailbox after its asker gave up, and eventually runs
        // HERE with an already-cancelled token. It must never touch the warm
        // child (the kill paths are for dispatches the child actually
        // overran) — and it must RETURN, not throw: any throw crashes the
        // mailbox and the restart eviction would kill the child anyway.
        if (ctx.Ct.IsCancellationRequested)
            return FailEffect("dispatch expired before reaching the handler");

        lock (_lock)
        {
            if (_disposed)
                throw new ObjectDisposedException(null, $"resident handler '{name}': torn down");
            // Safety net for hand-registered instances that bypassed the
            // Dispatcher's admission call — normal registration has started
            // long before any dispatch.
            if (!_started) Start(null);
        }

        // The ready gate rides the dispatch's own budget token. Not ready in
        // time is NOT a fault: fail mode + loud, uncounted — the child keeps
        // warming toward its own readiness timer (the panel's boot-starvation
        // find: the dispatch token must never kill a booting child).
        try
        {
            await _gate.Task.WaitAsync(ctx.Ct);
        }
        catch (OperationCanceledException)
        {
            Log.Warn("exec", "exec.notReady", F(e, ctx,
                msg: $"budget expired after {_sinceStart.Elapsed.TotalMilliseconds:F0}ms with the child still warming — taking fail-{Mode()}",
                data: new Dictionary<string, object> { ["handler"] = name }));
            return FailEffect("resident child not ready");
        }

        Process proc;
        StreamReader stdout;
        lock (_lock)
        {
            if (_state == ChildState.Failed)
                // Counted crash ⇒ supervised restart ⇒ fresh instance ⇒ fresh
                // spawn: the respawn path, loud and escalation-capped.
                throw new InvalidOperationException(
                    $"resident handler '{name}': child failed — {_failReason}{StderrTail()}");
            proc = _proc!;
            stdout = _stdout!;
        }

        var sw = Stopwatch.StartNew();
        var sentId = ctx.DispatchId ?? "";

        try
        {
            // The Memory overload carries the token (the char/string ones do
            // not): a child that stops READING stdin while a
            // bigger-than-pipe-buffer envelope is in flight must still honor
            // ctx.Ct — a wedged write would otherwise ride to the ask
            // window's Wedged classification with no kill.
            await proc.StandardInput.WriteAsync(
                (ExecWire.Envelope(e, sentId) + "\n").AsMemory(), ctx.Ct);
            await proc.StandardInput.FlushAsync(ctx.Ct);
        }
        catch (OperationCanceledException)
        {
            KillConversation(proc, e, ctx, "budget cancelled mid-envelope");
            throw;
        }
        catch (Exception ex)
        {
            // EPIPE with the child DEAD: fall through to the read, which
            // sees EOF and throws with the cached exit diagnostics. EPIPE
            // with the child ALIVE means it closed its own stdin — out of
            // protocol; burning the budget waiting for an answer it was
            // never asked would launder that into an uncounted cancel.
            bool alive;
            try { alive = !proc.HasExited; } catch (Exception) { alive = false; }
            if (alive)
                return ProtocolError(proc, e, ctx, sw,
                    $"envelope write failed with the child still alive ({ex.Message})");
        }

        var answerRead = ExecHandler.FirstNonEmptyLineAsync(stdout);
        ExecHandler.Observe(answerRead);   // a cancel abandons it; the kill resolves it
        string? line;
        try
        {
            line = await answerRead.WaitAsync(ctx.Ct);
        }
        catch (OperationCanceledException)
        {
            // The child overran the budget mid-conversation: kill it (every
            // timeout path replaces the child — the primary stale-answer
            // defense) and rethrow RAW — an honored cancel, uncounted
            // (ADR-0004 d5 carry-in c); the supervised restart still yields
            // a fresh instance and child.
            KillConversation(proc, e, ctx, "budget overrun mid-conversation");
            throw;
        }

        if (line is null)
        {
            int code;
            lock (_lock) code = _cachedExitCode;
            throw new InvalidOperationException(
                $"resident handler '{name}': child died mid-conversation" +
                $"{(code == int.MinValue ? "" : $" (exit {code})")}{StderrTail()}");
        }

        switch (ExecWire.ParseAnswer(line))
        {
            case ExecAnswer.Ok(var effect, var echo) when echo == sentId:
                Log.Info("exec", "exec.answered", F(e, ctx, sw.Elapsed.TotalMilliseconds,
                    data: new Dictionary<string, object>
                    {
                        ["handler"] = name, ["effect"] = effect.GetType().Name, ["mode"] = "resident",
                    }));
                return effect;

            case ExecAnswer.Ok(_, var echo):
                // Missing OR mismatched echo — the resident wire REQUIRES it
                // (ADR d3): kill + counted, never guess whose answer this was.
                return ProtocolError(proc, e, ctx, sw, echo is null
                    ? "answer missing the mandatory dispatchId echo"
                    : $"dispatchId echo mismatch: sent '{sentId}', answered '{echo}'");

            case ExecAnswer.Malformed(var violations):
                return ProtocolError(proc, e, ctx, sw,
                    $"answer violates the wire grammar: {string.Join("; ", violations)}");

            default:
                return ProtocolError(proc, e, ctx, sw, "empty answer line");
        }
    }

    private Effect ProtocolError(Process proc, HookEvent e, HandlerContext ctx, Stopwatch sw, string why)
    {
        Log.Error("exec", "exec.protocolError", F(e, ctx, sw.Elapsed.TotalMilliseconds,
            msg: why, data: new Dictionary<string, object> { ["handler"] = name }));
        KillConversation(proc, e, ctx, "protocol error");
        throw new InvalidOperationException($"resident handler '{name}': {why}{StderrTail()}");
    }

    /// Dispatch-path kill: transition to Failed first (the losing watchers
    /// stand down), TERM the group synchronously, escalate detached — the
    /// child stays visible to DisposeAsync until its kill concludes (the
    /// chunk-A drain-orphan lesson: a drain must be able to await it).
    private void KillConversation(Process proc, HookEvent e, HandlerContext ctx, string why)
    {
        TryFail(why, killGroup: false);
        int pid;
        lock (_lock) pid = _pid;
        if (pid == 0 || !ProcessGroup.IsAlive(pid, ExecHandler.Pgrouped, proc)) return;
        ProcessGroup.Term(pid, ExecHandler.Pgrouped, proc);
        Log.Warn("exec", "exec.kill", F(e, ctx, msg: why,
            data: new Dictionary<string, object> { ["handler"] = name, ["pid"] = pid }));
        _ = Task.Run(async () =>
        {
            var how = await ProcessGroup.TermThenKillAsync(pid, ExecHandler.Pgrouped, proc, KillGrace, termSent: true);
            if (how == "kill")
                Log.Warn("exec", "exec.kill", F(e, ctx,
                    msg: $"{why} — SIGKILL after {KillGrace.TotalSeconds:F0}s grace",
                    data: new Dictionary<string, object> { ["handler"] = name, ["pid"] = pid, ["how"] = how }));
            ChildRecords.Delete(_recordPath);
        });
    }

    // ---- teardown (the chunk-A seam) --------------------------------------

    public async ValueTask DisposeAsync()
    {
        Task startTask;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            startTask = _startTask;
        }
        // Resolve the ready gate: a dispatch parked there during a drain
        // must wake NOW into the Failed throw (its fail mode) instead of
        // silently burning its whole budget against a child being killed
        // under it (verify-round find).
        TryFail("torn down", killGroup: false);
        // Watchers conclude in ms (the readiness timer must never hold the
        // drain's 6s child-phase bound hostage), and any in-flight spawn
        // aborts pre-Start or commits before we snapshot (the lifetime token
        // cuts the predecessor wait short, so this bound only ever covers
        // the Process.Start window + the doomed self-kill).
        _lifetime.Cancel();
        await ExecHandler.BoundedAsync(startTask, PredecessorBound + TimeSpan.FromSeconds(1));

        Process? proc;
        int pid;
        lock (_lock)
        {
            proc = _proc;
            pid = _pid;
        }
        if (proc is null || pid == 0) return;   // never spawned (or spawn self-killed)

        var how = await ProcessGroup.TermThenKillAsync(pid, ExecHandler.Pgrouped, proc, KillGrace);
        if (how != "gone")
            Log.Warn("exec", "exec.kill", Fields(msg: "teardown",
                data: new Dictionary<string, object> { ["handler"] = name, ["pid"] = pid, ["how"] = how }));
        ChildRecords.Delete(_recordPath);
        proc.Dispose();
    }

    // ---- small parts -------------------------------------------------------

    private Effect FailEffect(string reason) => onFailure == FailMode.Closed
        ? new Effect.Decide(Verdict.Deny, $"{name}: {reason}")
        : new Effect.Noop();

    private string Mode() => onFailure == FailMode.Closed ? "closed" : "open";

    /// Cwd rungs for a pre-event spawn: configured cwd if it exists, else the
    /// runtime home. (The event rung of the oneshot ladder needs an event.)
    private (string? Cwd, bool Fallback) ResolveCwd()
    {
        if (cwd is not null && Directory.Exists(cwd)) return (cwd, false);
        var fellBack = cwd is not null;
        var home = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".captainHook");
        return (Directory.Exists(home) ? home : null, fellBack);
    }

    private string StderrTail()
    {
        lock (_stderr)
            return _stderr.Length == 0 ? "" : $" — stderr: {Truncate(_stderr.ToString().Trim(), 512)}";
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    /// Spawn-side trail lines carry no dispatch (they happen between hooks).
    private static LogFields Fields(double? durMs = null, string? msg = null,
                                    IDictionary<string, object>? data = null) =>
        new() { DurMs = durMs, Msg = msg, Data = data };

    private static LogFields F(HookEvent e, HandlerContext ctx, double? durMs = null,
                               string? msg = null, IDictionary<string, object>? data = null) =>
        new()
        {
            DispatchId = ctx.DispatchId,
            SessionId = e.SessionId,
            HookEvent = e.Type,
            DurMs = durMs,
            Msg = msg,
            Data = data,
        };
}
