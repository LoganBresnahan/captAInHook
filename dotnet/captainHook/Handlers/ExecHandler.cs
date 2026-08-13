using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using CaptainHook.Actors;
using CaptainHook.Core;

namespace CaptainHook.Handlers;

/// ADR-0010 decisions 1/2/5/6: the ONE coded handler that makes the closed
/// set extensible in user space — a configured command run as the payload,
/// oneshot mode (spawn per dispatch). The envelope goes out on the child's
/// stdin (then stdin closes — one request, oneshot); the answer is the FIRST
/// non-empty stdout LINE, strict-parsed by ExecWire against the closed
/// grammar. Semantics:
///
///   * answer parsed        ⇒ the effect counts the moment it parses; the
///                            child may REPLY-THEN-LINGER — its remaining
///                            lifetime is its own, observed by an async
///                            reaper (which keeps draining pipes so a chatty
///                            lingerer can never wedge on a full buffer),
///                            never awaited by the dispatch. Post-answer
///                            stdout is linger chatter: drained, discarded —
///                            an already-returned effect cannot be failed
///                            retroactively (d2's temporal resolution;
///                            strictness binds up to and including the
///                            answer line).
///   * EOF, no answer, exit 0 ⇒ Noop (the "nothing to say" case, decided).
///   * EOF, no answer, exit≠0 ⇒ handler failure ⇒ the fail mode (supervision
///                            free-rides: crash/timeout mapping is the
///                            dispatcher's existing machinery).
///   * garbage / trailing content on the answer line ⇒ protocol error ⇒
///                            child killed, handler failure ⇒ fail mode.
///   * budget cancellation  ⇒ child killed — SIGTERM to its process group
///                            now, grace → SIGKILL escalated OFF the
///                            dispatch path (ProcessGroup) — and
///                            OperationCanceledException rethrown so the
///                            classified ask counts it as an honored
///                            cancel. `exec.kill` in the trail (a second
///                            line with how=kill when TERM was ignored).
///
/// Grandchildren decouple pipe-EOF from child-exit (a backgrounded process
/// inherits the fds), so exit and answer are raced and every post-exit pipe
/// join is bounded by PipeGrace — a `sleep 30 & exit 0` child yields its
/// decided Noop immediately, and the grandchild dies with the child's
/// process group (the setsid spawn — ProcessGroup). WHO the group belongs
/// to follows one rule: KILL paths (budget, protocol error, teardown) take
/// the whole group, leader dead or not; a GRACEFUL conclusion (answered, or
/// EOF + exit 0) releases it when the pipes close — a payload that
/// deliberately daemonizes (redirects its fds, exits 0) has left the
/// dispatch and its survivors are its own business, while one still holding
/// the answer channel is attached and dies at teardown. A payload that
/// deliberately daemonizes (`sleep 30 & exit 0` — untracked synchronously at
/// dispatch via the EOF+exit0 path) legitimately leaves its grandchild
/// running in EITHER mode; that is its own business, not an orphan.
///
/// TEARDOWN (kill-discipline slice): the handler owns its children's
/// lifetime end-to-end — every spawn is tracked live, and DisposeAsync
/// (invoked by the Dispatcher's teardown seam on supervised replacement,
/// escalation, daemon drain, AND the collapsed run's own drain — phase 6)
/// kills whatever is still running TERM→grace→KILL and refuses new spawns. A
/// child must never outlive its handler generation, and no TRACKED child
/// survives its run: `CollapsedAsync` awaits `DisposeHandlersAsync` in a
/// finally after the answer, so a collapsed run's kill escalation lands
/// under the same bound the daemon drain gives it (the old TERM-only
/// collapsed hole is closed for tracked children).
///
/// The child environment is STRIPPED from day one (decision 5): never
/// inherited — a fixed allowlist only. Config-driven env{}/passEnv[] arrive
/// with handlers.json; until then nothing else crosses, because the daemon's
/// environment is the user's shell environment at first-hook time, secrets
/// included. Cwd: the event's cwd when it exists, else the runtime home
/// (decision 4's default).
public sealed class ExecHandler(
    string name,
    string command,
    IReadOnlyList<string>? args = null,
    FailMode onFailure = FailMode.Open,
    IReadOnlyDictionary<string, string>? env = null,
    IReadOnlyList<string>? passEnv = null,
    string? cwd = null) : IHandler, IAsyncDisposable
{
    public string Name => name;
    public FailMode OnFailure => onFailure;

    /// Whether spawns get their own process group (a spawn prefix resolved —
    /// co-located bosun, else setsid from PATH) — decided once per process;
    /// the degraded case is flagged per-spawn. Internal: shared with
    /// ResidentExecHandler (one spawn discipline).
    internal static readonly bool Pgrouped = ProcessGroup.Prefix.Pgroup;

    /// SIGTERM's window to work before SIGKILL. Small enough to fit inside
    /// the daemon drain's child phase; large enough for a signal-handling
    /// payload to flush and exit.
    private static readonly TimeSpan KillGrace = TimeSpan.FromSeconds(2);

    // Every child this INSTANCE has spawned and not yet seen off (value =
    // pid, cached because Process.Id is unreadable after Dispose). Lock-
    // guarded: the reaper untracks concurrently with new dispatch spawns,
    // and DisposeAsync snapshots against both.
    private readonly object _liveLock = new();
    private readonly Dictionary<Process, int> _liveChildren = new();
    private bool _disposed;

    /// Decision 5's fixed allowlist (exact names; LC_* by prefix). Explicit
    /// per-entry env/passEnv are handlers.json fields (phase 4) — this static
    /// core is deliberately baked into the FIRST spawn site so no build ever
    /// ships an inherit-everything window.
    private static readonly HashSet<string> AllowedEnv = new(StringComparer.Ordinal)
        {
            "PATH", "HOME", "USER", "SHELL", "LANG", "TZ", "TMPDIR",
            // The engine's own config PATHS cross too (they are ours, and
            // they are paths, never secrets): an engine-as-payload child —
            // `mail digest` (ADR-0016 d7) — that resolved the DEFAULT mail
            // dir or harness dir while its daemon ran a redirected one would
            // read the wrong mailbox, write cursors into the live tree, or
            // plan against a spec view the daemon-side capability gate does
            // not hold (advance-then-swallow = silent mail loss; the
            // mail-digest skeptic pass, finding 5). Same for the trail path:
            // a sandboxed daemon's children must not log into the live tree.
            "CAPTAINHOOK_MAIL_DIR", "CAPTAINHOOK_HARNESS_DIR", "CAPTAINHOOK_LOG",
        };

    private const int StderrCapBytes = 8 * 1024;

    /// The bound on every post-exit pipe join: whatever the CHILD wrote is
    /// already buffered and arrives in single-digit ms; anything slower means
    /// a grandchild is holding the fd, and the decided outcome must not wait
    /// for it.
    private static readonly TimeSpan PipeGrace = TimeSpan.FromMilliseconds(250);

    /// Await `task` for at most `grace`; a timeout yields default — the
    /// caller proceeds with what it has (partial stderr, no answer line).
    /// Internal: ResidentExecHandler shares these exact semantics.
    internal static async Task<T?> BoundedAsync<T>(Task<T> task, TimeSpan grace)
    {
        try { return await task.WaitAsync(grace); }
        catch (TimeoutException) { return default; }
    }

    internal static async Task BoundedAsync(Task task, TimeSpan grace)
    {
        try { await task.WaitAsync(grace); }
        catch (TimeoutException) { }
    }

    /// Observe an abandoned task's eventual fault so it can never surface as
    /// an unobserved-task exception (the ask layer's ContinueWith idiom) — a
    /// read left behind by a WaitAsync timeout can still fault long after,
    /// against a since-disposed Process.
    internal static void Observe(Task t) =>
        t.ContinueWith(x => _ = x.Exception, TaskContinuationOptions.OnlyOnFaulted);

    public async Task<Effect> HandleAsync(HookEvent e, HandlerContext ctx)
    {
        lock (_liveLock)
            if (_disposed)
                throw new ObjectDisposedException(null, $"exec handler '{name}': torn down");

        var sw = Stopwatch.StartNew();
        var psi = BuildPsi(command, args, env, passEnv);
        var (resolvedCwd, cwdFallback) = ResolveCwd(e.Cwd);
        if (resolvedCwd is not null) psi.WorkingDirectory = resolvedCwd;

        Process proc;
        try
        {
            proc = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
        }
        catch (Win32Exception ex)
        {
            // The BinaryNotFound nicety (pharos ADR-018): a clean message
            // naming the command instead of a cryptic spawn crash.
            throw new InvalidOperationException(
                $"exec handler '{name}': cannot start '{command}' — {ex.Message}");
        }
        Track(proc, e, ctx);

        var spawnData = new Dictionary<string, object>
        {
            ["handler"] = name, ["pid"] = proc.Id, ["command"] = command, ["mode"] = "oneshot",
            ["spawner"] = ProcessGroup.Prefix.Name,   // ADR-0014 d2: which rung won
        };
        if (!Pgrouped) spawnData["pgroup"] = false;   // no wrapper — tree-walk kills only
        if (resolvedCwd is not null) spawnData["cwd"] = resolvedCwd;
        if (cwdFallback) spawnData["cwdFallback"] = true;   // configured cwd missing — fell through
        Log.Info("exec", "exec.spawn", F(e, ctx, data: spawnData));

        // All three pipes serviced CONCURRENTLY from the start — the classic
        // full-pipe-buffer deadlock (child blocked writing stderr while we
        // block reading stdout, or child never reads the stdin we block
        // writing) is structurally impossible.
        var stderrBuf = new StringBuilder();
        // The echo comparison target is the LITERAL id written into this
        // envelope — captured here, never re-derived — so null-vs-"" drift
        // between the context and the wire is structurally impossible.
        var sentId = ctx.DispatchId ?? "";
        var stderrTask = DrainStderrAsync(proc, stderrBuf);
        var stdinTask = WriteEnvelopeAsync(proc, e, sentId);
        var answerTask = FirstNonEmptyLineAsync(proc.StandardOutput);
        var exitTask = proc.WaitForExitAsync(CancellationToken.None);

        // Answer vs exit is a RACE, not a sequence: pipe-EOF and child-exit
        // decouple whenever the child backgrounds a grandchild that inherits
        // its fds (`sleep 30 & exit 0` — the everyday start-a-daemon idiom).
        // Waiting on the pipes alone would ride the GRANDCHILD's lifetime —
        // the decided-Noop case burning the budget as a counted wedge (caught
        // by this slice's adversarial verify). So: whoever finishes first
        // decides the shape, and every pipe join after exit is BOUNDED —
        // anything the child itself wrote is already in the pipe buffers.
        string? line;
        try
        {
            var winner = await Task.WhenAny(answerTask, exitTask).WaitAsync(ctx.Ct);
            if (winner == answerTask)
                line = await answerTask;
            else
                // Exited before answering — give the already-buffered stdout
                // one short, bounded chance to yield the answer, then treat a
                // held-open pipe (grandchild) as no-answer.
                line = await BoundedAsync(answerTask, PipeGrace);
        }
        catch (OperationCanceledException)
        {
            // Budget expired: honor the token — kill the child (best-effort
            // tree walk; setpgid rigor is the kill-discipline slice) and let
            // the OCE classify as an honored cancel.
            KillQuietly(proc, e, ctx, "budget cancelled");
            throw;
        }

        if (line is null)
        {
            // EOF (or exit with stdout held by a grandchild) before any
            // answer: the exit code decides. The exit wait rides the budget
            // token; the stderr join is bounded — a grandchild can hold that
            // pipe open for 30 days and the decided outcome must not care.
            try
            {
                await exitTask.WaitAsync(ctx.Ct);
            }
            catch (OperationCanceledException)
            {
                KillQuietly(proc, e, ctx, "budget cancelled awaiting exit");
                throw;
            }
            await BoundedAsync(stderrTask, PipeGrace);
            var code = proc.ExitCode;
            Log.Info("exec", "exec.exit", F(e, ctx, sw.Elapsed.TotalMilliseconds,
                data: new Dictionary<string, object> { ["handler"] = name, ["code"] = code }));
            Untrack(proc);
            proc.Dispose();
            if (code == 0)
            {
                // Exit 0 + empty stdout ⇒ Noop — decided, not guessed (d2).
                Log.Info("exec", "exec.answered", F(e, ctx, sw.Elapsed.TotalMilliseconds,
                    data: new Dictionary<string, object> { ["handler"] = name, ["effect"] = "Noop", ["empty"] = true }));
                return new Effect.Noop();
            }
            throw new InvalidOperationException(
                $"exec handler '{name}': exited {code} before answering{StderrTail(stderrBuf)}");
        }

        var answer = ExecWire.ParseAnswer(line);
        switch (answer)
        {
            // Oneshot echo rule: OPTIONAL, must match when present. There is
            // no second conversation to mis-attribute, so absence is fine —
            // but a child that echoes a DIFFERENT id than the envelope it
            // just read is confused, and confusion is a protocol error.
            case ExecAnswer.Ok(_, var echo) when echo is not null && echo != sentId:
                Log.Error("exec", "exec.protocolError", F(e, ctx, sw.Elapsed.TotalMilliseconds,
                    msg: $"dispatchId echo mismatch: sent '{sentId}', answered '{echo}'",
                    data: new Dictionary<string, object> { ["handler"] = name }));
                KillQuietly(proc, e, ctx, "dispatchId echo mismatch");
                // Same stderr-join parity as the Malformed arm: a confused
                // child likely says WHY on stderr — carry it, never torn.
                await BoundedAsync(stderrTask, PipeGrace);
                throw new InvalidOperationException(
                    $"exec handler '{name}': answer echoed dispatchId '{echo}' for envelope '{sentId}'{StderrTail(stderrBuf)}");

            case ExecAnswer.Ok(var effect, _):
                Log.Info("exec", "exec.answered", F(e, ctx, sw.Elapsed.TotalMilliseconds,
                    data: new Dictionary<string, object>
                    {
                        ["handler"] = name, ["effect"] = effect.GetType().Name,
                    }));
                // Reply-then-linger: the effect returns NOW; the reaper owns
                // the child's afterlife (drain, exit code, disposal) and can
                // never fail the dispatch.
                _ = ReapAsync(proc, e, ctx, sw, stderrTask, stderrBuf);
                return effect;

            case ExecAnswer.Malformed(var violations):
                Log.Error("exec", "exec.protocolError", F(e, ctx, sw.Elapsed.TotalMilliseconds,
                    msg: string.Join("; ", violations),
                    data: new Dictionary<string, object> { ["handler"] = name }));
                KillQuietly(proc, e, ctx, "protocol error");
                // The kill tears the stderr pipe, so the drain lands promptly
                // — bounded anyway, and joined BEFORE reading the buffer so
                // the tail in the thrown message is never a torn read.
                await BoundedAsync(stderrTask, PipeGrace);
                throw new InvalidOperationException(
                    $"exec handler '{name}': answer violates the wire grammar: {string.Join("; ", violations)}{StderrTail(stderrBuf)}");

            default:   // ExecAnswer.Empty is unreachable off a non-empty line; strict anyway.
                KillQuietly(proc, e, ctx, "empty answer line");
                throw new InvalidOperationException($"exec handler '{name}': empty answer line");
        }
    }

    /// The child's afterlife, observed but never awaited by a dispatch:
    /// drain remaining stdout (linger chatter — discarded, a returned effect
    /// cannot be failed retroactively), collect stderr, record the exit.
    private async Task ReapAsync(Process proc, HookEvent e, HandlerContext ctx,
                                 Stopwatch sw, Task stderrTask, StringBuilder stderrBuf)
    {
        try
        {
            var drain = Task.Run(async () =>
            {
                var buf = new char[4096];
                while (await proc.StandardOutput.ReadAsync(buf, 0, buf.Length) > 0) { }
            });
            await proc.WaitForExitAsync(CancellationToken.None);
            // Cache the code BEFORE the pipe drains: a teardown kill can
            // Dispose(proc) while a grandchild still holds the pipes, and a
            // post-Dispose ExitCode read throws — losing the real exit line.
            var code = proc.ExitCode;
            await drain;
            await stderrTask;
            Log.Info("exec", "exec.exit", F(e, ctx, sw.Elapsed.TotalMilliseconds,
                data: new Dictionary<string, object> { ["handler"] = name, ["code"] = code }));
            if (stderrBuf.Length > 0)
                Log.Info("exec", "exec.stderr", F(e, ctx,
                    msg: Truncate(stderrBuf.ToString(), 2048),
                    data: new Dictionary<string, object> { ["handler"] = name }));
        }
        catch (Exception ex)
        {
            // The reaper is observation, not lifecycle: never let it throw
            // unobserved, never let it matter.
            Log.Warn("exec", "exec.exit", F(e, ctx, msg: $"reaper: {ex.Message}",
                data: new Dictionary<string, object> { ["handler"] = name }));
        }
        finally
        {
            // Untrack BEFORE Dispose so a concurrent DisposeAsync snapshot
            // can never race a disposed Process (its cached pid would still
            // be safe, but why hand it one).
            Untrack(proc);
            proc.Dispose();
        }
    }

    /// Track a spawned child for teardown. Rechecked under the lock because
    /// DisposeAsync can interleave between HandleAsync's entry guard and
    /// Process.Start: a child spawned into a torn-down handler is killed on
    /// the spot — teardown means NO survivors, not "none we noticed".
    private void Track(Process proc, HookEvent e, HandlerContext ctx)
    {
        var pid = proc.Id;
        lock (_liveLock)
        {
            if (!_disposed)
            {
                _liveChildren[proc] = pid;
                return;
            }
        }
        KillQuietly(proc, e, ctx, "spawned into torn-down handler");
        throw new ObjectDisposedException(null, $"exec handler '{name}': torn down");
    }

    private void Untrack(Process proc)
    {
        lock (_liveLock) _liveChildren.Remove(proc);
    }

    /// Teardown — the Dispatcher's seam (supervised replacement, escalation,
    /// drain): kill every live child TERM→grace→KILL as a group, refuse new
    /// spawns, and don't return until the kills have concluded (the drain's
    /// child phase awaits this; the restart/escalation paths fire-and-forget
    /// it, so the supervisor's fault mailbox never blocks on a grace window).
    public async ValueTask DisposeAsync()
    {
        KeyValuePair<Process, int>[] live;
        lock (_liveLock)
        {
            if (_disposed) return;
            _disposed = true;
            live = _liveChildren.ToArray();
            _liveChildren.Clear();
        }
        await Task.WhenAll(live.Select(async kv =>
        {
            var (proc, pid) = (kv.Key, kv.Value);
            var how = await ProcessGroup.TermThenKillAsync(pid, Pgrouped, proc, KillGrace);
            if (how != "gone")
                Log.Warn("exec", "exec.kill", new LogFields
                {
                    Msg = "teardown",
                    Data = new Dictionary<string, object>
                    {
                        ["handler"] = name, ["pid"] = pid, ["how"] = how,
                    },
                });
            proc.Dispose();
        }));
    }

    private static async Task WriteEnvelopeAsync(Process proc, HookEvent e, string sentId)
    {
        try
        {
            await proc.StandardInput.WriteAsync(ExecWire.Envelope(e, sentId));
            await proc.StandardInput.WriteAsync('\n');
            await proc.StandardInput.FlushAsync();
            proc.StandardInput.Close();   // oneshot: one request, then EOF
        }
        catch (Exception)
        {
            // EPIPE from a child that exited (or never read) before we wrote —
            // e.g. `exit 0`. Not this task's call: the answer/exit paths
            // decide the outcome.
        }
    }

    /// Internal: the resident lock-step reader uses the same skip-blank
    /// semantics per line read (ReadLineAsync owns \n and \r\n framing).
    internal static async Task<string?> FirstNonEmptyLineAsync(StreamReader stdout)
    {
        while (await stdout.ReadLineAsync() is { } l)
            if (!string.IsNullOrWhiteSpace(l))
                return l;
        return null;
    }

    private async Task DrainStderrAsync(Process proc, StringBuilder into)
    {
        try
        {
            var buf = new char[4096];
            int n;
            while ((n = await proc.StandardError.ReadAsync(buf, 0, buf.Length)) > 0)
                lock (into)   // StderrTail may read while a grandchild keeps this pipe alive
                {
                    if (into.Length < StderrCapBytes)
                        into.Append(buf, 0, Math.Min(n, StderrCapBytes - into.Length));
                }
        }
        catch (Exception) { /* pipe torn by kill — the buffer holds what arrived */ }
    }

    /// Kill one child from a dispatch path (budget cancel, protocol error):
    /// SIGTERM its group NOW — logged, so the trail shows the kill at the
    /// moment it was decided — then escalate grace→SIGKILL on a detached
    /// task, because the dispatch is about to rethrow and an honored-cancel
    /// OCE must reach the ask window without waiting out a stubborn child.
    /// A TERM-ignorer draws a second exec.kill line with how=kill.
    /// The child stays TRACKED until the escalation has CONCLUDED (the
    /// drain-orphan find): untracking up front let a daemon exit outrun the
    /// detached SIGKILL — DisposeAsync's snapshot must keep seeing the child
    /// so the drain's awaited kill covers it; double-kill is idempotent.
    private void KillQuietly(Process proc, HookEvent e, HandlerContext ctx, string why)
    {
        int pid;
        lock (_liveLock)
            if (!_liveChildren.TryGetValue(proc, out pid))
                try { pid = proc.Id; } catch (Exception) { pid = 0; }

        // GROUP-aware gate, never leader-only: a leader that exited between
        // the cancel and this kill can leave live group members (`bad &
        // exit`), and an early return here would leave them unsignaled
        // forever (adversarial-verify find).
        if (pid == 0 || !ProcessGroup.IsAlive(pid, Pgrouped, proc))
        {
            Untrack(proc);
            proc.Dispose();
            return;
        }

        // TERM lands SYNCHRONOUSLY — in collapsed mode the process can exit
        // within ms of the effect reaching stdout, and a TERM living only in
        // the detached task might never be sent. Only the grace→KILL
        // escalation detaches; a TERM-ignorer in collapsed mode can still
        // outlive it (documented residue — the daemon path is covered by the
        // drain's awaited kill via the still-tracked child).
        ProcessGroup.Term(pid, Pgrouped, proc);
        Log.Warn("exec", "exec.kill", F(e, ctx, msg: why,
            data: new Dictionary<string, object> { ["handler"] = name, ["pid"] = pid }));
        _ = Task.Run(async () =>
        {
            var how = await ProcessGroup.TermThenKillAsync(pid, Pgrouped, proc, KillGrace, termSent: true);
            if (how == "kill")
                Log.Warn("exec", "exec.kill", F(e, ctx,
                    msg: $"{why} — SIGKILL after {KillGrace.TotalSeconds:F0}s grace",
                    data: new Dictionary<string, object> { ["handler"] = name, ["pid"] = pid, ["how"] = how }));
            Untrack(proc);
            proc.Dispose();
        });
    }

    /// Shared spawn shape for BOTH exec modes (one spawn discipline): the
    /// resolved spawn prefix is the kill discipline's spawn half — it execs
    /// the payload IN PLACE (the fork child is never a group leader), so
    /// Process.Id is the payload's pid AND its pgid, and kill(-pid) reaches
    /// every grandchild (ProcessGroup). One trade: a missing command no
    /// longer throws Win32Exception at Start (the wrapper itself starts
    /// fine) — it surfaces as a nonzero exit with the wrapper's stderr naming
    /// the command (bosun pins 127 not-found / 126 not-executable as its own
    /// contract; setsid's is whatever util-linux chose). All three pipes
    /// redirected; env stripped to the allowlist.
    /// `prefix` defaults to the process's resolved rung; production never
    /// passes it. Tests do — the deploy rung (bosun) is not the rung a test
    /// tree resolves to, and its argv contract must be pinned anyway.
    internal static ProcessStartInfo BuildPsi(string command, IReadOnlyList<string>? args,
                                              IReadOnlyDictionary<string, string>? env,
                                              IReadOnlyList<string>? passEnv,
                                              ProcessGroup.SpawnPrefix? prefix = null)
    {
        prefix ??= ProcessGroup.Prefix;
        var psi = new ProcessStartInfo
        {
            FileName = prefix.Exe ?? command,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        if (prefix.Exe is not null)
        {
            foreach (var a in prefix.PreArgs) psi.ArgumentList.Add(a);   // bosun's mandatory `--`
            psi.ArgumentList.Add(command);
        }
        foreach (var a in args ?? []) psi.ArgumentList.Add(a);
        ApplyEnvAllowlist(psi, env, passEnv);
        return psi;
    }

    private static void ApplyEnvAllowlist(ProcessStartInfo psi,
                                          IReadOnlyDictionary<string, string>? env,
                                          IReadOnlyList<string>? passEnv)
    {
        // ProcessStartInfo.Environment starts PRE-POPULATED with the parent
        // env — Clear() is the whole security property (a missing Clear ships
        // "inherit everything + adds" with every positive test green).
        // Precedence (decision 5): fixed allowlist ∪ passEnv NAMES cross from
        // the parent env (a passEnv name the parent lacks is silently skipped
        // — nothing to pass); the entry's literal env{} is applied LAST, so
        // explicit always beats inherited.
        psi.Environment.Clear();
        foreach (DictionaryEntry kv in Environment.GetEnvironmentVariables())
        {
            var k = (string)kv.Key;
            var allowed = AllowedEnv.Contains(k)
                          || k.StartsWith("LC_", StringComparison.Ordinal)
                          || (passEnv?.Contains(k) ?? false);
            if (allowed && kv.Value is string v)
                psi.Environment[k] = v;
        }
        foreach (var (k, v) in env ?? new Dictionary<string, string>())
            psi.Environment[k] = v;
    }

    /// Cwd precedence (decision 4): the entry's configured cwd, else the
    /// event's cwd, else the runtime home — each rung only if it EXISTS
    /// (bad-cwd falls through rather than failing the spawn); the resolved
    /// choice and any fallback ride exec.spawn's data.
    private (string? Cwd, bool Fallback) ResolveCwd(string? eventCwd)
    {
        if (cwd is not null && Directory.Exists(cwd)) return (cwd, false);
        var fellBack = cwd is not null;   // configured but missing
        if (eventCwd is not null && Directory.Exists(eventCwd)) return (eventCwd, fellBack);
        var home = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".captainHook");
        return (Directory.Exists(home) ? home : null, fellBack);
    }

    private static string StderrTail(StringBuilder buf)
    {
        lock (buf)
            return buf.Length == 0 ? "" : $" — stderr: {Truncate(buf.ToString().Trim(), 512)}";
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

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
