using System.Text.Json;
using CaptainHook.Core;
using CaptainHook.Handlers;
using static CaptainHook.Tests.TestUtil;

namespace CaptainHook.Tests;

// exec-handler-adapter (ADR-0010 d1/d2/d5/d6, oneshot): real /bin/sh children
// throughout — the state machine under test IS process lifecycle. No test-side
// sleeps: children either answer, exit, or are killed by the budget; the only
// waits are bounded WaitAsync/PollUntilAsync.

public class ExecHandlerTests
{
    private static HookEvent Ev(string? cwd = null) => new(
        "UserPromptSubmit", "s-exec", cwd,
        JsonDocument.Parse("""{"prompt":"hi"}""").RootElement.Clone());

    private static HandlerContext Ctx(string? dispatchId = "ex123", CancellationToken ct = default) =>
        new(DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10), ct, dispatchId);

    private static ExecHandler Sh(string script, params string[] extra)
    {
        var args = new List<string> { "-c", script, "sh" };
        args.AddRange(extra);
        return new ExecHandler("exec-test", "/bin/sh", args);
    }

    [Fact]
    public async Task Answer_Inject_EffectReturned()
    {
        var h = Sh("""read line; printf '{"effect":"inject","text":"from child"}\n'""");
        var eff = await h.HandleAsync(Ev(), Ctx());
        Assert.Equal("from child", Assert.IsType<Effect.Inject>(eff).Text);
    }

    [Fact]
    public async Task Envelope_DeliveredOnStdin_ExactBytes_WithDispatchId()
    {
        using var tmp = new TempRuntimeDir();
        Directory.CreateDirectory(tmp.Path);
        var outFile = Path.Combine(tmp.Path, "seen-envelope");
        var h = Sh("""cat > "$1"; printf '{"effect":"noop"}\n'""", outFile);

        var e = Ev();
        await h.HandleAsync(e, Ctx(dispatchId: "env-pin1"));

        var seen = await File.ReadAllTextAsync(outFile);
        Assert.Equal(ExecWire.Envelope(e, "env-pin1") + "\n", seen);
    }

    [Fact]
    public async Task EmptyStdout_ExitZero_IsNoop()
    {
        var h = Sh("exit 0");
        Assert.IsType<Effect.Noop>(await h.HandleAsync(Ev(), Ctx()));
    }

    [Fact]
    public async Task NonzeroExit_BeforeAnswer_ThrowsWithCodeAndStderr()
    {
        var h = Sh("echo boom >&2; exit 3");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.HandleAsync(Ev(), Ctx()));
        Assert.Contains("exited 3", ex.Message);
        Assert.Contains("boom", ex.Message);
    }

    [Fact]
    public async Task GarbageAnswer_ProtocolError()
    {
        var h = Sh("echo not-json-at-all");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.HandleAsync(Ev(), Ctx()));
        Assert.Contains("wire grammar", ex.Message);
    }

    [Fact]
    public async Task SameLineTrailingGarbage_ProtocolError()
    {
        var h = Sh("""printf '{"effect":"noop"} extra\n'""");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.HandleAsync(Ev(), Ctx()));
        Assert.Contains("wire grammar", ex.Message);
    }

    [Fact]
    public async Task ReplyThenLinger_EffectReturnsImmediately()
    {
        // The child answers then holds stdout open for 30s: the dispatch must
        // NOT wait for it (d2 reply-then-linger). The orphaned sleep dies on
        // its own; nothing here waits on it.
        var h = Sh("""printf '{"effect":"inject","text":"quick"}\n'; exec sleep 30""");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var eff = await h.HandleAsync(Ev(), Ctx());
        sw.Stop();

        Assert.Equal("quick", Assert.IsType<Effect.Inject>(eff).Text);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5),
            $"returned in {sw.Elapsed.TotalSeconds:F1}s — must not await the lingering child");
    }

    [Fact]
    public async Task PostAnswerChatter_DrainedAndIgnored_NeverRetroactivelyFails()
    {
        // d2's temporal resolution: strictness binds up to the answer line;
        // later stdout — even a second effect object — is linger chatter.
        var h = Sh("""printf '{"effect":"noop"}\n'; echo chatter; printf '{"effect":"inject","text":"late"}\n'""");
        Assert.IsType<Effect.Noop>(await h.HandleAsync(Ev(), Ctx()));
    }

    [Fact]
    public async Task GrandchildHoldsPipes_ExitZero_NoopImmediately_NeverAWedge()
    {
        // THE adversarial-verify find: `sleep 30 & exit 0` — the everyday
        // start-a-daemon idiom. The grandchild inherits stdout+stderr, so
        // pipe-EOF arrives ~30s after child-exit; waiting on pipes alone
        // burned the whole budget and (stderr variant) classified the
        // decided-Noop case as a COUNTED WEDGE. The race + bounded joins
        // must yield Noop at child-exit speed.
        var h = Sh("sleep 30 & exit 0");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var eff = await h.HandleAsync(Ev(), Ctx());
        sw.Stop();

        Assert.IsType<Effect.Noop>(eff);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5),
            $"Noop took {sw.Elapsed.TotalSeconds:F1}s — decided outcome must not ride the grandchild's lifetime");
    }

    [Fact]
    public async Task GrandchildHoldsPipes_NonzeroExit_FailsFastWithCode()
    {
        var h = Sh("echo pre-exit-err >&2; sleep 30 & exit 7");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.HandleAsync(Ev(), Ctx()));
        sw.Stop();

        Assert.Contains("exited 7", ex.Message);
        Assert.Contains("pre-exit-err", ex.Message);   // buffered stderr still surfaces
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5),
            $"failure took {sw.Elapsed.TotalSeconds:F1}s — must not ride the grandchild");
    }

    [Fact]
    public async Task GrandchildHoldsPipes_AnswerAlreadyBuffered_StillParsed()
    {
        // Exit-first race where the answer IS in the pipe buffer: the bounded
        // post-exit read must find it, not misclassify as empty.
        var h = Sh("""printf '{"effect":"inject","text":"buffered"}\n'; sleep 30 & exit 0""");
        var eff = await h.HandleAsync(Ev(), Ctx());
        Assert.Equal("buffered", Assert.IsType<Effect.Inject>(eff).Text);
    }

    [Fact]
    public async Task BudgetCancel_KillsTheChild_ThrowsOce()
    {
        using var tmp = new TempRuntimeDir();
        Directory.CreateDirectory(tmp.Path);
        var pidFile = Path.Combine(tmp.Path, "child-pid");
        // Writes its pid, then blocks forever (never answers, ignores stdin).
        var h = Sh("""echo $$ > "$1"; exec sleep 30""", pidFile);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => h.HandleAsync(Ev(), Ctx(ct: cts.Token)));

        // The kill is best-effort-immediate; poll /proc for the death.
        var pid = int.Parse((await File.ReadAllTextAsync(pidFile)).Trim());
        await PollUntilAsync(() => Task.FromResult(!Directory.Exists($"/proc/{pid}")),
            TimeSpan.FromSeconds(10), "budget-killed child reaped");
    }

    [Fact]
    public async Task Environment_StrippedToAllowlist_NothingElseCrosses()
    {
        // The canary is set in THIS process — the child must not see it
        // (ProcessStartInfo.Environment pre-populates with the parent env; a
        // missing Clear() ships every positive test green while the security
        // property is void — this is the canary-ABSENCE check).
        Environment.SetEnvironmentVariable("CAPTAINHOOK_TEST_CANARY", "sekrit");
        try
        {
            var h = Sh("""printf '{"effect":"inject","text":"%s|%s"}\n' "${CAPTAINHOOK_TEST_CANARY:-ABSENT}" "${HOME:-NOHOME}" """);
            var eff = Assert.IsType<Effect.Inject>(await h.HandleAsync(Ev(), Ctx()));

            var parts = eff.Text.Split('|');
            Assert.Equal("ABSENT", parts[0]);          // stripped
            Assert.NotEqual("NOHOME", parts[1]);       // allowlisted survives
        }
        finally
        {
            Environment.SetEnvironmentVariable("CAPTAINHOOK_TEST_CANARY", null);
        }
    }

    [Fact]
    public async Task PassEnv_NamedVarCrosses_UnnamedStaysAbsent()
    {
        // The canary-ABSENCE discipline extended to passEnv: only the NAMED
        // variable crosses; a sibling secret set in the same parent stays out.
        Environment.SetEnvironmentVariable("CAPTAINHOOK_TEST_PASSED", "crossed");
        Environment.SetEnvironmentVariable("CAPTAINHOOK_TEST_SECRET", "sekrit");
        try
        {
            var h = new ExecHandler("passer", "/bin/sh",
                ["-c", """printf '{"effect":"inject","text":"%s|%s"}\n' "${CAPTAINHOOK_TEST_PASSED:-ABSENT}" "${CAPTAINHOOK_TEST_SECRET:-ABSENT}" """],
                passEnv: ["CAPTAINHOOK_TEST_PASSED"]);
            var eff = Assert.IsType<Effect.Inject>(await h.HandleAsync(Ev(), Ctx()));
            Assert.Equal("crossed|ABSENT", eff.Text);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CAPTAINHOOK_TEST_PASSED", null);
            Environment.SetEnvironmentVariable("CAPTAINHOOK_TEST_SECRET", null);
        }
    }

    [Fact]
    public async Task EnvLiteral_Applied_AndBeatsInherited()
    {
        // Literal env{} lands, and explicit beats the allowlisted inherit
        // (HOME is allowlisted — the literal must still win).
        var h = new ExecHandler("literal", "/bin/sh",
            ["-c", """printf '{"effect":"inject","text":"%s|%s"}\n' "${MY_LITERAL:-ABSENT}" "$HOME" """],
            env: new Dictionary<string, string> { ["MY_LITERAL"] = "42", ["HOME"] = "/custom-home" });
        var eff = Assert.IsType<Effect.Inject>(await h.HandleAsync(Ev(), Ctx()));
        Assert.Equal("42|/custom-home", eff.Text);
    }

    [Fact]
    public async Task PassEnvNameParentLacks_SilentlySkipped()
    {
        var h = new ExecHandler("skipper", "/bin/sh",
            ["-c", """printf '{"effect":"inject","text":"%s"}\n' "${NOT_SET_ANYWHERE_XYZ:-ABSENT}" """],
            passEnv: ["NOT_SET_ANYWHERE_XYZ"]);
        var eff = Assert.IsType<Effect.Inject>(await h.HandleAsync(Ev(), Ctx()));
        Assert.Equal("ABSENT", eff.Text);   // nothing to pass — not an error
    }

    [Fact]
    public async Task ConfigCwd_BeatsEventCwd()
    {
        var configDir = Directory.CreateTempSubdirectory("exec-configcwd-");
        var eventDir = Directory.CreateTempSubdirectory("exec-eventcwd-");
        try
        {
            var h = new ExecHandler("cwd-config", "/bin/sh",
                ["-c", """printf '{"effect":"inject","text":"%s"}\n' "$PWD" """],
                cwd: configDir.FullName);
            var eff = Assert.IsType<Effect.Inject>(await h.HandleAsync(Ev(cwd: eventDir.FullName), Ctx()));
            Assert.Equal(configDir.FullName, eff.Text);
        }
        finally { configDir.Delete(); eventDir.Delete(); }
    }

    [Fact]
    public async Task BadConfigCwd_FallsThroughToEventCwd()
    {
        var eventDir = Directory.CreateTempSubdirectory("exec-eventcwd-");
        try
        {
            var h = new ExecHandler("cwd-fallback", "/bin/sh",
                ["-c", """printf '{"effect":"inject","text":"%s"}\n' "$PWD" """],
                cwd: "/no/such/dir/xyz");
            var eff = Assert.IsType<Effect.Inject>(await h.HandleAsync(Ev(cwd: eventDir.FullName), Ctx()));
            Assert.Equal(eventDir.FullName, eff.Text);   // bad cwd never fails the spawn
        }
        finally { eventDir.Delete(); }
    }

    [Fact]
    public async Task Cwd_IsTheEventsCwd_WhenItExists()
    {
        var dir = Directory.CreateTempSubdirectory("exec-cwd-");
        try
        {
            var h = Sh("""printf '{"effect":"inject","text":"%s"}\n' "$PWD" """);
            var eff = Assert.IsType<Effect.Inject>(await h.HandleAsync(Ev(cwd: dir.FullName), Ctx()));
            Assert.Equal(dir.FullName, eff.Text);
        }
        finally { dir.Delete(); }
    }

    [Fact]
    public async Task CommandNotFound_CleanThrow_NamesTheCommand()
    {
        var h = new ExecHandler("gone", "definitely-not-a-command-xyz");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.HandleAsync(Ev(), Ctx()));
        Assert.Contains("definitely-not-a-command-xyz", ex.Message);
    }

    [Fact]
    public async Task ThroughDispatcher_FailOpen_CrashingChildMergesToNoop()
    {
        var reg = new Registry().On("UserPromptSubmit",
            Sh("echo doomed >&2; exit 7"));
        var dispatcher = new Dispatcher(reg, TimeSpan.FromSeconds(5));

        var r = await dispatcher.DispatchAsync(TestUtil.Ev());
        Assert.IsType<Effect.Noop>(r.Merged);
        Assert.Contains("ERROR -> fail-open", r.Trace.Render());
    }

    [Fact]
    public async Task ThroughDispatcher_FailClosed_CrashingChildMergesToDeny()
    {
        var reg = new Registry().On("UserPromptSubmit",
            new ExecHandler("exec-gate", "/bin/sh", ["-c", "exit 9"], FailMode.Closed));
        var dispatcher = new Dispatcher(reg, TimeSpan.FromSeconds(5));

        var r = await dispatcher.DispatchAsync(TestUtil.Ev());
        var deny = Assert.IsType<Effect.Decide>(r.Merged);
        Assert.Equal(Verdict.Deny, deny.Verdict);
    }

    [Fact]
    public async Task ThroughDispatcher_DecideDeny_WinsTheMerge()
    {
        // The end-to-end shape the tool gate will ride: a child denies, a
        // sibling injects; the deny wins the merge exactly like any handler.
        var reg = new Registry()
            .On("PreToolUse", TestHandler.Returning("friendly", new Effect.Inject("context")))
            .On("PreToolUse", Sh("""read l; printf '{"effect":"decide","verdict":"deny","reason":"child said no"}\n'"""));
        var dispatcher = new Dispatcher(reg, TimeSpan.FromSeconds(5));

        var r = await dispatcher.DispatchAsync(TestUtil.Ev("PreToolUse"));
        var deny = Assert.IsType<Effect.Decide>(r.Merged);
        Assert.Equal(Verdict.Deny, deny.Verdict);
        Assert.Equal("child said no", deny.Reason);
    }
}
