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
///   * budget cancellation  ⇒ child killed (best-effort process tree; the
///                            setpgid discipline is the kill-discipline
///                            slice), OperationCanceledException rethrown so
///                            the classified ask counts it as an honored
///                            cancel. `exec.kill` in the trail.
///
/// Grandchildren decouple pipe-EOF from child-exit (a backgrounded process
/// inherits the fds), so exit and answer are raced and every post-exit pipe
/// join is bounded by PipeGrace — a `sleep 30 & exit 0` child yields its
/// decided Noop immediately, and the grandchild itself is the
/// kill-discipline slice's business (setpgid). Reply-then-linger is a
/// DAEMON-mode pattern: in collapsed mode the engine exits after the
/// effect, abandoning the reaper (no exec.exit line) and closing the pipe
/// read-ends — a lingering child that writes afterwards gets SIGPIPE. Its
/// own risk; documented, not defended.
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
    FailMode onFailure = FailMode.Open) : IHandler
{
    public string Name => name;
    public FailMode OnFailure => onFailure;

    /// Decision 5's fixed allowlist (exact names; LC_* by prefix). Explicit
    /// per-entry env/passEnv are handlers.json fields (phase 4) — this static
    /// core is deliberately baked into the FIRST spawn site so no build ever
    /// ships an inherit-everything window.
    private static readonly HashSet<string> AllowedEnv = new(StringComparer.Ordinal)
        { "PATH", "HOME", "USER", "SHELL", "LANG", "TZ", "TMPDIR" };

    private const int StderrCapBytes = 8 * 1024;

    /// The bound on every post-exit pipe join: whatever the CHILD wrote is
    /// already buffered and arrives in single-digit ms; anything slower means
    /// a grandchild is holding the fd, and the decided outcome must not wait
    /// for it.
    private static readonly TimeSpan PipeGrace = TimeSpan.FromMilliseconds(250);

    /// Await `task` for at most `grace`; a timeout yields default — the
    /// caller proceeds with what it has (partial stderr, no answer line).
    private static async Task<T?> BoundedAsync<T>(Task<T> task, TimeSpan grace)
    {
        try { return await task.WaitAsync(grace); }
        catch (TimeoutException) { return default; }
    }

    private static async Task BoundedAsync(Task task, TimeSpan grace)
    {
        try { await task.WaitAsync(grace); }
        catch (TimeoutException) { }
    }

    public async Task<Effect> HandleAsync(HookEvent e, HandlerContext ctx)
    {
        var sw = Stopwatch.StartNew();
        var psi = new ProcessStartInfo
        {
            FileName = command,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args ?? []) psi.ArgumentList.Add(a);
        ApplyEnvAllowlist(psi);
        ApplyCwd(psi, e.Cwd);

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

        Log.Info("exec", "exec.spawn", F(e, ctx, data: new Dictionary<string, object>
        {
            ["handler"] = name, ["pid"] = proc.Id, ["command"] = command, ["mode"] = "oneshot",
        }));

        // All three pipes serviced CONCURRENTLY from the start — the classic
        // full-pipe-buffer deadlock (child blocked writing stderr while we
        // block reading stdout, or child never reads the stdin we block
        // writing) is structurally impossible.
        var stderrBuf = new StringBuilder();
        var stderrTask = DrainStderrAsync(proc, stderrBuf);
        var stdinTask = WriteEnvelopeAsync(proc, e, ctx);
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
            case ExecAnswer.Ok(var effect):
                Log.Info("exec", "exec.answered", F(e, ctx, sw.Elapsed.TotalMilliseconds,
                    data: new Dictionary<string, object>
                    {
                        ["handler"] = name, ["effect"] = effect.GetType().Name,
                    }));
                // Reply-then-linger: the effect returns NOW; the reaper owns
                // the child's afterlife (drain, exit code, disposal) and can
                // never fail the dispatch.
                _ = ReapAsync(proc, e, ctx, name, sw, stderrTask, stderrBuf);
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
    private static async Task ReapAsync(Process proc, HookEvent e, HandlerContext ctx, string name,
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
            await drain;
            await stderrTask;
            Log.Info("exec", "exec.exit", F(e, ctx, sw.Elapsed.TotalMilliseconds,
                data: new Dictionary<string, object> { ["handler"] = name, ["code"] = proc.ExitCode }));
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
            proc.Dispose();
        }
    }

    private static async Task WriteEnvelopeAsync(Process proc, HookEvent e, HandlerContext ctx)
    {
        try
        {
            await proc.StandardInput.WriteAsync(ExecWire.Envelope(e, ctx.DispatchId ?? ""));
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

    private static async Task<string?> FirstNonEmptyLineAsync(StreamReader stdout)
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

    private void KillQuietly(Process proc, HookEvent e, HandlerContext ctx, string why)
    {
        try
        {
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
                Log.Warn("exec", "exec.kill", F(e, ctx, msg: why,
                    data: new Dictionary<string, object> { ["handler"] = name, ["pid"] = proc.Id }));
            }
        }
        catch (Exception) { /* already gone — the goal state */ }
        finally { proc.Dispose(); }
    }

    private static void ApplyEnvAllowlist(ProcessStartInfo psi)
    {
        // ProcessStartInfo.Environment starts PRE-POPULATED with the parent
        // env — Clear() is the whole security property (a missing Clear ships
        // "inherit everything + adds" with every positive test green).
        psi.Environment.Clear();
        foreach (DictionaryEntry kv in Environment.GetEnvironmentVariables())
        {
            var k = (string)kv.Key;
            if ((AllowedEnv.Contains(k) || k.StartsWith("LC_", StringComparison.Ordinal)) && kv.Value is string v)
                psi.Environment[k] = v;
        }
    }

    private static void ApplyCwd(ProcessStartInfo psi, string? eventCwd)
    {
        if (eventCwd is not null && Directory.Exists(eventCwd))
            psi.WorkingDirectory = eventCwd;
        else
        {
            var home = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".captainHook");
            if (Directory.Exists(home)) psi.WorkingDirectory = home;
        }
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
